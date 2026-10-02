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

/// Phase 964 — the drain loop's backoff after an exception from the
/// queue itself (`WaitForWork` / `TryDequeue`). A durable store that
/// fails on every call used to be called again at once, spinning a core
/// and writing one error per call. The wait doubles from
/// `DrainBackoffInitial` per consecutive failure up to `DrainBackoffCap`,
/// resets on the first call that succeeds, and is cut short by the
/// stopping token.
let private DrainBackoffInitial = TimeSpan.FromMilliseconds 100.0

/// The ceiling `DrainBackoffInitial` doubles towards (Phase 964).
let private DrainBackoffCap = TimeSpan.FromSeconds 10.0

/// The wait after the `failures`-th consecutive drain-loop failure
/// (1-based): `DrainBackoffInitial` doubled per earlier failure, capped
/// at `DrainBackoffCap` (Phase 964).
let private drainBackoffAfter (failures: int) : TimeSpan =
    let doublings = min (max (failures - 1) 0) 30
    let ms = DrainBackoffInitial.TotalMilliseconds * Math.Pow(2.0, float doublings)
    TimeSpan.FromMilliseconds(min ms DrainBackoffCap.TotalMilliseconds)

/// Minimum spacing between repeat provider-unavailable alerts for one
/// scope — dedups a provider outage that fails N chunks down to a
/// single notification.
let private ProviderAlertDedupWindow = TimeSpan.FromMinutes 5.0

/// Every exception reachable from `ex`: itself, then its inner
/// exceptions depth-first — every member of an `AggregateException`, not
/// only the first. The async machinery wraps a provider's exception in one
/// or more `AggregateException`s, and a caller may wrap it again; the
/// classifier must see the typed cause wherever it sits.
let rec private causeChain (ex: exn) : exn seq = seq {
    if not (isNull ex) then
        yield ex

        match ex with
        | :? AggregateException as agg ->
            for inner in agg.InnerExceptions do
                yield! causeChain inner
        | _ -> yield! causeChain ex.InnerException
}

/// Classify a per-chunk index failure so the caller can decide between
/// immediate dead-letter (permanent) and backed-off retry (transient).
///
/// **The typed provider signal comes first (Phase 867).** An API-backed
/// provider following the Phase 14u contract does not surface a 401 / 403
/// as an HTTP exception: it raises `EmbeddingProviderUnavailableException`
/// (a revoked or wrong key — the same request fails identically). It is
/// looked for anywhere in the cause chain, before any generic arm, and is
/// `Permanent` whatever else sits beside it. Before Phase 867 it fell
/// through to `Transient`, so a revoked key was retried for half an hour,
/// dead-lettered as transient, and never raised the "provider rejected
/// credentials" alert from ingestion.
///
/// Otherwise the outermost exception (or the first inner of an
/// `AggregateException`) is classified by shape. A provider that reports
/// HTTP failures as `HttpRequestException` (via `EnsureSuccessStatusCode`)
/// populates `StatusCode` on .NET 5+ — 401/403 (bad credentials) and other
/// 4xx (malformed request) are permanent; 429 (rate limit) and 5xx are
/// transient. Timeouts / cancellations / network faults are transient.
/// Anything unrecognised defaults to `Transient` — the whole point of
/// Phase 14t is to stop silently DROPPING chunks, so the safe default is
/// "retry, then dead-letter loudly" rather than "drop".
let classifyIndexFailure (ex: exn) : EmbedFailureClass =
    let classifyByShape (ex: exn) =
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

    let credentialsRejected =
        causeChain ex
        |> Seq.tryPick (fun cause ->
            match cause with
            | EmbeddingProviderUnavailableException(status, _) -> Some status
            | _ -> None)

    match credentialsRejected with
    | Some status -> Permanent(sprintf "embedding provider rejected credentials (HTTP %d)" status)
    | None -> classifyByShape ex

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

/// Phase 894 — one worker slot, held by exactly one document at a time.
///
/// The drain loop acquires a slot only once the queue has work (Phase 945:
/// it waits through `IIngestionQueue.WaitForWork` holding nothing) and
/// hands it to the document it dequeued; the document releases it when it
/// is done. An in-process retry releases its slot for the backoff sleep and
/// waits for one again before the next attempt, so a sleeping retry does
/// not hold the pool. `Release` is idempotent, so a document's final
/// release never returns a permit a cancelled sleep already gave back.
///
/// Phase 945 removed the idle-permit steal Phase 894 needed: the drain loop
/// no longer holds a permit while it waits for the next document, so a
/// waking retry waits on the semaphore like any other claimant instead of
/// polling for the loop's idle permit.
type internal WorkerSlot(sem: SemaphoreSlim) =
    let mutable held = 1

    /// Give the permit back, if this slot still holds it.
    member _.Release() =
        if Interlocked.Exchange(&held, 0) = 1 then
            sem.Release() |> ignore

    /// Take a permit again, waiting for one to come free. Cancelled with
    /// the job (the drain loop starts it on the service's drain set, which
    /// `StopAsync` cancels when the shutdown window closes — Phase 869).
    member _.Reacquire() = async {
        let! ct = Async.CancellationToken
        do! sem.WaitAsync(ct) |> Async.AwaitTask
        Interlocked.Exchange(&held, 1) |> ignore
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
        Attempt = doc.Attempt
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
                Attempt = doc.Attempt
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
        (slot: WorkerSlot)
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
                    // Phase 894 — a backoff sleep holds no worker slot: the
                    // pool serves other documents meanwhile, and this one
                    // queues for a slot again before its next attempt.
                    slot.Release()
                    do! Async.Sleep delay
                    do! slot.Reacquire()

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
        (slot: WorkerSlot)
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
                    | None -> do! inProcessRetry slot doc chunkIndex chunkId chunk job ex.Message
        }

    let processJobCore (slot: WorkerSlot) (doc: DocumentIngestionJob) = async {
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
            // Phase 14z — pre-warm through the DOCUMENT'S SCOPE's embedder.
            // The warm-up only pays off if it populates the same cache
            // entries `pipeline.Index` will probe, and under a scope-keyed
            // provider those are keyed by the scope's own `ModelId`.
            // Warming the unscoped entries instead would leave every
            // per-chunk embed a cache miss — the batched round-trip
            // amortisation this step exists for, silently lost. Identity
            // on every stateless provider (GP 11).
            let scopeEmbedder = ScopedEmbedding.forScope embedder doc.Scope

            try
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let! _ = scopeEmbedder.GenerateEmbeddings chunkTexts
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
                                    "provider", scopeEmbedder.ProviderId
                                    "model", scopeEmbedder.ModelId
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
                    $"[IngestionBackgroundService] event=batched_embedding_failed doc={doc.DocumentId} docName={doc.DocumentName} chunks={doc.Chunks.Length} provider={scopeEmbedder.ProviderId}/{scopeEmbedder.ModelId}: the batched embedding call failed; the per-chunk Index path uses the same provider, so every chunk of this document will now fail the same way. This is almost always a provider-level problem (missing/invalid API key, rate limit, or network), not a per-chunk data issue — fix the embedding provider and re-ingest the document.",
                    Some ex
                )

            // Phase 894 (carrying 892.t6) — when the pipeline can write a
            // whole document in one batched round-trip (its store implements
            // `IVectorStoreBatch`), do so, then report every chunk indexed.
            // Any failure falls through to the per-chunk path below, which
            // classifies, retries and dead-letters per chunk exactly as
            // before; per-chunk upserts are idempotent over what the batch
            // wrote. A pipeline that cannot batch never takes this branch
            // (GP 11).
            let! batched =
                match pipeline with
                | :? ToolUp.RAG.RetrievalPipeline.IBatchIndexer as batch when batch.SupportsBatch -> async {
                    try
                        do! batch.IndexBatch doc.Chunks doc.Scope
                        return true
                    with ex ->
                        logger.Warn(
                            $"[IngestionBackgroundService] event=batched_index_failed doc={doc.DocumentId} chunks={doc.Chunks.Length}: {ex.Message}; falling back to per-chunk indexing"
                        )

                        return false
                  }
                | _ -> async.Return false

            if batched then
                for (chunkId, chunk) in doc.Chunks do
                    let job = chunkJob doc chunkId chunk
                    do! emitIndexed job
                    do! notifyObservers "OnChunkIndexed" job (fun o -> o.OnChunkIndexed job)
            else
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
                        do! handleChunkFailure slot doc chunkIndex chunkId chunk job ex
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
    ///
    /// Phase 894 — the worker slot arrives HELD: the drain loop acquired it
    /// before dequeuing this lease. This releases it when done.
    let processJob (slot: WorkerSlot) (lease: IngestionLease) = async {
        let doc = lease.Job

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
                do! processJobCore slot doc
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
            slot.Release()
    }

    // ─── Phase 869 — the drain set ───────────────────────────────────
    //
    // Every document the drain loop starts runs through `Background.start`
    // under `jobs`, and its lease sits in `unsettled` until `processJob` has
    // run to its end (which settles the lease: `Ack`, or `Abandon` on the
    // durable arm). A document still in `unsettled` when the shutdown window
    // closes is cancelled and handed back to the queue by `StopAsync`.
    let jobs = Background.DrainSet()
    let unsettled = ConcurrentDictionary<string, IngestionLease>()

    /// Start `lease`'s document on the drain set. Its lease leaves
    /// `unsettled` only when `processJob` returns — a cancelled document
    /// never reaches that line, so it stays to be handed back.
    let startJob (slot: WorkerSlot) (lease: IngestionLease) =
        unsettled[lease.LeaseId] <- lease

        Background.start
            jobs
            logger
            $"[IngestionBackgroundService] event=process_job_unhandled doc={lease.Job.DocumentId} attempt={lease.Attempt}"
            (async {
                do! processJob slot lease
                unsettled.TryRemove lease.LeaseId |> ignore
            })

    /// Hand every unsettled lease back to the queue — redelivered on a
    /// durable queue (subject to its attempt cap); recorded and dropped on the
    /// in-memory one, which dies with the process either way.
    let handBackUnsettled () = async {
        let mutable returned = 0

        for leaseId in List.ofSeq unsettled.Keys do
            match unsettled.TryRemove leaseId with
            | true, lease ->
                try
                    do! queue.Abandon(lease.LeaseId, "the ingestion service stopped before this document finished")

                    returned <- returned + 1
                with ex ->
                    logger.Error(
                        $"[IngestionBackgroundService] event=hand_back_failed doc={lease.Job.DocumentId} lease={lease.LeaseId}",
                        Some ex
                    )
            | _ -> ()

        return returned
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

        let mutable queueClosed = false

        // Phase 964 — consecutive queue failures, and the wait the next
        // pass takes before it calls the queue again. Logged once per
        // change of state (entering backoff, reaching the cap, recovering),
        // never once per failure.
        let mutable consecutiveFailures = 0
        let mutable backoff = TimeSpan.Zero

        while not stoppingToken.IsCancellationRequested && not queueClosed do
            if backoff > TimeSpan.Zero then
                try
                    do! Task.Delay(backoff, stoppingToken)
                with :? OperationCanceledException ->
                    ()

            if not stoppingToken.IsCancellationRequested then
                try
                    // Phase 894 — a document leaves the queue only when a worker
                    // is free to take it, so the queue's depth is exactly what is
                    // waiting and its capacity bounds what is held in memory.
                    // Phase 945 — wait for work FIRST, holding no permit, then take
                    // a permit, then take the job. An idle loop holds nothing, so a
                    // waking retry never starves behind it. A wake whose job another
                    // drainer took first finds `None` and gives the permit back.
                    let! hasWork = Async.StartAsTask(queue.WaitForWork stoppingToken, cancellationToken = stoppingToken)

                    if hasWork then
                        do! sem.WaitAsync(stoppingToken)
                        let mutable handedOff = false

                        try
                            // Phase 869 — NOT under the stopping token. The claim
                            // is one store call that never waits; cancelled at the
                            // bind after it, a lease it had already taken was
                            // dropped and sat out its expiry. A claim that returns
                            // is started, and the stop waits for it like any other.
                            let! lease = Async.StartAsTask(queue.TryDequeue())

                            match lease with
                            | Some claimed ->
                                // Fire the job without awaiting; it owns the slot
                                // now and releases it when done.
                                handedOff <- true
                                startJob (WorkerSlot sem) claimed
                            | None -> ()
                        finally
                            if not handedOff then
                                sem.Release() |> ignore
                    elif not stoppingToken.IsCancellationRequested then
                        // The queue will never produce another job.
                        queueClosed <- true

                    // The queue answered: a failure streak, if there was one,
                    // is over.
                    if consecutiveFailures > 0 then
                        logger.Info(
                            sprintf
                                "[IngestionBackgroundService] event=dequeue_loop_recovered provider=%s failures=%d: the queue answered again; the drain loop's backoff is reset."
                                providerLabel
                                consecutiveFailures
                        )

                        consecutiveFailures <- 0
                        backoff <- TimeSpan.Zero
                with
                | :? OperationCanceledException -> ()
                | ex ->
                    consecutiveFailures <- consecutiveFailures + 1
                    let previous = backoff
                    backoff <- drainBackoffAfter consecutiveFailures

                    if consecutiveFailures = 1 then
                        logger.Error(
                            sprintf
                                "[IngestionBackgroundService] event=dequeue_loop_error provider=%s backoff_ms=%.0f: unexpected error from the ingestion queue; the drain loop backs off, doubling to %.0f ms, until a call succeeds. Repeat failures are not logged one by one."
                                providerLabel
                                backoff.TotalMilliseconds
                                DrainBackoffCap.TotalMilliseconds,
                            Some ex
                        )
                    elif backoff = DrainBackoffCap && previous <> DrainBackoffCap then
                        logger.Error(
                            sprintf
                                "[IngestionBackgroundService] event=dequeue_loop_backoff_capped provider=%s failures=%d backoff_ms=%.0f: the ingestion queue is still failing; the drain loop now retries at the cap."
                                providerLabel
                                consecutiveFailures
                                backoff.TotalMilliseconds,
                            Some ex
                        )
    }

    member private _.StopBase(cancellationToken: CancellationToken) = base.StopAsync cancellationToken

    /// Phase 869 — stop taking documents, let the ones in flight finish
    /// within the host's shutdown window, and hand the rest back. The base
    /// stop cancels the drain loop (nothing new is dequeued); the documents
    /// already started are NOT cancelled by it — they run on `jobs`, not on
    /// the loop's token — so each gets the window to finish and acknowledge.
    /// When the window closes first, the unfinished ones are cancelled and
    /// returned to the queue, so a durable queue redelivers them at once
    /// rather than after their lease expires.
    override this.StopAsync(cancellationToken: CancellationToken) =
        task {
            do! this.StopBase cancellationToken
            let! drained = jobs.WaitAsync cancellationToken

            if not drained then
                jobs.Cancel()
                let! returned = handBackUnsettled () |> Async.StartAsTask

                logger.Warn(
                    sprintf
                        "[IngestionBackgroundService] event=ingestion_handed_back_at_shutdown count=%d durable=%b — the host's shutdown window closed before these documents finished"
                        returned
                        queue.IsDurable
                )
        }
        :> Task

    interface IDisposable with
        // Phase 869 — a document still running releases its worker slot as it
        // ends, so the semaphore is disposed only once every started document
        // has ended.
        member _.Dispose() =
            jobs.WaitAsync(CancellationToken.None).ContinueWith(fun (_: Task<bool>) -> sem.Dispose())
            |> ignore

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