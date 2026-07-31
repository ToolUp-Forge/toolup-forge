// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Org.BouncyCastle.Crypto.Parameters
open Org.BouncyCastle.Crypto.Signers

// Phase 30a — default edge-side `IArtifactVerifier`. Resolves the
// publisher key via `IPublisherKeyStore`, re-canonicalises the manifest,
// and Ed25519-verifies the signature against `canonical(manifest) ||
// payload`.
//
// **Crypto provider** — `BouncyCastle.Cryptography` (MIT; see
// `Ed25519ArtifactSigner` for the rationale).
//
// **Refusal reasons.**
//   * `"untrusted publisher"` — `PublisherKeyId` is not in
//     `IPublisherKeyStore` (or the store returned `None`).
//   * `"invalid signature length"` — signature is not 64 bytes.
//   * `"invalid public key length"` — store returned a public key that
//     isn't 32 bytes.
//   * `"signature mismatch"` — Ed25519 verification returned false.
//
// **Audit emission.** The verifier records `AuditEvent.ModuleArtefactVerified`
// on `Ok` and `AuditEvent.ModuleArtefactRejected` on `Error` via the injected
// `IAuditLog`. Audit emission failures are swallowed (the contract of
// `IAuditLog.Record` is best-effort, never blocking).

/// Phase 30a — Ed25519 edge-side verifier. Stateless between calls —
/// each `Verify` re-resolves the publisher key from `IPublisherKeyStore`
/// (rule 4: stateless handlers).
type Ed25519ArtifactVerifier(keyStore: IPublisherKeyStore, audit: IAuditLog, edgeScopeId: string) =

    let recordAudit event = async {
        try
            do! audit.Record(edgeScopeId, event)
        with _ ->
            // IAuditLog.Record is best-effort; swallow per the
            // documented contract (audit emission must never block
            // a primary operation).
            ()
    }

    let verifyBytes (publicKey: byte[]) (message: byte[]) (signature: byte[]) : bool =
        let pub = Ed25519PublicKeyParameters(publicKey, 0)
        let verifier = Ed25519Signer()
        verifier.Init(false, pub)
        verifier.BlockUpdate(message, 0, message.Length)
        verifier.VerifySignature(signature)

    interface IArtifactVerifier with
        member _.Verify(artefact: SignedArtifact) : Async<ArtifactValidation> = async {
            let manifest = artefact.Manifest
            let publisherKeyId = manifest.PublisherKeyId

            // Signature length check first — cheap, deterministic, no
            // store round-trip needed.
            if isNull (box artefact.Signature) || artefact.Signature.Length <> 64 then
                let reason = "invalid signature length"

                do!
                    recordAudit (
                        AuditEvent.ModuleArtefactRejected {
                            PublisherKeyId = Some(PublisherKeyId.value publisherKeyId)
                            ModuleId = manifest.ModuleId
                            ArtifactVersion = manifest.Version
                            Reason = reason
                        }
                    )

                return ArtifactValidation.Error reason
            else
                let! publicKeyOpt = keyStore.TryGetPublicKey(publisherKeyId)

                match publicKeyOpt with
                | None ->
                    let reason = "untrusted publisher"

                    do!
                        recordAudit (
                            AuditEvent.ModuleArtefactRejected {
                                PublisherKeyId = Some(PublisherKeyId.value publisherKeyId)
                                ModuleId = manifest.ModuleId
                                ArtifactVersion = manifest.Version
                                Reason = reason
                            }
                        )

                    return ArtifactValidation.Error reason
                | Some publicKey when publicKey.Length <> 32 ->
                    let reason = "invalid public key length"

                    do!
                        recordAudit (
                            AuditEvent.ModuleArtefactRejected {
                                PublisherKeyId = Some(PublisherKeyId.value publisherKeyId)
                                ModuleId = manifest.ModuleId
                                ArtifactVersion = manifest.Version
                                Reason = reason
                            }
                        )

                    return ArtifactValidation.Error reason
                | Some publicKey ->
                    let message = ArtifactCanonical.messageToSign manifest artefact.Payload

                    let valid =
                        try
                            verifyBytes publicKey message artefact.Signature
                        with _ ->
                            false

                    if valid then
                        do!
                            recordAudit (
                                AuditEvent.ModuleArtefactVerified {
                                    PublisherKeyId = PublisherKeyId.value publisherKeyId
                                    ModuleId = manifest.ModuleId
                                    ArtifactVersion = manifest.Version
                                }
                            )

                        return ArtifactValidation.Ok
                    else
                        let reason = "signature mismatch"

                        do!
                            recordAudit (
                                AuditEvent.ModuleArtefactRejected {
                                    PublisherKeyId = Some(PublisherKeyId.value publisherKeyId)
                                    ModuleId = manifest.ModuleId
                                    ArtifactVersion = manifest.Version
                                    Reason = reason
                                }
                            )

                        return ArtifactValidation.Error reason
        }

module Ed25519ArtifactVerifier =
    /// Construct an `IArtifactVerifier` against the given key store +
    /// audit log. `edgeScopeId` is the scope under which
    /// `ModuleArtefactVerified` / `ModuleArtefactRejected` events are recorded —
    /// typically `"_platform"` (artefact installation is a
    /// deployment-wide concern, not tenant-scoped).
    let create (keyStore: IPublisherKeyStore) (audit: IAuditLog) (edgeScopeId: string) : IArtifactVerifier =
        Ed25519ArtifactVerifier(keyStore, audit, edgeScopeId) :> IArtifactVerifier