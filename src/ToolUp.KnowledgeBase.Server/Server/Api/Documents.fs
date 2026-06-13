module KnowledgeBase.ServerApiDocuments

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerExtractors
open KnowledgeBase.ServerExtractionErrors
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps

let uploadDocument (deps: KnowledgeApiDeps) (bytes: byte[]) (fileName: string) : Async<KnowledgeDocument> = async {
    let docId = Guid.NewGuid().ToString()
    let ext = Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.')

    // Persist the raw blob synchronously so a refresh during extraction
    // still finds the source file. ChunkCount stays 0 until extraction
    // completes — the observer reads it from the index, so the chunk
    // total has to land before the first chunk callback fires (the
    // background async below stamps it before enqueue).
    let doc: KnowledgeDocument = {
        Id = docId
        FileName = fileName
        FileType = ext
        UploadedAt = DateTimeOffset.UtcNow
        UploadedBy = deps.UserId
        Status = Queued
        SizeBytes = int64 bytes.Length
        ChunkCount = 0
        Source = UploadedFile
    }

    let rawBlobName = sprintf "knowledge/%s/%s" docId fileName
    let! _ = deps.Storage.Upload(deps.Scope.Container, rawBlobName, bytes)

    let! existing = loadIndex deps.Storage deps.Scope.Container

    let updated =
        existing |> List.filter (fun d -> d.Id <> docId) |> List.append [ doc ]

    do! saveIndex deps.Storage deps.Scope.Container updated

    // Seed initial cache state. The background extractor flips this to
    // ExtractingText as soon as it starts running.
    statusCache.AddOrUpdate(docId, Queued, fun _ _ -> Queued) |> ignore

    // Spawn extraction off the request path. UploadDocument returns
    // within the time it takes to persist the raw blob (~hundreds of
    // ms), not the time it takes to OCR a PDF (~10s). Status flow
    // mirrors the synchronous predecessor:
    //   Queued → ExtractingText → Embedding(0, n) → Complete(n) | Failed reason
    // Errors are caught and routed through `deps.MarkIngestionFailed`
    // so the user sees a Failed badge rather than a doc stuck in
    // ExtractingText. Async.Start swallows otherwise-unobserved
    // exceptions, so the try/with is non-negotiable.
    let extractAndEnqueue = async {
        try
            let extractingStatus = ExtractingText

            statusCache.AddOrUpdate(docId, extractingStatus, fun _ _ -> extractingStatus)
            |> ignore

            do! updateIndexStatus deps.Storage deps.Scope.Container docId extractingStatus

            let! extracted = extractChunks deps.OcrProvider deps.TableExtractor docId fileName bytes

            // Phase 103 — stamp the structured original-document ref
            // (+ Phase 106 neutral locator) into each chunk so retrieval
            // surfaces `RetrievedSource.OriginalRef` without rebuilding
            // the blob-name convention.
            let chunks = stampOriginalRefs docId fileName ext (int64 bytes.Length) extracted

            if box deps.Queue <> null && not chunks.IsEmpty then
                // Stamp ChunkCount BEFORE enqueue: the observer reads
                // it from the index to compute progress, and the first
                // chunk callback can fire before this method returns.
                do! updateIndexChunkCount deps.Storage deps.Scope.Container docId chunks.Length

                let initialStatus = Embedding(0, chunks.Length)

                // Seed the cache; the observer's `AddOrUpdate` won't
                // overwrite a fresher value (e.g. one already advanced
                // to Embedding(1, n) by a racing callback).
                statusCache.AddOrUpdate(
                    docId,
                    initialStatus,
                    fun _ existing ->
                        match existing with
                        | Queued
                        | ExtractingText -> initialStatus
                        | other -> other
                )
                |> ignore

                do! updateIndexStatus deps.Storage deps.Scope.Container docId initialStatus

                let chunkPairs =
                    chunks |> List.mapi (fun i (chunk, _) -> sprintf "%s:chunk:%d" docId i, chunk)

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
            elif chunks.IsEmpty then
                let terminal = Complete 0
                statusCache.AddOrUpdate(docId, terminal, fun _ _ -> terminal) |> ignore
                do! updateIndexStatus deps.Storage deps.Scope.Container docId terminal

            do! deps.PublishInventory()
        with ex ->
            deps.Logger.Error(sprintf "[KnowledgeBase] Extraction failed for %s/%s" docId fileName, Some ex)

            let reason = classify fileName ex
            do! deps.MarkIngestionFailed docId fileName reason
            do! deps.PublishInventory()
    }

    Async.Start extractAndEnqueue

    do! deps.PublishInventory()
    return doc
}

let getDocuments (deps: KnowledgeApiDeps) : Async<KnowledgeDocument list> =
    loadIndex deps.Storage deps.Scope.Container

let deleteDocument (deps: KnowledgeApiDeps) (docId: string) : Async<Result<unit, string>> = async {
    // Fail closed if scope resolution collapsed onto the shared
    // `user-anonymous` container (item 2), then gate on owner/admin in
    // Team / MultiTeam modes (item 1 — same gate `SetAIContext` uses;
    // `Ok ()` in Anonymous / Ephemeral / Individual where the caller only
    // reaches their own scope). Destructive document deletion must not be
    // available to a non-admin team member via a raw API call.
    match KnowledgeApiDeps.guardResolvedScope deps with
    | Error e -> return Error e
    | Ok() ->
        match! deps.EnsureContextWriteAllowed() with
        | Error msg -> return Error msg
        | Ok() ->
            let! existing = loadIndex deps.Storage deps.Scope.Container
            let prior = existing |> List.tryFind (fun d -> d.Id = docId)

            match prior with
            | Some doc ->
                // Phase 115 — index deletion runs FIRST, through the unified
                // lifecycle seam (vector store + sparse index fan-out), and the
                // `index.json` entry is removed only after it completes clean.
                // The pre-115 ordering removed the index entry first and then
                // ran an unguarded `vs.DeleteChunk` loop — one throw left
                // orphaned embeddings still retrievable with no index entry to
                // trace them. A partial failure now leaves the document listed
                // (retryable) with a survivor-summary log instead.
                let! lifecycleReport =
                    match deps.IndexLifecycle with
                    | Some lifecycle -> lifecycle.DeleteDocument deps.VectorScope docId doc.ChunkCount
                    | None -> async.Return ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.empty

                if not (ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.isClean lifecycleReport) then
                    let summary =
                        ToolUp.Platform.IIndexLifecycle.IndexLifecycleReport.summarise lifecycleReport

                    deps.Logger.Warn(
                        sprintf
                            "[KnowledgeBase] deleteDocument %s completed partially — %s. The document stays listed so the delete can be retried."
                            docId
                            summary
                    )

                    return
                        Error(
                            sprintf "Some of the document's index entries could not be deleted (%s). Try again." summary
                        )
                else
                    let rawBlobName = sprintf "knowledge/%s/%s" docId doc.FileName
                    let! _ = deps.Storage.Delete(deps.Scope.Container, rawBlobName)

                    let updated = existing |> List.filter (fun d -> d.Id <> docId)
                    do! saveIndex deps.Storage deps.Scope.Container updated

                    statusCache.TryRemove(docId) |> ignore
                    // Invalidate the prompt-build inventory cache so the next AI
                    // turn sees the updated document count, not the stale 30-s
                    // cached string.
                    KnowledgeBase.ServerInventory.invalidateInventoryCache deps.Scope.Container
                    do! deps.PublishInventory()
                    return Ok()
            | None ->
                // Unknown id — preserve the pre-115 idempotent shape (the index
                // is already in the requested state).
                statusCache.TryRemove(docId) |> ignore
                KnowledgeBase.ServerInventory.invalidateInventoryCache deps.Scope.Container
                do! deps.PublishInventory()
                return Ok()
}

// ─── Original-document retrieval (Phase 102) ─────────────────────

/// `KnowledgeSource` case name for the Phase 107 audit payload.
let private sourceKindName (source: KnowledgeSource) =
    match source with
    | UploadedFile -> "UploadedFile"
    | FromNarrative _ -> "FromNarrative"
    | Note _ -> "Note"

/// Best-effort audit emission for the original-access trail
/// (Phase 107). Awaited on the request path (GP 7) — `IAuditLog.Record`
/// is contractually best-effort and swallows its own failures, so audit
/// gaps never fail the fetch.
let private recordOriginalAccessAudit (deps: KnowledgeApiDeps) (event: AuditEvent) : Async<unit> =
    match deps.AuditLog with
    | Some a -> a.Record(deps.Scope.ScopeId, event)
    | None -> async.Return()

/// Fetch the *original* ingested document for a `docId` (Phase 102).
/// The lookup runs against the caller's resolved scope's index only —
/// scope isolation is structural (GP 4): the container comes from the
/// server-side scope resolver, never from the caller, so a document in
/// another team's scope is simply not findable and returns the same
/// `NotInScope` as a nonexistent id (no existence oracle). Resolution
/// of the bytes is per-`KnowledgeSource` via the Phase 104 resolver;
/// `None` becomes the typed `NoOriginalAvailable` rather than a
/// 404-shaped guess. Successful fetches and refusals both emit the
/// Phase 107 audit events.
let getOriginalDocument (deps: KnowledgeApiDeps) (docId: string) : Async<Result<OriginalDocument, KnowledgeBaseError>> = async {
    let denied (reason: string) =
        recordOriginalAccessAudit
            deps
            (KnowledgeOriginalRetrievalDenied {
                UserId = deps.UserId
                DocumentId = docId
                ScopeId = deps.Scope.ScopeId
                Reason = reason
            })

    try
        let! index = loadIndex deps.Storage deps.Scope.Container

        match index |> List.tryFind (fun d -> d.Id = docId) with
        | None ->
            do! denied "NotInScope"
            return Error NotInScope
        | Some doc ->
            let! resolved = deps.OriginalResolver.Resolve(deps.Storage, deps.Scope.Container, doc)

            match resolved with
            | Some original ->
                do!
                    recordOriginalAccessAudit
                        deps
                        (KnowledgeOriginalRetrieved {
                            UserId = deps.UserId
                            DocumentId = doc.Id
                            ScopeId = deps.Scope.ScopeId
                            SourceKind = sourceKindName doc.Source
                            FileName = doc.FileName
                        })

                return Ok original
            | None ->
                do! denied "NoOriginalAvailable"
                return Error NoOriginalAvailable
    with ex ->
        deps.Logger.Error(sprintf "[KnowledgeBase] GetOriginalDocument failed for %s" docId, Some ex)
        return Error(OriginalRetrievalFailed ex.Message)
}

let getStatus (deps: KnowledgeApiDeps) (docId: string) : Async<IngestionStatus> = async {
    match statusCache.TryGetValue(docId) with
    | true, status -> return status
    | _ ->
        // Cache miss — process restarted, or no observer touched this doc
        // yet. Fall back to the persisted index so the client can still see
        // the last known terminal status (Complete/Failed) survive a reboot.
        let! index = loadIndex deps.Storage deps.Scope.Container

        return
            index
            |> List.tryFind (fun d -> d.Id = docId)
            |> Option.map _.Status
            |> Option.defaultValue (Failed "Document not found")
}

let getSuggestedQuestions (deps: KnowledgeApiDeps) (activeModule: string option) : Async<string list> = async {
    // Sample document names from the team's KB and produce
    // canned questions that point at real content. Fall back
    // to a generic onboarding set when the KB is empty so
    // the zero-state isn't a blank input box. Server-side
    // AI-generated suggestions (per the WS4.4 plan) are a
    // follow-up — this v1 is heuristic-only and free.
    try
        let! docs = loadIndex deps.Storage deps.Scope.Container

        let documents, notes =
            docs
            |> List.partition (fun d ->
                match d.Source with
                | Note _ -> false
                | UploadedFile
                | FromNarrative _ -> true)

        let topDocs = documents |> List.sortByDescending _.UploadedAt |> List.truncate 3

        let questions =
            [
                for doc in topDocs do
                    yield sprintf "What's in \"%s\"?" doc.FileName

                if notes.Length >= 1 then
                    yield "Summarise the team's notes."

                if documents.Length >= 2 then
                    yield "What themes do these documents share?"

                match activeModule with
                | Some name when documents.Length > 0 -> yield sprintf "How does our %s data look?" name
                | _ -> ()
            ]
            |> List.distinct
            |> List.truncate 5

        if questions.IsEmpty && documents.IsEmpty && notes.IsEmpty then
            // Empty KB — return nothing so the client can
            // render its own empty-state hint instead of
            // questions with no grounding.
            return []
        else
            return questions
    with ex ->
        deps.Logger.Warn(sprintf "[KnowledgeBase] GetSuggestedQuestions failed: %s" ex.Message)

        return []
}