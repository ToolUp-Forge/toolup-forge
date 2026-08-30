module KnowledgeBase.ServerApiNotes

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerNotes
open KnowledgeBase.ServerApiDeps

let addNote (deps: KnowledgeApiDeps) (req: AddNoteRequest) : Async<Result<KnowledgeDocument, string>> = async {
    let title = req.Title.Trim()
    let body = req.Body

    if title = "" then
        return Error "Note title is required."
    else
        let paragraphs = chunkNoteBody body

        if List.isEmpty paragraphs then
            return Error "Note body is required."
        else
            let docId = Guid.NewGuid().ToString()
            let safeTitle = sanitiseTitle title
            let fileName = sprintf "%s.md" safeTitle

            let chunks =
                paragraphs
                |> List.mapi (fun i p ->
                    let chunk, _ = buildNoteChunk docId fileName title paragraphs.Length i p
                    chunk)

            let noteSource: NoteSource = {
                Title = title
                Author = deps.UserId
                CreatedAt = DateTimeOffset.UtcNow
                LastEditedAt = None
            }

            let sizeBytes = int64 (Encoding.UTF8.GetByteCount body)

            let doc: KnowledgeDocument = {
                Id = docId
                FileName = fileName
                FileType = "note"
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = deps.UserId
                Status = Queued
                SizeBytes = sizeBytes
                ChunkCount = chunks.Length
                Source = Note noteSource
                // Phase 14x — dedup applies to uploaded files only; a
                // note whose body happens to match an upload must not
                // hijack it.
                ContentHash = None
                // Phase 510 — notes are not versioned by the upload
                // lineage: `UpdateNote` already edits in place, so a note
                // is permanently version 1 of itself.
                Version = 1
                // Phase 502.C — a fresh note starts untagged; tags are
                // applied afterwards through `SetDocumentTags`, which
                // re-chunks the note body and stamps them.
                Tags = []
            }

            // Persist raw body so the note round-trips even if the
            // vector store is rebuilt — same shape as narrative-commit.
            let rawBlobName = sprintf "knowledge/%s/note.md" docId
            let! _ = deps.Storage.Upload(deps.Scope.Container, rawBlobName, Encoding.UTF8.GetBytes body)

            // Phase 116 — atomic index RMW (see `upsertIndexEntry`).
            // Released before the enqueue block below, which can route
            // through `MarkIngestionFailed` → `updateIndexStatus` and
            // re-acquire the same container lock.
            do! upsertIndexEntry deps.Storage deps.Scope.Container doc

            let mutable returnedDoc = doc

            if box deps.Queue <> null && not chunks.IsEmpty then
                let initialStatus = Embedding(0, chunks.Length)

                statusCache.AddOrUpdate(
                    docId,
                    initialStatus,
                    fun _ existing ->
                        match existing with
                        | Queued -> initialStatus
                        | other -> other
                )
                |> ignore

                let chunkPairs =
                    chunks |> List.mapi (fun i chunk -> sprintf "%s:chunk:%d" docId i, chunk)

                let job: DocumentIngestionJob = {
                    DocumentId = docId
                    DocumentName = fileName
                    Chunks = chunkPairs
                    Scope = deps.VectorScope
                    ScopeId = deps.Scope.ScopeId
                    Container = deps.Scope.Container
                    OriginatingUserId = Some deps.UserId
                }

                // Phase 723 — async enqueue: the sync form is a blocking
                // store round-trip on the request thread once a
                // deployment composes a durable queue.
                let! accepted = deps.Queue.EnqueueAsync(job)
                deps.RecordEnqueue accepted

                if not accepted then
                    let reason =
                        sprintf
                            "Knowledge-base ingestion queue is full (%d/%d). Try again in a few seconds."
                            deps.Queue.Count
                            deps.Queue.Capacity

                    do! deps.MarkIngestionFailed docId fileName reason

                    returnedDoc <- {
                        doc with
                            Status = IngestionStatus.Failed reason
                    }

            do! deps.Notifications.Publish(deps.UserId, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
            do! deps.PublishInventory()

            return Ok returnedDoc
}

let updateNote (deps: KnowledgeApiDeps) (req: UpdateNoteRequest) : Async<Result<KnowledgeDocument, string>> = async {
    let title = req.Title.Trim()
    let body = req.Body

    if title = "" then
        return Error "Note title is required."
    else
        let paragraphs = chunkNoteBody body

        if List.isEmpty paragraphs then
            return Error "Note body is required."
        else
            let! existing = loadIndex deps.Storage deps.Scope.Container

            match existing |> List.tryFind (fun d -> d.Id = req.DocId) with
            | None -> return Error "Note not found in this scope."
            | Some prior ->
                match prior.Source with
                | Note priorNote ->
                    // Phase 115 — drop the old chunks through the unified
                    // lifecycle seam so the sparse index sheds them too
                    // (pre-115 the `vs.DeleteChunk` loop left the prior
                    // note text retrievable through the hybrid sparse
                    // leg). Best-effort here: the re-ingestion below
                    // overwrites the same chunk ids, so survivors are
                    // logged by the seam rather than failing the update.
                    match deps.IndexLifecycle with
                    | Some lifecycle ->
                        let! _ = lifecycle.DeleteDocument deps.VectorScope req.DocId prior.ChunkCount
                        ()
                    | None -> ()

                    let safeTitle = sanitiseTitle title
                    let fileName = sprintf "%s.md" safeTitle

                    let chunks =
                        paragraphs
                        |> List.mapi (fun i p ->
                            let chunk, _ = buildNoteChunk req.DocId fileName title paragraphs.Length i p

                            chunk)

                    let updatedNote: NoteSource = {
                        priorNote with
                            Title = title
                            LastEditedAt = Some DateTimeOffset.UtcNow
                    }

                    let sizeBytes = int64 (Encoding.UTF8.GetByteCount body)

                    let updatedDoc: KnowledgeDocument = {
                        prior with
                            FileName = fileName
                            Status = Queued
                            SizeBytes = sizeBytes
                            ChunkCount = chunks.Length
                            Source = Note updatedNote
                    }

                    let rawBlobName = sprintf "knowledge/%s/note.md" req.DocId
                    let! _ = deps.Storage.Upload(deps.Scope.Container, rawBlobName, Encoding.UTF8.GetBytes body)

                    // Phase 116 — atomic index RMW (see `upsertIndexEntry`).
                    // `updatedDoc.Id = req.DocId`, so the helper's
                    // filter-by-id replaces the prior entry in place. The
                    // existence check above still reads outside the lock —
                    // that only governs last-writer-wins on *this* doc, not
                    // the cross-document loss the lock prevents.
                    do! upsertIndexEntry deps.Storage deps.Scope.Container updatedDoc

                    progressCache.TryRemove(req.DocId) |> ignore

                    let mutable returnedDoc = updatedDoc

                    if box deps.Queue <> null && not chunks.IsEmpty then
                        let initialStatus = Embedding(0, chunks.Length)

                        statusCache.AddOrUpdate(req.DocId, initialStatus, fun _ _ -> initialStatus)
                        |> ignore

                        let chunkPairs =
                            chunks |> List.mapi (fun i chunk -> sprintf "%s:chunk:%d" req.DocId i, chunk)

                        let job: DocumentIngestionJob = {
                            DocumentId = req.DocId
                            DocumentName = fileName
                            Chunks = chunkPairs
                            Scope = deps.VectorScope
                            ScopeId = deps.Scope.ScopeId
                            Container = deps.Scope.Container
                            OriginatingUserId = Some deps.UserId
                        }

                        // Phase 723 — async enqueue; see `addNote`.
                        let! accepted = deps.Queue.EnqueueAsync(job)
                        deps.RecordEnqueue accepted

                        if not accepted then
                            let reason =
                                sprintf
                                    "Knowledge-base ingestion queue is full (%d/%d). Try again in a few seconds."
                                    deps.Queue.Count
                                    deps.Queue.Capacity

                            do! deps.MarkIngestionFailed req.DocId fileName reason

                            returnedDoc <- {
                                updatedDoc with
                                    Status = IngestionStatus.Failed reason
                            }

                    do! deps.Notifications.Publish(deps.UserId, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
                    do! deps.PublishInventory()

                    return Ok returnedDoc
                | _ -> return Error "Document is not a note."
}