module ToolUp.Platform.RateLimiterInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Multi-instance + in-process / in-memory rate limiters ──────────
//
// Two SDK-shipped rate limiters keep their counters in process memory:
//   - The outbound `IRateLimiter` (`RateLimiter = EnabledRateLimiter`)
//     tracks per-provider sliding windows in a process-local
//     `ConcurrentDictionary` (see `InProcessRateLimiter.fs` — its own
//     header flags the "Distributed limitation": N replicas burst past
//     the declared limit by N× until a Redis-backed companion ships).
//   - The inbound `IRateLimitStore` (`RateLimitStore =
//     InMemoryRateLimitStore`) counts requests per-process, so a
//     per-subject inbound budget is effectively multiplied by the
//     replica count.
//
// In a single-instance deployment both are exact. In a multi-instance
// deployment (load balancer fronting N replicas) the declared limit is
// silently the *per-instance* limit — the effective ceiling is N× what
// the operator configured, with no signal. The `ExternalRateLimitStore`
// path (e.g. the Redis companion) shares state across replicas and is
// the correct multi-instance choice.
//
// Mirrors `JobSchedulerInstanceValidator` / `NotificationChannelInstanceValidator`:
// operator-declared multi-instance via `ServerConfig.ReplicaCount`.
// `Warning` (not `Error`) and no escape hatch — matching the soft-touch
// posture of `NotificationChannelInstanceValidator`. An over-permissive
// rate limit is a quota-imprecision / mild-DoS-surface concern, not a
// security boundary, and some deployments legitimately accept the N×
// burst until they wire a distributed store; the warning surfaces in the
// startup log + HealthMonitorUI Preflight tab + `/dev/inspect` so the
// operator can make an informed call.

/// Config validator that warns when an in-process outbound limiter
/// (`RateLimiter = EnabledRateLimiter`) or an in-memory inbound store
/// (`RateLimitStore = InMemoryRateLimitStore`) runs under
/// `ReplicaCount > 1` — the declared limit becomes per-instance, so the
/// effective ceiling is N× the configured value across the deployment.
type RateLimiterInstanceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rate-limiter-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1

            let outboundInProcess =
                match config.RateLimiter with
                | EnabledRateLimiter -> true
                | NoRateLimiter -> false

            let inboundInMemory =
                match config.RateLimitStore with
                | InMemoryRateLimitStore -> true
                | NoRateLimitStore
                | ExternalRateLimitStore -> false

            if multiInstance && (outboundInProcess || inboundInMemory) then
                let offenders =
                    [
                        if outboundInProcess then
                            "RateLimiter = EnabledRateLimiter (outbound per-provider quotas via the in-process sliding-window limiter)"
                        if inboundInMemory then
                            "RateLimitStore = InMemoryRateLimitStore (inbound per-subject request budget via the process-local counter)"
                    ]
                    |> String.concat " and "

                return
                    Warning(
                        sprintf
                            "ReplicaCount = %d with %s. These limiters keep their counters in process memory, so each replica enforces the declared limit independently — the effective ceiling across the deployment is %d× the configured value. Wire the external/distributed store (set RateLimitStore = ExternalRateLimitStore and register the Redis companion's IRateLimitStore) so the budget is shared across replicas, or run a single instance. The outbound limiter has no shipped distributed backend yet — accept the N× burst knowingly until one is wired."
                            config.ReplicaCount
                            offenders
                            config.ReplicaCount
                    )
            else
                return Ok
        }