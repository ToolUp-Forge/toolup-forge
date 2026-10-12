module ToolUp.RAG.Benchmarks.HnswScaleBenchmark

open System
open System.Diagnostics
open System.IO
open System.Numerics
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.VectorStores.Hnsw
open ToolUp.RAG.Benchmarks.BeirTypes
open ToolUp.RAG.Benchmarks.LatencyMetrics

// ─── Phase 970 — HNSW at representative scale ──────────────────────
//
// Phase 14k set two bounds for the HNSW companion and deferred both for
// want of a representative corpus: recall@10 within 2 % of a flat scan,
// and p95 query latency under 50 ms at 500k chunks. This runner measures
// both against a BEIR corpus:
//
//   1. the corpus and the queries are embedded ONCE with the local
//      embedder and cached to disk beside the dataset, so a rerun (or an
//      `efSearch` sweep) never re-embeds;
//   2. the corpus is optionally padded to `--pad-to N` chunks with
//      deterministic synthetic vectors at the same dimensionality and the
//      same sparsity as the embedded corpus;
//   3. the exact top-K is computed by a flat scan over every vector, one
//      query at a time on one thread and timed — the
//      ranking `InMemoryVectorStore` returns (cosine over unit vectors,
//      ties broken by chunk id);
//   4. every requested `efSearch` builds a fresh `HnswVectorStore` over
//      the same vectors and runs every query through
//      `IVectorStore.Search`, timing each one.
//
// Recall is the ann-benchmarks definition: an HNSW result counts when its
// cosine is at least the K-th exact cosine (less a 1e-6 tolerance). A set
// overlap would score a tie between two identical vectors — BEIR corpora
// carry duplicate documents — as a miss although both are exact answers.
//
// The run is opt-in and never part of a test pack: at 500k chunks it holds
// several GB, and its HNSW graph build alone runs for hours (`--flat-only`
// skips it). `HnswFidelityTests` pins the
// outcome behind `TOOLUP_HNSW_SCALE_CORPUS`.

/// The pseudo-dataset that skips BEIR entirely: every chunk and every
/// query is synthetic. Useful to measure latency on a machine with no
/// dataset cached; it says nothing about recall on real text.
[<Literal>]
let SyntheticDataset = "synthetic"

type ScaleConfig = {
    /// A BEIR dataset id (`scifact`, `nfcorpus`, `fiqa`, …) or `synthetic`.
    Dataset: string
    /// Pad the corpus to this many chunks with synthetic vectors; `0` = no padding.
    PadTo: int
    /// One store build per value; empty = the store's default only.
    EfSearch: int list
    /// Neighbour budget; `None` = the store's default.
    M: int option
    /// Cap on the number of queries timed (taken in dataset order).
    MaxQueries: int option
    TopK: int
    /// Measure the flat scan only and build no HNSW graph — the comparison
    /// rung at sizes whose graph build is too slow to wait for.
    FlatOnly: bool
    /// Where the JSON report is written.
    ReportPath: string
}

/// Ids in index order plus their unit vectors, row-major in one array so a
/// 500k-chunk corpus is one allocation rather than 500k.
type EmbeddedSet = {
    Ids: string[]
    Dim: int
    Vectors: float32[]
}

module EmbeddedSet =
    let row (set: EmbeddedSet) (i: int) : float32[] =
        Array.sub set.Vectors (i * set.Dim) set.Dim

    let count (set: EmbeddedSet) = set.Ids.Length

    /// Mean count of non-zero slots per vector — the sparsity the synthetic
    /// padding reproduces.
    let meanNonZero (set: EmbeddedSet) : int =
        if set.Ids.Length = 0 then
            0
        else
            let mutable nz = 0L

            for x in set.Vectors do
                if x <> 0.0f then
                    nz <- nz + 1L

            int (Math.Round(float nz / float set.Ids.Length))

// ─── Vector arithmetic ────────────────────────────────────────────

let private normaliseInPlace (v: float32[]) =
    let mutable sq = 0.0

    for x in v do
        sq <- sq + float x * float x

    let mag = float32 (sqrt sq)

    if mag > 0.0f then
        for i in 0 .. v.Length - 1 do
            v[i] <- v[i] / mag

/// Dot product of corpus row `offset` (in `vectors`) with `q`.
let private dotAt (vectors: float32[]) (offset: int) (q: float32[]) : float32 =
    let dim = q.Length
    let width = Vector<float32>.Count
    let mutable acc = Vector<float32>.Zero
    let mutable j = 0

    while j <= dim - width do
        acc <- acc + Vector<float32>(vectors, offset + j) * Vector<float32>(q, j)
        j <- j + width

    let mutable s = Vector.Dot(acc, Vector<float32>.One)

    while j < dim do
        s <- s + vectors[offset + j] * q[j]
        j <- j + 1

    s

let private gaussian (rng: Random) : float =
    let u1 = max 1e-12 (rng.NextDouble())
    let u2 = rng.NextDouble()
    sqrt (-2.0 * log u1) * cos (2.0 * Math.PI * u2)

// ─── Synthetic vectors ────────────────────────────────────────────

/// The seed every synthetic vector derives from. A vector is a function of
/// `(seed, index)` alone, so the padded corpus is identical on every run and
/// every machine (`System.Random(int)` is the stable legacy generator).
[<Literal>]
let SyntheticSeed = 970

/// A deterministic synthetic unit vector: `nonZero` slots drawn uniformly
/// over `dim`, Gaussian weights, normalised. Hashed TF-IDF vectors are
/// sparse in exactly this way — a document touches as many slots as it has
/// distinct terms — so padding at the corpus's own sparsity keeps the
/// padded index's geometry close to the real one's.
let syntheticVector (index: int) (dim: int) (nonZero: int) : float32[] =
    let rng = Random(SyntheticSeed * 1_000_003 + index)
    let v = Array.zeroCreate<float32> dim

    for _ in 1 .. max 1 nonZero do
        let slot = rng.Next dim
        v[slot] <- v[slot] + float32 (gaussian rng)

    normaliseInPlace v
    v

/// A synthetic query near corpus vector `anchor`: its weights perturbed and
/// a few slots added, so the query has a true near neighbour but is not a
/// copy of it.
let private syntheticQuery (index: int) (anchor: float32[]) : float32[] =
    let rng = Random(SyntheticSeed * 7_919 + index)
    let v = Array.copy anchor

    for i in 0 .. v.Length - 1 do
        if v[i] <> 0.0f then
            v[i] <- v[i] + float32 (0.3 * gaussian rng / sqrt (float v.Length))

    for _ in 1..8 do
        let slot = rng.Next v.Length
        v[slot] <- v[slot] + float32 (0.05 * gaussian rng)

    normaliseInPlace v
    v

let private syntheticId (index: int) = sprintf "synthetic-%07d" index

// ─── Embedding + the on-disk cache ────────────────────────────────

[<Literal>]
let private CacheMagic = "toolup-hnsw-scale-embeddings-v1"

let private writeSet (w: BinaryWriter) (set: EmbeddedSet) =
    w.Write set.Ids.Length

    for id in set.Ids do
        w.Write id

    let bytes = MemoryMarshal.AsBytes(ReadOnlySpan<float32>(set.Vectors))
    w.Write bytes

let private readSet (r: BinaryReader) (dim: int) : EmbeddedSet =
    let n = r.ReadInt32()
    let ids = Array.init n (fun _ -> r.ReadString())
    let vectors = Array.zeroCreate<float32> (n * dim)
    let bytes = MemoryMarshal.AsBytes(Span<float32>(vectors))
    let mutable read = 0

    while read < bytes.Length do
        let got = r.Read(bytes.Slice read)

        if got = 0 then
            raise (EndOfStreamException "embedding cache truncated")

        read <- read + got

    {
        Ids = ids
        Dim = dim
        Vectors = vectors
    }

let private writeCache (path: string) (modelId: string) (corpus: EmbeddedSet) (queries: EmbeddedSet) =
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    let temp = path + ".tmp"

    do
        use w = new BinaryWriter(File.Create temp)
        w.Write CacheMagic
        w.Write modelId
        w.Write corpus.Dim
        writeSet w corpus
        writeSet w queries

    File.Move(temp, path, overwrite = true)

/// The cached embeddings, when the file exists, was written for this model
/// and holds exactly the expected corpus and query ids in order. Anything
/// else re-embeds: a cache that disagrees with the dataset is stale, never
/// authoritative.
let private tryReadCache
    (path: string)
    (modelId: string)
    (corpusIds: string[])
    (queryIds: string[])
    : (EmbeddedSet * EmbeddedSet) option =
    if not (File.Exists path) then
        None
    else
        try
            use r = new BinaryReader(File.OpenRead path)

            if r.ReadString() <> CacheMagic || r.ReadString() <> modelId then
                None
            else
                let dim = r.ReadInt32()
                let corpus = readSet r dim
                let queries = readSet r dim

                if corpus.Ids = corpusIds && queries.Ids = queryIds then
                    Some(corpus, queries)
                else
                    None
        with _ ->
            None

let private embedAll (embedder: IEmbeddingProvider) (ids: string[]) (texts: string[]) : Async<EmbeddedSet> = async {
    let dim = embedder.Dimensions
    let vectors = Array.zeroCreate<float32> (ids.Length * dim)

    for i in 0 .. texts.Length - 1 do
        let! v = embedder.GenerateEmbedding texts[i]
        // Stored unit-normalised so the flat scan's dot product IS cosine.
        let unit = Array.copy v
        normaliseInPlace unit
        Array.blit unit 0 vectors (i * dim) dim

        if (i + 1) % 10_000 = 0 then
            printfn "[hnsw-scale] embedded %d / %d" (i + 1) texts.Length

    return {
        Ids = ids
        Dim = dim
        Vectors = vectors
    }
}

/// Embed a BEIR dataset's corpus and queries with the local embedder, read
/// from the disk cache when it holds them. The corpus is embedded first and
/// the queries after it, in file order — the local embedder's IDF state
/// moves with every text it sees, so the order is part of what the cache
/// pins.
let embedDataset (ds: BeirDataset) : Async<EmbeddedSet * EmbeddedSet * IEmbeddingProvider> = async {
    let embedder = LocalEmbeddingProvider.create ()
    let path = BeirLoader.embeddingCachePath ds.Name embedder.ModelId
    let corpusDocs = List.toArray ds.Corpus
    let corpusIds = corpusDocs |> Array.map _.Id
    let queryArr = List.toArray ds.Queries
    let queryIds = queryArr |> Array.map _.Id

    match tryReadCache path embedder.ModelId corpusIds queryIds with
    | Some(corpus, queries) ->
        printfn "[hnsw-scale] embeddings read from %s" path
        return corpus, queries, embedder
    | None ->
        printfn "[hnsw-scale] embedding %d documents + %d queries…" corpusIds.Length queryIds.Length
        let! corpus = embedAll embedder corpusIds (corpusDocs |> Array.map BeirLoader.chunkContent)
        let! queries = embedAll embedder queryIds (queryArr |> Array.map _.Text)
        writeCache path embedder.ModelId corpus queries
        printfn "[hnsw-scale] embeddings cached to %s" path
        return corpus, queries, embedder
}

// ─── Corpus assembly ──────────────────────────────────────────────

/// Append synthetic rows to `real` until it holds `padTo` chunks.
let pad (real: EmbeddedSet) (padTo: int) (nonZero: int) : EmbeddedSet =
    let n = EmbeddedSet.count real

    if padTo <= n then
        real
    else
        let vectors = Array.zeroCreate<float32> (padTo * real.Dim)
        Array.blit real.Vectors 0 vectors 0 real.Vectors.Length

        Parallel.For(
            n,
            padTo,
            fun i -> Array.blit (syntheticVector i real.Dim nonZero) 0 vectors (i * real.Dim) real.Dim
        )
        |> ignore

        {
            Ids = Array.append real.Ids (Array.init (padTo - n) (fun k -> syntheticId (n + k)))
            Dim = real.Dim
            Vectors = vectors
        }

/// The synthetic-only corpus and queries: `size` chunks, and one query per
/// `size / queryCount` stride anchored on a corpus vector.
let syntheticCorpus (size: int) (dim: int) (nonZero: int) (queryCount: int) : EmbeddedSet * EmbeddedSet =
    let empty = {
        Ids = [||]
        Dim = dim
        Vectors = [||]
    }

    let corpus = pad empty size nonZero
    let stride = max 1 (size / max 1 queryCount)

    let queries =
        Array.init queryCount (fun qi -> syntheticQuery qi (EmbeddedSet.row corpus ((qi * stride) % size)))

    corpus,
    {
        Ids = Array.init queryCount (sprintf "synthetic-query-%04d")
        Dim = dim
        Vectors = Array.concat queries
    }

// ─── The flat scan ────────────────────────────────────────────────

/// The exact top-K of one query: (corpus index, cosine) pairs, best first,
/// ties broken by chunk id — `InMemoryVectorStore`'s ranking.
let exactTopK (corpus: EmbeddedSet) (q: float32[]) (k: int) : (int * float32)[] =
    let best = ResizeArray<int * float32>(k + 1)

    let better (i: int, s: float32) (j: int, t: float32) =
        s > t || (s = t && String.CompareOrdinal(corpus.Ids[i], corpus.Ids[j]) < 0)

    for i in 0 .. EmbeddedSet.count corpus - 1 do
        let s = dotAt corpus.Vectors (i * corpus.Dim) q

        if best.Count < k || better (i, s) best[best.Count - 1] then
            let mutable at = best.Count

            while at > 0 && better (i, s) best[at - 1] do
                at <- at - 1

            best.Insert(at, (i, s))

            if best.Count > k then
                best.RemoveAt k

    best.ToArray()

/// The ann-benchmarks recall of one HNSW answer against the exact one.
let private recallOf (exact: (int * float32)[]) (hnswScores: float list) (k: int) : float =
    if exact.Length = 0 then
        1.0
    else
        let threshold = float (snd exact[exact.Length - 1]) - 1e-6
        let hits = hnswScores |> List.filter (fun s -> s >= threshold) |> List.length
        float (min hits k) / float (min k exact.Length)

// ─── One HNSW pass ────────────────────────────────────────────────

/// The measured outcome of one `HnswVectorStore` configuration.
type HnswRun = {
    M: int
    EfSearch: int
    LevelLambda: float
    NeighbourHeuristic: string
    ExpandBestSelection: bool
    KeepPrunedConnections: bool
    /// `HNSW.Net`'s `ConstructionPruning` — the build-time candidate list,
    /// its efConstruction. The store leaves it at the library default.
    ConstructionPruning: int
    IngestSeconds: float
    GraphBuildSeconds: float
    RecallAt10: float
    QueriesTimed: int
    P50Ms: int
    P95Ms: int
    P99Ms: int
    AvgMs: float
    /// The store's flush of the whole scope (it runs on `Dispose`): one
    /// JSON serialisation of every entry. A deployment pays it on every
    /// flush interval while the scope is dirty.
    PersistSeconds: float
}

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private scope = Deployment

let runHnsw
    (corpus: EmbeddedSet)
    (queries: EmbeddedSet)
    (exact: (int * float32)[][])
    (parameters: HnswVectorStore.HnswParameters)
    (topK: int)
    : Async<HnswRun> =
    async {
        let tempDir =
            Path.Combine(Path.GetTempPath(), "rag-bench-hnsw-scale-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore

        // The flush interval is effectively never: the store persists a scope
        // as ONE JSON document, and re-serialising a 500k-chunk scope every
        // two seconds of ingestion would time the persistence, not the index.
        // The single flush `Dispose` performs is timed as `PersistSeconds`.
        let store =
            new HnswVectorStore.HnswVectorStore(
                LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage,
                SilentLogger() :> ILogger,
                flushIntervalMs = Int32.MaxValue,
                parameters = parameters
            )

        let mutable disposed = false

        try
            let vs = store :> IVectorStore
            let ingest = Stopwatch.StartNew()

            for i in 0 .. EmbeddedSet.count corpus - 1 do
                let chunk: TextChunk = {
                    Content = corpus.Ids[i]
                    Metadata = Map.empty
                }

                do! vs.Upsert scope corpus.Ids[i] (EmbeddedSet.row corpus i) chunk

            ingest.Stop()

            // The graph builds lazily on the first search; time it alone so
            // the latency samples measure steady-state queries.
            let build = Stopwatch.StartNew()
            let! _ = vs.Search [ scope ] (EmbeddedSet.row queries 0) topK
            build.Stop()

            let latencies = ResizeArray<int64>()
            let recalls = ResizeArray<float>()

            for qi in 0 .. EmbeddedSet.count queries - 1 do
                let q = EmbeddedSet.row queries qi
                let sw = Stopwatch.StartNew()
                let! hits = vs.Search [ scope ] q topK
                sw.Stop()
                latencies.Add sw.ElapsedMilliseconds
                recalls.Add(recallOf exact[qi] (hits |> List.map _.Score) topK)

            let latency = profile (List.ofSeq latencies)
            // Disposing is the store's final flush: one serialisation and
            // write of the whole scope. A failure there is the store's to log.
            let persist = Stopwatch.StartNew()
            disposed <- true
            (store :> IDisposable).Dispose()
            persist.Stop()

            return {
                M = parameters.M
                EfSearch = parameters.EfSearch
                LevelLambda = parameters.LevelLambda
                NeighbourHeuristic = string parameters.NeighbourHeuristic
                ExpandBestSelection = parameters.ExpandBestSelection
                KeepPrunedConnections = parameters.KeepPrunedConnections
                ConstructionPruning = HNSW.Net.SmallWorldParameters().ConstructionPruning
                IngestSeconds = ingest.Elapsed.TotalSeconds
                GraphBuildSeconds = build.Elapsed.TotalSeconds
                RecallAt10 = Seq.average recalls
                QueriesTimed = latencies.Count
                P50Ms = int latency.P50
                P95Ms = int latency.P95
                P99Ms = int latency.P99
                AvgMs = latency.Avg
                PersistSeconds = persist.Elapsed.TotalSeconds
            }
        finally
            if not disposed then
                (store :> IDisposable).Dispose()

            try
                Directory.Delete(tempDir, recursive = true)
            with _ ->
                ()
    }

// ─── The report ───────────────────────────────────────────────────

type MachineInfo = {
    Os: string
    Processor: string
    LogicalCores: int
    MemoryGb: float
    Runtime: string
}

type ScaleReport = {
    Phase: string
    StartedAtUtc: DateTime
    Dataset: string
    BeirChunks: int
    SyntheticChunks: int
    TotalChunks: int
    Queries: int
    Embedder: string
    Dimensions: int
    SyntheticSeed: int
    SyntheticNonZeroSlots: int
    TopK: int
    Machine: MachineInfo
    /// The single-threaded exact scan over the same corpus and queries.
    FlatP50Ms: int
    FlatP95Ms: int
    FlatP99Ms: int
    Runs: HnswRun list
    WallClockSeconds: float
}

let machine () : MachineInfo =
    let processor =
        match Environment.GetEnvironmentVariable "PROCESSOR_IDENTIFIER" with
        | null
        | "" -> string RuntimeInformation.ProcessArchitecture
        | p -> p

    {
        Os = RuntimeInformation.OSDescription
        Processor = processor
        LogicalCores = Environment.ProcessorCount
        MemoryGb = Math.Round(float (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) / 1073741824.0, 1)
        Runtime = RuntimeInformation.FrameworkDescription
    }

let private jsonOptions =
    let o = FableConverters.create ()
    o.WriteIndented <- true
    o

let writeReport (path: string) (report: ScaleReport) =
    let dir = Path.GetDirectoryName path

    if not (String.IsNullOrEmpty dir) then
        Directory.CreateDirectory dir |> ignore

    File.WriteAllText(path, JsonSerializer.Serialize(report, jsonOptions))

// ─── Entry ────────────────────────────────────────────────────────

/// The synthetic-only corpus has no embedder to take a dimensionality or a
/// sparsity from, so it uses the local embedder's: 512 hashed slots.
[<Literal>]
let private SyntheticOnlyDim = 512

[<Literal>]
let private SyntheticOnlyNonZero = 64

[<Literal>]
let private SyntheticOnlyQueries = 300

let run (cfg: ScaleConfig) : Async<ScaleReport> = async {
    let wall = Stopwatch.StartNew()
    let startedAt = DateTime.UtcNow

    let! real, queries, embedderLabel, nonZero = async {
        if cfg.Dataset = SyntheticDataset then
            let corpus, queries =
                syntheticCorpus (max cfg.PadTo 1) SyntheticOnlyDim SyntheticOnlyNonZero SyntheticOnlyQueries

            return corpus, queries, "none (synthetic vectors)", SyntheticOnlyNonZero
        else
            let! ds = BeirLoader.load cfg.Dataset
            let! corpus, queries, embedder = embedDataset ds
            return corpus, queries, sprintf "%s/%s" embedder.ProviderId embedder.ModelId, EmbeddedSet.meanNonZero corpus
    }

    let beirChunks =
        if cfg.Dataset = SyntheticDataset then
            0
        else
            EmbeddedSet.count real

    let corpus = pad real cfg.PadTo nonZero

    let queries =
        match cfg.MaxQueries with
        | Some n when n < EmbeddedSet.count queries -> {
            queries with
                Ids = Array.truncate n queries.Ids
                Vectors = Array.sub queries.Vectors 0 (n * queries.Dim)
          }
        | _ -> queries

    printfn
        "[hnsw-scale] corpus %d chunks (%d BEIR + %d synthetic, %d non-zero slots), %d queries, dim %d"
        (EmbeddedSet.count corpus)
        beirChunks
        (EmbeddedSet.count corpus - beirChunks)
        nonZero
        (EmbeddedSet.count queries)
        corpus.Dim

    // The flat scan, one query at a time on one thread — the exact answer
    // every recall figure is measured against, and the latency an
    // in-memory linear scan pays at this corpus size.
    let flatLatencies = ResizeArray<int64>()

    let exact =
        Array.init (EmbeddedSet.count queries) (fun qi ->
            let sw = Stopwatch.StartNew()
            let top = exactTopK corpus (EmbeddedSet.row queries qi) cfg.TopK
            sw.Stop()
            flatLatencies.Add sw.ElapsedMilliseconds
            top)

    let flat = profile (List.ofSeq flatLatencies)
    printfn "[hnsw-scale] flat scan p50/p95/p99 %d/%d/%d ms" flat.P50 flat.P95 flat.P99

    let defaults = HnswVectorStore.HnswParameters.defaults

    let baseParams =
        match cfg.M with
        | Some m -> {
            defaults with
                M = m
                LevelLambda = 1.0 / log (float m)
          }
        | None -> defaults

    let efs =
        if cfg.FlatOnly then []
        elif cfg.EfSearch.IsEmpty then [ baseParams.EfSearch ]
        else cfg.EfSearch

    let runs = ResizeArray<HnswRun>()

    for ef in efs do
        let p = { baseParams with EfSearch = ef }
        printfn "[hnsw-scale] building HNSW (M=%d, efSearch=%d)…" p.M ef
        let! r = runHnsw corpus queries exact p cfg.TopK

        printfn
            "[hnsw-scale]   ingest %.1f s · graph build %.1f s · recall@%d %.4f · p50/p95/p99 %d/%d/%d ms · persist %.1f s"
            r.IngestSeconds
            r.GraphBuildSeconds
            cfg.TopK
            r.RecallAt10
            r.P50Ms
            r.P95Ms
            r.P99Ms
            r.PersistSeconds

        runs.Add r
        GC.Collect()

    wall.Stop()

    let report = {
        Phase = "970"
        StartedAtUtc = startedAt
        Dataset = cfg.Dataset
        BeirChunks = beirChunks
        SyntheticChunks = EmbeddedSet.count corpus - beirChunks
        TotalChunks = EmbeddedSet.count corpus
        Queries = EmbeddedSet.count queries
        Embedder = embedderLabel
        Dimensions = corpus.Dim
        SyntheticSeed = SyntheticSeed
        SyntheticNonZeroSlots = nonZero
        TopK = cfg.TopK
        Machine = machine ()
        FlatP50Ms = int flat.P50
        FlatP95Ms = int flat.P95
        FlatP99Ms = int flat.P99
        Runs = List.ofSeq runs
        WallClockSeconds = wall.Elapsed.TotalSeconds
    }

    writeReport cfg.ReportPath report
    return report
}