// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.IngestionQueues.Redis.Health

open System
open StackExchange.Redis
open ToolUp.Platform.HealthChecks

// ─── Phase 509 — Redis ingestion-queue health probe ──────────────────
//
// A PING against the multiplexer backing the queue. Deliberately never
// `Unhealthy`, for the same reason the Redis embedding-cache probe is
// not: every replica looks at the SAME Redis, so failing readiness would
// empty the rotation rather than route around anything — turning a
// degraded-ingestion problem into a total-outage one. Retrieval over the
// already-indexed corpus keeps working throughout; what stops is NEW
// documents being queued, and the enqueue path already surfaces that as
// backpressure (the caller marks the document `Failed` and emits the
// drop-observability triple).
//
// Registered as `Readiness` so it appears on `/ready` rather than driving
// orchestrator RESTARTS from `/health`.

/// Companion-contributed `IHealthCheck` for the Redis ingestion queue.
/// Pass the same `IConnectionMultiplexer` the store was built from, so
/// the probe reflects the queue's actual connection state.
type RedisIngestionQueueHealthCheck(multiplexer: IConnectionMultiplexer) =
    interface IHealthCheck with
        member _.Name = "ingestion_queue:redis"
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
                                "Redis PING took %.0fms — ingestion enqueue and claim are on that round-trip, so uploads will be slow to become searchable"
                                latency.TotalMilliseconds
                        )
                else
                    return Healthy
            with ex ->
                return
                    Degraded(
                        sprintf
                            "Redis is unreachable (%s) — already-indexed content still retrieves, but NEW uploads cannot be queued for indexing and are surfaced to the uploader as failed"
                            ex.Message
                    )
        }

/// Create the probe from a live `IConnectionMultiplexer`.
let create (multiplexer: IConnectionMultiplexer) : IHealthCheck =
    RedisIngestionQueueHealthCheck(multiplexer) :> IHealthCheck