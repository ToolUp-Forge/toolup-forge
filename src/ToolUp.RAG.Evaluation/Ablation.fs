/// Phase 122 — fixture-driven ablation gate over retrieval-pipeline
/// composition. Runs the same BEIR-derived subset through four pipeline
/// configurations (dense-only / dense+BM25 / dense+BM25+reranker /
/// HNSW-vs-flat), reports Recall@K + nDCG@K per configuration, and fails
/// (non-zero exit) on:
///   (i)  a per-configuration metric-floor breach (floors pinned below,
///        tolerance baked into the pin), or
///   (ii) non-deterministic ordering across two fresh-build runs of the
///        same configuration — the class-wide net for tie-break/ordering
///        drift in any `IVectorStore` / `ISparseIndex` companion (GP 9:
///        silent quality drift is a silent default).
/// Also asserts the Phase 122 stage-timing surfaces: traces carry the
/// expected `StageTimings` entries per configuration, and a deliberately
/// slowed reranker double is identifiable from the `/health/rag` snapshot
/// shape alone (per-stage P50).
module ToolUp.RAG.Evaluation.Ablation

open System
open System.IO
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IReranker
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.IRagTelemetry
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.RetrievalPipeline
open ToolUp.RAG.VectorStores.Hnsw
open ToolUp.RAG.Evaluation.EvalTypes

// ─── Deterministic reranker double ───────────────────────────────

let private tokenPattern = Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)

let private tokenSet (text: string) : Set<string> =
    if String.IsNullOrEmpty text then
        Set.empty
    else
        [
            for m in tokenPattern.Matches(text) do
                m.Value.ToLowerInvariant()
        ]
        |> Set.ofList

/// Deterministic `IReranker` double: scores each candidate by query/content
/// token-set Jaccard overlap — a cheap stand-in for a cross-encoder that
/// (a) is a pure function of the data, (b) genuinely reorders the fused
/// pool, so the ablation measures the Rerank stage's effect on metrics and
/// ordering. `delayMs > 0` makes every call artificially slow — the
/// "deliberately slowed reranker" of the Phase 122 acceptance criteria.
type LexicalOverlapReranker(?delayMs: int) =
    let delay = defaultArg delayMs 0

    interface IReranker with
        member _.Name = "lexical-overlap-double"
        member _.MaxBatchSize = 64

        member _.Rerank query candidates = async {
            if delay > 0 then
                do! Async.Sleep delay

            let queryTokens = tokenSet query

            let jaccard (a: Set<string>) (b: Set<string>) =
                if a.IsEmpty && b.IsEmpty then
                    0.0
                else
                    let union = Set.union a b |> Set.count |> float

                    if union = 0.0 then
                        0.0
                    else
                        (Set.intersect a b |> Set.count |> float) / union

            return
                candidates
                |> List.map (fun m -> {
                    m with
                        Score = jaccard queryTokens (tokenSet m.Content)
                })
                // Total-order sort — equal overlaps are common (the duplicate
                // distractors tie by construction), so tie-break on the unique
                // (Scope, ChunkId) like every in-tree ranked-list producer.
                |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
        }

// ─── Capturing tracer ────────────────────────────────────────────

/// `IRetrievalTracer` that collects every trace in memory so the gate can
/// assert on `StageTimings` shape after a run.
type CapturingTracer() =
    let traces = ResizeArray<RetrievalTrace>()
    member _.Traces = traces |> List.ofSeq

    interface IRetrievalTracer with
        member _.Trace trace _ =
            traces.Add trace
            async.Return()

        member _.Miss _ _ = async.Return()

// ─── Configurations ──────────────────────────────────────────────

type StoreKind =
    | FlatStore
    | HnswStore

type AblationConfig = {
    Name: string
    Sparse: bool
    Reranker: bool
    Store: StoreKind
    /// Stage names expected in every trace's `StageTimings` for this
    /// configuration (the timed subset; bookkeeping stages excluded).
    ExpectedTimedStages: Set<string>
}

/// The four ablation cells. Order is fixed — the report prints in this
/// order and the floor table below keys by `Name`.
let configurations = [
    {
        Name = "dense-only"
        Sparse = false
        Reranker = false
        Store = FlatStore
        ExpectedTimedStages = set [ "Dense"; "Merge" ]
    }
    {
        Name = "dense+bm25"
        Sparse = true
        Reranker = false
        Store = FlatStore
        ExpectedTimedStages = set [ "Dense"; "Sparse"; "RRF"; "Merge" ]
    }
    {
        Name = "dense+bm25+reranker"
        Sparse = true
        Reranker = true
        Store = FlatStore
        ExpectedTimedStages = set [ "Dense"; "Sparse"; "RRF"; "Rerank"; "Merge" ]
    }
    {
        Name = "hnsw-dense+bm25"
        Sparse = true
        Reranker = false
        Store = HnswStore
        ExpectedTimedStages = set [ "Dense"; "Sparse"; "RRF"; "Merge" ]
    }
]

/// Per-configuration metric floors, `(Recall@10, nDCG@10)`. Pinned from
/// the values observed on the deterministic scifact subset (2026-06-12:
/// dense-only 0.125/0.066, dense+bm25 0.583/0.321, dense+bm25+reranker
/// 0.875/0.764, hnsw-dense+bm25 0.542/0.309) minus a 0.05 absolute
/// tolerance — generous enough to absorb a legitimate embedder / fixture
/// tweak (which re-pins these in the same PR, like the rag-eval baseline),
/// tight enough that a real composition regression (RRF-weighting drift, a
/// reranker that stops reordering, a store that drops results) breaches
/// loudly. The absolute numbers are not a quality bar — the local TF-IDF
/// embedder is a smoke-grade dense signal (hence dense-only's low floor);
/// the gate guards the *differential* shape: hybrid lifts dense-only,
/// rerank lifts hybrid, HNSW tracks flat.
let metricFloors =
    Map [
        "dense-only", (0.07, 0.01)
        "dense+bm25", (0.53, 0.27)
        "dense+bm25+reranker", (0.82, 0.71)
        "hnsw-dense+bm25", (0.49, 0.25)
    ]

// ─── Pipeline construction ───────────────────────────────────────

/// Build one fresh, fully-isolated pipeline for an ablation run. Mirrors
/// `Program.buildPipeline` but parameterised by configuration; every run
/// gets its own `tempDir` so no state leaks between runs (fresh stores are
/// what make the two-run determinism check meaningful).
let private buildPipeline
    (tempDir: string)
    (config: AblationConfig)
    (tracer: IRetrievalTracer)
    (telemetry: IRagTelemetry option)
    (rerankerDelayMs: int)
    : IRetrievalPipeline =
    Directory.CreateDirectory tempDir |> ignore

    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> BlobStorage.IBlobStorage

    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    let embedder: IEmbeddingProvider = LocalEmbeddingProvider.create ()

    let vectorStore: IVectorStore =
        match config.Store with
        | FlatStore -> new InMemoryVectorStore(storage, logger, flushIntervalMs = 50) :> IVectorStore
        | HnswStore -> HnswVectorStore.create storage (Some logger)

    let sparseIndex: ISparseIndex option =
        if config.Sparse then
            Some(new InMemoryBM25Index(storage, logger, flushIntervalMs = 50) :> ISparseIndex)
        else
            None

    let options = {
        RetrievalPipelineOptions.defaults with
            Reranker =
                if config.Reranker then
                    Some(LexicalOverlapReranker(delayMs = rerankerDelayMs) :> IReranker)
                else
                    None
    }

    match sparseIndex with
    | Some sparse ->
        RetrievalPipeline(
            store = vectorStore,
            embedder = embedder,
            sparseIndex = sparse,
            options = options,
            tracer = tracer,
            ?telemetry = telemetry
        )
        :> IRetrievalPipeline
    | None ->
        RetrievalPipeline(
            store = vectorStore,
            embedder = embedder,
            options = options,
            tracer = tracer,
            ?telemetry = telemetry
        )
        :> IRetrievalPipeline

// ─── Gate ────────────────────────────────────────────────────────

type private ConfigOutcome = {
    Config: AblationConfig
    Report: EvalReport
    Traces: RetrievalTrace list
    /// `(queryId, rankedChunkIds)` from the second fresh-build run —
    /// compared against the first run's for the determinism assertion.
    SecondRunOrdering: (string * string list) list
}

let private runOnce
    (fixture: Fixture)
    (config: AblationConfig)
    (telemetry: IRagTelemetry option)
    (rerankerDelayMs: int)
    : Async<EvalReport * RetrievalTrace list> =
    async {
        let tempDir =
            Path.Combine(Path.GetTempPath(), "rag-ablation-" + Guid.NewGuid().ToString("N"))

        try
            let tracer = CapturingTracer()

            let pipeline =
                buildPipeline tempDir config (tracer :> IRetrievalTracer) telemetry rerankerDelayMs

            let! report = RetrievalEval.evaluate pipeline fixture
            return report, tracer.Traces
        finally
            try
                Directory.Delete(tempDir, recursive = true)
            with _ ->
                ()
    }

let private ordering (report: EvalReport) =
    report.PerQuery |> List.map (fun q -> q.QueryId, q.Found)

/// Two fresh-build runs with the identical seed order: catches run-to-run
/// enumeration nondeterminism in any ranked-list producer (Dictionary /
/// hash-order leakage, parallelism, time-dependent scoring) end-to-end
/// across the whole pipeline composition.
///
/// **Scope note.** Insertion-order dependence with a *fixed* insert
/// sequence (a score-only stable sort over tied candidates — the HNSW
/// tie-break gap, Investigate-gaps 2026-06-12 Gap 7) is NOT triggerable
/// from this harness: the local embedder is an incremental TF-IDF, so
/// even exact-content duplicates get distinct vectors (first occurrence
/// embeds against different df state than the second) and never tie in
/// the vector store. That class is owned by the store-level
/// ordering-contract test in the TIDY-UP "RAG retrieval + ingestion edge
/// guards" bundle (hand-crafted equal vectors against each `IVectorStore`
/// / `ISparseIndex` impl); this gate complements it, not replaces it. The
/// duplicate distractors stay in the fixture — they do produce genuine
/// BM25 ties (identical tf / doc length), so the sparse index's
/// tie-break is exercised here.
let private runConfig (fixture: Fixture) (config: AblationConfig) : Async<ConfigOutcome> = async {
    let! report, traces = runOnce fixture config None 0
    let! secondReport, _ = runOnce fixture config None 0

    return {
        Config = config
        Report = report
        Traces = traces
        SecondRunOrdering = ordering secondReport
    }
}

/// Run the full gate. Returns the process exit code: `0` when every
/// assertion held, `1` otherwise (all failures are reported before
/// exiting — one run surfaces every breach, not just the first).
let run (datasetName: string) : Async<int> = async {
    let failures = ResizeArray<string>()
    let warnings = ResizeArray<string>()

    let! fixture = BeirSubset.loadFixture datasetName BeirSubset.defaultSpec
    printfn "[ablation] %s" fixture.Description
    printfn ""

    let! outcomeArray = configurations |> List.map (runConfig fixture) |> Async.Sequential
    let outcomes = outcomeArray |> List.ofArray

    printfn "═══ Ablation report: %s ═══" fixture.Name
    printfn "  %-22s %10s %10s %10s  %s" "configuration" "Recall@10" "nDCG@10" "MRR" "deterministic"

    for outcome in outcomes do
        let report = outcome.Report
        let deterministic = ordering report = outcome.SecondRunOrdering

        printfn
            "  %-22s %10.3f %10.3f %10.3f  %s"
            outcome.Config.Name
            report.RecallAt10
            report.NdcgAt10
            report.Mrr
            (if deterministic then "yes" else "NO")

        // (i) Metric floors.
        match metricFloors.TryFind outcome.Config.Name with
        | None ->
            failures.Add(sprintf "%s: no metric floor pinned — add it to Ablation.metricFloors" outcome.Config.Name)
        | Some(recallFloor, ndcgFloor) ->
            if report.RecallAt10 < recallFloor then
                failures.Add(
                    sprintf
                        "%s: Recall@10 %.3f breached the %.3f floor"
                        outcome.Config.Name
                        report.RecallAt10
                        recallFloor
                )

            if report.NdcgAt10 < ndcgFloor then
                failures.Add(
                    sprintf "%s: nDCG@10 %.3f breached the %.3f floor" outcome.Config.Name report.NdcgAt10 ndcgFloor
                )

        // (ii) Ordering determinism across two fresh-build runs.
        if not deterministic then
            failures.Add(
                sprintf
                    "%s: ranked output differed across two fresh-build runs of the identical configuration — tie-break/ordering drift in a ranked-list producer (cf. TIDY-UP \"RAG retrieval + ingestion edge guards\", HNSW tie-break item)"
                    outcome.Config.Name
            )

        // Stage-timing shape (Phase 122 slice 1): every trace carries the
        // configuration's expected timed stages.
        let badTraces =
            outcome.Traces
            |> List.filter (fun t ->
                (t.StageTimings |> List.map fst |> Set.ofList)
                <> outcome.Config.ExpectedTimedStages)

        if not badTraces.IsEmpty then
            let sample = badTraces.Head.StageTimings |> List.map fst |> String.concat ", "

            failures.Add(
                sprintf
                    "%s: %d/%d traces carried unexpected StageTimings (expected {%s}, sample [%s])"
                    outcome.Config.Name
                    badTraces.Length
                    outcome.Traces.Length
                    (outcome.Config.ExpectedTimedStages |> String.concat ", ")
                    sample
            )

    // Cross-configuration sanity: hybrid retrieval should not collapse
    // below dense-only — the regression RRF-weighting drift produces.
    let recallOf name =
        outcomes
        |> List.tryFind (fun o -> o.Config.Name = name)
        |> Option.map _.Report.RecallAt10

    match recallOf "dense-only", recallOf "dense+bm25" with
    | Some dense, Some hybrid when hybrid < dense - 0.10 ->
        failures.Add(
            sprintf
                "dense+bm25 Recall@10 (%.3f) fell >0.10 below dense-only (%.3f) — fusion is hurting retrieval"
                hybrid
                dense
        )
    | _ -> ()

    // Slowed-reranker identification (Phase 122 acceptance): with a 40 ms
    // delay injected into the reranker double and live telemetry attached,
    // the bottleneck stage must be identifiable from the snapshot alone —
    // Rerank's P50 strictly dominates every other stage's.
    let rerankerConfig =
        configurations |> List.find (fun c -> c.Name = "dense+bm25+reranker")

    let telemetry = ToolUp.RAG.RagTelemetry.createDefault ()
    let! _ = runOnce fixture rerankerConfig (Some telemetry) 40
    let! snapshot = telemetry.Snapshot()

    match snapshot.RetrievalStageP50Ms |> List.sortByDescending snd with
    | ("Rerank", rerankP50) :: rest when rerankP50 >= 40.0 ->
        let runnerUp =
            match rest with
            | (stage, ms) :: _ -> sprintf "%s at %.1f ms" stage ms
            | [] -> "none"

        printfn ""
        printfn "  slowed-reranker check: Rerank P50 %.1f ms dominates (runner-up: %s) ✓" rerankP50 runnerUp
    | top ->
        let shape =
            top |> List.map (fun (s, ms) -> sprintf "%s=%.1f" s ms) |> String.concat ", "

        failures.Add(
            sprintf
                "slowed-reranker check: Rerank P50 did not dominate the /health/rag per-stage snapshot (got [%s])"
                shape
        )

    printfn ""

    for w in warnings do
        printfn "⚠ %s" w

    if failures.Count = 0 then
        printfn "✓ Ablation gate passed (%d configurations)" outcomes.Length
        return 0
    else
        for f in failures do
            eprintfn "✗ %s" f

        return 1
}