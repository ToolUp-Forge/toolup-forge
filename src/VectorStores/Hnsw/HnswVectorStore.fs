module ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore

open System
open System.Collections.Generic
open System.Threading
open Newtonsoft.Json
open HNSW.Net
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore

// ─── Per-store random generator ──────────────────────────────────
//
// `HNSW.Net.DefaultRandomGenerator.Instance` is a process-wide
// singleton wrapping a single `System.Random`. `System.Random` is
// documented as NOT thread-safe — concurrent `.Next()` calls from
// different threads corrupt its internal state and can throw
// `IndexOutOfRangeException` or `InvalidOperationException` from
// inside `Graph.AddItems`. The corruption typically surfaces as a
// `Graph.Core..ctor` failure when the next graph build pulls a bad
// random value.
//
// In a process running a single `HnswVectorStore`, the singleton is
// fine — the store serialises graph builds under `state.Lock`. But
// any consumer that constructs multiple stores concurrently (an
// Expecto test run with parallel `testList` execution being the
// canonical surfacing case) shares the singleton across threads, and
// the RNG races.
//
// Fix: each `HnswVectorStore` instance gets its own RNG. Internally
// thread-safe (the store's own `state.Lock` already serialises graph
// builds within one store, but the lock guarantees the RNG sees no
// cross-store races without depending on the consumer's discipline).

type private LocalRandomGenerator() =
    let rng = Random()
    let lockObj = obj ()

    interface IProvideRandomValues with
        member _.IsThreadSafe = true

        member _.NextFloat() =
            lock lockObj (fun () -> rng.NextSingle())

        member _.NextFloats(span: Span<single>) =
            // F# closures can't capture byref-like `Span<T>`, so
            // use explicit Monitor.Enter/Exit instead of `lock`.
            Monitor.Enter lockObj

            try
                for i in 0 .. span.Length - 1 do
                    span[i] <- rng.NextSingle()
            finally
                Monitor.Exit lockObj

        member _.Next(minValue: int, maxValue: int) =
            lock lockObj (fun () -> rng.Next(minValue, maxValue))

// ─── Persisted entry shape ────────────────────────────────────────
//
// We persist `(chunkId, vector, content, metadata)` per scope as JSON
// (same on-disk shape as `InMemoryVectorStore`'s index). The HNSW graph
// itself is rebuilt from this list on first search after staleness —
// graph build for ~50k entries is sub-second and amortises across the
// thousands of queries that follow before the next ingest batch.
//
// Persisting the JSON (rather than the graph's own binary form) means
// the on-disk shape is portable between vector-store implementations:
// a future `pgvector` or `Qdrant` companion can import the same JSON
// without HNSW.Net's serialiser being involved. The trade-off is one
// graph rebuild per cold load, paid lazily on the first search of each
// scope after process start.

[<CLIMutable>]
type IndexEntry = {
    ChunkId: string
    Vector: float32 array
    Content: string
    Metadata: Map<string, string>
}

// ─── Scope key + blob name ────────────────────────────────────────

let private scopeToKey (scope: VectorScope) =
    match scope with
    | Platform -> "platform"
    | Deployment -> "deployment"
    | Team teamId -> $"team:{teamId}"

let private blobName (scope: VectorScope) =
    $"_rag/{scopeToKey scope}/hnsw-index.json"

let private scopeFromKey (sk: string) =
    if sk = "platform" then Platform
    elif sk = "deployment" then Deployment
    else Team(sk.Substring("team:".Length))

// ─── Vector math ──────────────────────────────────────────────────

let private magnitude (v: float32 array) =
    let mutable sum = 0.0f

    for x in v do
        sum <- sum + x * x

    sqrt sum

let private normalise (v: float32 array) =
    let m = magnitude v
    if m = 0.0f then v else Array.map (fun x -> x / m) v

let private toDoubleArray (v: float32 array) =
    let result = Array.zeroCreate<float> v.Length

    for i in 0 .. v.Length - 1 do
        result[i] <- float v[i]

    result

// ─── JSON helpers ─────────────────────────────────────────────────

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private toJson o =
    JsonConvert.SerializeObject(o, jsonSettings)

let private fromJson<'T> (s: string) =
    JsonConvert.DeserializeObject<'T>(s, jsonSettings)

// ─── Per-scope state ──────────────────────────────────────────────

/// Mutable per-scope state. `Lock` serialises mutators and graph rebuilds;
/// search reads under the same lock to avoid racing a partially-rebuilt
/// graph. The lock is per-scope, so concurrent searches across scopes
/// proceed in parallel — scope isolation is structural, not just contractual.
type private ScopeState() =
    let entries = Dictionary<string, IndexEntry>()
    let lockObj = obj ()
    let mutable graph: SmallWorld<float[], float> option = None
    let mutable graphIds: string array = Array.empty
    let mutable graphStale = true

    member _.Lock = lockObj
    member _.Entries = entries
    member _.Graph = graph
    member _.GraphIds = graphIds
    member _.GraphStale = graphStale

    member _.SetGraph(g: SmallWorld<float[], float>, ids: string array) =
        graph <- Some g
        graphIds <- ids
        graphStale <- false

    member _.MarkStale() = graphStale <- true

    member _.Clear() =
        entries.Clear()
        graph <- None
        graphIds <- Array.empty
        graphStale <- true

// ─── HNSW parameters ──────────────────────────────────────────────

/// HNSW build parameters. Defaults chosen for ≥0.98 recall vs flat scan
/// on the platform-readme smoke fixture. `M = 16` gives a comfortable
/// neighbour budget; `LevelLambda = 1 / ln M` is the canonical setting
/// from the original paper. Tune per deployment via the eval harness.
type HnswParameters = { M: int; LevelLambda: float }

module HnswParameters =
    let defaults: HnswParameters = { M = 16; LevelLambda = 1.0 / log 16.0 }

// ─── Cosine distance ──────────────────────────────────────────────
//
// Stored vectors are pre-normalised at upsert time, so cosine distance
// reduces to `1 - dot(a, b)`. The HNSW package's bundled cosine variants
// vary across versions; computing it ourselves keeps the implementation
// independent of which `CosineDistance.*` API the package exposes.

let private cosineDistance (a: float[]) (b: float[]) : float =
    if a.Length <> b.Length then
        // Dimension mismatch — a stale chunk from an embedding model /
        // provider swap. Truncating to the shorter length silently
        // produced a ranked-but-meaningless score; returning the maximum
        // distance (1.0) makes the mismatched vector simply not match
        // until it is re-embedded. Absent is recoverable; corrupt is not.
        1.0
    else
        let mutable dot = 0.0

        for i in 0 .. a.Length - 1 do
            dot <- dot + a[i] * b[i]

        1.0 - dot

// ─── Tombstone helpers ────────────────────────────────────────────

let private isTombstoned (entry: IndexEntry) =
    entry.Metadata.ContainsKey ChunkMetadata.DeletedAtKey

// ─── HnswVectorStore ──────────────────────────────────────────────

/// Approximate-nearest-neighbour vector store backed by HNSW (Curiosity
/// fork of Microsoft's HNSW.Net). Per-scope graph — scope isolation is
/// structural: a query against scope A consults only A's graph, so
/// cross-scope leakage is impossible by construction.
///
/// Persistence is debounced JSON-on-disk per scope (same shape and cadence
/// as `InMemoryVectorStore`). The HNSW graph itself is rebuilt lazily from
/// the persisted entries on first search of a scope after process start
/// or after a batch of mutations — the build is O(N log N) but dwarfed by
/// the thousands of queries that follow before the next batch.
///
/// Suitable for deployments up to ~1M chunks per scope. Above that, an
/// external companion (pgvector, Qdrant, Pinecone) is the standard next
/// step — the same `IVectorStore` interface and `composeWithRAG` wiring.
type HnswVectorStore(storage: IBlobStorage, ?logger: ILogger, ?flushIntervalMs: int, ?parameters: HnswParameters) =

    let log =
        logger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let flushMs = defaultArg flushIntervalMs 2000
    let hnswParams = defaultArg parameters HnswParameters.defaults

    let scopes = Dictionary<string, ScopeState>()
    let scopesLock = obj ()
    let dirty = HashSet<string>()
    let dirtyLock = obj ()
    let cts = new CancellationTokenSource()

    let getOrCreateScope (scopeKey: string) =
        lock scopesLock (fun () ->
            match scopes.TryGetValue scopeKey with
            | true, s -> s
            | _ ->
                let s = ScopeState()
                scopes[scopeKey] <- s
                s)

    let markDirty (scope: VectorScope) =
        lock dirtyLock (fun () -> dirty.Add(scopeToKey scope) |> ignore)

    let buildGraph (state: ScopeState) =
        // Caller must hold `state.Lock`.
        let live = state.Entries.Values |> Seq.filter (isTombstoned >> not) |> Seq.toArray

        if live.Length = 0 then
            state.SetGraph(Unchecked.defaultof<_>, Array.empty)
        else
            let vectors = live |> Array.map (fun e -> toDoubleArray (normalise e.Vector))

            let ids = live |> Array.map _.ChunkId

            let parameters = SmallWorldParameters()
            parameters.M <- hnswParams.M
            parameters.LevelLambda <- hnswParams.LevelLambda

            let graph =
                SmallWorld<float[], float>(
                    Func<float[], float[], float>(cosineDistance),
                    LocalRandomGenerator() :> IProvideRandomValues,
                    parameters,
                    threadSafe = true
                )

            graph.AddItems(vectors, null) |> ignore
            state.SetGraph(graph, ids)

    let ensureGraphFresh (state: ScopeState) =
        if state.GraphStale then
            buildGraph state

    let persistScope (scopeKey: string) = async {
        match scopes.TryGetValue scopeKey with
        | false, _ -> return ()
        | true, state ->
            let snapshot = lock state.Lock (fun () -> state.Entries.Values |> Seq.toList)

            let bytes = toJson snapshot |> System.Text.Encoding.UTF8.GetBytes
            let scope = scopeFromKey scopeKey
            let! result = storage.Upload("_rag", blobName scope, bytes)

            match result with
            | Ok _ -> ()
            | Error e ->
                log.Warn $"[HnswVectorStore] Failed to persist scope {scopeKey}: {e} — will retry on next flush."
                lock dirtyLock (fun () -> dirty.Add scopeKey |> ignore)
    }

    let flushAll () = async {
        let scopeKeys =
            lock dirtyLock (fun () ->
                let captured = dirty |> Seq.toList
                dirty.Clear()
                captured)

        for sk in scopeKeys do
            try
                do! persistScope sk
            with ex ->
                log.Error($"[HnswVectorStore] Flush failed for {sk}", Some ex)
                lock dirtyLock (fun () -> dirty.Add sk |> ignore)
    }

    let loadScope (scope: VectorScope) = async {
        let scopeKey = scopeToKey scope

        match! storage.Download("_rag", blobName scope) with
        | Ok bytes ->
            try
                let json = System.Text.Encoding.UTF8.GetString bytes
                let entries = fromJson<IndexEntry list> json
                let state = getOrCreateScope scopeKey

                lock state.Lock (fun () ->
                    state.Clear()

                    for e in entries do
                        // Re-normalise on load — historical indexes may
                        // predate upsert-time normalisation.
                        let normalised = { e with Vector = normalise e.Vector }

                        state.Entries[e.ChunkId] <- normalised

                    state.MarkStale())
            with ex ->
                log.Warn $"[HnswVectorStore] Corrupt index for {scopeKey}: {ex.Message} — starting empty."
        | Error _ -> ()
    }

    let ensureScopeLoaded (scope: VectorScope) = async {
        let scopeKey = scopeToKey scope

        let alreadyLoaded = lock scopesLock (fun () -> scopes.ContainsKey scopeKey)

        if not alreadyLoaded then
            do! loadScope scope
    }

    // Eagerly load Platform / Deployment at construction; team scopes
    // hydrate lazily on first access (team ids unknown at construction).
    do
        loadScope Platform |> Async.RunSynchronously
        loadScope Deployment |> Async.RunSynchronously

    let flushLoop = async {
        while not cts.IsCancellationRequested do
            try
                do! Async.Sleep flushMs

                if not cts.IsCancellationRequested then
                    do! flushAll ()
            with
            | :? OperationCanceledException -> ()
            | ex -> log.Error("[HnswVectorStore] Flush loop error", Some ex)
    }

    do Async.Start(flushLoop, cts.Token)

    interface IVectorStore with

        member _.Upsert scope chunkId vector chunk = async {
            let scopeKey = scopeToKey scope
            let state = getOrCreateScope scopeKey

            let cleanedMetadata = chunk.Metadata |> Map.remove ChunkMetadata.DeletedAtKey

            let entry = {
                ChunkId = chunkId
                Vector = normalise vector
                Content = chunk.Content
                Metadata = cleanedMetadata
            }

            lock state.Lock (fun () ->
                state.Entries[chunkId] <- entry
                state.MarkStale())

            markDirty scope
        }

        member _.Search scopes' query topK = async {
            // Hydrate any team scopes from blob; Platform / Deployment
            // are eager-loaded at construction.
            for scope in scopes' do
                do! ensureScopeLoaded scope

            let queryUnit = toDoubleArray (normalise query)

            let allMatches = ResizeArray<VectorMatch>()

            for scope in scopes' do
                let scopeKey = scopeToKey scope

                match scopes.TryGetValue scopeKey with
                | false, _ -> ()
                | true, state ->
                    lock state.Lock (fun () ->
                        ensureGraphFresh state

                        match state.Graph with
                        | None -> ()
                        | Some graph when state.GraphIds.Length = 0 -> ()
                        | Some graph ->
                            // KNN over the per-scope graph. We over-fetch
                            // and post-filter tombstones — they are excluded
                            // at build time but a concurrent delete could
                            // tombstone a still-graphed chunk before the
                            // next rebuild lands.
                            let results =
                                graph.KNNSearch(queryUnit, topK, null, System.Threading.CancellationToken.None)

                            for r in results do
                                let chunkId = state.GraphIds[r.Id]

                                match state.Entries.TryGetValue chunkId with
                                | true, entry when not (isTombstoned entry) ->
                                    let score = 1.0 - r.Distance

                                    allMatches.Add {
                                        ChunkId = entry.ChunkId
                                        Content = entry.Content
                                        Score = score
                                        Scope = scope
                                        Metadata = entry.Metadata
                                    }
                                | _ -> ())

            return allMatches |> Seq.sortByDescending _.Score |> Seq.truncate topK |> Seq.toList
        }

        member _.ListChunks scope includeDeleted = async {
            do! ensureScopeLoaded scope
            let scopeKey = scopeToKey scope

            match scopes.TryGetValue scopeKey with
            | false, _ -> return []
            | true, state ->
                let results =
                    lock state.Lock (fun () ->
                        state.Entries.Values
                        |> Seq.choose (fun e ->
                            if includeDeleted || not (isTombstoned e) then
                                Some(
                                    e.ChunkId,
                                    {
                                        Content = e.Content
                                        Metadata = e.Metadata
                                    }
                                )
                            else
                                None)
                        |> Seq.toList)

                return results
        }

        member _.DeleteByScope scope = async {
            let scopeKey = scopeToKey scope

            match scopes.TryGetValue scopeKey with
            | false, _ -> ()
            | true, state -> lock state.Lock (fun () -> state.Clear())

            markDirty scope
        }

        member _.DeleteChunk scope chunkId = async {
            let scopeKey = scopeToKey scope

            match scopes.TryGetValue scopeKey with
            | false, _ -> ()
            | true, state ->
                lock state.Lock (fun () ->
                    match state.Entries.TryGetValue chunkId with
                    | true, entry when not (isTombstoned entry) ->
                        let stamped = {
                            entry with
                                Metadata =
                                    entry.Metadata
                                    |> Map.add ChunkMetadata.DeletedAtKey (DateTimeOffset.UtcNow.ToString("o"))
                        }

                        state.Entries[chunkId] <- stamped
                        state.MarkStale()
                        markDirty scope
                    | _ -> ())
        }

        member _.RestoreChunk scope chunkId = async {
            let scopeKey = scopeToKey scope

            match scopes.TryGetValue scopeKey with
            | false, _ -> ()
            | true, state ->
                lock state.Lock (fun () ->
                    match state.Entries.TryGetValue chunkId with
                    | true, entry when isTombstoned entry ->
                        let restored = {
                            entry with
                                Metadata = entry.Metadata |> Map.remove ChunkMetadata.DeletedAtKey
                        }

                        state.Entries[chunkId] <- restored
                        state.MarkStale()
                        markDirty scope
                    | _ -> ())
        }

        member _.Vacuum scope olderThan = async {
            let scopeKey = scopeToKey scope

            match scopes.TryGetValue scopeKey with
            | false, _ -> return 0
            | true, state ->
                let purged =
                    lock state.Lock (fun () ->
                        let toPurge =
                            state.Entries.Values
                            |> Seq.choose (fun e ->
                                match e.Metadata.TryFind ChunkMetadata.DeletedAtKey with
                                | Some ts ->
                                    match DateTimeOffset.TryParse ts with
                                    | true, parsed when parsed < olderThan -> Some e.ChunkId
                                    | _ -> None
                                | None -> None)
                            |> Seq.toList

                        for cid in toPurge do
                            state.Entries.Remove cid |> ignore

                        if not toPurge.IsEmpty then
                            state.MarkStale()

                        toPurge.Length)

                if purged > 0 then
                    markDirty scope

                return purged
        }

        member _.ListScopes() = async {
            let keys = lock scopesLock (fun () -> scopes.Keys |> Seq.toList)
            return keys |> List.map scopeFromKey
        }

        member this.Erase(scope, subjectUserId, policy, dryRun) =
            ToolUp.Platform.IVectorStore.eraseSubject (this :> IVectorStore) scope subjectUserId policy dryRun

    interface IDisposable with
        member _.Dispose() =
            cts.Cancel()

            try
                flushAll () |> Async.RunSynchronously
            with ex ->
                log.Error("[HnswVectorStore] Final flush on dispose failed", Some ex)

            cts.Dispose()

/// Factory matching the companion convention. Inject into `composeWithRAG`'s
/// service config to substitute for `InMemoryVectorStore`:
///
/// ```fsharp
/// services.AddSingleton<IVectorStore>(fun sp ->
///     let storage = sp.GetService<IBlobStorage>()
///     let logger = sp.GetService<ILogger>()
///     HnswVectorStore(storage, logger) :> IVectorStore)
/// ```
let create (storage: IBlobStorage) (logger: ILogger option) : IVectorStore =
    match logger with
    | Some l -> new HnswVectorStore(storage, l) :> IVectorStore
    | None -> new HnswVectorStore(storage) :> IVectorStore