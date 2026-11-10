// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The knowledge base companion's audit events: platform-document upload
/// and delete, original-document retrieval and its refusals, a scope's
/// erasure, ingestion deduplication, retention purges and content scans.
/// Registered from `AICompose.register`; Core never names them. They are
/// recorded as `AuditEvent.Opaque` and read back typed through
/// `KnowledgeAuditEvent.tryRead` or the `KnowledgeAudit` pattern.
module KnowledgeBase.ServerKnowledgeAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

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

/// Knowledge Base *original* document retrieved. Emitted by
/// the KB `GetOriginalDocument` handler on every successful fetch of an
/// original ingested document — a state-observing access to potentially
/// sensitive content, audited distinctly from the upload event so the
/// trail answers "who pulled which source when" (GP 6 extended to
/// sensitive reads). Identifiers + source kind only — no document
/// content, no bytes (same PII envelope as the KB upload events).
type KnowledgeOriginalRetrievedPayload = {
    /// User who fetched the original.
    UserId: UserId
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

/// Knowledge Base original-document fetch refused. Emitted
/// by the KB `GetOriginalDocument` handler when a fetch is denied —
/// out-of-scope document id, or a source kind with no retrievable
/// original. The refusal is itself audit-worthy: denials on the team
/// boundary are material security signals (GP 4 + GP 6).
type KnowledgeOriginalRetrievalDeniedPayload = {
    /// User whose fetch was refused.
    UserId: UserId
    /// Document id the caller asked for. May not exist anywhere —
    /// recorded verbatim so enumeration attempts are visible.
    DocumentId: string
    /// Scope the caller was acting within.
    ScopeId: string
    /// Refusal reason — the `KnowledgeBaseError` case name
    /// ("NotInScope" / "NoOriginalAvailable").
    Reason: string
}

/// Knowledge Base scope wiped. Emitted by the KB
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
    UserId: UserId
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

/// a KB upload was deduplicated onto an existing document:
/// the caller's scope already held a `KnowledgeDocument` with the same
/// content hash, so `UploadDocument` returned the existing document and
/// skipped ingestion entirely (no re-chunk, no re-embed, no duplicate
/// retrieval hits). Audited so the idempotent-upload decision is
/// queryable in the trail (GP 5 — the dedup outcome is recorded, not
/// silent). Identifiers + hash only; no document content travels
/// through the audit trail (same PII envelope as the other KB events).
type KnowledgeDocumentDeduplicatedPayload = {
    /// User whose upload was deduplicated.
    UserId: UserId
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

/// one or more `KnowledgeDocument`s were purged from a scope
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

/// an upload was inspected by the composed `IContentScanner`
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
    UserId: UserId
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

/// Every audit event the ToolUp.KnowledgeBase companion records.
type KnowledgeAuditEvent =
    /// Platform Knowledge Base document uploaded. Emitted
    /// by `IPlatformKnowledgeApi.UploadPlatformDocument` on success.
    | PlatformDocumentUploaded of PlatformDocumentUploadedPayload
    /// Platform Knowledge Base document deleted. Emitted
    /// by `IPlatformKnowledgeApi.DeletePlatformDocument` on successful
    /// removal (idempotent deletes of unknown ids suppress the event).
    | PlatformDocumentDeleted of PlatformDocumentDeletedPayload
    /// an original ingested document was fetched from the
    /// Knowledge Base via `GetOriginalDocument`. Sensitive-read audit,
    /// distinct from the upload event (GP 6).
    | KnowledgeOriginalRetrieved of KnowledgeOriginalRetrievedPayload
    /// a `GetOriginalDocument` fetch was refused
    /// (out-of-scope id or no retrievable original). Denials on the
    /// team boundary are audited (GP 4 + GP 6).
    | KnowledgeOriginalRetrievalDenied of KnowledgeOriginalRetrievalDeniedPayload
    /// a Knowledge Base scope was wiped via `ResetIndex`,
    /// fanning the deletion across every retrieval index. Carries the
    /// erasure outcome (document count + surviving-chunk count) so a
    /// half-completed fan-out is loud in the audit trail (GP 6 + GP 9),
    /// complementing the generic dispatcher action row.
    | KnowledgeScopeErased of KnowledgeScopeErasedPayload
    /// a KB upload matched an existing document's content
    /// hash in the caller's scope and was deduplicated onto it
    /// (idempotent upload; ingestion skipped). Recorded under the
    /// caller's scope.
    | KnowledgeDocumentDeduplicated of KnowledgeDocumentDeduplicatedPayload
    /// the age-based KB retention sweep purged one or more
    /// documents from a scope. Recorded under the swept scope; emitted
    /// only by runs that actually removed something.
    | KnowledgeDocumentsPurged of KnowledgeDocumentsPurgedPayload
    /// the composed `IContentScanner` returned a verdict for
    /// an upload at the upload boundary. Emitted for every verdict
    /// (clean included), under the uploader's scope; absent entirely on a
    /// deployment that composed no scanner.
    | ContentScanned of ContentScannedPayload

module KnowledgeAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.KnowledgeBase"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.KnowledgeBase family: its kinds and its codec.
    let family: AuditEventFamily<KnowledgeAuditEvent> = {
        Registrant = Registrant
        Kinds = [
            kind "PlatformDocumentUploaded"
            kind "PlatformDocumentDeleted"
            kind "KnowledgeOriginalRetrieved"
            kind "KnowledgeOriginalRetrievalDenied"
            kind "KnowledgeScopeErased"
            kind "KnowledgeDocumentDeduplicated"
            kind "KnowledgeDocumentsPurged"
            kind "ContentScanned"
        ]
        Encode =
            function
            | PlatformDocumentUploaded p -> "PlatformDocumentUploaded", AuditPayloadJson.write p
            | PlatformDocumentDeleted p -> "PlatformDocumentDeleted", AuditPayloadJson.write p
            | KnowledgeOriginalRetrieved p -> "KnowledgeOriginalRetrieved", AuditPayloadJson.write p
            | KnowledgeOriginalRetrievalDenied p -> "KnowledgeOriginalRetrievalDenied", AuditPayloadJson.write p
            | KnowledgeScopeErased p -> "KnowledgeScopeErased", AuditPayloadJson.write p
            | KnowledgeDocumentDeduplicated p -> "KnowledgeDocumentDeduplicated", AuditPayloadJson.write p
            | KnowledgeDocumentsPurged p -> "KnowledgeDocumentsPurged", AuditPayloadJson.write p
            | ContentScanned p -> "ContentScanned", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "PlatformDocumentUploaded" -> PlatformDocumentUploaded(AuditPayloadJson.read payload)
                | "PlatformDocumentDeleted" -> PlatformDocumentDeleted(AuditPayloadJson.read payload)
                | "KnowledgeOriginalRetrieved" -> KnowledgeOriginalRetrieved(AuditPayloadJson.read payload)
                | "KnowledgeOriginalRetrievalDenied" -> KnowledgeOriginalRetrievalDenied(AuditPayloadJson.read payload)
                | "KnowledgeScopeErased" -> KnowledgeScopeErased(AuditPayloadJson.read payload)
                | "KnowledgeDocumentDeduplicated" -> KnowledgeDocumentDeduplicated(AuditPayloadJson.read payload)
                | "KnowledgeDocumentsPurged" -> KnowledgeDocumentsPurged(AuditPayloadJson.read payload)
                | "ContentScanned" -> ContentScanned(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.KnowledgeBase audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: KnowledgeAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : KnowledgeAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.KnowledgeBase events.
let (|KnowledgeAudit|_|) (audit: AuditEvent) : KnowledgeAuditEvent option = KnowledgeAuditEvent.tryRead audit