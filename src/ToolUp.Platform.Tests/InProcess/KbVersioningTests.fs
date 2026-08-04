module ToolUp.Platform.Tests.InProcess.KbVersioningTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IIndexLifecycle
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 510 — KB document versioning + incremental re-index ───────
//
// Three behaviours that only make sense together, which is why they are
// one phase rather than three:
//
//   * **Lineage.** A re-upload of an EDITED file under a name the scope
//     already holds supersedes in place — one document, `Version + 1`,
//     the prior version preserved as an immutable record with its
//     original bytes copied aside. Before this, the re-upload landed as
//     a second unrelated document and the corpus quietly accumulated
//     revisions nobody could relate to each other.
//
//   * **The orphan tail.** Superseding in place means REUSING the
//     `{docId}:chunk:{n}` namespace, and a shorter new version's upserts
//     only cover `0 .. newCount-1`. Positions `newCount .. oldCount-1`
//     would survive in the vector store AND the sparse index under the
//     live document id, so retrieval would keep serving the previous
//     version's tail as current content with nothing to signal it. The
//     lineage work is what MAKES this defect reachable on the upload
//     path, so the fix ships with it, not after it.
//
//   * **Incremental re-index.** Only chunk positions whose content
//     actually changed are re-enqueued. The pin below asserts the
//     enqueued set directly rather than reading a cache counter: what
//     matters is that the unchanged chunk never reaches the ingestion
//     job at all, and a telemetry assertion could pass while the work
//     was still being done.
//
// The GP 11 arm is asserted explicitly: a deployment that never composes
// `withDocumentVersioning` behaves exactly as it did before — two
// documents, no supersede, no sidecar, no deletion fan-out.

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

/// Records every chunk id the deletion fan-out was asked to remove.
/// Reports `clean` for each, so the production code's partial-failure
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

let private mkDeps
    (storage: IBlobStorage)
    (queue: IngestionQueue)
    (lifecycle: IIndexLifecycle option)
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
        VectorScope = Deployment
        VectorStore = None
        IndexLifecycle = lifecycle
        EventStore = None
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
        DedupPolicy = KnowledgeDedupPolicy.enabled
        VersioningPolicy = versioning
        QuotaPolicy = KnowledgeQuotaPolicy.unlimited
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        // Phase 515 — no scanner composed: the pre-515 upload path.
        ContentScanner = None
        ScanPolicy = ContentScanPolicy.defaults
        DisclosureGate = None
        // Phase 511 — bulk import is not exercised here: archive guards at
        // their shipped defaults, URL ingestion inert, no transport.
        ArchiveImportPolicy = ArchiveImportPolicy.defaults
        UrlIngestionPolicy = UrlIngestionPolicy.disabled
        UrlFetcher = None
    }

// ─── Fixtures ────────────────────────────────────────────────────────

/// A CSV big enough that the tabular chunker (512-token budget, zero
/// overlap) emits SEVERAL row-group chunks on top of the leading
/// "schema + sample" chunk. Row width is fixed so two documents with the
/// same row count pack into the same number of chunks — the property the
/// incremental pin relies on, without depending on the exact boundaries.
let private csvOf (rowCount: int) (mutatedLastValue: string) : byte[] =
    let rows = [
        for i in 0 .. rowCount - 1 do
            let value =
                if i = rowCount - 1 then
                    mutatedLastValue
                else
                    sprintf "%04d" i

            sprintf "r%04d,%s" i value
    ]

    ("name,score\n" + String.concat "\n" rows) |> Encoding.UTF8.GetBytes

/// `uploadDocument` spawns extraction + enqueue off the request path
/// (`Async.Start`), so every assertion about chunks has to wait for that
/// background pass to land. Poll the persisted index until the document
/// leaves the pre-extraction statuses, rather than sleeping a fixed
/// interval — a fixed sleep is the flake this codebase can least afford
/// under a sequenced Expecto run on a loaded machine.
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

/// Drain every job the queue currently holds.
let private drain (queue: IngestionQueue) : DocumentIngestionJob list =
    let jobs = ResizeArray<DocumentIngestionJob>()
    let mutable job = Unchecked.defaultof<DocumentIngestionJob>

    while queue.Reader.TryRead(&job) do
        jobs.Add job

    jobs |> List.ofSeq

let private enqueuedChunkIds (jobs: DocumentIngestionJob list) =
    jobs |> List.collect (fun j -> j.Chunks |> List.map fst)

let tests =
    testList "Phase 510 — KB document versioning + incremental re-index" [

        testCaseAsync
            "a re-uploaded edited document becomes ONE document with two versions; the prior version stays addressable"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let lifecycle = RecordingIndexLifecycle()

            let deps =
                mkDeps storage queue (Some(lifecycle :> IIndexLifecycle)) KnowledgeVersioningPolicy.enabled "team-ver-a"

            let v1 = csvOf 300 "0299"
            let! first = uploadDocument deps v1 "scores.csv"
            let! firstIngested = waitForIngest storage "team-ver-a" first.Id
            drain queue |> ignore

            // Byte-DIFFERENT content under the same name — the edit case.
            let v2 = csvOf 300 "9999"
            let! second = uploadDocument deps v2 "scores.csv"
            let! secondIngested = waitForIngest storage "team-ver-a" second.Id

            Expect.equal second.Id first.Id "the re-upload supersedes in place — same document id, not a new document"
            Expect.equal first.Version 1 "the original upload is version 1"
            Expect.equal second.Version 2 "the re-upload is version 2 of the same lineage"

            let! index = loadIndex storage "team-ver-a"
            Expect.hasLength index 1 "the scope holds exactly ONE document, not two unrelated ones"

            Expect.equal
                (index |> List.head |> _.Version)
                2
                "the live index entry describes the current (latest) version"

            // Prior version addressable, with its bytes preserved.
            let! versions = getDocumentVersions deps first.Id
            Expect.hasLength versions 2 "the lineage reports two versions"

            Expect.equal
                (versions |> List.map _.Version)
                [ 2; 1 ]
                "versions come back newest-first, current version included"

            let current = versions |> List.head
            let prior = versions |> List.item 1
            Expect.isNone current.SupersededAt "the current version carries no supersede timestamp"
            Expect.isSome prior.SupersededAt "the prior version records when it was superseded"

            let! priorBytes = storage.Download("team-ver-a", prior.OriginalBlobName)

            match priorBytes with
            | Ok bytes -> Expect.equal bytes v1 "the superseded version's ORIGINAL bytes are preserved verbatim"
            | Error e -> failtest (sprintf "the prior version's preserved original was not readable: %s" e)

            // The live original is the CURRENT version's bytes.
            let! liveBytes = storage.Download("team-ver-a", sprintf "knowledge/%s/scores.csv" first.Id)

            match liveBytes with
            | Ok bytes -> Expect.equal bytes v2 "the conventional live blob path now holds the current version"
            | Error e -> failtest (sprintf "the live original was not readable: %s" e)

            Expect.isGreaterThan firstIngested.ChunkCount 1 "the fixture produced a multi-chunk document"
            Expect.isGreaterThan secondIngested.ChunkCount 1 "the superseding version is also multi-chunk"
        }

        testCaseAsync "a SHORTER new version leaves ZERO orphan chunks — the removed tail is deleted from every index"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let lifecycle = RecordingIndexLifecycle()

            let deps =
                mkDeps storage queue (Some(lifecycle :> IIndexLifecycle)) KnowledgeVersioningPolicy.enabled "team-ver-b"

            let! first = uploadDocument deps (csvOf 400 "0399") "report.csv"
            let! longVersion = waitForIngest storage "team-ver-b" first.Id
            drain queue |> ignore

            // A much shorter revision: strictly fewer chunks.
            let! second = uploadDocument deps (csvOf 8 "0007") "report.csv"
            let! shortVersion = waitForIngest storage "team-ver-b" second.Id
            drain queue |> ignore

            Expect.isLessThan
                shortVersion.ChunkCount
                longVersion.ChunkCount
                "the fixture actually shrank the document (otherwise there is no tail to orphan)"

            // The exact set of ids the previous version occupied and the
            // new one does not.
            let expectedTail = [
                for i in shortVersion.ChunkCount .. longVersion.ChunkCount - 1 do
                    sprintf "%s:chunk:%d" first.Id i
            ]

            Expect.equal
                (lifecycle.DeletedChunkIds |> List.sort)
                (expectedTail |> List.sort)
                "every removed trailing chunk id — and only those — was deleted through the IIndexLifecycle fan-out"

            Expect.isNonEmpty expectedTail "the tail was non-empty, so this assertion is not vacuous"

            let! index = loadIndex storage "team-ver-b"

            Expect.equal
                (index |> List.head |> _.ChunkCount)
                shortVersion.ChunkCount
                "the live document reports only the chunks the current version actually indexed"
        }

        testCaseAsync "an unchanged section is NOT re-embedded — only changed chunk positions reach the ingestion job"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let lifecycle = RecordingIndexLifecycle()

            let deps =
                mkDeps storage queue (Some(lifecycle :> IIndexLifecycle)) KnowledgeVersioningPolicy.enabled "team-ver-c"

            let! first = uploadDocument deps (csvOf 300 "0299") "data.csv"
            let! v1 = waitForIngest storage "team-ver-c" first.Id
            let firstJobs = drain queue

            Expect.equal
                (enqueuedChunkIds firstJobs |> List.length)
                v1.ChunkCount
                "the FIRST ingest enqueues every chunk — pre-510 behaviour, and the baseline the diff is measured against"

            // Same row count, same row widths: identical chunk packing,
            // with exactly one row-group chunk's content changed.
            let! second = uploadDocument deps (csvOf 300 "9999") "data.csv"
            let! v2 = waitForIngest storage "team-ver-c" second.Id
            let secondJobs = drain queue
            let reEnqueued = enqueuedChunkIds secondJobs

            Expect.equal v2.ChunkCount v1.ChunkCount "the fixture kept the chunk count stable across the edit"

            Expect.isNonEmpty reEnqueued "the changed content WAS re-embedded — the diff does not skip real edits"

            Expect.isLessThan
                reEnqueued.Length
                v2.ChunkCount
                "strictly fewer chunks were re-embedded than the document holds — unchanged sections were skipped"

            // The leading "schema + sample" chunk covers the first five
            // rows, which this edit did not touch, so it is the concrete
            // unchanged chunk we can name.
            Expect.isFalse
                (reEnqueued |> List.contains (sprintf "%s:chunk:0" first.Id))
                "the unchanged leading chunk was not re-enqueued, so it is neither re-chunked nor re-embedded"

            Expect.isEmpty
                lifecycle.DeletedChunkIds
                "an equal-length revision deletes nothing — there is no tail to clean up"
        }

        testCaseAsync
            "a byte-identical re-upload still dedups — no new version is minted for content that did not change"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()

            let deps = mkDeps storage queue None KnowledgeVersioningPolicy.enabled "team-ver-d"

            let bytes = csvOf 40 "0039"
            let! first = uploadDocument deps bytes "same.csv"
            let! _ = waitForIngest storage "team-ver-d" first.Id

            let! second = uploadDocument deps bytes "same.csv"

            Expect.equal second.Id first.Id "the duplicate resolves onto the existing document"

            Expect.equal
                second.Version
                1
                "identical bytes are not a new revision — dedup wins before versioning is consulted"

            let! versions = getDocumentVersions deps first.Id
            Expect.hasLength versions 1 "no superseded-version record was written for a no-op re-upload"
        }

        testCaseAsync "GP 11 — without withDocumentVersioning a re-upload lands as a second, unrelated document"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let lifecycle = RecordingIndexLifecycle()

            // The default a deployment gets when it never opts in.
            let deps =
                mkDeps
                    storage
                    queue
                    (Some(lifecycle :> IIndexLifecycle))
                    KnowledgeVersioningPolicy.disabled
                    "team-ver-e"

            let! first = uploadDocument deps (csvOf 300 "0299") "scores.csv"
            let! _ = waitForIngest storage "team-ver-e" first.Id
            drain queue |> ignore

            let! second = uploadDocument deps (csvOf 8 "0007") "scores.csv"
            let! secondIngested = waitForIngest storage "team-ver-e" second.Id
            let secondJobs = drain queue

            Expect.notEqual second.Id first.Id "pre-510 behaviour: the re-upload is its own document"

            let! index = loadIndex storage "team-ver-e"
            Expect.hasLength index 2 "both documents are in the index, unrelated"
            Expect.all index (fun d -> d.Version = 1) "nothing is versioned when versioning is not composed"

            Expect.isEmpty
                lifecycle.DeletedChunkIds
                "no deletion fan-out runs — the two documents own disjoint chunk namespaces, so there is no tail"

            Expect.equal
                (enqueuedChunkIds secondJobs |> List.length)
                secondIngested.ChunkCount
                "every chunk of the second document is embedded — no incremental skipping without a lineage"

            let! versions = storage.List("team-ver-e", sprintf "knowledge/%s/versions" first.Id)
            Expect.isEmpty versions "no version sidecar is written at all"
        }

        testCaseAsync "deleting a versioned document removes its preserved prior-version originals and sidecars"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let lifecycle = RecordingIndexLifecycle()

            let deps =
                mkDeps storage queue (Some(lifecycle :> IIndexLifecycle)) KnowledgeVersioningPolicy.enabled "team-ver-f"

            let! first = uploadDocument deps (csvOf 40 "0039") "doc.csv"
            let! _ = waitForIngest storage "team-ver-f" first.Id
            let! second = uploadDocument deps (csvOf 40 "9999") "doc.csv"
            let! _ = waitForIngest storage "team-ver-f" second.Id

            let! beforeDelete = storage.List("team-ver-f", sprintf "knowledge/%s/" first.Id)

            Expect.isTrue
                (beforeDelete |> List.exists (fun b -> b.Contains "/versions/"))
                "a preserved prior-version original exists before the delete"

            let! deleted = deleteDocument deps first.Id
            Expect.isOk deleted "the delete succeeds"

            let! remaining = storage.List("team-ver-f", sprintf "knowledge/%s/" first.Id)

            Expect.isEmpty
                remaining
                "the whole lineage is gone — preserved originals, version sidecar and chunk-hash manifest included"
        }

        testCaseAsync "a pre-510 index record (no Version property) loads as version 1 of its own lineage"
        <| async {
            // `loadIndex` swallows deserialisation failures into an empty
            // list, and a missing JSON number absorbs to 0 rather than to
            // anything detectable downstream — so this pins BOTH that the
            // legacy record still loads and that the store's coercion
            // fired.
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let legacyJson =
                """[{"Id":"doc-legacy","FileName":"old.pdf","FileType":"pdf","UploadedAt":"2026-01-01T00:00:00+00:00","UploadedBy":"user-1","Status":{"Complete":3},"SizeBytes":10,"ChunkCount":3,"Source":"UploadedFile"}]"""

            let! _ = storage.Upload("team-ver-g", "knowledge/index.json", Encoding.UTF8.GetBytes legacyJson)

            let! index = loadIndex storage "team-ver-g"

            match index with
            | [ doc ] ->
                Expect.equal doc.Id "doc-legacy" "the legacy record still loads — the widening did not blank the index"
                Expect.equal doc.ContentHash None "the pre-14x missing option property still absorbs to None"
                Expect.equal doc.Version 1 "the missing Version number is coerced to 1 at the store, never left at 0"
            | other -> failtest $"expected the single legacy document, got %A{other}"
        }
    ]