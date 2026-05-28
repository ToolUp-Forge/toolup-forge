module ToolUp.Platform.JobSchedulerInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

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
type JobSchedulerInstanceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "job-scheduler-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isInProcess =
                match config.JobScheduler with
                | InProcessJobScheduler -> true
                | NoJobScheduler -> false

            let multiInstance = config.ReplicaCount > 1
            let escapeHatch = config.AcceptInProcessSchedulerInMultiInstance

            if isInProcess && multiInstance && not escapeHatch then
                return
                    Error(
                        sprintf
                            "ServerConfig.JobScheduler = InProcessJobScheduler with ReplicaCount = %d. The in-process scheduler fires every cron job, every event-triggered job, and every webhook fan-out from every instance independently — N replicas means N times every job, every audit emission, every webhook delivery. Configure a distributed scheduler companion (Phase 9c.A Akka actor port) or set ServerConfig.AcceptInProcessSchedulerInMultiInstance = true if your jobs are idempotent and you accept the duplicate execution. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            config.ReplicaCount
                    )
            else
                return Ok
        }