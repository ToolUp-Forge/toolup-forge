// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module SharedTypes

open System
open ToolUp.Platform
open ToolUp.Platform.Narrative

// ─── Ingestion status ─────────────────────────────────────────────

type IngestionStatus =
    | Queued
    | ExtractingText
    | Embedding of chunksProcessed: int * chunksTotal: int
    | Complete of chunkCount: int
    | Failed of reason: string
    /// Phase 119 — the upload was refused by the deployment's
    /// `KnowledgeUploadPolicy` (oversize, disallowed extension, or an
    /// unsafe filename) *before* anything was persisted. The returned
    /// `KnowledgeDocument` carries this status purely so the client can
    /// surface the reason; nothing is stored, so it never appears in
    /// `GetDocuments` / `GetStatus`.
    | UploadRejected of reason: string
    /// Phase 119 — the document was stored (its original stays
    /// downloadable) but no extractor recognised its type, so it is not
    /// searchable. Distinct from `Complete 0` (a recognised-but-empty
    /// file) so the UI badges "stored, not searchable" honestly rather
    /// than implying a successful index.
    | UnsupportedFormat of detail: string

// ─── Provenance ───────────────────────────────────────────────────

/// Typed provenance — the exact location within a source document from which
/// a chunk was extracted. Serialised into TextChunk.Metadata at ingestion and
/// deserialised at retrieval so retrieved facts can be traced to their origin.
type SourceLocation =
    | Page of number: int
    | Slide of number: int * title: string option
    | Sheet of name: string * rowRange: string option
    | Section of heading: string
    | RowGroup of startRow: int * endRow: int

/// Full provenance record for a text chunk. Stored as JSON in `TextChunk.Metadata["_source"]`.
type SourceReference = {
    DocumentId: string
    DocumentName: string
    /// "pdf" | "pptx" | "docx" | "xlsx" | "csv"
    FileType: string
    Location: SourceLocation
    IndexedAt: DateTimeOffset
}

module SourceLocation =
    /// Map the KB-side provenance location onto the neutral
    /// `ToolUp.Platform` `SourceLocator` (Phase 106). The neutral DU is
    /// deliberately smaller — slide titles and sheet row-ranges are
    /// dropped — so `ToolUp.Platform` never grows toward the KB's
    /// domain shape (GP 1). The mapping happens here, at the producer
    /// boundary; Platform/RAG code never sees `SourceLocation`.
    let toLocator (location: SourceLocation) : ToolUp.Platform.VectorKnowledgeTypes.SourceLocator =
        match location with
        | Page number -> ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Page number
        | Slide(number, _) -> ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Slide number
        | Sheet(name, _) -> ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Sheet name
        | Section heading -> ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Section heading
        | RowGroup(startRow, endRow) -> ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.RowGroup(startRow, endRow)

// ─── Source distinction ──────────────────────────────────────────

/// Identifies where a stored narrative came from — the analytical module
/// that produced it plus the settings that drove the analysis. Used by
/// the Knowledge Base to deduplicate stored narratives and to display
/// meaningful labels in the document list.
type NarrativeDocSource = {
    ModuleId: string
    PageRoute: string option
    SettingsKey: string
    SettingsDisplay: (string * string) list
    GeneratedAt: DateTimeOffset
}

/// Provenance for a user-authored note. Notes are free-form team prose
/// (decisions, conventions, "we chose X over Y because…") that a user
/// types directly into the Knowledge Base — no file, no source module.
/// `Author` is the user id that created the note; `LastEditedAt` is
/// `None` for an unedited note, `Some` after the first `UpdateNote`.
type NoteSource = {
    Title: string
    Author: string
    CreatedAt: DateTimeOffset
    LastEditedAt: DateTimeOffset option
}

/// Distinguishes user-uploaded files, narratives saved out of a running
/// module, and free-form Notes typed directly into the KB. Retrieval
/// keeps all three kinds in the same vector store but the UI, prompt
/// builders, and dedup logic all need to tell them apart.
type KnowledgeSource =
    | UploadedFile
    | FromNarrative of NarrativeDocSource
    | Note of NoteSource

// ─── Document record ──────────────────────────────────────────────

type KnowledgeDocument = {
    /// Stable GUID used as prefix for chunk IDs.
    Id: string
    FileName: string
    FileType: string
    UploadedAt: DateTimeOffset
    UploadedBy: string
    Status: IngestionStatus
    SizeBytes: int64
    /// Number of chunks enqueued at ingestion. Used to clean up the
    /// vector store when the document is deleted or an earlier version
    /// is overwritten by a new ingestion of the same narrative.
    ChunkCount: int
    Source: KnowledgeSource
}

// ─── Narrative ingestion ─────────────────────────────────────────

/// Parameters for saving a `NarrativeDocument` into the Knowledge Base.
/// The document must carry a `Provenance` — the server rejects documents
/// without it because dedup and labelling both rely on it.
///
/// `Overwrite` lets the client confirm a deliberate replacement after
/// seeing a `DuplicateExists` error: the client re-submits the same
/// request with `Overwrite = true`.
type IngestNarrativeRequest = {
    Document: NarrativeDocument
    Overwrite: bool
}

/// Error outcomes for `IngestNarrative`. `DuplicateExists` carries the
/// existing stored document so the UI can show "previous version from
/// {Date}" in the overwrite prompt.
type IngestNarrativeError =
    | MissingProvenance
    | DuplicateExists of existing: KnowledgeDocument
    | IngestFailed of reason: string

// ─── Real-time status notification ───────────────────────────────

/// Wire-format key for the `CustomNotification` published by the
/// ingestion observer when a document's status reaches a terminal
/// state. The AI Assistant chat surface subscribes to it via
/// `NotificationClient` so the user sees ingestion completing without
/// waiting for the next 2s status poll.
[<Literal>]
let IngestionStatusNotificationKey = "KnowledgeBase.IngestionStatus"

/// Payload of `CustomNotification(IngestionStatusNotificationKey, _)`.
/// Serialised on the server with `FableConverters`; parsed on the
/// client with `Fable.SimpleJson`. v1 fires only on terminal
/// transitions (`Complete` / `Failed`) — incremental progress is
/// noise in chat.
///
/// Wire shape is intentionally flat (no embedded DU): subscribers in
/// other companion packages (e.g. the AI Assistant chat surface) parse
/// this without having to mirror the `IngestionStatus` DU across the
/// module boundary.
type IngestionStatusUpdate = {
    DocumentId: string
    FileName: string
    /// "Complete" or "Failed" — the only terminal kinds published.
    Outcome: string
    /// Chunk count when `Outcome = "Complete"`, 0 otherwise.
    ChunkCount: int
    /// Failure reason when `Outcome = "Failed"`, empty otherwise.
    ErrorReason: string
    UploadedBy: string
}

// ─── Inventory summary notification ──────────────────────────────

/// Wire-format key for the `CustomNotification` published whenever the
/// team's knowledge-base inventory changes (document/note added,
/// removed, or edited; standing AI-context entry saved). Subscribers in
/// other companion packages — typically the AI Assistant side panel —
/// use this to maintain a presence badge ("KB has N items") without
/// pulling on `KnowledgeApi` directly, which would create a forbidden
/// `AI → KnowledgeBase` compile-time edge.
///
/// External KB replacements that want their inventory surfaced in the
/// AI panel must publish a `CustomNotification` with this exact key and
/// an `InventorySummary`-shaped JSON payload.
[<Literal>]
let InventoryUpdatedNotificationKey = "KnowledgeBase.InventoryUpdated"

/// Payload of `CustomNotification(InventoryUpdatedNotificationKey, _)`.
/// Serialised on the server with `FableConverters`; parsed on the
/// client with `Fable.SimpleJson`. Fired on every additive or destructive
/// inventory change — fine-grained enough for badges, coarse enough that
/// downstream consumers don't have to reason about per-document state.
type InventorySummary = {
    /// Total uploaded documents in the team's KB (including failed and
    /// in-progress ingests — the badge is presence-of-content, not
    /// retrieval-readiness).
    DocumentCount: int
    /// Total free-form notes in the team's KB.
    NoteCount: int
    /// Whether a standing AI-context entry has been written. Tracked
    /// separately because it carries different semantics from notes — a
    /// single entry that's always-injected, not a searchable collection.
    HasAIContext: bool
    /// Server-side timestamp of the change that produced this snapshot.
    /// Used by the badge to ignore out-of-order publishes.
    LastUpdated: DateTime
    /// Up to 5 zero-state suggested questions tailored to the team's
    /// current KB content. Empty when the KB has no documents or notes.
    /// AI clients render these as clickable affordances on a fresh
    /// (empty) conversation. Carrying them in the inventory payload
    /// keeps the AI client free of any KB compile-time dependency —
    /// same wire-format channel as the existing badge.
    SuggestedQuestions: string list
}

// ─── Original-document retrieval (Phase 102) ─────────────────────

/// Wire record returned by `KnowledgeApi.GetOriginalDocument` — the
/// *original* ingested bytes behind a `KnowledgeDocument`, plus the
/// metadata a client needs to save or render them. Raw originals
/// persist at upload (`knowledge/{docId}/{filename}`) and survive
/// ingestion; this record is the first-class retrieval shape so
/// callers never rebuild the blob-name convention by hand.
type OriginalDocument = {
    /// Original file name (e.g. "Q3 brand audit.pdf"; "note.md" bodies
    /// surface under the note's display file name).
    FileName: string
    /// MIME content type ("application/pdf", "text/markdown", …) so a
    /// client can set the download / render disposition directly.
    ContentType: string
    /// Size of `Content` in bytes.
    SizeBytes: int64
    /// The original document bytes.
    Content: byte[]
}

/// Typed refusals for `KnowledgeApi.GetOriginalDocument`. Absence and
/// denial are results, never exceptions (GP 9).
type KnowledgeBaseError =
    /// The document id is not visible in the caller's scope — it may
    /// belong to another team or not exist at all. Deliberately
    /// indistinguishable so out-of-scope callers cannot probe for
    /// document existence (GP 4); a denial audit is emitted.
    | NotInScope
    /// The document exists in the caller's scope but its source kind
    /// has no retrievable original (module-generated narratives,
    /// AI-context entries), or the underlying blob is gone. Resolution
    /// is per-`KnowledgeSource` via `IOriginalSourceResolver` (Phase 104).
    | NoOriginalAvailable
    /// The fetch failed for an operational reason (storage error).
    /// Carries the underlying message for diagnostics.
    | OriginalRetrievalFailed of reason: string

// ─── Upload policy (Phase 119) ────────────────────────────────────

/// How an upload whose type no extractor recognises is handled.
/// `Reject` refuses it outright (a typed `UploadRejected`); `AcceptUnindexed`
/// stores the original (so it stays downloadable) but flags it
/// `UnsupportedFormat` — stored, never searchable.
type UnsupportedUploadHandling =
    | Reject
    | AcceptUnindexed

/// Compose-time Knowledge Base upload policy. Every lever is opt-in;
/// the default (`KnowledgeUploadPolicy.permissive`, what a deployment
/// that never calls `withUploadPolicy` gets) imposes no size cap and no
/// type allowlist and stores unrecognised types unindexed — pre-119
/// behaviour, save for the always-on filename sanitisation and the
/// `Complete 0` → `UnsupportedFormat` status fix. Value-typed (GP 12),
/// opt-in (GP 13).
type KnowledgeUploadPolicy = {
    /// Hard ceiling on a single upload's byte length, enforced at the KB
    /// boundary *before* the bytes are persisted. `None` = no KB-level
    /// cap (the only lever is then Kestrel's `MaxRequestBodySize`, which
    /// holds the whole `byte[]` in memory through Remoting + extraction).
    MaxUploadBytes: int64 option
    /// Extension allowlist — lower-case, no leading dot (e.g.
    /// `Set.ofList [ "pdf"; "csv" ]`). `None` allows any extension; an
    /// upload whose extension is absent from a `Some` set is rejected.
    AllowedExtensions: Set<string> option
    /// What to do when no extractor recognises the upload's type.
    OnUnsupportedType: UnsupportedUploadHandling
    /// Explicit opt-out for the `Team` / `MultiTeam` preflight `Warning`
    /// that fires when `MaxUploadBytes = None` (an in-memory-DoS lever in
    /// a shared deployment). Mirrors `AcceptSharedEmbeddingCacheInTeamMode`.
    /// `false` by default; set `true` to accept unbounded uploads and
    /// silence the warning.
    AcceptUnboundedUploads: bool
}

module KnowledgeUploadPolicy =
    /// The default policy: no size cap, no allowlist, unrecognised types
    /// stored-but-unsearchable. Pre-119 behaviour (modulo the always-on
    /// filename sanitisation and the `UnsupportedFormat` status fix).
    /// Resolved when a deployment never composes `withUploadPolicy`.
    let permissive: KnowledgeUploadPolicy = {
        MaxUploadBytes = None
        AllowedExtensions = None
        OnUnsupportedType = AcceptUnindexed
        AcceptUnboundedUploads = false
    }

    /// `true` when `ext` (lower-case, no leading dot) is admitted by the
    /// policy's allowlist (`None` = allow all).
    let allowsExtension (ext: string) (policy: KnowledgeUploadPolicy) : bool =
        match policy.AllowedExtensions with
        | None -> true
        | Some allowed -> allowed.Contains ext

    /// `Some reason` when `byteLength` exceeds the policy's cap,
    /// `None` otherwise (no cap, or within it).
    let exceedsSizeCap (byteLength: int64) (policy: KnowledgeUploadPolicy) : string option =
        match policy.MaxUploadBytes with
        | Some max when byteLength > max ->
            Some(sprintf "file is %d bytes, exceeding the %d-byte upload limit" byteLength max)
        | _ -> None

    /// `true` when the preflight validator should warn: a `Team` /
    /// `MultiTeam` deployment (`teamScoped`) left `MaxUploadBytes`
    /// unset and did not explicitly accept unbounded uploads. The
    /// uncapped in-memory `byte[]` is then a per-tenant DoS lever.
    let warnsUncappedInTeamMode (teamScoped: bool) (policy: KnowledgeUploadPolicy) : bool =
        teamScoped && policy.MaxUploadBytes.IsNone && not policy.AcceptUnboundedUploads

// ─── Note authoring ──────────────────────────────────────────────

/// Parameters for creating a free-form note.
type AddNoteRequest = {
    Title: string
    /// Markdown body. Empty body is rejected.
    Body: string
}

/// Parameters for editing an existing note. `DocId` must reference a
/// `KnowledgeDocument` whose `Source` is `Note _` in the caller's scope.
type UpdateNoteRequest = {
    DocId: string
    Title: string
    Body: string
}

// ─── Standing AI context ────────────────────────────────────────

/// Team-curated standing context the AI assistant sees on every
/// message. Stored separately from the KB document index because it is
/// prompt-injection content, not retrieval-target content. Loaded by
/// `KnowledgeBase.Server.standingContextBuilder` and composed into the
/// system prompt at the deployment's `composeWithAI` call.
type AIContextEntry = {
    /// Markdown body. Empty body clears the standing context — the
    /// builder returns `""` and `compose` drops the contribution.
    Body: string
    UpdatedAt: DateTimeOffset
    /// User id of the author of the most recent edit. Surfaced in the
    /// UI for accountability and recorded in the `AIContextUpdated`
    /// audit event.
    UpdatedBy: string
}

// ─── API contract ─────────────────────────────────────────────────

type KnowledgeApi = {
    /// Anonymous-mode deployments upload KB documents into session
    /// scope; the handler derives the target scope from the resolved
    /// `StorageScope`, never from the caller (GP 4).
    // Phase 69g.tail — document ingestion (extract + chunk + embed) is
    // an expensive multi-stage path. Conservative per-subject cap;
    // dormant until an `IRateLimitStore` is composed.
    [<AllowAnonymous>]
    [<RateLimit(20, RateLimitSeconds.perMinute)>]
    UploadDocument: byte[] -> string -> Async<KnowledgeDocument>
    [<AllowAnonymous>]
    GetDocuments: unit -> Async<KnowledgeDocument list>
    /// Delete a single document from the caller's KB scope — removes the
    /// index entry, raw blob, and embedded vector chunks. Owner / Admin
    /// only in Team / MultiTeam modes (server-side gate, mirroring
    /// `SetAIContext`); unrestricted in Anonymous / AuthenticatedEphemeral
    /// / Individual modes where the caller only reaches their own scope.
    /// Refused with `Error` when storage scope is unresolved
    /// (ScopeResolutionMiddleware unwired). Idempotent on an unknown id.
    /// Stays `[<AllowAnonymous>]` at the dispatcher (anonymous users manage
    /// their own session-scoped KB); the per-mode owner/admin gate is a
    /// runtime decision the handler makes. Successful deletes emit a
    /// `Custom:KnowledgeDocumentDeleted` audit row.
    [<AllowAnonymous>]
    [<Audit "Custom:KnowledgeDocumentDeleted">]
    DeleteDocument: string -> Async<Result<unit, string>>
    [<AllowAnonymous>]
    GetStatus: string -> Async<IngestionStatus>
    [<AllowAnonymous>]
    IngestNarrative: IngestNarrativeRequest -> Async<Result<KnowledgeDocument, IngestNarrativeError>>
    /// Create a free-form team note. Body is split by blank lines into
    /// paragraph chunks and enqueued through the same RAG ingestion path
    /// as uploads, so the AI can retrieve the note like any other KB
    /// content. Empty body returns `Error`.
    [<AllowAnonymous>]
    AddNote: AddNoteRequest -> Async<Result<KnowledgeDocument, string>>
    /// Edit an existing note. Re-chunks the body, replaces the prior
    /// chunks in the vector store (per-doc scope-delete + re-enqueue),
    /// bumps `LastEditedAt`. Returns `Error` if `DocId` is not a note
    /// in the caller's scope.
    [<AllowAnonymous>]
    UpdateNote: UpdateNoteRequest -> Async<Result<KnowledgeDocument, string>>
    /// Read the team's standing AI context. Returns `None` when no
    /// context has been written for this scope or when the scope is
    /// `Anonymous` (no persistent scope).
    [<AllowAnonymous>]
    GetAIContext: unit -> Async<AIContextEntry option>
    /// Write the team's standing AI context. Body must be markdown;
    /// passing `""` clears the entry. Owner / Admin only in Team and
    /// MultiTeam modes; unrestricted in Individual /
    /// AuthenticatedEphemeral. Always rejected in Anonymous mode (no
    /// persistent scope). Emits an `AIContextUpdated` audit event on
    /// every successful write.
    [<RequiresClaim "scope">]
    SetAIContext: string -> Async<Result<AIContextEntry, string>>
    /// Wipe the caller's KB scope: deletes the index, every uploaded blob,
    /// and every embedded vector chunk. Owner / Admin only in Team and
    /// MultiTeam modes (server-side gate, mirroring `SetAIContext`);
    /// unrestricted in Anonymous / AuthenticatedEphemeral / Individual
    /// modes (the user only has access to their own scope). Refused with
    /// `Error` when storage scope is unresolved (ScopeResolutionMiddleware
    /// unwired) so a raw call can't wipe the shared anonymous container.
    /// Idempotent — calling twice on an empty index returns `Ok ()`.
    [<AllowAnonymous>]
    [<Audit "Custom:KnowledgeIndexReset">]
    ResetIndex: unit -> Async<Result<unit, string>>
    /// Suggested zero-state questions for the AI side panel. The AI
    /// client renders 3-5 of these as clickable affordances when the
    /// conversation is empty so the user has a starting point grounded
    /// in their actual KB content. Pass the active module name (or
    /// `None` for global suggestions); the server samples document
    /// names and notes from the KB to produce contextual prompts.
    /// Returns an empty list when the KB is empty.
    [<AllowAnonymous>]
    GetSuggestedQuestions: string option -> Async<string list>
    /// Re-publish a fresh `InventoryUpdated` notification so subscribed
    /// clients (the AI side panel's KB-presence badge, the suggested-
    /// questions zero-state) re-anchor to the current KB state. Inventory
    /// is published automatically on every mutation, so this endpoint
    /// exists for two cases: (a) clients that connected after recent
    /// mutations and want to force a fresh snapshot, and (b) operators
    /// who want a manual "push current state to AI" affordance.
    /// Idempotent — safe to call repeatedly.
    [<AllowAnonymous>]
    RefreshAIContext: unit -> Async<unit>
    /// Fetch the *original* ingested document for a `KnowledgeDocument`
    /// id (Phase 102) — the handle a citation's
    /// `RetrievedSource.OriginalRef.DocumentId` carries. Scope-gated:
    /// the lookup runs against the caller's resolved scope only, so an
    /// out-of-scope id returns `Error NotInScope` (no bytes, no
    /// existence signal) and emits a denial audit. Source-kind-aware
    /// via `IOriginalSourceResolver` (Phase 104): uploaded files return
    /// their raw bytes + content type, notes return their markdown,
    /// synthetic sources (narratives, AI-context) return
    /// `Error NoOriginalAvailable`. Every successful fetch emits a
    /// `KnowledgeOriginalRetrieved` audit event (Phase 107).
    /// Deliberately NOT `[<Audit>]`-annotated — the Phase 107 handler
    /// emits the richer `KnowledgeOriginalRetrieved` row; a dispatcher
    /// attribute here would double-row the trail.
    [<AllowAnonymous>]
    GetOriginalDocument: string -> Async<Result<OriginalDocument, KnowledgeBaseError>>
}