// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 646 — the registry as a provenance-chain source ───────────
//
// Phase 524's walk stitches ingestion → run → fact → narrative → cited
// answer into one graph. A model artifact was not on it, and neither were
// the opaque records a promotion carries — so a published number produced
// by a promoted model could be traced to the dataset it read and no
// further back than the deployment that happened to hold it.
//
// This is the adapter that closes that: `IArtifactProvenanceSource` filled
// over `IModelRegistry`, the shape `IFactEvidenceSource` established.
//
// **Why an adapter rather than a dependency.** `ProvenanceGraph` is a view
// over `ILineageStore` and compiles ahead of the registry, which is a store
// built on top of the same substrate. Wiring the registry into the graph
// directly would invert that — and it would make a graph over a deployment
// with no registry impossible to construct, which is the ordinary case for
// every deployment that fits no models.
//
// **The attachment content never crosses this seam.** A chain node CITES an
// attachment by hash and media type; materialising every payload would turn
// "show the working" into a bulk download of exactly the opaque bytes forge
// has no business interpreting.

/// Fills the Phase 524 `IArtifactProvenanceSource` seam from an
/// `IModelRegistry`.
[<RequireQualifiedAccess>]
module ModelArtifactProvenance =

    /// Project one artifact onto the chain's own shape: its status, the
    /// vintage it was fit against, and its attachments as citations.
    ///
    /// The attachments are cited in the order the artifact holds them,
    /// which is append order — a total order every reader of the same
    /// artifact record computes identically, so two walks of one chain
    /// produce the same node sequence.
    let ofArtifact (artifact: ModelArtifact) : ArtifactProvenance = {
        ArtifactKey = artifact.CompositeKey.Hash
        Status = ModelArtifactStatus.name artifact.Status
        DatasetVersion = artifact.CompositeKey.DatasetVersion
        Attachments =
            artifact.Attachments
            |> List.map (fun a -> {
                ContentHash = a.ContentHash
                MediaType = a.MediaType
            })
    }

    /// An `IArtifactProvenanceSource` over `registry`.
    ///
    /// A registry error reads as `None` — "this scope holds no such
    /// artifact" — rather than propagating. That is the same posture
    /// `IFactEvidenceSource` takes and it is the right one for a READ-ONLY
    /// view: a chain is a best-effort account of what this deployment can
    /// resolve, and a store having a bad day must degrade the account
    /// rather than fail the answer that cited it. **Including the
    /// hash-verification failure**: an artifact whose attachment bytes no
    /// longer hash to their declared digest is refused by the registry's
    /// own read, so it is absent from the chain rather than present with a
    /// citation that does not hold — which is the honest rendering of a
    /// broken citation.
    let source (registry: IModelRegistry) : IArtifactProvenanceSource =
        { new IArtifactProvenanceSource with
            member _.GetArtifact(scopeId: string, artifactKey: string) : Async<ArtifactProvenance option> = async {
                match! registry.Get(scopeId, artifactKey) with
                | Error _ -> return None
                | Ok artifact -> return Some(ofArtifact artifact)
            }
        }