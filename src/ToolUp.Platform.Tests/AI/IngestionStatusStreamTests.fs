module ToolUp.Platform.Tests.AI.IngestionStatusStreamTests

open System
open System.Collections.Generic
open System.Threading
open Expecto
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerIngestionStatusStream

// ─── Phase 69c.tail D — KB ingestion status as a typed stream ─────
//
// The seam under test is the one Phase 69c recorded as missing: the KB
// status cache had no change notification, so intermediate ingestion
// progress was reachable only by polling `GetStatus` every 2 s. These
// cases pin the four properties that make the stream a replacement for
// that poll rather than a second way to be wrong:
//
//   * every transition written through the cache's write helpers reaches
//     a subscriber, seeded with the status the cache already held — a
//     consumer that arrives mid-ingest is told where it is;
//   * `updateStatus` publishes the value the cache SETTLED on. The
//     enqueue paths deliberately decline to overwrite a fresher status,
//     and publishing the declined one would tell a subscriber the
//     ingestion had gone BACKWARDS;
//   * a document removed mid-ingest terminates the stream. The cache
//     entry simply disappears, so without this the subscriber waits for
//     a transition that can never come;
//   * disposing the stream unsubscribes. This is why the bridge is
//     `AsyncStream.fromSubscription` and not `fromCallback` — the latter
//     holds no handle, so every aborted connection would leak a
//     subscriber.

// ─── Harness ─────────────────────────────────────────────────────

/// Each case uses its own document id: the status cache is process-wide
/// and the pack runs beside others that touch it.
let private freshDocId () =
    "69c-tail-d-" + Guid.NewGuid().ToString("N")

/// Open a subscription synchronously. `GetAsyncEnumerator` is what
/// subscribes and seeds, so every write after this call is observed.
let private openStream (docId: string) : IAsyncEnumerator<IngestionStatus> =
    (ingestionStatusStream docId).GetAsyncEnumerator CancellationToken.None

/// Read every element the stream has to give, refusing to hang: the
/// writes under test all end in a terminal status, so the enumerator
/// must run out. One that does not is the defect, not a slow test.
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

// ─── Cases ───────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 69c.tail D — KB ingestion status streaming" [

        test "every transition reaches the subscriber, in order, ending on the terminal" {
            let docId = freshDocId ()
            setStatus docId Queued

            let stream = openStream docId
            setStatus docId ExtractingText
            setStatus docId (Embedding(1, 3))
            setStatus docId (Embedding(2, 3))
            setStatus docId (Complete 3)

            let seen = drain stream
            dispose stream

            Expect.equal
                seen
                [ Queued; ExtractingText; Embedding(1, 3); Embedding(2, 3); Complete 3 ]
                "the seed first, then each transition, ending on the terminal"

            clearStatus docId
        }

        test "a document with no cached status yields no seed, only later transitions" {
            let docId = freshDocId ()

            let stream = openStream docId
            setStatus docId (Failed "nope")

            let seen = drain stream
            dispose stream

            Expect.equal seen [ Failed "nope" ] "nothing is invented for a status the cache has never held"

            clearStatus docId
        }

        test "an already-terminal document yields exactly one element and completes" {
            let docId = freshDocId ()
            setStatus docId (Complete 7)

            let stream = openStream docId
            let seen = drain stream
            dispose stream

            Expect.equal seen [ Complete 7 ] "the seed is terminal, so the stream ends on it"

            clearStatus docId
        }

        test "updateStatus publishes the value the cache settled on, never the declined one" {
            let docId = freshDocId ()
            // The upload path's shape: a racing chunk callback has already
            // advanced this document, so the enqueue's seed is declined.
            setStatus docId (Embedding(2, 5))

            let stream = openStream docId

            updateStatus docId (Embedding(0, 5)) (function
                | Queued -> Embedding(0, 5)
                | other -> other)

            setStatus docId (Complete 5)

            let seen = drain stream
            dispose stream

            Expect.equal
                seen
                [ Embedding(2, 5); Embedding(2, 5); Complete 5 ]
                "the declined write republishes the value that is true, not the one that was offered"

            clearStatus docId
        }

        test "a document removed mid-ingest terminates the stream instead of stranding it" {
            let docId = freshDocId ()
            setStatus docId (Embedding(1, 4))

            let stream = openStream docId
            clearStatus docId

            let seen = drain stream
            dispose stream

            match seen with
            | [ Embedding(1, 4); Failed reason ] ->
                Expect.isTrue (reason.Contains "removed") "the terminal says why nothing further is coming"
            | other -> failwithf "expected the seed then a terminal failure, got %A" other
        }

        test "disposing the stream unsubscribes — an aborted consumer leaks nothing" {
            let docId = freshDocId ()
            setStatus docId Queued

            let before = IngestionStatusFeed.count ()
            let stream = openStream docId
            let during = IngestionStatusFeed.count ()
            dispose stream
            let after = IngestionStatusFeed.count ()

            Expect.equal during (before + 1) "enumerating subscribes exactly once"
            Expect.equal after before "disposal removes that subscription"

            clearStatus docId
        }

        test "a subscriber that throws does not fail the ingestion write that triggered it" {
            let docId = freshDocId ()

            use _bad =
                IngestionStatusFeed.subscribe docId (fun _ -> failwith "a badly-written observer")

            // The write must complete and the cache must hold the value:
            // a subscriber observes the ingestion path, it never
            // participates in it.
            setStatus docId (Complete 1)

            match statusCache.TryGetValue docId with
            | true, status -> Expect.equal status (Complete 1) "the write landed despite the throwing subscriber"
            | _ -> failwith "the write was lost"

            clearStatus docId
        }
    ]