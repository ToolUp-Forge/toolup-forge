// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── HealthMonitorApi (production-safe Owner/Admin surface) ──────────
//
// Read-only ToolUp.Remoting API surfacing live `IHealthCheck` results
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

// ─── Phase 47 — AI action-denial rollup (wire records + seam) ────────
//
// The `_platform.ai.tool_allowlist_denial` audit stream (Phase 45) is
// written by the AI tier's agent loop every time the client-tool
// authorizer refuses a model-driven action. Written, but until Phase 47
// invisible without hand-querying `IEventStore` — so a prompt-injection
// campaign or a mis-scoped allowlist looked exactly like silence.
//
// These records are the ONE shape both readers render: the debug
// endpoint `/dev/ai-allowlist` serialises them directly, and the
// production-safe `HealthMonitorUI` panel receives them over
// ToolUp.Remoting. They live in Core because only Core ships its source
// under `fable/` (GP 10) — a duplicate DTO in the AI tier would drift.

/// One grouped denial count. `Key` is the grouping value (a tool name,
/// an active-module id, or a scope id); `Count` the denials in the
/// rolling window carrying it.
type AIDenialGroupCount = { Key: string; Count: int }

/// One top-N `(tool, module)` denial pair — the cut that answers "what
/// is being refused, and where is it being driven from", which neither
/// single-axis breakdown can.
type AIDenialToolModulePair = {
    ToolName: string
    /// The `ActiveModule` the chat turn was driving, or `"(none)"` when
    /// the turn carried no active module.
    ActiveModule: string
    Count: int
}

/// One recent denial, sanitised for operator display.
///
/// **PII / injection envelope.** `Reason` is the authorizer's own
/// refusal string, control-stripped and truncated by the producing
/// tier. Raw model arguments are NEVER carried here — the model chose
/// them, so they are attacker-influenced text, and an operator console
/// is the wrong place to render it verbatim.
type RecentAIDenial = {
    ToolName: string
    ActiveModule: string
    /// Sanitised + truncated refusal reason.
    Reason: string
    OccurredAt: DateTime
}

/// Rolling-window rollup of AI action denials for one scope.
///
/// `TotalDenialsAllTime` is the unwindowed count in the caller's scope
/// (so a burst that has already aged out of the window is still
/// visible as history); `TotalDenialsInWindow` and everything below it
/// describe the trailing `WindowMinutes` only.
type AIDenialRollup = {
    GeneratedAt: DateTime
    /// The caller's resolved scope. Every count below is read from this
    /// scope alone — cross-team denial enumeration is structurally
    /// impossible (GP 4).
    ScopeId: string
    WindowMinutes: int
    TotalDenialsAllTime: int
    TotalDenialsInWindow: int
    /// `TotalDenialsInWindow / WindowMinutes`. The rate an alerting
    /// threshold is expressed against.
    DenialsPerMinute: float
    ByToolName: AIDenialGroupCount list
    ByActiveModule: AIDenialGroupCount list
    ByScopeId: AIDenialGroupCount list
    TopToolModulePairs: AIDenialToolModulePair list
    RecentDenials: RecentAIDenial list
}

/// Optional DI seam (GP 1 / GP 13). The AI companion (`ToolUp.AI`)
/// implements and registers this so the platform-tier health-monitor
/// admin surface can render the denial rollup **without**
/// `Platform.Server` taking a dependency on `ToolUp.AI` — the same
/// shape `IActiveAiProbe` uses for the Home overview. Absent from DI
/// when no AI is composed → `GetAIDenialRollup` returns `Ok None` and
/// the UI suppresses the panel rather than rendering a misleading zero.
///
/// **Six-rule portability audit (GP 12), clean.**
/// 1. *Identity by value* — `scopeId` is a `string`; the return is a
///    value record of primitives and lists. No live handles.
/// 2. *Async at every boundary* — the single method returns
///    `Async<AIDenialRollup>`.
/// 3. *Retry / supervision as data* — there is none to express: the
///    read is a pure projection over `IEventStore`, and a store that
///    cannot answer surfaces its own failure. No callback parameters.
/// 4. *Stateless handlers between invocations* — the implementation
///    holds no per-call state; every input arrives as a parameter, so
///    a grain deactivation between calls is safe.
/// 5. *No cross-shard ordering promises* — the rollup is an unordered
///    aggregate over one scope. `RecentDenials` is sorted by
///    `OccurredAt` *within* the returned value, which is a property of
///    the projection, not a promise about the store's read order (the
///    implementation sorts, per `IEventStore`'s own ordering contract).
/// 6. *Precision at the lower bound* — the window is declared in whole
///    MINUTES (`WindowMinutes`), and no sub-minute claim is made about
///    when a denial becomes visible in it.
type IAIDenialRollupProbe =
    /// Roll up the AI action denials recorded for `scopeId` over the
    /// implementation's trailing window.
    abstract Rollup: scopeId: string -> Async<AIDenialRollup>

/// Owner/Admin-gated read-only ToolUp.Remoting surface. Auto-injected
/// by `compose` — `Anonymous` mode returns `Error` from both methods;
/// `Team` / `MultiTeam` require Owner or Admin role; `Individual` /
/// `AuthenticatedEphemeral` require an authenticated user.
///
/// `Result<_, string>` is the established ToolUp.Remoting failure
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

    /// Phase 47 — read the rolling AI action-denial rollup for the
    /// caller's scope, through the optional `IAIDenialRollupProbe` seam.
    /// `Ok None` when no AI companion is composed (the seam is absent
    /// from DI) — the UI suppresses the panel rather than rendering a
    /// misleading zero, exactly as it does for an absent scheduler.
    ///
    /// Production-safe by construction: this path is gated on
    /// `PlatformRole.PlatformAdmin` like every other method here and
    /// needs no `EnableDevEndpoints`, so an operator can watch a
    /// prompt-injection campaign in production without turning the
    /// debug surface on.
    [<RequiresRole "PlatformAdmin">]
    GetAIDenialRollup: unit -> Async<Result<AIDenialRollup option, string>>
}