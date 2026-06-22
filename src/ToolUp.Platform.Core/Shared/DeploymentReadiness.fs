// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 177 — Deployment-readiness scorecard ──────────────────────
//
// One operator-facing "is this deployment operationally ready?" read
// that consolidates the four already-shipped operability signals into a
// single verdict, instead of the operator stitching four independent
// admin surfaces together:
//
//   * Preflight   — `IConfigValidator` outcomes (Phase 9m).
//   * SmokeTests  — `ISmokeTest` results        (Phase 9o).
//   * Drift       — `ConfigDrift` finding       (Phase 9q).
//   * Health      — `IHealthCheck` aggregate    (Phase 9k).
//
// **Pure projection (GP 13).** This file declares only the wire-shape
// records + the pure `summarise` verdict aggregation. No new gate, no
// new control-plane behaviour, no I/O — the server-side
// `DeploymentReadinessReport` gathers the four signals and calls
// `summarise`. Kept in Shared (GP 10) so a future Fable admin panel
// renders the same shape without re-deriving the wire format.
//
// **Naming.** The four sub-summary records live inside the
// `DeploymentReadiness` module (qualified access) because the bare
// names `PreflightSummary` / `DriftSummary` / `HealthSummary` already
// exist at `ToolUp.Platform` scope as the Phase 9p.A ServiceStatusBoard
// section abbreviations. The scorecard is a distinct, narrower surface
// (a single go/no-go verdict, not the six-section board), so it carries
// its own record shapes rather than reusing `SectionSummary`.

/// Per-source roll-up feeding the verdict. `NotComposed` means the
/// source's substrate is not wired in this deployment — there is no
/// signal to read (never a fabricated pass). `Clean` / `Warned` /
/// `Failed` describe a wired source's worst observed state:
///   * `Failed`  — a hard go/no-go failure (preflight `Error`, a failed
///                 smoke test, an `Unhealthy` probe).
///   * `Warned`  — a soft signal (preflight `Warning`, drift detected, a
///                 `Degraded` probe) with no hard failure.
///   * `Clean`   — wired and all-green.
[<RequireQualifiedAccess>]
type SourceStatus =
    | NotComposed
    | Clean
    | Warned
    | Failed

/// Top-line go/no-go verdict.
///   * `Ready`         — every wired readiness signal is green and at
///                       least one signal is composed.
///   * `DegradedReady` — ready to serve, but with caveats: a soft signal
///                       fired (warning / drift / degraded probe), OR no
///                       signal at all is composed (an empty scorecard
///                       cannot attest full readiness — confidence is
///                       degraded, not green).
///   * `NotReady`      — at least one hard go/no-go failure; do not route
///                       production traffic.
[<RequireQualifiedAccess>]
type ReadinessVerdict =
    | Ready
    | DegradedReady
    | NotReady

module DeploymentReadiness =

    /// `IConfigValidator` preflight roll-up. `Errors` / `Warnings` carry
    /// the offending validator names so a `NotReady` / `DegradedReady`
    /// verdict can name the failing item(s). `Total` is the count of
    /// validators in the last preflight run.
    type PreflightSummary = {
        Status: SourceStatus
        Total: int
        Errors: string list
        Warnings: string list
    }

    /// `ISmokeTest` roll-up. Smoke tests are two-valued (`Pass` / `Fail`)
    /// — there is no soft state — so the worst status is `Failed` or
    /// `Clean`. `Failures` names the failing probes.
    type SmokeSummary = {
        Status: SourceStatus
        Total: int
        Failures: string list
    }

    /// `ConfigDrift` roll-up. Drift is never a hard failure — a drifted
    /// deployment can still serve — so a detected drift maps to `Warned`.
    /// `DriftEventCount` is the number of `ConfigDrift` audit rows in the
    /// lookback window.
    type DriftSummary = {
        Status: SourceStatus
        DriftEventCount: int
    }

    /// `IHealthCheck` roll-up. `Unhealthy` / `Degraded` carry the
    /// offending probe names. `Total` is the count of registered probes.
    type HealthSummary = {
        Status: SourceStatus
        Total: int
        Unhealthy: string list
        Degraded: string list
    }

    /// The consolidated scorecard. Each sub-summary is independently
    /// `NotComposed` when its substrate isn't wired, so a deployment that
    /// composes a subset (or none) of the signals gets an honest report
    /// rather than a fabricated pass or an error (GP 13).
    type DeploymentReadinessReport = {
        Verdict: ReadinessVerdict
        Preflight: PreflightSummary
        SmokeTests: SmokeSummary
        Drift: DriftSummary
        Health: HealthSummary
        GeneratedAt: DateTime
    }

    /// `NotComposed` sub-summaries — the gatherer uses these when a
    /// source's substrate is absent, and the truth-table tests use them
    /// to assert a missing signal never inflates the verdict to `Ready`.
    module PreflightSummary =
        let notComposed: PreflightSummary = {
            Status = SourceStatus.NotComposed
            Total = 0
            Errors = []
            Warnings = []
        }

    module SmokeSummary =
        let notComposed: SmokeSummary = {
            Status = SourceStatus.NotComposed
            Total = 0
            Failures = []
        }

    module DriftSummary =
        let notComposed: DriftSummary = {
            Status = SourceStatus.NotComposed
            DriftEventCount = 0
        }

    module HealthSummary =
        let notComposed: HealthSummary = {
            Status = SourceStatus.NotComposed
            Total = 0
            Unhealthy = []
            Degraded = []
        }

    /// Pure verdict aggregation over the four source statuses.
    /// Deterministic, side-effect-free, unit-testable in isolation:
    ///   * any `Failed`            ⇒ `NotReady`;
    ///   * else any `Warned`       ⇒ `DegradedReady`;
    ///   * else any `Clean`        ⇒ `Ready` (≥1 wired-and-green source,
    ///                               none failed / warned — remaining
    ///                               `NotComposed` sources do not block);
    ///   * else (all `NotComposed`) ⇒ `DegradedReady` — an empty scorecard
    ///                               cannot attest readiness, and a
    ///                               `NotComposed` source must never
    ///                               inflate the verdict to `Ready`.
    let verdictOf (statuses: SourceStatus list) : ReadinessVerdict =
        if List.contains SourceStatus.Failed statuses then
            ReadinessVerdict.NotReady
        elif List.contains SourceStatus.Warned statuses then
            ReadinessVerdict.DegradedReady
        elif List.contains SourceStatus.Clean statuses then
            ReadinessVerdict.Ready
        else
            ReadinessVerdict.DegradedReady

    /// Assemble the report from the four gathered sub-summaries. `summarise`
    /// owns only the verdict roll-up; gathering the signals (DI reads,
    /// running probes) is the server-side `DeploymentReadinessReport`'s job.
    let summarise
        (generatedAt: DateTime)
        (preflight: PreflightSummary)
        (smoke: SmokeSummary)
        (drift: DriftSummary)
        (health: HealthSummary)
        : DeploymentReadinessReport =
        let verdict =
            verdictOf [ preflight.Status; smoke.Status; drift.Status; health.Status ]

        {
            Verdict = verdict
            Preflight = preflight
            SmokeTests = smoke
            Drift = drift
            Health = health
            GeneratedAt = generatedAt
        }

/// Platform-Admin-gated read-only Fable.Remoting surface returning the
/// consolidated scorecard. Mirrors `IHealthMonitorApi` /
/// `IServiceStatusBoardApi`: Anonymous-mode and non-admin callers both
/// receive `Error` (deployment-wide dependency state is a reconnaissance
/// gift to surface to every visitor); `Result<_, string>` is the
/// established Fable.Remoting failure shape. Deployment-wide, never
/// per-tenant (GP 4) — the read carries no tenant-scoped data.
///
/// Mounted only when `ServerConfig.DeploymentReadiness =
/// EnabledReadinessReport` (default `NoReadinessReport`, GP 11/13) — an
/// unopted deployment 404s the route and pays nothing.
type IDeploymentReadinessApi = {
    [<RequiresRole "PlatformAdmin">]
    GetReadinessReport: unit -> Async<Result<DeploymentReadiness.DeploymentReadinessReport, string>>
}