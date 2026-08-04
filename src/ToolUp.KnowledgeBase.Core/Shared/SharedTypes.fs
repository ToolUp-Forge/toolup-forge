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
    /// Phase 500 — the document was stored, its type IS recognised, and
    /// it yielded no text because it has no text layer to yield: a
    /// scanned PDF, a photographed page, an image upload. Recovering
    /// that content needs an `IOcrProvider` companion, and this
    /// deployment composed none.
    ///
    /// This exists because the honest alternative did not: before it,
    /// such an upload reported `Complete 0` — a successful index of
    /// nothing — so the user was told their scan was searchable when it
    /// was not, and the operator got no signal that OCR was the missing
    /// piece. Distinct from `UnsupportedFormat` (the type itself is not
    /// recognised) and from `Failed` (nothing went wrong; a capability
    /// is simply absent). `detail` names the remedy, not just the
    /// symptom.
    | OcrUnavailable of detail: string

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
    /// Phase 14x — lowercase SHA-256 hex of the raw uploaded bytes,
    /// stamped by `UploadDocument` so a scope-local re-upload of the
    /// same file short-circuits to this document instead of
    /// re-ingesting (see `KnowledgeDedupPolicy`). `None` for notes,
    /// narratives, and documents ingested before the dedup landed —
    /// legacy entries never match, so the first re-upload of a legacy
    /// document still re-ingests once and the new entry then carries
    /// the hash. Deserialising a pre-14x index record leaves this
    /// `None` (missing JSON properties absorb leniently).
    ContentHash: string option
    /// Phase 510 — 1-based version of THIS document's lineage. The
    /// `Id` is the stable lineage identity: a versioned re-upload
    /// supersedes in place (same `Id`, same `{Id}:chunk:{n}` namespace)
    /// and increments this, so the index always describes exactly one
    /// live document per lineage and retrieval structurally targets the
    /// current version — there is no stale-version chunk to filter out.
    /// Prior versions survive as immutable `KnowledgeDocumentVersion`
    /// records beside the document (GP 5) with their original bytes
    /// preserved.
    ///
    /// Always `>= 1` when read through `loadIndex`: a pre-510 index
    /// record carries no `Version` property at all, and a missing JSON
    /// *number* absorbs to `0` rather than to a sentinel we could
    /// detect later, so the store coerces `< 1` to `1` on load. Every
    /// legacy document is therefore version 1 of its own lineage, which
    /// is exactly what it is.
    Version: int
}

/// Phase 510 — an immutable record of one superseded version of a
/// document lineage. Written once, at the moment a new version takes
/// over, and never mutated afterwards (GP 5): the current version is
/// described by the live `KnowledgeDocument` in `knowledge/index.json`,
/// and every version before it by one of these.
///
/// `DocumentId` is the lineage id — the same value as the live
/// `KnowledgeDocument.Id` — so a citation's
/// `RetrievedSource.OriginalRef.DocumentId` resolves to the lineage and
/// then to whichever version is asked for, without the chunk ids ever
/// having to encode a version.
type KnowledgeDocumentVersion = {
    /// The lineage id (= the live `KnowledgeDocument.Id`).
    DocumentId: string
    /// 1-based version number this record describes.
    Version: int
    FileName: string
    FileType: string
    SizeBytes: int64
    /// Chunk count this version indexed, retained so a later repair
    /// pass knows how much of the `{docId}:chunk:{n}` namespace this
    /// version occupied.
    ChunkCount: int
    ContentHash: string option
    UploadedAt: DateTimeOffset
    UploadedBy: string
    /// Blob name of this version's preserved original bytes. The live
    /// version keeps the conventional `knowledge/{docId}/{fileName}`
    /// path; a superseded one is copied aside to
    /// `knowledge/{docId}/versions/{version}/{fileName}` before the live
    /// path is overwritten, so no version's bytes are ever lost to a
    /// re-upload.
    OriginalBlobName: string
    /// When this version stopped being current. `None` on the
    /// synthesised record for the version that is current right now.
    SupersededAt: DateTimeOffset option
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

// ─── Original-document preview (Phase 200) ───────────────────────
//
// Phase 106 gave a citation a `SourceLocator` — *where* in the original
// the answer came from. Phase 102/104 gave the caller the original's
// bytes. Nothing tied the two together, so every consumer had to
// re-derive "open the PDF at page 4 and highlight it" from two
// unrelated values. `PreviewAnchor` + `PreviewTarget` are that tie: a
// neutral, viewer-agnostic descriptor a consumer's PDF / PPTX / HTML
// viewer reads to open-at-and-highlight. The SDK ships no viewer
// (GP 1) — only the seam, the descriptor, and the projection.

/// Where a consumer's viewer should open the original document, and
/// what it should highlight. Deliberately *neutral*: it names a
/// location in document terms (page, slide, sheet, heading, row range,
/// character range), never a viewer API, a DOM selector, or a
/// URL-fragment convention — so one anchor serves a PDF.js viewer, a
/// server-side render, and a plain download alike.
///
/// `RequireQualifiedAccess` because the case names collide with
/// `SourceLocation`'s, which files commonly open alongside this one.
///
/// **Consuming each case** (the documented anchor contract; see
/// `docs/migrations/200-original-document-preview-anchor-highlight-seam.md`):
///
///   * `Page n` — PDF viewers: scroll to 1-based page `n`
///     (`#page=n` in the PDF open-parameters convention).
///   * `Slide n` — PPTX viewers: 1-based slide index.
///   * `Sheet name` — XLSX viewers: activate the named worksheet.
///   * `Section heading` — DOCX / HTML / markdown: locate the heading
///     text and scroll to it. Text, not an id — the SDK does not
///     control the viewer's id scheme.
///   * `Rows(from, to)` — CSV / tabular: inclusive 1-based *source*
///     row range, header row included in the numbering.
///   * `CharRange(from, to)` — a document-relative character span,
///     half-open `[from, to)`, for text-shaped originals where a
///     citation carries exact offsets rather than a structural
///     location. This is the case a character-offset citation span maps
///     onto (see `PreviewAnchor.ofCharSpan`).
///   * `WholeDocument` — no precise anchor is known. Open at the top
///     and highlight nothing. Never fabricated into a guessed page
///     (GP 9): absence is a case, not a default of 1.
[<RequireQualifiedAccess>]
type PreviewAnchor =
    /// 1-based page within a paginated document (PDF).
    | Page of number: int
    /// 1-based slide within a presentation (PPTX).
    | Slide of number: int
    /// Worksheet name within a workbook (XLSX).
    | Sheet of name: string
    /// Section heading text within a structured document (DOCX, md).
    | Section of heading: string
    /// Inclusive 1-based source-row range within tabular data (CSV).
    | Rows of startRow: int * endRow: int
    /// Half-open `[startOffset, endOffset)` document-relative character
    /// span for text-shaped originals.
    | CharRange of startOffset: int * endOffset: int
    /// No precise anchor — open at the top, highlight nothing.
    | WholeDocument

module PreviewAnchor =
    /// Project the neutral `SourceLocator` a citation carries (Phase
    /// 106) onto the anchor a viewer consumes. Total and lossless in
    /// the direction that matters: every locator case has an anchor,
    /// and a citation with *no* locator becomes `WholeDocument` rather
    /// than a fabricated page 1 (GP 9).
    let ofLocator (locator: ToolUp.Platform.VectorKnowledgeTypes.SourceLocator option) : PreviewAnchor =
        match locator with
        | None -> PreviewAnchor.WholeDocument
        | Some(ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Page number) -> PreviewAnchor.Page number
        | Some(ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Slide number) -> PreviewAnchor.Slide number
        | Some(ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Sheet name) -> PreviewAnchor.Sheet name
        | Some(ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.Section heading) -> PreviewAnchor.Section heading
        | Some(ToolUp.Platform.VectorKnowledgeTypes.SourceLocator.RowGroup(startRow, endRow)) ->
            PreviewAnchor.Rows(startRow, endRow)

    /// Project a **document-relative character span** onto a
    /// `CharRange` anchor. Takes a plain offset pair rather than any
    /// citation-span type so the preview seam never grows a dependency
    /// on a retrieval-side record: a caller holding a span record maps
    /// it here at its own boundary, exactly as `SourceLocation.toLocator`
    /// maps at the producer boundary.
    ///
    /// A degenerate span (negative start, or an end at or before the
    /// start) degrades to `WholeDocument` — a viewer asked to highlight
    /// nothing should open the document, not throw or highlight a
    /// nonsense range.
    let ofCharSpan (startOffset: int) (endOffset: int) : PreviewAnchor =
        if startOffset < 0 || endOffset <= startOffset then
            PreviewAnchor.WholeDocument
        else
            PreviewAnchor.CharRange(startOffset, endOffset)

/// How the original's bytes reach the consumer's viewer.
///
/// `Inline` embeds the `OriginalDocument` (the Phase 102 wire record) —
/// simplest, and what a deployment that composes nothing extra gets.
/// `SignedUrl` hands back a **time-bound** URL instead, so large
/// originals are fetched directly from storage rather than streamed
/// through the API — the same byte-light posture `IMediaLibrary` takes
/// for range-served media (Phase 88). The URL carries its own expiry so
/// a client can tell a stale link from a denied one without a round trip.
[<RequireQualifiedAccess>]
type PreviewContent =
    /// The original bytes, inline.
    | Inline of original: OriginalDocument
    /// A time-bound URL and the instant it stops working (UTC).
    | SignedUrl of url: string * expiresAt: DateTimeOffset

/// Everything a consumer's viewer needs to open the original document
/// at the cited location: the content reference plus the neutral anchor.
/// Carries the original's metadata at the top level so the
/// `SignedUrl` delivery mode can pick a viewer by content type without
/// first downloading anything.
///
/// For `PreviewContent.Inline`, `FileName` / `ContentType` / `SizeBytes`
/// mirror the embedded `OriginalDocument`'s fields — construct through
/// `PreviewTarget.ofOriginal` / `PreviewTarget.withSignedUrl` so the two
/// cannot diverge.
type PreviewTarget = {
    /// The `KnowledgeDocument.Id` this preview is of.
    DocumentId: string
    /// Original file name (e.g. "Q3 brand audit.pdf").
    FileName: string
    /// MIME content type — which viewer to pick.
    ContentType: string
    /// Size of the original in bytes.
    SizeBytes: int64
    /// How the bytes are delivered.
    Content: PreviewContent
    /// Where to open, and what to highlight.
    Anchor: PreviewAnchor
}

module PreviewTarget =
    /// Inline-delivery target over a resolved original.
    let ofOriginal (documentId: string) (anchor: PreviewAnchor) (original: OriginalDocument) : PreviewTarget = {
        DocumentId = documentId
        FileName = original.FileName
        ContentType = original.ContentType
        SizeBytes = original.SizeBytes
        Content = PreviewContent.Inline original
        Anchor = anchor
    }

    /// Signed-URL-delivery target over a resolved original: the same
    /// metadata, but the bytes stay out of the response.
    let withSignedUrl
        (documentId: string)
        (anchor: PreviewAnchor)
        (original: OriginalDocument)
        (url: string)
        (expiresAt: DateTimeOffset)
        : PreviewTarget =
        {
            DocumentId = documentId
            FileName = original.FileName
            ContentType = original.ContentType
            SizeBytes = original.SizeBytes
            Content = PreviewContent.SignedUrl(url, expiresAt)
            Anchor = anchor
        }

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

// ─── Document dedup policy (Phase 14x) ───────────────────────────

/// Compose-time content-hash dedup policy for KB uploads. When
/// `DedupUploads` is `true` (the default a deployment gets without
/// calling `withDocumentDedup`), `UploadDocument` SHA-256-hashes the
/// raw uploaded bytes and — when the caller's scope already holds a
/// document with the same hash — returns the existing document instead
/// of re-ingesting: uploads are idempotent and the corpus never
/// accumulates byte-identical duplicates (Phase 14x). The dedup
/// decision is audited (`KnowledgeDocumentDeduplicated`) and surfaced
/// to the user as an Info `SystemMessage`.
///
/// `KnowledgeBase.Server.withDocumentDedup false` opts a deployment out
/// for cases where byte-identical re-uploads should legitimately stay
/// separate documents (e.g. contract revisions where each upload is its
/// own record); the opt-out restores the pre-14x upload path
/// byte-for-byte (GP 11).
type KnowledgeDedupPolicy = { DedupUploads: bool }

module KnowledgeDedupPolicy =
    /// The default: dedup on. Resolved when a deployment never calls
    /// `withDocumentDedup`.
    let enabled: KnowledgeDedupPolicy = { DedupUploads = true }

    /// The opt-out: pre-14x behaviour byte-for-byte — no hashing, no
    /// lookup, every upload ingests.
    let disabled: KnowledgeDedupPolicy = { DedupUploads = false }

// ─── Document versioning policy (Phase 510) ──────────────────────

/// Compose-time lever for KB upload **versioning**. When
/// `VersionUploads` is `true`, re-uploading an *edited* file under a
/// name the caller's scope already holds supersedes that document in
/// place — same `Id`, `Version + 1`, the prior version preserved as an
/// immutable `KnowledgeDocumentVersion` — instead of landing a second,
/// unrelated document beside the first.
///
/// **The default is `false`, and deliberately so.** Versioning is not a
/// bug fix that can be switched on under a deployment: it changes what
/// a re-upload MEANS. A corpus where every revision is its own record
/// (a contracts archive, an evidence bundle) is a legitimate and common
/// shape, and silently collapsing those records into one lineage would
/// destroy the distinction the deployment was relying on. So an
/// existing deployment keeps the pre-510 behaviour exactly — two
/// documents, two chunk namespaces, nothing superseded, no version
/// sidecar written, no extra blob read on the upload path (GP 11 /
/// GP 13) — until it opts in with
/// `KnowledgeBase.Server.withDocumentVersioning true`.
///
/// Versioning composes *after* content-hash dedup rather than
/// replacing it: a byte-IDENTICAL re-upload still dedups onto the
/// existing document (nothing changed, so there is no new version to
/// mint), and only a byte-DIFFERENT upload under a name already held
/// reaches the supersede path. The two levers answer different
/// questions — "is this the same bytes?" and "is this a new revision of
/// the same document?".
type KnowledgeVersioningPolicy = {
    /// `true` to supersede a same-named document in place on re-upload.
    VersionUploads: bool
}

module KnowledgeVersioningPolicy =
    /// The default a deployment gets without calling
    /// `withDocumentVersioning`: no lineage, no version sidecar —
    /// pre-510 upload behaviour byte-for-byte (GP 11).
    let disabled: KnowledgeVersioningPolicy = { VersionUploads = false }

    /// The opt-in: a re-upload under an existing name becomes the next
    /// version of that document.
    let enabled: KnowledgeVersioningPolicy = { VersionUploads = true }

// ─── Per-scope quota + retention (Phase 512) ─────────────────────

/// Compose-time per-scope Knowledge Base storage quota — the corpus-level
/// sibling of `KnowledgeUploadPolicy`, which caps ONE upload. Both levers
/// are `None` by default (`KnowledgeQuotaPolicy.unlimited`, what a
/// deployment that never calls `withKnowledgeQuota` gets), so an
/// uncomposed deployment is byte-for-byte its pre-512 self: no count is
/// taken, no refusal is possible (GP 11 / GP 13). Value-typed (GP 12).
///
/// The quota is always evaluated against the caller's **resolved scope**
/// — the container the server-side scope resolver produced, never a
/// caller-supplied id — so one team's corpus can never consume another
/// team's headroom (GP 4).
type KnowledgeQuotaPolicy = {
    /// Maximum number of `KnowledgeDocument`s a scope may hold. `None` =
    /// unlimited. Counted across every `KnowledgeSource` kind — uploads,
    /// notes, and committed narratives all occupy the same index, so the
    /// cap describes the corpus a scope actually carries.
    MaxDocuments: int option
    /// Maximum total `SizeBytes` across a scope's documents. `None` =
    /// unlimited. Distinct from `KnowledgeUploadPolicy.MaxUploadBytes`,
    /// which is a per-file in-memory-DoS guard; this bounds accumulated
    /// storage growth.
    MaxBytes: int64 option
}

module KnowledgeQuotaPolicy =
    /// The default: no document-count cap, no byte cap. Resolved when a
    /// deployment never composes `withKnowledgeQuota` — pre-512 behaviour
    /// exactly (GP 11).
    let unlimited: KnowledgeQuotaPolicy = { MaxDocuments = None; MaxBytes = None }

    /// `true` when neither lever is set — the upload path can then skip
    /// the usage tally entirely (GP 13: an uncomposed deployment pays
    /// nothing, not even a `List.sumBy`).
    let isUnlimited (policy: KnowledgeQuotaPolicy) : bool =
        policy.MaxDocuments.IsNone && policy.MaxBytes.IsNone

    /// `Some reason` when admitting one more document of `incomingBytes`
    /// would breach the scope's quota, `None` otherwise. The reason is
    /// user-legible on purpose — it is surfaced verbatim through the
    /// existing `IngestionStatus.UploadRejected` (a quota breach IS a
    /// pre-persist policy refusal, so it needs no new status case) and
    /// has to tell the uploader what to delete, not merely that they
    /// were refused (GP 9).
    ///
    /// The document cap is evaluated as "does the scope already hold
    /// `MaxDocuments`" and the byte cap as "would current + incoming
    /// exceed `MaxBytes`" — so a policy of `MaxDocuments = Some 0` or
    /// `MaxBytes = Some 0` refuses every upload rather than admitting a
    /// first freebie.
    let exceeds
        (currentDocuments: int)
        (currentBytes: int64)
        (incomingBytes: int64)
        (policy: KnowledgeQuotaPolicy)
        : string option =
        match policy.MaxDocuments with
        | Some maxDocs when currentDocuments >= maxDocs ->
            Some(
                sprintf
                    "knowledge-base document quota reached — this scope holds %d of %d permitted documents. Delete an existing document before uploading another."
                    currentDocuments
                    maxDocs
            )
        | _ ->
            match policy.MaxBytes with
            | Some maxBytes when currentBytes + incomingBytes > maxBytes ->
                Some(
                    sprintf
                        "knowledge-base storage quota reached — this scope uses %d of %d permitted bytes and the upload adds %d. Delete an existing document before uploading another."
                        currentBytes
                        maxBytes
                        incomingBytes
                )
            | _ -> None

/// Compose-time per-scope age-based retention. `MaxAge = None`
/// (`KnowledgeRetentionPolicy.retainForever`, what a deployment that
/// never calls `withKnowledgeRetention` gets) retains everything
/// forever — pre-512 behaviour, and the reason an uncomposed deployment
/// registers no sweep job at all (GP 11 / GP 13).
///
/// Expiry is deliberately **per source kind**. An uploaded file ageing
/// out of a corpus is routine; a hand-authored note or a committed
/// narrative disappearing on a timer is a data-loss surprise, so both
/// are opt-in on top of `MaxAge` rather than swept by default.
type KnowledgeRetentionPolicy = {
    /// Documents whose `UploadedAt` is older than this are purged on the
    /// next sweep. `None` = retain forever (no sweep is scheduled).
    MaxAge: TimeSpan option
    /// Expire `Note` documents too. `false` by default.
    ExpireNotes: bool
    /// Expire `FromNarrative` documents too. `false` by default.
    ExpireNarratives: bool
    /// Five-field cron expression for the sweep, in the subset
    /// `IJobScheduler` validates (`*`, integers, comma lists, `*/N`).
    /// Defaults to 03:00 daily — off the ingestion peak, and `Minute`
    /// precision so the in-process scheduler accepts it (GP 12 rule 6).
    SweepSchedule: string
}

module KnowledgeRetentionPolicy =
    /// The default: nothing ever expires, uploads-only if a `MaxAge` is
    /// later set, daily 03:00 sweep cadence.
    let retainForever: KnowledgeRetentionPolicy = {
        MaxAge = None
        ExpireNotes = false
        ExpireNarratives = false
        SweepSchedule = "0 3 * * *"
    }

    /// `true` when the policy can never expire anything — no `MaxAge`.
    /// The compose helper reads this to decide whether to register the
    /// sweep job at all.
    let isInert (policy: KnowledgeRetentionPolicy) : bool = policy.MaxAge.IsNone

    /// `true` when `source`'s kind is in scope for expiry under `policy`.
    let appliesTo (source: KnowledgeSource) (policy: KnowledgeRetentionPolicy) : bool =
        match source with
        | UploadedFile -> true
        | Note _ -> policy.ExpireNotes
        | FromNarrative _ -> policy.ExpireNarratives

    /// `true` when `doc` is past `MaxAge` *and* its source kind is in
    /// scope. Pure — the sweep's whole selection decision, testable
    /// without storage, a scheduler, or a clock.
    let isExpired (now: DateTimeOffset) (policy: KnowledgeRetentionPolicy) (doc: KnowledgeDocument) : bool =
        match policy.MaxAge with
        | None -> false
        | Some maxAge -> appliesTo doc.Source policy && now - doc.UploadedAt > maxAge

    /// The expired subset of `docs`, in index order. `retainForever`
    /// returns `[]` for any input — the GP 11 guarantee stated as code.
    let selectExpired
        (now: DateTimeOffset)
        (policy: KnowledgeRetentionPolicy)
        (docs: KnowledgeDocument list)
        : KnowledgeDocument list =
        docs |> List.filter (isExpired now policy)

/// Per-scope Knowledge Base usage against the composed quota — what the
/// UI renders as a headroom bar and what the dev endpoint reports.
/// Wholly *derived* from the scope's document index plus the composed
/// `KnowledgeQuotaPolicy`; nothing new is persisted for it, so it can
/// never drift from the corpus it describes.
type KnowledgeScopeUsage = {
    /// Documents the scope currently holds, all source kinds.
    DocumentCount: int
    /// Sum of `SizeBytes` across those documents.
    TotalBytes: int64
    /// The composed caps, echoed so a client can render "12 / 50"
    /// without a second round-trip. `None` = that lever is unlimited.
    MaxDocuments: int option
    MaxBytes: int64 option
    /// Headroom, floored at zero. `None` exactly when the corresponding
    /// cap is `None` — an unlimited quota has no "remaining" to render,
    /// which is a different statement from "zero remaining".
    DocumentsRemaining: int option
    BytesRemaining: int64 option
}

module KnowledgeScopeUsage =
    /// Project a scope's document index onto its usage against `policy`.
    let ofDocuments (policy: KnowledgeQuotaPolicy) (docs: KnowledgeDocument list) : KnowledgeScopeUsage =
        let count = List.length docs
        let bytes = docs |> List.sumBy _.SizeBytes

        {
            DocumentCount = count
            TotalBytes = bytes
            MaxDocuments = policy.MaxDocuments
            MaxBytes = policy.MaxBytes
            DocumentsRemaining = policy.MaxDocuments |> Option.map (fun m -> max 0 (m - count))
            BytesRemaining = policy.MaxBytes |> Option.map (fun m -> max 0L (m - bytes))
        }

    /// An empty scope under an unlimited quota — the zero value.
    let empty: KnowledgeScopeUsage = ofDocuments KnowledgeQuotaPolicy.unlimited []

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
    /// Phase 512 — the caller's scope usage against the composed
    /// `KnowledgeQuotaPolicy` (document count / total bytes, the caps,
    /// and the remaining headroom). Read-only and derived from the
    /// scope's own document index, so it carries no cross-tenant signal
    /// (GP 4) and needs no owner/admin gate — a member reading their own
    /// scope's headroom is the point. A deployment that composed no
    /// quota gets counts with `None` caps and `None` remaining, which is
    /// how a UI tells "unlimited" from "full".
    [<AllowAnonymous>]
    GetScopeUsage: unit -> Async<KnowledgeScopeUsage>
    /// Phase 510 — the version history of one document lineage, newest
    /// first: the current version (synthesised from the live index
    /// entry, `SupersededAt = None`) followed by every superseded
    /// version still preserved beside it. Scope-gated exactly like
    /// `GetOriginalDocument` — the lookup runs against the caller's
    /// resolved scope's index only, so an out-of-scope or unknown id
    /// returns `[]` rather than an existence signal (GP 4).
    ///
    /// A deployment that never composed `withDocumentVersioning`
    /// returns a single-element list for every document — which is
    /// honest rather than empty: an unversioned document genuinely IS
    /// version 1 of its own lineage.
    [<AllowAnonymous>]
    GetDocumentVersions: string -> Async<KnowledgeDocumentVersion list>
}