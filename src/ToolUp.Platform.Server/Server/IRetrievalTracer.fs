module ToolUp.Platform.IRetrievalTracer

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

/// Trace payload emitted by `IRetrievalPipeline.Retrieve` after the pipeline
/// has run. Captures a single retrieval call's shape and outcome — enough to
/// reconstruct quality regressions, latency drift, and stage-coverage drops
/// without storing user query text.
///
/// **Privacy contract.** `QueryHash` is the SHA256 hex digest of the raw
/// query string. The plaintext query MUST NOT be persisted by any tracer
/// implementation — even an admin-facing retrieval-trace UI works from the
/// hash so plaintext never leaves request memory. `QueryLength` is exposed
/// so analysts can spot abnormally short / long queries without reading them.
type RetrievalTrace = {
    /// SHA256 hex digest of the raw query string. 64 lowercase hex chars.
    QueryHash: string
    /// Character count of the raw query string. Useful for spotting
    /// degenerate inputs (`""`, single-token queries) without storing text.
    QueryLength: int
    /// Scopes the caller asked the pipeline to search.
    RequestedScopes: VectorScope list
    /// Scopes that survived the access-control filter. A shorter list than
    /// `RequestedScopes` indicates an unauthorised-team request.
    PermittedScopes: VectorScope list
    /// `RetrievalRequest.TopK` — the per-call top-K cap.
    TopK: int
    /// True when the request carried an `AdaptiveKHint`.
    AdaptiveK: bool
    /// Size of the candidate pool fetched from each retriever before fusion
    /// / rerank / MMR / truncation. Larger than `TopK` whenever hybrid search
    /// or rerank is active; equal to `TopK` for the cosine-only path.
    CandidatePoolSize: int
    /// Top match's score after the final stage. `0.0` when the result list
    /// is empty.
    TopScore: float
    /// True when dense (cosine) retrieval contributed to the candidate pool.
    /// Always `true` in the current pipeline; `false` reserved for future
    /// sparse-only optimisations.
    DenseUsed: bool
    /// True when an `ISparseIndex` was registered and contributed.
    SparseUsed: bool
    /// `IReranker.Name` when rerank ran; `None` otherwise.
    RerankerName: string option
    /// Wall-clock latency from pipeline entry to trace emission, in ms.
    LatencyMs: int64
    /// Ordered list of stages that ran. Cardinality and order describe the
    /// realised pipeline shape. Examples: `["AuthoriseScopes"; "Dense"; "TopK"]`
    /// (cosine-only minimum), `["AuthoriseScopes"; "Dense"; "Sparse"; "RRF";
    /// "Rerank"; "MMR"; "AdaptiveK"; "Merge"]` (everything wired). Stage names
    /// are stable; do not rename without considering downstream consumers.
    Stages: string list
    /// Number of matches returned to the caller after every stage. May be
    /// less than `TopK` when retrieval returned a smaller pool or when
    /// `AdaptiveKHint` truncated below the cap.
    ResultCount: int
}

/// Diagnostic payload for the "retrieval was thin" event. Fired when the
/// post-filter result set has fewer than `MissThreshold` matches above
/// `MinScoreThreshold` — the signal that a team's KB is too small / too
/// noisy / off-topic for the queries it's getting. Privacy contract is
/// the same as `RetrievalTrace`: query plaintext is never stored.
type RetrievalMiss = {
    /// SHA256 hex digest of the raw query string. Same hashing as
    /// `RetrievalTrace.QueryHash` so admin UIs can join misses to traces.
    QueryHash: string
    /// Character count of the raw query. Spotting degenerate inputs
    /// (`""`, single-token queries) is a common miss-cluster cause.
    QueryLength: int
    /// Scopes the call ran against (post-authorisation).
    Scopes: VectorScope list
    /// Number of matches scoring above the deployment's `MinScore` threshold.
    /// Below `MissThreshold` (default 2) by definition — this is the value
    /// that classified the call as a miss.
    MatchesAboveMinScore: int
    /// The deployment's configured `MinScore` at the time of the call.
    /// `0.0` when the deployment didn't set one (the gate is "any non-zero
    /// score above zero").
    MinScoreThreshold: float
    /// Top-result score from the raw retrieval (pre-filter). Useful for
    /// distinguishing "nothing matched at all" (TopScore = 0) from
    /// "weak matches dropped by the threshold" (TopScore > 0).
    TopScore: float
}

/// Implementation-agnostic tracer for retrieval emissions. Default
/// implementation is `NoOpRetrievalTracer` (in `ToolUp.RAG`), which discards.
/// The supplied default in Phase 14j is `EventStoreRetrievalTracer`, which
/// writes a `KnowledgeRetrieved` event into the configured `IEventStore` so
/// existing audit-trail / event-replay tooling can read it.
///
/// **Phase 9c portability.** Identity-by-value (`RetrievalTrace` is a record);
/// async at every boundary; no callbacks; stateless between calls; no
/// cross-shard ordering claim.
type IRetrievalTracer =
    /// Emit a per-call trace. Implementations must not throw — failure is
    /// swallowed and logged at most. Tracing must never fail the primary
    /// retrieval.
    abstract Trace: trace: RetrievalTrace -> ctx: AccessContext -> Async<unit>

    /// Emit a miss diagnostic. Fired by `RAGPromptBuilder.withRetrieval`
    /// when fewer than the configured `MissThreshold` matches survive the
    /// `MinScore` filter. Captured separately from `Trace` so admin UIs can
    /// surface "queries that fall through" without scanning every trace.
    /// Implementations must not throw.
    abstract Miss: miss: RetrievalMiss -> ctx: AccessContext -> Async<unit>

/// Reserved event-type literal for `IEventStore` writes from the
/// `EventStoreRetrievalTracer`. Wire-format constant — downstream
/// consumers (admin UIs, eval harnesses) match on this string.
[<Literal>]
let KnowledgeRetrievedEventType = "KnowledgeRetrieved"

/// Reserved event-type literal for `IEventStore` writes from the miss
/// diagnostic path. Pairs with `KnowledgeRetrievedEventType` — admin UIs
/// match on this string to surface "thin retrieval" patterns.
[<Literal>]
let KnowledgeRetrievalMissEventType = "KnowledgeRetrievalMiss"

/// Reserved source-module literal for retrieval-trace events. Mirrors
/// `AuditSourceModule.value`'s `_platform.audit` convention so retrieval
/// traces sit alongside platform audit events under the same event store.
[<Literal>]
let RetrievalTraceSourceModule = "_platform.retrieval"