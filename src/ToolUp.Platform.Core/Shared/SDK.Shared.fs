// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.Narrative

// ─── Inter-module communication ────────────────────────────────────

/// Persisted event envelope for inter-module communication via the datastore.
/// Events are team-scoped via the `ScopeId` field — every read path must
/// filter by scope to preserve team isolation.
type ModuleEvent = {
    Id: Guid
    OccurredAt: DateTime
    ScopeId: string
    SourceModule: string
    EventType: string
    Payload: string // JSON-serialised
}

/// Interface for persisting and querying module events.
///
/// **Ordering contract.** Each method documents its own ordering guarantee; no
/// cross-method ordering is promised. Distributed implementations (journal-based
/// stores, DynamoDB, etc.) may not preserve wall-clock order across partitions
/// or across filter dimensions. Callers that need strict ordering must sort by
/// `OccurredAt` after reading, and must not rely on "read all of type X then type Y
/// and assume total order."
///
/// **Scoping.** All read methods take a `scopeId` parameter; implementations
/// MUST return only events whose `ModuleEvent.ScopeId` matches. Events from
/// other scopes are never returned under any circumstance — a bug here is a
/// team-isolation breach, not a feature gap. The reserved `_platform` scope
/// is for SDK-level events that span all tenants (audit events, health events).
type IEventStore =
    /// Persist an event. The event's `ScopeId` determines which scope the
    /// event belongs to; subsequent reads filtered to that scope will return
    /// it. Write ordering across concurrent callers is serialised by the
    /// implementation (per-store FIFO); `Id` and `OccurredAt` are the
    /// authoritative timestamps for downstream consumers.
    abstract Write: ModuleEvent -> Async<unit>
    /// Read all events for a given scope, reverse-chronological by
    /// `OccurredAt`. No guarantee across partitions in distributed
    /// implementations.
    abstract ReadAll: scopeId: string -> Async<ModuleEvent list>
    /// Read events for a given scope filtered by event type. No ordering
    /// guarantee — callers must sort by `OccurredAt` if order matters.
    abstract ReadByType: scopeId: string * eventType: string -> Async<ModuleEvent list>
    /// Read events for a given scope from a specific source module. No
    /// ordering guarantee — callers must sort by `OccurredAt` if order matters.
    abstract ReadBySource: scopeId: string * sourceModule: string -> Async<ModuleEvent list>
    /// Enumerate every scope id that has at least one persisted event in
    /// this store. Used by background sweeps that fan out per-scope reads
    /// (`AuditReplicator`'s startup recovery + catch-up sweep, GDPR
    /// erasure, …). Implementations may be expensive — distributed
    /// stores list all scope-prefixed records; callers must not invoke
    /// per-request. Order is not guaranteed. Mirrors the existing
    /// `IJobStore.ListScopesWithJobs` and `IVectorStore.ListScopes`
    /// patterns. Portability rule 1 — identity by string.
    abstract ListScopes: unit -> Async<string list>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or redact)
    /// every event in `scopeId` whose `Payload` names `subjectUserId`,
    /// interpreting `policy` per the event-store's own semantics:
    ///
    ///  - `HardDelete` — remove the matching events entirely. Breaks
    ///    event-log integrity for the subject; only valid where no
    ///    compliance-driven retention applies.
    ///  - `Tombstone` — keep the envelope (`Id` / `OccurredAt` /
    ///    `ScopeId` / `SourceModule` / `EventType`) but replace the
    ///    whole `Payload` with `Erasure.TombstoneMarker`. The store
    ///    has no schema knowledge of module payloads, so it redacts
    ///    the entire payload rather than risk leaving PII in an
    ///    unrecognised field. Preserves the sequence chain + makes
    ///    the erasure discoverable.
    ///  - `RetainPerCompliance` — refuse. The event log is the
    ///    "what happened" record; for regimes where event/audit
    ///    retention legally overrides Article 17, the handler returns
    ///    `HandlerRefused` and the run records the refusal.
    ///
    /// **Subject match.** `ModuleEvent.Payload` is opaque JSON; the
    /// store has no structured subject column, so "names the subject"
    /// is a substring match of `subjectUserId` within the serialised
    /// payload. Declared precision: substring-within-payload,
    /// best-effort. A blank `subjectUserId` is a zero-count no-op
    /// (it would otherwise match every payload).
    ///
    /// **Scope isolation (GP 4).** Only events whose `ScopeId`
    /// equals `scopeId` are ever touched — Team A's erasure never
    /// reaches Team B even when the same `subjectUserId` substring
    /// appears in both.
    ///
    /// `dryRun = true` computes the affected count without mutating
    /// (the two-phase-commit preview path). `dryRun = false` applies
    /// the policy. Portability audit (GP 12): identity by value,
    /// async at boundary, failure as `ErasureError` data, stateless
    /// between calls, single-scope (no cross-shard ordering),
    /// precision declared above.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>

/// Helpers for creating and emitting events
module Events =
    /// Create a new ModuleEvent scoped to the given scopeId. Callers should
    /// obtain the scopeId from the request's resolved `StorageScope` or use
    /// the reserved `_platform` value for SDK-level events.
    let create (scopeId: string) (sourceModule: string) (eventType: string) (payload: string) = {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow
        ScopeId = scopeId
        SourceModule = sourceModule
        EventType = eventType
        Payload = payload
    }

/// Retention policy for persistent event stores. Both dimensions are
/// optional — set neither to keep events forever (useful when the
/// event log is the audit trail and the deployment's compliance regime
/// requires full history).
type EventRetentionPolicy = {
    /// Maximum age of an event. Events older than this are eligible
    /// for pruning. `None` = no age-based pruning.
    MaxAge: TimeSpan option
    /// Maximum number of events retained per scope. When the count is
    /// exceeded, the oldest events (by `OccurredAt`) are pruned first.
    /// `None` = no count-based pruning.
    MaxCountPerScope: int option
}

module EventRetentionPolicy =
    /// No pruning — events are retained indefinitely.
    let unlimited = {
        MaxAge = None
        MaxCountPerScope = None
    }

    /// Retain events for the given age; no per-scope count cap.
    let byAge (maxAge: TimeSpan) = {
        MaxAge = Some maxAge
        MaxCountPerScope = None
    }

    /// Retain the most recent `n` events per scope; no age limit.
    let byCount (maxCount: int) = {
        MaxAge = None
        MaxCountPerScope = Some maxCount
    }

    /// Common default: 90 days, no count cap. Suitable for audit trails
    /// where regulators typically require 90-day retention.
    let ninetyDays = byAge (TimeSpan.FromDays 90.0)

/// Replay helpers for reconstructing state from an event history. The
/// store always returns reverse-chronological; `EventReplay` re-orders
/// to chronological before folding, so callers write a natural
/// left-fold over past events.
module EventReplay =
    /// Fold every event for a scope (chronological, ascending by
    /// `OccurredAt`) through the given folder. Useful for rebuilding a
    /// projection on startup, debugging a timeline, or replaying onto
    /// a test double.
    let foldScope
        (store: IEventStore)
        (scopeId: string)
        (initialState: 'State)
        (folder: 'State -> ModuleEvent -> 'State)
        : Async<'State> =
        async {
            let! events = store.ReadAll scopeId
            // ReadAll contract: reverse-chronological. Replay wants chronological.
            let ordered = events |> List.sortBy _.OccurredAt
            return ordered |> List.fold folder initialState
        }

    /// Same as `foldScope` but filtered to a single event type. Offloads
    /// the filter to the store so distributed implementations can push
    /// it down when possible.
    let foldScopeOfType
        (store: IEventStore)
        (scopeId: string)
        (eventType: string)
        (initialState: 'State)
        (folder: 'State -> ModuleEvent -> 'State)
        : Async<'State> =
        async {
            let! events = store.ReadByType(scopeId, eventType)
            let ordered = events |> List.sortBy _.OccurredAt
            return ordered |> List.fold folder initialState
        }

/// Selects which `IEventStore` implementation `compose` registers.
/// Default: `InMemoryOnly` (preserves existing behaviour — events are
/// lost on restart). Apps that want persistent, audit-grade history
/// opt in to `PersistentBlobBacked` and pass a retention policy.
type EventStoreMode =
    /// In-process, thread-safe list. Lost on restart. Suitable for
    /// development and for deployments that treat events as ephemeral
    /// signals rather than the system of record.
    | InMemoryOnly
    /// Blob-backed append-only store. One JSON blob per event under
    /// `_platform/events/{scopeId}/`. Uses whatever `IBlobStorage`
    /// implementation is registered (local disk, Azure, S3, GCS).
    /// The retention policy governs when old events are pruned; prune
    /// is NOT automatic — apps call `PersistentEventStore.pruneScope`
    /// from a scheduled job or startup routine.
    | PersistentBlobBacked of EventRetentionPolicy

/// Selects which `IResultStore` implementation `compose` registers.
/// Default: `NoResultStore` — analytical-result persistence is
/// opt-in. Apps that don't enable a mode get nothing
/// in DI; modules calling `ctx.RequestServices.GetService<IResultStore>()`
/// receive `null` and fall back to local computation only. Apps that
/// want results-without-history use `InMemoryResultStore` for
/// dev/test; production uses `PersistentResultStore` which routes
/// through `IDataObjectStore` with `StrictlyVersioned` policy and
/// emits `AnalysisCompleted` events.
type ResultStoreMode =
    /// No `IResultStore` registered. Modules treat result persistence
    /// as unavailable. Default — keeps the SDK lean for deployments
    /// that don't care about analytical-output history.
    | NoResultStore
    /// In-process store. Lost on restart. Suitable for tests and
    /// short-lived dev environments that want the API surface but
    /// not the durability cost.
    | InMemoryResultStore
    /// Blob-backed via `IDataObjectStore`. Results are stored
    /// `StrictlyVersioned` (no delete, full history). Each successful
    /// `SaveResult` emits an `AnalysisCompleted` event to
    /// `IEventStore` and (when `Lineage = EnabledLineageStore`) one
    /// `LineageLink` per declared input.
    | PersistentResultStore

/// Selects whether `ILineageStore` is registered. Lineage
/// is a query layer over `IEventStore` filtered to `LineageLink`
/// events — there is no separate persistence to enable, so the toggle
/// is binary. Default: `NoLineageStore`. Enabling this mode also
/// activates auto-emit in `PersistentResultStore`: every
/// `SaveResult` with a non-empty `inputs` list writes a
/// `LineageLink` per input.
type LineageStoreMode =
    /// `ILineageStore` is not registered; `PersistentResultStore`
    /// silently ignores the `inputs` parameter on `SaveResult`.
    /// Default.
    | NoLineageStore
    /// `ILineageStore` is registered as a query layer over
    /// `IEventStore`. Auto-emit on `SaveResult` activates.
    | EnabledLineageStore

/// Selects whether `compose` registers the share-token substrate.
/// Default: `NoShareTokenStore` — no `IShareTokenStore`
/// in DI, no `_platform/share-tokens/` blob layout, no signing-key
/// secret resolved. Apps that issue signed share-links (publishable
/// forms, magic-login links, public dashboards) opt in to
/// `EnabledShareTokenStore`.
type ShareTokenStoreMode =
    /// No share-token substrate registered. Default — keeps the SDK
    /// lean for deployments that don't issue tokens.
    | NoShareTokenStore
    /// Register `BlobShareTokenStore` against the resolved
    /// `IBlobStorage` and `ISecretStore`. Audit emission flows
    /// through the configured `IAuditLog` — when `AuditLog =
    /// NoAuditLog` the impl simply skips audit emission with no
    /// runtime cost.
    | EnabledShareTokenStore

/// Selects which `IJobScheduler` implementation `compose` registers.
/// Default: `NoJobScheduler` — background jobs are opt-in. Apps that
/// don't enable a mode get nothing — no `IJobScheduler` in DI, no
/// `_platform/jobs/` blob layout, no scheduler tick. Apps that opt in
/// pick the in-process default; future distributed companions (Akka,
/// Orleans, Hangfire) add new cases here without changing existing
/// consumers.
type JobSchedulerMode =
    /// No `IJobScheduler` registered. Default — keeps the SDK lean
    /// for deployments that don't run background work.
    | NoJobScheduler
    /// In-process scheduler with minute-precision cron evaluation
    /// and `IBlobStorage`-backed `IJobStore`. Suitable for single-
    /// instance deployments. Multi-silo deployments need a
    /// distributed companion to avoid double-dispatch.
    | InProcessJobScheduler

/// Selects whether `compose` registers the data-ingestion substrate.
/// Default: `NoDataIngestion` — no `IDataIngestor`, no
/// `IDataSourceConfigStore`, no `_platform/data-sources/` blob
/// layout, no `IDataIngestionApi` route. Apps that opt in pick the
/// `EnabledDataIngestion` mode; the connectors themselves are
/// supplied via DI (companion packages register their `IDataSource`
/// implementations against this surface).
type DataIngestionMode =
    /// No data-ingestion infrastructure registered. Default — keeps
    /// the SDK lean for deployments that don't pull from external
    /// data sources.
    | NoDataIngestion
    /// `IDataIngestor` + `IDataSourceConfigStore` registered.
    /// Connectors register via DI as `IDataSource` (one per `Kind`).
    /// The data-ingestion API (`IDataIngestionApi`) is auto-injected
    /// as a Fable.Remoting endpoint when the scheduler is also
    /// enabled — `TriggerRefresh` schedules a `Manual` job through
    /// `IJobScheduler`, so apps that want triggered ingestion need
    /// `JobScheduler = InProcessJobScheduler` too.
    | EnabledDataIngestion

/// Selects whether `compose` registers the column-mapping substrate
/// that backs the mapping-aware Data Manager (`DataManager =
/// MappingDataManager` on the client). Default: `NoColumnMapping` — no
/// `IColumnMappingStore`, no `IColumnMappingApi` route. Pairs with the
/// client `DataManagerMode`, mirroring the `DataIngestion` (server) /
/// `DataManager` (client) split: the client mode renders the wizard,
/// this server flag persists the reusable maps and mounts the API.
type ColumnMappingMode =
    /// No column-mapping infrastructure. Default — the built-in Data
    /// Manager (`DefaultDataManager`) needs no per-CSV mapping store.
    | NoColumnMapping
    /// `IColumnMappingStore` (default `IDataObjectStore`-backed) in DI
    /// and `IColumnMappingApi` auto-mounted. Enable alongside
    /// `ClientConfig.DataManager = MappingDataManager`.
    | EnabledColumnMapping

/// Phase 218 — policy for the mapping-aware Data Manager's dry-run
/// validation step (the per-row/per-cell error preview shown before a
/// mapped CSV is committed). Read only when `ColumnMapping =
/// EnabledColumnMapping`; the report is always shown, the policy only
/// decides whether failing rows BLOCK the commit or merely WARN.
type MappingDryRunPolicy =
    /// Default — the dry-run report is advisory: failing rows are
    /// surfaced but the user may still commit (GP 11: prior behaviour,
    /// where commit was unconditional, is preserved).
    | WarnOnValidationFailure
    /// The dry-run report blocks commit when any row would fail
    /// validation — the user must fix the mapping or the source first.
    | BlockOnValidationFailure

/// Phase 10h — generic OAuth 2.0 refresh-token lifecycle substrate.
/// Selects whether `compose` registers `IOAuthTokenRefresher` +
/// `OAuthRefreshJobHandler` (`_platform.oauth.refresh`). Default:
/// `NoOAuthRefresher` — connectors fall back to per-call
/// `IOAuthCredentialFlow.RefreshAccessToken` (the Phase 10e path),
/// no background refresh, no admin-UI token-status column. Apps that
/// want background refresh enable with `EnabledOAuthRefresher`;
/// connectors register their `OAuthRefreshDescriptor` at `Connect`
/// (and unregister on `Disconnect`).
///
/// Pair with `JobScheduler = InProcessJobScheduler` (or any future
/// distributed scheduler companion). The
/// `ServiceStatusBoardDepsValidator`-shaped `IConfigValidator`
/// emits a Warning at startup when `OAuthRefresher = EnabledOAuthRefresher`
/// but `JobScheduler = NoJobScheduler` — the refresher has no way to
/// schedule dispatches.
type OAuthRefresherMode =
    /// No `IOAuthTokenRefresher` registered. Default — connectors
    /// using OAuth Authorization Code (Phase 10e) refresh
    /// synchronously per API call via
    /// `IOAuthCredentialFlow.RefreshAccessToken`.
    | NoOAuthRefresher
    /// `InProcessOAuthTokenRefresher` registered;
    /// `OAuthRefreshJobHandler` registered under
    /// `_platform.oauth.refresh`; admin-UI token-status column on
    /// the data-ingestion admin renders refresh outcomes + next
    /// scheduled refresh. Requires `JobScheduler =
    /// InProcessJobScheduler` (or distributed companion) to schedule
    /// dispatches.
    | EnabledOAuthRefresher

/// Selects whether `compose` registers the entity-store substrate.
/// Default: `NoEntityStore` — no `IEntityStore` in DI, no
/// `EntityRegistry`, no per-entity-type index space allocated. Apps
/// that want typed entity persistence with declared indexes opt in to
/// `EnabledEntityStore`; entity types and their indexes are registered
/// via `ServerApp.withEntity<'T>` per type.
type EntityStoreMode =
    /// No entity-store infrastructure registered. Default — keeps the
    /// SDK lean for deployments whose only persistence needs are
    /// blob/event/result-shaped.
    | NoEntityStore
    /// `IEntityStore` + `EntityRegistry` registered. Default impl
    /// (`BlobEntityStore`) wraps `IDataObjectStore` for versioning
    /// and `BlobIndex` for declared indexes. Entity types register
    /// via `ServerApp.withEntity<'T> registration`.
    | EnabledEntityStore

/// Phase 68 — selects the graph-data store backend (`IGraphStore`), the
/// graph-shaped peer of `IEntityStore`. Default: `InMemoryGraphStore` —
/// the zero-dependency in-memory default (GP 2, no engine-by-default) is
/// registered *lazily*, so a deployment that never calls a graph API pays
/// nothing (GP 13). Engine companions (Kùzu / Neo4j / AGE) register their
/// own `IGraphStore` singleton in DI and select `CustomGraphStore`.
type GraphStoreMode =
    /// The zero-dependency in-memory `IGraphStore` (from
    /// `ToolUp.Graph.InMemory`), registered lazily. The default — a
    /// consumer gets a working graph store with no external dependency.
    /// Interprets the documented openCypher subset (the portability floor).
    | InMemoryGraphStore
    /// A companion-provided `IGraphStore` (e.g. an engine-backed
    /// `ToolUp.Graph.Kuzu`) is registered in DI by the deployment;
    /// `compose` registers no default and leaves the consumer's singleton
    /// in place.
    | CustomGraphStore

/// Phase 161 — selects the time-series storage backend (`ITimeSeriesStore`)
/// for high-frequency numeric/analytical series. Default:
/// `NoTimeSeriesStore` — no `ITimeSeriesStore` registered, zero cost.
type TimeSeriesStoreMode =
    /// No time-series substrate registered (default). A deployment with no
    /// high-frequency series pays nothing (GP 13).
    | NoTimeSeriesStore
    /// Register the dev/test in-memory `ITimeSeriesStore` — unbounded
    /// in-memory retention, no durability. For local dev / single-instance
    /// demos only.
    | InMemoryTimeSeries
    /// A companion-provided `ITimeSeriesStore` (e.g.
    /// `ToolUp.TimeSeriesStores.Timescale`) is registered in DI by the
    /// deployment; `compose` registers no default and leaves the consumer's
    /// singleton in place.
    | CustomTimeSeriesStore

/// Phase 163 — selects the end-user product-telemetry sink (`ITelemetrySink`).
/// Default: `NoTelemetrySink` — the `NoOpTelemetrySink` (a true no-op) is
/// registered, so `Track` emission sites are free at runtime (GP 13).
type TelemetrySinkMode =
    /// Register the `NoOpTelemetrySink` (default) — a true no-op; analytics
    /// events go nowhere and cost nothing.
    | NoTelemetrySink
    /// A companion-provided `ITelemetrySink` (e.g.
    /// `ToolUp.TelemetrySinks.Ga4`) is registered in DI by the deployment;
    /// `compose` registers no default and leaves the consumer's sink in place.
    | CustomTelemetrySink

/// Selects whether `compose` registers the usage-metering substrate.
/// Default: `NoUsageMetering` — `IUsageLog` resolves to
/// `NoOpUsageLog` so emission sites (`SessionFileStore`, the AI
/// metering middleware in `ToolUp.AI`, future ingestion / API-request
/// emitters) stay free at runtime. `ITeamQuotaPolicy` similarly
/// resolves to the no-op default. Apps that need per-team billing /
/// fair-use quotas opt in to `EnabledUsageMetering`.
///
/// Mirrors `AuditLogMode` (binary, not three-way) — the in-memory
/// middle case is redundant when blob-backed default + an in-memory
/// `IBlobStorage` already gives tests the surface they need without
/// the durability cost.
type UsageMeteringMode =
    /// `IUsageLog` resolves to `NoOpUsageLog`; `ITeamQuotaPolicy`
    /// resolves to `NoOpTeamQuotaPolicy`. The `UsageBatchFlusher`
    /// `BackgroundService` is not registered, the `IUsageQueryApi`
    /// route is not mounted, and emission sites are free. Default.
    | NoUsageMetering
    /// `BlobUsageLog` + `UsageBatchFlusher` registered;
    /// `BlobBackedTeamQuotaPolicy` registered; the `IUsageQueryApi`
    /// route is auto-injected. Storage layout
    /// `_platform/usage/{scopeId}/{yyyy-MM-dd}.json`. Quota fields
    /// read from the reserved `_platform.usage` config schema —
    /// missing config = unrestricted.
    | EnabledUsageMetering

/// Selects whether `compose` registers the metrics substrate.
/// Default: `Disabled` — `IMetricsSink` resolves to
/// `NoOpMetricsSink` so emission sites in `compose`-controlled code
/// (request middleware, job scheduler, SSE connection manager) stay
/// free at runtime. The `/metrics` endpoint is not mounted, and no
/// `MetricsMiddleware` is added to the pipeline. Apps that want
/// Prometheus-format scraping opt in to `Enabled`.
///
/// Information-disclosure caution: a `/metrics` endpoint exposes
/// route templates, tag values, and traffic patterns. The `Disabled`
/// default avoids surprising deployments that don't want this surface
/// open. When `Enabled`, the endpoint is mounted at the literal
/// Prometheus convention `/metrics` and is exempt from
/// `AuthEnforcementMiddleware` (so vanilla scrapers without bearer
/// tokens can read it); deployments needing authn gate at the
/// network layer (LB allowlist, monitoring-network CIDR).
///
/// Mirrors `EntityStoreMode` / `JobSchedulerMode` (binary, opt-in).
type MetricsEndpointMode =
    /// `IMetricsSink` resolves to `NoOpMetricsSink`. No
    /// `MetricsMiddleware` is added; the `/metrics` route is not
    /// mounted; SDK standard metrics (`toolup.requests.total`, etc.)
    /// are not registered. Default.
    | NoMetricsEndpoint
    /// `PrometheusMetricsSink` registered; `MetricsMiddleware` injected
    /// into the pipeline before `RequestTimingMiddleware`; the
    /// `/metrics` endpoint is mounted in OpenMetrics text format; SDK
    /// standard metrics are pre-registered. Companions (e.g. the
    /// OpenTelemetry exporter at `src/Metrics/OpenTelemetry/`)
    /// register additional sinks via DI; multiple sinks compose via
    /// fan-out so a single emission hits every registered sink.
    | EnabledMetricsEndpoint

/// Selects whether `compose` registers the inter-platform peer
/// substrate (Phase 18) — opt-in, cross-deployment typed-RPC where one
/// ToolUp deployment can call a typed contract exposed by another
/// ToolUp deployment (peer), with identity propagation, versioning,
/// and audit. Default: `NoPeerSubstrate` — no `/peer/v1/{contractId}`
/// route is mounted, no `IPlatformPeer` / `IPeerClient` /
/// `IPeerAuthProvider` resolved in DI, no peer audit emission wired.
/// Zero cost when not enabled (GP 13). Enable with
/// `EnabledPeerSubstrate` to activate the substrate; contracts are
/// hosted via `PeerCompose.compose` and the JSON-RPC 2.0 host.
///
/// Distinct from `PeerRoutePrefixes` (the simpler shared-bearer
/// peer-call middleware) — the two coexist on different route
/// prefixes; this richer substrate adds JWT / delegated assertions,
/// a version handshake, and job-substrate fusion for long-running
/// peer calls.
///
/// Mirrors `EntityStoreMode` / `JobSchedulerMode` (binary, opt-in).
type PeerSubstrateMode =
    /// No peer-substrate infrastructure registered. The
    /// `/peer/v1/{contractId}` route is not mounted; `IPlatformPeer`,
    /// `IPeerClient`, `IPeerAuthProvider`, `IPeerHandshake`, and
    /// `IPeerRegistry` are absent from DI; no peer audit events are
    /// emitted. Default — keeps the SDK lean and the public attack
    /// surface closed for deployments that don't federate.
    | NoPeerSubstrate
    /// JSON-RPC 2.0 peer host mounted at `/peer/v1/{contractId}`;
    /// `JwtPeerAuthProvider` (fail-closed HS256), `BlobPeerRegistry`,
    /// and `InMemoryPeerHandshake` registered as defaults; peer-call
    /// and handshake lifecycle events emitted via `IEventStore` under
    /// `SourceModule = "_platform.peer"`. Contracts are authored as
    /// `IPlatformPeer`-shaped records and hosted through
    /// `PeerCompose.compose`.
    | EnabledPeerSubstrate

/// Phase 54 — selects whether `compose` registers the tenant-lifecycle
/// substrate: the four first-party `ITenantLifecycle` hooks
/// (encryption-key destroy, membership-cache invalidate, scheduled-job
/// cancel, subject-data erasure) + the `/api/_platform/tenants/*` admin
/// API that drives the aggregator. Default `NoTenantLifecycle` — no
/// hooks resolved, no route mounted, no `ITenantLifecycle` in DI; an
/// existing deployment that upgrades stays byte-for-byte identical until
/// it opts in (GP 11 + GP 13). Mirrors `PeerSubstrateMode` /
/// `EntityStoreMode` (binary, opt-in).
type TenantLifecycleMode =
    /// No tenant-lifecycle infrastructure registered. The
    /// `/api/_platform/tenants/*` route is not mounted; the four
    /// first-party hooks are absent from DI; no tenant-lifecycle audit
    /// events are emitted. Default — keeps the SDK lean and the
    /// destructive offboard surface closed for deployments that manage
    /// tenant teardown out-of-band.
    | NoTenantLifecycle
    /// `IPlatformTenantApi` mounted at `/api/_platform/tenants/*`
    /// (Owner / Platform-Admin gated); the four first-party
    /// `ITenantLifecycle` hooks registered (each self-`Skipped` when its
    /// substrate is inactive); companion hooks register additively via
    /// `services.AddSingleton<ITenantLifecycle>`. One `DeprovisionTenant`
    /// call runs every hook with audit + per-hook isolation under
    /// `SourceModule = "_platform.tenant"`.
    | EnabledTenantLifecycle

/// Phase 54i — confirmation gate in front of the destructive
/// tenant-offboard surface. Default `NoConfirmation` preserves Phase 54's
/// one-call behaviour byte-for-byte (GP 11); the two stronger modes
/// require an out-of-band confirmation token (minted via
/// `IPlatformTenantApi.RequestDeprovisionToken`, backed by
/// `IShareTokenStore`) before any token-less destructive offboard
/// (`DeprovisionTenant` / `…Sync` / `…Async` / `ExportThenDeprovision`)
/// is allowed — those token-less paths are refused under a confirmation
/// mode, and the operator must instead call `DeprovisionTenantConfirmed`
/// with a valid token. Opt-in per deployment (GP 13).
type OffboardConfirmationMode =
    /// No confirmation gate. The token-less destructive offboard
    /// surface behaves exactly as Phase 54 — a single Owner /
    /// Platform-Admin call shreds the tenant. Default.
    | NoConfirmation
    /// A short-lived confirmation token (minted by
    /// `RequestDeprovisionToken`) is required: a token-less offboard is
    /// refused with `Error "offboard confirmation required"`, and
    /// `DeprovisionTenantConfirmed` proceeds only with a valid, in-scope,
    /// unexpired token. Guards against fat-finger single-click teardown.
    | TokenConfirmation
    /// As `TokenConfirmation`, plus a two-person rule: the admin who
    /// *redeems* the token (executes `DeprovisionTenantConfirmed`) must be
    /// a *different* Platform-Admin than the one who requested it
    /// (`ShareTokenClaim.IssuedBy`). Same-admin redemption is refused.
    /// Guards against single-insider destruction.
    | TwoPersonRule

module OffboardConfirmationMode =
    /// `true` when the mode requires a confirmation token before a
    /// destructive offboard (everything except `NoConfirmation`). The
    /// handler refuses every token-less destructive path when this holds.
    let requiresToken =
        function
        | NoConfirmation -> false
        | TokenConfirmation
        | TwoPersonRule -> true

/// Sink-level cardinality cap configuration. The
/// `MetricDefinition.Tags` allowlist is the structural defence
/// against caller-side cardinality explosions; this knob is the
/// hard ceiling on how many distinct `(tag-set)` combinations a
/// single metric can hold before subsequent combinations are
/// routed to a single overflow series tagged `_overflow=true`.
type MetricsSinkConfig = {
    /// Maximum distinct `(tag-set)` series per metric. New
    /// combinations beyond this ceiling fold into a single overflow
    /// series. The first overflow logs `Warn` once (with metric
    /// name + offending tag); subsequent overflows are silent so
    /// the warning itself doesn't blow up. Default 1000 — defensive
    /// for well-behaved metrics, large enough that legitimate
    /// per-team / per-route partitioning is unlikely to hit it.
    MaxSeriesPerMetric: int
    /// Per-metric overrides. A known-high-cardinality metric (e.g. a
    /// per-team usage counter on a 5000-tenant deployment) can raise
    /// its ceiling without raising the global default. Keys are
    /// post-namespace metric names (`toolup.foo.total`,
    /// `toolup.mymod.bar.total`).
    PerMetricMaxSeries: Map<string, int>
}

module MetricsSinkConfig =
    let defaults: MetricsSinkConfig = {
        MaxSeriesPerMetric = 1000
        PerMetricMaxSeries = Map.empty
    }

/// Selects whether `compose` registers the outbound-webhook substrate.
/// Default: `NoWebhooks` — keeps the SDK lightweight by
/// skipping the `WebhookDispatcher` `BackgroundService`, the
/// `HookedEventStore` decorator wrapping `IEventStore`, the
/// `IWebhookRegistry` / `IWebhookDeliveryLog` / `IWebhookDispatcher`
/// DI services, and the `webhookHandler` route. Apps that want to
/// publish events to third-party systems opt in to `EnabledWebhooks`.
type WebhookMode =
    /// No webhook infrastructure registered. Default. The inner
    /// `IEventStore` is registered directly without the
    /// `HookedEventStore` decorator, so event writes carry zero
    /// dispatch overhead.
    | NoWebhooks
    /// Full webhook substrate. `HookedEventStore` decorates
    /// `IEventStore` so every event write fires registered
    /// subscriptions; `WebhookDispatcher` runs as a `BackgroundService`
    /// consuming a bounded `Channel<DispatchTask>` with HMAC-SHA256
    /// signed delivery and exponential-backoff retry; the admin API
    /// (`IWebhookApi`) is auto-injected.
    | EnabledWebhooks

/// Selects whether `compose` registers the audit-log emission path.
/// Default: `NoAuditLog` — `IAuditLog` is still registered
/// in DI (the interface is core), but the registration is a no-op
/// `NoOpAuditLog` that swallows every `Record` call. Emission sites
/// (`ScopeResolutionMiddleware`, `PlatformApi` handlers,
/// `fileManagementApi`) keep their unconditional `IAuditLog.Record`
/// calls — the no-op makes them free at runtime, and callsites stay
/// clean across mode changes. Apps that need a compliance trail opt
/// in to `EnabledAuditLog`.
type AuditLogMode =
    /// `IAuditLog` resolves to a no-op. Audit emission sites still
    /// run but write nothing. Default — Anonymous-mode demos and
    /// disposable-tenant deployments shouldn't pay for an audit
    /// trail they don't read.
    | NoAuditLog
    /// `IAuditLog` resolves to `EventStoreAuditLog` over `IEventStore`
    /// with reserved `SourceModule = "_platform.audit"`. Combine with
    /// `EventStore = PersistentBlobBacked _` for a durable trail.
    | EnabledAuditLog

/// Selects how `compose` registers `INotificationChannel` and the
/// `/api/notifications` SSE route. Default:
/// `NotificationsAuto` — `compose` flips to `InMemoryNotifications`
/// whenever a feature that publishes notifications is active
/// (`JobScheduler <> NoJobScheduler`, `Mode = MultiTeam`, or a
/// companion like `composeWithAI` / `composeWithRAG` is wrapping),
/// otherwise `NoNotifications`. Apps can override explicitly to pin
/// behaviour or to swap in a distributed backend.
type NotificationMode =
    /// `compose` decides based on the rest of the config and on
    /// companion wrappings (`composeWithAI` / `composeWithRAG` add
    /// themselves to `extensions.NotificationConsumers`). Default —
    /// the lightweight shape gets `NoNotifications`; deployments
    /// running jobs / MultiTeam / AI / RAG get `InMemoryNotifications`
    /// without any extra config.
    | NotificationsAuto
    /// No `INotificationChannel` is registered, no `/api/notifications`
    /// SSE route is mounted. Force this mode when you want to suppress
    /// the SSE endpoint even though a notification-publishing feature
    /// is active (e.g. a deployment that uses webhooks for fan-out
    /// instead).
    | NoNotifications
    /// Phase 58 — like `NoNotifications` but emits a bundle-constant
    /// signal to the client that the absence is deliberate. The
    /// client's `NotificationClient` reads
    /// `BundleConstants.NotificationsDisabledExplicitly` and skips
    /// EventSource instantiation entirely (no 404 retry loop, no
    /// console warnings). Use this mode in serverless / public-utility
    /// deployments where SSE is fundamentally inappropriate and the
    /// silent 404 loop would otherwise burn client CPU / bandwidth.
    | NoNotificationsExplicit
    /// `InMemoryNotificationChannel` is registered, `/api/notifications`
    /// SSE route is mounted. Single-process fan-out — multi-instance
    /// deployments need a distributed backend.
    | InMemoryNotifications
    /// `RedisNotificationChannel` is registered against the supplied
    /// connection string; `/api/notifications` SSE route is mounted.
    /// Per-scope channel naming gives structural scope isolation at
    /// transport level. Requires the `src/NotificationChannels/Redis/`
    /// companion package.
    | RedisNotifications of connectionString: string

/// Helpers for `NotificationMode`. `resolve` answers the auto-detection
/// question that `compose` runs at composition time: given the user's
/// declared mode plus the rest of the relevant config (job scheduler,
/// platform mode, declared companion consumers), which `NotificationMode`
/// is actually in effect? Extracted so unit tests can pin the resolution
/// rules directly without spinning up a `WebApplicationBuilder`.
module NotificationMode =
    /// Resolve `NotificationsAuto` against the rest of the config.
    /// Other modes pass through unchanged. Rules:
    ///   - `JobScheduler <> NoJobScheduler` → publishes dead-letter
    ///     notifications, so notifications need to flow.
    ///   - `hasMultiTeamSwitcher` → membership-change events feed the
    ///     client team-switch reset path; the caller passes
    ///     `DeploymentConfig.hasMultiTeamSwitcher config`.
    ///   - Any consumer in `notificationConsumers` (typically
    ///     `composeWithAI` / `composeWithRAG` declaring themselves) →
    ///     publishes through the channel.
    /// Otherwise the lightweight default flips to `NoNotifications`.
    let resolve
        (declared: NotificationMode)
        (jobScheduler: JobSchedulerMode)
        (hasMultiTeamSwitcher: bool)
        (notificationConsumers: string list)
        : NotificationMode =
        match declared with
        | NotificationsAuto ->
            let needs =
                jobScheduler <> NoJobScheduler
                || hasMultiTeamSwitcher
                || not (List.isEmpty notificationConsumers)

            if needs then InMemoryNotifications else NoNotifications
        | other -> other

// `PageConfig` and `ModuleDefinition` previously lived here. They moved
// to `Client/SDK.ClientTypes.fs` when the `Icon` field changed type from
// `string` (URL path) to `ReactElement` (typed SVGR-imported component).
// `ReactElement` is Fable-only, so the records can no longer live in
// shared types. Server-side code never constructed `ModuleDefinition`
// anyway (only doc-comment references), so the move was safe.

// ─── Module name filter ────────────────────────────────────────────

/// Case- and whitespace-insensitive substring match against module
/// names. Shared by `ServerConfig.ModuleFilter` and
/// `ClientConfig.ModuleFilter` so single-module dev runs behave the
/// same on both sides.
module ModuleFilter =
    let private normalise (s: string) = s.ToLowerInvariant().Replace(" ", "")

    /// `true` when the filter is absent/blank or when `name` contains
    /// the filter as a case-insensitive substring (whitespace ignored).
    let matches (filter: string option) (name: string) : bool =
        match filter with
        | None -> true
        | Some f when String.IsNullOrWhiteSpace f -> true
        | Some f -> (normalise name).Contains(normalise f)

    /// Filter `items` by `nameOf >> matches filter`. An absent or blank
    /// filter keeps every item — `matches` already encodes that, so this
    /// is a single `List.filter` with no duplicated guard.
    let apply (filter: string option) (nameOf: 'a -> string) (items: 'a list) : 'a list =
        items |> List.filter (nameOf >> matches filter)

// ─── Server configuration ──────────────────────────────────────────

/// What to do when `ServerConfig.PublicPath` does not exist on disk at
/// startup. Saturn's `use_static` defaulted to a silent skip; the post-
/// SAFE composition pipeline makes this an explicit choice so production
/// can fail loudly on a missing static-asset deployment instead of
/// returning 404s for every SPA route.
type StaticPathBehaviour =
    /// Log a warning and skip `UseStaticFiles`. Backward-compatible
    /// default — appropriate for `dotnet run` development where the
    /// Vite dev server is serving assets and `deploy/public` is empty.
    | Warn
    /// Throw at startup with a clear error message. Production
    /// deployments should choose this so a misconfigured container or
    /// missing build artefact crashes the process instead of silently
    /// breaking the SPA.
    | RequireExist
    /// Skip `UseStaticFiles` without logging. Use when the deployment
    /// has no static assets at all (pure API server, asset CDN front-
    /// end, etc.) and the warning is noise.
    | SkipSilent

/// Phase 66 Stream C.3 (design §3.10 + D21). A single fixed-window
/// rate-limit policy: `PermitLimit` requests are allowed per
/// `WindowSeconds`; the next `QueueLimit` requests queue rather than
/// being rejected outright. Beyond that, the limiter returns `429 Too
/// Many Requests` with a `Retry-After` header.
///
/// **Divergence from the design's `Window: TimeSpan`.** The shipped
/// shape keeps `WindowSeconds: int` (the pre-C.3 field name) rather
/// than the design §3.10 `Window: TimeSpan`. `TimeSpan` is awkward in
/// a Core shared type (Fable surface), and keeping seconds-as-int
/// leaves env parsing, the middleware's `TimeSpan.FromSeconds`
/// conversion, the validators, and the existing tests unchanged. The
/// middleware converts to `TimeSpan` at the .NET rate-limiter boundary.
type RateLimitPolicy = {
    PermitLimit: int
    WindowSeconds: int
    QueueLimit: int
}

module RateLimitPolicy =
    /// Partition key for a subject under the fixed-window limiter. The
    /// partition is implied by subject kind: anonymous traffic
    /// partitions on client IP (the session id is client-minted and
    /// unbounded, so it can't be the limiter key), authenticated users
    /// on user id, team members on team id (one shared budget across a
    /// team's members), claim-bearers on token id.
    ///
    /// `clientIp` is passed in rather than read off the subject because
    /// `AnonymousSession` carries only a client-minted session id, not
    /// the remote IP — the middleware supplies the resolved remote IP.
    /// Pure (no `HttpContext` dependency) so the D.7 test pack can
    /// exercise every branch directly.
    let partitionFor (clientIp: string) (subject: Subject) : string =
        match subject with
        | AnonymousSession _ -> sprintf "ip:%s" clientIp
        | AuthenticatedUser uid -> sprintf "user:%s" uid
        | TeamMember(_, tid) -> sprintf "team:%s" tid
        | ClaimBearer claim -> sprintf "token:%s" claim.TokenId

/// Phase 66 Stream C.3 (design §3.10 + D21) — per-subject-kind rate
/// limiting. `Default` applies to any subject kind without a `PerShape`
/// override; `PerShape` carries per-kind policies (e.g. a tight window
/// for `AnonymousKind`, a looser one for `UserKind`). A subject-kind
/// lookup that misses `PerShape` falls back to `Default`; when
/// `Default` is also `None`, that kind is unlimited.
///
/// **Default = no rate limiting.** `RateLimitConfig.none` (both
/// `Default = None` and `PerShape = Map.empty`) registers no limiter
/// at all — byte-for-byte the pre-C.3 `RateLimit = None` behaviour
/// (GP 11 backward-compatible default).
type RateLimitConfig = {
    /// Policy for any subject kind without a `PerShape` entry. `None` =
    /// no default limit (only `PerShape` kinds are limited).
    Default: RateLimitPolicy option
    /// Per-subject-kind policy overrides. A kind present here uses its
    /// policy; a kind absent falls back to `Default`.
    PerShape: Map<SubjectKind, RateLimitPolicy>
}

module RateLimitConfig =
    /// No rate limiting — no default, no per-shape overrides. The
    /// `ServerConfig` default; byte-for-byte the pre-C.3 pipeline (no
    /// `UseRateLimiter`, GP 11).
    let none: RateLimitConfig = { Default = None; PerShape = Map.empty }

    /// One policy for every subject kind. `PerShape` stays empty; every
    /// kind resolves to `policy` via the `Default` fallback.
    let uniform (policy: RateLimitPolicy) : RateLimitConfig = {
        Default = Some policy
        PerShape = Map.empty
    }

    /// Per-kind policies only, no default. Subject kinds absent from `m`
    /// are unlimited.
    let perShape (m: Map<SubjectKind, RateLimitPolicy>) : RateLimitConfig = { Default = None; PerShape = m }

    /// A default policy plus per-kind overrides. Kinds in `overrides`
    /// use their policy; all others fall back to `defaultPolicy`.
    let withOverrides
        (defaultPolicy: RateLimitPolicy)
        (overrides: Map<SubjectKind, RateLimitPolicy>)
        : RateLimitConfig =
        {
            Default = Some defaultPolicy
            PerShape = overrides
        }

    /// Resolve the policy for a subject kind: a `PerShape` entry wins,
    /// otherwise `Default`. `None` = that kind is unlimited.
    let policyFor (config: RateLimitConfig) (kind: SubjectKind) : RateLimitPolicy option =
        config.PerShape |> Map.tryFind kind |> Option.orElse config.Default

    /// `true` when this config would register a limiter at all — any
    /// default or any per-shape entry. `RateLimitConfig.none` returns
    /// `false` (the pre-C.3 "no `UseRateLimiter`" path).
    let isEnabled (config: RateLimitConfig) : bool =
        config.Default.IsSome || not (Map.isEmpty config.PerShape)

/// Typed CORS allowlist for the SDK's built-in CORS middleware.
/// Maps to `Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder`
/// — see `compose` for the registration. `Origins = ["*"]` is honoured
/// as `AllowAnyOrigin`; otherwise the listed origins become the explicit
/// allowlist. Same convention applies to `Methods` and `Headers`.
/// `AllowCredentials` cannot combine with wildcard origins (browsers
/// reject the combination); `compose` logs a warning and falls back to
/// non-credentialed mode if the deployment misconfigures both.
///
/// For CORS shapes that don't fit (per-route policies, dynamic origin
/// validation, vary-by-header), use `ServerApp.withPreMiddleware` and
/// register the policy by hand — `withPreMiddleware` runs before scope
/// resolution so OPTIONS preflight short-circuits cleanly.
type CorsConfig = {
    /// Origins to allow. `["*"]` = any origin (no credentials);
    /// explicit list otherwise. Default in `CorsConfig.permissive` is
    /// `["*"]`.
    Origins: string list
    /// HTTP methods to allow. `["*"]` = any. Default: `["GET"; "POST"; "OPTIONS"]`.
    Methods: string list
    /// Request headers to allow. `["*"]` = any. Default: `["*"]`.
    Headers: string list
    /// Whether the browser should send cookies / `Authorization`
    /// headers cross-origin. Cannot combine with wildcard origins.
    /// Default: `false`.
    AllowCredentials: bool
}

module CorsConfig =
    /// Wide-open CORS — any origin, any method, any header, no
    /// credentials. Suitable for public APIs and dev environments.
    let permissive: CorsConfig = {
        Origins = [ "*" ]
        Methods = [ "*" ]
        Headers = [ "*" ]
        AllowCredentials = false
    }

    /// Allowlist a specific list of origins with the typical
    /// credentialed-API shape (GET/POST/OPTIONS, any header,
    /// credentials enabled).
    let forOrigins (origins: string list) : CorsConfig = {
        Origins = origins
        Methods = [ "GET"; "POST"; "OPTIONS" ]
        Headers = [ "*" ]
        AllowCredentials = true
    }

/// How the SDK handles auth for SSE endpoints
/// (`/api/ai/events`, `/api/notifications`).
///
/// Browser `EventSource` cannot send custom request headers — only
/// cookies travel automatically — so a deployment using header-based
/// auth (`X-User-Id` / `Authorization: Bearer <jwt>`) needs special
/// handling for these endpoints.
///
/// `QueryParamFallback` (default): SSE endpoints are exempt from
/// `AuthEnforcementMiddleware`. The handlers fall back to the
/// `?userId=` query parameter for scope resolution. Convenient for
/// dev / Anonymous mode / `HeaderAuthProvider`-only deployments.
/// **Trade-off:** the userId is client-supplied with no
/// cryptographic proof; any browser can subscribe to any scopeId.
/// Acceptable for dev / single-user / trusted-network deployments.
///
/// `CookieRequired`: SSE endpoints go through the same
/// `AuthEnforcementMiddleware` as every other `/api/*` request.
/// Auth providers must read the JWT from a cookie (set via
/// `OidcAuthConfig.TokenLocation = Cookie name`). The client
/// `IAuthBridge` writes the JWT to `document.cookie` on sign-in so
/// EventSource handshakes carry it automatically. Production
/// recommendation for any deployment with multiple users on the
/// same network.
type SseAuthMode =
    | QueryParamFallback
    | CookieRequired

/// Phase 133 — whether the server mounts the BFF-style auth-cookie
/// reflection endpoint (`POST` / `DELETE /api/auth/session`). When the
/// client posts a freshly-acquired JWT, the server validates it through
/// the registered `IAuthProvider` and reflects it into an
/// `HttpOnly; Secure; SameSite=Strict; Path=/` cookie — so the bearer
/// credential never lives in JS-readable `localStorage` or a JS-readable
/// `document.cookie`. The browser then sends it automatically for SSE
/// (`EventSource`) and same-origin XHR, and an XSS cannot dump a usable
/// token from either store.
///
/// `NoAuthCookieIssuance` (default): the endpoint is not mounted; an
/// existing deployment is byte-for-byte unchanged (GP 11). The legacy
/// client `document.cookie` + `localStorage` writes remain the only
/// cookie path (dev EventSource handshake).
///
/// `EnabledAuthCookieIssuance`: the endpoint is mounted. Pairs with
/// `ClientConfig.AuthTokenStorage = ServerSetHttpOnlyCookie` on the
/// client and an `IAuthProvider` whose `TokenLocation` admits the
/// bearer header on the reflect call AND the cookie on every later
/// request — i.e. `BearerOrCookie "toolup-auth-token"`. Override via
/// `TOOLUP_AUTH_COOKIE_ISSUANCE=enabled|disabled`.
type AuthCookieIssuanceMode =
    | NoAuthCookieIssuance
    | EnabledAuthCookieIssuance

/// Controls whether `VectorScope.Platform` is exposed to RAG
/// retrieval. The toggle gates READ access; the WRITE side is gated
/// separately by `AccessContext.canModifyPlatformConfig` and
/// structurally restricted to `IPlatformKnowledgeApi`'s upload path.
/// Read and write are orthogonal — admins can pre-populate Platform
/// KB content when
/// the toggle is off, then flip to `EnabledPlatformKnowledgeBase` to
/// make it visible.
///
/// `NoPlatformKnowledgeBase` (default): `RetrievalPipeline.authorisedScopes`
/// filters `Platform` out of the returned scope list regardless of
/// caller. Existing Platform-scoped chunks stay on disk but are
/// invisible. `ListPlatformDocuments` still functions so admins can
/// manage the content.
///
/// `EnabledPlatformKnowledgeBase`: `Platform` scope is universally
/// readable for authenticated users; queries return Platform-scope
/// matches alongside Team-scope matches. RAGPromptBuilder annotates
/// citations with the scope origin so the model can qualify answers
/// with the authority level. Pairs with the convention used by other
/// mode toggles (`NoEntityStore` / `EnabledEntityStore`,
/// `NoLineageStore` / `EnabledLineageStore`, etc.).
type PlatformKnowledgeBaseMode =
    | NoPlatformKnowledgeBase
    | EnabledPlatformKnowledgeBase

/// Phase 9j — opt-in HTTP-surface security hardening. Distinct from
/// the static `ServerConfig.SecurityHeaders` map: this mode drives
/// the *companion-aware* `CspMiddleware` (a `Content-Security-Policy`
/// auto-generated from every registered `ICspContributor`, correct
/// by construction) plus the `CsrfMiddleware` cross-origin POST
/// guard.
///
///   * `NoSecurityHardening` (default, GP 13) — neither middleware
///     stamps anything and the `/api/csrf-token` route is not
///     mounted. A deployment on the default retains today's
///     behaviour exactly.
///   * `DefaultSecurityHardening` — aggregated CSP header on every
///     response; CSRF token required on state-changing `/api/*`
///     requests. `style-src` keeps `'unsafe-inline'` so Feliz /
///     Tailwind dynamic styles keep working.
///   * `StrictSecurityHardening` — as Default, but `script-src` /
///     `style-src` drop `'unsafe-inline'` (deployment must serve
///     nonce-driven tags) and `object-src 'none'` +
///     `upgrade-insecure-requests` are added.
///
/// The opt-in `SecurityHardening` and the existing static
/// `SecurityHeaders` map compose: a per-route handler (or the
/// `SecurityHeaders` map) that already wrote a `Content-Security-Policy`
/// header wins — `CspMiddleware` only sets the header when absent.
type SecurityHardeningMode =
    | NoSecurityHardening
    | DefaultSecurityHardening
    | StrictSecurityHardening

/// Phase 9q — startup-time config-drift detection. At `compose` end,
/// the SDK snapshots the resolved `ServerConfig` (secrets redacted)
/// plus a hash of the active companion-assembly set, persists it to
/// `_platform/_deploy/last-config.json`, and on the next startup
/// diffs the persisted snapshot against the new one. Differences are
/// emitted as a `Warn` log line plus a `ConfigDrift` audit event —
/// pure observation, no abort, no rollback.
///
///   * `NoConfigDriftDetection` (default, GP 13) — no snapshot
///     written, no comparison performed, no blob layout under
///     `_platform/_deploy/` touched. Stock deployments behave
///     exactly as before.
///   * `EnabledConfigDriftDetection` — snapshot + compare on every
///     `compose`. Requires `AuditLog = EnabledAuditLog` for the
///     audit-event side of the emission to land durably (the log
///     side fires regardless); a deployment running with
///     `NoAuditLog` still gets the `Warn` log but the audit row
///     resolves through the no-op sink and is lost. The detector
///     does not enforce the pairing — `EnabledConfigDriftDetection`
///     with `NoAuditLog` is a legitimate "log-only" stance.
type ConfigDriftDetectionMode =
    | NoConfigDriftDetection
    | EnabledConfigDriftDetection

/// Phase 9v — outbound rate-limiter selection. Distinct from
/// `ServerConfig.RateLimit` (Phase 9 inbound limiter, gates platform-
/// edge HTTP by team/user/IP); this mode controls the **outbound**
/// `IRateLimiter` (gates calls to third-party services partitioned
/// by `(scopeId, provider)` plus optional sub-key).
///
///   * `NoRateLimiter` (default, GP 13) — `IRateLimiter` resolves to
///     `NoOpRateLimiter` so emission sites resolve unconditionally
///     and the call elides at zero cost. Deployments without external
///     API connectors pay nothing.
///   * `EnabledRateLimiter` — `IRateLimiter` resolves to the SDK-
///     shipped `InProcessRateLimiter` (sliding-window default with a
///     soft 95% ceiling). Companion descriptors registered via
///     `ServerApp.withRateLimitDescriptor` are applied per-`(scopeId,
///     provider)` bucket. Multi-instance deployments should swap in
///     the Phase 9c half-2 Redis-backed companion to avoid Nx burst
///     past declared quotas; the contract is designed so the swap is
///     contract-free.
type RateLimiterMode =
    | NoRateLimiter
    | EnabledRateLimiter

/// Phase 9o — post-deploy smoke-test endpoint
/// (`GET /api/_internal/smoke`). Different from `/ready`
/// (per-component readiness probe polled on every load-balancer
/// interval): the smoke endpoint exercises every wired companion path
/// end-to-end (write/read a sentinel blob, publish + observe a
/// sentinel notification, schedule + dispatch a sentinel job, …)
/// against the reserved `_smoke` sentinel scope. Intended to run once
/// per deploy as a pre-traffic gate; token-gated via
/// `TOOLUP_SMOKE_TOKEN` so the surface is closed to anyone without
/// the deploy script's shared secret.
///
///   * `NoSmokeTest` (default, GP 13) — `/api/_internal/smoke` is not
///     mounted, no first-party smoke tests register. Stock
///     deployments pay zero runtime cost; the surface 404s.
///   * `EnabledSmokeTest` — the route mounts behind the token gate
///     and first-party smoke tests register against the SDK's wired
///     substrate (blob storage, notification channel, job scheduler,
///     event store, data-object store, audit log). Companion-
///     contributed smoke tests register alongside via
///     `services.AddSingleton<ISmokeTest>(...)` or
///     `ServerApp.withSmokeTest`.
type SmokeTestMode =
    | NoSmokeTest
    | EnabledSmokeTest

/// Phase 177 — opt-in deployment-readiness scorecard. The read
/// consolidates the four already-shipped operability signals
/// (`IConfigValidator` preflight, `ISmokeTest` results, the
/// `ConfigDrift` finding, the `IHealthCheck` aggregate) into one
/// Platform-Admin go/no-go verdict.
///
///   * `NoReadinessReport` (default, GP 11/13) — the
///     `IDeploymentReadinessApi` route is not mounted; the surface 404s
///     and the deployment is byte-for-byte unchanged.
///   * `EnabledReadinessReport` — mounts the Platform-Admin-gated read.
///     Each source sub-summary is independently `NotComposed` when its
///     substrate isn't wired, so enabling the report over a deployment
///     that composes a subset of the signals yields an honest partial
///     scorecard rather than a fabricated pass. Pure projection — no new
///     gate, no new control-plane behaviour.
type DeploymentReadinessMode =
    | NoReadinessReport
    | EnabledReadinessReport

/// Phase 53 — `IConversationStore` substrate opt-in. Promotes AI
/// assistant conversations from ephemeral `AIAssistantHandler` state
/// to a first-class persisted record so conversations are auditable,
/// recoverable, and re-runnable. Default `NoConversationStore` (GP 13):
/// `AIAssistantHandler` resolves `IConversationStore` as `null` and
/// runs its byte-for-byte pre-Phase-53 path — no audit emission, no
/// persistence cost.
///
/// `EnabledConversationStore { RetentionDays }` registers
/// `PersistentConversationStore` (built on `IDataObjectStore` —
/// inherits versioning + content-hash dedup + DSR erasure surface
/// for free), the `ConversationEraseHandler` (one of the
/// `IErasureHandler`s composed into the `ErasurePipeline` when
/// `DataSubjectRequests = Enabled`), and the five audit cases
/// under `_platform.conversations.*`. `RetentionDays` is recorded
/// in the deployment's config-drift snapshot but pruning is not
/// automatic — operators schedule pruning via a job under
/// `IJobScheduler`. (Today there's no first-party pruner; the
/// retention field is forward-looking + audit-visible.)
type ConversationStoreMode =
    | NoConversationStore
    | EnabledConversationStore of retentionDays: int

/// Phase 38 — compose-time content root for the public-rendering
/// companion. Absolute path to a directory holding `pages/`,
/// `news/`, `events/`, etc., plus an optional `redirects.csv`.
/// `ToolUp.PublicRendering.MarkdownContentLoader` walks this tree at
/// startup and (in dev) watches it for changes.
///
/// The type lives in `Platform.Core` so `ServerConfig.PublicRendering`
/// can reference it without introducing a Core→companion dependency.
/// The companion-side helpers `ToolUp.PublicRendering.Slug`,
/// `LayoutName`, `PublicPage`, etc. stay in the companion namespace.
type ContentRoot = ContentRoot of absolutePath: string

module ContentRoot =
    let value (ContentRoot p) = p

/// Phase 38 — `IPublicContentApi` substrate opt-in.
///
///   * `NoPublicRendering` (default, GP 13) — no `/sitemap.xml`
///     handler, no markdown-watcher hosted service, no redirect
///     middleware, no public-page catch-all. Strip-imports byte-for-
///     byte to the pre-Phase-38 behaviour; deployments that don't
///     opt in pay zero runtime cost.
///   * `EnabledPublicRendering root` — the
///     `ToolUp.PublicRendering` companion's `MarkdownContentLoader`
///     reads `root`, the catch-all `PublicPageHandler` mounts at
///     lowest precedence, the redirect map applies, and the sitemap
///     handler emits at `/sitemap.xml`.
type PublicRenderingMode =
    | NoPublicRendering
    | EnabledPublicRendering of root: ContentRoot

/// Phase 39 — `IAssetStore` substrate opt-in.
///
///   * `NoAssetStore` (default, GP 13) — no `/api/assets/*`
///     handlers mount, no `IAssetStore` DI singleton, no audit
///     emission. Strip-imports byte-for-byte to the
///     pre-Phase-39 behaviour; deployments that don't opt in
///     pay zero runtime cost.
///   * `EnabledAssetStore` — the `ToolUp.AssetStore` companion
///     registers `DefaultAssetStore` (wrapping the SDK's
///     configured `IBlobStorage` for originals + derivative
///     cache), mounts the Fable.Remoting `IAssetApi` handler at
///     `/api/assets/`, the form-multipart upload endpoint at
///     `/api/assets/upload`, and (when audit emission is
///     enabled at compose time) emits `AssetUploaded` /
///     `AssetDeleted` via `IAuditLog`.
///
/// The detailed shape (`AssetStoreOptions`, `DerivativeSpec`,
/// `DerivativeProfileId`) lives in the companion namespace
/// `ToolUp.AssetStore` rather than `Platform.Core` — the
/// opt-in mode here is a bool-shaped gate; the substrate
/// configuration is a companion-owned record. Same shape as
/// the `PublicRendering` gate above.
type AssetStoreMode =
    | NoAssetStore
    | EnabledAssetStore

/// Phase 88 — `IMediaLibrary` substrate opt-in (time-based media:
/// video / audio hosting).
///
///   * `NoMediaLibrary` (default, GP 13) — no `/api/media/*` or
///     `/media/*` handlers mount, no `IMediaLibrary` DI singleton,
///     no URL-signing key resolution, no range endpoint. Strip-
///     imports byte-for-byte to the pre-Phase-88 behaviour;
///     deployments that don't opt in pay zero runtime cost.
///   * `EnabledMediaLibrary` — the `ToolUp.MediaLibrary` companion
///     registers `DefaultMediaLibrary` (over the SDK's configured
///     `IBlobStorage`), mounts the HTTP-range-serving endpoint
///     (`206 Partial Content` for `<video>` seeking), the
///     scope-signed expiring-URL minting + verification, and the
///     Fable.Remoting `IMediaApi` handler. Transcode / HLS
///     rendition production is delivered by opt-in sub-companions
///     (`ToolUp.Media.FFmpeg`, `ToolUp.Media.CloudTranscode`);
///     the default impl range-serves over blob storage with no
///     transcode dependency (GP 1 / GP 2).
///
/// The detailed shape (`MediaLibraryOptions`, `MediaRecord`,
/// `ByteRange`) lives in the companion namespace
/// `ToolUp.MediaLibrary` rather than `Platform.Core` — the opt-in
/// mode here is a bool-shaped gate; the substrate configuration is
/// a companion-owned record. Same shape as the `AssetStore` gate
/// above.
type MediaLibraryMode =
    | NoMediaLibrary
    | EnabledMediaLibrary

/// Phase 26 — deploy-plane substrate opt-in. Default `NoDeployPlane`
/// (GP 13) registers nothing: no `IBuildOrchestrator`, no
/// `IDeployPipeline`, no `ITenantFleet`, no `Tenant` entity
/// registration, no `_platform.build` / `_platform.deploy` event
/// emission. Byte-for-byte identical to pre-Phase-26 behaviour for any
/// deployment that does not opt in.
///
/// `SingleNodeDeployPlane` registers the three SDK-shipped defaults
/// (`JobSchedulerBuildOrchestrator` over `IJobScheduler`,
/// `DefaultDeployPipeline`, `EntityStoreTenantFleet`) plus the
/// `Tenant` entity. `IContainerScheduler` is **consumer-supplied** —
/// the SDK does not register a default. Operators wire a backend
/// (`DockerLocalContainerScheduler` is the dev-grade reference
/// companion; Fly Machines / K8s / CloudRun ship as downstream
/// cloud-specific companions). When `SingleNodeDeployPlane` is set
/// without an `IContainerScheduler` in DI, an `IConfigValidator` emits
/// a startup error.
///
/// **Dependencies.** `SingleNodeDeployPlane` requires
/// `JobScheduler = InProcessJobScheduler` (for the build orchestrator's
/// dispatch substrate) and `EntityStore = EnabledEntityStore` (for the
/// tenant catalog). A future config validator may enforce; for now the
/// composition root raises at construction time if either is missing.
///
/// A distributed companion (an Akka-cluster-sharded build orchestrator)
/// replaces the singletons via DI and adds new cases here without
/// changing existing consumers.
type DeployPlaneMode =
    /// No deploy-plane infrastructure registered. Default — keeps the
    /// SDK lean for deployments not running the Layer 3 deploy plane.
    | NoDeployPlane
    /// Single-node defaults registered:
    /// `JobSchedulerBuildOrchestrator` + `DefaultDeployPipeline` +
    /// `EntityStoreTenantFleet` + `Tenant` entity. Consumer supplies
    /// `IContainerScheduler` separately via DI.
    | SingleNodeDeployPlane

// ─── Wave 10 — Phase 56 inbound rate-limit substrate types ────────

/// Identity key used by the inbound `IRateLimitStore` substrate.
/// Identity-by-value (GP 12 rule 1) — every key is a serialisable
/// string. `InboundComposite` combines two dimensions (e.g. IP +
/// route) so a policy can rate-limit per-`(ip, route)` rather than
/// just per-IP. Stores partition counts per `InboundRateLimitKey`.
/// Distinct from the record-shaped `RateLimitKey` used by
/// `IRateLimiter` (outbound, per-provider quotas).
type InboundRateLimitKey =
    | IpAddressKey of string
    | UserIdKey of string
    | InboundComposite of string

module InboundRateLimitKey =
    /// Stable string projection for store-key derivation. Always
    /// prefixed so an IP key never collides with a UserId key that
    /// happens to be a valid IP literal.
    let asStoreKey =
        function
        | IpAddressKey ip -> sprintf "ip:%s" ip
        | UserIdKey u -> sprintf "uid:%s" u
        | InboundComposite c -> sprintf "c:%s" c

/// Window shape for an `IRateLimitStore` count. `PerSecond` /
/// `PerMinute` / `PerHour` / `PerDay` are calendar-aligned (the
/// store's wall-clock implementation truncates `OccurredAt` to the
/// matching boundary). `SlidingWindow` is duration-bounded; the
/// store keeps a rolling count of events within the trailing
/// window.
type RateLimitWindow =
    | PerSecond
    | PerMinute
    | PerHour
    | PerDay
    | SlidingWindow of duration: TimeSpan * bucketCount: int

/// Typed error payload returned to the client when a rate-limit
/// policy denies the request. Mirrored on the wire as the body of
/// the 429 response and emitted via `RateLimit-Limit` /
/// `RateLimit-Remaining` / `RateLimit-Reset` / `Retry-After`
/// headers.
type RateLimitedError = {
    RetryAfterSeconds: int
    Limit: int
    Window: RateLimitWindow
}

/// Outcome of an atomic `IncrementAndCheck` call on
/// `IRateLimitStore`. `AllowWithRemaining` carries the remaining
/// count so the middleware can stamp `RateLimit-Remaining`.
/// `DenyWithError` carries the typed error. Distinct from
/// `IRateLimiter`'s outbound `RateLimitDecision`.
type InboundRateLimitDecision =
    | AllowWithRemaining of remaining: int
    | DenyWithError of RateLimitedError

/// What to do when a policy's threshold is exceeded. `Return429` is
/// the canonical case (HTTP 429 with typed payload). `DelayAndAllow`
/// holds the request for up to the window boundary then admits it
/// (degraded-service mode — useful for non-critical analytics
/// endpoints). `DenySilently` returns 204 — for endpoints where the
/// caller should not be told they hit the cap (anti-abuse).
type RateLimitOnExceeded =
    | Return429
    | DelayAndAllow of maxDelay: TimeSpan
    | DenySilently

/// Selector for `RouteLimit.Key` — which axis the middleware reads.
type RateLimitKeyKind =
    | ByIp
    | ByUserId
    | ByComposite of customKey: string

/// Declarative rate-limit policy. `Route` is a prefix match
/// (case-insensitive `StartsWith`) against the request path. `Key`
/// names which `InboundRateLimitKey` dimension to evaluate; the
/// middleware extracts the value from `HttpContext` per dimension
/// (IP from `Connection.RemoteIpAddress`, UserId from the resolved
/// `AccessContext`, Composite from a developer-supplied
/// `keyOverride`).
type RouteLimit = {
    Route: string
    Key: RateLimitKeyKind
    Window: RateLimitWindow
    Threshold: int
    OnExceeded: RateLimitOnExceeded
}

/// Audit-event shape recorded when `IRateLimitStore.IncrementAndCheck`
/// returns a `Deny`. Surfaced via the Phase 61 PlatformAdmin rate-limit
/// event log widget (`/api/_platform/admin/rate-limits`). Identity-by-
/// value (GP 12 rule 1): the key is a serialisable DU over `string`,
/// the route is the matched prefix. Lives in `Platform.Core` (not
/// `Platform.Server`) because the Fable client widget parses this
/// shape over the wire — wire types must be visible to both tiers.
type RateLimitDecisionEvent = {
    Key: InboundRateLimitKey
    Route: string
    Window: RateLimitWindow
    Threshold: int
    Decision: InboundRateLimitDecision
    OccurredAt: DateTimeOffset
}

module RouteLimit =
    /// Per-IP-per-minute policy with `Return429` on exceed — the
    /// most common shape.
    let perIpPerMinute (route: string) (threshold: int) = {
        Route = route
        Key = ByIp
        Window = PerMinute
        Threshold = threshold
        OnExceeded = Return429
    }

// ─── Wave 10 — Phase 59 consent-management substrate types ────────

/// Categories a consumer might request consent for. Loosely mirrors
/// IAB TCF categories and Funding Choices' default category list.
/// `Necessary` is always granted (cannot be denied — strictly-
/// necessary cookies / first-party session). Others depend on
/// consumer + jurisdiction + CMP configuration.
type ConsentCategory =
    | Necessary
    | Functional
    | Analytics
    | Marketing
    | Personalisation
    | ThirdPartyEmbeds

/// Per-category consent decision. `NotYetDecided` is the
/// pre-banner-interaction state.
type ConsentDecision =
    | Granted
    | Denied
    | NotYetDecided

/// Current consent state — snapshot of which categories the user
/// has granted vs denied, with audit metadata. `ConsentVersion`
/// allows consumers to invalidate prior decisions on a CMP policy
/// change.
type ConsentState = {
    Granted: Set<ConsentCategory>
    Denied: Set<ConsentCategory>
    LastUpdatedAt: DateTimeOffset
    ConsentVersion: int
}

module ConsentState =
    /// Default state — only `Necessary` granted, everything else
    /// not yet decided. Appropriate for first-page-load before the
    /// CMP runs.
    let initial =
        let now = DateTimeOffset.UtcNow

        {
            Granted = Set.singleton Necessary
            Denied = Set.empty
            LastUpdatedAt = now
            ConsentVersion = 1
        }

    /// True when the given category is currently granted.
    let isGranted (category: ConsentCategory) (state: ConsentState) : bool = Set.contains category state.Granted

    /// True when every category in `required` is granted.
    let hasAll (required: ConsentCategory list) (state: ConsentState) : bool =
        required |> List.forall (fun c -> isGranted c state)

/// Server-side audit row recorded when `ServerConfig.ConsentAudit =
/// EnabledConsentAudit`. The anonymous-user id is the browser-local
/// UUID until [Phase 62](62-premium-claim-recognition.md) establishes
/// an authenticated-user seam for consent state.
type ConsentEvent = {
    AnonymousUserId: string
    Category: ConsentCategory
    Decision: ConsentDecision
    Timestamp: DateTimeOffset
    CmpProvider: string
}

// ─── Wave 10 — Phase 60 AdPanel substrate types ────────────────────

/// AdSense format DUs mirroring the documented `data-ad-format`
/// values. `Fluid` carries the layout key (for in-content layouts).
type AdFormat =
    | AdAuto
    | AdRectangle
    | AdVertical
    | AdHorizontal
    | AdFluid of layoutKey: string

/// Optional style hint applied to the `<ins>` element's `style`
/// attribute. AdSense's default is `display:block`; consumers may
/// override for fixed-size slots.
type AdStyleHint = { CssStyle: string }

/// Per-slot configuration consumed by the Feliz `<AdSlot>`
/// component. `AdClientId` is the publisher's `ca-pub-XXXX` id;
/// `SlotId` is the AdSense slot identifier minted in the AdSense
/// console.
type AdSlotConfig = {
    AdClientId: string
    SlotId: string
    Format: AdFormat
    Style: AdStyleHint option
}

/// Identity for the audit-side impression / click events.
type AdImpression = {
    SlotId: string
    AdClientId: string
    OccurredAt: DateTimeOffset
    PathAtImpression: string
}

type AdClick = {
    SlotId: string
    AdClientId: string
    OccurredAt: DateTimeOffset
    PathAtClick: string
    ClickToken: string
}

/// AdPanel composition mode — `ClientConfig.AdPanel`. Default
/// `NoAdPanel` strips every `<AdSlot>` render path (slot renders
/// empty fragment without loading AdSense JS). `EnabledAdPanel`
/// activates the substrate; per-slot ad units pick their config up
/// from `AdPanelConfig` and the consent gate runs against
/// `IConsentProvider` (Phase 59).
type AdPanelConfig = {
    DefaultAdClientId: string
    ConsentCategoriesRequired: ConsentCategory list
}

type AdPanelMode =
    | NoAdPanel
    | EnabledAdPanel of AdPanelConfig

// ─── Wave 10 — Phase 62 premium-claim substrate types ──────────────

/// Premium status the SDK reads from the active auth provider's
/// user-metadata. `NotPremium` is the default for anonymous +
/// non-premium-logged-in users.
type PremiumStatus =
    | NotPremium
    | Premium of grantedAt: DateTimeOffset * grantedBy: string * reason: string option

/// Top-level premium model. `AnonymousFirst` is the v1 shipping
/// case — anonymous-by-default with operator-granted premium status.
/// Self-serve / billing-driven models are future cases.
type PremiumModel = | AnonymousFirst

// ─── Wave 10 — Phase 61 PlatformAdmin profile types ────────────────

/// Standard `PlatformAdmin` widget bundle (today's set —
/// HealthMonitor, TeamAdmin, etc.) vs the public-utility bundle
/// (traffic dashboard, rate-limit log, ad-unit config, premium
/// users). The bundle controls which widgets the PlatformAdmin
/// module surfaces; non-applicable widgets auto-skip when their
/// substrate dependency is unwired.
type PlatformAdminProfile =
    | StandardPlatformAdminProfile
    | PublicUtilityPlatformAdminProfile

/// Phase 59 — declarative consent-provider selection visible to the
/// client `ClientConfig.ConsentProvider`. `NoConsentProvider`
/// (default) is the `NoOpConsentProvider` shape — `Necessary` always
/// granted, every other category `NotYetDecided`; appropriate for
/// deployments outside jurisdictions that require explicit consent.
/// `FundingChoicesConsent` wires Google Funding Choices via the
/// `data-ad-client` id (companion-Fable side ships the bootstrap).
/// `CustomConsentProvider` reserves the seam for third-party CMP
/// companions (`ToolUp.Consent.Quantcast`, `Cookiebot`, etc.) that
/// inject their own `IConsentProvider`.
type ConsentProviderMode =
    | NoConsentProvider
    | FundingChoicesConsent of adClientId: string
    | CustomConsentProvider of providerName: string

// ─── Wave 10 — public-utility substrate modes ──────────────────────

/// Phase 16 — host-model selection. Default `KestrelHost` runs the
/// standard long-running Kestrel server with every registered
/// `BackgroundService`. `ServerlessHost` opts the deployment into a
/// serverless-compatible composition: `compose` skips every
/// `BackgroundService` registration (job scheduler, webhook
/// dispatcher, transactional dispatcher, RAG ingestion service) and
/// returns a request/response pipeline a Functions / Lambda / GCF
/// adapter can drive. Apps composing the serverless host adapter
/// companion (e.g. `ToolUp.Hosts.AzureFunctions`) set this flag; apps
/// running under Kestrel leave it at the default.
type ServerlessHostMode =
    /// Default — long-running Kestrel + every registered
    /// `BackgroundService` runs.
    | KestrelHost
    /// Serverless-compatible composition. `compose` skips
    /// `BackgroundService` registrations; the host adapter drives the
    /// request pipeline per invocation. Pair with `JobScheduler =
    /// NoJobScheduler`, `Webhooks = NoWebhooks`, and `Notifications =
    /// NoNotificationsExplicit` for a clean serverless shape; an
    /// inbound-only deployment that wants jobs runs a separate worker
    /// silo with `ProcessProfile = WorkerOnly` against the same
    /// `IBlobStorage` / `IEventStore`.
    | ServerlessHost

/// Phase 16a — process-profile selection. Default `AllInOne` runs
/// every `IHostedService` and HTTP middleware in one binary
/// (today's behaviour). `WebOnly` skips every `BackgroundService` —
/// the silo serves `/api/*` only; jobs scheduled by requests are
/// picked up by the worker silo via the persistent `IJobStore`.
/// `WorkerOnly` skips HTTP middleware and `/api/*` routes —
/// `compose` returns a worker-only `IHostBuilder`; the silo runs the
/// scheduler / webhook dispatcher / RAG ingestion / transactional
/// dispatcher. `DispatcherOnly` runs only the outbound-side
/// dispatchers (transactional + webhook) — for deployments wanting
/// outbound-delivery isolation.
///
/// Coordination across silos relies on a distributed notification
/// channel (Phase 6e Redis) and Phase 9i `IDistributedLock` for
/// cross-silo single-leader concerns (cron tick, webhook retry
/// timer).
type ProcessProfile =
    /// Default. Web tier + every `BackgroundService` in one process.
    /// Single-binary deployments.
    | AllInOne
    /// HTTP middleware + handlers; no `BackgroundService` work.
    /// Multiple instances scale stateless; a separate `WorkerOnly`
    /// silo drains jobs.
    | WebOnly
    /// Background work only; no HTTP middleware, no `/api/*` routes.
    /// One instance unless paired with `IDistributedLock` for
    /// single-leader coordination.
    | WorkerOnly
    /// Only the transactional + webhook dispatchers; no scheduler,
    /// no RAG ingestion, no `/api/*`. Outbound-delivery isolation.
    | DispatcherOnly

/// Phase 56 — `IRateLimitStore` substrate opt-in. Default
/// `NoRateLimitStore` strips the entire inbound rate-limit middleware
/// + `IRateLimitStore` registration. `InMemoryRateLimitStore`
/// activates the single-instance default — concurrent-dictionary +
/// per-key TTL eviction; appropriate for Kestrel single-instance
/// dev/test. External-store variants (Azure Table Storage / Redis /
/// Cosmos / DynamoDB) ship as `ToolUp.RateLimit.<store>` sub-
/// companion packages that register their own `IRateLimitStore`
/// against the seam.
type RateLimitStoreMode =
    /// No `IRateLimitStore` registered; the new
    /// `RateLimitMiddleware` is not mounted. Default.
    | NoRateLimitStore
    /// In-memory single-instance store. Dev / Kestrel-default.
    /// Single-instance only — multi-instance deployments share
    /// counts only via an external store.
    | InMemoryRateLimitStore
    /// Operator-supplied external `IRateLimitStore` implementation
    /// (registered as a singleton in DI by the companion's
    /// composition extension).
    | ExternalRateLimitStore

/// Phase 59 — server-side consent-audit opt-in. Default
/// `NoConsentAudit` strips the `/api/_platform/consent-audit`
/// endpoint and no `ConsentEvent` is persisted server-side; client-
/// side consent state still works (lives in the browser via
/// `IConsentProvider`). `EnabledConsentAudit` mounts the endpoint
/// and lands events via the configured `IAuditLog` for deployments
/// needing demonstrable evidence of consent decisions.
type ConsentAuditMode =
    | NoConsentAudit
    | EnabledConsentAudit

/// Phase 159 — server-side durable per-subject consent-state store
/// opt-in. Default `NoConsentStateStore` registers nothing — consent
/// state lives only in the browser via `IConsentProvider` (Phase 59),
/// byte-for-byte unchanged (GP 13). `InMemoryConsentStateStore`
/// registers the single-instance dev store (does NOT survive restart).
/// `EntityBackedConsentStateStore` registers the durable production
/// store over `IEntityStore` — requires `EntityStore =
/// EnabledEntityStore` (the compose path prepends the `ConsentRecord`
/// entity registration automatically). Distinct from `ConsentAudit`
/// (which mounts the client-event audit endpoint); this is the
/// authoritative server-side read-back store.
type ConsentStateStoreMode =
    | NoConsentStateStore
    | InMemoryConsentStateStore
    | EntityBackedConsentStateStore

/// Phase 60 — server-side ad-analytics opt-in. Default
/// `NoAdAnalytics` strips the `/api/_platform/ads/analytics`
/// endpoint. `EnabledAdAnalytics` mounts the endpoint and lands
/// `AdImpression` / `AdClick` audit events via `IAuditLog`. Distinct
/// from the client-side `ClientConfig.AdPanel` mode that controls
/// whether `<AdSlot>` Feliz components render at all.
type AdAnalyticsMode =
    | NoAdAnalytics
    | EnabledAdAnalytics

/// Configuration for the server application
type ServerConfig = {
    /// Kestrel listen port. Default `5000`. Vite dev server runs on
    /// `8080` (configurable via `VITE_DEV_PORT`) and proxies `/api/*`
    /// to this port. Set the `SERVER_PORT` env var to override both
    /// sides — F# reads it for Kestrel and Vite reads the same name
    /// to set the proxy target.
    Port: int
    PublicPath: string
    /// Declared subject shapes this deployment supports. Non-empty
    /// list — `SurfaceCoherenceValidator` refuses startup on an empty
    /// `Surfaces`. A single-shape deployment declares one entry (e.g.
    /// `Surfaces.individual`); mixed-mode deployments declare two or
    /// more (e.g. `Surfaces.anonymousAndIndividual`).
    Surfaces: SurfaceProfile list
    /// Names of the modules this deployment exposes — used by the
    /// permission system (to report "what modules exist?" and filter
    /// what a given user can access). Order matches the app's intended
    /// sidebar ordering. Empty list = no RBAC-visible modules (the
    /// `/api/platform/GetAccessibleModules` endpoint returns an empty
    /// list; route-level guards still work). Apps populate this from
    /// their `allModules` tuple at compose time.
    ModuleNames: string list
    /// Event-store selection. Default: `InMemoryOnly`. Opt in to
    /// `PersistentBlobBacked retentionPolicy` when the deployment needs
    /// events to survive restarts or act as an audit trail.
    EventStore: EventStoreMode
    /// Controls whether `VectorScope.Platform` is exposed
    /// to RAG retrieval. Default: `Disabled`. The toggle gates READ
    /// access; the WRITE side is gated by `canModifyPlatformConfig`
    /// regardless of this setting. Admins can pre-populate Platform
    /// KB content via `IPlatformKnowledgeApi` while this is `Disabled`
    /// and flip to `Enabled` to make it visible to all authenticated
    /// users.
    PlatformKnowledgeBase: PlatformKnowledgeBaseMode
    /// Dev convenience — when `Some userId`, the platform-admin
    /// bootstrap falls back to this user-id if `TOOLUP_INITIAL_PLATFORM_ADMIN`
    /// is unset / empty AND the admin list is empty. Lets dev composition
    /// roots skip the env-var dance per local run by flipping this on
    /// inside their own `#if DEBUG`. **Production deployments MUST leave
    /// this `None`** and rely exclusively on the env var — auto-bootstrap
    /// would grant admin to whoever happens to sign in first, which is
    /// fine in dev but a security hole in production. Logs at `Warn` on
    /// successful auto-bootstrap so the dev path is clearly marked in
    /// startup output.
    AutoBootstrapDevAdmin: string option
    /// Per-module configuration schemas registered at compose time.
    /// Keyed by `ModuleConfigEntry.ModuleKey` (typically the module's
    /// `Definition.Id`; or `ConfigKeys.PlatformModuleKey` for the
    /// reserved platform-level lane). Empty list = no configurable
    /// modules, `IConfigApi.ListModules` returns `[]`. Order determines
    /// the default admin-UI tab order. Additive default keeps existing
    /// apps compiling unchanged.
    ModuleConfigs: ModuleConfigEntry list
    /// Whether to inject the SDK-shipped `_platform` schema entry
    /// ("Platform Defaults" with the `currencySymbol` field) when the
    /// app supplies no `_platform` entry of its own. Default `true`
    /// preserves backward-compat with apps that consume
    /// `Visualisation.PlatformDefaults.CurrencySymbol`. Consumer apps
    /// whose modules don't render monetary values set this `false`
    /// so the admin UI doesn't
    /// surface an irrelevant Configuration → Platform Defaults tab.
    /// When `false` and the app supplies its own `_platform` entry,
    /// the merge behaviour is unchanged.
    IncludePlatformDefaults: bool
    /// Declared feature flags visible to the deployment. Union of
    /// platform-level flags (set here) and module-declared flags
    /// (collected from `ClientModule.FeatureFlags`).
    /// Drives admin-UI rendering and `FlagEvaluator` schema checks —
    /// reads on an undeclared key log a Warn and return a safe default.
    /// Empty list = no declared flags; `IFeatureFlagApi.GetResolvedFlags`
    /// returns the empty map.
    FeatureFlags: FeatureFlag list
    /// Optional case-insensitive substring filter over `ServerModule.Name`
    /// — modules whose name does not contain the filter as a substring
    /// (whitespace ignored) are dropped at `ServerApp.addModule` time.
    /// Populated from the `TOOLUP_MODULE` env var in the reference app;
    /// `None` / empty keeps every module registered. Keeps single-module
    /// dev runs consistent with the client's `ClientConfig.ModuleFilter`.
    ModuleFilter: string option
    /// Phase 170 — module-binding trust configuration (anchor descriptors +
    /// the unbound-allowed policy bit). Default = no anchors + `AllowUnbound`
    /// = `true` (binding off; byte-for-byte the pre-binding pipeline, GP 13).
    /// Populated from `TOOLUP_MODULE_BINDING_*` by `fromEnv`; resolved into a
    /// verifier at compose time by `ToolUp.ArtefactSigning`'s resolver
    /// (symmetric key material via `ISecretStore`, never plaintext config).
    ModuleBindingTrust: ModuleBindingTrustConfig
    /// Register `app.UseHttpsRedirection()` ahead of the scope-resolution
    /// middleware. Default `false` — local dev runs over HTTP. Production
    /// deployments behind a TLS-terminating load balancer should set this
    /// alongside `TrustForwardedHeaders` so the redirect honours the
    /// `X-Forwarded-Proto` value rather than the proxy-to-origin scheme.
    RequireHttps: bool
    /// Register `app.UseForwardedHeaders(...)` honouring `X-Forwarded-Proto`
    /// and `X-Forwarded-For`. Default `true` (Phase 16d) — containerised
    /// and serverless deploys are almost always behind a TLS-terminating
    /// ingress (Cloud Run, ALB, App Service Front Door, AKS Ingress,
    /// function gateway), and without forwarded-headers trust the SDK
    /// misreports client IPs in audit logs and rejects HTTPS redirects.
    /// Set `false` (or `TOOLUP_TRUST_FORWARDED_HEADERS=0`) on a direct-
    /// bind dev shell with no proxy hop.
    TrustForwardedHeaders: bool
    /// Phase 325 — CIDR allowlist scoping `TrustForwardedHeaders`.
    /// When non-empty, `UseForwardedHeaders` honours `X-Forwarded-For`
    /// / `X-Forwarded-Proto` only from peers inside these networks
    /// (`ForwardedHeadersOptions.KnownIPNetworks` is populated from
    /// the parsed entries instead of being cleared). Entries are
    /// IPv4/IPv6 networks in CIDR form with host bits zero
    /// (`"10.0.0.0/8"`, `"2001:db8::/32"`); a malformed entry fails
    /// loud at startup. Default `[]` preserves the pre-325
    /// trust-any-peer posture (GP 11) — but
    /// `ForwardedHeadersTrustValidator` escalates that posture to a
    /// preflight `Error` in auth-requiring modes unless
    /// `AcceptForwardedHeadersFromAnyProxy` is set. Override via
    /// `TOOLUP_TRUSTED_PROXY_CIDRS` (comma-separated CIDR list).
    TrustedProxyCidrs: string list
    /// Phase 325 — explicit opt-in to keeping the trust-any-peer
    /// forwarded-headers posture (`TrustForwardedHeaders = true` with
    /// an empty `TrustedProxyCidrs`) in an auth-requiring mode. Set
    /// `true` only when a single trusted proxy that strips
    /// client-supplied `X-Forwarded-*` headers fronts every request
    /// path. Default `false` — `ForwardedHeadersTrustValidator`
    /// refuses startup in auth-requiring modes otherwise. Override
    /// via `TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY=1`.
    AcceptForwardedHeadersFromAnyProxy: bool
    /// What to do when `PublicPath` doesn't exist on disk at startup.
    /// Default `Warn` (backward-compatible — local dev appropriate).
    /// Production sets `RequireExist` so a missing artefact crashes
    /// loudly instead of returning 404s for every SPA route.
    StaticPathBehaviour: StaticPathBehaviour
    /// Threshold above which `RequestTimingMiddleware` logs a `Warn`
    /// for the request. Default `TimeSpan.FromSeconds 1.0` — production
    /// can tighten to surface latency regressions earlier or relax for
    /// chatty endpoints. Slow requests are an operational signal, not
    /// a state change, so no audit event is emitted.
    SlowRequestThreshold: TimeSpan
    /// Per-route overrides for `SlowRequestThreshold`, keyed by request
    /// path prefix (case-insensitive). Longest-prefix match wins; an
    /// unmatched request falls back to `SlowRequestThreshold`. Use this
    /// for endpoints whose happy-path latency legitimately exceeds the
    /// global default — e.g. `KnowledgeApi/UploadDocument` (synchronous
    /// extraction), AI inference routes, large file uploads. Default
    /// `Map.empty`.
    SlowRequestThresholdOverrides: Map<string, TimeSpan>
    /// Default per-team storage quota in bytes, enforced by
    /// `SessionFileStore` on `AddFile`. `None` (default) disables the
    /// check — the deployment-wide knob is opt-in. A team that exceeds
    /// its quota gets `Error "storage quota exceeded …"` from the
    /// upload path; no file is persisted and no audit event is
    /// recorded (rejection is a transient operator concern, not a
    /// state change worth auditing). Per-team overrides via
    /// `IConfigStore` are a follow-up — the quota resolver shape on
    /// `SessionFileStore` already supports them.
    DefaultTeamStorageQuotaBytes: int64 option
    /// Phase 66 Stream C.3 — per-subject-kind rate-limit configuration.
    /// `RateLimitConfig.none` (default) disables rate limiting —
    /// deployments opt in via `RateLimitConfig.uniform` /
    /// `.perShape` / `.withOverrides`. When enabled, `app.UseRateLimiter()`
    /// is registered with a fixed-window policy whose partition is
    /// implied by the resolved `Subject` kind (`team:` / `user:` /
    /// `token:` / `ip:` — see `RateLimitPolicy.partitionFor`), and whose
    /// limits resolve per kind via `RateLimitConfig.policyFor`.
    /// `/health`, `/ready`, `/api/notifications` (SSE), and
    /// `/api/ai/events` (SSE) are excluded — long-lived and probe
    /// traffic must not be capped.
    RateLimit: RateLimitConfig
    /// Result-store selection. Default: `NoResultStore` —
    /// no `IResultStore` is registered. Modules that produce
    /// analytical outputs only persist them when the deployment opts
    /// in to `InMemoryResultStore` (tests / dev) or
    /// `PersistentResultStore` (production: routes through
    /// `IDataObjectStore` with `StrictlyVersioned`, emits
    /// `AnalysisCompleted` events).
    ResultStore: ResultStoreMode
    /// Lineage selection. Default: `NoLineageStore` — no
    /// `ILineageStore` is registered, the `inputs` parameter on
    /// `IResultStore.SaveResult` is silently ignored. Enable with
    /// `EnabledLineageStore` to activate query support over
    /// `IEventStore` and auto-emit lineage links from
    /// `PersistentResultStore`.
    Lineage: LineageStoreMode
    /// Job-scheduler selection. Default: `NoJobScheduler`
    /// — no `IJobScheduler` is registered, no scheduler tick runs,
    /// no `_platform/jobs/` blob layout is touched. Enable with
    /// `InProcessJobScheduler` to activate the in-process default
    /// (minute-precision, single-instance). Distributed companions
    /// add new cases here.
    JobScheduler: JobSchedulerMode
    /// Phase 9b.A — opt-in back-fill of `OnEvent`-triggered jobs after
    /// detected scheduler tick drift. Default: `false` — a missed
    /// minute boundary surfaces as a `JobSchedulerTickMissed`
    /// operational event under `_platform.jobs` (deliberately a
    /// separate stream from the `AuditEvent` DU; correlatable by
    /// `ScopeId + OccurredAt` if a sink needs both) + a
    /// `HealthMonitorUI` counter, but no work re-fires.
    /// When `true`, the in-process scheduler re-fires each active
    /// `OnEvent`-triggered job once on drift recovery. Cron jobs are
    /// NOT back-filled regardless (cron semantics expect "fire on the
    /// boundary"; drift-fire is the wrong shape — a `*/5 * * * *`
    /// rollup re-fired three times back-to-back after a 15-minute
    /// pause would conflate three separate roll-up windows). Operators
    /// opt in when their `OnEvent` work is safely re-entrant.
    BackfillMissedTicks: bool
    /// Share-token substrate selection. Default:
    /// `NoShareTokenStore` — no `IShareTokenStore` is registered,
    /// no `_platform/share-tokens/` blob layout is touched, no
    /// signing-key secret is resolved. Enable with
    /// `EnabledShareTokenStore` when the deployment issues signed
    /// share-links (publishable forms, magic-login links, public
    /// read-only dashboards). The default `BlobShareTokenStore` is
    /// blob-backed and HMAC-signed against an auto-generated key in
    /// `ISecretStore`.
    ShareTokenStore: ShareTokenStoreMode
    /// Peer-bearer-auth route registry. Path prefixes
    /// listed here are owned by `PeerBearerAuthMiddleware`: the
    /// middleware validates the request's `Authorization: Bearer
    /// <token>` header against the per-peer secret resolved via
    /// `ISecretStore.GetSecret("_platform", $"peers/{peerName}/bearer")`,
    /// where `peerName` is read from the `X-Peer-Name` request
    /// header. On success the middleware sets `HttpContext.Items
    /// ["PeerName"]` so downstream handlers can partition state per
    /// caller; on mismatch the response is 401 before the request
    /// reaches the handler. Peer routes are exempt from
    /// `AuthEnforcementMiddleware`'s user-auth check — the bearer
    /// IS the authentication. Companions wanting cross-instance
    /// peer calls register their handler prefix here via
    /// `ServerApp.withPeerRoutePrefix`. Empty list (the default)
    /// disables the middleware entirely — strip-imports clean.
    /// Supersedes nothing: the richer `IPeerAuthProvider`
    /// (JWT, delegated assertions, version handshake) coexists on
    /// different prefixes when both are configured.
    PeerRoutePrefixes: string list
    /// Per-request body-size cap. ASP.NET Core's
    /// default Kestrel limit is 30 MB, which is fine for most Fable.
    /// Remoting payloads but is too small for legitimate large file
    /// uploads and is too large for API-only deployments wanting a
    /// tighter DoS posture. `None` keeps Kestrel's 30 MB default; set
    /// to `Some bytes` to override (compose stamps it on
    /// `Kestrel.Limits.MaxRequestBodySize`). The
    /// `MaxRequestBodyBytesValidator` warns when an internet-facing
    /// auth-mode deployment combines a high cap (>50 MB) with no
    /// rate-limiting.
    MaxRequestBodyBytes: int64 option
    /// Operator-supplied allowlist of webhook
    /// target hosts that bypass the SDK's SSRF defence. The default
    /// `WebhookUrlValidator` refuses URLs that resolve to
    /// loopback / link-local / RFC1918 / unique-local IPv6 ranges
    /// (preventing tenants from registering URLs that hit internal
    /// services like AWS instance-metadata). Hosts named here skip
    /// the IP-range check — use only for legitimate internal targets
    /// (staging deployments, internal mocks). Match is exact-host,
    /// case-insensitive against `Uri.Host`. Empty list = no
    /// allowlist (every webhook URL goes through full validation).
    WebhookUrlAllowedHosts: string list
    /// Base URL the SDK uses to compose share-link URLs
    /// from issued tokens. Companions issuing tokens (Forms
    /// `IssueTokens`) read this when building the embed URL —
    /// `{PublicBaseUrl}/r/{token}`. `None` (the default) means no
    /// base URL is configured; companions that need it for issuance
    /// fail with a clear error instead of generating broken links.
    /// Set to the deployment's public origin without a trailing slash
    /// (e.g. `Some "https://surveys.example.com"`); the path the
    /// embed entry expects (`/r/{token}`) is appended by the issuer.
    PublicBaseUrl: string option
    /// Data-ingestion selection. Default:
    /// `NoDataIngestion` — no `IDataIngestor` is registered, no
    /// `_platform/data-sources/` blob layout is touched, no
    /// `IDataIngestionApi` route is mounted. Enable with
    /// `EnabledDataIngestion` to activate the substrate; connector
    /// implementations register as `IDataSource` via DI. Triggered-
    /// refresh through `IDataIngestionApi.TriggerRefresh` requires
    /// `JobScheduler = InProcessJobScheduler` (or any future
    /// distributed scheduler companion) to schedule the `Manual`
    /// job — the API returns an error explaining the missing
    /// dependency when the scheduler is disabled.
    DataIngestion: DataIngestionMode
    /// Column-mapping selection. Default: `NoColumnMapping` — no
    /// `IColumnMappingStore`, no `IColumnMappingApi` route. Enable with
    /// `EnabledColumnMapping` to back the mapping-aware Data Manager
    /// (`ClientConfig.DataManager = MappingDataManager`): a reusable
    /// CSV-column→schema-field map is persisted per storage scope,
    /// keyed by the source CSV's column-structure fingerprint.
    ColumnMapping: ColumnMappingMode
    /// Phase 218 — policy for the mapping-aware Data Manager's dry-run
    /// validation preview. Default: `WarnOnValidationFailure` — failing
    /// rows are surfaced before commit but do not block it (GP 11). Set
    /// `BlockOnValidationFailure` to refuse commit while any row would
    /// fail. Read only when `ColumnMapping = EnabledColumnMapping`.
    MappingDryRun: MappingDryRunPolicy
    /// Phase 10h — generic OAuth 2.0 refresh-token lifecycle
    /// substrate. Default: `NoOAuthRefresher` — no
    /// `IOAuthTokenRefresher` registered; OAuth-using connectors
    /// (Phase 10e) refresh per-API-call via the synchronous
    /// `IOAuthCredentialFlow.RefreshAccessToken` path. Enable with
    /// `EnabledOAuthRefresher` to activate the background refresh
    /// substrate: `InProcessOAuthTokenRefresher` in DI,
    /// `OAuthRefreshJobHandler` under `_platform.oauth.refresh`, and
    /// the admin-UI token-status column on the data-ingestion admin.
    /// Requires `JobScheduler = InProcessJobScheduler` (or a future
    /// distributed scheduler companion); the
    /// `OAuthRefresherDepsValidator` startup check warns when the
    /// pair is misconfigured.
    OAuthRefresher: OAuthRefresherMode
    /// Entity-store selection. Default: `NoEntityStore` —
    /// no `IEntityStore` is registered, no `EntityRegistry`. Enable
    /// with `EnabledEntityStore` to activate the substrate; entity
    /// types register via `ServerApp.withEntity<'T> registration`.
    EntityStore: EntityStoreMode
    /// Phase 68 — graph-data store selection (`IGraphStore`). Default:
    /// `InMemoryGraphStore` — the zero-dependency in-memory default is
    /// registered lazily (GP 13: never instantiated until a graph API is
    /// resolved). `CustomGraphStore` leaves an engine companion's own
    /// `IGraphStore` singleton in place.
    GraphStore: GraphStoreMode
    /// Phase 161 — time-series storage selection. Default:
    /// `NoTimeSeriesStore` — no `ITimeSeriesStore` registered, zero cost.
    /// `InMemoryTimeSeries` registers the dev/test in-memory default;
    /// `CustomTimeSeriesStore` leaves a companion-registered singleton
    /// (e.g. `ToolUp.TimeSeriesStores.Timescale`) in place.
    TimeSeriesStore: TimeSeriesStoreMode
    /// Phase 163 — end-user product-telemetry sink selection. Default:
    /// `NoTelemetrySink` — the `NoOpTelemetrySink` is registered (a true
    /// no-op). `CustomTelemetrySink` leaves a companion-registered sink
    /// (e.g. `ToolUp.TelemetrySinks.Ga4`) in place.
    TelemetrySink: TelemetrySinkMode
    /// Usage-metering selection. Default: `NoUsageMetering`
    /// — `IUsageLog` and `ITeamQuotaPolicy` resolve to no-op defaults
    /// so emission sites are free at runtime. Enable with
    /// `EnabledUsageMetering` to activate the blob-backed `BlobUsageLog`,
    /// the `UsageBatchFlusher` `BackgroundService`, the
    /// `BlobBackedTeamQuotaPolicy` (reads quotas from the reserved
    /// `_platform.usage` config schema), and the `IUsageQueryApi`
    /// admin route. Required for per-team billing, cost-to-serve
    /// telemetry, and BYOK-vs-Managed line-item attribution.
    UsageMetering: UsageMeteringMode
    /// Metrics endpoint selection. Default:
    /// `Disabled` — `IMetricsSink` resolves to `NoOpMetricsSink`,
    /// no `MetricsMiddleware`, no `/metrics` route, no SDK standard
    /// metrics registered. Enable with `Enabled` to mount `/metrics`
    /// in Prometheus / OpenMetrics text format. Companion sinks
    /// (OTel exporter at `src/Metrics/OpenTelemetry/`) register
    /// alongside the in-process default and compose via fan-out.
    MetricsEndpoint: MetricsEndpointMode
    /// Per-metric cardinality cap configuration. Only
    /// meaningful when `MetricsEndpoint = EnabledMetricsEndpoint`.
    /// Default `MetricsSinkConfig.defaults` — 1000 distinct
    /// `(tag-set)` combinations per metric before overflow folding
    /// kicks in.
    MetricsSink: MetricsSinkConfig
    /// Outbound-webhook selection. Default: `NoWebhooks`.
    /// Enable with `EnabledWebhooks` when the deployment publishes
    /// events to third-party systems (Slack, PagerDuty, customer
    /// ingestion). Opt-in so the lightweight shape carries no
    /// dispatcher `BackgroundService`, no `HookedEventStore`
    /// decorator overhead, and no admin API.
    Webhooks: WebhookMode
    /// Audit-log selection. Default: `NoAuditLog` —
    /// `IAuditLog` resolves to a no-op so emission sites stay free at
    /// runtime. Enable with `EnabledAuditLog` when the deployment
    /// requires a state-change trail (compliance, forensics,
    /// debugging). Pair with `EventStore = PersistentBlobBacked _`
    /// for durability.
    AuditLog: AuditLogMode
    /// Phase 66 Stream C.2 — per-subject-kind audit sampling consulted
    /// by `AuditReplicator` before delivering each event to its
    /// `IAuditSink`s. Default: `AuditSamplingPolicy.none` (keep every
    /// event for every subject kind) — byte-for-byte the pre-C.2
    /// pipeline. Operators on anonymous-heavy public surfaces opt in to
    /// thinning (e.g. keep 100% authenticated, 10% anonymous) to bound
    /// sink cost without losing the higher-value authenticated trail.
    /// Central, not per-sink (design D17): the decision is taken once
    /// per event in the replicator and applies to every registered sink.
    AuditSamplingPolicy: AuditSamplingPolicy
    /// Notification-channel selection. Default:
    /// `NotificationsAuto` — `compose` infers `InMemoryNotifications`
    /// when any feature that publishes notifications is active and
    /// `NoNotifications` otherwise. Override with an explicit value
    /// to pin behaviour or to swap in a distributed backend
    /// (`RedisNotifications`).
    Notifications: NotificationMode
    /// Response headers stamped on every response by the SDK's
    /// `SecurityHeadersMiddleware`. Common keys:
    /// `Content-Security-Policy`, `Strict-Transport-Security`,
    /// `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`.
    /// Default: `Map.empty` — no headers added. Existing handlers
    /// that already write the same header are not overwritten —
    /// the middleware skips keys already present so per-route
    /// overrides keep working.
    SecurityHeaders: Map<string, string>
    /// Phase 9j — opt-in companion-aware HTTP hardening. Default
    /// `NoSecurityHardening` (GP 13): `CspMiddleware` / `CsrfMiddleware`
    /// no-op and `/api/csrf-token` is not mounted, so a stock
    /// deployment behaves exactly as before. `DefaultSecurityHardening`
    /// stamps an auto-generated `Content-Security-Policy` aggregated
    /// from every registered `ICspContributor` and enforces a
    /// per-session CSRF token on state-changing `/api/*` requests.
    /// `StrictSecurityHardening` additionally drops `'unsafe-inline'`.
    /// Independent of `SecurityHeaders` — both compose, with any
    /// already-present header winning (per-route override preserved).
    SecurityHardening: SecurityHardeningMode
    /// CORS policy. `None` (default) = no CORS middleware
    /// registered, browsers see same-origin responses. `Some` =
    /// `compose` calls `services.AddCors(...)` and `app.UseCors(...)`
    /// at the documented pipeline position. For policies that don't
    /// fit `CorsConfig`, use `ServerApp.withPreMiddleware` and
    /// register the policy by hand.
    Cors: CorsConfig option
    /// Dev diagnostics endpoint. Default `false`.
    /// When `true`, `compose` mounts `/dev/inspect` (JSON) +
    /// `/dev/inspect/html` surfacing registered modules, the caller's
    /// resolved `AccessContext` / `StorageScope`, the data catalog, and
    /// the DI service list (type names only). The previous compile-time
    /// `#if DEBUG` gate was removed when ToolUp.Platform stopped
    /// carrying compile-time gates; this runtime flag is now the sole
    /// gate. Production deployments leave it at the default `false`;
    /// dev environments opt in explicitly.
    EnableDevEndpoints: bool
    /// Suppress-only per-endpoint override for the RAG citation dev
    /// endpoint (`/dev/rag-citation`). Surfaced separately from the
    /// master `EnableDevEndpoints` flag because the citation telemetry
    /// exposes per-(provider, model) rewrite samples — conversation-
    /// derived text, the most privacy-sensitive dev surface — and is
    /// worth being able to suppress independently.
    /// `None` (default) — follow `EnableDevEndpoints`: endpoint
    /// is registered iff the master switch is on.
    /// `Some false` — suppress the citation endpoint specifically
    /// while leaving other dev endpoints enabled. Recommended for
    /// deployments that want `/dev/inspect` / `/dev/ai-latency`
    /// but treat citation samples as too sensitive to expose.
    /// `Some true` — same as `None`. The override can never force
    /// the endpoint on while the master switch is off: the former
    /// force-on arm (Phase 14s) broke the "master off ⇒ no dev
    /// surface" audit invariant for an unauthenticated endpoint and
    /// was reversed by the 2026-06-12 gaps audit; a `Some true`
    /// under a disabled master now draws a startup `Warning` from
    /// `CitationDevEndpointValidator`.
    EnableCitationDevEndpoint: bool option
    /// Startup config-preflight escape hatch. Default
    /// `false`. When `true`, `ConfigValidatorAggregator.validate`
    /// skips the *non*-security-class validators and startup proceeds
    /// even with an unreachable dependency. For emergency boots only
    /// (e.g. an OIDC issuer outage you want to ride through, or a
    /// known-broken companion validator you haven't yet had a chance
    /// to fix). Pair with explicit monitoring — the deployment will
    /// not fail loud on the dependency that preflight would have
    /// caught.
    ///
    /// NOTE: `SkipPreflight` does NOT bypass the security-class
    /// validators (every validator that also implements
    /// `ISecurityClassValidator` — the auth / secret / cross-instance-auth-state
    /// guards). Those always run and still abort startup on `Error`; a single boolean
    /// must not silently disable identity-spoofing / unauthenticated-
    /// access protection. The skipped validators' names are logged at
    /// `Warn` so the bypass is visible in the deployment log.
    SkipPreflight: bool
    /// Opt-in periodic probe-state tracker. Default
    /// `false`. When `true`, `HealthStateTracker`
    /// `BackgroundService` polls every registered `IHealthCheck` once
    /// per minute (wall-clock-aligned, matching `JobScheduler`'s
    /// cadence) and emits a `HealthStateChanged` audit event when a
    /// probe's stable state changes (3 consecutive observations of a
    /// new status). Single-observation flaps from 1–10 Hz LB polling
    /// are absorbed by the debounce so the audit trail stays signal,
    /// not noise. Disabled by default because the audit trail is
    /// optional infrastructure and the tick cost (one parallel probe
    /// fan-out per minute) is non-zero.
    HealthStateTracking: bool

    /// Phase 178 — opt-in alert-rule / threshold engine. Default
    /// `AlertRule.none` (empty). Each rule watches a metric or health
    /// probe and delivers a notification when its `ThresholdCondition`
    /// holds for `ForDuration`. A non-empty set causes `compose` to host
    /// the `AlertRuleEngine` `BackgroundService` (subject to the
    /// `ProcessProfile` gate); an empty set registers no service and
    /// pays zero runtime cost (GP 13). Rules are code-authored — there
    /// is no env-var path (unlike scalar knobs), so `fromEnv` inherits
    /// the `defaults` empty set. Wire rules via `ServerApp.withAlertRule`
    /// / `withAlertRules`.
    AlertRules: AlertRule list

    /// Floor on `ILogger`
    /// `Debug`/`Info`/`Warn`/`Error` emission. The default
    /// `ConsoleLogger` honours this; alternative implementations are
    /// free to ignore it but should respect the documented intent —
    /// "no output below this level except `Error`, which is always
    /// emitted." Default `LogLevel.Info`. Override per-deployment via
    /// `TOOLUP_LOG_LEVEL`.
    LogLevel: LogLevel

    /// Whitelist of trace categories
    /// the default `ConsoleLogger` (and any other `ITraceLogger`-aware
    /// implementation) will emit at the `Trace` level. Empty (the
    /// default) silences every Trace call. Populate to light up
    /// specific subsystems without
    /// recompiling: `TOOLUP_TRACE_CATEGORIES=ai.sse,platform.sse,auth`.
    TraceCategories: Set<string>

    /// SSE endpoint auth strategy.
    /// `QueryParamFallback` (default): SSE handshakes skip auth and
    /// resolve scope from the `?userId=` query param — works with
    /// HeaderAuthProvider / dev / Anonymous, but the userId is
    /// client-supplied with no cryptographic proof.
    /// `CookieRequired`: SSE handshakes go through the same auth as
    /// `/api/*` — auth provider reads the JWT from a cookie.
    /// Production recommendation; pairs with `IAuthBridge` on the
    /// client (which writes the JWT to `document.cookie`).
    /// Override per-deployment via `TOOLUP_SSE_AUTH=cookie|fallback`.
    SseAuthMode: SseAuthMode

    /// Phase 133 — whether the BFF-style server-set auth-cookie
    /// reflection endpoint (`POST` / `DELETE /api/auth/session`) is
    /// mounted. `NoAuthCookieIssuance` (default) leaves an existing
    /// deployment unchanged; `EnabledAuthCookieIssuance` mounts the
    /// endpoint so a client on `AuthTokenStorage = ServerSetHttpOnlyCookie`
    /// can move its JWT out of JS-readable storage into an
    /// `HttpOnly; Secure; SameSite=Strict` cookie. Override via
    /// `TOOLUP_AUTH_COOKIE_ISSUANCE=enabled|disabled`.
    AuthCookieIssuance: AuthCookieIssuanceMode

    /// Per-scope concurrent SSE connection cap. Each
    /// browser tab opens roughly one SSE connection per channel
    /// (`/api/notifications` + `/api/ai/events` = up to 2 per tab).
    /// Without a cap, a single misbehaving (or malicious) client can
    /// open thousands of connections and pin server memory: every
    /// SSE connection holds an open TCP/TLS socket plus per-connection
    /// state in `SSEConnectionManager`. `RateLimiting.fs` deliberately
    /// exempts SSE endpoints from the per-request limiter (per-request
    /// limits are the wrong shape for long-lived streams) — this cap
    /// is the connection-shaped equivalent.
    ///
    /// Default `Some 10` — pessimistic bound generous enough for one
    /// user with five tabs across both channels but tight enough that
    /// a runaway connection loop fails fast. Deployments expecting
    /// many concurrent connections per scope (multi-device, embedded
    /// dashboards) raise the cap; deployments with strict resource
    /// budgets lower it. `None` removes the cap entirely (legacy
    /// behaviour).
    ///
    /// On refusal, the SSE handler returns HTTP 429 with
    /// `Retry-After: 30`. The `SseTraceContributor` records the
    /// refusal in its ring buffer so `/dev/sse-trace` shows operators
    /// when scopes hit the cap.
    MaxSseConnectionsPerScope: int option

    /// Explicit opt-in to running `HeaderAuthProvider`
    /// in an authenticated `Mode`. `HeaderAuthProvider` trusts the
    /// `X-User-Id` request header at face value with no cryptographic
    /// proof, so a deployment that exposes `/api/*` directly to
    /// callers can be impersonated by any client setting that header.
    ///
    /// Default `false` — `HeaderAuthProviderModeValidator` refuses
    /// startup when an auth-requiring `Mode` is paired with
    /// `HeaderAuthProvider`. The intended production path is OIDC
    /// (`TOOLUP_AUTH_MODE=oidc + TOOLUP_OIDC_ISSUER=...`).
    ///
    /// Set `true` only for deployments behind an mTLS-terminating
    /// proxy that strips any incoming `X-User-Id` and re-injects the
    /// value it has cryptographically verified itself. The proxy is
    /// the trust boundary in that topology — the SDK trusts the
    /// header because the proxy guarantees it. Override via
    /// `TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE=1`.
    AcceptHeaderAuthWhenAuthRequired: bool

    /// Explicit opt-in to running `EncryptedSecretStore`
    /// without a master key (`TOOLUP_SECRETS_MASTER_KEY` unset) in
    /// an authenticated `Mode`. Without a master key the wrapper
    /// passes plaintext secrets through to the inner store; this is
    /// safe only when the inner store has its own at-rest encryption
    /// (cloud KMS-managed bucket, disk-level FDE).
    ///
    /// Default `false` — `EncryptedSecretStoreModeValidator` refuses
    /// startup when an auth-requiring `Mode` is paired with a
    /// no-master-key wrapper. Operators set the env var or accept
    /// the risk by flipping this flag.
    ///
    /// Override via `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE=1`.
    AcceptPlaintextSecretsWhenAuthRequired: bool

    /// Operator-declared replica count for the running
    /// process. `1` (default) when the deployment runs a single
    /// instance; raise to N when N instances of the same SDK build
    /// run behind a load balancer.
    ///
    /// `JobSchedulerInstanceValidator` reads this to refuse startup
    /// when `JobScheduler = InProcessJobScheduler` and `ReplicaCount
    /// > 1`: the in-process scheduler runs every cron job, every
    /// event-triggered job, and every webhook fan-out N times in N
    /// instances. Webhook duplicates, audit duplication, third-party
    /// API rate-limit hits — all silent otherwise.
    ///
    /// The escape hatch is configuring a distributed scheduler
    /// companion (Akka actor port). For deployments that
    /// genuinely intend to run InProcessJobScheduler in N replicas
    /// (e.g. background jobs are idempotent + cheap, deduplication
    /// happens downstream), set
    /// `AcceptInProcessSchedulerInMultiInstance = true`.
    ///
    /// Override via `TOOLUP_REPLICA_COUNT=N`.
    ReplicaCount: int

    /// Explicit opt-in to running
    /// `InProcessJobScheduler` with `ReplicaCount > 1`. Default
    /// `false` — `JobSchedulerInstanceValidator` refuses startup.
    /// Set `true` only when the deployment understands and accepts
    /// duplicate job execution. Override via
    /// `TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE=1`.
    AcceptInProcessSchedulerInMultiInstance: bool

    /// Explicit opt-in to running the in-process RAG ingestion queue
    /// with `ReplicaCount > 1`. The ingestion queue is a process-local
    /// channel with no leasing/redelivery: only the replica that
    /// handled the upload can drain it, and a crash between dequeue and
    /// completion loses that job. Default `false` —
    /// `RagIngestionInstanceValidator` refuses startup. Set `true` only
    /// when the deployment accepts that ingestion is best-effort
    /// per-instance (a distributed ingestion path is a roadmap item).
    /// Override via
    /// `TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE=1`.
    AcceptInProcessIngestionInMultiInstance: bool

    /// Explicit opt-in to running the default `InMemoryEmbeddingCache`
    /// under `Team` / `MultiTeam` mode. The cache keys on
    /// `(provider, model, dimensions, sha256(text))` with no tenant
    /// component (`EmbeddingCacheKey` in `ToolUp.Platform.IEmbeddingCache`),
    /// so two teams indexing identical document text share cache
    /// entries — fine for correctness (embeddings are deterministic
    /// for the same provider+model+text), but in a multi-instance
    /// deployment each replica's cache is independent, so retrieval
    /// is non-deterministic across replicas. Default `false` —
    /// `TeamModeSharedEmbeddingCacheValidator` emits a `Warning`. Set
    /// `true` (or `TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE=1`)
    /// to silence the warning, or wire a tenant-aware `IEmbeddingCache`
    /// override at the composition root.
    AcceptSharedEmbeddingCacheInTeamMode: bool

    /// Explicit operator attestation that, with `ReplicaCount > 1` and
    /// AI composed, cancel / client-tool-result POSTs are pinned to the
    /// replica running the agent loop (sticky-session load balancer or
    /// single-replica AI traffic). The cancel + client-tool-dispatch
    /// registries are per-process; without pinning a cancel/result that
    /// lands on the wrong replica 404s silently. Default `false` —
    /// `AICancellationDispatchInstanceValidator` refuses startup. A
    /// distributed registry is a roadmap item. Override via
    /// `TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE=1`.
    AcceptStickyRoutedAiInMultiInstance: bool

    /// Explicit opt-in to running an authenticated,
    /// HTTPS-required deployment with `RateLimit = RateLimitConfig.none`.
    /// Default `false` — `RateLimitModeValidator` emits a `Warning` (not
    /// `Error`) since legitimate deployments behind a rate-limiting
    /// proxy want no in-process limiter. Setting this flag silences the
    /// warning so the operator's `/dev/inspect` Validators panel /
    /// HealthMonitorUI Preflight tab stays clean.
    ///
    /// Override via `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1`.
    AcceptNoRateLimitWhenAuthRequired: bool

    /// Phase 21e — explicit opt-in to registering `Publishable` form
    /// schemas without an `IShareTokenStore` configured. Default
    /// `false` — `PublishableFormConfigValidator` refuses startup in
    /// persistent-data modes (`Individual` / `Team` / `MultiTeam`)
    /// because a misconfigured production deployment that booted with
    /// only a `Warning` would ship a token-less public surface (no
    /// signed-token gate, no use-limit enforcement, no revocation).
    /// Anonymous / AuthenticatedEphemeral modes always tolerate the
    /// gap — those modes are explicitly demo-shaped and the warning
    /// remains visible in `/dev/inspect`.
    ///
    /// Set `true` (or `TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE=1`) for the
    /// staging-shape-in-production-mode edge case where Publishable
    /// schemas are registered but the share-link surface is not yet
    /// wired — e.g. dry runs of a production tenancy before token
    /// issuance is enabled. The validator downgrades to `Warning` and
    /// emits an audit row "accepted unsigned publishable" so the
    /// override is traceable.
    AcceptUnsignedPublishable: bool

    /// Explicit opt-in to running an authenticated mode
    /// with `SseAuthMode = QueryParamFallback`. Default `false` —
    /// `SseAuthModeValidator` refuses startup because the fallback
    /// places the userId in the URL, which then leaks via CDN logs,
    /// web-server logs, browser history, and Referer headers.
    ///
    /// Set `true` (or `TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE=1`)
    /// for dev / CI runs of authenticated mode where the client
    /// `IAuthBridge` JWT-cookie path isn't wired up yet, or for
    /// deployments behind a proxy that strips query strings before
    /// they reach any logging surface.
    AcceptQueryParamSseAuthWhenAuthRequired: bool

    /// Phase 129d — explicit acknowledgement that a cookie-authenticated
    /// deployment (`SseAuthMode = CookieRequired`) deliberately relies on
    /// the `SameSite=Strict` cookie alone for CSRF protection, with no
    /// server-side double-submit check (`SecurityHardening =
    /// NoSecurityHardening`). Default `false` — `CsrfDefaultModeValidator`
    /// refuses startup, because `SameSite` is browser-version-dependent
    /// and subdomain-bypassable, so cookie-authenticated mutations have no
    /// portable server-side CSRF guard.
    ///
    /// Set `true` (or `TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE=1`)
    /// only when CSRF is managed out of band (a strict same-origin SPA, an
    /// upstream gateway that enforces origin checks) and the SameSite-only
    /// posture is a conscious choice. The preferred fix is to enable
    /// `withSecurityHardening` (which mounts the server-side CSRF check);
    /// this flag is the documented downgrade for deployments that cannot.
    AcceptSameSiteOnlyCsrfWhenAuthRequired: bool

    /// Explicit opt-in to running an authenticated OIDC mode
    /// (`TOOLUP_AUTH_MODE=oidc`) without an audience binding
    /// (`TOOLUP_OIDC_AUDIENCE` unset). Default `false` —
    /// `OidcAudienceBindingValidator` refuses startup because an
    /// unbound audience accepts any token the issuer minted, including
    /// tokens issued for a different application that shares the same
    /// IdP (confused-deputy / token reuse). Validating `aud` restricts
    /// the token to this application.
    ///
    /// Set `true` (or `TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE=1`)
    /// for dev / CI runs against a single-app issuer where no other
    /// relying party shares the issuer, or where the audience claim is
    /// not yet provisioned on the IdP side.
    AcceptUnboundAudienceWhenAuthRequired: bool

    /// Explicit opt-in to running the in-memory `IOAuthStateStore`
    /// (the SDK default) with `ReplicaCount > 1`. The in-memory store
    /// keeps OAuth CSRF/PKCE state in a process-local dictionary, so a
    /// provider redirect that lands on a different replica than the one
    /// that issued the `state` cannot find it — the callback fails with
    /// a state-mismatch and the connector authorisation never completes.
    ///
    /// Default `false` — `OAuthStateStoreInstanceValidator` refuses
    /// startup. The escape hatch is a distributed `IOAuthStateStore`
    /// companion (the Phase 9c half-2 Redis-backed port) or a
    /// sticky-session load balancer; set `true` (or
    /// `TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE=1`) only when
    /// the deployment pins OAuth-flow traffic to one replica.
    AcceptInMemoryOAuthStateInMultiInstance: bool

    /// Phase 136 part 2 — explicit operator opt-in to running the
    /// in-memory `IShareTokenRateLimiter`
    /// (`InMemoryShareTokenRateLimiter`, the SDK default) in a
    /// scale-out-shaped deployment. The in-memory limiter keeps each
    /// per-token sliding window in a process-local dictionary, so with
    /// `ReplicaCount > 1` a leaked share-token's per-window admission
    /// cap is silently multiplied by the replica count
    /// (`N × MaxUses`) — the operator configured a rate limit and gets a
    /// weaker one than declared.
    ///
    /// Default `false` — `ShareTokenRateLimiterDistributionValidator`
    /// refuses startup when `ReplicaCount > 1`. The fix is wiring a
    /// distributed companion (Redis / `IRateLimitStore`-backed, Phase
    /// 56) via `FormsServerApp.withShareTokenRateLimiter`. Set `true`
    /// (or `TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE=1`)
    /// only when the deployment pins share-token traffic to one replica,
    /// or knowingly accepts the `N × MaxUses` burst (the absolute
    /// persisted `UseLimit` cap still holds). The validator downgrades
    /// to clean and the override is visible in the preflight output.
    AcceptInMemoryShareTokenRateLimiterInMultiInstance: bool

    /// Explicit operator opt-in for running `PendingInviteStore`
    /// (the email-keyed pre-invite blob backing
    /// `ITeamInviteApi.IssuePendingInviteByEmail`) in a multi-replica
    /// deployment. The store serialises writes via a process-local
    /// `SemaphoreSlim` + full-blob overwrite; two replicas writing
    /// concurrently silently lose updates, and a 30-second
    /// per-process read cache can serve stale entries that a peer
    /// already consumed (double auto-join).
    ///
    /// Default `false` — `PendingInviteStoreInstanceValidator` emits
    /// `Warning` (not Error — the link-based invitation flow is
    /// unaffected; only the IssuePendingInviteByEmail surface
    /// silently corrupts). The escape hatch is ETag-based optimistic
    /// concurrency on `IBlobStorage.Upload` (the Phase 9c half-2
    /// follow-up); set `true` (or
    /// `TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE=1`) only
    /// when the deployment knows the risk is acceptable.
    AcceptPendingInviteStoreInMultiInstance: bool

    /// Explicit operator opt-in to running the team invite-by-email
    /// surface (`ITeamInviteApi.IssuePendingInviteByEmail`) without an
    /// `IUserDirectory` companion wired. Without a directory the pending
    /// invite is still recorded (the invitee auto-joins on next sign-in)
    /// but the invitation email is never sent and the recipient typeahead
    /// degrades to a free-text box — both silently.
    ///
    /// Default `false` — `InviteEmailCapabilityValidator` emits a
    /// `Warning` (not Error — the auto-join path is unaffected; only the
    /// notification is missing) when a team-scoped, auth-requiring
    /// deployment mounts the surface with no directory. Set `true` (or
    /// `TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY=1`) to acknowledge
    /// the "operator tells the invitee out of band" posture and silence
    /// the warning.
    AcceptInviteByEmailWithoutDirectory: bool

    /// Eviction TTL for ephemeral session stores
    /// (Anonymous + AuthenticatedEphemeral modes). Default 60 minutes.
    /// `AuthenticatedEphemeral` deployments supporting trial-account
    /// workflows that exceed 60 minutes raise this; demos with
    /// faster-recycling sessions lower it.
    ///
    /// Override via `TOOLUP_STORE_EVICTION_MINUTES=N`. Persistent
    /// modes (`Individual`, `Team`, `MultiTeam`) are unaffected —
    /// their stores never evict.
    EphemeralStoreEvictionMinutes: float

    /// Data-subject-request substrate opt-in. `Disabled`
    /// (the default) wires no DSR endpoints, no admin module, no
    /// erasure orchestrator — apps that don't carry GDPR / CCPA /
    /// DPDPA exposure pay nothing. `Enabled policy` activates the
    /// `IDataSubjectRequestApi` endpoint and the `ErasurePipeline`
    /// orchestrator with the chosen `ErasurePolicy` as the default
    /// for inbound erasure requests (per-request override available
    /// via the API).
    ///
    /// The deploying organisation chooses the policy and accepts
    /// liability for the choice — the SDK provides the tools but
    /// not legal counsel.
    DataSubjectRequests: DataSubjectRequestMode
    /// Phase 9q — startup-time config-drift detection.
    /// `NoConfigDriftDetection` (default, GP 13) — no snapshot
    /// written, no comparison performed. `EnabledConfigDriftDetection`
    /// snapshots the resolved `ServerConfig` (secrets redacted) plus
    /// a hash of the active companion-assembly set to
    /// `_platform/_deploy/last-config.json` at the end of `compose`,
    /// and on subsequent restarts compares the persisted snapshot
    /// against the new one. Differences emit a `Warn` log + a
    /// `ConfigDrift` audit event under `_platform.audit`. Pure
    /// observation — no abort, no rollback.
    ConfigDriftDetection: ConfigDriftDetectionMode
    /// Phase 9v — outbound rate-limiter selection. `NoRateLimiter`
    /// (default, GP 13) resolves `IRateLimiter` to a pass-through;
    /// `EnabledRateLimiter` activates the SDK-shipped sliding-window
    /// default. Companions register their per-provider quotas via
    /// `ServerApp.withRateLimitDescriptor`. Distinct from `RateLimit`
    /// (inbound HTTP) above.
    RateLimiter: RateLimiterMode
    /// Phase 9v — threshold above which an outbound `IRateLimiter.Wait`
    /// call qualifies for a `RateLimitWaited` audit row. Sub-threshold
    /// waits emit metrics (`toolup.ratelimit.waited_total`,
    /// `toolup.ratelimit.wait_ms`) but stay silent in the audit trail
    /// to keep the trail focused on material stalls. Default
    /// `TimeSpan.FromSeconds 5.0`; ignored when
    /// `RateLimiter = NoRateLimiter`.
    SlowRateLimitThreshold: TimeSpan
    /// Phase 9o — post-deploy smoke-test endpoint. `NoSmokeTest`
    /// (default, GP 13) leaves `/api/_internal/smoke` unmounted and
    /// registers no first-party smoke tests. `EnabledSmokeTest`
    /// mounts the route behind the `TOOLUP_SMOKE_TOKEN` env-var gate
    /// and registers first-party smoke tests against the SDK's wired
    /// substrate. Companion-contributed smoke tests register
    /// alongside via `services.AddSingleton<ISmokeTest>(...)` or
    /// `ServerApp.withSmokeTest`.
    SmokeTest: SmokeTestMode
    /// Phase 53 — `IConversationStore` substrate opt-in. Default
    /// `NoConversationStore` (GP 13) leaves `IConversationStore`
    /// unregistered; `AIAssistantHandler` resolves it as `null` and
    /// runs its pre-Phase-53 ephemeral path verbatim. Setting
    /// `EnabledConversationStore { RetentionDays = N }` registers
    /// `PersistentConversationStore` (built on `IDataObjectStore`)
    /// + the `ConversationEraseHandler` (DSR contributor) + the
    /// five `_platform.conversations.*` audit cases. See
    /// `ConversationStoreMode` for the retention-pruning caveat.
    ConversationStore: ConversationStoreMode

    /// Phase 38 — `IPublicContentApi` substrate opt-in. Default
    /// `NoPublicRendering` (GP 13) strips the entire public-
    /// rendering surface (no `/sitemap.xml`, no markdown-watcher
    /// hosted service, no redirect middleware, no catch-all page
    /// handler). `EnabledPublicRendering root` brings up the
    /// `ToolUp.PublicRendering` companion's loader + handlers
    /// against the supplied `ContentRoot`. See
    /// `PublicRenderingMode` for the strip-imports contract.
    PublicRendering: PublicRenderingMode

    /// Phase 39 — `IAssetStore` substrate opt-in. Default
    /// `NoAssetStore` (GP 13) strips the entire asset-store
    /// surface (no `/api/assets/*` handlers, no `IAssetStore` DI
    /// singleton, no audit emission). `EnabledAssetStore` brings
    /// up the `ToolUp.AssetStore` companion's `DefaultAssetStore`
    /// (over the configured `IBlobStorage`), the Fable.Remoting
    /// `IAssetApi` handler, the multipart upload endpoint, and
    /// audit emission of `AssetUploaded` / `AssetDeleted`. See
    /// `AssetStoreMode` for the strip-imports contract.
    AssetStore: AssetStoreMode

    /// Phase 88 — `IMediaLibrary` substrate opt-in (video / audio
    /// hosting). Default `NoMediaLibrary` (GP 13) strips the entire
    /// media surface (no `/api/media/*` or `/media/*` handlers, no
    /// `IMediaLibrary` DI singleton, no URL-signing key, no range
    /// endpoint). `EnabledMediaLibrary` brings up the
    /// `ToolUp.MediaLibrary` companion's `DefaultMediaLibrary` (over
    /// the configured `IBlobStorage`), the `206`-range-serving
    /// endpoint, scope-signed expiring URLs, and the Fable.Remoting
    /// `IMediaApi` handler. See `MediaLibraryMode` for the strip-
    /// imports contract.
    MediaLibrary: MediaLibraryMode

    /// Phase 26 — Layer 3 deploy-plane substrate opt-in. Default
    /// `NoDeployPlane` (GP 13) registers nothing. `SingleNodeDeployPlane`
    /// brings up `IBuildOrchestrator` / `IDeployPipeline` /
    /// `ITenantFleet` over the SDK-shipped single-node defaults plus
    /// the `Tenant` entity registration. `IContainerScheduler` is
    /// consumer-supplied — register a companion separately via DI
    /// (`DockerLocalContainerScheduler` is the dev-grade reference
    /// impl). See `DeployPlaneMode` for the contract.
    DeployPlane: DeployPlaneMode

    /// Phase 16 — host-model selection. `KestrelHost` (default)
    /// runs every registered `BackgroundService`. `ServerlessHost`
    /// gates compose to skip `BackgroundService` registrations so
    /// the deployment can run under a serverless host adapter
    /// (Azure Functions / AWS Lambda / Google Cloud Functions).
    /// Pair with `JobScheduler = NoJobScheduler`, `Webhooks =
    /// NoWebhooks`, `Notifications = NoNotificationsExplicit` for
    /// a clean serverless shape.
    ServerlessHost: ServerlessHostMode

    /// Phase 16a — process-profile selection. `AllInOne`
    /// (default) is today's behaviour — web tier + every
    /// `BackgroundService` in one binary. `WebOnly` /
    /// `WorkerOnly` / `DispatcherOnly` activate documented
    /// subsets of `IHostedService` registrations so deployments
    /// can horizontally scale per role against a shared
    /// persistence tier.
    ProcessProfile: ProcessProfile

    /// Phase 56 — `IRateLimitStore` substrate opt-in. Default
    /// `NoRateLimitStore` (GP 13) strips the
    /// `RateLimitMiddleware`. `InMemoryRateLimitStore` activates
    /// the single-instance default; external-store variants
    /// (Redis, Azure Table Storage, DynamoDB, Cosmos) ship as
    /// sub-companion packages that register against
    /// `ExternalRateLimitStore`. Distinct from `RateLimit`
    /// (legacy fixed-window team-keyed limiter) — this substrate
    /// supports route-keyed per-IP policies that compose with
    /// `ServerlessHost` and `ProcessProfile = WebOnly`.
    RateLimitStore: RateLimitStoreMode

    /// Phase 56 — declared rate-limit policies. Each `RouteLimit`
    /// names a route prefix, key-extractor, window, and
    /// threshold. Empty (default) = no rate-limit policies
    /// declared. When non-empty, `RateLimitStore` must be set;
    /// `RateLimitStoreDepsValidator` refuses startup otherwise.
    /// `RouteLimit` shape declared above this record.
    RateLimits: RouteLimit list

    /// Phase 59 — server-side consent-audit opt-in. Default
    /// `NoConsentAudit` strips the
    /// `/api/_platform/consent-audit` endpoint. Client-side
    /// consent state still works via `IConsentProvider`.
    ConsentAudit: ConsentAuditMode

    /// Phase 159 — server-side durable per-subject consent-state
    /// store opt-in. Default `NoConsentStateStore` registers nothing
    /// (GP 13). `EntityBackedConsentStateStore` registers the durable
    /// store over `IEntityStore`; `InMemoryConsentStateStore` is the
    /// dev-only single-instance store.
    ConsentStateStore: ConsentStateStoreMode

    /// Phase 60 — server-side ad-analytics opt-in. Default
    /// `NoAdAnalytics` strips the
    /// `/api/_platform/ads/analytics` endpoint.
    /// `EnabledAdAnalytics` lands `AdImpression` / `AdClick`
    /// audit events via `IAuditLog`.
    AdAnalytics: AdAnalyticsMode

    /// Phase 5f — who may call `TeamApi.CreateTeam` on a `Team`
    /// / `MultiTeam` deployment. Default `PlatformAdminOnly`
    /// gates team creation on `IPlatformAdminStore.IsAdmin` so
    /// closed-roster deployments don't have to remember to add
    /// the check. Set
    /// `{ ServerConfig.defaults with TeamCreationPolicy = AnyAuthenticatedUser }`
    /// to preserve the pre-5f shape (any authenticated user can
    /// create + auto-Own a team). Inert in modes that don't
    /// register `ITeamStore` (Anonymous / AuthenticatedEphemeral
    /// / Individual) — `CreateTeam` already returns `Error "Team
    /// management not available in this mode"` there.
    TeamCreationPolicy: TeamCreationPolicy
    /// Opt-in cap on how many teams a single non-admin user may own under
    /// `TeamCreationPolicy = AnyAuthenticatedUser`. `None` (default) =
    /// unlimited (pre-228 behaviour, byte-for-byte). `Some n` rejects a
    /// create once the caller already owns `n` teams (audited as
    /// `TeamCreationDenied`). Platform Admins are never limited; inert
    /// under `PlatformAdminOnly` (admins provision freely) and in modes
    /// that don't register `ITeamStore`.
    TeamCreationQuota: int option
    /// Per-scope retention policy for the registered `INarrativeStore`.
    /// Default `NarrativeRetentionPolicy.defaults` keeps the historical
    /// 100-per-scope cap with no age limit; deployments with long-lived
    /// scopes that want bounded storage set `MaxAge` to evict stale
    /// narratives lazily on subsequent writes. The in-process stores
    /// honour both knobs; external implementations may use the policy as
    /// guidance or layer their own retention.
    NarrativeRetention: NarrativeRetentionPolicy
    /// Inter-platform peer substrate selection (Phase 18). Default:
    /// `NoPeerSubstrate` — no `/peer/v1/{contractId}` route, no peer
    /// interfaces in DI, no peer audit emission. Enable with
    /// `EnabledPeerSubstrate` to host typed cross-deployment contracts
    /// over JSON-RPC 2.0 with identity propagation, version handshake,
    /// and job-substrate fusion. Zero cost when not enabled (GP 13).
    PeerSubstrate: PeerSubstrateMode
    /// Phase 54 — tenant-lifecycle substrate selection. Default
    /// `NoTenantLifecycle` — no `/api/_platform/tenants/*` route, no
    /// first-party `ITenantLifecycle` hooks in DI, no tenant-lifecycle
    /// audit emission. Enable with `EnabledTenantLifecycle` to drive
    /// tenant provision / offboard choreography through one operator
    /// call with per-hook isolation + audit. Zero cost when not enabled
    /// (GP 13).
    TenantLifecycle: TenantLifecycleMode
    /// Phase 54i — confirmation gate in front of the destructive tenant
    /// offboard. Default `NoConfirmation` preserves Phase 54's one-call
    /// behaviour byte-for-byte (GP 11). `TokenConfirmation` requires a
    /// short-lived `RequestDeprovisionToken` before any token-less
    /// destructive path runs; `TwoPersonRule` additionally requires the
    /// redeeming admin to differ from the requester. Only consulted when
    /// `TenantLifecycle = EnabledTenantLifecycle`; the token modes need an
    /// `IShareTokenStore` composed (the Phase 21b share-token substrate) —
    /// if absent, `RequestDeprovisionToken` / `DeprovisionTenantConfirmed`
    /// return a clear "requires an IShareTokenStore" error. Zero cost when
    /// not enabled (GP 13).
    TenantOffboardConfirmation: OffboardConfirmationMode
    /// Phase 177 — opt-in deployment-readiness scorecard. Default
    /// `NoReadinessReport` (GP 11/13) leaves the
    /// `IDeploymentReadinessApi` route unmounted (the surface 404s, the
    /// deployment is byte-for-byte unchanged). `EnabledReadinessReport`
    /// mounts the Platform-Admin-gated read that consolidates the
    /// `IConfigValidator` / `ISmokeTest` / `ConfigDrift` / `IHealthCheck`
    /// signals into one go/no-go verdict. Set via
    /// `ServerApp.withDeploymentReadiness`. Pure projection over existing
    /// signals — zero cost when not enabled.
    DeploymentReadiness: DeploymentReadinessMode
    /// Phase 179 — the locales this deployment declares support for.
    /// Default `[ LocaleCode.en ]`. Read by the translation-coverage
    /// gate (`I18nCoverage.validator`) when `I18nCoverageMode` is on, so
    /// the SDK's own `sdk.*` + `ApiError` keys (and every module's
    /// registered translations) are checked against exactly these
    /// locales. A single-locale deployment leaves the default; a
    /// French-serving deployment declares `[ LocaleCode.en; LocaleCode.fr ]`.
    RegisteredLocales: LocaleCode list
    /// Phase 179 — translation-coverage-gate policy. Default
    /// `NoCoverageCheck` (GP 11/13) — no gate, byte-for-byte unchanged.
    /// `WarnOnMissing` logs a `Warn` per missing (key, locale) and
    /// continues; `FailOnMissing` joins the `IConfigValidator` preflight
    /// and aborts startup, naming the missing key + locale. Checked
    /// against `RegisteredLocales`.
    I18nCoverageMode: I18nCoverageMode
}

// ─── Phase 11.G — curated app-supplied overrides for `ServerConfig.fromEnv` ──

/// Curated app-supplied overrides that compose on top of
/// `ServerConfig.fromEnv`'s env-var-derived baseline. Each `Some`
/// wins over the env-derived value; each `None` lets `fromEnv`
/// use the env default. Reference-app posture knobs (webhooks,
/// audit log, default hardening) are pre-bundled in
/// `ServerConfigOverrides.referenceApp`.
type ServerConfigOverrides = {
    /// Override `ServerConfig.PublicPath`. Default
    /// `"deploy/public"` (per `ServerConfig.defaults`); reference
    /// apps typically set `"public"`.
    PublicPath: string option
    /// Phase 66 Stream A.2 — override `ServerConfig.Surfaces`.
    /// Reference posture: `Some Surfaces.individual` (matches the
    /// retiring `Mode = Individual` reference default). Consumers
    /// declaring mixed-mode override per their deployment.
    Surfaces: SurfaceProfile list option
    /// Override `ServerConfig.Webhooks`. Reference posture:
    /// `EnabledWebhooks`.
    Webhooks: WebhookMode option
    /// Override `ServerConfig.AuditLog`. Reference posture:
    /// `EnabledAuditLog`.
    AuditLog: AuditLogMode option
    /// Override `ServerConfig.SecurityHardening`. Reference
    /// posture: `DefaultSecurityHardening`.
    SecurityHardening: SecurityHardeningMode option
    /// Override `ServerConfig.SlowRequestThresholdOverrides` with
    /// app-supplied per-route ceilings (KB upload, AI inference,
    /// large file paths). Reference posture supplies its own map.
    SlowRequestThresholdOverrides: Map<string, TimeSpan> option
    /// Override `ServerConfig.EnableDevEndpoints`. Reference
    /// posture: `Some true` under `#if DEBUG`, `Some false`
    /// otherwise.
    EnableDevEndpoints: bool option
    /// Override `ServerConfig.AutoBootstrapDevAdmin`. Reference
    /// posture: `Some "dev-admin"` under `#if DEBUG`, omit
    /// otherwise.
    AutoBootstrapDevAdmin: string option
    /// Override `ServerConfig.IncludePlatformDefaults`. Consumers
    /// whose modules render no monetary values set `Some false`
    /// to drop the irrelevant `Platform Defaults` admin tab.
    IncludePlatformDefaults: bool option
    /// Override `ServerConfig.ShareTokenStore`. `None` (default) leaves
    /// the resolved value at `ServerConfig.defaults.ShareTokenStore`
    /// (`NoShareTokenStore`); deployments that issue signed share-links
    /// — publishable forms, magic-login links, public dashboards, or the
    /// auto-mounted `ITeamInviteApi` (whose impl hard-depends on
    /// `IShareTokenStore`) — set `Some EnabledShareTokenStore` here
    /// rather than patching the resolved `ServerConfig` record after
    /// `fromEnv`. The compose-time `ClaimBearer`-surface auto-promotion
    /// (`ComposeNotifications`) still applies on top of whatever this
    /// resolves to.
    ShareTokenStore: ShareTokenStoreMode option
}

module ServerConfigOverrides =
    let empty: ServerConfigOverrides = {
        PublicPath = None
        Surfaces = None
        Webhooks = None
        AuditLog = None
        SecurityHardening = None
        SlowRequestThresholdOverrides = None
        EnableDevEndpoints = None
        AutoBootstrapDevAdmin = None
        IncludePlatformDefaults = None
        ShareTokenStore = None
    }

    /// Reference-deployment posture — webhooks on, audit on,
    /// default security hardening on, single-shape Individual
    /// surface. Matches the reference composition root's bundled
    /// feature set.
    let referenceApp: ServerConfigOverrides = {
        empty with
            Surfaces = Some Surfaces.individual
            Webhooks = Some EnabledWebhooks
            AuditLog = Some EnabledAuditLog
            SecurityHardening = Some DefaultSecurityHardening
    }

/// Canonical predicates over the per-deployment `Surfaces` list.
/// Validators / handlers / composition-root branches consult these
/// instead of pattern-matching directly on `config.Surfaces`.
module DeploymentConfig =
    /// True iff any declared surface requires the request to carry
    /// authenticated credentials. Anonymous is the only surface
    /// shape that admits unauthenticated requests; every other
    /// shape derives an authenticated `Subject`. A mixed-mode
    /// deployment containing both an `Anonymous` profile AND any
    /// authenticated profile still admits authenticated routes, so
    /// the predicate is "any non-Anonymous surface present".
    let requiresAnyAuth (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> false
            | _ -> true)

    /// True when the deployment is reachable over the public internet —
    /// either it enforces HTTPS itself (`RequireHttps`) or it trusts a
    /// TLS-terminating proxy's forwarded headers (`TrustForwardedHeaders`).
    /// The broad "is this exposed?" signal; the rate-limit and
    /// security-headers preflights use it. Named here (not recomputed at
    /// each call site) so the four startup validators that reason about
    /// internet exposure pick an *intent* rather than re-deriving a
    /// boolean that could silently drift apart — cf. the deliberately
    /// stricter `isHttpsTerminatedHere`.
    let isInternetFacing (config: ServerConfig) : bool =
        config.RequireHttps || config.TrustForwardedHeaders

    /// The stricter "HTTPS is explicitly enforced at this layer"
    /// (`RequireHttps`) signal — deliberately NOT `isInternetFacing`. The
    /// auto-bootstrap-dev-admin and max-request-body preflights use this
    /// narrower check because `TrustForwardedHeaders` defaults to `true`
    /// (Phase 16d) and would otherwise flag a plain local-dev shell as
    /// internet-facing. Centralised so the intended divergence from
    /// `isInternetFacing` is explicit, not buried in per-validator
    /// comments.
    let isHttpsTerminatedHere (config: ServerConfig) : bool = config.RequireHttps

    /// True iff the deployment supports the `Team` subject shape
    /// (single-team or multi-team UX). Used by team-store wiring +
    /// team-CRUD validators.
    let hasTeamScope (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Team _ -> true
            | _ -> false)

    /// True iff the deployment supports multi-team switching — any
    /// `Team` surface whose `Switching = HeaderSwitcher`. Used by
    /// `NotificationMode.resolve`: membership-change events feed the
    /// client team-switch reset path, which only exists when the
    /// switcher is present.
    let hasMultiTeamSwitcher (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Team { Switching = HeaderSwitcher } -> true
            | _ -> false)

    /// True iff the deployment supports the `ClaimBearer` subject
    /// shape. Used by `IShareTokenStore` auto-promotion + decorator
    /// coherence checks.
    let hasClaimBearer (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.ClaimBearer _ -> true
            | _ -> false)

    /// True iff the deployment supports the `Anonymous` subject
    /// shape. The complement of "auth-required everywhere".
    let hasAnonymous (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> true
            | _ -> false)

    /// True iff at least one surface in the deployment carries
    /// persistent authenticated storage — any `AuthenticatedUser`
    /// with `Persistence = Persistent` (the canonical Individual
    /// shape) or any `Team` profile (`Team` scope is persistent by
    /// design). The complement is "deployment is ephemeral / public-
    /// only by design". Used by validators that escalate severity
    /// when persistent data would be at stake under the
    /// configuration being checked (publishable-form unsigned-token
    /// gap, RAG vector-store durability, etc.).
    let hasPersistentAuthenticatedStorage (config: ServerConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.AuthenticatedUser { Persistence = Persistent } -> true
            | SurfaceProfile.Team _ -> true
            | _ -> false)

    /// One-line label for diagnostic / error messages naming the
    /// deployment shape. Single-surface deployments produce a single
    /// name (`"Individual"` / `"Team"` / `"MultiTeam"` /
    /// `"AuthenticatedEphemeral"` / `"Anonymous"`); mixed-mode
    /// deployments produce a `+`-joined list (e.g.
    /// `"Anonymous + Individual"`).
    let surfacesLabel (config: ServerConfig) : string =
        let labelOne =
            function
            | SurfaceProfile.Anonymous _ -> "Anonymous"
            | SurfaceProfile.AuthenticatedUser { Persistence = Persistent } -> "Individual"
            | SurfaceProfile.AuthenticatedUser { Persistence = Ephemeral } -> "AuthenticatedEphemeral"
            | SurfaceProfile.Team { Switching = NoSwitcher } -> "Team"
            | SurfaceProfile.Team { Switching = HeaderSwitcher } -> "MultiTeam"
            | SurfaceProfile.ClaimBearer _ -> "ClaimBearer"

        config.Surfaces |> List.map labelOne |> String.concat " + "

module ServerConfig =
    let defaults = {
        Port = 5000
        PublicPath = "deploy/public"
        Surfaces = Surfaces.anonymous
        ModuleNames = []
        EventStore = InMemoryOnly
        PlatformKnowledgeBase = NoPlatformKnowledgeBase
        AutoBootstrapDevAdmin = None
        ModuleConfigs = []
        IncludePlatformDefaults = true
        FeatureFlags = []
        ModuleFilter = None
        ModuleBindingTrust = ModuleBindingTrustConfig.defaults
        RequireHttps = false
        TrustForwardedHeaders = true
        TrustedProxyCidrs = []
        AcceptForwardedHeadersFromAnyProxy = false
        StaticPathBehaviour = Warn
        SlowRequestThreshold = TimeSpan.FromSeconds 1.0
        SlowRequestThresholdOverrides = Map.empty
        DefaultTeamStorageQuotaBytes = None
        RateLimit = RateLimitConfig.none
        ResultStore = NoResultStore
        Lineage = NoLineageStore
        JobScheduler = NoJobScheduler
        BackfillMissedTicks = false
        ShareTokenStore = NoShareTokenStore
        PeerRoutePrefixes = []
        MaxRequestBodyBytes = None
        WebhookUrlAllowedHosts = []
        PublicBaseUrl = None
        DataIngestion = NoDataIngestion
        ColumnMapping = NoColumnMapping
        MappingDryRun = WarnOnValidationFailure
        OAuthRefresher = NoOAuthRefresher
        EntityStore = NoEntityStore
        // Phase 68 — the in-memory graph store is the default (registered
        // lazily; zero cost until a graph API is resolved — GP 13).
        GraphStore = InMemoryGraphStore
        TimeSeriesStore = NoTimeSeriesStore
        TelemetrySink = NoTelemetrySink
        UsageMetering = NoUsageMetering
        MetricsEndpoint = NoMetricsEndpoint
        MetricsSink = MetricsSinkConfig.defaults
        Webhooks = NoWebhooks
        AuditLog = NoAuditLog
        AuditSamplingPolicy = AuditSamplingPolicy.none
        Notifications = NotificationsAuto
        SecurityHeaders = Map.empty
        SecurityHardening = NoSecurityHardening
        Cors = None
        EnableDevEndpoints = false
        EnableCitationDevEndpoint = None
        SkipPreflight = false
        HealthStateTracking = false
        AlertRules = AlertRule.none
        LogLevel = LogLevel.Info
        TraceCategories = Set.empty
        SseAuthMode = QueryParamFallback
        AuthCookieIssuance = NoAuthCookieIssuance
        AcceptHeaderAuthWhenAuthRequired = false
        AcceptPlaintextSecretsWhenAuthRequired = false
        ReplicaCount = 1
        AcceptInProcessSchedulerInMultiInstance = false
        AcceptInProcessIngestionInMultiInstance = false
        AcceptSharedEmbeddingCacheInTeamMode = false
        AcceptStickyRoutedAiInMultiInstance = false
        AcceptNoRateLimitWhenAuthRequired = false
        AcceptUnsignedPublishable = false
        AcceptQueryParamSseAuthWhenAuthRequired = false
        AcceptSameSiteOnlyCsrfWhenAuthRequired = false
        AcceptUnboundAudienceWhenAuthRequired = false
        AcceptInMemoryOAuthStateInMultiInstance = false
        AcceptInMemoryShareTokenRateLimiterInMultiInstance = false
        AcceptPendingInviteStoreInMultiInstance = false
        AcceptInviteByEmailWithoutDirectory = false
        EphemeralStoreEvictionMinutes = 60.0
        MaxSseConnectionsPerScope = Some 10
        DataSubjectRequests = DataSubjectRequestMode.Disabled
        ConfigDriftDetection = NoConfigDriftDetection
        RateLimiter = NoRateLimiter
        SlowRateLimitThreshold = TimeSpan.FromSeconds 5.0
        SmokeTest = NoSmokeTest
        ConversationStore = NoConversationStore
        PublicRendering = NoPublicRendering
        AssetStore = NoAssetStore
        MediaLibrary = NoMediaLibrary
        DeployPlane = NoDeployPlane
        ServerlessHost = KestrelHost
        ProcessProfile = AllInOne
        RateLimitStore = NoRateLimitStore
        RateLimits = []
        ConsentAudit = NoConsentAudit
        ConsentStateStore = NoConsentStateStore
        AdAnalytics = NoAdAnalytics
        TeamCreationPolicy = PlatformAdminOnly
        TeamCreationQuota = None
        NarrativeRetention = NarrativeRetentionPolicy.defaults
        PeerSubstrate = NoPeerSubstrate
        TenantLifecycle = NoTenantLifecycle
        TenantOffboardConfirmation = NoConfirmation
        DeploymentReadiness = NoReadinessReport
        RegisteredLocales = [ LocaleCode.en ]
        I18nCoverageMode = NoCoverageCheck
    }

// ─── Phase 11.G — env-var-driven config construction ──────────
//
// Server-only. Fable can't compile `System.Environment.GetEnvironmentVariable`;
// wrapping in `#if !FABLE_COMPILER` keeps the helpers usable from
// server composition roots while leaving the Fable client to see
// only the `ServerConfig` type + `defaults` value above.

#if !FABLE_COMPILER
    let private envVar (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> None
        | v -> Some v

    let private envFlag (name: string) =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | Some("1" | "true" | "yes" | "on") -> true
        | _ -> false

    /// Phase 16d — parse a boolean env var with an explicit
    /// `defaultWhenMissing` and **fail loud** on any unrecognised
    /// value. Used for flags whose silently-wrong values are dangerous
    /// (forwarded-headers trust silently off in a containerised deploy
    /// misreports client IPs and breaks HTTPS redirects). Missing →
    /// `defaultWhenMissing`. Recognised: `1` / `true` / `yes` / `on`
    /// → `true`; `0` / `false` / `no` / `off` → `false` (all case-
    /// insensitive). Any other value throws at startup, mirroring the
    /// `SERVER_PORT` fail-fast pattern in `SDK.Server.compose` — names
    /// the offending value and points at the recognised set.
    let private envFlagOrFail (name: string) (defaultWhenMissing: bool) =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> defaultWhenMissing
        | Some("1" | "true" | "yes" | "on") -> true
        | Some("0" | "false" | "no" | "off") -> false
        | Some other ->
            failwithf
                "%s=%s is not a recognised boolean value. Expected one of: 1, true, yes, on (case-insensitive) → on; 0, false, no, off → off. Unset the variable to use the default (%b)."
                name
                other
                defaultWhenMissing

    /// Phase 66 Stream A.8 — parse a single token from
    /// `TOOLUP_PLATFORM_SURFACES` into a `SurfaceProfile`. Accepts
    /// the canonical lowercase form plus a couple of separator
    /// tolerant aliases (`multi-team` / `multi_team` / `multiteam`).
    /// Returns `Error <raw>` for unrecognised tokens so the caller
    /// can surface a clear list of bad entries.
    let private parseSurfaceProfile (raw: string) : Result<SurfaceProfile, string> =
        match raw with
        | "anonymous" -> Ok SurfaceProfile.anonymous
        | "anonymous_persistent"
        | "anonymous-persistent"
        | "anonymouspersistent" -> Ok SurfaceProfile.anonymousPersistent
        | "trial"
        | "authephemeral"
        | "auth-ephemeral"
        | "auth_ephemeral" -> Ok SurfaceProfile.trial
        | "individual" -> Ok SurfaceProfile.individual
        | "team" -> Ok SurfaceProfile.team
        | "multiteam"
        | "multi-team"
        | "multi_team" -> Ok SurfaceProfile.multiTeam
        | "claimbearer"
        | "claim-bearer"
        | "claim_bearer" -> Ok SurfaceProfile.claimBearer
        | other -> Error other

    let private parseStaticPathBehaviour (logger: ILogger) =
        match envVar "TOOLUP_STATIC_PATH_BEHAVIOUR" |> Option.map _.ToLowerInvariant() with
        | Some "warn"
        | None -> Warn
        | Some("require" | "requireexist" | "require-exist") -> RequireExist
        | Some("skip" | "skipsilent" | "skip-silent") -> SkipSilent
        | Some other ->
            logger.Warn
                $"TOOLUP_STATIC_PATH_BEHAVIOUR={other} not recognised. Valid: warn, require, skip. Falling back to Warn."

            Warn

    let private parseSseAuthMode (logger: ILogger) =
        match envVar "TOOLUP_SSE_AUTH" |> Option.map _.ToLowerInvariant() with
        | Some "cookie" -> CookieRequired
        | Some "fallback"
        | Some "queryparam"
        | None -> QueryParamFallback
        | Some other ->
            logger.Warn
                $"TOOLUP_SSE_AUTH={other} not recognised. Valid values: cookie, fallback. Falling back to fallback (default)."

            QueryParamFallback

    let private parseAuthCookieIssuance (logger: ILogger) =
        match envVar "TOOLUP_AUTH_COOKIE_ISSUANCE" |> Option.map _.ToLowerInvariant() with
        | Some "enabled"
        | Some "on"
        | Some "1" -> EnabledAuthCookieIssuance
        | Some "disabled"
        | Some "off"
        | Some "0"
        | None -> NoAuthCookieIssuance
        | Some other ->
            logger.Warn
                $"TOOLUP_AUTH_COOKIE_ISSUANCE={other} not recognised. Valid values: enabled, disabled. Falling back to disabled (default)."

            NoAuthCookieIssuance

    let private parseReplicaCount (logger: ILogger) =
        match envVar "TOOLUP_REPLICA_COUNT" with
        | None -> 1
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> n
            | _ ->
                logger.Warn $"TOOLUP_REPLICA_COUNT={raw} not a positive integer. Defaulting to 1."
                1

    let private parseEphemeralStoreEvictionMinutes (logger: ILogger) =
        match envVar "TOOLUP_STORE_EVICTION_MINUTES" with
        | None -> defaults.EphemeralStoreEvictionMinutes
        | Some raw ->
            match Double.TryParse raw with
            | true, n when n > 0.0 -> n
            | _ ->
                logger.Warn $"TOOLUP_STORE_EVICTION_MINUTES={raw} not a positive number. Using default 60."
                defaults.EphemeralStoreEvictionMinutes

    let private parseRateLimit (logger: ILogger) =
        let parsePositive (name: string) =
            match envVar name with
            | None -> None
            | Some raw ->
                match Int32.TryParse raw with
                | true, n when n > 0 -> Some n
                | _ ->
                    logger.Warn $"{name}={raw} not a positive integer. Rate limit disabled."
                    None

        // Phase 66 Stream C.3 — the env-var path configures a single
        // uniform policy (one limit for every subject kind). Per-shape
        // overrides are a code-level concern (`RateLimitConfig.perShape`
        // / `.withOverrides`), not expressible via three scalar env vars.
        match
            parsePositive "TOOLUP_RATE_LIMIT_PERMITS",
            parsePositive "TOOLUP_RATE_LIMIT_WINDOW_SECONDS",
            parsePositive "TOOLUP_RATE_LIMIT_QUEUE"
        with
        | Some permits, Some windowSeconds, Some queue ->
            RateLimitConfig.uniform {
                PermitLimit = permits
                WindowSeconds = windowSeconds
                QueueLimit = queue
            }
        | None, None, None -> RateLimitConfig.none
        | _ ->
            logger.Warn
                "Rate limit requires all three of TOOLUP_RATE_LIMIT_PERMITS / _WINDOW_SECONDS / _QUEUE. Partial configuration ignored — rate limit disabled."

            RateLimitConfig.none

    let private parseDefaultTeamStorageQuotaBytes (logger: ILogger) =
        match envVar "TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES" with
        | None -> defaults.DefaultTeamStorageQuotaBytes
        | Some "none"
        | Some "0" -> None
        | Some raw ->
            match Int64.TryParse raw with
            | true, n when n > 0L -> Some n
            | _ ->
                logger.Warn $"TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES={raw} not a positive integer or 'none'. Using default."

                defaults.DefaultTeamStorageQuotaBytes

    let private parseSlowRequestThreshold (logger: ILogger) =
        match envVar "TOOLUP_SLOW_REQUEST_MS" with
        | None -> defaults.SlowRequestThreshold
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> TimeSpan.FromMilliseconds(float n)
            | _ ->
                logger.Warn $"TOOLUP_SLOW_REQUEST_MS={raw} not a positive integer. Using default 1000ms."
                defaults.SlowRequestThreshold

    let private parseMaxSseConnectionsPerScope (logger: ILogger) =
        match envVar "TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE" with
        | None -> defaults.MaxSseConnectionsPerScope
        | Some "none"
        | Some "0" -> None
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> Some n
            | _ ->
                logger.Warn
                    $"TOOLUP_MAX_SSE_CONNECTIONS_PER_SCOPE={raw} not a positive integer or 'none'. Using default."

                defaults.MaxSseConnectionsPerScope

    let private parseLogLevel () : LogLevel * Set<string> =
        let level =
            match envVar "TOOLUP_LOG_LEVEL" with
            | None -> LogLevel.Info
            | Some raw ->
                match LogLevel.tryParse raw with
                | Some lvl -> lvl
                | None ->
                    // Bootstrap problem — the SDK's `fromEnv` helpers
                    // emit this warning via `eprintfn` because the
                    // logger they'd use is constructed from the same
                    // env var. `ServerConfig.fromEnv` is called AFTER
                    // the logger is built, so it could use it here,
                    // but using `eprintfn` keeps the warning surfacing
                    // identical to `ConsoleLogger.envSettings`. Worth
                    // it: an operator misreading TOOLUP_LOG_LEVEL once
                    // shouldn't see the warning twice.
                    eprintfn $"[WRN] TOOLUP_LOG_LEVEL={raw} not recognised. Using Info."
                    LogLevel.Info

        let categories =
            match envVar "TOOLUP_TRACE_CATEGORIES" with
            | None -> Set.empty
            | Some raw ->
                raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map _.Trim()
                |> Array.filter (fun s -> s <> "")
                |> Set.ofArray

        level, categories

    /// Phase 71.A.3 — `SERVER_PORT` read inside the `fromEnv` seam so a
    /// `/dev/inspect` config snapshot reflects the actually-bound port
    /// (previously `Port` stayed at `defaults.Port` because the only
    /// read lived in `SDK.Server.compose`). Non-integer / out-of-range
    /// → fail loud, mirroring the compose-time guard. Unset → default.
    let private parseServerPort () : int =
        match envVar "SERVER_PORT" with
        | None -> defaults.Port
        | Some raw ->
            match Int32.TryParse raw with
            | true, p when p >= 1 && p <= 65535 -> p
            | _ ->
                failwithf
                    "SERVER_PORT=%s is not a valid TCP port. Expected an integer in 1-65535 (unset SERVER_PORT to use the default %d)."
                    raw
                    defaults.Port

    /// Phase 71.A.4 — `TOOLUP_PUBLIC_BASE_URL` runtime resolution. Empty
    /// / whitespace is ambiguous → warn + fall back to `None`. A trailing
    /// slash is stripped (idempotent) because token issuers append their
    /// own `/r/{token}`-style segment and a pasted `https://x/` otherwise
    /// produces a double slash. Unset → `defaults.PublicBaseUrl` (`None`).
    let private parsePublicBaseUrl (logger: ILogger) : string option =
        match envVar "TOOLUP_PUBLIC_BASE_URL" with
        | None -> defaults.PublicBaseUrl
        | Some raw ->
            let trimmed = raw.Trim()

            if trimmed = "" then
                logger.Warn
                    "TOOLUP_PUBLIC_BASE_URL is set but empty/whitespace; the ambiguous empty value is ignored. Unset the variable or give it a value. Falling back to no public base URL."

                defaults.PublicBaseUrl
            else
                let noTrailing = trimmed.TrimEnd('/')

                if noTrailing <> trimmed then
                    logger.Warn
                        $"TOOLUP_PUBLIC_BASE_URL={raw} had a trailing slash; stripped to {noTrailing} (token issuers append their own path segment)."

                // Validate it parses as an absolute http(s) URL. A malformed
                // value (missing scheme, non-http scheme, stray host) would
                // otherwise be accepted silently and produce broken
                // share-token / public / OAuth-redirect links that only fail
                // at link-follow time. Fail soft (warn + fall back to None),
                // mirroring the empty-value handling above.
                match System.Uri.TryCreate(noTrailing, System.UriKind.Absolute) with
                | true, uri when uri.Scheme = System.Uri.UriSchemeHttp || uri.Scheme = System.Uri.UriSchemeHttps ->
                    Some noTrailing
                | _ ->
                    logger.Warn
                        $"TOOLUP_PUBLIC_BASE_URL={raw} is not a valid absolute http(s) URL; ignoring it (public links fall back to relative). Set a value like https://app.example.com."

                    defaults.PublicBaseUrl

    /// Phase 71.A.5 — `TOOLUP_PUBLIC_PATH`. Canonical precedence:
    /// env var > override-record value > `defaults.PublicPath`.
    let private resolvePublicPath (overrides: ServerConfigOverrides) : string =
        envVar "TOOLUP_PUBLIC_PATH"
        |> Option.orElse overrides.PublicPath
        |> Option.defaultValue defaults.PublicPath

    /// Phase 71.A.6 — boolean env var with an override-record middle tier:
    /// env (fail loud on garbage) > override > fallback. Needed for fields
    /// like `IncludePlatformDefaults` (default `true`) where a plain
    /// `envFlag` (false-when-unset) would wrongly flip an unset var off.
    let private envFlagTri (name: string) (overrideVal: bool option) (fallback: bool) : bool =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | Some("1" | "true" | "yes" | "on") -> true
        | Some("0" | "false" | "no" | "off") -> false
        | Some other ->
            failwithf
                "%s=%s is not a recognised boolean value. Expected 1/true/yes/on or 0/false/no/off (case-insensitive). Unset the variable to use the configured value."
                name
                other
        | None -> overrideVal |> Option.defaultValue fallback

    /// Phase 71.A.6 — optional boolean: `Some` when set (fail loud on
    /// garbage), `None` when unset (preserves a `bool option` default).
    let private envFlagOpt (name: string) : bool option =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> None
        | Some("1" | "true" | "yes" | "on") -> Some true
        | Some("0" | "false" | "no" | "off") -> Some false
        | Some other ->
            failwithf
                "%s=%s is not a recognised boolean value. Expected 1/true/yes/on or 0/false/no/off (case-insensitive). Unset the variable to leave it unset."
                name
                other

    /// Phase 71.A.6 — optional positive int64: parse when set, warn + `None`
    /// on garbage, `None` (or `none`/`0`) when unset.
    let private envInt64Opt (logger: ILogger) (name: string) : int64 option =
        match envVar name with
        | None
        | Some "none"
        | Some "0" -> None
        | Some raw ->
            match Int64.TryParse raw with
            | true, n when n > 0L -> Some n
            | _ ->
                logger.Warn $"{name}={raw} not a positive integer or 'none'. Leaving unset."
                None

    /// Phase 71.A.6 — positive-millisecond `TimeSpan` with a fallback;
    /// warn + fallback on garbage.
    let private envTimeSpanMs (logger: ILogger) (name: string) (fallback: TimeSpan) : TimeSpan =
        match envVar name with
        | None -> fallback
        | Some raw ->
            match Int32.TryParse raw with
            | true, n when n > 0 -> TimeSpan.FromMilliseconds(float n)
            | _ ->
                logger.Warn
                    $"{name}={raw} not a positive integer (milliseconds). Using default {fallback.TotalMilliseconds}ms."

                fallback

    /// Phase 71.A.8 — comma / semicolon / space-separated string list
    /// (the Surfaces-parser tokenisation, reused). Empty / whitespace → `[]`.
    let private parseStringList (name: string) : string list =
        match envVar name with
        | None -> []
        | Some raw ->
            raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s <> "")
            |> Array.toList

    /// Phase 71.A.7 — generic flat-case-DU env reader. `cases` maps
    /// lowercase tokens → the (payload-free) DU value; precedence is
    /// env > override > fallback. An unrecognised token warns (naming
    /// the valid tokens) and falls through to the configured value, so
    /// a typo never silently flips a subsystem on/off.
    let private parseFlatDuCase
        (logger: ILogger)
        (name: string)
        (cases: (string * 'T) list)
        (overrideVal: 'T option)
        (fallback: 'T)
        : 'T =
        let configured = overrideVal |> Option.defaultValue fallback

        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> configured
        | Some raw ->
            match cases |> List.tryFind (fun (tok, _) -> tok = raw) with
            | Some(_, v) -> v
            | None ->
                let valid = cases |> List.map fst |> String.concat ", "
                logger.Warn $"{name}={raw} not recognised. Valid: {valid}. Using the configured value."
                configured

    /// Phase 71.A.7 — the common `No* | Enabled*` binary shape. Accepts
    /// `no`/`off`/`disabled` and `enabled`/`on`/`yes` (case-insensitive).
    let private enabledDisabledTokens (disabledVal: 'T) (enabledVal: 'T) : (string * 'T) list = [
        "no", disabledVal
        "off", disabledVal
        "disabled", disabledVal
        "enabled", enabledVal
        "on", enabledVal
        "yes", enabledVal
    ]

    let private parseEnabledDisabled
        (logger: ILogger)
        (name: string)
        (disabledVal: 'T)
        (enabledVal: 'T)
        (fallback: 'T)
        : 'T =
        parseFlatDuCase logger name (enabledDisabledTokens disabledVal enabledVal) None fallback

    /// Phase 71.A.7 batch 2 — `parseEnabledDisabled` with an override-record
    /// middle tier (env > override > fallback) for binary toggles that ship
    /// a `ServerConfigOverrides` member.
    let private parseEnabledDisabledWith
        (logger: ILogger)
        (name: string)
        (overrideVal: 'T option)
        (disabledVal: 'T)
        (enabledVal: 'T)
        (fallback: 'T)
        : 'T =
        parseFlatDuCase logger name (enabledDisabledTokens disabledVal enabledVal) overrideVal fallback

    /// Phase 71.A.11 — select a hybrid DU case from an env var. `cases`
    /// maps a token to either `Ok value` (a case that's constructible
    /// from a default / curated factory — e.g. the nilary disabled case,
    /// or `PersistentBlobBacked EventRetentionPolicy.ninetyDays`) or
    /// `Error why` (the case carries a payload that can't be expressed via
    /// a single env var — `EnabledPublicRendering` needs a `ContentRoot`
    /// path; enabling DSR needs an explicit `ErasurePolicy`). An `Error`
    /// token **fails loud** naming how to supply the payload; unset /
    /// unrecognised → the configured value (GP 11).
    let private parseHybridCase
        (logger: ILogger)
        (name: string)
        (cases: (string * Result<'T, string>) list)
        (fallback: 'T)
        : 'T =
        match envVar name |> Option.map _.ToLowerInvariant() with
        | None -> fallback
        | Some raw ->
            match cases |> List.tryFind (fun (tok, _) -> tok = raw) with
            | Some(_, Ok v) -> v
            | Some(_, Error why) -> failwithf "%s=%s cannot be selected via env var: %s" name raw why
            | None ->
                let valid = cases |> List.map fst |> String.concat ", "
                logger.Warn $"{name}={raw} not recognised. Valid: {valid}. Using the configured value."
                fallback

    /// Build a `ServerConfig` from `TOOLUP_*` env vars + a curated
    /// overrides record. Every env-var read, warning message, and
    /// fallback semantics is byte-for-byte identical to the
    /// hand-written reference composition root pre-11.G — except for
    /// the Phase 66 Stream A.8 cutover from `TOOLUP_PLATFORM_MODE` to
    /// `TOOLUP_PLATFORM_SURFACES` (clean cutover; no aliasing).
    ///
    /// Surface-resolution (Phase 71.A — env-var beats library-default
    /// override-record value):
    ///   1. `TOOLUP_PLATFORM_SURFACES` (comma- / semicolon- /
    ///      space-separated token list) when set and at least one
    ///      token parses cleanly.
    ///   2. `overrides.Surfaces` when `Some` and non-empty (the
    ///      library-default fallback — `ServerConfigOverrides.referenceApp`
    ///      pins `Some Surfaces.individual`).
    ///   3. `defaults.Surfaces` as a final fallback.
    ///
    /// Consumer-authored literals (`{ ServerConfig.defaults with
    /// Surfaces = ... }`) never traverse this helper, so they still win
    /// at the highest altitude. The flip moves operator-deployer intent
    /// (env var) ahead of library-author defaults — fixes the
    /// silent-precedence trap documented in
    /// [`docs/migrations/71-runtime-config-audit.md`](../../docs/migrations/71-runtime-config-audit.md)
    /// §3.
    ///
    /// An unrecognised token (or an empty-after-parse result) falls
    /// back to the override-record value when present, else
    /// `defaults.Surfaces`, and surfaces the bad tokens via the
    /// supplied logger.
    let fromEnv (logger: ILogger) (overrides: ServerConfigOverrides) : ServerConfig =
        let overridesFallback =
            match overrides.Surfaces with
            | Some s when not (List.isEmpty s) -> s
            | _ -> defaults.Surfaces

        let surfaces =
            match envVar "TOOLUP_PLATFORM_SURFACES" with
            | None -> overridesFallback
            | Some raw ->
                let tokens =
                    raw.Split([| ','; ';'; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map _.Trim().ToLowerInvariant()
                    |> Array.filter (fun s -> s <> "")
                    |> Array.toList

                let parsed = tokens |> List.map parseSurfaceProfile

                let errors =
                    parsed
                    |> List.choose (function
                        | Error e -> Some e
                        | _ -> None)

                match errors with
                | [] ->
                    let resolved =
                        parsed
                        |> List.choose (function
                            | Ok s -> Some s
                            | _ -> None)

                    if List.isEmpty resolved then
                        logger.Warn
                            $"TOOLUP_PLATFORM_SURFACES={raw} resolved to an empty surface list. Valid tokens: anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer. Falling back to the library-default override value (or defaults)."

                        overridesFallback
                    else
                        resolved
                | bad ->
                    let badList = String.concat ", " bad

                    logger.Warn
                        $"TOOLUP_PLATFORM_SURFACES={raw} contains unrecognised token(s): {badList}. Valid tokens: anonymous, anonymous_persistent, trial, individual, team, multi_team, claim_bearer. Falling back to the library-default override value (or defaults)."

                    overridesFallback

        let logLevel, traceCategories = parseLogLevel ()

        // Phase 170 — module-binding trust anchors from the environment.
        // `TOOLUP_MODULE_BINDING_ALLOW_UNBOUND` is the policy bit (default
        // matches the off-by-default config); `TOOLUP_MODULE_BINDING_ANCHORS`
        // is a `;`-separated list of `mac:<keyId>:<scope>:<key>` (symmetric;
        // key resolved via ISecretStore at compose time) or
        // `asym:<keyId>:<alg>:<base64pubkey>` (asymmetric). A malformed entry
        // is warned + skipped; an unresolvable symmetric secret is the
        // compose-time validator's fail-closed concern (Phase 170 validator).
        let moduleBindingTrust =
            let allowUnbound =
                envFlagOrFail "TOOLUP_MODULE_BINDING_ALLOW_UNBOUND" ModuleBindingTrustConfig.defaults.AllowUnbound

            let anchors =
                match envVar "TOOLUP_MODULE_BINDING_ANCHORS" with
                | None -> []
                | Some raw ->
                    raw.Split([| ';' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.choose (fun entry ->
                        match entry.Trim().Split(':') with
                        | [| "mac"; keyId; scope; key |] -> Some(SymmetricAnchorRef(keyId, scope, key))
                        | [| "asym"; keyId; alg; pub |] -> Some(AsymmetricAnchorRef(keyId, alg, pub))
                        | _ ->
                            logger.Warn
                                $"TOOLUP_MODULE_BINDING_ANCHORS entry '{entry}' is malformed (expected 'mac:<keyId>:<scope>:<key>' or 'asym:<keyId>:<alg>:<base64pubkey>'); skipped."

                            None)
                    |> Array.toList

            {
                Anchors = anchors
                AllowUnbound = allowUnbound
            }

        {
            defaults with
                PublicPath = resolvePublicPath overrides // Phase 71.A.5
                Surfaces = surfaces
                ModuleFilter = envVar "TOOLUP_MODULE"
                ModuleBindingTrust = moduleBindingTrust
                RequireHttps = envFlag "TOOLUP_REQUIRE_HTTPS"
                TrustForwardedHeaders = envFlagOrFail "TOOLUP_TRUST_FORWARDED_HEADERS" defaults.TrustForwardedHeaders
                // Phase 325 — trusted-proxy CIDR allowlist + its escape hatch.
                // Entries are validated (fail-loud on malformed CIDR) by the
                // preflight validator + the pipeline's options builder, not here:
                // `fromEnv` stays a pure string read so the error surfaces with
                // the same message whichever construction path built the config.
                TrustedProxyCidrs = parseStringList "TOOLUP_TRUSTED_PROXY_CIDRS"
                AcceptForwardedHeadersFromAnyProxy = envFlag "TOOLUP_ACCEPT_FORWARDED_HEADERS_FROM_ANY_PROXY"
                StaticPathBehaviour = parseStaticPathBehaviour logger
                SlowRequestThresholdOverrides =
                    overrides.SlowRequestThresholdOverrides
                    |> Option.defaultValue defaults.SlowRequestThresholdOverrides
                // Phase 71.A.6 — env wins over override-record value, else fallback.
                EnableDevEndpoints =
                    envFlagTri "TOOLUP_ENABLE_DEV_ENDPOINTS" overrides.EnableDevEndpoints defaults.EnableDevEndpoints
                AutoBootstrapDevAdmin = overrides.AutoBootstrapDevAdmin
                IncludePlatformDefaults =
                    envFlagTri
                        "TOOLUP_INCLUDE_PLATFORM_DEFAULTS"
                        overrides.IncludePlatformDefaults
                        defaults.IncludePlatformDefaults
                // Phase 71.A.7 batch 2 — override-bearing toggles: env > override > default.
                ShareTokenStore =
                    parseEnabledDisabledWith
                        logger
                        "TOOLUP_SHARE_TOKEN_STORE"
                        overrides.ShareTokenStore
                        NoShareTokenStore
                        EnabledShareTokenStore
                        defaults.ShareTokenStore
                Webhooks =
                    parseEnabledDisabledWith
                        logger
                        "TOOLUP_WEBHOOKS"
                        overrides.Webhooks
                        NoWebhooks
                        EnabledWebhooks
                        defaults.Webhooks
                AuditLog =
                    parseEnabledDisabledWith
                        logger
                        "TOOLUP_AUDIT_LOG"
                        overrides.AuditLog
                        NoAuditLog
                        EnabledAuditLog
                        defaults.AuditLog
                SecurityHardening =
                    parseFlatDuCase
                        logger
                        "TOOLUP_SECURITY_HARDENING"
                        [
                            "no", NoSecurityHardening
                            "off", NoSecurityHardening
                            "disabled", NoSecurityHardening
                            "default", DefaultSecurityHardening
                            "on", DefaultSecurityHardening
                            "strict", StrictSecurityHardening
                        ]
                        overrides.SecurityHardening
                        defaults.SecurityHardening
                LogLevel = logLevel
                TraceCategories = traceCategories
                SseAuthMode = parseSseAuthMode logger
                AuthCookieIssuance = parseAuthCookieIssuance logger
                AcceptHeaderAuthWhenAuthRequired = envFlag "TOOLUP_ACCEPT_HEADER_AUTH_IN_AUTH_MODE"
                AcceptPlaintextSecretsWhenAuthRequired = envFlag "TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE"
                ReplicaCount = parseReplicaCount logger
                AcceptInProcessSchedulerInMultiInstance = envFlag "TOOLUP_ACCEPT_INPROCESS_SCHEDULER_MULTI_INSTANCE"
                AcceptNoRateLimitWhenAuthRequired = envFlag "TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE"
                AcceptUnsignedPublishable = envFlag "TOOLUP_ACCEPT_UNSIGNED_PUBLISHABLE"
                AcceptQueryParamSseAuthWhenAuthRequired = envFlag "TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE"
                AcceptSameSiteOnlyCsrfWhenAuthRequired = envFlag "TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE"
                AcceptInMemoryShareTokenRateLimiterInMultiInstance =
                    envFlag "TOOLUP_ACCEPT_INMEMORY_SHARE_TOKEN_RATE_LIMITER_MULTI_INSTANCE"
                // Phase 71.A.2 — six `Accept*` flags whose documented env
                // vars `fromEnv` never read (audit §7). Each preserves
                // GP 11: unset → `false`, and the matching validator still
                // refuses startup unless the operator opts in.
                AcceptInProcessIngestionInMultiInstance = envFlag "TOOLUP_ACCEPT_INPROCESS_INGESTION_MULTI_INSTANCE"
                AcceptSharedEmbeddingCacheInTeamMode = envFlag "TOOLUP_ACCEPT_SHARED_EMBEDDING_CACHE_IN_TEAM_MODE"
                AcceptStickyRoutedAiInMultiInstance = envFlag "TOOLUP_ACCEPT_STICKY_ROUTED_AI_MULTI_INSTANCE"
                AcceptUnboundAudienceWhenAuthRequired = envFlag "TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE"
                AcceptInMemoryOAuthStateInMultiInstance = envFlag "TOOLUP_ACCEPT_INMEMORY_OAUTH_STATE_MULTI_INSTANCE"
                AcceptPendingInviteStoreInMultiInstance = envFlag "TOOLUP_ACCEPT_PENDING_INVITE_STORE_MULTI_INSTANCE"
                AcceptInviteByEmailWithoutDirectory = envFlag "TOOLUP_ACCEPT_INVITE_BY_EMAIL_WITHOUT_DIRECTORY"
                // Phase 71.A.3 / 71.A.4 — Port + PublicBaseUrl now resolve
                // inside the `fromEnv` seam (were compose-only / unread).
                Port = parseServerPort ()
                PublicBaseUrl = parsePublicBaseUrl logger
                // Phase 71.A.6 — boolean / scalar bundle. Each is additive and
                // preserves GP 11: unset → the prior `defaults.X` value.
                BackfillMissedTicks = envFlag "TOOLUP_BACKFILL_MISSED_TICKS"
                SkipPreflight = envFlag "TOOLUP_SKIP_PREFLIGHT"
                HealthStateTracking = envFlag "TOOLUP_HEALTH_STATE_TRACKING"
                EnableCitationDevEndpoint = envFlagOpt "TOOLUP_ENABLE_CITATION_DEV_ENDPOINT"
                MaxRequestBodyBytes = envInt64Opt logger "TOOLUP_MAX_REQUEST_BODY_BYTES"
                SlowRateLimitThreshold =
                    envTimeSpanMs logger "TOOLUP_SLOW_RATE_LIMIT_MS" defaults.SlowRateLimitThreshold
                // Phase 71.A.8 — server string lists.
                WebhookUrlAllowedHosts = parseStringList "TOOLUP_WEBHOOK_URL_ALLOWED_HOSTS"
                PeerRoutePrefixes = parseStringList "TOOLUP_PEER_ROUTE_PREFIXES"
                // Phase 71.A.7 (batch 1) — flat-case DU lifts (no override
                // member, no payload). Additive: unset → `defaults.X`.
                ResultStore =
                    parseFlatDuCase
                        logger
                        "TOOLUP_RESULT_STORE"
                        [
                            "no", NoResultStore
                            "inmemory", InMemoryResultStore
                            "in-memory", InMemoryResultStore
                            "persistent", PersistentResultStore
                        ]
                        None
                        defaults.ResultStore
                Lineage =
                    parseEnabledDisabled logger "TOOLUP_LINEAGE" NoLineageStore EnabledLineageStore defaults.Lineage
                DataIngestion =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_DATA_INGESTION"
                        NoDataIngestion
                        EnabledDataIngestion
                        defaults.DataIngestion
                ColumnMapping =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_COLUMN_MAPPING"
                        NoColumnMapping
                        EnabledColumnMapping
                        defaults.ColumnMapping
                MappingDryRun =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_MAPPING_DRYRUN_BLOCK"
                        WarnOnValidationFailure
                        BlockOnValidationFailure
                        defaults.MappingDryRun
                OAuthRefresher =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_OAUTH_REFRESHER"
                        NoOAuthRefresher
                        EnabledOAuthRefresher
                        defaults.OAuthRefresher
                EntityStore =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_ENTITY_STORE"
                        NoEntityStore
                        EnabledEntityStore
                        defaults.EntityStore
                UsageMetering =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_USAGE_METERING"
                        NoUsageMetering
                        EnabledUsageMetering
                        defaults.UsageMetering
                MetricsEndpoint =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_METRICS_ENDPOINT"
                        NoMetricsEndpoint
                        EnabledMetricsEndpoint
                        defaults.MetricsEndpoint
                PlatformKnowledgeBase =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_PLATFORM_KNOWLEDGE_BASE"
                        NoPlatformKnowledgeBase
                        EnabledPlatformKnowledgeBase
                        defaults.PlatformKnowledgeBase
                ConfigDriftDetection =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_CONFIG_DRIFT_DETECTION"
                        NoConfigDriftDetection
                        EnabledConfigDriftDetection
                        defaults.ConfigDriftDetection
                RateLimiter =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_RATE_LIMITER"
                        NoRateLimiter
                        EnabledRateLimiter
                        defaults.RateLimiter
                SmokeTest =
                    parseEnabledDisabled logger "TOOLUP_SMOKE_TEST" NoSmokeTest EnabledSmokeTest defaults.SmokeTest
                DeploymentReadiness =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_DEPLOYMENT_READINESS"
                        NoReadinessReport
                        EnabledReadinessReport
                        defaults.DeploymentReadiness
                AssetStore =
                    parseEnabledDisabled logger "TOOLUP_ASSET_STORE" NoAssetStore EnabledAssetStore defaults.AssetStore
                ConsentAudit =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_CONSENT_AUDIT"
                        NoConsentAudit
                        EnabledConsentAudit
                        defaults.ConsentAudit
                AdAnalytics =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_AD_ANALYTICS"
                        NoAdAnalytics
                        EnabledAdAnalytics
                        defaults.AdAnalytics
                // Phase 159 — durable per-subject consent-state store mode.
                ConsentStateStore =
                    parseFlatDuCase
                        logger
                        "TOOLUP_CONSENT_STATE_STORE"
                        [
                            "no", NoConsentStateStore
                            "off", NoConsentStateStore
                            "disabled", NoConsentStateStore
                            "inmemory", InMemoryConsentStateStore
                            "in-memory", InMemoryConsentStateStore
                            "entity", EntityBackedConsentStateStore
                            "entity-backed", EntityBackedConsentStateStore
                        ]
                        None
                        defaults.ConsentStateStore
                ServerlessHost =
                    parseFlatDuCase
                        logger
                        "TOOLUP_SERVERLESS_HOST"
                        [ "kestrel", KestrelHost; "serverless", ServerlessHost ]
                        None
                        defaults.ServerlessHost
                ProcessProfile =
                    parseFlatDuCase
                        logger
                        "TOOLUP_PROCESS_PROFILE"
                        [
                            "allinone", AllInOne
                            "all-in-one", AllInOne
                            "web", WebOnly
                            "webonly", WebOnly
                            "worker", WorkerOnly
                            "workeronly", WorkerOnly
                            "dispatcher", DispatcherOnly
                            "dispatcheronly", DispatcherOnly
                        ]
                        None
                        defaults.ProcessProfile
                // Phase 71.A.7 batch 2 — TeamCreationPolicy (no override member).
                TeamCreationPolicy =
                    parseFlatDuCase
                        logger
                        "TOOLUP_TEAM_CREATION_POLICY"
                        [
                            "platformadminonly", PlatformAdminOnly
                            "platform-admin-only", PlatformAdminOnly
                            "admin", PlatformAdminOnly
                            "anyauthenticateduser", AnyAuthenticatedUser
                            "any", AnyAuthenticatedUser
                            "authenticated", AnyAuthenticatedUser
                        ]
                        None
                        defaults.TeamCreationPolicy
                // Phase 71.A.11 — fully-nilary DUs the audit grouped as HY
                // (the in-tree DU carries no payload): pure flat lifts.
                JobScheduler =
                    parseEnabledDisabled
                        logger
                        "TOOLUP_JOB_SCHEDULER"
                        NoJobScheduler
                        InProcessJobScheduler
                        defaults.JobScheduler
                RateLimitStore =
                    parseFlatDuCase
                        logger
                        "TOOLUP_RATE_LIMIT_STORE"
                        [
                            "no", NoRateLimitStore
                            "off", NoRateLimitStore
                            "disabled", NoRateLimitStore
                            "inmemory", InMemoryRateLimitStore
                            "in-memory", InMemoryRateLimitStore
                            "external", ExternalRateLimitStore
                        ]
                        None
                        defaults.RateLimitStore
                // Phase 71.A.11 — hybrid case-flips: nilary / curated-default
                // cases select; payload-bearing cases fail loud (the payload
                // must be supplied via overrides / a `defaults with` literal).
                EventStore =
                    parseHybridCase
                        logger
                        "TOOLUP_EVENT_STORE"
                        [
                            "inmemory", Ok InMemoryOnly
                            "in-memory", Ok InMemoryOnly
                            "persistent", Ok(PersistentBlobBacked EventRetentionPolicy.ninetyDays)
                        ]
                        defaults.EventStore
                ConversationStore =
                    parseHybridCase
                        logger
                        "TOOLUP_CONVERSATION_STORE"
                        [
                            "no", Ok NoConversationStore
                            "off", Ok NoConversationStore
                            "disabled", Ok NoConversationStore
                            "enabled",
                            Error
                                "EnabledConversationStore requires a retentionDays value; set ServerConfig.ConversationStore via overrides or a `{ defaults with ... }` literal"
                        ]
                        defaults.ConversationStore
                PublicRendering =
                    parseHybridCase
                        logger
                        "TOOLUP_PUBLIC_RENDERING"
                        [
                            "no", Ok NoPublicRendering
                            "off", Ok NoPublicRendering
                            "disabled", Ok NoPublicRendering
                            "enabled",
                            Error
                                "EnabledPublicRendering requires a ContentRoot path; set ServerConfig.PublicRendering via overrides"
                        ]
                        defaults.PublicRendering
                DataSubjectRequests =
                    parseHybridCase
                        logger
                        "TOOLUP_DATA_SUBJECT_REQUESTS"
                        [
                            "disabled", Ok DataSubjectRequestMode.Disabled
                            "no", Ok DataSubjectRequestMode.Disabled
                            "off", Ok DataSubjectRequestMode.Disabled
                            "enabled",
                            Error
                                "Enabling DSR requires an explicit ErasurePolicy (a compliance decision, not defaulted); set ServerConfig.DataSubjectRequests via overrides"
                        ]
                        defaults.DataSubjectRequests
                EphemeralStoreEvictionMinutes = parseEphemeralStoreEvictionMinutes logger
                RateLimit = parseRateLimit logger
                DefaultTeamStorageQuotaBytes = parseDefaultTeamStorageQuotaBytes logger
                SlowRequestThreshold = parseSlowRequestThreshold logger
                MaxSseConnectionsPerScope = parseMaxSseConnectionsPerScope logger
        }
#endif