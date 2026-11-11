module ToolUp.Platform.Tests.InProcess.IngestionAttemptStoreTests

// The ingestion attempt: both producers' attempt stores held to the one
// conformance pack, the knowledge base's index written once per change of
// class rather than once per chunk, a chunk reported on two replicas counted
// once, a terminal announced once, and the announcement's key and payload
// byte for byte what subscribers have always parsed.

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerIngestionObserver
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private run = Async.RunSynchronously

let private silent =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Counts the writes to each blob, over storage that offers no conditional
/// write (so every guarded update is one plain upload).
type private CountingStorage(inner: IBlobStorage) =
    let uploads = ConcurrentDictionary<string, int>()

    member _.UploadsOf(blobName: string) =
        match uploads.TryGetValue blobName with
        | true, n -> n
        | _ -> 0

    member _.Reset() = uploads.Clear()

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            uploads.AddOrUpdate(blobName, 1, (fun _ n -> n + 1)) |> ignore
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// Records every `CustomNotification`.
type private CapturingChannel() =
    let published = ConcurrentQueue<string * string * string>()
    member _.Published = List.ofSeq published

    interface INotificationChannel with
        member _.Publish(scope, notification) = async {
            match notification with
            | CustomNotification(key, json) -> published.Enqueue(scope, key, json)
            | _ -> ()
        }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

let private document (docId: string) : KnowledgeDocument = {
    Id = docId
    FileName = "notes.txt"
    FileType = "txt"
    UploadedAt = DateTimeOffset.UtcNow
    UploadedBy = "alice"
    Status = Queued
    SizeBytes = 10L
    ChunkCount = 0
    Source = UploadedFile
    ContentHash = None
    Version = 1
    Tags = []
}

let private chunkJob (container: string) (docId: string) (attempt: IngestionAttempt) (chunk: int) : IngestionJob = {
    DocumentId = docId
    DocumentName = "notes.txt"
    ChunkId = sprintf "%s:chunk:%d" docId chunk
    Chunk = { Content = "x"; Metadata = Map.empty }
    Scope = Deployment
    ScopeId = container
    Container = container
    OriginatingUserId = Some(UserId "alice")
    Attempt = Some attempt
}

// ── the conformance pack, bound to both producers ──

let private contract =
    testList "IIngestionAttemptStore conformance" [
        IIngestionAttemptStoreContract.tests "the Data Manager's attempts" (fun () -> {
            Store =
                IngestionAttemptStore.dataManager
                    (IngestionStatusStore.createInMemory ())
                    (InMemoryBlobStorage())
                    silent
            Track = fun _ _ -> async.Return()
        })

        IIngestionAttemptStoreContract.tests "the knowledge base's attempts" (fun () ->
            let storage = InMemoryBlobStorage() :> IBlobStorage

            {
                Store = KnowledgeAttemptStore(storage, None, silent)
                Track = fun scope docId -> upsertIndexEntry storage scope (document docId)
            })
    ]

// ── the knowledge base's attempt, measured ──

/// A scope of `documents` documents, one of which ingests `chunks` chunks
/// through the observer; returns the index writes the ingestion cost.
let private indexWritesFor (documents: int) (chunks: int) =
    let storage = CountingStorage(InMemoryBlobStorage())
    let blobs = storage :> IBlobStorage
    let container = "team-attempt-cost"

    for i in 1..documents do
        run (upsertIndexEntry blobs container (document (sprintf "doc-%d" i)))

    storage.Reset()

    let attempts = KnowledgeAttemptStore(blobs, None, silent)

    let attempt = {
        AttemptId = "attempt-1"
        EnqueuedChunks = chunks
    }

    run (attempts.Begin(container, "doc-1", attempt.AttemptId, chunks, Some chunks))

    let observer = makeIngestionStatusObserver blobs None silent

    for chunk in 0 .. chunks - 1 do
        run (observer.OnChunkIndexed(chunkJob container "doc-1" attempt chunk))

    let read = run (loadIndex blobs container)

    let settled =
        Expect.wantOk read "the index reads" |> List.find (fun d -> d.Id = "doc-1")

    Expect.equal settled.Status (Complete chunks) "the document completed"
    storage.UploadsOf indexBlobName

let private cost =
    testList "the index is written per change of class, not per chunk" [
        test "a 50-chunk document in a 20-document scope rewrites the index at most twice" {
            Expect.isLessThanOrEqual (indexWritesFor 20 50) 2 "seeded in flight once, settled once"
        }

        test "a 500-chunk document costs the index no more than a 50-chunk one" {
            Expect.equal (indexWritesFor 20 500) (indexWritesFor 20 50) "bounded independent of chunk count"
        }
    ]

let private replicas =
    testList "an attempt across replicas" [
        test "a chunk retried on a second replica is counted once" {
            let shared = InMemoryBlobStorage() :> IBlobStorage
            let container = "team-replicas"
            run (upsertIndexEntry shared container (document "doc-1"))

            // Two replicas: separate storage clients over one backend, so no
            // in-process state is shared between them.
            let replicaA = CountingStorage(shared) :> IBlobStorage
            let replicaB = CountingStorage(shared) :> IBlobStorage

            let attempt = {
                AttemptId = "attempt-1"
                EnqueuedChunks = 3
            }

            run (KnowledgeAttemptStore(replicaA, None, silent).Begin(container, "doc-1", attempt.AttemptId, 3, Some 3))

            let onA = makeIngestionStatusObserver replicaA None silent
            let onB = makeIngestionStatusObserver replicaB None silent

            run (onA.OnChunkIndexed(chunkJob container "doc-1" attempt 0))
            run (onA.OnChunkIndexed(chunkJob container "doc-1" attempt 1))
            // Replica B re-runs chunk 1 (a durable queue's redelivery).
            run (onB.OnChunkIndexed(chunkJob container "doc-1" attempt 1))

            Expect.equal
                (run (persistedStatus shared container "doc-1"))
                (Some(Embedding(2, 3)))
                "the redelivered chunk was not counted twice"

            run (onB.OnChunkIndexed(chunkJob container "doc-1" attempt 2))

            Expect.equal (run (persistedStatus shared container "doc-1")) (Some(Complete 3)) "the third chunk completes"
        }
    ]

let private announcements =
    testList "a terminal is announced once, in the published shape" [
        test "a terminal transition publishes exactly once" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let container = "team-announce"
            run (upsertIndexEntry storage container (document "doc-1"))
            let channel = CapturingChannel()

            let attempt = {
                AttemptId = "attempt-1"
                EnqueuedChunks = 2
            }

            run (KnowledgeAttemptStore(storage, None, silent).Begin(container, "doc-1", attempt.AttemptId, 2, Some 2))
            let observer = makeIngestionStatusObserver storage (Some channel) silent

            for chunk in [ 0; 1; 1; 0 ] do
                run (observer.OnChunkIndexed(chunkJob container "doc-1" attempt chunk))

            run (observer.OnChunkFailed(chunkJob container "doc-1" attempt 1, "late"))

            Expect.equal
                channel.Published
                [
                    "alice",
                    IngestionStatusNotificationKey,
                    """{"DocumentId":"doc-1","FileName":"notes.txt","Outcome":"Complete","ChunkCount":2,"ErrorReason":"","UploadedBy":"alice"}"""
                ]
                "one announcement, to the uploader"
        }

        test "the knowledge-base key and payload are byte-identical to the published contract" {
            // Captured from the observer this one replaced, on the tree before it.
            Expect.equal IngestionStatusNotificationKey "KnowledgeBase.IngestionStatus" "the key"

            let payload status =
                terminalAnnouncement "doc-1" "notes.txt" "alice" status |> Option.map _.Payload

            Expect.equal
                (payload (Complete 3))
                (Some
                    """{"DocumentId":"doc-1","FileName":"notes.txt","Outcome":"Complete","ChunkCount":3,"ErrorReason":"","UploadedBy":"alice"}""")
                "Complete"

            Expect.equal
                (payload (Failed "boom \"x\""))
                (Some
                    """{"DocumentId":"doc-1","FileName":"notes.txt","Outcome":"Failed","ChunkCount":0,"ErrorReason":"boom \"x\"","UploadedBy":"alice"}""")
                "Failed"

            Expect.equal (payload (Embedding(1, 2))) None "an in-flight status announces nothing"
        }

        test "the Data Manager payload is byte-identical to the published contract" {
            let attempts =
                IngestionAttemptStore.dataManager
                    (IngestionStatusStore.createInMemory ())
                    (InMemoryBlobStorage())
                    silent

            run (attempts.Begin("team-dm", "sales.csv", "a", 1))

            let transition =
                run (
                    attempts.Advance(
                        {
                            Scope = "team-dm"
                            DocumentId = "sales.csv"
                            AttemptId = Some "a"
                            OriginatingUser = Some "alice"
                            Report = ChunkReport.Indexed "sales.csv:chunk:0"
                        }
                    )
                )

            Expect.equal
                transition
                (AttemptTransition.Terminal(
                    Some {
                        Recipient = "alice"
                        Key = "DataManager.IngestionStatus"
                        Payload = """{"FileName":"sales.csv","Status":"Indexed"}"""
                    }
                ))
                "the Data Manager announcement"
        }
    ]

let tests =
    testList "Phase 1085 — the ingestion attempt" [ contract; cost; replicas; announcements ]