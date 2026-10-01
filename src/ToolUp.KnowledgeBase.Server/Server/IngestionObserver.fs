module KnowledgeBase.ServerIngestionObserver

open System
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerJsonHelpers

// ─── Phase 867 — ingestion attempts ───────────────────────────────
//
// A document can be ingested more than once under the same id: an
// edited re-upload supersedes in place (Phase 510), a note is re-saved, a
// failed document is re-indexed. Each of those enqueues is an ATTEMPT,
// and chunks of an earlier attempt can still report long after a newer
// one has started — a scheduled retry fires up to half an hour later, a
// durable queue redelivers an unacknowledged lease. Counted against the
// newer attempt, a stale success completes it early and a stale failure
// fails it for nothing.
//
// So every enqueue mints an `IngestionAttempt` (an id and the number of
// chunks it enqueued), carries it on the job, and records it beside the
// document as the CURRENT attempt. The observer drops a callback whose
// attempt is not the current one, and completes against the attempt's
// own enqueued count — which an incremental re-index makes smaller than
// the document's chunk count.
//
// The record lives under the document's own `knowledge/{docId}/` prefix
// like the Phase 510 sidecars, so a scope reset sheds it by prefix and
// `deleteDocument` / the retention sweep remove it by name.

/// Where a document's CURRENT ingestion attempt is persisted.
let ingestionAttemptBlobName (docId: string) =
    sprintf "knowledge/%s/ingestion-attempt.json" docId

/// Start a new ingestion attempt of `docId` that enqueues `enqueuedChunks`
/// chunks: persist it as the document's current attempt, persist the
/// document's status as `Embedding(0, enqueuedChunks)`, and return the
/// attempt for the job to carry.
///
/// **Call it BEFORE the enqueue, never after**: the first chunk callback
/// can fire before `EnqueueAsync` returns, and it must find its own
/// attempt current and its own total seeded. Writing the attempt first
/// also retires the previous one at a single instant — from here on its
/// late callbacks are dropped. A failed attempt write is logged and the
/// attempt still returned: the document then completes against the job's
/// own count exactly as before, only without the superseded-attempt guard.
///
/// **Do not call from inside an `acquireContainerLock` critical section** —
/// the status write takes the lock itself.
let beginIngestionAttempt
    (storage: IBlobStorage)
    (logger: ILogger)
    (container: string)
    (docId: string)
    (enqueuedChunks: int)
    : Async<IngestionAttempt> =
    async {
        let attempt = {
            AttemptId = Guid.NewGuid().ToString("N")
            EnqueuedChunks = enqueuedChunks
        }

        let bytes = (toJson attempt: string) |> Encoding.UTF8.GetBytes

        match! storage.Upload(container, ingestionAttemptBlobName docId, bytes) with
        | Ok _ -> ()
        | Error reason ->
            logger.Warn(
                sprintf
                    "[KnowledgeBase] Could not record ingestion attempt %s for %s (%s); a late callback from an earlier attempt will not be told apart from this one."
                    attempt.AttemptId
                    docId
                    reason
            )

        do! updateIndexStatus storage container docId (Embedding(0, enqueuedChunks))
        return attempt
    }

/// Forget `docId`'s ingestion attempt — the document was deleted or swept.
/// Idempotent: a document with no recorded attempt deletes nothing.
let forgetIngestionAttempt (storage: IBlobStorage) (container: string) (docId: string) = async {
    let! _ = storage.Delete(container, ingestionAttemptBlobName docId)
    ()
}

/// The id of `docId`'s current attempt, or `None` when none is recorded (a
/// document ingested before attempts existed, or whose attempt write
/// failed). An unreadable record reads as `None` for the same reason: it
/// can only relax the superseded-attempt guard, never drop a live callback.
let private currentAttemptId (storage: IBlobStorage) (container: string) (docId: string) = async {
    match! storage.Download(container, ingestionAttemptBlobName docId) with
    | Ok bytes ->
        try
            return Some (fromJson<IngestionAttempt> (Encoding.UTF8.GetString bytes)).AttemptId
        with _ ->
            return None
    | Error _ -> return None
}

/// One chunk's outcome, as the observer applies it.
type private ChunkOutcome =
    | ChunkIndexed
    | ChunkFailed of reason: string

/// The status lattice. One chunk outcome moves an in-flight status
/// (`Queued` / `ExtractingText` / `Embedding`) forward; every other status
/// is terminal for the attempt and is never left by a callback — only a
/// new attempt (`beginIngestionAttempt`) re-seeds it. `None` = the callback
/// changes nothing.
///
/// Progress is the persisted `Embedding(processed, _)`; the target is the
/// attempt's enqueued count (a job persisted before attempts existed falls
/// back to the seeded total, else the document's chunk count). `Complete`
/// always reports the document's FULL chunk count — the display figure —
/// even when the attempt re-embedded only the changed subset.
let private next (doc: KnowledgeDocument) (job: IngestionJob) (outcome: ChunkOutcome) : IngestionStatus option =
    let inFlight, processed =
        match doc.Status with
        | Queued
        | ExtractingText -> true, 0
        | Embedding(p, _) -> true, p
        | Complete _
        | Failed _
        | UploadRejected _
        | UnsupportedFormat _
        | OcrUnavailable _ -> false, 0

    if not inFlight then
        None
    else
        match outcome with
        | ChunkFailed reason -> Some(IngestionStatus.Failed reason)
        | ChunkIndexed ->
            let target =
                match job.Attempt, doc.Status with
                | Some attempt, _ -> attempt.EnqueuedChunks
                | None, Embedding(_, total) -> total
                | None, _ -> doc.ChunkCount

            let processed = processed + 1

            if processed >= target then
                Some(IngestionStatus.Complete doc.ChunkCount)
            else
                Some(Embedding(processed, target))

// ─── Ingestion status observer ────────────────────────────────────

/// Builds an `IIngestionStatusObserver` that reflects RAG ingestion
/// progress back into the Knowledge Base's status cache and persists
/// the latest status to `knowledge/index.json` so it survives a
/// process restart.
///
/// Phase 867 — progress is held in the PERSISTED status, not in a
/// per-process counter: each callback reads the document's status,
/// applies one step of the lattice above and writes it back, under the
/// container lock, so a chunk retried on another replica advances the same
/// count and a restart resumes from it. The target is the job's
/// `IngestionAttempt.EnqueuedChunks`, and a callback from a superseded
/// attempt is dropped.
///
/// When a `notificationChannel` is supplied, the observer publishes a
/// `CustomNotification` keyed by `IngestionStatusNotificationKey` to
/// the uploader's user scope on every terminal transition (`Complete`
/// or `Failed`). The AI Assistant subscribes to it so the user sees
/// ingestion finish without waiting for the next 2s status poll. A
/// dropped callback publishes nothing, so each attempt publishes at most
/// one terminal notification.
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

    /// Apply one chunk outcome: read-modify-write of the persisted status
    /// under the container lock, the status cache updated inside the same
    /// critical section so two racing callbacks cannot leave the cache
    /// behind the index (a late `Embedding` over a settled `Complete`).
    /// Returns the document and the status it settled on, or `None` when
    /// the callback was dropped.
    let apply (job: IngestionJob) (outcome: ChunkOutcome) = async {
        let lock = acquireContainerLock job.Container
        do! lock.WaitAsync() |> Async.AwaitTask

        try
            let! current = currentAttemptId storage job.Container job.DocumentId

            let superseded =
                match job.Attempt, current with
                | Some attempt, Some currentId -> attempt.AttemptId <> currentId
                | _ -> false

            if superseded then
                logger.Debug(
                    sprintf
                        "[KnowledgeBase] Dropped a late callback for %s/%s: its ingestion attempt has been superseded."
                        job.DocumentId
                        job.ChunkId
                )

                return None
            else
                let! index = loadIndex storage job.Container

                match index |> List.tryFind (fun d -> d.Id = job.DocumentId) with
                | None ->
                    // Document was deleted between enqueue and ingestion — drop.
                    clearStatus job.DocumentId
                    return None
                | Some doc ->
                    match next doc job outcome with
                    | None -> return None
                    | Some status ->
                        let updated =
                            index
                            |> List.map (fun d -> if d.Id = doc.Id then { d with Status = status } else d)

                        do! saveIndex storage job.Container updated
                        setStatus doc.Id status
                        return Some(doc, status)
        finally
            lock.Release() |> ignore
    }

    let settle (job: IngestionJob) (outcome: ChunkOutcome) = async {
        match! apply job outcome with
        | Some(doc, (IngestionStatus.Complete _ as status))
        | Some(doc, (IngestionStatus.Failed _ as status)) -> do! publishTerminal doc status job.OriginatingUserId
        | Some _
        | None -> ()
    }

    { new IIngestionStatusObserver with
        member _.OnChunkIndexed(job: IngestionJob) = async {
            try
                do! settle job ChunkIndexed
            with ex ->
                logger.Error(
                    sprintf "[KnowledgeBase] OnChunkIndexed failed for %s/%s" job.DocumentId job.ChunkId,
                    Some ex
                )
        }

        member _.OnChunkFailed(job: IngestionJob, error: string) = async {
            try
                do! settle job (ChunkFailed error)
            with ex ->
                logger.Error(
                    sprintf "[KnowledgeBase] OnChunkFailed handler threw for %s/%s" job.DocumentId job.ChunkId,
                    Some ex
                )
        }
    }