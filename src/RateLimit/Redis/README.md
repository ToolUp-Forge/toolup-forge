# ToolUp.RateLimit.Redis

Redis `IRateLimitStore` companion for `ToolUp.Platform`. `INCR` + `EXPIRE` atomic increment-and-check for calendar windows; Lua-scripted true sliding window via sorted sets.

## When to use this companion

- Multi-instance Kestrel deployments where the rate-limit budget is shared across the fleet (the canonical choice).
- High-RPS endpoints — Redis hits 100k+ ops/sec per instance vs ~10-100 ops/sec for Azure Tables ETag-retry.
- Sliding-window policies — Redis's Lua scripting supports true bucket-sharded sliding windows; the in-memory and Azure Tables defaults approximate them.

For deployments already running on Azure Storage primitives that don't need Redis for other workloads, `ToolUp.RateLimit.AzureTableStorage` keeps the dep graph smaller.

## Install

```xml
<PackageReference Include="ToolUp.RateLimit.Redis" />
```

The package depends on `StackExchange.Redis`, the .NET community-canonical Redis client.

## Usage

```fsharp
open ToolUp.Platform
open ToolUp.RateLimit.Redis

let options = {
    Options.defaults with
        ConnectionString = System.Environment.GetEnvironmentVariable "REDIS_CONNECTION_STRING"
}

ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        RateLimitStore = ExternalRateLimitStore
        RateLimits = [
            RouteLimit.perIpPerMinute "/api/calculate/" 60
            { Route = "/api/burst/"
              Key = ByIp
              Window = SlidingWindow(TimeSpan.FromSeconds 10.0, 10)
              Threshold = 20
              OnExceeded = Return429 }
        ]
}
|> ServerApp.withServiceConfig (fun services ->
    let logger = ConsoleLogger.create ()
    services.AddSingleton<IRateLimitStore>(RedisRateLimitStore.create options logger))
|> ServerApp.run
```

## Key layout

`toolup:rl:<window>:<storeKey>` (configurable prefix via `Options.KeyPrefix`).

- **Calendar windows** (`PerSecond` / `PerMinute` / `PerHour` / `PerDay`): the key includes the window-boundary tick count — e.g. `toolup:rl:PerMinute:ip:1.2.3.4:638529456000000000`. Each window owns its own counter; old windows expire via Redis TTL (set to the window duration on `EXPIRE`).
- **Sliding window** (`SlidingWindow(duration, _)`): single key per `(window, identity)` — `toolup:rl:Sliding30.000000s:ip:1.2.3.4`. The value is a sorted set of timestamps; `ZREMRANGEBYSCORE` evicts old members, `ZCARD` returns the trailing count.

## Atomic increment-and-check

### Calendar windows

```
MULTI
  INCR  toolup:rl:PerMinute:ip:1.2.3.4:<boundaryTicks>
  EXPIRE toolup:rl:PerMinute:ip:1.2.3.4:<boundaryTicks> 60
EXEC
```

Two round-trips collapsed via pipelining (StackExchange.Redis handles this automatically when both operations target the same key). Returns the post-increment count; compared against threshold to emit `AllowWithRemaining` or `DenyWithError`.

### Sliding windows (Lua-scripted)

```lua
local key = KEYS[1]
local now = tonumber(ARGV[1])
local duration_ms = tonumber(ARGV[2])
local cutoff = now - duration_ms

redis.call('ZREMRANGEBYSCORE', key, '-inf', cutoff)
redis.call('ZADD', key, now, now .. ':' .. (redis.call('ZCARD', key) + 1))
local count = redis.call('ZCARD', key)
redis.call('PEXPIRE', key, duration_ms)
return count
```

Runs server-side under Redis's single-thread-per-shard model — atomic without `MULTI/EXEC`. The `ZADD` member is `<now>:<seq>` to keep entries unique even when two requests share the same millisecond timestamp.

## Six-rule portability audit (GP 12)

`IRateLimitStore` honours all six rules. This companion specifically:

1. **Identity by value.** `InboundRateLimitKey` serialises to a string Redis key with the `KeyPrefix:window:storeKey` shape. No live handles.
2. **Async at every boundary.** Every interface method awaits `StackExchange.Redis` `Async`-suffixed methods. The `ScriptEvaluateAsync` Lua call is non-blocking.
3. **Retry / supervision as data.** Failures surface as `Result<_, RateLimitStoreError>`. `StackExchange.Redis` handles its own reconnect logic internally; transient connection failures bubble as `StoreUnavailable`.
4. **Stateless handlers between invocations.** `IncrementAndCheck` makes a single Redis round-trip per call. No in-memory cache between calls. Multiple SDK instances pointing at the same Redis see consistent counts via Redis's single-thread-per-shard model.
5. **No cross-shard ordering promises.** Counts partition per `(window, key)`. Cross-key totals are not guaranteed monotonic. Per-key ordering is honoured by Redis's per-key serialisation.
6. **Precision at the lower bound.** Atomic-increment ceiling: ~100k ops/sec per Redis instance. The `SlidingWindow` Lua script is O(log N) per call where N is the count in the trailing window; tight policies (threshold ≤ 1000) stay sub-millisecond.

## Limitations

- **In-memory `GetRecentDecisions` buffer.** Per-instance recent-events buffer; multi-instance deployments aggregate via the metrics sink, not via the store. Same shape as the Azure Tables companion.
- **No Redis Cluster verification yet.** The companion uses `StackExchange.Redis`'s `ConnectionMultiplexer` which supports cluster mode, but the key layout uses a single key per `(window, identity)` — for `Cluster`, multiple SDK instances may end up addressing the same shard for hot IPs. Verify your cluster sharding handles the workload before flipping production.
- **No Lua-script SHA cache.** Each sliding-window call sends the full Lua source. For deployments where this matters (very-high-RPS sliding-window policies), the next iteration caches the script SHA via `EVALSHA` with `SCRIPT LOAD` fallback.
- **Sliding-window member format.** `<now-ms>:<seq>` provides per-call uniqueness up to ~1000 calls/ms per key (a reasonable ceiling). At higher rates, multiple calls share a sequence number and the sorted set deduplicates — count becomes lossy. For typical inbound-HTTP rate-limit workloads this is not a concern.

## License

Apache-2.0. See `LICENSE` at the repo root.
