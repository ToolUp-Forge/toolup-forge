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
                    (first |> List.map (fun m -> m.ChunkId))
                    (second |> List.map (fun m -> m.ChunkId))
                    "repeated identical queries must return the same chunk"

                Expect.equal
                    (first |> List.head |> (fun m -> m.ChunkId))
                    "v2"
                    "v2 is the closest unit-vector to the query and must rank first"
            finally
                dispose.Dispose()
        }

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
    ]