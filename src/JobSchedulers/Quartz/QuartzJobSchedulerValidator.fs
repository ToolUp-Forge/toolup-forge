// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.JobSchedulers.QuartzValidator

open System
open Quartz
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.JobSchedulers.QuartzStore

// ─── Phase 9c.E Quartz config preflight ──────────────────────────────
//
// Runs once at compose end. Two things it checks that the health probe
// structurally cannot, because they are about CONFIGURATION rather than
// current state:
//
//   1. `QuartzConfig` values that would make the scheduler useless
//      rather than broken — a thread pool that cannot run a job, a
//      scheduler with no name, a misfire threshold of zero. Each of
//      these produces a deployment that starts cleanly and never
//      dispatches, which is the failure mode preflight exists for.
//
//   2. **The multi-replica double-dispatch hazard, asked of the job
//      store rather than of the mode.** `JobSchedulerInstanceValidator`
//      answers this question for the in-process default by looking at
//      `ServerConfig.JobScheduler`, and it cannot answer it for this
//      companion: whether a Quartz deployment double-dispatches depends
//      on whether its job store is CLUSTERED, which is a Quartz
//      configuration choice the SDK mode does not carry. So this
//      validator asks Quartz — `SchedulerMetadata.JobStoreClustered` —
//      and reports the real answer. The companion's default is the
//      in-memory store, which is NOT clustered: N replicas of that
//      configuration each fire every cron job independently. A
//      deployment that means it sets
//      `ServerConfig.AcceptInProcessSchedulerInMultiInstance`, exactly
//      as it would for the in-process default.

type private Impl(scheduler: IScheduler, quartzConfig: QuartzConfig, config: ServerConfig) =

    let configFaults () = [
        if quartzConfig.MaxConcurrency < 1 then
            $"QuartzConfig.MaxConcurrency = {quartzConfig.MaxConcurrency}; a thread pool smaller than one job never dispatches."

        if String.IsNullOrWhiteSpace quartzConfig.SchedulerName then
            "QuartzConfig.SchedulerName is empty; Quartz keys its scheduler registry on the name and two unnamed schedulers in one process collide."

        if quartzConfig.MisfireThreshold <= TimeSpan.Zero then
            $"QuartzConfig.MisfireThreshold = {quartzConfig.MisfireThreshold}; a non-positive threshold makes every fire a misfire."
    ]

    interface IConfigValidator with
        member _.Name = "quartz-job-scheduler"
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Validate() = async {
            match configFaults () with
            | fault :: _ -> return Error fault
            | [] ->
                try
                    let! metadata = await (scheduler.GetMetadata())

                    let multiInstance = config.ReplicaCount > 1
                    let accepted = config.AcceptInProcessSchedulerInMultiInstance

                    if multiInstance && not metadata.JobStorePersistent then
                        if accepted then
                            return
                                Warning(
                                    sprintf
                                        "ServerConfig.JobScheduler = QuartzJobScheduler with ReplicaCount = %d over a non-persistent Quartz job store (%s). Every replica fires every cron job independently; AcceptInProcessSchedulerInMultiInstance is set, so this is accepted as deliberate."
                                        config.ReplicaCount
                                        metadata.JobStoreTypeName
                                )
                        else
                            return
                                Error(
                                    sprintf
                                        "ServerConfig.JobScheduler = QuartzJobScheduler with ReplicaCount = %d, but the composed Quartz job store (%s) is not persistent and therefore not clustered — each replica holds its own copy of every trigger and fires it, so N replicas means N times every job. Compose the companion with QuartzJobScheduler.createWith and a persistent, clustered store (Quartz's UsePersistentStore), or set ServerConfig.AcceptInProcessSchedulerInMultiInstance = true if your jobs are idempotent and you accept the duplicate execution. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                                        config.ReplicaCount
                                        metadata.JobStoreTypeName
                                )
                    elif multiInstance && not metadata.JobStoreClustered then
                        return
                            Warning(
                                sprintf
                                    "ServerConfig.JobScheduler = QuartzJobScheduler with ReplicaCount = %d over a persistent but NON-CLUSTERED Quartz job store (%s). Persistence alone does not elect one firing node; enable Quartz clustering so exactly one replica acquires each trigger."
                                    config.ReplicaCount
                                    metadata.JobStoreTypeName
                            )
                    elif metadata.Status = SchedulerStatus.Shutdown then
                        return
                            Error "The composed Quartz scheduler is already shut down; no job will ever be dispatched."
                    else
                        return Ok
                with ex ->
                    return Error $"Quartz scheduler metadata probe failed: {ex.Message}"
        }

/// Construct the preflight validator over the companion's Quartz
/// scheduler and the `QuartzConfig` it was composed with. Register
/// through `ServerApp.withConfigValidator`.
let create (scheduler: IScheduler) (quartzConfig: QuartzConfig) (config: ServerConfig) : IConfigValidator =
    Impl(scheduler, quartzConfig, config) :> IConfigValidator