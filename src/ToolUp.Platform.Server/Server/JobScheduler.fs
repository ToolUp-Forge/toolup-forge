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

// ─── Phase 598 — catch-up scan tuning ────────────────────────────
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

// ─── Phase 319 — external hand-off reconciliation tuning ─────────

/// Per-scope cap on runs reconciled in one tick
/// (`IJobStore.AwaitingExternalRuns`' `limit`). A scope with more
/// outstanding hand-offs than this gets the remainder on the next tick —
/// bounded work per tick, no run dropped, and no single saturated scope
/// starving another scope's reconciliation.
let private awaitingExternalBatchSize = 200

/// How many of a job's most recent runs the pre-dispatch guard inspects
/// to decide whether an external hand-off is still outstanding
/// (Phase 319.E).
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

// ─── Phase 319 — external hand-off event payloads ────────────────
//
// **The three in-process terminal events are NOT extended.** A run that
// completes externally emits exactly the `JobCompleted` /
// `JobFailed` / `JobDeadLettered` payload an in-process run emits,
// byte-for-byte — that is the acceptance criterion, and an admin UI or
// downstream consumer must not have to learn a second shape to count a
// completion. The external detail (which backend, which handle, what
// the backend said, where the result landed) therefore rides on its own
// additive event types rather than as new fields on the shared ones,
// which also means no historical event payload changes shape.

/// Phase 319 — emitted when a handler returns `JobResult.HandedOff` and
/// the run enters `AwaitingExternal`. The counterpart to `JobStarted`
/// for externally-run work: it marks the point the scheduler stopped
/// occupying a slot and started owning a handle.
type JobExternalHandedOffPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    /// `ExternalHandle.HandleId` — the platform-minted identity.
    HandleId: Guid
    /// `ExternalHandle.Backend` — which dispatcher accepted the work.
    Backend: string
    /// `ExternalHandle.NativeRef` — the backend's own opaque token,
    /// echoed for operator correlation and never parsed.
    NativeRef: string
    SubmittedAt: DateTime
}

/// Phase 319 — emitted on every reconciliation that moved a run out of
/// `AwaitingExternal`, alongside (not instead of) the standard terminal
/// event. Carries the external-side detail the shared payloads have no
/// field for: the outcome label, the backend's result reference on
/// success, and how long the run waited.
type JobExternalReconciledPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    HandleId: Guid
    Backend: string
    NativeRef: string
    /// `ExternalOutcome.label` — `"succeeded"` / `"failed"` /
    /// `"cancelled"`.
    Outcome: string
    /// The backend's opaque reference to the result, present only for
    /// `ExternalOutcome.Succeeded`. Echoed, never dereferenced.
    ResultRef: string option
    /// Backend-reported failure message, present only for
    /// `ExternalOutcome.Failed`.
    Error: string option
    /// Whether the backend called the failure retriable — recorded
    /// because it is the input to the retry-vs-dead-letter decision and
    /// an operator diagnosing a dead-letter needs to see which branch
    /// was taken and why.
    Retriable: bool option
    /// Wall-clock the run spent awaiting the backend, submit to
    /// reconciliation.
    AwaitedMs: int64
}

/// Phase 319 — emitted when a run is abandoned because its
/// `AwaitingExternal` row carries no handle. Structurally impossible
/// through `HandedOff` (the handle is what the case carries), so this
/// means hand-edited state or a store that dropped the field on a
/// round-trip. It gets its own event because the alternative — leaving
/// the run awaiting forever, un-pollable and un-completable — is the one
/// outcome an operator can neither see nor act on.
type JobExternalHandleMissingPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
}

// ─── Lifecycle event payloads ────────────────────────────────────
//
// Persisted to `IEventStore` under `SourceModule = "_platform.jobs"`.
// `FableConverters` (mirrors `AuditLog.fs:34-43` and Webhook
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

/// Phase 598 — payload for the `JobTriggerCatchUp` operational event,
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

// ─── JSON helper ─────────────────────────────────────────────────

module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

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
// **Dispatch concurrency (Phase 9i).** Each in-flight job takes a
// per-`JobId` lease on the injected `IDistributedLock` before any
// read-modify-write cycle, so concurrent ticks for the same job (e.g. a
// tick that fires while the previous run is still going) cannot
// interleave. This replaced a per-`JobId` `SemaphoreSlim`: under the
// in-process default the exclusion is identical (one table, one process),
// but a deployment that composes a store-backed lock now gets the same
// exclusion ACROSS instances for free — the seam is the whole point.
// Distributed *schedulers* still additionally rely on
// `IBlobStorage.UploadIfMatch` / their own leasing for due-job selection;
// this lock covers the dispatch critical section, not tick election.
//
// **External hand-off (Phase 319).** A handler may return
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

    /// Phase 598 — catch-up is live only when the deployment opted in
    /// AND compose supplied the shared watermark (the two arrive
    /// together via `ComposeJobs.registerJobScheduler`; the guard
    /// keeps a hand-constructed scheduler honest).
    let catchUpEnabled = config.EventTriggerCatchUp && triggerWatermark.IsSome

    let handlers = ConcurrentDictionary<string, IJobHandler>()

    /// Phase 321 — the progress sink, when the deployment wants progress.
    ///
    /// **Derived from config rather than threaded through a new factory
    /// arity**, and that is a deliberate departure from how Phase 319's
    /// dispatcher and Phase 320's handle store arrive. Those are *companion
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

    /// Phase 9i — the lease primitive backing the per-`JobId` dispatch
    /// mutex. Defaults to the process-wide in-process lock (the same
    /// instance `compose` registers in DI), so a hand-constructed
    /// scheduler and a composed one contend on the same table; a
    /// deployment wanting cross-instance exclusion passes a store-backed
    /// companion.
    let jobLock = defaultArg distributedLock InProcessDistributedLock.shared

    /// Lock id for a job's dispatch critical section. Namespaced so it
    /// cannot collide with another subsystem's ids in a shared store.
    let jobLockId (jobId: JobId) = sprintf "toolup:job-dispatch:%O" jobId

    /// Lease TTL for a dispatch hold. Deliberately generous: the critical
    /// section spans the WHOLE retry loop, including its backoff
    /// `Async.Sleep`s, and a lease that lapses mid-loop would admit a
    /// second dispatcher for the same job — the one failure mode the lock
    /// exists to prevent. The `SemaphoreSlim` this replaced had no
    /// expiry at all, so a long window is also the behaviour-preserving
    /// choice. Heartbeat renewal (`IDistributedLock.Renew` on a timer,
    /// letting the TTL drop to seconds) is the follow-on for deployments
    /// where a crashed dispatcher must be reclaimed promptly.
    let dispatchLeaseTtl = TimeSpan.FromHours 1.0

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
    ///
    /// Phase 319 — `startAttempt` is where the retry counter begins.
    /// Every ordinary dispatch passes `1` (via `dispatchOne` below); only
    /// external reconciliation passes a higher value, when a retriable
    /// backend failure on attempt N must continue at N+1.
    ///
    /// **That parameter is what stops attempts being double-counted.**
    /// The alternative — reconciliation calling a fresh `dispatchOne` —
    /// would restart the loop at attempt 1, so a job whose external work
    /// failed retriably would get `MaxAttempts` fresh submissions on every
    /// external failure, i.e. `MaxAttempts²` in total, and would never
    /// dead-letter as long as the backend kept failing retriably.
    let dispatchFrom (startAttempt: int) (job: JobDefinition) (source: TriggerSource) (scheduledAt: DateTime) = async {
        // Phase 9i — wait for the job's dispatch lease. `acquireBlocking`
        // preserves the `SemaphoreSlim.WaitAsync` semantics this replaced
        // (a concurrent tick for the same job queues rather than being
        // dropped); the primitive itself is fail-fast, so a distributed
        // scheduler that would rather skip a contended tick uses
        // `TryAcquire` directly.
        let! dispatchLease = DistributedLock.acquireBlocking jobLock (jobLockId job.JobId) dispatchLeaseTtl

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

            // Phase 319.E — submission idempotency, scheduler side.
            //
            // A run of this job may already be `AwaitingExternal`: the
            // handler submitted work to a backend and the backend has
            // not finished. Re-entering the handler now would call
            // `Submit` a SECOND time for work that is already running —
            // two GPU jobs, two bills, two results racing to reconcile
            // one run. The window is real and not exotic: a cron job
            // whose external work outlives its own interval fires again
            // while awaiting, and a restart mid-await re-dispatches
            // whatever the recovery path re-queues.
            //
            // So the guard is here, inside the job's dispatch lease,
            // where the check and the decision cannot be split by a
            // concurrent tick. It is the scheduler's half of the
            // contract; the dispatcher's half is
            // `ExternalWorkSpec.Idempotency`, which Phase 318 requires a
            // backend to honour by returning the existing handle. Two
            // independent lines of defence, because the scheduler cannot
            // see inside a handler (a handler that submits without an
            // idempotency key is only protected by this guard) and the
            // scheduler cannot see the backend's dedup table (a handler
            // re-dispatched after its run row was lost is only protected
            // by the key).
            //
            // Skipped for a reconciliation-driven continuation
            // (`startAttempt > 1`): reconciliation has, by then, already
            // rewritten the awaiting row to its terminal status, so
            // nothing is outstanding — and treating that continuation as
            // "outstanding" would abandon the retry the RetryPolicy
            // asked for.
            let! outstanding = async {
                match current, externalDispatcher with
                | Some c, Some _ when startAttempt = 1 ->
                    let! recent = store.GetRecentRuns(c.ScopeId, c.JobId, awaitingGuardRunLookback)
                    return recent |> List.tryFind (fun r -> r.Status = AwaitingExternal)
                | _ -> return None
            }

            match current with
            | None -> ()
            | Some j when j.Status <> Active -> ()
            | Some _ when outstanding.IsSome ->
                let awaiting = outstanding.Value

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

                    let mutable attempt = max 1 startAttempt
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
                            ExternalHandle = None
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
                            // Phase 321 — make this attempt's progress
                            // reporter ambient so the handler reaches it as
                            // `ctx.Progress` without resolving a sink from
                            // DI (321.B).
                            //
                            // `use` is load-bearing: the scope must pop even
                            // when the handler throws, or a subsequent
                            // dispatch on the same execution-context lineage
                            // could inherit a reporter bound to the WRONG
                            // job id — a cross-scope progress publish, which
                            // is a GP 4 breach and not merely a wrong label.
                            // The `with` arm below catches the throw, so
                            // without `use` the pop would be skipped
                            // silently.
                            use _progress =
                                JobProgressScope.push (
                                    JobProgressSink.reporterForOption progressSink current.JobId current.ScopeId
                                )

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
                            ExternalHandle = None
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
                        | HandedOff handle ->
                            // Phase 319 — the attempt has NOT ended, so
                            // the row deliberately carries no
                            // `CompletedAt` and no `DurationMs`: this run
                            // is open, waiting on a backend, and a
                            // duration stamped here would read as a
                            // completed attempt that took however long
                            // `Submit` took. `finalRow` is not reused for
                            // exactly that reason.
                            //
                            // The handle is persisted with the row and
                            // nowhere else. That single write is what
                            // makes the state survive a restart, and
                            // recording it BEFORE releasing the lease is
                            // what makes it survive a crash in between:
                            // if the process dies after this line the
                            // handle is durable and reconciliation finds
                            // it; if it dies before, no run claims to be
                            // awaiting anything.
                            let awaitingRow: JobRun = {
                                RunId = runId
                                JobId = current.JobId
                                ScopeId = current.ScopeId
                                Attempt = attempt
                                StartedAt = runningAt
                                CompletedAt = None
                                Status = AwaitingExternal
                                Error = None
                                DurationMs = None
                                ExternalHandle = Some handle
                            }

                            do! store.RecordRun awaitingRow

                            // Phase 320 — register the handle and mint
                            // its callback credential.
                            //
                            // Ordering is deliberate and load-bearing:
                            // the awaiting ROW is durable first (above),
                            // so a crash here leaves a run the poll loop
                            // still resolves; then the handle record, so
                            // a callback can route; then the credential
                            // hand-off, which is the only step whose
                            // failure costs nothing but latency.
                            //
                            // Skipped entirely when no handle store is
                            // composed — every pre-320 deployment then
                            // takes the identical path it always did
                            // (GP 11 / GP 13).
                            match externalHandleStore with
                            | None -> ()
                            | Some handleStore ->
                                let secret, secretHash = ExternalCallbackSecret.mint ()

                                try
                                    do! handleStore.Register(handle, runId, secretHash)

                                    // Only a backend that DECLARED the
                                    // capability is handed a secret. One
                                    // that did not is reconciled by
                                    // polling, exactly as before — and
                                    // never receives a credential it
                                    // would have no code path for.
                                    match externalDispatcher |> Option.map box with
                                    | Some(:? IExternalCallbackCapableBackend as capable) ->
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
                                            // Swallowed by contract (see
                                            // `AcceptCallbackCredential`).
                                            // The work is accepted, the
                                            // run is durable, the poll
                                            // loop resolves it — turning
                                            // this into a failure would
                                            // trade a latency regression
                                            // for a lost job.
                                            logger.Warn
                                                $"[JobScheduler] event=callback_credential_refused jobId=%O{current.JobId} runId=%O{runId} handle=%O{handle.HandleId} backend=%s{handle.Backend}: {ex.Message} — the backend cannot call back; this run will resolve by poll"
                                    | _ -> ()
                                with ex ->
                                    logger.Warn
                                        $"[JobScheduler] event=handle_registration_failed jobId=%O{current.JobId} runId=%O{runId} handle=%O{handle.HandleId}: {ex.Message} — no completion callback can route to this run; it will resolve by poll"

                            do!
                                emitEvent current.ScopeId "JobExternalHandedOff" {
                                    JobId = current.JobId
                                    ScopeId = current.ScopeId
                                    Handler = current.Handler
                                    RunId = runId
                                    Attempt = attempt
                                    HandleId = handle.HandleId
                                    Backend = handle.Backend
                                    NativeRef = handle.NativeRef
                                    SubmittedAt = handle.SubmittedAt
                                }

                            // Leave the retry loop without consuming a
                            // further attempt. `AwaitingExternal` is not
                            // a failure, so `lastError` stays `None` and
                            // `ConsecutiveFailures` is untouched by the
                            // definition update below — the retry budget
                            // is spent only when the BACKEND reports a
                            // failure, which is reconciliation's call.
                            lastStatus <- AwaitingExternal
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
                                ExternalHandle = None
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
            releaseJobLease dispatchLease
            dispatchActivityOpt |> Option.iter _.Dispose()
    }

    /// A dispatch that begins its retry counter at attempt 1 — every
    /// caller except external reconciliation. Keeps the five existing
    /// call sites (tick, drift back-fill, catch-up replay, `TriggerOnce`,
    /// `NotifyEventWritten`) reading exactly as they did before Phase 319.
    let dispatchOne (job: JobDefinition) (source: TriggerSource) (scheduledAt: DateTime) =
        dispatchFrom 1 job source scheduledAt

    // ─── Status transitions ──────────────────────────────────────

    let setStatus (scopeId: string) (jobId: JobId) (status: JobStatus) = async {
        // Phase 9i — same per-`JobId` lease as `dispatchOne`, so a status
        // transition can never interleave with an in-flight dispatch's
        // read-modify-write. A short TTL is right here (the section is one
        // store read + one write), unlike the dispatch hold.
        let! lease = DistributedLock.acquireBlocking jobLock (jobLockId jobId) (TimeSpan.FromMinutes 1.0)

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
            releaseJobLease lease
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

    // ─── Phase 320 — the exactly-once terminal claim ─────────────
    //
    // Ask the handle store whether THIS caller may resolve `handle`.
    //
    // `true` means proceed. Two distinct cases produce it, and keeping
    // them both is what stops this gate becoming a way to strand a run:
    //
    //   * No handle store composed. Every pre-320 deployment, and any
    //     deployment whose blob backend cannot do conditional writes.
    //     The gate is absent, and Phase 319's path runs byte-identically
    //     (GP 11).
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

    // ─── Phase 320 — the ONE terminal-drive path ─────────────────
    //
    // Extracted from the Phase 319 reconciliation pass so the
    // completion callback and the poll loop share it rather than each
    // carrying a copy. "Identical to an in-process failure" was already
    // Phase 319's acceptance criterion; "identical whether it arrived by
    // push or by poll" is Phase 320's, and two code paths that must stay
    // identical do not.
    //
    // **The `MarkTerminal` claim is at the head of this function, which
    // is what makes callback-vs-poll exactly-once.** Both callers reach
    // the claim through here, so there is one gate rather than two that
    // must agree, and every write below it happens only for the caller
    // that won. `outcome` must be terminal — the non-terminal poll case
    // is handled by the caller, because "still running" is a decision
    // not to write at all rather than a way of resolving.
    let applyExternalOutcome
        (scope: string)
        (job: JobDefinition)
        (run: JobRun)
        (handle: ExternalHandle)
        (outcome: ExternalOutcome)
        (now: DateTime)
        : Async<ExternalResolution> =
        async {
            if not (ExternalOutcome.isTerminal outcome) then
                // Defensive: the wire contract refuses non-terminal
                // statuses and the poll loop branches before calling.
                return ExternalResolution.NoAwaitingRun
            elif run.ScopeId <> handle.ScopeId then
                // GP 4. The handle's scope comes from the handle store's
                // own partition, the run's from the job store — two
                // independent sources, so a disagreement means one of
                // them is pointing somewhere it should not, and nothing
                // is written until they agree.
                logger.Error(
                    $"[JobScheduler] event=external_scope_mismatch handle=%O{handle.HandleId} handleScope=%s{handle.ScopeId} runScope=%s{run.ScopeId} runId=%O{run.RunId} — refusing to resolve across scopes",
                    None
                )

                return ExternalResolution.ScopeMismatch(handle.ScopeId, run.ScopeId)
            else
                let! claimed = claimTerminal handle

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

                    let awaitedMs = int64 (now - run.StartedAt).TotalMilliseconds

                    /// Write the terminal run row for this attempt.
                    /// Overwrites the awaiting row in place (same `RunId` +
                    /// `StartedAt` ⇒ same blob), which is also what removes
                    /// it from the store's awaiting index.
                    let recordTerminal status error = async {
                        do!
                            store.RecordRun {
                                run with
                                    Status = status
                                    CompletedAt = Some now
                                    Error = error
                                    DurationMs = Some awaitedMs
                            }
                    }

                    /// The external-side companion event — emitted alongside
                    /// the standard terminal event, never instead of it.
                    let emitReconciled resultRef error retriable = async {
                        do!
                            emitEvent scope "JobExternalReconciled" {
                                JobId = run.JobId
                                ScopeId = scope
                                Handler = job.Handler
                                RunId = run.RunId
                                Attempt = run.Attempt
                                HandleId = handle.HandleId
                                Backend = handle.Backend
                                NativeRef = handle.NativeRef
                                Outcome = ExternalOutcome.label outcome
                                ResultRef = resultRef
                                Error = error
                                Retriable = retriable
                                AwaitedMs = awaitedMs
                            }
                    }

                    /// Dead-letter this attempt exactly as an in-process
                    /// `PermanentFailure` / exhausted-retry does: the same
                    /// run status, the same `JobDeadLettered` payload, the
                    /// same `Warning` notification, the same
                    /// `ConsecutiveFailures` bump.
                    let deadLetter (error: string) = async {
                        do! recordTerminal DeadLettered (Some error)

                        do!
                            emitEvent scope "JobDeadLettered" {
                                JobId = run.JobId
                                ScopeId = scope
                                Handler = job.Handler
                                Error = error
                                Attempts = run.Attempt
                            }

                        do! notifyDeadLetter scope job error

                        do!
                            store.Update {
                                job with
                                    LastRunAt = Some now
                                    LastRunStatus = Some DeadLettered
                                    LastRunError = Some error
                                    ConsecutiveFailures = job.ConsecutiveFailures + 1
                            }
                    }

                    match outcome with
                    | ExternalOutcome.Succeeded resultRef ->
                        do! recordTerminal Succeeded None

                        // Byte-identical to the in-process success path's
                        // event.
                        do!
                            emitEvent scope "JobCompleted" {
                                JobId = run.JobId
                                ScopeId = scope
                                Handler = job.Handler
                                RunId = run.RunId
                                Attempt = run.Attempt
                                DurationMs = awaitedMs
                            }

                        do! emitReconciled (Some resultRef) None None

                        do!
                            store.Update {
                                job with
                                    LastRunAt = Some now
                                    LastRunStatus = Some Succeeded
                                    LastRunError = None
                                    ConsecutiveFailures = 0
                            }

                        return ExternalResolution.Resolved "succeeded"

                    | ExternalOutcome.Failed error ->
                        do! emitReconciled None (Some error.Message) (Some error.Retriable)

                        let attemptsRemain = run.Attempt < job.RetryPolicy.MaxAttempts

                        if error.Retriable && attemptsRemain then
                            // Record the attempt as `Failed` (not
                            // dead-lettered) and continue the SAME retry
                            // sequence at the next attempt number. The row
                            // must be written before the re-dispatch so the
                            // run leaves the awaiting index and the 319.E
                            // guard does not mistake it for outstanding work
                            // and refuse its own retry.
                            do! recordTerminal Failed (Some error.Message)

                            do!
                                emitEvent scope "JobFailed" {
                                    JobId = run.JobId
                                    ScopeId = scope
                                    Handler = job.Handler
                                    RunId = run.RunId
                                    Attempt = run.Attempt
                                    Error = error.Message
                                    DurationMs = awaitedMs
                                }

                            do!
                                store.Update {
                                    job with
                                        LastRunAt = Some now
                                        LastRunStatus = Some Failed
                                        LastRunError = Some error.Message
                                }

                            logger.Info(
                                sprintf
                                    "[JobScheduler] event=external_retry jobId=%O runId=%O handler=%s backend=%s attempt=%d nextAttempt=%d maxAttempts=%d"
                                    run.JobId
                                    run.RunId
                                    job.Handler
                                    handle.Backend
                                    run.Attempt
                                    (run.Attempt + 1)
                                    job.RetryPolicy.MaxAttempts
                            )

                            // Fire-and-forget, like every other dispatch.
                            // From the poll path it blocks on this job's
                            // lease until the caller's `finally` releases it
                            // — `acquireBlocking` waits rather than failing,
                            // so the queueing is correct rather than merely
                            // tolerated.
                            Async.Start(
                                dispatchFrom (run.Attempt + 1) job (ScheduledByEvent("_external-retry", run.RunId)) now
                            )

                            return ExternalResolution.Resolved "failed"
                        else
                            // Terminal backend failure, or a retriable one
                            // with the retry budget spent. Both dead-letter —
                            // the first skipping the remaining attempts
                            // exactly as `PermanentFailure` does.
                            do! deadLetter error.Message
                            return ExternalResolution.Resolved "dead-lettered"

                    | ExternalOutcome.Cancelled ->
                        // Terminal, but NOT a failure: no dead-letter
                        // notification and no `ConsecutiveFailures` bump,
                        // because a cancelled attempt says nothing about the
                        // job's health and must not push it toward an
                        // auto-disable threshold.
                        do! recordTerminal ExternallyCancelled None
                        do! emitReconciled None None None

                        do!
                            store.Update {
                                job with
                                    LastRunAt = Some now
                                    LastRunStatus = Some ExternallyCancelled
                                    LastRunError = None
                            }

                        logger.Info(
                            sprintf
                                "[JobScheduler] event=external_cancelled jobId=%O runId=%O handler=%s backend=%s awaitedMs=%d"
                                run.JobId
                                run.RunId
                                job.Handler
                                handle.Backend
                                awaitedMs
                        )

                        return ExternalResolution.Resolved "externally-cancelled"

                    | ExternalOutcome.Pending
                    | ExternalOutcome.Running _ ->
                        // Unreachable — guarded at the head.
                        return ExternalResolution.NoAwaitingRun
        }

    // ─── Phase 319 — external hand-off reconciliation ────────────
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
    // protection for free (the Phase 9i seam paying off again — two silos
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
                            let! lease =
                                DistributedLock.acquireBlocking jobLock (jobLockId run.JobId) (TimeSpan.FromMinutes 5.0)

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
                                        // The job definition was deleted while
                                        // its external work ran. There is no
                                        // handler name to report and no policy
                                        // to apply, but the run must still
                                        // leave `AwaitingExternal` — otherwise
                                        // it is polled on every tick forever.
                                        logger.Warn
                                            $"[JobScheduler] event=reconcile_orphan_run jobId=%O{run.JobId} runId=%O{run.RunId} scope=%s{scope} — job definition no longer exists; abandoning the awaiting run"

                                        do!
                                            store.RecordRun {
                                                run with
                                                    Status = DeadLettered
                                                    CompletedAt = Some now
                                                    Error =
                                                        Some
                                                            "job definition no longer exists; external hand-off abandoned"
                                                    DurationMs = Some(int64 (now - run.StartedAt).TotalMilliseconds)
                                            }
                                    | Some job ->

                                        match run.ExternalHandle with
                                        | None ->
                                            // Structurally impossible via
                                            // `HandedOff` — the case carries the
                                            // handle — so this is hand-edited
                                            // state or a store that lost the field
                                            // on a round-trip. Fail it loudly
                                            // rather than leave a run that can
                                            // never be polled and never complete.
                                            logger.Error(
                                                $"[JobScheduler] event=reconcile_handle_missing jobId=%O{run.JobId} runId=%O{run.RunId} handler=%s{job.Handler} — an AwaitingExternal run carries no ExternalHandle; dead-lettering (it cannot be polled)",
                                                None
                                            )

                                            do!
                                                emitEvent scope "JobExternalHandleMissing" {
                                                    JobId = run.JobId
                                                    ScopeId = scope
                                                    Handler = job.Handler
                                                    RunId = run.RunId
                                                    Attempt = run.Attempt
                                                }

                                            let error = "external hand-off recorded no handle; the run cannot be polled"

                                            do!
                                                store.RecordRun {
                                                    run with
                                                        Status = DeadLettered
                                                        CompletedAt = Some now
                                                        Error = Some error
                                                        DurationMs = Some(int64 (now - run.StartedAt).TotalMilliseconds)
                                                }

                                            do!
                                                emitEvent scope "JobDeadLettered" {
                                                    JobId = run.JobId
                                                    ScopeId = scope
                                                    Handler = job.Handler
                                                    Error = error
                                                    Attempts = run.Attempt
                                                }

                                            do! notifyDeadLetter scope job error

                                            do!
                                                store.Update {
                                                    job with
                                                        LastRunAt = Some now
                                                        LastRunStatus = Some DeadLettered
                                                        LastRunError = Some error
                                                        ConsecutiveFailures = job.ConsecutiveFailures + 1
                                                }
                                        | Some handle ->

                                            let! outcome = dispatcher.Poll handle

                                            match outcome with
                                            | ExternalOutcome.Pending
                                            | ExternalOutcome.Running _ ->
                                                // Still going. Deliberately NO store
                                                // write: re-recording an unchanged row
                                                // every tick is pure blob churn.
                                                //
                                                // Phase 321 resolved the "nowhere to
                                                // put the progress" half of this, and
                                                // NOT by adding a `JobRun` field as
                                                // Phase 319 anticipated. A field would
                                                // reintroduce exactly the per-tick blob
                                                // rewrite this arm exists to avoid, and
                                                // would make an additive change to a
                                                // persisted record for a value that is
                                                // interesting live and uninteresting
                                                // afterwards. Fractional progress now
                                                // goes to `IJobProgressSink` instead —
                                                // the notification channel for the live
                                                // bar, the event store only for the
                                                // durable/terminal subset (321.D).
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
                                                // Phase 320 — the poll now goes
                                                // through the SAME gated path the
                                                // completion callback does, so
                                                // whichever arrives first resolves the
                                                // run and the other is a no-op.
                                                let! _ = applyExternalOutcome scope job run handle terminalOutcome now

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
                    Async.Start(dispatchOne job ScheduledByCron now)
        with ex ->
            logger.Error($"[JobScheduler] event=tick_error tickAt={now:o} lastScope={currentScope}", Some ex)

        // Phase 319 — reconcile external hand-offs AFTER due-job
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

    // ─── Phase 598 — event-trigger catch-up (startup scan + sweep) ──
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

                                        Async.Start(
                                            dispatchOne job (ScheduledByEvent(evt.EventType, evt.Id)) evt.OccurredAt
                                        )
                                | None -> ()

                                // Advance whether or not anything matched —
                                // non-trigger events must not rescan forever.
                                watermark.Advance evt

                if triggersDispatched > 0 then
                    do!
                        emitEvent "_platform" "JobTriggerCatchUp" {
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
            // Phase 598 — startup recovery scan BEFORE the first tick:
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

                    // Phase 598 — flush dirty trigger cursors once per
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

            // Phase 598 — best-effort final flush on graceful shutdown
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

    /// Phase 598 — run one catch-up pass on demand (`startup = true`
    /// applies the persisted-cursor + overlap semantics of the boot
    /// scan; `false` the in-memory-cursor + settle-window semantics of
    /// the periodic sweep). No-op unless the deployment opted into
    /// `ServerConfig.EventTriggerCatchUp` and compose supplied the
    /// watermark. Public for operational tooling and tests — the
    /// hosted-service loop calls it on the cadence documented above.
    member _.RunCatchUpScan(startup: bool) : Async<unit> = runCatchUpScan startup

    /// Phase 319 — run one external-hand-off reconciliation pass on
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

    /// Phase 321 — the progress sink this scheduler is using, or `None`
    /// when the deployment did not opt in.
    ///
    /// Exposed so `compose` can register the SAME instance in DI rather
    /// than constructing a second one. That matters and is not tidiness:
    /// the fan-out sink holds the per-job rate-limit state, so two
    /// instances would each keep their own window and a chatty handler
    /// would publish at twice the configured rate — the flood the
    /// coalescer exists to prevent, reintroduced by duplication.
    member _.ProgressSink: IJobProgressSink option = progressSink

    // ─── IJobScheduler ───────────────────────────────────────────

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        // In-process registration is a synchronous dictionary write; the
        // async overload performs the same mutation and reports success.
        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

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

    // ─── Phase 320 — IExternalCompletionSink ─────────────────────
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
                let! lease =
                    DistributedLock.acquireBlocking jobLock (jobLockId located.JobId) (TimeSpan.FromMinutes 5.0)

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
                        | Some job -> return! applyExternalOutcome handle.ScopeId job run handle outcome now
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

/// Phase 598 — `create` plus the shared trigger watermark. Compose
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

/// Phase 319 — `create` / `createWithCatchUp` plus the deployment's
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

/// Phase 320 — `createWithExternalCompute` plus the handle store that
/// makes the completion-callback ingress work: handle-to-run routing, the
/// callback secret's hash, and the atomic `MarkTerminal` gate.
///
/// A **separate arity** rather than an extra parameter on
/// `createWithExternalCompute`, because that function is curried — adding
/// a parameter would break every existing call site for a capability they
/// do not use (GP 11). A scheduler built by the older factory carries no
/// handle store, mints no secrets, registers no handles, and reconciles
/// exactly as Phase 319 did.
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