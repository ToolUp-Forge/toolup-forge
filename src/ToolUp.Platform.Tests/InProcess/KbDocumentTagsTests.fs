module ToolUp.Platform.Tests.InProcess.KbDocumentTagsTests

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

// ─── Phase 502.C — document tagging ──────────────────────────────────
//
// 502.C adds a tag VOCABULARY, not a filtering capability: the filter
// machinery shipped in 502.A/B and the threading in 502.D. What this
// phase had to get right is that a tag REACHES that machinery.
//
// That is the whole design constraint, and it is why `SetDocumentTags`
// re-indexes rather than merely labelling. Tags are matched as
// `_tag.{tag}` CHUNK metadata; a tag written only to the index record
// would narrow nothing, and a filter that silently matches nothing is
// precisely the defect Phase 502.A was filed to remove. So the pack
// below asserts the stamp on the chunks that actually reach the
// ingestion queue — never on the returned document record, which could
// be right while the chunks were wrong.
//
// The retrieval half (a tag filter narrowing a real pipeline, multi-tag
// AND, strict absence) lives in `RAG/MetadataFilterContract.fs`
// alongside the filter behaviours it extends, so the two halves are
// each tested at the seam they own.

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

type private RecordingIndexLifecycle() =
    interface IIndexLifecycle with
        member _.DeleteChunk _ _ = async.Return IndexLifecycleReport.empty
        member _.DeleteDocument _ _ _ = async.Return IndexLifecycleReport.empty
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
        IndexLifecycle = Some(RecordingIndexLifecycle() :> IIndexLifecycle)
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
        DedupPolicy = KnowledgeDedupPolicy.enabled
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

let private csvOf (rowCount: int) (lastValue: string) : byte[] =
    let rows = [
        for i in 0 .. rowCount - 1 do
            let value = if i = rowCount - 1 then lastValue else sprintf "%04d" i

            sprintf "r%04d,%s" i value
    ]

    ("name,score\n" + String.concat "\n" rows) |> Encoding.UTF8.GetBytes

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

let private drain (queue: IngestionQueue) : DocumentIngestionJob list =
    let jobs = ResizeArray<DocumentIngestionJob>()
    let mutable job = Unchecked.defaultof<DocumentIngestionJob>

    while queue.Reader.TryRead(&job) do
        jobs.Add job

    jobs |> List.ofSeq

/// Every chunk the drained jobs carried — the set that actually reaches
/// embedding and upsert, which is the only place a tag stamp matters.
let private enqueuedChunks (jobs: DocumentIngestionJob list) =
    jobs |> List.collect (fun j -> j.Chunks |> List.map snd)

/// Wait for the queue to receive at least one job. `SetDocumentTags`
/// enqueues synchronously, but an upload's extraction runs off the
/// request path.
let private tagKeysOf (chunk: ToolUp.Platform.VectorKnowledgeTypes.TextChunk) =
    chunk.Metadata
    |> Map.toList
    |> List.map fst
    |> List.filter (fun k -> k.StartsWith "_tag.")
    |> List.sort

let tests =
    testList "Phase 502.C — KB document tagging" [

        testCaseAsync "setting tags re-stamps EVERY chunk with `_tag.{tag}` metadata"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let container = "team-tag-a"
            let deps = mkDeps storage queue KnowledgeVersioningPolicy.disabled container

            let! doc = uploadDocument deps (csvOf 200 "0199") "handbook.csv"
            let! ingested = waitForIngest storage container doc.Id
            let firstJobs = drain queue

            Expect.isNonEmpty
                (enqueuedChunks firstJobs)
                "the upload enqueued chunks, so the comparison below is not vacuous"

            // GP 11 control — before any tag is set, no `_tag.` key exists
            // at all, so an untagged corpus is byte-identical to pre-502.C.
            Expect.isEmpty
                (enqueuedChunks firstJobs |> List.collect tagKeysOf |> List.distinct)
                "an untagged document's chunks carry NO tag metadata"

            let! result =
                setDocumentTags deps {
                    DocId = doc.Id
                    Tags = [ "policy"; "hr" ]
                }

            match result with
            | Error e -> failtest (sprintf "setting tags failed: %s" e)
            | Ok updated ->
                Expect.equal updated.Tags [ "policy"; "hr" ] "the document record carries the normalised tags"

            let retagJobs = drain queue
            let retagged = enqueuedChunks retagJobs

            Expect.hasLength
                retagged
                ingested.ChunkCount
                "EVERY chunk position is re-enqueued — the Phase 510 content diff is deliberately bypassed, because a tag change alters no content and the diff would correctly skip everything"

            Expect.all
                retagged
                (fun c -> tagKeysOf c = [ "_tag.hr"; "_tag.policy" ])
                "every re-enqueued chunk carries both tag keys — a partial stamp would leave part of the document unreachable by its own tag"

            Expect.all
                retagged
                (fun c -> c.Metadata.TryFind "_tag.policy" = Some "true")
                "and the stamped value is the one the filter compares against"

            // The persisted record agrees with what was stamped.
            let! index = loadIndex storage container

            Expect.equal
                (index |> List.head |> _.Tags)
                [ "policy"; "hr" ]
                "the index entry is updated in the same operation, so a reload shows the same tags the chunks carry"
        }

        testCaseAsync "tags are normalised — casing, whitespace, duplicates and ordering"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let container = "team-tag-b"
            let deps = mkDeps storage queue KnowledgeVersioningPolicy.disabled container

            let! doc = uploadDocument deps (csvOf 40 "0039") "n.csv"
            let! _ = waitForIngest storage container doc.Id
            drain queue |> ignore

            let! result =
                setDocumentTags deps {
                    DocId = doc.Id
                    Tags = [ "  Policy  "; "POLICY"; "Human Resources"; ""; "   " ]
                }

            match result with
            | Error e -> failtest (sprintf "setting tags failed: %s" e)
            | Ok updated ->
                Expect.equal
                    updated.Tags
                    [ "policy"; "human-resources" ]
                    "trimmed, lower-cased, inner whitespace collapsed to '-', duplicates dropped, empties dropped, input order preserved"

            // Normalisation is not cosmetic: the stamp is written from the
            // normalised form, so a filter built from raw input has to
            // land on the same key or it matches nothing.
            let stamped = drain queue |> enqueuedChunks

            Expect.all
                stamped
                (fun c -> tagKeysOf c = [ "_tag.human-resources"; "_tag.policy" ])
                "the chunk keys are the normalised ones — which is what makes a raw-input filter still hit"
        }

        testCaseAsync "setting the SAME tags is idempotent and re-indexes nothing"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let container = "team-tag-c"
            let deps = mkDeps storage queue KnowledgeVersioningPolicy.disabled container

            let! doc = uploadDocument deps (csvOf 100 "0099") "n.csv"
            let! _ = waitForIngest storage container doc.Id
            drain queue |> ignore

            let! first = setDocumentTags deps { DocId = doc.Id; Tags = [ "policy" ] }
            Expect.isOk first "the first set succeeds"
            Expect.isNonEmpty (drain queue) "and re-indexes, because the tags genuinely changed"

            // Same tags, different casing and order of arrival — the
            // comparison is on the NORMALISED form, so this is a no-op.
            let! second =
                setDocumentTags deps {
                    DocId = doc.Id
                    Tags = [ "  POLICY " ]
                }

            Expect.isOk second "the repeat set succeeds"

            Expect.isEmpty
                (drain queue)
                "nothing is re-enqueued — a no-op write must not cost a full document re-index, and normalising first is what makes the equality check see it as a no-op"
        }

        testCaseAsync "clearing tags removes the stamp from the chunks"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let container = "team-tag-d"
            let deps = mkDeps storage queue KnowledgeVersioningPolicy.disabled container

            let! doc = uploadDocument deps (csvOf 60 "0059") "n.csv"
            let! _ = waitForIngest storage container doc.Id
            drain queue |> ignore

            let! _ = setDocumentTags deps { DocId = doc.Id; Tags = [ "policy" ] }
            let tagged = drain queue |> enqueuedChunks

            Expect.all
                tagged
                (fun c -> tagKeysOf c = [ "_tag.policy" ])
                "tagged first, so the clear below has something to remove"

            let! cleared = setDocumentTags deps { DocId = doc.Id; Tags = [] }

            match cleared with
            | Error e -> failtest (sprintf "clearing tags failed: %s" e)
            | Ok updated -> Expect.isEmpty updated.Tags "the record's tags are cleared"

            let recleared = drain queue |> enqueuedChunks
            Expect.isNonEmpty recleared "clearing re-indexes too — the stale stamp has to be overwritten"

            Expect.isEmpty
                (recleared |> List.collect tagKeysOf |> List.distinct)
                "the re-stamped chunks carry no tag keys, so the document stops matching its old tag rather than merely looking untagged in the list"
        }

        testCaseAsync "tagging a module-generated narrative is REFUSED, not silently partial"
        <| async {
            // A narrative's chunks are produced by the owning module, so
            // nothing on this path could re-stamp them. Returning `Ok`
            // and writing the record would recreate exactly the silent
            // no-op 502.A was filed to remove: a tag the UI shows and no
            // filter can ever match.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let container = "team-tag-e"
            let deps = mkDeps storage queue KnowledgeVersioningPolicy.disabled container

            let narrativeDoc: KnowledgeDocument = {
                Id = "doc-narrative"
                FileName = "forecast.md"
                FileType = "narrative"
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = "user-1"
                Status = Complete 2
                SizeBytes = 100L
                ChunkCount = 2
                Source =
                    FromNarrative {
                        ModuleId = "forecasts"
                        PageRoute = Some "/forecasts"
                        SettingsKey = "k"
                        SettingsDisplay = [ "Horizon", "12m" ]
                        GeneratedAt = DateTimeOffset.UtcNow
                    }
                ContentHash = None
                Version = 1
                Tags = []
            }

            do! saveIndex storage container [ narrativeDoc ]

            let! result =
                setDocumentTags deps {
                    DocId = "doc-narrative"
                    Tags = [ "policy" ]
                }

            match result with
            | Ok _ -> failtest "tagging a narrative document must be refused, not reported as success"
            | Error reason ->
                Expect.stringContains
                    (reason.ToLowerInvariant())
                    "narrative"
                    "the refusal names what was refused and why, rather than failing opaquely"

            let! index = loadIndex storage container

            Expect.isEmpty
                (index |> List.head |> _.Tags)
                "and nothing was written — a refusal leaves no half-applied tag on the record"
        }

        testCaseAsync "a re-uploaded new version KEEPS the lineage's tags, and its chunks are stamped"
        <| async {
            // The lineage is the same document. Dropping its tags on a
            // re-upload would silently un-scope every query that had been
            // narrowing to it, on nothing more than an edit.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let container = "team-tag-f"
            let deps = mkDeps storage queue KnowledgeVersioningPolicy.enabled container

            let! first = uploadDocument deps (csvOf 100 "0099") "revised.csv"
            let! _ = waitForIngest storage container first.Id
            drain queue |> ignore

            let! _ =
                setDocumentTags deps {
                    DocId = first.Id
                    Tags = [ "policy" ]
                }

            drain queue |> ignore

            // A byte-different re-upload under the same name supersedes.
            let! second = uploadDocument deps (csvOf 100 "9999") "revised.csv"
            let! _ = waitForIngest storage container second.Id
            let versionJobs = drain queue

            Expect.equal second.Id first.Id "the re-upload superseded in place"
            Expect.equal second.Version 2 "the lineage advanced"
            Expect.equal second.Tags [ "policy" ] "and carried its tags forward"

            let reindexed = enqueuedChunks versionJobs
            Expect.isNonEmpty reindexed "the changed content was re-embedded"

            Expect.all
                reindexed
                (fun c -> tagKeysOf c = [ "_tag.policy" ])
                "the new version's chunks are stamped at ingestion — the document stays filterable without a manual re-tag"
        }

        testCaseAsync "a pre-502.C index record (no Tags property) loads as [] and not null"
        <| async {
            // A missing JSON list deserialises to `null`, and F# `[]` is
            // NOT null — so without the store's coercion every
            // `doc.Tags |> List.map …` on a legacy record would throw, on
            // read, in a document list that had worked for months. The
            // legacy JSON here is written by hand rather than derived, so
            // it is genuinely the pre-502.C shape.
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let legacyJson =
                """[{"Id":"doc-legacy","FileName":"old.pdf","FileType":"pdf","UploadedAt":"2026-01-01T00:00:00+00:00","UploadedBy":"user-1","Status":{"Complete":3},"SizeBytes":10,"ChunkCount":3,"Source":"UploadedFile","Version":2}]"""

            let! _ = storage.Upload("team-tag-g", "knowledge/index.json", Encoding.UTF8.GetBytes legacyJson)

            let! index = loadIndex storage "team-tag-g"

            match index with
            | [ doc ] ->
                Expect.equal doc.Id "doc-legacy" "the legacy record still loads — the widening did not blank the index"
                Expect.isFalse (isNull (box doc.Tags)) "Tags is not null"
                Expect.isEmpty doc.Tags "Tags reads as the empty list"

                // The operation that would actually have thrown.
                Expect.isEmpty
                    (KnowledgeTags.metadataPairs doc.Tags)
                    "and a tag stamp derived from it is empty rather than an NRE"

                Expect.equal doc.Version 2 "the Phase 510 coercion is unaffected by the new one"
            | other -> failtest $"expected the single legacy document, got %A{other}"
        }
    ]