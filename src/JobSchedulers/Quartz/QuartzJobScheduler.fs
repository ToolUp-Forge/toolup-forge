// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JobSchedulers.QuartzScheduler

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Quartz
open ToolUp.Platform
open ToolUp.Platform.JobSchedulers.QuartzStore

// ─── Phase 9c.E — IJobScheduler over Quartz.NET ──────────────────────
//
// The second implementation of `IJobScheduler` / `IJobStore`, and the
// first written by someone holding a framework the interfaces were not
// designed around. The split of responsibilities is deliberate and is
// what keeps each side doing what it is good at:
//
//   * POLICY lives here — the `Schedule` validation chain, idempotency,
//     status transitions, and the synthesis of a `JobDefinition` from a
//     `JobRegistration`. All of it is expressed against the SDK's own
//     vocabulary, and the cron subset is validated with forge's own
//     `CronExpression.tryParse` so `ScheduleError.InvalidCron` means
//     exactly what it means on the in-process default.
//   * PROJECTION lives in `QuartzJobStore` — every definition written
//     becomes a Quartz job detail and, for an active cron job, a Quartz
//     trigger.
//   * EVALUATION AND DISPATCH live in Quartz — trigger firing, thread
//     pool, misfire handling and retry backoff are the backend's, not
//     re-implemented here.
//
// **Licence posture (GP 1 / GP 2).** Quartz.NET is Apache-2.0 with no
// commercial tier, which is why this companion exists at all: the
// obvious alternative was rejected on licensing, not on capability. See
// the companion README.

/// The system user id a scheduler-driven run is attributed to.
[<Literal>]
let private SchedulerUserId = "_system"

/// The largest run-history page this scheduler will answer with,
/// whatever a caller asks for. Mirrors the in-process default's cap.
[<Literal>]
let private MaxRunsPerJob = 100

/// `IJobScheduler` over a Quartz `IScheduler`, with the deployment's own
/// `IJobStore` as the canonical record of definitions and run history.
///
/// Also an `IHostedService`: the composing application's host starts the
/// Quartz scheduler (or leaves it in standby, per
/// `QuartzConfig.StartScheduler`) and shuts it down on stop. Construct
/// via `QuartzJobScheduler.create`, or `QuartzJobScheduler.createWith`
/// when the deployment needs more of Quartz than the defaults (an
/// ADO-backed, clustered store, for instance: in Quartz that is
/// configuration, not code, so it needs no second companion).
type QuartzJobScheduler
    internal
    (
        quartz: IScheduler,
        store: QuartzJobStore,
        handlers: QuartzHandlerRegistry,
        quartzConfig: QuartzConfig,
        config: ServerConfig,
        logger: ILogger
    ) =

    let jobStore = store :> IJobStore

    // ─── Trigger → NextRunAt ─────────────────────────────────────
    //
    // Computed with forge's own cron parser rather than Quartz's, on
    // purpose: `NextRunAt` is a field of the SHARED `JobDefinition` that
    // an admin UI renders and `IJobStore.DueJobs` selects on, so it must
    // mean the same thing under every implementation. Quartz's trigger
    // is what actually fires; after a run the adapter writes Quartz's
    // own next-fire time back, so the two converge on the backend's
    // answer once the job has run at least once.
    let computeNextRunAt (trigger: Trigger) (after: DateTime) : DateTime option =
        match trigger with
        | CronTrigger expression ->
            match CronExpression.tryParse expression with
            | Ok cron -> CronExpression.nextRunAfter cron after
            | Error _ -> None
        | OnEvent _
        | Manual -> None

    // ─── Schedule validation chain ───────────────────────────────
    //
    // Same order and same errors as the in-process default: cron parse,
    // then handler registration, then precision.
    //
    // **`Second` is rejected, and that is a statement about the TRIGGER
    // VOCABULARY, not about Quartz.** Quartz's own cron dialect carries
    // a seconds field and would happily fire sub-minute. Forge's
    // `Trigger.CronTrigger` is five-field crontab, whose finest
    // granularity IS one minute, so there is no way to ASK for a
    // sub-minute schedule through this interface — and portability rule
    // 6 says an implementation that cannot honour a precision rejects it
    // at registration rather than silently delaying dispatch.
    let validateRegistration (registration: JobRegistration) : Result<unit, ScheduleError> =
        let cronCheck =
            match registration.Trigger with
            | CronTrigger expression ->
                match CronExpression.tryParse expression with
                | Ok _ -> Ok()
                | Error reason -> Error(InvalidCron(expression, reason))
            | _ -> Ok()

        match cronCheck with
        | Error e -> Error e
        | Ok() ->
            if not (handlers.IsRegistered registration.Handler) then
                Error(HandlerNotRegistered registration.Handler)
            elif registration.Precision = Second then
                Error(PrecisionUnsupported(Second, [ Minute ]))
            else
                Ok()

    let createNewJob (registration: JobRegistration) : Async<Result<JobId, ScheduleError>> = async {
        let now = DateTime.UtcNow

        let definition: JobDefinition = {
            JobId = Guid.NewGuid()
            ScopeId = registration.ScopeId
            Handler = registration.Handler
            Payload = registration.Payload
            Trigger = registration.Trigger
            Idempotency = registration.Idempotency
            RetryPolicy = registration.RetryPolicy
            ShardKey = registration.ShardKey
            Precision = registration.Precision
            Status = Active
            CreatedAt = now
            CreatedBy = registration.CreatedBy
            NextRunAt = computeNextRunAt registration.Trigger now
            LastRunAt = None
            LastRunStatus = None
            LastRunError = None
            ConsecutiveFailures = 0
            Tags = registration.Tags
        }

        try
            do! jobStore.Save definition
            return Ok definition.JobId
        with ex ->
            return Error(ScheduleError.StorageFailure ex.Message)
    }

    /// Status transition. Writing through the projecting store is what
    /// removes the Quartz trigger on `Disable` / `Cancel` and restores
    /// it on `Enable`, so the schedule and the record can never disagree
    /// about whether a job should fire.
    let setStatus (scopeId: string) (jobId: JobId) (status: JobStatus) = async {
        match! jobStore.Get(scopeId, jobId) with
        | None -> ()
        | Some job when job.Status = status -> ()
        | Some job ->
            let updated = {
                job with
                    Status = status
                    NextRunAt =
                        match status with
                        | Active -> computeNextRunAt job.Trigger DateTime.UtcNow
                        | _ -> None
            }

            do! jobStore.Update updated
    }

    /// Fire a job now, whatever its trigger says. Re-projects the
    /// definition first when Quartz has no job to fire — which is what a
    /// restart of an in-memory Quartz store leaves behind, and the one
    /// case where an otherwise-correct `TriggerOnce` would silently do
    /// nothing.
    let fireNow (job: JobDefinition) (data: JobDataMap) = async {
        let jobKey = QuartzMapping.jobKey job.ScopeId job.JobId
        let! exists = await (quartz.Exists jobKey)

        if not exists then
            do! store.Project job

        data.Add(QuartzDataKeys.JobId, job.JobId.ToString("N"))
        data.Add(QuartzDataKeys.ScopeId, job.ScopeId)

        do! awaitVoid (quartz.TriggerJob(jobKey, data))
    }

    /// The Quartz scheduler this companion drives. Read by the
    /// companion's health check and config validator; a deployment can
    /// also use it to inspect trigger state directly.
    member _.QuartzScheduler: IScheduler = quartz

    /// The projecting `IJobStore` this scheduler writes through. Register
    /// this — not the store it wraps — as the deployment's `IJobStore`,
    /// so a definition written by the Job API is projected onto Quartz
    /// exactly as one written by `Schedule`.
    member _.JobStore: QuartzJobStore = store

    /// The deployment settings this scheduler was composed with.
    member _.Config: QuartzConfig = quartzConfig

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers.Register(name, handler)

        /// Registration is a local dictionary write for this companion
        /// too — Quartz resolves handlers in-process through the
        /// registry, never over the network — so the async overload
        /// performs the same mutation and reports success.
        member _.RegisterHandlerAsync(name, handler) = async {
            handlers.Register(name, handler)
            return Ok()
        }

        member _.Schedule(registration) = async {
            match validateRegistration registration with
            | Error e -> return Error e
            | Ok() ->
                match registration.Idempotency with
                | Some key ->
                    let! existing =
                        jobStore.FindByIdempotencyKey(registration.ScopeId, key.Key, key.TtlSeconds, DateTime.UtcNow)

                    match existing with
                    | Some jobId -> return Ok jobId
                    | None -> return! createNewJob registration
                | None -> return! createNewJob registration
        }

        member _.Cancel(scopeId, jobId) = setStatus scopeId jobId Cancelled

        member _.Disable(scopeId, jobId) = setStatus scopeId jobId Disabled

        member _.Enable(scopeId, jobId) = setStatus scopeId jobId Active

        member _.Get(scopeId, jobId) = jobStore.Get(scopeId, jobId)

        member _.ListJobs(scopeId) = jobStore.ListJobs scopeId

        member _.GetRecentRuns(scopeId, jobId, count) =
            jobStore.GetRecentRuns(scopeId, jobId, min count MaxRunsPerJob)

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match! jobStore.Get(scopeId, jobId) with
            | None -> return Error $"Job %A{jobId} not found in scope %s{scopeId}"
            | Some job when job.Status = Cancelled -> return Error "Cannot trigger a cancelled job"
            | Some job ->
                try
                    let data = JobDataMap()
                    data.Add(QuartzDataKeys.TriggerSource, "manual")

                    data.Add(
                        QuartzDataKeys.UserId,
                        (if String.IsNullOrWhiteSpace byUserId then
                             SchedulerUserId
                         else
                             byUserId)
                    )

                    do! fireNow job data
                    return Ok()
                with ex ->
                    return Error $"Quartz refused the manual fire: {ex.Message}"
        }

        member _.NotifyEventWritten(scopeId, eventType, eventId) = async {
            let! jobs = jobStore.ListJobs scopeId

            let matches =
                jobs
                |> List.filter (fun job ->
                    job.Status = Active
                    && match job.Trigger with
                       | OnEvent declared -> declared = eventType
                       | _ -> false)

            for job in matches do
                try
                    let data = JobDataMap()
                    data.Add(QuartzDataKeys.TriggerSource, "event")
                    data.Add(QuartzDataKeys.EventType, eventType)
                    data.Add(QuartzDataKeys.EventId, eventId.ToString())
                    do! fireNow job data
                with ex ->
                    logger.Warn
                        $"[QuartzJobScheduler] event=event_fire_failed jobId={job.JobId} eventType=%s{eventType}: {ex.Message}"
        }

    interface IHostedService with
        member _.StartAsync(cancellationToken) =
            let work = async {
                if quartzConfig.StartScheduler then
                    do! awaitVoid (quartz.Start cancellationToken)

                    logger.Info
                        $"[QuartzJobScheduler] event=started scheduler=%s{quartzConfig.SchedulerName} maxConcurrency={quartzConfig.MaxConcurrency}"
                else
                    do! awaitVoid (quartz.Standby cancellationToken)

                    logger.Info
                        $"[QuartzJobScheduler] event=standby scheduler=%s{quartzConfig.SchedulerName} — QuartzConfig.StartScheduler is false, so this process schedules and queries jobs but fires none."
            }

            Async.StartAsTask(work, cancellationToken = cancellationToken) :> Task

        member _.StopAsync(cancellationToken) =
            let work = async {
                try
                    do! awaitVoid (quartz.Shutdown(false, cancellationToken))
                with ex ->
                    logger.Warn $"[QuartzJobScheduler] event=shutdown_failed: {ex.Message}"
            }

            Async.StartAsTask(work, cancellationToken = cancellationToken) :> Task

[<RequireQualifiedAccess>]
module QuartzJobScheduler =

    /// Build the Quartz scheduler this companion drives: Quartz's own
    /// in-memory store, the configured thread pool and misfire
    /// threshold, the dispatch adapter registered in the scheduler's own
    /// service container so every fire resolves it by type, and whatever
    /// else `configure` asks for. Not started — the `IHostedService`
    /// start does that, honouring `QuartzConfig.StartScheduler`.
    let private buildSchedulerWith
        (context: QuartzDispatchContext)
        (quartzConfig: QuartzConfig)
        (configure: IQuartzBuilder -> unit)
        : Async<IScheduler> =
        async {
            let builder =
                QuartzSchedulerBuilder.Create(fun (b: IQuartzBuilder) ->
                    b.ConfigureScheduler(fun options -> options.InstanceName <- quartzConfig.SchedulerName)
                    |> ignore

                    b.UseInMemoryStore(fun options -> options.MisfireThreshold <- quartzConfig.MisfireThreshold)
                    |> ignore

                    b.UseDefaultThreadPool(max 1 quartzConfig.MaxConcurrency) |> ignore

                    // The adapter is resolved from the scheduler's own
                    // container on every fire, which is what makes it a
                    // thin per-invocation instance over a singleton
                    // context rather than captured state.
                    b.Services.AddSingleton<QuartzDispatchContext>(context) |> ignore
                    b.Services.AddTransient<QuartzDispatchJob>() |> ignore

                    configure b)

            return! await (builder.BuildScheduler())
        }

    /// Compose the whole companion over a Quartz scheduler this function
    /// builds: the projecting store over `inner`, and the
    /// `IJobScheduler` that drives both.
    ///
    /// `configure` is the seam for a deployment that needs more of
    /// Quartz than the defaults — a persistent or clustered ADO store
    /// (`UsePersistentStore`), listeners, plugins. In Quartz those are
    /// CONFIGURATION, not a second implementation, which is why this
    /// companion ships one scheduler type rather than one per store.
    let createWith
        (configure: IQuartzBuilder -> unit)
        (inner: IJobStore)
        (notificationChannel: INotificationChannel)
        (config: ServerConfig)
        (quartzConfig: QuartzConfig)
        (logger: ILogger)
        : Async<QuartzJobScheduler> =
        async {
            let handlers = QuartzHandlerRegistry()

            let context: QuartzDispatchContext = {
                Store = inner
                Handlers = handlers
                NotificationChannel = notificationChannel
                Config = config
                Logger = logger
            }

            let! quartz = buildSchedulerWith context quartzConfig configure
            let store = QuartzJobStore.create inner quartz logger
            return QuartzJobScheduler(quartz, store, handlers, quartzConfig, config, logger)
        }

    /// Compose the companion with Quartz's defaults — the in-memory job
    /// store, the configured thread pool, nothing else. The call a
    /// deployment makes before `ServerApp.run`, registering the result
    /// as the `IJobScheduler` instance `ComposeJobs` adopts when
    /// `ServerConfig.JobScheduler = QuartzJobScheduler`.
    let create
        (inner: IJobStore)
        (notificationChannel: INotificationChannel)
        (config: ServerConfig)
        (quartzConfig: QuartzConfig)
        (logger: ILogger)
        : Async<QuartzJobScheduler> =
        createWith ignore inner notificationChannel config quartzConfig logger