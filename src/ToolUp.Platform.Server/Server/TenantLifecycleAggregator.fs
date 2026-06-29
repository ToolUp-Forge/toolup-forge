module ToolUp.Platform.TenantLifecycleAggregator

open System
open System.Collections.Concurrent
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
// for the *same* scope via a per-scope `SemaphoreSlim` so two operators
// (or a double-clicked admin button) can't interleave two offboards of
// one tenant. Different scopes never contend. Process-local — a
// distributed deployment that needs cross-replica offboard exclusion
// layers its own lock (the audit trail is the cross-replica record of
// truth regardless).

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

// Per-scope semaphores backing `runGuarded`. Keyed by scope id; a
// scope's first guarded run lazily creates its semaphore. Never
// removed — the count of distinct tenant scopes a process offboards in
// its lifetime is bounded and small, so the leak is immaterial and
// removal would race the acquire path.
let private scopeLocks = ConcurrentDictionary<string, SemaphoreSlim>()

let private lockFor (scopeId: string) : SemaphoreSlim =
    scopeLocks.GetOrAdd(scopeId, fun _ -> new SemaphoreSlim(1, 1))

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

/// `runWithDefaults` serialised per scope: concurrent runs for the same
/// `scopeId` execute one-at-a-time (per-scope `SemaphoreSlim`), so two
/// operators offboarding the same tenant don't interleave. Different
/// scopes run fully in parallel. Use this from the request handler;
/// the contract pack exercises both `run` (parallel/timeout/isolation)
/// and `runGuarded` (per-scope serialisation) directly.
let runGuarded
    (emitAudit: string -> AuditEvent -> Async<unit>)
    (hooks: ITenantLifecycle list)
    (phase: TenantLifecyclePhase)
    (scopeId: string)
    (actorUserId: string)
    : Async<LifecycleSummary> =
    async {
        let gate = lockFor scopeId
        do! gate.WaitAsync() |> Async.AwaitTask

        try
            return! runWithDefaults emitAudit hooks phase scopeId actorUserId
        finally
            gate.Release() |> ignore
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