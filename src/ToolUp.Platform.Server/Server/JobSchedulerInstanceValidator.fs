module ToolUp.Platform.JobSchedulerInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open Microsoft.Extensions.DependencyInjection

// ─── Phase 6l.F — multi-instance + InProcessJobScheduler ────────────
//
// `InProcessJobScheduler` schedules cron jobs, event-triggered jobs,
// and webhook fan-out from each instance independently. In a single-
// instance deployment that's correct. In a multi-instance deployment
// (load balancer fronting N replicas of the same SDK build) every
// instance fires every job — silently, without warning — which means:
//   - Cron jobs run N times every minute.
//   - Webhooks fire N times per source event.
//   - Audit emission duplicates.
//   - Third-party API rate limits trip from the X-multiplied volume.
//   - Deduplication has to happen downstream (idempotency keys, vendor-
//     side dedup), if it happens at all.
//
// The Phase 9b SDK ships InProcessJobScheduler documented as
// "single-instance only", but nothing structural enforces that.
// Operators scaling from 1 → N with the same config get silent
// duplication.
//
// This validator catches operator-declared multi-instance via
// `ServerConfig.ReplicaCount`. Auto-detection via heartbeats in
// `IBlobStorage` is a Phase 6l.F.2 follow-up — needs more design
// (heartbeat TTL, lock-holder election semantics, race between two
// instances starting in the same window).

/// Phase 6l.F — config validator that refuses
/// `JobScheduler = InProcessJobScheduler` paired with
/// `ReplicaCount > 1`. Single-instance deployments are unaffected;
/// multi-instance deployments either configure a distributed
/// scheduler companion (Phase 9c.A) or set the explicit-opt-in
/// escape hatch.
///
/// A deployment that composed a SHARED lock (`ServerApp.withDistributedLock`,
/// visible here as the `IDistributedLock` instance compose registered in
/// `services`) is accepted at any replica count: the scheduler's cron tick is
/// an election over that lock, so N replicas run each due occurrence once.
type JobSchedulerInstanceValidator(config: ServerConfig, ?timeout: TimeSpan, ?services: IServiceCollection) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    /// Whether the lock compose registered is anything but the in-process
    /// default (the last unkeyed instance registration wins, as on resolve).
    let sharedLockComposed () =
        match services with
        | None -> false
        | Some services ->
            services
            |> Seq.filter (fun d ->
                d.ServiceType = typeof<IDistributedLock>
                && not d.IsKeyedService
                && not (isNull d.ImplementationInstance))
            |> Seq.tryLast
            |> Option.exists (fun d ->
                not (obj.ReferenceEquals(d.ImplementationInstance, InProcessDistributedLock.shared)))

    interface IConfigValidator with
        member _.Name = "job-scheduler-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isInProcess =
                match config.JobScheduler with
                | InProcessJobScheduler -> true
                | NoJobScheduler -> false
                // Phase 9c.E — the Quartz companion is NOT this
                // validator's question. Whether a Quartz deployment
                // double-dispatches depends on whether its job store is
                // clustered, which is Quartz configuration this mode
                // does not carry — so the companion's own
                // `IConfigValidator` asks the scheduler
                // (`SchedulerMetadata.JobStoreClustered`) and answers
                // from the real state rather than from the mode.
                | QuartzJobScheduler _ -> false

            let multiInstance = config.ReplicaCount > 1
            let escapeHatch = config.AcceptInProcessSchedulerInMultiInstance

            if isInProcess && multiInstance && not escapeHatch && not (sharedLockComposed ()) then
                return
                    Error(
                        sprintf
                            "ServerConfig.JobScheduler = InProcessJobScheduler with ReplicaCount = %d. The in-process scheduler fires every cron job, every event-triggered job, and every webhook fan-out from every instance independently — N replicas means N times every job, every audit emission, every webhook delivery. Compose a shared IDistributedLock (ServerApp.withDistributedLock) so the cron tick is an election across replicas, configure a distributed scheduler companion (Phase 9c.A Akka actor port), or set ServerConfig.AcceptInProcessSchedulerInMultiInstance = true if your jobs are idempotent and you accept the duplicate execution. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            config.ReplicaCount
                    )
            else
                return Ok
        }