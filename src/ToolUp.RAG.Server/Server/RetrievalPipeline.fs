module ToolUp.RAG.RetrievalPipeline

open System.Collections.Generic
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IReranker
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.IRagTelemetry

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
/// - Anonymous users (userId = "anonymous") may read Platform / Deployment
///   only; team scopes are filtered out.
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
            | None -> false)

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
}

module RetrievalPipelineOptions =
    let defaults: RetrievalPipelineOptions = {
        Reranker = None
        EnableMmr = false
        MmrLambda = 0.5
        ActiveModuleBoost = 0.05
        SummaryBoost = 0.10
    }

// ─── Pipeline implementation ──────────────────────────────────────

/// Default candidate-pool inflation when hybrid retrieval or rerank is
/// active. We ask each retriever for `max(topK × 4, 32)` so RRF and any
/// downstream cross-encoder have a useful working set — a top-1 query
/// with a single dense and single sparse hit produces no signal for either.
let private inflateForHybridOrRerank (topK: int) = max (topK * 4) 32

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
        ?telemetry: IRagTelemetry
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

    interface IRetrievalPipeline with

        member _.Retrieve request ctx = async {
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let stages = ResizeArray<string>()
            // Per-stage `(name, elapsedMs)` pairs (Phase 122). Appended
            // sequentially — the concurrent dense/sparse branches return
            // their elapsed time and the join appends both, so no two
            // threads touch this list.
            let timings = ResizeArray<string * float>()
            stages.Add "AuthoriseScopes"

            let permitted = authorisedScopes (readPlatformKbMode ()) ctx request.Scopes

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
                    }

                    do! t.Trace trace ctx
            }

            if permitted.IsEmpty then
                do! emitTrace [] 0 false None
                return []
            else
                // Stage 1: candidate retrieval (dense [+ sparse]).
                let pool =
                    if needsHybridPool then
                        inflateForHybridOrRerank request.TopK
                    else
                        request.TopK

                stages.Add "Dense"

                let! rawInitial =
                    match sparse with
                    | None -> async {
                        // Cosine-only path. When no sparse index AND no
                        // reranker is wired, this is byte-equivalent to the
                        // pre-Phase-14e pipeline.
                        let denseSw = System.Diagnostics.Stopwatch.StartNew()
                        let! queryVector = embedder.GenerateEmbedding request.Query
                        let! results = store.Search permitted queryVector pool
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
                            let! queryVector = embedder.GenerateEmbedding request.Query
                            let! results = store.Search permitted queryVector pool
                            sw.Stop()
                            return results, sw.Elapsed.TotalMilliseconds
                        }

                        let sparseAsync = async {
                            let sw = System.Diagnostics.Stopwatch.StartNew()
                            let! results = sparseIdx.Search permitted request.Query pool
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

                // Optional `OriginFilter`: drop chunks whose `_origin`
                // metadata isn't in the request's allow-set. Applied here,
                // before rerank / MMR / topK, so the pool fed to those
                // downstream stages already respects the filter.
                let filtered =
                    match request.OriginFilter with
                    | None -> rawInitial
                    | Some allowed ->
                        stages.Add "OriginFilter"

                        rawInitial
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
                            let! reorderedHead = r.Rerank request.Query head
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
                return final
        }

        member _.Index chunkId chunk scope = async {
            let! vector = embedder.GenerateEmbedding chunk.Content

            let stamped = {
                chunk with
                    Metadata =
                        chunk.Metadata
                        |> Map.add EmbeddingVersion.MetadataProviderKey embedder.ProviderId
                        |> Map.add EmbeddingVersion.MetadataModelKey embedder.ModelId
                        |> Map.add EmbeddingVersion.MetadataDimensionsKey (string embedder.Dimensions)
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