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

// ─── IJobScheduler over Quartz.NET ───────────────────────────────────
//
// The second implementation of `IJobScheduler` / `IJobStore`, and the first
// written by someone holding a framework the interfaces were not designed
// around. The split of responsibilities is what keeps each side doing what it
// is good at:
//
//   * POLICY is the platform's `SchedulerPolicy` — the `Schedule` validation
//     chain and its idempotency, status transitions, the synthesis of a
//     `JobDefinition` from a `JobRegistration`, and which jobs a manual fire or
//     an event may dispatch — the same module the in-process default runs, so
//     `ScheduleError.InvalidCron` (and every other answer) means exactly what
//     it means there. How a fired run ends is `JobLifecycle.transition`, also
//     shared (`QuartzJobStore`'s dispatch job).
//   * PROJECTION lives in `QuartzJobStore` — every definition written becomes
//     a Quartz job detail and, for an active cron job, a Quartz trigger.
//   * EVALUATION AND DISPATCH live in Quartz — trigger firing, thread pool,
//     misfire handling and retry backoff are the backend's, not re-implemented
//     here.
//
// **Licence posture (GP 1 / GP 2).** Quartz.NET is Apache-2.0 with no
// commercial tier, which is why this companion exists at all: the obvious
// alternative was rejected on licensing, not on capability. See the companion
// README.

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
        logger: ILogger,
        scopeCarrier: ScopeCarrier ref
    ) =

    let jobStore = store :> IJobStore

    /// Set when the host begins stopping this companion. A manual fire offered
    /// after it is refused as `SchedulerStopping`, as the in-process default
    /// refuses one, rather than reaching a Quartz scheduler mid-shutdown.
    let mutable stopping = false

    /// Both `Schedule` overloads, once each has settled the registration's
    /// scope and how its tags are persisted. This companion has no event
    /// store, so a new job is announced to nobody.
    let scheduleValidated (tagsFor: JobId -> Result<Map<string, string>, string>) (registration: JobRegistration) =
        SchedulerPolicy.scheduleValidated jobStore handlers.IsRegistered (fun _ -> async.Return()) tagsFor registration

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

        // The typed overload registers under
        // `scope.ScopeId` and persists, as an opaque string on the
        // definition's tags, the token the platform's carrier issued for
        // the job. At a fire the dispatch asks the platform to redeem it
        // (`CarriedJobScope.ofDefinition`), so the handler runs under the
        // scope the scheduling request resolved to, across a restart when
        // composition has bound the deployment's carrier. This companion
        // never mints: it holds a string and the platform re-mints.
        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) =
            let carrier = scopeCarrier.Value

            scheduleValidated (fun jobId -> CarriedJobScope.stamp carrier scope jobId registration.Tags) {
                registration with
                    ScopeId = scope.ScopeId
            }

        // The string overload strips the reserved prefix, so a registration
        // (which may have arrived over the wire) cannot present a token.
        member _.Schedule(registration: JobRegistration) =
            let stripped = CarriedJobScope.strip registration.Tags
            scheduleValidated (fun _ -> Ok stripped) registration

        // Written through the projecting store, which is what removes the
        // Quartz trigger on `Disable` / `Cancel` and restores it on `Enable`,
        // so the schedule and the record never disagree about whether a job
        // should fire.
        member _.Cancel(scopeId, jobId) =
            SchedulerPolicy.setStatus jobStore scopeId jobId Cancelled

        member _.Disable(scopeId, jobId) =
            SchedulerPolicy.setStatus jobStore scopeId jobId Disabled

        member _.Enable(scopeId, jobId) =
            SchedulerPolicy.setStatus jobStore scopeId jobId Active

        member _.Get(scopeId, jobId) = jobStore.Get(scopeId, jobId)

        member _.ListJobs(scopeId) = jobStore.ListJobs scopeId

        member _.GetRecentRuns(scopeId, jobId, count) =
            jobStore.GetRecentRuns(scopeId, jobId, min count MaxRunsPerJob)

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match! SchedulerPolicy.triggerable jobStore scopeId jobId with
            | Error refusal -> return Error refusal
            | Ok _ when stopping -> return Error TriggerError.SchedulerStopping
            | Ok job ->
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
                    return Error(TriggerError.DispatchRefused $"Quartz refused the manual fire: {ex.Message}")
        }

        member _.NotifyEventWritten(scopeId, eventType, eventId) = async {
            let! matches = SchedulerPolicy.eventTriggered jobStore scopeId eventType

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

    // Composition binds the deployment's carrier; the dispatch
    // context holds the same cell, so a fire redeems through it.
    interface IScopeCarrierBinding with
        member _.BindScopeCarrier(carrier) = scopeCarrier.Value <- carrier

    // Re-issue a job's token through the platform's one re-issue
    // (`CarriedJobScope.reissue`), written through the projecting store like
    // every other definition change. The Quartz trigger is untouched: the
    // token rides the definition, and a fire redeems whatever it carries.
    interface IJobScopeReissue with
        member _.ReissueScope(scope, scopeId, jobId) = async {
            if not (CarriedJobScope.owns scope scopeId) then
                return Error(ScopeReissueError.ScopeDoesNotOwnJob(scope.ScopeId, scopeId))
            else
                match! jobStore.Get(scopeId, jobId) with
                | None -> return Error(ScopeReissueError.JobNotFound(scopeId, jobId))
                | Some job ->
                    match CarriedJobScope.reissue scopeCarrier.Value scope job with
                    | Error e -> return Error e
                    | Ok updated ->
                        match! Async.Catch(jobStore.Update updated) with
                        | Choice1Of2() -> return Ok()
                        | Choice2Of2 ex -> return Error(ScopeReissueError.Failed ex.Message)
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
            stopping <- true

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

            // This companion's own carrier until composition
            // binds the deployment's (`IScopeCarrierBinding`).
            let scopeCarrier = ref (ScopeCarrier.ephemeral ())

            let context: QuartzDispatchContext = {
                Store = inner
                Handlers = handlers
                NotificationChannel = notificationChannel
                Config = config
                Logger = logger
                ScopeCarrier = scopeCarrier
            }

            let! quartz = buildSchedulerWith context quartzConfig configure
            let store = QuartzJobStore.create inner quartz logger
            return QuartzJobScheduler(quartz, store, handlers, quartzConfig, config, logger, scopeCarrier)
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