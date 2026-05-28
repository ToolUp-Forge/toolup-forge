module ToolUp.Platform.Tests.InProcess.RedisNotificationChannelTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.NotificationChannels.Redis
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated ───────────────────────────────────────────────────────
//
// Runs the shared `INotificationChannelContract` pack against a real
// Redis instance when `TOOLUP_REDIS_CONNECTION` is set (e.g.
// `localhost:6379`). The same pack is bound to the in-process default
// in `InMemoryNotificationChannelTests`; running both in the same CI
// job is how Phase 6e proves Guiding Principle 12 portability against a
// second backend.
//
// Scope isolation is structural in the Redis implementation — each
// `scopeId` maps to a distinct Redis channel
// (`toolup:notifications:{scopeId}`) — so running several tests in
// parallel against the same Redis instance is safe provided each test
// uses fresh scope ids. `IBlobStorageContract.tests` already does this
// (GUID-suffixed scope names); the notification pack does the same.
//
// When the env var is unset, the pack emits a single `pending` test so
// the CI signal shows "skipped" rather than "green" — a missing
// CI-side Redis connection is visible rather than silently passing.

let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_REDIS_CONNECTION" with
    | null
    | "" -> testList "RedisNotificationChannel" [ ptestCase "skipped — TOOLUP_REDIS_CONNECTION not set" <| fun _ -> () ]
    | connectionString ->
        let factory () =
            RedisNotificationChannel.fromConnectionString connectionString None

        INotificationChannelContract.tests "RedisNotificationChannel" factory