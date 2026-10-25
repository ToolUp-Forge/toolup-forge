module ToolUp.Platform.Tests.InProcess.TerminalStatusAnnouncementTests

// Every terminal ingestion status a knowledge-base document can reach is
// announced on `"KnowledgeBase.IngestionStatus"` — including the five that
// are set directly on the upload path rather than by a chunk callback: an
// unsupported format, a missing OCR capability, a recognised-but-empty
// extraction, an unchanged re-upload and an empty narrative commit.

open System
open System.Collections.Concurrent
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerApiNarrative
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

/// Records every `CustomNotification` published under the ingestion key.
type private CapturingChannel() =
    let published = ConcurrentQueue<string * IngestionStatusUpdate>()
    member _.Published = List.ofSeq published

    interface INotificationChannel with
        member _.Publish(scope, notification) = async {
            match notification with
            | CustomNotification(key, json) when key = IngestionStatusNotificationKey ->
                let options = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
                published.Enqueue(scope, JsonSerializer.Deserialize<IngestionStatusUpdate>(json, options))
            | _ -> ()
        }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private mkDeps
    (storage: IBlobStorage)
    (channel: INotificationChannel)
    (versioning: KnowledgeVersioningPolicy)
    (dedup: KnowledgeDedupPolicy)
    : KnowledgeApiDeps =
    {
        Storage = storage
        Queue = IngestionQueue()
        OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        Notifications = channel
        Logger = noopLogger
        Scope = {
            ScopeId = "stream-team"
            Container = "team-stream"
            Persist = true
        }
        UserId = "user-1"
        VectorScope = Team "stream-team"
        VectorStore = None
        IndexLifecycle = None
        EventStore = None
        EmbeddingProvider = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AuthenticatedUser "user-1")
        OriginalResolver = createDefault ()
        AuditLog = None
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
        ScopeResolvedFromRequest = true
        UploadPolicy = KnowledgeUploadPolicy.permissive
        DedupPolicy = dedup
        VersioningPolicy = versioning
        DataObjectStore = None
        QuotaPolicy = KnowledgeQuotaPolicy.unlimited
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        ContentScanner = None
        ScanPolicy = ContentScanPolicy.defaults
        DisclosureGate = None
        ArchiveImportPolicy = ArchiveImportPolicy.defaults
        UrlIngestionPolicy = UrlIngestionPolicy.disabled
        UrlFetcher = None
    }

let private defaultDeps storage channel =
    mkDeps storage channel KnowledgeVersioningPolicy.disabled KnowledgeDedupPolicy.enabled

/// Extraction runs off the request path, so wait for the announcement.
let rec private waitPublished (channel: CapturingChannel) (docId: string) (attempts: int) = async {
    match channel.Published |> List.filter (fun (_, u) -> u.DocumentId = docId) with
    | [] when attempts > 0 ->
        do! Async.Sleep 50
        return! waitPublished channel docId (attempts - 1)
    | found -> return found
}

/// One announcement, to the uploader, with the outcome named.
let private expectOne (published: (string * IngestionStatusUpdate) list) (outcome: string) =
    match published with
    | [ scope, update ] ->
        Expect.equal scope "user-1" "published to the uploader"
        Expect.equal update.Outcome outcome "the terminal status is the one announced"
        Expect.equal update.UploadedBy "user-1" "the payload names the uploader"
        update
    | other -> failtestf "expected exactly one %s announcement, got %A" outcome other

let private bytesOf (s: string) = Text.Encoding.UTF8.GetBytes s

let tests =
    testSequenced
    <| testList "Ingestion status stream — every terminal status is announced" [
        testCaseAsync "an unsupported format announces UnsupportedFormat"
        <| async {
            let channel = CapturingChannel()
            let deps = defaultDeps (InMemoryBlobStorage() :> IBlobStorage) channel
            let! doc = uploadDocument deps (bytesOf "binary blob") "drawing.xyz"
            let! published = waitPublished channel doc.Id 100
            let update = expectOne published "UnsupportedFormat"
            Expect.stringContains update.ErrorReason "xyz" "the detail travels in ErrorReason"
        }

        testCaseAsync "an image with no OCR companion announces OcrUnavailable"
        <| async {
            let channel = CapturingChannel()
            let deps = defaultDeps (InMemoryBlobStorage() :> IBlobStorage) channel
            let! doc = uploadDocument deps (bytesOf "not really a png") "scan.png"
            let! published = waitPublished channel doc.Id 100
            let update = expectOne published "OcrUnavailable"
            Expect.stringContains update.ErrorReason "IOcrProvider" "the detail travels in ErrorReason"
        }

        testCaseAsync "a recognised-but-empty extraction announces Complete 0"
        <| async {
            let channel = CapturingChannel()
            let deps = defaultDeps (InMemoryBlobStorage() :> IBlobStorage) channel
            let! doc = uploadDocument deps (bytesOf "   ") "empty.txt"
            let! published = waitPublished channel doc.Id 100
            let update = expectOne published "Complete"
            Expect.equal update.ChunkCount 0 "nothing to index"
        }

        testCaseAsync "an unchanged re-upload announces Complete with the full chunk count"
        <| async {
            let channel = CapturingChannel()

            let deps =
                mkDeps
                    (InMemoryBlobStorage() :> IBlobStorage)
                    channel
                    KnowledgeVersioningPolicy.enabled
                    KnowledgeDedupPolicy.disabled

            let body =
                bytesOf "first paragraph of the handbook\n\nsecond paragraph of the handbook"

            let! first = uploadDocument deps body "handbook.txt"
            // The first upload enqueues its chunks; with no drainer, nothing settles it.
            let! _ = waitPublished channel first.Id 4
            let! second = uploadDocument deps body "handbook.txt"
            let! published = waitPublished channel second.Id 100
            let update = expectOne published "Complete"
            Expect.isGreaterThan update.ChunkCount 0 "the reused chunks are counted"
        }

        testCaseAsync "an empty narrative commit announces Complete 0"
        <| async {
            let channel = CapturingChannel()
            let deps = defaultDeps (InMemoryBlobStorage() :> IBlobStorage) channel

            let request: IngestNarrativeRequest = {
                Document = {
                    Title = "Empty summary"
                    Subtitle = None
                    Sections = []
                    Provenance =
                        Some {
                            ModuleId = "ModA"
                            PageRoute = None
                            GeneratedAt = DateTimeOffset.UtcNow
                            SettingsKey = "k"
                            SettingsDisplay = []
                        }
                    Lang = None
                    CanonicalUrl = None
                }
                Overwrite = false
            }

            let! result = ingestNarrative deps request
            let doc = Expect.wantOk result "the narrative commits"
            let! published = waitPublished channel doc.Id 100
            let update = expectOne published "Complete"
            Expect.equal update.ChunkCount 0 "nothing to index"
        }
    ]