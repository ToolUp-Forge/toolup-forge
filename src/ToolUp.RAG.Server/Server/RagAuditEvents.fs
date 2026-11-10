// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The RAG companion's audit event: a document dropped by ingestion
/// back-pressure. Registered from `withRAG`; Core never names it.
module ToolUp.RAG.RagAuditEvents

open System
open ToolUp.Platform
open ToolUp.Platform.Audit.AuditLog

/// a `DocumentIngestionJob` was dropped because the
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

/// Every audit event the ToolUp.RAG companion records.
type RagAuditEvent =
    /// a document was dropped from ingestion because the
    /// in-process queue was full and the bounded enqueue retry was
    /// exhausted. Recorded under `KnowledgeSourceModule.value`; makes the
    /// formerly telemetry-only queue-overflow loss queryable per-document
    /// (GP 6 + GP 9).
    | KnowledgeIngestionDropped of KnowledgeIngestionDroppedPayload

module RagAuditEvent =
    [<Literal>]
    let Registrant = "ToolUp.RAG"

    let private kind name : AuditEventKindInfo = {
        Kind = name
        Registrant = Registrant
        Severity = None
    }

    /// The ToolUp.RAG family: its kinds and its codec.
    let family: AuditEventFamily<RagAuditEvent> = {
        Registrant = Registrant
        Kinds = [ kind "KnowledgeIngestionDropped" ]
        Encode =
            function
            | KnowledgeIngestionDropped p -> "KnowledgeIngestionDropped", AuditPayloadJson.write p
        Decode =
            fun kind payload ->
                match kind with
                | "KnowledgeIngestionDropped" -> KnowledgeIngestionDropped(AuditPayloadJson.read payload)
                | other -> failwithf "not a ToolUp.RAG audit kind: %s" other
    }

    /// The `AuditEvent` one of these events is recorded as.
    let event (value: RagAuditEvent) : AuditEvent = AuditEventFamily.event family value

    /// The event an `AuditEvent` carries, if it carries one of these.
    let tryRead (audit: AuditEvent) : RagAuditEvent option = AuditEventFamily.tryRead family audit

/// Matches an `AuditEvent` carrying one of the ToolUp.RAG events.
let (|RagAudit|_|) (audit: AuditEvent) : RagAuditEvent option = RagAuditEvent.tryRead audit