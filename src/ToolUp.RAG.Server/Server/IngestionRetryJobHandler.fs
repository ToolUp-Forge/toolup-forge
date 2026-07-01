// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.IngestionRetryJobHandler

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.RAG.IngestionTypes
open ToolUp.RAG.IngestionService

// ─── Phase 14t — IngestionRetryJobHandler ─────────────────────────
//
// `IJobHandler` registered under `IngestionService.RetryHandlerName`
// (`_platform.rag.ingestion-retry`). `IngestionBackgroundService`
// schedules one Manual job per transient-failed chunk (see
// `IngestionService.scheduleRetry`) and fires it via `TriggerOnce`; the
// scheduler then persists the job (so the pending retry survives a
// process restart — the old in-memory channel lost it), drives the
// attempt count, and dead-letters the *job* on exhaustion.
//
// This handler re-attempts `IRetrievalPipeline.Index` for the one chunk
// carried in `JobContext.Payload`. It owns the backoff (the scheduled
// job's own backoff is `Zero`) keyed to the TRUE ingestion attempt —
// `ctx.Attempt + 1`, because attempt 1 was the inline index in the
// background service — plus jitter, so a provider-wide rate-limit
// window doesn't cause every retrying chunk to re-hit the provider in
// lockstep.
//
// **Outcome parity.** Success / permanent-failure / dead-letter all go
// through the shared `IngestionService.Outcome` helpers, so a retried
// chunk records `KnowledgeChunkIndexed` / `KnowledgeChunkFailed` /
// `KnowledgeIngestionDeadLettered` and drives the ingestion-status
// observers exactly as the first-attempt path does — the Data Manager
// badge flips to Indexed on a late success and to Failed on dead-letter.
//
// **Stateless across invocations** (portability rule 4). The handler
// resolves everything from `ctx.Payload`; no in-memory state survives
// between dispatches. A malformed payload is `PermanentFailure` (it
// will not parse on retry).

let private jsonOptions = FableConverters.create ()

let private tryDeserialize (payload: string) : IngestionRetryPayload option =
    try
        Some(JsonSerializer.Deserialize<IngestionRetryPayload>(payload, jsonOptions))
    with _ ->
        None

let private jobToIngestionJob (p: IngestionRetryPayload) : IngestionJob = {
    DocumentId = p.DocumentId
    DocumentName = p.DocumentName
    ChunkId = p.ChunkId
    Chunk = p.Chunk
    Scope = p.Scope
    ScopeId = p.ScopeId
    Container = p.Container
    OriginatingUserId = p.OriginatingUserId
}

/// `IJobHandler` for `_platform.rag.ingestion-retry`. `deps` is the same
/// `IngestionRetryDeps` bundle the background service uses (crucially the
/// SAME `AlertState`, so provider-unavailable dedup + the dead-letter-rate
/// threshold count both paths).
type IngestionRetryJobHandler(deps: IngestionRetryDeps) =
    // The scheduled job's `MaxAttempts` = `Policy.MaxAttempts - 1` (attempt
    // 1 was the inline index). Mirror it so the handler knows when it is on
    // the terminal attempt and should emit the RAG-specific dead-letter.
    let maxJobAttempts = max 1 (deps.Policy.MaxAttempts - 1)

    interface IJobHandler with
        member _.Execute(ctx) = async {
            match tryDeserialize ctx.Payload with
            | None ->
                deps.Logger.Warn(sprintf "[IngestionRetryJobHandler] malformed payload for job %A" ctx.JobId)
                return JobResult.PermanentFailure "malformed ingestion-retry payload"
            | Some payload ->
                let job = jobToIngestionJob payload

                // True ingestion attempt: scheduler attempt 1 == ingestion
                // attempt 2 (attempt 1 was the inline index). The handler
                // owns the full backoff for this attempt + jitter.
                let ingestionAttempt = ctx.Attempt + 1

                let delay =
                    IngestionRetryPolicy.baseDelayFor deps.Policy ingestionAttempt
                    + IngestionRetryPolicy.jitterComponentFor deps.Policy ingestionAttempt (Random.Shared.NextDouble())

                if delay > TimeSpan.Zero then
                    do! Async.Sleep delay

                try
                    do! deps.Pipeline.Index payload.ChunkId payload.Chunk payload.Scope
                    do! Outcome.emitChunkIndexed deps.EventStore deps.Logger job

                    do!
                        Outcome.notifyObservers
                            deps.Observers
                            deps.Telemetry
                            deps.Logger
                            "ingestion-retry"
                            "OnChunkIndexed"
                            job
                            (fun o -> o.OnChunkIndexed job)

                    return JobResult.Success
                with ex ->
                    match classifyIndexFailure ex with
                    | Permanent reason ->
                        // Non-retryable — dead-letter + alert now.
                        do! Outcome.deadLetterPermanent deps job payload.ChunkIndex reason ingestionAttempt
                        return JobResult.PermanentFailure reason
                    | Transient _ ->
                        if ctx.Attempt >= maxJobAttempts then
                            // Terminal transient attempt — emit the RAG-specific
                            // dead-letter (the scheduler emits its own generic
                            // `JobDeadLettered` independently).
                            do! Outcome.deadLetterChunk deps job payload.ChunkIndex ex.Message ingestionAttempt
                            return JobResult.PermanentFailure(sprintf "exhausted ingestion retries: %s" ex.Message)
                        else
                            // Retries remain — let the scheduler re-dispatch.
                            return JobResult.TransientFailure ex.Message
        }

/// Factory. The composition wiring constructs the handler and registers
/// it under `IngestionService.RetryHandlerName` on the resolved scheduler.
let create (deps: IngestionRetryDeps) : IJobHandler =
    IngestionRetryJobHandler(deps) :> IJobHandler