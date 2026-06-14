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
/// Registered as a DI singleton when `TenantLifecycle` is enabled; read
/// by `GetLifecycleSummary`, written after each run. Best-effort — the
/// durable record is the audit trail (`TenantProvisioned` /
/// `TenantDeprovisioned`); this exists so the admin UI can show the last
/// run's disposition without replaying audit events. A distributed
/// deployment sees per-replica snapshots; the audit trail remains the
/// cross-replica source of truth.
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

    let accessContext =
        match services.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ -> None

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

    {
        ProvisionTenant =
            fun (scopeId, _wireActor, _request) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary =
                        TenantLifecycleAggregator.runGuarded emitAudit (resolveHooks ()) Provisioning scopeId actor

                    snapshot.Set(scopeId, summary)
                    return Ok summary
            }

        DeprovisionTenant =
            fun (scopeId, _wireActor, _reason) -> async {
                if not isAdmin then
                    return Error adminError
                else
                    let! summary =
                        TenantLifecycleAggregator.runGuarded emitAudit (resolveHooks ()) Deprovisioning scopeId actor

                    snapshot.Set(scopeId, summary)
                    return Ok summary
            }

        GetLifecycleSummary =
            fun scopeId -> async {
                if not isAdmin then
                    return Error adminError
                else
                    return Ok(snapshot.Get scopeId)
            }
    }