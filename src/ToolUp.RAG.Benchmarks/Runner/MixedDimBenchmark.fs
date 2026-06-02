module ToolUp.RAG.Benchmarks.MixedDimBenchmark

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.VectorStores.Hnsw
open ToolUp.RAG.Benchmarks.LatencyMetrics
open ToolUp.RAG.Benchmarks.BenchmarkRunner

// ─── Phase 14k WS5 Item 3 — mixed-dim fixture latency benchmark ────
//
// Mirrors the loader at
// `src/ToolUp.Platform.Tests/Support/MixedDimFixture.fs` byte-for-byte so
// the test side and the benchmark side see identical corpora. Bypasses
// `IRetrievalPipeline` (which expects text content + an embedder) and
// drives `HnswVectorStore` directly with the pre-computed mixed-dim
// vectors — the only pipeline shape the recall-fidelity contract
// reaches into.
//
// Emits a `RunRow` in the same shape the BEIR runner uses. Quality
// columns (NdcgAt10 / RecallAt100 / MrrAt10) are 0.0 by construction —
// this runner measures p50/p95/p99 latency under a mixed-dim load, not
// retrieval quality.

// CLIMutable + STJ FableConverters deserialisation requires the record
// type to be public — F# `type private` emits non-public CLR members
// that reflection-based deserialisers silently skip.
[<CLIMutable>]
type FixtureMetadata = {
    name: string
    description: string
    seed: int
    scope: string
    textDim: int
    imageDim: int
    textCount: int
    imageCount: int
    queryCountPerKind: int
    queryNoiseScale: float
    queryAnchorStride: int
}

type ChunkKind =
    | Text
    | Image

type Chunk = {
    ChunkId: string
    Kind: ChunkKind
    Dim: int
    Vector: float32[]
}

type Query = {
    QueryId: string
    Kind: ChunkKind
    Vector: float32[]
}

let private normalise (v: float[]) : float32[] =
    let norm = sqrt (Array.sumBy (fun x -> x * x) v)
    v |> Array.map (fun x -> float32 (x / (norm + 1e-12)))

let private randomVec (rng: Random) (dim: int) : float32[] =
    Array.init dim (fun _ -> rng.NextDouble() - 0.5) |> normalise

let private gaussian (rng: Random) : float =
    let u1 = max 1e-12 (rng.NextDouble())
    let u2 = rng.NextDouble()
    sqrt (-2.0 * log u1) * cos (2.0 * Math.PI * u2)

let private perturb (rng: Random) (noiseScale: float) (anchor: float32[]) : float32[] =
    anchor |> Array.map (fun x -> float x + noiseScale * gaussian rng) |> normalise

let private idText i = sprintf "text-%03d" i
let private idImage i = sprintf "image-%03d" i

type private LoadedFixture = {
    Meta: FixtureMetadata
    Corpus: Chunk[]
    Queries: Query[]
}

let private loadFixture (path: string) : LoadedFixture =
    if not (File.Exists path) then
        failwithf "Mixed-dim fixture not found at %s" path

    let json = File.ReadAllText path
    let options = FableConverters.create ()
    let meta = JsonSerializer.Deserialize<FixtureMetadata>(json, options)
    let rng = Random meta.seed

    let textCorpus =
        Array.init meta.textCount (fun i -> {
            ChunkId = idText i
            Kind = Text
            Dim = meta.textDim
            Vector = randomVec rng meta.textDim
        })

    let imageCorpus =
        Array.init meta.imageCount (fun i -> {
            ChunkId = idImage i
            Kind = Image
            Dim = meta.imageDim
            Vector = randomVec rng meta.imageDim
        })

    let corpus = Array.append textCorpus imageCorpus

    let mkQueries (kind: ChunkKind) (sourceCorpus: Chunk[]) =
        Array.init meta.queryCountPerKind (fun qi ->
            let anchorIdx = (qi * meta.queryAnchorStride) % sourceCorpus.Length
            let anchor = sourceCorpus[anchorIdx]

            let kindTag =
                match kind with
                | Text -> "t"
                | Image -> "i"

            {
                QueryId = sprintf "q%s-%02d" kindTag qi
                Kind = kind
                Vector = perturb rng meta.queryNoiseScale anchor.Vector
            })

    let textQueries = mkQueries Text textCorpus
    let imageQueries = mkQueries Image imageCorpus

    {
        Meta = meta
        Corpus = corpus
        Queries = Array.append textQueries imageQueries
    }

// ─── Bench runner ─────────────────────────────────────────────────

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private parseScope (raw: string) : VectorScope =
    let trimmed = raw.Trim().ToLowerInvariant()

    if trimmed = "platform" then
        Platform
    elif trimmed = "deployment" then
        Deployment
    elif trimmed.StartsWith "team:" then
        Team(trimmed.Substring(5))
    else
        failwithf "Unknown scope: '%s' (expected 'platform' / 'deployment' / 'team:<id>')" raw

/// Drive the HNSW store with the fixture's pre-computed mixed-dim
/// vectors and emit a `RunRow`. Replicate iterates the full query set
/// `replicate` times so latency samples scale beyond the 20-query
/// fixture; quality metrics stay 0.0 (the run is a latency benchmark).
let run (fixturePath: string) (topK: int) (replicate: int) : Async<RunRow> = async {
    let wallClock = Stopwatch.StartNew()
    let fixture = loadFixture fixturePath
    let scope = parseScope fixture.Meta.scope

    let tempDir =
        Path.Combine(Path.GetTempPath(), "rag-bench-mixed-dim-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore

    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let logger = SilentLogger() :> ILogger

    use store =
        new HnswVectorStore.HnswVectorStore(storage, logger, flushIntervalMs = 50)

    try
        // 1. Seed the corpus (timed). One scope, mixed dims.
        let ingestSw = Stopwatch.StartNew()

        let metadata = Map.empty

        for c in fixture.Corpus do
            let chunk: TextChunk = {
                Content = c.ChunkId
                Metadata = metadata
            }

            do! (store :> IVectorStore).Upsert scope c.ChunkId c.Vector chunk

        ingestSw.Stop()

        // 2. Warm: pay the lazy graph build on one throw-away query so
        //    the latency samples below measure steady-state.
        let warmupQuery = fixture.Queries[0]
        let! _ = (store :> IVectorStore).Search [ scope ] warmupQuery.Vector topK

        // 3. Replicate-N over the fixture's queries.
        let queries =
            if replicate <= 1 then
                fixture.Queries
            else
                Array.init (fixture.Queries.Length * replicate) (fun i -> fixture.Queries[i % fixture.Queries.Length])

        let mutable latencies = []

        for q in queries do
            let sw = Stopwatch.StartNew()
            let! _ = (store :> IVectorStore).Search [ scope ] q.Vector topK
            sw.Stop()
            latencies <- sw.ElapsedMilliseconds :: latencies

        let latency = profile (List.rev latencies)

        wallClock.Stop()

        return {
            Dataset = "mixed-dim-text-image-500"
            Embedder = "n/a"
            VectorStore = "hnsw"
            Reranker = "off"
            Mmr = "off"
            TopK = topK
            Replicate = replicate
            QueriesEvaluated = queries.Length
            NdcgAt10 = 0.0
            RecallAt100 = 0.0
            MrrAt10 = 0.0
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