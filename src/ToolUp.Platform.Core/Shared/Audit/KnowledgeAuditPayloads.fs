// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of AuditTypes.fs: the platform-admin role, platform
// document, knowledge-base, content-scan and orphan-blob-sweep payload records.
// Declaration order is unchanged from the monolith; same namespace, same
// record names — only the file boundary moved. The `AuditEvent` union that
// registers these payloads stays in AuditTypes.fs, append-only.

// ─── Platform Admin role audit payloads ───────────────────────────────

/// `PlatformAdmin` role assigned to a user. Emitted by
/// `IPlatformAdminStore.AssignPlatformAdmin` on success and by the
/// SDK's bootstrap path when `TOOLUP_INITIAL_PLATFORM_ADMIN` seeds
/// the first admin (in which case `Actor = "_bootstrap"`). Recorded
/// under `_platform.audit` with `ScopeId = "_platform"` — Platform
/// Admin role is deployment-wide, not team-scoped.
type PlatformAdminAssignedPayload = {
    /// User who triggered the assignment. `"_bootstrap"` for the
    /// env-var-seeded initial admin; an existing Platform Admin's
    /// userId for subsequent assignments via the API.
    Actor: string
    /// User who received the role.
    TargetUserId: UserId
}

/// `PlatformAdmin` role revoked from a user. Emitted by
/// `IPlatformAdminStore.RevokePlatformAdmin` on success. Always has a
/// real actor — there's no bootstrap revocation path (the bootstrap
/// only seeds, never removes).
type PlatformAdminRevokedPayload = {
    /// User who triggered the revocation. Must be an existing Platform
    /// Admin (gated by `canModifyPlatformConfig`).
    Actor: string
    /// User whose role was revoked.
    TargetUserId: UserId
}

/// Phase 14v — reserved audit scope for RAG/KB knowledge-index
/// infrastructure events. `KnowledgeIndexLoadFailed` is recorded under
/// this scope (via `IAuditLog.Record`) so an operator can query the
/// knowledge-index health trail in isolation, distinct from per-tenant
/// activity. Deployment-wide, like the `_platform` scope the Platform
/// KB document events use.
module KnowledgeSourceModule =
    [<Literal>]
    let value = "_platform.knowledge"

/// Phase 14v — a persisted RAG vector-index blob failed to deserialise
/// on scope load. Today the in-memory vector store catches the
/// deserialisation failure, logs a single `Warn`, and starts the scope
/// empty; in multi-replica deploys a blob corrupted by one node (disk
/// failure, partial flush during a pod kill) makes the next replica read
/// that scope and start it silently empty — retrieval returns nothing
/// and the operator has no signal beyond a buried log line. This event
/// makes the corrupt load loud (GP 6 + GP 9). Identifiers + cardinality
/// only; no index content travels through the audit trail.
type KnowledgeIndexLoadFailedPayload = {
    /// Vector-store scope key whose index failed to load —
    /// `platform` / `deployment` / `team:{teamId}`.
    ScopeKey: string
    /// Deserialisation failure detail (the exception message). Verbatim
    /// so operators can correlate against the store's own `Warn` log line.
    Reason: string
    /// Size of the corrupt blob in bytes. Cardinality only — the body
    /// itself never travels through the audit trail.
    Bytes: int
    /// Blob location (the index path within the RAG container) so the
    /// operator can find and replace / delete the corrupt artefact.
    BlobLocation: string
}

// ─── Data-object orphan-blob sweep payloads (Phase 7c) ────────────────

/// Phase 7c — one orphaned content blob was reclaimed from a scope's
/// content-addressable dedup pool (`objects/_content/{hash}.data`). An
/// orphan is a content blob no surviving `v{N}.json` metadata blob
/// references — the residue of a `Save` that wrote its content and then
/// died before writing its metadata. Emitted once **per reclaimed blob**,
/// under the swept scope, so a deletion is attributable rather than only
/// countable: the GDPR question ("is the deleted user's content actually
/// gone from content-addressable storage?") is answered per hash, and the
/// storage-cost question is answered by summing `Bytes`.
///
/// Identifiers + sizes only. The content hash is a correlation
/// identifier, never content — the bytes themselves never travel through
/// the audit trail (same envelope as every other storage-side event).
type OrphanedContentBlobReclaimedPayload = {
    /// Scope the blob was reclaimed from — the container's scope id.
    /// GP 4: one sweep run reaches exactly one scope's container.
    ScopeId: string
    /// Lowercase SHA-256 hex the blob was keyed by (its
    /// `_content/{hash}.data` name).
    ContentHash: string
    /// Size of the reclaimed blob in bytes, as the backing store
    /// reported it immediately before the delete.
    Bytes: int64
    /// Whole hours between the blob's last write and the sweep — always
    /// at least the configured grace period, since younger orphans are
    /// deferred rather than reclaimed. Carried so the row shows the
    /// evidence for the reclaim decision, not just its result.
    AgeHours: int64
}

/// Phase 7c — aggregate summary of one orphan-sweep run over one scope.
/// Emitted **only by a run that reclaimed at least one blob**, alongside
/// the per-blob `OrphanedContentBlobReclaimed` rows.
///
/// **Deviation from the phase text, recorded deliberately.** The Phase 7c
/// task asked for a summary "per scope per run". A daily sweep across N
/// scopes would then write N rows a day forever saying nothing happened —
/// and Phase 512 settled the estate posture on exactly this question: a
/// purge trail records deletions, not the absence of them
/// (`KnowledgeDocumentsPurged` is emitted only by runs that removed
/// something). A run that reclaimed nothing is visible in the operator
/// log and the returned report; it does not need an audit row.
type OrphanSweepCompletedPayload = {
    /// Scope swept — the container's scope id (GP 4).
    ScopeId: string
    /// Orphaned content blobs found in the container, before the grace
    /// filter. `OrphansFound - ReclaimedCount - Failures` is the number
    /// deferred as too young.
    OrphansFound: int
    /// Blobs actually deleted by this run.
    ReclaimedCount: int
    /// Total bytes reclaimed across `ReclaimedCount`.
    ReclaimedBytes: int64
    /// Orphans left in place because they were younger than the grace
    /// window — the in-flight-`Save` protection working, not a failure.
    DeferredCount: int
    /// Grace window in whole hours that produced `DeferredCount`, so the
    /// row carries the policy that produced it.
    GracePeriodHours: int64
    /// Deletes the backing store refused. Non-zero means the orphans are
    /// still there and the next run retries them (GP 9).
    FailureCount: int
}