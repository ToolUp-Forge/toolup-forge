// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── IJobSchedulerTelemetry (Phase 9b.A — missed-tick observability) ─
//
// Read-only snapshot of the job scheduler's drift state. Exposed so
// `/dev/inspect` (the `JobSchedulerDiagnosticsContributor`) and the
// production `HealthMonitorApi` can surface tick-miss telemetry without
// reaching into the scheduler's internals or extending the portable
// `IJobScheduler` contract.
//
// **Why a separate interface.** Adding drift state to `IJobScheduler`
// would force every distributed companion (Akka, Orleans Reminders,
// Hangfire) to implement the same in-process notion of "missed
// boundary" — but distributed schedulers detect drift through entirely
// different mechanisms (cluster gossip, lease expiry). Keeping the
// telemetry on a side interface lets the in-process default ship a
// concrete reading while distributed companions register their own
// implementation, or none at all.
//
// **Snapshot, not stream.** Telemetry is pulled by callers (the
// dev-page request, the HealthMonitor refresh button). The scheduler
// must not push — `IMetricsSink` is the SDK's push surface; this one
// is for human-readable drift state.

/// One snapshot of the job scheduler's missed-tick telemetry. All
/// counters are scoped to the running process — restart resets them.
type JobSchedulerTelemetrySnapshot = {
    /// Count of distinct minute boundaries the scheduler detected as
    /// missed in the last 60 minutes. A normally-running scheduler
    /// reports zero. A process that paused for 120s (debugger, GC
    /// stall, container throttling, VM hiccup) and resumed produces
    /// one entry per missed boundary while the entry's wall-clock age
    /// stays under 60 minutes.
    TickMissedCount60Min: int
    /// The most recent computed drift (`now - expectedTickTime`) on a
    /// tick that was flagged as missed. `None` until a missed tick has
    /// been observed since process start; never cleared (operators
    /// reading historical drift see the last-known value even after
    /// the rolling-window counter has aged out).
    LastDriftMs: int64 option
    /// When the most recent missed tick was detected. `None` until
    /// observed; mirrors `LastDriftMs`'s lifecycle.
    LastTickMissedAt: DateTime option
    /// Wall-clock at the moment the snapshot was generated. Operators
    /// confirm a refresh re-read the underlying state (vs a cached
    /// surface) by watching this value change.
    GeneratedAt: DateTime
}

/// Telemetry pull-surface for the job scheduler. The in-process
/// default (`InProcessJobScheduler`) implements this directly and is
/// registered as the `IJobSchedulerTelemetry` singleton when
/// `ServerConfig.JobScheduler = InProcessJobScheduler`. Distributed
/// companions implement their own (or leave the registration absent —
/// the dev-page contributor and HealthMonitorApi handle a missing
/// singleton by surfacing "no scheduler / no telemetry" rather than
/// throwing).
type IJobSchedulerTelemetry =
    abstract Snapshot: unit -> JobSchedulerTelemetrySnapshot