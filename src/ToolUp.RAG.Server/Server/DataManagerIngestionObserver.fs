// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.DataManagerIngestionObserver

open System.Collections.Concurrent
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

    // In-flight indexed-chunk count per document. Keyed by
    // container + documentId so two scopes ingesting same-named files
    // don't collide. The total comes from the persisted `Pending`
    // entry (survives a restart); only the live progress count is
    // in-memory.
    //
    // Phase 509 — that used to read "which is correct — a restart
    // re-drains the queue", and it was not true. On the DEFAULT
    // in-memory queue a restart drops every queued job, so the document
    // never re-drains and its `Pending` entry sits forever; that gap is
    // closed from the other end by
    // `RAGServerApp.withIngestionRecoverySweep`, which marks such
    // entries `Failed` at startup. On a queue backed by an
    // `IIngestionQueueStore` the job IS redelivered, so losing the live
    // progress count is genuinely harmless — the redelivery re-indexes
    // every chunk and the count rebuilds from zero against the same
    // persisted total.
    let progress = ConcurrentDictionary<string, int>()
    let keyOf (job: IngestionJob) = job.Container + "/" + job.DocumentId

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
                // Only Data Manager files carry a `Pending` entry (with a
                // chunk total) written by the post-save hook. A `0` total
                // ⇒ no entry ⇒ not a Data Manager file (e.g. a KB
                // document) ⇒ this observer is a no-op for it.
                let! total = store.GetTotal(job.Container, job.DocumentId)

                if total > 0 then
                    let key = keyOf job
                    let indexed = progress.AddOrUpdate(key, 1, (fun _ n -> n + 1))

                    if indexed >= total then
                        progress.TryRemove key |> ignore
                        do! store.Set(job.Container, job.DocumentId, Indexed)
                        do! publishTerminal job Indexed
            with ex ->
                logger.Error(
                    sprintf "[DataManagerIngestionObserver] OnChunkIndexed failed for %s/%s" job.DocumentId job.ChunkId,
                    Some ex
                )
        }

        member _.OnChunkFailed(job: IngestionJob, error: string) = async {
            try
                // Same scoping guard — only mark a file this store is
                // already tracking (a Data Manager file). KB documents
                // have no entry, so leave them to KB's own observer.
                let! existing = store.Get(job.Container, job.DocumentId)

                match existing with
                | Some _ ->
                    progress.TryRemove(keyOf job) |> ignore
                    do! store.Set(job.Container, job.DocumentId, Failed error)
                    do! publishTerminal job (Failed error)
                | None -> ()
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