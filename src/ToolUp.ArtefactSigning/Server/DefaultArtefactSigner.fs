// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

open System
open System.Security.Cryptography
open ToolUp.Platform
open ToolUp.Platform.Secrets

/// Phase 40 — in-process default `IArtefactSigner`. Holds no secret
/// material: each `Sign` / `VerifyKey` re-reads the signing key from
/// `ISecretStore` (scope `_platform`, key `signing/{keyId}`), auto-
/// provisioning a fresh key pair on first use. Rotating the key in the
/// store takes effect on the next call (GP 12 rule 4 — stateless between
/// invocations).
///
/// `actor` is stamped onto the `ArtefactSigned` audit emission —
/// `"system"` for automated pipelines, an authenticated user id for
/// operator-initiated signs.
type DefaultArtefactSigner
    (secrets: ISecretStore, audit: IAuditLog, keyId: string, algorithm: SigningAlgorithm, actor: string) =

    let recordAudit event = async {
        try
            do! audit.Record(SigningKeyMaterial.SecretScope, event)
        with _ ->
            // IAuditLog.Record is best-effort; swallow per its contract
            // (audit emission must never fail the primary operation).
            ()
    }

    let sha256Hex (bytes: byte[]) : string =
        use sha = SHA256.Create()
        sha.ComputeHash(bytes) |> Convert.ToHexStringLower

    interface IArtefactSigner with
        member _.KeyId() = keyId

        member _.VerifyKey() : Async<PublicKeyMetadata> = async {
            match! SigningKeyMaterial.loadOrCreate secrets keyId algorithm with
            | Ok key -> return SigningKeyMaterial.metadata keyId key
            | Error e ->
                // VerifyKey has no error channel; a read-only store with
                // no pre-seeded key is a deployment misconfiguration.
                return raise (InvalidOperationException($"signing key '{keyId}' unavailable: {e}"))
        }

        member _.Sign(artefact: byte[]) : Async<Result<ArtefactSignature, SigningError>> = async {
            match! SigningKeyMaterial.loadOrCreate secrets keyId algorithm with
            | Error e -> return Error(KeyUnavailable e)
            | Ok key ->
                match Jws.sign key keyId artefact with
                | Error e -> return Error e
                | Ok detachedJws ->
                    do!
                        recordAudit (
                            AuditEvent.ArtefactSigned {
                                Actor = actor
                                KeyId = keyId
                                Algorithm = SigningAlgorithm.name key.Algorithm
                                ArtefactSha256 = sha256Hex artefact
                            }
                        )

                    return
                        Ok {
                            KeyId = keyId
                            Algorithm = key.Algorithm
                            SignedAt = DateTimeOffset.UtcNow
                            DetachedJws = detachedJws
                        }
        }

module DefaultArtefactSigner =
    /// Construct an in-process `IArtefactSigner`. `keyId` is the active
    /// signing-key id (auto-provisioned on first use); `algorithm`
    /// selects the curve for a freshly-generated key (ignored when a key
    /// already exists under `keyId`). `actor` is stamped on audit.
    let create
        (secrets: ISecretStore)
        (audit: IAuditLog)
        (keyId: string)
        (algorithm: SigningAlgorithm)
        (actor: string)
        : IArtefactSigner =
        DefaultArtefactSigner(secrets, audit, keyId, algorithm, actor) :> IArtefactSigner

    /// Construct a signer that attributes signs to `"system"` — the
    /// common case for automated signing pipelines.
    let createSystem
        (secrets: ISecretStore)
        (audit: IAuditLog)
        (keyId: string)
        (algorithm: SigningAlgorithm)
        : IArtefactSigner =
        create secrets audit keyId algorithm "system"