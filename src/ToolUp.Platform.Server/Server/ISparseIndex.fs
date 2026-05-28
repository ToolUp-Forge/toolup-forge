module ToolUp.Platform.ISparseIndex

open ToolUp.Platform.VectorKnowledgeTypes

/// Sparse (lexical) retrieval index. Sibling to `IVectorStore`; together they
/// support hybrid retrieval. Implementations are companion packages under
/// `src/SparseIndices/` (e.g., a Tantivy-backed companion); the in-process
/// default is `ToolUp.RAG.InMemoryBM25Index`.
///
/// Returns `VectorMatch.Score` on a BM25-style scale (typically [0, ~20]).
/// The retrieval pipeline does not assume scores are bounded or normalised
/// before fusion — Reciprocal Rank Fusion treats sparse and dense scores
/// rank-wise, not absolutely.
///
/// Scope semantics are identical to `IVectorStore`: chunks in one scope are
/// never accessible from another. `IRetrievalPipeline` validates `AccessContext`
/// before any call here; direct callers are responsible for their own access
/// enforcement.
///
/// Satisfies Phase 9c portability rules:
/// 1. Identity by value — `chunkId: string`, `scope: VectorScope`
/// 2. Async at every boundary
/// 3. No callback / supervision hooks
/// 4. Stateless between calls (each operation parameterised fully)
/// 5. No cross-scope ordering promises (results ranked by score within search)
/// 6. Precision: term-level lexical matching, deterministic given the same corpus
type ISparseIndex =
    /// Index or replace a chunk's lexical representation. Idempotent on
    /// `(scope, chunkId)`. The implementation tokenises `chunk.Content`;
    /// the caller does not pre-tokenise.
    abstract Upsert: scope: VectorScope -> chunkId: string -> chunk: TextChunk -> Async<unit>

    /// Find the highest-scoring chunks for a free-text query within the
    /// given scopes. Results are returned in descending BM25 score order.
    /// Implementations should cap the candidate pool to `topK`.
    abstract Search: scopes: VectorScope list -> query: string -> topK: int -> Async<VectorMatch list>

    /// Delete all chunks belonging to `scope`.
    abstract DeleteByScope: scope: VectorScope -> Async<unit>

    /// Delete a single chunk by its stable id within a scope.
    abstract DeleteChunk: scope: VectorScope -> chunkId: string -> Async<unit>