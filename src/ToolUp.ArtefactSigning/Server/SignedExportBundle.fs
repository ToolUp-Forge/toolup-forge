// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.SignedExportBundle

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Phase 162 — signed compliance export bundle (composition glue) ─────
//
// Adapts an `IArtefactSigner` (this layer) onto the neutral
// `IExportEnvelopeSigner` seam `ToolUp.Platform.Server` exposes, so a
// completed DSR export can ship tamper-evident: `DownloadSignedExport`
// returns the envelope plus a detached JWS over its exact bytes, verifiable
// against the public key the Phase 40 `/_platform/signing-key/{keyId}`
// endpoint serves.
//
// **Why the glue lives here, not in `ToolUp.Platform.Server`** (a deviation
// from the phase's stated `Key files`): `ToolUp.ArtefactSigning` already
// `ProjectReference`s `ToolUp.Platform.Server`, so the dependency cannot run
// the other way — the SDK core must stay signer-free (GP 1). The neutral
// `IExportEnvelopeSigner` seam + the `SignedExportEnvelope` wire type live in
// Platform.Server / Core; this adapter is the only place an `IArtefactSigner`
// meets them. The bundle signs *any* export envelope and carries no DSR /
// compliance policy (§4.12 substrate-neutrality) — the signature is a
// detached JWS over opaque bytes.

/// Build an `IExportEnvelopeSigner` over `signer`. Each `SignEnvelope` is one
/// `IArtefactSigner.Sign` call over the envelope bytes (the private key never
/// enters process memory for a KMS-backed signer). The `SigningKeyUrl` is the
/// origin-relative public-key path (`/_platform/signing-key/{keyId}`) the
/// Phase 40 endpoint serves, so a verifying party confirms tamper-evidence
/// offline.
let adapter (signer: IArtefactSigner) : IExportEnvelopeSigner =
    { new IExportEnvelopeSigner with
        member _.SignEnvelope(envelope: byte[]) : Async<Result<ExportSignature, string>> = async {
            match! signer.Sign envelope with
            | Error e -> return Error(SigningError.describe e)
            | Ok signature ->
                return
                    Ok {
                        DetachedJws = signature.DetachedJws
                        SigningKeyId = signature.KeyId
                        SigningKeyUrl = SigningKeyHandler.RoutePrefix + signature.KeyId
                    }
        }
    }

/// Register a `SignedExportBundle.adapter signer` as the deployment's
/// `IExportEnvelopeSigner`. Call this at compose alongside
/// `DataSubjectRequests = Enabled (DataSubjectRequestConfig.signedBackground
/// policy)`; the Phase 162 preflight `IConfigValidator` refuses startup if
/// `SignExports = true` without this registration.
let register (services: IServiceCollection) (signer: IArtefactSigner) : IServiceCollection =
    services.AddSingleton<IExportEnvelopeSigner>(adapter signer)