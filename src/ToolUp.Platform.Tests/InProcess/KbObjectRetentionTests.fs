module ToolUp.Platform.Tests.InProcess.KbObjectRetentionTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IIndexLifecycle
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 105 — KB original retention on IDataObjectStore ───────────
//
// The phase moves a KB original off the raw `knowledge/{docId}/{name}`
// blob convention and onto `IDataObjectStore`, which already provides
// content-addressable dedup at rest, a metadata envelope, a version
// chain, and the Phase 9h `Erase` surface.
//
// Four properties are worth pinning, and each is asserted against a
// control that would fail if the property were merely incidental:
//
//   * **Opt-in.** `IDataObjectStore` is registered for EVERY composed
//     deployment (`ComposeRuntimeServices`), so "a store is present"
//     could never have been the signal — probing DI would have moved
//     every existing deployment's originals on upgrade. The GP 11 case
//     below pins that an un-opted-in deployment still writes the
//     convention blob and creates no object at all.
//
//   * **Dedup at rest, and its agreement with Phase 14x.** The two
//     layers answer different questions: 14x decides whether to INGEST
//     a duplicate, 105 decides how many COPIES OF THE BYTES exist. The
//     reconciliation case runs with `withDocumentDedup false` — the
//     configuration where 14x deliberately admits both uploads as
//     separate documents — and asserts the object store still collapses
//     them onto one content blob. If the layers disagreed, that is
//     exactly where it would show.
//
//   * **The read fallback IS the migration.** A document uploaded
//     before the opt-in has no object, so retrieval falls back to the
//     convention blob — forever, with no backfill pass. Pinned by
//     writing a legacy document by hand and reading it back through the
//     composed-retention deps.
//
//   * **Erasure coverage — the actual delta.** A raw KB original was
//     invisible to a data-subject sweep: `Erase` matches on `CreatedBy`
//     and blob-convention writes record no such field. The erasure case
//     asserts a store-retained original IS matched, and its control
//     asserts a convention-path original is NOT — which is what makes
//     the assertion a statement about the phase rather than about the
//     store.

let private noopLogger =
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

/// Records the chunk ids the deletion fan-out was asked to remove, and
/// reports `clean` for each so the production code's partial-failure
/// branch is not what these tests happen to be exercising.
type private RecordingIndexLifecycle() =
    let deleted = ResizeArray<string>()
    let gate = obj ()

    member _.DeletedChunkIds = lock gate (fun () -> deleted |> List.ofSeq)

    interface IIndexLifecycle with
        member _.DeleteChunk _ chunkId = async {
            lock gate (fun () -> deleted.Add chunkId)

            return {
                IndexLifecycleReport.empty with
                    Succeeded = [ "vector-store" ]
            }
        }

        member _.DeleteDocument _ docId chunkCount = async {
            lock gate (fun () ->
                for i in 0 .. chunkCount - 1 do
                    deleted.Add(sprintf "%s:chunk:%d" docId i))

            return {
                IndexLifecycleReport.empty with
                    Succeeded = [ "vector-store" ]
            }
        }

        member _.DeleteByScope _ = async.Return IndexLifecycleReport.empty

        member _.Erase(_, _, _, _) =
            async.Return(
                Result.Ok {
                    HandlerName = "recording"
                    RecordsAffected = 0
                    Note = None
                }
            )

/// `ScopeId` and `Container` are deliberately the same string here:
/// `DataObjectStore.containerFor` is the identity, so the store and the
/// KB share one container and every assertion below can be made about
/// blob names in a single place. The two prefixes never collide —
/// originals land under `objects/`, the KB index under `knowledge/`.
let private mkDeps
    (storage: IBlobStorage)
    (queue: IngestionQueue)
    (lifecycle: IIndexLifecycle option)
    (objectStore: IDataObjectStore option)
    (dedup: KnowledgeDedupPolicy)
    (versioning: KnowledgeVersioningPolicy)
    (container: string)
    : KnowledgeApiDeps =
    {
        Storage = storage
        Queue = queue
        OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        Notifications = noopNotifications
        Logger = noopLogger
        Scope = {
            ScopeId = container
            Container = container
            Persist = true
        }
        UserId = "user-1"
        VectorScope = VectorKnowledgeTypes.Deployment
        VectorStore = None
        IndexLifecycle = lifecycle
        EventStore = None
        EmbeddingProvider = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
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
        DataObjectStore = objectStore
        QuotaPolicy = KnowledgeQuotaPolicy.unlimited
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        ContentScanner = None
        ScanPolicy = ContentScanPolicy.defaults
        DisclosureGate = None
        ArchiveImportPolicy = ArchiveImportPolicy.defaults
        UrlIngestionPolicy = UrlIngestionPolicy.disabled
        UrlFetcher = None
    }

let private csvOf (rowCount: int) (lastValue: string) : byte[] =
    let rows = [
        for i in 0 .. rowCount - 1 do
            let value = if i = rowCount - 1 then lastValue else sprintf "%04d" i

            sprintf "r%04d,%s" i value
    ]

    ("name,score\n" + String.concat "\n" rows) |> Encoding.UTF8.GetBytes

/// Extraction + enqueue run off the request path (`Async.Start`), so
/// every assertion has to wait for that background pass. Polls the
/// persisted index rather than sleeping a fixed interval — a fixed
/// sleep is the flake this codebase can least afford under a sequenced
/// Expecto run on a loaded machine.
let private waitForIngest (storage: IBlobStorage) (container: string) (docId: string) : Async<KnowledgeDocument> = async {
    let deadline = DateTimeOffset.UtcNow.AddSeconds 30.0
    let mutable result = None

    while result.IsNone && DateTimeOffset.UtcNow < deadline do
        let! index = loadIndex storage container

        match index |> List.tryFind (fun d -> d.Id = docId) with
        | Some doc ->
            match doc.Status with
            | Queued
            | ExtractingText -> do! Async.Sleep 25
            | _ -> result <- Some doc
        | None -> do! Async.Sleep 25

    match result with
    | Some doc -> return doc
    | None -> return failtest (sprintf "document %s never left the pre-extraction status within 30s" docId)
}

/// Every deduplicated content blob the object store holds in a scope.
let private contentBlobs (storage: IBlobStorage) (container: string) = async {
    let! all = storage.List(container, "objects/_content/")
    return all
}

let private newStore (storage: IBlobStorage) : IDataObjectStore =
    ToolUp.Platform.DataObjectStore.DataObjectStore(storage) :> IDataObjectStore

let tests =
    testList "Phase 105 — KB original retention on IDataObjectStore" [

        testCaseAsync "with retention composed the original is saved through the store, NOT at the convention blob path"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let container = "team-ret-a"

            let deps =
                mkDeps
                    storage
                    queue
                    None
                    (Some store)
                    KnowledgeDedupPolicy.enabled
                    KnowledgeVersioningPolicy.disabled
                    container

            let bytes = csvOf 40 "0039"
            let! doc = uploadDocument deps bytes "scores.csv"
            let! _ = waitForIngest storage container doc.Id

            // The store holds it, under objectId = docId.
            match! store.Get(container, doc.Id) with
            | Ok(stored, storedBytes) ->
                Expect.equal storedBytes bytes "the object store holds the original bytes verbatim"

                Expect.equal
                    stored.DataType
                    KnowledgeObjectRetentionPolicy.ObjectDataType
                    "the envelope's DataType selects KB originals out of a mixed scope"

                Expect.equal
                    stored.CreatedBy
                    "user-1"
                    "CreatedBy is stamped — this is what the Phase 9h Erase sweep matches on"

                Expect.equal
                    (stored.Metadata |> Map.tryFind "kb.fileName")
                    (Some "scores.csv")
                    "the envelope carries the original file name"
            | Error e -> failtest (sprintf "the original was not retrievable from the object store: %A" e)

            // And the convention path was NOT written.
            let! legacy = storage.Download(container, sprintf "knowledge/%s/scores.csv" doc.Id)

            Expect.isError
                legacy
                "the raw convention blob is not written when retention is composed — otherwise the bytes would be stored twice and the phase would buy nothing"

            // Retrieval returns it through the ordinary Phase 102 surface.
            let! original = getOriginalDocument deps doc.Id

            match original with
            | Ok o -> Expect.equal o.Content bytes "GetOriginalDocument returns the store-retained bytes"
            | Error e -> failtest (sprintf "GetOriginalDocument failed for a store-retained original: %A" e)
        }

        testCaseAsync
            "14x reconciliation — with document dedup OFF, two identical uploads are two documents but ONE content blob"
        <| async {
            // This is the configuration where the two dedup layers could
            // disagree: `withDocumentDedup false` tells Phase 14x to
            // admit both uploads as separate documents (a contracts
            // archive where each submission is its own record), and the
            // object store is then the only thing standing between the
            // scope and a second copy of the bytes.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let container = "team-ret-b"

            let deps =
                mkDeps
                    storage
                    queue
                    None
                    (Some store)
                    KnowledgeDedupPolicy.disabled
                    KnowledgeVersioningPolicy.disabled
                    container

            let bytes = csvOf 40 "0039"
            let! first = uploadDocument deps bytes "a.csv"
            let! _ = waitForIngest storage container first.Id
            let! second = uploadDocument deps bytes "b.csv"
            let! _ = waitForIngest storage container second.Id

            Expect.notEqual
                second.Id
                first.Id
                "withDocumentDedup false keeps its documented meaning — the second upload is its own document"

            let! index = loadIndex storage container
            Expect.hasLength index 2 "both documents are in the index"

            let! blobs = contentBlobs storage container

            Expect.hasLength
                blobs
                1
                "identical content collapses onto ONE deduplicated content blob — the object store dedups the bytes even where 14x deliberately did not dedup the document"

            // Both documents still resolve to the same bytes, so the
            // collapse is invisible to a reader.
            let! a = getOriginalDocument deps first.Id
            let! b = getOriginalDocument deps second.Id

            match a, b with
            | Ok x, Ok y ->
                Expect.equal x.Content bytes "the first document still resolves to its bytes"
                Expect.equal y.Content bytes "the second document resolves to the same bytes through its own objectId"
            | _ -> failtest "one of the two deduplicated documents was not retrievable"
        }

        testCaseAsync "a legacy convention-stored document stays retrievable — the read fallback IS the migration"
        <| async {
            // Written by hand exactly as a pre-105 upload would have left
            // it: an index entry plus a `knowledge/{docId}/{name}` blob,
            // and NO object in the store. Then read back through deps
            // that DO have retention composed, which is the upgrade path
            // an existing deployment actually takes.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let container = "team-ret-c"
            let legacyBytes = Encoding.UTF8.GetBytes "legacy,content\n1,2\n"

            let legacyDoc: KnowledgeDocument = {
                Id = "doc-legacy"
                FileName = "old.csv"
                FileType = "csv"
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = "user-1"
                Status = Complete 1
                SizeBytes = int64 legacyBytes.Length
                ChunkCount = 1
                Source = UploadedFile
                ContentHash = None
                Version = 1
                // Phase 502.C — untagged fixture.
                Tags = []
            }

            do! saveIndex storage container [ legacyDoc ]
            let! _ = storage.Upload(container, "knowledge/doc-legacy/old.csv", legacyBytes)

            let deps =
                mkDeps
                    storage
                    queue
                    None
                    (Some store)
                    KnowledgeDedupPolicy.enabled
                    KnowledgeVersioningPolicy.disabled
                    container

            // Precondition, so the assertion below cannot pass by the
            // store happening to hold the object.
            let! versions = store.ListVersions(container, "doc-legacy")

            Expect.isEmpty
                versions
                "the legacy document has no object in the store — that is what makes this the fallback case"

            let! original = getOriginalDocument deps "doc-legacy"

            match original with
            | Ok o ->
                Expect.equal
                    o.Content
                    legacyBytes
                    "a pre-105 document still resolves through the convention-path fallback"

                Expect.equal o.ContentType "text/csv" "and still reports its extension-derived content type"
            | Error e -> failtest (sprintf "the legacy convention-stored document was not retrievable: %A" e)
        }

        testCaseAsync "deleting a store-retained document removes BOTH its bytes and its chunks"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let lifecycle = RecordingIndexLifecycle()
            let container = "team-ret-d"

            let deps =
                mkDeps
                    storage
                    queue
                    (Some(lifecycle :> IIndexLifecycle))
                    (Some store)
                    KnowledgeDedupPolicy.enabled
                    KnowledgeVersioningPolicy.disabled
                    container

            let! doc = uploadDocument deps (csvOf 40 "0039") "gone.csv"
            let! ingested = waitForIngest storage container doc.Id

            Expect.isGreaterThan
                ingested.ChunkCount
                0
                "the fixture indexed chunks, so the chunk assertion below is not vacuous"

            let! blobsBefore = contentBlobs storage container
            Expect.isNonEmpty blobsBefore "the original's content blob exists before the delete"

            let! result = deleteDocument deps doc.Id
            Expect.isOk result "the delete succeeds"

            match! store.Get(container, doc.Id) with
            | Ok _ -> failtest "the object survived the delete"
            | Error _ -> ()

            let! blobsAfter = contentBlobs storage container

            Expect.isEmpty
                blobsAfter
                "the deduplicated content blob is garbage-collected once no object references it — bytes at rest are gone, not merely unreferenced"

            Expect.equal
                (lifecycle.DeletedChunkIds |> List.length)
                ingested.ChunkCount
                "every chunk was removed through the IIndexLifecycle fan-out — bytes AND chunks, which is the phase's acceptance"

            let! index = loadIndex storage container
            Expect.isEmpty index "the index entry is gone"
        }

        testCaseAsync
            "GDPR — a store-retained original is MATCHED by the subject erasure sweep; a convention-stored one is not"
        <| async {
            // The actual delta the phase buys. `IDataObjectStore.Erase`
            // matches an object when a version has `CreatedBy =
            // subjectUserId`; a raw blob write records no such field, so
            // a pre-105 KB original was structurally invisible to a
            // right-to-be-forgotten sweep. The control arm is what makes
            // this a statement about Phase 105 rather than about the
            // store's erasure implementation.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let container = "team-ret-e"

            let retained =
                mkDeps
                    storage
                    queue
                    None
                    (Some store)
                    KnowledgeDedupPolicy.enabled
                    KnowledgeVersioningPolicy.disabled
                    container

            let! doc = uploadDocument retained (csvOf 40 "0039") "personal.csv"
            let! _ = waitForIngest storage container doc.Id

            // Control: the same upload with retention NOT composed goes
            // to the convention path and contributes nothing erasable.
            let legacyContainer = "team-ret-e-legacy"

            let unretained =
                mkDeps
                    storage
                    queue
                    None
                    None
                    KnowledgeDedupPolicy.enabled
                    KnowledgeVersioningPolicy.disabled
                    legacyContainer

            let! legacy = uploadDocument unretained (csvOf 40 "0039") "personal.csv"
            let! _ = waitForIngest storage legacyContainer legacy.Id

            // A dry run counts without mutating — the shape a DSR
            // preview uses.
            match! store.Erase(container, "user-1", ErasurePolicy.HardDelete, true) with
            | Ok summary ->
                Expect.isGreaterThan
                    summary.RecordsAffected
                    0
                    "the dry run counts the subject's KB original as affected"
            | Error e -> failtest (sprintf "the dry-run erasure failed: %A" e)

            match! store.Get(container, doc.Id) with
            | Ok _ -> ()
            | Error e -> failtest (sprintf "the dry run must not have removed anything, but the object is gone: %A" e)

            match! store.Erase(container, "user-1", ErasurePolicy.HardDelete, false) with
            | Ok summary ->
                Expect.isGreaterThan summary.RecordsAffected 0 "the erasure removed the subject's KB original"
            | Error e -> failtest (sprintf "the erasure failed: %A" e)

            match! store.Get(container, doc.Id) with
            | Ok _ -> failtest "the store-retained original survived a HardDelete erasure of its creator"
            | Error _ -> ()

            let! blobs = contentBlobs storage container
            Expect.isEmpty blobs "the erased original's bytes are gone from the content pool, not merely dereferenced"

            // The control: nothing was erasable in the un-retained scope,
            // and its bytes are still sitting at the convention path.
            match! store.Erase(legacyContainer, "user-1", ErasurePolicy.HardDelete, true) with
            | Ok summary ->
                Expect.equal
                    summary.RecordsAffected
                    0
                    "a convention-stored original contributes NOTHING to the subject sweep — this is precisely the gap Phase 105 closes"
            | Error e -> failtest (sprintf "the control erasure probe failed: %A" e)

            let! stillThere = storage.Download(legacyContainer, sprintf "knowledge/%s/personal.csv" legacy.Id)

            Expect.isOk stillThere "and its bytes remain at rest after the sweep, which is the defect the phase removes"
        }

        testCaseAsync "GP 11 — without withObjectStoreRetention the convention blob is written and no object is created"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let container = "team-ret-f"

            // The default a deployment gets when it never opts in — note
            // that a real deployment HAS an IDataObjectStore registered,
            // which is exactly why the policy and not the registration is
            // the gate.
            let deps =
                mkDeps storage queue None None KnowledgeDedupPolicy.enabled KnowledgeVersioningPolicy.disabled container

            let bytes = csvOf 40 "0039"
            let! doc = uploadDocument deps bytes "plain.csv"
            let! _ = waitForIngest storage container doc.Id

            let! atConvention = storage.Download(container, sprintf "knowledge/%s/plain.csv" doc.Id)

            match atConvention with
            | Ok stored -> Expect.equal stored bytes "the pre-105 convention blob is written byte-for-byte"
            | Error e -> failtest (sprintf "the convention blob was not written: %s" e)

            let! versions = store.ListVersions(container, doc.Id)
            Expect.isEmpty versions "no object is created at all — an un-opted-in deployment makes no store call"

            let! blobs = contentBlobs storage container
            Expect.isEmpty blobs "and no deduplicated content blob exists"

            let! original = getOriginalDocument deps doc.Id

            match original with
            | Ok o -> Expect.equal o.Content bytes "retrieval is the Phase 102 path unchanged"
            | Error e -> failtest (sprintf "GetOriginalDocument failed on the un-opted-in path: %A" e)
        }

        testCaseAsync "510 reconciliation — a superseded version's original bytes are still preserved under retention"
        <| async {
            // Versioning archives the outgoing original before the live
            // one is overwritten, by reading it from wherever it lives.
            // Under retention that is the object store, so if the archive
            // step still assumed the convention path it would silently
            // write a version record claiming preserved bytes that were
            // never copied — the one thing an immutable-version store
            // must not do.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let store = newStore storage
            let container = "team-ret-g"

            let deps =
                mkDeps
                    storage
                    queue
                    None
                    (Some store)
                    KnowledgeDedupPolicy.enabled
                    KnowledgeVersioningPolicy.enabled
                    container

            let v1 = csvOf 60 "0059"
            let! first = uploadDocument deps v1 "revised.csv"
            let! _ = waitForIngest storage container first.Id

            let v2 = csvOf 60 "9999"
            let! second = uploadDocument deps v2 "revised.csv"
            let! _ = waitForIngest storage container second.Id

            Expect.equal second.Id first.Id "the re-upload supersedes in place"
            Expect.equal second.Version 2 "the lineage advanced"

            let! versions = getDocumentVersions deps first.Id
            Expect.hasLength versions 2 "the lineage reports two versions"

            let prior = versions |> List.item 1

            let! priorBytes = storage.Download(container, prior.OriginalBlobName)

            match priorBytes with
            | Ok bytes ->
                Expect.equal
                    bytes
                    v1
                    "the superseded version's original bytes were preserved, read out of the object store"
            | Error e -> failtest (sprintf "the prior version's preserved original was not readable: %s" e)

            // And the object store's own chain advanced in step, because
            // objectId = docId means version N of the lineage IS version
            // N of the object.
            let! objectVersions = store.ListVersions(container, first.Id)

            Expect.hasLength
                objectVersions
                2
                "the object store's version chain tracks the document lineage — the two version axes agree because the ids are the same"

            match! store.Get(container, first.Id) with
            | Ok(_, current) -> Expect.equal current v2 "the store's latest version is the current document content"
            | Error e -> failtest (sprintf "the current version was not retrievable: %A" e)
        }
    ]