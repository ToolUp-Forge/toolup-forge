module ToolUp.Platform.Tests.InProcess.HnswVectorStoreTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.VectorStores.Hnsw

/// Silence the per-flush directory-name warnings: `LocalFileStorage` on
/// Windows can't materialise the `team:<id>` segment of the persisted
/// path, but the in-memory store and graph build are unaffected and the
/// tests never reload from disk. These tests exercise behaviour, not
/// the persistence subsystem.
type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

// Per-scope vector. Each scope gets a single-axis-aligned unit vector so a
// query against the same axis is the obvious nearest neighbour within its
// scope. The test then queries a wrong scope to assert *zero* leakage.
let private platformVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private teamAVec: float32 array =
    Array.init 8 (fun i -> if i = 1 then 1.0f else 0.0f)

let private teamBVec: float32 array =
    Array.init 8 (fun i -> if i = 2 then 1.0f else 0.0f)

let private chunk content tag : TextChunk = {
    Content = content
    Metadata = Map.ofList [ "tag", tag ]
}

let private newStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-hnsw-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let logger = SilentLogger() :> ILogger

    let store =
        new HnswVectorStore.HnswVectorStore(storage, logger, flushIntervalMs = 50)

    store :> IVectorStore, store :> IDisposable

let private newInMemoryStore () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-inmem-vec-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, logger = SilentLogger())

    store :> IVectorStore, store :> IDisposable

/// `IVectorStore` deterministic-ordering contract (RAG Gap 7, Investigate
/// gaps 2026-06-12): equal-score matches must come back in a stable total
/// order — tie-broken on `(Scope, ChunkId)` — never in enumeration /
/// accumulation order, which flips top-K membership run-to-run and is
/// fatal for the deterministic eval gate. Parameterised over a store
/// factory so every `IVectorStore` implementation (and any future
/// companion) binds the same check.
let deterministicOrderingContract (storeName: string) (makeStore: unit -> IVectorStore * IDisposable) =
    testCaseAsync (sprintf "%s: equal-score matches return in stable (Scope, ChunkId) order" storeName)
    <| async {
        let store, dispose = makeStore ()

        try
            let vec: float32 array = Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

            // Upsert in deliberately non-sorted id order, across two
            // scopes, all with the identical vector so every match ties
            // on score and only the tie-break determines the order.
            do! store.Upsert (Team "T") "t-c" vec (chunk "tc" "x")
            do! store.Upsert (Team "T") "t-a" vec (chunk "ta" "x")
            do! store.Upsert Platform "p-b" vec (chunk "pb" "x")
            do! store.Upsert (Team "T") "t-b" vec (chunk "tb" "x")

            let! first = store.Search [ Platform; Team "T" ] vec 10
            let! second = store.Search [ Platform; Team "T" ] vec 10

            let ids = first |> List.map (fun m -> m.Scope, m.ChunkId)

            Expect.equal
                ids
                [ Platform, "p-b"; Team "T", "t-a"; Team "T", "t-b"; Team "T", "t-c" ]
                "ties must resolve on (Scope, ChunkId), not enumeration order"

            Expect.equal
                (second |> List.map (fun m -> m.Scope, m.ChunkId))
                ids
                "repeated identical queries must return the identical ordering"
        finally
            dispose.Dispose()
    }

// ─── Phase 1007 — incremental writes ─────────────────────────────
//
// The store keeps a built graph current instead of rebuilding it after
// every write. These cases read the graph's own bookkeeping through
// `HnswVectorStore.GraphStats` (internal, `InternalsVisibleTo` this pack):
// a write must add a node and never move `Builds`; a delete or overwrite
// must retire a node that then never answers; and the rebuild threshold
// must fire exactly when retired nodes pass the declared fraction.

let private incrementalScope = Team "incremental"

let private incrementalDim = 16

/// A deterministic unit vector, distinct per index.
let private unitVector (index: int) : float32 array =
    let rng = Random(1007 * 7919 + index)
    let v = Array.init incrementalDim (fun _ -> float32 (rng.NextDouble() - 0.5))
    let m = sqrt (v |> Array.sumBy (fun x -> x * x))
    v |> Array.map (fun x -> x / m)

let private chunkIdOf (i: int) = sprintf "c%04d" i

let private newConcreteStore (parameters: HnswVectorStore.HnswParameters) =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-hnsw-1007-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage

    new HnswVectorStore.HnswVectorStore(
        storage,
        logger = SilentLogger(),
        flushIntervalMs = 60000,
        parameters = parameters
    ),
    tempDir

let private upsertRange (store: IVectorStore) (from: int) (count: int) = async {
    for i in from .. from + count - 1 do
        do! store.Upsert incrementalScope (chunkIdOf i) (unitVector i) (chunk (chunkIdOf i) "x")
}

let private statsOf (store: HnswVectorStore.HnswVectorStore) =
    match store.GraphStats incrementalScope with
    | Some s -> s
    | None -> failwith "the scope holds no state"

/// Search with chunk `i`'s own vector and return the hits' chunk ids.
let private searchFor (store: IVectorStore) (i: int) (topK: int) = async {
    let! hits = store.Search [ incrementalScope ] (unitVector i) topK
    return hits |> List.map _.ChunkId
}

// `testSequenced` paired with `HnswFidelityTests` — HNSW.Net carries
// shared static state beyond the per-store RNG fix in HnswVectorStore.fs;
// concurrent HNSW testList execution surfaces intermittent
// `Graph.Core..ctor` failures inside `Graph.AddItems`. The per-store
// RNG is now seeded (see HnswVectorStore.fs `graphBuildSeed`), which
// makes the recall-fidelity test deterministic, but the cross-list
// concurrency hazard is a separate static-state issue and still
// requires sequencing. See the matching comment block in
// HnswFidelityTests.fs for details.
let tests =
    testSequenced
    <| testList "HnswVectorStore" [
        testAsync "scope isolation: query against Team A returns no Team B chunks" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (Team "A") "a-1" teamAVec (chunk "team A note" "A")
                do! store.Upsert (Team "B") "b-1" teamBVec (chunk "team B note" "B")
                do! store.Upsert Platform "p-1" platformVec (chunk "platform note" "P")

                // Query Team A with Team B's exact vector — graph isolation
                // means Team A's graph never sees b-1, regardless of how
                // close the query is to it.
                let! results = store.Search [ Team "A" ] teamBVec 5

                Expect.all results (fun m -> m.Scope = Team "A") "every match must come from Team A's graph"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "b-1"))
                    "Team B's chunk must not surface in a Team A query"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "p-1"))
                    "Platform chunk must not surface unless Platform scope was requested"
            finally
                dispose.Dispose()
        }

        testAsync "scope isolation: multi-scope query returns matches only from requested scopes" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (Team "A") "a-1" teamAVec (chunk "team A" "A")
                do! store.Upsert (Team "B") "b-1" teamBVec (chunk "team B" "B")
                do! store.Upsert Platform "p-1" platformVec (chunk "platform" "P")

                // Ask for Platform + Team A only; Team B's chunk must be
                // structurally invisible to this query.
                let! results = store.Search [ Platform; Team "A" ] platformVec 10

                Expect.all
                    results
                    (fun m -> m.Scope = Platform || m.Scope = Team "A")
                    "no result may originate from a scope outside the request"

                Expect.isFalse
                    (results |> List.exists (fun m -> m.ChunkId = "b-1"))
                    "Team B chunk must never surface when only Platform + Team A are requested"
            finally
                dispose.Dispose()
        }

        testAsync
            "deterministic graph build: repeated builds against the same entries return the same nearest neighbour" {
            let store, dispose = newStore ()

            try
                // Three near-collinear vectors in the same scope. The seeded
                // RNG inside `HnswVectorStore` means the graph build picks
                // the same entry points every run; the same query against
                // the same data must therefore yield the same top hit.
                let v1: float32 array = [| 1.0f; 0.1f; 0.0f; 0.0f |]
                let v2: float32 array = [| 0.9f; 0.0f; 0.0f; 0.0f |]
                let v3: float32 array = [| 0.1f; 1.0f; 0.0f; 0.0f |]

                do! store.Upsert (Team "T") "v1" v1 (chunk "v1" "x")
                do! store.Upsert (Team "T") "v2" v2 (chunk "v2" "x")
                do! store.Upsert (Team "T") "v3" v3 (chunk "v3" "x")

                let query: float32 array = [| 1.0f; 0.0f; 0.0f; 0.0f |]

                let! first = store.Search [ Team "T" ] query 1
                let! second = store.Search [ Team "T" ] query 1

                Expect.equal
                    (first |> List.map _.ChunkId)
                    (second |> List.map _.ChunkId)
                    "repeated identical queries must return the same chunk"

                Expect.equal
                    (first |> List.head |> _.ChunkId)
                    "v2"
                    "v2 is the closest unit-vector to the query and must rank first"
            finally
                dispose.Dispose()
        }

        // Deterministic-ordering contract, bound against both in-tree
        // `IVectorStore` implementations. Lives here (rather than a new
        // Contracts file) so it rides an already-registered test module;
        // external companions bind `deterministicOrderingContract` the
        // same way from their own test packs.
        deterministicOrderingContract "HnswVectorStore" newStore
        deterministicOrderingContract "InMemoryVectorStore" newInMemoryStore

        testAsync "tombstoned chunks are excluded from search; restore reinstates them" {
            let store, dispose = newStore ()

            try
                do! store.Upsert (Team "T") "live" teamAVec (chunk "live" "x")
                do! store.Upsert (Team "T") "doomed" teamBVec (chunk "doomed" "x")

                do! store.DeleteChunk (Team "T") "doomed"

                let! afterDelete = store.Search [ Team "T" ] teamBVec 5

                Expect.isFalse
                    (afterDelete |> List.exists (fun m -> m.ChunkId = "doomed"))
                    "tombstoned chunk must not surface in search"

                do! store.RestoreChunk (Team "T") "doomed"

                let! afterRestore = store.Search [ Team "T" ] teamBVec 5

                Expect.isTrue
                    (afterRestore |> List.exists (fun m -> m.ChunkId = "doomed"))
                    "restored chunk must reappear in search"
            finally
                dispose.Dispose()
        }

        testAsync "Phase 1007: a write into a built scope adds to the graph and never rebuilds it" {
            let store, _ = newConcreteStore HnswVectorStore.HnswParameters.defaults
            use _ = store
            let vs = store :> IVectorStore
            do! upsertRange vs 0 200
            let! _ = searchFor vs 0 5
            let built = statsOf store
            Expect.equal (built.Builds, built.Nodes) (1, 200) "the first search builds the scope once"

            do! upsertRange vs 200 50

            for i in 200..249 do
                let! hits = searchFor vs i 1
                Expect.equal hits [ chunkIdOf i ] "a chunk written after the build is its own nearest neighbour"

            let after = statsOf store
            Expect.equal after.Builds 1 "writes into a built scope must not rebuild it"
            Expect.equal (after.Nodes, after.Retired) (250, 0) "each write adds exactly one node"
        }

        testAsync "Phase 1007: a deleted chunk never returns, and topK stays full" {
            let store, _ = newConcreteStore HnswVectorStore.HnswParameters.defaults
            use _ = store
            let vs = store :> IVectorStore
            do! upsertRange vs 0 200
            let! _ = searchFor vs 0 5
            // 30 of 200: under the default 25 % rebuild threshold.
            let deleted = [ 0..2..58 ]

            for i in deleted do
                do! vs.DeleteChunk incrementalScope (chunkIdOf i)

            let deletedIds = deleted |> List.map chunkIdOf |> Set.ofList

            for i in deleted do
                // A deleted chunk's own vector is the query most likely to
                // surface it.
                let! hits = searchFor vs i 10
                Expect.equal hits.Length 10 "topK stays full with retired nodes in the graph"

                Expect.isEmpty
                    (hits |> List.filter deletedIds.Contains)
                    "a deleted chunk must never come back from search"

            let stats = statsOf store
            Expect.equal (stats.Builds, stats.Retired) (1, 30) "a delete retires the node and does not rebuild"
        }

        testAsync "Phase 1007: an overwrite retires the old vector; a same-vector rewrite keeps its node" {
            let store, _ = newConcreteStore HnswVectorStore.HnswParameters.defaults
            use _ = store
            let vs = store :> IVectorStore
            do! upsertRange vs 0 100
            let! _ = searchFor vs 0 5

            // Chunk 5 moves to vector 500.
            do! vs.Upsert incrementalScope (chunkIdOf 5) (unitVector 500) (chunk "moved" "x")
            let! atNew = vs.Search [ incrementalScope ] (unitVector 500) 1

            Expect.equal
                (atNew |> List.map (fun m -> m.ChunkId, m.Content))
                [ chunkIdOf 5, "moved" ]
                "found at its new vector"

            let! atOld = searchFor vs 5 1
            Expect.notEqual atOld [ chunkIdOf 5 ] "the old vector no longer answers for the chunk"

            // Chunk 6 is rewritten with its own vector and new content.
            do! vs.Upsert incrementalScope (chunkIdOf 6) (unitVector 6) (chunk "same vector" "x")
            let! rewritten = vs.Search [ incrementalScope ] (unitVector 6) 1
            Expect.equal (rewritten |> List.map _.Content) [ "same vector" ] "the rewrite's content is served"

            let stats = statsOf store

            Expect.equal
                (stats.Builds, stats.Nodes, stats.Retired)
                (1, 101, 1)
                "the overwrite adds one node and retires one; the same-vector rewrite adds none"
        }

        testAsync "Phase 1007: a restore revives the chunk's node; a vacuumed chunk stays retired" {
            let store, _ = newConcreteStore HnswVectorStore.HnswParameters.defaults
            use _ = store
            let vs = store :> IVectorStore
            do! upsertRange vs 0 100
            let! _ = searchFor vs 0 5
            do! vs.DeleteChunk incrementalScope (chunkIdOf 7)
            let! gone = searchFor vs 7 1
            Expect.notEqual gone [ chunkIdOf 7 ] "deleted"
            do! vs.RestoreChunk incrementalScope (chunkIdOf 7)
            let! back = searchFor vs 7 1
            Expect.equal back [ chunkIdOf 7 ] "restored"
            let stats = statsOf store

            Expect.equal (stats.Builds, stats.Nodes, stats.Retired) (1, 100, 0) "the node came back; nothing was added"

            do! vs.DeleteChunk incrementalScope (chunkIdOf 8)
            let! purged = vs.Vacuum incrementalScope DateTimeOffset.MaxValue
            Expect.equal purged 1 "one tombstone purged"
            let! afterVacuum = searchFor vs 8 10
            Expect.isFalse (afterVacuum |> List.contains (chunkIdOf 8)) "a purged chunk never returns"
            Expect.equal (statsOf store).Retired 1 "the purged chunk's node stays retired until a rebuild"
        }

        testAsync "Phase 1007: retired nodes past TombstoneRebuildFraction rebuild the scope, and not before" {
            let parameters = {
                HnswVectorStore.HnswParameters.defaults with
                    TombstoneRebuildFraction = 0.1
            }

            let store, _ = newConcreteStore parameters
            use _ = store
            let vs = store :> IVectorStore
            do! upsertRange vs 0 100
            let! _ = searchFor vs 0 5

            for i in 0..9 do
                do! vs.DeleteChunk incrementalScope (chunkIdOf i)

            let! _ = searchFor vs 50 5
            let atThreshold = statsOf store

            Expect.equal
                (atThreshold.Builds, atThreshold.Retired)
                (1, 10)
                "10 retired of 100 is AT the 0.1 threshold, not past it: no rebuild"

            do! vs.DeleteChunk incrementalScope (chunkIdOf 10)
            let! hits = searchFor vs 10 5
            Expect.isFalse (hits |> List.contains (chunkIdOf 10)) "the deleted chunk is gone after the rebuild"
            let past = statsOf store

            Expect.equal
                (past.Builds, past.Nodes, past.Retired)
                (2, 89, 0)
                "11 retired of 100 passes the threshold: the scope rebuilds over its 89 live chunks"
        }

        testAsync "Phase 1007: a cold load builds the graph once from the persisted entries" {
            let store, dir = newConcreteStore HnswVectorStore.HnswParameters.defaults
            let vs = store :> IVectorStore

            for i in 0..19 do
                do! vs.Upsert Deployment (chunkIdOf i) (unitVector i) (chunk (chunkIdOf i) "x")

            // Disposing flushes the scope to storage.
            (store :> IDisposable).Dispose()

            use reloaded =
                new HnswVectorStore.HnswVectorStore(
                    LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage,
                    logger = SilentLogger(),
                    flushIntervalMs = 60000
                )

            let! hits = (reloaded :> IVectorStore).Search [ Deployment ] (unitVector 3) 1
            Expect.equal (hits |> List.map _.ChunkId) [ chunkIdOf 3 ] "the reloaded scope answers"

            match reloaded.GraphStats Deployment with
            | Some s -> Expect.equal (s.Builds, s.Nodes) (1, 20) "one build over every persisted entry"
            | None -> failtest "the reloaded scope holds no state"
        }

        test "Phase 1007: HnswParameters.validate bounds ConstructionPruning and TombstoneRebuildFraction" {
            let d = HnswVectorStore.HnswParameters.defaults
            Expect.isOk (HnswVectorStore.HnswParameters.validate d) "defaults are valid"

            Expect.isOk
                (HnswVectorStore.HnswParameters.validate HnswVectorStore.HnswParameters.fastBuild)
                "fastBuild is valid"

            for bad in
                [
                    { d with ConstructionPruning = 0 }
                    { d with ConstructionPruning = 2001 }
                    {
                        d with
                            TombstoneRebuildFraction = -0.01
                    }
                    {
                        d with
                            TombstoneRebuildFraction = 1.01
                    }
                    {
                        d with
                            TombstoneRebuildFraction = nan
                    }
                ] do
                Expect.isError (HnswVectorStore.HnswParameters.validate bad) (sprintf "%A must be refused" bad)
        }

        // The scope map is read lock-free (`ListChunks`, `Search`, the delete
        // and flush paths) while `Upsert` inserts a new scope's state, so it
        // is a concurrent map. This is a regression guard, not a reproduction:
        // a plain `Dictionary` read torn by an insert is rare enough that the
        // loop does not fail on one reliably. The readers read the map without
        // building a graph: HNSW.Net's own static state is not safe for
        // concurrent graph builds (see `HnswFidelityTests`), a separate hazard.
        testAsync "a first upsert into a new scope is safe against concurrent reads of the scope map" {
            let storage =
                ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

            use store =
                new HnswVectorStore.HnswVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)

            let vs = store :> IVectorStore
            let scopeCount = 20000
            let scopeOf i = User(sprintf "u%d" i)

            // How many scopes the writer has finished; every one below it
            // exists, so a read that misses it read a torn map.
            let written = ref 0

            let writer = async {
                for i in 0 .. scopeCount - 1 do
                    do! vs.Upsert (scopeOf i) (sprintf "c%d" i) teamAVec (chunk "note" "U")
                    System.Threading.Volatile.Write(&written.contents, i + 1)
            }

            let reader (seed: int) = async {
                let failures = ResizeArray<exn>()
                let rnd = Random seed

                while System.Threading.Volatile.Read(&written.contents) < scopeCount do
                    let done' = System.Threading.Volatile.Read(&written.contents)

                    if done' > 0 then
                        let i = rnd.Next done'

                        try
                            let! chunks = vs.ListChunks (scopeOf i) true

                            if List.isEmpty chunks then
                                failures.Add(exn (sprintf "scope u%d was written but read as absent" i))
                        with ex ->
                            failures.Add ex

                return List.ofSeq failures
            }

            let! results =
                Async.Parallel [
                    async {
                        do! writer
                        return []
                    }
                    reader 1
                    reader 2
                    reader 3
                ]

            let failures = results |> List.concat
            Expect.isEmpty failures (sprintf "no concurrent read may fail: %A" (failures |> List.truncate 3))

            for i in [ 0; scopeCount / 2; scopeCount - 1 ] do
                let! chunks = vs.ListChunks (scopeOf i) true
                Expect.equal (chunks |> List.map fst) [ sprintf "c%d" i ] "every scope written is readable"
        }

        // Phase 861 — the restart laws: every mutation hydrates a lazily
        // loaded scope first, and an unreadable snapshot refuses it. Over
        // the in-memory blob double, because these laws are ABOUT reloading
        // from storage (the `team:<id>` segment cannot materialise on a
        // Windows `LocalFileStorage`, see `SilentLogger` above).
        ToolUp.Platform.Tests.Contracts.IVectorStoreContract.tests "HnswVectorStore" (fun storage ->
            new HnswVectorStore.HnswVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)
            :> IVectorStore)

        ToolUp.Platform.Tests.Contracts.IVectorStoreContract.unreadableSnapshotTests
            "HnswVectorStore"
            (function
            | :? HnswVectorStore.HnswScopeSnapshotUnreadableException -> true
            | _ -> false)
            (fun storage ->
                new HnswVectorStore.HnswVectorStore(storage, logger = SilentLogger(), flushIntervalMs = 60000)
                :> IVectorStore)
    ]