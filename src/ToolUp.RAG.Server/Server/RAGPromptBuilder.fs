module ToolUp.RAG.RAGPromptBuilder

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.IRagTelemetry
open ToolUp.AI.SystemPromptBuilder
open Newtonsoft.Json

/// Threshold below which `RAGPromptBuilder.withRetrieval` emits a
/// `KnowledgeRetrievalMiss` diagnostic via `IRetrievalTracer.Miss`.
/// Less than two surviving matches is the cutoff: a single match might
/// be the right answer, but two-or-more is the qualitative signal the
/// corpus has *something* to offer. Below that the team's KB is too
/// thin / noisy / off-topic for the queries it's getting.
let private MissThreshold = 2

// ─── Source reference deserialisation ────────────────────────────

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private tryDeserialise<'T> (json: string) =
    try
        Some(JsonConvert.DeserializeObject<'T>(json, jsonSettings))
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
/// JSON for either field.
let private toRetrievedSource (charLimit: int) (m: VectorMatch) : RetrievedSource =
    let src = trySource m

    let origin =
        m.Metadata.TryFind ChunkMetadata.OriginKey
        |> Option.map ChunkOrigin.fromMetadataValue
        |> Option.defaultValue (Other "unknown")

    let locationHint = m.Metadata.TryFind ChunkMetadata.LocationHintKey

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
    }

// ─── Retrieval builder ────────────────────────────────────────────

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
let withRetrieval
    (defaults: RetrievalDefaults)
    (telemetry: IRagTelemetry option)
    (tracer: IRetrievalTracer option)
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
                return "Knowledge-base search ran for this turn and returned no relevant matches."
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