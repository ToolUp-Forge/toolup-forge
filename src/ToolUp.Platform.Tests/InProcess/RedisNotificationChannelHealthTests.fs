module ToolUp.Platform.Tests.InProcess.RedisNotificationChannelHealthTests

open System
open Expecto
open StackExchange.Redis
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.NotificationChannels.RedisHealth
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Binds the shared `IHealthCheckContract` pack against a real Redis
// instance when `TOOLUP_REDIS_CONNECTION` is set. Mirrors the pattern
// in `RedisNotificationChannelTests` — Phase 9c portability is proved
// by binding the same contract pack against a second backend without
// touching the pack itself.
//
// Skipped when the env var is unset (CI without Redis available
// reports `pending` rather than green).

let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_REDIS_CONNECTION" with
    | null
    | "" ->
        testList "RedisNotificationChannelHealth" [
            ptestCase "skipped — TOOLUP_REDIS_CONNECTION not set" <| fun _ -> ()
        ]
    | connectionString ->
        let factory () =
            let multiplexer =
                ConnectionMultiplexer.Connect(connectionString) :> IConnectionMultiplexer

            RedisNotificationChannelHealth.create multiplexer

        IHealthCheckContract.tests "RedisNotificationChannelHealth" factory Healthy