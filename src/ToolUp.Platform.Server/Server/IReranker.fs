module ToolUp.Platform.IReranker

open ToolUp.Platform.VectorKnowledgeTypes

/// Cross-encoder reranking. A reranker takes a query and a candidate pool
/// (typically the fused top-K from dense + sparse retrieval) and returns
/// the candidates reordered by a higher-fidelity scoring function. Unlike
/// retrieval — which scores each document independently against a query
/// embedding — a cross-encoder attends to (query, document) pairs jointly,
/// producing scores that correlate better with relevance at a higher
/// per-call cost.
///
/// Implementations live in companion packages under `src/Rerankers/`.
/// `src/Rerankers/Local/` ships a CPU-bound ONNX cross-encoder; deployments
/// wanting a paid API (Cohere, Voyage) drop in additional companions
/// without any change to the SDK.
///
/// Satisfies Phase 9c portability rules:
/// 1. Identity by value — query is `string`; candidates carry their own
///    `(scope, chunkId)` identity from `VectorMatch`.
/// 2. Async at every boundary — `Rerank` returns `Async<_>`.
/// 3. No callback / supervision hooks.
/// 4. Stateless between calls — each `Rerank` parameterised fully; no
///    assumed mutable state (model weights are immutable infrastructure,
///    not state).
/// 5. No cross-call ordering promises — each call is independent.
/// 6. Precision: scores are model-dependent; the reranker contract only
///    promises a relative ordering, not a calibrated scale.
type IReranker =
    /// Stable identifier for trace/observability — e.g. "local-mxbai-xsmall",
    /// "cohere-rerank-3". Used by the eval harness (Phase 14j) to attribute
    /// quality lifts to a specific reranker; never serialised on the wire.
    abstract Name: string

    /// Maximum candidates the reranker will accept in a single call. Callers
    /// truncate before calling; implementations may also fail loudly if
    /// exceeded. Typical values: 32 (small CPU model), 1000 (paid API).
    abstract MaxBatchSize: int

    /// Reorder `candidates` by joint (query, document) relevance. The
    /// returned list is the same set of candidates with new `Score` values
    /// reflecting the cross-encoder output, sorted descending.
    ///
    /// Implementations must:
    /// - Preserve every input candidate (no filtering — that's the caller's
    ///   job via `topK`).
    /// - Replace `Score` with the cross-encoder score (callers that want
    ///   the original retrieval score can hold their own copy).
    /// - Honour `MaxBatchSize` — callers that pass more than `MaxBatchSize`
    ///   should expect either truncation or an exception, depending on the
    ///   implementation's contract.
    abstract Rerank: query: string -> candidates: VectorMatch list -> Async<VectorMatch list>