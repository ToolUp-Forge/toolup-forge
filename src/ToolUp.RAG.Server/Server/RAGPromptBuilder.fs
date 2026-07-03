module ToolUp.RAG.RAGPromptBuilder

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.IRagTelemetry
open ToolUp.AI.SystemPromptBuilder
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

/// Threshold below which `RAGPromptBuilder.withRetrieval` emits a
/// `KnowledgeRetrievalMiss` diagnostic via `IRetrievalTracer.Miss`.
/// Less than two surviving matches is the cutoff: a single match might
/// be the right answer, but two-or-more is the qualitative signal the
/// corpus has *something* to offer. Below that the team's KB is too
/// thin / noisy / off-topic for the queries it's getting.
let private MissThreshold = 2

// ─── Tool-aware framing (Phase 14r) ──────────────────────────────

/// Phase 14r — a compact, request-independent summary of the tools
/// available to the assistant, derived once at compose time from the
/// deployment's registered tool list. Drives tool-aware retrieval
/// framing: when the deployment has live-interface tools loaded, the
/// framing (and the empty-retrieval message) teach the model that a
/// knowledge-base miss is not the only fallback — "what is on the user's
/// screen right now" is answered by calling the inspection tool, not by
/// the knowledge base. A deployment with no such tools sees the historical
/// knowledge-base-first behaviour unchanged.
type ToolFraming = {
    /// True when the deployment has at least one live-interface tool — a
    /// tool that reads or drives the browser-resident module state the
    /// user is currently looking at, so a question about live on-screen
    /// state is answerable without the knowledge base.
    HasLiveUiTools: bool
}

module ToolFraming =
    /// The zero value: no tools known / no live-interface awareness. The
    /// back-compat `withRetrieval` and any caller that doesn't thread a
    /// tool list use this, so their behaviour is byte-identical to the
    /// pre-Phase-14r knowledge-base-first framing.
    let none = { HasLiveUiTools = false }

    /// A tool counts as a "live-interface" tool when it reads or drives
    /// the user's on-screen module state: the platform `_platform.ui.*`
    /// inspection / mutation family, or any client-resident tool (whose
    /// body runs in the browser against live UI state). Server-resident
    /// analytical tools — including the `_platform.ai.*` cross-module read
    /// family — are *not* live-interface tools: they read persisted data,
    /// so the knowledge-base-first framing still applies to them.
    let private isLiveUiTool (def: AIToolDefinition) : bool =
        def.Location = ClientResident
        || def.Name.StartsWith("_platform.ui.", System.StringComparison.Ordinal)

    /// Derive the framing summary from the deployment's full tool list
    /// (the `AIToolDefinition`s aggregated on the base `ServerApp`, which
    /// `RAGCompose` reads at compose time).
    let fromTools (tools: AIToolDefinition list) : ToolFraming = {
        HasLiveUiTools = tools |> List.exists isLiveUiTool
    }

// ─── Source reference deserialisation ────────────────────────────

let private jsonOptions = FableConverters.create ()

let private tryDeserialise<'T> (json: string) =
    try
        Some(JsonSerializer.Deserialize<'T>(json, jsonOptions))
    with _ ->
        None

// ─── Citation formatting ──────────────────────────────────────────

/// Treat `SourceReference` opaquely here — deserialise only the fields we
/// care about via dynamic JSON so `RAGPromptBuilder` has no compile-time
/// dependency on `KnowledgeBase.SharedTypes`.
type private SourceFields = {|
    DocumentId: string
    DocumentName: string
    FileType: string
    Location: obj
|}

let private trySource (m: VectorMatch) =
    m.Metadata.TryFind "_source" |> Option.bind tryDeserialise<SourceFields>

/// Citation markers used when injecting retrieved chunks into the system
/// prompt. The model is asked (via the framing preamble) to append these
/// to sentences it grounds in the corresponding chunk; client renderers
/// then highlight them as click-through references to the Sources panel.
/// Falls back to bracketed integers (`[10]`, `[11]`) past the superscript
/// digit set so a freakishly large `TopK` doesn't break the convention.
let private citationMarker (i: int) =
    let supers = [| "[¹]"; "[²]"; "[³]"; "[⁴]"; "[⁵]"; "[⁶]"; "[⁷]"; "[⁸]"; "[⁹]" |]

    if i >= 0 && i < supers.Length then
        supers[i]
    else
        $"[{i + 1}]"

/// Scope-origin annotation for citation headers. `Platform` matches are
/// labelled `(from Platform KB)` so the model can distinguish
/// deployment-curated reference content (Phase 4b's Platform Knowledge
/// Base) from per-team content. Team matches get `(from your team's KB)`.
/// `Deployment` matches use a generic `(from deployment knowledge)`
/// label — this scope is reserved for any pre-Phase-4b deployment-wide
/// content seeded outside Platform KB; same universal-read semantics.
let private scopeOriginLabel (scope: VectorScope) =
    match scope with
    | Platform -> "from Platform KB"
    | Deployment -> "from deployment knowledge"
    | Team _ -> "from your team's KB"

/// Format a single `VectorMatch` as a cited context block. The `index`
/// (0-based) drives the citation marker — `[¹]` for the first match,
/// `[²]` for the second, etc. — so the model has a stable handle to
/// cite when it grounds a sentence in the chunk. The scope-origin
/// suffix in parentheses lets the model qualify its answer with the
/// authority level of the citation (deployment-curated vs team-private).
let private formatMatch (index: int) (m: VectorMatch) =
    let originLabel = scopeOriginLabel m.Scope

    let header =
        match trySource m with
        | Some src -> $"{citationMarker index} {src.DocumentName} ({originLabel})"
        | None -> $"{citationMarker index} {originLabel}"

    $"{header}\n{m.Content}"

// ─── Source-record projection ────────────────────────────────────

let private snippet (charLimit: int) (content: string) =
    if isNull content then ""
    elif content.Length <= charLimit then content
    else content.Substring(0, charLimit) + "…"

/// Project a `VectorMatch` into the wire-format `RetrievedSource` the AI
/// client renders in its Sources panel. `_source` carries provenance; the
/// `_origin` and `_locationHint` metadata flags are stamped by the chunk
/// producer so retrieval doesn't have to parse the structured `_source`
/// JSON for either field. `_originalRef` (Phase 103) carries the
/// producer-stamped `OriginalDocumentRef` — a `ToolUp.Platform` type, so
/// it deserialises here directly with no producer compile-time edge;
/// chunks without the key (notes, narratives, pre-Phase-103 ingests)
/// surface `OriginalRef = None`, never a rebuilt blob-name guess.
/// Public so contract tests can pin the projection; not otherwise part
/// of the consumer-facing surface.
let toRetrievedSource (charLimit: int) (m: VectorMatch) : RetrievedSource =
    let src = trySource m

    let origin =
        m.Metadata.TryFind ChunkMetadata.OriginKey
        |> Option.map ChunkOrigin.fromMetadataValue
        |> Option.defaultValue (Other "unknown")

    let locationHint = m.Metadata.TryFind ChunkMetadata.LocationHintKey

    let originalRef =
        m.Metadata.TryFind ChunkMetadata.OriginalRefKey
        |> Option.bind tryDeserialise<OriginalDocumentRef>

    {
        DocumentId = src |> Option.map _.DocumentId |> Option.defaultValue ""
        DocumentName =
            src
            |> Option.map _.DocumentName
            |> Option.defaultWith (fun () ->
                match m.Scope with
                | Platform -> "Platform knowledge"
                | Deployment -> "Deployment knowledge"
                | Team _ -> "Team knowledge")
        Snippet = snippet charLimit m.Content
        Score = m.Score
        Origin = origin
        LocationHint = locationHint
        OriginalRef = originalRef
        // Lineage widening (Investigate gaps 2026-06-12): scope drives
        // the Sources-panel authority badge; chunk id is the stable
        // join key between a source and its citation marker. Both are
        // always present on a live VectorMatch — `None` exists only
        // for pre-widening persisted conversation payloads.
        Scope = Some m.Scope
        ChunkId = Some m.ChunkId
    }

// ─── Retrieval builder ────────────────────────────────────────────

/// Phase 14r — the message injected when knowledge-base retrieval found
/// nothing for the turn. With no live-interface tools the wording is the
/// historical neutral one (byte-identical — a no-UI-tools deployment sees
/// no change). When the deployment *does* have live-interface tools, the
/// empty result is no longer framed as a dead end: the model is told to
/// call the interface inspection tool before concluding it cannot answer,
/// so a "what filters do I have applied?" question resolves via inspection
/// rather than an "I don't have that in the knowledge base" refusal.
let private emptyRetrievalMessage (toolFraming: ToolFraming) : string =
    if toolFraming.HasLiveUiTools then
        "Knowledge-base search ran for this turn and returned no relevant matches. \
         If the user's question is about the live state of the interface they are viewing — the filters currently applied, the current selection, or the values on screen — call the interface inspection tool before concluding you cannot answer, rather than treating this empty result as the final word."
    else
        "Knowledge-base search ran for this turn and returned no relevant matches."

/// Build a `SystemPromptBuilder` that injects retrieved context from the
/// team's knowledge base into the system prompt.
///
/// - Skips retrieval when `ctx.CurrentMessage = None` (graceful no-op for
///   non-message contexts such as system-level prompt construction at startup).
/// - Limits query to the caller's authorised team scope; falls back to
///   Platform + Deployment when the caller has no team.
/// - Emits an explicit miss signal when no chunks match, so the model can
///   distinguish "search ran and found nothing" from "no KB exists" — paired
///   with `ragAwarePreamble` in `RAGCompose` to prevent silent hallucination.
/// - Populates `ctx.RetrievedSources` with one `RetrievedSource` per match so
///   `AIAssistantHandler` can attach them to the outbound `ConversationMessage`
///   for the client's Sources panel — without coupling the prompt-builder
///   return type to the wire-format shape.
/// - Applies `RetrievalDefaults` knobs: `TopK`, `Merge`, `MinScore` (drops
///   matches scoring at or below the threshold), and `SnippetCharLimit`
///   (controls Sources panel preview length).
/// - Records the post-filter outcome via `IRagTelemetry.RecordRetrieval`
///   when one is supplied so `/health/rag` reflects what the user actually
///   saw (raw pipeline results pre-filter would over-count hits when
///   `MinScore` is gating a noisy corpus).
/// - Emits a `KnowledgeRetrievalMiss` diagnostic via `IRetrievalTracer.Miss`
///   when fewer than `MissThreshold` matches survive the score filter, so
///   admins can spot teams whose KB is too thin for their queries without
///   sampling individual conversations.
///
/// Phase 14r — `toolFraming` makes the empty-retrieval message tool-aware:
/// when the deployment has live-interface tools loaded, a knowledge-base
/// miss redirects the model to the interface inspection tool instead of
/// framing the miss as a dead end (see `emptyRetrievalMessage`). Pass
/// `ToolFraming.none` (as the back-compat `withRetrieval` wrapper does) for
/// the historical knowledge-base-first empty message.
let withRetrievalToolAware
    (defaults: RetrievalDefaults)
    (telemetry: IRagTelemetry option)
    (tracer: IRetrievalTracer option)
    (toolFraming: ToolFraming)
    (pipeline: IRetrievalPipeline)
    : SystemPromptBuilder =
    fun ctx -> async {
        match ctx.CurrentMessage with
        | None -> return ""
        | Some query ->
            let scopes =
                match ctx.Access.TeamId with
                | Some teamId -> [ Platform; Deployment; Team teamId ]
                | None -> [ Platform; Deployment ]

            let request = {
                RetrievalRequest.create query scopes defaults.TopK defaults.Merge with
                    OriginFilter = defaults.OriginFilter
                    ActiveModule = ctx.ActiveModule
            }

            let! rawMatches = pipeline.Retrieve request ctx.Access

            // Apply MinScore filter: a deployment that wants to refuse weak
            // matches drops them here so neither the prompt nor the Sources
            // panel surfaces them — keeps the model and the user aligned.
            let matches =
                match defaults.MinScore with
                | None -> rawMatches
                | Some threshold -> rawMatches |> List.filter (fun m -> m.Score > threshold)

            let rawTop =
                match rawMatches with
                | top :: _ -> top.Score
                | [] -> 0.0

            let threshold = defaults.MinScore |> Option.defaultValue 0.0

            // Telemetry classifies hit / low-score-miss / empty against the
            // *raw* top score — passing the raw top through preserves the
            // distinction between "pipeline returned nothing" (Empty) and
            // "pipeline returned weak matches the threshold dropped"
            // (LowScoreMiss). The post-filter `matches.Length` then tells
            // the consumer how many actually reached the model.
            match telemetry with
            | None -> ()
            | Some t -> t.RecordRetrieval(rawTop, matches.Length, threshold)

            // Miss diagnostic: fewer than `MissThreshold` matches surviving
            // the gate is the signal the team's KB is too thin / off-topic
            // for the queries it's getting. Emitted via the existing
            // tracer so admin UIs can scan one event source.
            if matches.Length < MissThreshold then
                match tracer with
                | None -> ()
                | Some t ->
                    let miss: RetrievalMiss = {
                        QueryHash = ToolUp.RAG.RetrievalTracers.hashQuery query
                        QueryLength = if isNull query then 0 else query.Length
                        Scopes = scopes
                        MatchesAboveMinScore = matches.Length
                        MinScoreThreshold = threshold
                        TopScore = rawTop
                    }

                    do! t.Miss miss ctx.Access

            ctx.RetrievedSources.Value <- matches |> List.map (toRetrievedSource defaults.SnippetCharLimit)

            if matches.IsEmpty then
                return emptyRetrievalMessage toolFraming
            else
                let formattedChunks = matches |> List.mapi formatMatch |> String.concat "\n\n"

                let hasPlatform =
                    matches
                    |> List.exists (fun m ->
                        match m.Scope with
                        | Platform -> true
                        | _ -> false)

                let hasTeam =
                    matches
                    |> List.exists (fun m ->
                        match m.Scope with
                        | Team _ -> true
                        | _ -> false)

                let preamble =
                    match hasPlatform, hasTeam with
                    | true, true -> "Relevant context from the Platform KB and your team's knowledge base"
                    | true, false -> "Relevant context from the Platform KB"
                    | false, _ -> "Relevant context from your team's knowledge base"

                return $"{preamble}:\n\n{formattedChunks}"
    }

/// Back-compat retrieval builder (pre-Phase-14r shape). Delegates to
/// `withRetrievalToolAware` with `ToolFraming.none`, so the empty-retrieval
/// message and every other behaviour are byte-identical to the historical
/// knowledge-base-first path. Callers that can supply the deployment's tool
/// list (e.g. `RAGCompose`) call `withRetrievalToolAware` directly.
let withRetrieval
    (defaults: RetrievalDefaults)
    (telemetry: IRagTelemetry option)
    (tracer: IRetrievalTracer option)
    (pipeline: IRetrievalPipeline)
    : SystemPromptBuilder =
    // Eta-expanded over `ctx` (rather than a point-free delegation) so the
    // compiled arity matches the pre-Phase-14r `withRetrieval` exactly — a
    // point-free `= withRetrievalToolAware …` would reflect as a 4-arg method
    // returning a function, tripping the public-API baseline gate on an
    // otherwise source-compatible change. Keeps IL-level back-compat.
    fun ctx -> withRetrievalToolAware defaults telemetry tracer ToolFraming.none pipeline ctx