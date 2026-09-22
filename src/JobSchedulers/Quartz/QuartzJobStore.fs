// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JobSchedulers.QuartzStore

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Quartz
open ToolUp.Platform

// ─── Phase 9c.E — the Quartz side of a forge job ─────────────────────
//
// This file holds everything that answers "what does a `JobDefinition`
// look like to Quartz?" — the key mapping, the trigger and retry-policy
// translation, the handler registry a fired instance looks a handler up
// in, the fired-instance adapter itself, and the `IJobStore` decorator
// that keeps Quartz's view in step with the canonical record.
//
// **Why the canonical record is NOT Quartz's job store.** Quartz stores
// *schedules*: a job detail, its triggers, and their fire state. An
// `IJobStore` stores rather more — per-scope isolation (GP 4), an
// idempotency-key index, per-attempt run history, and the Phase 319
// awaiting-external secondary index — and Quartz has an equivalent for
// none of them. Rebuilding those over `JobDataMap` (a string/object bag
// with no query surface) would produce a second, weaker persistence
// format inside a package whose entire job is to be a *better* one. So
// the shipped `IJobStore` — `BlobJobStore` in a default deployment —
// stays the canonical record, and this decorator PROJECTS each write
// onto Quartz so trigger evaluation happens where Quartz is strong. The
// decision and its consequences are written out in the companion README.
//
// **The projection is best-effort and rebuildable, deliberately.** A
// Quartz-side failure warns and never fails the canonical write: the
// definition is the truth, and a projection that could veto it would
// make the scheduler's availability a function of Quartz's. `Reproject`
// is the recovery lever — it re-derives a scope's whole Quartz view from
// the canonical store, the same shape `BlobJobStore.Rebuild` has for its
// own indexes.

/// Keys the companion writes into a Quartz `JobDataMap`. Every fired
/// instance re-reads the job's definition from the store by
/// `(ScopeId, JobId)`, so these carry identity and trigger provenance
/// only — never state (portability rule 4).
[<RequireQualifiedAccess>]
module QuartzDataKeys =
    /// The forge `JobId`, as its 32-digit hex form.
    let JobId = "toolup.jobId"
    /// The forge `ScopeId` the job belongs to.
    let ScopeId = "toolup.scopeId"
    /// `"cron"`, `"manual"` or `"event"` — which `TriggerSource` the
    /// adapter synthesises for this fire.
    let TriggerSource = "toolup.triggerSource"
    /// For an event fire, the `EventType` that matched.
    let EventType = "toolup.eventType"
    /// For an event fire, the originating event's id.
    let EventId = "toolup.eventId"
    /// For a manual fire, the user id that asked for it.
    let UserId = "toolup.userId"

/// Translation between the forge job vocabulary and Quartz's. Every
/// function here is total on its input except `cronSchedule`, which
/// surfaces Quartz's own parse failure — the caller decides what a
/// schedule Quartz cannot read means for it.
[<RequireQualifiedAccess>]
module QuartzMapping =

    /// Quartz group for a scope. The group is the isolation boundary on
    /// the Quartz side, mirroring the `ScopeId` key prefix the blob
    /// store uses, so a `GroupMatcher` never crosses scopes.
    let group (scopeId: string) = scopeId

    /// The Quartz `JobKey` for a forge job. Name is the `JobId`'s hex
    /// form (stable, collision-free, no separator to escape); group is
    /// the scope.
    let jobKey (scopeId: string) (jobId: JobId) =
        JobKey(jobId.ToString("N"), group scopeId)

    /// The Quartz `TriggerKey` for a forge job's schedule trigger. One
    /// schedule trigger per job, so it shares the job's name and group.
    let triggerKey (scopeId: string) (jobId: JobId) =
        TriggerKey(jobId.ToString("N"), group scopeId)

    /// Read a forge `CronTrigger` expression as a Quartz schedule.
    ///
    /// Forge's `Trigger.CronTrigger` is a five-field crontab expression
    /// (`Minute Hour DayOfMonth Month DayOfWeek`), which Quartz reads
    /// directly under `CronFormat.Unix` — so there is no hand-rolled
    /// rewrite here to drift from the forge parser. The misfire
    /// instruction is `DoNothing`: a job whose deployment was down over
    /// a fire boundary resumes at the NEXT boundary rather than firing
    /// immediately, which is what minute-precision cron means and what
    /// keeps a restart from replaying a backlog.
    ///
    /// Raises whatever Quartz raises for an expression it cannot read —
    /// the store logs and skips, the scheduler rejects at `Schedule`.
    let cronSchedule (expression: string) : IScheduleBuilder =
        CronScheduleBuilder
            .Create(expression, CronFormat.Unix)
            .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing)
        :> IScheduleBuilder

    /// Map a `JobRetryPolicy` onto Quartz's own trigger retry policy,
    /// or `None` when the job asked for no retries.
    ///
    /// The two vocabularies have the same SHAPE — a doubling backoff
    /// under a ceiling — but they are indexed differently at BOTH ends,
    /// and both offsets are load-bearing:
    ///
    ///  * **The count.** Forge's `MaxAttempts` is inclusive of the first
    ///    dispatch; Quartz's is the number of retries AFTER the first
    ///    failure. So the map is `MaxAttempts - 1`.
    ///  * **The first wait.** Forge's `delayFor` is
    ///    `InitialBackoff * 2^(attempt-1)`, and the first RETRY is
    ///    attempt 2 — so forge's first retry already waits TWICE the
    ///    initial backoff. Quartz's first retry waits `InitialDelay`
    ///    exactly. So the map is `2 * InitialBackoff`, clamped to
    ///    `MaxBackoff` exactly as `delayFor` clamps. (Passing
    ///    `InitialBackoff` straight through looks obviously right and
    ///    halves every wait; the binding's mapping test is what caught
    ///    it, by comparing against `JobRetryPolicy.delayFor` attempt for
    ///    attempt rather than against the record's fields.)
    ///
    /// A policy of one attempt is `None`: attaching a Quartz policy of
    /// zero retries is not expressible (`Exponential` requires at least
    /// one) and would misreport the job's contract if it were.
    ///
    /// `Quartz.RetryPolicy` is written out in full at every mention:
    /// forge has a `RetryPolicy` of its own in `ToolUp.Platform`
    /// (the webhook one), and this file opens both namespaces.
    let retryPolicy (policy: JobRetryPolicy) : Quartz.RetryPolicy option =
        if policy.MaxAttempts <= 1 then
            None
        else
            // A policy whose cap is below its initial backoff is
            // pathological but constructible; forge's `delayFor` clamps
            // to the cap and Quartz refuses a ceiling under its own
            // floor, so the ceiling is widened rather than the call
            // being allowed to throw.
            let ceiling = max policy.MaxBackoff policy.InitialBackoff

            let firstRetryDelay =
                TimeSpan.FromMilliseconds(min (policy.InitialBackoff.TotalMilliseconds * 2.0) ceiling.TotalMilliseconds)

            Some(
                Quartz.RetryPolicy.Exponential(
                    policy.MaxAttempts - 1,
                    firstRetryDelay,
                    2.0,
                    Nullable<TimeSpan>(ceiling)
                )
            )

/// Await a Quartz `ValueTask` as an F# `Async`.
let internal awaitVoid (vt: ValueTask) : Async<unit> = vt.AsTask() |> Async.AwaitTask

/// Await a Quartz `ValueTask<'T>` as an F# `Async`.
let internal await (vt: ValueTask<'T>) : Async<'T> = vt.AsTask() |> Async.AwaitTask

/// The name-to-handler bindings a fired Quartz instance resolves
/// against. Owned by `QuartzJobScheduler` and shared with the adapter
/// below; a handler is registered at compose time and looked up by name
/// on every fire, so no handler instance is ever captured in Quartz
/// state (portability rule 4 — the Quartz side holds identity, never
/// implementations).
type internal QuartzHandlerRegistry() =
    let handlers = ConcurrentDictionary<string, IJobHandler>()

    member _.Register(name: string, handler: IJobHandler) = handlers[name] <- handler

    member _.IsRegistered(name: string) = handlers.ContainsKey name

    member _.TryGet(name: string) =
        match handlers.TryGetValue name with
        | true, handler -> Some handler
        | _ -> None

/// Everything a fired instance needs, resolved once at compose time.
/// Held as a singleton in the Quartz scheduler's own service container
/// and injected into each `QuartzDispatchJob`.
///
/// `Store` is the CANONICAL store, not the projecting decorator: a run
/// that finished has nothing to re-project (Quartz advanced its own
/// trigger, which is where `NextRunAt` is then read back FROM), and
/// re-projecting would rewrite the trigger it just fired.
type internal QuartzDispatchContext = {
    Store: IJobStore
    Handlers: QuartzHandlerRegistry
    NotificationChannel: INotificationChannel
    Config: ServerConfig
    Logger: ILogger
}

/// The system user id a scheduler-driven run is attributed to. Mirrors
/// the in-process default's own constant.
[<Literal>]
let internal SystemUserId = "_system"

/// Synthesise the `AccessContext` a run executes under. Copied in intent
/// from the in-process scheduler: the SCHEDULING user is deliberately
/// not used — a cron job fires when nobody is online, so binding the run
/// to that user's permissions would either let a deleted user keep
/// firing work or block an admin-deleted user's analytics from
/// finishing. The subject carries scope identity only.
let internal systemContext (config: ServerConfig) (scopeId: string) : AccessContext =
    let subject =
        if DeploymentConfig.hasTeamScope config then
            TeamMember(SystemUserId, scopeId)
        elif DeploymentConfig.requiresAnyAuth config then
            AuthenticatedUser SystemUserId
        else
            AnonymousSession SystemUserId

    AccessContext.unrestricted subject

/// The Quartz `IJob` every forge job fires through: a thin, stateless
/// adapter that re-reads the definition from the store on every
/// invocation, runs the registered handler, writes the attempt's
/// `JobRun` row and folds the outcome back into the definition.
///
/// **Retry is Quartz's, and that is the whole design.** A transient
/// failure with attempts remaining throws `JobExecutionException`, which
/// is the signal the trigger's `RetryPolicy` (mapped from
/// `JobRetryPolicy` at schedule time) re-fires on — so the backoff
/// arithmetic lives in the backend rather than being re-implemented
/// here, and `IJobExecutionContext.RetryAttempt` is the attempt counter.
/// A terminal outcome returns normally so the trigger goes back to its
/// ordinary schedule.
type internal QuartzDispatchJob(context: QuartzDispatchContext) =

    let readKey (map: JobDataMap) (key: string) =
        if map.ContainsKey key then
            match map[key] with
            | null -> None
            | value -> Some(string value)
        else
            None

    let triggerSourceOf (map: JobDataMap) : TriggerSource =
        match readKey map QuartzDataKeys.TriggerSource with
        | Some "manual" -> ScheduledManually(readKey map QuartzDataKeys.UserId |> Option.defaultValue SystemUserId)
        | Some "event" ->
            let eventType = readKey map QuartzDataKeys.EventType |> Option.defaultValue ""

            let eventId =
                match readKey map QuartzDataKeys.EventId with
                | Some raw ->
                    match Guid.TryParse raw with
                    | true, id -> id
                    | _ -> Guid.Empty
                | None -> Guid.Empty

            ScheduledByEvent(eventType, eventId)
        | _ -> ScheduledByCron

    /// Record (or overwrite) this attempt's run row. One `RunId` per
    /// attempt, written twice — once as `Running` so an in-flight run is
    /// visible, once with the terminal status — which is what the store
    /// layout (`runs/{runId}.json`) already models.
    let recordRun (job: JobDefinition) (runId: Guid) (attempt: int) (startedAt: DateTime) status error handle = async {
        let completedAt =
            match status with
            | Running -> None
            | _ -> Some DateTime.UtcNow

        let run: JobRun = {
            RunId = runId
            JobId = job.JobId
            ScopeId = job.ScopeId
            Attempt = attempt
            StartedAt = startedAt
            CompletedAt = completedAt
            Status = status
            Error = error
            DurationMs = completedAt |> Option.map (fun c -> int64 (c - startedAt).TotalMilliseconds)
            ExternalHandle = handle
        }

        try
            do! context.Store.RecordRun run
        with ex ->
            context.Logger.Warn
                $"[QuartzJobScheduler] event=run_record_failed jobId={job.JobId} runId={runId} attempt={attempt}: {ex.Message}"
    }

    /// Fold an attempt's outcome back into the persisted definition.
    /// `nextRunAt` comes from the trigger Quartz just advanced, so the
    /// canonical record mirrors the schedule that will actually fire
    /// rather than a second, independently-computed one.
    let updateDefinition
        (job: JobDefinition)
        (status: JobRunStatus)
        (error: string option)
        (nextRunAt: DateTime option)
        =
        async {
            let updated = {
                job with
                    LastRunAt = Some DateTime.UtcNow
                    LastRunStatus = Some status
                    LastRunError = error
                    ConsecutiveFailures =
                        match status with
                        | Succeeded -> 0
                        | DeadLettered -> job.ConsecutiveFailures + 1
                        | _ -> job.ConsecutiveFailures
                    NextRunAt =
                        match nextRunAt with
                        | Some _ -> nextRunAt
                        | None -> job.NextRunAt
            }

            try
                do! context.Store.Update updated
            with ex ->
                context.Logger.Warn
                    $"[QuartzJobScheduler] event=definition_update_failed jobId={job.JobId} status=%A{status}: {ex.Message}"
        }

    let notifyDeadLetter (job: JobDefinition) (error: string) = async {
        try
            let text =
                $"Job {job.Handler} failed permanently after {job.RetryPolicy.MaxAttempts} attempts: {error}"

            do! context.NotificationChannel.Publish(job.ScopeId, SystemMessage(SystemMessageLevel.Warning, text))
        with ex ->
            context.Logger.Warn
                $"[QuartzJobScheduler] event=dead_letter_notify_failed jobId={job.JobId} handler={job.Handler}: {ex.Message}"
    }

    let execute (ctx: IJobExecutionContext) = async {
        let map = ctx.MergedJobDataMap
        let scopeId = readKey map QuartzDataKeys.ScopeId |> Option.defaultValue ""

        let jobId =
            match readKey map QuartzDataKeys.JobId with
            | Some raw ->
                match Guid.TryParse raw with
                | true, id -> Some id
                | _ -> None
            | None -> None

        match jobId with
        | None ->
            context.Logger.Warn
                $"[QuartzJobScheduler] event=fire_without_identity jobKey={ctx.JobDetail.Key} — the job data map carried no readable {QuartzDataKeys.JobId}; nothing was dispatched."
        | Some jobId ->
            let! definition = context.Store.Get(scopeId, jobId)

            match definition with
            | None ->
                context.Logger.Warn
                    $"[QuartzJobScheduler] event=fire_for_unknown_job jobId={jobId} scope=%s{scopeId} — the Quartz job outlived its definition; delete the Quartz job or reproject the scope."
            | Some job ->
                let source = triggerSourceOf map

                let isManual =
                    (match source with
                     | ScheduledManually _ -> true
                     | _ -> false)

                // A cancelled job never runs. A disabled job runs only
                // when an operator asked for this specific fire —
                // `Disable` stops the SCHEDULE, and `TriggerOnce` is
                // allowed against a disabled job by the IJobScheduler
                // contract.
                if job.Status = Cancelled || (job.Status = Disabled && not isManual) then
                    context.Logger.Debug
                        $"[QuartzJobScheduler] event=fire_skipped jobId={jobId} status=%A{job.Status} manual={isManual}"
                else
                    let attempt = ctx.RetryAttempt + 1
                    let runId = Guid.NewGuid()
                    let startedAt = DateTime.UtcNow

                    let nextRunAt =
                        if ctx.NextFireTimeUtc.HasValue then
                            Some ctx.NextFireTimeUtc.Value.UtcDateTime
                        else
                            None

                    do! recordRun job runId attempt startedAt Running None None

                    let jobContext: JobContext = {
                        JobId = job.JobId
                        ScopeId = job.ScopeId
                        AccessContext = systemContext context.Config job.ScopeId
                        Attempt = attempt
                        Trigger = job.Trigger
                        TriggerSource = source
                        ScheduledAt =
                            if ctx.ScheduledFireTimeUtc.HasValue then
                                ctx.ScheduledFireTimeUtc.Value.UtcDateTime
                            else
                                startedAt
                        RunningAt = startedAt
                        Payload = job.Payload
                        DeadLetterDestination = job.RetryPolicy.DeadLetterDestination
                    }

                    let! outcome = async {
                        match context.Handlers.TryGet job.Handler with
                        | None ->
                            return PermanentFailure $"Handler '{job.Handler}' is not registered with this scheduler"
                        | Some handler ->
                            try
                                return! handler.Execute jobContext
                            with ex ->
                                return TransientFailure ex.Message
                    }

                    // `MaxAttempts` is inclusive of the first dispatch,
                    // so attempts remain while `attempt < MaxAttempts`.
                    let retriesRemain = attempt < job.RetryPolicy.MaxAttempts

                    match outcome with
                    | Success ->
                        do! recordRun job runId attempt startedAt Succeeded None None
                        do! updateDefinition job Succeeded None nextRunAt

                    | HandedOff handle ->
                        // Phase 319's non-blocking hand-off. The handle
                        // is persisted so nothing is lost, but this
                        // companion runs NO reconciliation pass — that
                        // lives in the in-process scheduler — so the run
                        // stays awaiting until something drives it. Said
                        // once, loudly, rather than left to be inferred
                        // from a run that never finishes.
                        do! recordRun job runId attempt startedAt AwaitingExternal None (Some handle)

                        context.Logger.Warn
                            $"[QuartzJobScheduler] event=external_handoff_unreconciled jobId={jobId} handle={handle.HandleId} — the Quartz companion persists the handle but runs no external-compute reconciliation pass, so this run stays AwaitingExternal. Use JobScheduler = InProcessJobScheduler for external-compute hand-off, or drive IExternalCompletionSink yourself."

                        do! updateDefinition job AwaitingExternal None nextRunAt

                    | PermanentFailure error ->
                        do! recordRun job runId attempt startedAt DeadLettered (Some error) None
                        do! updateDefinition job DeadLettered (Some error) nextRunAt
                        do! notifyDeadLetter job error

                    | TransientFailure error when retriesRemain ->
                        do! recordRun job runId attempt startedAt Failed (Some error) None
                        do! updateDefinition job Failed (Some error) nextRunAt

                        // Hand the retry decision to the trigger's own
                        // policy: Quartz re-fires, with the backoff the
                        // `JobRetryPolicy` was mapped onto.
                        raise (JobExecutionException(error))

                    | TransientFailure error ->
                        do! recordRun job runId attempt startedAt DeadLettered (Some error) None
                        do! updateDefinition job DeadLettered (Some error) nextRunAt
                        do! notifyDeadLetter job error
    }

    interface IJob with
        member _.Execute(ctx: IJobExecutionContext, cancellationToken: CancellationToken) : ValueTask =
            ValueTask(Async.StartAsTask(execute ctx, cancellationToken = cancellationToken) :> Task)

/// An `IJobStore` that keeps a Quartz scheduler's view of a scope in
/// step with the canonical store behind it.
///
/// Every read delegates verbatim — the canonical store answers
/// `Get` / `ListJobs` / `FindByIdempotencyKey` / `GetRecentRuns` /
/// `DueJobs` / `AwaitingExternalRuns` / `ListScopesWithJobs`, and every
/// contract those methods carry is inherited rather than re-implemented.
/// `Save` and `Update` additionally project the definition onto Quartz:
/// a durable job detail per job (so a manual `TriggerOnce` always has
/// something to fire), plus a cron trigger for an `Active` cron job. A
/// `Cancelled` job's Quartz job is deleted; a `Disabled` one keeps its
/// detail and loses its trigger.
///
/// Construct via `QuartzJobStore.create`.
type QuartzJobStore(inner: IJobStore, scheduler: IScheduler, logger: ILogger) =

    let jobDetail (definition: JobDefinition) =
        let map = JobDataMap()
        map.Add(QuartzDataKeys.JobId, definition.JobId.ToString("N"))
        map.Add(QuartzDataKeys.ScopeId, definition.ScopeId)

        JobBuilder
            .Create<QuartzDispatchJob>()
            .WithIdentity(QuartzMapping.jobKey definition.ScopeId definition.JobId)
            .WithDescription(definition.Handler)
            .UsingJobData(map)
            .StoreDurably(true)
            .Build()

    let cronTrigger (definition: JobDefinition) (expression: string) =
        let builder =
            TriggerBuilder
                .Create()
                .WithIdentity(QuartzMapping.triggerKey definition.ScopeId definition.JobId)
                .ForJob(QuartzMapping.jobKey definition.ScopeId definition.JobId)
                .WithSchedule(QuartzMapping.cronSchedule expression)

        match QuartzMapping.retryPolicy definition.RetryPolicy with
        | Some policy -> builder.WithRetryPolicy(policy).Build()
        | None -> builder.Build()

    /// Project one definition onto the Quartz scheduler. Never throws:
    /// the canonical write has already happened and is the truth, so a
    /// Quartz-side failure is a warning naming the job and the remedy
    /// (`Reproject`), not a failed `Save`.
    let project (definition: JobDefinition) = async {
        try
            let jobKey = QuartzMapping.jobKey definition.ScopeId definition.JobId
            let triggerKey = QuartzMapping.triggerKey definition.ScopeId definition.JobId

            match definition.Status with
            | Cancelled ->
                let! _ = await (scheduler.DeleteJob jobKey)
                ()
            | Disabled ->
                do! awaitVoid (scheduler.AddJob(jobDetail definition, AddJobOptions.Replacing))
                let! _ = await (scheduler.UnscheduleJob triggerKey)
                ()
            | Active ->
                let detail = jobDetail definition

                match definition.Trigger, definition.NextRunAt with
                | CronTrigger expression, Some _ ->
                    let! _ =
                        await (
                            scheduler.ScheduleJob(
                                detail,
                                cronTrigger definition expression,
                                ScheduleJobOptions.Replacing
                            )
                        )

                    ()
                | _ ->
                    // Event-driven and manual jobs have no schedule of
                    // their own — they fire through `TriggerJob` — so
                    // they get the durable detail and no trigger. A cron
                    // job with no `NextRunAt` has no further occurrence
                    // and is treated the same way.
                    do! awaitVoid (scheduler.AddJob(detail, AddJobOptions.Replacing))
                    let! _ = await (scheduler.UnscheduleJob triggerKey)
                    ()
        with ex ->
            logger.Warn
                $"[QuartzJobStore] event=projection_failed jobId={definition.JobId} scope=%s{definition.ScopeId} status=%A{definition.Status}: {ex.Message} — the definition is persisted and authoritative; call Reproject on this scope to restore the Quartz view."
    }

    /// Project ONE definition onto the Quartz scheduler — add or replace
    /// its job detail, and add, replace or remove its trigger to match
    /// the definition's status and schedule. Idempotent, and it never
    /// throws: a Quartz-side failure warns and leaves the canonical
    /// record untouched (it is the truth). `Save` and `Update` call this
    /// for you; the scheduler calls it directly when a manual fire finds
    /// no Quartz job to fire.
    member _.Project(definition: JobDefinition) : Async<unit> = project definition

    /// Re-derive the whole Quartz view of `scopeId` from the canonical
    /// store, and answer how many definitions were projected. The
    /// recovery lever for a projection that failed, for a Quartz
    /// in-memory store that lost its state to a restart, and for a store
    /// that was written through before this decorator was composed.
    member _.Reproject(scopeId: string) : Async<int> = async {
        let! jobs = inner.ListJobs scopeId

        for job in jobs do
            do! project job

        return jobs.Length
    }

    interface IJobStore with
        member _.Save definition = async {
            do! inner.Save definition
            do! project definition
        }

        member _.Update definition = async {
            do! inner.Update definition
            do! project definition
        }

        member _.Get(scopeId, jobId) = inner.Get(scopeId, jobId)

        member _.ListJobs scopeId = inner.ListJobs scopeId

        member _.FindByIdempotencyKey(scopeId, key, ttlSeconds, now) =
            inner.FindByIdempotencyKey(scopeId, key, ttlSeconds, now)

        member _.RecordRun run = inner.RecordRun run

        member _.GetRecentRuns(scopeId, jobId, count) =
            inner.GetRecentRuns(scopeId, jobId, count)

        member _.DueJobs(scopeId, now) = inner.DueJobs(scopeId, now)

        member _.AwaitingExternalRuns(scopeId, limit) =
            inner.AwaitingExternalRuns(scopeId, limit)

        member _.ListScopesWithJobs() = inner.ListScopesWithJobs()

[<RequireQualifiedAccess>]
module QuartzJobStore =
    /// Wrap the deployment's canonical `IJobStore` so every definition
    /// written through it is also projected onto `scheduler`. The
    /// `inner` store keeps every contract it already satisfied; this
    /// decorator adds the Quartz-side view and nothing else.
    let create (inner: IJobStore) (scheduler: IScheduler) (logger: ILogger) : QuartzJobStore =
        QuartzJobStore(inner, scheduler, logger)