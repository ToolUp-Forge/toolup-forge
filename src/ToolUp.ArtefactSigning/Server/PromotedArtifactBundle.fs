// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.PromotedArtifactBundle

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.ArtefactSigning

// ─── Phase 646 — signing a promoted artifact on acceptance ───────────
//
// A promotion transfer exists so a builder deployment can be retired: the
// data host holds the artifact, its opaque spec payload and its provenance
// attachments, and answers the whole chain without dereferencing anybody.
// A signature is what makes that holding worth citing — a grounding
// certificate that names a model artifact is only as good as the data
// host's ability to say "we accepted exactly this, at exactly this status,
// carrying exactly these attachments", and to prove it to a party that
// trusts neither deployment.
//
// **Why the glue lives here** (the same deviation `SignedExportBundle`
// records, for the same reason): `ToolUp.ArtefactSigning` already
// ProjectReferences `ToolUp.Platform.Server`, so the dependency cannot run
// the other way and the SDK core must stay signer-free (GP 1). The neutral
// `IPromotedArtifactSigner` seam and the `ModelArtifactSignature` wire type
// live in Platform.Server; this adapter is the only place an
// `IArtefactSigner` meets them.
//
// The bundle signs **opaque bytes**. It carries no promotion policy, no
// lifecycle knowledge and no view of what the input means — the canonical
// signing input is built by `ModelPromotionSigningInput`, on the other side
// of the seam.

/// Build an `IPromotedArtifactSigner` over `signer`.
///
/// Each call is one `IArtefactSigner.Sign` over the canonical signing
/// input, so a KMS-backed signer keeps the private key out of process
/// memory exactly as it does everywhere else. `SigningKeyUrl` is the
/// origin-relative public-key path the Phase 40
/// `/_platform/signing-key/{keyId}` endpoint serves, and `SignedInputHash`
/// is the digest of the exact bytes signed — carried so a verifier can
/// confirm WHICH bytes carried the signature without rebuilding the
/// canonical form under its own idea of it.
let adapter (signer: IArtefactSigner) : IPromotedArtifactSigner =
    { new IPromotedArtifactSigner with
        member _.SignArtifact(signingInput: byte[]) : Async<Result<ModelArtifactSignature, string>> = async {
            match! signer.Sign signingInput with
            | Error e -> return Error(SigningError.describe e)
            | Ok signature ->
                return
                    Ok {
                        DetachedJws = signature.DetachedJws
                        SigningKeyId = signature.KeyId
                        SigningKeyUrl = SigningKeyHandler.RoutePrefix + signature.KeyId
                        SignedInputHash = ProvenanceAttachment.hashOf signingInput
                    }
        }
    }

/// Register a `PromotedArtifactBundle.adapter signer` as the deployment's
/// `IPromotedArtifactSigner`. Call this at compose on a data host that
/// accepts promotion transfers; a host that does not register one still
/// accepts them and the artifact simply carries no acceptance signature —
/// an absence the promotion record and the audited row both state, rather
/// than one a reader has to infer.
let register (services: IServiceCollection) (signer: IArtefactSigner) : IServiceCollection =
    services.AddSingleton<IPromotedArtifactSigner>(adapter signer)