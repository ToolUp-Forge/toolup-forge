// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JobSchedulers.QuartzHealth

open System
open Quartz
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.JobSchedulers.QuartzStore

// ─── Phase 9c.E Quartz scheduler health probe ────────────────────────
//
// Reads the scheduler's own status rather than inferring liveness from
// the absence of errors. The distinction that matters to an operator is
// STARTED vs STANDBY: a standby scheduler accepts every write, answers
// every query and fires nothing, so a deployment whose scheduler quietly
// went to standby looks healthy from every other angle while its
// background work silently stops. That is exactly the shape a readiness
// probe exists to surface.
//
// `Degraded` rather than `Unhealthy` for standby, because standby is a
// legitimate configuration (`QuartzConfig.StartScheduler = false` on a
// web-only replica): the probe reports the state and lets the operator
// decide whether it is the intended one. A shut-down scheduler is
// `Unhealthy` without qualification — nothing will fire again in this
// process, whatever the configuration says.

/// Companion-contributed `IHealthCheck` for the Quartz job scheduler.
/// Construct via `QuartzJobSchedulerHealth.create scheduler.QuartzScheduler`
/// and register through `ServerApp.withHealthCheck`.
type QuartzJobSchedulerHealth(scheduler: IScheduler) =
    interface IHealthCheck with
        member _.Name = "job_scheduler:quartz"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            try
                let! status = await (scheduler.GetStatus())

                match status with
                | SchedulerStatus.Running -> return Healthy
                | SchedulerStatus.Standby ->
                    return
                        Degraded
                            "Quartz scheduler is in standby — it accepts and stores jobs but fires none. Expected only when QuartzConfig.StartScheduler is false."
                | SchedulerStatus.Created ->
                    return Degraded "Quartz scheduler is built but has never been started; no trigger will fire."
                | SchedulerStatus.ShuttingDown -> return Unhealthy "Quartz scheduler is shutting down."
                | SchedulerStatus.Shutdown ->
                    return Unhealthy "Quartz scheduler is shut down; no trigger will fire again in this process."
                | other -> return Degraded $"Quartz scheduler reported an unrecognised status (%A{other})."
            with ex ->
                return Unhealthy ex.Message
        }

[<RequireQualifiedAccess>]
module QuartzJobSchedulerHealth =
    /// Create the probe over the companion's Quartz scheduler — the same
    /// instance `QuartzJobScheduler.QuartzScheduler` exposes, so the
    /// probe reports on the scheduler that actually dispatches.
    let create (scheduler: IScheduler) : IHealthCheck =
        QuartzJobSchedulerHealth(scheduler) :> IHealthCheck