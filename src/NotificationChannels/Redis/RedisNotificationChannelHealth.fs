module ToolUp.Platform.NotificationChannels.RedisHealth

open System
open StackExchange.Redis
open ToolUp.Platform.HealthChecks

// ─── Phase 9k Redis health probe ─────────────────────────────────────
//
// Probes the Redis connection by issuing a `PING` against the supplied
// `IConnectionMultiplexer`. PING is the canonical liveness command —
// no auth, no key access, no payload — and a healthy Redis server
// answers in single-digit milliseconds, so the 1-second probe budget
// is generous enough to absorb network jitter without masking real
// degradation.
//
// **Why a probe at all when StackExchange.Redis already auto-reconnects?**
// `IConnectionMultiplexer` will silently mark itself unavailable when
// the server is unreachable; commands then queue or fail depending on
// configuration. Without a probe, `/ready` doesn't notice and the load
// balancer keeps routing traffic to a deployment whose notification
// channel is broken. The PING surfaces the underlying state to the
// orchestrator.

/// Companion-contributed `IHealthCheck` for the Redis notification
/// channel. Construct via `RedisNotificationChannelHealth.create
/// multiplexer` and register through `ServerApp.withHealthCheck`. The
/// multiplexer should be the same one passed to
/// `RedisNotificationChannel(...)` so the probe and the channel share
/// the same TCP connection (cheaper, less surprising — Redis-down for
/// the probe means Redis-down for the channel too).
type RedisNotificationChannelHealth(multiplexer: IConnectionMultiplexer) =
    interface IHealthCheck with
        member _.Name = "notification_channel:redis"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            try
                let database = multiplexer.GetDatabase()
                let! latency = database.PingAsync() |> Async.AwaitTask

                if latency.TotalMilliseconds > 500.0 then
                    return Degraded(sprintf "Redis PING took %.0fms (degraded — investigate)" latency.TotalMilliseconds)
                else
                    return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

module RedisNotificationChannelHealth =
    /// Create the probe from a live `IConnectionMultiplexer`. The same
    /// multiplexer should back `RedisNotificationChannel` so the probe
    /// reflects the channel's actual connection state.
    let create (multiplexer: IConnectionMultiplexer) : IHealthCheck =
        RedisNotificationChannelHealth(multiplexer) :> IHealthCheck