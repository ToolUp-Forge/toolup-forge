module ToolUp.Platform.ISparseIndex

open ToolUp.Platform.VectorKnowledgeTypes

/// Sparse (lexical) retrieval index. Sibling to `IVectorStore`; together they
/// support hybrid retrieval. Implementations are companion packages under
/// `src/SparseIndices/` (e.g. `ToolUp.SparseIndices.Postgres`, which holds
/// the index in the database so it is the same on every replica); the
/// in-process default is `ToolUp.RAG.InMemoryBM25Index`. A composition picks
/// one with `RAGServerApp.withSparseIndex` / `withAnalyzedSparseIndex`, or
/// none with `withoutSparseIndex` (Phase 893).
///
/// `VectorMatch.Score` is on the implementation's own scale — BM25 for the
/// in-process default (typically [0, ~20]), the database's ranking function
/// for a database-backed one. The retrieval pipeline does not assume scores
/// are bounded, normalised or comparable across implementations —
/// Reciprocal Rank Fusion treats sparse and dense scores rank-wise, not
/// absolutely — so what an implementation changes is the ORDER it ranks in.
///
/// **Ordering.** Results are sorted by score descending, and equal scores by
/// `(Scope, ChunkId)` ascending (`VectorScope`'s structural order, then the
/// ordinal chunk id), so a tie never depends on hash or storage order. The
/// `ISparseIndexContract` pack pins this, with scope isolation and delete
/// semantics, for every implementation.
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

    /// Phase 115 — data-subject erasure over the lexical index. Removes
    /// every chunk in `scope` that names the subject (same matching
    /// contract as `IVectorStore.eraseSubject`: the subject id appears
    /// in `Content` or in any metadata value) and ensures any persisted
    /// snapshot no longer contains the erased text. `dryRun = true`
    /// reports the affected count without deleting. Lexical indexes
    /// have no tombstone tier, so `policy` distinctions collapse to a
    /// hard delete — implementations note this in the returned summary.
    /// Without this member the BM25 leg of hybrid retrieval kept
    /// serving (and persisting at rest) content the vector store had
    /// already erased.
    abstract Erase:
        scope: VectorScope * subjectUserId: string * policy: ToolUp.Platform.ErasurePolicy * dryRun: bool ->
            Async<Result<ToolUp.Platform.ErasureSummary, ToolUp.Platform.ErasureError>>

/// Phase 964 — the keyword index's twin of `IVectorStoreLocality`: where
/// this index's postings live, as the index itself declares it. It reuses
/// `VectorIndexLocality` (the question and its two answers are the same for
/// a lexical index as for a vector index) and is a SEPARATE interface from
/// `ISparseIndex` for the same reason the vector one is separate from
/// `IVectorStore`: the core interface gains no member, and no existing
/// implementation breaks.
///
/// A decorator forwards the declaration of the index it wraps
/// (`member _.IndexLocality = SparseIndexLocality.declared inner`). An index
/// that declares nothing reads as not in-process (GP 11).
type ISparseIndexLocality =
    /// Where this index's postings live: `Some` for an index that knows,
    /// `None` for a decorator over an index that declares nothing.
    abstract IndexLocality: ToolUp.Platform.IVectorStore.VectorIndexLocality option

/// The probe for the Phase 964 keyword-index locality declaration.
[<RequireQualifiedAccess>]
module SparseIndexLocality =
    /// The locality `index` declares, or `None` when it implements no
    /// `ISparseIndexLocality`.
    let declared (index: ISparseIndex) : ToolUp.Platform.IVectorStore.VectorIndexLocality option =
        match box index with
        | :? ISparseIndexLocality as declaring -> declaring.IndexLocality
        | _ -> None

    /// `true` only when `index` declares its postings `InProcess`. An
    /// undeclared index is `false` (see `ISparseIndexLocality`).
    let isInProcess (index: ISparseIndex) : bool =
        declared index = Some ToolUp.Platform.IVectorStore.VectorIndexLocality.InProcess