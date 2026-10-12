module ToolUp.RAG.Benchmarks.Program

open System
open System.IO
open ToolUp.RAG.Benchmarks.PipelineFactory
open ToolUp.RAG.Benchmarks.BenchmarkRunner
open ToolUp.RAG.Benchmarks.ReportWriter

// ─── CLI parsing ─────────────────────────────────────────────────

type private CliArgs = {
    Dataset: string option
    MixedDimFixture: string option
    HnswScale: string option
    PadTo: int
    EfSearch: int list
    M: int option
    MaxQueries: int option
    FlatOnly: bool
    Embedder: EmbedderChoice
    VectorStore: VectorStoreChoice
    RerankerOn: bool
    MmrOn: bool
    TopK: int
    Replicate: int
    Out: string
}

let private defaults: CliArgs = {
    Dataset = None
    MixedDimFixture = None
    HnswScale = None
    PadTo = 0
    EfSearch = []
    M = None
    MaxQueries = None
    FlatOnly = false
    Embedder = Local
    VectorStore = Flat
    RerankerOn = false
    MmrOn = false
    TopK = 10
    Replicate = 1
    Out = "bench-results.csv"
}

let private parseEmbedder =
    function
    | "local" -> Some Local
    | "openai" -> Some OpenAI
    | _ -> None

let private parseVectorStore =
    function
    | "flat" -> Some Flat
    | "hnsw" -> Some Hnsw
    | _ -> None

let private parseOnOff =
    function
    | "on"
    | "true" -> Some true
    | "off"
    | "false" -> Some false
    | _ -> None

let private parseArgs (argv: string array) : Result<CliArgs, string> =
    let mutable args = defaults
    let mutable i = 0
    let mutable error: string option = None

    let setError msg =
        if error.IsNone then
            error <- Some msg

    while i < argv.Length && error.IsNone do
        match argv[i] with
        | "--dataset" when i + 1 < argv.Length ->
            args <- { args with Dataset = Some argv[i + 1] }

            i <- i + 2
        | "--mixed-dim-fixture" when i + 1 < argv.Length ->
            args <- {
                args with
                    MixedDimFixture = Some argv[i + 1]
            }

            i <- i + 2
        | "--hnsw-scale" when i + 1 < argv.Length ->
            args <- {
                args with
                    HnswScale = Some argv[i + 1]
            }

            i <- i + 2
        | "--pad-to" when i + 1 < argv.Length ->
            match Int32.TryParse argv[i + 1] with
            | true, n when n >= 0 -> args <- { args with PadTo = n }
            | _ -> setError (sprintf "invalid --pad-to value: %s (expected integer >= 0)" argv[i + 1])

            i <- i + 2
        | "--ef-search" when i + 1 < argv.Length ->
            let parsed = argv[i + 1].Split(',') |> Array.map Int32.TryParse

            if parsed |> Array.forall (fun (ok, n) -> ok && n > 0) then
                args <- {
                    args with
                        EfSearch = parsed |> Array.map snd |> List.ofArray
                }
            else
                setError (
                    sprintf "invalid --ef-search value: %s (expected comma-separated positive integers)" argv[i + 1]
                )

            i <- i + 2
        | "--m" when i + 1 < argv.Length ->
            match Int32.TryParse argv[i + 1] with
            | true, n when n >= 2 -> args <- { args with M = Some n }
            | _ -> setError (sprintf "invalid --m value: %s (expected integer >= 2)" argv[i + 1])

            i <- i + 2
        | "--max-queries" when i + 1 < argv.Length ->
            match Int32.TryParse argv[i + 1] with
            | true, n when n > 0 -> args <- { args with MaxQueries = Some n }
            | _ -> setError (sprintf "invalid --max-queries value: %s (expected positive integer)" argv[i + 1])

            i <- i + 2
        | "--flat-only" ->
            args <- { args with FlatOnly = true }
            i <- i + 1
        | "--embedder" when i + 1 < argv.Length ->
            match parseEmbedder argv[i + 1] with
            | Some e -> args <- { args with Embedder = e }
            | None -> setError (sprintf "unknown --embedder value: %s (expected local|openai)" argv[i + 1])

            i <- i + 2
        | "--vector-store" when i + 1 < argv.Length ->
            match parseVectorStore argv[i + 1] with
            | Some v -> args <- { args with VectorStore = v }
            | None -> setError (sprintf "unknown --vector-store value: %s (expected flat|hnsw)" argv[i + 1])

            i <- i + 2
        | "--reranker" when i + 1 < argv.Length ->
            match parseOnOff argv[i + 1] with
            | Some v -> args <- { args with RerankerOn = v }
            | None -> setError (sprintf "unknown --reranker value: %s (expected on|off)" argv[i + 1])

            i <- i + 2
        | "--mmr" when i + 1 < argv.Length ->
            match parseOnOff argv[i + 1] with
            | Some v -> args <- { args with MmrOn = v }
            | None -> setError (sprintf "unknown --mmr value: %s (expected on|off)" argv[i + 1])

            i <- i + 2
        | "--topK" when i + 1 < argv.Length ->
            match Int32.TryParse argv[i + 1] with
            | true, n when n > 0 -> args <- { args with TopK = n }
            | _ -> setError (sprintf "invalid --topK value: %s (expected positive integer)" argv[i + 1])

            i <- i + 2
        | "--replicate" when i + 1 < argv.Length ->
            match Int32.TryParse argv[i + 1] with
            | true, n when n >= 1 -> args <- { args with Replicate = n }
            | _ -> setError (sprintf "invalid --replicate value: %s (expected integer >= 1)" argv[i + 1])

            i <- i + 2
        | "--out" when i + 1 < argv.Length ->
            args <- { args with Out = argv[i + 1] }
            i <- i + 2
        | unknown ->
            setError (sprintf "unknown argument: %s" unknown)
            i <- i + 1

    match error with
    | Some msg -> Error msg
    | None ->
        match [ args.Dataset; args.MixedDimFixture; args.HnswScale ] |> List.choose id with
        | [] -> Error "--dataset, --mixed-dim-fixture or --hnsw-scale is required (e.g. --dataset scifact)"
        | [ _ ] -> Ok args
        | _ -> Error "--dataset, --mixed-dim-fixture and --hnsw-scale are mutually exclusive"

let private printUsage () =
    eprintfn "ToolUp.RAG.Benchmarks — BEIR retrieval benchmark runner"
    eprintfn ""
    eprintfn "Usage: dotnet run --project src/ToolUp.RAG.Benchmarks -- [options]"
    eprintfn ""
    eprintfn "Required (one of):"
    eprintfn "  --dataset <name>           BEIR dataset id (e.g. scifact, nfcorpus, fiqa)"
    eprintfn "  --mixed-dim-fixture <path> Mixed-dim fixture JSON (Phase 14k WS5 Item 3 — drives HNSW directly)"
    eprintfn "  --hnsw-scale <name>        HNSW recall@10 vs flat scan + latency on a BEIR dataset (Phase 970),"
    eprintfn "                             or 'synthetic' for synthetic vectors only. JSON report beside --out."
    eprintfn ""
    eprintfn "--hnsw-scale options:"

    eprintfn
        "  --pad-to <int>             pad the corpus with synthetic vectors to this many chunks (default: no padding)"

    eprintfn "  --ef-search <n[,n...]>     one HNSW build per value (default: the store's default)"
    eprintfn "  --m <int>                  HNSW neighbour budget (default: the store's default)"
    eprintfn "  --max-queries <int>        time only the first N queries (default: all)"
    eprintfn "  --flat-only                time the exact flat scan only; build no HNSW graph"
    eprintfn ""
    eprintfn "Optional:"
    eprintfn "  --embedder local|openai    default: local"
    eprintfn "  --vector-store flat|hnsw   default: flat"
    eprintfn "  --reranker on|off          default: off (no concrete reranker yet — flag is a no-op)"
    eprintfn "  --mmr on|off               default: off (when on, λ=0.5)"
    eprintfn "  --topK <int>               default: 10"
    eprintfn "  --replicate <int>          default: 1 (>1 suppresses quality metrics)"
    eprintfn "  --out <path>               default: bench-results.csv"
    eprintfn ""
    eprintfn "Examples:"
    eprintfn "  dotnet run --project src/ToolUp.RAG.Benchmarks -- --dataset scifact"
    eprintfn "  dotnet run --project src/ToolUp.RAG.Benchmarks -- --mixed-dim-fixture fixtures/eval-mixed-dim.json"
    eprintfn "  dotnet run -c Release --project src/ToolUp.RAG.Benchmarks -- --hnsw-scale scifact --pad-to 500000"
    eprintfn ""
    eprintfn "OpenAI embedder reads OPENAI_API_KEY (or openai-api-key) from env."
    eprintfn "BEIR datasets are downloaded to data/beir/ on first use (override via TOOLUP_BEIR_CACHE)."

// ─── Entry point ─────────────────────────────────────────────────

[<EntryPoint>]
let main argv =
    match parseArgs argv with
    // Phase 886 — the concurrent load harness has its own verb and parser
    // (`load gate|retrieval|facts`); everything else is the BEIR runner.
    | _ when argv.Length > 0 && argv[0] = "load" -> FactStoreLoadBenchmark.LoadCommand.run argv[1..]
    | Error msg ->
        eprintfn "Error: %s" msg
        eprintfn ""
        printUsage ()
        1
    | Ok args when args.HnswScale.IsSome ->
        let reportPath =
            Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath args.Out),
                Path.GetFileNameWithoutExtension args.Out + ".hnsw-scale.json"
            )

        try
            let report =
                HnswScaleBenchmark.run {
                    Dataset = args.HnswScale.Value
                    PadTo = args.PadTo
                    EfSearch = args.EfSearch
                    M = args.M
                    MaxQueries = args.MaxQueries
                    FlatOnly = args.FlatOnly
                    TopK = args.TopK
                    ReportPath = reportPath
                }
                |> Async.RunSynchronously

            printfn
                "[bench] %d HNSW run(s) over %d chunks; report written to %s"
                report.Runs.Length
                report.TotalChunks
                reportPath

            0
        with ex ->
            eprintfn "HNSW scale benchmark failed: %s" ex.Message
            eprintfn "%s" ex.StackTrace
            2
    | Ok args ->
        if args.RerankerOn then
            eprintfn "[bench] --reranker on requested but no concrete reranker is wired in v1; ignoring."

        let resolveFixturePath (raw: string) =
            // Allow the caller to pass a bare filename (defaults to the
            // copy MSBuild drops next to the runner under fixtures/).
            if File.Exists raw then
                raw
            else
                let runner = Path.Combine(AppContext.BaseDirectory, "fixtures", raw)
                if File.Exists runner then runner else raw

        try
            let row =
                match args.MixedDimFixture with
                | Some path ->
                    let resolved = resolveFixturePath path

                    MixedDimBenchmark.run resolved args.TopK args.Replicate
                    |> Async.RunSynchronously
                | None ->
                    let dataset = args.Dataset.Value

                    let cfg: RunConfig = {
                        DatasetName = dataset
                        Build = {
                            Embedder = args.Embedder
                            VectorStore = args.VectorStore
                            EnableMmr = args.MmrOn
                            MmrLambda = 0.5
                        }
                        TopK = args.TopK
                        Replicate = args.Replicate
                    }

                    runOne cfg |> Async.RunSynchronously

            printfn ""

            printfn
                "═══ %s · %s · %s · topK=%d · replicate=%d ═══"
                row.Dataset
                row.Embedder
                row.VectorStore
                row.TopK
                row.Replicate

            printfn "  Queries evaluated:    %d" row.QueriesEvaluated

            if row.Replicate <= 1 then
                printfn "  nDCG@10:              %.4f" row.NdcgAt10
                printfn "  Recall@100:           %.4f" row.RecallAt100
                printfn "  MRR@10:               %.4f" row.MrrAt10
            else
                printfn "  Quality metrics:      suppressed (replicate=%d)" row.Replicate

            printfn "  Ingest:               %.2f s" row.IngestSeconds
            printfn "  Query p50/p95/p99:    %d / %d / %d ms" row.QueryP50Ms row.QueryP95Ms row.QueryP99Ms
            printfn "  Wall clock:           %.2f s" row.WallClockSeconds
            printfn ""

            appendRow args.Out row
            printfn "[bench] Row appended to %s" args.Out
            0
        with ex ->
            eprintfn "Benchmark failed: %s" ex.Message
            eprintfn "%s" ex.StackTrace
            2