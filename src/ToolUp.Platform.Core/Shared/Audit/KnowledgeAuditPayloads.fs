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
    TargetUserId: string
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
    TargetUserId: string
}

/// Platform Knowledge Base document uploaded. Emitted by
/// `IPlatformKnowledgeApi.UploadPlatformDocument` on success. Records
/// the cardinality (size) and identity (id + file name) of the upload
/// without persisting the document body in the audit trail. Recorded
/// under `_platform` scope — Platform KB content is deployment-wide.
type PlatformDocumentUploadedPayload = {
    /// Actor (Platform Admin) who uploaded the document. Read from the
    /// caller's `AccessContext.UserId`; gated by
    /// `canModifyPlatformConfig` server-side.
    Actor: string
    /// Document id assigned by the upload handler. Stable identifier
    /// the operator can cross-reference against the Platform KB blob
    /// at `_platform/knowledge/{DocumentId}/{FileName}`.
    DocumentId: string
    /// Original file name. Surfaced for audit readability — operators
    /// reading the trail recognise file names faster than UUIDs.
    FileName: string
    /// File size in bytes. Cardinality only; the body itself does not
    /// travel through the audit trail.
    SizeBytes: int64
}

/// Platform Knowledge Base document deleted. Emitted by
/// `IPlatformKnowledgeApi.DeletePlatformDocument` on successful
/// deletion (the underlying blob + index entry + vector chunks are
/// removed). Idempotent deletes (the document didn't exist) suppress
/// the audit emission so the trail reflects only material state
/// changes.
type PlatformDocumentDeletedPayload = {
    /// Actor (Platform Admin) who triggered the deletion.
    Actor: string
    /// Document id of the deleted entry. The blob, index entry, and
    /// vector chunks are gone after this event — the id is preserved
    /// in the audit trail so historical reads can identify what was
    /// removed.
    DocumentId: string
    /// File name at the time of deletion. Same audit-readability
    /// rationale as `PlatformDocumentUploadedPayload.FileName`.
    FileName: string
}

/// Knowledge Base *original* document retrieved (Phase 107). Emitted by
/// the KB `GetOriginalDocument` handler on every successful fetch of an
/// original ingested document — a state-observing access to potentially
/// sensitive content, audited distinctly from the upload event so the
/// trail answers "who pulled which source when" (GP 6 extended to
/// sensitive reads). Identifiers + source kind only — no document
/// content, no bytes (same PII envelope as the KB upload events).
type KnowledgeOriginalRetrievedPayload = {
    /// User who fetched the original.
    UserId: string
    /// Id of the `KnowledgeDocument` whose original was fetched.
    DocumentId: string
    /// Scope the document lives in (the caller's resolved scope —
    /// the structural gate guarantees they match, GP 4).
    ScopeId: string
    /// Source-kind case name ("UploadedFile" / "Note" /
    /// "FromNarrative") so the trail distinguishes binary originals
    /// from note-markdown fetches without payload introspection.
    SourceKind: string
    /// Original file name. Audit-readability — operators reading the
    /// trail recognise file names faster than UUIDs.
    FileName: string
}

/// Knowledge Base original-document fetch refused (Phase 107). Emitted
/// by the KB `GetOriginalDocument` handler when a fetch is denied —
/// out-of-scope document id, or a source kind with no retrievable
/// original. The refusal is itself audit-worthy: denials on the team
/// boundary are material security signals (GP 4 + GP 6).
type KnowledgeOriginalRetrievalDeniedPayload = {
    /// User whose fetch was refused.
    UserId: string
    /// Document id the caller asked for. May not exist anywhere —
    /// recorded verbatim so enumeration attempts are visible.
    DocumentId: string
    /// Scope the caller was acting within.
    ScopeId: string
    /// Refusal reason — the `KnowledgeBaseError` case name
    /// ("NotInScope" / "NoOriginalAvailable").
    Reason: string
}

/// Knowledge Base scope wiped (Phase 115). Emitted by the KB
/// `ResetIndex` handler after `performReset` has fanned the deletion
/// out across every retrieval index (vector store + sparse BM25 leg +
/// persisted snapshots) via `IIndexLifecycle`. Distinct from, and
/// complementary to, the generic `[<Audit "Custom:KnowledgeIndexReset">]`
/// action row the dispatcher already emits: that records *who* called
/// reset; this records the *erasure outcome* — how many documents the
/// scope held and, critically, whether the fan-out left any chunk
/// retrievable in the indexes (GP 6 + GP 9 — a half-completed delete is
/// audit-worthy and must be loud). Identifiers + counts only, no
/// document content (same PII envelope as the other KB audit events).
type KnowledgeScopeErasedPayload = {
    /// User who triggered the scope reset (the caller's resolved `UserId`).
    UserId: string
    /// Scope that was wiped (the caller's resolved scope — the structural
    /// gate guarantees they match, GP 4).
    ScopeId: string
    /// Number of `KnowledgeDocument`s the scope held at reset time.
    DocumentCount: int
    /// Chunks that survived the fan-out across the retrieval indexes — `0`
    /// on a clean wipe. A non-zero value means RAG may keep surfacing
    /// wiped documents, so the audit trail carries the same loud signal
    /// the operator log does (GP 9).
    OrphanChunkCount: int
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

/// Phase 303 — a `DocumentIngestionJob` was dropped because the
/// in-process ingestion queue was at capacity and the bounded enqueue
/// retry was exhausted. The source file persists to KB / Data-Manager
/// blob storage and appears in the document list, but its chunks never
/// reach retrieval — so without this row the loss is silent (the user
/// thinks the upload "worked"; retrieval returns nothing relevant).
/// Recorded under `KnowledgeSourceModule.value` scope (deployment-wide,
/// like the corrupt-index trail) so an operator can query queue-drop
/// pressure in isolation. Identifiers + cardinality only; no chunk
/// content travels through the audit trail.
type KnowledgeIngestionDroppedPayload = {
    /// Vector-store scope key the dropped document was bound for —
    /// `platform` / `deployment` / `team:{teamId}`.
    ScopeKey: string
    /// Document id (the file name) that was dropped. Recorded verbatim
    /// so the operator can correlate against the KB / Data-Manager
    /// document list and re-upload.
    DocId: string
    /// Number of chunks (including any summary chunk) that would have
    /// been indexed. Cardinality only — the chunk bodies never travel
    /// through the audit trail.
    ChunkCount: int
    /// Configured `IngestionQueue.Capacity` at drop time, so the
    /// operator can size the gap between offered load and capacity.
    QueueCapacity: int
    /// Why the document was dropped (e.g. "ingestion queue full after
    /// bounded retry").
    Reason: string
}

/// Phase 14x — a KB upload was deduplicated onto an existing document:
/// the caller's scope already held a `KnowledgeDocument` with the same
/// content hash, so `UploadDocument` returned the existing document and
/// skipped ingestion entirely (no re-chunk, no re-embed, no duplicate
/// retrieval hits). Audited so the idempotent-upload decision is
/// queryable in the trail (GP 5 — the dedup outcome is recorded, not
/// silent). Identifiers + hash only; no document content travels
/// through the audit trail (same PII envelope as the other KB events).
type KnowledgeDocumentDeduplicatedPayload = {
    /// User whose upload was deduplicated.
    UserId: string
    /// Scope the existing document lives in — the caller's resolved
    /// scope (the hash index is container-local, GP 4).
    ScopeId: string
    /// Id of the pre-existing `KnowledgeDocument` the upload matched
    /// and that was returned to the caller.
    ExistingDocumentId: string
    /// File name of the *attempted* upload. May differ from the stored
    /// document's name — dedup keys on content, not name.
    FileName: string
    /// Lowercase SHA-256 hex of the uploaded bytes. A correlation
    /// identifier, not content.
    ContentHash: string
}

/// Phase 512 — one or more `KnowledgeDocument`s were purged from a scope
/// by the age-based retention sweep. Emitted once per sweep run that
/// removed anything (a run that expired nothing writes no row — a purge
/// trail must record deletions, not the absence of them), under the
/// swept scope, so an operator can answer "what did retention take, and
/// when" from the trail alone (GP 6). Identifiers + cardinality only; no
/// document content travels through the audit trail.
type KnowledgeDocumentsPurgedPayload = {
    /// Scope whose corpus was swept — the container's scope id (GP 4:
    /// the sweep only ever reaches one scope per run).
    ScopeId: string
    /// Document ids removed by this run, in index order. The list is the
    /// trail's evidence — a count alone cannot be reconciled against the
    /// corpus afterwards.
    DocumentIds: string list
    /// Number of documents removed (`DocumentIds.Length`, denormalised so
    /// a sink can aggregate without parsing the list).
    PurgedCount: int
    /// Total `SizeBytes` reclaimed across the purged documents.
    ReclaimedBytes: int64
    /// Retention age in whole seconds that selected them, so the row
    /// carries the policy that produced it rather than requiring the
    /// reader to correlate against a config snapshot.
    MaxAgeSeconds: int64
    /// Chunks that survived the index fan-out across the retrieval
    /// indexes — `0` on a clean purge. Non-zero means RAG may keep
    /// surfacing purged documents, so the trail carries the same loud
    /// signal the operator log does (GP 9), exactly as
    /// `KnowledgeScopeErased` does for a scope reset.
    OrphanChunkCount: int
}

/// Phase 515 — an upload was inspected by the composed `IContentScanner`
/// at the upload boundary. Emitted on **every** verdict, not only a
/// refusal (GP 6): "this file was scanned and came back clean at 14:02"
/// is precisely the fact an incident reconstruction needs when the same
/// file is implicated a week later, and a trail that records only
/// rejections cannot distinguish a scanner that passed the payload from
/// one that was never consulted. A deployment that composed no scanner
/// emits nothing at all (GP 13) — there is no row for the no-op default.
///
/// Identifiers, the verdict label and the scanner's own reason string
/// only. The payload itself never travels through the audit trail; the
/// digest is the correlation handle, exactly as in
/// `KnowledgeDocumentDeduplicated`.
type ContentScannedPayload = {
    /// Subject whose upload was scanned.
    UserId: string
    /// Scope the upload was made under — the caller's resolved scope
    /// (GP 4: it comes from the resolver, never from the caller).
    ScopeId: string
    /// `IContentScanner.Name` of the scanner that produced the verdict,
    /// so a trail spanning a scanner swap stays attributable.
    ScannerName: string
    /// Sanitised file name of the upload, post `Path.GetFileName`.
    FileName: string
    /// Lowercase SHA-256 hex of the scanned bytes — a correlation
    /// identifier, not content. Always present: the digest is computed
    /// for the audit row even where the upload path would not otherwise
    /// hash (e.g. `withDocumentDedup false`), because a scan verdict
    /// with no handle on WHAT was scanned is not investigable.
    ContentHash: string
    /// Size in bytes of the scanned payload.
    SizeBytes: int64
    /// `ScanVerdict.label` — `"clean"` / `"rejected"` / `"unavailable"`.
    Verdict: string
    /// The scanner's reason for a non-clean verdict; `None` when clean.
    Reason: string option
    /// `true` when the platform refused the upload on the strength of
    /// this verdict. Distinct from `Verdict` because the two come apart
    /// exactly where it matters: an `"unavailable"` verdict under
    /// `FailOpenOnScanError` is recorded and ADMITTED, and an operator
    /// auditing a fail-open deployment needs to find those rows without
    /// re-deriving the policy that was in force at the time.
    Refused: bool
    OccurredAt: DateTimeOffset
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