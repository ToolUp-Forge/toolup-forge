// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformKnowledgeApi

open SharedTypes

// ─── PlatformKnowledgeApi (Fable.Remoting wire surface) ───────────────
//
// Phase 4b — deployment-wide Knowledge Base content management. This is
// the SDK's only write path to `VectorScope.Platform`. Sister API to
// `KnowledgeApi` (which writes to the caller's team / user scope);
// shares the same `KnowledgeDocument` shape so client-side rendering
// can reuse the existing document-list components.
//
// **Hard rule.** `VectorScope.Platform` is reachable from this API
// only — every team-side handler (`KnowledgeApi.UploadDocument`,
// `IngestNarrative`, `AddNote`, `UpdateNote`, `SetAIContext`) derives
// its target scope from the request's `StorageScope` and never accepts
// a caller-supplied scope, so cross-scope writes are structurally
// impossible. The SDK's grep audit (Phase 4b verification step 12)
// catches any future regression.
//
// **Permission gating.** Every write method is gated server-side on
// `AccessContext.canModifyPlatformConfig` (the predicate added in
// commit 4a). Non-admin callers receive `Error "platform admin role
// required"` so the client surfaces a uniform error banner.
// `ListPlatformDocuments` is unconditionally available to authenticated
// callers in commit 4e — when the `ServerConfig.PlatformKnowledgeBase
// = Enabled` toggle ships in commit 5, `ListPlatformDocuments` will
// remain available to admins regardless of toggle state (so they can
// pre-populate before flipping the read switch); non-admins will see
// the list only when retrieval is enabled.
//
// **Storage layout.** Platform KB content lives under the `_platform`
// blob container at `_platform/knowledge/{docId}/{filename}` for raw
// blobs and `_platform/knowledge/index.json` for the document index.
// Vectorisation routes chunks to `VectorScope.Platform`. The
// `IIngestionStatusObserver` already keys on `job.Container` so
// progress notifications flow back to the uploading admin without
// further plumbing.

type IPlatformKnowledgeApi = {
    /// Upload a document to the Platform Knowledge Base. Gated on
    /// `canModifyPlatformConfig`; non-admin callers receive `Error
    /// "platform admin role required"`. Returns the persisted
    /// `KnowledgeDocument` on success — same shape returned by
    /// `KnowledgeApi.UploadDocument` so the client renders the new
    /// entry through the same code path. Vectorisation runs
    /// asynchronously after the call returns; subscribe to
    /// `IngestionStatusNotificationKey` to watch progress.
    UploadPlatformDocument: byte[] -> string -> Async<Result<KnowledgeDocument, string>>

    /// Delete a Platform Knowledge Base document by id. Gated on
    /// `canModifyPlatformConfig`. Removes the raw blob, the index
    /// entry, and every embedded vector chunk in `VectorScope.Platform`.
    /// Idempotent — deleting an unknown id returns `Ok ()`.
    DeletePlatformDocument: string -> Async<Result<unit, string>>

    /// List every document in the Platform Knowledge Base. Read-only
    /// surface available to authenticated callers — the `_platform`
    /// blob index is universally readable when the toggle is enabled
    /// (Phase 4b commit 5); admins can see the list regardless of
    /// toggle state so they can manage content before flipping the
    /// read switch on. Empty list when no Platform KB content exists
    /// in the deployment.
    ListPlatformDocuments: unit -> Async<KnowledgeDocument list>

    /// Phase 4b deferred follow-up — server-side copy of a team-scope
    /// document into the Platform Knowledge Base. Gated on
    /// `canModifyPlatformConfig`; the actor's resolved team scope is
    /// the source. The original document stays in the team scope
    /// (copy semantics, not move) — promote is non-destructive.
    /// Returns a fresh `KnowledgeDocument` with a new id under
    /// `_platform/knowledge/{newDocId}/{filename}`; vector chunks
    /// land under `VectorScope.Platform` via the standard ingestion
    /// pipeline. Use case: a team produces reusable reference
    /// content (style guide, methodology doc) that should be
    /// available to every team in the deployment.
    ///
    /// Failure modes:
    ///   - `Error "platform admin role required"` — caller lacks the role
    ///   - `Error "no team scope resolved"` — Anonymous mode, or no
    ///     active team (the source can't be located)
    ///   - `Error "document {id} not found in team KB"` — id doesn't
    ///     match any document in the caller's team scope
    ///   - `Error <storage-error>` — blob read or write failed
    ///
    /// Client-side UI affordance (a "Promote to Platform KB" button on
    /// each team-side document row, conditionally rendered when the
    /// caller holds `PlatformRole.PlatformAdmin`) is a follow-up;
    /// operators can call the API directly via the Fable.Remoting
    /// proxy in the meantime.
    PromoteTeamDocumentToPlatform: string -> Async<Result<KnowledgeDocument, string>>
}