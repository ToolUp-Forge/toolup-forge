module ToolUp.Platform.TenantLifecycleAggregator

open System
open System.Diagnostics
open System.Text.Json.Nodes
open System.Threading
open ToolUp.Platform

// ─── Phase 54 — tenant-lifecycle aggregator ──────────────────────────
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
// **Per-scope idempotency.** `runGuarded` serialises concurrent runs
// for the *same* scope so two operators (or a double-clicked admin
// button) can't interleave two offboards of one tenant. Different scopes
// never contend. Phase 54h lifts the exclusion behind `ILifecycleLock`:
// `runGuarded` routes through the process-local `InProcessLifecycleLock`
// (byte-identical single-instance serialisation), while `runGuardedWith`
// accepts an explicit lock so a distributed deployment composes a
// cross-replica lock (the Redis reference impl) and the losing replica
// gets a prompt `AlreadyInProgress` rather than interleaving a second
// offboard. The audit trail stays the cross-replica record of truth.

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
let private invokeHook
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
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
                    | Provisioning -> hook.OnProvisioned(scopeId, actorUserId)
                    | Deprovisioning -> hook.OnDeprovisioned(scopeId, actorUserId)

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

/// Invoke one hook with `LifecycleRetryPolicy` retry (Phase 54b): a hook
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
    (timeout: TimeSpan)
    (attempt: int)
    (hook: ITenantLifecycle)
    : Async<LifecycleHookOutcome> =
    async {
        let! outcome = invokeHook phase scopeId actorUserId timeout hook

        match outcome.Result with
        | LifecycleHookResult.Failed _ when attempt < policy.MaxAttempts ->
            let delay = LifecycleRetryPolicy.delayFor policy (attempt + 1)

            if delay > TimeSpan.Zero then
                do! Async.Sleep(int delay.TotalMilliseconds)

            return! invokeHookWithRetry policy phase scopeId actorUserId timeout (attempt + 1) hook
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

/// Run every hook for `phase` against `scopeId` (attributed to
/// `actorUserId`) with the supplied per-hook `timeout`, emit the audit
/// rows via `emitAudit` (scopeId + event), and return the aggregated
/// `LifecycleSummary`. Hooks run in parallel; per-hook failure does not
/// abort the run. `emitAudit` is best-effort from the run's
/// perspective — the canonical wiring (`IAuditLog.Record`) swallows its
/// own failures, so an audit-sink outage cannot fail an offboard.
let run
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (timeout: TimeSpan)
    (hooks: ITenantLifecycle list)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<LifecycleSummary> =
    async {
        let sw = Stopwatch.StartNew()

        let! outcomesArr =
            hooks
            |> List.map (invokeHook phase scopeId actorUserId timeout)
            |> Async.Parallel

        sw.Stop()
        let outcomes = List.ofArray outcomesArr

        let summary = {
            ScopeId = scopeId
            Phase = phase
            Outcomes = outcomes
            TotalElapsedMs = sw.ElapsedMilliseconds
        }

        do! emitRunAudit emitAudit phase scopeId actorUserId summary
        return summary
    }

/// `run` with the phase's default per-hook timeout.
let runWithDefaults
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (hooks: ITenantLifecycle list)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<LifecycleSummary> =
    run emitAudit (defaultTimeout phase) hooks phase scopeId actorUserId

/// Outcome of a lock-guarded lifecycle run (Phase 54h).
[<RequireQualifiedAccess>]
type GuardedRun =
    /// The lock was acquired and the run completed with this summary.
    | Ran of LifecycleSummary
    /// Another holder (a concurrent replica under a distributed lock)
    /// already holds the scope, so the run did not start. The in-process
    /// default never yields this — it serialises same-scope runs — so this
    /// case only materialises under a distributed `ILifecycleLock`.
    | AlreadyInProgress

/// `runWithDefaults` guarded by an explicit `ILifecycleLock` (Phase 54h):
/// acquire the scope, run on success, release the lease on completion.
/// Returns `GuardedRun.AlreadyInProgress` WITHOUT running when the lock
/// reports another holder (a concurrent replica under a distributed lock),
/// so the caller can surface a prompt "offboard already in progress"
/// rather than blocking the request for the minutes a cross-store offboard
/// can take. Under the in-process default the acquire blocks until the
/// scope frees and so always returns `Some`, making this always `Ran`
/// (Phase 54 serialisation preserved). `lease.Dispose()` releases in a
/// `finally`, so a hook that throws still frees the scope.
let runGuardedWith
    (lock: ILifecycleLock)
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (hooks: ITenantLifecycle list)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<GuardedRun> =
    async {
        match! lock.Acquire scopeId with
        | None -> return GuardedRun.AlreadyInProgress
        | Some lease ->
            try
                let! summary = runWithDefaults emitAudit hooks phase scopeId actorUserId
                return GuardedRun.Ran summary
            finally
                lease.Dispose()
    }

/// `runWithDefaults` serialised per scope: concurrent runs for the same
/// `scopeId` execute one-at-a-time, so two operators offboarding the same
/// tenant don't interleave. Different scopes run fully in parallel. Use
/// this from the request handler; the contract pack exercises both `run`
/// (parallel/timeout/isolation) and the per-scope serialisation directly.
///
/// Phase 54h — routes through `runGuardedWith` over the process-local
/// `InProcessLifecycleLock`, whose `Acquire` blocks until the scope frees,
/// so single-instance behaviour is byte-identical to Phase 54. A
/// deployment that needs cross-replica exclusion composes a distributed
/// `ILifecycleLock` and calls `runGuardedWith` instead.
let runGuarded
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (hooks: ITenantLifecycle list)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<LifecycleSummary> =
    async {
        match! runGuardedWith InProcessLifecycleLock.shared emitAudit hooks phase scopeId actorUserId with
        | GuardedRun.Ran summary -> return summary
        | GuardedRun.AlreadyInProgress ->
            // Unreachable with the in-process lock (it serialises, never
            // refuses); kept total so the match is exhaustive.
            return! runWithDefaults emitAudit hooks phase scopeId actorUserId
    }

// ─── Phase 54a — background / async offboard ─────────────────────────
//
// When an `IJobScheduler` is composed, a long offboard runs as a
// background lifecycle job instead of awaiting inline under the per-hook
// timeout: `enqueue` schedules + fires the job (returning a
// `LifecycleJobHandle` promptly), and `runResumable` is the
// progress-persisting, restart-survivable sweep `LifecycleJobHandler`
// drives on the job thread. The inline `run` / `runGuarded` path above is
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
/// sweep. `Manual` trigger + `TriggerOnce` (mirrors the Phase 9h.A async
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
        | Ok jobId ->
            // Fire immediately (Manual trigger) so the offboard does not
            // wait for the next scheduler tick.
            let! _ = scheduler.TriggerOnce(scopeId, jobId, actorUserId)

            return
                Ok {
                    JobId = jobId
                    ScopeId = scopeId
                    Phase = phase
                }
    }

// ─── Phase 54f — scheduled / grace-period offboard ───────────────────
//
// `ScheduleDeprovision` registers a cron-polled job under the tenant scope
// whose handler (`ScheduledDeprovisionJobHandler`) checks each tick
// whether the grace window has elapsed; when it has, it fires the offboard
// (via `enqueue` — reusing the Phase 54a/54b resumable path) and retires
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
            | Ok jobId ->
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
/// Idempotent — returns `false` when nothing was pending. Returns the
/// cancelled job's `ScheduledDeprovision` so the caller can audit the
/// reason it carried.
let cancelScheduledDeprovision (scheduler: IJobScheduler) (scopeId: string) : Async<ScheduledDeprovision option> = async {
    match! pendingScheduledJob scheduler scopeId with
    | None -> return None
    | Some job ->
        do! scheduler.Cancel(scopeId, job.JobId)
        return toScheduledDeprovision job
}

/// Resumable, progress-persisting sweep for the background path. Unlike
/// `run` (parallel, fire-and-forget audit), this runs hooks
/// **sequentially** and records each terminal-success hook
/// (`Completed` / `Skipped`) via `recordCompleted` as it lands, so a
/// process killed mid-sweep re-dispatches and resumes from the last
/// completed hook without re-running it. `readCompleted` returns the set
/// of hook names already done on a prior (crashed) attempt — those are
/// recorded as `Completed` outcomes without re-invocation. `onProgress`
/// receives each intermediate `LifecycleSummary` so a snapshot surface
/// (`GetLifecycleSummary`) can stream running → N-of-M progress. A
/// `Failed` hook is NOT recorded as done, so the re-dispatch retries it;
/// the end-of-run audit (`emitRunAudit`) matches the inline path exactly.
///
/// Phase 54b — `retryPolicy` re-invokes a `Failed` hook per backoff
/// before recording its terminal outcome (`LifecycleRetryPolicy.noRetry`
/// preserves the prior single-attempt behaviour). `readCompleted` /
/// `recordCompleted` are the ledger seam: a hook already recorded for
/// this `(scopeId, phase)` is skipped (idempotent re-run).
let runResumable
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (readCompleted: unit -> Async<Set<string>>)
    (recordCompleted: LifecycleHookOutcome -> Async<unit>)
    (onProgress: LifecycleSummary -> Async<unit>)
    (retryPolicy: LifecycleRetryPolicy)
    (timeout: TimeSpan)
    (hooks: ITenantLifecycle list)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<LifecycleSummary> =
    async {
        let sw = Stopwatch.StartNew()
        let! alreadyDone = readCompleted ()

        // Hooks completed on a prior (crashed) attempt — recorded as
        // Completed outcomes (elapsed 0) WITHOUT re-invoking, so the
        // summary reaches its terminal marker without re-running them.
        let resumed =
            hooks
            |> List.filter (fun h -> Set.contains h.Name alreadyDone)
            |> List.map (fun h -> {
                HookName = h.Name
                Result = LifecycleHookResult.Completed
                ElapsedMs = 0L
            })

        let outcomes = ResizeArray<LifecycleHookOutcome>(resumed)
        let pending = hooks |> List.filter (fun h -> not (Set.contains h.Name alreadyDone))

        for hook in pending do
            let! outcome = invokeHookWithRetry retryPolicy phase scopeId actorUserId timeout 1 hook
            outcomes.Add outcome

            match outcome.Result with
            | LifecycleHookResult.Completed
            | LifecycleHookResult.Skipped _ -> do! recordCompleted outcome
            | LifecycleHookResult.Failed _ -> ()

            // Stream partial progress after each hook resolves.
            do!
                onProgress {
                    ScopeId = scopeId
                    Phase = phase
                    Outcomes = List.ofSeq outcomes
                    TotalElapsedMs = sw.ElapsedMilliseconds
                }

        sw.Stop()

        let summary = {
            ScopeId = scopeId
            Phase = phase
            Outcomes = List.ofSeq outcomes
            TotalElapsedMs = sw.ElapsedMilliseconds
        }

        do! emitRunAudit emitAudit phase scopeId actorUserId summary
        return summary
    }

// ─── Phase 54c — offboard preview / dry-run ──────────────────────────
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
                        return! p.OnDeprovisionPreview(scopeId, actorUserId)
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

// ─── Phase 54j — export-then-erase bundle ────────────────────────────
//
// "Give me my data, then delete it" in one operation: produce the
// tenant's export archive (via the caller-supplied `runExport`, which
// drives the Phase 9h `IDataExporter` surface) and ONLY THEN run the
// erasure sweep. Fail-closed ordering (the load-bearing GDPR property):
// a failed export aborts the offboard before any destruction, so the
// tenant's data stays intact. The `TenantDataExported` audit row is
// emitted between the durable export and the erasure, so its presence
// proves the export committed first. `runExport` is a callback so the
// aggregator stays DI-free (the handler resolves `IDataExporter` /
// `IBlobStorage`); it returns the durable archive reference or an error.

/// Outcome of a lock-guarded export-then-erase bundle (Phase 54h). Mirrors
/// `GuardedRun` for the Phase 54j bundle: because the WHOLE bundle
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

/// `exportThenDeprovision` guarded by an explicit `ILifecycleLock` (Phase
/// 54h). Acquires the scope BEFORE the export so the whole bundle (export +
/// audit + erasure sweep) is one cross-replica-exclusive unit: under a
/// distributed lock a peer replica already mid-bundle yields
/// `AlreadyInProgress` and neither the export nor the erasure runs, closing
/// the window where two replicas produce concurrent export-then-erase
/// bundles of the same tenant (which the Phase 54j inline `runGuarded` —
/// process-local, and guarding only the erasure — left open). Fail-closed
/// ordering is preserved inside the lease (a failed export is `ExportFailed`,
/// no erasure). The erasure runs via `runWithDefaults` — NOT `runGuarded` —
/// because the lease is already held; re-entering the same-scope in-process
/// semaphore would deadlock. Under the in-process default the acquire blocks
/// until the scope frees, so this is always `Ran` / `ExportFailed` (Phase 54j
/// behaviour preserved, now also serialising the export in-process).
let exportThenDeprovisionWith
    (lock: ILifecycleLock)
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (runExport: unit -> Async<Result<LifecycleExportArchive, string>>)
    (hooks: ITenantLifecycle list)
    (scopeId: string)
    (actorUserId: string)
    : Async<GuardedExportThenDeprovision> =
    async {
        match! lock.Acquire scopeId with
        | None -> return GuardedExportThenDeprovision.AlreadyInProgress
        | Some lease ->
            try
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
                        ScopeId = scopeId
                        Actor = actorUserId
                        ArchiveContainer = archive.Container
                        ArchivePath = archive.BlobPath
                        ContentHash = archive.ContentHash
                        SegmentCount = archive.SegmentCount
                    }

                    do! emitAudit scopeId (AuditEvent.TenantDataExported payload)

                    // The lease is already held, so run the sweep directly
                    // (`runWithDefaults`, not `runGuarded`) — re-entering the
                    // same-scope in-process lock would deadlock.
                    let! summary = runWithDefaults emitAudit hooks Deprovisioning scopeId actorUserId

                    return GuardedExportThenDeprovision.Ran { Summary = summary; Archive = archive }
            finally
                lease.Dispose()
    }

/// Export-then-erase: run `runExport` (export the scope's data durably),
/// then — only on success — audit `TenantDataExported` and run the
/// `Deprovisioning` sweep. A failed export returns `Error` and runs NO
/// erasure hook (fail-closed). Returns the erasure summary + the archive
/// reference.
///
/// Phase 54h — delegates to `exportThenDeprovisionWith` over the
/// process-local `InProcessLifecycleLock`, whose `Acquire` blocks until the
/// scope frees, so single-instance behaviour is preserved (the export now
/// also serialises in-process against a concurrent same-scope bundle). A
/// deployment that needs cross-replica exclusion composes a distributed
/// `ILifecycleLock` and calls `exportThenDeprovisionWith` instead.
let exportThenDeprovision
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (runExport: unit -> Async<Result<LifecycleExportArchive, string>>)
    (hooks: ITenantLifecycle list)
    (scopeId: string)
    (actorUserId: string)
    : Async<Result<ExportThenDeprovisionResult, string>> =
    async {
        match!
            exportThenDeprovisionWith InProcessLifecycleLock.shared emitAudit runExport hooks scopeId actorUserId
        with
        | GuardedExportThenDeprovision.Ran result -> return Ok result
        | GuardedExportThenDeprovision.ExportFailed err -> return Error err
        | GuardedExportThenDeprovision.AlreadyInProgress ->
            // Unreachable with the in-process lock (it blocks, never
            // refuses); kept total so the match is exhaustive.
            return Error "offboard already in progress for this scope"
    }