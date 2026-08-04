# ToolUp.Platform.NotificationChannels.Redis

Redis `INotificationChannel` for `ToolUp.Platform` — scope-isolated pub/sub over `StackExchange.Redis`. Replaces the default `InMemoryNotificationChannel` for multi-instance / multi-silo deployments where SSE subscribers and notification publishers may live on different process boundaries.

Activated via `TOOLUP_NOTIFICATION_CHANNEL=redis` + `TOOLUP_REDIS_CONNECTION=<conn-string>`. Per-scope topic isolation is structural (one topic per `ScopeId`) — not a post-hoc filter.

## Also in this package

Two further Redis-backed substrates ship here rather than in their own packages, because they share the channel's `IConnectionMultiplexer` — a deployment that already runs Redis for notifications gets both without opening a second connection:

- **`RedisLifecycleLock`** — cross-replica `ILifecycleLock` for tenant-offboard exclusion, over `SET NX PX` + a compare-and-delete Lua release.
- **`RedisDistributedLock`** — cross-instance `IDistributedLock`, the SDK-wide lease primitive: `SET NX PX` for the fail-fast acquire, `INCR` for monotonic fence tokens, and compare-and-act Lua scripts for release / renew. Activated via `TOOLUP_DISTRIBUTED_LOCK=redis` (+ the same `TOOLUP_REDIS_CONNECTION`); wire it with `DistributedLockSelection.fromEnv logger [ RedisDistributedLock.resolver ]` and register the result from `ComposeExtensions.ServiceConfig`.

  **The contract is a "single-Redis lease", not Redlock.** Every operation targets one Redis, so a failover to a replica that has not yet received the lock key can hand the same id to a second holder. `Lease.FenceToken` is the path to store-side safety for callers that need more than contention reduction; a deployment needing quorum semantics implements `IDistributedLock` over its own consensus store. Both the file header and the SDK technical guide spell out the trade.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
