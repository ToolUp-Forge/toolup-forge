// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The load harness's at-rest step (`src/ToolUp.RAG.Benchmarks`, linked
/// here). A seeded in-process index still owes persistence passes when the
/// seed returns, and each pass reads its blob before it writes it back. The
/// harness counts the blob reads of its measured rounds, so a pass that runs
/// into them is counted as a retrieval's read. These tests pin that after
/// `RestingIndexes.atRest` no seed write is owed: a window of searches over
/// the reopened indexes reads and writes nothing, even when every write is
/// slow enough to keep a pass in flight past the seed.
module ToolUp.Platform.Tests.InProcess.RestingIndexesTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing
open ToolUp.RAG.Benchmarks

/// An in-memory `IBlobStorage` whose every write takes `writeDelay`, so a
/// persistence pass begun during a seed is still writing when the seed
/// returns. Counts the reads and writes that reach it since `Reset`.
type private SlowWriteStorage(writeDelay: TimeSpan) =
    let inner = InMemoryBlobStorage() :> IBlobStorage
    let mutable downloads = 0L
    let mutable uploads = 0L

    member _.Downloads = Interlocked.Read(&downloads)
    member _.Uploads = Interlocked.Read(&uploads)

    member _.Reset() =
        Interlocked.Exchange(&downloads, 0L) |> ignore
        Interlocked.Exchange(&uploads, 0L) |> ignore

    interface IBlobStorage with
        member _.CanComposeFrom = false
        member _.ComposeFrom(c, t, s) = inner.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dryRun) = inner.Erase(c, p, policy, dryRun)

        member _.Upload(c, n, content) = async {
            Interlocked.Increment(&uploads) |> ignore
            do! Async.Sleep writeDelay
            return! inner.Upload(c, n, content)
        }

        member _.Download(c, n) =
            Interlocked.Increment(&downloads) |> ignore
            inner.Download(c, n)

        member _.DownloadRange(c, n, offset, length) =
            Interlocked.Increment(&downloads) |> ignore
            inner.DownloadRange(c, n, offset, length)

        member _.Delete(c, n) = inner.Delete(c, n)
        member _.List(c, p) = inner.List(c, p)
        member _.Exists(c, n) = inner.Exists(c, n)
        member _.GetMetadata(c, n) = inner.GetMetadata(c, n)

let private chunks = 400

let private vectorOf (i: int) = [| float32 (i % 7 + 1); float32 (i % 5 + 1); float32 (i % 3 + 1); 1.0f |]

let private chunkOf (i: int) : TextChunk = {
    Content = $"alpha w{i % 11} w{i % 13} beta"
    Metadata = Map.empty
}

/// Seed `chunks` into a vector store and a BM25 index over `storage` (each
/// flushing every 50 ms and the vector store every 100 marks, as the load
/// harness composes them), bring them to rest, warm them, reset the counters,
/// then search for `window`. Returns the reads and writes in the window.
let private measureWindow (storage: SlowWriteStorage) (window: TimeSpan) = async {
    let blobs = storage :> IBlobStorage

    let openVector () =
        new InMemoryVectorStore(blobs, logger = Logger.silent, flushIntervalMs = 50, flushChunkThreshold = 100)
        :> IVectorStore

    let openSparse () =
        new InMemoryBM25Index(blobs, logger = Logger.silent, flushIntervalMs = 50) :> ISparseIndex

    let seededVector = openVector ()
    let seededSparse = openSparse ()

    for i in 0 .. chunks - 1 do
        let chunkId = $"c{i:D5}"
        do! seededVector.Upsert Deployment chunkId (vectorOf i) (chunkOf i)
        do! seededSparse.Upsert Deployment chunkId (chunkOf i)

    let vector, sparse =
        RestingIndexes.atRest
            {
                ReopenVector = Some openVector
                OpenSparse = openSparse
            }
            seededVector
            seededSparse

    let search () = async {
        let! vectorHits = vector.Search [ Deployment ] (vectorOf 3) 10
        let! sparseHits = sparse.Search [ Deployment ] "alpha w3" 10
        return vectorHits.Length, sparseHits.Length
    }

    // The warm-up loads each scope from its blob, as the harness's does.
    let! warmed = search ()
    storage.Reset()

    let clock = Stopwatch.StartNew()
    let mutable answered = warmed

    while clock.Elapsed < window do
        let! hits = search ()
        answered <- hits
        do! Async.Sleep 20

    let reads, writes = storage.Downloads, storage.Uploads

    for index in [ box vector; box sparse ] do
        match index with
        | :? IDisposable as disposable -> disposable.Dispose()
        | _ -> ()

    return warmed, answered, reads, writes
}

let tests =
    testList "load harness: seeded indexes at rest" [
        testCase "no seed persistence pass reads or writes inside the measured window"
        <| fun () ->
            // Every write takes 400 ms, so the seed's first pass is still
            // writing when the seed returns and a second pass is owed behind
            // it; the window is long enough for that pass to read and write.
            let storage = SlowWriteStorage(TimeSpan.FromMilliseconds 400.0)

            let (vectorWarm, sparseWarm), (vectorHits, sparseHits), reads, writes =
                measureWindow storage (TimeSpan.FromMilliseconds 1500.0)
                |> Async.RunSynchronously

            Expect.equal (vectorWarm, sparseWarm) (10, 10) "the reopened indexes hold the seeded corpus"
            Expect.equal (vectorHits, sparseHits) (10, 10) "every search in the window answers from the corpus"

            Expect.equal
                (reads, writes)
                (0L, 0L)
                "no blob is read or written inside the measured window: no seed persistence pass is left to run, and searches answer from memory"
    ]