module KnowledgeBase.ServerApiNotes

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerIngestionObserver
open KnowledgeBase.ServerNotes
open KnowledgeBase.ServerApiDeps
open ToolUp.Platform.Compose

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
                Author = deps.UserId.Value
                CreatedAt = DateTimeOffset.UtcNow
                LastEditedAt = None
            }

            let sizeBytes = int64 (Encoding.UTF8.GetByteCount body)

            let doc: KnowledgeDocument = {
                Id = docId
                FileName = fileName
                FileType = "note"
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = deps.UserId.Value
                Status = Queued
                SizeBytes = sizeBytes
                ChunkCount = chunks.Length
                Source = Note noteSource
                // Dedup applies to uploaded files only; a
                // note whose body happens to match an upload must not
                // hijack it.
                ContentHash = None
                // Notes are not versioned by the upload
                // lineage: `UpdateNote` already edits in place, so a note
                // is permanently version 1 of itself.
                Version = 1
                // A fresh note starts untagged; tags are
                // applied afterwards through `SetDocumentTags`, which
                // re-chunks the note body and stamps them.
                Tags = []
            }

            // Persist raw body so the note round-trips even if the
            // vector store is rebuilt — same shape as narrative-commit. Phase
            // 863 — a refused write fails the add before the index entry.
            let rawBlobName = sprintf "knowledge/%s/note.md" docId

            match! deps.Storage.Upload(deps.Scope.Container, rawBlobName, Encoding.UTF8.GetBytes body) with
            | Error storageError -> return Error(sprintf "The note could not be saved: %s" storageError)
            | Ok _ ->
                // Atomic index RMW (see `upsertIndexEntry`), released before
                // the enqueue, which takes the same container lock.
                do! upsertIndexEntry deps.Storage deps.Scope.Container doc

                let chunkPairs =
                    chunks
                    |> List.mapi (fun i chunk -> ChunkId.render { DocumentId = docId; Ordinal = i }, chunk)

                let! enqueued = KnowledgeIngest.enqueue deps doc chunkPairs

                let returnedDoc =
                    match enqueued with
                    | Ok _ -> doc
                    | Error refused -> {
                        doc with
                            Status = IngestionStatus.Failed(EnqueueRefused.describe refused)
                      }

                do! deps.Notifications.Publish(deps.UserId.Value, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
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

            match existing |> Result.map (List.tryFind (fun d -> d.Id = req.DocId)) with
            | Error error -> return Error(IndexReadError.describe error)
            | Ok None -> return Error "Note not found in this scope."
            | Ok(Some prior) ->
                match prior.Source with
                | Note priorNote ->
                    // Persist the raw body FIRST, ahead of the chunk drop,
                    // the index entry and the enqueue, so a refused write fails the
                    // update with nothing else moved.
                    let rawBlobName = sprintf "knowledge/%s/note.md" req.DocId

                    match! deps.Storage.Upload(deps.Scope.Container, rawBlobName, Encoding.UTF8.GetBytes body) with
                    | Error storageError -> return Error(sprintf "The note could not be saved: %s" storageError)
                    | Ok _ ->
                        // Drop the old chunks through the unified
                        // lifecycle seam so the sparse index sheds them too
                        // (pre-115 the `vs.DeleteChunk` loop left the prior
                        // note text retrievable through the hybrid sparse
                        // leg). Best-effort here: the re-ingestion below
                        // overwrites the same chunk ids, so survivors are
                        // logged rather than failing the update.
                        match deps.IndexLifecycle with
                        | Some lifecycle ->
                            let! report = lifecycle.DeleteDocument deps.VectorScope req.DocId prior.ChunkCount

                            if not (ToolUp.Platform.Infra.IIndexLifecycle.IndexLifecycleReport.isClean report) then
                                deps.Logger.Warn(
                                    sprintf
                                        "[KnowledgeBase] updateNote chunk delete completed partially for docId=%s (scope=%s): %s"
                                        req.DocId
                                        deps.Scope.ScopeId
                                        (ToolUp.Platform.Infra.IIndexLifecycle.IndexLifecycleReport.summarise report)
                                )
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

                        // Atomic index RMW (see `upsertIndexEntry`).
                        // `updatedDoc.Id = req.DocId`, so the helper's
                        // filter-by-id replaces the prior entry in place. The
                        // existence check above still reads outside the lock —
                        // that only governs last-writer-wins on *this* doc, not
                        // the cross-document loss the lock prevents.
                        do! upsertIndexEntry deps.Storage deps.Scope.Container updatedDoc

                        let chunkPairs =
                            chunks
                            |> List.mapi (fun i chunk -> ChunkId.render { DocumentId = req.DocId; Ordinal = i }, chunk)

                        // A re-save is a new attempt: it re-seeds the persisted
                        // status and retires the previous save's late callbacks.
                        let! enqueued = KnowledgeIngest.enqueue deps updatedDoc chunkPairs

                        let returnedDoc =
                            match enqueued with
                            | Ok _ -> updatedDoc
                            | Error refused -> {
                                updatedDoc with
                                    Status = IngestionStatus.Failed(EnqueueRefused.describe refused)
                              }

                        do!
                            deps.Notifications.Publish(
                                deps.UserId.Value,
                                DataRefreshed("KnowledgeBase", deps.Scope.ScopeId)
                            )

                        do! deps.PublishInventory()

                        return Ok returnedDoc
                | _ -> return Error "Document is not a note."
}