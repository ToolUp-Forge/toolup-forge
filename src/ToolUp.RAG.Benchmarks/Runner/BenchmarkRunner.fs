module ToolUp.RAG.Benchmarks.BenchmarkRunner

open System
open System.Diagnostics
open System.IO
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.RAG.Benchmarks.EvalCore
open ToolUp.RAG.Benchmarks.BeirTypes
open ToolUp.RAG.Benchmarks.BeirLoader
open ToolUp.RAG.Benchmarks.LatencyMetrics
open ToolUp.RAG.Benchmarks.PipelineFactory

// ─── Run config + output row ─────────────────────────────────────

/// One cell in the embedder × vector-store × MMR × topK matrix.
type RunConfig = {
    DatasetName: string
    Build: BuildOptions
    TopK: int
    Replicate: int
}

/// One CSV row. Column order matches `csvHeader` in `ReportWriter.fs`.
type RunRow = {
    Dataset: string
    Embedder: string
    VectorStore: string
    Reranker: string
    Mmr: string
    TopK: int
    Replicate: int
    QueriesEvaluated: int
    NdcgAt10: float
    RecallAt100: float
    MrrAt10: float
    IngestSeconds: float
    QueryP50Ms: int64
    QueryP95Ms: int64
    QueryP99Ms: int64
    WallClockSeconds: float
}

// ─── Per-query orchestration ─────────────────────────────────────

/// Run one query against the pipeline at the configured TopK and capture
/// rank + latency. Replicates the eval project's private `runQuery` shape
/// (`RetrievalEval.fs`) but parameterised by TopK — the eval helper
/// hard-codes 10, which would cap `Recall@100` at 10 and make the metric
/// useless for benchmarks.
let private runQuery (pipeline: IRetrievalPipeline) (topK: int) (q: LabelledQuery) : Async<QueryResult> = async {
    let request: RetrievalRequest = {
        Query = q.Query
        Scopes = q.Scopes
        TopK = topK
        Merge = Interleaved
        Filters = None
        History = None
        AdaptiveK = None
        OriginFilter = None
        ActiveModule = None
        FactClause = None
    }

    let sw = Stopwatch.StartNew()
    let! matches = pipeline.Retrieve request evalContext
    sw.Stop()

    let relevantRanks =
        matches
        |> List.mapi (fun i m ->
            if q.RelevantChunkIds.Contains m.ChunkId then
                Some(i + 1)
            else
                None)
        |> List.choose id

    return {
        QueryId = q.Id
        Query = q.Query
        Found = matches |> List.map _.ChunkId
        RelevantRanks = relevantRanks
        LatencyMs = sw.ElapsedMilliseconds
    }
}

// ─── Top-level run ───────────────────────────────────────────────

let private embedderLabel =
    function
    | Local -> "local"
    | OpenAI -> "openai"

let private vectorStoreLabel =
    function
    | Flat -> "flat"
    | Hnsw -> "hnsw"

let private mmrLabel (opts: BuildOptions) =
    if opts.EnableMmr then
        sprintf "λ=%.2f" opts.MmrLambda
    else
        "off"

/// Execute one benchmark cell: load BEIR dataset, build pipeline, seed
/// corpus (timed), run every labelled query (timed), aggregate metrics.
/// In replicate-N mode the qrels are empty by construction, so quality
/// columns come back as zero — that's the documented contract for
/// stress-test runs.
let runOne (cfg: RunConfig) : Async<RunRow> = async {
    let wallClock = Stopwatch.StartNew()

    // 1. Load BEIR dataset (download + parse, both cached).
    let! dataset = BeirLoader.load cfg.DatasetName

    let dataset =
        if cfg.Replicate > 1 then
            BeirLoader.replicate cfg.Replicate dataset
        else
            dataset

    let fixture = BeirLoader.toFixture dataset

    // 2. Per-run isolated storage so successive runs don't share a vector
    //    index — every benchmark cell scores from a clean slate.
    let tempDir =
        Path.Combine(Path.GetTempPath(), "rag-bench-" + Guid.NewGuid().ToString("N"))

    try
        let pipeline = PipelineFactory.build tempDir cfg.Build

        // 3. Seed corpus, timed.
        let ingestSw = Stopwatch.StartNew()
        do! seedCorpus pipeline fixture.Corpus
        ingestSw.Stop()

        // 4. Run queries at our chosen TopK.
        let mutable results = []

        for q in fixture.Queries do
            let! r = runQuery pipeline cfg.TopK q
            results <- r :: results

        let perQuery = List.rev results

        // 5. Aggregate. Quality metrics are suppressed in replicate mode
        //    because qrels are empty — all `RelevantRanks` are `[]`, so
        //    Recall/nDCG/MRR are 0.0 by definition. Setting them
        //    explicitly to 0.0 documents the intent.
        let isReplicate = cfg.Replicate > 1

        let ndcg = if isReplicate then 0.0 else ndcgAt 10 perQuery
        let recall = if isReplicate then 0.0 else recallAt 100 perQuery
        let mrr = if isReplicate then 0.0 else mrrAt 10 perQuery

        let latency = profile (perQuery |> List.map _.LatencyMs)

        wallClock.Stop()

        return {
            Dataset = cfg.DatasetName
            Embedder = embedderLabel cfg.Build.Embedder
            VectorStore = vectorStoreLabel cfg.Build.VectorStore
            Reranker = "off"
            Mmr = mmrLabel cfg.Build
            TopK = cfg.TopK
            Replicate = cfg.Replicate
            QueriesEvaluated = perQuery.Length
            NdcgAt10 = ndcg
            RecallAt100 = recall
            MrrAt10 = mrr
            IngestSeconds = ingestSw.Elapsed.TotalSeconds
            QueryP50Ms = latency.P50
            QueryP95Ms = latency.P95
            QueryP99Ms = latency.P99
            WallClockSeconds = wallClock.Elapsed.TotalSeconds
        }
    finally
        try
            Directory.Delete(tempDir, recursive = true)
        with _ ->
            ()
}