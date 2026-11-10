// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Infra

open System
open System.Collections.Concurrent
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open DataManagementTypes

// ─── The ingestion attempt ────────────────────────────────────────
//
// Every enqueue of a document is an ATTEMPT: an id minted by the producer
// and the number of chunks it enqueued. A chunk callback advances the
// attempt it belongs to, a callback of a superseded attempt is dropped,
// and a chunk counted once is never counted again, whichever replica
// reports it. The attempt's progress is persisted per attempt, in one
// small document beside the document it ingests, so no process-local
// counter decides a status (GP 12 rule 4) and a 50-chunk document costs
// one small write per chunk rather than a rewrite of its scope's index.
//
// Two producers keep attempts: the knowledge base (status
// `IngestionStatus`, settled into its document index) and the Data
// Manager post-save hook (status `FileIngestionStatus`, settled into
// `IIngestionStatusStore`). One observer advances either store, and
// announces the transition a store reports as terminal.

/// One chunk's report against an ingestion attempt.
[<RequireQualifiedAccess>]
type ChunkReport =
    /// The chunk was indexed.
    | Indexed of chunkId: string
    /// The chunk was given up on (dead-lettered); the attempt fails.
    | Failed of chunkId: string * reason: string

/// One chunk callback, addressed to the attempt it belongs to.
type AttemptReport = {
    /// The storage container of the document's scope.
    Scope: string
    DocumentId: string
    /// The attempt the chunk was enqueued under. `None` for a job enqueued
    /// without one: it counts against whichever attempt is current.
    AttemptId: string option
    /// The user the enqueue was attributed to, when known.
    OriginatingUser: string option
    Report: ChunkReport
}

/// The notification a terminal transition announces.
type AttemptAnnouncement = {
    /// The user scope the notification is published to.
    Recipient: string
    /// The `CustomNotification` key.
    Key: string
    /// The JSON payload.
    Payload: string
}

/// What `Advance` persisted.
[<RequireQualifiedAccess>]
type AttemptTransition =
    /// Nothing: a superseded attempt, a chunk already counted, an attempt
    /// already settled, or a document the store does not track.
    | Dropped
    /// The attempt moved and is still in flight.
    | Stepped
    /// The attempt settled, with the announcement to publish when the
    /// store has one.
    | Terminal of AttemptAnnouncement option

/// The seam both ingestion observers advance: an attempt is begun by its
/// producer before the enqueue, advanced once per chunk callback, and
/// forgotten with its document. Every member is keyed by value — the
/// scope's container, the document id and the attempt id.
type IIngestionAttemptStore =
    /// Begin attempt `attemptId` of `documentId`, which enqueues
    /// `enqueuedChunks` chunks. It becomes the document's current attempt;
    /// the previous attempt's late callbacks are dropped from here on.
    /// Call it BEFORE the enqueue: the first callback can fire before the
    /// enqueue returns.
    abstract Begin: scope: string * documentId: string * attemptId: string * enqueuedChunks: int -> Async<unit>

    /// Apply one chunk callback and say what was persisted.
    abstract Advance: report: AttemptReport -> Async<AttemptTransition>

    /// Forget the document's attempt: the document was deleted or swept.
    /// Idempotent.
    abstract Forget: scope: string * documentId: string -> Async<unit>

/// The persisted progress of a document's current attempt.
type AttemptRecord<'Status> = {
    AttemptId: string
    /// The chunks the attempt enqueued: its completion target.
    EnqueuedChunks: int
    /// The ids of the chunks counted so far.
    Counted: string list
    /// The status the attempt has reached.
    Status: 'Status
}

/// How one chunk report moved a record.
[<RequireQualifiedAccess>]
type AttemptStep<'Status> =
    /// The record is unchanged: no record, a superseded attempt, a chunk
    /// already counted, or a status the report does not move.
    | Unmoved
    /// The record moved to this one, which was persisted.
    | Moved of AttemptRecord<'Status>

/// Companion functions for `AttemptAnnouncement`.
module AttemptAnnouncement =

    /// Publish `announcement`. A failure is logged, never raised: the status
    /// is already persisted, and a status read still finds it.
    let publish (channel: INotificationChannel) (logger: ILogger) (announcement: AttemptAnnouncement) : Async<unit> = async {
        try
            do! channel.Publish(announcement.Recipient, CustomNotification(announcement.Key, announcement.Payload))
        with ex ->
            logger.Error(
                sprintf "[IngestionAttempt] Failed to publish %s to %s" announcement.Key announcement.Recipient,
                Some ex
            )
    }

/// The per-attempt records of one producer, one document per blob through
/// the guarded document store. Writes to one record are serialised in
/// process as well, so a backend without conditional writes loses no count
/// to two callbacks on the same replica.
type AttemptRecords<'Status>(storage: IBlobStorage, logger: ILogger) =
    let docs =
        BlobDocumentStore<AttemptRecord<'Status>>(storage, BlobCodec.json, logger, BlobMapStoreOptions.defaults)

    let locks = ConcurrentDictionary<string, SemaphoreSlim>()

    // A record persisted before a field existed decodes with that field null.
    let normalise (record: AttemptRecord<'Status>) =
        if isNull (box record.Counted) then
            { record with Counted = [] }
        else
            record

    let describe (container: string) (blobName: string) (error: BlobMapStoreError) =
        logger.Warn(sprintf "[IngestionAttempt] %s/%s: %s" container blobName (BlobMapStoreError.describe error))

    /// The record at `blobName`, or `None` when there is none or it cannot be
    /// read (logged): an unreadable record can only relax the attempt guard.
    member _.Read(container: string, blobName: string) : Async<AttemptRecord<'Status> option> = async {
        match! docs.ReadAt(container, blobName) with
        | Ok record -> return record |> Option.map normalise
        | Error error ->
            describe container blobName error
            return None
    }

    /// Write `record` as the current attempt, whatever was there. A refused
    /// write is logged: the attempt still runs, only without the guard.
    member _.Replace(container: string, blobName: string, record: AttemptRecord<'Status>) : Async<unit> = async {
        match! docs.ReplaceAt(container, blobName, record) with
        | Ok() -> ()
        | Error error -> describe container blobName error
    }

    /// Remove the record. Idempotent.
    member _.Delete(container: string, blobName: string) : Async<unit> = async {
        match! docs.DeleteAt(container, blobName) with
        | Ok() -> ()
        | Error error -> describe container blobName error
    }

    /// Apply `report` to the record at `blobName` under the producer's
    /// lattice: `step target counted status report` is the status one report
    /// moves to, given the attempt's target and how many chunks it has
    /// already counted, or `None` for a report it does not move. A report of
    /// another attempt, or of a chunk already counted, does not reach `step`.
    member _.Advance
        (
            container: string,
            blobName: string,
            attemptId: string option,
            report: ChunkReport,
            step: int -> int -> 'Status -> ChunkReport -> 'Status option
        ) : Async<AttemptStep<'Status>> =
        async {
            let gate =
                locks.GetOrAdd(container + "/" + blobName, fun _ -> new SemaphoreSlim(1, 1))

            do! gate.WaitAsync() |> Async.AwaitTask

            try
                let! result =
                    docs.UpdateAt(
                        container,
                        blobName,
                        fun current ->
                            match current |> Option.map normalise with
                            | None -> BlobUpdate.Keep AttemptStep.Unmoved
                            | Some record when attemptId |> Option.exists ((<>) record.AttemptId) ->
                                BlobUpdate.Keep AttemptStep.Unmoved
                            | Some record ->
                                let counted =
                                    match report with
                                    | ChunkReport.Indexed chunkId -> Some chunkId
                                    | ChunkReport.Failed _ -> None

                                if counted |> Option.exists (fun id -> List.contains id record.Counted) then
                                    BlobUpdate.Keep AttemptStep.Unmoved
                                else
                                    match step record.EnqueuedChunks record.Counted.Length record.Status report with
                                    | None -> BlobUpdate.Keep AttemptStep.Unmoved
                                    | Some status ->
                                        let moved = {
                                            record with
                                                Status = status
                                                Counted = (Option.toList counted) @ record.Counted
                                        }

                                        BlobUpdate.Write(moved, AttemptStep.Moved moved)
                    )

                match result with
                | Ok moved -> return moved
                | Error error ->
                    describe container blobName error
                    return AttemptStep.Unmoved
            finally
                gate.Release() |> ignore
        }

/// The Data Manager's attempt store.
module IngestionAttemptStore =

    /// Where a Data Manager file's current attempt is persisted, inside its
    /// scope's container.
    let dataManagerRecordName (documentId: string) =
        "_ingestion-attempts/" + Uri.EscapeDataString documentId + ".json"

    /// The Data Manager lattice: `Pending` until the attempt's last chunk,
    /// then `Indexed`; a failure settles it `Failed`; a settled file does
    /// not move.
    let private dataManagerStep (target: int) (counted: int) (status: FileIngestionStatus) (report: ChunkReport) =
        match status, report with
        | Pending, ChunkReport.Failed(_, reason) -> Some(FileIngestionStatus.Failed reason)
        | Pending, ChunkReport.Indexed _ when counted + 1 >= target -> Some Indexed
        | Pending, ChunkReport.Indexed _ -> Some Pending
        | _ -> None

    /// The Data Manager's attempts over `status` (the file-list badge, with
    /// its chunk total) and per-attempt records kept in `storage`. A
    /// terminal transition is announced on `DataManagerIngestionStatusKey`
    /// to the user the enqueue was attributed to; one with no user is
    /// settled and not announced. A document this store did not begin (a
    /// knowledge-base document) is dropped.
    let dataManager (status: IIngestionStatusStore) (storage: IBlobStorage) (logger: ILogger) : IIngestionAttemptStore =
        let records = AttemptRecords<FileIngestionStatus>(storage, logger)

        { new IIngestionAttemptStore with
            member _.Begin(scope, documentId, attemptId, enqueuedChunks) = async {
                do!
                    records.Replace(
                        scope,
                        dataManagerRecordName documentId,
                        {
                            AttemptId = attemptId
                            EnqueuedChunks = enqueuedChunks
                            Counted = []
                            Status = Pending
                        }
                    )

                do! status.SetPending(scope, documentId, enqueuedChunks)
            }

            member _.Advance(report) = async {
                let! moved =
                    records.Advance(
                        report.Scope,
                        dataManagerRecordName report.DocumentId,
                        report.AttemptId,
                        report.Report,
                        dataManagerStep
                    )

                match moved with
                | AttemptStep.Unmoved -> return AttemptTransition.Dropped
                | AttemptStep.Moved record ->
                    match record.Status with
                    | Pending
                    | NotIngested -> return AttemptTransition.Stepped
                    | final ->
                        do! status.Set(report.Scope, report.DocumentId, final)

                        let announcement =
                            report.OriginatingUser
                            |> Option.map (fun recipient -> {
                                Recipient = recipient
                                Key = DataManagerIngestionStatusKey
                                Payload =
                                    Text.Json.JsonSerializer.Serialize<DataManagerIngestionUpdate>(
                                        {
                                            FileName = report.DocumentId
                                            Status = final
                                        },
                                        ToolUp.Remoting.Json.SystemTextJson.PlatformJson.options
                                    )
                            })

                        return AttemptTransition.Terminal announcement
            }

            member _.Forget(scope, documentId) =
                records.Delete(scope, dataManagerRecordName documentId)
        }