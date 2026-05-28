# ToolUp.RateLimit.AzureTableStorage

Azure Table Storage `IRateLimitStore` companion for `ToolUp.Platform`. Atomic increment-and-check via ETag-retry; suitable for multi-instance Kestrel deployments and serverless silos (Azure Functions) that share a single rate-limit budget across a fleet.

## When to use this companion

- Multi-instance `WebOnly` silos behind a load balancer that need a single shared rate-limit count.
- Azure Functions Consumption / Premium deployments — Table Storage is the cheapest Azure-native option for low-RPS endpoints.
- Deployments already running on Azure Storage primitives (the SDK's default `IBlobStorage` is also Azure-backed).

For higher-RPS workloads (≥ 100 RPS per key), prefer `ToolUp.RateLimit.Redis` — Redis `INCR` is O(1) per call; Azure Tables' ETag-retry pattern starts contending at that scale.

## Install

```xml
<PackageReference Include="ToolUp.RateLimit.AzureTableStorage" />
```

The package depends on `Azure.Data.Tables` (Microsoft-shipped SDK) — no other vendor dep.

## Usage

```fsharp
open ToolUp.Platform
open ToolUp.RateLimit.AzureTableStorage

let options = {
    Options.defaults with
        ConnectionString = System.Environment.GetEnvironmentVariable "AZURE_STORAGE_CONNECTION_STRING"
}

ServerApp.empty
|> ServerApp.withConfig {
    ServerConfig.defaults with
        RateLimitStore = ExternalRateLimitStore
        RateLimits = [
            RouteLimit.perIpPerMinute "/api/calculate/" 60
            RouteLimit.perIpPerMinute "/api/export/" 10
        ]
}
|> ServerApp.withServiceConfig (fun services ->
    let logger = ConsoleLogger.create ()
    services.AddSingleton<IRateLimitStore>(AzureTableRateLimitStore.create options logger))
|> ServerApp.run
```

The store auto-creates the `ToolupRateLimit` table on first use. Override the table name via `Options.TableName` if a deployment has multiple SDK-rooted apps sharing one storage account.

## Storage layout

- **Table**: `ToolupRateLimit` (configurable).
- **PartitionKey**: `<window>|<storeKey>` — e.g. `PerMinute|ip:1.2.3.4`. One partition per `(window, key)` pair so each `IncrementAndCheck` writes within a single partition (Azure Tables' atomicity unit).
- **RowKey**: `<windowStartTicks>` — UTC tick count of the calendar-aligned window boundary. A `PerMinute` window keyed at 12:34:56 writes to row `<ticks for 12:34:00>`.
- **Properties**:
  - `Count: Int32` — running count inside the current window.
  - `ExpiresAt: DateTimeOffset` — used by `Maintenance.sweep` (when wired) for cleanup of old window rows. Azure Tables has no native TTL, so a periodic sweep is the cleanup mechanism.

## Atomic increment-and-check

ETag-retry pattern, retry up to `Options.MaxRetries` (default 5):

1. Read entity for `(partitionKey, windowStartRowKey)`. Returns `(count, ETag)` or `None` on first writer.
2. Compute `next = current + 1`.
3. Write entity with the read-time ETag. `AddEntityAsync` on first write; `UpdateEntityAsync` thereafter.
4. On `PreconditionFailed (412)` / `Conflict (409)` — another writer raced. Re-read, recompute, retry.
5. Compare `next` against `threshold` — emit `AllowWithRemaining (threshold - next)` or `DenyWithError`.

Throughput ceiling is bounded by Azure Tables' per-partition write rate (~500 ops/sec under best conditions; lower with contention). For per-IP keys at scale, partitions are naturally distributed (one per IP) so the per-IP ceiling is what matters. For per-route or per-tenant aggregates that funnel many requests through one partition, the ceiling becomes the bottleneck — switch to Redis.

## Cleanup of old windows

Azure Tables has no native TTL. Old window rows (counts in a calendar minute that's already past) sit in the table forever unless swept. Three options:

1. **Ignore.** Counts overwrite themselves in the same row each new window — the dead rows are old boundaries. Storage growth is one row per (key, window) per window-cycle. For a deployment with 1000 IPs and per-minute policies, that's 1.4M rows/day of dead entries. Cheap on Azure Tables (< $0.10/month/M rows) but unbounded.

2. **Per-key boundary update.** Modify the store to UPDATE the same row each new window with a new RowKey-encoded boundary. Saves on row count but loses the per-window historical signal (no "how many requests in the 12:34 minute" auditability).

3. **Periodic sweep.** A consumer-wired `IJobHandler` running every hour walks rows with `ExpiresAt < now` and deletes them. The companion's `Maintenance.sweep` helper (when shipped) does this. For now: option 1 is the default; consumers can wire option 3 ad-hoc.

## Six-rule portability audit (GP 12)

`IRateLimitStore` honours all six rules. This companion specifically:

1. **Identity by value.** `InboundRateLimitKey` is a serialisable DU over `string`; the PartitionKey derivation `<window>|<storeKey>` is purely string-based with no live handles.
2. **Async at every boundary.** Every interface method returns `Async<_>`; the implementation awaits `Azure.Data.Tables` `Async` task overloads throughout.
3. **Retry / supervision as data.** Failures surface as `Result<_, RateLimitStoreError>`; the ETag-retry loop is in-store, but exhausted retries return `Error(StoreUnavailable _)` rather than throwing.
4. **Stateless handlers between invocations.** `IncrementAndCheck` re-reads its state per call — no in-memory cache between calls. Multiple SDK instances pointing at the same Table see consistent counts via the ETag conflict resolution.
5. **No cross-shard ordering promises.** Counts partition per `(window, key)` — cross-key totals are not guaranteed monotonic. Per-key ordering is honoured by Azure Tables' partition-scoped writes.
6. **Precision at the lower bound.** Atomic-increment ceiling documented: ~10-100 RPS per partition (per Azure Tables docs); higher RPS workloads need Redis. The `RateLimitWindow.SlidingWindow` case uses a single-bucket approximation here (per-bucket sharding is a Redis-only feature in v1).

## Limitations

- **In-memory `GetRecentDecisions` buffer.** The Phase 61 admin widget reads recent deny events from an in-memory queue, not from the Table. Multi-instance deployments aggregate via a sidecar logging/metrics path (not via the store); this is the same shape as the in-memory default and intentional — recent-events tracking is observability, not correctness.
- **`SlidingWindow` is approximate.** The Redis impl supports true bucket-sharded sliding windows via Lua; Azure Tables doesn't have the per-bucket atomic-increment primitive needed. Use `PerMinute` / `PerHour` for tight calendar-aligned windows.
- **No native TTL.** See "Cleanup of old windows" above.
- **First-write race possible at high concurrency.** When two writers concurrently see no entity (`AddEntityAsync`), the second sees `Conflict (409)` and retries — adding one round-trip. The ETag-retry loop handles this transparently.

## License

Apache-2.0. See `LICENSE` at the repo root.
