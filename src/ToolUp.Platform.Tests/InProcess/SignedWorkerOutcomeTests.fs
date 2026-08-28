module ToolUp.Platform.Tests.InProcess.SignedWorkerOutcomeTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// ─── Phase 486 — signed worker outcomes ──────────────────────────────
//
// The phase's claim is "this outcome came from THAT worker, and this is
// the result it signed". Both halves are made falsifiable rather than
// merely exercised:
//
//   * **Every rejection test has a paired positive control on the same
//     material.** The tampered-artifact test signs a genuine envelope,
//     shows it VERIFIES against the body it was signed over, and then
//     shows the identical envelope refused against a substituted body.
//     Without the control, a test that only asserted "refused" could not
//     tell a working hash check from a verifier that refuses everything —
//     and a verifier that refuses everything is a plausible way to break
//     this file.
//   * **The revocation test revokes a key that has ALREADY verified a
//     callback**, so the refusal cannot be explained by the key never
//     having worked.
//   * **The policy is asserted BOTH WAYS at the HTTP boundary** — the same
//     unsigned callback, 200 under one policy and 403 under another —
//     because "unsigned is rejected" and "unsigned is accepted" are
//     separately breakable and a one-directional test hides half of it.
//   * **The GP 13 control sends a signature header to a deployment that
//     composed no verification**, and requires 200. A header that garbage
//     cannot be verified proves the gate did not run at all, which is the
//     byte-for-byte claim; asserting only "an unsigned request still
//     works" would pass even if the gate ran and happened to allow it.

// ── signing, as a worker would ───────────────────────────────────────

/// A worker's key pair, plus everything needed to register the public half.
type private WorkerKeyPair = {
    Key: ECDsa
    /// Base64 DER SubjectPublicKeyInfo — the registry's storage form.
    PublicKey: string
}

let private newKeyPair () : WorkerKeyPair =
    let key = ECDsa.Create ECCurve.NamedCurves.nistP256

    {
        Key = key
        PublicKey = key.ExportSubjectPublicKeyInfo() |> Convert.ToBase64String
    }

/// Unpadded base64url, the encoding the envelope's `sig` parameter is
/// defined as.
let private toBase64Url (bytes: byte[]) =
    let standard = Convert.ToBase64String bytes
    standard.TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private sha256Hex (text: string) =
    text
    |> Encoding.UTF8.GetBytes
    |> SHA256.HashData
    |> Convert.ToHexString
    |> _.ToLowerInvariant()

/// Sign an outcome exactly as a conforming worker would: hash the
/// canonical descriptor, build the envelope, sign the canonical payload
/// with IEEE-P1363 encoding.
///
/// **This is the emitting half of the contract, and it goes through the
/// SHIPPED helpers** (`SignedOutcomeVerifier.artifactHash`,
/// `WorkerOutcomeSignature.signingPayload` / `render`) rather than
/// re-deriving them. A test that re-implemented the canonicalisation could
/// agree with itself while disagreeing with every real worker.
let private signOutcome
    (pair: WorkerKeyPair)
    (workerId: string)
    (keyId: string)
    (signedAt: DateTimeOffset)
    (handleId: Guid)
    (outcome: ExternalOutcome)
    (diagnostics: string)
    : string =
    let artifact =
        match SignedOutcomeVerifier.artifactHash outcome with
        | Ok h -> h
        | Error e -> failwithf "the test outcome has no artifact hash: %s" e

    let unsigned = {
        WorkerId = workerId
        KeyId = keyId
        SignedAt = signedAt.ToString "o"
        ArtifactHash = artifact
        DiagnosticsHash = sha256Hex diagnostics
        Signature = "placeholder"
    }

    let payload = WorkerOutcomeSignature.signingPayload handleId unsigned

    let signature =
        pair.Key.SignData(
            Encoding.UTF8.GetBytes payload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        )
        |> toBase64Url

    WorkerOutcomeSignature.render { unsigned with Signature = signature }

// ── 486.B — the wire envelope ────────────────────────────────────────

let private envelopeTests =
    testList "486.B — the signature envelope (wire shape)" [

        test "render and parse round-trip" {
            let envelope = {
                WorkerId = "gpu-node-7"
                KeyId = "k-2026-08"
                SignedAt = "2026-08-04T10:00:00.0000000+00:00"
                ArtifactHash = sha256Hex "succeeded:s3://out"
                DiagnosticsHash = sha256Hex "logs"
                Signature = toBase64Url (Array.create 64 7uy)
            }

            match WorkerOutcomeSignature.parse (WorkerOutcomeSignature.render envelope) with
            | Ok parsed -> Expect.equal parsed envelope "the envelope survives render -> parse unchanged"
            | Error e -> failtestf "a rendered envelope must parse: %s" e
        }

        test "parse is order-independent and ignores an unknown parameter" {
            // Forward compatibility is deliberate: the envelope must be
            // able to gain an attestation parameter later without every
            // existing server rejecting it.
            let header =
                sprintf
                    "attestation=sgx-quote-abc,sig=%s,diagnostics=%s,artifact=%s,t=2026-08-04T10:00:00Z,key=k1,v=1,worker=w1"
                    (toBase64Url (Array.create 64 1uy))
                    (sha256Hex "d")
                    (sha256Hex "a")

            match WorkerOutcomeSignature.parse header with
            | Ok parsed ->
                Expect.equal parsed.WorkerId "w1" "worker"
                Expect.equal parsed.KeyId "k1" "key"
            | Error e -> failtestf "reordered parameters plus an unknown one must parse: %s" e
        }

        testList
            "each malformed shape is refused, and the message names the parameter"
            ([
                "v=2,worker=w,key=k,t=2026-08-04T10:00:00Z", "version"
                "worker=w,key=k,t=2026-08-04T10:00:00Z", "missing v"
                "v=1,key=k,t=2026-08-04T10:00:00Z", "missing worker"
                "v=1,worker=w,worker=x,key=k", "duplicate worker"
                "v=1,worker=w with space,key=k", "unsafe worker id"
                "v=1,worker=w,key=k,t=2026-08-04T10:00:00Z,artifact=SHORT", "bad artifact digest"
                "nonsense", "not a key=value pair"
             ]
             |> List.map (fun (header, name) -> test name {
                 Expect.isError (WorkerOutcomeSignature.parse header) $"'%s{header}' must not parse"
             }))

        test "an UPPERCASE hex digest is refused, not normalised" {
            // The digest text enters the signing payload. Case-folding it
            // would make two distinct payloads verify against one
            // signature.
            let upper = (sha256Hex "a").ToUpperInvariant()

            Expect.isFalse (WorkerOutcomeSignature.isHexDigest upper) "uppercase hex is not a valid digest"
            Expect.isTrue (WorkerOutcomeSignature.isHexDigest (sha256Hex "a")) "lowercase hex is"
        }

        test "a PADDED base64 signature is refused" {
            // Unpadded base64url is what lets the parameter parser reject
            // any value containing '=' — the smuggling guard.
            Expect.isFalse (WorkerOutcomeSignature.isBase64Url "abc=") "padded base64 is refused"
            Expect.isFalse (WorkerOutcomeSignature.isBase64Url "a+b/c") "standard base64 alphabet is refused"
            Expect.isTrue (WorkerOutcomeSignature.isBase64Url "a-b_c") "base64url is accepted"
        }

        test "an over-long header is refused without parsing" {
            let header = String.replicate 3000 "x"
            Expect.isError (WorkerOutcomeSignature.parse header) "a 3000-character header is refused"
        }

        test "no two terminal outcomes share a descriptor" {
            // A `Succeeded ""` and a `Cancelled` MUST NOT collide, or a
            // relay could convert one into the other under a genuine
            // signature.
            let descriptors =
                [
                    ExternalOutcome.Succeeded ""
                    ExternalOutcome.Succeeded "s3://out"
                    ExternalOutcome.Cancelled
                    ExternalOutcome.Failed { Message = ""; Retriable = false }
                    ExternalOutcome.Failed { Message = ""; Retriable = true }
                ]
                |> List.map (fun o ->
                    match WorkerOutcomeSignature.outcomeDescriptor o with
                    | Ok d -> d
                    | Error e -> failtestf "terminal outcome without a descriptor: %s" e)

            Expect.equal
                (descriptors |> List.distinct |> List.length)
                descriptors.Length
                "five distinct terminal outcomes produce five distinct descriptors"
        }

        test "a NON-terminal outcome has no descriptor and no artifact hash" {
            Expect.isError
                (WorkerOutcomeSignature.outcomeDescriptor ExternalOutcome.Pending)
                "pending has no signed form"

            Expect.isError
                (SignedOutcomeVerifier.artifactHash (ExternalOutcome.Running(Some 0.5)))
                "running has no artifact hash"
        }

        test "the signing payload is domain-separated and binds the handle" {
            let envelope = {
                WorkerId = "w"
                KeyId = "k"
                SignedAt = "2026-08-04T10:00:00Z"
                ArtifactHash = sha256Hex "a"
                DiagnosticsHash = sha256Hex "d"
                Signature = "s"
            }

            let a = WorkerOutcomeSignature.signingPayload (Guid.NewGuid()) envelope
            let b = WorkerOutcomeSignature.signingPayload (Guid.NewGuid()) envelope

            Expect.stringStarts a WorkerOutcomeSignature.Domain "the payload opens with the domain tag"
            Expect.notEqual a b "two handles produce two payloads — a signature cannot move between handles"
        }
    ]

// ── 486.A — the worker key registry ──────────────────────────────────

/// An `IBlobStorage` that does NOT implement `IConditionalBlobStorage` —
/// the shape `BlobWorkerKeyRegistry` must refuse at construction.
let private plainBlobStorage () : IBlobStorage =
    { new IBlobStorage with
        // Phase 741 — no bounded multi-part commit primitive here; callers assemble through memory.
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(_, _, _) = async { return Ok "" }
        member _.Download(_, _) = async { return Error "not conditional" }
        member _.DownloadRange(_, _, _, _) = async { return Error "not conditional" }
        member _.Delete(_, _) = async { return Ok() }
        member _.List(_, _) = async { return [] }
        member _.Exists(_, _) = async { return false }
        member _.GetMetadata(_, _) = async { return Error "not conditional" }
        member _.Erase(_, _, _, _) = async { return Ok(Unchecked.defaultof<_>) }
    }

let private run x = Async.RunSynchronously x

/// The registry laws, bound against BOTH shipped implementations —
/// contract-shaped, so a companion registry can be held to the same bar.
let private registryContract (name: string) (build: unit -> IWorkerKeyRegistry) =
    testList $"486.A — IWorkerKeyRegistry laws (%s{name})" [

        test "operator registration lands APPROVED and resolves" {
            let registry = build ()
            let pair = newKeyPair ()

            match
                registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops@example.com")
                |> run
            with
            | Error e -> failtestf "registration must succeed: %s" (WorkerKeyError.describe e)
            | Ok key ->
                Expect.equal key.Status WorkerKeyStatus.Approved "registered keys are approved"
                Expect.equal key.ApprovedBy (Some "ops@example.com") "the approver is recorded (GP 6)"

            match registry.Resolve("gpu-1", "k1") |> run with
            | Some resolved ->
                Expect.isTrue (WorkerKeyRegistry.isUsable resolved) "and it is usable"
                Expect.equal resolved.PublicKey pair.PublicKey "the stored material is what was registered"
            | None -> failtest "a registered key must resolve"
        }

        test "first-contact enrolment lands PENDING and is NOT usable" {
            // The whole safety property of an unauthenticated-ish
            // enrolment path: it cannot make anything verify.
            let registry = build ()
            let pair = newKeyPair ()

            match
                registry.EnrolOnFirstContact("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey)
                |> run
            with
            | Error e -> failtestf "first contact must be recorded: %s" (WorkerKeyError.describe e)
            | Ok key ->
                Expect.equal key.Status WorkerKeyStatus.PendingApproval "pending, not approved"
                Expect.isFalse (WorkerKeyRegistry.isUsable key) "and NOT usable"
                Expect.isNone key.ApprovedBy "nobody approved it"

            match registry.Approve("gpu-1", "k1", "admin@example.com") |> run with
            | Error e -> failtestf "approval must succeed: %s" (WorkerKeyError.describe e)
            | Ok approved ->
                Expect.isTrue (WorkerKeyRegistry.isUsable approved) "approval makes it usable"
                Expect.equal approved.ApprovedBy (Some "admin@example.com") "and names the admin"
        }

        test "a repeat first contact must NOT demote an approved key" {
            // Otherwise an unauthenticated re-enrolment is a switch
            // that turns a working worker off.
            let registry = build ()
            let pair = newKeyPair ()

            registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
            |> run
            |> ignore

            match
                registry.EnrolOnFirstContact("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey)
                |> run
            with
            | Error e ->
                failtestf "a repeat enrolment of identical material is idempotent Ok: %s" (WorkerKeyError.describe e)
            | Ok key -> Expect.equal key.Status WorkerKeyStatus.Approved "still approved"
        }

        test "DIFFERENT material under a known key id is a KeyConflict, never an overwrite" {
            let registry = build ()
            let first = newKeyPair ()
            let attacker = newKeyPair ()

            registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, first.PublicKey, "ops")
            |> run
            |> ignore

            match
                registry.EnrolOnFirstContact("gpu-1", "k1", WorkerKeyAlgorithm.Es256, attacker.PublicKey)
                |> run
            with
            | Ok _ -> failtest "substituting material under a known key id must be refused"
            | Error(WorkerKeyError.KeyConflict("gpu-1", "k1")) -> ()
            | Error other -> failtestf "expected KeyConflict, got %s" (WorkerKeyError.label other)

            // And the stored material is untouched — the assertion
            // that matters, since a refusal that still wrote would be
            // the actual defect.
            match registry.Resolve("gpu-1", "k1") |> run with
            | Some key -> Expect.equal key.PublicKey first.PublicKey "the original material survived"
            | None -> failtest "the key must still be there"
        }

        test "revocation is STICKY — neither re-enrolment nor approval restores it" {
            let registry = build ()
            let pair = newKeyPair ()

            registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
            |> run
            |> ignore

            match registry.Revoke("gpu-1", "k1", "node decommissioned") |> run with
            | Error e -> failtestf "revocation must succeed: %s" (WorkerKeyError.describe e)
            | Ok key ->
                Expect.isFalse (WorkerKeyRegistry.isUsable key) "a revoked key is not usable"

                match key.Status with
                | WorkerKeyStatus.Revoked(reason, _) -> Expect.equal reason "node decommissioned" "the reason is kept"
                | other -> failtestf "expected Revoked, got %s" (WorkerKeyStatus.label other)

            for label, attempt in
                [
                    "re-enrolment",
                    fun () ->
                        registry.EnrolOnFirstContact("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey)
                        |> run
                    "re-registration",
                    fun () ->
                        registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
                        |> run
                    "approval", fun () -> registry.Approve("gpu-1", "k1", "admin") |> run
                ] do
                match attempt () with
                | Error(WorkerKeyError.KeyRevoked("gpu-1", "k1", _)) -> ()
                | Ok _ -> failtestf "%s must not resurrect a revoked key" label
                | Error other -> failtestf "%s: expected KeyRevoked, got %s" label (WorkerKeyError.label other)

            // Rotation is a NEW key id, and that path must still work
            // — a revocation that also blocked rotation would be a
            // control nobody uses.
            let rotated = newKeyPair ()

            Expect.isOk
                (registry.Register("gpu-1", "k2", WorkerKeyAlgorithm.Es256, rotated.PublicKey, "ops")
                 |> run
                 |> Result.mapError WorkerKeyError.describe)
                "rotation to a new key id is allowed"
        }

        test "re-revoking keeps the FIRST reason" {
            let registry = build ()
            let pair = newKeyPair ()

            registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
            |> run
            |> ignore

            registry.Revoke("gpu-1", "k1", "first") |> run |> ignore

            match registry.Revoke("gpu-1", "k1", "second") |> run with
            | Ok key ->
                match key.Status with
                | WorkerKeyStatus.Revoked(reason, _) ->
                    Expect.equal reason "first" "the first revocation is the one an incident needs"
                | other -> failtestf "expected Revoked, got %s" (WorkerKeyStatus.label other)
            | Error e -> failtestf "re-revocation is idempotent Ok: %s" (WorkerKeyError.describe e)
        }

        test "an unknown key is UnknownKey on approve / revoke and None on resolve" {
            let registry = build ()

            Expect.isNone (registry.Resolve("nobody", "k1") |> run) "resolve"

            match registry.Approve("nobody", "k1", "admin") |> run with
            | Error(WorkerKeyError.UnknownKey _) -> ()
            | other -> failtestf "expected UnknownKey from approve, got %A" other

            match registry.Revoke("nobody", "k1", "why") |> run with
            | Error(WorkerKeyError.UnknownKey _) -> ()
            | other -> failtestf "expected UnknownKey from revoke, got %A" other
        }

        test "invalid key material is refused at REGISTRATION, not at first callback" {
            let registry = build ()

            let cases = [
                "not base64 at all", WorkerKeyAlgorithm.Es256, "!!!not base64!!!"
                "base64 but not an SPKI", WorkerKeyAlgorithm.Es256, Convert.ToBase64String [| 1uy; 2uy; 3uy |]
                "ed25519 of the wrong length", WorkerKeyAlgorithm.Ed25519, Convert.ToBase64String(Array.create 16 0uy)
            ]

            for name, algorithm, material in cases do
                match
                    registry.Register("gpu-1", "k-" + string name.Length, algorithm, material, "ops")
                    |> run
                with
                | Error(WorkerKeyError.InvalidPublicKey _) -> ()
                | other -> failtestf "%s: expected InvalidPublicKey, got %A" name other

            // A P-384 key is valid SPKI and valid ECDSA — and still
            // wrong for `es256`. The curve check, shown firing.
            use wrongCurve = ECDsa.Create ECCurve.NamedCurves.nistP384

            match
                registry.Register(
                    "gpu-1",
                    "k-p384",
                    WorkerKeyAlgorithm.Es256,
                    wrongCurve.ExportSubjectPublicKeyInfo() |> Convert.ToBase64String,
                    "ops"
                )
                |> run
            with
            | Error(WorkerKeyError.InvalidPublicKey reason) ->
                Expect.stringContains reason "P-256" "the refusal names the required curve"
            | other -> failtestf "a P-384 key must be refused for es256, got %A" other
        }

        test "an identifier the signature envelope could never carry is refused" {
            // Registering under such an identifier would produce a key
            // that can never be used, discovered at first callback.
            let registry = build ()
            let pair = newKeyPair ()

            match
                registry.Register("gpu 1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
                |> run
            with
            | Error(WorkerKeyError.InvalidIdentifier _) -> ()
            | other -> failtestf "a worker id with a space must be refused, got %A" other

            match
                registry.Register("gpu-1", "k,1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
                |> run
            with
            | Error(WorkerKeyError.InvalidIdentifier _) -> ()
            | other -> failtestf "a key id containing the parameter separator must be refused, got %A" other
        }

        test "an ed25519 key registers even though the in-tree verifier cannot check it" {
            // Registrable and not verifiable in-tree is the honest
            // split; a registry that refused the algorithm outright
            // would make the companion seam unreachable.
            let registry = build ()
            let material = Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)

            Expect.isOk
                (registry.Register("gpu-1", "ed", WorkerKeyAlgorithm.Ed25519, material, "ops")
                 |> run
                 |> Result.mapError WorkerKeyError.describe)
                "a 32-byte ed25519 key registers"
        }

        test "List returns pending and revoked keys too" {
            let registry = build ()
            let a = newKeyPair ()
            let b = newKeyPair ()
            let c = newKeyPair ()

            registry.Register("w1", "k1", WorkerKeyAlgorithm.Es256, a.PublicKey, "ops")
            |> run
            |> ignore

            registry.EnrolOnFirstContact("w2", "k1", WorkerKeyAlgorithm.Es256, b.PublicKey)
            |> run
            |> ignore

            registry.Register("w3", "k1", WorkerKeyAlgorithm.Es256, c.PublicKey, "ops")
            |> run
            |> ignore

            registry.Revoke("w3", "k1", "gone") |> run |> ignore

            let listed = registry.List() |> run

            Expect.equal listed.Length 3 "all three are listed"

            Expect.equal
                (listed |> List.map (_.Status >> WorkerKeyStatus.label) |> List.sort)
                [ "approved"; "pending-approval"; "revoked" ]
                "including the rows an operator opened the list to act on"
        }
    ]

let private registryStructureTests =
    testList "486.A — registry implementations differ where they must" [

        test "the in-memory registry declares itself NOT distributed" {
            Expect.isFalse (InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry).IsDistributed "in-memory"
        }

        test "the blob registry declares itself distributed" {
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            Expect.isTrue (BlobWorkerKeyRegistry blobs :> IWorkerKeyRegistry).IsDistributed "blob-backed"
        }

        test "the blob registry REFUSES a backend without conditional writes, at construction" {
            // Every mutation is a read-decide-write whose decision is a
            // security decision. A store that silently degraded would let
            // a concurrent enrolment overwrite a revocation.
            let plain = plainBlobStorage ()

            Expect.throwsT<ArgumentException>
                (fun () -> BlobWorkerKeyRegistry plain |> ignore)
                "a non-conditional backend is refused"

            Expect.isNone (BlobWorkerKeyRegistry.TryCreate plain) "and the probing form returns None"

            Expect.isSome
                (BlobWorkerKeyRegistry.TryCreate(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage))
                "while a conditional backend is accepted"
        }

        test "the blob registry survives a new registry instance over the same storage" {
            // Durability, asserted the only way that means anything: a
            // fresh instance over the same blobs.
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let pair = newKeyPair ()

            (BlobWorkerKeyRegistry blobs :> IWorkerKeyRegistry)
                .Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
            |> run
            |> ignore

            (BlobWorkerKeyRegistry blobs :> IWorkerKeyRegistry).Revoke("gpu-1", "k1", "compromised")
            |> run
            |> ignore

            match
                (BlobWorkerKeyRegistry blobs :> IWorkerKeyRegistry).Resolve("gpu-1", "k1")
                |> run
            with
            | Some key ->
                Expect.isFalse
                    (WorkerKeyRegistry.isUsable key)
                    "the revocation is visible to an independently-constructed registry"
            | None -> failtest "the key must survive"
        }

        test "a key blob copied into another worker's partition does not resolve" {
            // The cross-check, shown firing against a deliberately forged
            // record — the same posture BlobExternalHandleStore takes.
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let registry = BlobWorkerKeyRegistry blobs :> IWorkerKeyRegistry
            let pair = newKeyPair ()

            registry.Register("gpu-1", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
            |> run
            |> ignore

            let stored =
                blobs.Download("_platform", "external-compute/worker-keys/gpu-1/k1.json") |> run

            match stored with
            | Error e -> failtestf "the key blob must be readable: %s" e
            | Ok bytes ->
                blobs.Upload("_platform", "external-compute/worker-keys/gpu-2/k1.json", bytes)
                |> run
                |> ignore

                Expect.isNone
                    (registry.Resolve("gpu-2", "k1") |> run)
                    "a record whose own WorkerId disagrees with its partition is refused, not followed"
        }
    ]

// ── 486.B/C — verification ───────────────────────────────────────────

let private approvedRegistry (pair: WorkerKeyPair) (workerId: string) (keyId: string) =
    let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry

    registry.Register(workerId, keyId, WorkerKeyAlgorithm.Es256, pair.PublicKey, "ops")
    |> run
    |> ignore

    registry

let private verificationFor (registry: IWorkerKeyRegistry) (policy: SignedOutcomePolicy) =
    SignedOutcomeVerification.create policy registry

let private verifierTests =
    testList "486.B — SignedOutcomeVerifier" [

        test "a signed outcome verifies end to end, and the attribution names the worker" {
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://bucket/result.parquet"

            let header =
                signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handleId outcome "logs"

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    handleId
                    outcome
                    (Some header)
                |> run
            with
            | Ok(Some attribution) ->
                Expect.equal attribution.WorkerId "gpu-7" "the verified worker"
                Expect.equal attribution.KeyId "k1" "the key that signed"
                Expect.equal attribution.Algorithm WorkerKeyAlgorithm.Es256 "the algorithm from the REGISTRY"

                Expect.equal
                    attribution.ArtifactHash
                    (SignedOutcomeVerifier.artifactHash outcome |> Result.defaultValue "")
                    "the artifact digest matches the outcome"
            | Ok None -> failtest "a presented, valid signature must produce an attribution"
            | Error e -> failtestf "verification must succeed: %s" (SignedOutcomeRejection.describe e)
        }

        test "TAMPERED artifact — the same genuine envelope over a substituted result is refused" {
            // The falsifiable form: one envelope, two bodies. It VERIFIES
            // against the body it was signed over and is REFUSED against
            // the substituted one, so the refusal cannot be explained by a
            // verifier that rejects everything.
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let signedOutcome = ExternalOutcome.Succeeded "s3://bucket/honest.parquet"
            let substituted = ExternalOutcome.Succeeded "s3://attacker/swapped.parquet"

            let header =
                signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handleId signedOutcome "logs"

            let verification = verificationFor registry VerifyWhenPresented

            // The control.
            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId signedOutcome (Some header)
                |> run
            with
            | Ok(Some _) -> ()
            | other -> failtestf "CONTROL: the envelope must verify against its own body, got %A" other

            // The assertion.
            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId substituted (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.ArtifactHashMismatch(signed, actual)) ->
                Expect.notEqual signed actual "the digests differ, which is the whole point"
            | other -> failtestf "expected ArtifactHashMismatch, got %A" other
        }

        test "an envelope bound to ANOTHER handle is refused" {
            // The signature covers the handle id, so a genuine envelope
            // cannot be moved between handles even with an identical body.
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let outcome = ExternalOutcome.Succeeded "s3://out"
            let signedFor = Guid.NewGuid()

            let header =
                signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow signedFor outcome "logs"

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    (Guid.NewGuid())
                    outcome
                    (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.SignatureInvalid _) -> ()
            | other -> failtestf "an envelope replayed onto another handle must be refused, got %A" other
        }

        test "REVOKED key — a key that already verified stops verifying" {
            // Revoking a key that has demonstrably worked is what makes
            // this a revocation test rather than an "unknown key" test in
            // disguise.
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            let header =
                signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handleId outcome "logs"

            let verification = verificationFor registry VerifyWhenPresented

            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId outcome (Some header)
                |> run
            with
            | Ok(Some _) -> ()
            | other -> failtestf "CONTROL: the key must verify before revocation, got %A" other

            registry.Revoke("gpu-7", "k1", "worker node compromised") |> run |> ignore

            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId outcome (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.KeyRevoked(_, _, reason)) ->
                Expect.equal reason "worker node compromised" "the audit-facing reason survives"
            | other -> failtestf "expected KeyRevoked, got %A" other
        }

        test "a PENDING key is refused, and distinguishably from an unknown one" {
            let pair = newKeyPair ()
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry

            registry.EnrolOnFirstContact("gpu-7", "k1", WorkerKeyAlgorithm.Es256, pair.PublicKey)
            |> run
            |> ignore

            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            let header =
                signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handleId outcome "logs"

            let verification = verificationFor registry VerifyWhenPresented

            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId outcome (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.KeyNotApproved(_, _, status)) ->
                Expect.equal status "pending-approval" "the status is named"
            | other -> failtestf "expected KeyNotApproved, got %A" other

            // Approval flips it — the control that shows the enrolment was
            // otherwise sound.
            registry.Approve("gpu-7", "k1", "admin") |> run |> ignore

            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId outcome (Some header)
                |> run
            with
            | Ok(Some a) -> Expect.equal a.WorkerId "gpu-7" "approval is the only thing that was missing"
            | other -> failtestf "an approved key must verify, got %A" other
        }

        test "an UNKNOWN worker key is refused" {
            let pair = newKeyPair ()
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            let header =
                signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handleId outcome "logs"

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    handleId
                    outcome
                    (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.UnknownWorkerKey("gpu-7", "k1")) -> ()
            | other -> failtestf "expected UnknownWorkerKey, got %A" other
        }

        test "a signature by the WRONG key is refused" {
            let honest = newKeyPair ()
            let attacker = newKeyPair ()
            let registry = approvedRegistry honest "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"
            // Signed by the attacker's key, naming the honest worker.
            let header =
                signOutcome attacker "gpu-7" "k1" DateTimeOffset.UtcNow handleId outcome "logs"

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    handleId
                    outcome
                    (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.SignatureInvalid _) -> ()
            | other -> failtestf "expected SignatureInvalid, got %A" other
        }

        test "a DER-encoded ECDSA signature is refused with the encoding NAMED" {
            // The most likely real-world integration mistake (OpenSSL
            // emits DER by default). A message that did not name the
            // encoding would send the operator hunting a key problem.
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            let artifact = SignedOutcomeVerifier.artifactHash outcome |> Result.defaultValue ""

            let envelope = {
                WorkerId = "gpu-7"
                KeyId = "k1"
                SignedAt = DateTimeOffset.UtcNow.ToString "o"
                ArtifactHash = artifact
                DiagnosticsHash = sha256Hex "logs"
                Signature = "placeholder"
            }

            let der =
                pair.Key.SignData(
                    Encoding.UTF8.GetBytes(WorkerOutcomeSignature.signingPayload handleId envelope),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence
                )
                |> toBase64Url

            let header = WorkerOutcomeSignature.render { envelope with Signature = der }

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    handleId
                    outcome
                    (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.SignatureInvalid reason) ->
                Expect.stringContains reason "DER" "the refusal names the encoding"
                Expect.stringContains reason "P1363" "and the one it wanted"
            | other -> failtestf "expected SignatureInvalid naming the encoding, got %A" other
        }

        test "ed25519 is refused BY NAME by the in-tree verifier, and a companion can satisfy it" {
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry
            let material = Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)

            registry.Register("gpu-7", "ed", WorkerKeyAlgorithm.Ed25519, material, "ops")
            |> run
            |> ignore

            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            let envelope = {
                WorkerId = "gpu-7"
                KeyId = "ed"
                SignedAt = DateTimeOffset.UtcNow.ToString "o"
                ArtifactHash = SignedOutcomeVerifier.artifactHash outcome |> Result.defaultValue ""
                DiagnosticsHash = sha256Hex "logs"
                Signature = toBase64Url (Array.create 64 3uy)
            }

            let header = WorkerOutcomeSignature.render envelope

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    handleId
                    outcome
                    (Some header)
                |> run
            with
            | Error(SignedOutcomeRejection.SignatureInvalid reason) ->
                Expect.stringContains reason "ed25519" "the refusal names the algorithm"
                Expect.stringContains reason "GP 1" "and why the SDK core does not ship it"
            | other -> failtestf "the in-tree verifier must refuse ed25519 by name, got %A" other

            // The seam: a composed verifier handles the arm the default
            // cannot, and nothing else about the pipeline changes.
            let companion: WorkerSignatureVerifier =
                fun key payload signature ->
                    match key.Algorithm with
                    | WorkerKeyAlgorithm.Ed25519 -> Ok()
                    | _ -> WorkerSignature.bclVerifier key payload signature

            let composed = {
                verificationFor registry VerifyWhenPresented with
                    Verify = companion
            }

            match
                SignedOutcomeVerifier.verify composed "gpu-pool" false handleId outcome (Some header)
                |> run
            with
            | Ok(Some a) -> Expect.equal a.Algorithm WorkerKeyAlgorithm.Ed25519 "the composed verifier satisfied it"
            | other -> failtestf "a composed verifier must be able to satisfy ed25519, got %A" other
        }

        test "a STALE timestamp is refused in both directions" {
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"
            let now = DateTimeOffset.Parse "2026-08-04T12:00:00Z"

            let verification = {
                verificationFor registry VerifyWhenPresented with
                    Now = fun () -> now
                    MaxClockSkew = TimeSpan.FromMinutes 5.0
            }

            for label, signedAt in [ "too old", now.AddMinutes -6.0; "too far in the future", now.AddMinutes 6.0 ] do
                let header = signOutcome pair "gpu-7" "k1" signedAt handleId outcome "logs"

                match
                    SignedOutcomeVerifier.verify verification "gpu-pool" false handleId outcome (Some header)
                    |> run
                with
                | Error(SignedOutcomeRejection.StaleTimestamp _) -> ()
                | other -> failtestf "%s: expected StaleTimestamp, got %A" label other

            // Inside the window, the identical construction verifies — the
            // control that shows the window is a window and not a wall.
            let fresh =
                signOutcome pair "gpu-7" "k1" (now.AddMinutes -1.0) handleId outcome "logs"

            match
                SignedOutcomeVerifier.verify verification "gpu-pool" false handleId outcome (Some fresh)
                |> run
            with
            | Ok(Some _) -> ()
            | other -> failtestf "a timestamp inside the window must verify, got %A" other
        }

        test "an UNPARSEABLE timestamp is refused as itself" {
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            let envelope = {
                WorkerId = "gpu-7"
                KeyId = "k1"
                SignedAt = "not-a-time"
                ArtifactHash = SignedOutcomeVerifier.artifactHash outcome |> Result.defaultValue ""
                DiagnosticsHash = sha256Hex "logs"
                Signature = toBase64Url (Array.create 64 0uy)
            }

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    false
                    handleId
                    outcome
                    (Some(WorkerOutcomeSignature.render envelope))
                |> run
            with
            | Error(SignedOutcomeRejection.UnparseableTimestamp "not-a-time") -> ()
            | other -> failtestf "expected UnparseableTimestamp, got %A" other
        }

        test "486.C — the policy matrix, both directions" {
            let table = [
                NoSignedOutcomes, false, false
                NoSignedOutcomes, true, false
                VerifyWhenPresented, false, false
                VerifyWhenPresented, true, false
                RequireForIsolatingBackends, false, false
                RequireForIsolatingBackends, true, true
                RequireForAllBackends, false, true
                RequireForAllBackends, true, true
            ]

            for policy, isolating, expected in table do
                Expect.equal
                    (SignedOutcomeVerifier.requiresSignature policy isolating)
                    expected
                    $"%s{SignedOutcomePolicy.label policy} with isolating=%b{isolating}"
        }

        test "486.C — NoSignedOutcomes ignores a presented signature ENTIRELY" {
            // Not "accepts an invalid one" as a courtesy: the gate does not
            // run, which is what makes an opted-out deployment
            // byte-for-byte Phase 320.
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry NoSignedOutcomes)
                    "gpu-pool"
                    true
                    (Guid.NewGuid())
                    (ExternalOutcome.Succeeded "s3://out")
                    (Some "v=1,worker=liar,key=nope,t=x,artifact=y,diagnostics=z,sig=w")
                |> run
            with
            | Ok None -> ()
            | other -> failtestf "NoSignedOutcomes must not examine the header at all, got %A" other
        }

        test "486.C — an unsigned outcome is accepted or refused according to policy" {
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry
            let handleId = Guid.NewGuid()
            let outcome = ExternalOutcome.Succeeded "s3://out"

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry VerifyWhenPresented)
                    "gpu-pool"
                    true
                    handleId
                    outcome
                    None
                |> run
            with
            | Ok None -> ()
            | other -> failtestf "VerifyWhenPresented must accept an unsigned outcome, got %A" other

            match
                SignedOutcomeVerifier.verify
                    (verificationFor registry RequireForIsolatingBackends)
                    "clean-room-pool"
                    true
                    handleId
                    outcome
                    None
                |> run
            with
            | Error(SignedOutcomeRejection.SignatureRequired(backend, policy)) ->
                Expect.equal backend "clean-room-pool" "the refusal names the backend"
                Expect.equal policy "require-for-isolating-backends" "and the policy that demanded it"
            | other -> failtestf "an isolating backend's unsigned outcome must be refused, got %A" other
        }
    ]

// ── 486.B/C — over a real HTTP pipeline ──────────────────────────────

type private CapturingAuditLog() =
    let events = System.Collections.Concurrent.ConcurrentQueue<string * AuditEvent>()

    member _.Events = events |> List.ofSeq
    member this.Kinds = this.Events |> List.map (snd >> AuditEvent.eventTypeName)

    member this.RejectionReasons =
        this.Events
        |> List.choose (fun (_, e) ->
            match e with
            | ExternalCallbackRejected p -> Some p.Reason
            | _ -> None)

    member this.Resolutions =
        this.Events
        |> List.choose (fun (_, e) ->
            match e with
            | ExternalCallbackResolved p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId: string, audit: AuditEvent) = async { events.Enqueue((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private CapturingLogger() =
    let warns = System.Collections.Concurrent.ConcurrentQueue<string>()

    member _.Warnings = warns |> List.ofSeq

    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn msg = warns.Enqueue msg
        member _.Error(_, _) = ()

/// Records what it was asked to resolve. **The load-bearing assertion in
/// every refusal test is that this stayed empty** — a gate that audits a
/// refusal while still driving the run is precisely the defect a
/// status-code-only test cannot see.
type private ScriptedSink() =
    let calls = System.Collections.Concurrent.ConcurrentQueue<Guid * Guid>()

    member _.Calls = calls |> List.ofSeq

    interface IExternalCompletionSink with
        member _.ResolveExternal(handle: ExternalHandle, jobRunId: Guid, _outcome: ExternalOutcome) = async {
            calls.Enqueue((handle.HandleId, jobRunId))
            return ExternalResolution.Resolved "succeeded"
        }

/// A dispatcher that declares (or does not declare) the Phase 478
/// isolation posture — what `RequireForIsolatingBackends` keys on.
type private PostureDispatcher(isolating: bool) =
    interface IExternalComputeDispatcher with
        member _.Backend = "gpu-pool"
        member _.Submit(_, _) = async { return Error(ExternalComputeError.terminal "not used") }
        member _.Poll _ = async { return ExternalOutcome.Pending }
        member _.Cancel _ = async { return () }

    interface IIsolatedComputeBackend with
        member _.IsolationPosture =
            if isolating then
                IsolationPosture.clauses "test sandbox"
            else
                IsolationPosture.standardOnly

type private Harness = {
    Client: HttpClient
    Audit: CapturingAuditLog
    Logger: CapturingLogger
    Sink: ScriptedSink
    Store: IExternalHandleStore
    Dispose: unit -> unit
}

let private buildHarness
    (verification: SignedOutcomeVerification option)
    (dispatcher: IExternalComputeDispatcher option)
    : Harness =
    ExternalComputeCallback.resetThrottleState ()

    let audit = CapturingAuditLog()
    let logger = CapturingLogger()
    let sink = ScriptedSink()
    let store = InMemoryExternalHandleStore() :> IExternalHandleStore

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun (services: IServiceCollection) ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<IAuditLog>(audit :> IAuditLog) |> ignore
                        services.AddSingleton<ILogger>(logger :> ILogger) |> ignore
                        services.AddSingleton<IExternalHandleStore>(store) |> ignore

                        services.AddSingleton<IExternalCompletionSink>(sink :> IExternalCompletionSink)
                        |> ignore

                        verification
                        |> Option.iter (fun v -> services.AddSingleton<SignedOutcomeVerification>(v) |> ignore)

                        dispatcher
                        |> Option.iter (fun d -> services.AddSingleton<IExternalComputeDispatcher>(d) |> ignore))
                    .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe(choose ExternalComputeCallback.routes))
                |> ignore)
            .Build()

    host.Start()

    {
        Client = host.GetTestClient()
        Audit = audit
        Logger = logger
        Sink = sink
        Store = store
        Dispose = fun () -> host.Dispose()
    }

let private post (h: Harness) (secret: string) (signature: string option) (body: string) =
    let req =
        new HttpRequestMessage(
            HttpMethod.Post,
            ExternalCallback.Route,
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        )

    req.Headers.Add(ExternalCallback.SecretHeader, secret)

    signature
    |> Option.iter (fun s -> req.Headers.Add(WorkerOutcomeSignature.Header, s))

    h.Client.SendAsync req |> Async.AwaitTask |> run

let private bodyOf (response: HttpResponseMessage) =
    response.Content.ReadAsStringAsync() |> Async.AwaitTask |> run

let private registerHandle (h: Harness) =
    let handle = {
        HandleId = Guid.NewGuid()
        Backend = "gpu-pool"
        ScopeId = "team-alpha"
        NativeRef = Guid.NewGuid().ToString "N"
        SubmittedAt = DateTime.UtcNow
    }

    let runId = Guid.NewGuid()
    let secret, hash = ExternalCallbackSecret.mint ()
    h.Store.Register(handle, runId, hash) |> run
    handle, runId, secret

let private succeededBody (handleId: Guid) (resultRef: string) =
    sprintf """{"handleId":"%O","status":"succeeded","resultRef":"%s"}""" handleId resultRef

let private ingressTests =
    testList "486.B/C — the ingress gate over a real HTTP pipeline" [

        test "a signed callback resolves, and the attribution reaches the audit trail AND the response" {
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let h = buildHarness (Some(verificationFor registry RequireForAllBackends)) None

            try
                let handle, runId, secret = registerHandle h
                let outcome = ExternalOutcome.Succeeded "s3://out"

                let signature =
                    signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handle.HandleId outcome "logs"

                let response =
                    post h secret (Some signature) (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.OK "200"
                Expect.equal h.Sink.Calls [ handle.HandleId, runId ] "the sink was driven with the stored routing"
                Expect.stringContains (bodyOf response) "gpu-7" "the response echoes the verified worker"

                match h.Audit.Resolutions with
                | [ resolution ] ->
                    Expect.equal resolution.WorkerId (Some "gpu-7") "the audit row attributes the outcome"
                    Expect.equal resolution.WorkerKeyId (Some "k1") "and names the key"
                    Expect.equal resolution.SignatureAlgorithm (Some "es256") "and the algorithm"

                    Expect.equal
                        resolution.ArtifactHash
                        (SignedOutcomeVerifier.artifactHash outcome |> Result.toOption)
                        "and the artifact digest the worker committed to"
                | other -> failtestf "expected exactly one resolution audit, got %d" other.Length

                Expect.isEmpty h.Audit.RejectionReasons "and nothing was recorded as a refusal"
            finally
                h.Dispose()
        }

        test
            "TAMPERED body — a genuine signature over a substituted resultRef is refused 403 and never reaches the sink" {
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let h = buildHarness (Some(verificationFor registry VerifyWhenPresented)) None

            try
                let handle, _, secret = registerHandle h

                let signature =
                    signOutcome
                        pair
                        "gpu-7"
                        "k1"
                        DateTimeOffset.UtcNow
                        handle.HandleId
                        (ExternalOutcome.Succeeded "s3://honest")
                        "logs"

                let response =
                    post h secret (Some signature) (succeededBody handle.HandleId "s3://swapped")

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403, not 401 — the uniform refusal"

                // THE assertion: the gate held.
                Expect.isEmpty h.Sink.Calls "the sink was never driven"

                Expect.equal
                    h.Audit.RejectionReasons
                    [ "signature-artifact-mismatch" ]
                    "the refusal is audited under the reason worth alerting on"

                Expect.isEmpty h.Audit.Resolutions "and nothing was audited as a resolution"

                Expect.isTrue
                    (h.Logger.Warnings
                     |> List.exists (fun w -> w.Contains "callback_signature_refused" && w.Contains "REPLAY"))
                    "and the warning explains what a hash mismatch means"

                // The control: the SAME envelope over the body it was
                // signed for resolves. Without it, "refused" could be a
                // verifier that refuses everything.
                let control = buildHarness (Some(verificationFor registry VerifyWhenPresented)) None

                try
                    let handle2, _, secret2 = registerHandle control

                    let signature2 =
                        signOutcome
                            pair
                            "gpu-7"
                            "k1"
                            DateTimeOffset.UtcNow
                            handle2.HandleId
                            (ExternalOutcome.Succeeded "s3://honest")
                            "logs"

                    let ok =
                        post control secret2 (Some signature2) (succeededBody handle2.HandleId "s3://honest")

                    Expect.equal ok.StatusCode HttpStatusCode.OK "CONTROL: the honest body resolves"
                    Expect.equal control.Sink.Calls.Length 1 "CONTROL: and drives the sink"
                finally
                    control.Dispose()
            finally
                h.Dispose()
        }

        test "REVOKED key — 403, audited as revoked, sink untouched" {
            let pair = newKeyPair ()
            let registry = approvedRegistry pair "gpu-7" "k1"
            let h = buildHarness (Some(verificationFor registry VerifyWhenPresented)) None

            try
                let handle, _, secret = registerHandle h
                let outcome = ExternalOutcome.Succeeded "s3://out"

                let signature =
                    signOutcome pair "gpu-7" "k1" DateTimeOffset.UtcNow handle.HandleId outcome "logs"

                registry.Revoke("gpu-7", "k1", "node compromised") |> run |> ignore

                let response =
                    post h secret (Some signature) (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403"
                Expect.isEmpty h.Sink.Calls "the sink was never driven"
                Expect.equal h.Audit.RejectionReasons [ "signature-key-revoked" ] "audited as a revoked key"
            finally
                h.Dispose()
        }

        test "486.C — the SAME unsigned callback: 200 under VerifyWhenPresented, 403 under RequireForAllBackends" {
            // The policy, asserted in both directions over identical
            // material. Either direction alone is half a test.
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry

            let permissive =
                buildHarness (Some(verificationFor registry VerifyWhenPresented)) None

            try
                let handle, _, secret = registerHandle permissive

                let response =
                    post permissive secret None (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.OK "unsigned is accepted when policy permits"
                Expect.equal permissive.Sink.Calls.Length 1 "and the run resolves"
            finally
                permissive.Dispose()

            let strict =
                buildHarness (Some(verificationFor registry RequireForAllBackends)) None

            try
                let handle, _, secret = registerHandle strict
                let response = post strict secret None (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "unsigned is refused when policy demands"
                Expect.isEmpty strict.Sink.Calls "and the run does NOT resolve"
                Expect.equal strict.Audit.RejectionReasons [ "signature-required" ] "audited as a missing signature"
            finally
                strict.Dispose()
        }

        test "486.C — RequireForIsolatingBackends keys off the backend's DECLARED posture" {
            // Same policy, same unsigned callback, two dispatchers. The
            // isolating one demands a signature; the standard one does
            // not.
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry
            let policy = verificationFor registry RequireForIsolatingBackends

            let isolating = buildHarness (Some policy) (Some(PostureDispatcher true))

            try
                let handle, _, secret = registerHandle isolating
                let response = post isolating secret None (succeededBody handle.HandleId "s3://out")

                Expect.equal
                    response.StatusCode
                    HttpStatusCode.Forbidden
                    "an isolating backend's unsigned outcome never enters the platform"

                Expect.isEmpty isolating.Sink.Calls "the sink was never driven"
            finally
                isolating.Dispose()

            let standard = buildHarness (Some policy) (Some(PostureDispatcher false))

            try
                let handle, _, secret = registerHandle standard
                let response = post standard secret None (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.OK "a standard backend's unsigned outcome is accepted"
                Expect.equal standard.Sink.Calls.Length 1 "and resolves"
            finally
                standard.Dispose()

            // A deployment with no introspectable dispatcher declares
            // nothing, and therefore reads as non-isolating — forgetting to
            // declare is never mistaken for declaring.
            let undeclared = buildHarness (Some policy) None

            try
                let handle, _, secret = registerHandle undeclared

                let response =
                    post undeclared secret None (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.OK "an undeclared backend is read as standard-only"
            finally
                undeclared.Dispose()
        }

        test "GP 13 / GP 11 — with NO verification composed, a garbage signature header is not even read" {
            // The byte-for-byte claim. A header this malformed could not
            // survive any gate, so a 200 proves the gate does not exist on
            // an opted-out deployment.
            let h = buildHarness None None

            try
                let handle, runId, secret = registerHandle h

                let response =
                    post
                        h
                        secret
                        (Some "utter nonsense, not even a parameter list")
                        (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.OK "200 — Phase 320 unchanged"
                Expect.equal h.Sink.Calls [ handle.HandleId, runId ] "the run resolved"
                Expect.isEmpty h.Audit.RejectionReasons "nothing was refused"

                match h.Audit.Resolutions with
                | [ resolution ] ->
                    Expect.isNone resolution.WorkerId "and the audit row asserts NO attribution"
                    Expect.isNone resolution.SignatureAlgorithm "nor an algorithm"
                | other -> failtestf "expected one resolution audit, got %d" other.Length
            finally
                h.Dispose()
        }

        test "the signature gate runs AFTER the secret check — a wrong secret never reaches the registry" {
            // Ordering, made observable: a caller with a bad secret gets
            // the Phase 320 refusal reason, not a signature one, even
            // though its signature is also absent under a demanding
            // policy.
            let registry = InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry
            let h = buildHarness (Some(verificationFor registry RequireForAllBackends)) None

            try
                let handle, _, _ = registerHandle h
                let wrong, _ = ExternalCallbackSecret.mint ()
                let response = post h wrong None (succeededBody handle.HandleId "s3://out")

                Expect.equal response.StatusCode HttpStatusCode.Forbidden "403"

                Expect.equal
                    h.Audit.RejectionReasons
                    [ "secret-mismatch" ]
                    "transport auth refused it first; the signature gate never ran"

                Expect.isEmpty h.Sink.Calls "and nothing resolved"
            finally
                h.Dispose()
        }
    ]

let tests =
    testList "Phase 486 — signed worker outcomes" [
        envelopeTests
        registryContract "in-memory" (fun () -> InMemoryWorkerKeyRegistry() :> IWorkerKeyRegistry)
        registryContract "blob-backed" (fun () ->
            BlobWorkerKeyRegistry(InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage) :> IWorkerKeyRegistry)
        registryStructureTests
        verifierTests
        ingressTests
    ]