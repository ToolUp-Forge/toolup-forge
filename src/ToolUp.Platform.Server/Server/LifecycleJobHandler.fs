// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.LifecycleJobHandler

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PlatformTenantApiHandler

// ─── Phase 54a/54b — background offboard job handler ─────────────────
//
// `IJobHandler` registered under `TenantLifecycleAggregator.
// LifecycleJobHandlerName`. Runs the offboard sweep on an `IJobScheduler`
// job so a 25-minute multi-store erasure survives a process restart
// (`IJobStore` re-dispatches the persisted job) and never blocks an HTTP
// thread — `DeprovisionTenantAsync` returns a `LifecycleJobHandle`
// promptly while this executes in the background.
//
// **Restart survival + retry (Phase 54b).** The sweep runs through
// `TenantLifecycleAggregator.runResumable`, consulting an
// `ILifecycleLedger`: each terminal-success hook (`Completed` /
// `Skipped`) is recorded as it lands, so a process killed mid-sweep
// re-dispatches the same job, skips the already-completed hooks, and
// resumes from the last incomplete one — reaching the
// `TenantDeprovisioned` marker without re-running anything. A `Failed`
// hook is retried per `LifecycleRetryPolicy` within the sweep and, if
// still failing, left unrecorded so the re-dispatch retries it. The
// ledger is cleared on a clean finish. (Phase 54a shipped this as an
// inline blob record; 54b lifted it into the portable `ILifecycleLedger`
// seam + added retry.)
//
// **Progress streaming.** Each intermediate summary is pushed to the
// process-local `TenantLifecycleSnapshot` (and the durable Phase 54e
// store on completion), so `GetLifecycleSummary` reflects running →
// N-of-M progress while the job is in flight.
//
// **Stateless between invocations (GP 12 rule 4).** The handler resolves
// its substrate (`seq<ITenantLifecycle>`, `IAuditLog`,
// `TenantLifecycleSnapshot`, `ILifecycleSummaryStore`, `ILifecycleLedger`)
// from the injected `IServiceProvider` on every `Execute`; no state
// survives across dispatches except the durable ledger.

type LifecycleJobHandler(services: IServiceProvider) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match TenantLifecycleAggregator.LifecycleJobPayload.parse ctx.Payload with
            | Error e ->
                // A malformed payload will not recover on retry.
                return PermanentFailure(sprintf "malformed tenant-lifecycle payload: %s" e)
            | Ok(phase, scopeId, actorUserId) ->
                let hooks =
                    match services.GetService(typeof<seq<ITenantLifecycle>>) with
                    | :? seq<ITenantLifecycle> as hs -> List.ofSeq hs
                    | _ -> []

                let auditLog =
                    match services.GetService(typeof<IAuditLog>) with
                    | :? IAuditLog as a -> Some a
                    | _ -> None

                let snapshot =
                    match services.GetService(typeof<TenantLifecycleSnapshot>) with
                    | :? TenantLifecycleSnapshot as s -> s
                    | _ -> TenantLifecycleSnapshot()

                // Phase 54e — durable backing so a `GetLifecycleSummary`
                // on a fresh replica / post-restart cold cache still sees
                // the last run. `None` in minimal/test wiring.
                let summaryStore =
                    match services.GetService(typeof<ILifecycleSummaryStore>) with
                    | :? ILifecycleSummaryStore as s -> Some s
                    | _ -> None

                // Best-effort by contract — a sink failure cannot fail
                // an offboard (mirrors the inline handler's seam).
                let emitAudit (scope: string) (event: AuditEvent) =
                    match auditLog with
                    | Some a -> a.Record(scope, event)
                    | None -> async { return () }

                // Persist the terminal summary durably (best-effort) +
                // refresh the process-local cache — mirrors
                // `PlatformTenantApiHandler.persist`. A durable-store
                // outage degrades to a stale admin read, never a failed
                // offboard (the hooks ran, the audit trail recorded it).
                let persistFinal (summary: LifecycleSummary) = async {
                    match summaryStore with
                    | Some store ->
                        try
                            do! store.SetLast(scopeId, summary)
                        with _ ->
                            ()
                    | None -> ()

                    snapshot.Set(scopeId, summary)
                }

                // Phase 54b — resumability ledger. Prefer a composed
                // ILifecycleLedger; else build the blob-backed default from
                // IBlobStorage. None only in minimal/test wiring with no
                // blob store at all → the non-resumable fallback below.
                let ledger =
                    match services.GetService(typeof<ILifecycleLedger>) with
                    | :? ILifecycleLedger as l -> Some l
                    | _ ->
                        match services.GetService(typeof<IBlobStorage>) with
                        | :? IBlobStorage as blob -> Some(BlobBackedLifecycleLedger.create blob)
                        | _ -> None

                match ledger with
                | Some ledger ->
                    try
                        let recordToLedger (outcome: LifecycleHookOutcome) =
                            let disposition =
                                match outcome.Result with
                                | LifecycleHookResult.Skipped _ -> LedgerDisposition.Skipped
                                | _ -> LedgerDisposition.Completed

                            ledger.Record(scopeId, phase, outcome.HookName, disposition)

                        let! summary =
                            TenantLifecycleAggregator.runResumable
                                emitAudit
                                (fun () -> ledger.GetCompleted(scopeId, phase))
                                recordToLedger
                                (fun partial -> async { snapshot.Set(scopeId, partial) })
                                LifecycleRetryPolicy.defaults
                                (TenantLifecycleAggregator.defaultTimeout phase)
                                hooks
                                phase
                                scopeId
                                actorUserId

                        do! persistFinal summary
                        do! ledger.Clear(scopeId, phase)
                        return Success
                    with ex ->
                        // Transient — the job re-dispatches; the ledger
                        // lets the retry resume from the last completed hook.
                        return TransientFailure ex.Message
                | None ->
                    // No ledger backing at all — fall back to a
                    // non-resumable inline sweep (still off the HTTP thread,
                    // still survives at the job level via IJobStore
                    // re-dispatch, but re-runs every hook on restart). The
                    // first-party hooks are idempotent, so a full re-run is
                    // safe.
                    let! summary = TenantLifecycleAggregator.runWithDefaults emitAudit hooks phase scopeId actorUserId

                    do! persistFinal summary
                    return Success
        }

/// Construct the background offboard job handler. Resolves its substrate
/// from `services` on every `Execute` (stateless between invocations).
let create (services: IServiceProvider) : IJobHandler =
    LifecycleJobHandler(services) :> IJobHandler