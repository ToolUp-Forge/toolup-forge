module ToolUp.RAG.Evaluation.EvalTypes

open ToolUp.Platform.VectorKnowledgeTypes

/// A labelled fixture entry. `RelevantChunkIds` is the set of chunk ids that
/// are considered correct for the query. Multiple ids let a query have
/// multiple acceptable answers (recall lifts when any of them lands in the
/// top-K, nDCG rewards higher-ranked relevant matches).
type LabelledQuery = {
    Id: string
    Query: string
    Scopes: VectorScope list
    RelevantChunkIds: Set<string>
    /// Phase 502.E — optional metadata-equality scope for this query, put
    /// straight onto `RetrievalRequest.Filters`. AND-combined, strict
    /// equality, and a chunk MISSING the key does not pass — the semantics
    /// both shipped pipelines are held to.
    ///
    /// `None` (the default, and what a fixture with no `filters` key loads
    /// as) issues exactly the request the harness issued before 502.E, so
    /// every pre-existing fixture measures what it always measured.
    ///
    /// A query that DOES set it is scored twice over: the ordinary
    /// recall/nDCG/MRR metrics say whether the right chunks were still found
    /// *within* the slice, and `QueryResult.FilterViolations` says whether
    /// anything outside the slice came back at all.
    Filters: Map<string, string> option
}

/// A corpus entry — content the harness seeds into the in-memory index
/// before evaluation runs. Mirrors `TextChunk` plus an explicit `ChunkId`
/// and scope so fixtures are self-contained (no external data required).
type CorpusEntry = {
    ChunkId: string
    Content: string
    Scope: VectorScope
    Metadata: Map<string, string>
}

/// One fixture file — a corpus + the queries to evaluate against it. The
/// harness loads, seeds, evaluates, then reports. Multiple files run
/// independently; they are not joined.
type Fixture = {
    Name: string
    Description: string
    Corpus: CorpusEntry list
    Queries: LabelledQuery list
}

/// Per-query evaluation outcome. `Found` lists the chunk ids returned in
/// rank order; `RelevantRanks` is the rank position (1-based) of each
/// relevant id that appeared. Used to compute Recall@K, nDCG@K, MRR.
type QueryResult = {
    QueryId: string
    Query: string
    Found: string list
    RelevantRanks: int list
    LatencyMs: int64
    /// Phase 502.E — chunk ids the pipeline returned that do NOT satisfy
    /// this query's `Filters`. Always empty for an unfiltered query.
    ///
    /// Derived from the metadata on the returned `VectorMatch`es rather
    /// than from the fixture's corpus declaration, so it measures what the
    /// pipeline actually handed back and a fixture cannot mislabel its way
    /// to green. This is the assertion half of "a filtered query returns
    /// only in-filter chunks": recall alone cannot see it, because a
    /// pipeline that ignored the filter entirely still finds every relevant
    /// chunk and scores 1.000.
    FilterViolations: string list
}

/// Aggregated evaluation report. Recall and nDCG are computed at multiple
/// k-values so a single eval run captures coverage and ranking quality at
/// the levels callers tune for. `PerQuery` is the row-level breakdown so
/// regressions can be traced to specific fixtures.
type EvalReport = {
    FixtureName: string
    QueryCount: int
    RecallAt1: float
    RecallAt5: float
    RecallAt10: float
    NdcgAt5: float
    NdcgAt10: float
    Mrr: float
    AvgLatencyMs: float
    /// Phase 502.E — how many queries in this fixture returned at least one
    /// out-of-filter chunk. A non-zero count fails the harness run: unlike a
    /// recall dip (a quality signal, judged against a baseline tolerance),
    /// leaking content the caller asked to exclude is a correctness defect
    /// with no acceptable tolerance.
    ///
    /// An older baseline report deserialises this as `0`, which is the
    /// honest value — it was produced by a run that issued no filters.
    FilterViolationCount: int
    PerQuery: QueryResult list
}