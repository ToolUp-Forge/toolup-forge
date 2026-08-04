// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.EmbeddingCaches.Redis.Health

open System
open StackExchange.Redis
open ToolUp.Platform.HealthChecks

// ─── Phase 513 — Redis embedding-cache health probe ──────────────────
//
// A PING against the multiplexer backing the cache. Deliberately NEVER
// `Unhealthy`: an unreachable embedding cache costs money, not
// correctness — `RedisEmbeddingCache.TryGet` degrades to a miss and the
// embedding provider recomputes. Taking the replica out of rotation
// (which is what `Unhealthy` on a Readiness probe does) would turn a
// cost problem into an availability problem, and every OTHER replica is
// looking at the same Redis, so the rotation would empty.
//
// `Degraded` is exactly the state the aggregator defines for this: the
// dependency is misbehaving and the operator should see it, but `/ready`
// stays 200. Registered as `Readiness` so it appears on `/ready` rather
// than driving orchestrator RESTARTS from `/health`.

/// Companion-contributed `IHealthCheck` for the Redis embedding cache.
/// Pass the same `IConnectionMultiplexer` the cache was built from, so
/// the probe reflects the cache's actual connection state.
type RedisEmbeddingCacheHealthCheck(multiplexer: IConnectionMultiplexer) =
    interface IHealthCheck with
        member _.Name = "embedding_cache:redis"
        member _.Kind = Readiness

        // A PING on a healthy connection finishes in single-digit ms; a
        // 1s ceiling absorbs network jitter without hiding degradation
        // under the timeout.
        member _.Timeout = TimeSpan.FromSeconds 1.0

        member _.Check() = async {
            try
                let database = multiplexer.GetDatabase()
                let! latency = database.PingAsync() |> Async.AwaitTask

                if latency.TotalMilliseconds > 500.0 then
                    return
                        Degraded(
                            sprintf
                                "Redis PING took %.0fms — embedding-cache lookups are slower than recomputing for some providers"
                                latency.TotalMilliseconds
                        )
                else
                    return Healthy
            with ex ->
                return
                    Degraded(
                        sprintf
                            "Redis is unreachable (%s) — embedding lookups fall back to the provider, so retrieval still works but embedding spend rises"
                            ex.Message
                    )
        }

/// Create the probe from a live `IConnectionMultiplexer`.
let create (multiplexer: IConnectionMultiplexer) : IHealthCheck =
    RedisEmbeddingCacheHealthCheck(multiplexer) :> IHealthCheck