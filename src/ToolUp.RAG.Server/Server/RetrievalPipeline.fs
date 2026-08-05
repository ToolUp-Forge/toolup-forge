module ToolUp.RAG.RetrievalPipeline

open System.Collections.Generic
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IReranker
open ToolUp.Platform.IQueryRewriter
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.IRagTelemetry

// ─── Query-size guard (Phase 14y) ─────────────────────────────────

/// Reserved event-type literal for the `KnowledgeQueryRejected` audit
/// emitted when a retrieval query exceeds the pipeline's configured
/// `MaxQueryChars` cap. Wire-format constant — ops dashboards / admin
/// UIs match on this string alongside the other RAG `Document*` audit
/// events (`DocumentRejected`, `DocumentVectorisationSkipped`, …).
[<Literal>]
let KnowledgeQueryRejectedEventType = "KnowledgeQueryRejected"

/// Raised by `RetrievalPipeline.Retrieve` when the query length exceeds
/// the pipeline's configured `MaxQueryChars` cap. A hard refusal: a query
/// this long is almost always a programming bug (an entire document pasted
/// into the query slot) — embedding it wastes provider spend and can trip
/// the provider's own token limit with an opaque error, so the pipeline
/// refuses at the contract boundary before any embedding call. Carries the
/// offending length and the cap (both as inspectable properties) so the
/// message names `MaxQueryChars` explicitly.
type KnowledgeQueryTooLargeException(queryChars: int, maxQueryChars: int) =
    inherit
        exn(
            sprintf
                "Retrieval query of %d characters exceeds the configured MaxQueryChars cap of %d. A query this long is almost always a programming bug (an entire document pasted into the query slot) — split it, or raise the cap via RAGServerApp.withMaxQueryChars."
                queryChars
                maxQueryChars
        )

    /// Character length of the query that was refused.
    member _.QueryChars = queryChars
    /// The configured `MaxQueryChars` cap the query exceeded.
    member _.MaxQueryChars = maxQueryChars

/// STJ options carrying the full F# converter set, for the
/// `KnowledgeQueryRejected` audit payload. Constructed once at module
/// level (the payload is a plain anonymous record, but Option / list
/// fields still need the converters).
let private queryRejectedJsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

// ─── Access validation ────────────────────────────────────────────

/// Returns the subset of requested scopes the caller is permitted to read.
///
/// Rules:
/// - `Platform` is readable when `ServerConfig.PlatformKnowledgeBase = Enabled`
///   AND the caller is authenticated. When `Disabled`, this scope is filtered
///   out regardless of caller — Platform chunks stay on disk but are invisible
///   to retrieval (Phase 4b commit 5). The toggle is the read-side gate;
///   write-side is gated by `canModifyPlatformConfig` and structurally
///   restricted to `IPlatformKnowledgeApi`.
/// - `Deployment` is readable by all authenticated users.
/// - `Team teamId` is readable only when `ctx.TeamId = Some teamId`.
/// - `User userId` is readable only by that user — `ctx.UserId = userId`.
///   This is the structural enforcement of per-user KB isolation (GAP-1
///   fix): one non-team caller can never read another's uploaded chunks,
///   mirroring the `user-{id}` blob-container boundary at the vector layer.
/// - Anonymous users (userId = "anonymous") may read Platform / Deployment
///   only; team scopes are filtered out. A session with a stable id reads
///   its own `User` scope (its `ctx.UserId`).
let private authorisedScopes
    (platformKnowledgeBase: PlatformKnowledgeBaseMode)
    (ctx: AccessContext)
    (scopes: VectorScope list)
    =
    scopes
    |> List.filter (fun scope ->
        match scope with
        | Platform ->
            match platformKnowledgeBase with
            | EnabledPlatformKnowledgeBase -> true
            | NoPlatformKnowledgeBase -> false
        | Deployment -> true
        | Team teamId ->
            match ctx.TeamId with
            | Some id -> id = teamId
            | None -> false
        | User userId -> ctx.UserId = userId)

// ─── Reciprocal Rank Fusion ───────────────────────────────────────

/// RRF constant. 60 is the value originally proposed by Cormack et al. and
/// the de-facto default across the Lucene / Elasticsearch / Vespa ecosystem.
/// Larger `k` smooths the contribution of top-ranked documents (the head
/// of each list contributes less); smaller `k` weights the very top of
/// each list more heavily. Held configurable on the off-chance a deployment
/// wants to tune, but exposed via a stage option rather than `RetrievalRequest`
/// — RRF tuning is a backend concern, not a per-call parameter.
let private rrfK = 60.0

/// Reciprocal Rank Fusion. Fuse two ranked lists by summing each candidate's
/// `1 / (k + rank)` contribution from each list it appears in, where `rank`
/// is 1-based. Score scales between dense (cosine) and sparse (BM25) are
/// incomparable, so RRF treats them rank-wise — robust to either retriever
/// dominating the absolute score range.
///
/// Identity is `(scope, chunkId)`: the same chunk in different scopes is
/// kept separate (they cannot be the same document by construction —
/// per-scope namespaces in `IVectorStore` / `ISparseIndex`).
let private fuseRRF (dense: VectorMatch list) (sparse: VectorMatch list) : VectorMatch list =
    let scores = Dictionary<VectorScope * string, float * VectorMatch>()

    let contribute (results: VectorMatch list) =
        results
        |> List.iteri (fun i m ->
            let key = (m.Scope, m.ChunkId)
            let contribution = 1.0 / (rrfK + float (i + 1))

            match scores.TryGetValue key with
            | true, (existing, prev) ->
                let preferred = if prev.Score >= m.Score then prev else m
                scores[key] <- (existing + contribution, preferred)
            | false, _ -> scores[key] <- (contribution, m))

    contribute dense
    contribute sparse

    // Total-order sort: `scores` is a Dictionary enumerated in unspecified
    // order, and equal RRF scores are common (every chunk that appears at
    // the same rank in both lists ties). A score-only `sortByDescending`
    // is stable but inherits the non-deterministic enumeration order for
    // ties — fatal for the deterministic eval gate. Tie-break on the unique
    // `(Scope, ChunkId)` so the result is a pure function of the data.
    [
        for KeyValue(_, (rrf, match')) in scores do
            { match' with Score = rrf }
    ]
    |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)

// ─── Tokenisation for MMR similarity ──────────────────────────────

let private tokenPattern = Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)

let private tokeniseSet (text: string) : Set<string> =
    if System.String.IsNullOrEmpty text then
        Set.empty
    else
        [
            for m in tokenPattern.Matches(text) do
                m.Value.ToLowerInvariant()
        ]
        |> Set.ofList

let private jaccard (a: Set<string>) (b: Set<string>) =
    if a.IsEmpty && b.IsEmpty then
        0.0
    else
        let inter = Set.intersect a b |> Set.count |> float
        let union = Set.union a b |> Set.count |> float
        if union = 0.0 then 0.0 else inter / union

// ─── Maximal Marginal Relevance ───────────────────────────────────

/// MMR rerank: greedily pick the next candidate that maximises a balance
/// between its retrieval score and its dissimilarity from the already-
/// picked set. Token-level Jaccard is the similarity proxy — exact for
/// near-duplicates, cheap, and produces no extra embedder calls.
///
/// `lambda` ∈ [0, 1]. λ = 1 is pure relevance (no MMR effect); λ = 0 is
/// pure diversity. The default `0.5` balances the two — typical of the
/// literature for first-pass diversification.
///
/// Score scales between MMR-out and the input may differ; callers that
/// truncate by absolute score should not chain MMR with another scoring
/// stage. The pipeline applies MMR after rerank so the resulting order is
/// the diversified rerank.
let private applyMmr (lambda: float) (candidates: VectorMatch list) : VectorMatch list =
    if candidates.Length <= 1 then
        candidates
    else
        // Cache token sets — we'll touch each candidate O(N) times during
        // the picking loop.
        let tokens =
            candidates |> List.map (fun c -> (c, tokeniseSet c.Content)) |> List.toArray

        let remaining = ResizeArray(tokens)
        let picked = ResizeArray<VectorMatch * Set<string>>()
        let result = ResizeArray<VectorMatch>()

        // Normalise relevance scores to [0, 1] within the pool so the
        // λ * relevance term and the (1 - λ) * diversity term are on the
        // same scale. Scores from RRF or rerank are otherwise on incomparable
        // ranges.
        let rawScores = candidates |> List.map _.Score
        let minS = List.min rawScores
        let maxS = List.max rawScores
        let range = maxS - minS

        let normalised (s: float) =
            if range = 0.0 then 1.0 else (s - minS) / range

        while remaining.Count > 0 do
            let mutable bestIdx = 0
            let mutable bestScore = System.Double.NegativeInfinity

            for i in 0 .. remaining.Count - 1 do
                let (cand, candTokens) = remaining[i]
                let relevance = normalised cand.Score

                let maxSim =
                    if picked.Count = 0 then
                        0.0
                    else
                        picked
                        |> Seq.map (fun (_, pickedTokens) -> jaccard candTokens pickedTokens)
                        |> Seq.max

                let mmrScore = lambda * relevance - (1.0 - lambda) * maxSim

                if mmrScore > bestScore then
                    bestScore <- mmrScore
                    bestIdx <- i

            let (chosen, chosenTokens) = remaining[bestIdx]
            picked.Add((chosen, chosenTokens))
            result.Add(chosen)
            remaining.RemoveAt(bestIdx)

        result |> List.ofSeq

// ─── Adaptive top-K ───────────────────────────────────────────────

/// Adaptive truncation. When the relevance score gap between successive
/// candidates collapses below `ScoreFloor`, that's a signal the long tail
/// is no longer informative — truncate there. The result is bounded to
/// `[MinK, MaxK]` so a query with no obvious score cliff still returns a
/// sensible result. Without an `AdaptiveKHint`, the pipeline falls through
/// to the request's fixed `TopK`.
///
/// Operates on a list already sorted by descending score. Returns the
/// truncated list.
let private adaptiveTruncate (hint: AdaptiveKHint) (results: VectorMatch list) : VectorMatch list =
    if results.IsEmpty then
        results
    else
        // Walk down the list looking for the first score cliff. The cliff
        // index is the position AFTER which we cut.
        let arr = results |> List.toArray

        let cliff =
            let mutable found = -1
            let mutable i = 0

            while i < arr.Length - 1 && found = -1 do
                let gap = arr[i].Score - arr[i + 1].Score

                if gap >= hint.ScoreFloor && i + 1 >= hint.MinK then
                    found <- i + 1

                i <- i + 1

            // No cliff found within MaxK → take MaxK. Cliff before MinK is
            // ignored (handled inline above).
            if found = -1 then
                min arr.Length hint.MaxK
            else
                min found hint.MaxK

        let take = max hint.MinK cliff |> min arr.Length
        arr |> Array.take take |> Array.toList

// ─── Merge strategies ─────────────────────────────────────────────

let private applyMerge (strategy: MergeStrategy) (topK: int) (results: VectorMatch list) =
    match strategy with
    | Interleaved ->
        results
        |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
        |> List.truncate topK
    | Separate ->
        // For Separate, return all results grouped but still respect topK total.
        results |> List.truncate topK

// ─── Pipeline options ─────────────────────────────────────────────

/// Backend-tuning knobs for `RetrievalPipeline`. Exposed at construction
/// time, not per-request, because they affect the wiring shape rather than
/// the query intent — a deployment that wants per-call control wraps the
/// pipeline.
type RetrievalPipelineOptions = {
    /// Optional cross-encoder reranker. Applied after RRF fusion, before
    /// MMR / adaptive-K / topK truncation. `None` skips rerank. The
    /// candidate pool is inflated when a reranker is present so the
    /// reranker sees a meaningful working set.
    Reranker: IReranker option
    /// Enable MMR diversity reranking. Off by default — MMR helps for
    /// duplicate-heavy corpora (long documents chunked many times) and
    /// hurts for fact-extraction queries where the user expects the most
    /// relevant chunk on top.
    EnableMmr: bool
    /// MMR `λ` parameter ∈ [0, 1]. Higher values favour relevance; lower
    /// values favour diversity. 0.5 is the literature default and generally
    /// balanced. Ignored when `EnableMmr = false`.
    MmrLambda: float
    /// Score boost applied to chunks whose `_originModule` metadata
    /// matches the request's `ActiveModule`. Defaults to `+0.05` — small
    /// enough that strongly-relevant content from another module still
    /// outranks a weak module-match, large enough to break ties when
    /// multiple chunks score similarly. Set to `0.0` to disable.
    ActiveModuleBoost: float
    /// Score boost applied to chunks marked with `_isSummary = "true"`
    /// (WS4.1). Defaults to `+0.10` — larger than `ActiveModuleBoost`
    /// because summary chunks are *intentionally* the document-level
    /// entry point: "what is this document about?" should land on the
    /// summary first. Set to `0.0` to disable.
    SummaryBoost: float
    /// Score boost applied to a chunk whose `_factRefs` metadata (Phase
    /// 521.D) cites a fact the pre-vector stage resolved for this request
    /// (Phase 522.E) — the fact→narrative join. A fact's own narrative
    /// context should outrank generic similarity, so the default `+0.15`
    /// is larger than `SummaryBoost`. Set to `0.0` to disable, or when no
    /// fact clause / resolver is present it simply never fires.
    FactNarrativeJoinBoost: float
    /// Wall-clock bound, in milliseconds, on a single `IQueryRewriter`
    /// call (Phase 506). The rewrite sits in front of retrieval, which
    /// sits in front of the answering call, so a wedged rewriter would
    /// stall the whole turn; on overrun the pipeline abandons the rewrite
    /// and searches the raw query, recording `Failed` on the trace. Bounds
    /// the seam generically — an implementation may of course impose its
    /// own tighter budget as well. Ignored when no rewriter is wired.
    QueryRewriteTimeoutMs: int
}

module RetrievalPipelineOptions =
    let defaults: RetrievalPipelineOptions = {
        Reranker = None
        EnableMmr = false
        MmrLambda = 0.5
        ActiveModuleBoost = 0.05
        SummaryBoost = 0.10
        FactNarrativeJoinBoost = 0.15
        QueryRewriteTimeoutMs = 2_000
    }

// ─── Pipeline implementation ──────────────────────────────────────

/// Default candidate-pool inflation when hybrid retrieval or rerank is
/// active. We ask each retriever for `max(topK × 4, 32)` so RRF and any
/// downstream cross-encoder have a useful working set — a top-1 query
/// with a single dense and single sparse hit produces no signal for either.
let private inflateForHybridOrRerank (topK: int) = max (topK * 4) 32

// ─── Metadata-equality filter (Phase 502) ─────────────────────────

/// Normalise `RetrievalRequest.Filters` to "is there an actual narrowing
/// intent here?". `None` and `Some (empty map)` both mean "no filter" —
/// an empty map constrains nothing, so treating it as a live filter would
/// inflate the candidate pool and add a trace stage for a no-op (GP 11:
/// a filter-less request stays byte-identical).
let private activeFilters (filters: Map<string, string> option) : Map<string, string> option =
    filters |> Option.filter (Map.isEmpty >> not)

/// Does a candidate match satisfy every requested metadata pair?
///
/// Strict equality on every key: a chunk missing the key does NOT pass.
/// This is deliberately the opposite of `OriginFilter`'s absent-key
/// leniency, and it is the semantic the static-corpus pipeline has always
/// applied (`StaticCorpusRetrievalPipeline`, pinned by its contract pack).
/// A filter is a narrowing / isolation intent (GP 4) — "only document X",
/// "only tag=policy" — so a chunk that cannot prove it belongs to the
/// requested slice must not reach the model. Leniency here would silently
/// re-admit exactly the content the caller asked to exclude, which is the
/// defect this phase exists to close.
let private matchesMetadataFilters (filters: Map<string, string>) (m: VectorMatch) : bool =
    filters |> Map.forall (fun key value -> m.Metadata.TryFind key = Some value)

/// Candidate-pool inflation for a metadata-filtered query. The filter is
/// applied *after* the store returns its top-N, so without inflation a
/// `TopK = 5` query against a store dominated by out-of-filter chunks
/// would return far fewer than 5 in-filter results even when plenty
/// exist — filtering would cost recall inside the very slice the caller
/// scoped to. `IVectorStore` carries no filter-pushdown contract, so the
/// pipeline compensates by over-fetching. Same shape (and the same
/// bounded, best-effort promise) as the hybrid/rerank inflation above.
let private inflateForMetadataFilter (topK: int) = max (topK * 8) 64

// ─── Fact-first stage (Phase 522) ─────────────────────────────────

/// Score stamped on a resolved-fact match. Facts are exact hits, not
/// similarity matches — they are prepended ahead of the similarity chunks
/// after every ranking stage, so the value only needs to read as
/// "maximally relevant" for any downstream consumer that inspects it (the
/// Sources-panel badge). It never competes in rerank / MMR / topK.
let private factMatchScore = 1.0

/// Project a resolved fact into a `VectorMatch` carrying its id /
/// rendering / freshness / supersession pointer in metadata under the
/// Phase 522.C `ChunkMetadata.Fact*` keys, stamped `_origin = "Fact"`.
/// `RAGPromptBuilder.toRetrievedSource` reads these back onto the fact
/// fields of `RetrievedSource`.
let private factToMatch (scope: VectorScope) (rf: ResolvedFact) : VectorMatch =
    let freshnessMeta =
        match rf.Freshness with
        | FactFresh -> "Fresh"
        | FactStale sinceIso -> sprintf "Stale:%s" sinceIso

    let metadata =
        [
            ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue Fact
            ChunkMetadata.FactIdKey, rf.FactId
            ChunkMetadata.FactRenderingKey, rf.Rendering
            ChunkMetadata.FactFreshnessKey, freshnessMeta
            ChunkMetadata.FactMetricKey, rf.Metric
        ]
        @ (match rf.SupersededBy with
           | Some s -> [ ChunkMetadata.FactSupersededByKey, s ]
           | None -> [])
        |> Map.ofList

    {
        // A synthetic, collision-free chunk id: facts are content-addressed
        // and never share an id namespace with vector chunks.
        ChunkId = "fact:" + rf.FactId
        Content = rf.Rendering
        Score = factMatchScore
        Scope = scope
        Metadata = metadata
    }

/// The scope a resolved fact rides on — the narrowest team/user scope the
/// request is asking for (facts are team-scoped like KB content), else
/// `Deployment` as a coherent default for the Sources-panel authority
/// badge. Purely cosmetic — fact tenant isolation is enforced by the
/// resolver's scope-filtered store read, not by this label.
let private factScopeFor (scopes: VectorScope list) : VectorScope =
    scopes
    |> List.tryFind (fun s ->
        match s with
        | Team _
        | User _ -> true
        | _ -> false)
    |> Option.defaultValue Deployment

type RetrievalPipeline
    (
        store: IVectorStore,
        embedder: IEmbeddingProvider,
        ?sparseIndex: ISparseIndex,
        ?options: RetrievalPipelineOptions,
        ?tracer: IRetrievalTracer,
        ?platformKnowledgeBase: PlatformKnowledgeBaseMode,
        ?platformKnowledgeBaseSnapshot: unit -> PlatformKnowledgeBaseMode,
        // Phase 122 — when supplied, each `Retrieve` reports its per-stage
        // timing breakdown via `RecordRetrievalStages` so `/health/rag`
        // can expose per-stage P50/P95. `None` costs nothing (GP 13).
        ?telemetry: IRagTelemetry,
        // Phase 14y — hard cap on query length. A query longer than this is
        // refused at the top of `Retrieve` (before any embedding call) with a
        // `KnowledgeQueryTooLargeException`. `None` disables the guard.
        ?maxQueryChars: int,
        // Phase 14y — audit sink for the `KnowledgeQueryRejected` event a
        // refusal emits. `None` ⇒ the refusal still raises, just without the
        // audit row (GP 13 — zero cost when unwired, e.g. eval / benchmark).
        ?eventStore: IEventStore,
        // Phase 522 — optional fact resolver. When supplied AND the request
        // carries a `FactClause`, the pipeline resolves the (subject,
        // metric, period) query against the fact store BEFORE vector search
        // and merges the exact fact hits ahead of the similarity chunks as
        // a distinct `ChunkOrigin.Fact` origin. `None` (or a clause-less
        // request) ⇒ no fact stage, byte-identical retrieval (GP 11 / GP 13).
        ?factResolver: IFactResolver,
        // Phase 525.B — the retrieval egress door. When supplied, every
        // resolved fact is checked against the disclosure gate at the
        // `FactRetrieval` surface BEFORE merge: a denied fact is simply
        // absent from the results and the prompt block — never annotated
        // ("see but don't say" is not a mode). Wire this whenever a
        // resolver is wired: the fact companion's compose registers the
        // gate in DI alongside the store, so the pairing is structural.
        // `None` ⇒ pass-through (GP 11; a resolver-less or fact-less
        // deployment is byte-identical either way).
        ?disclosureGate: IFactDisclosureGate,
        // Phase 506 — conversation-aware query rewrite. When supplied AND
        // the request carries non-empty `History`, a follow-up query
        // ("what about it?", "and the second one?") is resolved against the
        // recent turns before anything embeds it. `None` (or a
        // history-less request) ⇒ the stage never runs and the pipeline is
        // byte-for-byte its pre-506 self: same pool, same stage list, same
        // results (GP 11 / GP 13). The call is bounded by
        // `RetrievalPipelineOptions.QueryRewriteTimeoutMs` and degrades to
        // the raw query on any failure — retrieval never fails because a
        // rewrite did.
        ?queryRewriter: IQueryRewriter
    ) =

    let sparse = sparseIndex
    let opts = defaultArg options RetrievalPipelineOptions.defaults
    // Phase 4b — read the toggle via a thunk so runtime mutation
    // (Phase 4b deferred follow-up — `IPlatformRuntimeConfigStore`)
    // takes effect immediately on the next request without rebuilding
    // the pipeline. When supplied, the snapshot thunk wins; otherwise
    // fall back to the static `platformKnowledgeBase` constructor
    // parameter (test-harness compat) or `NoPlatformKnowledgeBase`
    // (safe default matching `ServerConfig.PlatformKnowledgeBase`).
    let staticMode = defaultArg platformKnowledgeBase NoPlatformKnowledgeBase

    let readPlatformKbMode () =
        match platformKnowledgeBaseSnapshot with
        | Some snap -> snap ()
        | None -> staticMode

    let needsHybridPool = sparse.IsSome || opts.Reranker.IsSome

    // ─── Phase 14z — scope-keyed dense retrieval ──────────────────
    //
    // A TF-IDF-class embedder whose state is keyed per `VectorScope`
    // produces a DIFFERENT vector space per scope: dimension `i` denotes
    // `vocab[i]`, and a per-scope vocabulary makes `vocab[i]` a different
    // term in each scope. One query vector searched across every
    // authorised scope is therefore comparing coordinates that do not
    // denote the same thing — which is exactly what Phase 4b's acceptance
    // criterion ("a team-KB document and a Platform-KB document about the
    // same topic both surface, ranked") rests on.
    //
    // **Option 1, the operator's resolution.** Embed the query once per
    // authorised scope, search each scope with its own vector, and merge
    // the per-scope results before the filter / rerank / MMR / topK
    // stages — which then operate on the merged pool exactly as they
    // operated on the multi-scope pool before.
    //
    // The N embeds are affordable only because the probe below fails on
    // every stateless provider: OpenAI / Cohere / Anthropic embedders are
    // not scope-keyed and cannot be, so they take the single-vector
    // branch, byte-for-byte the pre-14z path (GP 11), and the per-scope
    // cost lands on the in-process dev embedder alone.
    let scopedEmbedderFactory = ScopedEmbedding.tryFactory embedder

    /// Dense candidate retrieval. Single-vector when the embedder is not
    /// scope-keyed; one embed + one search per authorised scope, merged,
    /// when it is.
    ///
    /// **Merge order, and the caveat it carries.** Per-scope results are
    /// merged by descending score with the pipeline's standard
    /// `(Scope, ChunkId)` tie-break, so the merge is a pure function of
    /// the data. Scores from two scopes are NOT strictly comparable —
    /// each is a cosine in its own scope's space, and a small scope's
    /// IDF weights differ from a large one's — so cross-scope ordering is
    /// approximate. That is accepted deliberately rather than papered
    /// over with a rank-fusion framework: the only provider that can take
    /// this branch is the documented dev-only embedder, and a
    /// deployment that needs defensible cross-tenant ranking wants a
    /// stateless embedder, which takes the other branch and has no
    /// incomparability to fuse. (RRF already fuses dense against sparse
    /// downstream, and it is the right tool for genuinely incomparable
    /// *retrievers*; applying it here would flatten the score signal the
    /// boosts and MMR stages read.)
    let denseSearch (query: string) (permitted: VectorScope list) (pool: int) : Async<VectorMatch list> =
        match scopedEmbedderFactory with
        | None -> async {
            let! queryVector = embedder.GenerateEmbedding query
            return! store.Search permitted queryVector pool
          }
        | Some factory -> async {
            let! perScope =
                permitted
                |> List.map (fun scope -> async {
                    let scopedEmbedder = factory.For scope
                    let! queryVector = scopedEmbedder.GenerateEmbedding query
                    return! store.Search [ scope ] queryVector pool
                })
                |> Async.Parallel

            return
                perScope
                |> Array.toList
                |> List.concat
                |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                |> List.truncate pool
          }

    // Phase 14y — emit the `KnowledgeQueryRejected` audit for an over-length
    // query. Privacy contract matches `RetrievalTrace`: the plaintext query
    // is NEVER persisted — only its SHA256 hash + character count. Best-
    // effort (try/with): a failed audit write must never mask the primary
    // refusal exception. `None` event store ⇒ no-op.
    let emitQueryRejected
        (query: string)
        (queryChars: int)
        (limit: int)
        (scopes: VectorScope list)
        (teamId: string option)
        : Async<unit> =
        async {
            match eventStore with
            | None -> ()
            | Some es ->
                try
                    let payload = {|
                        QueryHash = ToolUp.RAG.RetrievalTracers.hashQuery query
                        QueryChars = queryChars
                        MaxQueryChars = limit
                        Scopes = scopes
                    |}

                    let json =
                        System.Text.Json.JsonSerializer.Serialize(payload, queryRejectedJsonOptions)

                    let scopeId = teamId |> Option.defaultValue "_platform"
                    do! es.Write(Events.create scopeId "ToolUp.RAG" KnowledgeQueryRejectedEventType json)
                with _ ->
                    ()
        }

    interface IRetrievalPipeline with

        member _.Retrieve request ctx = async {
            // Phase 14y — query-size guard. Refuse an over-length query at the
            // contract boundary, before any embedding call: it is almost always
            // a programming bug (an entire document pasted into the query slot),
            // and embedding it wastes provider spend / can trip the provider's
            // own token cap with an opaque error. Emit a `KnowledgeQueryRejected`
            // audit (hash + length only) then raise a structured exception
            // naming `MaxQueryChars`.
            let queryChars = if isNull request.Query then 0 else request.Query.Length

            match maxQueryChars with
            | Some limit when queryChars > limit ->
                do! emitQueryRejected request.Query queryChars limit request.Scopes ctx.TeamId
                raise (KnowledgeQueryTooLargeException(queryChars, limit))
            | _ -> ()

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let stages = ResizeArray<string>()
            // Per-stage `(name, elapsedMs)` pairs (Phase 122). Appended
            // sequentially — the concurrent dense/sparse branches return
            // their elapsed time and the join appends both, so no two
            // threads touch this list.
            let timings = ResizeArray<string * float>()
            stages.Add "AuthoriseScopes"

            let permitted = authorisedScopes (readPlatformKbMode ()) ctx request.Scopes

            // Phase 522.B — pre-vector fact-resolution stage. When a fact
            // resolver is wired AND the request carries a fact clause,
            // resolve the (subject, metric, period) query against the fact
            // store FIRST. The resolver is scope-filtered (GP 4): it is
            // handed the caller's own fact scope and reads only within it,
            // so a fact from another tenant is structurally unreachable.
            // Resolution is independent of the vector-scope authorisation
            // below — a fact clause is answerable even for a caller with no
            // readable KB scope. Clause-less / resolver-less ⇒ no facts,
            // byte-identical retrieval (GP 11 / GP 13).
            let! factMatches =
                match factResolver, request.FactClause with
                | Some resolver, Some clause -> async {
                    stages.Add "FactResolve"
                    let factScopeId = ctx.TeamId |> Option.defaultValue ctx.UserId
                    let! resolved = resolver.Resolve(factScopeId, clause)

                    // Phase 525.B — disclosure egress filter, applied to the
                    // resolved facts BEFORE merge. Default-deny at retrieval:
                    // a fact the gate does not affirmatively disclose (denied,
                    // or missing from the verdict map) never enters the
                    // result set, the `RetrievedSource`s, or the prompt block
                    // — absent, not annotated. The gate is handed the same
                    // caller-derived fact scope as the resolver (GP 4), so
                    // scope never overrides a deny and disclosure never
                    // widens scope. No gate wired ⇒ pass-through (GP 11).
                    let! disclosed =
                        match disclosureGate, resolved with
                        | Some gate, _ :: _ -> async {
                            let ids = resolved |> List.map _.FactId
                            let! verdicts = gate.Check(factScopeId, ctx.UserId, FactRetrieval, ids)

                            let permitted =
                                resolved
                                |> List.filter (fun rf ->
                                    match verdicts.TryFind rf.FactId with
                                    | Some FactDisclosable -> true
                                    | Some(FactNotDisclosable _)
                                    | None -> false)

                            if permitted.Length < resolved.Length then
                                stages.Add "DisclosureFilter"

                            return permitted
                          }
                        | _ -> async.Return resolved

                    let scope = factScopeFor request.Scopes
                    return disclosed |> List.map (factToMatch scope)
                  }
                | _ -> async.Return []

            // The ids of the facts resolved this turn — drives the
            // fact→narrative join boost (Phase 522.E) below.
            let resolvedFactIds =
                factMatches
                |> List.choose (fun m -> m.Metadata.TryFind ChunkMetadata.FactIdKey)
                |> Set.ofList

            // Phase 506 — what the query-rewrite stage decided, read by
            // `emitTrace` after retrieval finishes. Ref cells rather than
            // `let mutable` because the trace emitter is a closure and F#
            // cannot capture a mutable local; `None` on both is the "no
            // rewrite stage ran" state every pre-506 deployment stays in.
            let rewriteDecision: string option ref = ref None
            let rewrittenQueryHash: string option ref = ref None

            let emitTrace (results: VectorMatch list) (poolSize: int) (sparseRan: bool) (rerankerName: string option) = async {
                match tracer with
                | None -> ()
                | Some t ->
                    stopwatch.Stop()

                    let trace: RetrievalTrace = {
                        QueryHash = ToolUp.RAG.RetrievalTracers.hashQuery request.Query
                        QueryLength = if isNull request.Query then 0 else request.Query.Length
                        RequestedScopes = request.Scopes
                        PermittedScopes = permitted
                        TopK = request.TopK
                        AdaptiveK = request.AdaptiveK.IsSome
                        CandidatePoolSize = poolSize
                        TopScore =
                            match results with
                            | top :: _ -> top.Score
                            | [] -> 0.0
                        DenseUsed = true
                        SparseUsed = sparseRan
                        RerankerName = rerankerName
                        LatencyMs = stopwatch.ElapsedMilliseconds
                        Stages = stages |> List.ofSeq
                        ResultCount = results.Length
                        StageTimings = timings |> List.ofSeq
                        RewriteDecision = rewriteDecision.Value
                        RewrittenQueryHash = rewrittenQueryHash.Value
                    }

                    do! t.Trace trace ctx
            }

            if permitted.IsEmpty then
                // No readable vector scope — but any resolved facts still
                // surface (fact resolution is independent of KB-scope
                // authorisation). Clause-less ⇒ `factMatches` is empty and
                // this returns `[]` exactly as before (GP 11).
                do! emitTrace factMatches 0 false None
                return factMatches
            else
                // Phase 506 — conversation-aware query rewrite.
                //
                // Retrieval embeds the query text, so a follow-up whose
                // subject lives only in the previous turn ("what about
                // it?") embeds to nothing useful and multi-turn recall
                // collapses even when the corpus holds the answer.
                // `RetrievalRequest.History` was documented as feeding
                // exactly this stage but no stage existed, so the field was
                // inert. This is the stage.
                //
                // Three properties, and each is load-bearing:
                //
                // - **Nothing wired ⇒ nothing changes.** No rewriter, or a
                //   request with no history, and the match below returns the
                //   raw query with no stage recorded and no timing entry —
                //   the pipeline is byte-for-byte its pre-506 self (GP 11).
                // - **Bounded.** The call runs under
                //   `QueryRewriteTimeoutMs`; the rewrite sits in front of
                //   retrieval which sits in front of the answering call, so
                //   an unbounded rewriter would stall the user's whole turn.
                // - **Degrades, never fails.** Any exception (including the
                //   timeout) falls back to the raw query and records
                //   `Failed`. A rewrite is an enhancement over a path that
                //   already works; it must never be able to break it.
                //
                // Placed inside the authorised branch deliberately: a
                // caller with no readable scope is going to retrieve nothing
                // regardless, and must not spend a provider call finding
                // that out.
                let! effectiveQuery =
                    match queryRewriter, request.History with
                    | Some rewriter, Some(_ :: _ as history) when not (System.String.IsNullOrWhiteSpace request.Query) -> async {
                        stages.Add "QueryRewrite"
                        let sw = System.Diagnostics.Stopwatch.StartNew()

                        // `StartChild` + its millisecond timeout is the
                        // bound: the child raises `TimeoutException` when
                        // awaited past it, which `Async.Catch` turns into
                        // the same degradation path as any other failure.
                        // The overrunning child is ABANDONED, not cancelled
                        // — it finishes on its own and its result is
                        // discarded. That is the right trade here: the
                        // guarantee this bound owes the user is that the
                        // turn proceeds, and a rewriter's in-flight HTTP
                        // call is the implementation's to bound (the
                        // shipped one passes the same budget to its
                        // provider as a `RetryPolicy.Timeout`).
                        let bounded = async {
                            let! child =
                                Async.StartChild(
                                    rewriter.Rewrite request.Query history,
                                    max 1 opts.QueryRewriteTimeoutMs
                                )

                            return! child
                        }

                        let! outcome = Async.Catch bounded
                        sw.Stop()
                        timings.Add("QueryRewrite", sw.Elapsed.TotalMilliseconds)

                        match outcome with
                        | Choice1Of2 QuerySelfContained ->
                            rewriteDecision.Value <- Some QueryRewriteDecision.SelfContained
                            return request.Query
                        | Choice1Of2(QueryRewritten rewritten) when not (System.String.IsNullOrWhiteSpace rewritten) ->
                            rewriteDecision.Value <- Some QueryRewriteDecision.Rewritten

                            rewrittenQueryHash.Value <- Some(ToolUp.RAG.RetrievalTracers.hashQuery rewritten)

                            return rewritten
                        | Choice1Of2 _ ->
                            // A blank rewrite is not a rewrite. Searching
                            // it would replace a usable query with an
                            // empty embedding — worse than doing nothing.
                            rewriteDecision.Value <- Some QueryRewriteDecision.SelfContained
                            return request.Query
                        | Choice2Of2 _ ->
                            rewriteDecision.Value <- Some QueryRewriteDecision.Failed
                            return request.Query
                      }
                    | _ -> async.Return request.Query

                // Phase 502 — the request's metadata-equality filter, if it
                // carries a live one. Resolved once here: it drives both the
                // candidate-pool size below and the post-search filter stage.
                let metadataFilters = activeFilters request.Filters

                // Stage 1: candidate retrieval (dense [+ sparse]).
                //
                // Phase 502 — a metadata filter narrows the pool *after* the
                // store has ranked it, so over-fetch when one is present or
                // the filtered result set is short-changed. Take the larger
                // of the two inflations when both apply. `Filters = None`
                // leaves the pool exactly as before (GP 11).
                let pool =
                    match needsHybridPool, metadataFilters with
                    | false, None -> request.TopK
                    | true, None -> inflateForHybridOrRerank request.TopK
                    | false, Some _ -> inflateForMetadataFilter request.TopK
                    | true, Some _ ->
                        max (inflateForHybridOrRerank request.TopK) (inflateForMetadataFilter request.TopK)

                stages.Add "Dense"

                let! rawInitial =
                    match sparse with
                    | None -> async {
                        // Cosine-only path. When no sparse index AND no
                        // reranker is wired, this is byte-equivalent to the
                        // pre-Phase-14e pipeline.
                        let denseSw = System.Diagnostics.Stopwatch.StartNew()
                        let! results = denseSearch effectiveQuery permitted pool
                        denseSw.Stop()
                        timings.Add("Dense", denseSw.Elapsed.TotalMilliseconds)
                        return results
                      }

                    | Some sparseIdx -> async {
                        stages.Add "Sparse"

                        // Each branch times itself and returns the elapsed
                        // ms alongside its matches; the post-join appends
                        // keep `timings` single-threaded.
                        let denseAsync = async {
                            let sw = System.Diagnostics.Stopwatch.StartNew()
                            let! results = denseSearch effectiveQuery permitted pool
                            sw.Stop()
                            return results, sw.Elapsed.TotalMilliseconds
                        }

                        let sparseAsync = async {
                            let sw = System.Diagnostics.Stopwatch.StartNew()
                            let! results = sparseIdx.Search permitted effectiveQuery pool
                            sw.Stop()
                            return results, sw.Elapsed.TotalMilliseconds
                        }

                        let! both = Async.Parallel [ denseAsync; sparseAsync ]
                        let denseResults, denseMs = both[0]
                        let sparseResults, sparseMs = both[1]
                        timings.Add("Dense", denseMs)
                        timings.Add("Sparse", sparseMs)

                        stages.Add "RRF"
                        let fuseSw = System.Diagnostics.Stopwatch.StartNew()
                        let fused = fuseRRF denseResults sparseResults
                        fuseSw.Stop()
                        timings.Add("RRF", fuseSw.Elapsed.TotalMilliseconds)
                        return fused
                      }

                // Phase 502 — optional `Filters`: drop chunks whose metadata
                // does not match every requested key/value pair exactly.
                // Applied at the same site as `OriginFilter` (immediately
                // below), before rerank / MMR / topK, so every downstream
                // stage sees a pool that already respects the caller's
                // scoping intent.
                //
                // This field was documented and honoured by the static-corpus
                // pipeline but never read here, so a caller passing `Filters`
                // silently got unfiltered results — content they had asked to
                // exclude reached the model with no diagnostic. GP 10: a
                // shared contract field must mean the same thing on both
                // pipelines; `MetadataFilterContract` pins the parity.
                //
                // Scope note: like `OriginFilter`, this applies to the vector
                // candidate set only. Resolved facts (Phase 522) are merged
                // in separately after every ranking stage and do not carry
                // chunk metadata, so a chunk-metadata filter is not a
                // meaningful predicate over them.
                let metadataFiltered =
                    match metadataFilters with
                    | None -> rawInitial
                    | Some pairs ->
                        stages.Add "MetadataFilter"
                        rawInitial |> List.filter (matchesMetadataFilters pairs)

                // Optional `OriginFilter`: drop chunks whose `_origin`
                // metadata isn't in the request's allow-set. Applied here,
                // before rerank / MMR / topK, so the pool fed to those
                // downstream stages already respects the filter.
                let filtered =
                    match request.OriginFilter with
                    | None -> metadataFiltered
                    | Some allowed ->
                        stages.Add "OriginFilter"

                        metadataFiltered
                        |> List.filter (fun m ->
                            match m.Metadata.TryFind ChunkMetadata.OriginKey with
                            | None ->
                                // No `_origin` stamped — keep the chunk so
                                // pre-filter producers aren't silently dropped.
                                true
                            | Some value -> allowed.Contains(ChunkOrigin.fromMetadataValue value))

                // Optional `ActiveModule` boost: nudge chunks whose
                // `_originModule` matches the caller's active module up
                // the ranking. Re-sorts after applying the boost so
                // downstream stages see a coherent ordering.
                let moduleBoosted =
                    match request.ActiveModule, opts.ActiveModuleBoost with
                    | Some _, b when b <= 0.0 -> filtered
                    | None, _ -> filtered
                    | Some moduleName, boost ->
                        stages.Add "ActiveModuleBoost"

                        filtered
                        |> List.map (fun m ->
                            match m.Metadata.TryFind ChunkMetadata.OriginModuleKey with
                            | Some value when value = moduleName -> { m with Score = m.Score + boost }
                            | _ -> m)
                        |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)

                // Optional summary-chunk boost (WS4.1): chunks stamped
                // `_isSummary = "true"` are the document-level entry
                // point and should outrank paragraph chunks for "what is
                // this about?" queries. Applied after the module boost
                // so a summary in the matching module gets both nudges.
                let initial =
                    if opts.SummaryBoost <= 0.0 then
                        moduleBoosted
                    else
                        let mutable boosted = false

                        let result =
                            moduleBoosted
                            |> List.map (fun m ->
                                match m.Metadata.TryFind ChunkMetadata.IsSummaryKey with
                                | Some "true" ->
                                    boosted <- true

                                    {
                                        m with
                                            Score = m.Score + opts.SummaryBoost
                                    }
                                | _ -> m)

                        if boosted then
                            stages.Add "SummaryBoost"
                            result |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                        else
                            result

                // Phase 522.E — fact→narrative join. A chunk whose
                // `_factRefs` metadata (Phase 521.D) cites a fact the
                // pre-vector stage resolved this turn gets a boost, so the
                // fact's own narrative context outranks generic similarity.
                // No resolved facts (the common clause-less path) ⇒ no-op,
                // byte-identical ordering (GP 11).
                let initial =
                    if Set.isEmpty resolvedFactIds || opts.FactNarrativeJoinBoost <= 0.0 then
                        initial
                    else
                        let mutable joined = false

                        let result =
                            initial
                            |> List.map (fun m ->
                                match m.Metadata.TryFind ChunkMetadata.FactRefsKey with
                                | Some csv when csv.Split(',') |> Array.exists resolvedFactIds.Contains ->
                                    joined <- true

                                    {
                                        m with
                                            Score = m.Score + opts.FactNarrativeJoinBoost
                                    }
                                | _ -> m)

                        if joined then
                            stages.Add "FactNarrativeJoin"
                            result |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                        else
                            result

                // Stage 2: optional cross-encoder rerank.
                let! reranked =
                    match opts.Reranker with
                    | None -> async.Return initial
                    | Some r ->
                        stages.Add "Rerank"
                        let cap = min initial.Length r.MaxBatchSize
                        let head = initial |> List.truncate cap

                        async {
                            let sw = System.Diagnostics.Stopwatch.StartNew()
                            let! reorderedHead = r.Rerank effectiveQuery head
                            sw.Stop()
                            timings.Add("Rerank", sw.Elapsed.TotalMilliseconds)
                            // Keep any tail beyond MaxBatchSize at the end —
                            // they had a lower retrieval rank to begin with,
                            // and the reranker hasn't seen them.
                            let tail = initial |> List.skip cap
                            return reorderedHead @ tail
                        }

                // Stage 3: optional MMR diversification.
                let diversified =
                    if opts.EnableMmr then
                        stages.Add "MMR"
                        let sw = System.Diagnostics.Stopwatch.StartNew()
                        let result = applyMmr opts.MmrLambda reranked
                        sw.Stop()
                        timings.Add("MMR", sw.Elapsed.TotalMilliseconds)
                        result
                    else
                        reranked

                // Stage 4: adaptive top-K (if hint present) else fixed top-K.
                let truncated =
                    match request.AdaptiveK with
                    | Some hint ->
                        stages.Add "AdaptiveK"
                        adaptiveTruncate hint diversified
                    | None ->
                        stages.Add "TopK"
                        diversified |> List.truncate request.TopK

                stages.Add "Merge"
                let mergeSw = System.Diagnostics.Stopwatch.StartNew()
                let final = applyMerge request.Merge request.TopK truncated
                mergeSw.Stop()
                timings.Add("Merge", mergeSw.Elapsed.TotalMilliseconds)

                let rerankerName = opts.Reranker |> Option.map _.Name

                match telemetry with
                | Some t -> t.RecordRetrievalStages(timings |> List.ofSeq)
                | None -> ()

                do! emitTrace final pool sparse.IsSome rerankerName

                // Phase 522.D — resolved facts merge AHEAD of the similarity
                // chunks. Facts are exact hits, not similarity matches, so
                // they bypass rerank / MMR / topK (which only shaped the
                // chunk set) and lead the result. `factMatches` is empty on
                // the clause-less path, so this is `final` unchanged (GP 11).
                return factMatches @ final
        }

        member _.Index chunkId chunk scope = async {
            // Phase 14z — the write side of the same geometry. A chunk
            // must be embedded under the vocabulary the queries for its
            // scope will be embedded under; indexing everything through
            // one global embedder while retrieval queried per scope would
            // put the two in different sparse spaces and break retrieval
            // outright. `ScopedEmbedding.forScope` is the identity
            // function on every stateless provider, so the byte-identical
            // path is preserved here too (GP 11).
            let scopeEmbedder = ScopedEmbedding.forScope embedder scope
            let! vector = scopeEmbedder.GenerateEmbedding chunk.Content

            let stamped = {
                chunk with
                    Metadata =
                        chunk.Metadata
                        |> Map.add EmbeddingVersion.MetadataProviderKey scopeEmbedder.ProviderId
                        |> Map.add EmbeddingVersion.MetadataModelKey scopeEmbedder.ModelId
                        |> Map.add EmbeddingVersion.MetadataDimensionsKey (string scopeEmbedder.Dimensions)
            }

            do! store.Upsert scope chunkId vector stamped

            match sparse with
            | Some idx -> do! idx.Upsert scope chunkId stamped
            | None -> ()
        }

        member _.DeleteByScope scope = async {
            do! store.DeleteByScope scope

            match sparse with
            | Some idx -> do! idx.DeleteByScope scope
            | None -> ()
        }