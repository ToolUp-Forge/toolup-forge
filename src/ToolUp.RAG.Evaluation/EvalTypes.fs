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
    PerQuery: QueryResult list
}