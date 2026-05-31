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
        match args.Dataset, args.MixedDimFixture with
        | None, None -> Error "--dataset or --mixed-dim-fixture is required (e.g. --dataset scifact)"
        | Some _, Some _ -> Error "--dataset and --mixed-dim-fixture are mutually exclusive"
        | _ -> Ok args

let private printUsage () =
    eprintfn "ToolUp.RAG.Benchmarks — BEIR retrieval benchmark runner"
    eprintfn ""
    eprintfn "Usage: dotnet run --project src/ToolUp.RAG.Benchmarks -- [options]"
    eprintfn ""
    eprintfn "Required (one of):"
    eprintfn "  --dataset <name>           BEIR dataset id (e.g. scifact, nfcorpus, fiqa)"
    eprintfn "  --mixed-dim-fixture <path> Mixed-dim fixture JSON (Phase 14k WS5 Item 3 — drives HNSW directly)"
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
    eprintfn ""
    eprintfn "OpenAI embedder reads OPENAI_API_KEY (or openai-api-key) from env."
    eprintfn "BEIR datasets are downloaded to data/beir/ on first use (override via TOOLUP_BEIR_CACHE)."

// ─── Entry point ─────────────────────────────────────────────────

[<EntryPoint>]
let main argv =
    match parseArgs argv with
    | Error msg ->
        eprintfn "Error: %s" msg
        eprintfn ""
        printUsage ()
        1
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