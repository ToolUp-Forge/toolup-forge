// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 9p.A — IServiceStatusBoardApi (unified operator status) ──
//
// Platform-Admin-gated read-only Fable.Remoting surface returning a
// composite `ServiceStatusSnapshot` plus per-section live refresh
// methods. The handler composes existing audited interfaces; this is
// not a new substrate interface and no GP-12 audit applies.
//
// **Anonymous mode.** Returns `Error` from every method — anonymous
// deployments have no role concept to gate on; surfacing deployment-
// wide dependency state to every visitor is a reconnaissance gift.
// The shell skips the sidebar entry on the client too (defence in
// depth — the handler check is the source of truth).
//
// **No streaming.** The board polls on operator click; SSE / push is
// out of scope per the phase body. Refused for ROI reasons — operator
// inspection is rare enough that a fresh fan-out on click is cheaper
// than holding an SSE connection open for every admin user.

/// Owner / Platform-Admin-gated composite status board. Auto-mounted
/// by `compose` — Anonymous-mode callers and non-admin callers both
/// receive `Error`. `Result<_, string>` matches the established
/// Fable.Remoting failure shape (`IHealthMonitorApi`, `IConfigApi`).
type IServiceStatusBoardApi = {
    /// Build the composite snapshot — fans out to every section
    /// builder in parallel with per-section timeout. Disabled
    /// substrates are skipped (no underlying read; section reports
    /// `Disabled = true`). One Fable.Remoting call per snapshot.
    GetSnapshot: unit -> Async<Result<ServiceStatusSnapshot, string>>

    /// Re-run every registered `IHealthCheck` and return the fresh
    /// section summary. Same per-probe timeout as the dedicated
    /// `IHealthMonitorApi.GetCurrentHealth`.
    RefreshHealth: unit -> Async<Result<HealthSummary, string>>

    /// Re-read `IPreflightSnapshot.LastRun`. Validators are
    /// heavier than probes (sentinel writes, DNS resolution) so we
    /// do not re-run them on refresh — the snapshot is the most
    /// recent boot's outcome; redeploying re-populates it.
    RefreshPreflight: unit -> Async<Result<PreflightSummary, string>>

    /// Re-read the most recent `ConfigDrift` audit event under the
    /// `_platform` scope. We do not re-run the drift detector
    /// (which writes to `IBlobStorage` and emits a fresh audit row)
    /// — a refresh is observational, not a state transition.
    RefreshDrift: unit -> Async<Result<DriftSummary, string>>

    /// Re-build the rate-limiter summary. Best-effort — the
    /// `IRateLimiter` substrate does not expose a cross-scope
    /// refused-count read today (per-scope audit rows live under
    /// each tenant scope, not `_platform`), so the section reports
    /// substrate enablement plus a pointer to the audit log rather
    /// than a live refused-count figure.
    RefreshRateLimit: unit -> Async<Result<RateLimitSummary, string>>

    /// Re-walk `IJobStore.ListScopesWithJobs()` and tally
    /// active / failed / disabled job counts across every scope
    /// with jobs. Bounded by the deployment's scope count × jobs-
    /// per-scope; cheap up to the few-thousand-jobs ceiling the
    /// blob-backed default targets.
    RefreshJobQueue: unit -> Async<Result<JobQueueSummary, string>>

    /// Re-read the most recent smoke-test audit event under the
    /// `_platform.diagnostics` source. We do not invoke
    /// `ISmokeTest.RunOnce` here — smoke tests can have side
    /// effects (sentinel writes against blob storage, transient
    /// notifications), and the deploy pipeline owns triggering
    /// them via the token-gated `/api/_internal/smoke` endpoint.
    RefreshSmokeTest: unit -> Async<Result<SmokeTestSummary, string>>
}