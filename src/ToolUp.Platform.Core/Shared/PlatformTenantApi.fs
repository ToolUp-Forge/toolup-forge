// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IPlatformTenantApi (Phase 54 Fable.Remoting wire surface) ───────
//
// Deployment-wide tenant-lifecycle operations for the operator/admin
// surface. Drives the `ITenantLifecycle` aggregator: one
// `DeprovisionTenant` call runs every registered `OnDeprovisioned`
// hook for the scope (crypto-shred, membership-cache eviction,
// scheduled-job cancellation, subject-data erasure, plus any companion
// hook) with audit + per-hook isolation, and returns the aggregated
// `LifecycleSummary`.
//
// **Gating.** Every method is Owner / Platform-Admin only (Phase 4b),
// enforced server-side in `PlatformTenantApiHandler` via
// `canModifyPlatformConfig`. Non-admin callers receive
// `Error "platform admin role required"` so the client surfaces a
// uniform error banner. No `[<AllowAnonymous>]` member exists — tenant
// lifecycle is a destructive operator action, never client-self-serve.
//
// **Opt-in.** The route is mounted only when
// `ServerConfig.TenantLifecycle = EnabledTenantLifecycle`; the default
// `NoTenantLifecycle` skips the mount entirely so the proxy surface
// 404s and no lifecycle hooks are resolved (GP 13 — zero cost when
// unused).
//
// **Route shape.** Fable.Remoting member routes resolve at
// `/api/_platform/tenants/{methodName}` (the `scopeId` rides in the
// request body as the first tuple element, per the Fable.Remoting
// convention the other `_platform` admin APIs use). The phase's
// illustrative `POST /api/_platform/tenants/{scopeId}/deprovision`
// path-param shape is the same operation expressed RESTfully; the
// typed contract is the canonical surface.
//
// **`ProvisioningRequest` parameter.** `ProvisionTenant` carries the
// deploy-plane `ProvisioningRequest` (Phase 26) for forward
// compatibility — a future hook can read provisioning context (region,
// tier, slug) from it. The v1 first-party `OnProvisioned` hooks consume
// only `(scopeId, actorUserId)`; the request is forwarded to the
// aggregator's hook context unchanged.

type IPlatformTenantApi = {
    /// Run every registered `OnProvisioned` hook for `scopeId`,
    /// attributed to `actorUserId`, with `request` as provisioning
    /// context. Returns the aggregated `LifecycleSummary`. Owner /
    /// Platform-Admin only; non-admins receive
    /// `Error "platform admin role required"`. Per-hook failure does
    /// NOT abort the run — the summary records the partial state and a
    /// `TenantLifecycleHookFailed` audit row fires per failed hook.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "TenantCreated">]
    ProvisionTenant: string * string * ProvisioningRequest -> Async<Result<LifecycleSummary, string>>

    /// Run every registered `OnDeprovisioned` hook for `scopeId`,
    /// attributed to `actorUserId`, with the operator-supplied
    /// `reason`. Returns the aggregated `LifecycleSummary` + writes the
    /// `TenantDeprovisioned` audit envelope. Owner / Platform-Admin
    /// only. The offboard runs every hook even if one fails (partial
    /// state recorded), so a single misbehaving companion hook cannot
    /// block the crypto-shred / erasure of the rest.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "TenantDeleted">]
    DeprovisionTenant: string * string * string -> Async<Result<LifecycleSummary, string>>

    /// Read the most recent lifecycle run's `LifecycleSummary` for
    /// `scopeId` (process-local best-effort snapshot — `None` when no
    /// run has happened in this process since startup). The durable
    /// record is the audit trail; this surface exists so the admin UI
    /// can render "last offboard: 3 hooks completed, 1 skipped" without
    /// replaying audit events. Owner / Platform-Admin only.
    [<RequiresRole "PlatformAdmin">]
    GetLifecycleSummary: string -> Async<Result<LifecycleSummary option, string>>

    // ─── Phase 54a — async / inline offboard (append-only) ───────────
    //
    // `DeprovisionTenant` (above) keeps its original inline,
    // summary-returning semantics for backward compatibility (GP 11);
    // the two methods below add the explicit inline + the background
    // paths without changing it.

    /// Phase 54a — explicit inline (synchronous) offboard. Identical
    /// semantics to `DeprovisionTenant`: runs every `OnDeprovisioned`
    /// hook inline and returns the aggregated `LifecycleSummary`. Named
    /// so callers / tests can request the inline path unambiguously even
    /// after the async path exists. Owner / Platform-Admin only.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "TenantDeleted">]
    DeprovisionTenantSync: string * string * string -> Async<Result<LifecycleSummary, string>>

    /// Phase 54a — background / async offboard. Enqueues an
    /// `IJobScheduler`-backed lifecycle job and returns a
    /// `LifecycleJobHandle` promptly instead of awaiting the (potentially
    /// 25-minute) multi-store erasure inline; the job survives a process
    /// restart and resumes from the last completed hook. Poll progress
    /// via `GetLifecycleSummary scopeId`. Returns
    /// `Error "background offboard requires an IJobScheduler…"` when no
    /// scheduler is composed (use `DeprovisionTenant` /
    /// `DeprovisionTenantSync` for the inline path). Owner /
    /// Platform-Admin only.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "TenantDeleted">]
    DeprovisionTenantAsync: string * string * string -> Async<Result<LifecycleJobHandle, string>>

    /// Phase 54c — preview the offboard's blast radius **without mutating
    /// anything**: per-hook would-affect counts (the encryption key that
    /// would be destroyed, the member-cache entries that would be
    /// invalidated, the scheduled jobs that would be cancelled, the
    /// per-store records that would be erased) before the irreversible
    /// `DeprovisionTenant`. A hook that opts out of preview surfaces a
    /// clear "no preview available" item. Owner / Platform-Admin only;
    /// emits no destructive audit (read-only dry-run).
    [<RequiresRole "PlatformAdmin">]
    PreviewDeprovision: string -> Async<Result<LifecyclePreview, string>>

    /// Phase 54j — export-then-erase: produce the tenant's data-export
    /// archive (via the registered `IDataExporter`s) as a durable
    /// pre-step, then run the offboard. **Fail-closed ordering**: the
    /// erasure hooks run only after the export has been durably written;
    /// a failed export aborts the offboard before any destruction, so the
    /// tenant's data stays intact. Returns the erasure summary + the
    /// archive reference (blob path + content hash) so the operator can
    /// hand the archive to the departing customer. Owner / Platform-Admin
    /// only; `TenantDataExported` is audited before the erasure sweep.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "TenantDeleted">]
    ExportThenDeprovision: string * string * string -> Async<Result<ExportThenDeprovisionResult, string>>
}

module PlatformTenantApi =
    /// Fable.Remoting route builder. Mirrors `IPlatformTenantApi` member
    /// names; consumed on both the server (registration) and client
    /// (proxy construction). Surface lives under the reserved
    /// `/api/_platform/tenants/` prefix so admin clients discover the
    /// endpoint by path alone.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_platform/tenants/%s" methodName