// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.DataManagerIngestionObserver

open System.Collections.Generic
open System.Threading
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open DataManagementTypes
open ToolUp.RAG.IngestionTypes

// ─── Data-Manager ingestion-status observer (Phase 173) ──────────
//
// Reflects RAG ingestion progress for **Data Manager** files back into
// the platform-tier `IIngestionStatusStore`, so `FileManagerUI` /
// `MappingDataManagerUI` can render a per-file status badge — the
// parity with Knowledge Base's per-document badge.
//
// The post-save vectorisation hook (`RAGCompose.makeVectorisationHook`)
// marks each Data Manager file `Pending` (with its chunk total) at
// enqueue time, and `Failed reason` on the skip / reject / drop arms.
// This observer closes the loop on the success path: it counts indexed
// chunks against that total and flips the file to `Indexed` once the
// last chunk lands, and to `Failed` on a chunk-level failure.
//
// The observer fires for **every** ingestion job, including Knowledge
// Base's (KB enqueues `DocumentIngestionJob`s through its own path).
// It scopes itself to Data Manager files by acting only on documents
// the post-save hook already marked (`GetTotal > 0`, i.e. a `Pending`
// entry exists). A KB document has no entry in this store, so the
// observer is a no-op for it — KB keeps its own observer.

/// One document's progress while its job runs: the persisted chunk total
/// (`0` for a document this store does not track), the distinct chunk ids
/// indexed so far, and how many callbacks the attempt has delivered.
type private DocumentProgress(total: int) =
    member _.Total = total
    member val Indexed = HashSet<string>()
    member val Callbacks = 0 with get, set

/// Build an `IIngestionStatusObserver` that drives the Data Manager
/// `IIngestionStatusStore`. `notificationChannel` (when composed)
/// publishes a `DataManagerIngestionStatusKey` `CustomNotification` on
/// each terminal transition so the badge updates live; `None` ⇒ the
/// badge still updates on the next `ListFiles` (GP 13 — no channel, no
/// publish, no cost).
let create
    (store: IIngestionStatusStore)
    (notificationChannel: INotificationChannel option)
    (logger: ILogger)
    : IIngestionStatusObserver =

    let jsonOptions = FableConverters.create ()

    // Per-document progress for the life of the job. The first callback
    // for a document reads the store once to learn whether it is a Data
    // Manager file (a `Pending` entry with a chunk total exists) and every
    // later callback reads the remembered answer, so a knowledge-base
    // document costs one read, not one per chunk.
    //
    // The count is of distinct indexed chunk ids, so it never decreases:
    // a chunk failure records the failure and leaves the count alone, and
    // the retried chunk's later success counts once. A chunk id indexed a
    // second time is a redelivery of the whole document (a durable queue
    // re-runs every chunk), so the entry is re-read and the count rebuilds
    // from that chunk against the persisted total.
    //
    // The progress itself is process-local: on the default in-memory queue
    // a restart drops every queued job and `withIngestionRecoverySweep`
    // marks the stranded `Pending` entry `Failed`; on a durable queue the
    // job is redelivered and the count rebuilds.
    let documents =
        System.Collections.Concurrent.ConcurrentDictionary<string, DocumentProgress>()

    let gate = new SemaphoreSlim(1, 1)
    let keyOf (job: IngestionJob) = job.Container + "/" + job.DocumentId

    /// The document's progress entry, reading the store on the first
    /// callback for it (and on a redelivery).
    let progressFor (job: IngestionJob) (indexedChunk: string option) = async {
        do! gate.WaitAsync() |> Async.AwaitTask

        try
            let key = keyOf job

            let redelivered =
                match documents.TryGetValue key, indexedChunk with
                | (true, entry), Some chunkId -> lock entry (fun () -> entry.Indexed.Contains chunkId)
                | _ -> false

            match documents.TryGetValue key with
            | true, entry when not redelivered -> return key, entry
            | _ ->
                let! total = store.GetTotal(job.Container, job.DocumentId)
                let entry = DocumentProgress(total)
                documents[key] <- entry
                return key, entry
        finally
            gate.Release() |> ignore
    }

    /// Forget a document whose job is over: a Data Manager file once it is
    /// `Indexed`, any other document once its attempt's callbacks are in.
    /// A Data Manager file left `Failed` keeps its entry, so the retried
    /// chunk's success counts against what was already indexed.
    let retire (key: string) (job: IngestionJob) (entry: DocumentProgress) (finished: bool) =
        lock entry (fun () ->
            entry.Callbacks <- entry.Callbacks + 1

            // A document this store does not track is remembered only
            // while its attempt's callbacks arrive; without an attempt
            // there is no end to wait for, so it is not remembered at all.
            let untrackedOver =
                entry.Total = 0
                && (match job.Attempt with
                    | Some attempt -> entry.Callbacks >= attempt.EnqueuedChunks
                    | None -> true)

            if finished || untrackedOver then
                documents.TryRemove key |> ignore)

    // Publish to the originating uploader's scope when known (matches
    // KB's publish-to-uploader posture); skip when the enqueue had no
    // user attribution (e.g. system hydration) — the badge still
    // refreshes on the next list. No channel ⇒ no-op.
    let publishTerminal (job: IngestionJob) (status: FileIngestionStatus) = async {
        match notificationChannel, job.OriginatingUserId with
        | Some channel, Some uid ->
            try
                let payload: DataManagerIngestionUpdate = {
                    FileName = job.DocumentId
                    Status = status
                }

                let json = JsonSerializer.Serialize(payload, jsonOptions)
                do! channel.Publish(uid, CustomNotification(DataManagerIngestionStatusKey, json))
            with ex ->
                logger.Error(
                    sprintf "[DataManagerIngestionObserver] failed to publish status for %s" job.DocumentId,
                    Some ex
                )
        | _ -> ()
    }

    { new IIngestionStatusObserver with
        member _.OnChunkIndexed(job: IngestionJob) = async {
            try
                let! key, entry = progressFor job (Some job.ChunkId)

                // Only Data Manager files carry a `Pending` entry with a
                // chunk total; a `0` total is any other document (e.g. a
                // KB document), for which this observer is a no-op.
                if entry.Total > 0 then
                    let indexed =
                        lock entry (fun () ->
                            entry.Indexed.Add job.ChunkId |> ignore
                            entry.Indexed.Count)

                    let finished = indexed >= entry.Total
                    retire key job entry finished

                    if finished then
                        do! store.Set(job.Container, job.DocumentId, Indexed)
                        do! publishTerminal job Indexed
                else
                    retire key job entry false
            with ex ->
                logger.Error(
                    sprintf "[DataManagerIngestionObserver] OnChunkIndexed failed for %s/%s" job.DocumentId job.ChunkId,
                    Some ex
                )
        }

        member _.OnChunkFailed(job: IngestionJob, error: string) = async {
            try
                let! key, entry = progressFor job None
                retire key job entry false

                // Same scoping guard — only mark a file this store is
                // tracking (a Data Manager file). The indexed count is
                // kept: a later success of the retried chunk counts on it.
                if entry.Total > 0 then
                    do! store.Set(job.Container, job.DocumentId, Failed error)
                    do! publishTerminal job (Failed error)
            with ex ->
                logger.Error(
                    sprintf
                        "[DataManagerIngestionObserver] OnChunkFailed handler threw for %s/%s"
                        job.DocumentId
                        job.ChunkId,
                    Some ex
                )
        }
    }