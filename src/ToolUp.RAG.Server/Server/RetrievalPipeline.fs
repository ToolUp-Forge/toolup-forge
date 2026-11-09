module ToolUp.RAG.RetrievalPipeline

open System.Collections.Generic
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.Platform.Infra.IReranker
open ToolUp.Platform.Infra.IQueryRewriter
open ToolUp.Platform.Infra.IRetrievalPipeline
open ToolUp.Platform.Infra.IRetrievalTracer
open ToolUp.Platform.Infra.IRagTelemetry
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Query-size guard ─────────────────────────────────

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
                "Retrieval query of %d characters exceeds the configured MaxQueryChars cap of %d. A query this long is almost always a programming bug (an entire document pasted into the query slot) — split it, or raise the cap via RagOptions.withMaxQueryChars."
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
    ToolUp.Remoting.Json.SystemTextJson.PlatformJson.options

// ─── Access validation ────────────────────────────────────────────

/// Returns the subset of requested scopes the caller is permitted to read.
///
/// Rules:
/// - `Platform` is readable when `ServerConfig.PlatformKnowledgeBase = Enabled`
///   AND the caller is authenticated. When `Disabled`, this scope is filtered
///   out regardless of caller — Platform chunks stay on disk but are invisible
///   to retrieval. The toggle is the read-side gate;
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
let private authorisedScopes (platformKnowledgeBase: FeatureMode) (ctx: AccessContext) (scopes: VectorScope list) =
    scopes
    |> List.filter (fun scope ->
        match scope with
        | Platform ->
            match platformKnowledgeBase with
            | FeatureMode.Enabled -> true
            | FeatureMode.Disabled -> false
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
/// The raw fused score tops out at `2 / (k + 1)`; the pipeline normalises
/// the pool onto `[0, 1]` before anything reads it
/// (`RetrievalScoreSpace.Fused`).
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

// ─── Score space ──────────────────────────────────────────────────

/// Which scale a retrieved match's `Score` is in — the seam's
/// `ToolUp.Platform.Infra.IRetrievalPipeline.RetrievalScoreSpace`, named here
/// so a caller that opens only this module keeps compiling.
type RetrievalScoreSpace = ToolUp.Platform.Infra.IRetrievalPipeline.RetrievalScoreSpace

module RetrievalScoreSpace =
    /// The stage mark recorded on the trace when the fused pool is
    /// normalised — the trace states the normalisation it applied.
    [<Literal>]
    let NormaliseStage = "Normalise:MinMax"

    /// Stable name of the space, as it appears in the trace stage mark.
    let label (space: RetrievalScoreSpace) : string =
        match space with
        | RetrievalScoreSpace.Cosine -> "Cosine"
        | RetrievalScoreSpace.Fused -> "Fused"
        | RetrievalScoreSpace.Reranked -> "Reranked"

    /// The `ScoreSpace:<space>` stage mark the pipeline appends to
    /// `RetrievalTrace.Stages`, naming the space of the returned scores.
    let stageMark (space: RetrievalScoreSpace) : string = "ScoreSpace:" + label space

    /// The space a composition's scores reach a reader in: a reranker
    /// rescores whatever it is given, otherwise a keyword index means fusion.
    let ofComposition (keywordIndex: bool) (reranker: bool) : RetrievalScoreSpace =
        if reranker then RetrievalScoreSpace.Reranked
        elif keywordIndex then RetrievalScoreSpace.Fused
        else RetrievalScoreSpace.Cosine

    /// The highest unboosted score the space can produce, when the SDK
    /// knows it. `Cosine` and `Fused` both top out at `1.0` (the fused
    /// pool's best candidate is exactly `1.0`); `Reranked` is uncalibrated.
    let upperBound (space: RetrievalScoreSpace) : float option =
        match space with
        | RetrievalScoreSpace.Cosine
        | RetrievalScoreSpace.Fused -> Some 1.0
        | RetrievalScoreSpace.Reranked -> None

    /// Whether a `MinScore` threshold can be met in `space`, as the message
    /// a startup validator should warn with — `None` when it can. `space`
    /// is `None` when a supplied `IRetrievalPipeline` owns retrieval and
    /// its space is not the SDK's to see. The gate keeps only scores
    /// strictly ABOVE the threshold, so a threshold at the space's upper
    /// bound drops every unboosted match.
    let thresholdFinding (space: RetrievalScoreSpace option) (threshold: float option) : string option =
        match threshold with
        | None -> None
        | Some t ->
            match space with
            | None ->
                Some(
                    sprintf
                        "MinScore = %g is applied to the scores of a supplied IRetrievalPipeline (RagOptions.withRetrievalPipeline), whose score space the SDK cannot see. Confirm that pipeline's scores can exceed %g; if they cannot, the gate drops every chunk and the assistant answers with no retrieved context."
                        t
                        t
                )
            | Some space ->
                match upperBound space with
                | None ->
                    Some(
                        sprintf
                            "MinScore = %g is applied in the %s score space: the composed reranker's own scores (RagOptions.withReranker). IReranker promises an ordering, not a calibrated scale, so the SDK cannot confirm %g is reachable. Check it against that reranker's documented score range; a threshold above it drops every chunk and the assistant answers with no retrieved context."
                            t
                            (label space)
                            t
                    )
                | Some bound when not (RetrievalScoreSpace.admits space threshold bound) ->
                    Some(
                        sprintf
                            "MinScore = %g cannot be met in the %s score space, which tops out at %g: the gate keeps only scores strictly above the threshold, so it drops every unboosted chunk and the assistant answers with no retrieved context. Lower it (RagOptions.withMinScore)."
                            t
                            (label space)
                            bound
                    )
                | Some _ -> None

// ─── Stage marks ──────────────────────────────────────────────────

/// How a bounded stage ended without its result.
[<RequireQualifiedAccess>]
type BoundedFault =
    /// The stage overran its budget and was abandoned.
    | TimedOut
    /// The stage raised.
    | Failed

module BoundedFault =
    /// The fault's name, as it appears in a `…Degraded:<fault>` mark.
    let label (fault: BoundedFault) : string =
        match fault with
        | BoundedFault.TimedOut -> "TimedOut"
        | BoundedFault.Failed -> "Failed"

/// The three additive nudges the pipeline applies after the reranker.
[<RequireQualifiedAccess>]
type BoostKind =
    /// A chunk whose `_originModule` is the request's `ActiveModule`.
    | ActiveModule
    /// A chunk stamped `_isSummary = "true"`.
    | Summary
    /// A chunk whose `_factRefs` cite a fact resolved this turn.
    | FactNarrativeJoin

/// The step that ordered the pool, named on the trace as `FinalOrder:<step>`.
[<RequireQualifiedAccess>]
type OrderingStep =
    | Dense
    | RRF
    | Rerank
    | Boost of BoostKind
    | MMR
    | Interleaved
    | Separate

/// One mark a retrieval stage records on `RetrievalTrace.Stages`. The
/// trace carries the marks rendered by `RetrievalStage.mark`; the strings
/// are a shipped grammar, so a case is added rather than a string renamed.
[<RequireQualifiedAccess>]
type RetrievalStage =
    | AuthoriseScopes
    | FactResolve
    /// The disclosure gate withheld at least one resolved fact.
    | DisclosureFilter
    | FactsDegraded of BoundedFault
    | QueryRewrite
    | Dense
    /// The bounded query embed produced no vector (never `Embedded`).
    | DenseDegraded of QueryEmbedOutcome
    | Sparse
    | RRF
    | MetadataFilter
    | OriginFilter
    | DerivedDisclosureDegraded of BoundedFault
    | DerivedDisclosure
    | DerivedDisclosureDropped of dropped: int * pool: int
    | Normalise
    | Rerank
    /// The pool was larger than the reranker's batch and was capped at it.
    | RerankCapped of reranked: int * pool: int
    | Boost of BoostKind
    | MMR
    | AdaptiveK
    | TopK
    /// The merge; `ordered` names the step when the merge itself ordered the pool.
    | Merge of ordered: OrderingStep option
    | FinalOrder of OrderingStep
    | ScoreSpace of RetrievalScoreSpace

module RetrievalStage =
    let private boostMark (kind: BoostKind) =
        match kind with
        | BoostKind.ActiveModule -> "ActiveModuleBoost"
        | BoostKind.Summary -> "SummaryBoost"
        | BoostKind.FactNarrativeJoin -> "FactNarrativeJoin"

    let private orderingMark (step: OrderingStep) =
        match step with
        | OrderingStep.Dense -> "Dense"
        | OrderingStep.RRF -> "RRF"
        | OrderingStep.Rerank -> "Rerank"
        | OrderingStep.Boost kind -> boostMark kind
        | OrderingStep.MMR -> "MMR"
        | OrderingStep.Interleaved -> "Interleaved"
        | OrderingStep.Separate -> "Separate"

    /// The mark's string on the trace.
    let mark (stage: RetrievalStage) : string =
        match stage with
        | RetrievalStage.AuthoriseScopes -> "AuthoriseScopes"
        | RetrievalStage.FactResolve -> "FactResolve"
        | RetrievalStage.DisclosureFilter -> "DisclosureFilter"
        | RetrievalStage.FactsDegraded fault -> "FactsDegraded:" + BoundedFault.label fault
        | RetrievalStage.QueryRewrite -> "QueryRewrite"
        | RetrievalStage.Dense -> "Dense"
        | RetrievalStage.DenseDegraded outcome -> "DenseDegraded:" + QueryEmbedOutcome.label outcome
        | RetrievalStage.Sparse -> "Sparse"
        | RetrievalStage.RRF -> "RRF"
        | RetrievalStage.MetadataFilter -> "MetadataFilter"
        | RetrievalStage.OriginFilter -> "OriginFilter"
        | RetrievalStage.DerivedDisclosureDegraded fault -> "DerivedDisclosureDegraded:" + BoundedFault.label fault
        | RetrievalStage.DerivedDisclosure -> "DerivedDisclosure"
        | RetrievalStage.DerivedDisclosureDropped(dropped, pool) -> sprintf "DerivedDisclosure:%d/%d" dropped pool
        | RetrievalStage.Normalise -> RetrievalScoreSpace.NormaliseStage
        | RetrievalStage.Rerank -> "Rerank"
        | RetrievalStage.RerankCapped(reranked, pool) -> sprintf "Rerank:%d/%d" reranked pool
        | RetrievalStage.Boost kind -> boostMark kind
        | RetrievalStage.MMR -> "MMR"
        | RetrievalStage.AdaptiveK -> "AdaptiveK"
        | RetrievalStage.TopK -> "TopK"
        | RetrievalStage.Merge _ -> "Merge"
        | RetrievalStage.FinalOrder step -> "FinalOrder:" + orderingMark step
        | RetrievalStage.ScoreSpace space -> RetrievalScoreSpace.stageMark space

    /// The order a stage leaves the pool in, when the stage orders it.
    let ordering (stage: RetrievalStage) : OrderingStep option =
        match stage with
        | RetrievalStage.Dense -> Some OrderingStep.Dense
        | RetrievalStage.RRF -> Some OrderingStep.RRF
        | RetrievalStage.Rerank -> Some OrderingStep.Rerank
        | RetrievalStage.Boost kind -> Some(OrderingStep.Boost kind)
        | RetrievalStage.MMR -> Some OrderingStep.MMR
        | RetrievalStage.Merge ordered -> ordered
        | _ -> None

/// A bounded stage: the work runs under a wall-clock budget, and an overrun
/// or a fault is returned as data for the stage to mark.
module internal Bounded =
    /// Run `work` within `budgetMs`. `None` leaves it unbounded, and a fault
    /// raises. `StartChild` bounds the wait, not the work: an overrunning
    /// child is abandoned and finishes on its own, so `work` must touch no
    /// state the caller reads afterwards.
    let run (budgetMs: int option) (work: Async<'T>) : Async<Result<'T, BoundedFault>> =
        match budgetMs with
        | None -> async {
            let! value = work
            return Ok value
          }
        | Some budget -> async {
            let bounded = async {
                let! child = Async.StartChild(work, max 1 budget)
                return! child
            }

            match! Async.Catch bounded with
            | Choice1Of2 value -> return Ok value
            | Choice2Of2(:? System.TimeoutException) -> return Error BoundedFault.TimedOut
            | Choice2Of2 _ -> return Error BoundedFault.Failed
          }

/// Min-max normalisation of a pool's scores onto `[0, 1]`: the
/// best candidate maps to `1.0`, the weakest to `0.0`, and a pool whose
/// scores are all equal (a single candidate included) maps every score to
/// `1.0`. Affine and increasing, so it never changes the pool's order.
/// Shared by the fused-pool normalisation and MMR's relevance term.
let private minMaxNormaliser (scores: float list) : float -> float =
    match scores with
    | [] -> id
    | _ ->
        let minS = List.min scores
        let range = List.max scores - minS

        if range = 0.0 then
            (fun _ -> 1.0)
        else
            (fun s -> (s - minS) / range)

/// Normalise a fused pool onto `[0, 1]` (see `Fused`).
let private normaliseWithinPool (pool: VectorMatch list) : VectorMatch list =
    let normalise = minMaxNormaliser (pool |> List.map _.Score)

    pool |> List.map (fun m -> { m with Score = normalise m.Score })

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
/// stage. The pipeline applies MMR after the rerank and the boosts, and its
/// order is the order the caller receives — nothing re-sorts after it
/// .
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
        // same scale. A reranker's scores are otherwise on an uncalibrated
        // range, and a boosted pool can exceed 1.0.
        let normalised = minMaxNormaliser (candidates |> List.map _.Score)

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

/// How many candidates adaptive-K keeps. When the score gap between
/// successive candidates of `relevance` reaches `ScoreFloor`, the long tail
/// is no longer informative: the cut is there. Bounded to `[MinK, MaxK]`, so
/// a list with no cliff still keeps a sensible number. `relevance` is the
/// pool in descending score order: the cut is read on scores, so on the
/// relevance order and never on an order MMR diversified.
let private adaptiveCount (hint: AdaptiveKHint) (relevance: VectorMatch list) : int =
    let scores = relevance |> List.map _.Score |> List.toArray

    let cliff =
        [ 0 .. scores.Length - 2 ]
        |> List.tryFind (fun i -> scores[i] - scores[i + 1] >= hint.ScoreFloor && i + 1 >= hint.MinK)
        |> Option.map (fun i -> i + 1)

    let cut =
        match cliff with
        | Some at -> min at hint.MaxK
        | None -> min scores.Length hint.MaxK

    max hint.MinK cut |> min scores.Length

// ─── Merge ────────────────────────────────────────────────────────

/// `Separate`: the matches grouped by scope, in the order the request
/// listed the scopes, each group in the order it arrived (a stable sort).
let private groupByScope (scopes: VectorScope list) (matches: VectorMatch list) : VectorMatch list =
    let rank (scope: VectorScope) =
        scopes |> List.tryFindIndex ((=) scope) |> Option.defaultValue scopes.Length

    matches |> List.sortBy (fun m -> rank m.Scope)

// ─── Pipeline options ─────────────────────────────────────────────

/// Backend-tuning knobs for `RetrievalPipeline`. Exposed at construction
/// time, not per-request, because they affect the wiring shape rather than
/// the query intent — a deployment that wants per-call control wraps the
/// pipeline.
type RetrievalPipelineOptions = {
    /// Optional cross-encoder reranker. Applied after RRF fusion and its
    /// normalisation, before the boosts, MMR and adaptive-K / topK
    /// truncation. `None` skips rerank. The candidate pool is inflated when
    /// a reranker is present so the reranker sees a meaningful working set.
    ///
    /// The reranker scores every candidate the pipeline returns:
    /// a pool larger than `MaxBatchSize` is capped at it (the trace records
    /// `Rerank:<n>/<pool>`), so a reranker whose `MaxBatchSize` is below the
    /// request's `TopK` returns at most `MaxBatchSize` matches. The pool is
    /// not split across calls, because `IReranker` promises no scale shared
    /// between calls.
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
    ///
    /// Every boost is an additive NUDGE within the pool's
    /// score space (`RetrievalScoreSpace`), applied after the fused pool is
    /// normalised onto `[0, 1]`: a boost of `b` lifts a match over one at
    /// most `b` better and never over one further ahead. The three defaults
    /// were re-derived against that unit range (5 %, 10 % and 15 % of it)
    /// and against cosine similarity on the dense-only path, where the same
    /// magnitudes are the same kind of nudge.
    ///
    /// The boosts apply AFTER the reranker, when one is
    /// composed, and there `b` is measured in units of the reranked pool's
    /// range: the same share of the pool it is on the normalised fused
    /// scale.
    ActiveModuleBoost: float
    /// Score boost applied to chunks marked with `_isSummary = "true"`.
    /// Defaults to `+0.10` — larger than `ActiveModuleBoost`
    /// because summary chunks are *intentionally* the document-level
    /// entry point: "what is this document about?" should land on the
    /// summary first. Set to `0.0` to disable.
    SummaryBoost: float
    /// Score boost applied to a chunk whose `_factRefs` metadata cites a
    /// fact the pre-vector stage resolved for this request — the
    /// fact→narrative join. A fact's own narrative
    /// context should outrank generic similarity, so the default `+0.15`
    /// is larger than `SummaryBoost`. Set to `0.0` to disable, or when no
    /// fact clause / resolver is present it simply never fires.
    FactNarrativeJoinBoost: float
    /// Wall-clock bound, in milliseconds, on a single `IQueryRewriter`
    /// call. The rewrite sits in front of retrieval, which
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

/// The wall-clock budgets a chat turn's retrieval runs under.
/// A slow embedding provider or a slow fact store degrades the turn's
/// retrieval and never stalls the turn: each bounded stage that overruns
/// or faults contributes nothing, and says so with a stage mark (GP 9 —
/// a degraded retrieval is marked, never silent).
///
/// Supplied to `RetrievalPipeline` as the optional `budgets` argument.
/// Omitting it leaves every stage unbounded, a provider failure raising
/// out of `Retrieve` (GP 11). `RagOptions` supplies `defaults` unless a
/// deployment sets its own.
type RetrievalBudgets = {
    /// Policy for the query embed (the dense branch). On any outcome but
    /// `Embedded`, the dense branch returns nothing and the sparse branch,
    /// when one is composed, answers alone; the trace carries a
    /// `DenseDegraded:<TimedOut|Refused|Failed>` stage mark.
    QueryEmbed: QueryEmbedPolicy
    /// Budget for fact resolution and the disclosure check TOGETHER. On
    /// overrun or fault the turn proceeds without pushed facts and the
    /// trace carries `FactsDegraded:<TimedOut|Failed>`. Fail-closed by
    /// construction: a fact whose disclosure check did not complete is
    /// absent, never admitted. The same bound applies, separately, to the
    /// derived-disclosure check: on overrun or fault every
    /// chunk citing facts is dropped and the trace carries
    /// `DerivedDisclosureDegraded:<TimedOut|Failed>`.
    FactStage: System.TimeSpan
}

module RetrievalBudgets =
    /// SDK default: `QueryEmbedPolicy.defaults` (one attempt, 5 s, no
    /// concurrency ceiling) and a 3 s fact stage.
    let defaults: RetrievalBudgets = {
        QueryEmbed = QueryEmbedPolicy.defaults
        FactStage = System.TimeSpan.FromSeconds 3.0
    }

/// An optional batched write path beside
/// `IRetrievalPipeline.Index`. The ingestion drainer probes for it and,
/// when `SupportsBatch` holds, indexes a whole document in one call —
/// one batched embed and one `IVectorStoreBatch.UpsertBatch` round-trip
/// instead of one upsert per chunk. A pipeline without it, or whose store
/// cannot batch, keeps the per-chunk path unchanged (GP 11).
type IBatchIndexer =
    /// `true` when `IndexBatch` is cheaper than per-chunk `Index` — i.e.
    /// the underlying vector store implements `IVectorStoreBatch`.
    abstract SupportsBatch: bool

    /// Index every `(chunkId, chunk)` of one document into `scope`, with
    /// the per-chunk semantics of `Index` (the same embedding-version
    /// stamp, the same sparse-index upsert). All-or-nothing from the
    /// caller's view: on an exception the caller falls back to per-chunk
    /// `Index`, which is idempotent over whatever the batch wrote.
    abstract IndexBatch: chunks: (string * TextChunk) list -> scope: VectorScope -> Async<unit>

// ─── Pipeline implementation ──────────────────────────────────────

/// Default candidate-pool inflation when hybrid retrieval or rerank is
/// active. We ask each retriever for `max(topK × 4, 32)` so RRF and any
/// downstream cross-encoder have a useful working set — a top-1 query
/// with a single dense and single sparse hit produces no signal for either.
let private inflateForHybridOrRerank (topK: int) = max (topK * 4) 32

// ─── Metadata-equality filter ─────────────────────────

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
/// re-admit exactly the content the caller asked to exclude.
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

// ─── Fact-first stage ─────────────────────────────────

/// Score stamped on a resolved-fact match. Facts are exact hits, not
/// similarity matches — they are prepended ahead of the similarity chunks
/// after every ranking stage, so the value only needs to read as
/// "maximally relevant" for any downstream consumer that inspects it (the
/// Sources-panel badge). It never competes in rerank / MMR / topK.
let private factMatchScore = 1.0

/// Project a resolved fact into a `VectorMatch` carrying its id /
/// rendering / freshness / supersession pointer in metadata under the
/// `ChunkMetadata.Fact*` keys, stamped `_origin = "Fact"`.
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

// ─── Derived-corpus disclosure ────────────────────────

/// The fact ids a chunk cites through its `_factRefs` metadata — the
/// comma-separated list narrative-commit stamps. Empty for a
/// chunk with no refs. Read by the derived-disclosure stage (which re-gates
/// the cited facts) and the fact→narrative join boost (which prefers the
/// chunks citing a fact resolved this turn).
let private factRefsOf (m: VectorMatch) : string list =
    match m.Metadata.TryFind ChunkMetadata.FactRefsKey with
    | Some csv ->
        csv.Split(
            ',',
            System.StringSplitOptions.RemoveEmptyEntries
            ||| System.StringSplitOptions.TrimEntries
        )
        |> List.ofArray
    | None -> []

/// Whether a chunk survives the derived-disclosure stage: a chunk with no
/// refs always does; a chunk with refs does only when EVERY cited fact has
/// an affirmative `FactDisclosable` verdict. A denied ref, or one missing
/// from the verdict map, drops the chunk (default-deny).
let private derivedChunkDisclosed (verdicts: Map<string, FactDisclosureVerdict>) (m: VectorMatch) : bool =
    factRefsOf m
    |> List.forall (fun factId ->
        match verdicts.TryFind factId with
        | Some FactDisclosable -> true
        | Some(FactNotDisclosable _)
        | None -> false)

// ─── The stages' working value ────────────────────────────────────

/// One request as it moves through the stages: the pool so far, and what
/// the stages before decided that a later stage or the trace reads.
type private Turn = {
    Request: RetrievalRequest
    Access: AccessContext
    Clock: System.Diagnostics.Stopwatch
    Permitted: VectorScope list
    /// The query the stages search: the request's, or its rewrite.
    Query: string
    /// The resolved facts, merged ahead of the chunks at the end.
    Facts: VectorMatch list
    Pool: ScoredPool
    PoolSize: int
    DenseUsed: bool
    SparseUsed: bool
    /// Each dense candidate's similarity, before fusion.
    DenseSimilarity: Map<VectorScope * string, float>
    RewriteDecision: string option
    RewrittenQueryHash: string option
    Timings: (string * float) list
}

module private Turn =
    let start (request: RetrievalRequest) (access: AccessContext) (space: RetrievalScoreSpace) : Turn = {
        Request = request
        Access = access
        Clock = System.Diagnostics.Stopwatch.StartNew()
        Permitted = []
        Query = request.Query
        Facts = []
        Pool = ScoredPool.ofMatches space []
        PoolSize = 0
        DenseUsed = true
        SparseUsed = false
        DenseSimilarity = Map.empty
        RewriteDecision = None
        RewrittenQueryHash = None
        Timings = []
    }

/// A stage reads the turn and returns the next one with the marks it
/// records. Stages return their marks rather than write them anywhere, so
/// a bounded stage abandoned under its budget leaves nothing behind.
type private Stage = Turn -> Async<Turn * RetrievalStage list>

module private Stage =
    /// The stages run in order, their marks in the same order.
    let rec compose (stages: Stage list) : Stage =
        fun turn ->
            match stages with
            | [] -> async.Return(turn, [])
            | stage :: rest -> async {
                let! next, marks = stage turn
                let! last, later = compose rest next
                return last, marks @ later
              }

type RetrievalPipeline
    (
        store: IVectorStore,
        embedder: IEmbeddingProvider,
        ?sparseIndex: ISparseIndex,
        ?options: RetrievalPipelineOptions,
        ?tracer: IRetrievalTracer,
        ?platformKnowledgeBase: FeatureMode,
        ?platformKnowledgeBaseSnapshot: unit -> FeatureMode,
        // When supplied, each `Retrieve` reports its per-stage
        // timing breakdown via `RecordRetrievalStages` so `/health/rag`
        // can expose per-stage P50/P95. `None` costs nothing (GP 13).
        ?telemetry: IRagTelemetry,
        // Hard cap on query length. A query longer than this is
        // refused at the top of `Retrieve` (before any embedding call) with a
        // `KnowledgeQueryTooLargeException`. `None` disables the guard.
        ?maxQueryChars: int,
        // Audit sink for the `KnowledgeQueryRejected` event a
        // refusal emits. `None` ⇒ the refusal still raises, just without the
        // audit row (GP 13 — zero cost when unwired, e.g. eval / benchmark).
        ?eventStore: IEventStore,
        // Optional fact resolver. When supplied AND the request
        // carries a `FactClause`, the pipeline resolves the (subject,
        // metric, period) query against the fact store BEFORE vector search
        // and merges the exact fact hits ahead of the similarity chunks as
        // a distinct `ChunkOrigin.Fact` origin. `None` (or a clause-less
        // request) ⇒ no fact stage, byte-identical retrieval (GP 11 / GP 13).
        ?factResolver: IFactResolver,
        // The retrieval egress door. When supplied, every
        // resolved fact is checked against the disclosure gate at the
        // `FactRetrieval` surface BEFORE merge: a denied fact is simply
        // absent from the results and the prompt block — never annotated
        // ("see but don't say" is not a mode). Wire this whenever a
        // resolver is wired: the fact companion's compose registers the
        // gate in DI alongside the store, so the pairing is structural.
        // `None` ⇒ pass-through (GP 11; a resolver-less or fact-less
        // deployment is byte-identical either way).
        ?disclosureGate: IFactDisclosureGate,
        // Conversation-aware query rewrite. When supplied AND
        // the request carries non-empty `History`, a follow-up query
        // ("what about it?", "and the second one?") is resolved against the
        // recent turns before anything embeds it. `None` (or a
        // history-less request) ⇒ the stage never runs: same pool, same
        // stage list, same results (GP 11 / GP 13). The call is bounded by
        // `RetrievalPipelineOptions.QueryRewriteTimeoutMs` and degrades to
        // the raw query on any failure — retrieval never fails because a
        // rewrite did.
        ?queryRewriter: IQueryRewriter,
        // The per-turn budgets (query embed, fact stage). `None`
        // ⇒ every stage unbounded (GP 11); `RagOptions` passes `RetrievalBudgets.defaults` unless
        // configured otherwise.
        ?budgets: RetrievalBudgets
    ) =

    let sparse = sparseIndex
    let opts = defaultArg options RetrievalPipelineOptions.defaults
    // Read the toggle via a thunk so runtime mutation
    //
    // takes effect immediately on the next request without rebuilding
    // the pipeline. When supplied, the snapshot thunk wins; otherwise
    // fall back to the static `platformKnowledgeBase` constructor
    // parameter (test-harness compat) or `FeatureMode.Disabled`
    // (safe default matching `ServerConfig.PlatformKnowledgeBase`).
    let staticMode = defaultArg platformKnowledgeBase FeatureMode.Disabled

    let readPlatformKbMode () =
        match platformKnowledgeBaseSnapshot with
        | Some snap -> snap ()
        | None -> staticMode

    let needsHybridPool = sparse.IsSome || opts.Reranker.IsSome

    // ─── Scope-keyed dense retrieval ──────────────────────────────
    //
    // A TF-IDF-class embedder whose state is keyed per `VectorScope`
    // produces a DIFFERENT vector space per scope: dimension `i` denotes
    // `vocab[i]`, and a per-scope vocabulary makes `vocab[i]` a different
    // term in each scope. One query vector searched across every
    // authorised scope is therefore comparing coordinates that do not
    // denote the same thing, so a team-KB document and a Platform-KB
    // document about the same topic could not both surface, ranked.
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
    // branch (GP 11), and the per-scope
    // cost lands on the in-process dev embedder alone.
    let scopedEmbedderFactory = ScopedEmbedding.tryFactory embedder

    // One gate per pipeline, so the concurrency ceiling spans
    // every request it serves. `None` when no budgets were supplied.
    let embedGate = budgets |> Option.map (fun b -> QueryEmbedGate(b.QueryEmbed))

    /// Embed the query. With a gate: bounded, and every non-vector outcome
    /// is returned as `Error` for the caller to degrade on. Without one:
    /// the direct call, which raises on a provider failure.
    let embedQuery (provider: IEmbeddingProvider) (query: string) : Async<Result<float32 array, QueryEmbedOutcome>> =
        match embedGate with
        | None -> async {
            let! vector = provider.GenerateEmbedding query
            return Ok vector
          }
        | Some gate -> async {
            match! gate.Embed provider query with
            | QueryEmbedOutcome.Embedded vector -> return Ok vector
            | other -> return Error other
          }

    /// The per-chunk write shared by `Index` and `IndexBatch`: the chunk
    /// stamped with the embedding version of the scope's embedder.
    let stampFor (scopeEmbedder: IEmbeddingProvider) (chunk: TextChunk) = {
        chunk with
            Metadata =
                chunk.Metadata
                |> Map.add EmbeddingVersion.MetadataProviderKey scopeEmbedder.ProviderId
                |> Map.add EmbeddingVersion.MetadataModelKey scopeEmbedder.ModelId
                |> Map.add EmbeddingVersion.MetadataDimensionsKey (string scopeEmbedder.Dimensions)
    }

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
    ///
    /// Returns the degradation alongside the matches: `Some`
    /// when a bounded query embed produced no vector (for any authorised
    /// scope, on the scope-keyed branch), in which case that scope
    /// contributes nothing. Always `None` when no budgets were supplied.
    let denseSearch
        (query: string)
        (permitted: VectorScope list)
        (pool: int)
        : Async<VectorMatch list * QueryEmbedOutcome option> =
        match scopedEmbedderFactory with
        | None -> async {
            match! embedQuery embedder query with
            | Ok queryVector ->
                let! results = store.Search permitted queryVector pool
                return results, None
            | Error degraded -> return [], Some degraded
          }
        | Some factory -> async {
            let! perScope =
                permitted
                |> List.map (fun scope -> async {
                    let scopedEmbedder = factory.For scope

                    match! embedQuery scopedEmbedder query with
                    | Ok queryVector ->
                        let! results = store.Search [ scope ] queryVector pool
                        return results, None
                    | Error degraded -> return [], Some degraded
                })
                |> Async.Parallel

            let merged =
                perScope
                |> Array.toList
                |> List.collect fst
                |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
                |> List.truncate pool

            return merged, perScope |> Array.tryPick snd
          }

    // Emit the `KnowledgeQueryRejected` audit for an over-length
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

                    let scopeId =
                        match teamId with
                        | Some id -> VectorScope.auditScopeId (Team id)
                        | None -> VectorScope.auditScopeId Platform

                    do!
                        EventStore.writeOrRaise
                            es
                            (Events.create scopeId "ToolUp.RAG" KnowledgeQueryRejectedEventType json)
                with _ ->
                    ()
        }

    /// The space this pipeline's returned scores are in: a reranker replaces
    /// every score it is handed with its own, otherwise the keyword index
    /// means fusion. The boosts nudge within it and the caller reads it.
    let composedSpace =
        RetrievalScoreSpace.ofComposition sparse.IsSome opts.Reranker.IsSome

    let factBudgetMs =
        budgets |> Option.map (fun b -> int b.FactStage.TotalMilliseconds)

    let timed (name: string) (work: Async<'T>) : Async<'T * (string * float)> = async {
        let sw = System.Diagnostics.Stopwatch.StartNew()
        let! value = work
        sw.Stop()
        return value, (name, sw.Elapsed.TotalMilliseconds)
    }

    let resort (matches: VectorMatch list) =
        matches |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)

    let withMatches (turn: Turn) (matches: VectorMatch list) = {
        turn with
            Pool = { turn.Pool with Matches = matches }
    }

    // ─── The stages, in the order `retrieveScored` composes them ──────

    /// The scopes the caller may read.
    let authorise: Stage =
        fun turn ->
            let permitted =
                authorisedScopes (readPlatformKbMode ()) turn.Access turn.Request.Scopes

            async.Return({ turn with Permitted = permitted }, [ RetrievalStage.AuthoriseScopes ])

    /// Resolve the request's fact clause against the fact store, scope-
    /// filtered to `request.FactScope` (GP 4) and gated for disclosure
    /// BEFORE merge: a fact the gate does not affirmatively disclose is
    /// absent, not annotated. Independent of the vector-scope
    /// authorisation. Clause-less or resolver-less ⇒ no facts and no mark.
    /// Bounded by the fact budget and fail-closed: a fact whose disclosure
    /// check did not complete is absent.
    let facts: Stage =
        fun turn ->
            match factResolver, turn.Request.FactClause with
            | Some resolver, Some clause -> async {
                let factScope = turn.Request.FactScope

                let resolveAndDisclose = async {
                    let! resolved = resolver.Resolve(factScope, clause)

                    let! disclosed =
                        match disclosureGate, resolved with
                        | Some gate, _ :: _ -> async {
                            let ids = resolved |> List.map _.FactId
                            let! verdicts = gate.Check(factScope, turn.Access.UserId, FactRetrieval, ids)

                            let permitted =
                                resolved
                                |> List.filter (fun rf ->
                                    match verdicts.TryFind rf.FactId with
                                    | Some FactDisclosable -> true
                                    | Some(FactNotDisclosable _)
                                    | None -> false)

                            let marks =
                                if permitted.Length < resolved.Length then
                                    [ RetrievalStage.DisclosureFilter ]
                                else
                                    []

                            return permitted, marks
                          }
                        | _ -> async.Return(resolved, [])

                    let disclosed, marks = disclosed
                    let scope = factScopeFor turn.Request.Scopes
                    return disclosed |> List.map (factToMatch scope), marks
                }

                match! Bounded.run factBudgetMs resolveAndDisclose with
                | Ok(matches, marks) -> return { turn with Facts = matches }, RetrievalStage.FactResolve :: marks
                | Error fault -> return turn, [ RetrievalStage.FactResolve; RetrievalStage.FactsDegraded fault ]
              }
            | _ -> async.Return(turn, [])

    /// Conversation-aware query rewrite: a follow-up whose subject lives
    /// only in an earlier turn is resolved before anything embeds it.
    /// Nothing wired, or no history, ⇒ no mark and the raw query. Bounded by
    /// `QueryRewriteTimeoutMs`, and it degrades to the raw query on any
    /// fault, recording `Failed` — retrieval never fails because a rewrite did.
    let rewrite: Stage =
        fun turn ->
            let request = turn.Request

            match queryRewriter, request.History with
            | Some rewriter, Some(_ :: _ as history) when not (System.String.IsNullOrWhiteSpace request.Query) -> async {
                let! outcome, timing =
                    timed
                        "QueryRewrite"
                        (Bounded.run (Some opts.QueryRewriteTimeoutMs) (rewriter.Rewrite request.Query history))

                let decision, query, hash =
                    match outcome with
                    | Ok(QueryRewritten rewritten) when not (System.String.IsNullOrWhiteSpace rewritten) ->
                        QueryRewriteDecision.Rewritten, rewritten, Some(ToolUp.RAG.RetrievalTracers.hashQuery rewritten)
                    // A blank rewrite is not a rewrite: searching it would
                    // replace a usable query with an empty embedding.
                    | Ok _ -> QueryRewriteDecision.SelfContained, request.Query, None
                    | Error _ -> QueryRewriteDecision.Failed, request.Query, None

                return
                    {
                        turn with
                            Query = query
                            RewriteDecision = Some decision
                            RewrittenQueryHash = hash
                            Timings = turn.Timings @ [ timing ]
                    },
                    [ RetrievalStage.QueryRewrite ]
              }
            | _ -> async.Return(turn, [])

    /// Candidate retrieval: dense, and with a keyword index sparse beside it
    /// fused by RRF. The pool is inflated for a hybrid or reranked pool and
    /// for a metadata filter (which narrows it after the store ranked it).
    /// A bounded query embed that produced no vector leaves the dense branch
    /// empty and says so. Each candidate's dense similarity is kept beside
    /// the pool: it is the absolute reading a fused order no longer carries.
    let candidates: Stage =
        fun turn -> async {
            let request = turn.Request

            let poolSize =
                match needsHybridPool, activeFilters request.Filters with
                | false, None -> request.TopK
                | true, None -> inflateForHybridOrRerank request.TopK
                | false, Some _ -> inflateForMetadataFilter request.TopK
                | true, Some _ -> max (inflateForHybridOrRerank request.TopK) (inflateForMetadataFilter request.TopK)

            let degradedMark (degraded: QueryEmbedOutcome option) =
                degraded |> Option.map RetrievalStage.DenseDegraded |> Option.toList

            let similarity (dense: VectorMatch list) =
                dense |> List.map (fun m -> (m.Scope, m.ChunkId), m.Score) |> Map.ofList

            match sparse with
            | None ->
                let! (dense, degraded), denseTiming = timed "Dense" (denseSearch turn.Query turn.Permitted poolSize)

                return
                    {
                        withMatches turn dense with
                            PoolSize = poolSize
                            DenseUsed = degraded.IsNone
                            DenseSimilarity = similarity dense
                            Timings = turn.Timings @ [ denseTiming ]
                    },
                    RetrievalStage.Dense :: degradedMark degraded
            | Some sparseIdx ->
                // The two branches run concurrently and each returns its own
                // timing, so nothing they share is written from two threads.
                let! denseChild = Async.StartChild(timed "Dense" (denseSearch turn.Query turn.Permitted poolSize))
                let! sparseResults, sparseTiming = timed "Sparse" (sparseIdx.Search turn.Permitted turn.Query poolSize)
                let! (dense, degraded), denseTiming = denseChild
                let! fused, fuseTiming = timed "RRF" (async { return fuseRRF dense sparseResults })

                return
                    {
                        withMatches turn fused with
                            PoolSize = poolSize
                            DenseUsed = degraded.IsNone
                            SparseUsed = true
                            DenseSimilarity = similarity dense
                            Timings = turn.Timings @ [ denseTiming; sparseTiming; fuseTiming ]
                    },
                    [ RetrievalStage.Dense; RetrievalStage.Sparse ]
                    @ degradedMark degraded
                    @ [ RetrievalStage.RRF ]
        }

    /// The scoping filters, before any ranking stage sees the pool. The
    /// metadata filter is strict equality on every requested pair (a chunk
    /// missing a key does not pass — GP 4, and the static-corpus semantics);
    /// the origin filter keeps a chunk with no `_origin` stamp. Then the
    /// derived-corpus disclosure: a chunk whose `_factRefs` cite a fact
    /// without an affirmative verdict is dropped, one gate call for every
    /// distinct cited fact, bounded by the fact budget and fail-closed (an
    /// overrun drops every chunk carrying refs). Facts merge separately and
    /// carry no chunk metadata, so none of these reads them.
    let filters: Stage =
        fun turn -> async {
            let request = turn.Request

            let metadataFiltered, metadataMarks =
                match activeFilters request.Filters with
                | None -> turn.Pool.Matches, []
                | Some pairs ->
                    turn.Pool.Matches |> List.filter (matchesMetadataFilters pairs), [ RetrievalStage.MetadataFilter ]

            let originFiltered, originMarks =
                match request.OriginFilter with
                | None -> metadataFiltered, []
                | Some allowed ->
                    metadataFiltered
                    |> List.filter (fun m ->
                        match m.Metadata.TryFind ChunkMetadata.OriginKey with
                        | None -> true
                        | Some value -> allowed.Contains(ChunkOrigin.fromMetadataValue value)),
                    [ RetrievalStage.OriginFilter ]

            let! admitted, disclosureMarks, timings =
                match disclosureGate with
                | None -> async.Return(originFiltered, [], [])
                | Some gate ->
                    match originFiltered |> List.collect factRefsOf |> List.distinct with
                    | [] -> async.Return(originFiltered, [], [])
                    | cited -> async {
                        let check = gate.Check(request.FactScope, turn.Access.UserId, FactRetrieval, cited)
                        let! verdicts, timing = timed "DerivedDisclosure" (Bounded.run factBudgetMs check)

                        let admitted, degraded =
                            match verdicts with
                            | Ok v -> originFiltered |> List.filter (derivedChunkDisclosed v), []
                            | Error fault ->
                                originFiltered |> List.filter (fun m -> List.isEmpty (factRefsOf m)),
                                [ RetrievalStage.DerivedDisclosureDegraded fault ]

                        let dropped =
                            if admitted.Length < originFiltered.Length then
                                [
                                    RetrievalStage.DerivedDisclosure
                                    RetrievalStage.DerivedDisclosureDropped(
                                        originFiltered.Length - admitted.Length,
                                        originFiltered.Length
                                    )
                                ]
                            else
                                []

                        return admitted, degraded @ dropped, [ timing ]
                      }

            return
                {
                    withMatches turn admitted with
                        Timings = turn.Timings @ timings
                },
                metadataMarks @ originMarks @ disclosureMarks
        }

    /// A raw fused score tops out at 2/61, so the fused pool is min-max
    /// normalised onto [0, 1] after the scoping filters and before the
    /// boosts. Affine and increasing: the order is unchanged. The cosine
    /// path is left as the store scored it.
    let normalise: Stage =
        fun turn ->
            match turn.Pool.Space, turn.Pool.Matches with
            | RetrievalScoreSpace.Fused, _ :: _ ->
                async.Return(withMatches turn (normaliseWithinPool turn.Pool.Matches), [ RetrievalStage.Normalise ])
            | _ -> async.Return(turn, [])

    /// The composed reranker scores the whole pool the pipeline goes on to
    /// return, capped at its `MaxBatchSize`. The pool is not split across
    /// calls (`IReranker` promises an ordering within a call, not a scale
    /// shared across calls) and no unreranked tail is kept. The boost unit
    /// is read off the pool this stage leaves.
    let rerank: Stage =
        fun turn -> async {
            let! turn, marks =
                match opts.Reranker with
                | None -> async.Return(turn, [])
                | Some r -> async {
                    let pool = turn.Pool.Matches
                    let cap = max 0 (min pool.Length r.MaxBatchSize)

                    let! reordered, timing = timed "Rerank" (r.Rerank turn.Query (List.truncate cap pool))

                    return
                        {
                            turn with
                                Pool = {
                                    turn.Pool with
                                        Matches = reordered
                                        Space = RetrievalScoreSpace.Reranked
                                }
                                Timings = turn.Timings @ [ timing ]
                        },
                        RetrievalStage.Rerank
                        :: (if cap < pool.Length then
                                [ RetrievalStage.RerankCapped(cap, pool.Length) ]
                            else
                                [])
                  }

            return
                {
                    turn with
                        Pool = {
                            turn.Pool with
                                Unit = ScoredPool.unitOf composedSpace turn.Pool.Matches
                        }
                },
                marks
        }

    /// One additive nudge of `magnitude` × the pool's unit to every match
    /// `trigger` selects, then a re-sort. `None` (the boost does not apply
    /// to this request) or a non-positive magnitude is no stage at all. The
    /// active-module boost is announced whenever the request names a
    /// module; the other two only when they lifted a chunk.
    let boost (kind: BoostKind, magnitude: float) (trigger: (VectorMatch -> bool) option) : Stage =
        fun turn ->
            match trigger with
            | Some lifts when magnitude > 0.0 ->
                let lifted =
                    turn.Pool.Matches
                    |> List.map (fun m ->
                        if lifts m then
                            true,
                            {
                                m with
                                    Score = m.Score + magnitude * turn.Pool.Unit
                            }
                        else
                            false, m)

                if kind = BoostKind.ActiveModule || List.exists fst lifted then
                    async.Return(withMatches turn (lifted |> List.map snd |> resort), [ RetrievalStage.Boost kind ])
                else
                    async.Return(turn, [])
            | _ -> async.Return(turn, [])

    /// The three boosts, after the reranker and on its scale.
    let boosts: Stage =
        fun turn ->
            let resolvedFactIds =
                turn.Facts
                |> List.choose (fun m -> m.Metadata.TryFind ChunkMetadata.FactIdKey)
                |> Set.ofList

            let activeModule =
                turn.Request.ActiveModule
                |> Option.map (fun moduleName m -> m.Metadata.TryFind ChunkMetadata.OriginModuleKey = Some moduleName)

            let summary =
                Some(fun (m: VectorMatch) -> m.Metadata.TryFind ChunkMetadata.IsSummaryKey = Some "true")

            let factNarrativeJoin =
                if Set.isEmpty resolvedFactIds then
                    None
                else
                    Some(fun m -> factRefsOf m |> List.exists resolvedFactIds.Contains)

            Stage.compose
                [
                    boost (BoostKind.ActiveModule, opts.ActiveModuleBoost) activeModule
                    boost (BoostKind.Summary, opts.SummaryBoost) summary
                    boost (BoostKind.FactNarrativeJoin, opts.FactNarrativeJoinBoost) factNarrativeJoin
                ]
                turn

    /// MMR diversification, the last step that orders the pool. The order it
    /// diversified is kept as the pool's `Relevance`.
    let mmr: Stage =
        fun turn ->
            if opts.EnableMmr then
                async {
                    let! diversified, timing = timed "MMR" (async { return applyMmr opts.MmrLambda turn.Pool.Matches })

                    return
                        {
                            turn with
                                Pool = {
                                    turn.Pool with
                                        Matches = diversified
                                        Relevance = Some turn.Pool.Matches
                                }
                                Timings = turn.Timings @ [ timing ]
                        },
                        [ RetrievalStage.MMR ]
                }
            else
                async.Return(turn, [])

    /// Adaptive-K when the request carries a hint, else its fixed `TopK`.
    /// Adaptive-K reads the score gaps of the relevance order, never MMR's
    /// diversity order, and keeps that many of the pool in the pool's order.
    let truncate: Stage =
        fun turn ->
            let pool = turn.Pool

            match turn.Request.AdaptiveK with
            | Some hint ->
                let kept = adaptiveCount hint (pool.Relevance |> Option.defaultValue pool.Matches)
                async.Return(withMatches turn (List.truncate kept pool.Matches), [ RetrievalStage.AdaptiveK ])
            | None ->
                async.Return(withMatches turn (List.truncate turn.Request.TopK pool.Matches), [ RetrievalStage.TopK ])

    /// The merge bounds the result to `TopK`. `Interleaved` re-sorts by score
    /// unless MMR ordered the pool (MMR's order is final); `Separate` groups
    /// the result by scope in the order the request listed the scopes, each
    /// group in the order it arrived.
    let merge: Stage =
        fun turn -> async {
            let request = turn.Request
            let pool = turn.Pool.Matches

            let! (merged, ordered), timing =
                timed
                    "Merge"
                    (async {
                        return
                            match request.Merge with
                            | Interleaved when not opts.EnableMmr ->
                                resort pool |> List.truncate request.TopK, Some OrderingStep.Interleaved
                            | Interleaved -> List.truncate request.TopK pool, None
                            | Separate ->
                                let grouped = groupByScope request.Scopes (List.truncate request.TopK pool)

                                let spansScopes = grouped |> List.map _.Scope |> List.distinct |> List.length > 1

                                grouped, (if spansScopes then Some OrderingStep.Separate else None)
                    })

            return
                {
                    withMatches turn merged with
                        Timings = turn.Timings @ [ timing ]
                },
                [ RetrievalStage.Merge ordered ]
        }

    /// Emit the turn's trace. The plaintext query is never persisted: the
    /// trace carries its hash and length (the `RetrievalTrace` contract).
    let emitTrace (turn: Turn) (results: VectorMatch list) (marks: RetrievalStage list) (rerankerName: string option) = async {
        match tracer with
        | None -> ()
        | Some t ->
            turn.Clock.Stop()
            let request = turn.Request

            let trace: RetrievalTrace = {
                QueryHash = ToolUp.RAG.RetrievalTracers.hashQuery request.Query
                QueryLength = if isNull request.Query then 0 else request.Query.Length
                RequestedScopes = request.Scopes
                PermittedScopes = turn.Permitted
                TopK = request.TopK
                AdaptiveK = request.AdaptiveK.IsSome
                CandidatePoolSize = turn.PoolSize
                TopScore =
                    match results with
                    | top :: _ -> top.Score
                    | [] -> 0.0
                DenseUsed = turn.DenseUsed
                SparseUsed = turn.SparseUsed
                RerankerName = rerankerName
                LatencyMs = turn.Clock.ElapsedMilliseconds
                Stages = marks |> List.map RetrievalStage.mark
                ResultCount = results.Length
                StageTimings = turn.Timings
                RewriteDecision = turn.RewriteDecision
                RewrittenQueryHash = turn.RewrittenQueryHash
            }

            do! t.Trace trace turn.Access
    }

    /// Before the vector scopes are read: who may read what, and the facts.
    let front = Stage.compose [ authorise; facts ]

    /// `Retrieve` over the readable scopes: eleven stages composed once.
    let ranked =
        Stage.compose [
            rewrite
            candidates
            filters
            normalise
            rerank
            boosts
            mmr
            truncate
            merge
        ]

    let retrieveScored (request: RetrievalRequest) (ctx: AccessContext) : Async<ScoredPool> = async {
        // Query-size guard. Refuse an over-length query at the contract
        // boundary, before any embedding call: it is almost always a
        // programming bug (an entire document pasted into the query slot).
        // Emit a `KnowledgeQueryRejected` audit (hash + length only) then
        // raise a structured exception naming `MaxQueryChars`.
        let queryChars = if isNull request.Query then 0 else request.Query.Length

        match maxQueryChars with
        | Some limit when queryChars > limit ->
            do! emitQueryRejected request.Query queryChars limit request.Scopes ctx.TeamId
            raise (KnowledgeQueryTooLargeException(queryChars, limit))
        | _ -> ()

        let start =
            Turn.start request ctx (RetrievalScoreSpace.ofComposition sparse.IsSome false)

        let! turn, frontMarks = front start

        if turn.Permitted.IsEmpty then
            // No readable vector scope: resolved facts still surface (fact
            // resolution is independent of KB-scope authorisation).
            do! emitTrace turn turn.Facts frontMarks None
            return ScoredPool.ofMatches composedSpace turn.Facts
        else
            let! turn, rankedMarks = ranked turn
            let marks = frontMarks @ rankedMarks
            let finalOrder = marks |> List.choose RetrievalStage.ordering |> List.last

            let marks =
                marks
                @ [
                    RetrievalStage.FinalOrder finalOrder
                    RetrievalStage.ScoreSpace composedSpace
                ]

            match telemetry with
            | Some t -> t.RecordRetrievalStages turn.Timings
            | None -> ()

            let final = turn.Pool.Matches
            do! emitTrace turn final marks (opts.Reranker |> Option.map _.Name)

            // Resolved facts merge AHEAD of the similarity chunks: they are
            // exact hits, not similarity matches, and bypass every ranking
            // stage. Clause-less ⇒ no facts, so this is the chunks unchanged.
            return {
                turn.Pool with
                    Matches = turn.Facts @ final
                    DenseSimilarity =
                        if turn.DenseUsed then
                            match final |> List.choose (fun m -> turn.DenseSimilarity.TryFind(m.Scope, m.ChunkId)) with
                            | [] -> None
                            | similarities -> Some(List.max similarities)
                        else
                            None
            }
    }

    interface IScoredRetrievalPipeline with
        member _.RetrieveScored request ctx = retrieveScored request ctx

    interface IRetrievalPipeline with

        member _.Retrieve request ctx = async {
            let! pool = retrieveScored request ctx
            return pool.Matches
        }

        member _.Index chunkId chunk scope = async {
            // The write side of the same geometry. A chunk
            // must be embedded under the vocabulary the queries for its
            // scope will be embedded under; indexing everything through
            // one global embedder while retrieval queried per scope would
            // put the two in different sparse spaces and break retrieval
            // outright. `ScopedEmbedding.forScope` is the identity
            // function on every stateless provider, so the byte-identical
            // path is preserved here too (GP 11).
            let scopeEmbedder = ScopedEmbedding.forScope embedder scope
            let! vector = scopeEmbedder.GenerateEmbedding chunk.Content

            let stamped = stampFor scopeEmbedder chunk

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

    // The batched write path. Same stamp and
    // the same sparse upsert as `Index`; the difference is one batched embed
    // (a cache hit after the drainer's warm-up) and one `upsertBatch`, which
    // reaches `IVectorStoreBatch.UpsertBatch` in a single round-trip.
    interface IBatchIndexer with
        member _.SupportsBatch = store :? IVectorStoreBatch

        member _.IndexBatch chunks scope = async {
            match chunks with
            | [] -> ()
            | _ ->
                let scopeEmbedder = ScopedEmbedding.forScope embedder scope
                let! vectors = scopeEmbedder.GenerateEmbeddings(chunks |> List.map (fun (_, c) -> c.Content))

                if vectors.Length <> chunks.Length then
                    failwithf
                        "Embedding provider '%s/%s' returned %d embeddings for %d chunks; refusing the batched write."
                        scopeEmbedder.ProviderId
                        scopeEmbedder.ModelId
                        vectors.Length
                        chunks.Length

                let stamped =
                    chunks
                    |> List.mapi (fun i (chunkId, chunk) -> chunkId, vectors[i], stampFor scopeEmbedder chunk)

                do! upsertBatch store scope stamped

                match sparse with
                | Some idx ->
                    for chunkId, _, chunk in stamped do
                        do! idx.Upsert scope chunkId chunk
                | None -> ()
        }