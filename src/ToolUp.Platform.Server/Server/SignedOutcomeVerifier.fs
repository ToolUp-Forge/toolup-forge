// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Globalization
open System.Security.Cryptography
open System.Text

// ─── Phase 486 — verifying a signed worker outcome ───────────────────
//
// The other half of `WorkerOutcomeSignature` (Core): given a presented
// envelope, the callback body's terminal outcome, and the worker key
// registry, decide whether the platform may attribute this outcome to that
// worker — and refuse in a way that NAMES what did not match.
//
// **Order of the gates, and why it is this order.**
//   1. policy — is a signature required / permitted / ignored at all? An
//      opted-out deployment leaves here having done nothing.
//   2. parse the envelope — a shape error is a misconfigured worker far
//      more often than an attack.
//   3. resolve the key — status decides usability: `PendingApproval` and
//      `Revoked` are both refusals, distinguished in the audit trail and
//      indistinguishable on the wire.
//   4. **artifact-hash match** — before any crypto. A body that does not
//      hash to the signed digest is refused without a curve operation,
//      which is both cheaper and the honest ordering: the signature is
//      only meaningful once we know WHAT it would be attesting to.
//   5. freshness — the signed timestamp against the platform clock.
//   6. the signature itself.
//
// **Why the hash check precedes the signature check and not the reverse.**
// Both must pass, so the order is not a correctness question — it is a
// question of what a rejection *means*. `ArtifactHashMismatch` says "a
// genuine signature was replayed against a substituted body";
// `SignatureInvalid` says "this is not a signature by that key". Checking
// the hash first means the first of those is reported as itself rather
// than as the second, and a relay-substitution incident does not read in
// the audit trail as a key problem.
//
// **`Ed25519` is registrable and NOT verifiable in-tree.** .NET 10's BCL
// ships no Ed25519 primitive (probed: `System.Security.Cryptography`
// 10.0.0.0 exposes four `ECDsa*` types and zero `Ed*`), and pulling
// BouncyCastle or NSec into `ToolUp.Platform.Server` would put a
// third-party crypto stack in the SDK core — a GP 1 violation for a
// capability the overwhelming majority of deployments never compose. So
// the default verifier refuses `Ed25519` **by name**, naming the fix
// (compose a verifier), and `WorkerSignatureVerifier` is a structural
// function seam a companion satisfies — the same GP 1 decoupling
// `VerifyDetachedJws` uses for detached-JWS artefacts. An algorithm that
// silently never verified would be strictly worse than one that says why.

/// Phase 486 — the signature-verification seam: given the registered key,
/// the exact signing payload, and the presented base64url signature,
/// `Ok ()` or a failure description.
///
/// **A structural function, not an interface over a crypto stack**, so a
/// deployment can satisfy `Ed25519` (or a future algorithm) from a
/// companion without `ToolUp.Platform.Server` gaining a crypto dependency.
/// Compose one by wrapping the default:
///
/// ```fsharp skip=fragment
/// let verify : WorkerSignatureVerifier =
///     fun key payload signature ->
///         match key.Algorithm with
///         | WorkerKeyAlgorithm.Ed25519 -> myEd25519Verify key.PublicKey payload signature
///         | _ -> WorkerSignature.bclVerifier key payload signature
/// ```
type WorkerSignatureVerifier = WorkerSigningKey -> string -> string -> Result<unit, string>

/// Phase 486 — the in-tree, BCL-only signature verifier.
[<RequireQualifiedAccess>]
module WorkerSignature =

    /// Decode unpadded base64url. `Error` for anything that is not.
    ///
    /// Hand-rolled rather than reaching for a newer BCL helper so the
    /// accepted encoding is pinned here in one readable place: the
    /// envelope's `sig` parameter is *defined* as unpadded base64url
    /// precisely so it cannot contain `=` and confuse the parameter
    /// parser, and a decoder that also accepted padding would quietly
    /// widen that definition.
    let decodeBase64Url (value: string) : Result<byte[], string> =
        if String.IsNullOrEmpty value then
            Error "the signature is empty"
        elif not (WorkerOutcomeSignature.isBase64Url value) then
            Error "the signature is not unpadded base64url"
        else
            let padded =
                let swapped = value.Replace('-', '+').Replace('_', '/')

                match swapped.Length % 4 with
                | 0 -> swapped
                | 2 -> swapped + "=="
                | 3 -> swapped + "="
                | _ -> swapped // length % 4 = 1 is not a valid base64 length; FromBase64String refuses it

            try
                Ok(Convert.FromBase64String padded)
            with _ ->
                Error "the signature is not decodable base64url"

    /// ECDSA P-256 / SHA-256 signature length: two 32-byte field elements
    /// concatenated (IEEE P1363).
    [<Literal>]
    let Es256SignatureBytes = 64

    /// Verify an `Es256` signature over `payload`.
    ///
    /// The signature encoding is **IEEE P1363 (`r || s`, fixed-width)**,
    /// stated explicitly to `VerifyData` rather than left to the overload
    /// default. ECDSA has two encodings in common use — P1363 (what JWS
    /// `ES256` uses) and ASN.1 DER (what OpenSSL emits by default) — and
    /// an implicit choice here is a defect that presents as "signatures
    /// from my worker never verify" with nothing in any message naming the
    /// encoding. The length is checked first so the common mistake
    /// (a ~70-byte DER signature) is reported as itself.
    let verifyEs256 (key: WorkerSigningKey) (payload: string) (signature: string) : Result<unit, string> =
        match decodeBase64Url signature with
        | Error e -> Error e
        | Ok signatureBytes when signatureBytes.Length <> Es256SignatureBytes ->
            Error
                $"an es256 signature is %d{Es256SignatureBytes} bytes (IEEE P1363 r||s); this one is %d{signatureBytes.Length} bytes — an ASN.1 DER-encoded ECDSA signature is the usual cause, and must be converted to fixed-field concatenation"
        | Ok signatureBytes ->
            match WorkerPublicKey.tryImportEs256 key.PublicKey with
            | Error e -> Error $"the registered public key is unusable: %s{e}"
            | Ok ecdsa ->
                // Key material is imported per verification and disposed —
                // GP 12 rule 4 (no state between invocations), the same
                // posture `DefaultArtefactSigner` takes by re-reading its
                // material per `Sign`.
                try
                    let verified =
                        ecdsa.VerifyData(
                            Encoding.UTF8.GetBytes payload,
                            signatureBytes,
                            HashAlgorithmName.SHA256,
                            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
                        )

                    if verified then
                        Ok()
                    else
                        Error "the signature does not verify against the registered key"
                finally
                    ecdsa.Dispose()

    /// The default verifier: `Es256` in-tree, `Ed25519` refused by name.
    ///
    /// The `Ed25519` arm is a **refusal, not a fallback**. A verifier that
    /// returned `Ok` for an algorithm it cannot check would be the worst
    /// possible failure in this file; one that returned a vague error would
    /// send an operator hunting a key problem that does not exist.
    let bclVerifier: WorkerSignatureVerifier =
        fun key payload signature ->
            match key.Algorithm with
            | WorkerKeyAlgorithm.Es256 -> verifyEs256 key payload signature
            | WorkerKeyAlgorithm.Ed25519 ->
                Error
                    "ed25519 cannot be verified by the in-tree verifier: .NET 10's BCL ships no Ed25519 primitive, and the SDK core carries no third-party crypto dependency (GP 1). Compose a WorkerSignatureVerifier that delegates the ed25519 arm to a crypto companion, or register the worker's key as es256 (ECDSA P-256)."

/// Phase 486 — when a completion callback's outcome must carry a verified
/// worker signature.
///
/// Four modes rather than a `bool` because "verify what is offered" and
/// "demand one" are genuinely different postures, and the middle pair is
/// how a deployment rolls the feature out: turn on `VerifyWhenPresented`,
/// watch the audit trail until every worker signs, then tighten. A binary
/// switch would make adoption a flag day.
type SignedOutcomePolicy =
    /// **Default.** Signatures are neither required nor examined; a
    /// presented envelope is ignored entirely. Byte-for-byte Phase 320
    /// (GP 11 / GP 13) — this is also the mode an absent
    /// `SignedOutcomeVerification` registration means.
    | NoSignedOutcomes
    /// Verify a signature when one is presented, and refuse the callback
    /// if it does not verify. An unsigned outcome is accepted. The
    /// roll-out mode: a broken signer is caught, a not-yet-updated worker
    /// keeps working.
    | VerifyWhenPresented
    /// Every outcome from a backend that **declares the Phase 478
    /// isolation posture** must carry a verified signature; outcomes from
    /// other backends are verified when presented.
    ///
    /// This is the `ExecutionProfile.Isolated` clause, keyed to the
    /// BACKEND rather than to the individual spec — see
    /// `SignedOutcomeVerifier.verify` for why that is both what the
    /// platform can honestly evaluate at ingress time and the stronger
    /// of the two readings.
    | RequireForIsolatingBackends
    /// Every outcome must carry a verified signature, whatever the
    /// backend. The strictest mode; an unsigned callback never resolves a
    /// run.
    | RequireForAllBackends

[<RequireQualifiedAccess>]
module SignedOutcomePolicy =
    /// Stable lowercase label for logs / audit payloads / dev panels.
    let label =
        function
        | NoSignedOutcomes -> "no-signed-outcomes"
        | VerifyWhenPresented -> "verify-when-presented"
        | RequireForIsolatingBackends -> "require-for-isolating-backends"
        | RequireForAllBackends -> "require-for-all-backends"

/// Phase 486 — why a signed outcome was refused.
///
/// Every case NAMES what did not match. That is not politeness: the three
/// operationally-distinct causes here — a worker whose key is not yet
/// approved, a worker whose key was revoked, and a genuine signature
/// replayed against a substituted body — have completely different
/// responses, and an operator who cannot tell them apart from the audit
/// trail has to treat all three as a possible compromise. The HTTP
/// response stays uniform (the Phase 232 posture); this DU is the part
/// that is not.
[<RequireQualifiedAccess>]
type SignedOutcomeRejection =
    /// Policy demands a signature and none was presented.
    | SignatureRequired of backend: string * policy: string
    /// A signature header was presented but is not a well-formed envelope.
    | MalformedEnvelope of reason: string
    /// The envelope names a `(worker, key)` pair the registry has never
    /// seen.
    | UnknownWorkerKey of workerId: string * keyId: string
    /// The key exists but is not `Approved` (a first-contact enrolment
    /// awaiting an admin).
    | KeyNotApproved of workerId: string * keyId: string * status: string
    /// The key exists and is revoked.
    | KeyRevoked of workerId: string * keyId: string * reason: string
    /// The signed artifact digest does not match the callback body's
    /// outcome. **The tamper signal** — a genuine signature over a
    /// different result.
    | ArtifactHashMismatch of signed: string * actual: string
    /// The signed timestamp is not a parseable instant.
    | UnparseableTimestamp of signedAt: string
    /// The signed timestamp is outside the accepted clock-skew window.
    | StaleTimestamp of signedAt: string * skew: TimeSpan * tolerance: TimeSpan
    /// The signature does not verify against the registered key, or the
    /// algorithm cannot be verified by the composed verifier.
    | SignatureInvalid of reason: string

[<RequireQualifiedAccess>]
module SignedOutcomeRejection =
    /// Stable lowercase label for the audit `Reason` field.
    ///
    /// All labels are `signature-`-prefixed so they share one namespace
    /// with — and stay distinguishable from — the Phase 320 refusal
    /// reasons (`missing-secret`, `unknown-handle`, `secret-mismatch`, …)
    /// that land in the same audit field.
    let label (rejection: SignedOutcomeRejection) : string =
        match rejection with
        | SignedOutcomeRejection.SignatureRequired _ -> "signature-required"
        | SignedOutcomeRejection.MalformedEnvelope _ -> "signature-malformed-envelope"
        | SignedOutcomeRejection.UnknownWorkerKey _ -> "signature-unknown-key"
        | SignedOutcomeRejection.KeyNotApproved _ -> "signature-key-not-approved"
        | SignedOutcomeRejection.KeyRevoked _ -> "signature-key-revoked"
        | SignedOutcomeRejection.ArtifactHashMismatch _ -> "signature-artifact-mismatch"
        | SignedOutcomeRejection.UnparseableTimestamp _ -> "signature-unparseable-timestamp"
        | SignedOutcomeRejection.StaleTimestamp _ -> "signature-stale-timestamp"
        | SignedOutcomeRejection.SignatureInvalid _ -> "signature-invalid"

    /// Operator-facing description. Never returned to the caller — the
    /// wire response is uniform — but logged and audited.
    let describe (rejection: SignedOutcomeRejection) : string =
        match rejection with
        | SignedOutcomeRejection.SignatureRequired(backend, policy) ->
            $"no worker signature was presented, and policy '%s{policy}' requires one for outcomes from backend '%s{backend}'"
        | SignedOutcomeRejection.MalformedEnvelope reason ->
            $"the %s{WorkerOutcomeSignature.Header} header is not a well-formed signature envelope: %s{reason}"
        | SignedOutcomeRejection.UnknownWorkerKey(workerId, keyId) ->
            $"no signing key '%s{keyId}' is registered for worker '%s{workerId}'"
        | SignedOutcomeRejection.KeyNotApproved(workerId, keyId, status) ->
            $"signing key '%s{keyId}' for worker '%s{workerId}' is '%s{status}', not approved; approve it before its signatures are honoured"
        | SignedOutcomeRejection.KeyRevoked(workerId, keyId, reason) ->
            $"signing key '%s{keyId}' for worker '%s{workerId}' is REVOKED (%s{reason}) — a signature under a revoked key is refused whatever else about it is valid"
        | SignedOutcomeRejection.ArtifactHashMismatch(signed, actual) ->
            $"the signed artifact digest (%s{signed}) does not match the outcome this callback delivers (%s{actual}). The signature itself may be genuine — this is what a REPLAY against a substituted result looks like, and it is the case worth alerting on."
        | SignedOutcomeRejection.UnparseableTimestamp signedAt ->
            $"the signed timestamp '%s{signedAt}' is not a parseable instant"
        | SignedOutcomeRejection.StaleTimestamp(signedAt, skew, tolerance) ->
            $"the signed timestamp '%s{signedAt}' is %g{abs skew.TotalSeconds}s from platform time, outside the %g{tolerance.TotalSeconds}s tolerance"
        | SignedOutcomeRejection.SignatureInvalid reason -> $"the worker signature did not verify: %s{reason}"

/// Phase 486 — the verified attribution recorded against an accepted
/// outcome: which worker produced it, under which key, over what.
///
/// Identity by value (GP 12 rule 1). Present on the resolution audit event
/// and stamped into `HttpContext.Items` under
/// `SignedOutcomeVerifier.ContextItemKey`, so a deployment's own
/// post-handler stage can read it without this phase widening
/// `IExternalCompletionSink` (a breaking change for every implementation,
/// for a field most never populate).
type OutcomeAttribution = {
    WorkerId: string
    KeyId: string
    Algorithm: WorkerKeyAlgorithm
    /// The verified digest of the outcome this callback delivered.
    ArtifactHash: string
    /// The worker's diagnostics commitment — recorded, never
    /// dereferenced.
    DiagnosticsHash: string
    /// The signed timestamp, as signed.
    SignedAt: string
}

[<RequireQualifiedAccess>]
module OutcomeAttribution =
    /// One-line description for logs / audit payloads.
    let describe (attribution: OutcomeAttribution) : string =
        $"worker '%s{attribution.WorkerId}' key '%s{attribution.KeyId}' (%s{WorkerKeyAlgorithm.label attribution.Algorithm}) artifact %s{attribution.ArtifactHash}"

/// Phase 486 — everything the completion-callback ingress needs to verify
/// a signed outcome, registered as ONE DI singleton.
///
/// **A record rather than four separate registrations**, because the four
/// fields are only meaningful together: a policy without a registry
/// refuses everything, and a registry without a policy verifies nothing.
/// One singleton also makes the opt-in unambiguous — its **absence** is
/// `NoSignedOutcomes`, and the ingress therefore costs an opted-out
/// deployment a single null DI probe and no branch beyond it (GP 13).
type SignedOutcomeVerification = {
    Policy: SignedOutcomePolicy
    Registry: IWorkerKeyRegistry
    /// The verifier. `WorkerSignature.bclVerifier` unless the deployment
    /// composed one that also handles `Ed25519`.
    Verify: WorkerSignatureVerifier
    /// Platform clock, injectable so freshness is testable without
    /// sleeping.
    Now: unit -> DateTimeOffset
    /// How far the signed timestamp may be from platform time, in either
    /// direction.
    MaxClockSkew: TimeSpan
}

[<RequireQualifiedAccess>]
module SignedOutcomeVerification =
    /// Default clock-skew tolerance: five minutes either way.
    ///
    /// The same window `ToolUp.Stripe.Webhook`'s signature freshness check
    /// uses, and for the same reason — it is wide enough for unsynchronised
    /// worker clocks and narrow enough that a captured envelope is not
    /// indefinitely replayable. Note the replay window is bounded by more
    /// than this: the handle's own one-shot terminal claim means a replayed
    /// envelope has nothing left to resolve once the run has resolved.
    let DefaultMaxClockSkew = TimeSpan.FromMinutes 5.0

    /// Verification against `registry` with the in-tree BCL verifier, the
    /// system clock, and the default skew tolerance.
    let create (policy: SignedOutcomePolicy) (registry: IWorkerKeyRegistry) : SignedOutcomeVerification = {
        Policy = policy
        Registry = registry
        Verify = WorkerSignature.bclVerifier
        Now = fun () -> DateTimeOffset.UtcNow
        MaxClockSkew = DefaultMaxClockSkew
    }

/// Phase 486 — the gates, in the order the file header sets out.
[<RequireQualifiedAccess>]
module SignedOutcomeVerifier =

    /// `HttpContext.Items` key the ingress stamps the verified attribution
    /// under. An idiomatic interop stamp (see the type-erasure boundaries
    /// note in the SDK guide), not a domain erasure: written and read as
    /// `OutcomeAttribution` at both ends.
    [<Literal>]
    let ContextItemKey = "ToolUp.SignedOutcome.Attribution"

    /// Lowercase hex SHA-256 over the canonical descriptor of a terminal
    /// outcome — the value a worker puts in the envelope's `artifact`
    /// parameter, and the value the ingress recomputes from the body it
    /// received.
    ///
    /// Exposed (rather than kept private to the check) because a worker
    /// implemented in .NET needs exactly this function to sign correctly,
    /// and two implementations of one digest is how a signature scheme
    /// stops interoperating.
    let artifactHash (outcome: ExternalOutcome) : Result<string, string> =
        WorkerOutcomeSignature.outcomeDescriptor outcome
        |> Result.map (fun descriptor ->
            descriptor
            |> Encoding.UTF8.GetBytes
            |> SHA256.HashData
            |> Convert.ToHexString
            |> _.ToLowerInvariant())

    /// Does `policy` demand a signature for an outcome from a backend
    /// whose isolation posture is `isolating`?
    let requiresSignature (policy: SignedOutcomePolicy) (isolating: bool) : bool =
        match policy with
        | NoSignedOutcomes
        | VerifyWhenPresented -> false
        | RequireForIsolatingBackends -> isolating
        | RequireForAllBackends -> true

    /// Verify a presented envelope, or decide that none was needed.
    ///
    /// * `Ok None` — no signature was presented and policy permits that.
    ///   The outcome is accepted exactly as Phase 320 accepted it, with no
    ///   attribution.
    /// * `Ok (Some attribution)` — a signature was presented and verified.
    /// * `Error rejection` — refused, with the cause named.
    ///
    /// **`isolating` is the backend's declared Phase 478 posture, and the
    /// `Isolated` clause is keyed to it rather than to the individual
    /// spec's `ExecutionProfile`. That is a deliberate reading of the
    /// requirement, for two reasons.** The first is what the ingress
    /// actually has: `ExternalHandle` and `ExternalHandleRecord` do not
    /// carry the profile, and `JobResult.HandedOff` carries only the
    /// handle — so recovering the submitted profile at callback time would
    /// mean adding a field to a persisted record, a parameter to
    /// `IExternalHandleStore.Register`, and a payload to a DU case, all
    /// breaking, for one boolean. The second is that the posture-keyed
    /// form is *stronger*: it cannot be dodged by mislabelling a spec
    /// `Standard`, because it applies to every outcome a clean-room-capable
    /// backend produces. A deployment that composes an isolating backend
    /// and enables this mode is asserting "results from that fleet are
    /// attributable", which is the property `Isolated` needed in the first
    /// place.
    let verify
        (verification: SignedOutcomeVerification)
        (backend: string)
        (isolating: bool)
        (handleId: Guid)
        (outcome: ExternalOutcome)
        (presented: string option)
        : Async<Result<OutcomeAttribution option, SignedOutcomeRejection>> =
        async {
            match verification.Policy with
            | NoSignedOutcomes -> return Ok None
            | policy ->
                match presented with
                | None ->
                    if requiresSignature policy isolating then
                        return
                            Error(SignedOutcomeRejection.SignatureRequired(backend, SignedOutcomePolicy.label policy))
                    else
                        return Ok None
                | Some header ->
                    match WorkerOutcomeSignature.parse header with
                    | Error reason -> return Error(SignedOutcomeRejection.MalformedEnvelope reason)
                    | Ok envelope ->
                        let! resolved = verification.Registry.Resolve(envelope.WorkerId, envelope.KeyId)

                        match resolved with
                        | None ->
                            return Error(SignedOutcomeRejection.UnknownWorkerKey(envelope.WorkerId, envelope.KeyId))
                        | Some key ->
                            match key.Status with
                            | WorkerKeyStatus.Revoked(reason, _) ->
                                return
                                    Error(SignedOutcomeRejection.KeyRevoked(envelope.WorkerId, envelope.KeyId, reason))
                            | WorkerKeyStatus.PendingApproval ->
                                return
                                    Error(
                                        SignedOutcomeRejection.KeyNotApproved(
                                            envelope.WorkerId,
                                            envelope.KeyId,
                                            WorkerKeyStatus.label key.Status
                                        )
                                    )
                            | WorkerKeyStatus.Approved ->
                                match artifactHash outcome with
                                | Error reason ->
                                    // Unreachable through the ingress (the
                                    // wire contract already refuses a
                                    // non-terminal status upstream). The
                                    // belt, and it fails CLOSED.
                                    return Error(SignedOutcomeRejection.MalformedEnvelope reason)
                                | Ok actual when actual <> envelope.ArtifactHash ->
                                    return
                                        Error(
                                            SignedOutcomeRejection.ArtifactHashMismatch(envelope.ArtifactHash, actual)
                                        )
                                | Ok _ ->
                                    let parsedAt =
                                        match
                                            DateTimeOffset.TryParse(
                                                envelope.SignedAt,
                                                CultureInfo.InvariantCulture,
                                                DateTimeStyles.RoundtripKind
                                            )
                                        with
                                        | true, instant -> Some instant
                                        | _ -> None

                                    match parsedAt with
                                    | None ->
                                        return Error(SignedOutcomeRejection.UnparseableTimestamp envelope.SignedAt)
                                    | Some instant ->
                                        let skew = verification.Now() - instant

                                        if abs skew.Ticks > verification.MaxClockSkew.Ticks then
                                            return
                                                Error(
                                                    SignedOutcomeRejection.StaleTimestamp(
                                                        envelope.SignedAt,
                                                        skew,
                                                        verification.MaxClockSkew
                                                    )
                                                )
                                        else
                                            let payload = WorkerOutcomeSignature.signingPayload handleId envelope

                                            match verification.Verify key payload envelope.Signature with
                                            | Error reason ->
                                                return Error(SignedOutcomeRejection.SignatureInvalid reason)
                                            | Ok() ->
                                                return
                                                    Ok(
                                                        Some {
                                                            WorkerId = envelope.WorkerId
                                                            KeyId = envelope.KeyId
                                                            Algorithm = key.Algorithm
                                                            ArtifactHash = envelope.ArtifactHash
                                                            DiagnosticsHash = envelope.DiagnosticsHash
                                                            SignedAt = envelope.SignedAt
                                                        }
                                                    )
        }