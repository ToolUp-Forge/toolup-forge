// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Support.PerturbedApplicationSigner

open ToolUp.ArtefactSigning

// ─── Deliberately-broken application signers ───────────────────────────
//
// The conformance pack's value rests entirely on it FAILING a provider
// that does not honour the contract. A pack asserted only against
// conforming providers reports the same green whether it discriminates
// or not — which is precisely the shape a signing gate must not have.
//
// Each defect below is a provider that is correct in every respect but
// one, and each names the single conformance case it must trip. The
// probe asserts that specific case rather than "something failed", so a
// pack that started failing everything for an unrelated reason cannot
// masquerade as a working control.

/// One way an application signing provider can look right and be wrong.
type SigningDefect =
    /// Signs the payload without binding the purpose, and never compares
    /// the envelope's purpose to the one being verified. The purpose
    /// becomes a label rather than a claim.
    | PurposeNotBound
    /// Frames with a fixed attestation level rather than the envelope's,
    /// so the level can be edited after signing without breaking the
    /// signature.
    | LevelNotBound
    /// Stamps the envelope with a level other than the one the provider
    /// declares — a signature overstating (or understating) its custody.
    | LevelMisdeclared
    /// Never consults the key ledger, so a revoked key keeps verifying.
    | RevocationIgnored
    /// Verifies against the ACTIVE key rather than the key the envelope
    /// names, so every signature made before a rotation stops verifying.
    | RotatedKeysLost

module SigningDefect =

    let all = [
        PurposeNotBound
        LevelNotBound
        LevelMisdeclared
        RevocationIgnored
        RotatedKeysLost
    ]

    let name =
        function
        | PurposeNotBound -> "purpose not bound into the signature"
        | LevelNotBound -> "attestation level not bound into the signature"
        | LevelMisdeclared -> "envelope level differs from the declared level"
        | RevocationIgnored -> "key revocation not consulted"
        | RotatedKeysLost -> "verification pinned to the active key"

    /// The conformance case each defect MUST trip. Keep in step with the
    /// case names in `ISigningProviderConformance` — the probe looks them
    /// up verbatim.
    let expectedFailingCase =
        function
        | PurposeNotBound -> "the purpose is bound into the signature"
        | LevelNotBound -> "the attestation level is bound into the signature"
        | LevelMisdeclared -> "the envelope carries the provider's declared attestation level"
        | RevocationIgnored -> "a revoked key refuses signatures made before the revocation"
        | RotatedKeysLost -> "rotation keeps earlier signatures verifiable"

/// A provider deviating from the contract in exactly one respect.
/// Mirrors `DefaultApplicationSigner`'s logic so the deviation, and only
/// the deviation, is what the pack sees.
type PerturbedApplicationSigner(provider: SigningProvider, defect: SigningDefect) =

    /// The level actually signed over — `LevelNotBound` pins it, so a
    /// later edit to the envelope's level changes nothing.
    let framingLevel (envelopeLevel: AttestationLevel) =
        match defect with
        | LevelNotBound -> Attribution
        | _ -> envelopeLevel

    /// The purpose actually signed over — `PurposeNotBound` erases it.
    let framingPurpose (purpose: string) =
        match defect with
        | PurposeNotBound -> ""
        | _ -> purpose

    interface IApplicationSigner with

        member _.Level() = provider.Level

        member _.ActiveKeyId() = provider.Signer.KeyId()

        member _.KeyHistory() = provider.Ledger.History()

        member _.SignPayload(purpose: string, payload: byte[]) : Async<Result<SignedPayloadEnvelope, SigningError>> = async {
            let stamped =
                match defect with
                | LevelMisdeclared ->
                    match provider.Level with
                    | Attribution -> IsolatedSigner
                    | _ -> Attribution
                | _ -> provider.Level

            let framed =
                ApplicationPayload.canonicalBytes (framingPurpose purpose) (framingLevel provider.Level) payload

            match! provider.Signer.Sign framed with
            | Error e -> return Error e
            | Ok signature ->
                return
                    Ok {
                        Purpose = purpose
                        Level = stamped
                        Signature = signature
                    }
        }

        member _.VerifyPayload
            (purpose: string, payload: byte[], envelope: SignedPayloadEnvelope)
            : Async<Result<unit, PayloadVerificationError>> =
            async {
                let purposeMatches =
                    match defect with
                    | PurposeNotBound -> true
                    | _ -> envelope.Purpose = purpose

                if not purposeMatches then
                    return Error(PayloadVerificationError.PurposeMismatch(purpose, envelope.Purpose))
                else
                    let! history = provider.Ledger.History()

                    let revocation =
                        match defect with
                        | RevocationIgnored -> None
                        | _ ->
                            history
                            |> SigningKeyHistory.tryFind envelope.Signature.KeyId
                            |> Option.bind (fun entry ->
                                match entry.State with
                                | RevokedKey(at, reason) -> Some(at, reason)
                                | _ -> None)

                    match revocation with
                    | Some(at, reason) ->
                        return Error(PayloadVerificationError.KeyRevoked(envelope.Signature.KeyId, at, reason))
                    | None ->
                        let signature =
                            match defect with
                            | RotatedKeysLost -> {
                                envelope.Signature with
                                    KeyId = provider.Signer.KeyId()
                              }
                            | _ -> envelope.Signature

                        let framed =
                            ApplicationPayload.canonicalBytes
                                (framingPurpose envelope.Purpose)
                                (framingLevel envelope.Level)
                                payload

                        match! provider.Verifier.Verify(framed, signature) with
                        | Ok() -> return Ok()
                        | Error e -> return Error(PayloadVerificationError.SignatureRejected e)
            }

/// Wrap a provider in a single deviation from the contract.
let create (defect: SigningDefect) (provider: SigningProvider) : IApplicationSigner =
    PerturbedApplicationSigner(provider, defect) :> IApplicationSigner