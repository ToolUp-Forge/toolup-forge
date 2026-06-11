// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.VectorKnowledgeTypes

/// Determines which knowledge store a vector chunk belongs to or is queried from.
/// Scopes are ordered from broadest (Platform) to narrowest (Team). Access is
/// validated at the `IRetrievalPipeline` layer — consumers never call `IVectorStore`
/// directly; they go through the pipeline which enforces that the calling
/// `AccessContext` is permitted to read each requested scope.
type VectorScope =
    /// Cross-team global knowledge. Populated by Platform Admins via
    /// `IPlatformKnowledgeApi.UploadPlatformDocument`. When
    /// `ServerConfig.PlatformKnowledgeBase = Enabled`, universally
    /// readable for authenticated callers; when `Disabled`,
    /// `RetrievalPipeline.authorisedScopes` filters this scope out of
    /// the returned list so existing chunks stay on disk but are
    /// invisible to retrieval. `ListPlatformDocuments` keeps working
    /// either way so admins can pre-populate before flipping the read
    /// switch.
    ///
    /// HARD RULE: only Platform Admins may write to this scope.
    /// Narrative-commit, KB document upload, KB note creation, AI-
    /// context writes, and every other in-app authoring path is
    /// structurally restricted to the caller's `Team teamId` scope
    /// (or session scope in Anonymous / AuthenticatedEphemeral) — no
    /// caller-supplied scope parameter on any team-side endpoint.
    /// The only write surface that targets `Platform` is
    /// `IPlatformKnowledgeApi`, gated server-side by
    /// `AccessContext.canModifyPlatformConfig`.
    | Platform
    /// Per-deployment shared knowledge. Set up for a specific app instance and shared
    /// across all teams on that deployment (e.g. agency house style, approved vendor
    /// lists). Readable by all users; writable by deployment owners.
    | Deployment
    /// Team-private knowledge. Readable and writable only by members of the named team.
    /// All `KnowledgeBase` module uploads land here by default.
    | Team of teamId: string

/// A unit of text content ready to be embedded and indexed. `Metadata` is
/// a free-form string map that travels with the vector through storage and
/// retrieval — use it to carry provenance (`"_source"`), module context
/// (`"dataTypeId"`), or any application-specific tags.
type TextChunk = {
    Content: string
    Metadata: Map<string, string>
}

/// Metadata-key constants reserved by the platform. Module authors and
/// vectorisation handlers may read these but should not write them — the
/// retrieval pipeline and vector store own their lifecycle.
module ChunkMetadata =
    /// Tombstone marker — ISO 8601 UTC timestamp of the soft-delete.
    /// `IVectorStore.Search` filters chunks where this key is present;
    /// `Vacuum` hard-removes entries past the retention window.
    [<Literal>]
    let DeletedAtKey = "_deletedAt"

    /// Origin classification — set by the producer (KB, narrative-commit,
    /// AI-context writer) at ingestion time so retrieval can filter by
    /// content kind without coupling RAG to the producer's concrete types.
    /// Value is the case name of `ChunkOrigin` (e.g. `"Document"`, `"Note"`).
    [<Literal>]
    let OriginKey = "_origin"

    /// Free-form human-readable location hint — page / slide / row / section.
    /// Set by the chunk producer alongside `_source` so retrieval can surface
    /// a click-through label without parsing the structured `_source` JSON.
    /// e.g. `"Page 4"`, `"Slide 12: Methodology"`, `"rows 102–250"`.
    [<Literal>]
    let LocationHintKey = "_locationHint"

    /// Module identifier the chunk was produced from — `ActiveModule`-aware
    /// retrieval boosts chunks whose value matches the caller's
    /// `RetrievalRequest.ActiveModule`. Stamped by narrative-commit and
    /// AI-context writers; empty / absent means "no module preference".
    [<Literal>]
    let OriginModuleKey = "_originModule"

    /// Marks a chunk as the document-level AI-generated summary produced
    /// by `VectorisationHandler.Summarise`. Retrieval applies a small
    /// score boost to summary chunks so "what is this document about?"
    /// queries reliably surface the summary first. Value is `"true"`
    /// when set; absent / any other value means "not a summary".
    [<Literal>]
    let IsSummaryKey = "_isSummary"

    /// Structured original-document reference (Phase 103) — JSON-serialised
    /// `OriginalDocumentRef` stamped by the chunk producer at ingestion
    /// when the chunk's source has a fetchable binary original. Retrieval
    /// reads the ref instead of rebuilding the producer's blob-name
    /// convention. Absent for chunks whose source has no original
    /// (notes, narratives, AI-context) and for chunks ingested before
    /// the producer stamped refs — absence surfaces as
    /// `RetrievedSource.OriginalRef = None` (GP 9: never guessed).
    [<Literal>]
    let OriginalRefKey = "_originalRef"

/// Coarse classification of where a chunk came from. Stamped onto chunk
/// metadata at ingestion time under `ChunkMetadata.OriginKey`. RAG stays
/// agnostic to the producer's domain types — KB / narrative / AI-context
/// writers categorise their own output via this enum, and `RetrievalRequest`
/// can filter by origin set without naming any KB type.
type ChunkOrigin =
    /// User-uploaded document (PDF / PPTX / DOCX / XLSX / CSV / TXT).
    | Document
    /// Free-form team note typed directly into the KB.
    | Note
    /// Module-generated narrative committed via narrative-commit.
    | Narrative
    /// Standing AI-context entry. Excluded from default retrieval because
    /// the deployment already injects it verbatim every turn via a system-
    /// prompt builder — re-retrieving would be a double-injection.
    | AIContext
    /// Indexed conversation turn (scaffolding — off by default).
    | Conversation
    /// Catch-all for producers that don't fit a built-in category.
    | Other of label: string

module ChunkOrigin =
    /// Round-trip a `ChunkOrigin` to its metadata-string form.
    let toMetadataValue (origin: ChunkOrigin) : string =
        match origin with
        | Document -> "Document"
        | Note -> "Note"
        | Narrative -> "Narrative"
        | AIContext -> "AIContext"
        | Conversation -> "Conversation"
        | Other label -> sprintf "Other:%s" label

    /// Parse the metadata-string form back into `ChunkOrigin`. Unknown
    /// values are surfaced as `Other` so producers from outside the SDK
    /// round-trip cleanly through retrieval.
    let fromMetadataValue (value: string) : ChunkOrigin =
        match value with
        | "Document" -> Document
        | "Note" -> Note
        | "Narrative" -> Narrative
        | "AIContext" -> AIContext
        | "Conversation" -> Conversation
        | s when s.StartsWith "Other:" -> Other(s.Substring 6)
        | s -> Other s

/// A retrieved chunk together with its relevance score and provenance.
/// Returned by `IRetrievalPipeline.Retrieve`; consumers inspect `Scope`
/// to understand the authority of the content (platform benchmark vs
/// team upload) and `Metadata` for source document details.
type VectorMatch = {
    ChunkId: string
    Content: string
    Score: float
    Scope: VectorScope
    Metadata: Map<string, string>
}

/// Neutral within-document locator for the cited location (Phase 106).
/// A deliberately small mirror of the producer-side provenance cases
/// (page / slide / sheet / section / row-group) so a Sources panel can
/// construct a deep link / scroll target into the original document —
/// without `ToolUp.Platform` referencing any producer's domain types
/// (GP 1: the KnowledgeBase `SourceLocation` DU maps onto this at the
/// producer boundary, never the reverse). `RequireQualifiedAccess`
/// because the case names (`Page`, `Section`, …) collide with
/// producer-side DUs that files commonly open alongside this module.
[<RequireQualifiedAccess>]
type SourceLocator =
    /// Page number within a paginated document (PDF). 1-based.
    | Page of number: int
    /// Slide number within a presentation (PPTX). 1-based.
    | Slide of number: int
    /// Sheet name within a workbook (XLSX).
    | Sheet of name: string
    /// Section heading within a structured document (DOCX, plain text).
    | Section of heading: string
    /// Inclusive 1-based source-row range within tabular data (CSV).
    | RowGroup of startRow: int * endRow: int

/// Structured reference to the *original* ingested document behind a
/// retrieved chunk (Phase 103). Value-typed and producer-neutral — no
/// server handles, no KB-layer types — so it can live in
/// `ToolUp.Platform` and cross the AI/RAG/KB tri-package boundary the
/// same way `RetrievedSource` does. The ref carries no bytes and is
/// not a fetch capability: retrieving the original goes through the
/// producer's scope-gated retrieval surface (e.g. the KnowledgeBase
/// `GetOriginalDocument` API), which enforces team isolation (GP 4).
type OriginalDocumentRef = {
    /// Source-document id (matches `KnowledgeDocument.Id` for KB-sourced
    /// chunks). The handle a client passes to the retrieval surface.
    DocumentId: string
    /// Original file name (e.g. "Q3 brand audit.pdf").
    FileName: string
    /// File-type extension as stamped by the producer ("pdf", "pptx",
    /// "docx", "xlsx", "csv", "txt").
    FileType: string
    /// Size of the original blob in bytes. Lets the UI show a download
    /// size before fetching.
    SizeBytes: int64
    /// Within-document locator for the cited location (Phase 106).
    /// `None` when the chunk has no precise anchor (plain-text bodies,
    /// producers that don't stamp locations) — never fabricated (GP 9).
    Location: SourceLocator option
}

/// Wire-format record for a retrieved chunk surfaced to the AI client. A
/// projection of `VectorMatch` that strips the embedding-pipeline internals
/// (chunk id, full content, scope) and adds caller-friendly fields the UI
/// renders directly. The AI assistant attaches a list of these to each
/// outbound `AIMessage` so the client can render a "Sources" panel under
/// the assistant's reply, and so users can audit grounding.
///
/// Lives in `ToolUp.Platform` so it crosses the AI/RAG/KB tri-package
/// boundary via Platform — no new compile-time edges between companions.
type RetrievedSource = {
    /// Source-document id (matches `KnowledgeDocument.Id` for KB-sourced
    /// chunks). Empty string when no `_source` metadata is present.
    DocumentId: string
    /// Human-readable document name for display (e.g. "Q3 brand audit.pdf").
    DocumentName: string
    /// Up to ~240 chars of the chunk content, suitable for a one-line
    /// preview in the Sources panel. Truncated with an ellipsis when the
    /// chunk content exceeds the budget.
    Snippet: string
    /// Cosine / fused score from the retrieval pipeline. Higher = more
    /// relevant. Surfaced as a small badge so users can spot weak matches.
    Score: float
    /// Origin classification — `Document` for uploaded files, `Note` for
    /// free-form notes, `Narrative` for module-committed narratives, etc.
    /// Reads `ChunkMetadata.OriginKey` from chunk metadata; falls back to
    /// `Other "unknown"` when the producer didn't stamp one.
    Origin: ChunkOrigin
    /// Free-form location hint extracted from `_source` metadata when
    /// present (e.g. "Page 4", "Slide 12: Methodology", "rows 102–250").
    /// `None` when no `_source` metadata is present or the source kind
    /// doesn't have a meaningful location.
    LocationHint: string option
    /// Structured reference to the fetchable original document (Phase
    /// 103), read from the `ChunkMetadata.OriginalRefKey` metadata the
    /// producer stamped at ingestion. `Some` only when the chunk's
    /// source has a binary original (uploaded files); `None` for note /
    /// narrative / AI-context chunks and for chunks ingested before the
    /// producer stamped refs. Additive and backward-compatible (GP 11):
    /// pre-existing wire payloads without the field deserialise to
    /// `None`.
    OriginalRef: OriginalDocumentRef option
}

/// Controls how results from multiple scopes are combined.
type MergeStrategy =
    /// Re-rank all results by embedding similarity score regardless of scope.
    /// Use for AI context injection where the single most-relevant chunks win.
    | Interleaved
    /// Return results grouped by scope, labelled. Use when the caller wants
    /// to present "platform knowledge" and "your team's data" separately, or
    /// when debugging retrieval quality.
    | Separate

/// Hint to the adaptive top-K stage. When the score margin between
/// successive matches collapses below `ScoreFloor`, the pipeline truncates
/// to the smaller of `MinK` and the natural cutoff. `MaxK` caps the
/// candidate pool the pipeline asks each store for. Used by the
/// adaptive top-K stage; ignored by the cosine-only path until then.
type AdaptiveKHint = {
    MinK: int
    MaxK: int
    ScoreFloor: float
}

/// Input to `IRetrievalPipeline.Retrieve`. `Scopes` is ordered by caller
/// preference — implementations may use this for tie-breaking. `TopK`
/// applies to the total result set after merging when `Merge = Interleaved`,
/// or per-scope when `Merge = Separate`.
///
/// `Filters`, `History`, and `AdaptiveK` are optional extensions used by
/// later pipeline stages (metadata filtering, conversation-aware retrieval,
/// adaptive top-K). Construct via `RetrievalRequest.create` to default them.
type RetrievalRequest = {
    Query: string
    Scopes: VectorScope list
    TopK: int
    Merge: MergeStrategy
    /// Optional metadata-equality filter applied at retrieval time. Each
    /// pair must match the chunk's `Metadata` exactly. `None` = no filter.
    Filters: Map<string, string> option
    /// Prior conversation turns, oldest-first. Used by query-rewrite stages
    /// to disambiguate pronouns / follow-up references. `None` = first turn.
    History: string list option
    /// Adaptive top-K hint. `None` = pipeline uses fixed `TopK` exactly.
    AdaptiveK: AdaptiveKHint option
    /// When `Some`, retrieval drops chunks whose `_origin` metadata is not
    /// in the set. `None` keeps every origin — equivalent to "show me
    /// everything that matches semantically". Useful for excluding
    /// `AIContext` (already injected verbatim every turn) or restricting
    /// to a single origin (e.g. only documents, not narratives).
    OriginFilter: Set<ChunkOrigin> option
    /// Module the user was viewing when the query was submitted. When
    /// `Some`, the pipeline applies a small score boost to chunks whose
    /// `_originModule` metadata matches — surfacing module-relevant
    /// content first when the user asks from a specific module's view.
    /// `None` (default) preserves prior behaviour.
    ActiveModule: string option
}

module RetrievalRequest =
    /// Construct a `RetrievalRequest` with the optional fields defaulted to
    /// `None`. Existing callers should migrate to this helper to insulate
    /// against future field additions.
    let create (query: string) (scopes: VectorScope list) (topK: int) (merge: MergeStrategy) : RetrievalRequest = {
        Query = query
        Scopes = scopes
        TopK = topK
        Merge = merge
        Filters = None
        History = None
        AdaptiveK = None
        OriginFilter = None
        ActiveModule = None
    }

/// Per-deployment defaults applied by `RAGPromptBuilder.withRetrieval`. Lets
/// operators tune retrieval shape (top-K, score gate, merge strategy,
/// snippet budget, origin filter) without recompiling — wired via
/// `RAGServerApp.withRetrievalDefaults`.
type RetrievalDefaults = {
    /// Number of matches to include in the system-prompt context block.
    /// Higher lets the model ground in more material; too high crowds the
    /// context window and washes out per-source attention. Default 5.
    TopK: int
    /// Minimum score a match must clear to be surfaced to the model. `None`
    /// passes everything the pipeline returns; `Some x` drops every match
    /// scoring at or below `x`. Useful for deployments where a poorly-
    /// matched paragraph confuses more than it helps. Default `None`.
    MinScore: float option
    /// Merge strategy for multi-scope retrieval. `Interleaved` re-ranks by
    /// score regardless of scope; `Separate` keeps per-scope grouping.
    /// Default `Interleaved` — matches the prior behaviour.
    Merge: MergeStrategy
    /// Maximum characters of chunk content surfaced as a `RetrievedSource.Snippet`
    /// preview in the AI client's Sources panel. Trimmed with an ellipsis when
    /// content exceeds the budget. Default 240.
    SnippetCharLimit: int
    /// Default origin filter applied per-call when the `RetrievalRequest`
    /// itself doesn't carry one. Defaults exclude `AIContext` because it's
    /// already injected verbatim every turn via `standingContextBuilder` —
    /// re-retrieving would double-inject. Operators wanting AI-context
    /// chunks back in the retrieval set pass `None` to clear the gate.
    OriginFilter: Set<ChunkOrigin> option
}

module RetrievalDefaults =
    /// Built-in default origin filter — excludes `AIContext` from semantic
    /// retrieval (it's injected verbatim every turn elsewhere). Anything
    /// else is allowed; modules wanting to be excluded should categorise
    /// their chunks with a different `ChunkOrigin` and the operator can
    /// extend this set via `withOriginFilter`.
    let private defaultOriginFilter: Set<ChunkOrigin> =
        Set.ofList [ Document; Note; Narrative; Conversation ]

    /// Default values used when a deployment doesn't override.
    let defaults: RetrievalDefaults = {
        TopK = 5
        MinScore = None
        Merge = Interleaved
        SnippetCharLimit = 240
        OriginFilter = Some defaultOriginFilter
    }

    /// Clamp a `RetrievalDefaults` to sane bounds. The targeted fluent
    /// setters (`withTopK` / `withMinScore` / `withSnippetCharLimit`)
    /// clamp individually, but `withRetrievalDefaults` replaces the
    /// whole record — a fat-fingered `TopK = 0` (no context surfaced)
    /// or `MinScore = Some 1.0` (every match gated) would otherwise
    /// silently disable retrieval with no diagnostic. Applied wherever
    /// a whole-record override enters, so every path is bounded
    /// identically:
    ///   * `TopK >= 1`
    ///   * `MinScore` (when set) in `[0.0, 0.99]`
    ///   * `SnippetCharLimit >= 16`
    let clamp (d: RetrievalDefaults) : RetrievalDefaults = {
        d with
            TopK = max 1 d.TopK
            MinScore = d.MinScore |> Option.map (fun t -> max 0.0 (min 0.99 t))
            SnippetCharLimit = max 16 d.SnippetCharLimit
    }