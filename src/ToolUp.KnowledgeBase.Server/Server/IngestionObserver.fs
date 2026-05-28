module KnowledgeBase.ServerIngestionObserver

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerJsonHelpers

// ─── Ingestion status observer ────────────────────────────────────

/// Builds an `IIngestionStatusObserver` that reflects RAG ingestion
/// progress back into the Knowledge Base's status cache and persists
/// the latest status to `knowledge/index.json` so it survives a
/// process restart.
///
/// Total chunk count comes from the persisted `KnowledgeDocument.ChunkCount`
/// (set at upload time) — the observer itself doesn't need to know how
/// many chunks were enqueued.
///
/// When a `notificationChannel` is supplied, the observer publishes a
/// `CustomNotification` keyed by `IngestionStatusNotificationKey` to
/// the uploader's user scope on every terminal transition (`Complete`
/// or `Failed`). The AI Assistant subscribes to it so the user sees
/// ingestion finish without waiting for the next 2s status poll.
let makeIngestionStatusObserver
    (storage: IBlobStorage)
    (notificationChannel: INotificationChannel option)
    (logger: ILogger)
    : IIngestionStatusObserver =
    // Wave 2B Gap #7 — re-auth before publishing.
    // `doc.UploadedBy` comes from the persisted KB index, which was
    // stamped at upload time. If a non-KB module ever enqueues a
    // `DocumentIngestionJob` with a spoofed `UploadedBy` and writes to
    // the same KB index slot, the historical publish path would deliver
    // the IngestionStatusUpdate to the wrong user's scope. The
    // `IngestionJob.OriginatingUserId` (set from the request's
    // `AccessContext` at enqueue time) is the trusted side of the
    // comparison; on mismatch we skip the publish and log. `None`
    // (post-save hook / non-user enqueue path) falls through to the
    // historical behaviour.
    let publishTerminal (doc: KnowledgeDocument) (status: IngestionStatus) (originatingUserId: string option) = async {
        let publishOk =
            match originatingUserId with
            | None -> true
            | Some uid -> uid = doc.UploadedBy

        if not publishOk then
            logger.Warn(
                sprintf
                    "[KnowledgeBase] IngestionStatus publish skipped for %s: doc.UploadedBy=%s but OriginatingUserId=%A — refusing to send notification to a user other than the original uploader."
                    doc.Id
                    doc.UploadedBy
                    originatingUserId
            )
        else
            match notificationChannel with
            | None -> ()
            | Some channel ->
                try
                    let outcome, chunkCount, errorReason =
                        match status with
                        | IngestionStatus.Complete n -> "Complete", n, ""
                        | IngestionStatus.Failed reason -> "Failed", 0, reason
                        | _ -> "", 0, ""

                    if outcome <> "" then
                        let payload: IngestionStatusUpdate = {
                            DocumentId = doc.Id
                            FileName = doc.FileName
                            Outcome = outcome
                            ChunkCount = chunkCount
                            ErrorReason = errorReason
                            UploadedBy = doc.UploadedBy
                        }

                        let payloadJson = toJson payload
                        let notification = CustomNotification(IngestionStatusNotificationKey, payloadJson)
                        do! channel.Publish(doc.UploadedBy, notification)
                with ex ->
                    logger.Error(
                        sprintf "[KnowledgeBase] Failed to publish IngestionStatus notification for %s" doc.Id,
                        Some ex
                    )
    }

    { new IIngestionStatusObserver with
        member _.OnChunkIndexed(job: IngestionJob) = async {
            try
                // Read total chunk count from the persisted index — the upload
                // path stamps this at enqueue time. Index is the source of truth.
                let! existing = loadIndex storage job.Container

                match existing |> List.tryFind (fun d -> d.Id = job.DocumentId) with
                | None ->
                    // Document was deleted between enqueue and ingestion — drop.
                    statusCache.TryRemove(job.DocumentId) |> ignore
                    progressCache.TryRemove(job.DocumentId) |> ignore
                | Some doc ->
                    let total = doc.ChunkCount

                    let processed = progressCache.AddOrUpdate(job.DocumentId, 1, fun _ n -> n + 1)

                    let status =
                        if processed >= total then
                            IngestionStatus.Complete total
                        else
                            Embedding(processed, total)

                    statusCache.AddOrUpdate(job.DocumentId, status, fun _ _ -> status) |> ignore

                    // Persist on every transition so a restart mid-ingest can resume
                    // reporting from the last persisted progress point.
                    do! updateIndexStatus storage job.Container job.DocumentId status

                    if processed >= total then
                        progressCache.TryRemove(job.DocumentId) |> ignore
                        do! publishTerminal doc status job.OriginatingUserId
            with ex ->
                logger.Error(
                    sprintf "[KnowledgeBase] OnChunkIndexed failed for %s/%s" job.DocumentId job.ChunkId,
                    Some ex
                )
        }

        member _.OnChunkFailed(job: IngestionJob, error: string) = async {
            try
                let status = IngestionStatus.Failed error
                statusCache.AddOrUpdate(job.DocumentId, status, fun _ _ -> status) |> ignore
                progressCache.TryRemove(job.DocumentId) |> ignore
                do! updateIndexStatus storage job.Container job.DocumentId status

                // Look up the doc once to surface uploader id + filename in
                // the notification payload. Index was just persisted above so
                // the read sees the new `Failed` status.
                let! existing = loadIndex storage job.Container

                match existing |> List.tryFind (fun d -> d.Id = job.DocumentId) with
                | Some doc -> do! publishTerminal doc status job.OriginatingUserId
                | None -> ()
            with ex ->
                logger.Error(
                    sprintf "[KnowledgeBase] OnChunkFailed handler threw for %s/%s" job.DocumentId job.ChunkId,
                    Some ex
                )
        }
    }