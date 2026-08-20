// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open ToolUp.Platform
open ToolUp.Platform.Secrets

/// One signing provider, described uniformly: a byte-level signer, the
/// verifier that resolves its public keys, the lifecycle ledger its keys
/// are recorded in, and — the field that makes providers comparable —
/// the attestation level its key custody honestly supports.
///
/// The provider set is deliberately a DESCRIPTOR over the existing
/// `IArtefactSigner` implementations rather than a second signer
/// interface. Every shipped signer already satisfies the byte-level
/// contract; what was missing was a uniform statement of what each one's
/// key custody entitles it to claim, and a single conformance bar all of
/// them are held to. Adding a provider means constructing one of these,
/// not implementing anything new.
type SigningProvider = {
    /// Stable provider name, for diagnostics and conformance reporting.
    Name: string
    /// What signatures from this provider may claim. Set by the
    /// provider's key custody, never by the calling application.
    Level: AttestationLevel
    Signer: IArtefactSigner
    Verifier: IArtefactVerifier
    Ledger: ISigningKeyLedger
}

/// Default `IApplicationSigner` over a `SigningProvider`. Holds no
/// state: every call re-reads key material through the signer/verifier
/// and re-reads key trust through the ledger, so a rotation or a
/// revocation takes effect on the next call without a restart and
/// without a cache to invalidate (GP 12 rule 4).
type DefaultApplicationSigner(provider: SigningProvider) =

    interface IApplicationSigner with

        member _.Level() = provider.Level

        member _.ActiveKeyId() = provider.Signer.KeyId()

        member _.KeyHistory() = provider.Ledger.History()

        member _.SignPayload(purpose: string, payload: byte[]) : Async<Result<SignedPayloadEnvelope, SigningError>> = async {
            let framed = ApplicationPayload.canonicalBytes purpose provider.Level payload

            match! provider.Signer.Sign framed with
            | Error e -> return Error e
            | Ok signature ->
                return
                    Ok {
                        Purpose = purpose
                        Level = provider.Level
                        Signature = signature
                    }
        }

        member _.VerifyPayload
            (purpose: string, payload: byte[], envelope: SignedPayloadEnvelope)
            : Async<Result<unit, PayloadVerificationError>> =
            async {
                if envelope.Purpose <> purpose then
                    return Error(PayloadVerificationError.PurposeMismatch(purpose, envelope.Purpose))
                else
                    let! history = provider.Ledger.History()
                    let keyId = envelope.Signature.KeyId

                    match history |> SigningKeyHistory.tryFind keyId with
                    | Some entry ->
                        match entry.State with
                        | RevokedKey(at, reason) -> return Error(PayloadVerificationError.KeyRevoked(keyId, at, reason))
                        | ActiveKey
                        | RetiredKey ->
                            // Retired is rotation, not distrust: a
                            // signature made before the rotation still
                            // verifies. That continuity is the whole
                            // reason key material outlives key use.
                            let framed =
                                ApplicationPayload.canonicalBytes envelope.Purpose envelope.Level payload

                            match! provider.Verifier.Verify(framed, envelope.Signature) with
                            | Ok() -> return Ok()
                            | Error e -> return Error(PayloadVerificationError.SignatureRejected e)
                    | None ->
                        // No recorded history for this key. An empty or
                        // partial ledger is a legitimate state — a
                        // deployment that has recorded nothing has
                        // revoked nothing — so the signature is judged
                        // on its bytes, exactly as it would be without
                        // a ledger composed at all (GP 11).
                        let framed =
                            ApplicationPayload.canonicalBytes envelope.Purpose envelope.Level payload

                        match! provider.Verifier.Verify(framed, envelope.Signature) with
                        | Ok() -> return Ok()
                        | Error e -> return Error(PayloadVerificationError.SignatureRejected e)
            }

/// Composition surface for the application signing seam.
///
/// Every entry point here is opt-in. A deployment that calls none of
/// them registers no service and behaves exactly as it did before this
/// surface existed.
module ApplicationSigning =

    // ── providers ──────────────────────────────────────────────────────

    /// The in-process provider: keys are held in the deployment's own
    /// `ISecretStore` and loaded into the signing process to sign. In
    /// development that store is typically local or file-backed; in
    /// production it is whichever managed store the deployment composed.
    ///
    /// Level is `Attribution` in BOTH cases, and deliberately so. What
    /// decides the level is whether the private key can reach process
    /// memory, not how well the place it is fetched from is guarded — a
    /// key read out of a hardened store to sign locally is still a key
    /// the host can leak. A provider that never sees key material is
    /// `keyManaged` below.
    ///
    /// `keyId` is auto-provisioned on first use; `actor` is stamped onto
    /// the underlying sign audit emission.
    let inProcess
        (secrets: ISecretStore)
        (audit: IAuditLog)
        (keyId: string)
        (algorithm: SigningAlgorithm)
        (actor: string)
        : SigningProvider =
        {
            Name = "in-process"
            Level = Attribution
            Signer = DefaultArtefactSigner.create secrets audit keyId algorithm actor
            Verifier = DefaultArtefactVerifier.create secrets
            Ledger = SecretStoreSigningKeyLedger.create secrets
        }

    /// A provider whose private key is held by an external key-management
    /// service and never enters the signing process: the process sends a
    /// digest out and receives a signature back.
    ///
    /// `signer` is the key-management-backed `IArtefactSigner`;
    /// `verifier` resolves the corresponding public keys (a public key
    /// is not secret, so it may be resolved from anywhere the deployment
    /// can reach — including its own secret store).
    ///
    /// Level is `IsolatedSigner`. Passing an in-process signer here
    /// would overstate the claim, which is why this is a separate entry
    /// point rather than a level parameter on the previous one.
    let keyManaged
        (name: string)
        (signer: IArtefactSigner)
        (verifier: IArtefactVerifier)
        (ledger: ISigningKeyLedger)
        : SigningProvider =
        {
            Name = name
            Level = IsolatedSigner
            Signer = signer
            Verifier = verifier
            Ledger = ledger
        }

    /// A provider with an explicitly-stated level — the escape hatch for
    /// custody arrangements the two named constructors do not describe.
    /// The caller takes responsibility for the level being honest; the
    /// conformance pack checks that the level is bound into signatures,
    /// not that it is deserved.
    let provider
        (name: string)
        (level: AttestationLevel)
        (signer: IArtefactSigner)
        (verifier: IArtefactVerifier)
        (ledger: ISigningKeyLedger)
        : SigningProvider =
        {
            Name = name
            Level = level
            Signer = signer
            Verifier = verifier
            Ledger = ledger
        }

    /// Replace a provider's ledger — for a deployment recording key
    /// lifecycle somewhere other than the provider's default (a shared
    /// ledger across several providers, or a store with a
    /// compare-and-swap append).
    let withLedger (ledger: ISigningKeyLedger) (p: SigningProvider) : SigningProvider = { p with Ledger = ledger }

    // ── key lifecycle, as recorded events ──────────────────────────────

    let private record (ledger: ISigningKeyLedger) kind (actor: string) (keyId: string) (reason: string option) =
        ledger.Record {
            KeyId = keyId
            Kind = kind
            At = DateTimeOffset.UtcNow
            Actor = actor
            Reason = reason
        }

    /// Record that `keyId` became an active signing key.
    let activate (ledger: ISigningKeyLedger) (actor: string) (keyId: string) : Async<Result<unit, string>> =
        record ledger SigningKeyEventKind.Activated actor keyId None

    /// Record that `keyId` is no longer used for NEW signatures.
    /// Signatures already made under it stay verifiable — that is the
    /// difference between retirement and revocation, and the reason a
    /// rotation costs nothing to the signatures that came before it.
    let retire (ledger: ISigningKeyLedger) (actor: string) (keyId: string) : Async<Result<unit, string>> =
        record ledger SigningKeyEventKind.Retired actor keyId None

    /// Record that `keyId` is no longer trusted. Every signature under
    /// it is refused from this point on, including ones made before the
    /// revocation: a compromised key was compromised for longer than
    /// anyone knew, so refusing only later signatures would be a
    /// comforting fiction.
    ///
    /// `reason` is mandatory. It is what a relying party is shown when a
    /// signature is refused, and an unexplained revocation is
    /// indistinguishable from a mistake.
    let revoke
        (ledger: ISigningKeyLedger)
        (actor: string)
        (keyId: string)
        (reason: string)
        : Async<Result<unit, string>> =
        record ledger SigningKeyEventKind.Revoked actor keyId (Some reason)

    // ── construction + composition ─────────────────────────────────────

    /// Build an `IApplicationSigner` over a provider. Pure — no I/O, no
    /// recording. Pair with `activate` when the provider's active key is
    /// newly minted.
    let create (p: SigningProvider) : IApplicationSigner =
        DefaultApplicationSigner p :> IApplicationSigner

    /// Build an `IApplicationSigner` and record its active key's
    /// activation if the ledger does not already carry one for that key.
    /// Idempotent, so calling it on every start does not accumulate
    /// duplicate activations.
    let createActivated (actor: string) (p: SigningProvider) : Async<IApplicationSigner> = async {
        let keyId = p.Signer.KeyId()
        let! history = p.Ledger.History()

        let alreadyRecorded =
            history
            |> SigningKeyHistory.tryFind keyId
            |> Option.map (fun e -> e.Events |> List.exists (fun ev -> ev.Kind = SigningKeyEventKind.Activated))
            |> Option.defaultValue false

        if not alreadyRecorded then
            // A ledger write failure must not prevent the deployment
            // from signing: the ledger records trust decisions, and
            // "this key started being used" is the least load-bearing of
            // them. A revocation failing to write is the case that
            // matters, and `revoke` surfaces its Result to the caller.
            do! activate p.Ledger actor keyId |> Async.Ignore

        return create p
    }

    /// Register an `IApplicationSigner` as a singleton so application
    /// code can inject it. Nothing else in the SDK resolves this service
    /// — it exists for the composing application's own use.
    let register (services: IServiceCollection) (signer: IApplicationSigner) : IServiceCollection =
        services.AddSingleton<IApplicationSigner>(signer)

    /// Register the provider's signer, and its underlying pieces, in one
    /// call: `IApplicationSigner` for application code, plus the
    /// `IArtefactSigner` / `IArtefactVerifier` / `ISigningKeyLedger` the
    /// provider is built from, for code that needs the byte-level
    /// surface (the publish path, the public-key endpoint).
    ///
    /// Uses `TryAddSingleton` for the three underlying services so a
    /// deployment that already composed one of them — a publish pipeline
    /// signer, say — keeps its own registration rather than having it
    /// silently replaced.
    let registerProvider (services: IServiceCollection) (p: SigningProvider) : IServiceCollection =
        services.TryAddSingleton<IArtefactSigner> p.Signer
        services.TryAddSingleton<IArtefactVerifier> p.Verifier
        services.TryAddSingleton<ISigningKeyLedger> p.Ledger
        register services (create p)