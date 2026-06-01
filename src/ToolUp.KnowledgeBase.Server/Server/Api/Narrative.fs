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

                // Vector-chunk cleanup on overwrite. When the regenerated
                // narrative has fewer sections than the prior version,
                // trailing chunks (indices chunks.Length..oldN-1) would
                // orphan in the vector store — the re-ingestion's
                // Upsert paths cover 0..N-1 but leave the tail untouched,
                // so RAG retrieval keeps surfacing stale section content
                // under the live document id. Delete the tail now while
                // we still know the old chunk count from the prior
                // KnowledgeDocument; falls through silently when no
                // vector store is wired (test harnesses) or when the
                // new section count meets or exceeds the old.
                //
                // 0.4.3 — per-chunk DeleteChunk failures are caught,
                // counted, and surfaced as a single summary log line.
                // Previously they were swallowed silently; a partial
                // failure left orphans in the store and RAG kept
                // surfacing them under the live document id with no
                // operator signal.
                match duplicate, deps.VectorStore with
                | Some existingDoc, Some vs when chunks.Length < existingDoc.ChunkCount ->
                    let mutable orphanIds: string list = []

                    for i in chunks.Length .. existingDoc.ChunkCount - 1 do
                        let chunkId = sprintf "%s:chunk:%d" docId i

                        try
                            do! vs.DeleteChunk deps.VectorScope chunkId
                        with ex ->
                            orphanIds <- chunkId :: orphanIds

                            deps.Logger.Warn(
                                sprintf
                                    "[KnowledgeBase] Orphan-chunk delete failed for %s (docId=%s scope=%s): %s"
                                    chunkId
                                    docId
                                    deps.Scope.ScopeId
                                    ex.Message
                            )

                    if not (List.isEmpty orphanIds) then
                        deps.Logger.Warn(
                            sprintf
                                "[KnowledgeBase] %d orphan chunk(s) remain in vector store for docId=%s after narrative overwrite; RAG may surface stale section content. Surviving chunk ids: %s"
                                orphanIds.Length
                                docId
                                (orphanIds |> List.rev |> String.concat "; ")
                        )
                | _ -> ()

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
    // `deleteDocument` uses. The narrative-overwrite path in
    // `ingestNarrative` does its own per-write trailing-chunk
    // cleanup (using the prior KnowledgeDocument's ChunkCount
    // captured before re-ingestion), so by the time we reach this
    // sweep there are no orphan tails left to chase.
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
        //
        // 0.4.3 — per-chunk failures caught, counted, and surfaced
        // as a single summary log line per ResetIndex call. Without
        // this, a partial-failure reset left RAG quoting wiped docs
        // with no operator signal.
        match deps.VectorStore with
        | Some vs ->
            let mutable orphanIds: string list = []

            for doc in priorDocs do
                for i in 0 .. doc.ChunkCount - 1 do
                    let chunkId = sprintf "%s:chunk:%d" doc.Id i

                    try
                        do! vs.DeleteChunk deps.VectorScope chunkId
                    with ex ->
                        orphanIds <- chunkId :: orphanIds

                        deps.Logger.Warn(
                            sprintf
                                "[KnowledgeBase] ResetIndex chunk delete failed for %s (scope=%s): %s"
                                chunkId
                                deps.Scope.ScopeId
                                ex.Message
                        )

            if not (List.isEmpty orphanIds) then
                deps.Logger.Warn(
                    sprintf
                        "[KnowledgeBase] ResetIndex left %d orphan vector chunk(s) in scope %s; RAG may continue surfacing wiped documents. Surviving chunk ids: %s"
                        orphanIds.Length
                        deps.Scope.ScopeId
                        (orphanIds |> List.rev |> String.concat "; ")
                )
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