module ToolUp.RAG.Evaluation.RetrievalEval

open System.Diagnostics
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.RAG.Evaluation.EvalTypes

/// Anonymous-user access context — eval queries against `Platform` and
/// `Deployment` scopes pass; `Team` scopes are filtered out by the
/// pipeline's `authorisedScopes`. Fixtures should use `Deployment` (or
/// expose a team id matching `ctx.TeamId`) to match meaningfully.
let evalContext: AccessContext =
    AccessContext.unrestricted (Subject.AnonymousSession "eval-harness")

/// Seed a pipeline with the corpus entries — `Index` each chunk under its
/// declared scope and chunkId. Done before evaluation so all queries see
/// the same index. Ingestion failures are propagated; a failing seed
/// invalidates the eval and should not be silently masked.
let seedCorpus (pipeline: IRetrievalPipeline) (corpus: CorpusEntry list) : Async<unit> = async {
    for entry in corpus do
        let chunk: TextChunk = {
            Content = entry.Content
            Metadata = entry.Metadata
        }

        do! pipeline.Index entry.ChunkId chunk entry.Scope
}

let private rankRelevant (relevant: Set<string>) (matches: VectorMatch list) : int list =
    matches
    |> List.mapi (fun i m -> if relevant.Contains m.ChunkId then Some(i + 1) else None)
    |> List.choose id

let private runQuery (pipeline: IRetrievalPipeline) (q: LabelledQuery) : Async<QueryResult> = async {
    let request: RetrievalRequest = {
        Query = q.Query
        Scopes = q.Scopes
        TopK = 10
        Merge = Interleaved
        Filters = None
        History = None
        AdaptiveK = None
        OriginFilter = None
        ActiveModule = None
    }

    let sw = Stopwatch.StartNew()
    let! matches = pipeline.Retrieve request evalContext
    sw.Stop()

    return {
        QueryId = q.Id
        Query = q.Query
        Found = matches |> List.map _.ChunkId
        RelevantRanks = rankRelevant q.RelevantChunkIds matches
        LatencyMs = sw.ElapsedMilliseconds
    }
}

/// Evaluate a fixture against a pipeline. Seeds the corpus, runs every
/// query at TopK=10, computes Recall@{1,5,10} / nDCG@{5,10} / MRR, and
/// returns the report. The pipeline is expected to be otherwise empty —
/// noise in the index from prior runs distorts metrics.
let evaluate (pipeline: IRetrievalPipeline) (fixture: Fixture) : Async<EvalReport> = async {
    do! seedCorpus pipeline fixture.Corpus

    let mutable results = []

    for q in fixture.Queries do
        let! r = runQuery pipeline q
        results <- r :: results

    return Metrics.buildReport fixture.Name (List.rev results)
}

/// Compare two reports and decide whether the second is a regression. A
/// regression is `>tolerance` worse on Recall@5 *or* nDCG@10 — the two
/// metrics that map most directly to user-visible RAG quality. Default
/// tolerance is 5% absolute. Returns `Ok` (no regression) or `Error`
/// describing which metric fell.
let detectRegression (tolerance: float) (baseline: EvalReport) (candidate: EvalReport) =
    let recallDelta = candidate.RecallAt5 - baseline.RecallAt5
    let ndcgDelta = candidate.NdcgAt10 - baseline.NdcgAt10

    if recallDelta < -tolerance then
        Error(
            sprintf
                "Recall@5 regressed by %.3f (baseline %.3f, candidate %.3f)"
                -recallDelta
                baseline.RecallAt5
                candidate.RecallAt5
        )
    elif ndcgDelta < -tolerance then
        Error(
            sprintf
                "nDCG@10 regressed by %.3f (baseline %.3f, candidate %.3f)"
                -ndcgDelta
                baseline.NdcgAt10
                candidate.NdcgAt10
        )
    else
        Ok()