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

    // ─── Phase 54i — confirmation-gated offboard (append-only) ───────
    //
    // When `ServerConfig.TenantOffboardConfirmation` is `TokenConfirmation`
    // / `TwoPersonRule`, the token-less destructive methods above
    // (`DeprovisionTenant` / `…Sync` / `…Async` / `ExportThenDeprovision`)
    // are refused with `Error "offboard confirmation required"`, and an
    // offboard must go through the two methods below. Under the default
    // `NoConfirmation` the token-less methods behave exactly as Phase 54
    // (GP 11) and these two are still callable (the confirmed path always
    // works; the gate just isn't required).

    /// Phase 54i — mint a short-lived confirmation token for a pending
    /// offboard of `scopeId`, capturing the operator's `reason`. Backed by
    /// `IShareTokenStore`; the returned `OffboardConfirmation.Token` is
    /// replayed into `DeprovisionTenantConfirmed`. Returns
    /// `Error "offboard confirmation requires an IShareTokenStore…"` when
    /// no share-token substrate is composed. Owner / Platform-Admin only;
    /// emits `TenantOffboardConfirmationRequested` (no destruction).
    [<RequiresRole "PlatformAdmin">]
    RequestDeprovisionToken: string * string -> Async<Result<OffboardConfirmation, string>>

    /// Phase 54i — execute a confirmation-gated offboard: validate the
    /// `confirmationToken` (in-scope, unexpired, unused) and, under
    /// `TwoPersonRule`, that the redeeming admin differs from the
    /// requester; then run the same offboard as `DeprovisionTenant` and
    /// return the `LifecycleSummary`. A missing/expired/wrong-scope token,
    /// or a same-admin redemption under `TwoPersonRule`, is refused with
    /// `Error "offboard confirmation required"` (and a
    /// `TenantOffboardConfirmationRefused` audit row) before any
    /// destruction. On success the token is consumed (one-time) and
    /// `TenantOffboardConfirmationApproved` is audited. Tuple:
    /// `(scopeId, actorUserId, reason, confirmationToken)`. Owner /
    /// Platform-Admin only.
    [<RequiresRole "PlatformAdmin">]
    [<Audit "TenantDeleted">]
    DeprovisionTenantConfirmed: string * string * string * string -> Async<Result<LifecycleSummary, string>>

    // ─── Phase 54f — scheduled / grace-period offboard (append-only) ─

    /// Phase 54f — schedule a tenant offboard to fire after a cancellable
    /// grace window ("deprovision team-X in 30 days unless cancelled")
    /// rather than the immediate `DeprovisionTenant`. Registers an
    /// `IJobScheduler` poll job under the tenant scope; the offboard fires
    /// after `afterDays`, attributed to `actorUserId`. Idempotent per
    /// scope — a second schedule inside the window returns the existing
    /// pending offboard. Returns the pending `ScheduledDeprovision` (incl.
    /// the backing job id + due time). Returns
    /// `Error "scheduled offboard requires an IJobScheduler…"` when no
    /// scheduler is composed (immediate `DeprovisionTenant` still works).
    /// Tuple: `(scopeId, actorUserId, afterDays, reason)`. Owner /
    /// Platform-Admin only; audits `TenantDeprovisionScheduled`.
    [<RequiresRole "PlatformAdmin">]
    ScheduleDeprovision: string * string * int * string -> Async<Result<ScheduledDeprovision, string>>

    /// Phase 54f — cancel the pending grace-period offboard for `scopeId`
    /// before it fires (no hooks run). Idempotent — cancelling when
    /// nothing is pending returns `Ok ()`. Owner / Platform-Admin only;
    /// audits `TenantDeprovisionCancelled` when a pending offboard was
    /// actually cancelled.
    [<RequiresRole "PlatformAdmin">]
    CancelScheduledDeprovision: string -> Async<Result<unit, string>>

    /// Phase 54f — read the pending grace-period offboard for `scopeId`
    /// (the due window + reason + requester) for the admin UI / countdown
    /// banner. `None` when nothing is scheduled. Owner / Platform-Admin
    /// only; read-only, no audit.
    [<RequiresRole "PlatformAdmin">]
    GetScheduledDeprovision: string -> Async<Result<ScheduledDeprovision option, string>>

    // ─── Phase 543 — derived principal enumeration (append-only) ─────

    /// Phase 543 — enumerate every principal the substrate has evidence
    /// for: `_platform/memberships/` blob owners, `user-*` storage
    /// scopes, and distinct `UserLoggedIn` audit subjects within a
    /// bounded look-back window, merged per `UserId`
    /// (`PrincipalRegistry.listPrincipals`). A **derived, read-only
    /// projection** — no stored registry, nothing to drift; a fresh call
    /// reflects a membership added one second ago. `TeamLess = true`
    /// exactly when no membership row exists — the surface behind every
    /// stray team-less-login investigation. Owner / Platform-Admin only;
    /// read-only, no audit. Returns a clear error when the storage /
    /// event-store substrate is not resolvable (fail-closed, GP 13).
    [<RequiresRole "PlatformAdmin">]
    ListPrincipals: unit -> Async<Result<PrincipalSummary list, string>>
}

module PlatformTenantApi =
    /// Fable.Remoting route builder. Mirrors `IPlatformTenantApi` member
    /// names; consumed on both the server (registration) and client
    /// (proxy construction). Surface lives under the reserved
    /// `/api/_platform/tenants/` prefix so admin clients discover the
    /// endpoint by path alone.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_platform/tenants/%s" methodName