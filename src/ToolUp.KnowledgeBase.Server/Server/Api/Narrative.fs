module KnowledgeBase.ServerApiNarrative

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Narrative
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerExtractors
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps

let ingestNarrative
    (deps: KnowledgeApiDeps)
    (request: IngestNarrativeRequest)
    : Async<Result<KnowledgeDocument, IngestNarrativeError>> =
    async {
        match request.Document.Provenance with
        | None -> return Error MissingProvenance
        | Some prov ->
            let! existing = loadIndex deps.Storage deps.Scope.Container

            let duplicate =
                existing
                |> List.tryFind (fun d ->
                    match d.Source with
                    | FromNarrative n -> n.ModuleId = prov.ModuleId && n.SettingsKey = prov.SettingsKey
                    | UploadedFile
                    | Note _ -> false)

            match duplicate, request.Overwrite with
            | Some existingDoc, false -> return Error(DuplicateExists existingDoc)
            | _ ->
                // Use existing docId on overwrite so vector-store upserts replace by
                // chunkId; mint a new id otherwise.
                let docId =
                    match duplicate with
                    | Some d -> d.Id
                    | None -> Guid.NewGuid().ToString()

                let fileName =
                    let safeTitle =
                        request.Document.Title
                        |> String.map (fun c ->
                            if Char.IsLetterOrDigit c || c = '-' || c = ' ' then
                                c
                            else
                                '-')

                    sprintf "%s.narrative.md" safeTitle

                // One chunk per section so retrieval can cite the section
                // that contributed — matches the per-page chunking we use
                // for uploaded documents.
                let chunks =
                    request.Document.Sections
                    |> List.mapi (fun i section ->
                        let sectionDoc: NarrativeDocument = {
                            Title = request.Document.Title
                            Subtitle = request.Document.Subtitle
                            Sections = [ section ]
                            Provenance = request.Document.Provenance
                        }

                        let body = NarrativeMarkdown.render sectionDoc

                        let src: SourceReference = {
                            DocumentId = docId
                            DocumentName = fileName
                            FileType = "narrative"
                            Location = Section section.Heading
                            IndexedAt = DateTimeOffset.UtcNow
                        }

                        let header = sprintf "Section %d · %s" (i + 1) section.Heading
                        makeChunk Narrative fileName header body src)

                let fullMarkdown = NarrativeMarkdown.render request.Document
                let sizeBytes = int64 (Encoding.UTF8.GetByteCount fullMarkdown)

                let doc: KnowledgeDocument = {
                    Id = docId
                    FileName = fileName
                    FileType = "narrative"
                    UploadedAt = DateTimeOffset.UtcNow
                    UploadedBy = deps.UserId
                    Status = Queued
                    SizeBytes = sizeBytes
                    ChunkCount = chunks.Length
                    Source =
                        FromNarrative {
                            ModuleId = prov.ModuleId
                            PageRoute = prov.PageRoute
                            SettingsKey = prov.SettingsKey
                            SettingsDisplay = prov.SettingsDisplay
                            GeneratedAt = prov.GeneratedAt
                        }
                }

                // Note: on overwrite with fewer sections than the previous
                // ingestion, trailing chunks (indices chunks.Length..oldN-1)
                // orphan in the vector store. The re-ingested chunks 0..N-1
                // replace the old ones via `IVectorStore.Upsert` idempotency
                // on (scope, chunkId), so stale *content* never leaks — only
                // excess count. Matches the existing `DeleteDocument` path
                // which also leaves orphan chunks. A future vector-chunk
                // cleanup pass will address both.

                // Persist rendered markdown so the document can be re-read
                // even if the embedding/store is rebuilt.
                let rawBlobName = sprintf "knowledge/%s/%s" docId fileName
                let! _ = deps.Storage.Upload(deps.Scope.Container, rawBlobName, Encoding.UTF8.GetBytes fullMarkdown)

                let updated =
                    existing |> List.filter (fun d -> d.Id <> docId) |> List.append [ doc ]

                do! saveIndex deps.Storage deps.Scope.Container updated

                let mutable returnedDoc = doc

                if box deps.Queue <> null && not chunks.IsEmpty then
                    // Seed before enqueue so an early observer firing isn't
                    // overwritten. Same pattern as the upload path above.
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

                    let accepted = deps.Queue.Enqueue(job)
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
                elif chunks.IsEmpty then
                    statusCache.AddOrUpdate(docId, Complete 0, fun _ _ -> Complete 0) |> ignore

                // Nudge the KB client to reload its document list.
                // The notification scope is the user id (matches the
                // SSE subscription filter); the payload's `scopeId`
                // carries the storage scope for downstream consumers
                // that need it.
                do! deps.Notifications.Publish(deps.UserId, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
                do! deps.PublishInventory()

                return Ok returnedDoc
    }

let resetIndex (deps: KnowledgeApiDeps) : Async<Result<unit, string>> = async {
    // Wipe every blob under `knowledge/` (index + raw uploads +
    // rendered narratives) and drop the in-memory status/progress
    // entries for the docs that lived in this scope. The
    // container lock blocks concurrent observer writes during
    // the read-then-delete window so a chunk completing mid-reset
    // can't resurrect the index after we've cleared it.
    //
    // Vector chunks are also dropped so RAG stops surfacing the
    // wiped documents. Chunks for each doc are deleted by id
    // (`{docId}:chunk:{i}`) up to its `ChunkCount` — same pattern
    // `deleteDocument` uses. Note the narrative-overwrite path in
    // `submitNarrative` still leaves trailing-chunk orphans on a
    // shrink (its existing comment); that one's a separate fix
    // because we don't have the prior chunk count there without
    // re-reading the index inside the lock.
    //
    // No server-side role gate — `ITeamStore` is in the SDK's
    // server-only compile context and isn't visible to module
    // projects. Destructive intent is gated client-side via a
    // confirm dialog. Phase 4's `IPermissionStore` pathway is
    // the right home for a future hard gate; until then, a team
    // member can reset their own team's index, matching today's
    // unrestricted `DeleteDocument` semantics.
    let lock = acquireContainerLock deps.Scope.Container
    do! lock.WaitAsync() |> Async.AwaitTask

    try
        let! priorDocs = async {
            try
                let! existing = loadIndex deps.Storage deps.Scope.Container
                return existing
            with _ ->
                return []
        }

        try
            let! blobs = deps.Storage.List(deps.Scope.Container, "knowledge/")

            for blobName in blobs do
                let! _ = deps.Storage.Delete(deps.Scope.Container, blobName)
                ()
        with ex ->
            deps.Logger.Warn(
                sprintf
                    "[KnowledgeBase] ResetIndex: blob list failed for %s; falling back to index-only delete (%s)"
                    deps.Scope.Container
                    ex.Message
            )

            let! _ = deps.Storage.Delete(deps.Scope.Container, indexBlobName)
            ()

        // Drop each prior doc's vector chunks so RAG retrieval
        // doesn't keep surfacing them. Falls through if no vector
        // store is wired (tests).
        match deps.VectorStore with
        | Some vs ->
            for doc in priorDocs do
                for i in 0 .. doc.ChunkCount - 1 do
                    do! vs.DeleteChunk deps.VectorScope (sprintf "%s:chunk:%d" doc.Id i)
        | None -> ()

        for doc in priorDocs do
            statusCache.TryRemove(doc.Id) |> ignore
            progressCache.TryRemove(doc.Id) |> ignore

        // Cross-store coherence: wipe persisted narrative-store entries
        // for this scope alongside KB blobs and vector chunks. Without
        // this the user resets their KB but `list_narratives` continues
        // to return prior entries (the persistent narrative store is a
        // separate persistence layer from KB's own index — Phase 11.C.2
        // closure work added per-scope persistence to INarrativeStore;
        // ResetIndex needs to wipe both views to stay coherent).
        // No-op when no INarrativeStore is registered (test harnesses
        // without the SDK's standard compose).
        match deps.NarrativeStore with
        | Some narrativeStore ->
            try
                let! deletedNarratives = narrativeStore.DeleteScope deps.Scope.ScopeId

                if deletedNarratives > 0 then
                    deps.Logger.Info(
                        sprintf
                            "[KnowledgeBase] ResetIndex: deleted %d persisted narrative entries for scope %s"
                            deletedNarratives
                            deps.Scope.ScopeId
                    )
            with ex ->
                deps.Logger.Warn(
                    sprintf
                        "[KnowledgeBase] ResetIndex: INarrativeStore.DeleteScope failed for %s — leaving narrative entries in place (%s)"
                        deps.Scope.ScopeId
                        ex.Message
                )
        | None -> ()
    finally
        lock.Release() |> ignore

    KnowledgeBase.ServerInventory.invalidateInventoryCache deps.Scope.Container
    do! deps.Notifications.Publish(deps.UserId, DataRefreshed("KnowledgeBase", deps.Scope.ScopeId))
    do! deps.PublishInventory()
    return Ok()
}