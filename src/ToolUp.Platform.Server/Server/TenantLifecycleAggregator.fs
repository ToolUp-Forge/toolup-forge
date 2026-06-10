module ToolUp.Platform.TenantLifecycleAggregator

open System
open System.Collections.Concurrent
open System.Diagnostics
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

        // One audit row per failed hook (non-aborting — the run already
        // completed every hook above).
        for o in outcomes do
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

        // The single end-of-phase marker.
        let completed = LifecycleSummary.completedCount summary
        let skipped = LifecycleSummary.skippedCount summary
        let failed = LifecycleSummary.failedCount summary

        match phase with
        | Provisioning ->
            let payload: TenantProvisionedPayload = {
                ScopeId = scopeId
                Actor = actorUserId
                HooksRun = outcomes.Length
                HooksCompleted = completed
                HooksSkipped = skipped
                HooksFailed = failed
                ElapsedMs = sw.ElapsedMilliseconds
            }

            do! emitAudit scopeId (AuditEvent.TenantProvisioned payload)
        | Deprovisioning ->
            let payload: TenantDeprovisionedPayload = {
                ScopeId = scopeId
                Actor = actorUserId
                HooksRun = outcomes.Length
                HooksCompleted = completed
                HooksSkipped = skipped
                HooksFailed = failed
                ElapsedMs = sw.ElapsedMilliseconds
            }

            do! emitAudit scopeId (AuditEvent.TenantDeprovisioned payload)

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