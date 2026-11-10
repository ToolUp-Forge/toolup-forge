// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.IngestionObserver

open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.RAG.IngestionTypes

/// The report one chunk callback makes against its attempt.
let private reportOf (job: IngestionJob) (report: ChunkReport) : AttemptReport = {
    Scope = job.Container
    DocumentId = job.DocumentId
    AttemptId = job.Attempt |> Option.map _.AttemptId
    OriginatingUser = job.OriginatingUserId |> Option.map UserId.value
    Report = report
}

/// The ingestion-status observer over an attempt store: each chunk callback
/// advances the attempt it belongs to, and a transition the store reports as
/// terminal is announced through `notificationChannel` (`None` ⇒ the status
/// is persisted and announced to nobody, GP 13). Every terminal path —
/// including the producers' direct settles — announces once per attempt.
///
/// The announcement goes to the user the store names, and only when that is
/// the user the enqueue was attributed to: a job whose
/// `OriginatingUserId` (stamped from the request's access context) names
/// someone else is not announced, so a spoofed document entry cannot route a
/// notification to another user's scope. A job with no attributed user (a
/// system enqueue) announces to the store's recipient.
let create
    (store: IIngestionAttemptStore)
    (notificationChannel: INotificationChannel option)
    (logger: ILogger)
    : IIngestionStatusObserver =
    let announce (job: IngestionJob) (announcement: AttemptAnnouncement) = async {
        match job.OriginatingUserId, notificationChannel with
        | Some uid, _ when UserId.value uid <> announcement.Recipient ->
            logger.Warn(
                sprintf
                    "[IngestionObserver] %s announcement for %s skipped: it names %s but the job was enqueued by %s."
                    announcement.Key
                    job.DocumentId
                    announcement.Recipient
                    (UserId.value uid)
            )
        | _, Some channel -> do! AttemptAnnouncement.publish channel logger announcement
        | _, None -> ()
    }

    let advance (callback: string) (job: IngestionJob) (report: ChunkReport) = async {
        try
            match! store.Advance(reportOf job report) with
            | AttemptTransition.Terminal(Some announcement) -> do! announce job announcement
            | AttemptTransition.Terminal None
            | AttemptTransition.Stepped
            | AttemptTransition.Dropped -> ()
        with ex ->
            logger.Error(sprintf "[IngestionObserver] %s failed for %s/%s" callback job.DocumentId job.ChunkId, Some ex)
    }

    { new IIngestionStatusObserver with
        member _.OnChunkIndexed(job: IngestionJob) =
            advance "OnChunkIndexed" job (ChunkReport.Indexed job.ChunkId)

        member _.OnChunkFailed(job: IngestionJob, error: string) =
            advance "OnChunkFailed" job (ChunkReport.Failed(job.ChunkId, error))
    }