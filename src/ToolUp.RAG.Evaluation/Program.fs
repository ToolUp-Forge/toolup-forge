module ToolUp.RAG.Evaluation.Program

open System
open System.IO
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.RetrievalPipeline
open ToolUp.RAG.RetrievalTracers
open ToolUp.RAG.Evaluation.EvalTypes

// ─── Wiring helpers ───────────────────────────────────────────────

/// Build a fresh in-memory pipeline rooted at `tempDir`. Each invocation
/// gets its own `LocalFileStorage` directory so eval runs are independent
/// — the BM25/vector indices don't leak across fixtures.
let private buildPipeline (tempDir: string) : IRetrievalPipeline =
    Directory.CreateDirectory tempDir |> ignore

    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> BlobStorage.IBlobStorage

    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    let embedder = LocalEmbeddingProvider.create ()

    // Disable debounced flush in eval — every Index call should be visible
    // to the next Retrieve without waiting on a 2-second timer.
    let vectorStore = new InMemoryVectorStore(storage, logger, flushIntervalMs = 50)

    let sparseIndex = new InMemoryBM25Index(storage, logger, flushIntervalMs = 50)

    let tracer = createNoOp ()

    // The smoke fixture is entirely `Platform`-scoped. `authorisedScopes`
    // filters `Platform` out unless the Platform KB is enabled, and the
    // constructor defaults to `NoPlatformKnowledgeBase` — so without this
    // the pipeline early-returns `[]` for every query and the harness
    // scores 0.000 (the regression this restores: the eval is a
    // measurement tool, it must enable the KB scope its fixture lives in).
    RetrievalPipeline(
        store = vectorStore,
        embedder = embedder,
        sparseIndex = sparseIndex,
        tracer = tracer,
        platformKnowledgeBase = EnabledPlatformKnowledgeBase
    )
    :> IRetrievalPipeline

// ─── Reporting ────────────────────────────────────────────────────

let private printReport (report: EvalReport) =
    printfn ""
    printfn "═══ Eval report: %s ═══" report.FixtureName
    printfn "  Queries:        %d" report.QueryCount
    printfn "  Recall@1:       %.3f" report.RecallAt1
    printfn "  Recall@5:       %.3f" report.RecallAt5
    printfn "  Recall@10:      %.3f" report.RecallAt10
    printfn "  nDCG@5:         %.3f" report.NdcgAt5
    printfn "  nDCG@10:        %.3f" report.NdcgAt10
    printfn "  MRR:            %.3f" report.Mrr
    printfn "  Avg latency:    %.1f ms" report.AvgLatencyMs

    printfn ""
    printfn "Per-query:"

    for q in report.PerQuery do
        let firstRank =
            match q.RelevantRanks with
            | [] -> "—"
            | _ -> string (List.min q.RelevantRanks)

        printfn "  [%s] firstRelevant=%-3s latency=%dms" q.QueryId firstRank q.LatencyMs

    printfn ""

let private writeReport (path: string) (report: EvalReport) =
    let options =
        let o = FableConverters.create ()
        o.WriteIndented <- true
        o

    let json = JsonSerializer.Serialize(report, options)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, json)
    printfn "Report written to %s" path

// ─── Entry point ──────────────────────────────────────────────────

// Phase 122 — `ablation` subcommand: the pipeline-composition gate.
// `dotnet run --project src/ToolUp.RAG.Evaluation -- ablation [--dataset <name>]`
// (default dataset: scifact). Exits non-zero on a metric-floor breach or
// on non-deterministic ordering across identical runs. Kept as a console
// subcommand rather than a `dotnet test` category per the repo's testing
// convention (Expecto-style console runners; `dotnet test` is a silent
// false-green against them — see CLAUDE.md "Build pipeline").
let private runAblation (argv: string list) =
    let datasetName =
        match argv with
        | "--dataset" :: name :: _ -> name
        | _ -> "scifact"

    Ablation.run datasetName |> Async.RunSynchronously

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "ablation" :: rest -> runAblation rest
    | _ ->

        let fixturesArg, baselineArg, outArg =
            let mutable fixtures = []
            let mutable baseline: string option = None
            let mutable out: string option = None
            let mutable i = 0

            while i < argv.Length do
                match argv[i] with
                | "--baseline" when i + 1 < argv.Length ->
                    baseline <- Some argv[i + 1]
                    i <- i + 2
                | "--out" when i + 1 < argv.Length ->
                    out <- Some argv[i + 1]
                    i <- i + 2
                | f ->
                    fixtures <- fixtures @ [ f ]
                    i <- i + 1

            fixtures, baseline, out

        let fixtures =
            if List.isEmpty fixturesArg then
                [ Path.Combine(AppContext.BaseDirectory, "fixtures", "platform-readme.json") ]
            else
                fixturesArg

        let mutable regressionFound = false

        for fixturePath in fixtures do
            if not (File.Exists fixturePath) then
                eprintfn "Fixture not found: %s" fixturePath
                regressionFound <- true
            else
                let fixture = FixtureLoader.load fixturePath

                let tempDir =
                    Path.Combine(Path.GetTempPath(), "rag-eval-" + Guid.NewGuid().ToString("N"))

                try
                    let pipeline = buildPipeline tempDir
                    let report = RetrievalEval.evaluate pipeline fixture |> Async.RunSynchronously
                    printReport report

                    match outArg with
                    | Some path -> writeReport path report
                    | None -> ()

                    match baselineArg with
                    | None -> ()
                    | Some path when not (File.Exists path) ->
                        eprintfn "Baseline not found: %s — skipping regression check" path
                    | Some path ->
                        let json = File.ReadAllText path

                        let options = FableConverters.create ()

                        let baseline = JsonSerializer.Deserialize<EvalReport>(json, options)

                        match RetrievalEval.detectRegression 0.05 baseline report with
                        | Ok() -> printfn "✓ No regression vs baseline (%s)" path
                        | Error msg ->
                            eprintfn "✗ Regression detected: %s" msg
                            regressionFound <- true
                finally
                    try
                        Directory.Delete(tempDir, recursive = true)
                    with _ ->
                        ()

        if regressionFound then 1 else 0