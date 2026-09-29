// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.Benchmarks.FactStoreLoadBenchmark

open System
open System.Diagnostics
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Facts
open ToolUp.FactStores.Postgres
open ToolUp.RAG.Benchmarks.ConcurrentLoadBenchmark

// ─── Phase 886 — the concurrent load harness (fact half) ─────────────
//
// The shipped fact store under concurrent callers: point read, population
// read, single assert and batch assert, each timed per operation and each
// reported with the blob reads it cost.
//
// ── How the store is seeded, and why ──
//
// Directly, by writing each fact's blob in the store's own format — the
// shape Phase 702's scale test uses and for the same reason: seeding
// through `Assert` re-enumerates the scope on every call, so it is
// quadratic in the seed. What an `Assert` costs at size is one of the
// things MEASURED below, not a way of getting there. The seeded blobs are
// proven readable through the store before anything is timed.
//
// ── The population ──
//
// `subjects` subjects in one hierarchy, `metrics` metrics each, and one
// fact per (subject, metric, week) for `weeks` consecutive weekly periods —
// "a year of weekly refreshes" is 52. Each week is its own period, so a
// subject-metric pair carries `weeks` heads, one per period; the reads
// below ask for the LATEST week, which is what an answer surface asks.
//
// At the stated scale (300,000 × several × 52) that is tens of millions of
// facts. Whether a given machine can hold and enumerate that many is part
// of what the harness reports: `docs/rag/performance.md` says which sizes
// were MEASURED and which figures are EXTRAPOLATED from the measured
// per-fact slope.

/// One cell of the fact matrix.
type FactCell = {
    /// Phase 929 — which `IFactStore` the cell drives: `blob`
    /// (`BlobFactStore` over `BlobArm`) or `postgres` (the
    /// `ToolUp.FactStores.Postgres` companion, armed by
    /// `TOOLUP_PGVECTOR_CONNECTION_STRING`; `BlobArm` is then unused).
    Store: string
    BlobArm: string
    Subjects: int
    Metrics: int
    Weeks: int
    Concurrency: int
    /// Operations per round, per operation kind.
    OpsPerRound: int
    Rounds: int
    /// Drafts per batch assert.
    BatchSize: int
}

/// One operation kind's measurement.
type FactOpResult = {
    Operation: string
    Latency: LatencySummary
    RoundP95s: float array
    ThroughputOps: float
    BlobReadsPerOp: float
    BlobListsPerOp: float
}

/// What one fact cell measured.
type FactResult = {
    Cell: FactCell
    /// `blob` or `postgres` — printed on every row.
    StoreLabel: string
    /// The blob arm's label, or `none` for a store that reads no blob.
    BlobLabel: string
    Facts: int
    SeedSeconds: float
    Operations: FactOpResult list
}

let private json = FableConverters.create ()

let private hierarchy = "load"
let private yearStart = DateTime(2025, 1, 6, 0, 0, 0, DateTimeKind.Utc)
let private harnessMethod = Computed("load-harness", "1", "p0")

let private subjectOf (i: int) : SubjectRef = {
    Hierarchy = hierarchy
    Path = [ sprintf "region-%02d" (i % 32); sprintf "s%07d" i ]
}

let private metricOf (m: int) = MetricRef(sprintf "metric-%02d" m)

let private week (w: int) : TemporalExtent = {
    From = yearStart.AddDays(7.0 * float w)
    To = yearStart.AddDays(7.0 * float (w + 1))
    Label = Some(sprintf "W%02d" (w + 1))
}

let private evidence (tag: string) : Evidence = {
    ResultRef = None
    InputHashes = [ tag ]
    TriggerRef = None
}

let private seededFact (subject: int) (metric: int) (w: int) : Fact =
    let s = subjectOf subject
    let m = metricOf metric
    let p = week w
    let e = evidence "seed"
    let value = Scalar(decimal ((subject * 7 + metric * 13 + w) % 100_000))

    {
        FactId = Fact.compute s m p harnessMethod (Fact.effectiveInputHashes harnessMethod e value)
        Subject = s
        Metric = m
        Value = value
        Period = p
        AsOf = (p.To).AddHours 1.0
        Method = harnessMethod
        Evidence = e
        Confidence = None
        Supersedes = None
        Disclosure = Surfaceable
    }

/// The draft whose assert stores `seededFact subject metric w` (the same
/// content address) — how a store with no blob format of its own is
/// seeded.
let private seededDraft (subject: int) (metric: int) (w: int) : FactDraft =
    let fact = seededFact subject metric w

    {
        Subject = fact.Subject
        Metric = fact.Metric
        Value = fact.Value
        Period = fact.Period
        Method = fact.Method
        Evidence = fact.Evidence
        Confidence = None
        Disclosure = Surfaceable
    }

let private draftFor (subject: int) (metric: int) (w: int) (tag: string) : FactDraft = {
    Subject = subjectOf subject
    Metric = metricOf metric
    Value = Scalar(decimal (subject % 997))
    Period = week w
    Method = harnessMethod
    Evidence = evidence tag
    Confidence = None
    Disclosure = Surfaceable
}

let private measureOperation
    (name: string)
    (counting: CountingBlobStorage)
    (cell: FactCell)
    (operation: int -> int -> Async<unit>)
    : FactOpResult =
    counting.Reset()
    let all = ResizeArray<float>()
    let roundP95s = ResizeArray<float>()
    let mutable wall = 0.0

    for round in 0 .. cell.Rounds - 1 do
        let latencies, seconds =
            runClosedLoop cell.Concurrency cell.OpsPerRound (fun i -> operation round i)

        all.AddRange latencies
        roundP95s.Add (summarise latencies).P95
        wall <- wall + seconds

    let ops = float (cell.OpsPerRound * cell.Rounds)

    {
        Operation = name
        Latency = summarise (all.ToArray())
        RoundP95s = roundP95s.ToArray()
        ThroughputOps = ops / max 1e-9 wall
        BlobReadsPerOp = float counting.Downloads / ops
        BlobListsPerOp = float counting.Lists / ops
    }

// ─── Phase 929 — the fact store a cell drives ───────────────────────
//
// `blob` is the shipped default, `BlobFactStore`, over one of the blob
// arms; it is seeded by writing each fact's blob in its own format.
// `postgres` is the Phase 888 companion over a real local database; it has
// no blob format to write, so it is seeded through `AssertBatch` — which
// on that store is an indexed write, linear in the seed, so seeding
// through the contract is affordable there where it is quadratic on the
// blob store. A store that is named but not armed is an error, exactly as
// a blob arm is.

/// A fact store resolved for one run.
type private FactBackend = {
    StoreLabel: string
    BlobLabel: string
    Store: IFactStore
    /// Counts the blob reads the store makes. For `postgres` it wraps a
    /// dictionary nothing reads, so its counts are a measured zero.
    Counting: CountingBlobStorage
    /// Seed facts `0 .. total - 1` (fact `n` is `seededFact` of its
    /// subject, metric and week) into `scope`.
    Seed: string -> int -> (int -> int * int * int) -> Async<unit>
    Cleanup: unit -> unit
}

let private seedBlobs (storage: IBlobStorage) (scope: string) (total: int) (coords: int -> int * int * int) = async {
    for start in 0..4096 .. total - 1 do
        let! _ =
            [ start .. min total (start + 4096) - 1 ]
            |> List.map (fun n -> async {
                let subject, metric, w = coords n
                let fact = seededFact subject metric w
                let payload = JsonSerializer.Serialize(fact, json) |> Encoding.UTF8.GetBytes
                let! r = storage.Upload(scope, sprintf "_facts/%s.json" fact.FactId, payload)

                match r with
                | Ok _ -> ()
                | Error e -> failwithf "seeding failed: %s" e
            })
            |> fun seeds -> Async.Parallel(seeds, maxDegreeOfParallelism = 64)

        ()
}

let private seedThroughBatches (store: IFactStore) (scope: string) (total: int) (coords: int -> int * int * int) = async {
    let batch = 1_000

    let! _ =
        [ 0..batch .. total - 1 ]
        |> List.map (fun start -> async {
            let drafts = [
                for n in start .. min total (start + batch) - 1 do
                    let subject, metric, w = coords n
                    seededDraft subject metric w
            ]

            match! store.AssertBatch(scope, drafts) with
            | Ok _ -> ()
            | Error e -> failwithf "seeding failed: %s" e
        })
        |> fun batches -> Async.Parallel(batches, maxDegreeOfParallelism = 8)

    ()
}

let private resolveFactBackend (cell: FactCell) : Result<FactBackend, string> =
    match cell.Store.Trim().ToLowerInvariant() with
    | "blob" ->
        resolveBlobArm cell.BlobArm
        |> Result.map (fun arm ->
            let counting = CountingBlobStorage(arm.Storage)
            let storage = counting :> IBlobStorage

            {
                StoreLabel = "blob"
                BlobLabel = arm.Label
                Store = BlobFactStore.create storage (InMemoryEventStore.InMemoryEventStore())
                Counting = counting
                Seed = seedBlobs storage
                Cleanup = arm.Cleanup
            })
    | "postgres" ->
        match Environment.GetEnvironmentVariable PgvectorVariable with
        | null
        | "" ->
            Error
                $"the 'postgres' fact store was requested but {PgvectorVariable} is not set — start a local Postgres (docker run -d -p 5432:5432 -e POSTGRES_PASSWORD=<password> pgvector/pgvector:pg17) and point it there"
        | connectionString ->
            // A table of its own per run, dropped afterwards: the indexes
            // are measured as the run built them, never over an earlier
            // run's rows.
            let options = {
                PostgresFactStoreOptions.defaults with
                    Table = "toolup_load_" + Guid.NewGuid().ToString("N").Substring(0, 12)
            }

            let store =
                PostgresFactStore.create
                    connectionString
                    options
                    (InMemoryEventStore.InMemoryEventStore())
                    None
                    (fun () -> DateTime.UtcNow)

            Ok {
                StoreLabel = "postgres"
                BlobLabel = "none"
                Store = store :> IFactStore
                Counting = CountingBlobStorage(MemoryBlobStorage())
                Seed = seedThroughBatches store
                Cleanup =
                    fun () ->
                        (store :> IDisposable).Dispose()

                        try
                            use dataSource = Npgsql.NpgsqlDataSource.Create connectionString
                            use drop = dataSource.CreateCommand($"DROP TABLE IF EXISTS {options.Table}")
                            drop.ExecuteNonQuery() |> ignore
                        with ex ->
                            eprintfn "[load] could not drop %s: %s" options.Table ex.Message
            }
    | other -> Error $"unknown fact store '{other}' (expected blob|postgres)"

/// Run one fact cell: seed, prove the seed readable, then measure the four
/// operations in turn — reads first, so the writes do not change what the
/// reads were measured over.
let runFactCell (cell: FactCell) : Async<Result<FactResult, string>> = async {
    match
        (try
            resolveFactBackend cell
         with ex ->
             Error ex.Message)
    with
    | Error e -> return Error e
    | Ok backend ->
        try
            let counting = backend.Counting
            let store = backend.Store
            let scope = "load-" + Guid.NewGuid().ToString("N").Substring(0, 12)
            let latest = cell.Weeks - 1

            // Seed, in parallel batches.
            let seedClock = Stopwatch.StartNew()
            let total = cell.Subjects * cell.Metrics * cell.Weeks

            do!
                backend.Seed scope total (fun n ->
                    n / (cell.Metrics * cell.Weeks), (n / cell.Weeks) % cell.Metrics, n % cell.Weeks)

            seedClock.Stop()

            // Verify the probe before trusting any verdict: the seeded facts
            // must read back through the store as the facts written. The
            // transaction time is the store's to stamp (the blob seed
            // writes it; the database stamps its own clock), so it is the
            // one field not compared.
            let probe = seededFact (cell.Subjects / 2) 0 latest
            let! readBack = store.Get(scope, probe.FactId)

            match readBack with
            | Some found when { found with AsOf = probe.AsOf } = probe -> ()
            | _ -> failwith "a seeded fact does not read back through the store — the seed format has drifted"

            let pointRead =
                measureOperation "point read (subject + metric, latest week)" counting cell (fun round i -> async {
                    let subject = (round * cell.OpsPerRound + i) * 7919 % cell.Subjects

                    let query = {
                        FactQuery.forSubjectMetric (subjectOf subject) (metricOf (i % cell.Metrics)) with
                            PeriodOverlaps = Some(week latest)
                    }

                    let! facts = store.Query(scope, query)

                    if List.length facts <> 1 then
                        failwithf "point read for subject %d returned %d facts, expected 1" subject (List.length facts)
                })

            let byId =
                measureOperation "point read by fact id" counting cell (fun round i -> async {
                    let subject = (round * cell.OpsPerRound + i) * 7919 % cell.Subjects
                    let fact = seededFact subject (i % cell.Metrics) latest
                    let! found = store.Get(scope, fact.FactId)

                    if found.IsNone then
                        failwithf "fact %s is missing" fact.FactId
                })

            let population =
                measureOperation "population read (one metric, latest week, top 10)" counting cell (fun _ i -> async {
                    let query = {
                        PopulationQuery.create (metricOf (i % cell.Metrics)) hierarchy with
                            PeriodOverlaps = Some(week latest)
                            Ordering = Descending
                            TopK = 10
                    }

                    match! store.QueryPopulation(scope, query) with
                    | Ok result when result.Stats.SubjectCount = cell.Subjects -> ()
                    | Ok result ->
                        failwithf
                            "population read ranked %d subjects, expected %d"
                            result.Stats.SubjectCount
                            cell.Subjects
                    | Error e -> failwithf "population read refused: %s" e
                })

            // Writes: each assert revises the latest week of a distinct
            // subject (new inputs, so it SUPERSEDES rather than being an
            // idempotent skip — a skip costs one List and would flatter
            // the number).
            let single =
                measureOperation "single assert (supersedes the latest week)" counting cell (fun round i -> async {
                    let subject = (round * cell.OpsPerRound + i) % cell.Subjects
                    let tag = sprintf "single-%d-%d" round i

                    match! store.Assert(scope, draftFor subject 0 latest tag) with
                    | Ok _ -> ()
                    | Error e -> failwithf "assert failed: %s" e
                })

            let batch =
                measureOperation (sprintf "batch assert (%d drafts)" cell.BatchSize) counting cell (fun round i -> async {
                    let first = ((round * cell.OpsPerRound + i) * cell.BatchSize) % cell.Subjects
                    let tag = sprintf "batch-%d-%d" round i

                    let drafts = [
                        for k in 0 .. cell.BatchSize - 1 do
                            draftFor ((first + k) % cell.Subjects) (1 % cell.Metrics) latest tag
                    ]

                    match! store.AssertBatch(scope, drafts) with
                    | Ok _ -> ()
                    | Error e -> failwithf "batch assert failed: %s" e
                })

            backend.Cleanup()

            return
                Ok {
                    Cell = cell
                    StoreLabel = backend.StoreLabel
                    BlobLabel = backend.BlobLabel
                    Facts = total
                    SeedSeconds = seedClock.Elapsed.TotalSeconds
                    Operations = [ pointRead; byId; population; single; batch ]
                }
        with ex ->
            backend.Cleanup()

            let inner =
                match ex with
                | :? AggregateException as a -> a.Flatten().InnerExceptions[0].Message
                | _ -> ex.Message

            return
                Error
                    $"facts/{cell.Store}/{cell.BlobArm} at {cell.Subjects} subjects x {cell.Metrics} metrics x {cell.Weeks} weeks failed: {inner}"
}

/// The printed rows for one fact result — one per operation, each naming
/// its backend, population, concurrency and statistic.
let renderFacts (r: FactResult) : string list = [
    for op in r.Operations do
        sprintf
            "facts | store=%s blob=%s | subjects=%d metrics=%d weeks=%d (%d facts) | concurrency=%d | %s | %d ops x %d rounds | latency ms p50=%.2f p95=%.2f p99=%.2f max=%.2f | min-over-rounds p95=%.2f | throughput=%.1f ops/s | blob reads/op=%.1f lists/op=%.2f | seed=%.1fs"
            r.StoreLabel
            r.BlobLabel
            r.Cell.Subjects
            r.Cell.Metrics
            r.Cell.Weeks
            r.Facts
            r.Cell.Concurrency
            op.Operation
            r.Cell.OpsPerRound
            r.Cell.Rounds
            op.Latency.P50
            op.Latency.P95
            op.Latency.P99
            op.Latency.Max
            (Array.min op.RoundP95s)
            op.ThroughputOps
            op.BlobReadsPerOp
            op.BlobListsPerOp
            r.SeedSeconds
]

/// The fact gate's configuration — what `perf-budget-gate.ps1` runs on
/// every push. Deliberately small: every assert and every subject-metric
/// read enumerates the whole scope, so this is sized to finish in seconds
/// on a shared runner, not to be representative of the stated scale.
let gateFactCell: FactCell = {
    Store = "blob"
    BlobArm = "memory"
    Subjects = 1_000
    Metrics = 3
    Weeks = 4
    Concurrency = 4
    OpsPerRound = 20
    Rounds = 5
    BatchSize = 50
}

/// The gated samples one fact result yields.
let factGateSamples (r: FactResult) : GateSample list =
    let where =
        $"store={r.StoreLabel} blob={r.BlobLabel} subjects={r.Cell.Subjects} metrics={r.Cell.Metrics} weeks={r.Cell.Weeks} facts={r.Facts} concurrency={r.Cell.Concurrency} ops={r.Cell.OpsPerRound}x{r.Cell.Rounds}"

    let op (prefix: string) =
        r.Operations
        |> List.find (fun o -> o.Operation.StartsWith(prefix, StringComparison.Ordinal))

    let clock (metric: string) (o: FactOpResult) =
        let lo, median, hi = roundStatistics o.RoundP95s

        {
            Metric = metric
            Value = lo
            Rounds = o.RoundP95s.Length
            Observed = o.Latency.Count = r.Cell.OpsPerRound * r.Cell.Rounds
            Evidence = $"{o.Operation}: p95 per round, min over rounds — {where}"
            Median = median
            Max = hi
        }

    let pointRead = op "point read (subject"

    [
        clock "factPointReadMs" pointRead
        clock "factPopulationReadMs" (op "population read")
        clock "factAssertMs" (op "single assert")
        clock "factBatchAssertMs" (op "batch assert")
        {
            Metric = "factBlobReadsPerPointRead"
            Value = pointRead.BlobReadsPerOp
            Rounds = pointRead.RoundP95s.Length
            Observed = pointRead.Latency.Count = r.Cell.OpsPerRound * r.Cell.Rounds
            Evidence = $"blob downloads per subject-and-metric read — {where}"
            Median = pointRead.BlobReadsPerOp
            Max = pointRead.BlobReadsPerOp
        }
    ]

/// Phase 929 — the fact gate's configuration over the object-storage
/// emulator. Smaller than the memory arm's: on object storage every blob
/// read is a request, and the store's whole-scope paths (the index build a
/// point read triggers, the population surface) read the scope.
let azuriteGateFactCell: FactCell = {
    gateFactCell with
        BlobArm = "azurite"
        Subjects = 250
        OpsPerRound = 10
}

/// Phase 929 — the fact gate's configuration over the database-backed
/// store (Phase 888): the memory arm's population, so the two rows compare.
let postgresGateFactCell: FactCell = { gateFactCell with Store = "postgres" }

/// The gate configuration each arm runs: its measurement label, the
/// retrieval cell (optional, so an arm may budget facts alone) and the fact
/// cell. `memory` is the configuration CI runs; `azurite` moves the blob arm
/// onto the emulator; `postgres` puts retrieval on the pgvector store and
/// the facts on the database-backed fact store, both over one local
/// PostgreSQL. The two are armed by the same variables as the matrix
/// (`TOOLUP_PARITY_AZURITE`, `TOOLUP_PGVECTOR_CONNECTION_STRING`) and refuse
/// when they are not set.
let gateArm (arm: string) : Result<string * RetrievalCell option * FactCell, string> =
    match arm.Trim().ToLowerInvariant() with
    | "memory" -> Ok("Phase 886 load harness - gate configurations", Some gateRetrievalCell, gateFactCell)
    | "azurite" ->
        Ok(
            "Phase 929 load harness - gate configurations, azurite arm",
            Some {
                gateRetrievalCell with
                    BlobArm = "azurite"
            },
            azuriteGateFactCell
        )
    | "postgres" ->
        Ok(
            "Phase 929 load harness - gate configurations, postgres arm",
            Some {
                gateRetrievalCell with
                    Backend = Pgvector
            },
            postgresGateFactCell
        )
    | other -> Error $"unknown gate arm '{other}' (expected memory|azurite|postgres)"

// ─── The `load` command ─────────────────────────────────────────────

/// `dotnet run --project src/ToolUp.RAG.Benchmarks -- load <mode> [options]`.
/// `gate` runs the two gate configurations and writes the measurement file
/// the `load` block of perf-budgets.json is decided against; `retrieval`
/// and `facts` run a matrix by hand and print one row per cell.
module LoadCommand =

    let private usage () =
        eprintfn
            "Usage: dotnet run --project src/ToolUp.RAG.Benchmarks -c Release -- load <gate|retrieval|facts> [options]"

        eprintfn ""
        eprintfn "  load gate      [--arm memory|azurite|postgres] [--measurements <path>]"
        eprintfn "                 the gate configuration of one arm; writes the measurement file"

        eprintfn
            "  load retrieval [--stores flat,hnsw,pgvector] [--blob memory|disk|azurite] [--sizes 10000,100000,1000000]"

        eprintfn "                 [--concurrency 8] [--queries 200] [--rounds 5] [--recall 50] [--out <path>]"

        eprintfn
            "  load facts     [--store blob|postgres] [--blob memory|disk|azurite] [--subjects 300000] [--metrics 3] [--weeks 52]"

        eprintfn "                 [--concurrency 4] [--ops 20] [--rounds 5] [--batch 50] [--out <path>]"
        eprintfn ""

        eprintfn
            "  azurite reads TOOLUP_PARITY_AZURITE; pgvector and the postgres fact store read TOOLUP_PGVECTOR_CONNECTION_STRING."

        eprintfn "  An arm that is named but not armed is an error, never a fallback to memory."

    let private options (args: string array) : Map<string, string> =
        args
        |> Array.chunkBySize 2
        |> Array.choose (fun pair ->
            match pair with
            | [| key; value |] when key.StartsWith("--", StringComparison.Ordinal) -> Some(key.Substring 2, value)
            | _ -> None)
        |> Map.ofArray

    let private int' (o: Map<string, string>) key fallback =
        match o.TryFind key with
        | Some v -> int (v.Replace("_", ""))
        | None -> fallback

    let private list (o: Map<string, string>) key (fallback: string) =
        (o.TryFind key |> Option.defaultValue fallback).Split(',', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map _.Trim()
        |> Array.toList

    let private emit (outPath: string option) (rows: string list) =
        rows |> List.iter (printfn "%s")

        match outPath with
        | Some path ->
            IO.File.AppendAllLines(path, rows)
            printfn "[load] %d row(s) appended to %s" rows.Length path
        | None -> ()

    let private gate (o: Map<string, string>) =
        let arm = o.TryFind "arm" |> Option.defaultValue "memory"

        match gateArm arm with
        | Error e ->
            eprintfn "[load] %s" e
            1
        | Ok(label, retrievalCell, factCell) ->
            let path =
                o.TryFind "measurements"
                |> Option.defaultValue (
                    if arm = "memory" then
                        "artifacts/perf-budget/load-measurements.json"
                    else
                        $"artifacts/perf-budget/load-{arm}-measurements.json"
                )

            let retrieval =
                retrievalCell
                |> Option.map (fun cell ->
                    printfn "[load] gate (%s): retrieval %A" arm cell
                    runRetrievalCell cell |> Async.RunSynchronously)

            printfn "[load] gate (%s): facts %A" arm factCell
            let facts = runFactCell factCell |> Async.RunSynchronously

            match retrieval, facts with
            | (None | Some(Ok _)), Ok f ->
                let rows = [
                    match retrieval with
                    | Some(Ok r) -> renderRetrieval r
                    | _ -> ()
                    yield! renderFacts f
                ]

                rows |> List.iter (printfn "%s")

                let samples = [
                    match retrieval with
                    | Some(Ok r) -> yield! retrievalGateSamples r
                    | _ -> ()
                    yield! factGateSamples f
                ]

                writeMeasurements path label samples rows
                printfn "[load] measurements written to %s" path
                0
            | r, f ->
                // Every half that failed is named; nothing is written, so the
                // decider refuses on a missing file rather than reading half a run.
                [
                    match r with
                    | Some(Error e) -> Some e
                    | _ -> None
                    match f with
                    | Error e -> Some e
                    | Ok _ -> None
                ]
                |> List.choose id
                |> List.iter (eprintfn "[load] %s")

                2

    let private retrieval (o: Map<string, string>) =
        let stores = list o "stores" "flat,hnsw" |> List.map parseBackend
        let blob = o.TryFind "blob" |> Option.defaultValue "memory"

        let sizes =
            list o "sizes" "10000,100000" |> List.map (fun s -> int (s.Replace("_", "")))

        let mutable failures = 0

        for store in stores do
            match store with
            | Error e ->
                eprintfn "[load] %s" e
                failures <- failures + 1
            | Ok backend ->
                for size in sizes do
                    let cell: RetrievalCell = {
                        Backend = backend
                        BlobArm = blob
                        Chunks = size
                        Concurrency = int' o "concurrency" 8
                        QueriesPerRound = int' o "queries" 200
                        Rounds = int' o "rounds" 5
                        RecallQueries = int' o "recall" 50
                        Seed = 886
                    }

                    match runRetrievalCell cell |> Async.RunSynchronously with
                    | Ok r -> emit (o.TryFind "out") [ renderRetrieval r ]
                    | Error e ->
                        eprintfn "[load] %s" e
                        failures <- failures + 1

        if failures = 0 then 0 else 2

    let private facts (o: Map<string, string>) =
        let cell: FactCell = {
            Store = o.TryFind "store" |> Option.defaultValue "blob"
            BlobArm = o.TryFind "blob" |> Option.defaultValue "memory"
            Subjects = int' o "subjects" 300_000
            Metrics = int' o "metrics" 3
            Weeks = int' o "weeks" 52
            Concurrency = int' o "concurrency" 4
            OpsPerRound = int' o "ops" 20
            Rounds = int' o "rounds" 5
            BatchSize = int' o "batch" 50
        }

        match runFactCell cell |> Async.RunSynchronously with
        | Ok r ->
            emit (o.TryFind "out") (renderFacts r)
            0
        | Error e ->
            eprintfn "[load] %s" e
            2

    /// Entry point for `load`; `args` excludes the word `load` itself.
    let run (args: string array) : int =
        match Array.tryHead args with
        | Some "gate" -> gate (options args[1..])
        | Some "retrieval" -> retrieval (options args[1..])
        | Some "facts" -> facts (options args[1..])
        | _ ->
            usage ()
            1