module ToolUp.Platform.Tests.AI.IngestionStatusStreamTests

// ─── KB ingestion status as a typed stream ────────────────────────
//
// `ingestionStatusStream` is the push form of the `GetStatus` poll. These
// cases pin the properties that make it a replacement for that poll rather
// than a second way to be wrong:
//
//   * the first element is the document's PERSISTED status — its in-flight
//     attempt's progress, else its index entry — so a consumer that arrives
//     mid-ingest, on any replica, is told where it is;
//   * every transition the attempt store publishes reaches the subscriber,
//     ending on the terminal;
//   * a document removed mid-ingest terminates the stream, instead of
//     leaving the subscriber waiting for a transition that can never come;
//   * disposing the stream unsubscribes, so an aborted connection leaks
//     nothing.

open System
open System.Collections.Generic
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerIngestionStatusStream
open KnowledgeBase.ServerIngestionObserver

// ─── Harness ─────────────────────────────────────────────────────

let private container = "team-stream-tests"

let private silent =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Each case uses its own document id and its own storage: the feed is
/// process-wide and the pack runs beside others that publish to it.
let private freshDocId () =
    "stream-" + Guid.NewGuid().ToString("N")

let private freshStorage () =
    ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

/// Put `docId` in the index at `status`.
let private seedIndex (storage: IBlobStorage) (docId: string) (status: IngestionStatus) =
    upsertIndexEntry storage container {
        Id = docId
        FileName = "notes.txt"
        FileType = "txt"
        UploadedAt = DateTimeOffset.UtcNow
        UploadedBy = "alice"
        Status = status
        SizeBytes = 10L
        ChunkCount = 4
        Source = UploadedFile
        ContentHash = None
        Version = 1
        Tags = []
    }
    |> Async.RunSynchronously

let private openStream (storage: IBlobStorage) (docId: string) : IAsyncEnumerator<IngestionStatus> =
    (ingestionStatusStream storage container docId).GetAsyncEnumerator CancellationToken.None

/// The next element, refusing to hang.
let private next (enumerator: IAsyncEnumerator<IngestionStatus>) : IngestionStatus =
    let moved = enumerator.MoveNextAsync().AsTask()

    if not (moved.Wait(TimeSpan.FromSeconds 30.0)) || not moved.Result then
        failwith "the ingestion-status stream yielded nothing within 30s"

    enumerator.Current

/// Read every element the stream has left to give, refusing to hang: the
/// writes under test all end in a terminal status, so the enumerator
/// completes on its own.
let private drain (enumerator: IAsyncEnumerator<IngestionStatus>) : IngestionStatus list =
    let work = task {
        let acc = ResizeArray<IngestionStatus>()
        let mutable moved = true

        while moved do
            let! next = enumerator.MoveNextAsync()
            moved <- next

            if moved then
                acc.Add enumerator.Current

        return List.ofSeq acc
    }

    if not (work.Wait(TimeSpan.FromSeconds 30.0)) then
        failwith "the ingestion-status stream did not terminate within 30s"

    work.Result

let private dispose (enumerator: IAsyncEnumerator<IngestionStatus>) =
    enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

[<Tests>]
let tests =
    testList "Phase 69c.tail D — KB ingestion status streaming" [

        test "the persisted status first, then every transition, ending on the terminal" {
            let storage = freshStorage ()
            let docId = freshDocId ()
            seedIndex storage docId Queued

            let stream = openStream storage docId
            Expect.equal (next stream) Queued "the seed is the persisted status"

            IngestionStatusFeed.publish docId ExtractingText
            IngestionStatusFeed.publish docId (Embedding(1, 3))
            IngestionStatusFeed.publish docId (Embedding(2, 3))
            IngestionStatusFeed.publish docId (Complete 3)

            let seen = drain stream
            dispose stream

            Expect.equal
                seen
                [ ExtractingText; Embedding(1, 3); Embedding(2, 3); Complete 3 ]
                "each transition, ending on the terminal"
        }

        test "a document not in the index yields no seed, only later transitions" {
            let storage = freshStorage ()
            let docId = freshDocId ()

            let stream = openStream storage docId
            IngestionStatusFeed.publish docId (Failed "nope")

            let seen = drain stream
            dispose stream

            Expect.equal seen [ Failed "nope" ] "nothing is invented for a document the store does not hold"
        }

        test "an already-terminal document yields exactly one element and completes" {
            let storage = freshStorage ()
            let docId = freshDocId ()
            seedIndex storage docId (Complete 7)

            let stream = openStream storage docId
            let seen = drain stream
            dispose stream

            Expect.equal seen [ Complete 7 ] "the seed is terminal, so the stream ends on it"
        }

        test "an attempt in flight seeds from its persisted progress, which the index does not carry" {
            let storage = freshStorage ()
            let docId = freshDocId ()
            seedIndex storage docId Queued

            let attempts = KnowledgeAttemptStore(storage, None, silent)
            attempts.Begin(container, docId, "attempt-1", 5, None) |> Async.RunSynchronously

            for chunk in 0..1 do
                (attempts :> IIngestionAttemptStore)
                    .Advance(
                        {
                            Scope = container
                            DocumentId = docId
                            AttemptId = Some "attempt-1"
                            OriginatingUser = None
                            Report = ChunkReport.Indexed(sprintf "%s:chunk:%d" docId chunk)
                        }
                    )
                |> Async.RunSynchronously
                |> ignore

            let stream = openStream storage docId
            Expect.equal (next stream) (Embedding(2, 5)) "the seed is the attempt's progress, read from storage"
            dispose stream
        }

        test "a document removed mid-ingest terminates the stream instead of stranding it" {
            let storage = freshStorage ()
            let docId = freshDocId ()
            seedIndex storage docId (Embedding(1, 4))

            let stream = openStream storage docId
            Expect.equal (next stream) (Embedding(1, 4)) "the seed"
            clearStatus docId

            let seen = drain stream
            dispose stream

            match seen with
            | [ Failed reason ] ->
                Expect.isTrue (reason.Contains "removed") "the terminal says why nothing further is coming"
            | other -> failwithf "expected a terminal failure, got %A" other
        }

        test "disposing the stream unsubscribes — an aborted consumer leaks nothing" {
            let storage = freshStorage ()
            let docId = freshDocId ()

            let before = IngestionStatusFeed.count ()
            let stream = openStream storage docId
            let during = IngestionStatusFeed.count ()
            dispose stream
            let after = IngestionStatusFeed.count ()

            Expect.equal during (before + 1) "enumerating subscribes exactly once"
            Expect.equal after before "disposal removes that subscription"
        }

        test "a subscriber that throws does not fail the publisher" {
            let docId = freshDocId ()

            use _bad =
                IngestionStatusFeed.subscribe docId (fun _ -> failwith "a badly-written observer")

            // A subscriber observes the ingestion path; it never participates.
            IngestionStatusFeed.publish docId (Complete 1)
        }
    ]