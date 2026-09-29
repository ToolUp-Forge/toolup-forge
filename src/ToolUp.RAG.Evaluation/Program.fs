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
open ToolUp.RAG.SparseAnalysis
open ToolUp.SparseIndices.Snowball
open ToolUp.SparseIndices.Cjk
open ToolUp.SparseIndices.Postgres
open ToolUp.RAG.Evaluation.EvalTypes

// ─── Wiring helpers ───────────────────────────────────────────────

// ─── Phase 501 — sparse-analyzer selection ────────────────────────
//
// `--analyzer <id>` picks the `ISparseAnalyzer` the BM25 leg runs with, so
// the harness can MEASURE the retrieval-quality claim a language analyzer
// makes rather than assert it. Run the same fixture twice and compare:
//
//   dotnet run --project src/ToolUp.RAG.Evaluation -- \
//       src/ToolUp.RAG.Evaluation/fixtures/eval-morphology.json
//   dotnet run --project src/ToolUp.RAG.Evaluation -- --analyzer snowball-en \
//       src/ToolUp.RAG.Evaluation/fixtures/eval-morphology.json
//
// Default is `identity` — the shipped tokenisation — so every pre-existing
// invocation of this harness measures exactly what it measured before.

let private analyzerNames = [ "identity"; "snowball-en"; "cjk" ]

let private resolveAnalyzer (name: string) : Result<ISparseAnalyzer, string> =
    match name.Trim().ToLowerInvariant() with
    | "identity" -> Ok ToolUp.RAG.SparseAnalysis.identity
    | "snowball-en" -> Ok(SnowballAnalyzer.english ())
    | "cjk" -> Ok(CjkAnalyzer.bigrams ())
    | other -> Error(sprintf "Unknown analyzer '%s'. Known: %s" other (String.Join(", ", analyzerNames)))

// ─── Phase 893 — keyword-index selection ──────────────────────────
//
// `--sparse-index postgres` swaps the in-process BM25 leg for the
// `ToolUp.SparseIndices.Postgres` companion (connection string from
// `TOOLUP_PG_FULLTEXT_CONNECTION_STRING`; each fixture gets its own table,
// dropped afterwards), so the two keyword indexes' retrieval quality is
// MEASURED side by side over the same fixtures rather than asserted. The
// companion runs its own ranking function, so the numbers differ by design;
// the companion README records them.
//
//   dotnet run --project src/ToolUp.RAG.Evaluation -- --sparse-index postgres //       src/ToolUp.RAG.Evaluation/fixtures/eval-morphology.json
//
// Default is `inprocess`, so every pre-existing invocation measures exactly
// what it measured before.

let private sparseIndexNames = [ "inprocess"; "postgres" ]

/// The keyword leg for one fixture run: the index, and the teardown that
/// runs after the fixture (a no-op in process; dropping the table for the
/// database companion).
type private SparseLeg = {
    Index: ISparseIndex.ISparseIndex
    Teardown: unit -> unit
}

let private postgresLeg (analyzer: ISparseAnalyzer) : Result<SparseLeg, string> =
    match Environment.GetEnvironmentVariable ConfigKeys.Names.pgFullTextConnectionString with
    | null
    | "" ->
        Error(
            sprintf
                "--sparse-index postgres needs %s (a PostgreSQL connection string)."
                ConfigKeys.Names.pgFullTextConnectionString
        )
    | connectionString ->
        let table = "rag_eval_" + Guid.NewGuid().ToString("N").Substring(0, 16)

        let options = {
            PostgresFullTextIndex.PostgresFullTextOptions.defaults with
                Table = table
        }

        let index = PostgresFullTextIndex.create connectionString options analyzer None

        Ok {
            Index = index
            Teardown =
                fun () ->
                    (index :?> IDisposable).Dispose()
                    use dataSource = Npgsql.NpgsqlDataSource.Create connectionString
                    use cmd = dataSource.CreateCommand(sprintf "DROP TABLE IF EXISTS %s;" table)
                    cmd.ExecuteNonQuery() |> ignore
        }

/// Build a fresh in-memory pipeline rooted at `tempDir`. Each invocation
/// gets its own `LocalFileStorage` directory so eval runs are independent
/// — the BM25/vector indices don't leak across fixtures.
let private buildPipeline
    (tempDir: string)
    (analyzer: ISparseAnalyzer)
    (sparseLeg: (ISparseAnalyzer -> Result<SparseLeg, string>) option)
    : Result<IRetrievalPipeline * (unit -> unit), string> =
    Directory.CreateDirectory tempDir |> ignore

    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> BlobStorage.IBlobStorage

    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    let embedder = LocalEmbeddingProvider.create ()

    // Disable debounced flush in eval — every Index call should be visible
    // to the next Retrieve without waiting on a 2-second timer.
    let vectorStore = new InMemoryVectorStore(storage, logger, flushIntervalMs = 50)

    let sparse =
        match sparseLeg with
        | Some build -> build analyzer
        | None ->
            Ok {
                Index =
                    new InMemoryBM25Index(storage, logger, flushIntervalMs = 50, analyzer = analyzer)
                    :> ISparseIndex.ISparseIndex
                Teardown = ignore
            }

    let tracer = createNoOp ()

    // The smoke fixture is entirely `Platform`-scoped. `authorisedScopes`
    // filters `Platform` out unless the Platform KB is enabled, and the
    // constructor defaults to `NoPlatformKnowledgeBase` — so without this
    // the pipeline early-returns `[]` for every query and the harness
    // scores 0.000 (the regression this restores: the eval is a
    // measurement tool, it must enable the KB scope its fixture lives in).
    sparse
    |> Result.map (fun leg ->
        RetrievalPipeline(
            store = vectorStore,
            embedder = embedder,
            sparseIndex = leg.Index,
            tracer = tracer,
            platformKnowledgeBase = EnabledPlatformKnowledgeBase
        )
        :> IRetrievalPipeline,
        leg.Teardown)

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
    printfn "  Filter leaks:   %d query/queries" report.FilterViolationCount

    printfn ""
    printfn "Per-query:"

    for q in report.PerQuery do
        let firstRank =
            match q.RelevantRanks with
            | [] -> "—"
            | _ -> string (List.min q.RelevantRanks)

        printfn "  [%s] firstRelevant=%-3s latency=%dms" q.QueryId firstRank q.LatencyMs

        // Phase 502.E — name the offending ids, not just the count. A
        // filtered-retrieval regression is diagnosed by WHICH chunk escaped
        // the slice ("the untagged one", "the other document's").
        match q.FilterViolations with
        | [] -> ()
        | ids -> printfn "        ✗ out-of-filter chunks returned: %s" (String.Join(", ", ids))

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

        let fixturesArg, baselineArg, outArg, analyzerArg, sparseIndexArg =
            let mutable fixtures = []
            let mutable baseline: string option = None
            let mutable out: string option = None
            let mutable analyzer = "identity"
            let mutable sparseIndex = "inprocess"
            let mutable i = 0

            while i < argv.Length do
                match argv[i] with
                | "--baseline" when i + 1 < argv.Length ->
                    baseline <- Some argv[i + 1]
                    i <- i + 2
                | "--out" when i + 1 < argv.Length ->
                    out <- Some argv[i + 1]
                    i <- i + 2
                | "--analyzer" when i + 1 < argv.Length ->
                    analyzer <- argv[i + 1]
                    i <- i + 2
                | "--sparse-index" when i + 1 < argv.Length ->
                    sparseIndex <- argv[i + 1]
                    i <- i + 2
                | f ->
                    fixtures <- fixtures @ [ f ]
                    i <- i + 1

            fixtures, baseline, out, analyzer, sparseIndex

        let fixtures =
            if List.isEmpty fixturesArg then
                [ Path.Combine(AppContext.BaseDirectory, "fixtures", "platform-readme.json") ]
            else
                fixturesArg

        let mutable regressionFound = false

        // Resolve before any fixture runs — an unknown analyzer must be an
        // error, never a silent fall-back to the default, which would report
        // "no lift" for a configuration that never ran.
        let analyzer =
            match resolveAnalyzer analyzerArg with
            | Ok a ->
                printfn "Sparse analyzer: %s" a.Id
                Some a
            | Error message ->
                eprintfn "%s" message
                regressionFound <- true
                None

        // Phase 893 — the keyword index, resolved up front for the same
        // reason: an unknown name must fail, never fall back.
        let sparseLeg =
            match sparseIndexArg.Trim().ToLowerInvariant() with
            | "inprocess" -> Some None
            | "postgres" ->
                printfn "Sparse index: ToolUp.SparseIndices.Postgres"
                Some(Some postgresLeg)
            | other ->
                eprintfn "Unknown sparse index '%s'. Known: %s" other (String.Join(", ", sparseIndexNames))
                regressionFound <- true
                None

        for fixturePath in (if analyzer.IsNone || sparseLeg.IsNone then [] else fixtures) do
            if not (File.Exists fixturePath) then
                eprintfn "Fixture not found: %s" fixturePath
                regressionFound <- true
            else
                let fixture = FixtureLoader.load fixturePath

                let tempDir =
                    Path.Combine(Path.GetTempPath(), "rag-eval-" + Guid.NewGuid().ToString("N"))

                let built = buildPipeline tempDir analyzer.Value sparseLeg.Value

                try
                    match built with
                    | Error message ->
                        eprintfn "%s" message
                        regressionFound <- true
                    | Ok(pipeline, _) ->
                        let report = RetrievalEval.evaluate pipeline fixture |> Async.RunSynchronously
                        printReport report

                        // Phase 502.E — a filter leak fails the run outright, and
                        // is checked BEFORE the baseline comparison because it is
                        // not a quality metric with a tolerance: a filter is a
                        // narrowing / isolation intent (GP 4), so returning
                        // content the query asked to exclude is wrong at any
                        // recall. There is no `--baseline` in which it is
                        // acceptable, and no fixture-declared floor to breach —
                        // the correct count is always zero.
                        if report.FilterViolationCount > 0 then
                            eprintfn
                                "✗ %d filtered query/queries returned out-of-filter chunks in %s"
                                report.FilterViolationCount
                                fixture.Name

                            regressionFound <- true

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
                    match built with
                    | Ok(_, teardown) ->
                        try
                            teardown ()
                        with ex ->
                            eprintfn "Sparse-index teardown failed: %s" ex.Message
                    | Error _ -> ()

                    try
                        Directory.Delete(tempDir, recursive = true)
                    with _ ->
                        ()

        if regressionFound then 1 else 0