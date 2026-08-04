# ToolUp.EmbeddingCaches.Redis

Redis-backed `IEmbeddingCache` for the `ToolUp.RAG` companion — a **cross-replica** embedding cache.

| Cache | Sharing | Survives restart | Cost model |
|---|---|---|---|
| `InMemoryEmbeddingCache` (default) | one process | no | every replica pays for the same text |
| **`ToolUp.EmbeddingCaches.Redis`** | **every replica** | yes (TTL-bounded) | the fleet pays once per `(model, text)` |

The shipped default is a per-process LRU. That is correct but not cheap once a deployment runs more than one replica: the same query embeds on replica A and embeds *again* on replica B, and a re-ingested document re-embeds on whichever replica happened to drain its ingestion job. Here the cache is shared state, so a miss on one replica becomes a hit on the next.

Licensed under Apache-2.0. `StackExchange.Redis` is the only vendor dependency, and it stays inside this package (GP 1).

## Composition

```fsharp
open ToolUp.RAG.EmbeddingCaches.Redis

// Simplest form — the companion connects and owns the multiplexer.
let cache = RedisEmbeddingCache.create connectionString (Some logger)

// Preferred when the deployment already runs Redis for notifications or
// the distributed lock: pass the SAME multiplexer, pay for one pool.
let cache =
    RedisEmbeddingCache.fromMultiplexer
        multiplexer
        RedisEmbeddingCacheOptions.defaults
        (Some logger)
        (Some metricsSink)
```

The connection string comes from the deployment (`ISecretStore` / configuration) — the companion never reads environment variables itself. Deployments that wire Redis for several substrates conventionally source all of them from `TOOLUP_REDIS_CONNECTION`.

Register the health probe alongside it:

```fsharp
Health.create multiplexer
```

## Options

| Option | Default | Notes |
|---|---|---|
| `KeyPrefix` | `toolup:embeddings` | Namespace for every key written. Glob metacharacters are refused at `create` — `Clear ()` deletes by `SCAN MATCH "<KeyPrefix>:*"`, and a glob in the prefix would widen that deletion beyond this cache. |
| `Ttl` | 7 days | Bounds the keyspace of a cache nobody prunes. An embedding is deterministic for a given key, so the TTL is a memory decision, not a correctness one. |
| `HitRateScope` | `LocalProcess` | See below. |
| `Database` | `-1` | Whatever the connection string selected. |

## What is in a key — and what is not

The key is the caller's `EmbeddingCacheKey` rendered into the Redis keyspace:

```
{KeyPrefix}:k:1:{providerId}:{modelId}:{dimensions}:{textHash}
```

`textHash` is **already** a SHA-256 hex digest by the time it reaches this companion (`CachingEmbeddingProvider` hashes before calling), so raw user text never lands in a Redis key — the same guarantee the in-memory cache gives, preserved across a store an operator can `KEYS`-browse. `providerId` and `modelId` are `%`-escaped, so a `:` inside either cannot shift a segment boundary and make two distinct models collide.

Because `EmbeddingVersion` is part of the key, **a model swap misses by construction**: `text-embedding-3-small` and `text-embedding-3-large` write disjoint keys, and there is no flush step to remember at cutover.

`EmbeddingCacheKey` carries no tenant component — by design, and unchanged here. Embeddings are a deterministic function of text, so two tenants indexing the same document text share an entry; what they do *not* share is any chunk, scope, or retrieval result. Sharing the cache across replicas removes the divergence the in-memory default has (hit on A, miss on B); it does not widen what is cached.

## Payload format

Values are framed rather than raw-copied, because the entire point is that a **different process** reads what this one wrote:

```
magic "TUEC" (4) | format version, int32 LE (4) | dimensions, int32 LE (4) | dimensions × float32 LE
```

A payload that fails any of those checks — foreign writer in the same namespace, truncation, a future format version — is discarded and reported as a miss, so the worst case is one recomputation rather than a wrong vector entering retrieval.

## Failure posture

**The cache is an optimisation, never a source of truth.** A Redis outage must make embeddings more expensive, never wrong or unavailable:

- `TryGet` degrades to a miss and logs `Warn` — the caller embeds as an uncached deployment would.
- `Set` drops the write and logs `Warn` — never raises; the embedding is already in the caller's hand.
- The health probe reports **`Degraded`, never `Unhealthy`**. `Unhealthy` on a readiness probe takes the replica out of rotation, and every other replica is looking at the same Redis — a cost problem would become an availability problem, and the rotation would empty.
- `Clear` is the deliberate exception: it is the privacy-correct response to an erasure request (Phase 9h DSR), so a failure is **raised**, not swallowed. It deletes the whole `{KeyPrefix}:*` namespace by `SCAN` (never `KEYS`), including entries written under an older key schema, across every connected primary.

Misconfiguration is fail-loud at `create` time, never at first use inside a live request: an invalid option or an empty/unusable connection string raises `RedisEmbeddingCacheException` with a message naming what is wrong. Option validation runs **before** the connection attempt, so a typo in the options is reported as a typo rather than surfacing as a connection error.

## Hit-rate telemetry

`IEmbeddingCache.HitRate ()` returns a single float, and the interface explicitly permits a distributed implementation to report a local figure. Both are available here:

- **`LocalProcess`** (default) — this instance's lookups only. Zero extra Redis round-trips, and the same shape the in-memory cache reports. `HitRate ()` returns the local rate.
- **`SharedAcrossReplicas`** — two Redis counters (`{KeyPrefix}:s:hits` / `:misses`) are maintained, so `HitRate ()` answers for the whole fleet. Costs one extra round-trip per lookup and one per read, which is why it is opt-in (GP 11).

The concrete type also exposes `Stats ()`, which returns both:

```fsharp
type EmbeddingCacheStats = {
    LocalHits: int64
    LocalMisses: int64
    LocalHitRate: float
    SharedHits: int64 option
    SharedMisses: int64 option
    SharedHitRate: float option
}
```

The `Shared*` fields are `None` under `LocalProcess` scope **and** when the counters could not be read — an absent figure is reported as absent, never as zero.

Pass an `IMetricsSink` to emit:

| Metric | Kind | Tags |
|---|---|---|
| `toolup.rag.embedding_cache.lookups.total` | Counter | `result` ∈ `hit` \| `miss` \| `error` |
| `toolup.rag.embedding_cache.hit_rate` | Gauge, `[0, 1]` | `scope` ∈ `local` \| `shared` |

Register `CacheMetrics.definitions` with the deployment's metric registry — the sink's tag allowlist silently drops tags for an unregistered metric, which would collapse hits and misses into one untagged series.

## Portability (GP 12)

| Rule | Status |
|---|---|
| 1 — Identity by value | `EmbeddingCacheKey` is a value record; no live handle crosses the surface. |
| 2 — Async at every boundary | Every `IEmbeddingCache` member returns `Async<_>`. |
| 3 — Retry / supervision as data | No callbacks. Retry and reconnection are StackExchange.Redis' own, configured through the connection string. |
| 4 — Stateless between calls | The only per-instance state is the hit/miss counters, which are telemetry and never affect a lookup's answer. `TryGet` / `Set` require no prior call in this process — the entry may have been written by a different replica. **This is what disqualifies `InMemoryEmbeddingCache` from distributed use and what this companion exists to fix.** |
| 5 — No cross-key ordering | None claimed. The interface already warns that a `Set` followed by `TryGet` may miss against a distributed backend. |

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
