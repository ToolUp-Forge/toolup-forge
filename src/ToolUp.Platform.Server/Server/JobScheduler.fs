module ToolUp.Platform.JobScheduler

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.Tracing

// ─── Constants ───────────────────────────────────────────────────

[<Literal>]
let JobsSourceModule = "_platform.jobs"

[<Literal>]
let private SystemUserId = "_system"

let private maxRunsPerJob = 50

// ─── Lifecycle event payloads ────────────────────────────────────
//
// Persisted to `IEventStore` under `SourceModule = "_platform.jobs"`.
// `FableJsonConverter` (mirrors `AuditLog.fs:34-43` and Webhook
// dispatcher) so the admin-UI can deserialise via `Fable.SimpleJson`
// without an extra converter.

type JobScheduledPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    Trigger: Trigger
    CreatedBy: string
    NextRunAt: DateTime option
}

type JobStartedPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    TriggerSource: TriggerSource
}

type JobCompletedPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    DurationMs: int64
}

type JobFailedPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    Error: string
    DurationMs: int64
}

type JobDeadLetteredPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    Error: string
    Attempts: int
}

/// Phase 9b.A — payload for the `JobSchedulerTickMissed` audit event.
/// Emitted under `_platform` scope (the scheduler is a deployment-wide
/// subsystem; the missed boundary is not per-team) when the tick loop
/// detects `drift > 60s` between the expected wall-clock minute
/// boundary and `DateTime.UtcNow` on wake-up. `MissedTickCount` is the
/// number of distinct minute boundaries the pause covered;
/// `JobsSkipped` lists the cron-triggered jobs whose intermediate
/// firings were collapsed into the catch-up tick (one fire on resume
/// vs the N fires a non-paused scheduler would have produced).
type JobSchedulerTickMissedPayload = {
    /// First boundary the pause was detected against (the
    /// scheduler-computed expected wake-up time). Identifies which
    /// minute the operator should correlate against deploy logs / GC
    /// traces.
    ExpectedTickAt: DateTime
    /// When the scheduler actually woke (`DateTime.UtcNow` immediately
    /// before `runTick`).
    ObservedTickAt: DateTime
    /// `ObservedTickAt - ExpectedTickAt`, milliseconds.
    DriftMs: int64
    /// Number of distinct minute boundaries inside the pause window.
    MissedTickCount: int
    /// Cron-triggered jobs in scopes whose dispatch was collapsed.
    /// Each entry is a job whose `NextRunAt` predates the pause and
    /// will fire ONCE on the catch-up tick instead of the N times a
    /// non-paused scheduler would have produced over the missed
    /// boundaries. `OnEvent` and `Manual` jobs are not listed — their
    /// dispatch is triggered by event-write or admin action, not by
    /// the cron tick, so the pause is irrelevant.
    JobsSkipped: JobId list
}

// ─── JSON helper ─────────────────────────────────────────────────

module private Json =
    let private settings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let serialize (value: 'T) : string =
        JsonConvert.SerializeObject(value, settings)

// ─── InProcessJobScheduler ───────────────────────────────────────
//
// Single-process default `IJobScheduler` + `BackgroundService`. Ticks
// once per minute (aligned to the wall clock so 12:00:00.0 is the
// boundary, not 12:00:00.7-startup-skew). On each tick, the scheduler:
//
//   1. Calls `IJobStore.ListScopesWithJobs` to find every scope with
//      at least one persisted job.
//   2. For each scope, calls `IJobStore.DueJobs(scope, now)` to fetch
//      `Active` jobs whose `NextRunAt <= now`.
//   3. Dispatches each due job concurrently via `Async.Start` —
//      independent jobs must not head-of-line-block each other (the
//      retry loop's `Async.Sleep delay` would otherwise stall the
//      scheduler thread).
//
// **Single-instance limitation.** Two silos running this scheduler
// against the same `IBlobStorage`-backed `IJobStore` would each fire
// the same due jobs at the same minute. This is enforced at preflight
// by `JobSchedulerInstanceValidator` (Phase 6l.F): a deployment with
// `ServerConfig.ReplicaCount > 1` and this scheduler fails config
// validation unless `ServerConfig.AcceptInProcessSchedulerInMultiInstance`
// is set. A distributed scheduler companion (Akka, Orleans Reminders,
// Hangfire) is the migration path for multi-silo deployments.
//
// **Dispatch concurrency.** Each in-flight job acquires a per-`JobId`
// `SemaphoreSlim` so concurrent ticks for the same job (e.g., a tick
// that fires while the previous run is still going) cannot interleave.
// The semaphore is local to this scheduler instance — distributed
// implementations rely on `IBlobStorage.UploadIfMatch` / their own
// leasing mechanism.

type InProcessJobScheduler
    (
        store: IJobStore,
        eventStore: IEventStore,
        notificationChannel: INotificationChannel,
        config: ServerConfig,
        logger: ILogger,
        activitySink: IActivitySink
    ) =
    inherit BackgroundService()

    let handlers = ConcurrentDictionary<string, IJobHandler>()

    /// Per-`JobId` mutex. Acquired before any read-modify-write cycle
    /// against the store. Lazily created — entries persist for the
    /// scheduler's lifetime; size is bounded by the active job count.
    let jobLocks = ConcurrentDictionary<JobId, SemaphoreSlim>()

    let lockFor (jobId: JobId) =
        jobLocks.GetOrAdd(jobId, fun _ -> new SemaphoreSlim(1, 1))

    // ─── Phase 9b.A — missed-tick telemetry ──────────────────────
    //
    // `missedTickWindow` is the rolling 60-minute log of detected
    // missed-tick timestamps (the boundary the scheduler woke late
    // against, NOT the wall-clock at detection — the count reflects
    // affected minute boundaries, not the number of catch-up runs).
    // Pruned on every read and on every detection so the snapshot is
    // accurate without a background sweeper. Guarded by
    // `telemetryLock` because both producer (tick loop) and consumer
    // (`IJobSchedulerTelemetry.Snapshot`) read it.
    let missedTickWindow = System.Collections.Generic.Queue<DateTime>()
    let telemetryLock = obj ()
    let mutable lastDriftMs: int64 option = None
    let mutable lastTickMissedAt: DateTime option = None

    /// Drift threshold: anything beyond one full minute past the
    /// expected boundary counts as a missed tick. Equal to the loop's
    /// own tick interval — one missed boundary is the minimum
    /// detectable signal.
    let driftThresholdMs = 60_000L

    let pruneMissedTickWindow (now: DateTime) =
        let cutoff = now.AddMinutes(-60.0)

        while missedTickWindow.Count > 0 && missedTickWindow.Peek() < cutoff do
            missedTickWindow.Dequeue() |> ignore

    let recordMissedTick (expectedTick: DateTime) (observedTick: DateTime) (drift: TimeSpan) =
        lock telemetryLock (fun () ->
            pruneMissedTickWindow observedTick

            // Count one entry per missed minute boundary so the
            // 60-min counter reflects affected boundaries, not
            // catch-up runs (a 5-minute pause is "5 missed
            // boundaries" not "1 missed tick").
            let missedCount = max 1 (int (drift.TotalMinutes))

            let mutable boundary = expectedTick

            for _ in 1..missedCount do
                missedTickWindow.Enqueue boundary
                boundary <- boundary.AddMinutes 1.0

            lastDriftMs <- Some(int64 drift.TotalMilliseconds)
            lastTickMissedAt <- Some observedTick)

    // ─── AccessContext synthesis for system-driven runs ──────────

    /// Synthesise an `AccessContext` for a job's execution. The
    /// scheduling user is NOT used — cron jobs run when no user is
    /// online, so binding the run to the scheduling user's
    /// permissions would either let a deleted user keep firing
    /// scheduled work (security hole) or prevent an admin-deleted
    /// user's analytics from completing (correctness hole). System-
    /// synthesised contexts are unrestricted at the module-permission
    /// layer; handlers that need a specific principal capture it in
    /// their payload and re-resolve.
    let buildSystemContext (scopeId: string) : AccessContext =
        // The run's subject shape derives from the deployment's
        // surfaces, not a caller (there is none). Team-scoped
        // deployments bind the run to the job's scope as a team;
        // auth-required deployments use a plain authenticated system
        // principal; anonymous-only deployments use a session subject.
        // `AccessContext` stays unrestricted regardless — the Subject
        // only carries scope identity here, not module authority.
        let subject =
            if DeploymentConfig.hasTeamScope config then
                TeamMember(SystemUserId, scopeId)
            elif DeploymentConfig.requiresAnyAuth config then
                AuthenticatedUser SystemUserId
            else
                AnonymousSession SystemUserId

        AccessContext.unrestricted subject

    // ─── Lifecycle event emission ────────────────────────────────

    /// Write a `_platform.jobs` event to `IEventStore`. Catches and
    /// logs to ensure event-emission failures never fail the primary
    /// dispatch — same idiom as `AuditLog.Record` /
    /// `WebhookDispatcher.emitAudit`.
    let emitEvent (scopeId: string) (eventType: string) (payload: 'T) = async {
        try
            let evt = Events.create scopeId JobsSourceModule eventType (Json.serialize payload)
            do! eventStore.Write evt
        with ex ->
            logger.Warn
                $"[JobScheduler] event=write_failed eventType={eventType} payloadType={typeof<'T>.Name} scope=%s{scopeId}: {ex.Message}"
    }

    /// Publish a user-visible `SystemMessage` notification on
    /// dead-letter. Failures are swallowed — notification is best-
    /// effort.
    let notifyDeadLetter (scopeId: string) (job: JobDefinition) (error: string) = async {
        try
            let text =
                $"Job {job.Handler} failed permanently after {job.RetryPolicy.MaxAttempts} attempts: {error}"

            let notification = SystemMessage(SystemMessageLevel.Warning, text)
            do! notificationChannel.Publish(scopeId, notification)
        with ex ->
            logger.Warn
                $"[JobScheduler] event=dead_letter_notify_failed jobId={job.JobId} handler={job.Handler} maxAttempts={job.RetryPolicy.MaxAttempts} scope=%s{scopeId}: {ex.Message}"
    }

    // ─── Compute NextRunAt from Trigger ──────────────────────────

    let computeNextRunAt (trigger: Trigger) (after: DateTime) : DateTime option =
        match trigger with
        | CronTrigger expr ->
            match CronExpression.tryParse expr with
            | Ok cron -> CronExpression.nextRunAfter cron after
            | Error _ -> None
        | OnEvent _
        | Manual -> None

    // ─── Single dispatch attempt + retry loop ────────────────────

    /// Run one full retry loop for a job. Records every attempt to
    /// the run history; on dead-letter emits the audit + notification;
    /// updates the persisted definition's terminal state once the
    /// loop completes. Mirrors `WebhookDispatcher.runDelivery` in
    /// shape but operates against `IJobStore` rather than the
    /// delivery log.
    let dispatchOne (job: JobDefinition) (source: TriggerSource) (scheduledAt: DateTime) = async {
        let mutexLock = lockFor job.JobId
        do! mutexLock.WaitAsync() |> Async.AwaitTask

        // Phase 9l — start a child activity per dispatch so the
        // OTel span tree links job run → audit emission →
        // notification publish back to whatever request scheduled
        // the job (OnEvent / Manual triggers inherit
        // `Activity.Current` from the request that wrote the
        // event; ScheduledByCron has no parent and starts a fresh
        // trace). The activity covers the lock acquisition above
        // through the retry-loop exit below so its duration is the
        // observable wall-clock time of the dispatch, not just one
        // attempt.
        let dispatchActivityOpt =
            activitySink.StartActivity(sprintf "job %s" job.Handler, None)

        try
            // Re-read inside the lock to avoid acting on a stale
            // snapshot if another tick mutated the job between the
            // DueJobs() return and our acquisition.
            let! current = store.Get(job.ScopeId, job.JobId)

            match current with
            | None -> ()
            | Some j when j.Status <> Active -> ()
            | Some current ->

                match handlers.TryGetValue current.Handler with
                | false, _ ->
                    logger.Warn(
                        sprintf
                            "[JobScheduler] handler '%s' not registered (scope=%s job=%A) — skipping"
                            current.Handler
                            current.ScopeId
                            current.JobId
                    )
                | true, handler ->

                    let mutable attempt = 1
                    let mutable terminate = false
                    let mutable lastError: string option = None
                    let mutable lastStatus: JobRunStatus = Pending

                    while attempt <= current.RetryPolicy.MaxAttempts && not terminate do
                        let delay = JobRetryPolicy.delayFor current.RetryPolicy attempt

                        if delay > TimeSpan.Zero then
                            do! Async.Sleep delay

                        let runId = Guid.NewGuid()
                        let runningAt = DateTime.UtcNow
                        let stopwatch = Stopwatch.StartNew()

                        let ctx: JobContext = {
                            JobId = current.JobId
                            ScopeId = current.ScopeId
                            AccessContext = buildSystemContext current.ScopeId
                            Attempt = attempt
                            Trigger = current.Trigger
                            TriggerSource = source
                            ScheduledAt = scheduledAt
                            RunningAt = runningAt
                            Payload = current.Payload
                            DeadLetterDestination = current.RetryPolicy.DeadLetterDestination
                        }

                        // Mark Pending → Running with a single Running row;
                        // the run-history table doesn't need a separate
                        // Pending → Running transition for in-process
                        // scheduling. Distributed schedulers may want to
                        // emit Pending up front.
                        let runningRow: JobRun = {
                            RunId = runId
                            JobId = current.JobId
                            ScopeId = current.ScopeId
                            Attempt = attempt
                            StartedAt = runningAt
                            CompletedAt = None
                            Status = Running
                            Error = None
                            DurationMs = None
                        }

                        do! store.RecordRun runningRow

                        do!
                            emitEvent current.ScopeId "JobStarted" {
                                JobId = current.JobId
                                ScopeId = current.ScopeId
                                Handler = current.Handler
                                RunId = runId
                                Attempt = attempt
                                TriggerSource = source
                            }

                        let! result = async {
                            try
                                return! handler.Execute ctx
                            with ex ->
                                return TransientFailure ex.Message
                        }

                        stopwatch.Stop()
                        let elapsed = stopwatch.ElapsedMilliseconds
                        let completedAt = DateTime.UtcNow

                        let finalRow status error : JobRun = {
                            RunId = runId
                            JobId = current.JobId
                            ScopeId = current.ScopeId
                            Attempt = attempt
                            StartedAt = runningAt
                            CompletedAt = Some completedAt
                            Status = status
                            Error = error
                            DurationMs = Some elapsed
                        }

                        match result with
                        | Success ->
                            do! store.RecordRun(finalRow Succeeded None)

                            do!
                                emitEvent current.ScopeId "JobCompleted" {
                                    JobId = current.JobId
                                    ScopeId = current.ScopeId
                                    Handler = current.Handler
                                    RunId = runId
                                    Attempt = attempt
                                    DurationMs = elapsed
                                }

                            lastStatus <- Succeeded
                            terminate <- true
                        | TransientFailure error ->
                            do! store.RecordRun(finalRow Failed (Some error))

                            do!
                                emitEvent current.ScopeId "JobFailed" {
                                    JobId = current.JobId
                                    ScopeId = current.ScopeId
                                    Handler = current.Handler
                                    RunId = runId
                                    Attempt = attempt
                                    Error = error
                                    DurationMs = elapsed
                                }

                            lastError <- Some error
                            lastStatus <- Failed
                            attempt <- attempt + 1
                        | PermanentFailure error ->
                            do! store.RecordRun(finalRow DeadLettered (Some error))

                            do!
                                emitEvent current.ScopeId "JobDeadLettered" {
                                    JobId = current.JobId
                                    ScopeId = current.ScopeId
                                    Handler = current.Handler
                                    Error = error
                                    Attempts = attempt
                                }

                            do! notifyDeadLetter current.ScopeId current error
                            lastError <- Some error
                            lastStatus <- DeadLettered
                            terminate <- true

                    // Retries exhausted with the loop in `Failed` state
                    // (no Success, no PermanentFailure). The loop counter
                    // reached `MaxAttempts + 1` without any terminal flag —
                    // promote the last `Failed` to `DeadLettered`.
                    if lastStatus = Failed && not terminate then
                        let runId = Guid.NewGuid()

                        do!
                            store.RecordRun {
                                RunId = runId
                                JobId = current.JobId
                                ScopeId = current.ScopeId
                                Attempt = current.RetryPolicy.MaxAttempts
                                StartedAt = DateTime.UtcNow
                                CompletedAt = Some(DateTime.UtcNow)
                                Status = DeadLettered
                                Error = lastError
                                DurationMs = Some 0L
                            }

                        do!
                            emitEvent current.ScopeId "JobDeadLettered" {
                                JobId = current.JobId
                                ScopeId = current.ScopeId
                                Handler = current.Handler
                                Error = lastError |> Option.defaultValue "max attempts reached"
                                Attempts = current.RetryPolicy.MaxAttempts
                            }

                        do!
                            notifyDeadLetter
                                current.ScopeId
                                current
                                (lastError |> Option.defaultValue "max attempts reached")

                        lastStatus <- DeadLettered

                    // Update the persisted definition's terminal state.
                    let nextRunAt = computeNextRunAt current.Trigger DateTime.UtcNow

                    let updated = {
                        current with
                            NextRunAt = nextRunAt
                            LastRunAt = Some(DateTime.UtcNow)
                            LastRunStatus = Some lastStatus
                            LastRunError = lastError

                            ConsecutiveFailures =
                                match lastStatus with
                                | Succeeded -> 0
                                | DeadLettered -> current.ConsecutiveFailures + 1
                                | _ -> current.ConsecutiveFailures
                    }

                    do! store.Update updated
        finally
            mutexLock.Release() |> ignore
            dispatchActivityOpt |> Option.iter _.Dispose()
    }

    // ─── Status transitions ──────────────────────────────────────

    let setStatus (scopeId: string) (jobId: JobId) (status: JobStatus) = async {
        let mutexLock = lockFor jobId
        do! mutexLock.WaitAsync() |> Async.AwaitTask

        try
            match! store.Get(scopeId, jobId) with
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

                do! store.Update updated
        finally
            mutexLock.Release() |> ignore
    }

    // ─── Build + persist a fresh job ─────────────────────────────

    let createNewJob (registration: JobRegistration) : Async<Result<JobId, ScheduleError>> = async {
        let jobId = Guid.NewGuid()
        let now = DateTime.UtcNow
        let nextRunAt = computeNextRunAt registration.Trigger now

        let definition: JobDefinition = {
            JobId = jobId
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
            NextRunAt = nextRunAt
            LastRunAt = None
            LastRunStatus = None
            LastRunError = None
            ConsecutiveFailures = 0
            Tags = registration.Tags
        }

        try
            do! store.Save definition

            do!
                emitEvent registration.ScopeId "JobScheduled" {
                    JobId = jobId
                    ScopeId = registration.ScopeId
                    Handler = registration.Handler
                    Trigger = registration.Trigger
                    CreatedBy = registration.CreatedBy
                    NextRunAt = nextRunAt
                }

            return Ok jobId
        with ex ->
            return Error(ScheduleError.StorageFailure ex.Message)
    }

    // ─── Schedule validation chain ───────────────────────────────

    let validateRegistration (registration: JobRegistration) : Result<unit, ScheduleError> =
        // 1. Cron parse — applies only to CronTrigger
        let cronCheck =
            match registration.Trigger with
            | CronTrigger expr ->
                match CronExpression.tryParse expr with
                | Ok _ -> Ok()
                | Error reason -> Error(InvalidCron(expr, reason))
            | _ -> Ok()

        match cronCheck with
        | Error e -> Error e
        | Ok() ->

            // 2. Handler registered
            if not (handlers.ContainsKey registration.Handler) then
                Error(HandlerNotRegistered registration.Handler)
            // 3. Precision supported (in-process default supports Minute only)
            elif registration.Precision = Second then
                Error(PrecisionUnsupported(Second, [ Minute ]))
            else
                Ok()

    // ─── Tick loop ───────────────────────────────────────────────

    let runTick (now: DateTime) = async {
        let mutable currentScope = "<none>"

        try
            let! scopes = store.ListScopesWithJobs()

            for scope in scopes do
                currentScope <- scope
                let! due = store.DueJobs(scope, now)

                for job in due do
                    Async.Start(dispatchOne job ScheduledByCron now)
        with ex ->
            logger.Error($"[JobScheduler] event=tick_error tickAt={now:o} lastScope={currentScope}", Some ex)
    }

    // ─── Phase 9b.A — drift handler (audit emit + optional back-fill) ──
    //
    // Invoked from the tick loop after a missed-boundary detection.
    // Walks every scope to collect the cron-triggered jobs whose
    // intermediate firings the catch-up tick will collapse, emits one
    // `JobSchedulerTickMissed` audit under `_platform`, and (when the
    // operator has opted into back-fill) re-fires every Active
    // `OnEvent`-triggered job once across all scopes.
    //
    // Audit-emit failures are swallowed (`emitEvent` already does this).
    // Back-fill failures are logged but do not block the catch-up tick
    // itself — the primary cron run is the load-bearing operation.
    let handleDetectedDrift (expectedTick: DateTime) (observedTick: DateTime) (drift: TimeSpan) = async {
        let missedCount = max 1 (int (drift.TotalMinutes))

        try
            let! scopes = store.ListScopesWithJobs()

            let collectScope (scope: string) = async {
                try
                    let! jobs = store.ListJobs scope

                    let skipped =
                        jobs
                        |> List.choose (fun j ->
                            match j.Status, j.Trigger with
                            | Active, CronTrigger _ when
                                (match j.NextRunAt with
                                 | Some next -> next <= expectedTick
                                 | None -> false)
                                ->
                                Some j.JobId
                            | _ -> None)

                    let backfillCandidates =
                        if config.BackfillMissedTicks then
                            jobs
                            |> List.filter (fun j ->
                                match j.Status, j.Trigger with
                                | Active, OnEvent _ -> true
                                | _ -> false)
                        else
                            []

                    return skipped, backfillCandidates
                with ex ->
                    logger.Warn $"[JobScheduler] event=drift_scope_scan_failed scope={scope}: {ex.Message}"
                    return [], []
            }

            let! perScope = scopes |> List.map collectScope |> Async.Sequential

            let allSkipped = perScope |> Array.collect (fst >> List.toArray) |> Array.toList

            let allBackfill = perScope |> Array.collect (snd >> List.toArray) |> Array.toList

            do!
                emitEvent "_platform" "JobSchedulerTickMissed" {
                    ExpectedTickAt = expectedTick
                    ObservedTickAt = observedTick
                    DriftMs = int64 drift.TotalMilliseconds
                    MissedTickCount = missedCount
                    JobsSkipped = allSkipped
                }

            logger.Warn(
                sprintf
                    "[JobScheduler] event=tick_missed expectedTickAt=%s observedTickAt=%s driftMs=%d missedTickCount=%d jobsSkipped=%d"
                    (expectedTick.ToString "o")
                    (observedTick.ToString "o")
                    (int64 drift.TotalMilliseconds)
                    missedCount
                    allSkipped.Length
            )

            // Back-fill: re-fire each affected OnEvent job ONCE on
            // recovery. Synthetic event identifiers (empty Guid +
            // `_backfill` eventType) so handler observability shows
            // the dispatch came from drift recovery, not a real
            // upstream event. Cron jobs are intentionally NOT
            // back-filled — the catch-up tick above already covers
            // the single canonical fire.
            if config.BackfillMissedTicks && not allBackfill.IsEmpty then
                logger.Info(
                    sprintf
                        "[JobScheduler] event=backfill_dispatch count=%d expectedTickAt=%s"
                        allBackfill.Length
                        (expectedTick.ToString "o")
                )

                for job in allBackfill do
                    let source = ScheduledByEvent("_backfill", Guid.Empty)
                    Async.Start(dispatchOne job source observedTick)
        with ex ->
            logger.Error($"[JobScheduler] event=drift_handler_error driftMs={int64 drift.TotalMilliseconds}", Some ex)
    }

    // ─── BackgroundService.ExecuteAsync ──────────────────────────

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Align ticks to the wall clock — sleep until the start
            // of the next minute, then loop with `TimeSpan.FromMinutes 1.0`.
            // Without alignment, a 12:00:00.7 startup would tick at
            // 12:01:00.7, 12:02:00.7, ... — tolerable, but operators
            // staring at log timestamps trust aligned-to-minute more.
            while not stoppingToken.IsCancellationRequested do
                let now = DateTime.UtcNow

                let nextTick =
                    DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes 1.0

                let delay = nextTick - now

                try
                    if delay > TimeSpan.Zero then
                        do! Task.Delay(delay, stoppingToken)

                    // Phase 9b.A — drift detection. `nextTick` is the
                    // boundary we asked `Task.Delay` to wake us at; the
                    // observed `DateTime.UtcNow` after the await is what
                    // we actually woke at. A debugger pause, GC stall,
                    // CPU-throttled container, or VM hiccup can stretch
                    // the gap arbitrarily — anything beyond a full
                    // minute past the boundary means at least one cron
                    // boundary was missed. Record telemetry + audit
                    // BEFORE running the catch-up tick so observers see
                    // the miss even if `runTick` itself throws.
                    let observedTick = DateTime.UtcNow
                    let drift = observedTick - nextTick

                    if drift.TotalMilliseconds > float driftThresholdMs then
                        recordMissedTick nextTick observedTick drift
                        do! handleDetectedDrift nextTick observedTick drift |> Async.StartAsTask :> Task

                    do! runTick observedTick |> Async.StartAsTask :> Task
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error($"[JobScheduler] event=tick_wrapper_error nextTick={nextTick:o}", Some ex)
        }
        :> Task

    // ─── IJobScheduler ───────────────────────────────────────────

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.Schedule(registration) = async {
            match validateRegistration registration with
            | Error e -> return Error e
            | Ok() ->

                // Idempotency check before any persistence work.
                match registration.Idempotency with
                | Some k ->
                    let! existing =
                        store.FindByIdempotencyKey(registration.ScopeId, k.Key, k.TtlSeconds, DateTime.UtcNow)

                    match existing with
                    | Some jobId -> return Ok jobId
                    | None -> return! createNewJob registration
                | None -> return! createNewJob registration
        }

        member _.Cancel(scopeId, jobId) = setStatus scopeId jobId Cancelled

        member _.Disable(scopeId, jobId) = setStatus scopeId jobId Disabled

        member _.Enable(scopeId, jobId) = setStatus scopeId jobId Active

        member _.Get(scopeId, jobId) = store.Get(scopeId, jobId)

        member _.ListJobs(scopeId) = store.ListJobs scopeId

        member _.GetRecentRuns(scopeId, jobId, count) =
            store.GetRecentRuns(scopeId, jobId, min count maxRunsPerJob)

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match! store.Get(scopeId, jobId) with
            | None -> return Error $"Job %A{jobId} not found in scope %s{scopeId}"
            | Some job when job.Status = Cancelled -> return Error "Cannot trigger a cancelled job"
            | Some job ->
                Async.Start(dispatchOne job (ScheduledManually byUserId) (DateTime.UtcNow))

                return Ok()
        }

        member _.NotifyEventWritten(scopeId, eventType, _eventId) = async {
            let! jobs = store.ListJobs scopeId

            let matches =
                jobs
                |> List.filter (fun j ->
                    j.Status = Active
                    && match j.Trigger with
                       | OnEvent et -> et = eventType
                       | _ -> false)

            for job in matches do
                let evtId = _eventId
                Async.Start(dispatchOne job (ScheduledByEvent(eventType, evtId)) (DateTime.UtcNow))
        }

    // ─── Phase 9b.A — IJobSchedulerTelemetry ─────────────────────

    interface IJobSchedulerTelemetry with
        member _.Snapshot() =
            lock telemetryLock (fun () ->
                let now = DateTime.UtcNow
                pruneMissedTickWindow now

                {
                    TickMissedCount60Min = missedTickWindow.Count
                    LastDriftMs = lastDriftMs
                    LastTickMissedAt = lastTickMissedAt
                    GeneratedAt = now
                })

// ─── Convenience constructor ─────────────────────────────────────

let create
    (store: IJobStore)
    (eventStore: IEventStore)
    (notificationChannel: INotificationChannel)
    (config: ServerConfig)
    (logger: ILogger)
    (activitySink: IActivitySink)
    : InProcessJobScheduler =
    new InProcessJobScheduler(store, eventStore, notificationChannel, config, logger, activitySink)