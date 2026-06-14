// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── HealthMonitorApi (production-safe Owner/Admin surface) ──────────
//
// Read-only Fable.Remoting API surfacing live `IHealthCheck` results
// and the most recent `IConfigValidator` preflight outcomes
// to authenticated Owner/Admin operators through a built-in
// admin module — so production deployments don't have to enable the
// debug-only `/dev/inspect` endpoint to answer "did this deploy pass
// preflight?" / "is Redis alive right now?".
//
// **Probe visibility (team-isolation carve-out).** Health probes are deployment-
// wide — `blob_storage`, `redis-notification`, `oidc-auth` — they
// describe the deployment's external dependencies, not any one team's
// data. Every Owner/Admin in every team sees the full probe list. The
// API never returns per-tenant data; the read is read-only and the
// visible surface is identical across tenants. Documented inline so
// the choice is auditable.
//
// **Anonymous mode.** Returns `Error` for both methods — anonymous
// deployments have no role concept to gate on; surfacing deployment
// dependency state to every visitor is a reconnaissance gift. The
// client shell skips the sidebar entry too (defence in depth — the
// handler check is the source of truth).

/// One health-probe row crossing the wire. Mirrors `HealthCheckSummary`
/// in `DevDiagnosticsHandler` — same field shape, same string statuses,
/// so a future operator using both surfaces sees equivalent data.
type HealthProbeView = {
    Name: string
    /// "Liveness" | "Readiness" — string at the wire boundary so the
    /// `HealthKind` DU can evolve without breaking the API contract.
    Kind: string
    TimeoutMs: int
    /// "Healthy" | "Degraded" | "Unhealthy" — string for the same reason.
    Status: string
    Message: string
    ElapsedMs: int64
}

/// One preflight outcome row crossing the wire. Mirrors `ValidatorSummary`
/// in `DevDiagnosticsHandler` for the same reason.
type PreflightOutcomeView = {
    Name: string
    /// "Ok" | "Warning" | "Error".
    Status: string
    Message: string
    ElapsedMs: int64
}

type HealthSnapshot = {
    /// Server wall-clock at the moment the snapshot was generated.
    /// Operators use this to confirm a refresh actually re-ran probes
    /// (vs returning a cached read).
    GeneratedAt: DateTime
    Probes: HealthProbeView list
}

type PreflightSnapshotView = {
    /// `false` when `IPreflightSnapshot` is not registered in DI —
    /// older deployments composed before preflight validation landed,
    /// or `ServerConfig.SkipPreflight = true`. The UI distinguishes
    /// "no validators ran" from "snapshot service not present".
    HasSnapshot: bool
    Outcomes: PreflightOutcomeView list
}

/// Phase 9b.A — job-scheduler missed-tick view crossing the wire.
/// Wire-flat record of primitives so the API contract does not pull
/// in `IJobSchedulerTelemetry` types; the server-side mapper reads
/// the underlying `JobSchedulerTelemetrySnapshot` and projects.
///
/// `HasScheduler = false` covers two operationally identical cases the
/// UI surfaces as "scheduler not active in this deployment":
/// (a) `ServerConfig.JobScheduler = NoJobScheduler` — the SDK never
/// registered a scheduler, so no telemetry is meaningful;
/// (b) distributed companion registered but skipped its own
/// `IJobSchedulerTelemetry` registration. In both cases the UI
/// suppresses the panel rather than rendering a misleading zero.
type JobSchedulerTelemetryView = {
    HasScheduler: bool
    /// Distinct minute boundaries the scheduler detected as missed in
    /// the last 60 minutes. Zero on a normally-running deployment.
    TickMissedCount60Min: int
    /// Most-recent drift on a missed tick, milliseconds. `None` until
    /// a miss has been observed since process start.
    LastDriftMs: int64 option
    /// When the most-recent missed tick was detected (UTC). `None`
    /// until observed.
    LastTickMissedAt: DateTime option
    /// Wall-clock at the moment the snapshot was generated.
    GeneratedAt: DateTime
}

/// Phase 118 — a capability that a compose-time or runtime best-effort
/// site registered as *degraded*: it was supposed to be active but
/// failed to wire (or is currently down) without crashing startup. The
/// motivating instance is a failed cross-silo crypto-shred cache-eviction
/// subscribe — the deployment boots fine, but a destroyed encryption key
/// keeps decrypting on every other silo until restart, with zero signal.
/// Surfaced on `/health`, `/dev/inspect`, and this admin API so a
/// silently-downgraded capability is answerable without log archaeology
/// (GP 9). Empty set on a healthy deployment (GP 13).
///
/// Value-typed (GP 5); identity is `Capability` (the registry key).
/// Crosses the wire as-is (the HealthMonitorUI panel renders it), so it
/// lives in Core alongside the other `IHealthMonitorApi` view records.
type DegradedCapability = {
    /// Stable machine-readable capability id, e.g.
    /// `"crypto-shred-cache-eviction"`. Registry key — re-registering the
    /// same id refreshes reason/impact/remediation but PRESERVES the
    /// original `DegradedSince` (the first observation of the degradation).
    Capability: string
    /// When the capability was first observed degraded (UTC).
    DegradedSince: DateTimeOffset
    /// Operator-readable failure cause (what went wrong).
    Reason: string
    /// The consequence while degraded (what is broken / unsafe).
    Impact: string
    /// What an operator should do to restore the capability.
    Remediation: string
}

/// Owner/Admin-gated read-only Fable.Remoting surface. Auto-injected
/// by `compose` — `Anonymous` mode returns `Error` from both methods;
/// `Team` / `MultiTeam` require Owner or Admin role; `Individual` /
/// `AuthenticatedEphemeral` require an authenticated user.
///
/// `Result<_, string>` is the established Fable.Remoting failure
/// shape (`IWebhookApi`, `IConfigApi`, `IFeatureFlagApi`) — RBAC
/// denials and transport failures both flow as `Error` so the client
/// branches uniformly.
type IHealthMonitorApi = {
    /// Run every registered `IHealthCheck` once, in parallel, with
    /// per-probe timeouts capped at the same 10s aggregator budget
    /// the dev endpoint uses. Returns the live snapshot.
    /// Phase 4b re-gate: the handler requires `PlatformRole.PlatformAdmin`
    /// (`canModifyPlatformConfig`) — mode-agnostic, team roles no longer
    /// suffice.
    [<RequiresRole "PlatformAdmin">]
    GetCurrentHealth: unit -> Async<Result<HealthSnapshot, string>>

    /// Read `IPreflightSnapshot.LastRun`. Snapshot-only — validators
    /// are heavier than health probes (sentinel writes, DNS
    /// resolution) so re-running on every UI refresh would amplify
    /// side effects. The dedicated refresh button still hits this
    /// method so a deployer can confirm the most recent boot's
    /// outcome without a hard reload.
    [<RequiresRole "PlatformAdmin">]
    GetPreflightSnapshot: unit -> Async<Result<PreflightSnapshotView, string>>

    /// Phase 9b.A — read the job scheduler's missed-tick telemetry.
    /// `HasScheduler = false` when no `IJobSchedulerTelemetry` is
    /// registered (no scheduler at all, or a distributed companion
    /// that didn't register one); the UI then suppresses the panel.
    /// Cheap pull (in-memory rolling counter) so the dedicated refresh
    /// hits this method on every press without amplifying load.
    [<RequiresRole "PlatformAdmin">]
    GetJobSchedulerTelemetry: unit -> Async<Result<JobSchedulerTelemetryView, string>>

    /// Phase 118 — read the deployment's degraded-capability set:
    /// compose-time or runtime best-effort wiring that failed without
    /// crashing startup (e.g. a failed cross-silo crypto-shred cache-
    /// eviction subscribe). Empty list on a healthy deployment (GP 13).
    /// Cheap in-memory snapshot (a `ConcurrentDictionary` read), so the
    /// dedicated refresh hits this on every press without amplifying load.
    [<RequiresRole "PlatformAdmin">]
    GetDegradedCapabilities: unit -> Async<Result<DegradedCapability list, string>>
}