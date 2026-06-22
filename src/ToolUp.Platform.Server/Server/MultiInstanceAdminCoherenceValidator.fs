module ToolUp.Platform.MultiInstanceAdminCoherenceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Multi-instance + in-process admin subsystems ───────────────────
//
// Several Platform/Team-admin subsystems hold per-process single-writer
// state and degrade silently when `ReplicaCount > 1`:
//   * the Platform-Admin store — a concurrent last-admin revoke can brick
//     the platform (assign/revoke is a per-process-locked read-modify-write);
//   * the permission store + team membership store — load-modify-save and
//     cross-user last-owner removal lose updates across replicas;
//   * the outbound webhook dispatcher — an in-process `Channel`, so an
//     event enqueued on one replica is only delivered by that replica;
//   * the DSR erasure-preview cache — in-process, so a confirm routed to a
//     different replica than the preview returns "no preview found".
//
// The underlying fix is the distributed-primitives work (Phase 9i
// `IDistributedLock` + Phase 9c durable queue / ETag CAS). Until that
// lands the degradation is invisible — unlike the team-cache case, which
// `NotificationChannelInstanceValidator` already warns about. This
// validator surfaces the whole class at preflight, enumerating only the
// subsystems actually composed. Warning (not Error), mirroring the other
// topology validators' soft-touch posture — some deployments legitimately
// run a single instance for these subsystems or accept the degradation.

/// Config validator that warns when `ReplicaCount > 1` and one or more
/// in-process single-writer admin subsystems are composed, naming each.
type MultiInstanceAdminCoherenceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "multi-instance-admin-coherence"
        member _.Timeout = timeout

        member _.Validate() = async {
            if config.ReplicaCount <= 1 then
                return Ok
            else
                let webhooksComposed =
                    match config.Webhooks with
                    | NoWebhooks -> false
                    | _ -> true

                let dsrComposed =
                    match config.DataSubjectRequests with
                    | DataSubjectRequestMode.Enabled _ -> true
                    | _ -> false

                let subsystems = [
                    if DeploymentConfig.requiresAnyAuth config then
                        "the Platform-Admin store — a concurrent last-admin revoke can brick the platform (assign/revoke is a per-process-locked read-modify-write)"
                    if DeploymentConfig.hasTeamScope config then
                        "the permission store + team membership store — load-modify-save and cross-user last-owner removal lose updates across replicas"
                    if webhooksComposed then
                        "the outbound webhook dispatcher — an in-process Channel; an event enqueued on one replica is only delivered by that replica (a load-balanced-away instance drops the delivery)"
                    if dsrComposed then
                        "the DSR erasure-preview cache — in-process; a confirm routed to a different replica than the preview returns 'no preview found'"
                ]

                match subsystems with
                | [] -> return Ok
                | active ->
                    let listing = active |> List.map (sprintf "  - %s") |> String.concat "\n"

                    return
                        Warning(
                            sprintf
                                "ServerConfig.ReplicaCount = %d with in-process single-writer admin subsystems composed:\n%s\nThese hold per-process state that does not cross instance boundaries — concurrent operations race (lost updates / dropped webhooks / lost previews), and a concurrent last-admin revoke can brick the platform. The distributed remedy is Phase 9i (IDistributedLock) + Phase 9c (durable queue / ETag CAS); until those land, run a single instance, or accept the documented degradation."
                                config.ReplicaCount
                                listing
                        )
        }