// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.BlobStorage

// ─── Phase 486 — the worker key registry ─────────────────────────────
//
// Phase 320's per-handle secret answers "may this caller resolve this
// handle". It cannot answer "which worker produced this result", because
// the secret is handed to a *backend* and every node behind that backend
// presents the same one. This registry is the missing identity: worker →
// public key, with an approval flow and first-class revocation.
//
// **Only public keys live here, and that is why there is no
// `ISecretStore` on this surface.** The private half never leaves the
// worker — that is the entire point of asymmetric attribution, and it is
// what distinguishes this from the Phase 320 secret (a shared bearer
// token the platform must hold a hash of). A registry that accepted
// private key material would let a compromised platform forge the very
// attribution it exists to prove. So custody here is *integrity*
// custody, not confidentiality custody: the store must be
// tamper-evident and durable, and the blob-backed implementation demands
// conditional writes for exactly that reason (below).
//
// **Two enrolment paths, one of which cannot self-authorise.**
//   * `Register` — the operator states the key out of band. Lands
//     `Approved` and immediately usable; the operator IS the authority.
//   * `EnrolOnFirstContact` — a worker presents a key the registry has
//     never seen. Lands `PendingApproval`, which **does not verify
//     anything**: an unapproved key is exactly as useless as an
//     unregistered one. What first contact buys is that the operator
//     approves a key they can see rather than transcribing one, which is
//     the difference between a five-second admin action and a
//     manual-key-distribution project nobody completes.
//
// **Revocation is sticky, and the stickiness is the feature.** Once a
// `(workerId, keyId)` pair is revoked it can never return to
// `Approved` — not by re-enrolment, not by a second `Approve`. A
// revocable-then-restorable key is a speed bump: the compromise that
// caused the revocation is precisely a capability to re-present that
// key, so a first-contact path that could resurrect it would hand the
// attacker the undo button. Rotation is a NEW `keyId`, which is why the
// envelope names the key rather than only the worker.
//
// **Key material is validated at registration, not at first callback.**
// A malformed public key that is discovered when a signature arrives is
// discovered inside an unauthenticated request path, hours after the
// mistake, and presents as "the worker's signatures are all rejected".
// Validated at the point of registration it presents as "this key is not
// a P-256 SPKI", to the operator who just typed it — the Phase 465-class
// preflight posture applied to a store.

/// Phase 486 — where a registered worker key sits in its lifecycle.
///
/// `[<RequireQualifiedAccess>]` because `Approved` already names a case on
/// the model-registry approval union in this same namespace, and an
/// unqualified second one is how a call site silently binds the union you
/// did not mean.
[<RequireQualifiedAccess>]
type WorkerKeyStatus =
    /// Enrolled at first contact, awaiting an admin decision. **Not
    /// usable** — a signature naming this key is refused exactly as one
    /// naming an unknown key is.
    | PendingApproval
    /// Usable. Reached by operator registration, or by an admin approving
    /// a first-contact enrolment.
    | Approved
    /// Withdrawn. Terminal — see the file header for why this never
    /// returns to `Approved`.
    | Revoked of reason: string * at: DateTime

[<RequireQualifiedAccess>]
module WorkerKeyStatus =
    /// Stable lowercase label for logs / audit payloads / admin panels.
    let label =
        function
        | WorkerKeyStatus.PendingApproval -> "pending-approval"
        | WorkerKeyStatus.Approved -> "approved"
        | WorkerKeyStatus.Revoked _ -> "revoked"

/// Phase 486 — one registered worker signing key.
///
/// Identity by value (GP 12 rule 1): primitives and a value DU, so the
/// record round-trips through blob storage and survives a restart.
type WorkerSigningKey = {
    /// Stable worker identity — a pool node name, a container identity, a
    /// fleet member id. Deployment-scoped, **not** tenant-scoped: see
    /// `IWorkerKeyRegistry` for why that does not weaken GP 4.
    WorkerId: string
    /// Identity of this key among the worker's keys. Rotation mints a new
    /// one; the old one is revoked rather than replaced in place.
    KeyId: string
    /// Which algorithm this key verifies under. Read from the registry,
    /// never from the request (a caller-named algorithm is a downgrade
    /// oracle).
    Algorithm: WorkerKeyAlgorithm
    /// Base64 (standard, padded) public key material — DER
    /// SubjectPublicKeyInfo for `Es256`, the raw 32 bytes for `Ed25519`.
    /// **Public** material only; the private half never reaches the
    /// platform.
    PublicKey: string
    Status: WorkerKeyStatus
    /// When the key was first recorded (UTC), whichever path recorded it.
    EnrolledAt: DateTime
    /// Who approved it. `None` while `PendingApproval`; `Some` for an
    /// operator registration (the operator's own identity) and for an
    /// approved first-contact enrolment (GP 6 — an approval nobody is
    /// named for is not an approval).
    ApprovedBy: string option
}

/// Phase 486 — why a registry operation was refused.
///
/// A typed DU rather than a `string` because three of these cases are
/// **security-relevant and distinguishable**, and a caller (an admin API,
/// an audit emitter) has to tell them apart: a key conflict is a
/// substitution attempt, a revoked key is a compromise being re-presented,
/// and an unknown key is routine.
[<RequireQualifiedAccess>]
type WorkerKeyError =
    /// No key is registered for this `(workerId, keyId)` pair.
    | UnknownKey of workerId: string * keyId: string
    /// A key IS registered for this pair, with **different** material.
    /// The key-substitution shape: a caller re-presenting a known key id
    /// with its own public key. Never resolved by overwriting.
    | KeyConflict of workerId: string * keyId: string
    /// The pair is revoked. Terminal for that key id — rotate to a new
    /// one.
    | KeyRevoked of workerId: string * keyId: string * reason: string
    /// The supplied public key is not valid material for its algorithm.
    | InvalidPublicKey of reason: string
    /// The identifiers are not usable (blank, over-long, or outside the
    /// safe charset the signature envelope permits).
    | InvalidIdentifier of reason: string
    /// The backing store failed for a reason unrelated to the request.
    | StoreFailure of message: string

[<RequireQualifiedAccess>]
module WorkerKeyError =
    /// One-line operator-readable description. Every case NAMES what did
    /// not match — an admin who cannot tell a substitution attempt from a
    /// typo has to escalate to make any progress.
    let describe (error: WorkerKeyError) : string =
        match error with
        | WorkerKeyError.UnknownKey(workerId, keyId) ->
            $"no signing key '%s{keyId}' is registered for worker '%s{workerId}'"
        | WorkerKeyError.KeyConflict(workerId, keyId) ->
            $"worker '%s{workerId}' already has a key '%s{keyId}' registered with DIFFERENT public material; this is the key-substitution shape and is refused. Revoke the existing key and enrol a new key id — a registered key's material is never overwritten."
        | WorkerKeyError.KeyRevoked(workerId, keyId, reason) ->
            $"signing key '%s{keyId}' for worker '%s{workerId}' is REVOKED (%s{reason}) and cannot be restored. Enrol a new key id; revocation is deliberately terminal for the key it names."
        | WorkerKeyError.InvalidPublicKey reason -> $"the supplied public key is not usable: %s{reason}"
        | WorkerKeyError.InvalidIdentifier reason -> $"invalid identifier: %s{reason}"
        | WorkerKeyError.StoreFailure message -> $"the worker key registry store failed: %s{message}"

    /// Stable lowercase label for the audit `Reason` field / metrics.
    let label (error: WorkerKeyError) : string =
        match error with
        | WorkerKeyError.UnknownKey _ -> "unknown-key"
        | WorkerKeyError.KeyConflict _ -> "key-conflict"
        | WorkerKeyError.KeyRevoked _ -> "key-revoked"
        | WorkerKeyError.InvalidPublicKey _ -> "invalid-public-key"
        | WorkerKeyError.InvalidIdentifier _ -> "invalid-identifier"
        | WorkerKeyError.StoreFailure _ -> "store-failure"

/// Phase 486 — parsing and validating worker public key material.
///
/// **BCL only (GP 1).** `Es256` imports through
/// `System.Security.Cryptography`; `Ed25519` has no BCL primitive on
/// .NET 10, so validation here is structural (a 32-byte key) and the
/// *verification* is a composed companion's job. That split is the honest
/// one: the registry can tell an operator "that is not 32 bytes" without
/// pretending it can tell them "that is a valid curve point".
[<RequireQualifiedAccess>]
module WorkerPublicKey =
    /// Ed25519 public keys are exactly 32 bytes (RFC 8032 §5.1.5).
    [<Literal>]
    let Ed25519KeyBytes = 32

    /// Decode the base64 material, or say why it is not base64.
    let tryDecode (material: string) : Result<byte[], string> =
        if String.IsNullOrWhiteSpace material then
            Error "the public key is empty"
        else
            try
                Ok(Convert.FromBase64String(material.Trim()))
            with _ ->
                Error "the public key is not valid base64"

    /// Import `Es256` material as a P-256 verification key. The CALLER
    /// owns the returned `ECDsa` and must dispose it — stated here because
    /// the verify path creates one per verification (GP 12 rule 4: no key
    /// state cached between calls, exactly as `DefaultArtefactSigner`
    /// re-reads its material per `Sign`).
    ///
    /// The curve is checked, not assumed: `ImportSubjectPublicKeyInfo`
    /// accepts P-384 and P-521 SPKI blobs happily, and a key on a curve
    /// the algorithm label does not name is a mismatch an operator wants
    /// told rather than a signature that mysteriously never verifies.
    let tryImportEs256 (material: string) : Result<ECDsa, string> =
        match tryDecode material with
        | Error e -> Error e
        | Ok bytes ->
            let key = ECDsa.Create()

            try
                let mutable read = 0
                key.ImportSubjectPublicKeyInfo(ReadOnlySpan bytes, &read)

                if key.KeySize <> 256 then
                    key.Dispose()
                    Error $"es256 requires a NIST P-256 key; this key is %d{key.KeySize}-bit"
                else
                    Ok key
            with ex ->
                key.Dispose()
                Error $"es256 material is not a DER SubjectPublicKeyInfo public key (%s{ex.Message})"

    /// Validate material for `algorithm` without retaining anything.
    /// `Ok ()` means the registry may store it.
    let validate (algorithm: WorkerKeyAlgorithm) (material: string) : Result<unit, string> =
        match algorithm with
        | WorkerKeyAlgorithm.Es256 ->
            match tryImportEs256 material with
            | Error e -> Error e
            | Ok key ->
                key.Dispose()
                Ok()
        | WorkerKeyAlgorithm.Ed25519 ->
            match tryDecode material with
            | Error e -> Error e
            | Ok bytes when bytes.Length <> Ed25519KeyBytes ->
                Error $"ed25519 requires a %d{Ed25519KeyBytes}-byte public key; this key is %d{bytes.Length} bytes"
            | Ok _ -> Ok()

/// Phase 486 — worker identity → public key, with an approval flow and
/// first-class revocation.
///
/// **Deployment-scoped, not tenant-scoped, and GP 4 is unaffected.** A
/// worker is a piece of the deployment's compute fleet, not a tenant's
/// asset: the same GPU node legitimately serves work for many scopes, so
/// keying the registry by scope would either duplicate every key per
/// tenant or invent a tenancy the fleet does not have. The tenant boundary
/// stays exactly where Phase 320 put it — the *handle record's* `ScopeId`,
/// which the callback path reads from platform state and never from the
/// request. This registry answers "who signed", never "what may they
/// touch".
///
/// **Six portability rules (GP 12).** 1. Identity by value — strings and
/// value records throughout; no key handle crosses the boundary.
/// 2. Async at every boundary. 3. Failure is data (`WorkerKeyError`), not
/// an exception or a callback. 4. Stateless between invocations — every
/// call carries its whole key. 5. No cross-key ordering promised;
/// atomicity is per `(workerId, keyId)`. 6. No timing primitive on the
/// surface, so no precision to declare.
type IWorkerKeyRegistry =
    /// **Operator registration.** Records `publicKey` for
    /// `(workerId, keyId)` as `Approved` and immediately usable.
    ///
    /// Refuses `KeyConflict` when the pair exists with different material
    /// and `KeyRevoked` when it is revoked — a registration never
    /// overwrites either, so an admin endpoint cannot be talked into
    /// laundering a substitution as a re-registration. Re-registering the
    /// IDENTICAL material is idempotent `Ok`.
    abstract Register:
        workerId: string * keyId: string * algorithm: WorkerKeyAlgorithm * publicKey: string * registeredBy: string ->
            Async<Result<WorkerSigningKey, WorkerKeyError>>

    /// **First-contact enrolment.** Records a key the registry has never
    /// seen as `PendingApproval` — visible to an admin, and useless until
    /// approved.
    ///
    /// Create-only by construction: an existing pair is answered
    /// idempotently when the material matches, `KeyConflict` when it does
    /// not, and `KeyRevoked` when it is revoked. It can therefore never
    /// resurrect a revoked key or displace a registered one, which is what
    /// makes exposing it to unauthenticated first contact defensible.
    abstract EnrolOnFirstContact:
        workerId: string * keyId: string * algorithm: WorkerKeyAlgorithm * publicKey: string ->
            Async<Result<WorkerSigningKey, WorkerKeyError>>

    /// Approve a `PendingApproval` key. `approvedBy` is recorded (GP 6).
    /// Approving an already-`Approved` key is idempotent `Ok`; approving a
    /// revoked one is `KeyRevoked`.
    abstract Approve:
        workerId: string * keyId: string * approvedBy: string -> Async<Result<WorkerSigningKey, WorkerKeyError>>

    /// Revoke a key, whatever its current status. Terminal — see the file
    /// header. Revoking an already-revoked key is idempotent `Ok` and
    /// keeps the FIRST reason and timestamp, because the first revocation
    /// is the one an incident reconstruction needs.
    abstract Revoke:
        workerId: string * keyId: string * reason: string -> Async<Result<WorkerSigningKey, WorkerKeyError>>

    /// Look one key up. `None` for an unknown pair. Returns the record
    /// whatever its status — the *verifier* decides what a status means, so
    /// that a refusal can say "revoked" rather than "unknown" in the audit
    /// trail while still refusing uniformly on the wire.
    abstract Resolve: workerId: string * keyId: string -> Async<WorkerSigningKey option>

    /// Every registered key, for an admin surface. Includes pending and
    /// revoked keys — a list that hid them would hide precisely the rows
    /// an operator opened it to act on.
    abstract List: unit -> Async<WorkerSigningKey list>

    /// Whether the registry is shared across replicas, declared as data
    /// rather than inferred from the runtime type (GP 12, mirroring
    /// `IExternalHandleStore.IsDistributed`). `false` ⇒ a revocation
    /// applies to one replica only, which for a revocation is the
    /// difference between a control and a decoration.
    abstract IsDistributed: bool

[<RequireQualifiedAccess>]
module WorkerKeyRegistry =
    /// Shared identifier + material validation, applied by both
    /// implementations before anything is stored.
    ///
    /// Identifiers are held to the SAME charset the signature envelope
    /// permits (`WorkerOutcomeSignature.isSafeIdentifier`). A key
    /// registered under an identifier no envelope can carry is a key that
    /// can never be used, and discovering that at first callback rather
    /// than at registration is the failure this shares with the material
    /// check.
    let validate
        (workerId: string)
        (keyId: string)
        (algorithm: WorkerKeyAlgorithm)
        (publicKey: string)
        : Result<unit, WorkerKeyError> =
        if not (WorkerOutcomeSignature.isSafeIdentifier workerId) then
            Error(
                WorkerKeyError.InvalidIdentifier
                    $"worker id '%s{workerId}' is empty, over %d{WorkerOutcomeSignature.MaxIdentifierLength} characters, or outside the signature envelope's charset (A-Z a-z 0-9 . _ : -)"
            )
        elif not (WorkerOutcomeSignature.isSafeIdentifier keyId) then
            Error(
                WorkerKeyError.InvalidIdentifier
                    $"key id '%s{keyId}' is empty, over %d{WorkerOutcomeSignature.MaxIdentifierLength} characters, or outside the signature envelope's charset (A-Z a-z 0-9 . _ : -)"
            )
        else
            WorkerPublicKey.validate algorithm publicKey
            |> Result.mapError WorkerKeyError.InvalidPublicKey

    /// Does `presented` match `existing`'s material exactly?
    ///
    /// An exact string compare on the stored base64, deliberately: two
    /// different encodings of the same key are a re-registration an
    /// operator should make consistent, not something to normalise away
    /// behind their back. There is no secret here, so no constant-time
    /// requirement.
    let sameMaterial (existing: WorkerSigningKey) (presented: string) : bool =
        existing.PublicKey = (if isNull presented then "" else presented.Trim())

    /// The one place a stored record's status decides whether it may
    /// verify a signature. `Approved` only.
    let isUsable (key: WorkerSigningKey) : bool =
        match key.Status with
        | WorkerKeyStatus.Approved -> true
        | WorkerKeyStatus.PendingApproval
        | WorkerKeyStatus.Revoked _ -> false

    /// One-line description for logs / admin panels.
    let describe (key: WorkerSigningKey) : string =
        let status =
            match key.Status with
            | WorkerKeyStatus.Revoked(reason, at) -> $"revoked at %O{at} (%s{reason})"
            | other -> WorkerKeyStatus.label other

        $"worker '%s{key.WorkerId}' key '%s{key.KeyId}' (%s{WorkerKeyAlgorithm.label key.Algorithm}, %s{status})"

    /// Apply an enrolment / registration / approval / revocation to
    /// whatever is currently stored, without touching a store.
    ///
    /// **Both implementations share this, and that is the point.** The
    /// lifecycle rules — revocation is sticky, material is never
    /// overwritten, re-presenting identical material is idempotent — are
    /// the security content of this file, and two copies of them is one
    /// copy too many. The in-memory and blob stores differ only in how
    /// they make the read-decide-write atomic.
    let internal decide
        (existing: WorkerSigningKey option)
        (proposed: WorkerSigningKey)
        : Result<WorkerSigningKey, WorkerKeyError> =
        match existing with
        | None -> Ok proposed
        | Some current ->
            match current.Status with
            | WorkerKeyStatus.Revoked(reason, at) ->
                match proposed.Status with
                // Revoking a revoked key keeps the FIRST reason.
                | WorkerKeyStatus.Revoked _ ->
                    Ok {
                        current with
                            Status = WorkerKeyStatus.Revoked(reason, at)
                    }
                | _ -> Error(WorkerKeyError.KeyRevoked(current.WorkerId, current.KeyId, reason))
            | _ when not (sameMaterial current proposed.PublicKey) ->
                match proposed.Status with
                // A revocation does not need to restate the material.
                | WorkerKeyStatus.Revoked _ ->
                    Ok {
                        current with
                            Status = proposed.Status
                    }
                | _ -> Error(WorkerKeyError.KeyConflict(current.WorkerId, current.KeyId))
            | _ ->
                match proposed.Status with
                | WorkerKeyStatus.Revoked _ ->
                    Ok {
                        current with
                            Status = proposed.Status
                    }
                | WorkerKeyStatus.Approved ->
                    Ok {
                        current with
                            Status = WorkerKeyStatus.Approved
                            ApprovedBy = proposed.ApprovedBy |> Option.orElse current.ApprovedBy
                    }
                // A repeat first-contact enrolment must not DEMOTE an
                // approved key back to pending — that would make an
                // unauthenticated request able to switch a working worker
                // off.
                | WorkerKeyStatus.PendingApproval -> Ok current

/// Phase 486 — process-local `IWorkerKeyRegistry`. Correct and
/// allocation-cheap for a single-instance deployment, and the registry the
/// contract-shaped tests exercise; **not** distributed
/// (`IsDistributed = false`) and **not** durable — a restart loses every
/// registration, so every signature is then refused as an unknown key
/// until the operator re-registers.
///
/// **Read the `IsDistributed = false` as a warning about revocation
/// specifically.** Losing registrations on restart fails closed (nothing
/// verifies). Holding them per-replica fails *open* in one direction: a
/// revocation applied on replica A leaves replica B still accepting the
/// revoked key. A multi-replica deployment therefore wants the blob
/// registry, and compose says so rather than leaving it to be inferred.
type InMemoryWorkerKeyRegistry() =
    // Documented mutable-state exception to GP 5, the same one
    // `InMemoryExternalHandleStore` takes: a registry IS state, and this
    // dictionary is the whole of it. Concurrent by construction, and the
    // read-decide-write is serialised per key by `AddOrUpdate`'s own
    // per-bucket lock, so two racing enrolments cannot both create.
    let keys = ConcurrentDictionary<string * string, WorkerSigningKey>()

    /// Live key count. Exposed so a test can assert a registration
    /// happened rather than inferring it from a downstream effect.
    member _.Count = keys.Count

    /// Apply `proposed` under the lifecycle rules, atomically per key.
    ///
    /// `AddOrUpdate`'s update delegate may run more than once under
    /// contention, so it must be free of side effects — it is: `decide` is
    /// pure. The refusal is carried out of the delegate in a cell rather
    /// than thrown, because throwing out of `AddOrUpdate` leaves the
    /// dictionary in whichever state the partial application reached.
    member private _.Apply(proposed: WorkerSigningKey) : Result<WorkerSigningKey, WorkerKeyError> =
        // A `ref` cell rather than a `let mutable`, because F# cannot
        // capture a mutable local in a closure and both delegates are
        // closures.
        let refusal = ref ValueNone

        let applied =
            keys.AddOrUpdate(
                (proposed.WorkerId, proposed.KeyId),
                (fun _ ->
                    refusal.Value <- ValueNone
                    proposed),
                (fun _ current ->
                    match WorkerKeyRegistry.decide (Some current) proposed with
                    | Ok next ->
                        refusal.Value <- ValueNone
                        next
                    | Error e ->
                        refusal.Value <- ValueSome e
                        current)
            )

        match refusal.Value with
        | ValueSome e -> Error e
        | ValueNone -> Ok applied

    interface IWorkerKeyRegistry with
        member _.IsDistributed = false

        member this.Register(workerId, keyId, algorithm, publicKey, registeredBy) = async {
            match WorkerKeyRegistry.validate workerId keyId algorithm publicKey with
            | Error e -> return Error e
            | Ok() ->
                return
                    this.Apply {
                        WorkerId = workerId
                        KeyId = keyId
                        Algorithm = algorithm
                        PublicKey = publicKey.Trim()
                        Status = WorkerKeyStatus.Approved
                        EnrolledAt = DateTime.UtcNow
                        ApprovedBy = Some registeredBy
                    }
        }

        member this.EnrolOnFirstContact(workerId, keyId, algorithm, publicKey) = async {
            match WorkerKeyRegistry.validate workerId keyId algorithm publicKey with
            | Error e -> return Error e
            | Ok() ->
                return
                    this.Apply {
                        WorkerId = workerId
                        KeyId = keyId
                        Algorithm = algorithm
                        PublicKey = publicKey.Trim()
                        Status = WorkerKeyStatus.PendingApproval
                        EnrolledAt = DateTime.UtcNow
                        ApprovedBy = None
                    }
        }

        member this.Approve(workerId, keyId, approvedBy) = async {
            match keys.TryGetValue((workerId, keyId)) with
            | false, _ -> return Error(WorkerKeyError.UnknownKey(workerId, keyId))
            | true, current ->
                return
                    this.Apply {
                        current with
                            Status = WorkerKeyStatus.Approved
                            ApprovedBy = Some approvedBy
                    }
        }

        member this.Revoke(workerId, keyId, reason) = async {
            match keys.TryGetValue((workerId, keyId)) with
            | false, _ -> return Error(WorkerKeyError.UnknownKey(workerId, keyId))
            | true, current ->
                return
                    this.Apply {
                        current with
                            Status = WorkerKeyStatus.Revoked(reason, DateTime.UtcNow)
                    }
        }

        member _.Resolve(workerId, keyId) = async {
            match keys.TryGetValue((workerId, keyId)) with
            | true, key -> return Some key
            | false, _ -> return None
        }

        member _.List() = async { return keys.Values |> List.ofSeq }

/// Phase 486 — the durable, distributed-ready `IWorkerKeyRegistry`
/// (`IsDistributed = true`). One blob per key in the reserved `_platform`
/// container:
///
///   * `external-compute/worker-keys/{workerId}/{keyId}.json`
///
/// Partitioned by worker rather than by scope, because a worker is not a
/// tenant's asset (see `IWorkerKeyRegistry`). Nothing in here is secret,
/// so the partitioning is for enumeration convenience, not isolation.
///
/// **Conditional writes are a hard requirement, checked at construction —
/// and here it is REVOCATION that demands them.** Every mutation is a
/// read-decide-write over one blob, and the decision it makes is a
/// security decision: "is this key already revoked", "does this material
/// match". A download-modify-upload fallback loses that race exactly where
/// it matters — a first-contact enrolment landing concurrently with a
/// revocation would overwrite the revocation and re-open the key. Refused
/// loudly at compose time rather than at the first race, on the
/// `BlobExternalHandleStore` precedent.
type BlobWorkerKeyRegistry(blobs: IBlobStorage, now: unit -> DateTime) =
    let container = "_platform"
    let prefix = "external-compute/worker-keys/"

    let cas =
        match box blobs with
        | :? IConditionalBlobStorage as c -> c
        | _ ->
            invalidArg
                "blobs"
                "BlobWorkerKeyRegistry requires an IBlobStorage that also implements IConditionalBlobStorage (conditional writes). Every mutation is a read-decide-write whose decision is a security decision — is this key revoked, does this material match — and a download-modify-upload fallback lets a concurrent first-contact enrolment overwrite a revocation and re-open a compromised key. Use InMemoryWorkerKeyRegistry for a single-instance deployment, or a blob backend with conditional-write support."

    let json = FableConverters.create ()

    let keyName (workerId: string) (keyId: string) =
        $"%s{prefix}%s{workerId}/%s{keyId}.json"

    let tryDecode (bytes: byte[]) : WorkerSigningKey option =
        try
            Some(JsonSerializer.Deserialize<WorkerSigningKey>(Encoding.UTF8.GetString bytes, json))
        with _ ->
            None

    let encode (key: WorkerSigningKey) =
        JsonSerializer.Serialize(key, json) |> Encoding.UTF8.GetBytes

    new(blobs: IBlobStorage) = BlobWorkerKeyRegistry(blobs, (fun () -> DateTime.UtcNow))

    /// Build the registry when `blobs` supports conditional writes, `None`
    /// otherwise — the probing form, for a compose path that falls back to
    /// the in-memory registry rather than failing the boot.
    static member TryCreate(blobs: IBlobStorage) : IWorkerKeyRegistry option =
        match box blobs with
        | :? IConditionalBlobStorage -> Some(BlobWorkerKeyRegistry blobs :> IWorkerKeyRegistry)
        | _ -> None

    /// Read-decide-CAS, retried on a genuine concurrent write. Bounded, so
    /// a pathological caller cannot spin a request thread indefinitely.
    member private _.Apply
        (workerId: string, keyId: string, propose: WorkerSigningKey option -> WorkerSigningKey option)
        : Async<Result<WorkerSigningKey, WorkerKeyError>> =
        async {
            let name = keyName workerId keyId
            let mutable attempts = 0
            let mutable result = ValueNone

            while result.IsNone && attempts < 8 do
                attempts <- attempts + 1

                let! current = cas.DownloadWithETag(container, name)

                let existing, condition =
                    match current with
                    | Ok(bytes, etag) -> tryDecode bytes, IfMatch etag
                    // An absent blob and an UNDECODABLE one both take the
                    // create path. That is deliberate: `IfAbsent` then
                    // fails against the corrupt blob rather than
                    // overwriting it, so a damaged record surfaces as a
                    // refusal an operator can see instead of being
                    // silently replaced with attacker-supplied material.
                    | Error _ -> None, IfAbsent

                match propose existing with
                | None ->
                    result <-
                        ValueSome(
                            Error(
                                match existing with
                                | Some key -> WorkerKeyError.UnknownKey(key.WorkerId, key.KeyId)
                                | None -> WorkerKeyError.UnknownKey(workerId, keyId)
                            )
                        )
                | Some proposed ->
                    match WorkerKeyRegistry.decide existing proposed with
                    | Error e -> result <- ValueSome(Error e)
                    | Ok next ->
                        let! written = cas.UploadWithETag(container, name, encode next, condition)

                        match written with
                        | Ok _ -> result <- ValueSome(Ok next)
                        | Error(ETagMismatch _) -> () // lost the race; re-read and re-decide
                        | Error(ConditionalWriteFailure message) ->
                            result <- ValueSome(Error(WorkerKeyError.StoreFailure message))

            return
                result
                |> ValueOption.defaultValue (
                    Error(
                        WorkerKeyError.StoreFailure
                            $"could not apply the change to worker '%s{workerId}' key '%s{keyId}' after 8 attempts against concurrent writers"
                    )
                )
        }

    interface IWorkerKeyRegistry with
        member _.IsDistributed = true

        member this.Register(workerId, keyId, algorithm, publicKey, registeredBy) = async {
            match WorkerKeyRegistry.validate workerId keyId algorithm publicKey with
            | Error e -> return Error e
            | Ok() ->
                return!
                    this.Apply(
                        workerId,
                        keyId,
                        fun _ ->
                            Some {
                                WorkerId = workerId
                                KeyId = keyId
                                Algorithm = algorithm
                                PublicKey = publicKey.Trim()
                                Status = WorkerKeyStatus.Approved
                                EnrolledAt = now ()
                                ApprovedBy = Some registeredBy
                            }
                    )
        }

        member this.EnrolOnFirstContact(workerId, keyId, algorithm, publicKey) = async {
            match WorkerKeyRegistry.validate workerId keyId algorithm publicKey with
            | Error e -> return Error e
            | Ok() ->
                return!
                    this.Apply(
                        workerId,
                        keyId,
                        fun _ ->
                            Some {
                                WorkerId = workerId
                                KeyId = keyId
                                Algorithm = algorithm
                                PublicKey = publicKey.Trim()
                                Status = WorkerKeyStatus.PendingApproval
                                EnrolledAt = now ()
                                ApprovedBy = None
                            }
                    )
        }

        member this.Approve(workerId, keyId, approvedBy) =
            this.Apply(
                workerId,
                keyId,
                Option.map (fun current -> {
                    current with
                        Status = WorkerKeyStatus.Approved
                        ApprovedBy = Some approvedBy
                })
            )

        member this.Revoke(workerId, keyId, reason) =
            this.Apply(
                workerId,
                keyId,
                Option.map (fun current -> {
                    current with
                        Status = WorkerKeyStatus.Revoked(reason, now ())
                })
            )

        member _.Resolve(workerId, keyId) = async {
            let! stored = blobs.Download(container, keyName workerId keyId)

            match stored |> Result.toOption |> Option.bind tryDecode with
            | None -> return None
            | Some key ->
                // Cross-check: the record must be the one asked for. A
                // blob copied between partitions is refused rather than
                // followed, the same posture `BlobExternalHandleStore`
                // takes on its pointer.
                if key.WorkerId = workerId && key.KeyId = keyId then
                    return Some key
                else
                    return None
        }

        member _.List() = async {
            let! names = blobs.List(container, prefix)

            let! keys =
                names
                |> List.map (fun name -> async {
                    let! stored = blobs.Download(container, name)
                    return stored |> Result.toOption |> Option.bind tryDecode
                })
                |> Async.Sequential

            return keys |> Array.toList |> List.choose id
        }