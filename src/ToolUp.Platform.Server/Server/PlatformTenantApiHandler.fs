module ToolUp.Platform.PlatformTenantApiHandler

open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Phase 54 — IPlatformTenantApi server handler ────────────────────
//
// Drives the tenant-lifecycle aggregator from the
// `/api/_platform/tenants/*` admin surface. Per-request DI resolution
// (mirrors the other `_platform` admin handlers): the registered
// `ITenantLifecycle` hooks, `IAuditLog`, the process-local
// `TenantLifecycleSnapshot`, and the caller's `AccessContext` are all
// resolved per call so a substrate that isn't wired can't cascade a
// failure into route construction.
//
// **Server-authoritative actor.** The wire `actorUserId` parameter is
// advisory (API shape / forward compat); the handler pins the *actual*
// actor to the authenticated caller's `AccessContext.UserId` so the
// audit trail can't be spoofed by a client-supplied id. The whole
// surface is Owner / Platform-Admin gated via
// `AccessContext.canModifyPlatformConfig`; a missing `AccessContext`
// (no resolved auth) is treated as non-admin — fail-closed.

/// Process-local holder of the most-recent `LifecycleSummary` per scope.
/// Registered as a DI singleton when `TenantLifecycle` is enabled.
///
/// **Phase 54e — this is now a read-through cache** in front of the
/// durable `ILifecycleSummaryStore`: written after each run alongside the
/// durable store, and read first by `GetLifecycleSummary` (a miss reads
/// the durable store and back-fills here). On a fresh replica or after a
/// restart the cache is cold, so the read falls through to the durable
/// store — that is the cross-replica/restart-survival fix. When no
/// durable store is registered (minimal/test wiring) this remains the
/// sole backing, preserving prior Phase 54 process-local behaviour. The
/// audit trail (`TenantProvisioned` / `TenantDeprovisioned`) remains the
/// durable record of the *fact* a run happened.
type TenantLifecycleSnapshot() =
    let last = ConcurrentDictionary<string, LifecycleSummary>()

    member _.Set(scopeId: string, summary: LifecycleSummary) = last[scopeId] <- summary

    member _.Get(scopeId: string) : LifecycleSummary option =
        match last.TryGetValue scopeId with
        | true, s -> Some s
        | false, _ -> None

/// Uniform refusal message for non-admin callers — matches the
/// `PlatformAdminApi` banner so the client surfaces one consistent error.
let adminError = "platform admin role required"

/// Build the `IPlatformTenantApi` for one request.
let platformTenantApi (ctx: HttpContext) : IPlatformTenantApi =
    let services = ctx.RequestServices

    let resolveHooks () =
        match services.GetService(typeof<seq<ITenantLifecycle>>) with
        | :? seq<ITenantLifecycle> as hooks -> List.ofSeq hooks
        | _ -> []

    let auditLog =
        match services.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    let snapshot =
        match services.GetService(typeof<TenantLifecycleSnapshot>) with
        | :? TenantLifecycleSnapshot as s -> s
        | _ -> TenantLifecycleSnapshot()

    // Phase 54e — durable backing for the last summary. Registered under
    // `EnabledTenantLifecycle`; `None` only in minimal/test wiring, where
    // the process-local snapshot remains the sole backing (prior Phase 54
    // behaviour). The snapshot is a read-through cache in front of it.
    let summaryStore =
        match services.GetService(typeof<ILifecycleSummaryStore>) with
        | :? ILifecycleSummaryStore as s -> Some s
        | _ -> None

    let accessContext =
        match services.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ -> None

    // Phase 54a — optional background-job substrate. `None` when no
    // scheduler is composed; `DeprovisionTenantAsync` then returns a clear
    // "requires an IJobScheduler" error and the inline paths are
    // unaffected (GP 13).
    let scheduler =
        match services.GetService(typeof<IJobScheduler>) with
        | :? IJobScheduler as s -> Some s
        | _ -> None

    // Phase 54b — completed-step offboard ledger. A fresh provision of a
    // scope supersedes any prior offboard ledger (re-onboarding resets
    // it), so the inline provision path clears the Deprovisioning ledger.
    // `None` in minimal/test wiring — then the clear is a no-op.
    let ledger =
        match services.GetService(typeof<ILifecycleLedger>) with
        | :? ILifecycleLedger as l -> Some l
        | _ -> None

    let clearOffboardLedger (scopeId: string) = async {
        match ledger with
        | Some l ->
            try
                do! l.Clear(scopeId, Deprovisioning)
            with _ ->
                () // best-effort — a stale ledger only costs a redundant skip
        | None -> ()
    }

    // Server-authoritative actor — the authenticated caller, never the
    // wire-supplied id. Empty when no AccessContext resolved (which also
    // fails the admin gate below).
    let actor = accessContext |> Option.map _.UserId |> Option.defaultValue ""

    let isAdmin =
        accessContext
        |> Option.map AccessContext.canModifyPlatformConfig
        |> Option.defaultValue false

    // Wire the aggregator's audit-emit seam to IAuditLog.Record (a no-op
    // when audit is off). IAuditLog.Record is best-effort by contract —
    // a sink failure can't fail an offboard.
    let emitAudit (scopeId: string) (event: AuditEvent) =
        match auditLog with
        | Some a -> a.Record(scopeId, event)
        | None -> async { return () }

    // Phase 54e — persist the run's summary durably (best-effort) and
    // refresh the process-local cache. A durable-store failure must not
    // fail an offboard: the hooks already ran and the audit trail already
    // recorded the run, so a blob-write outage degrades to "the admin UI
    // reads a stale/empty summary", never "the offboard errored".
    let persist (scopeId: string) (summary: LifecycleSummary) = async {
        match summaryStore with
        | Some store ->
            try
                do! store.SetLast(scopeId, summary)
            with _ ->
                () // swallowed by contract — see comment above
        | None -> ()

        snapshot.Set(scopeId, summary)
    }

    // Phase 54e — read-through cache. The process-local snapshot answers a
    // hit directly; on a miss (fresh replica, post-restart process) read
    // the durable store and populate the cache so subsequent reads are
    // local. With no durable store registered this is the prior Phase 54
    // process-local-only behaviour.
    let readSummary (scopeId: string) : Async<LifecycleSummary option> = async {
        match snapshot.Get scopeId with
        | Some s -> return Some s
        | None ->
            match summaryStore with
            | Some store ->
                match! store.GetLast scopeId with
                | Some s ->
                    snapshot.Set(scopeId, s)
                    return Some s
                | None -> return None
            | None -> return None
    }

    {
        ProvisionTenant =
            fun (scopeId, _wireActor, _request) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary =
                        TenantLifecycleAggregator.runGuarded emitAudit (resolveHooks ()) Provisioning scopeId actor

                    do! persist scopeId summary
                    // Phase 54b — re-onboarding supersedes a prior offboard
                    // ledger so a future offboard of this scope starts fresh.
                    do! clearOffboardLedger scopeId
                    return Ok summary
            }

        DeprovisionTenant =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary =
                        TenantLifecycleAggregator.runGuarded emitAudit (resolveHooks ()) Deprovisioning scopeId actor

                    do! persist scopeId summary
                    return Ok summary
            }

        GetLifecycleSummary =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary = readSummary scopeId
                    return Ok summary
            }

        // Phase 54a — explicit inline offboard. Same path as
        // `DeprovisionTenant` above (runGuarded inline + persist); named so
        // callers/tests can request the inline path unambiguously.
        DeprovisionTenantSync =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary =
                        TenantLifecycleAggregator.runGuarded emitAudit (resolveHooks ()) Deprovisioning scopeId actor

                    do! persist scopeId summary
                    return Ok summary
            }

        // Phase 54a — background offboard. Enqueues + fires a lifecycle
        // job and returns the handle promptly; the background
        // `LifecycleJobHandler` runs the resumable sweep and persists the
        // summary as it progresses. Requires a composed `IJobScheduler`.
        DeprovisionTenantAsync =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    match scheduler with
                    | None ->
                        return
                            Error
                                "background offboard requires an IJobScheduler (compose JobScheduler = InProcessJobScheduler); use DeprovisionTenant / DeprovisionTenantSync for the inline path"
                    | Some sch -> return! TenantLifecycleAggregator.enqueue sch Deprovisioning scopeId actor
            }

        // Phase 54c — read-only offboard preview. No mutation, no
        // destructive audit; aggregates each hook's would-affect item.
        PreviewDeprovision =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! preview = TenantLifecycleAggregator.previewDeprovision (resolveHooks ()) scopeId actor
                    return Ok preview
            }

        // Phase 54j — export-then-erase. The export step resolves
        // IDataExporter / IBlobStorage from DI (via exportArchive); the
        // aggregator enforces the fail-closed ordering (export durably,
        // audit, then erase). Persist the erasure summary on success.
        ExportThenDeprovision =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let runExport () =
                        DataSubjectRequestLifecycle.exportArchive services scopeId

                    match!
                        TenantLifecycleAggregator.exportThenDeprovision
                            emitAudit
                            runExport
                            (resolveHooks ())
                            scopeId
                            actor
                    with
                    | Error e -> return Error e
                    | Ok result ->
                        do! persist scopeId result.Summary
                        return Ok result
            }
    }