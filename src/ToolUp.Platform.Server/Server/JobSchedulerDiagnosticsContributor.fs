// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JobSchedulerDiagnosticsContributor

open System
open ToolUp.Platform

// ─── Phase 9b.A — Job scheduler `/dev/inspect` contributor ───────────
//
// Surfaces the scheduler's missed-tick telemetry as a `"Job scheduler"`
// panel on `/dev/inspect`. Registered in DI by `ComposeJobs.registerJobScheduler`
// when `ServerConfig.JobScheduler = InProcessJobScheduler`; absent in
// the lightweight default so deployments without background jobs carry
// no panel.
//
// The contributor is cheap (in-memory rolling counter read under one
// lock acquisition) so it comfortably fits the contributor budget
// (<50ms, no external I/O) per `IDevDiagnosticsContributor`'s contract.

/// Wire DTO. Plain primitives so the dev page's JSON renderer treats it
/// uniformly with every other contributor payload.
type private JobSchedulerPanelPayload = {
    TickMissedCount60Min: int
    LastDriftMs: int64 option
    LastTickMissedAt: DateTime option
    GeneratedAt: DateTime
}

/// `IDevDiagnosticsContributor` over `IJobSchedulerTelemetry`. Constructed
/// in `ComposeJobs` once the scheduler singleton is built; closes over the
/// telemetry instance so contributor invocations don't service-locate.
type JobSchedulerDiagnosticsContributor(telemetry: IJobSchedulerTelemetry) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let snapshot = telemetry.Snapshot()

            let payload: JobSchedulerPanelPayload = {
                TickMissedCount60Min = snapshot.TickMissedCount60Min
                LastDriftMs = snapshot.LastDriftMs
                LastTickMissedAt = snapshot.LastTickMissedAt
                GeneratedAt = snapshot.GeneratedAt
            }

            return "Job scheduler", box payload
        }