module KnowledgeBase.ServerIngestionObserver

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerJsonHelpers
open ToolUp.Platform.Infra

// ─── The knowledge base's ingestion attempts ──────────────────────
//
// A document can be ingested more than once under the same id: an edited
// re-upload supersedes in place, a note is re-saved, a failed document is
// re-indexed. Each enqueue is an attempt, and chunks of an earlier attempt
// can still report long after a newer one has started (a scheduled retry,
// a durable queue's redelivery). So each attempt keeps its own record —
// its id, its target and the chunks it has counted — and a callback of a
// superseded attempt, or of a chunk already counted, moves nothing.
//
// Progress is the record's: one small write per chunk. The scope's index
// is written when the attempt changes class — seeded in flight at `Begin`,
// settled at its terminal — so a document's ingestion costs its index two
// writes whatever its chunk count.

/// The `IngestionStatusUpdate` outcome, chunk count and reason for a
/// terminal status, or `None` for a status the attempt has not settled.
let private terminalOutcome (status: IngestionStatus) : (string * int * string) option =
    match status with
    | IngestionStatus.Complete n -> Some("Complete", n, "")
    | IngestionStatus.Failed reason -> Some("Failed", 0, reason)
    | UnsupportedFormat detail -> Some("UnsupportedFormat", 0, detail)
    | OcrUnavailable detail -> Some("OcrUnavailable", 0, detail)
    | UploadRejected reason -> Some("UploadRejected", 0, reason)
    | Queued
    | ExtractingText
    | Embedding _ -> None

/// The `IngestionStatusNotificationKey` announcement of a document's
/// terminal status to `uploadedBy`, or `None` for a status that is not
/// terminal.
let terminalAnnouncement
    (documentId: string)
    (fileName: string)
    (uploadedBy: string)
    (status: IngestionStatus)
    : AttemptAnnouncement option =
    terminalOutcome status
    |> Option.map (fun (outcome, chunkCount, errorReason) ->
        let payload: IngestionStatusUpdate = {
            DocumentId = documentId
            FileName = fileName
            Outcome = outcome
            ChunkCount = chunkCount
            ErrorReason = errorReason
            UploadedBy = uploadedBy
        }

        {
            Recipient = uploadedBy
            Key = IngestionStatusNotificationKey
            Payload = toJson payload
        })

/// The knowledge-base lattice over a chunk report. A record persisted
/// before it carried a status reads as in flight at its count.
let private knowledgeStep (target: int) (counted: int) (status: IngestionStatus) (report: ChunkReport) =
    let status =
        if isNull (box status) then
            Embedding(counted, target)
        else
            status

    let outcome =
        match report with
        | ChunkReport.Indexed _ -> ChunkIndexed
        | ChunkReport.Failed(_, reason) -> ChunkFailed reason

    IngestionStatus.step target status outcome

/// Forget `docId`'s ingestion attempt — the document was deleted or swept.
/// Idempotent: a document with no recorded attempt deletes nothing.
let forgetIngestionAttempt (storage: IBlobStorage) (container: string) (docId: string) =
    (attemptRecords storage).Delete(container, ingestionAttemptBlobName docId)

/// The knowledge base's `IIngestionAttemptStore`: attempt records beside
/// each document, its index entry written on a change of class. `Settle`
/// sets a terminal status no chunk callback will reach (the upload path's
/// direct sets) and announces it through `notifications`.
type KnowledgeAttemptStore(storage: IBlobStorage, notifications: INotificationChannel option, logger: ILogger) =
    let records = attemptRecords storage

    /// Begin an attempt: its record, then ONE index write seeding
    /// `Embedding(0, enqueuedChunks)` and, when given, the document's full
    /// chunk count (the figure `Complete` reports).
    member _.Begin
        (container: string, docId: string, attemptId: string, enqueuedChunks: int, chunkCount: int option)
        : Async<unit> =
        async {
            let status = Embedding(0, enqueuedChunks)

            do!
                records.Replace(
                    container,
                    ingestionAttemptBlobName docId,
                    {
                        AttemptId = attemptId
                        EnqueuedChunks = enqueuedChunks
                        Counted = []
                        Status = status
                    }
                )

            let! _ =
                updateIndexEntry storage container docId (fun d -> {
                    d with
                        Status = status
                        ChunkCount = defaultArg chunkCount d.ChunkCount
                })

            IngestionStatusFeed.publish docId status
        }

    /// Settle `docId` at `status` directly — a terminal no chunk callback
    /// will reach. Its attempt is forgotten (a late callback is then
    /// dropped), the index entry takes the status (and, for `Complete n`,
    /// the chunk count `n`), and the status is announced to `recipient`.
    member _.Settle(container: string, docId: string, recipient: string, status: IngestionStatus) : Async<unit> = async {
        do! records.Delete(container, ingestionAttemptBlobName docId)

        let! settled =
            updateIndexEntry storage container docId (fun d -> {
                d with
                    Status = status
                    ChunkCount =
                        match status with
                        | Complete n -> n
                        | _ -> d.ChunkCount
            })

        IngestionStatusFeed.publish docId status

        match settled, notifications with
        | Some doc, Some channel ->
            match terminalAnnouncement doc.Id doc.FileName recipient status with
            | Some announcement -> do! AttemptAnnouncement.publish channel logger announcement
            | None -> ()
        | _ -> ()
    }

    interface IIngestionAttemptStore with
        member this.Begin(container, docId, attemptId, enqueuedChunks) =
            this.Begin(container, docId, attemptId, enqueuedChunks, None)

        member _.Advance(report) = async {
            let! moved =
                records.Advance(
                    report.Scope,
                    ingestionAttemptBlobName report.DocumentId,
                    report.AttemptId,
                    report.Report,
                    knowledgeStep
                )

            match moved with
            | AttemptStep.Unmoved -> return AttemptTransition.Dropped
            | AttemptStep.Moved record when not (IngestionStatus.isTerminal record.Status) ->
                IngestionStatusFeed.publish report.DocumentId record.Status
                return AttemptTransition.Stepped
            | AttemptStep.Moved record ->
                // `Complete` reports the document's FULL chunk count — the
                // display figure — even when the attempt re-embedded only the
                // changed subset.
                let! settled =
                    updateIndexEntry storage report.Scope report.DocumentId (fun d -> {
                        d with
                            Status =
                                match record.Status with
                                | Complete _ -> Complete d.ChunkCount
                                | other -> other
                    })

                match settled with
                | None ->
                    clearStatus report.DocumentId
                    return AttemptTransition.Terminal None
                | Some doc ->
                    IngestionStatusFeed.publish doc.Id doc.Status

                    return
                        AttemptTransition.Terminal(terminalAnnouncement doc.Id doc.FileName doc.UploadedBy doc.Status)
        }

        member _.Forget(container, docId) =
            forgetIngestionAttempt storage container docId

/// The knowledge base's ingestion-status observer: the one RAG observer over
/// a `KnowledgeAttemptStore`, announcing each terminal transition through
/// `notificationChannel` to the document's uploader. The AI Assistant
/// subscribes to that key, so the user sees ingestion finish without waiting
/// for the next status poll.
let makeIngestionStatusObserver
    (storage: IBlobStorage)
    (notificationChannel: INotificationChannel option)
    (logger: ILogger)
    : ToolUp.RAG.IngestionTypes.IIngestionStatusObserver =
    ToolUp.RAG.IngestionObserver.create
        (KnowledgeAttemptStore(storage, notificationChannel, logger))
        notificationChannel
        logger