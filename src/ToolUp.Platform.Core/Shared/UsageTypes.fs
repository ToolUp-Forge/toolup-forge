// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Usage

open System

// ─── Usage metering & cost attribution ───────────────────────────
//
// `UsageRecord` is the canonical shape stored by `IUsageLog` and
// surfaced through `IUsageQueryApi`. The schema is deliberately narrow:
//
//   * `RecordId` is a deterministic dedup key. The flusher merges
//     incoming records into an existing rollup file de-duped by
//     `RecordId`, so a crash-mid-write retry does not double-count.
//   * `ScopeId` is the team-isolation handle. Blob keys carry the
//     scope; queries enumerate only the requested scope's prefix.
//     Team isolation (NON-NEGOTIABLE) is structural here.
//   * `ResourceKind` discriminates the metered resource. The SDK
//     ships five canonical kinds (`ResourceKinds`); companions can
//     publish new ones — the field is `string` so the schema stays
//     open.
//   * `Quantity` is `decimal`, never `float`. Billing precision.
//   * `Origin` is a typed `ProviderOrigin option` rather than a
//     metadata key. BYOK and Managed-AI billing split on this
//     field; stringly-typed metadata is the wrong shape for
//     a critical-path discriminator. `None` for non-AI resources.
//   * `Metadata` carries free-form tags (model name, latency
//     bucket, route) that are not load-bearing for billing.
//   * `Timestamp` is UTC. The blob layout buckets records by
//     `Timestamp.Date` (UTC midnight).
//
// Portability audit:
//   1. Identity by value: `ScopeId: string`, `RecordId: Guid`. No
//      live handles. ✓
//   2. Async at every interface boundary: see `IUsageLog`. ✓
//   3. Retry/backoff as data: see `BatchFlushPolicy`. ✓
//   4. Stateless handlers: the flusher reads from a bounded
//      `Channel`; per-(scope, day) write serialisation uses
//      `SemaphoreSlim`. No in-memory cursor that survives across
//      restarts. ✓
//   5. No cross-shard ordering: records are timestamped; ordering
//      is meaningful only within a single (scope, day) rollup. ✓
//   6. Precision lower-bound: `Timestamp = DateTime UTC`
//      (millisecond), `Quantity = decimal`. Documented above. ✓

/// Discriminates between user-supplied (BYOK) and deployment-default
/// (PlatformManaged) AI provider calls. BYOK is the default;
/// Managed-AI is a marked-up line item. The metering middleware tags
/// every AI call with one of these values so finance can split the
/// two billing surfaces.
///
/// `None` on `UsageRecord.Origin` for non-AI resources (storage
/// bytes, ingestion rows, request counts).
type ProviderOrigin =
    /// User supplied their own API key (resolved through the canonical
    /// platform `IProviderProfile` store). Token cost is the
    /// deployment's pass-through; ToolUp does not bill the tokens.
    | TenantBYOK
    /// Deployment-default API key. ToolUp bills the tokens at the
    /// Managed-AI markup rate.
    | PlatformManaged

/// One row in the usage ledger. Fire-and-forget from emission sites;
/// the flusher batches and writes daily rollup files. Cross-team
/// isolation is structural — `ScopeId` is part of the blob key, never
/// enumerated outside the requested scope.
type UsageRecord = {
    /// Deterministic dedup key. The blob-backed flusher merges
    /// incoming records into the existing rollup de-duped by this
    /// id, so a crash-mid-write retry does not double-count.
    RecordId: Guid
    /// Team / user / IP scope identifier (resolved server-side from
    /// `AccessContext`). Carries team isolation; the blob layout
    /// keys on this field.
    ScopeId: string
    /// Discriminates the metered resource. SDK constants in
    /// `ResourceKinds`; companion packages can add new kinds.
    ResourceKind: string
    /// Quantity recorded. `decimal`, not `float`, because billing.
    Quantity: decimal
    /// Display unit (`tokens`, `bytes`, `rows`, `requests`). Carried
    /// alongside the kind so admin UI / CSV export do not have to
    /// derive it from the kind string.
    Unit: string
    /// AI-provider origin discriminator. `None` for non-AI
    /// resources. See `ProviderOrigin`.
    Origin: ProviderOrigin option
    /// Free-form tags. Keys/values opaque to the SDK. Reserved
    /// keys: `model`, `provider`, `cached_input_tokens`, `userId`.
    Metadata: Map<string, string>
    /// UTC timestamp of the metered event. Bucketed by `.Date` for
    /// the rollup-file layout.
    Timestamp: DateTime
}

/// How `IUsageLog.Aggregate` rolls a scope's records up. The
/// returned map keys depend on the grouping:
///   * `ByDay`           — key = `yyyy-MM-dd` UTC date
///   * `ByMonth`         — key = `yyyy-MM` UTC year-month
///   * `ByResourceKind`  — key = `ResourceKind` literal
///   * `ByUser`          — key = `Metadata["userId"]` (records
///                         missing the key bucket under `""`)
type UsageGrouping =
    | ByDay
    | ByMonth
    | ByResourceKind
    | ByUser

/// Returned by `ITeamQuotaPolicy` decorator failures and surfaced to
/// callers as a typed error instead of an exception. `Kind` matches a
/// `ResourceKinds.*` constant or the literal `"concurrency"`.
/// `UsageThresholdCrossed` notifications carry the same shape.
type QuotaBreached = {
    /// `ResourceKinds.*` constant or `"concurrency"`.
    Kind: string
    /// Maximum allowed value (per the configured policy).
    Limit: decimal
    /// Value the caller requested that triggered the breach.
    Requested: decimal
    /// Scope whose limit was breached (echoed for log context).
    ScopeId: string
}

/// Canonical resource kinds shipped by the SDK. Companion packages
/// MAY publish additional kinds — `UsageRecord.ResourceKind` is
/// `string`, not a closed DU, so the schema stays open. New SDK
/// additions belong here so the admin UI's chart picker stays in
/// sync.
module ResourceKinds =
    /// AI provider input (prompt) tokens.
    [<Literal>]
    let aiTokensInput = "ai.tokens.input"

    /// AI provider output (completion) tokens.
    [<Literal>]
    let aiTokensOutput = "ai.tokens.output"

    /// File / blob storage bytes. `SessionFileStore` emits positive
    /// `Quantity` on add and negative `Quantity` on delete so the
    /// running sum is the live storage footprint.
    [<Literal>]
    let storageBytes = "storage.bytes"

    /// Rows ingested through `IDataIngestor`.
    [<Literal>]
    let ingestionRows = "ingestion.rows"

    /// Counted requests (one per metered HTTP call). Optional —
    /// emission site is the rate-limiting middleware, gated by
    /// usage metering being enabled.
    [<Literal>]
    let apiRequests = "api.requests"

/// Behaviour data for the `UsageBatchFlusher` `BackgroundService`.
/// Expressed as a record (data, not callbacks) so it satisfies
/// portability rule 3 — retry / batching as data, not framework
/// callbacks. The SDK ships sensible defaults; deployments override
/// via `ServerConfig`.
type BatchFlushPolicy = {
    /// Maximum buffered records before a flush is forced (regardless
    /// of the timer). Default 256.
    FlushAtCount: int
    /// Maximum interval between flushes when the count threshold is
    /// not hit. Default 30 seconds.
    FlushInterval: TimeSpan
    /// Bounded channel capacity. When full, producers wait up to
    /// `ProducerWaitTimeout`; only on timeout is a record dropped
    /// (and a `_platform.usage` event emitted). Billing data
    /// dropped silently is worse than a producer stall — `Wait` is
    /// the correct semantic, NOT `DropWrite`. Default 8192.
    ChannelCapacity: int
    /// Maximum time a producing call is allowed to wait when the
    /// channel is at capacity. Producers exceeding this fall through
    /// with a logged drop. Default 250ms.
    ProducerWaitTimeout: TimeSpan
}

module BatchFlushPolicy =
    /// SDK defaults for `BatchFlushPolicy`. Tuned for an in-process
    /// deployment with mid-thousands of usage events per minute.
    let defaults: BatchFlushPolicy = {
        FlushAtCount = 256
        FlushInterval = TimeSpan.FromSeconds 30.0
        ChannelCapacity = 8192
        ProducerWaitTimeout = TimeSpan.FromMilliseconds 250.0
    }