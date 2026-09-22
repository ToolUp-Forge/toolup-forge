// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.Narrative

// Phase 347 — SDK.Shared.fs now holds only the `ServerConfig` record: the
// documented irreducible core of the former monolith. A record type is one
// compilation unit and cannot be split across files without changing its
// public surface; the `*Mode` DUs it selects between live under
// Shared/Config/, its companion module (`defaults` + `fromEnv`) in
// Shared/Config/ServerConfigFromEnv.fs, and the inter-module event types in
// Shared/ModuleEvents.fs.

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
    /// Phase 321 — long-running job progress checkpoints. Default:
    /// `NoJobProgress` — `ctx.Progress` is the no-op reporter, no
    /// `IJobProgressSink` is registered, and no notification or event
    /// traffic is generated (GP 13). `EnabledJobProgress` fans checkpoints
    /// out to `INotificationChannel` (coalesced, scope-gated) and persists
    /// the durable + terminal ones to `IEventStore`. Requires a composed
    /// `JobScheduler` to have any effect — a deployment with
    /// `NoJobScheduler` runs no jobs to report on.
    JobProgress: JobProgressMode
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
    /// Phase 598 — opt-in event-trigger catch-up watermark. Default:
    /// `false` — `OnEvent` job triggering stays at-most-once: the
    /// in-memory notify hook is the only dispatch path, so an event
    /// durably written immediately before a process crash never fires
    /// its triggers. When `true` (and `JobScheduler =
    /// InProcessJobScheduler`), the scheduler persists a per-scope
    /// trigger cursor (`_platform/job-triggers/{scopeId}.cursor`,
    /// advanced after each live notify) and on startup — plus a
    /// periodic sweep — re-reads `IEventStore` past the cursor and
    /// dispatches any `OnEvent` triggers the notify hook never
    /// processed. Semantics become at-least-once: a crash between
    /// dispatch and cursor flush, or the deliberate startup overlap
    /// window, can re-fire a trigger — job handlers opting in must be
    /// re-entrant (the same bar `BackfillMissedTicks` sets). Unlike
    /// `BackfillMissedTicks` (which blind-re-fires every active
    /// `OnEvent` job on detected tick drift), the catch-up scan
    /// replays the *actual missed events*, each with its real
    /// `eventType` + `eventId` in `TriggerSource.ScheduledByEvent`.
    EventTriggerCatchUp: bool
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
    /// Phase 527 — service-account substrate selection. Default:
    /// `NoServiceAccounts` — no `IServiceAccountStore` is registered, no
    /// bearer-token middleware is inserted, no admin routes are mounted,
    /// and no `_platform/service-accounts/` blob layout is touched.
    /// Enable with `EnabledServiceAccounts` when the deployment needs
    /// machine principals (CI integrations, partner API access, agent
    /// hosts) that authenticate as themselves rather than borrowing a
    /// human identity or a raw admin token.
    ServiceAccounts: ServiceAccountStoreMode
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
    /// Phase 6d.A — opt-in one-shot migration of pre-6d.A webhook
    /// signing secrets to encryption-at-rest. Default `false`. When
    /// `true` AND `Webhooks = EnabledWebhooks`, compose runs
    /// `WebhookSecretMigration.migrate` once at startup (before
    /// preflight): every persisted subscription still carrying a
    /// plaintext `Secret` has it moved into `ISecretStore` and the blob
    /// rewritten with only a `SecretRef`. Idempotent — a subscription
    /// already migrated is skipped, so leaving this `true` costs one
    /// blob scan per boot. A deployment upgrading from a pre-6d.A
    /// version sets this `true` for the first boot so the
    /// `WebhookSecretAtRestValidator` (which Errors on any residual
    /// inline secret) passes. Env: `TOOLUP_MIGRATE_WEBHOOK_SECRETS`.
    MigrateWebhookSecretsAtRest: bool
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
    /// Phase 10g — OAuth 1.0a substrate selection. Default: `NoOAuth1a` —
    /// the three-legged flow routes are not mounted. `EnabledOAuth1a`
    /// mounts `/api/oauth1a/{flow}/*`; register flows via
    /// `ServerApp.withOAuth1aFlow`.
    OAuth1a: OAuth1aMode
    /// Entity-store selection. Default: `NoEntityStore` —
    /// no `IEntityStore` is registered, no `EntityRegistry`. Enable
    /// with `EnabledEntityStore` to activate the substrate; entity
    /// types register via `ServerApp.withEntity<'T> registration`.
    EntityStore: EntityStoreMode
    /// Phase 599 — entity-write outbox selection. Default:
    /// `NoEntityOutbox` — no outbox surface, no relay service.
    /// Enable with `EnabledEntityOutbox` (requires
    /// `EntityStore = EnabledEntityStore`) to register
    /// `OutboxEntityStore.SaveWithEvents` + the relay drain. Env:
    /// `TOOLUP_ENTITY_OUTBOX`.
    EntityOutbox: EntityOutboxMode
    /// Phase 447 — seed / fixture-data selection. Default: `NoSeedData` —
    /// no `ISeedPack` is applied, no `_platform/seed/` marker is touched,
    /// zero cost. `EnabledSeedData` applies registered packs once per
    /// `Name@Version` at end-of-compose (refused on a Team / multi-team
    /// production shape); `ForcedSeedData` applies even there.
    SeedData: SeedDataMode
    /// Phase 68 — graph-data store selection (`IGraphStore`). Default:
    /// `InMemoryGraphStore` — the zero-dependency in-memory default is
    /// registered lazily (GP 13: never instantiated until a graph API is
    /// resolved). `CustomGraphStore` leaves an engine companion's own
    /// `IGraphStore` singleton in place.
    GraphStore: GraphStoreMode
    /// Phase 68d — entity→graph projection selection. Default:
    /// `NoEntityGraphProjection` — no bridge is wired, the entity store is
    /// byte-identical (GP 13). `EnabledEntityGraphProjection` opts the
    /// deployment into the `ToolUp.Graph.Projection` bridge (wired via
    /// `EntityGraphProjectionCompose.wire`), which keeps a derived graph
    /// read-model of the registered entities + their declared relationships
    /// in sync. Requires `EntityStore = EnabledEntityStore` + an
    /// `IGraphStore`.
    EntityGraphProjection: EntityGraphProjectionMode
    /// Phase 161 — time-series storage selection. Default:
    /// `NoTimeSeriesStore` — no `ITimeSeriesStore` registered, zero cost.
    /// `InMemoryTimeSeries` registers the dev/test in-memory default;
    /// `CustomTimeSeriesStore` leaves a companion-registered singleton
    /// (e.g. `ToolUp.TimeSeriesStores.Timescale`) in place.
    TimeSeriesStore: TimeSeriesStoreMode
    /// Phase 10a — module data-migration substrate selection. Default:
    /// `NoDataMigrations` — nothing registered, nothing swept, zero
    /// cost (GP 13). `EnabledDataMigrations` adds the startup runner;
    /// `ManualDataMigrations` registers everything except the runner,
    /// leaving the admin module's trigger as the only way a pass
    /// starts.
    DataMigrations: DataMigrationMode
    /// Phase 10b — declared config schema migrators, one per forward
    /// step per module key. Default `[]`.
    ///
    /// **A plain list rather than a mode DU, unlike `DataMigrations`
    /// above, because the two have different cost shapes.** A data
    /// migration pass is a background sweep over every stored object,
    /// so it needs a knob to decide whether the hosted service runs at
    /// all. A config migration is lazy: it runs on the read that
    /// discovers a stale document, and only when the reading module's
    /// declared `ModuleConfigSchema.SchemaVersion` is above 1. An empty
    /// list therefore already costs nothing (GP 13), and a mode DU
    /// would be a second thing to set for no behaviour anyone could
    /// distinguish.
    ///
    /// Populated per module via `ServerModule.withConfigMigration`;
    /// declaring one directly on the config is the escape hatch for a
    /// deployment migrating a `_platform*` key it does not own a
    /// `ServerModule` for.
    ConfigMigrations: IConfigMigrator list
    /// Phase 448 — dataset substrate selection. Default: `NoDatasets` — no
    /// `IDatasetStore` registered, zero cost. `BlobDatasets` registers the
    /// blob-backed default over `IDataObjectStore` (JSON-frame codec, no
    /// vendor dependency); `CustomDatasetStore` leaves a companion-registered
    /// singleton (e.g. a Parquet-codec store) in place.
    Datasets: DatasetStoreMode
    /// Phase 528 — session-registry selection. Default:
    /// `NoSessionRegistry` — no session recording, no revocation
    /// middleware, no `ISessionApi` route, zero cost (GP 13).
    /// `BlobSessionRegistry` registers the blob-backed default over
    /// `IBlobStorage`; `CustomSessionRegistry` leaves a
    /// companion-registered singleton (e.g. Redis) in place. Both carry
    /// the `SessionRegistryOptions` that set the revocation window.
    SessionRegistry: SessionRegistryMode
    /// Phase 449 — model-fit substrate selection. Default: `NoModelFitting`
    /// — no fit envelope, zero cost. `EnabledModelFitting` indexes the
    /// registered `IModelFitProvider` companions + binds the fit-run job
    /// handler (requires a composed `JobScheduler`). Modelling math stays in
    /// the provider companion; forge only stores + compares (plan D10).
    ModelFitting: ModelFittingMode
    /// Phase 600 — out-of-process model-execution submitter API. Default:
    /// `NoModelExecutionApi` — route not mounted, zero cost (GP 13).
    ModelExecution: ModelExecutionApiMode
    /// Phase 318 — external-compute substrate selection
    /// (`IExternalComputeDispatcher`). Default: `NoExternalCompute` — the
    /// `NoExternalComputeDispatcher` is registered, so the seam resolves and
    /// every `Submit` returns a clean not-configured `Error`; no background
    /// service, no dependency pulled (GP 13). `CustomExternalCompute` leaves
    /// a companion-registered dispatcher (an HTTP worker pool, a batch
    /// backend) in place.
    ExternalCompute: ExternalComputeMode
    /// Phase 451 — compute-budget governance selection. Default:
    /// `NoComputeBudget` — no `IComputeBudgetStore` is registered, the
    /// composed dispatcher is not decorated, and the fit-enqueue path
    /// consults nothing, so an existing deployment is byte-for-byte
    /// unchanged (GP 11 + GP 13). `EnabledComputeBudget` registers the
    /// blob-backed store, wraps the composed `IExternalComputeDispatcher`
    /// in the budget decorator, and gates fit enqueue against the same
    /// per-scope budget.
    ComputeBudget: ComputeBudgetMode
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
    /// Phase 9t — behaviour when an audit write fails. Default:
    /// `LogAndContinue` (pre-9t behaviour byte-for-byte — GP 11).
    /// Production deployments with continuous-audit obligations opt
    /// into `RefuseAction` (fail the action) or `DegradeToFile`
    /// (spill locally + replay on recovery). Only consulted when
    /// `AuditLog = EnabledAuditLog`. Env:
    /// `TOOLUP_AUDIT_FAILURE_POLICY=log|refuse|degrade`.
    AuditFailurePolicy: AuditFailurePolicy
    /// Phase 9t — root directory for the `DegradeToFile` fallback
    /// spill. `None` (default) resolves to `audit-fallback/` under
    /// the process working directory. Only consulted when
    /// `AuditFailurePolicy = DegradeToFile`.
    AuditFallbackDirectory: string option
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
    /// NOTE: `SkipPreflight` reaches only the *external-probe* class —
    /// the validators that contact a dependency which may be down. Two
    /// marker-opted classes always run and still abort startup on
    /// `Error`: `ISecurityClassValidator` (auth / secret / CSRF /
    /// cross-instance-auth-state guards) and, since Phase 585,
    /// `IStructuralClassValidator` (in-process identity / integrity
    /// invariants over the composed surface — duplicate component ids,
    /// companion-slot legality, orphaned tool references). A single
    /// boolean must not silently disable identity-spoofing /
    /// unauthenticated-access protection, nor the checks that keep the
    /// composition itself sound; the structural rules cost microseconds
    /// and touch nothing external, so an emergency boot loses nothing by
    /// running them. The skipped validators' names are logged at `Warn`,
    /// alongside the always-run set and its classes, so the bypass is
    /// visible in the deployment log.
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

    /// Phase 441 — per-user notification preference + digest substrate.
    /// Default `NoNotificationPreferences`: no `INotificationPreferenceStore`
    /// in DI, the outbound channel is not wrapped, no digest job, no
    /// `INotificationPreferenceApi` route — sends flow exactly as before
    /// (GP 11 + GP 13). `EnabledNotificationPreferences settings` registers
    /// the blob-backed store, wraps the dispatcher-facing channel in
    /// `NotificationPreferenceFilter`, registers the
    /// `_platform.notifications.digest` job when a scheduler is composed,
    /// and mounts the preference API the built-in `NotificationPreferencesUI`
    /// talks to.
    NotificationPreferences: NotificationPreferenceMode

    /// Phase 441 — the notification categories this deployment's modules
    /// declare (GP 9: modules declare, the SDK never enumerates). Appended
    /// by `ServerModule.withNotificationCategories` through `addModule`
    /// and by `ServerApp.withNotificationCategory`; read by the filter and
    /// the preference API only when `NotificationPreferences` is enabled,
    /// so a declaration on a default deployment is inert. Code-authored —
    /// no env-var path; `fromEnv` inherits the empty set.
    NotificationCategories: NotificationCategory list

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
    /// Override via `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE=1` or,
    /// since Phase 457, the shorter `TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1`.
    /// Both spellings set this one field: the flag is the deployment's
    /// single "I know these secrets are not encrypted at rest" statement,
    /// honoured by `EncryptedSecretStoreModeValidator`,
    /// `OAuthSecretEncryptionModeValidator` and
    /// `SecretStoreAtRestPostureValidator` alike.
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
    /// to accept best-effort per-replica hit-rate and silence the
    /// warning. Phase 633 — the better answer is usually to remove the
    /// divergence instead: compose a cross-replica cache via
    /// `RAGServerApp.withEmbeddingCache` (the shipped backing is the
    /// `ToolUp.EmbeddingCaches.Redis` companion), which LIFTS the warning
    /// rather than suppressing it, and leaves this flag `false`.
    AcceptSharedEmbeddingCacheInTeamMode: bool

    /// Phase 9m.B — explicit opt-in to running RAG with no durable
    /// backing (neither an `IBlobStorage` nor an `IVectorStore`
    /// override) in a deployment that otherwise carries persistent
    /// authenticated storage. The in-memory vector store writes through
    /// a null blob store that DISCARDS bytes, so the corpus starts
    /// EMPTY after every process restart with no further signal.
    /// Default `false` — `RagPersistenceValidator` refuses startup on a
    /// persistent deployment. Set `true` (or
    /// `TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1`) for a deployment that
    /// deliberately re-ingests its corpus on boot (a build-time-seeded
    /// index, a demo/sandbox, an integration-test harness). The
    /// validator then degrades to a `Warning` so the choice stays
    /// visible in the `/dev/inspect` Validators panel rather than
    /// becoming silent.
    AcceptEphemeralRagIndex: bool

    /// Phase 9m.B — explicit opt-in to running the dev-only,
    /// process-stateful `LocalEmbeddingProvider` in a production-shaped
    /// deployment (`Individual` / `AuthenticatedEphemeral` / `Team` /
    /// `MultiTeam`). The local TF-IDF embedder derives vectors from an
    /// IDF dictionary that evolves with the corpus *in this process*,
    /// so embeddings are not reproducible across restarts or replicas
    /// and retrieval quality drifts as the corpus grows. Default
    /// `false` — `LocalEmbeddingProviderInProductionModeValidator`
    /// (non-team shapes) and `TeamModeLocalEmbedderValidator` (team
    /// shapes) each emit a `Warning`, and the companion's health probe
    /// reports `Degraded`. Set `true` (or
    /// `TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1`) to silence the family
    /// at once for a single-replica deployment that has accepted the
    /// trade-off.
    AcceptLocalEmbedderAtScale: bool

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
    /// silently corrupts). Since Phase 5h the SDK auto-selects the
    /// ETag-based `BlobPendingInviteStore` for `ReplicaCount > 1` when
    /// the blob backend supports conditional writes, so the Warning
    /// fires only when the in-memory store is still the resolved one;
    /// set `true` (or
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

    /// Phase 547.C — opt-in inviter notification on pending-invite
    /// expiry. When `true` AND a transactional email sink is composed
    /// (`ServerApp.withTransactionalSink`, `Kind = Email`), every
    /// pending-by-email invite that lapses unconsumed publishes a
    /// `TransactionalEmail` to the inviter ("your invite to X expired
    /// unconsumed — re-issue?") alongside the `TeamInviteExpired`
    /// audit row. Best-effort: a failed publish logs at `Warn` and
    /// never fails the sweep.
    ///
    /// Default `false` (GP 13) — a deployment that composes an email
    /// sink for other purposes must not silently start emailing
    /// inviters. With no email sink composed the published envelope
    /// reaches no sink and the feature is a no-op. Set `true` (or
    /// `TOOLUP_NOTIFY_INVITER_ON_INVITE_EXPIRY=1`) to opt in.
    NotifyInviterOnInviteExpiry: bool

    /// Phase 460 — explicit operator acknowledgement that the
    /// share-token HMAC signing key may be **ephemeral**: absent from
    /// `ISecretStore` at boot and therefore CSPRNG-generated and
    /// persisted by `BlobShareTokenStore` on first use.
    ///
    /// An auto-generated key is a security-critical secret nobody knows
    /// to back up or rotate: if the secret store is wiped or
    /// re-provisioned the key is silently re-minted and **every
    /// outstanding public share link stops verifying**. With several
    /// replicas booting against an empty store the authoritative key is
    /// whichever replica won the first write, not a value the operator
    /// chose.
    ///
    /// Default `false`. `ShareTokenSigningKeyProvenanceValidator`
    /// **refuses startup** when the share-token surface is live, the
    /// deployment is production-shaped (`PublicBaseUrl` set or
    /// `ReplicaCount > 1`) and the key is absent. Set `true` (or
    /// `TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1`) for a dev / CI /
    /// single-instance deployment that knowingly accepts a throwaway
    /// key; the refusal downgrades to a `Warning` naming the flag, so
    /// the posture stays visible on the preflight snapshot rather than
    /// becoming silent.
    ///
    /// A non-production-shaped deployment is unaffected either way —
    /// the validator is silent there, as it always was (GP 11).
    AcceptEphemeralShareTokenKey: bool

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

    /// Phase 6p — announce an ephemeral session store's
    /// eviction-then-recreate to the affected scope over
    /// `INotificationChannel`, so a connected client clears its stale
    /// file list and toasts instead of discovering the loss when a
    /// downstream module call fails with "File 'X' not found in
    /// session". `true` by default.
    ///
    /// GP 13 — a deployment that prefers the pre-6p behaviour sets this
    /// to `false` and the publish is suppressed. It does NOT suppress
    /// the `SessionStoreReset` AUDIT emission: audit answers a
    /// compliance question about data loss, and gating it on a UX
    /// preference would make the trail silent exactly where it matters.
    /// Nor does it suppress the client's own reconciliation — the
    /// on-mount epoch fetch and the pre-flight check are client-side and
    /// need no channel.
    NotifyOnSessionStoreReset: bool

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
    /// (over the configured `IBlobStorage`), the ToolUp.Remoting
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
    /// endpoint, scope-signed expiring URLs, and the ToolUp.Remoting
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

    /// Phase 437 — per-component resource envelopes: declared budgets
    /// (job concurrency, request rate, queue depth, an advisory memory
    /// hint) keyed by the stable `ComponentId`. Empty (default) = no
    /// component is budgeted, every admission check short-circuits, and
    /// the deployment is byte-for-byte its pre-437 self (GP 11 / GP 13).
    /// Enforced at seams that already exist — the Phase 9b job
    /// scheduler's handler registration, the Phase 56 rate-limit
    /// middleware, and any queue that consults
    /// `ResourceEnvelopeEnforcement`; nothing here starts a background
    /// service or adds a dependency. An id absent from the map resolves
    /// to `ResourceEnvelope.unconstrained`.
    ResourceEnvelopes: EnvelopeSignature

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
    /// gates team creation on `IPlatformAdminStore.IsPlatformAdmin` so
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
    /// Phase 549 — whether `TeamApi.AddTeamMember` /
    /// `TeamApi.CreateTeamWithOwner` demand an existence proof for the
    /// principal id they are handed, resolved against the composed
    /// `IUserDirectory`. Default `NoIdentityProof` is the pre-549
    /// behaviour byte-for-byte (GP 11): the id is sanitised at the store
    /// seam and written unverified, so a typo mints a ghost member.
    /// `RequireDirectoryProof` refuses ids the directory does not
    /// recognise, and is refused at startup preflight when no
    /// `IUserDirectory` is composed. The invite-by-email path is
    /// untouched — the sign-in that consumes a pending invite is its own
    /// existence proof.
    DirectAddIdentityProof: DirectAddIdentityProof
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
    /// Phase 7b — schema-first user-authoring substrate selection. Default
    /// `NoUserSchemaAuthoring` — no `IUserSchemaStore` in DI, no
    /// `IUserSchemaApi` route, no migration job handler, no schema-authoring
    /// audit. `EnabledUserSchemaAuthoring` registers the blob-backed store +
    /// mounts the Owner/Admin-gated authoring API + registers the migration
    /// job handler. Zero cost when not enabled (GP 11 + GP 13).
    UserSchemaAuthoring: UserSchemaAuthoringMode
    /// Phase 520 — grounding fact-store selection. Default `NoFactStore`
    /// — no `IFactStore` composed, the grounding fact tier inert, the
    /// deployment byte-for-byte unchanged (GP 11 + GP 13). `EnabledFactStore`
    /// declares that a fact store (the `ToolUp.Facts` `BlobFactStore`
    /// default) participates; the composition manifest / composable-surface
    /// descriptor report the resolved kind from this knob.
    FactStore: FactStoreMode
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
    /// Phase 686 — opt-in deployment verification report. Default
    /// `NoDeploymentVerification` (GP 11/13) leaves the
    /// `IDeploymentVerificationApi` route unmounted (the surface 404s,
    /// the deployment is byte-for-byte unchanged).
    /// `EnabledDeploymentVerification` mounts the Platform-Admin-gated
    /// read that composes the boot-verification verdict, the
    /// grounding-envelope continuity walk, the audit-ledger walk, the
    /// certificate issuance log and the answer-verification join into one
    /// artefact carrying a typed verdict per section. Set via
    /// `ServerApp.withDeploymentVerification`. Reads only — the report
    /// mutates nothing and runs no live probe.
    DeploymentVerification: DeploymentVerificationMode
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
    /// Phase 442 — presence + soft-lock collaboration substrate
    /// selection. Default `NoPresence` — no `IPresenceTracker` /
    /// `IEntityLockStore` in DI, no heartbeat cost, no
    /// `_platform.presence` / `_platform.lock` fan-out; an existing
    /// deployment that upgrades stays byte-for-byte identical until it
    /// opts in (GP 11 + GP 13). `EnabledPresence` registers the two
    /// in-memory defaults into DI over the notification channel —
    /// substrate only: the deployment exposes its own API over them and
    /// mounts `PresenceContext.provider` client-side (awareness only —
    /// not co-editing).
    Presence: PresenceMode
    /// Phase 535 — CRDT co-editing substrate selection, the merge-free
    /// tier above Phase 442's awareness floor. Default `NoCrdtDocuments`
    /// — no `ICrdtDocumentStore` in DI, no allocation, no
    /// `_platform.crdt` fan-out; an existing deployment that upgrades
    /// stays byte-for-byte identical until it opts in (GP 11 + GP 13).
    /// `EnabledCrdtDocuments` registers the single-instance in-memory
    /// log, wrapped in the notification-channel relay. Substrate only:
    /// the deployment exposes its own API over the resolved store, and
    /// the CRDT library itself is a client-side npm dependency of the
    /// consuming app — the server carries none.
    ///
    /// Phase 756 — `PersistentCrdtDocuments policy` selects the durable
    /// arm instead: the same relay over `BlobCrdtDocumentStore`, which
    /// keeps each document's log in the composed `IBlobStorage` so a
    /// co-edited document survives a restart. The in-memory case stays
    /// the dev default, mirroring `EventStore`'s
    /// `InMemoryOnly | PersistentBlobBacked _` split.
    CrdtDocuments: CrdtDocumentMode
    /// Phase 637 — server-authoritative module-visibility profiles.
    /// Default `NoModuleVisibility` — no store in DI, no admin API
    /// mounted, no profile read on the accessible-modules path, and the
    /// resolution the client receives is always `None`, so an existing
    /// deployment is byte-for-byte unchanged until it opts in (GP 11 +
    /// GP 13). `SurfacingModuleVisibility` curates navigation;
    /// `EnforcedModuleVisibility` additionally 404s an excluded module's
    /// declared route prefixes. See the `ModuleVisibilityMode` cases for
    /// what each buys and what route hardening can and cannot reach.
    ModuleVisibility: ModuleVisibilityMode
    /// Phase 555 — dual control (the two-person rule) over sensitive admin
    /// mutations. Default `SingleAdmin` — no pending-approval store is
    /// registered, no decorator wraps `IPermissionStore`, no approval blob
    /// is written or read, and every admin write applies exactly as it did
    /// before this field existed (GP 11 + GP 13).
    ///
    /// `DualControl settings` captures a gated write as a pending record
    /// naming its proposer and its exact payload; a SECOND, DISTINCT
    /// administrator approves or rejects, and only approval applies it.
    /// `settings.Scope` chooses between every widening permission write
    /// and only those touching a module that declares a Phase 551
    /// `GrantPolicy`; `settings.PendingTtlMinutes` bounds how long a
    /// proposal stays approvable.
    ///
    /// Composes WITH Phase 551 rather than instead of it, and in a fixed
    /// order: the module's declared grant policy is evaluated first, so a
    /// write the module would never admit is refused outright rather than
    /// parked awaiting an approval that could not have applied it.
    AdminMutationPolicy: AdminMutationPolicy
    /// Phase 552 — the consented-grant registry backing a module's declared
    /// `RequiresCounterpartyApproval` policy. Default `NoGrantConsentStore`
    /// — nothing registered, nothing read, and that arm keeps refusing
    /// every grant exactly as Phase 551 shipped it (GP 11 + GP 13).
    ///
    /// Composing a store is only half: signatures are checked by an
    /// `IGrantConsentVerifier`, and the default one is built over an EMPTY
    /// keyring, so until the deployment registers its counterparty keys
    /// every record denies with `consent-unknown-key`. That is deliberate
    /// — a registry that admitted unverified records would be worse than
    /// the refusal it replaced.
    GrantConsent: GrantConsentMode
    /// Phase 445 — the platform backup / restore coordinator. Default
    /// `NoBackup` — nothing registered, no scheduled job, no health probe,
    /// and the deployment is byte-for-byte its pre-445 self (GP 11 + GP 13).
    /// `BackupEnabled` needs an `IBackupTarget` in DI (the destination
    /// `IBlobStorage`); the compose validator refuses startup without one.
    Backup: BackupMode
    /// Phase 594 — the data-vocabulary packs this deployment pins. Default
    /// `[]` — no pack pinned, so the composition validator's
    /// `vocabulary-typename-unknown` / `vocabulary-schema-mismatch` rules
    /// degrade to no-ops and the deployment is byte-for-byte unchanged
    /// (GP 11 + GP 13). A pinned pack governs its namespace as a closed set:
    /// a registered data type whose name falls under the namespace must
    /// match a pack entry, and a `DeclaredDataSchemas` schema for a governed
    /// name must not drift from it. Cross-instance agreement is by pinned
    /// copy — the pins surface on the `PeerSurface` descriptor.
    PinnedVocabularyPacks: DataVocabularyPack list
    /// Phase 594 — the deployment's declared data-type schemas, checked
    /// against `PinnedVocabularyPacks` at preflight. Default `[]` — nothing
    /// declared, so the schema-drift rule finds nothing to compare (GP 13).
    /// A data type carries no field schema at registration, so a deployment
    /// that wants field/type drift caught declares the schema here, keyed by
    /// `TypeName`; the squatting rule (`vocabulary-typename-unknown`) needs
    /// no declaration — it reads the registered names from the manifest.
    DeclaredDataSchemas: VocabularyEntry list
    /// Phase 583 — the module set this deployment expects to compose, as
    /// the consumer declares it. `None` (the default) leaves the
    /// composition validator's `client-server-module-parity` rule
    /// **dormant** — it evaluates one `match` and yields nothing, so an
    /// existing deployment is byte-for-byte unchanged (GP 11 + GP 13).
    /// `Some names` makes the composed module set a checked assertion:
    /// preflight fails naming what was declared-but-not-composed and
    /// composed-but-not-declared.
    ///
    /// Parity between the two roots comes from declaring the SAME list on
    /// `ClientConfig.ExpectedModules`, which `ModuleParityValidator`
    /// checks at client boot — two sets equal to the same set are equal
    /// to each other. The entries are the cross-tier identity token: the
    /// server's `ServerModule.Name`, which is also the client's
    /// `ModuleDefinition.Id` (the identity law on
    /// `ModuleIdentity.componentIdOf`).
    ///
    /// Distinct from `ModuleNames`, which declares the RBAC-visible set
    /// the permission system reports and filters on — legitimately a
    /// subset of what a deployment composes, and therefore not usable as
    /// a parity assertion.
    ExpectedModules: string list option

    /// Phase 6m — explicit operator attestation that an AI deployment
    /// admitting an `Anonymous` surface, with no rate-limit policy
    /// resolving for `AnonymousKind` and a platform-paid provider wired,
    /// is nonetheless cost-bounded by controls the SDK cannot see
    /// (per-IP gating or request budgets at the proxy / CDN / WAF).
    /// Default `false` — `AnonymousAIModeValidator` refuses startup,
    /// because an unauthenticated caller driving a platform-funded
    /// provider is unbounded spend with no per-user attribution.
    ///
    /// Like `AcceptStickyRoutedAiInMultiInstance`, setting this
    /// **degrades the refusal to a `Warning` rather than clearing it**:
    /// upstream rate limiting is an assertion about someone else's
    /// infrastructure, so the residual exposure stays visible in the
    /// HealthMonitorUI Preflight tab / `/dev/inspect` Validators panel.
    ///
    /// Override via `TOOLUP_ACCEPT_ANONYMOUS_MODE_WITH_AI=1`.
    AcceptAnonymousModeWithAI: bool

    /// Phase 828 — self-hosted log-store selection. Default:
    /// `NoLogStore` — no `ILogStore` is registered, the resolved
    /// `ILogger` is not decorated, no database file is opened and no
    /// retention sweep runs, so a deployment shipping its logs to an
    /// external aggregator pays nothing (GP 13) and boots byte-for-byte
    /// as it did before this substrate existed (GP 11).
    /// `SqliteLogStore cfg` records every log line into a local SQLite
    /// database and makes it searchable in-platform.
    LogStore: LogStoreMode
}