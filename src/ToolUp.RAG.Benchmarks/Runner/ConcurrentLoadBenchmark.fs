// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.Benchmarks.ConcurrentLoadBenchmark

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.RAG
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryEmbeddingCache
open ToolUp.RAG.CachingEmbeddingProvider
open ToolUp.RAG.RetrievalPipeline
open ToolUp.RAG.VectorStores.Hnsw
open ToolUp.RAG.VectorStores.Pgvector

// ─── Phase 886 — the concurrent load harness (retrieval half) ─────────
//
// Every scale figure on record before this phase was taken ONE QUERY AT A
// TIME over an in-memory blob backend, where a blob read is a dictionary
// lookup. This harness drives N simultaneous callers against one composed
// pipeline and reports what a caller waits (TOTAL latency, p50/p95/p99),
// how much gets done (throughput), and — beside every clock — how many
// blob reads it cost, because the count is the number that survives a
// change of backend and the clock is not (the Phase 702 lesson).
//
// Every row names its backend, its corpus size, its concurrency and its
// statistic. The in-memory blob arm is labelled `memory` everywhere it
// appears: it is the second arm, never the headline.
//
// The first half of this file is the shared machinery the fact half
// (FactStoreLoadBenchmark.fs) also drives: the blob arms, the read
// counter, the closed-loop concurrent driver and the percentile summary.
//
// Nothing here asserts a wall-clock bound. The numbers go to the
// measurement file the budget gate decides (dev-scripts/perf-budget-gate.ps1
// → the `load` block of perf-budgets.json); the ordinary test suite reads
// no clock.

// ─── Blob arms ──────────────────────────────────────────────────────

/// The in-memory blob arm: a dictionary. A "read" here costs a lookup,
/// which is exactly why every number it produces is labelled `memory` and
/// why the read COUNT is reported beside it.
type MemoryBlobStorage() =
    let blobs = ConcurrentDictionary<struct (string * string), byte[]>()

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            async.Return(Error(ComposeRefusal.ComposeFailed "the memory arm does not compose"))

        member this.Erase(container, prefix, policy, dryRun) =
            eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun

        member _.Upload(container, blobName, content) = async {
            blobs[struct (container, blobName)] <- content
            return Ok blobName
        }

        member _.Download(container, blobName) = async {
            match blobs.TryGetValue(struct (container, blobName)) with
            | true, bytes -> return Ok bytes
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

        member this.DownloadRange(container, blobName, offset, length) =
            downloadRangeViaDownload (this :> IBlobStorage) container blobName offset length

        member _.Delete(container, blobName) = async {
            blobs.TryRemove(struct (container, blobName)) |> ignore
            return Ok()
        }

        member _.List(container, prefix) = async {
            return [
                for KeyValue(struct (c, n), _) in blobs do
                    if c = container && n.StartsWith(prefix, StringComparison.Ordinal) then
                        yield n
            ]
        }

        member _.Exists(container, blobName) =
            async.Return(blobs.ContainsKey(struct (container, blobName)))

        member _.GetMetadata(container, blobName) = async {
            match blobs.TryGetValue(struct (container, blobName)) with
            | true, bytes ->
                return
                    Ok {
                        Size = int64 bytes.Length
                        LastModified = DateTime.UtcNow
                        ContentType = None
                    }
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

/// Counts the reads that reach the backend. `Downloads` is whole-object
/// and ranged reads together; `Lists` is prefix enumerations, which on
/// object storage are paged requests of their own. Thread-safe: the
/// harness reads it while many callers are in flight.
type CountingBlobStorage(inner: IBlobStorage) =
    let mutable downloads = 0L
    let mutable lists = 0L
    let mutable uploads = 0L

    member _.Downloads = Interlocked.Read(&downloads)
    member _.Lists = Interlocked.Read(&lists)
    member _.Uploads = Interlocked.Read(&uploads)

    member _.Reset() =
        Interlocked.Exchange(&downloads, 0L) |> ignore
        Interlocked.Exchange(&lists, 0L) |> ignore
        Interlocked.Exchange(&uploads, 0L) |> ignore

    interface IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom
        member _.ComposeFrom(c, t, s) = inner.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dryRun) = inner.Erase(c, p, policy, dryRun)

        member _.Upload(c, n, content) =
            Interlocked.Increment(&uploads) |> ignore
            inner.Upload(c, n, content)

        member _.Download(c, n) =
            Interlocked.Increment(&downloads) |> ignore
            inner.Download(c, n)

        member _.DownloadRange(c, n, offset, length) =
            Interlocked.Increment(&downloads) |> ignore
            inner.DownloadRange(c, n, offset, length)

        member _.Delete(c, n) = inner.Delete(c, n)

        member _.List(c, p) =
            Interlocked.Increment(&lists) |> ignore
            inner.List(c, p)

        member _.Exists(c, n) = inner.Exists(c, n)
        member _.GetMetadata(c, n) = inner.GetMetadata(c, n)

/// The environment variable that arms the object-storage arm. The SAME
/// variable the cloud-parity lane reads, so `compose.parity.yml` brings up
/// what this harness needs with no second convention.
[<Literal>]
let AzuriteVariable = "TOOLUP_PARITY_AZURITE"

/// A blob arm resolved for one run: its label (printed on every row), the
/// storage, and the clean-up that removes what the run wrote.
type BlobArm = {
    Label: string
    Storage: IBlobStorage
    Cleanup: unit -> unit
}

/// Resolve a blob arm by name: `memory` (a dictionary), `disk` (the SDK's
/// `LocalFileStorage` over a temp directory — real file I/O, free), or
/// `azurite` (the Azure Blob companion against the local emulator, armed
/// by `TOOLUP_PARITY_AZURITE`). An arm that is named but not armed is an
/// ERROR, never a silent fallback to memory: a row labelled `azurite` that
/// ran against a dictionary is the failure this harness exists to prevent.
let resolveBlobArm (name: string) : Result<BlobArm, string> =
    match name.Trim().ToLowerInvariant() with
    | "memory" ->
        Ok {
            Label = "memory"
            Storage = MemoryBlobStorage() :> IBlobStorage
            Cleanup = ignore
        }
    | "disk" ->
        let dir =
            Path.Combine(Path.GetTempPath(), "toolup-load-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory dir |> ignore

        Ok {
            Label = "disk"
            Storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
            Cleanup =
                fun () ->
                    try
                        Directory.Delete(dir, recursive = true)
                    with _ ->
                        ()
        }
    | "azurite" ->
        match Environment.GetEnvironmentVariable AzuriteVariable with
        | null
        | "" ->
            Error
                $"the 'azurite' arm was requested but {AzuriteVariable} is not set — bring the emulator up (docker compose -f compose.parity.yml up -d --wait) and set it to UseDevelopmentStorage=true"
        | connectionString ->
            Ok {
                Label = "azurite"
                Storage =
                    ToolUp.Storage.AzureBlobStorage.create {
                        ConnectionString = connectionString
                        RootContainer = "toolup-load-" + Guid.NewGuid().ToString("N").Substring(0, 12)
                        ConnectionStringProvider = None
                        AuditLog = None
                    }
                // The container is unique per run; the emulator is torn
                // down with the lane (`docker compose ... down -v`).
                Cleanup = ignore
            }
    | other -> Error $"unknown blob arm '{other}' (expected memory|disk|azurite)"

// ─── Statistics ─────────────────────────────────────────────────────

/// Nearest-rank percentile over an already-sorted sample — the convention
/// `RollingRagTelemetry` uses, so a harness p95 and a `/health/rag` p95
/// mean the same thing.
let percentile (p: float) (sorted: float array) =
    if sorted.Length = 0 then
        0.0
    else
        let idx = max 0 (int (ceil (p * float sorted.Length)) - 1)
        sorted[min idx (sorted.Length - 1)]

/// The latency distribution of one measured set of operations, in ms.
type LatencySummary = {
    Count: int
    P50: float
    P95: float
    P99: float
    Max: float
}

/// Summarise a latency sample (milliseconds).
let summarise (latenciesMs: float array) : LatencySummary =
    let sorted = Array.sort latenciesMs

    {
        Count = sorted.Length
        P50 = percentile 0.50 sorted
        P95 = percentile 0.95 sorted
        P99 = percentile 0.99 sorted
        Max = (if sorted.Length = 0 then 0.0 else sorted[sorted.Length - 1])
    }

/// One closed-loop round: `concurrency` callers, each taking the next
/// operation index as soon as its previous one returns, until `total`
/// operations have run. Returns every operation's latency (ms) and the
/// round's wall time (s). Each caller is its own thread-pool task, so the
/// concurrency is real rather than interleaved on one thread. An
/// operation that throws fails the round: a failed call timed as a fast
/// one is a suspiciously good result, never a measurement.
let runClosedLoop (concurrency: int) (total: int) (operation: int -> Async<unit>) : float array * float =
    let latencies = Array.zeroCreate<float> total
    let mutable next = -1
    let wall = Stopwatch.StartNew()

    let caller () : Task = task {
        let mutable index = Interlocked.Increment(&next)

        while index < total do
            let clock = Stopwatch.StartNew()
            do! Async.StartAsTask(operation index)
            clock.Stop()
            latencies[index] <- clock.Elapsed.TotalMilliseconds
            index <- Interlocked.Increment(&next)
    }

    let callers =
        Array.init (max 1 concurrency) (fun _ -> Task.Run(Func<Task>(fun () -> caller ())))

    Task.WaitAll callers
    wall.Stop()
    latencies, wall.Elapsed.TotalSeconds

// ─── Measurement file (the budget gate's input) ──────────────────────

/// One gated sample, in the `toolup.perf-measurements/v1` shape the
/// `VerifyLoadPerfBudget` decider reads. `Value` is the MIN over rounds;
/// `Evidence` says what was measured, over what, at what concurrency —
/// every gated number names its backend, corpus size, concurrency and
/// statistic.
type GateSample = {
    Metric: string
    Value: float
    Rounds: int
    Observed: bool
    Evidence: string
    Median: float
    Max: float
}

/// Min / median / max of a per-round series.
let roundStatistics (perRound: float array) =
    let sorted = Array.sort perRound

    if sorted.Length = 0 then
        0.0, 0.0, 0.0
    else
        sorted[0], sorted[sorted.Length / 2], sorted[sorted.Length - 1]

let private json (s: string) = Text.Json.JsonSerializer.Serialize(s)

let private num (v: float) =
    v.ToString("0.####", Globalization.CultureInfo.InvariantCulture)

/// Write the measurement file the `load` block is decided against.
/// `rows` is the full human-readable report, carried alongside so a
/// reviewer holding the file sees what every gated number came from; the
/// decider ignores it.
let writeMeasurements (path: string) (label: string) (samples: GateSample list) (rows: string list) =
    let dir = Path.GetDirectoryName(Path.GetFullPath path)

    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory dir |> ignore

    let sampleJson (s: GateSample) =
        $"""    {{ "metric": {json s.Metric}, "statistic": "min", "value": {num s.Value}, "samples": {s.Rounds}, "observed": {(if s.Observed then "true" else "false")}, "evidence": {json s.Evidence}, "median": {num s.Median}, "max": {num s.Max} }}"""

    let text =
        String.concat Environment.NewLine [
            "{"
            """  "schema": "toolup.perf-measurements/v1","""
            $"""  "label": {json label},"""
            $"""  "appDirectory": {json AppContext.BaseDirectory},"""
            "  \"machine\": {"
            $"""    "os": {json Runtime.InteropServices.RuntimeInformation.OSDescription},"""
            $"""    "processors": {Environment.ProcessorCount},"""
            $"""    "takenAt": {json (DateTime.UtcNow.ToString "o")}"""
            "  },"
            "  \"samples\": ["
            String.Join("," + Environment.NewLine, samples |> List.map sampleJson)
            "  ],"
            "  \"report\": ["
            String.Join("," + Environment.NewLine, rows |> List.map (fun r -> "    " + json r))
            "  ]"
            "}"
        ]

    File.WriteAllText(path, text)

// ─── Synthetic corpus ───────────────────────────────────────────────
//
// Deterministic by seed. Chunks are drawn from a vocabulary with a topic
// bias (each chunk leans on one of 64 topics' word ranges), so the corpus
// has neighbourhood structure for recall to mean something; each query is
// a handful of words from one chunk, so it has a true nearest neighbour.

let private vocabularySize = 20_000
let private topics = 64
let private chunkWords = 40
let private queryWords = 6

let private word (i: int) = "w" + i.ToString("x5")

let private chunkText (seed: int) (index: int) =
    let rng = Random(seed * 1_000_003 + index)
    let topic = rng.Next topics
    let span = vocabularySize / topics

    Array.init chunkWords (fun _ ->
        if rng.NextDouble() < 0.7 then
            word (topic * span + rng.Next span)
        else
            word (rng.Next vocabularySize))
    |> String.concat " "

let private queryText (seed: int) (index: int) (corpusSize: int) =
    let rng = Random(seed * 7_919 + index)
    let source = chunkText seed (rng.Next corpusSize)
    let words = source.Split ' '

    Array.init queryWords (fun _ -> words[rng.Next words.Length])
    |> String.concat " "

// ─── The retrieval benchmark ────────────────────────────────────────

/// A vector store the harness can drive. `Pgvector` is armed by the same
/// variable the Pgvector companion's live test arm reads.
type VectorBackend =
    | Flat
    | Hnsw
    | Pgvector

/// The local PostgreSQL the harness's database arm runs against. Phase 929:
/// the fact half's `postgres` store reads it too — one local server serves
/// the pgvector store and the database-backed fact store, each in tables of
/// its own, so the database arm needs one service and one variable.
[<Literal>]
let PgvectorVariable = "TOOLUP_PGVECTOR_CONNECTION_STRING"

let backendLabel =
    function
    | Flat -> "flat"
    | Hnsw -> "hnsw"
    | Pgvector -> "pgvector"

let parseBackend (s: string) =
    match s.Trim().ToLowerInvariant() with
    | "flat" -> Ok Flat
    | "hnsw" -> Ok Hnsw
    | "pgvector" -> Ok Pgvector
    | other -> Error $"unknown vector backend '{other}' (expected flat|hnsw|pgvector)"

/// One cell of the retrieval matrix.
type RetrievalCell = {
    Backend: VectorBackend
    BlobArm: string
    Chunks: int
    Concurrency: int
    QueriesPerRound: int
    Rounds: int
    /// Queries whose store-level top-10 is compared with the exact flat
    /// scan (Phase 14k's deferred recall item). 0 skips recall.
    RecallQueries: int
    Seed: int
}

/// What one cell measured.
type RetrievalResult = {
    Cell: RetrievalCell
    BlobLabel: string
    SeedSeconds: float
    /// TOTAL latency over every measured query of every round.
    Latency: LatencySummary
    /// The p95 of each round, in round order — the gate reads their min.
    RoundP95s: float array
    ThroughputQps: float
    BlobReadsPerQuery: float
    BlobListsPerQuery: float
    PeakInFlight: int
    /// The `Total` pseudo-stage's p95 as `/health/rag` would report it.
    TelemetryTotalP95Ms: float
    /// Recall@10 of the store's own search against the exact flat scan.
    RecallAt10: float option
}

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private scope = Deployment

let private cosineTop (k: int) (vectors: float32 array array) (query: float32 array) : string list =
    // Exact flat scan: the ground truth recall is measured against. Every
    // vector the local provider emits is L2-normalised, so the dot product
    // IS the cosine; the norms are still divided out so the ground truth
    // does not depend on that property holding.
    let norm (v: float32 array) =
        sqrt (v |> Array.sumBy (fun x -> float x * float x))

    let qn = max 1e-12 (norm query)

    vectors
    |> Array.mapi (fun i v ->
        let mutable dot = 0.0

        for d in 0 .. v.Length - 1 do
            dot <- dot + float v[d] * float query[d]

        i, dot / (qn * max 1e-12 (norm v)))
    |> Array.sortByDescending snd
    |> Array.truncate k
    |> Array.map (fun (i, _) -> sprintf "c%07d" i)
    |> Array.toList

let private buildStore
    (backend: VectorBackend)
    (storage: IBlobStorage)
    (dimensions: int)
    : Result<IVectorStore * (unit -> unit), string> =
    let logger = SilentLogger() :> ILogger

    match backend with
    | Flat -> Ok(new InMemoryVectorStore(storage, logger, flushIntervalMs = 50) :> IVectorStore, ignore)
    | Hnsw -> Ok(HnswVectorStore.create storage (Some logger), ignore)
    | Pgvector ->
        match Environment.GetEnvironmentVariable PgvectorVariable with
        | null
        | "" ->
            Error
                $"the 'pgvector' backend was requested but {PgvectorVariable} is not set — start a local Postgres with the pgvector extension and point it there"
        | connectionString ->
            let options = {
                PgvectorVectorStore.PgvectorOptions.forDimensions dimensions with
                    Table = "toolup_load_" + Guid.NewGuid().ToString("N").Substring(0, 12)
                    AnnIndex = PgvectorVectorStore.HnswAnnIndex(16, 64)
            }

            // Phase 929 — the run's table is dropped when the run ends, so a
            // local database the harness is pointed at again and again does
            // not accumulate a table per run.
            let drop () =
                try
                    use dataSource = Npgsql.NpgsqlDataSource.Create connectionString
                    use command = dataSource.CreateCommand($"DROP TABLE IF EXISTS {options.Table}")
                    command.ExecuteNonQuery() |> ignore
                with ex ->
                    eprintfn "[load] could not drop %s: %s" options.Table ex.Message

            Ok(PgvectorVectorStore.create connectionString options (Some logger), drop)

/// Run one retrieval cell. Seeds `Chunks` synthetic chunks straight into
/// the vector store and the BM25 index (embedding through the local
/// provider in batches — seeding through `IRetrievalPipeline.Index` one
/// chunk at a time is the sequential shape this phase replaces), composes
/// the production hybrid pipeline over them, meters it, warms it, and
/// then runs `Rounds` closed-loop rounds.
let runRetrievalCell (cell: RetrievalCell) : Async<Result<RetrievalResult, string>> = async {
    match resolveBlobArm cell.BlobArm with
    | Error e -> return Error e
    | Ok arm ->
        try
            let counting = CountingBlobStorage(arm.Storage)
            let storage = counting :> IBlobStorage
            let embedder = LocalEmbeddingProvider.create ()

            match buildStore cell.Backend storage embedder.Dimensions with
            | Error e -> return Error e
            | Ok(store, dropStore) ->
                let sparse =
                    new InMemoryBM25Index(storage, (SilentLogger() :> ILogger), flushIntervalMs = 50) :> ISparseIndex

                // Seed.
                let seedClock = Stopwatch.StartNew()
                let keepVectors = cell.RecallQueries > 0
                let vectors = if keepVectors then Array.zeroCreate cell.Chunks else [||]
                let batch = 512

                for start in 0..batch .. cell.Chunks - 1 do
                    let count = min batch (cell.Chunks - start)
                    let texts = Array.init count (fun j -> chunkText cell.Seed (start + j))
                    let! embedded = embedder.GenerateEmbeddings texts

                    let rows = [
                        for j in 0 .. count - 1 ->
                            let chunk: TextChunk = {
                                Content = texts[j]
                                Metadata = Map.empty
                            }

                            sprintf "c%07d" (start + j), embedded[j], chunk
                    ]

                    // Phase 946 — one batched write per embedding batch
                    // (`IVectorStoreBatch`, one round-trip on pgvector)
                    // where the store offers it, one `Upsert` per chunk
                    // where it does not. Seeding pgvector one upsert at a
                    // time took 1,002 s for 100,000 chunks.
                    do! upsertBatch store scope rows

                    for chunkId, _, chunk in rows do
                        do! sparse.Upsert scope chunkId chunk

                    if keepVectors then
                        for j in 0 .. count - 1 do
                            vectors[start + j] <- embedded[j]

                seedClock.Stop()

                // Compose the production hybrid pipeline and meter it.
                let telemetry = RagTelemetry.createRolling 3600

                let cache =
                    new InMemoryEmbeddingCache() :> ToolUp.Platform.IEmbeddingCache.IEmbeddingCache

                let pipeline =
                    RetrievalPipeline(
                        store = store,
                        embedder = create embedder cache,
                        sparseIndex = sparse,
                        options = RetrievalPipelineOptions.defaults,
                        tracer = RetrievalTracers.createNoOp (),
                        telemetry = telemetry
                    )
                    :> IRetrievalPipeline

                let metered = RagTelemetry.meter telemetry pipeline
                let measured = metered :> IRetrievalPipeline

                let request (text: string) : RetrievalRequest = {
                    Query = text
                    Scopes = [ scope ]
                    TopK = 10
                    Merge = Interleaved
                    Filters = None
                    History = None
                    AdaptiveK = None
                    OriginFilter = None
                    ActiveModule = None
                    FactClause = None
                }

                let queries =
                    Array.init (cell.QueriesPerRound * cell.Rounds) (fun i -> queryText cell.Seed i cell.Chunks)

                // Warm: the index load, the HNSW graph build and the JIT are
                // paid here, outside every measured round.
                for i in 0 .. min 20 (queries.Length - 1) do
                    let! _ = measured.Retrieve (request (queryText (cell.Seed + 1) i cell.Chunks)) EvalCore.evalContext
                    ()

                counting.Reset()
                let all = ResizeArray<float>()
                let roundP95s = ResizeArray<float>()
                let mutable wallSeconds = 0.0

                for round in 0 .. cell.Rounds - 1 do
                    let offset = round * cell.QueriesPerRound

                    let latencies, wall =
                        runClosedLoop cell.Concurrency cell.QueriesPerRound (fun i -> async {
                            let! matches = measured.Retrieve (request queries[offset + i]) EvalCore.evalContext

                            if List.isEmpty matches then
                                failwithf "query %d returned nothing — the corpus is not being searched" (offset + i)
                        })

                    all.AddRange latencies
                    roundP95s.Add (summarise latencies).P95
                    wallSeconds <- wallSeconds + wall

                let measuredQueries = float (cell.QueriesPerRound * cell.Rounds)
                let reads = float counting.Downloads / measuredQueries
                let lists = float counting.Lists / measuredQueries
                let! snapshot = telemetry.Snapshot()

                let telemetryTotal =
                    snapshot.RetrievalStageP95Ms
                    |> List.tryFind (fun (stage, _) -> stage = RagTelemetry.TotalStage)
                    |> Option.map snd
                    |> Option.defaultValue 0.0

                // Recall of the STORE's own search (not the fused pipeline)
                // against the exact scan over the same vectors.
                let! recall = async {
                    if not keepVectors then
                        return None
                    else
                        let mutable hits = 0
                        let mutable possible = 0

                        for i in 0 .. cell.RecallQueries - 1 do
                            let! qv = embedder.GenerateEmbedding(queryText (cell.Seed + 2) i cell.Chunks)
                            let truth = cosineTop 10 vectors qv |> Set.ofList
                            let! found = store.Search [ scope ] qv 10
                            hits <- hits + (found |> List.filter (fun m -> truth.Contains m.ChunkId) |> List.length)
                            possible <- possible + truth.Count

                        return Some(float hits / float (max 1 possible))
                }

                match store with
                | :? IDisposable as disposable -> disposable.Dispose()
                | _ -> ()

                dropStore ()
                arm.Cleanup()

                return
                    Ok {
                        Cell = cell
                        BlobLabel = arm.Label
                        SeedSeconds = seedClock.Elapsed.TotalSeconds
                        Latency = summarise (all.ToArray())
                        RoundP95s = roundP95s.ToArray()
                        ThroughputQps = measuredQueries / max 1e-9 wallSeconds
                        BlobReadsPerQuery = reads
                        BlobListsPerQuery = lists
                        PeakInFlight = metered.PeakInFlight
                        TelemetryTotalP95Ms = telemetryTotal
                        RecallAt10 = recall
                    }
        with ex ->
            arm.Cleanup()
            return Error $"{backendLabel cell.Backend}/{cell.BlobArm} at {cell.Chunks} chunks failed: {ex.Message}"
}

/// One printed row. Every number names its backend, corpus size,
/// concurrency and statistic.
let renderRetrieval (r: RetrievalResult) =
    let recall =
        match r.RecallAt10 with
        | Some v -> sprintf "recall@10 vs flat scan %.3f (%d queries)" v r.Cell.RecallQueries
        | None -> "recall not measured"

    sprintf
        "retrieval | store=%s blob=%s | chunks=%d | concurrency=%d | queries=%d x %d rounds | total latency ms p50=%.2f p95=%.2f p99=%.2f max=%.2f | min-over-rounds p95=%.2f | throughput=%.1f q/s | blob reads/query=%.2f lists/query=%.2f | peak in-flight=%d | telemetry Total p95=%.2f | seed=%.1fs | %s"
        (backendLabel r.Cell.Backend)
        r.BlobLabel
        r.Cell.Chunks
        r.Cell.Concurrency
        r.Cell.QueriesPerRound
        r.Cell.Rounds
        r.Latency.P50
        r.Latency.P95
        r.Latency.P99
        r.Latency.Max
        (Array.min r.RoundP95s)
        r.ThroughputQps
        r.BlobReadsPerQuery
        r.BlobListsPerQuery
        r.PeakInFlight
        r.TelemetryTotalP95Ms
        r.SeedSeconds
        recall

/// The retrieval gate's configuration: what `perf-budget-gate.ps1` runs on
/// every push. Small enough for a shared CI runner; the full matrix is
/// `load retrieval`, run by hand and recorded in docs/rag/performance.md.
let gateRetrievalCell: RetrievalCell = {
    Backend = Flat
    BlobArm = "memory"
    Chunks = 10_000
    Concurrency = 8
    QueriesPerRound = 200
    Rounds = 5
    RecallQueries = 0
    Seed = 886
}

/// The gated samples one retrieval result yields.
let retrievalGateSamples (r: RetrievalResult) : GateSample list =
    let observed = r.Latency.Count = r.Cell.QueriesPerRound * r.Cell.Rounds
    let lo, median, hi = roundStatistics r.RoundP95s

    let where =
        $"store={backendLabel r.Cell.Backend} blob={r.BlobLabel} chunks={r.Cell.Chunks} concurrency={r.Cell.Concurrency} queries={r.Cell.QueriesPerRound}x{r.Cell.Rounds}"

    [
        {
            Metric = "retrievalP95Ms"
            Value = lo
            Rounds = r.RoundP95s.Length
            Observed = observed
            Evidence = $"p95 TOTAL latency per round, min over rounds — {where}; {r.Latency.Count} queries completed"
            Median = median
            Max = hi
        }
        {
            Metric = "retrievalBlobReadsPerQuery"
            Value = r.BlobReadsPerQuery
            Rounds = r.RoundP95s.Length
            Observed = observed
            Evidence = $"blob downloads per measured query (after warm-up) — {where}"
            Median = r.BlobReadsPerQuery
            Max = r.BlobReadsPerQuery
        }
    ]