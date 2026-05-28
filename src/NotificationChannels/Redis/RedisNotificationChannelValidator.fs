module ToolUp.Platform.NotificationChannels.RedisValidator

open System
open StackExchange.Redis
open ToolUp.Platform.ConfigValidation

// ─── Phase 9m Redis config preflight ─────────────────────────────────
//
// PING the configured Redis multiplexer once at startup. Fast (single-
// digit ms on a healthy network) and unauthenticated — no key access,
// no payload — so it works against any Redis configuration that the
// runtime channel can also reach. Mirrors the Phase 9k
// `RedisNotificationChannelHealth` probe exactly; the validator runs
// once at compose end while the health probe runs on every `/ready`
// poll.
//
// Companion auto-registration: a deployment that imports
// `RedisNotificationChannel.Server.props` and reads
// `TOOLUP_NOTIFICATION_CHANNEL=redis` constructs the multiplexer
// alongside the channel; the deployment composition root passes the
// same multiplexer to `RedisNotificationChannelValidator.create` so
// the validator and the channel share one TCP connection.

type private Impl(multiplexer: IConnectionMultiplexer) =
    interface IConfigValidator with
        member _.Name = "redis-notification"
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Validate() = async {
            try
                let database = multiplexer.GetDatabase()
                let! latency = database.PingAsync() |> Async.AwaitTask

                if latency.TotalMilliseconds > 500.0 then
                    return Warning(sprintf "Redis PING took %.0fms (degraded — investigate)" latency.TotalMilliseconds)
                else
                    return Ok
            with ex ->
                return Error(sprintf "Redis PING failed: %s" ex.Message)
        }

/// Construct a validator from a live `IConnectionMultiplexer`. The
/// same multiplexer should back `RedisNotificationChannel` so the
/// validator reflects the channel's actual connection state.
let create (multiplexer: IConnectionMultiplexer) : IConfigValidator = Impl(multiplexer) :> IConfigValidator