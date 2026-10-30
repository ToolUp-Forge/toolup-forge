module ToolUp.Platform.JobScheduler

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Tracing

// ─── Constants ───────────────────────────────────────────────────

[<Literal>]
let JobsSourceModule = "_platform.jobs"

[<Literal>]
let private SystemUserId = "_system"

let private maxRunsPerJob = 50

// ─── catch-up scan tuning ────────────────────────────────────────
//
// `catchUpStartupOverlap`: the startup scan re-reads from the
// persisted cursor MINUS this margin. The persisted cursor lags the
// in-memory one by up to a tick (flush cadence), and a concurrent
// write with a slightly-older `OccurredAt` can still be un-notified
// when a newer write advances the cursor — the overlap absorbs both,
// at the price of re-dispatching a bounded window of already-fired
// triggers on every restart (at-least-once by design).
//
// `catchUpSettleWindow`: the periodic sweep ignores events younger
// than this. A write can be visible in `ReadAll` while its notify
// hook is still in flight; sweeping it would double-fire. Events the
// live hook genuinely dropped are picked up one sweep later.
let private catchUpStartupOverlap = TimeSpan.FromSeconds 30.0
let private catchUpSettleWindow = TimeSpan.FromSeconds 30.0

/// Sweep cadence in scheduler ticks (1 tick = 1 minute) — mirrors
/// `AuditReplicatorOptions.CatchUpSweepInterval`'s 5-minute default.
let private catchUpSweepEveryTicks = 5L

// ─── external hand-off reconciliation tuning ─────────────────────

/// Per-scope cap on runs reconciled in one tick
/// (`IJobStore.AwaitingExternalRuns`' `limit`). A scope with more
/// outstanding hand-offs than this gets the remainder on the next tick —
/// bounded work per tick, no run dropped, and no single saturated scope
/// starving another scope's reconciliation.
let private awaitingExternalBatchSize = 200

/// How many of a job's most recent runs the pre-dispatch guard inspects
/// to decide whether an external hand-off is still outstanding.
///
/// **One would strictly do, and the reason is worth stating because it
/// looks like an off-by-N.** While a run is `AwaitingExternal` the guard
/// itself blocks every fresh dispatch of that job, so no *newer* run row
/// can be written until reconciliation resolves it — the awaiting row is
/// necessarily the newest. Three is margin against a store whose
/// newest-first ordering ties on `StartedAt` (two attempts inside the
/// same tick), which costs two extra blob reads on a path that already
/// does one.
let private awaitingGuardRunLookback = 3

// ─── Scheduling and dispatch event payloads ──────────────────────
//
// Persisted to `IEventStore` under `SourceModule = "_platform.jobs"`, through
// the same `FableConverters` options as the run-ending payloads in
// `JobLifecycle`. The events that END a run are `JobLifecycle`'s.

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

/// Payload for the `JobSchedulerTickMissed` audit event.
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

/// Payload for the `JobTriggerCatchUp` operational event,
/// emitted under `_platform` after a catch-up pass (startup scan or
/// periodic sweep) that dispatched at least one missed trigger.
/// `EventsReplayed` counts events found past the persisted/settled
/// cursor; `TriggersDispatched` counts the job dispatches they
/// produced (one event can match several `OnEvent` jobs, or none).
type JobTriggerCatchUpPayload = {
    /// `true` for the startup recovery scan, `false` for the
    /// periodic sweep.
    Startup: bool
    ScopesScanned: int
    EventsReplayed: int
    TriggersDispatched: int
    ScanStartedAt: DateTime
}

// ─── A status transition that could not get the job ──────────────

/// The typed "busy" refusal of a status transition: the job's lease is held by
/// an in-flight dispatch (typically sleeping in its retry backoff) and was not
/// released within `LeasePolicy.TransitionCeiling`. Retryable: nothing was
/// written. `IJobScheduler.Cancel` / `Disable` / `Enable` return
/// `Async<unit>`, so the refusal is raised; the admin API (`JobApiHandler`)
/// renders it as a retryable `Error`. A caller outside the server tier sees
/// an `InvalidOperationException`.
type internal JobStatusBusyException(jobId: JobId, waited: TimeSpan) =
    inherit
        InvalidOperationException(
            $"Job %O{jobId} is busy: an in-flight dispatch holds its lease (waited %.0f{waited.TotalMilliseconds} ms). Nothing was changed; retry shortly."
        )

    member _.JobId = jobId

/// Where the dispatch loop is: the attempt it runs next, and the error the
/// attempt before it left (a run that succeeds after a retriable failure keeps
/// that error as its `LastRunError`).
type private LoopState = {
    Attempt: int
    LastError: string option
}

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
//   3. Dispatches each due job concurrently via `startDispatch` (an
//      `Async.Start` behind a logging exception boundary) — independent
//      jobs must not head-of-line-block each other (the retry loop's
//      `Async.Sleep delay` would otherwise stall the scheduler thread),
//      and a throw with no caller left to observe it must not become an
//      unhandled thread-pool exception that takes the host down.
//
// **Single-instance limitation.** Two silos running this scheduler
// against the same `IBlobStorage`-backed `IJobStore` would each fire
// the same due jobs at the same minute. This is enforced at preflight
// by `JobSchedulerInstanceValidator`: a deployment with
// `ServerConfig.ReplicaCount > 1` and this scheduler fails config
// validation unless `ServerConfig.AcceptInProcessSchedulerInMultiInstance`
// is set. A distributed scheduler companion (Akka, Orleans Reminders,
// Hangfire) is the migration path for multi-silo deployments.
//
// **Dispatch concurrency.** Each in-flight job takes a
// per-`JobId` lease on the injected `IDistributedLock` before any
// read-modify-write cycle, so concurrent ticks for the same job (e.g. a
// tick that fires while the previous run is still going) cannot
// interleave. This replaced a per-`JobId` `SemaphoreSlim`: under the
// in-process default the exclusion is identical (one table, one process),
// but a deployment that composes a store-backed lock now gets the same
// exclusion ACROSS instances for free — the seam is the whole point.
// Distributed *schedulers* still additionally rely on
// `IConditionalBlobStorage.UploadWithETag` / their own leasing for due-job selection;
// this lock covers the dispatch critical section, not tick election.
//
// **External hand-off.** A handler may return
// `JobResult.HandedOff handle` instead of a result. The scheduler then
// records the run as `AwaitingExternal` with the handle persisted,
// releases the dispatch lease immediately, and reconciles the handle on
// subsequent ticks (`reconcileAwaitingExternal`, run after due-job
// dispatch) until the backend reports a terminal outcome.
//
// The slot economics are the point. Before this, a handler wanting a GPU
// had to submit and then poll inside its own body, holding the dispatch
// lease for the whole remote duration — so an eight-hour training run
// held a lease for eight hours, and a restart lost the submission
// entirely because nothing durable recorded that remote work existed.
// Now the waiting is the scheduler's, the handle is in the store, and a
// restart resumes by polling it.
//
// **Zero cost when unused (GP 13).** `externalDispatcher` is optional.
// When it is absent — every deployment that composes no external-compute
// backend — reconciliation short-circuits before touching the store, so
// the tick does not gain a single extra listing. When it is present but
// nothing has handed off, it costs one empty prefix listing per scope.

type InProcessJobScheduler
    (
        store: IJobStore,
        eventStore: IEventStore,
        notificationChannel: INotificationChannel,
        config: ServerConfig,
        logger: ILogger,
        activitySink: IActivitySink,
        ?triggerWatermark: JobTriggerWatermark.JobTriggerWatermark,
        ?distributedLock: IDistributedLock,
        ?externalDispatcher: IExternalComputeDispatcher,
        ?externalHandleStore: IExternalHandleStore,
        ?progressSink: IJobProgressSink
    ) =
    inherit BackgroundService()

    /// Catch-up is live only when the deployment opted in
    /// AND compose supplied the shared watermark (the two arrive
    /// together via `ComposeJobs.registerJobScheduler`; the guard
    /// keeps a hand-constructed scheduler honest).
    let catchUpEnabled = config.EventTriggerCatchUp && triggerWatermark.IsSome

    let handlers = ConcurrentDictionary<string, IJobHandler>()

    /// Every dispatch in flight. `StopAsync` cancels the set
    /// (each handler observes it through its async context) and then waits
    /// for it within the host's shutdown window.
    let dispatches = Background.DrainSet()

    /// The carrier a typed `Schedule` stamps a job's scope with
    /// and a dispatch redeems it through. Composition binds the
    /// deployment's (over its key ring, so a restart re-mints); until then
    /// this scheduler's own, so a hand-constructed one re-mints within its
    /// lifetime and runs a restarted job anonymous.
    let mutable scopeCarrier = ScopeCarrier.ephemeral ()

    /// The progress sink, when the deployment wants progress.
    ///
    /// **Derived from config rather than threaded through a new factory
    /// arity**, and that is a deliberate departure from how the external
    /// dispatcher and the handle store arrive. Those are *companion
    /// instances* the deployment composes and the scheduler cannot build.
    /// The fan-out sink is different: it needs only the channel, the event
    /// store and the logger — all three of which this constructor already
    /// holds — so a factory parameter would buy nothing except a
    /// combinatorial explosion of arities (`create` × catch-up × external ×
    /// callback × progress). The optional parameter stays as the override
    /// seam for a test or a distributed replacement.
    ///
    /// `None` when `JobProgress = NoJobProgress` (the default): handlers get
    /// `JobProgressReporter.noOp`, the reconciliation poll skips its
    /// checkpoint emission, and not one notification or event is written
    /// (GP 13).
    let progressSink: IJobProgressSink option =
        match progressSink with
        | Some sink -> Some sink
        | None ->
            match config.JobProgress with
            | NoJobProgress -> None
            | EnabledJobProgress ->
                // No `handlerFor` resolver is supplied: mapping a `JobId` to
                // its registered handler name needs a store read, and this
                // would run on the publish path of every checkpoint — a
                // blob read per progress frame to populate a label. Payloads
                // therefore carry `Handler = None` rather than a value
                // bought at that price; a consumer that wants the label
                // joins on `JobId` against the job it already has.
                Some(FanOutJobProgressSink(notificationChannel, eventStore, logger) :> IJobProgressSink)

    /// The lease primitive backing the per-`JobId` dispatch
    /// mutex. Defaults to the process-wide in-process lock (the same
    /// instance `compose` registers in DI), so a hand-constructed
    /// scheduler and a composed one contend on the same table; a
    /// deployment wanting cross-instance exclusion passes a store-backed
    /// companion.
    let jobLock = defaultArg distributedLock InProcessDistributedLock.shared

    /// Lock id for a job's dispatch critical section. Namespaced so it
    /// cannot collide with another subsystem's ids in a shared store.
    let jobLockId (jobId: JobId) = sprintf "toolup:job-dispatch:%O" jobId

    /// The lease every hold on a job's dispatch lock is taken for. The
    /// dispatch hold is deliberately generous: it spans the WHOLE retry loop,
    /// backoff sleeps included, and a lease that lapsed mid-loop would admit a
    /// second dispatcher for the same job, the one failure the lock exists to
    /// prevent. Heartbeat renewal (`IDistributedLock.Renew` on a timer,
    /// letting that TTL drop to seconds) is the follow-on for deployments
    /// where a crashed dispatcher must be reclaimed promptly.
    let leases = JobLifecycle.LeasePolicy.defaults

    /// Release from a `finally`. Best-effort: a failed release costs only
    /// the lease's remaining TTL, so it is logged, never raised — the
    /// dispatch itself already succeeded by this point.
    let releaseJobLease (lease: Lease) =
        lease
        |> DistributedLock.releaseDetached
            (fun ex ->
                logger.Warn
                    $"[JobScheduler] event=lock_release_failed lockId=%s{lease.LockId} fence=%d{lease.FenceToken}: {ex.Message} — the lease will expire via its TTL")
            jobLock

    // ─── missed-tick telemetry ───────────────────────────────────
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

    /// Write a `_platform.jobs` event to `IEventStore`. Catches and logs, so
    /// an event-emission failure never fails the primary dispatch — the same
    /// idiom as `AuditLog.Record` / `WebhookDispatcher.emitAudit`.
    let writeEvent (scopeId: string) (eventType: string) (payload: string) (payloadType: string) = async {
        try
            do! eventStore.Write(Events.create scopeId JobsSourceModule eventType payload)
        with ex ->
            logger.Warn
                $"[JobScheduler] event=write_failed eventType={eventType} payloadType={payloadType} scope=%s{scopeId}: {ex.Message}"
    }

    let emitEvent (scopeId: string) (eventType: string) (payload: 'T) =
        writeEvent scopeId eventType (JobLifecycle.serialize payload) typeof<'T>.Name

    /// Publish the user-visible dead-letter `SystemMessage`. Best-effort:
    /// failures are logged and swallowed.
    let notifyDeadLetter (scopeId: string) (text: string) = async {
        try
            do! notificationChannel.Publish(scopeId, SystemMessage(SystemMessageLevel.Warning, text))
        with ex ->
            logger.Warn
                $"[JobScheduler] event=dead_letter_notify_failed scope=%s{scopeId} notification=%s{text}: {ex.Message}"
    }

    /// Where a run-ending transition's effects go.
    let effects: JobLifecycle.LifecycleEffects = {
        RecordRun = store.RecordRun
        Emit = fun scopeId e -> writeEvent scopeId e.EventType e.Payload (e.EventType + "Payload")
        Notify = notifyDeadLetter
        Update = store.Update
    }

    // ─── The dispatch loop ───────────────────────────────────────

    let handlerFor (name: string) : IJobHandler option =
        match handlers.TryGetValue name with
        | true, handler -> Some handler
        | false, _ -> None

    /// One log line for every reason a dispatch that won the lease ran
    /// nothing.
    let logSkip (job: JobDefinition) (scheduledAt: DateTime) (skip: JobLifecycle.Skip) =
        match skip with
        | JobLifecycle.Gone ->
            logger.Info
                $"[JobScheduler] event=dispatch_skipped_gone jobId=%O{job.JobId} scope=%s{job.ScopeId} — the job definition no longer exists"
        | JobLifecycle.Inactive status ->
            logger.Info
                $"[JobScheduler] event=dispatch_skipped_inactive jobId=%O{job.JobId} scope=%s{job.ScopeId} status=%A{status} — only an Active job is dispatched"
        | JobLifecycle.NotDue nextRunAt ->
            // Winning the lease says nothing about whether the occurrence this
            // tick was dispatched for still exists: a sibling that won an
            // earlier contest for the same due job has, by the time we hold
            // the lease, run it and advanced `NextRunAt` past our tick.
            logger.Info(
                sprintf
                    "[JobScheduler] event=cron_tick_skipped_not_due jobId=%O scope=%s scheduledAt=%s nextRunAt=%s — a sibling replica already advanced this job past the tick"
                    job.JobId
                    job.ScopeId
                    (scheduledAt.ToString "o")
                    (nextRunAt
                     |> Option.map (fun n -> n.ToString "o")
                     |> Option.defaultValue "<none>")
            )
        | JobLifecycle.Outstanding awaiting ->
            logger.Info(
                sprintf
                    "[JobScheduler] event=dispatch_skipped_awaiting_external jobId=%O runId=%O attempt=%d handle=%s — an external hand-off from this job is still outstanding; the reconciliation pass owns it"
                    awaiting.JobId
                    awaiting.RunId
                    awaiting.Attempt
                    (awaiting.ExternalHandle
                     |> Option.map (fun h -> h.HandleId.ToString())
                     |> Option.defaultValue "<missing>")
            )
        | JobLifecycle.HandlerMissing name ->
            logger.Warn(
                sprintf
                    "[JobScheduler] handler '%s' not registered (scope=%s job=%A) — skipping"
                    name
                    job.ScopeId
                    job.JobId
            )

    /// Register a handed-off run's handle and mint its callback credential:
    /// the gate between the durable awaiting ROW and the hand-off event.
    ///
    /// Ordering is load-bearing: the awaiting row is durable first, so a
    /// crash here leaves a run the poll loop still resolves; then the handle
    /// record, so a callback can route; then the credential hand-off, the one
    /// step whose failure costs nothing but latency. Skipped entirely when no
    /// handle store is composed (GP 11 / GP 13). Never refuses: a failed
    /// registration only means this run resolves by poll.
    let registerHandle (job: JobDefinition) (runId: Guid) (handle: ExternalHandle) : Async<bool> = async {
        match externalHandleStore with
        | None -> ()
        | Some handleStore ->
            let secret, secretHash = ExternalCallbackSecret.mint ()

            try
                do! handleStore.Register(handle, runId, secretHash)

                // Only a backend that DECLARED the capability is handed a
                // secret; one that did not is reconciled by polling.
                match externalDispatcher |> Option.bind _.Capabilities.Callback with
                | Some capable ->
                    try
                        do!
                            capable.AcceptCallbackCredential(
                                handle,
                                {
                                    HandleId = handle.HandleId
                                    Secret = secret
                                    CallbackPath = ExternalCallback.Route
                                }
                            )
                    with ex ->
                        // Swallowed by contract (see `AcceptCallbackCredential`):
                        // the work is accepted, the run is durable, the poll
                        // loop resolves it.
                        logger.Warn
                            $"[JobScheduler] event=callback_credential_refused jobId=%O{job.JobId} runId=%O{runId} handle=%O{handle.HandleId} backend=%s{handle.Backend}: {ex.Message} — the backend cannot call back; this run will resolve by poll"
                | _ -> ()
            with ex ->
                logger.Warn
                    $"[JobScheduler] event=handle_registration_failed jobId=%O{job.JobId} runId=%O{runId} handle=%O{handle.HandleId}: {ex.Message} — no completion callback can route to this run; it will resolve by poll"

        return true
    }

    /// Run one attempt of `job` and every retry after it, each ended by one
    /// `JobLifecycle.transition`. `state` is where the loop is: the attempt to
    /// run, and the error the previous attempt left.
    let rec runAttempts
        (handler: IJobHandler)
        (job: JobDefinition)
        (source: TriggerSource)
        (scheduledAt: DateTime)
        (state: LoopState)
        : Async<unit> =
        async {
            if state.Attempt > job.RetryPolicy.MaxAttempts then
                let! _ = JobLifecycle.apply effects JobLifecycle.ungated (JobLifecycle.unattempted job DateTime.UtcNow)
                ()
            else
                let delay = JobRetryPolicy.delayFor job.RetryPolicy state.Attempt

                if delay > TimeSpan.Zero then
                    do! Async.Sleep delay

                let runId = Guid.NewGuid()
                let runningAt = DateTime.UtcNow
                let stopwatch = Stopwatch.StartNew()

                let ctx: JobContext = {
                    JobId = job.JobId
                    ScopeId = job.ScopeId
                    // The carried mint: the scope the typed `Schedule`'s token
                    // carries, redeemed for this job, otherwise the anonymous
                    // scope.
                    Scope = CarriedJobScope.ofDefinition scopeCarrier job
                    AccessContext = buildSystemContext job.ScopeId
                    Attempt = state.Attempt
                    Trigger = job.Trigger
                    TriggerSource = source
                    ScheduledAt = scheduledAt
                    RunningAt = runningAt
                    Payload = job.Payload
                    DeadLetterDestination = job.RetryPolicy.DeadLetterDestination
                }

                // One `Running` row per attempt; in-process scheduling needs no
                // separate `Pending` row.
                let runningRow: JobRun = {
                    RunId = runId
                    JobId = job.JobId
                    ScopeId = job.ScopeId
                    Attempt = state.Attempt
                    StartedAt = runningAt
                    CompletedAt = None
                    Status = Running
                    Error = None
                    DurationMs = None
                    ExternalHandle = None
                }

                do! store.RecordRun runningRow

                do!
                    emitEvent job.ScopeId "JobStarted" {
                        JobId = job.JobId
                        ScopeId = job.ScopeId
                        Handler = job.Handler
                        RunId = runId
                        Attempt = state.Attempt
                        TriggerSource = source
                    }

                let! result = async {
                    // The attempt's progress reporter is ambient, so the handler
                    // reaches it as `ctx.Progress`. `use` is load-bearing: the
                    // scope must pop even when the handler throws, or a later
                    // dispatch on the same execution-context lineage could
                    // inherit a reporter bound to the WRONG job id, a
                    // cross-scope publish (GP 4).
                    use _progress =
                        JobProgressScope.push (JobProgressSink.reporterForOption progressSink job.JobId job.ScopeId)

                    try
                        return! handler.Execute ctx
                    with ex ->
                        return TransientFailure ex.Message
                }

                stopwatch.Stop()
                let outcome = JobLifecycle.RunOutcome.ofJobResult result

                let transition =
                    JobLifecycle.transition
                        (JobLifecycle.Loop(stopwatch.ElapsedMilliseconds, state.LastError))
                        (Some job)
                        runningRow
                        DateTime.UtcNow
                        outcome

                let gate =
                    match outcome with
                    | JobLifecycle.HandedOff handle -> registerHandle job runId handle
                    | _ -> JobLifecycle.ungated

                let! _ = JobLifecycle.apply effects gate transition

                let lastError =
                    match outcome with
                    | JobLifecycle.Retriable error -> Some error
                    | _ -> state.LastError

                match transition.Continue with
                | JobLifecycle.RetryAt next ->
                    return!
                        runAttempts handler job source scheduledAt {
                            Attempt = next
                            LastError = lastError
                        }
                | JobLifecycle.Exhausted ->
                    // The loop spent its attempts: promote the last failure to
                    // a dead letter on a row of its own.
                    let now = DateTime.UtcNow

                    let promotion: JobRun = {
                        runningRow with
                            RunId = Guid.NewGuid()
                            StartedAt = now
                    }

                    let exhausted =
                        JobLifecycle.transition
                            (JobLifecycle.Loop(0L, lastError))
                            (Some job)
                            promotion
                            now
                            JobLifecycle.RetriesExhausted

                    let! _ = JobLifecycle.apply effects JobLifecycle.ungated exhausted
                    ()
                | JobLifecycle.Stop
                | JobLifecycle.Await _ -> ()
        }

    /// Run one full retry loop for a job under `dispatchLease`, which
    /// `dispatchFrom` below has already won (by election for a cron tick, by
    /// queueing for every other source); the lease is released on every exit
    /// path here.
    ///
    /// `startAttempt` is where the retry counter begins: `1` for every
    /// ordinary dispatch, higher only when an external outcome continues a
    /// retriable failure at attempt N+1. That parameter is what stops attempts
    /// being double-counted: restarting at 1 would give a job whose external
    /// work failed retriably `MaxAttempts` fresh submissions on every external
    /// failure, `MaxAttempts²` in total, and it would never dead-letter.
    let dispatchHolding dispatchLease startAttempt (job: JobDefinition) (source: TriggerSource) scheduledAt = async {
        // A child activity per dispatch, so the OTel span tree links job run
        // → audit emission → notification publish back to whatever request
        // scheduled the job (a cron tick has no parent and starts a fresh
        // trace). It covers the whole dispatch, not one attempt.
        let dispatchActivityOpt =
            activitySink.StartActivity(sprintf "job %s" job.Handler, None)

        try
            // Re-read inside the lease, so nothing acts on a `DueJobs`
            // snapshot another tick has since moved on from.
            let! current = store.Get(job.ScopeId, job.JobId)

            // Submission idempotency, scheduler side: a run of this job may
            // still be `AwaitingExternal`, and re-entering the handler would
            // submit the same work a second time. Checked inside the lease,
            // where the check and the decision cannot be split by a concurrent
            // tick; the backend's half is `ExternalWorkSpec.Idempotency`.
            // Skipped for a continuation at attempt N+1, whose awaiting row an
            // external outcome has already rewritten.
            let! outstanding = async {
                match current, externalDispatcher with
                | Some c, Some _ when startAttempt = 1 ->
                    let! recent = store.GetRecentRuns(c.ScopeId, c.JobId, awaitingGuardRunLookback)
                    return recent |> List.tryFind (fun r -> r.Status = AwaitingExternal)
                | _ -> return None
            }

            match JobLifecycle.admit handlerFor source scheduledAt outstanding current with
            | Error skip -> logSkip job scheduledAt skip
            | Ok(current, handler) ->
                do!
                    runAttempts handler current source scheduledAt {
                        Attempt = max 1 startAttempt
                        LastError = None
                    }
        finally
            releaseJobLease dispatchLease
            dispatchActivityOpt |> Option.iter _.Dispose()
    }

    /// Take the job's dispatch lease, then run `dispatchHolding` under it.
    ///
    /// The cron tick is an ELECTION over the per-`JobId` lease. How the lease
    /// is taken depends on who is asking:
    ///
    /// - **`ScheduledByCron` — `TryAcquire`, and the loser SKIPS.** Every
    ///   replica's tick reads the same due job from shared state, so a
    ///   contended lease here means a sibling is already running this
    ///   occurrence. Queueing behind it (an `acquireBlocking`)
    ///   was the genuine double-run: the loser eventually won the lease,
    ///   re-read a job that was `Active` with nothing outstanding, and ran
    ///   it a second time. Returning immediately is the first of the two
    ///   skip conditions; the in-lease due-ness re-read in
    ///   `dispatchHolding` is the second, for the loser whose `TryAcquire`
    ///   lands only after the winner has released.
    /// - **Every other source — `acquireBlocking`, queue semantics.** A
    ///   manual trigger, an event trigger, a back-fill or a reconciliation
    ///   continuation is a request for THIS run, not for "the occurrence
    ///   the clock produced"; dropping it because a sibling dispatch was
    ///   in flight would lose the request.
    let dispatchFrom (startAttempt: int) (job: JobDefinition) (source: TriggerSource) (scheduledAt: DateTime) = async {
        let lockId = jobLockId job.JobId

        let! held = async {
            match source with
            | ScheduledByCron -> return! jobLock.TryAcquire(lockId, leases.Dispatch)
            | ScheduledByEvent _
            | ScheduledManually _ ->
                let! lease = DistributedLock.acquireBlocking jobLock lockId leases.Dispatch
                return Some lease
        }

        match held with
        | Some dispatchLease -> do! dispatchHolding dispatchLease startAttempt job source scheduledAt
        | None ->
            logger.Info(
                sprintf
                    "[JobScheduler] event=cron_tick_skipped_contended jobId=%O scope=%s scheduledAt=%s — another replica holds the dispatch lease for this occurrence"
                    job.JobId
                    job.ScopeId
                    (scheduledAt.ToString "o")
            )
    }

    /// A dispatch that begins its retry counter at attempt 1 — every caller
    /// except an external outcome's retry (the tick, the drift back-fill, the
    /// catch-up replay, `TriggerOnce`, `NotifyEventWritten`).
    let dispatchOne (job: JobDefinition) (source: TriggerSource) (scheduledAt: DateTime) =
        dispatchFrom 1 job source scheduledAt

    /// The exception boundary every fire-and-forget dispatch goes through,
    /// answering whether the dispatch started.
    ///
    /// `Async.Start` leaves no caller to observe a throw, so anything that
    /// escapes `dispatchFrom` (a throwing `IJobStore.RecordRun` is the
    /// observed instance) would surface as an unhandled thread-pool exception
    /// and take the host down rather than fail one job. Through
    /// `Background.tryStart` the dispatch is logged and swallowed instead,
    /// runs under `dispatches`' token (cancellation reaches `handler.Execute`
    /// through its async context) and is tracked until it ends, so
    /// `StopAsync` can wait for it. Once `StopAsync` has cancelled the set,
    /// nothing new starts and the answer is `false`.
    let tryStartDispatch (job: JobDefinition) (source: TriggerSource) (work: Async<unit>) : bool =
        Background.tryStart
            dispatches
            logger
            $"[JobScheduler] event=dispatch_unhandled jobId=%O{job.JobId} handler=%s{job.Handler} source=%A{source}"
            work

    /// `tryStartDispatch` for a caller with nobody to tell it was refused.
    let startDispatch (job: JobDefinition) (source: TriggerSource) (work: Async<unit>) =
        tryStartDispatch job source work |> ignore

    // ─── Status transitions ──────────────────────────────────────

    /// Poll `TryAcquire` at the shared interval until the lease is held or
    /// `ceiling` elapses — `acquireBlocking` with a bound.
    let tryAcquireWithin (ceiling: TimeSpan) (lockId: string) (ttl: TimeSpan) : Async<Lease option> = async {
        let deadline = DateTime.UtcNow + ceiling
        let mutable held: Lease option = None
        let mutable expired = false

        while held.IsNone && not expired do
            match! jobLock.TryAcquire(lockId, ttl) with
            | Some lease -> held <- Some lease
            | None ->
                if DateTime.UtcNow >= deadline then
                    expired <- true
                else
                    do! Async.Sleep DistributedLock.defaultPollInterval

        return held
    }

    /// A status transition under the job's lease, so it can never interleave
    /// with an in-flight dispatch's read-modify-write. A dispatch holds that
    /// lease across its whole retry loop, backoff sleeps included, so the wait
    /// is bounded: past `leases.TransitionCeiling` the transition is refused,
    /// typed and retryable, with nothing written.
    let setStatus (scopeId: string) (jobId: JobId) (status: JobStatus) = async {
        let! lease = tryAcquireWithin leases.TransitionCeiling (jobLockId jobId) leases.Transition

        match lease with
        | None -> return raise (JobStatusBusyException(jobId, leases.TransitionCeiling))
        | Some lease ->
            try
                do! SchedulerPolicy.setStatus store scopeId jobId status
            finally
                releaseJobLease lease
    }

    // ─── Schedule ────────────────────────────────────────────────

    /// The `JobScheduled` event a newly persisted job is announced with.
    let announce (definition: JobDefinition) =
        emitEvent definition.ScopeId "JobScheduled" {
            JobId = definition.JobId
            ScopeId = definition.ScopeId
            Handler = definition.Handler
            Trigger = definition.Trigger
            CreatedBy = definition.CreatedBy
            NextRunAt = definition.NextRunAt
        }

    /// Both `Schedule` overloads, once each has settled the registration's
    /// scope and how its tags are persisted.
    let scheduleValidated (tagsFor: JobId -> Result<Map<string, string>, string>) (registration: JobRegistration) =
        SchedulerPolicy.scheduleValidated store handlers.ContainsKey announce tagsFor registration

    // ─── the exactly-once terminal claim ─────────────────────────
    //
    // Ask the handle store whether THIS caller may resolve `handle`.
    //
    // `true` means proceed. Two distinct cases produce it, and keeping
    // them both is what stops this gate becoming a way to strand a run:
    //
    //   * No handle store composed: a deployment with no completion
    //     callback, or whose blob backend cannot do conditional writes.
    //     The gate is absent and resolution is ungated (GP 11).
    //   * The handle is not registered. A hand-off recorded before the
    //     store existed, or one registered in an
    //     `InMemoryExternalHandleStore` the process has since restarted.
    //     `MarkTerminal` answers `false` for an unknown handle — it has
    //     nothing to claim — and treating that as "somebody else won"
    //     would leave the run `AwaitingExternal` forever, polled every
    //     tick, never resolved. That is the precise failure this store
    //     exists to prevent, reached from the other side, so an unknown
    //     handle falls back to the ungated path rather than blocking.
    //
    // The read-back is not racy in practice: registration completes
    // strictly before the awaiting row can be reconciled (both happen
    // inside the same `HandedOff` arm, row first), so a handle being
    // reconciled is never mid-registration.
    let claimTerminal (handle: ExternalHandle) = async {
        match externalHandleStore with
        | None -> return true
        | Some handleStore ->
            let! claimed = handleStore.MarkTerminal handle.HandleId

            if claimed then
                return true
            else
                let! known = handleStore.Resolve handle.HandleId

                match known with
                | Some _ -> return false
                | None ->
                    logger.Warn
                        $"[JobScheduler] event=external_handle_unregistered handle=%O{handle.HandleId} backend=%s{handle.Backend} — no handle-store record, so the exactly-once gate cannot apply; resolving ungated (a completion callback for this handle would be refused)"

                    return true
    }

    // ─── The one terminal-drive path ─────────────────────────────
    //
    // The completion callback and the poll loop share this one path, and it
    // ends the run through `JobLifecycle.transition`, the function that ends
    // an in-process attempt too: "identical to an in-process failure" and
    // "identical whether it arrived by push or by poll" hold because there
    // is one function, not because two paths were written alike.
    //
    // **The `MarkTerminal` claim is what makes callback-vs-poll
    // exactly-once.** Both callers reach the claim through here, so there
    // is one gate rather than two that must agree, and every event and
    // definition write below it happens only for the caller that won. The
    // terminal ROW is the one write that precedes it: it is
    // idempotent, and written after the claim a failure between the two
    // stranded the run for good. `outcome` must be terminal — the
    // non-terminal poll case is handled by the caller, because "still
    // running" is a decision not to write at all rather than a way of
    // resolving.
    let applyExternalOutcome
        (job: JobDefinition)
        (run: JobRun)
        (handle: ExternalHandle)
        (outcome: ExternalOutcome)
        (now: DateTime)
        : Async<ExternalResolution> =
        async {
            match JobLifecycle.RunOutcome.ofExternal outcome with
            | None ->
                // Defensive: the wire contract refuses non-terminal statuses
                // and the poll loop branches before calling.
                return ExternalResolution.NoAwaitingRun
            | Some _ when run.ScopeId <> handle.ScopeId ->
                // GP 4. The handle's scope comes from the handle store's own
                // partition, the run's from the job store: two independent
                // sources, so a disagreement means one of them points where it
                // should not, and nothing is written until they agree.
                logger.Error(
                    $"[JobScheduler] event=external_scope_mismatch handle=%O{handle.HandleId} handleScope=%s{handle.ScopeId} runScope=%s{run.ScopeId} runId=%O{run.RunId} — refusing to resolve across scopes",
                    None
                )

                return ExternalResolution.ScopeMismatch(handle.ScopeId, run.ScopeId)
            | Some runOutcome ->
                let transition =
                    JobLifecycle.transition (JobLifecycle.External(handle, outcome)) (Some job) run now runOutcome

                // The terminal ROW is written before the claim: the write is
                // idempotent (same `RunId` + `StartedAt`, same blob), and
                // `MarkTerminal` is one-shot, so a failure between a claim and
                // its row would leave the run awaiting with its handle already
                // terminal, every later poll losing the claim, forever. Row
                // first, a failure leaves the handle claimable and the next
                // poll completes both; the claim still decides who announces
                // the outcome and folds the definition.
                let! claimed = JobLifecycle.apply effects (claimTerminal handle) transition

                if not claimed then
                    logger.Info(
                        sprintf
                            "[JobScheduler] event=external_already_resolved jobId=%O runId=%O handle=%O backend=%s outcome=%s — another caller (callback or poll) claimed this handle first; no-op"
                            run.JobId
                            run.RunId
                            handle.HandleId
                            handle.Backend
                            (ExternalOutcome.label outcome)
                    )

                    return ExternalResolution.AlreadyResolved
                else
                    match transition.Continue, transition.Row with
                    | JobLifecycle.RetryAt next, _ ->
                        logger.Info(
                            sprintf
                                "[JobScheduler] event=external_retry jobId=%O runId=%O handler=%s backend=%s attempt=%d nextAttempt=%d maxAttempts=%d"
                                run.JobId
                                run.RunId
                                job.Handler
                                handle.Backend
                                run.Attempt
                                next
                                job.RetryPolicy.MaxAttempts
                        )

                        // Continue the SAME retry sequence at the next attempt.
                        // From the poll path the dispatch blocks on this job's
                        // lease until the caller's `finally` releases it, which
                        // is queueing, not failure.
                        let retrySource = ScheduledByEvent("_external-retry", run.RunId)
                        startDispatch job retrySource (dispatchFrom next job retrySource now)
                        return ExternalResolution.Resolved "failed"
                    | _, Some row when row.Status = JobRunStatus.ExternallyCancelled ->
                        logger.Info(
                            sprintf
                                "[JobScheduler] event=external_cancelled jobId=%O runId=%O handler=%s backend=%s awaitedMs=%d"
                                run.JobId
                                run.RunId
                                job.Handler
                                handle.Backend
                                (row.DurationMs |> Option.defaultValue 0L)
                        )

                        return ExternalResolution.Resolved "externally-cancelled"
                    | _, Some row when row.Status = Succeeded -> return ExternalResolution.Resolved "succeeded"
                    | _ -> return ExternalResolution.Resolved "dead-lettered"
        }

    // ─── external hand-off reconciliation ────────────────────────
    //
    // The other half of `JobResult.HandedOff`. A run sitting in
    // `AwaitingExternal` is waiting on work this process is not doing,
    // and something has to ask the backend how it went — that is this
    // pass, run once per tick after due-job dispatch.
    //
    // **Terminal outcomes are mapped onto the EXISTING retry / dead-letter
    // machinery, not a parallel one**, because "identical to an in-process
    // failure" is the acceptance criterion and two code paths that must
    // stay identical do not:
    //
    //   Succeeded        → run row `Succeeded`,   `JobCompleted`
    //   Failed retriable → per `RetryPolicy`: continue the SAME retry
    //                      sequence at attempt+1 (`dispatchFrom`), or
    //                      dead-letter when the budget is spent
    //   Failed terminal  → `DeadLettered` immediately, exactly as
    //                      `JobResult.PermanentFailure` does — retries are
    //                      skipped, not exhausted
    //   Cancelled        → run row `ExternallyCancelled`; no notification,
    //                      no `ConsecutiveFailures` bump (a cancel is not
    //                      a failure)
    //   Pending/Running  → left awaiting, no store write at all
    //
    // **Two invariants this pass must not break, both easy to break by
    // accident:**
    //
    // 1. *Do not double-count attempts.* The retriable path continues the
    //    original retry sequence via `dispatchFrom (run.Attempt + 1)`. It
    //    does not start a fresh loop — see `dispatchFrom`'s note for what
    //    that would cost (`MaxAttempts²` submissions and a job that never
    //    dead-letters).
    // 2. *Do not re-advance `NextRunAt`.* The hand-off dispatch already
    //    advanced it when its loop exited. Recomputing it here would push
    //    a cron job's next fire forward a second time for the same firing,
    //    silently skipping an interval — so reconciliation writes run
    //    status, error and counters, and leaves the schedule alone.
    //
    // Each run is reconciled under the job's own dispatch lease, which
    // buys two things: a reconciliation can never interleave with a
    // dispatch's read-modify-write, and a deployment that composed a
    // store-backed `IDistributedLock` gets multi-instance double-poll
    // protection for free (the lock seam paying off again — two silos
    // reconciling the same handle would otherwise both write a terminal
    // row and both emit a completion).
    let reconcileAwaitingExternal (now: DateTime) = async {
        match externalDispatcher with
        | None ->
            // No backend composed. Nothing can ever be awaiting, so this
            // costs one match and no I/O (GP 13).
            ()
        | Some dispatcher ->
            let mutable currentScope = "<none>"

            try
                let! scopes = store.ListScopesWithJobs()

                for scope in scopes do
                    currentScope <- scope
                    let! awaiting = store.AwaitingExternalRuns(scope, awaitingExternalBatchSize)

                    for run in awaiting do
                        try
                            let! lease = DistributedLock.acquireBlocking jobLock (jobLockId run.JobId) leases.Resolve

                            try
                                // Re-verify under the lease. Between the
                                // batch query and this acquisition another
                                // instance (or a `Cancel`) may already have
                                // resolved the run; taking the query's word
                                // for it is how the same handle gets two
                                // terminal rows and two completion events.
                                let! recent = store.GetRecentRuns(scope, run.JobId, awaitingGuardRunLookback)

                                let stillAwaiting =
                                    recent
                                    |> List.tryFind (fun r -> r.RunId = run.RunId && r.Status = AwaitingExternal)

                                match stillAwaiting with
                                | None -> ()
                                | Some run ->

                                    let! definition = store.Get(scope, run.JobId)

                                    match definition with
                                    | None ->
                                        // The job definition was deleted while its
                                        // external work ran. The run must still
                                        // leave `AwaitingExternal`, or it is polled
                                        // on every tick forever.
                                        logger.Warn
                                            $"[JobScheduler] event=reconcile_orphan_run jobId=%O{run.JobId} runId=%O{run.RunId} scope=%s{scope} — job definition no longer exists; abandoning the awaiting run"

                                        let abandoned =
                                            JobLifecycle.transition
                                                JobLifecycle.Reconciliation
                                                None
                                                run
                                                now
                                                (JobLifecycle.Abandoned JobLifecycle.DefinitionGone)

                                        let! _ = JobLifecycle.apply effects JobLifecycle.ungated abandoned
                                        ()
                                    | Some job ->

                                        match run.ExternalHandle with
                                        | None ->
                                            // Structurally impossible via `HandedOff`
                                            // (the case carries the handle), so this is
                                            // hand-edited state or a store that lost the
                                            // field on a round-trip. Fail it loudly
                                            // rather than leave a run that can never be
                                            // polled and never complete.
                                            logger.Error(
                                                $"[JobScheduler] event=reconcile_handle_missing jobId=%O{run.JobId} runId=%O{run.RunId} handler=%s{job.Handler} — an AwaitingExternal run carries no ExternalHandle; dead-lettering (it cannot be polled)",
                                                None
                                            )

                                            let abandoned =
                                                JobLifecycle.transition
                                                    JobLifecycle.Reconciliation
                                                    (Some job)
                                                    run
                                                    now
                                                    (JobLifecycle.Abandoned JobLifecycle.HandleMissing)

                                            let! _ = JobLifecycle.apply effects JobLifecycle.ungated abandoned
                                            ()
                                        | Some handle ->

                                            let! outcome = dispatcher.Poll handle

                                            match outcome with
                                            | ExternalOutcome.Pending
                                            | ExternalOutcome.Running _ ->
                                                // Still going. Deliberately NO store
                                                // write: re-recording an unchanged row
                                                // every tick is pure blob churn.
                                                //
                                                // The progress goes to
                                                // `IJobProgressSink`, NOT to a
                                                // `JobRun` field. A field would
                                                // reintroduce exactly the per-tick blob
                                                // rewrite this arm exists to avoid, and
                                                // would make an additive change to a
                                                // persisted record for a value that is
                                                // interesting live and uninteresting
                                                // afterwards. Fractional progress now
                                                // goes to `IJobProgressSink` instead —
                                                // the notification channel for the live
                                                // bar, the event store only for the
                                                // durable/terminal subset.
                                                //
                                                // Also deliberately BEFORE the shared
                                                // terminal path: "still running" is a
                                                // decision not to write at all, so it
                                                // must not consume the handle's
                                                // one-shot terminal claim.
                                                match progressSink, outcome with
                                                | Some sink, ExternalOutcome.Running(Some fraction) ->
                                                    // An externally-run job gets a
                                                    // progress bar with no handler code
                                                    // at all — the backend reported a
                                                    // fraction, so the platform
                                                    // publishes it. `Durable = false`:
                                                    // a poll-driven checkpoint arrives
                                                    // once per tick forever and is not
                                                    // worth a blob each; the terminal
                                                    // outcome is already recorded by
                                                    // `applyExternalOutcome`.
                                                    do!
                                                        sink.Report(
                                                            run.JobId,
                                                            scope,
                                                            ProgressCheckpoint.createAt
                                                                (Some fraction)
                                                                $"external work in progress on %s{handle.Backend}"
                                                                (Some "external")
                                                                now
                                                        )
                                                | _ ->
                                                    // No sink composed, or a backend
                                                    // that reports `Running None` —
                                                    // which says "running, cannot
                                                    // estimate" and must NOT be turned
                                                    // into a fabricated fraction.
                                                    ()

                                                logger.Debug(
                                                    sprintf
                                                        "[JobScheduler] event=reconcile_still_awaiting jobId=%O runId=%O handler=%s backend=%s outcome=%s awaitedMs=%d"
                                                        run.JobId
                                                        run.RunId
                                                        job.Handler
                                                        handle.Backend
                                                        (ExternalOutcome.label outcome)
                                                        (int64 (now - run.StartedAt).TotalMilliseconds)
                                                )

                                            | terminalOutcome ->
                                                // The poll now goes
                                                // through the SAME gated path the
                                                // completion callback does, so
                                                // whichever arrives first resolves the
                                                // run and the other is a no-op.
                                                let! _ = applyExternalOutcome job run handle terminalOutcome now

                                                ()
                            finally
                                releaseJobLease lease
                        with ex ->
                            // One misbehaving handle (a companion that
                            // throws from `Poll`, a storage blip) must not
                            // abandon the rest of the batch — nor the rest
                            // of the tick.
                            logger.Error(
                                $"[JobScheduler] event=reconcile_run_error jobId=%O{run.JobId} runId=%O{run.RunId} scope=%s{scope}",
                                Some ex
                            )
            with ex ->
                logger.Error($"[JobScheduler] event=reconcile_error tickAt={now:o} lastScope={currentScope}", Some ex)
    }

    // ─── Tick loop ───────────────────────────────────────────────

    let runTick (now: DateTime) = async {
        let mutable currentScope = "<none>"

        try
            let! scopes = store.ListScopesWithJobs()

            for scope in scopes do
                currentScope <- scope
                let! due = store.DueJobs(scope, now)

                for job in due do
                    startDispatch job ScheduledByCron (dispatchOne job ScheduledByCron now)
        with ex ->
            logger.Error($"[JobScheduler] event=tick_error tickAt={now:o} lastScope={currentScope}", Some ex)

        // Reconcile external hand-offs AFTER due-job
        // dispatch, and awaited rather than `Async.Start`ed.
        //
        // The order matters: due dispatch is the latency-sensitive half
        // of a tick (a cron job's fire time is observable), so it goes
        // first and keeps its fire-and-forget shape. Reconciliation is
        // then awaited so two ticks cannot overlap on the same batch —
        // the tick loop is serial, and a reconciliation still running
        // when the next tick queries `AwaitingExternalRuns` would hand
        // the same runs out twice. The per-job lease would still refuse
        // the second write, but relying on the lease to paper over an
        // overlap we can simply not create is the wrong trade.
        //
        // It has its own error boundary, so a reconciliation failure
        // cannot take the tick's cron dispatch down with it.
        do! reconcileAwaitingExternal now
    }

    // ─── drift handler (audit emit + optional back-fill) ───────────────
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
                emitEvent ScopeKey.PlatformContainer "JobSchedulerTickMissed" {
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
                    startDispatch job source (dispatchOne job source observedTick)
        with ex ->
            logger.Error($"[JobScheduler] event=drift_handler_error driftMs={int64 drift.TotalMilliseconds}", Some ex)
    }

    // ─── event-trigger catch-up (startup scan + sweep) ──────────────
    //
    // Recovers `OnEvent` triggers the in-memory notify hook never
    // processed. The startup scan reads each scope's PERSISTED cursor
    // (minus `catchUpStartupOverlap`) and replays everything past it —
    // the crash-recovery path. The periodic sweep compares against the
    // IN-MEMORY cursor (advanced synchronously after every live
    // notify), so anything it finds past the cursor and older than
    // `catchUpSettleWindow` was genuinely dropped (compose-window
    // `None`-scheduler write, notify-path throw). Both paths replay
    // the actual events — real `eventType` + `eventId` in
    // `TriggerSource.ScheduledByEvent` — never a blind re-fire.
    //
    // At-least-once: the overlap window and the flush lag both
    // re-dispatch triggers that already fired; a crash between
    // `Async.Start` and the run's first `RecordRun` can still lose a
    // dispatch (residual, milliseconds-wide). Handlers opting in must
    // be re-entrant — the same bar `BackfillMissedTicks` sets.
    //
    // Cost note: each pass is `ReadAll` per scope (the `IEventStore`
    // surface has no read-since); bounded by the store's retention
    // policy. Startup + every 5th tick only, and only when opted in.
    let runCatchUpScan (startup: bool) = async {
        match triggerWatermark with
        | Some watermark when catchUpEnabled ->
            let scanStartedAt = DateTime.UtcNow
            let mutable scopesScanned = 0
            let mutable eventsReplayed = 0
            let mutable triggersDispatched = 0

            try
                let! scopes = eventStore.ListScopes()

                for scope in scopes do
                    // Scan base: persisted cursor minus overlap on
                    // startup; live in-memory cursor on sweep. `None`
                    // skips the scope (first enable, unreadable
                    // cursor, or a scope no write has touched yet).
                    let! scanBase = async {
                        if startup then
                            match! watermark.LoadPersisted scope with
                            | JobTriggerWatermark.Loaded cursor ->
                                watermark.Seed(scope, cursor, false)

                                let floor =
                                    if cursor.LastDispatchedAt > DateTime.MinValue + catchUpStartupOverlap then
                                        cursor.LastDispatchedAt - catchUpStartupOverlap
                                    else
                                        cursor.LastDispatchedAt

                                return Some { cursor with LastDispatchedAt = floor }
                            | JobTriggerWatermark.Missing ->
                                // First enable — seed to "now" (persisted
                                // at the next flush) and skip: history
                                // predating the feature already fired live
                                // (or deliberately never will); replaying
                                // it would storm every OnEvent handler.
                                watermark.Seed(scope, JobTriggerWatermark.JobTriggerCursor.at scanStartedAt, true)
                                return None
                            | JobTriggerWatermark.Unreadable err ->
                                // Storage failure ≠ first enable: do NOT
                                // seed (seeding would overwrite the real
                                // cursor at the next flush) — skip and
                                // surface loudly; the next restart retries.
                                logger.Error(
                                    $"[JobScheduler] event=catchup_cursor_unreadable scope=%s{scope}: {err} — scope skipped this pass; missed triggers stay unrecovered until the cursor reads cleanly",
                                    None
                                )

                                return None
                        else
                            return watermark.TryGet scope
                    }

                    match scanBase with
                    | None -> ()
                    | Some baseCursor ->
                        scopesScanned <- scopesScanned + 1
                        let! events = eventStore.ReadAll scope
                        let settleFloor = DateTime.UtcNow - catchUpSettleWindow

                        let pending =
                            events
                            |> List.filter (JobTriggerWatermark.JobTriggerCursor.isAfter baseCursor)
                            |> List.filter (fun e -> startup || e.OccurredAt <= settleFloor)
                            |> List.sortBy (fun e -> e.OccurredAt, e.Id)

                        if not pending.IsEmpty then
                            let! jobs = store.ListJobs scope

                            let onEventJobs =
                                jobs
                                |> List.choose (fun j ->
                                    match j.Status, j.Trigger with
                                    | Active, OnEvent et -> Some(et, j)
                                    | _ -> None)
                                |> List.groupBy fst
                                |> List.map (fun (et, pairs) -> et, pairs |> List.map snd)
                                |> Map.ofList

                            for evt in pending do
                                eventsReplayed <- eventsReplayed + 1

                                match Map.tryFind evt.EventType onEventJobs with
                                | Some matched ->
                                    for job in matched do
                                        triggersDispatched <- triggersDispatched + 1

                                        let catchUpSource = ScheduledByEvent(evt.EventType, evt.Id)

                                        startDispatch job catchUpSource (dispatchOne job catchUpSource evt.OccurredAt)
                                | None -> ()

                                // Advance whether or not anything matched —
                                // non-trigger events must not rescan forever.
                                watermark.Advance evt

                if triggersDispatched > 0 then
                    do!
                        emitEvent ScopeKey.PlatformContainer "JobTriggerCatchUp" {
                            Startup = startup
                            ScopesScanned = scopesScanned
                            EventsReplayed = eventsReplayed
                            TriggersDispatched = triggersDispatched
                            ScanStartedAt = scanStartedAt
                        }

                if startup || triggersDispatched > 0 then
                    logger.Info(
                        sprintf
                            "[JobScheduler] event=catchup_scan startup=%b scopes=%d eventsReplayed=%d triggersDispatched=%d"
                            startup
                            scopesScanned
                            eventsReplayed
                            triggersDispatched
                    )

                do! watermark.FlushDirty()
            with ex ->
                logger.Error($"[JobScheduler] event=catchup_scan_error startup=%b{startup}", Some ex)
        | _ -> ()
    }

    /// Flush dirty trigger cursors — per-tick and on shutdown. No-op
    /// unless catch-up is live.
    let flushTriggerCursors () = async {
        match triggerWatermark with
        | Some watermark when catchUpEnabled -> do! watermark.FlushDirty()
        | _ -> ()
    }

    // ─── BackgroundService.ExecuteAsync ──────────────────────────

    override _.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // Startup recovery scan BEFORE the first tick:
            // replay `OnEvent` triggers for events written past the
            // persisted cursor (crash window, compose-window drops).
            // Runs after compose has populated the scheduler cell, so
            // live traffic arriving mid-scan advances the in-memory
            // cursor monotonically alongside the replay.
            if catchUpEnabled then
                do! runCatchUpScan true |> Async.StartAsTask :> Task

            let mutable tickCount = 0L

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

                    // Drift detection. `nextTick` is the
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

                    // Flush dirty trigger cursors once per
                    // tick (the persistence cadence the startup overlap
                    // is sized against) and run the catch-up sweep every
                    // `catchUpSweepEveryTicks` ticks.
                    if catchUpEnabled then
                        do! flushTriggerCursors () |> Async.StartAsTask :> Task
                        tickCount <- tickCount + 1L

                        if tickCount % catchUpSweepEveryTicks = 0L then
                            do! runCatchUpScan false |> Async.StartAsTask :> Task
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error($"[JobScheduler] event=tick_wrapper_error nextTick={nextTick:o}", Some ex)

            // Best-effort final flush on graceful shutdown
            // so the persisted cursor is as fresh as possible (smaller
            // replay window on the next start). Failures only widen the
            // at-least-once window — never lose a trigger.
            if catchUpEnabled then
                try
                    do! flushTriggerCursors () |> Async.StartAsTask :> Task
                with ex ->
                    logger.Warn $"[JobScheduler] event=shutdown_cursor_flush_failed: {ex.Message}"
        }
        :> Task

    member private _.StopBase(cancellationToken: CancellationToken) = base.StopAsync cancellationToken

    /// Cancel, then drain. Every dispatch in flight observes
    /// cancellation through its async context (a handler awaiting a
    /// cancellable async ends at its next bind), the tick loop stops, and
    /// the stop then waits for the dispatches to end within the host's
    /// shutdown window — so none is still writing run history after the host
    /// has torn down what it writes through. A dispatch that outlives the
    /// window is reported, never waited on past it, and its run row stays
    /// where a crash would have left it. A dispatch cancelled inside the
    /// window does too: cancellation ends the dispatch async at the bind it
    /// lands on, so no terminal row is written for the abandoned attempt.
    override this.StopAsync(cancellationToken: CancellationToken) =
        task {
            dispatches.Cancel()
            do! this.StopBase cancellationToken
            let! drained = dispatches.WaitAsync cancellationToken

            if not drained then
                logger.Warn(
                    sprintf
                        "[JobScheduler] event=dispatches_undrained_at_shutdown count=%d — the host's shutdown window closed before these cancelled dispatches ended"
                        dispatches.Count
                )
        }
        :> Task

    /// Run one catch-up pass on demand (`startup = true`
    /// applies the persisted-cursor + overlap semantics of the boot
    /// scan; `false` the in-memory-cursor + settle-window semantics of
    /// the periodic sweep). No-op unless the deployment opted into
    /// `ServerConfig.EventTriggerCatchUp` and compose supplied the
    /// watermark. Public for operational tooling and tests — the
    /// hosted-service loop calls it on the cadence documented above.
    member _.RunCatchUpScan(startup: bool) : Async<unit> = runCatchUpScan startup

    /// Run one cron tick on demand, as the hosted-service loop
    /// would at the minute boundary `now`: dispatch every job
    /// `IJobStore.DueJobs` reports due at `now` (fire-and-forget, each
    /// under the cron election), then reconcile external hand-offs.
    /// Returns once the dispatches have been STARTED, not once they have
    /// run — exactly the loop's own contract.
    ///
    /// Public for operational tooling and for tests, for the same reason
    /// `RunCatchUpScan` and `ReconcileAwaitingExternal` are: the cadence
    /// lives in `ExecuteAsync`, which only ASP.NET Core hosting starts,
    /// and a two-replica election cannot be placed against a wall clock.
    /// Not a substitute for the hosted loop — it records no drift, and a
    /// caller supplying a `now` behind the wall clock re-dispatches
    /// nothing that has already been advanced past it.
    member _.RunTick(now: DateTime) : Async<unit> = runTick now

    /// Run one external-hand-off reconciliation pass on
    /// demand: poll every `AwaitingExternal` run's persisted handle and
    /// drive the terminal ones to `Succeeded` / `Failed` / `DeadLettered`
    /// / `ExternallyCancelled`. No-op when no
    /// `IExternalComputeDispatcher` was supplied.
    ///
    /// Public for operational tooling ("resolve the stuck hand-offs now",
    /// without waiting for the next minute boundary) and for tests, which
    /// need it because the cadence lives in the hosted-service loop that
    /// only ASP.NET Core hosting starts — the same reason
    /// `RunCatchUpScan` is public.
    ///
    /// Idempotent: a run already terminal is not re-resolved (the pass
    /// re-verifies under the job's lease), and `Poll` is contractually
    /// non-destructive, so calling this twice costs a second poll and
    /// changes nothing.
    member _.ReconcileAwaitingExternal() : Async<unit> =
        reconcileAwaitingExternal DateTime.UtcNow

    /// The progress sink this scheduler is using, or `None`
    /// when the deployment did not opt in.
    ///
    /// Exposed so `compose` can register the SAME instance in DI rather
    /// than constructing a second one. That matters and is not tidiness:
    /// the fan-out sink holds the per-job rate-limit state, so two
    /// instances would each keep their own window and a chatty handler
    /// would publish at twice the configured rate — the flood the
    /// coalescer exists to prevent, reintroduced by duplication.
    member _.ProgressSink: IJobProgressSink option = progressSink

    // ─── the carrier binding ─────────────────────────────────────

    interface IScopeCarrierBinding with
        member _.BindScopeCarrier(carrier) = scopeCarrier <- carrier

    // ─── re-issuing a job's carried scope ────────────────────────
    //
    // Under the same per-`JobId` lease as `setStatus` and a dispatch, so a
    // re-issue never interleaves with an in-flight run's write-back of the
    // definition (which would otherwise restore the old tags).

    interface IJobScopeReissue with
        member _.ReissueScope(scope, scopeId, jobId) = async {
            if not (CarriedJobScope.owns scope scopeId) then
                return Error(ScopeReissueError.ScopeDoesNotOwnJob(scope.ScopeId, scopeId))
            else
                let! lease = DistributedLock.acquireBlocking jobLock (jobLockId jobId) leases.Transition

                try
                    match! store.Get(scopeId, jobId) with
                    | None -> return Error(ScopeReissueError.JobNotFound(scopeId, jobId))
                    | Some job ->
                        match CarriedJobScope.reissue scopeCarrier scope job with
                        | Error e -> return Error e
                        | Ok updated ->
                            match! Async.Catch(store.Update updated) with
                            | Choice1Of2() -> return Ok()
                            | Choice2Of2 ex -> return Error(ScopeReissueError.Failed ex.Message)
                finally
                    releaseJobLease lease
        }

    // ─── IJobScheduler ───────────────────────────────────────────

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        // In-process registration is a synchronous dictionary write; the
        // async overload performs the same mutation and reports success.
        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        // The string overload strips the reserved provenance
        // tags, so a registration (which may have arrived over the wire)
        // can never claim a resolver-minted scope; the typed overload is
        // the one place they are stamped.
        member _.Schedule(registration: JobRegistration) =
            let stripped = CarriedJobScope.strip registration.Tags
            scheduleValidated (fun _ -> Ok stripped) registration

        // The typed overload seals the scope into a token for
        // the job id the scheduler assigns, through the bound carrier.
        member _.Schedule(scope: ResolvedScope, registration: JobRegistration) =
            let carrier = scopeCarrier

            scheduleValidated (fun jobId -> CarriedJobScope.stamp carrier scope jobId registration.Tags) {
                registration with
                    ScopeId = scope.ScopeId
            }

        member _.Cancel(scopeId, jobId) = setStatus scopeId jobId Cancelled

        member _.Disable(scopeId, jobId) = setStatus scopeId jobId Disabled

        member _.Enable(scopeId, jobId) = setStatus scopeId jobId Active

        member _.Get(scopeId, jobId) = store.Get(scopeId, jobId)

        member _.ListJobs(scopeId) = store.ListJobs scopeId

        member _.GetRecentRuns(scopeId, jobId, count) =
            store.GetRecentRuns(scopeId, jobId, min count maxRunsPerJob)

        member _.TriggerOnce(scopeId, jobId, byUserId) = async {
            match! SchedulerPolicy.triggerable store scopeId jobId with
            | Error refusal -> return Error refusal
            | Ok job ->
                let source = ScheduledManually byUserId

                // A stopping scheduler starts nothing; saying `Ok` here would
                // report a run that never happens.
                if tryStartDispatch job source (dispatchOne job source DateTime.UtcNow) then
                    return Ok()
                else
                    return Error TriggerError.SchedulerStopping
        }

        member _.NotifyEventWritten(scopeId, eventType, eventId) = async {
            let! matches = SchedulerPolicy.eventTriggered store scopeId eventType

            for job in matches do
                let source = ScheduledByEvent(eventType, eventId)
                startDispatch job source (dispatchOne job source DateTime.UtcNow)
        }

    // ─── IJobSchedulerTelemetry ──────────────────────────────────

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

    // ─── IExternalCompletionSink ─────────────────────────────────
    //
    // The push counterpart to `reconcileAwaitingExternal`. Same lease,
    // same re-verify-under-the-lease, same shared terminal-drive path —
    // the ONLY differences are that the outcome was handed to us instead
    // of polled, and that the run is located by `RunId` (from the handle
    // store) rather than swept out of the awaiting index.
    //
    // **Why the lease is taken here too.** Without it a callback landing
    // mid-tick interleaves with the reconciliation's read-modify-write
    // on the same job, and `MarkTerminal` alone does not prevent that:
    // the gate makes exactly one of them *proceed*, but the loser may
    // already be inside a `RecordRun`. The lease is what makes the
    // winner's writes atomic against a concurrent dispatch, exactly as
    // it does for the poll.
    interface IExternalCompletionSink with
        member _.ResolveExternal(handle: ExternalHandle, jobRunId: Guid, outcome: ExternalOutcome) = async {
            let now = DateTime.UtcNow

            // Locate the awaiting run BEFORE taking a lease, because the
            // lease is per-`JobId` and the callback carries none — it
            // carries a handle. Scoped by `handle.ScopeId`, which came
            // from the handle store's own partition and never from the
            // request (GP 4), so this query cannot be steered across
            // tenants by a caller.
            //
            // This unleased read is a *locator*, not a decision. Every
            // decision is re-taken under the lease below, exactly as the
            // reconciliation pass re-verifies after its own batch query.
            let! located = store.AwaitingExternalRuns(handle.ScopeId, awaitingExternalBatchSize)

            match located |> List.tryFind (fun r -> r.RunId = jobRunId) with
            | None ->
                logger.Info(
                    sprintf
                        "[JobScheduler] event=callback_no_awaiting_run handle=%O runId=%O scope=%s — no AwaitingExternal run matches; already resolved, or never awaiting"
                        handle.HandleId
                        jobRunId
                        handle.ScopeId
                )

                return ExternalResolution.NoAwaitingRun
            | Some located ->
                // The SAME lock id the dispatch and the reconciliation
                // pass use. A different id would leave the callback
                // interleaving with a dispatch's read-modify-write, which
                // is the failure the lease exists to prevent —
                // `MarkTerminal` decides who proceeds, the lease is what
                // makes the winner's writes atomic.
                let! lease = DistributedLock.acquireBlocking jobLock (jobLockId located.JobId) leases.Resolve

                try
                    // Re-verify under the lease. Between the locator read
                    // and this acquisition the reconciliation pass (or a
                    // `Cancel`) may already have resolved the run.
                    let! recent = store.GetRecentRuns(handle.ScopeId, located.JobId, awaitingGuardRunLookback)

                    match
                        recent
                        |> List.tryFind (fun r -> r.RunId = jobRunId && r.Status = AwaitingExternal)
                    with
                    | None ->
                        logger.Info(
                            sprintf
                                "[JobScheduler] event=callback_no_awaiting_run handle=%O runId=%O scope=%s — the run left AwaitingExternal before the lease was acquired"
                                handle.HandleId
                                jobRunId
                                handle.ScopeId
                        )

                        return ExternalResolution.NoAwaitingRun
                    | Some run ->
                        let! definition = store.Get(handle.ScopeId, run.JobId)

                        match definition with
                        | None ->
                            // The job definition was deleted while its
                            // external work ran. The poll path
                            // dead-letters this case; here we decline
                            // rather than duplicate that policy, so
                            // orphan handling keeps a single owner and
                            // the next tick applies it.
                            logger.Warn
                                $"[JobScheduler] event=callback_orphan_run handle=%O{handle.HandleId} jobId=%O{run.JobId} runId=%O{run.RunId} scope=%s{handle.ScopeId} — job definition no longer exists; leaving the run for the reconciliation pass to abandon"

                            return ExternalResolution.NoAwaitingRun
                        | Some job -> return! applyExternalOutcome job run handle outcome now
                finally
                    releaseJobLease lease
        }

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

/// `create` plus the shared trigger watermark. Compose
/// uses this arity when `ServerConfig.EventTriggerCatchUp = true`;
/// the same `JobTriggerWatermark` instance must also be handed to
/// `JobNotifyEventStore` so live notifies advance the cursor the
/// scheduler's scans compare against.
let createWithCatchUp
    (store: IJobStore)
    (eventStore: IEventStore)
    (notificationChannel: INotificationChannel)
    (config: ServerConfig)
    (logger: ILogger)
    (activitySink: IActivitySink)
    (triggerWatermark: JobTriggerWatermark.JobTriggerWatermark)
    : InProcessJobScheduler =
    new InProcessJobScheduler(
        store,
        eventStore,
        notificationChannel,
        config,
        logger,
        activitySink,
        triggerWatermark = triggerWatermark
    )

/// `create` / `createWithCatchUp` plus the deployment's
/// `IExternalComputeDispatcher`, enabling `JobResult.HandedOff` and the
/// per-tick reconciliation pass. `triggerWatermark` is `None` for a
/// deployment that did not opt into event-trigger catch-up, so this one
/// arity covers both shapes.
///
/// Compose calls this **only** when the deployment composed an actual
/// backend (`ExternalComputeMode.CustomExternalCompute`). Passing the
/// `NoExternalComputeDispatcher` here instead would be harmless in
/// behaviour — nothing can hand off when every `Submit` refuses — but it
/// would switch the reconciliation pass on for every deployment in
/// existence, buying each one a per-scope index listing per minute
/// forever to discover the empty set it will always discover (GP 13).
let createWithExternalCompute
    (store: IJobStore)
    (eventStore: IEventStore)
    (notificationChannel: INotificationChannel)
    (config: ServerConfig)
    (logger: ILogger)
    (activitySink: IActivitySink)
    (triggerWatermark: JobTriggerWatermark.JobTriggerWatermark option)
    (externalDispatcher: IExternalComputeDispatcher)
    : InProcessJobScheduler =
    match triggerWatermark with
    | Some watermark ->
        new InProcessJobScheduler(
            store,
            eventStore,
            notificationChannel,
            config,
            logger,
            activitySink,
            triggerWatermark = watermark,
            externalDispatcher = externalDispatcher
        )
    | None ->
        new InProcessJobScheduler(
            store,
            eventStore,
            notificationChannel,
            config,
            logger,
            activitySink,
            externalDispatcher = externalDispatcher
        )

/// `createWithExternalCompute` plus the handle store that
/// makes the completion-callback ingress work: handle-to-run routing, the
/// callback secret's hash, and the atomic `MarkTerminal` gate.
///
/// A **separate arity** rather than an extra parameter on
/// `createWithExternalCompute`, because that function is curried — adding
/// a parameter would break every existing call site for a capability they
/// do not use (GP 11). A scheduler built by the older factory carries no
/// handle store, mints no secrets, registers no handles, and reconciles by
/// polling alone.
let createWithExternalCallback
    (store: IJobStore)
    (eventStore: IEventStore)
    (notificationChannel: INotificationChannel)
    (config: ServerConfig)
    (logger: ILogger)
    (activitySink: IActivitySink)
    (triggerWatermark: JobTriggerWatermark.JobTriggerWatermark option)
    (externalDispatcher: IExternalComputeDispatcher)
    (externalHandleStore: IExternalHandleStore)
    : InProcessJobScheduler =
    match triggerWatermark with
    | Some watermark ->
        new InProcessJobScheduler(
            store,
            eventStore,
            notificationChannel,
            config,
            logger,
            activitySink,
            triggerWatermark = watermark,
            externalDispatcher = externalDispatcher,
            externalHandleStore = externalHandleStore
        )
    | None ->
        new InProcessJobScheduler(
            store,
            eventStore,
            notificationChannel,
            config,
            logger,
            activitySink,
            externalDispatcher = externalDispatcher,
            externalHandleStore = externalHandleStore
        )