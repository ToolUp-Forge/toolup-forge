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
    ///
    /// HARD RULE: this scope is for genuinely deployment-wide *shared* content
    /// only. Per-user uploads on a non-team surface (Individual mode,
    /// AuthenticatedEphemeral, anonymous sessions) MUST NOT land here — they
    /// belong in `User` so one caller cannot retrieve another's chunks. Routing
    /// per-user content to `Deployment` reintroduces GAP-1 (cross-user KB leak).
    | Deployment
    /// Team-private knowledge. Readable and writable only by members of the named team.
    /// All team-surface `KnowledgeBase` module uploads land here by default.
    | Team of teamId: string
    /// Per-user private knowledge. The vector-layer twin of the `user-{id}`
    /// blob/index container: readable and writable only by the owning caller
    /// (matched by `AccessContext.UserId`). Every non-team KB upload
    /// (Individual mode, AuthenticatedEphemeral, anonymous session) lands here
    /// so per-user isolation carried by the blob container is preserved at the
    /// vector layer too (GP 4). Never a caller-supplied scope — derived
    /// server-side from the resolved `StorageScope` at ingestion and from the
    /// authenticated `AccessContext.UserId` at retrieval.
    | User of userId: string

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

    /// Referenced fact ids (Phase 521.D) — a comma-separated list of the
    /// content-addressed fact ids the chunk's narrative spans cite
    /// (`InlineSpan.Metric.factRef`). Stamped by narrative-commit when a
    /// section carries fact-referencing metric spans; absent for chunks
    /// with no fact references. Retrieval's fact→narrative join (Phase
    /// 522.E) reads this so a resolved fact can prefer the narrative
    /// chunks that cite it over generic similarity matches.
    [<Literal>]
    let FactRefsKey = "_factRefs"

    // ─── Fact-match metadata (Phase 522) ─────────────────────────────
    // A fact resolved by the pre-vector stage rides the retrieval result
    // set as a `VectorMatch` stamped `_origin = "Fact"` plus the keys
    // below, so `RAGPromptBuilder.toRetrievedSource` can project the fact
    // fields onto `RetrievedSource` without RAG depending on the fact
    // companion's types.

    /// Content-addressed id of a resolved fact (Phase 522.C). Present only
    /// on a `ChunkOrigin.Fact` match.
    [<Literal>]
    let FactIdKey = "_factId"

    /// Canonical value rendering of a resolved fact — the number quoted
    /// verbatim into the answer (Phase 522.C).
    [<Literal>]
    let FactRenderingKey = "_factRendering"

    /// Derived freshness of a resolved fact: `"Fresh"` or `"Stale:<iso>"`
    /// (the instant it went stale). Phase 522.C.
    [<Literal>]
    let FactFreshnessKey = "_factFreshness"

    /// When a resolved fact is stale/superseded, the id that superseded it
    /// (the lineage's current head). Absent for a fresh fact. Phase 522.C.
    [<Literal>]
    let FactSupersededByKey = "_factSupersededBy"

    /// Registered metric id a resolved fact is for — surfaced in the facts
    /// prompt block. Phase 522.C.
    [<Literal>]
    let FactMetricKey = "_factMetric"

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
    /// A fact resolved by the pre-vector fact-resolution stage (Phase
    /// 522): not a similarity match at all but an exact fact hit merged
    /// into the result set as a distinct origin, carrying its value
    /// rendering + freshness in metadata. Facts merge ahead of chunks and
    /// are rendered in a distinct prompt block whose numbers are quoted
    /// verbatim.
    | Fact
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
        | Fact -> "Fact"
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
        | "Fact" -> Fact
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

// ─── Fact-first retrieval seam (Phase 522) ───────────────────────────
//
// The types that let the retrieval pipeline resolve facts *before* vector
// search and merge them into the result set as a distinct origin. They are
// deliberately fact-companion-free — opaque id / metric strings and a
// generic (subject, metric, period) clause — so `ToolUp.Platform` and the
// RAG pipeline take no dependency on the fact store's typed model (GP 1).
// The concrete resolver (over the fact store, with registry-driven
// freshness) is implemented in the fact companion and wired in at compose
// time; the pipeline consumes the seam as an optional dependency, so a
// deployment with no fact store is byte-identical (GP 13).

/// Freshness of a resolved fact, projected fact-companion-free so it can
/// ride `RetrievedSource` / `ResolvedFact` without a `ToolUp.Facts` edge.
type FactFreshnessInfo =
    /// The fact is current under its metric's staleness policy.
    | FactFresh
    /// The fact is stale/superseded; `sinceIso` is the ISO-8601 instant it
    /// went stale (its transaction time plus the freshness window, or its
    /// successor's arrival).
    | FactStale of sinceIso: string

/// The optional fact-clause selectors on a `RetrievalRequest` (Phase
/// 522.A) — a generic, fact-companion-free (subject, metric, period)
/// query. The resolver maps it onto the fact store's own typed query.
/// Absent from a request ⇒ the pipeline resolves no facts and behaves
/// byte-identically (GP 11 / plan D17).
type FactClause = {
    /// Registered subject-hierarchy id.
    SubjectHierarchy: string
    /// Ordered member path from the root level down (empty = the
    /// hierarchy root, a total roll-up).
    SubjectPath: string list
    /// Registered metric id.
    Metric: string
    /// Optional valid-time window the fact's period must overlap.
    PeriodFrom: System.DateTime option
    PeriodTo: System.DateTime option
    /// Transaction-time visibility instant. `None` = the current head
    /// (latest); `Some t` reconstructs "what we knew at `t`".
    AsOf: System.DateTime option
}

/// A fact resolved by the pre-vector stage (Phase 522.B), projected
/// fact-companion-free so the RAG pipeline can carry it as a distinct
/// result origin without a `ToolUp.Facts` compile-time edge.
type ResolvedFact = {
    /// Content-addressed fact id.
    FactId: string
    /// Canonical display rendering of the value (the number quoted
    /// verbatim into the answer).
    Rendering: string
    /// Derived freshness status.
    Freshness: FactFreshnessInfo
    /// The id that superseded this fact, when it is stale/superseded.
    SupersededBy: string option
    /// Registered metric id the fact is for.
    Metric: string
}

/// Scope-filtered fact-resolution seam (Phase 522.B). Implemented over the
/// fact store in the fact companion; the RAG pipeline consumes it as an
/// optional dependency (`None` ⇒ dormant, GP 13). `Resolve` is handed the
/// caller's resolved storage scope, so tenant isolation is structural (GP
/// 4): the implementation scope-filters the store query *before* reading,
/// making a fact from one scope unreachable from another.
///
/// GP 12 audit: identity by value (`scopeId` string, value records); async
/// at the boundary; no callbacks; stateless between calls; scope is the
/// shard key with no cross-scope ordering promise; no timing primitives.
type IFactResolver =
    abstract Resolve: scopeId: string * clause: FactClause -> Async<ResolvedFact list>

// ─── Disclosure egress seam (Phase 525) ──────────────────────────────
//
// The fact-level `Disclosure` classification (carried on every fact from
// birth) is *enforced* at the enumerable egress choke points — retrieval
// into prompt context, AI tool results, narrative publication — never at
// module render seams. Like `IFactResolver` above, the seam here is
// deliberately fact-companion-free (opaque id / policy strings), so the
// choke points take no compile-time edge on the fact store's typed model
// (GP 1). The one predicate the gate applies lives beside the `Disclosure`
// type in the fact companion (`DisclosureEgress.evaluate`) — it is never
// re-implemented per surface; every choke point consults this seam.

/// The egress surface a disclosure check is being made for — the first
/// choke points (Phase 525), extended additively as later doors land
/// with their surfaces (Phase 564: exports + webhooks). Remaining doors
/// (external write-back, certificates) extend this DU the same way.
type FactEgressSurface =
    /// Fact resolution into retrieval results / prompt context. Default-
    /// deny at retrieval — the model never *sees* a denied fact ("see but
    /// don't say" is not a mode).
    | FactRetrieval
    /// A fact-reading AI tool returning facts to the model as a tool
    /// result.
    | FactToolResult
    /// Committing / publishing a narrative that references facts to a
    /// surfaceable store (KB commit, public-page publication).
    | FactNarrativePublication
    /// A rendered export leaving the deployment as a document — the
    /// Reporting render path over fact-referencing narrative content
    /// (Phase 564.B). Denied values are redacted to the policy-naming
    /// marker before rendering; the output notes withheld refs (id +
    /// policy, never the value).
    | FactExport
    /// An outbound fact-event webhook payload (Phase 564.C — contract-
    /// first: the surface + gate contract land before the emitter
    /// exists, so the emitter is born consulting the gate; see
    /// `FactWebhookEgress` for the pinned contract).
    | FactWebhook

module FactEgressSurface =
    /// Canonical string form, shared by audit events and diagnostics.
    let toString (s: FactEgressSurface) : string =
        match s with
        | FactRetrieval -> "Retrieval"
        | FactToolResult -> "ToolResult"
        | FactNarrativePublication -> "NarrativePublication"
        | FactExport -> "Export"
        | FactWebhook -> "Webhook"

/// Per-fact outcome of the disclosure predicate at one egress surface.
type FactDisclosureVerdict =
    /// The fact may cross this egress surface.
    | FactDisclosable
    /// The fact must not cross this surface. `policyRef` names *why* —
    /// `"Internal"` for the internal classification, the policy ref for a
    /// `Restricted` fact, `"unknown-fact"` for an id the scope cannot see.
    /// Refusal wording built from this names the policy, never the value.
    | FactNotDisclosable of policyRef: string

module FactDisclosureVerdict =
    /// The canonical refusal wording for a denied fact — names the policy,
    /// never the value ("the control demonstrably working"). Shared by the
    /// tool-result marker and commit-refusal diagnostics so refusal text
    /// never drifts between doors.
    let refusalText (policyRef: string) : string =
        sprintf "computed, but not disclosable under policy %s" policyRef

/// Scope-filtered disclosure gate over the fact base (Phase 525.A).
/// Implemented in the fact companion over the fact store + the one
/// `DisclosureEgress` predicate; registered in DI whenever the fact store
/// is composed, so a deployment without facts pays nothing (GP 13) and one
/// with facts cannot compose the store without the gate. `Check` is handed
/// the caller's resolved scope (GP 4): a fact id from another scope is
/// unresolvable and therefore denied, so disclosure never widens scope and
/// scope never overrides a deny. Every deny is audited (GP 6) with the
/// surface, fact id, policy ref, and `principal`.
///
/// GP 12 audit: identity by value (strings, value records); async at the
/// boundary; no callbacks; stateless between calls; scope is the shard key
/// with no cross-scope ordering promise; no timing primitives.
type IFactDisclosureGate =
    abstract Check:
        scopeId: string * principal: string * surface: FactEgressSurface * factIds: string list ->
            Async<Map<string, FactDisclosureVerdict>>

// ─── Webhook egress contract (Phase 564.C) ───────────────────────────
//
// The outbound fact-event webhook emitter is a later surface that does
// not exist yet; its gate contract lands FIRST so the emitter is born
// consulting the gate rather than retrofitted ("doors land with their
// surfaces"). THE CONTRACT — pinned by the disclosure test pack:
//
//   1. Every outbound fact-event payload MUST pass the fact ids it
//      carries through the one `IFactDisclosureGate` at the
//      `FactWebhook` surface before emission. The predicate is never
//      re-implemented emitter-side — `permittedFactIds` below is the
//      filter the emitter builds on.
//   2. **Suppression-on-deny**: a denied fact is ABSENT from the
//      outbound payload (the retrieval-door posture) — never annotated,
//      never marker-substituted. A webhook subscriber sits outside the
//      deployment's trust boundary; even a policy-naming marker would
//      leak the fact's existence.
//   3. Fail-closed: an id without an affirmative `FactDisclosable`
//      verdict (denied, unknown, or unresolvable in scope) is
//      suppressed.
//   4. Audit rides the gate: every deny is audited by the gate itself
//      (surface `"Webhook"`, fact id, policy ref, principal — GP 6);
//      the emitter adds nothing.

/// The `FactWebhook`-surface egress filter every outbound fact-event
/// emitter consults at birth (Phase 564.C). No gate composed ⇒ the
/// emitter must not emit fact ids at all or must treat the payload as
/// unclassified per its own composition contract — this helper takes
/// the gate, so "forgot the gate" is unrepresentable at the call site.
module FactWebhookEgress =

    /// Filter an outbound payload's fact ids through the disclosure
    /// gate at the `FactWebhook` surface. Suppression-on-deny: only ids
    /// with an affirmative `FactDisclosable` verdict survive; input
    /// order and multiplicity are preserved. An empty input never
    /// consults the gate.
    let permittedFactIds
        (gate: IFactDisclosureGate)
        (scopeId: string)
        (principal: string)
        (factIds: string list)
        : Async<string list> =
        async {
            match factIds with
            | [] -> return []
            | ids ->
                let! verdicts = gate.Check(scopeId, principal, FactWebhook, ids)

                return
                    ids
                    |> List.filter (fun id ->
                        match verdicts.TryFind id with
                        | Some FactDisclosable -> true
                        | Some(FactNotDisclosable _)
                        | None -> false)
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
    /// Scope the chunk was retrieved from, so the Sources panel can
    /// render an authority badge (Platform-curated vs deployment-wide
    /// vs the caller's own team) instead of relying on the prompt
    /// builder's in-prose labelling. Always `Some` at production time
    /// (every `VectorMatch` carries a scope); `None` only for
    /// conversation history persisted before this field existed —
    /// replayed pre-widening payloads deserialise safely (GP 11)
    /// rather than materialising a null scope, and the UI omits the
    /// badge rather than guessing (GP 9).
    Scope: VectorScope option
    /// Vector-store chunk id of the retrieved chunk — the stable wire
    /// join key between this source and the `[¹]` citation markers in
    /// the assistant's reply (markers are positional and survive
    /// `CitationNormaliser` rewrites only by index; the chunk id gives
    /// renderers an identity that doesn't shift when markers are
    /// normalised). Always `Some` at production time; `None` only for
    /// pre-widening persisted conversation payloads (GP 11).
    ChunkId: string option
    /// Phase 522.C — when this source is a resolved fact (`Origin =
    /// Fact`), its content-addressed id; `None` for document / note /
    /// narrative / AI-context sources. Additive + backward-compatible (GP
    /// 11): pre-existing wire payloads without the field deserialise to
    /// `None`.
    FactId: string option
    /// Canonical value rendering of the fact — the number the answer
    /// quotes verbatim. `None` for non-fact sources.
    FactRendering: string option
    /// Derived freshness of the fact. `None` for non-fact sources.
    FactFreshness: FactFreshnessInfo option
    /// When the fact is stale/superseded, the id that superseded it (the
    /// lineage's current head). `None` for a fresh fact or a non-fact
    /// source.
    FactSupersededBy: string option
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
    /// Optional metadata-equality filter applied at retrieval time. Every
    /// pair must match the chunk's `Metadata` exactly (AND-combined), and a
    /// chunk missing the key does NOT pass — a filter is a narrowing /
    /// isolation intent ("only document X", "only tag=policy"), so a chunk
    /// that cannot prove it belongs to the requested slice is excluded
    /// rather than admitted (GP 4). Note this is deliberately stricter than
    /// `OriginFilter`, which keeps chunks with no `_origin` stamp.
    ///
    /// `None` (and an empty map) = no filter, and the pipeline is then
    /// byte-identical to a filter-unaware one (GP 11). Honoured by every
    /// shipped `IRetrievalPipeline` — the default pipeline and the
    /// static-corpus pipeline are held to the same behaviour by the
    /// `MetadataFilterContract` parity pack (Phase 502).
    Filters: Map<string, string> option
    /// Prior conversation turns, oldest-first. Consumed by the Phase 506
    /// query-rewrite stage to resolve a follow-up query ("what about it?",
    /// "and the second one?") into a standalone one before anything embeds
    /// it — retrieval scores query *text*, so an unresolved anaphor embeds
    /// to nothing useful and multi-turn recall collapses even when the
    /// corpus holds the answer.
    ///
    /// `None` (and an empty list) = first turn / no history, and the stage
    /// does not run: no rewriter is called, no trace decision is recorded,
    /// and retrieval is byte-identical to a history-unaware pipeline
    /// (GP 11). The field also has no effect unless an `IQueryRewriter` is
    /// composed (GP 13) — populating it costs nothing on a deployment that
    /// has not opted in.
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
    /// Optional fact clause (Phase 522.A). When `Some`, the pipeline
    /// resolves the (subject, metric, period) query against the fact store
    /// *before* vector search and merges the exact fact hits ahead of the
    /// similarity chunks as a distinct `ChunkOrigin.Fact` origin. `None`
    /// (default) ⇒ no fact resolution, byte-identical retrieval (GP 11 /
    /// plan D17). The clause has no effect unless a fact resolver is wired
    /// into the pipeline (GP 13).
    FactClause: FactClause option
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
        FactClause = None
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