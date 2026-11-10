module ToolUp.Platform.Tests.InProcess.KnowledgeIndexReadTests

open System.Text
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
    ]