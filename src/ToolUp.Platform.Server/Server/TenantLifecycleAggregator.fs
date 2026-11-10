module ToolUp.Platform.Compose.TenantLifecycleAggregator

open System
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading
open ToolUp.Platform
open ToolUp.Platform.Jobs

// ─── tenant-lifecycle aggregator ──────────────────────────
//
// Runs every registered `ITenantLifecycle` hook for one provision /
// deprovision call, in parallel with a per-hook timeout, aggregates the
// outcomes into a `LifecycleSummary`, and emits audit:
//   * one `TenantLifecycleHookFailed` row per failed hook, and
//   * one `TenantProvisioned` / `TenantDeprovisioned` end-of-run marker.
//
// Mirrors the parallel-with-timeout shape of `HealthCheckAggregator` /
// `ConfigValidatorAggregator`. The difference: those run at compose
// time against the `IServiceCollection`; tenant lifecycle runs at
// request time against a resolved `ITenantLifecycle list` (the handler
// resolves `seq<ITenantLifecycle>` from DI per call). Keeping `run`
// a pure function of (hooks, phase, scope, actor) makes the contract
// pack able to exercise it with synthetic hooks — no DI container, no
// HTTP context.
//
// **Per-hook isolation.** A hook that throws or times out becomes a
// `Failed` outcome; the run never aborts. This is the load-bearing
// offboard property — a single misbehaving companion hook must not
// block the crypto-shred / erasure of the rest.
//
// **Per-scope idempotency.** A run carries its guard: an `ILifecycleLock`
// acquired for the scope before any hook runs, so two operators (or a
// double-clicked admin button) can't interleave two offboards of one
// tenant. Different scopes never contend. `LifecycleRun.create` guards with
// the process-local `InProcessLifecycleLock` (it blocks until the scope
// frees, so it never refuses); a distributed deployment sets a
// cross-replica lock, and the losing replica gets a prompt
// `AlreadyInProgress` rather than interleaving a second offboard. A caller
// that already holds the scope sets `Guard = None`. The audit trail stays
// the cross-replica record of truth.
//
// **Three entry points over one record.** `run` (parallel, per-hook
// timeout), `runResumable` (sequential, ledger-backed, for the background
// job) and `exportThenDeprovision` (export, audit, then the erasure sweep,
// under one lease) each take a `LifecycleRun`.

/// Default per-hook timeout for `OnProvisioned`. Provisioning hooks are
/// fast (key creation, config seeding); 30 s is generous headroom.
let DefaultProvisionTimeout = TimeSpan.FromSeconds 30.0

/// Default per-hook timeout for `OnDeprovisioned`. Offboard hooks can be
/// slow (multi-store erasure); 5 min bounds the worst case. Precision
/// contract (GP 12 rule 6): the lower bound is `Second`; hooks must not
/// assume sub-second scheduling.
let DefaultDeprovisionTimeout = TimeSpan.FromMinutes 5.0

/// Select the default per-hook timeout for a phase.
let defaultTimeout =
    function
    | Provisioning -> DefaultProvisionTimeout
    | Deprovisioning -> DefaultDeprovisionTimeout

/// Invoke one hook for the given phase with a per-hook timeout. Never
/// throws — a timeout or exception becomes a `Failed` outcome so the
/// caller's `Async.Parallel` always resolves every hook. Retry /
/// supervision as data (GP 12 rule 3).
///
/// On the `Provisioning` phase a hook that opts into
/// `ITenantLifecycleProvisionContext` is dispatched via `OnProvisionedWith`
/// (carrying `request`), so it can seed per-deployment values from the
/// triggering `ProvisioningRequest`; a hook that doesn't falls back to the
/// base `OnProvisioned(scopeId, actorUserId)`. `request` is `None` for
/// every offboard path and for provisioning paths that carry no request,
/// making those byte-identical to before.
let private invokeHook
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    (request: ProvisioningRequest option)
    (timeout: TimeSpan)
    (hook: ITenantLifecycle)
    : Async<LifecycleHookOutcome> =
    async {
        let sw = Stopwatch.StartNew()

        try
            try
                use cts = new CancellationTokenSource(timeout)

                let work =
                    match phase with
                    | Provisioning ->
                        match hook with
                        | :? ITenantLifecycleProvisionContext as rc ->
                            rc.OnProvisionedWith {
                                ScopeId = scopeId
                                ActorUserId = UserId actorUserId
                                Request = request
                            }
                        | _ -> hook.OnProvisioned(scopeId, UserId actorUserId)
                    | Deprovisioning -> hook.OnDeprovisioned(scopeId, UserId actorUserId)

                let probeTask = Async.StartImmediateAsTask(work, cts.Token)
                let! result = probeTask |> Async.AwaitTask
                sw.Stop()

                return {
                    HookName = hook.Name
                    Result = result
                    ElapsedMs = sw.ElapsedMilliseconds
                }
            with
            | :? OperationCanceledException ->
                sw.Stop()
                let ms = int timeout.TotalMilliseconds

                return {
                    HookName = hook.Name
                    Result = LifecycleHookResult.Failed(sprintf "hook exceeded timeout (%dms)" ms)
                    ElapsedMs = sw.ElapsedMilliseconds
                }
            | ex ->
                sw.Stop()

                return {
                    HookName = hook.Name
                    Result = LifecycleHookResult.Failed("hook threw: " + ex.Message)
                    ElapsedMs = sw.ElapsedMilliseconds
                }
        finally
            sw.Stop()
    }

/// Invoke one hook with `LifecycleRetryPolicy` retry: a hook
/// that returns `Failed` is re-invoked (after the policy's backoff) up to
/// `MaxAttempts`, returning the first non-`Failed` outcome or the last
/// `Failed`. `Completed` / `Skipped` short-circuits immediately. Retry as
/// data (GP 12 rule 3) — the policy is a record, never an `OnFailure`
/// callback. `MaxAttempts = 1` (`LifecycleRetryPolicy.noRetry`) is one
/// attempt, identical to a bare `invokeHook`.
let rec private invokeHookWithRetry
    (policy: LifecycleRetryPolicy)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    (request: ProvisioningRequest option)
    (timeout: TimeSpan)
    (attempt: int)
    (hook: ITenantLifecycle)
    : Async<LifecycleHookOutcome> =
    async {
        let! outcome = invokeHook phase scopeId actorUserId request timeout hook

        match outcome.Result with
        | LifecycleHookResult.Failed _ when attempt < policy.MaxAttempts ->
            let delay = LifecycleRetryPolicy.delayFor policy (attempt + 1)

            if delay > TimeSpan.Zero then
                do! Async.Sleep(int delay.TotalMilliseconds)

            return! invokeHookWithRetry policy phase scopeId actorUserId request timeout (attempt + 1) hook
        | _ -> return outcome
    }

/// Emit the post-run audit rows for an aggregated `summary`: one
/// `TenantLifecycleHookFailed` per failed hook (non-aborting — the run
/// already completed every hook), then the single end-of-phase marker
/// (`TenantProvisioned` / `TenantDeprovisioned`). Shared by the parallel
/// `run` and the resumable `runResumable` so both phases emit the
/// identical audit shape. `emitAudit` is best-effort by contract — a
/// sink outage cannot fail an offboard.
let private emitRunAudit
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    (summary: LifecycleSummary)
    : Async<unit> =
    async {
        for o in summary.Outcomes do
            match o.Result with
            | LifecycleHookResult.Failed err ->
                let payload: TenantLifecycleHookFailedPayload = {
                    ScopeId = scopeId
                    Actor = actorUserId
                    Phase = TenantLifecyclePhase.name phase
                    HookName = o.HookName
                    Error = err
                }

                do! emitAudit scopeId (AuditEvent.TenantLifecycleHookFailed payload)
            | _ -> ()

        let completed = LifecycleSummary.completedCount summary
        let skipped = LifecycleSummary.skippedCount summary
        let failed = LifecycleSummary.failedCount summary

        match phase with
        | Provisioning ->
            let payload: TenantProvisionedPayload = {
                ScopeId = scopeId
                Actor = actorUserId
                HooksRun = summary.Outcomes.Length
                HooksCompleted = completed
                HooksSkipped = skipped
                HooksFailed = failed
                ElapsedMs = summary.TotalElapsedMs
            }

            do! emitAudit scopeId (AuditEvent.TenantProvisioned payload)
        | Deprovisioning ->
            let payload: TenantDeprovisionedPayload = {
                ScopeId = scopeId
                Actor = actorUserId
                HooksRun = summary.Outcomes.Length
                HooksCompleted = completed
                HooksSkipped = skipped
                HooksFailed = failed
                ElapsedMs = summary.TotalElapsedMs
            }

            do! emitAudit scopeId (AuditEvent.TenantDeprovisioned payload)
    }

/// One tenant-lifecycle run: which scope, which phase, who asked, the
/// provisioning request (provision only), the per-hook timeout, the guard,
/// where audit rows go and the hooks to run.
[<NoEquality; NoComparison>]
type LifecycleRun = {
    /// The scope the hooks run against.
    Scope: string
    /// Provisioning or deprovisioning.
    Phase: TenantLifecyclePhase
    /// The actor the run and its audit rows are attributed to.
    Actor: string
    /// The triggering request, forwarded to each provisioning hook that opts
    /// into `ITenantLifecycleProvisionContext`; `None` on every offboard.
    Request: ProvisioningRequest option
    /// The per-hook timeout; `create` takes the phase's default.
    Timeout: TimeSpan
    /// The lock acquired for the scope before any hook runs; `None` when the
    /// caller already holds the scope.
    Guard: ILifecycleLock option
    /// Best-effort audit emission (scope id + event); the canonical wiring
    /// (`IAuditLog.Record`) swallows its own failures, so an audit-sink
    /// outage cannot fail an offboard.
    Audit: string -> AuditEvent -> Async<unit>
    /// The hooks to run.
    Hooks: ITenantLifecycle list
}

/// The resumable sweep's cursor: the ledger it reads and records completed
/// hooks in, where partial progress goes, and the per-hook retry policy.
[<NoEquality; NoComparison>]
type LifecycleCursor = {
    /// Hook names already done on a prior (crashed) attempt.
    ReadCompleted: unit -> Async<Set<string>>
    /// Record a hook that reached a terminal success.
    RecordCompleted: LifecycleHookOutcome -> Async<unit>
    /// Receives each intermediate summary.
    OnProgress: LifecycleSummary -> Async<unit>
    /// Re-invokes a `Failed` hook per backoff before recording its outcome.
    RetryPolicy: LifecycleRetryPolicy
}

/// Outcome of a guarded lifecycle run.
[<RequireQualifiedAccess>]
type GuardedRun =
    /// The guard was acquired (or none was set) and the run completed with
    /// this summary.
    | Ran of LifecycleSummary
    /// Another holder (a concurrent replica under a distributed lock)
    /// already holds the scope, so the run did not start. The in-process
    /// default never yields this — it serialises same-scope runs.
    | AlreadyInProgress

/// Building a `LifecycleRun`.
[<RequireQualifiedAccess>]
module LifecycleRun =

    /// A run of `phase` against `scope` for `actor`, with no provisioning
    /// request, the phase's default per-hook timeout and the process-local
    /// lock as its guard.
    let create
        (audit: string -> AuditEvent -> Async<unit>)
        (hooks: ITenantLifecycle list)
        (phase: TenantLifecyclePhase)
        (scope: string)
        (actor: string)
        : LifecycleRun =
        {
            Scope = scope
            Phase = phase
            Actor = actor
            Request = None
            Timeout = defaultTimeout phase
            Guard = Some InProcessLifecycleLock.shared
            Audit = audit
            Hooks = hooks
        }

/// Hold the run's guard for `body`: acquire the scope, run, release the
/// lease in a `finally` so a hook that throws still frees the scope. `None`
/// when the lock reports another holder.
let private guarded (r: LifecycleRun) (body: unit -> Async<'T>) : Async<'T option> = async {
    match r.Guard with
    | None ->
        let! result = body ()
        return Some result
    | Some lock ->
        match! lock.Acquire r.Scope with
        | None -> return None
        | Some lease ->
            try
                let! result = body ()
                return Some result
            finally
                lease.Dispose()
}

/// The parallel sweep, guard already held.
let private sweep (r: LifecycleRun) : Async<LifecycleSummary> = async {
    let sw = Stopwatch.StartNew()

    let! outcomesArr =
        r.Hooks
        |> List.map (invokeHook r.Phase r.Scope r.Actor r.Request r.Timeout)
        |> Async.Parallel

    sw.Stop()

    let summary = {
        ScopeId = r.Scope
        Phase = r.Phase
        Outcomes = List.ofArray outcomesArr
        TotalElapsedMs = sw.ElapsedMilliseconds
    }

    do! emitRunAudit r.Audit r.Phase r.Scope r.Actor summary
    return summary
}

/// Run every hook for the run's phase in parallel under its per-hook
/// timeout, emit the audit rows, and return the aggregated summary. Per-hook
/// failure does not abort the run. The guard is acquired first; a lock that
/// reports another holder yields `AlreadyInProgress` without running.
let run (r: LifecycleRun) : Async<GuardedRun> = async {
    match! guarded r (fun () -> sweep r) with
    | Some summary -> return GuardedRun.Ran summary
    | None -> return GuardedRun.AlreadyInProgress
}

// ─── background / async offboard ─────────────────────────
//
// When an `IJobScheduler` is composed, a long offboard runs as a
// background lifecycle job instead of awaiting inline under the per-hook
// timeout: `enqueue` schedules + fires the job (returning a
// `LifecycleJobHandle` promptly), and `runResumable` is the
// progress-persisting, restart-survivable sweep `LifecycleJobHandler`
// drives on the job thread. The inline `run` path above is
// unchanged — deployments without a scheduler keep the synchronous
// behaviour (GP 11 / GP 13).

/// Reserved `IJobScheduler` handler name for the background lifecycle
/// job. `ComposeTenantLifecycle` registers `LifecycleJobHandler` under
/// it at startup; `enqueue` schedules against it.
[<Literal>]
let LifecycleJobHandlerName = "_platform.tenant.lifecycle"

/// The job payload carried in `JobContext.Payload`: the phase + scope +
/// actor of the offboard. A flat JSON object (phase → stable case-name
/// string) so it survives `IJobStore` round-trips without a converter
/// dependency — mirrors `DsrJobPayload`.
module LifecycleJobPayload =
    /// Serialise `(phase, scopeId, actorUserId)` to the job payload string.
    let serialise (phase: TenantLifecyclePhase) (scopeId: string) (actorUserId: string) : string =
        let o = JsonObject()
        o["phase"] <- JsonValue.Create(TenantLifecyclePhase.name phase)
        o["scopeId"] <- JsonValue.Create scopeId
        o["actorUserId"] <- JsonValue.Create actorUserId
        o.ToJsonString()

    /// Parse the job payload back to `(phase, scopeId, actorUserId)`.
    let parse (payload: string) : Result<TenantLifecyclePhase * string * string, string> =
        try
            let node = JsonNode.Parse payload

            let phase =
                match node["phase"].GetValue<string>() with
                | "Provisioning" -> Provisioning
                | _ -> Deprovisioning

            Ok(phase, node["scopeId"].GetValue<string>(), node["actorUserId"].GetValue<string>())
        with ex ->
            Error ex.Message

/// Enqueue a background offboard against `scheduler` and fire it
/// immediately, returning a `LifecycleJobHandle` without awaiting the
/// sweep. `Manual` trigger + `TriggerOnce` (mirrors the data-subject async
/// DSR path) so the work starts now rather than at the next scheduler
/// tick. An idempotency key per `(phase, scope)` dedups a double-clicked
/// admin button within the TTL; a re-offboard inside the window returns
/// the existing handle rather than racing a second sweep.
let enqueue
    (scheduler: IJobScheduler)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<Result<LifecycleJobHandle, string>> =
    async {
        let registration: JobRegistration = {
            ScopeId = scopeId
            Handler = LifecycleJobHandlerName
            Payload = LifecycleJobPayload.serialise phase scopeId actorUserId
            Trigger = Manual
            Idempotency =
                Some {
                    Key = sprintf "tenant-lifecycle-%s-%s" (TenantLifecyclePhase.name phase) scopeId
                    TtlSeconds = 60 * 60 * 24
                }
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = None
            Precision = Minute
            CreatedBy = actorUserId
            Tags = Map [ "source", "tenant-lifecycle"; "phase", TenantLifecyclePhase.name phase ]
        }

        match! scheduler.Schedule registration with
        | Error err -> return Error(sprintf "failed to schedule offboard job: %A" err)
        | Ok(Scheduled jobId | Coalesced jobId) ->
            // Fire it now: a `Manual` job never fires on its own, and
            // a refused trigger is reported, not discarded: discarded, the
            // caller held a handle to an offboard that would never start.
            match! scheduler.TriggerOnce(scopeId, jobId, UserId actorUserId) with
            | Error refusal ->
                return
                    Error(
                        sprintf
                            "scheduled offboard job %A but could not trigger it: %s"
                            jobId
                            (TriggerError.message refusal)
                    )
            | Ok() ->
                return
                    Ok {
                        JobId = jobId
                        ScopeId = scopeId
                        Phase = phase
                    }
    }

// ─── scheduled / grace-period offboard ───────────────────
//
// `ScheduleDeprovision` registers a cron-polled job under the tenant scope
// whose handler (`ScheduledDeprovisionJobHandler`) checks each tick
// whether the grace window has elapsed; when it has, it fires the offboard
// (via `enqueue` — reusing the resumable path) and retires
// itself. The pending window is the job's payload, so `Get` / `Cancel`
// read/act on the persisted job with no extra store.

/// Reserved `IJobScheduler` handler name for the scheduled-offboard poll
/// job. `ComposeTenantLifecycle` registers `ScheduledDeprovisionJobHandler`
/// under it at startup.
[<Literal>]
let ScheduledDeprovisionHandlerName = "_platform.tenant.scheduled-deprovision"

/// Cron expression the poll job runs on — the top of every hour. A
/// grace window is measured in days, so hourly polling bounds the fire
/// latency to ≤1h while keeping the scheduler tick load negligible. The
/// handler compares `now` against the payload's `dueAt`; the cron only
/// decides *how often to check*, not *when to fire*.
[<Literal>]
let ScheduledDeprovisionCron = "0 * * * *"

/// Payload for the scheduled-offboard poll job: the target scope, the
/// requesting admin, the reason, and the absolute due time. Flat JSON
/// (mirrors `LifecycleJobPayload`) so it survives `IJobStore` round-trips.
module ScheduledDeprovisionPayload =
    let serialise (scopeId: string) (actorUserId: string) (reason: string) (dueAt: DateTimeOffset) : string =
        let o = JsonObject()
        o["scopeId"] <- JsonValue.Create scopeId
        o["actorUserId"] <- JsonValue.Create actorUserId
        o["reason"] <- JsonValue.Create reason
        o["dueAt"] <- JsonValue.Create(dueAt.ToString("o"))
        o.ToJsonString()

    let parse (payload: string) : Result<string * string * string * DateTimeOffset, string> =
        try
            let node = JsonNode.Parse payload

            let dueAt =
                DateTimeOffset.Parse(
                    node["dueAt"].GetValue<string>(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind
                )

            Ok(
                node["scopeId"].GetValue<string>(),
                node["actorUserId"].GetValue<string>(),
                node["reason"].GetValue<string>(),
                dueAt
            )
        with ex ->
            Error ex.Message

/// Project a persisted poll job to a `ScheduledDeprovision` (the wire
/// surface), or `None` when its payload is unreadable.
let private toScheduledDeprovision (job: JobDefinition) : ScheduledDeprovision option =
    match ScheduledDeprovisionPayload.parse job.Payload with
    | Ok(scopeId, requestedBy, reason, dueAt) ->
        Some {
            ScopeId = scopeId
            RequestedBy = requestedBy
            Reason = reason
            DueAt = dueAt
            JobId = string job.JobId
        }
    | Error _ -> None

/// The single pending (`Active`) scheduled-offboard job for `scopeId`, if
/// any. Enumerates the scope's jobs and matches the reserved handler name.
let private pendingScheduledJob (scheduler: IJobScheduler) (scopeId: string) : Async<JobDefinition option> = async {
    let! jobs = scheduler.ListJobs scopeId

    return
        jobs
        |> List.tryFind (fun j -> j.Handler = ScheduledDeprovisionHandlerName && j.Status = JobStatus.Active)
}

/// Schedule a grace-period offboard of `scopeId` to fire after `afterDays`
/// unless cancelled. Idempotent per scope (a second schedule inside the
/// TTL returns the existing pending offboard rather than registering a
/// duplicate). Returns the pending `ScheduledDeprovision`.
let scheduleDeprovision
    (scheduler: IJobScheduler)
    (scopeId: string)
    (actorUserId: string)
    (afterDays: int)
    (reason: string)
    : Async<Result<ScheduledDeprovision, string>> =
    async {
        // An existing pending offboard wins (idempotent) — re-scheduling a
        // scope that already has a grace window pending returns it
        // unchanged rather than stacking a second poll job.
        match! pendingScheduledJob scheduler scopeId with
        | Some existing ->
            match toScheduledDeprovision existing with
            | Some sd -> return Ok sd
            | None -> return Error "a scheduled offboard exists for this scope but its payload is unreadable"
        | None ->
            let dueAt = DateTimeOffset.UtcNow.AddDays(float (max 0 afterDays))

            let registration: JobRegistration = {
                ScopeId = scopeId
                Handler = ScheduledDeprovisionHandlerName
                Payload = ScheduledDeprovisionPayload.serialise scopeId actorUserId reason dueAt
                Trigger = CronTrigger ScheduledDeprovisionCron
                Idempotency =
                    Some {
                        Key = sprintf "tenant-scheduled-deprovision-%s" scopeId
                        TtlSeconds = 60 * 60 * 24 * 90
                    }
                RetryPolicy = JobRetryPolicy.defaults
                ShardKey = None
                Precision = Minute
                CreatedBy = actorUserId
                Tags = Map [ "source", "tenant-lifecycle"; "kind", "scheduled-deprovision" ]
            }

            match! scheduler.Schedule registration with
            | Error err -> return Error(sprintf "failed to schedule grace-period offboard: %A" err)
            | Ok(Scheduled jobId | Coalesced jobId) ->
                return
                    Ok {
                        ScopeId = scopeId
                        RequestedBy = actorUserId
                        Reason = reason
                        DueAt = dueAt
                        JobId = string jobId
                    }
    }

/// The pending scheduled offboard for `scopeId`, or `None`.
let getScheduledDeprovision (scheduler: IJobScheduler) (scopeId: string) : Async<ScheduledDeprovision option> = async {
    match! pendingScheduledJob scheduler scopeId with
    | Some job -> return toScheduledDeprovision job
    | None -> return None
}

/// Cancel the pending scheduled offboard for `scopeId` before it fires.
/// Idempotent — answers `Ok None` when nothing was pending. Answers the
/// cancelled job's `ScheduledDeprovision` so the caller can audit the
/// reason it carried, and the scheduler's refusal when the cancel did not
/// take — the offboard is then still pending.
let cancelScheduledDeprovision
    (scheduler: IJobScheduler)
    (scopeId: string)
    : Async<Result<ScheduledDeprovision option, JobTransitionError>> =
    async {
        match! pendingScheduledJob scheduler scopeId with
        | None -> return Ok None
        | Some job ->
            match! scheduler.Cancel(scopeId, job.JobId) with
            | Error error -> return Error error
            | Ok() -> return Ok(toScheduledDeprovision job)
    }

/// Resumable, progress-persisting sweep for the background path. Unlike
/// `run` (parallel), this runs hooks **sequentially** under the run's guard
/// and records each terminal-success hook (`Completed` / `Skipped`) through
/// the cursor's `RecordCompleted` as it lands, so a process killed
/// mid-sweep re-dispatches and resumes from the last completed hook without
/// re-running it. `ReadCompleted` names the hooks already done on a prior
/// (crashed) attempt; those are recorded as `Completed` outcomes without
/// re-invocation. `OnProgress` receives each intermediate summary so a
/// snapshot surface (`GetLifecycleSummary`) can stream N-of-M progress. A
/// `Failed` hook is retried per `RetryPolicy` and, if it stays failed, is
/// NOT recorded as done, so the re-dispatch retries it. The end-of-run
/// audit matches `run` exactly.
let runResumable (cursor: LifecycleCursor) (r: LifecycleRun) : Async<GuardedRun> =
    let resumable () = async {
        let sw = Stopwatch.StartNew()
        let! alreadyDone = cursor.ReadCompleted()

        // Hooks completed on a prior (crashed) attempt — recorded as
        // Completed outcomes (elapsed 0) WITHOUT re-invoking, so the
        // summary reaches its terminal marker without re-running them.
        let resumed =
            r.Hooks
            |> List.filter (fun h -> Set.contains h.Name alreadyDone)
            |> List.map (fun h -> {
                HookName = h.Name
                Result = LifecycleHookResult.Completed
                ElapsedMs = 0L
            })

        let outcomes = ResizeArray<LifecycleHookOutcome>(resumed)

        let pending =
            r.Hooks |> List.filter (fun h -> not (Set.contains h.Name alreadyDone))

        for hook in pending do
            // The resumable sweep is the background offboard path; no
            // provisioning request rides it.
            let! outcome = invokeHookWithRetry cursor.RetryPolicy r.Phase r.Scope r.Actor None r.Timeout 1 hook
            outcomes.Add outcome

            match outcome.Result with
            | LifecycleHookResult.Completed
            | LifecycleHookResult.Skipped _ -> do! cursor.RecordCompleted outcome
            | LifecycleHookResult.Failed _ -> ()

            // Stream partial progress after each hook resolves.
            do!
                cursor.OnProgress {
                    ScopeId = r.Scope
                    Phase = r.Phase
                    Outcomes = List.ofSeq outcomes
                    TotalElapsedMs = sw.ElapsedMilliseconds
                }

        sw.Stop()

        let summary = {
            ScopeId = r.Scope
            Phase = r.Phase
            Outcomes = List.ofSeq outcomes
            TotalElapsedMs = sw.ElapsedMilliseconds
        }

        do! emitRunAudit r.Audit r.Phase r.Scope r.Actor summary
        return summary
    }

    async {
        match! guarded r resumable with
        | Some summary -> return GuardedRun.Ran summary
        | None -> return GuardedRun.AlreadyInProgress
    }

// ─── offboard preview / dry-run ──────────────────────────
//
// Mutation-free projection of what a `Deprovisioning` run WOULD do. Each
// hook that implements `ITenantLifecyclePreview` contributes a count-only
// would-affect item; a hook that doesn't surfaces a "no preview
// available" item, so the operator sees the gap. Previews run in parallel
// (read-only); a preview that throws degrades to a no-preview item rather
// than aborting the whole preview. No audit, no mutation — the canonical
// handler emits at most a lightweight "previewed" row.

/// Aggregate every registered hook's offboard preview for `scopeId`.
/// Hooks without `ITenantLifecyclePreview` surface
/// `LifecyclePreviewItem.noPreview`. Pure read — calls no
/// `OnDeprovisioned`, mutates nothing.
let previewDeprovision
    (hooks: ITenantLifecycle list)
    (scopeId: string)
    (actorUserId: string)
    : Async<LifecyclePreview> =
    async {
        let! items =
            hooks
            |> List.map (fun hook -> async {
                match hook with
                | :? ITenantLifecyclePreview as p ->
                    try
                        return! p.OnDeprovisionPreview(scopeId, UserId actorUserId)
                    with ex ->
                        // A preview should never mutate, so a throw is a
                        // bug in the hook — degrade to no-preview rather
                        // than failing the operator's dry-run.
                        return {
                            HookName = hook.Name
                            HasPreview = false
                            WouldAffect = 0
                            Detail = "preview failed: " + ex.Message
                        }
                | _ -> return LifecyclePreviewItem.noPreview hook.Name
            })
            |> Async.Parallel

        let itemList = List.ofArray items

        let total =
            itemList |> List.sumBy (fun i -> if i.HasPreview then i.WouldAffect else 0)

        return {
            ScopeId = scopeId
            Items = itemList
            TotalWouldAffect = total
        }
    }

// ─── export-then-erase bundle ────────────────────────────
//
// "Give me my data, then delete it" in one operation: produce the
// tenant's export archive (via the caller-supplied `runExport`, which
// drives the `IDataExporter` surface) and ONLY THEN run the
// erasure sweep. Fail-closed ordering (the load-bearing GDPR property):
// a failed export aborts the offboard before any destruction, so the
// tenant's data stays intact. The `TenantDataExported` audit row is
// emitted between the durable export and the erasure, so its presence
// proves the export committed first. `runExport` is a callback so the
// aggregator stays DI-free (the handler resolves `IDataExporter` /
// `IBlobStorage`); it returns the durable archive reference or an error.

/// Outcome of a lock-guarded export-then-erase bundle. Mirrors
/// `GuardedRun` for the export-then-erase bundle: because the WHOLE bundle
/// (export + audit + erasure sweep) runs under one lease, a distributed
/// lock's refusal aborts BEFORE the export — so two replicas can never run
/// concurrent export-then-erase bundles of one tenant.
[<RequireQualifiedAccess>]
type GuardedExportThenDeprovision =
    /// The lease was acquired and the bundle completed — export committed
    /// then the erasure ran; carries the summary + archive reference.
    | Ran of ExportThenDeprovisionResult
    /// The lease was acquired but the export failed, so NO erasure ran
    /// (fail-closed — the tenant's data is intact). Carries the reason.
    | ExportFailed of string
    /// Another holder (a concurrent replica under a distributed lock)
    /// already holds the scope, so neither export nor erasure ran. The
    /// in-process default never yields this — it blocks until the scope
    /// frees — so this only materialises under a distributed `ILifecycleLock`.
    | AlreadyInProgress

/// Export-then-erase under the run's guard, acquired BEFORE the export so
/// the whole bundle (export + audit + erasure sweep) is one exclusive unit:
/// under a distributed lock a peer replica already mid-bundle yields
/// `AlreadyInProgress` and neither the export nor the erasure runs.
/// Fail-closed ordering holds inside the lease: a failed export is
/// `ExportFailed`, and no erasure hook runs. The erasure is the run's
/// `Deprovisioning` sweep, run with the lease already held.
let exportThenDeprovision
    (runExport: unit -> Async<Result<LifecycleExportArchive, string>>)
    (r: LifecycleRun)
    : Async<GuardedExportThenDeprovision> =
    let bundle () = async {
        match! runExport () with
        | Error err ->
            // Fail-closed — nothing erased, the tenant's data is intact.
            return
                GuardedExportThenDeprovision.ExportFailed(
                    sprintf "export failed — offboard aborted, no data erased: %s" err
                )
        | Ok archive ->
            // Audit the export BEFORE the erasure sweep (ordering proof).
            let payload: TenantDataExportedPayload = {
                ScopeId = r.Scope
                Actor = r.Actor
                ArchiveContainer = archive.Container
                ArchivePath = archive.BlobPath
                ContentHash = archive.ContentHash
                SegmentCount = archive.SegmentCount
            }

            do! r.Audit r.Scope (AuditEvent.TenantDataExported payload)

            let! summary =
                sweep {
                    r with
                        Phase = Deprovisioning
                        Request = None
                }

            return GuardedExportThenDeprovision.Ran { Summary = summary; Archive = archive }
    }

    async {
        match! guarded r bundle with
        | Some outcome -> return outcome
        | None -> return GuardedExportThenDeprovision.AlreadyInProgress
    }