namespace ToolUp.Platform

open System

/// Phase 9g — out-of-platform audit-event sink. One implementation per
/// destination (Splunk HEC, Datadog Logs, S3 Object Lock archive, custom
/// SIEM endpoint), injected via the
/// `src/AuditSinks/<Vendor>/` companion pattern.
///
/// **Why it exists.** Phase 9's `IEventStore`-backed audit log
/// (`_platform.audit`) is necessary but insufficient for regulated-sector
/// deployments. SOC 2 / HIPAA / GDPR Article 30 / SOX-style audits
/// require the audit log to be replicated to a sink the deploying
/// organisation does not also control. `IAuditSink` is the portable
/// shape every such replication target implements.
///
/// **Scope of responsibility.** Sinks are stateless transports. Batching,
/// retries, dead-letter, throttling, ordering, and cursor management
/// all live in the `AuditReplicator` `BackgroundService`. The smallest
/// viable sink is two members (`Name` + `Deliver`).
///
/// **Identity.** `Name` is the deployment-unique sink identifier — the
/// `AuditReplicator` keys cursors by it
/// (`_platform/audit-replicator/{Name}/{scopeId}.cursor`). The compose
/// layer rejects duplicate `Name`s with a fatal startup error (mirrors
/// Phase 6f duplicate-kind rejection on `INotificationSink.Kind`). Use
/// stable, vendor-neutral names — `"splunk-prod"` rather than
/// `"splunk-hec-7.0"` so refactors don't migrate cursors. Phase 9c rule
/// 1 — identity by string.
///
/// **Statelessness.** `Deliver` derives its result entirely from the
/// `batch` argument plus DI-resolved infrastructure (`HttpClient`,
/// `ISecretStore`). Implementations may keep vendor-SDK clients
/// (`HttpClient`) for connection-pooling, but those are pure plumbing,
/// not state. Phase 9c rule 4.
///
/// **Idempotency.** Sinks must be idempotent at the batch level — the
/// dispatcher may invoke `Deliver` with the same batch more than once
/// after a transient failure, a recovery sweep, or a process restart
/// where the cursor was not yet advanced. Recommended idempotency keys:
///   * Splunk HEC — `_meta.uuid` per event derived from the event id
///   * S3Archive — content-addressable blob naming
///   * Datadog Logs — natural deduplication via host+service+timestamp
///     tags (Datadog's intake is best-effort dedup; deployments needing
///     stricter guarantees route through a vendor-side dedup layer)
///
/// **Cross-batch ordering.** The dispatcher delivers batches to a single
/// sink in `OccurredAt` order **within a single scope**; cross-scope
/// ordering is not promised (Phase 9c rule 5 — per-scope cursors). A
/// vendor index that aggregates across tenants will see batches from
/// different scopes interleave; the per-event `OccurredAt` field is the
/// source of truth for downstream temporal queries.
///
/// **Precision.** Delivery is best-effort within seconds of `IEventStore.Write`
/// in steady state. The dispatcher's `BatchPolicy.LingerMs` adds debounce
/// (default 1s) so multi-event bursts arrive as one batch. Phase 9c
/// rule 6 — no sub-second contract.
///
/// **Audit responsibility.** Sinks do NOT emit audit events themselves.
/// The replicator reads the `Result` and emits the matching
/// `AuditSinkDelivered` / `AuditSinkFailed` / `AuditSinkDeadLettered`
/// event under `_platform`. Centralising audit at the dispatcher keeps
/// sinks minimal and avoids per-vendor bookkeeping drift.
///
/// **Phase 66 Stream B.7 — envelope contract.** `Deliver` takes
/// `AuditEnvelope list` (not bare `AuditEvent list`). The envelope
/// carries the resolved `AuditSubject`, `OccurredAt`, and `ScopeId`
/// alongside the event body — sinks read subject info from the
/// envelope rather than re-deriving via per-payload introspection.
/// Each sink declares `SchemaVersion: int` against
/// `LatestAuditSchemaVersion` so the dispatcher can detect drift and
/// future versions can introduce a translation step. Today every
/// shipped sink is at `2`; `1` is reserved for pre-Phase-66 wire
/// compatibility.
type IAuditSink =
    /// Deployment-unique sink identifier. Used as the cursor-key suffix
    /// in `_platform/audit-replicator/{Name}/{scopeId}.cursor`. Stable
    /// across the deployment lifetime — renaming a sink invalidates its
    /// cursors and triggers full re-replication of the audit history.
    abstract Name: string

    /// Phase 66 Stream B.7 (design D16) — wire-format envelope version
    /// the sink expects. Compare against `LatestAuditSchemaVersion`
    /// (defined in `AuditTypes.fs`); a sink at an older version
    /// receives events translated into its earlier shape by the
    /// dispatcher when a translation path exists. Today's three
    /// reference companions (SplunkHec / DatadogLogs / S3Archive)
    /// declare `2`; new companions should also declare `2` unless they
    /// deliberately consume the pre-Phase-66 bare-`AuditEvent` shape
    /// (`1` — historical only; no production sinks ship at this
    /// version).
    abstract SchemaVersion: int

    /// Deliver one batch as a single logical operation. Returns
    /// `Result.Ok ()` if every event in the batch reached the
    /// destination; `Result.Error msg` (with vendor diagnostic) if any
    /// event was rejected, the connection failed, or the destination
    /// reported a transient error. The dispatcher retries the entire
    /// batch on `Result.Error`; sinks must therefore be batch-idempotent
    /// (see the type-level docstring for recommended patterns).
    ///
    /// Empty batches are valid and must return `Result.Ok ()` without
    /// hitting the destination. The dispatcher only calls `Deliver` with
    /// non-empty batches in practice, but `Result.Ok ()` on empty
    /// keeps the contract trivially testable.
    ///
    /// Sinks classify failures by message content; the dispatcher does
    /// not introspect the error string for retry decisions — every
    /// `Result.Error` triggers the same exponential-backoff retry path
    /// up to `RetryPolicy.MaxAttempts`. Permanent failures (4xx-style
    /// vendor reject) are not distinguished from transient failures
    /// (5xx, network) at the interface level. If finer classification is
    /// needed, sinks may throw a typed exception that bypasses the
    /// `Result` contract — but the recommended path is to surface a
    /// diagnostic string and let the dispatcher exhaust the retry
    /// budget.
    abstract Deliver: batch: AuditEnvelope list -> Async<Result<unit, string>>