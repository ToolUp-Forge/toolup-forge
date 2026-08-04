module ToolUp.Platform.Tests.InProcess.RedisDistributedLockTests

open System
open Expecto
open ToolUp.Platform.NotificationChannels.RedisDistributedLock
open ToolUp.Platform.Tests.Contracts

// ─── Env-gated (Phase 9i) ────────────────────────────────────────────
//
// Runs the shared `IDistributedLockContract` pack against a real Redis
// when `TOOLUP_REDIS_CONNECTION` is set (e.g. `localhost:6379`) — the same
// env var `RedisNotificationChannelTests` gates on, so one CI-side Redis
// serves both. The same pack is bound to the in-process default in
// `IDistributedLockContract.tests`; running both in one job is how GP 12
// portability is proven against a second backend rather than asserted.
//
// This is the arm that actually earns the pack. The in-process binding
// exercises the semantics; only the Redis binding exercises them through
// `SET NX PX` + `INCR` + the compare-and-act Lua scripts, where the
// interesting divergences live (an expiry the store clamps, a fence
// counter that resets, a release that deletes a later holder's key).
//
// Contract ids are GUID-suffixed per case, so several runs against one
// shared Redis never contend — and the fence-counter keys the impl leaves
// behind are per-id and tiny.
//
// When the env var is unset the pack emits a single `pending` case, so CI
// reads "skipped" rather than "green": a missing Redis is visible rather
// than silently passing.

let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_REDIS_CONNECTION" with
    | null
    | "" -> testList "RedisDistributedLock" [ ptestCase "skipped — TOOLUP_REDIS_CONNECTION not set" <| fun _ -> () ]
    | connectionString ->
        let factory () =
            match RedisDistributedLock.fromConnectionString connectionString None with
            | Some lck -> lck
            | None ->
                failwithf
                    "TOOLUP_REDIS_CONNECTION=%s is set but no Redis connection could be established"
                    connectionString

        IDistributedLockContract.contractFor "RedisDistributedLock (single-Redis lease)" factory