/// Inlined subset of `ToolUp.RAG.Evaluation` reused by the benchmark runner.
/// Living here rather than via project reference avoids the dual-injection
/// problem: both the eval project and the benchmark project import
/// `ToolUp.Platform.Server.props`, so each compiles its own copy of
/// `IRetrievalPipeline.IRetrievalPipeline`. Referencing the eval project
/// would surface two CLR types with the same name and the runner couldn't
/// hand its pipeline to `seedCorpus`.
///
/// Symbols mirror the upstream definitions one-to-one — keep them in sync.
/// If `Metrics.recallAt` / `Metrics.ndcgAt` change shape upstream, change
/// them here too.
module ToolUp.RAG.Benchmarks.EvalCore

open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline

// ─── Types (mirrors `ToolUp.RAG.Evaluation.EvalTypes`) ──────────

type LabelledQuery = {
    Id: string
    Query: string
    Scopes: VectorScope list
    RelevantChunkIds: Set<string>
}

type CorpusEntry = {
    ChunkId: string
    Content: string
    Scope: VectorScope
    Metadata: Map<string, string>
}

type Fixture = {
    Name: string
    Description: string
    Corpus: CorpusEntry list
    Queries: LabelledQuery list
}

type QueryResult = {
    QueryId: string
    Query: string
    Found: string list
    RelevantRanks: int list
    LatencyMs: int64
}

// ─── Metrics (mirrors `ToolUp.RAG.Evaluation.Metrics`) ──────────

let recallAt (k: int) (results: QueryResult list) : float =
    if results.IsEmpty then
        0.0
    else
        let hits =
            results
            |> List.sumBy (fun r ->
                if r.RelevantRanks |> List.exists (fun rank -> rank <= k) then
                    1.0
                else
                    0.0)

        hits / float results.Length

let ndcgAt (k: int) (results: QueryResult list) : float =
    if results.IsEmpty then
        0.0
    else
        let perQuery (r: QueryResult) : float =
            let relevantInTop = r.RelevantRanks |> List.filter (fun rank -> rank <= k)

            if relevantInTop.IsEmpty then
                0.0
            else
                let dcg =
                    relevantInTop
                    |> List.sumBy (fun rank -> 1.0 / System.Math.Log2(float rank + 1.0))

                let totalRelevant = r.RelevantRanks.Length
                let idealK = min totalRelevant k

                let idealDcg =
                    [ 1..idealK ]
                    |> List.sumBy (fun rank -> 1.0 / System.Math.Log2(float rank + 1.0))

                if idealDcg = 0.0 then 0.0 else dcg / idealDcg

        results |> List.averageBy perQuery

let mrrAt (k: int) (results: QueryResult list) : float =
    if results.IsEmpty then
        0.0
    else
        let perQuery (r: QueryResult) : float =
            let withinK = r.RelevantRanks |> List.filter (fun rank -> rank <= k)

            match withinK with
            | [] -> 0.0
            | ranks -> 1.0 / float (List.min ranks)

        results |> List.averageBy perQuery

// ─── Pipeline harness (mirrors `RetrievalEval.evalContext` + `seedCorpus`) ──

/// Anonymous-mode access context — passes Platform / Deployment scopes,
/// filters Team scopes. BEIR has no team semantics so everything goes
/// under Deployment and this context retrieves cleanly.
let evalContext: AccessContext =
    AccessContext.unrestricted (Subject.AnonymousSession "bench-harness")

/// Index every corpus entry under its declared scope and chunk id. Done
/// once per benchmark cell, before the query loop. Errors propagate —
/// silent ingest failures would distort metrics.
let seedCorpus (pipeline: IRetrievalPipeline) (corpus: CorpusEntry list) : Async<unit> = async {
    for entry in corpus do
        let chunk: TextChunk = {
            Content = entry.Content
            Metadata = entry.Metadata
        }

        do! pipeline.Index entry.ChunkId chunk entry.Scope
}