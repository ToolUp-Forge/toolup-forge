module ToolUp.RAG.IngestionService

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRagTelemetry
open ToolUp.Platform.Usage
open ToolUp.RAG.IngestionTypes

// ─── Retry / dead-letter substrate (Phase 14t) ────────────────────

/// Logical handler name the scheduled per-chunk retry job registers
/// under. Referenced by the composition wiring + the background
/// service (which registers the handler) + `IngestionRetryJobHandler`.
[<Literal>]
let RetryHandlerName = "_platform.rag.ingestion-retry"

/// Rolling window over which a burst of dead-letters is counted for
/// the "your embedding provider is degraded" Owner/Admin alert.
let private DeadLetterRateWindow = TimeSpan.FromMinutes 5.0

/// Number of dead-letters within `DeadLetterRateWindow` that trips the
/// aggregate Owner/Admin `SystemMessage(Error)`.
[<Literal>]
let private DeadLetterRateThreshold = 10

/// Phase 509 — how often a durable queue is swept for leases stranded by
/// a dead drainer. A crashed replica's documents are therefore picked up
/// within roughly this window plus the lease's own expiry. Cheap enough
/// to run unconditionally on a durable queue (one list read), and never
/// runs at all on the in-memory default.
let LeaseReclaimInterval = TimeSpan.FromSeconds 30.0

/// Minimum spacing between repeat provider-unavailable alerts for one
/// scope — dedups a provider outage that fails N chunks down to a
/// single notification.
let private ProviderAlertDedupWindow = TimeSpan.FromMinutes 5.0

/// Classify a per-chunk index failure so the caller can decide between
/// immediate dead-letter (permanent) and backed-off retry (transient).
/// The OpenAI embedder surfaces HTTP failures as `HttpRequestException`
/// (via `EnsureSuccessStatusCode`), whose `StatusCode` is populated on
/// .NET 5+ — 401/403 (bad credentials) and other 4xx (malformed
/// request) are permanent; 429 (rate limit) and 5xx are transient.
/// Timeouts / cancellations / network faults are transient. Anything
/// unrecognised defaults to `Transient` — the whole point of Phase 14t
/// is to stop silently DROPPING chunks, so the safe default is "retry,
/// then dead-letter loudly" rather than "drop".
let classifyIndexFailure (ex: exn) : EmbedFailureClass =
    let inner =
        match ex with
        | :? AggregateException as agg when not (isNull agg.InnerException) -> agg.InnerException
        | _ -> ex

    match inner with
    | :? HttpRequestException as httpEx ->
        match Option.ofNullable httpEx.StatusCode with
        | Some code ->
            let status = int code

            if status = 401 || status = 403 then
                Permanent(sprintf "embedding provider rejected credentials (HTTP %d)" status)
            elif status = 429 then
                // Rate limit — retryable. The provider's `Retry-After`
                // header is not recoverable here (`EnsureSuccessStatusCode`
                // discards the response before the ingestion path sees
                // it), so the policy's exponential backoff governs.
                Transient None
            elif status >= 500 then
                Transient None
            else
                Permanent(sprintf "embedding provider returned a non-retryable HTTP %d" status)
        | None ->
            // No status ⇒ transport / DNS / connection failure — transient.
            Transient None
    | :? TaskCanceledException
    | :? TimeoutException
    | :? OperationCanceledException -> Transient None
    | _ -> Transient None

/// Per-scope alert throttling state shared by the background service
/// (first-attempt failures) and `IngestionRetryJobHandler` (retry
/// exhaustion) so provider-unavailable dedup + the dead-letter-rate
/// threshold count both paths coherently. Thread-safe.
type IngestionAlertState() =
    let providerAlerts = ConcurrentDictionary<string, DateTime>()
    let rateAlerts = ConcurrentDictionary<string, DateTime>()
    let deadLetters = ConcurrentDictionary<string, ResizeArray<DateTime>>()

    /// True at most once per `window` for a scope — gates the
    /// provider-unavailable notification so an outage failing N chunks
    /// yields ONE Owner/Admin message, not N.
    member _.ShouldAlertProvider(scopeId: string, now: DateTime, window: TimeSpan) : bool =
        let mutable send = false

        providerAlerts.AddOrUpdate(
            scopeId,
            (fun _ ->
                send <- true
                now),
            (fun _ last ->
                if now - last >= window then
                    send <- true
                    now
                else
                    last)
        )
        |> ignore

        send

    /// Record a dead-letter for `scopeId`; return true when the count
    /// within `window` has reached `threshold` AND the rate alert
    /// hasn't already fired inside the same window (once-per-window).
    member _.RecordDeadLetterAndShouldAlert(scopeId: string, now: DateTime, window: TimeSpan, threshold: int) : bool =
        let times = deadLetters.GetOrAdd(scopeId, (fun _ -> ResizeArray<DateTime>()))

        let crossed =
            lock times (fun () ->
                times.Add now
                times.RemoveAll(fun t -> now - t > window) |> ignore
                times.Count >= threshold)

        if not crossed then
            false
        else
            let mutable send = false

            rateAlerts.AddOrUpdate(
                scopeId,
                (fun _ ->
                    send <- true
                    now),
                (fun _ last ->
                    if now - last >= window then
                        send <- true
                        now
                    else
                        last)
            )
            |> ignore

            send

/// Everything the failure / dead-letter / retry-success paths need,
/// bundled so the background service and `IngestionRetryJobHandler`
/// (which re-attempts one chunk on a scheduled dispatch) run the exact
/// same outcome logic. `AlertState` is shared between the two so alert
/// dedup + the dead-letter-rate threshold count both paths.
type IngestionRetryDeps = {
    Pipeline: IRetrievalPipeline
    EventStore: IEventStore
    Observers: IIngestionStatusObserver list
    NotificationChannel: INotificationChannel option
    Telemetry: IRagTelemetry
    Logger: ILogger
    Policy: IngestionRetryPolicy
    AlertState: IngestionAlertState
}

/// Shared audit / notification / observer emission helpers, reused by
/// the background service and the retry job handler so the two paths
/// can never drift in how they record a chunk outcome.
module Outcome =
    let private jsonOptions = FableConverters.create ()

    let toJson (o: 'T) =
        JsonSerializer.Serialize(o, jsonOptions)

    /// Best-effort audit write (swallow + log). An event-store outage
    /// degrades to a lost audit event, never an escaping exception that
    /// aborts ingestion.
    let emitAudit (eventStore: IEventStore) (logger: ILogger) (scopeId: string) (eventType: string) (payload: 'T) = async {
        try
            let evt = Events.create scopeId "ToolUp.RAG" eventType (toJson payload)
            do! eventStore.Write evt
        with ex ->
            logger.Error(
                $"[Ingestion] event=audit_emit_failed kind={eventType} scope={scopeId}: event-store write failed; the audit event is lost",
                Some ex
            )
    }

    let emitChunkIndexed (eventStore: IEventStore) (logger: ILogger) (job: IngestionJob) =
        emitAudit eventStore logger job.ScopeId "KnowledgeChunkIndexed" {|
            DocumentId = job.DocumentId
            ChunkId = job.ChunkId
            Scope = job.Scope
        |}

    let emitChunkFailed (eventStore: IEventStore) (logger: ILogger) (job: IngestionJob) (error: string) =
        emitAudit eventStore logger job.ScopeId "KnowledgeChunkFailed" {|
            DocumentId = job.DocumentId
            ChunkId = job.ChunkId
            Error = error
        |}

    let notifyObservers
        (observers: IIngestionStatusObserver list)
        (telemetry: IRagTelemetry)
        (logger: ILogger)
        (providerLabel: string)
        (callback: string)
        (job: IngestionJob)
        (notify: IIngestionStatusObserver -> Async<unit>)
        =
        async {
            for obs in observers do
                try
                    do! notify obs
                with ex ->
                    telemetry.RecordObserverFailure(obs.GetType().Name)

                    logger.Error(
                        $"[Ingestion] event=observer_threw callback={callback} doc={job.DocumentId} chunk={job.ChunkId} provider={providerLabel}: continuing with remaining observers",
                        Some ex
                    )
        }

    let private publishSystemMessage
        (channelOpt: INotificationChannel option)
        (logger: ILogger)
        (scopeId: string)
        (level: SystemMessageLevel)
        (text: string)
        =
        async {
            match channelOpt with
            | None -> ()
            | Some channel ->
                try
                    do! channel.Publish(scopeId, SystemMessage(level, text))
                with ex ->
                    logger.Warn(
                        sprintf "[Ingestion] event=system_message_publish_failed scope=%s: %s" scopeId ex.Message
                    )
        }

    /// Dead-letter one chunk: emit `KnowledgeIngestionDeadLettered`,
    /// the per-chunk `KnowledgeChunkFailed`, notify observers
    /// `OnChunkFailed`, and — when dead-letters cross the rolling-window
    /// threshold — publish the aggregate Owner/Admin `SystemMessage(Error)`.
    let deadLetterChunk
        (deps: IngestionRetryDeps)
        (job: IngestionJob)
        (chunkIndex: int)
        (reason: string)
        (attemptCount: int)
        =
        async {
            do!
                emitAudit deps.EventStore deps.Logger job.ScopeId IngestionEventTypes.IngestionDeadLettered {|
                    DocId = job.DocumentId
                    ChunkIndex = chunkIndex
                    Reason = reason
                    AttemptCount = attemptCount
                |}

            do! emitChunkFailed deps.EventStore deps.Logger job reason

            do!
                notifyObservers
                    deps.Observers
                    deps.Telemetry
                    deps.Logger
                    "ingestion-retry"
                    "OnChunkFailed"
                    job
                    (fun o -> o.OnChunkFailed(job, reason))

            let now = DateTime.UtcNow

            if
                deps.AlertState.RecordDeadLetterAndShouldAlert(
                    job.ScopeId,
                    now,
                    DeadLetterRateWindow,
                    DeadLetterRateThreshold
                )
            then
                do!
                    publishSystemMessage
                        deps.NotificationChannel
                        deps.Logger
                        job.ScopeId
                        SystemMessageLevel.Error
                        (sprintf
                            "Knowledge-base ingestion is dead-lettering chunks at a high rate (≥%d in %d minutes) for this workspace. The embedding provider is likely degraded or misconfigured — recent uploads may be missing from search until it recovers and the affected documents are re-ingested."
                            DeadLetterRateThreshold
                            (int DeadLetterRateWindow.TotalMinutes))
        }

    /// Permanent (non-retryable) failure: emit
    /// `KnowledgeEmbeddingProviderUnavailable`, alert Owner/Admin once
    /// per dedup window, then dead-letter the chunk.
    let deadLetterPermanent
        (deps: IngestionRetryDeps)
        (job: IngestionJob)
        (chunkIndex: int)
        (reason: string)
        (attemptCount: int)
        =
        async {
            do!
                emitAudit deps.EventStore deps.Logger job.ScopeId IngestionEventTypes.EmbeddingProviderUnavailable {|
                    ScopeId = job.ScopeId
                    Reason = reason
                |}

            let now = DateTime.UtcNow

            if deps.AlertState.ShouldAlertProvider(job.ScopeId, now, ProviderAlertDedupWindow) then
                do!
                    publishSystemMessage
                        deps.NotificationChannel
                        deps.Logger
                        job.ScopeId
                        SystemMessageLevel.Error
                        (sprintf
                            "The embedding provider rejected knowledge-base ingestion and cannot recover by retrying: %s. New uploads will not be searchable until this is fixed (check the provider API key / configuration)."
                            reason)

            do! deadLetterChunk deps job chunkIndex reason attemptCount
        }

// ─── Background service ───────────────────────────────────────────

/// Dequeues `DocumentIngestionJob` entries and indexes every chunk. Each
/// document goes through a two-step flow:
///
/// 1. **Pre-warm the embedding cache** with a single batched call
///    (`embedder.GenerateEmbeddings`). For OpenAI this is one HTTP POST
///    carrying the whole document's chunk text instead of N sequential
///    POSTs — a 100-chunk document collapses from ~15 s to ~150 ms.
/// 2. **Index each chunk** through `IRetrievalPipeline.Index`. The
///    pipeline calls back into the *cached* embedder so each per-chunk
///    embed is a cache hit (the wrapper is a `CachingEmbeddingProvider`,
///    keyed by provider+model+dim+sha256(text)). This keeps `Index`'s
///    contract single-chunk while still amortising the network cost.
///
/// Per-chunk observer events fire after each `Index` call so the existing
/// progress UX is unchanged. `KnowledgeChunkIndexed` / `KnowledgeChunkFailed`
/// audit events are emitted per chunk for the same reason.
///
/// Concurrency is capped at `MaxConcurrency` parallel documents so the
/// embedding provider is not flooded. The default rose from 4 (per-chunk
/// world) to 8 (per-document world) because each "slot" now amortises
/// itself across a whole document. When Phase 9b (`IJobScheduler`) lands,
/// this service can be replaced at the composition root — the interface
/// contract and event emission are unchanged.
type IngestionBackgroundService
    (
        queue: IIngestionQueue,
        pipeline: IRetrievalPipeline,
        embedder: IEmbeddingProvider,
        eventStore: IEventStore,
        observers: IIngestionStatusObserver list,
        maxConcurrency: int,
        logger: ILogger,
        telemetry: IRagTelemetry,
        usageLog: IUsageLog,
        quota: ITeamQuotaPolicy,
        // Phase 14t — transient-embedder retry + dead-letter.
        retryPolicy: IngestionRetryPolicy,
        // Owner/Admin alerting for permanent failures + high dead-letter
        // rate. `None` ⇒ no channel composed; the audit events still fire.
        notificationChannel: INotificationChannel option,
        // Shared alert-throttle state (also handed to the retry job
        // handler so dedup / rate counting spans both paths).
        alertState: IngestionAlertState,
        // Lazy lookup of the durable job scheduler — resolved from the
        // real (host) service provider, so it's populated even though the
        // scheduler is built downstream of this service's construction.
        // `None` ⇒ no scheduler configured; retries fall back to an
        // in-process loop (which does not survive restart).
        getJobScheduler: unit -> IJobScheduler option,
        // Registers the retry job handler with a resolved scheduler. The
        // handler lives in a file compiled AFTER this one (it reuses this
        // module's `Outcome` helpers), so the composition wiring injects
        // its construction here rather than this service referencing it.
        registerRetryHandler: IJobScheduler -> unit
    ) =
    inherit BackgroundService()

    let sem = new SemaphoreSlim(maxConcurrency, maxConcurrency)

    let jsonOptions = FableConverters.create ()

    let toJson o =
        JsonSerializer.Serialize(o, jsonOptions)

    let providerLabel = $"{embedder.ProviderId}/{embedder.ModelId}"

    // Bundle the shared failure/dead-letter deps once.
    let retryDeps: IngestionRetryDeps = {
        Pipeline = pipeline
        EventStore = eventStore
        Observers = observers
        NotificationChannel = notificationChannel
        Telemetry = telemetry
        Logger = logger
        Policy = retryPolicy
        AlertState = alertState
    }

    // Register the retry handler at most once, on the first scheduler
    // resolution. 0 = not yet, 1 = done (Interlocked so concurrent
    // document processing races safely).
    let mutable retryHandlerRegistered = 0

    // Both emit helpers are best-effort swallow-and-log (mirroring
    // `usageLog.Record` below): `processJob` runs fire-and-forget via
    // `Async.Start`, and the emits are called from *catch paths* — an
    // event-store outage must degrade to a lost audit event, never to
    // an escaping exception that aborts the document's remaining chunks
    // and strands its ingestion status as "in progress" forever.
    // Delegated to the shared `Outcome` helpers so the retry job handler
    // records outcomes identically.
    let emitIndexed (job: IngestionJob) =
        Outcome.emitChunkIndexed eventStore logger job

    let emitFailed (job: IngestionJob) (error: string) =
        Outcome.emitChunkFailed eventStore logger job error

    let notifyObservers (callback: string) (job: IngestionJob) (notify: IIngestionStatusObserver -> Async<unit>) =
        Outcome.notifyObservers observers telemetry logger providerLabel callback job notify

    /// Build the per-chunk `IngestionJob` projection used by observer
    /// callbacks and audit events. The DocumentIngestionJob carries all the
    /// fields verbatim except per-chunk identity.
    let chunkJob (doc: DocumentIngestionJob) (chunkId: string) (chunk: TextChunk) : IngestionJob = {
        DocumentId = doc.DocumentId
        DocumentName = doc.DocumentName
        ChunkId = chunkId
        Chunk = chunk
        Scope = doc.Scope
        ScopeId = doc.ScopeId
        Container = doc.Container
        OriginatingUserId = doc.OriginatingUserId
    }

    // ─── Phase 14t — per-chunk retry / dead-letter ───────────────

    /// Resolve the durable scheduler (if configured) and register the
    /// retry job handler exactly once. Returns `None` when no scheduler
    /// is composed — callers then fall back to the in-process loop.
    let resolveScheduler () =
        match getJobScheduler () with
        | Some scheduler ->
            if Interlocked.CompareExchange(&retryHandlerRegistered, 1, 0) = 0 then
                try
                    registerRetryHandler scheduler
                with ex ->
                    logger.Error("[IngestionBackgroundService] event=retry_handler_register_failed", Some ex)

            Some scheduler
        | None -> None

    /// Schedule a durable retry job for one transient-failed chunk. The
    /// scheduler persists the job (so the pending retry survives process
    /// restart — today's in-memory channel loses it) and drives the
    /// attempt count + final dead-letter; the handler owns the backoff
    /// (keyed to the true ingestion attempt) + jitter, and emits the
    /// RAG-specific dead-letter audit on exhaustion. On a scheduling
    /// failure the chunk is dead-lettered immediately (better a loud
    /// drop than a silent one).
    let scheduleRetry
        (scheduler: IJobScheduler)
        (doc: DocumentIngestionJob)
        (chunkIndex: int)
        (chunkId: string)
        (chunk: TextChunk)
        (firstError: string)
        =
        async {
            let payload: IngestionRetryPayload = {
                DocumentId = doc.DocumentId
                DocumentName = doc.DocumentName
                ChunkId = chunkId
                ChunkIndex = chunkIndex
                Chunk = chunk
                Scope = doc.Scope
                ScopeId = doc.ScopeId
                Container = doc.Container
                OriginatingUserId = doc.OriginatingUserId
            }

            // The scheduled job's own backoff is disabled (`Zero`) — the
            // handler owns ALL the delay, keyed to the true ingestion
            // attempt (`ctx.Attempt + 1`, since attempt 1 was the inline
            // index). `MaxAttempts - 1` remaining scheduler attempts map
            // to ingestion attempts 2..MaxAttempts.
            let jobRetry: JobRetryPolicy = {
                MaxAttempts = max 1 (retryPolicy.MaxAttempts - 1)
                InitialBackoff = TimeSpan.Zero
                MaxBackoff = TimeSpan.Zero
                DeadLetterDestination = None
            }

            let registration: JobRegistration = {
                ScopeId = doc.ScopeId
                Handler = RetryHandlerName
                Payload = Outcome.toJson payload
                Trigger = Manual
                Idempotency = None
                RetryPolicy = jobRetry
                ShardKey = Some doc.DocumentId
                Precision = Minute
                CreatedBy = "system"
                Tags = Map.ofList [ "rag.retry.documentId", doc.DocumentId; "rag.retry.chunkId", chunkId ]
            }

            let deadLetterNow () =
                Outcome.deadLetterChunk retryDeps (chunkJob doc chunkId chunk) chunkIndex firstError 1

            try
                match! scheduler.Schedule registration with
                | Ok jobId ->
                    // Manual trigger sits until fired; dispatch it now so
                    // the retry runs (the handler applies the backoff).
                    match! scheduler.TriggerOnce(doc.ScopeId, jobId, "system") with
                    | Ok() -> ()
                    | Error err ->
                        logger.Warn(
                            sprintf
                                "[IngestionBackgroundService] event=retry_trigger_failed doc=%s chunk=%s: %s; dead-lettering"
                                doc.DocumentId
                                chunkId
                                err
                        )

                        do! deadLetterNow ()
                | Error err ->
                    logger.Warn(
                        sprintf
                            "[IngestionBackgroundService] event=retry_schedule_failed doc=%s chunk=%s: %A; dead-lettering"
                            doc.DocumentId
                            chunkId
                            err
                    )

                    do! deadLetterNow ()
            with ex ->
                logger.Error(
                    sprintf
                        "[IngestionBackgroundService] event=retry_schedule_crashed doc=%s chunk=%s: dead-lettering"
                        doc.DocumentId
                        chunkId,
                    Some ex
                )

                do! deadLetterNow ()
        }

    /// In-process fallback retry loop for deployments with no
    /// `IJobScheduler` composed. Mirrors the `WebhookDispatcher` shape —
    /// exponential backoff + jitter, dead-letter after `MaxAttempts`.
    /// Does NOT survive restart (the scheduled path does); it is the
    /// graceful-degradation path, not the default.
    let inProcessRetry
        (doc: DocumentIngestionJob)
        (chunkIndex: int)
        (chunkId: string)
        (chunk: TextChunk)
        (job: IngestionJob)
        (firstError: string)
        =
        async {
            let mutable attempt = 2
            let mutable handled = false
            let mutable lastError = firstError

            while attempt <= retryPolicy.MaxAttempts && not handled do
                let delay =
                    IngestionRetryPolicy.baseDelayFor retryPolicy attempt
                    + IngestionRetryPolicy.jitterComponentFor retryPolicy attempt (Random.Shared.NextDouble())

                if delay > TimeSpan.Zero then
                    do! Async.Sleep delay

                try
                    do! pipeline.Index chunkId chunk doc.Scope
                    do! emitIndexed job
                    do! notifyObservers "OnChunkIndexed" job (fun o -> o.OnChunkIndexed job)
                    handled <- true
                with ex ->
                    match classifyIndexFailure ex with
                    | Permanent reason ->
                        do! Outcome.deadLetterPermanent retryDeps job chunkIndex reason attempt
                        handled <- true
                    | Transient _ ->
                        lastError <- ex.Message
                        attempt <- attempt + 1

            if not handled then
                do! Outcome.deadLetterChunk retryDeps job chunkIndex lastError retryPolicy.MaxAttempts
        }

    /// Route a first-attempt (inline) index failure: dead-letter
    /// permanently on a non-retryable failure, otherwise hand off to the
    /// durable scheduler (or the in-process fallback) for retry.
    let handleChunkFailure
        (doc: DocumentIngestionJob)
        (chunkIndex: int)
        (chunkId: string)
        (chunk: TextChunk)
        (job: IngestionJob)
        (ex: exn)
        =
        async {
            match classifyIndexFailure ex with
            | Permanent reason -> do! Outcome.deadLetterPermanent retryDeps job chunkIndex reason 1
            | Transient _ ->
                if retryPolicy.MaxAttempts <= 1 then
                    // No retries configured — dead-letter now (still loud).
                    do! Outcome.deadLetterChunk retryDeps job chunkIndex ex.Message 1
                else
                    match resolveScheduler () with
                    | Some scheduler -> do! scheduleRetry scheduler doc chunkIndex chunkId chunk ex.Message
                    | None -> do! inProcessRetry doc chunkIndex chunkId chunk job ex.Message
        }

    let processJobCore (doc: DocumentIngestionJob) = async {
        let chunkTexts = doc.Chunks |> List.map (fun (_, c) -> c.Content) |> List.toArray

        // Phase 9 compute-quota: embedding API calls are billable
        // and were previously unattributed AND ungated. Pre-flight
        // the per-scope budget BEFORE the call. On breach, mark every
        // chunk `Failed` (observable — consistent with the ingestion
        // back-pressure contract; NEVER a silent drop) and skip the
        // document; no embedding spend is incurred.
        let! quotaGate = quota.CheckTokenBudget(doc.ScopeId, ResourceKinds.apiRequests, decimal chunkTexts.Length)

        match quotaGate with
        | Error qb ->
            let msg =
                sprintf
                    "Embedding quota exceeded for scope '%s' (%s limit %M): document not indexed. Usage resets per the configured per-day / per-month window."
                    qb.ScopeId
                    qb.Kind
                    qb.Limit

            logger.Error(
                $"[IngestionBackgroundService] event=embedding_quota_breached doc={doc.DocumentId} scope={doc.ScopeId}: {msg}",
                None
            )

            for (chunkId, chunk) in doc.Chunks do
                let job = chunkJob doc chunkId chunk
                do! emitFailed job msg
                do! notifyObservers "OnChunkFailed" job (_.OnChunkFailed(job, msg))
        | Ok() ->
            // Pre-warm the embedding cache with one batched call. Failures
            // here propagate to per-chunk failure events because the
            // subsequent `pipeline.Index` calls will rerun the same
            // embedding step and surface the same error per chunk —
            // observers see the failure on the unit they care about.
            try
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let! _ = embedder.GenerateEmbeddings chunkTexts
                sw.Stop()
                telemetry.RecordEmbedding(chunkTexts.Length, sw.ElapsedMilliseconds)

                // Best-effort per-scope usage attribution (billing
                // visibility). A `Record` failure must NEVER fail
                // ingestion (mirrors the AI `MeteringProvider`).
                try
                    do!
                        usageLog.Record {
                            RecordId = Guid.NewGuid()
                            ScopeId = doc.ScopeId
                            ResourceKind = ResourceKinds.apiRequests
                            Quantity = decimal chunkTexts.Length
                            Unit = "embeddings"
                            Origin = None
                            Metadata =
                                Map.ofList [
                                    "provider", embedder.ProviderId
                                    "model", embedder.ModelId
                                    "documentId", doc.DocumentId
                                ]
                            Timestamp = DateTime.UtcNow
                        }
                with _ ->
                    ()
            with ex ->
                // NOT a graceful fallback — `pipeline.Index` below calls
                // the SAME embedder per chunk, so a down / misconfigured
                // provider means every chunk of this document is about to
                // fail identically (N KnowledgeChunkFailed events). Say
                // that plainly instead of the old misleading "falling
                // back to per-chunk embed" text, which sent operators
                // chasing a per-chunk bug during a provider outage.
                logger.Error(
                    $"[IngestionBackgroundService] event=batched_embedding_failed doc={doc.DocumentId} docName={doc.DocumentName} chunks={doc.Chunks.Length} provider={embedder.ProviderId}/{embedder.ModelId}: the batched embedding call failed; the per-chunk Index path uses the same provider, so every chunk of this document will now fail the same way. This is almost always a provider-level problem (missing/invalid API key, rate limit, or network), not a per-chunk data issue — fix the embedding provider and re-ingest the document.",
                    Some ex
                )

            for (chunkIndex, (chunkId, chunk)) in List.indexed doc.Chunks do
                let job = chunkJob doc chunkId chunk

                try
                    do! pipeline.Index chunkId chunk doc.Scope
                    do! emitIndexed job
                    do! notifyObservers "OnChunkIndexed" job (fun o -> o.OnChunkIndexed job)
                with ex ->
                    // Phase 14t — classify + retry (transient) / dead-letter
                    // (permanent) instead of the former silent single
                    // `KnowledgeChunkFailed` that dropped the chunk for good.
                    do! handleChunkFailure doc chunkIndex chunkId chunk job ex
    }

    /// Phase 509 — process one leased document, then settle the lease.
    ///
    /// **`Ack` is what makes a durable queue durable.** The lease is
    /// released only after the document's chunks have each been reported
    /// (indexed, dead-lettered, or handed to the retry scheduler), so a
    /// process that dies part-way through leaves the lease to expire and
    /// the job is redelivered by the next reclaim sweep. On the
    /// in-memory default `Ack` / `Abandon` are no-ops — there is nothing
    /// to redeliver from, which is exactly the gap the durable arm
    /// closes.
    let processJob (lease: IngestionLease) = async {
        let doc = lease.Job
        do! sem.WaitAsync() |> Async.AwaitTask

        try
            // Top-level guard: `processJob` is dispatched via `Async.Start`
            // (fire-and-forget), so an exception escaping it has no
            // observer — the document's remaining chunks are silently
            // abandoned and its status stays "in progress" forever. The
            // per-chunk paths inside `processJobCore` carry their own
            // try/with (and the emit helpers swallow-and-log), so this
            // catch fires only for failures outside them (e.g. the quota
            // gate throwing); at that point no chunk has been reported,
            // so the best-effort mark-failed sweep below cannot
            // contradict an already-emitted per-chunk event.
            try
                do! processJobCore doc
                do! queue.Ack lease.LeaseId
            with ex ->
                logger.Error(
                    $"[IngestionBackgroundService] event=process_job_crashed doc={doc.DocumentId} docName={doc.DocumentName} chunks={doc.Chunks.Length} attempt={lease.Attempt} provider={embedder.ProviderId}/{embedder.ModelId}: document processing aborted before per-chunk reporting started",
                    Some ex
                )

                if queue.IsDurable then
                    // Durable arm: hand the document BACK rather than
                    // marking its chunks Failed. The store redelivers it
                    // (up to its attempt cap), so a transient crash costs
                    // a retry instead of a permanently unsearchable
                    // document. Marking Failed here would contradict the
                    // redelivery that is about to happen.
                    do! queue.Abandon(lease.LeaseId, ex.Message)
                else
                    // In-memory arm: unchanged historical behaviour —
                    // there is no redelivery, so the only honest outcome
                    // is to mark every chunk Failed (best-effort) rather
                    // than leave the document silently in progress.
                    let msg =
                        sprintf "Document processing aborted before this chunk was attempted: %s" ex.Message

                    for (chunkId, chunk) in doc.Chunks do
                        let job = chunkJob doc chunkId chunk
                        do! emitFailed job msg
                        do! notifyObservers "OnChunkFailed" job (_.OnChunkFailed(job, msg))

                    do! queue.Ack lease.LeaseId
        finally
            sem.Release() |> ignore
    }

    override _.ExecuteAsync(stoppingToken: CancellationToken) = task {
        // Register the retry job handler up front (idempotent) so the
        // first scheduled retry has no registration race. No-op when no
        // scheduler is composed.
        resolveScheduler () |> ignore

        // Phase 509 — restart recovery. On a durable queue, a replica that
        // died mid-document left its lease to expire; reclaiming those
        // leases is what turns "a restart loses every in-flight document"
        // into "a restart costs a redelivery". Runs once at startup (this
        // instance's own crashed predecessor) and then on an interval (a
        // SIBLING replica's crash, which no startup hook can observe).
        // No-op on the in-memory default — it returns 0 and the loop is
        // never started.
        if queue.IsDurable then
            let reclaimOnce () = async {
                try
                    let! reclaimed = queue.RecoverStranded()

                    if reclaimed > 0 then
                        logger.Warn(
                            sprintf
                                "[IngestionBackgroundService] event=ingestion_leases_reclaimed count=%d: document(s) whose drainer died mid-ingestion were returned to the queue for redelivery."
                                reclaimed
                        )
                with ex ->
                    logger.Error("[IngestionBackgroundService] event=lease_reclaim_failed", Some ex)
            }

            do! reclaimOnce () |> Async.StartAsTask

            Async.Start(
                async {
                    while not stoppingToken.IsCancellationRequested do
                        do! Async.Sleep LeaseReclaimInterval
                        do! reclaimOnce ()
                },
                stoppingToken
            )

        while not stoppingToken.IsCancellationRequested do
            try
                let! lease = Async.StartAsTask(queue.Dequeue stoppingToken, cancellationToken = stoppingToken)

                match lease with
                | Some claimed ->
                    // Fire each job without awaiting — concurrency is
                    // controlled by the semaphore inside processJob.
                    Async.Start(processJob claimed, stoppingToken)
                | None -> ()
            with
            | :? OperationCanceledException -> ()
            | ex ->
                logger.Error(
                    $"[IngestionBackgroundService] event=dequeue_loop_error provider={embedder.ProviderId}/{embedder.ModelId}: unexpected error",
                    Some ex
                )
    }

    interface IDisposable with
        member _.Dispose() = sem.Dispose()

// ─── Convenience constructor (default concurrency) ────────────────

/// `maxConcurrency` defaults to 8 because each slot now processes a full
/// document via batched embedding, not a single chunk — twice the legacy
/// per-chunk default of 4 keeps inflight work modest per provider key.
let create
    (queue: IIngestionQueue)
    pipeline
    embedder
    eventStore
    (observers: IIngestionStatusObserver list)
    (maxConcurrency: int)
    (logger: ILogger)
    (telemetry: IRagTelemetry)
    (usageLog: IUsageLog)
    (quota: ITeamQuotaPolicy)
    (retryPolicy: IngestionRetryPolicy)
    (notificationChannel: INotificationChannel option)
    (alertState: IngestionAlertState)
    (getJobScheduler: unit -> IJobScheduler option)
    (registerRetryHandler: IJobScheduler -> unit)
    =
    new IngestionBackgroundService(
        queue,
        pipeline,
        embedder,
        eventStore,
        observers,
        maxConcurrency = maxConcurrency,
        logger = logger,
        telemetry = telemetry,
        usageLog = usageLog,
        quota = quota,
        retryPolicy = retryPolicy,
        notificationChannel = notificationChannel,
        alertState = alertState,
        getJobScheduler = getJobScheduler,
        registerRetryHandler = registerRetryHandler
    )