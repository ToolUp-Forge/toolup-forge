module ToolUp.Platform.Tests.InProcess.KnowledgeIndexReadTests

open System
open System.Text
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Testing
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver

// ─── A corrupt `knowledge/index.json` is a refusal, not an empty list ──
//
// The deciders that read the scope's index — the corpus quota, content-hash
// dedup, upload versioning and deletion — refuse when the index exists and
// does not decode, instead of deciding on an empty list. The listing surface
// (`getDocuments`) still renders: an empty list, with the corruption recorded
// at Error so an operator sees why the scope looks empty.

let private container = "team-index-read"

let private notifications: INotificationChannel =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

let private mkDeps (storage: IBlobStorage) (logger: ILogger) (policies: KnowledgePolicies) : KnowledgeApiDeps = {
    Storage = storage
    Queue = Some(IngestionQueue())
    OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
    Notifications = notifications
    Logger = logger
    Scope = {
        ScopeId = container
        Container = container
        Persist = true
    }
    UserId = UserId "user-1"
    VectorScope = Deployment
    VectorStore = None
    IndexLifecycle = None
    EventStore = None
    EmbeddingProvider = None
    NarrativeStore = None
    AccessContext = AccessContexts.forSubject (AnonymousSession "user-1")
    OriginalResolver = createDefault ()
    AuditLog = TestAuditLog()
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    Policies = policies
    DataObjectStore = None
    ContentScanner = None
    DisclosureGate = None
    UrlFetcher = None
}

let private csvBytes = Encoding.UTF8.GetBytes "name,score\nalpha,1\nbeta,2"

let private corrupt (storage: IBlobStorage) = async {
    let! written = storage.Upload(container, indexBlobName, Encoding.UTF8.GetBytes "{ this is not an index")
    Expect.isOk written "the corrupt index is seeded"
}

/// Wait until the background extraction an admitted upload started has written
/// the document's index entry past `Queued`. The upload returns before that
/// extraction runs, and its index write is a read-modify-write: on this
/// in-memory storage, which has no conditional writes, a test that overwrites
/// the index while the write is in flight has its bytes replaced by the
/// extraction's. `mkDeps` composes a queue nothing drains, so the attempt's
/// `Embedding` seed is the extraction's last index write (a terminal status
/// is, on a path that enqueues nothing).
///
/// The status feed is subscribed before the index is read, and the feed
/// publishes after the index write, so a transition landing between the two
/// is seen by one or the other. `ExtractingText` is announced without an
/// index write, so it does not count.
let private awaitExtractionIndexWrite (storage: IBlobStorage) (docId: string) = async {
    let written =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    use _ =
        IngestionStatusFeed.subscribe docId (fun status ->
            match status with
            | ExtractingText -> ()
            | _ -> written.TrySetResult() |> ignore)

    let! index = loadIndex storage container

    let settled =
        match index with
        | Ok docs ->
            docs
            |> List.exists (fun d ->
                d.Id = docId
                && (match d.Status with
                    | Queued -> false
                    | _ -> true))
        | Error _ -> false

    if not settled then
        do! written.Task.WaitAsync(TimeSpan.FromMinutes 2.0) |> Async.AwaitTask
}

let private isRejected (status: IngestionStatus) =
    match status with
    | UploadRejected _ -> true
    | _ -> false

let private expectIndexRefusal (doc: KnowledgeDocument) =
    match doc.Status with
    | UploadRejected reason -> Expect.stringContains reason "knowledge index" "the refusal names the unreadable index"
    | other -> failtestf "expected the upload to be refused on the unreadable index, got %A" other

let tests =
    testList "Phase 1087 — a corrupt knowledge index refuses every decider" [

        testCaseAsync "the corpus quota refuses rather than counting an empty corpus"
        <| async {
            let storage = Fakes.TestBlobStorage() :> IBlobStorage

            let policies = {
                KnowledgePolicies.defaults with
                    Quota = {
                        KnowledgeQuotaPolicy.unlimited with
                            MaxDocuments = Some 10
                    }
            }

            do! corrupt storage
            let! doc = uploadDocument (mkDeps storage Logger.silent policies) csvBytes "scores.csv"
            expectIndexRefusal doc
        }

        testCaseAsync "dedup refuses rather than finding no duplicate"
        <| async {
            let storage = Fakes.TestBlobStorage() :> IBlobStorage
            let deps = mkDeps storage Logger.silent KnowledgePolicies.defaults
            let! first = uploadDocument deps csvBytes "scores.csv"
            Expect.isFalse (isRejected first.Status) "the first upload is admitted"
            do! awaitExtractionIndexWrite storage first.Id
            do! corrupt storage
            let! second = uploadDocument deps csvBytes "scores-copy.csv"
            expectIndexRefusal second
        }

        testCaseAsync "versioning refuses rather than finding no predecessor"
        <| async {
            let storage = Fakes.TestBlobStorage() :> IBlobStorage

            let policies = {
                KnowledgePolicies.defaults with
                    Dedup = false
                    Versioning = true
            }

            do! corrupt storage
            let! doc = uploadDocument (mkDeps storage Logger.silent policies) csvBytes "scores.csv"
            expectIndexRefusal doc
        }

        testCaseAsync "deletion refuses rather than reporting the document absent"
        <| async {
            let storage = Fakes.TestBlobStorage() :> IBlobStorage
            do! corrupt storage
            let! result = deleteDocument (mkDeps storage Logger.silent KnowledgePolicies.defaults) "doc-1"

            match result with
            | Error reason -> Expect.stringContains reason "knowledge index" "the refusal names the unreadable index"
            | Ok() -> failtest "a delete over an unreadable index must refuse"
        }

        testCaseAsync "the listing renders an empty list and records the corruption"
        <| async {
            let storage = Fakes.TestBlobStorage() :> IBlobStorage
            let logger = Logger.recording ()
            do! corrupt storage
            let! docs = getDocuments (mkDeps storage logger KnowledgePolicies.defaults)
            Expect.isEmpty docs "the listing renders"

            Expect.isNonEmpty
                (logger.MessagesAt LogSeverity.Error
                 |> List.filter (fun m -> m.Contains "knowledge index"))
                "the corruption is recorded at Error"
        }

        testCaseAsync "an absent index is an empty scope, not a refusal"
        <| async {
            let storage = Fakes.TestBlobStorage() :> IBlobStorage
            let! read = loadIndex storage container
            Expect.equal read (Ok []) "absence reads as an empty index"
            let! doc = uploadDocument (mkDeps storage Logger.silent KnowledgePolicies.defaults) csvBytes "scores.csv"
            Expect.isFalse (isRejected doc.Status) "an upload into an empty scope is admitted"
        }

        // The background extraction's index write is a decider too: one that
        // read the index before it was corrupted refuses rather than replacing
        // the corrupt bytes. The write is held between its read and its write,
        // the index corrupted under it, and then let go. On a conditional
        // backend it loses its precondition, re-reads the corrupt index and
        // refuses. (A backend without conditional writes cannot detect a
        // writer outside the process lock, which is why the dedup case above
        // waits for this write before corrupting.)
        testCaseAsync "an extraction write that read the index before it was corrupted refuses"
        <| async {
            let innerStore =
                ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage()

            let inner = innerStore :> IBlobStorage
            let conditional = innerStore :> IConditionalBlobStorage
            let held = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let release =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let heldResult =
                TaskCompletionSource<Result<string, ConditionalWriteError>>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let mutable indexWrites = 0

            // The request path's entry is the first index write; the
            // extraction's attempt seed is the second.
            let storage =
                { new IBlobStorage with
                    member _.CanComposeFrom = inner.CanComposeFrom
                    member _.ComposeFrom(c, t, s) = inner.ComposeFrom(c, t, s)
                    member _.Download(c, b) = inner.Download(c, b)
                    member _.Upload(c, b, content) = inner.Upload(c, b, content)
                    member _.Delete(c, b) = inner.Delete(c, b)
                    member _.List(c, p) = inner.List(c, p)
                    member _.Exists(c, b) = inner.Exists(c, b)
                    member _.GetMetadata(c, b) = inner.GetMetadata(c, b)
                    member _.DownloadRange(c, b, o, l) = inner.DownloadRange(c, b, o, l)
                    member _.Erase(c, p, policy, dryRun) = inner.Erase(c, p, policy, dryRun)
                  interface IConditionalBlobStorage with
                      member _.DownloadWithETag(c, b) = conditional.DownloadWithETag(c, b)

                      member _.UploadWithETag(c, b, content, condition) = async {
                          let isHeld =
                              b = indexBlobName && System.Threading.Interlocked.Increment &indexWrites = 2

                          if isHeld then
                              held.TrySetResult() |> ignore
                              do! release.Task |> Async.AwaitTask

                          let! result = conditional.UploadWithETag(c, b, content, condition)

                          if isHeld then
                              heldResult.TrySetResult result |> ignore

                          return result
                      }
                }

            let within (task: Task) =
                task.WaitAsync(TimeSpan.FromMinutes 2.0) |> Async.AwaitTask

            let deps = mkDeps storage Logger.silent KnowledgePolicies.defaults
            let! first = uploadDocument deps csvBytes "scores.csv"
            Expect.isFalse (isRejected first.Status) "the first upload is admitted"
            do! within held.Task
            do! corrupt inner
            release.TrySetResult() |> ignore
            do! within heldResult.Task
            Expect.isError heldResult.Task.Result "the held write loses its precondition"
            let! second = uploadDocument deps csvBytes "scores-copy.csv"
            expectIndexRefusal second
            let! index = loadIndex inner container
            Expect.isError index "the corrupt index was not replaced"
        }
    ]