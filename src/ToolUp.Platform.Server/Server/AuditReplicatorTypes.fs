namespace ToolUp.Platform

open System

// Phase 11.C.5 Tier 3 — `AuditReplicatorRetryPolicy` consolidated into
// the unified `RetryPolicy` at `Shared/Types/RetryPolicy.fs`. The
// audit-replicator-specific defaults (5 attempts / 1 s initial / 60 s
// cap) move to `AuditReplicatorRetryPolicy.defaults` below — kept as a
// named alias for grep-ability but typed as the unified `RetryPolicy`.

/// Audit-replicator-tuned defaults for the unified `RetryPolicy`.
/// More aggressive than `TransactionalRetryPolicy.defaults` (which is
/// conservative on first-attempt latency for emails) — audit
/// replication should recover transient blips quickly while still
/// bounding the budget for wedged sinks. Phase 9g sink delivery
/// behaviour preserved byte-for-byte; only the type name changes.
module AuditReplicatorRetryPolicy =
    /// Default retry policy. Five attempts, 1-second initial backoff,
    /// 60-second cap, no per-call timeout (sink batches manage their
    /// own deadlines via `BatchPolicy.LingerMs`).
    let defaults: RetryPolicy = {
        MaxAttempts = 5
        InitialBackoff = TimeSpan.FromSeconds 1.0
        MaxBackoff = TimeSpan.FromSeconds 60.0
        Timeout = None
    }

/// Batching policy for `AuditReplicator`. Controls how many events the
/// dispatcher accumulates per `IAuditSink.Deliver` call and how long it
/// waits before flushing a partial batch. Phase 9c rule 6 — precision
/// expressed as data; no sub-second LingerMs is required (1000ms
/// satisfies the "within seconds" SLA without sub-second precision
/// promises that distributed companions might struggle to honour).
type AuditReplicatorBatchPolicy = {
    /// Maximum events per `Deliver` call. Conservative default works
    /// across all three reference sinks (Splunk HEC accepts >500/req,
    /// S3Archive has no batch ceiling, Datadog Logs allows up to 1000
    /// events per /api/v2/logs request — we cap at 100 so vendor batch
    /// quirks don't surface as Result.Error). Default: 100.
    MaxBatchSize: int
    /// Time to wait after the first event in a partial batch before
    /// flushing. Trades latency for throughput. Default: 1000 ms
    /// (1-second debounce). Set higher (e.g. 5000) for write-once
    /// archive sinks where per-batch overhead dominates; set lower
    /// (e.g. 200) for low-latency SIEMs where freshness trumps
    /// efficiency.
    LingerMs: int
    /// Bounded channel capacity per sink. Drop-write on overflow —
    /// dropped events are recovered by the periodic catch-up sweep
    /// (`CatchUpSweepInterval` on `AuditReplicatorOptions`). Default:
    /// 1024 (mirrors `WebhookDispatcher` queue capacity).
    QueueCapacity: int
}

module AuditReplicatorBatchPolicy =
    let defaults: AuditReplicatorBatchPolicy = {
        MaxBatchSize = 100
        LingerMs = 1000
        QueueCapacity = 1024
    }

/// Phase 9g — tuning knobs for `AuditReplicator`. Optional on
/// `ServerConfig` (`None` → all defaults). Apps with stable single-sink
/// configs typically leave this unset; deployments running multiple
/// sinks with conflicting latency / throughput needs override per
/// deployment.
type AuditReplicatorOptions = {
    /// Retry behaviour on transient sink failure. Default:
    /// `AuditReplicatorRetryPolicy.defaults` (the unified `RetryPolicy`
    /// with audit-replicator-tuned values).
    RetryPolicy: RetryPolicy
    /// Batching behaviour per sink worker. Default:
    /// `AuditReplicatorBatchPolicy.defaults`.
    BatchPolicy: AuditReplicatorBatchPolicy
    /// Periodic catch-up sweep interval. The dispatcher re-reads each
    /// sink's per-scope cursor and replays any audit events newer than
    /// the cursor. Catches events dropped on bounded-channel overflow,
    /// missed during sink-down windows that didn't trigger a process
    /// restart, or written by a sibling silo (when distributed
    /// companions land — Phase 9c half 2). Default: 5 minutes. Set
    /// `TimeSpan.MaxValue` to disable (restart-only recovery).
    CatchUpSweepInterval: TimeSpan
}

module AuditReplicatorOptions =
    let defaults: AuditReplicatorOptions = {
        RetryPolicy = AuditReplicatorRetryPolicy.defaults
        BatchPolicy = AuditReplicatorBatchPolicy.defaults
        CatchUpSweepInterval = TimeSpan.FromMinutes 5.0
    }

/// Phase 9g — per-`(sinkName, scopeId)` cursor. Persisted as JSON via
/// `FableJsonConverter` at
/// `_platform/audit-replicator/{sinkName}/{scopeId}.cursor`. Advances
/// only after `IAuditSink.Deliver` returns `Result.Ok`. The pair
/// `(LastDeliveredAt, LastDeliveredEventId)` is a deterministic
/// total order over events within a scope — the timestamp is the
/// primary sort key, the event id (`Guid`) is a tie-breaker for events
/// with identical `OccurredAt` (rare but possible at high write rates
/// on systems with coarse clock resolution).
///
/// **Why per-scope, not global.** Cross-scope total ordering would
/// require a new global secondary index over all audit events, which
/// Phase 9f did not build. Per-scope cursor avoids the index — the
/// existing `events/{scope}/_by-source/_platform.audit/{eventId}.ref`
/// index gives "all audit events in scope since cursor" cheaply.
/// Trade-off: cross-scope events at the sink may interleave by arrival
/// order rather than `OccurredAt` order. Phase 9c rule 5 explicitly
/// permits this — ordering is per-shard.
type AuditReplicatorCursor = {
    /// `OccurredAt` of the most recent successfully delivered event.
    /// Used to filter `IEventStore.ReadBySource` results during catch-up
    /// sweeps and startup recovery.
    LastDeliveredAt: DateTime
    /// `Id` of the most recent successfully delivered event. Used as a
    /// tie-breaker for events with identical `OccurredAt`.
    LastDeliveredEventId: Guid
}

module AuditReplicatorCursor =
    /// Cursor representing "no events delivered yet" — initial state
    /// before the first batch lands. `LastDeliveredAt = MinValue` so
    /// every event in the audit history qualifies as "newer than the
    /// cursor" on first run.
    let empty: AuditReplicatorCursor = {
        LastDeliveredAt = DateTime.MinValue
        LastDeliveredEventId = Guid.Empty
    }

    /// Predicate: is `evt` strictly after the cursor's position? Used
    /// to filter `ReadBySource` results during catch-up sweeps.
    /// `OccurredAt` is the primary sort key; `Id` breaks ties.
    let isAfter (cursor: AuditReplicatorCursor) (evt: ModuleEvent) : bool =
        if evt.OccurredAt > cursor.LastDeliveredAt then
            true
        elif evt.OccurredAt < cursor.LastDeliveredAt then
            false
        else
            // Equal timestamp — tie-break on Guid value.
            evt.Id.CompareTo(cursor.LastDeliveredEventId) > 0