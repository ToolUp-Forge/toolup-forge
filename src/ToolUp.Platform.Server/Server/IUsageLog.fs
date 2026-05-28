namespace ToolUp.Platform.Usage

open System

/// Append-only ledger of `UsageRecord`s for the deployment, partitioned
/// by `ScopeId`. The default implementation is the blob-backed
/// `UsageLog` shipped with the SDK; companion packages MAY register an
/// alternative (e.g. database-backed for high-throughput tenants) by
/// satisfying this interface and replacing the DI registration.
///
/// **Cross-team isolation is non-negotiable (GP 4).** `Query` and
/// `Aggregate` MUST never return a record whose `ScopeId` differs
/// from the requested `scopeId`. The default impl achieves this
/// structurally — `_platform/usage/{scopeId}/...` keys are never
/// enumerated outside the requested scope. Replacement implementations
/// MUST preserve the same property; the contract pack
/// (`IUsageLogContract`) carries the load-bearing test.
///
/// **Async at every boundary (Phase 9c rule 2).** All members return
/// `Async<_>` even though `Record` is fire-and-forget — the caller does
/// not block on the flush, but the API still returns to the await
/// boundary so framework-portable replacements (Akka.NET / Orleans /
/// Hangfire-backed) compose cleanly.
type IUsageLog =
    /// Record one usage event. Fire-and-forget — the default
    /// implementation buffers internally and flushes in batches.
    /// Callers do not need to await completion of the underlying
    /// write; the returned `Async<unit>` completes once the record
    /// is in the queue (or has been dropped on a producer timeout —
    /// see `BatchFlushPolicy.ProducerWaitTimeout`).
    abstract Record: record: UsageRecord -> Async<unit>

    /// Return matching records for a scope. `resourceKind = None`
    /// matches every kind; `range = None` returns the entire history
    /// available to the implementation. Implementations enumerate
    /// only the requested scope's blobs / partition.
    abstract Query:
        scopeId: string * resourceKind: string option * range: (DateTime * DateTime) option -> Async<UsageRecord list>

    /// Aggregate the scope's records by the requested grouping. Sums
    /// `Quantity` per bucket. See `UsageGrouping` for key shape per
    /// case.
    abstract Aggregate: scopeId: string * grouping: UsageGrouping -> Async<Map<string, decimal>>

/// SDK-default no-op implementation of `IUsageLog`. Registered when
/// `ServerConfig.UsageMetering = NoUsageMetering` so emission sites
/// can resolve the service unconditionally — `IUsageLog` is never
/// `null` in DI when the SDK is composed. Mirrors the
/// `EventStoreAuditLog` no-op pattern used by `IAuditLog`.
type NoOpUsageLog() =
    interface IUsageLog with
        member _.Record(_record) = async.Zero()

        member _.Query(_scopeId, _resourceKind, _range) = async.Return []

        member _.Aggregate(_scopeId, _grouping) = async.Return Map.empty