module ToolUp.Platform.Tests.InProcess.Phase971KnowledgeBaseTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — a refused KB delete that is the operation says so ────
//
// `IBlobStorage.Delete` is idempotent on a missing blob, so an `Error` is
// a refusal: the bytes are still at rest. Document delete, the scope reset
// and the retention sweep each used to discard that result and report
// success — the document de-listed (so nothing would ever find it again)
// while its bytes stayed. These pins drive each delete through a double
// that refuses one named blob and assert the operation reports it AND
// leaves the thing a re-run finds the object by (the index entry / the
// index blob) in place.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noopNotifications =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

let private kbDeps (storage: IBlobStorage) (container: string) : KnowledgeApiDeps = {
    Storage = storage
    Queue = ToolUp.RAG.IngestionTypes.IngestionQueue()
    OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
    Notifications = noopNotifications
    Logger = silentLogger
    Scope = {
        ScopeId = container
        Container = container
        Persist = true
    }
    UserId = "user-1"
    VectorScope = ToolUp.Platform.VectorKnowledgeTypes.Deployment
    VectorStore = None
    IndexLifecycle = None
    EventStore = None
    EmbeddingProvider = None
    NarrativeStore = None
    AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
    OriginalResolver = KnowledgeBase.ServerOriginalSourceResolver.createDefault ()
    AuditLog = None
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    MarkIngestionFailed = fun _ _ _ -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    UploadPolicy = KnowledgeUploadPolicy.permissive
    DedupPolicy = KnowledgeDedupPolicy.enabled
    VersioningPolicy = KnowledgeVersioningPolicy.disabled
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

let private kbDoc (docId: string) (fileName: string) (source: KnowledgeSource) (uploadedAt: DateTimeOffset) = {
    Id = docId
    FileName = fileName
    FileType = "pdf"
    UploadedAt = uploadedAt
    UploadedBy = "user-1"
    Status = Complete 1
    SizeBytes = 2L
    ChunkCount = 1
    Source = source
    ContentHash = None
    Version = 1
    Tags = []
}

let private noteSource: NoteSource = {
    Title = "Quarterly plan"
    Author = "user-1"
    CreatedAt = DateTimeOffset.UtcNow
    LastEditedAt = None
}

/// An uploaded file with a full Phase 510 lineage at rest: the live
/// original, one preserved prior-version original, the version manifest
/// and the chunk-hash manifest.
let private seedFileWithLineage (storage: IBlobStorage) (container: string) (uploadedAt: DateTimeOffset) = async {
    let docId = Guid.NewGuid().ToString()
    let doc = kbDoc docId "report.pdf" UploadedFile uploadedAt
    do! upsertIndexEntry storage container doc
    let! _ = storage.Upload(container, sprintf "knowledge/%s/report.pdf" docId, [| 1uy; 2uy |])
    let! _ = storage.Upload(container, versionedOriginalBlobName docId 1 "report.pdf", [| 3uy |])
    let! _ = storage.Upload(container, versionsBlobName docId, Encoding.UTF8.GetBytes "[]")
    let! _ = storage.Upload(container, chunkHashesBlobName docId, Encoding.UTF8.GetBytes "[]")
    return doc
}

let private seedNote (storage: IBlobStorage) (container: string) (uploadedAt: DateTimeOffset) = async {
    let docId = Guid.NewGuid().ToString()
    let doc = kbDoc docId "Quarterly plan.md" (Note noteSource) uploadedAt
    do! upsertIndexEntry storage container doc
    let! _ = storage.Upload(container, noteBodyBlobName docId, Encoding.UTF8.GetBytes "# plan")
    return doc
}

let private isListed (storage: IBlobStorage) (container: string) (docId: string) = async {
    let! index = loadIndex storage container
    return index |> List.exists (fun d -> d.Id = docId)
}

/// A refused delete of `refusedName` answers `Error`, keeps the document
/// listed and the blob at rest; a re-run over healthy storage then finds
/// the document by its index entry and completes.
let private refusedDeleteKeepsDocListed
    (container: string)
    (seed: IBlobStorage -> Async<KnowledgeDocument>)
    (refusedName: KnowledgeDocument -> string)
    =
    async {
        let inner = InMemoryBlobStorage() :> IBlobStorage
        let! doc = seed inner
        let refused = refusedName doc
        let refusing = DeleteRefusingBlobStorage(inner, (=) refused) :> IBlobStorage

        let! result = KnowledgeBase.ServerApiDocuments.deleteDocument (kbDeps refusing container) doc.Id

        Expect.isError result "a refused delete is reported, not swallowed"

        match result with
        | Error msg -> Expect.stringContains msg refused "the error names the blob still at rest"
        | Ok() -> ()

        let! listed = isListed inner container doc.Id
        Expect.isTrue listed "the document stays listed so a re-run finds it"

        let! stillThere = inner.Exists(container, refused)
        Expect.isTrue stillThere "the refused blob is still at rest"

        let! rerun = KnowledgeBase.ServerApiDocuments.deleteDocument (kbDeps inner container) doc.Id
        Expect.isOk rerun "the re-run over healthy storage completes"

        let! listedAfter = isListed inner container doc.Id
        Expect.isFalse listedAfter "the re-run de-lists the document"

        let! residue = inner.List(container, sprintf "knowledge/%s/" doc.Id)
        Expect.isEmpty residue "the re-run leaves nothing under the document"
    }

/// `List` throws (the reset's fallback path) and `Delete` refuses the
/// blob names `refused` selects.
type private ListThrowingDeleteRefusingBlobStorage(inner: IBlobStorage, refused: string -> bool) =
    let refusing = DeleteRefusingBlobStorage(inner, refused) :> IBlobStorage

    interface IBlobStorage with
        member _.CanComposeFrom = false
        member _.ComposeFrom(a, b, c) = refusing.ComposeFrom(a, b, c)

        member _.Upload(container, blobName, content) =
            refusing.Upload(container, blobName, content)

        member _.Download(container, blobName) = refusing.Download(container, blobName)
        member _.Delete(container, blobName) = refusing.Delete(container, blobName)

        member _.List(_, _) = async { return raise (InvalidOperationException "simulated storage list failure") }

        member _.Exists(container, blobName) = refusing.Exists(container, blobName)

        member _.GetMetadata(container, blobName) =
            refusing.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            refusing.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            refusing.Erase(container, prefix, policy, dryRun)

let private sweepPolicy = {
    KnowledgeRetentionPolicy.retainForever with
        MaxAge = Some(TimeSpan.FromDays 30.0)
        ExpireNotes = true
}

/// The retention sweep over a double refusing `refusedName`: the expired
/// document is not reported purged, stays listed for the next sweep, and
/// its blob survives.
let private sweepRefusalKeepsDocListed
    (container: string)
    (seed: IBlobStorage -> DateTimeOffset -> Async<KnowledgeDocument>)
    (refusedName: KnowledgeDocument -> string)
    =
    async {
        let inner = InMemoryBlobStorage() :> IBlobStorage
        let sweepNow = DateTimeOffset.UtcNow
        let! doc = seed inner (sweepNow.AddDays -100.0)
        let refused = refusedName doc
        let refusing = DeleteRefusingBlobStorage(inner, (=) refused) :> IBlobStorage

        let! report =
            KnowledgeBase.ServerRetentionSweep.sweepScope refusing None None silentLogger sweepNow sweepPolicy container

        Expect.isEmpty report.Purged "a document whose bytes stayed is not reported purged"
        Expect.isNonEmpty report.Failures "the refusal is reported as a sweep failure"

        let! listed = isListed inner container doc.Id
        Expect.isTrue listed "the document stays listed so the next sweep retries it"

        let! stillThere = inner.Exists(container, refused)
        Expect.isTrue stillThere "the refused blob is still at rest"

        let! retry =
            KnowledgeBase.ServerRetentionSweep.sweepScope inner None None silentLogger sweepNow sweepPolicy container

        Expect.equal retry.Purged [ doc.Id ] "the next sweep over healthy storage purges it"
    }

let tests =
    testList "Phase 971 - KnowledgeBase" [

        testCaseAsync "deleteDocument: a refused original delete is an Error and the document stays listed"
        <| refusedDeleteKeepsDocListed
            "team-p971-orig"
            (fun s -> seedFileWithLineage s "team-p971-orig" DateTimeOffset.UtcNow)
            (fun d -> sprintf "knowledge/%s/report.pdf" d.Id)

        testCaseAsync "deleteDocument: a refused note-body delete is an Error and the note stays listed"
        <| refusedDeleteKeepsDocListed
            "team-p971-note"
            (fun s -> seedNote s "team-p971-note" DateTimeOffset.UtcNow)
            (fun d -> noteBodyBlobName d.Id)

        testCaseAsync
            "deleteDocument: a refused prior-version delete is an Error, the document stays listed and the version manifest is kept"
        <| async {
            let container = "team-p971-ver"
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let! doc = seedFileWithLineage inner container DateTimeOffset.UtcNow
            let versionBlob = versionedOriginalBlobName doc.Id 1 "report.pdf"
            let refusing = DeleteRefusingBlobStorage(inner, (=) versionBlob) :> IBlobStorage

            let! result = KnowledgeBase.ServerApiDocuments.deleteDocument (kbDeps refusing container) doc.Id
            Expect.isError result "a refused prior-version delete is reported"

            let! manifest = inner.Exists(container, versionsBlobName doc.Id)
            Expect.isTrue manifest "the version manifest still describes the surviving prior version"

            do!
                refusedDeleteKeepsDocListed
                    "team-p971-ver2"
                    (fun s -> seedFileWithLineage s "team-p971-ver2" DateTimeOffset.UtcNow)
                    (fun d -> versionedOriginalBlobName d.Id 1 "report.pdf")
        }

        testCaseAsync "deleteDocument: a refused version-manifest delete is an Error and the document stays listed"
        <| refusedDeleteKeepsDocListed
            "team-p971-vman"
            (fun s -> seedFileWithLineage s "team-p971-vman" DateTimeOffset.UtcNow)
            (fun d -> versionsBlobName d.Id)

        testCaseAsync "deleteDocument: a refused chunk-hash-manifest delete is an Error and the document stays listed"
        <| refusedDeleteKeepsDocListed
            "team-p971-hash"
            (fun s -> seedFileWithLineage s "team-p971-hash" DateTimeOffset.UtcNow)
            (fun d -> chunkHashesBlobName d.Id)

        testCaseAsync "resetIndex: a refused wipe is an Error and the index blob is kept"
        <| async {
            let container = "team-p971-reset"
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let! doc = seedFileWithLineage inner container DateTimeOffset.UtcNow
            let raw = sprintf "knowledge/%s/report.pdf" doc.Id
            let refusing = DeleteRefusingBlobStorage(inner, (=) raw) :> IBlobStorage

            let! result = KnowledgeBase.ServerApiNarrative.resetIndex (kbDeps refusing container)
            Expect.isError result "a refused wipe is reported"

            match result with
            | Error msg -> Expect.stringContains msg raw "the error names the blob still at rest"
            | Ok() -> ()

            let! indexKept = inner.Exists(container, indexBlobName)
            Expect.isTrue indexKept "the index blob is kept, so the surviving bytes stay listed"

            let! rawKept = inner.Exists(container, raw)
            Expect.isTrue rawKept "the refused blob is still at rest"

            let! rerun = KnowledgeBase.ServerApiNarrative.resetIndex (kbDeps inner container)
            Expect.isOk rerun "a re-run over healthy storage completes"

            let! residue = inner.List(container, "knowledge/")
            Expect.isEmpty residue "the re-run wipes everything, the index last"
        }

        testCaseAsync "resetIndex: when List fails and the index-only fallback delete is refused, the reset is an Error"
        <| async {
            let container = "team-p971-reset-fb"
            let inner = InMemoryBlobStorage() :> IBlobStorage
            let! _ = seedFileWithLineage inner container DateTimeOffset.UtcNow

            let storage =
                ListThrowingDeleteRefusingBlobStorage(inner, (=) indexBlobName) :> IBlobStorage

            let! result = KnowledgeBase.ServerApiNarrative.resetIndex (kbDeps storage container)
            Expect.isError result "a reset that removed nothing is not reported as done"

            let! indexKept = inner.Exists(container, indexBlobName)
            Expect.isTrue indexKept "control: the index blob really is still there"
        }

        testCaseAsync "retention sweep: a refused raw-blob delete leaves the document listed for the next sweep"
        <| sweepRefusalKeepsDocListed
            "user-p971-sweep-raw"
            (fun s at -> seedFileWithLineage s "user-p971-sweep-raw" at)
            (fun d -> sprintf "knowledge/%s/report.pdf" d.Id)

        testCaseAsync "retention sweep: a refused note-body delete leaves the note listed for the next sweep"
        <| sweepRefusalKeepsDocListed
            "user-p971-sweep-note"
            (fun s at -> seedNote s "user-p971-sweep-note" at)
            (fun d -> noteBodyBlobName d.Id)
    ]