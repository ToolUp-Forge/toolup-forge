# Architecture

ToolUp Platform separates **infrastructure** (what the SDK provides) from **domain** (what modules provide). Composition roots wire them together.

## Three-tier package shape

The Platform itself, plus every cross-tier companion (`ToolUp.AI`, `ToolUp.RAG`, `ToolUp.KnowledgeBase`, `ToolUp.Forms`, etc.), ships as a per-tier set of packages:

- **`.Core`** — shared types + interfaces. No server or client deps. Ships both the .NET DLL and source under `fable/` for Fable consumers.
- **`.Server`** — Giraffe-over-ASP.NET-Core server implementation. Depends on `.Core`. DLL-only nupkg.
- **`.Client`** — Fable + Elmish + Feliz client surface. Depends on `.Core`. Ships source under `fable/`.
- **`.Build`** (Platform only) — FAKE pipeline targets.

This per-tier split exists so consumers pull just what they need. A serverless function consuming the platform's interfaces but providing its own runtime takes `.Core` only. A pure API service takes `.Core` + `.Server`. A full-stack consumer takes `.Core` + `.Server` + `.Client`.

The cross-tier companions repeat this shape. Provider companions (`ToolUp.AIProviders.Claude`, `ToolUp.Storage.AwsS3`, `ToolUp.AuditSinks.S3Archive`, etc.) are single-side packages — they implement one or more interfaces and ship as a single nupkg.

## Composition roots

A consuming app has two thin composition roots (server + client) that list modules and call the run pipeline. Everything else — routing, scope resolution, auth wiring, default in-process implementations — is in the SDK.

### Server

The server composition root assembles every module as a `ServerModule` record, composes them into an `ServerApp` / `AIServerApp` / `RAGServerApp` pipeline, and calls `.run`.

```fsharp skip=fragment
let mySalesModule =
    ServerModule.create "SalesAnalysis"
    |> ServerModule.withGuardedApi salesAnalysisApi
    |> ServerModule.withDataTypes [ salesDataType ]
    |> ServerModule.withConfig salesConfigSchema

ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with Port = 5000; Surfaces = Surfaces.individual }
|> ServerApp.withAuth authProvider
|> ServerApp.withStorage blobStorage
|> ServerApp.addModules [ mySalesModule; (* … *) ]
|> ServerApp.run
```

`ServerApp.run` returns an `int` exit code — slot directly into `[<EntryPoint>]`.

For AI / RAG, use the flat-superset variants:

```fsharp
// + AI
AIServerApp.create (aiProviderFactory, aiConfigStore)
|> AIServerApp.withConfig config
|> AIServerApp.addModules modules
|> AIServerApp.withAITools AITools.allTools
|> AIServerApp.run

// + RAG (which wraps AI)
RAGServerApp.create (aiProviderFactory, aiConfigStore, embeddingProvider)
|> RAGServerApp.withConfig config
|> RAGServerApp.addModules modules
|> RAGServerApp.withAITools AITools.allTools
|> RAGServerApp.run
```

`AIServerApp` is a flat superset of `ServerApp` — same fluent shape, AI-specific helpers added. `RAGServerApp` is a flat superset of `AIServerApp`. There's no "compose" magic; each tier adds its own surface.

### Client

```fsharp
open ToolUp.Platform

let modules : ErasedModule list = [
    SalesAnalysis.ClientView.register ()
    // … other modules
]

Client.run
    { ClientConfig.defaults with AppName = "MyApp"; Surfaces = Surfaces.individual }
    modules
```

For AI, wrap the shell with `AIClientConfig.withAIAssistant`:

```fsharp
let aiMode = ConfiguredAIAssistant { Name = "Aria"; Icon = "/svg/spark.svg"; ShowSidePanel = true }
AIClientConfig.run aiMode { ClientConfig.defaults with AppName = "MyApp"; Surfaces = Surfaces.individual } modules
```

The Elmish shell handles sidebar navigation, module state management, file management UI (when modules declare data types), team management UI (when `Surfaces` includes a `Team` profile), and config admin UI.

## What the SDK auto-injects

`ServerApp.run` automatically adds:

- **Auth provider** — `HeaderAuthProvider` by default (trusts `X-User-Id`, dev-only) or whatever you `withAuth`ed. `withAuth` is only required when `ServerConfig.Surfaces` includes a non-Anonymous profile; pure-Anonymous deployments make `withAuth` optional.
- **Subject resolver** — single `ISubjectResolver` (default `DefaultSubjectResolver`) resolves a `Subject` per request from the validated share-token claim, auth-provider user, active team, or anonymous session id. See [`surfaces.md`](surfaces.md#request-resolution-flow) for the resolution order.
- **Team store** — auto-wired when `ServerConfig.Surfaces` contains any `Team _` profile. Default blob-backed `BlobTeamStore` unless you `withTeamStore`ed.
- **Share-token store** — auto-promotes to `BlobShareTokenStore` when `ServerConfig.Surfaces` contains a `ClaimBearer _` profile and `ShareTokenStore` is unset.
- **File management API** — when any registered module has data types.
- **Config admin API** — when any registered module declares a `ModuleConfigSchema`.
- **Audit log** — `AuditLog` backed by `IEventStore`, sourcing events under `_platform.audit`.
- **Notification channel** — `InMemoryNotificationChannel` by default; replaceable.
- **Giraffe response-helper services** — `INegotiationConfig`, `Json.ISerializer`, and `Xml.ISerializer`, so route handlers can use Giraffe's stock helpers (`json`, `xml`, `negotiate`, and the negotiating `RequestErrors.*` / `ServerErrors.*` / `Successful.*` families) out of the box. The `json` helper is backed by the SDK's System.Text.Json wire options (`ToolUp.Remoting.Json.SystemTextJson.FableConverters`), so its output matches the platform wire format for F# records / options / DUs — not Giraffe's default camelCase serialization. Every registration uses `TryAdd` semantics and runs after consumer/companion service config, so registering your own `Json.ISerializer` / `INegotiationConfig` overrides the SDK default.
- **Health / ready endpoints** — `/health` (liveness) + `/ready` (readiness).
- **Config preflight** — runs every registered `IConfigValidator` before HTTP binds; refuses to start on `Error` outcomes.
- **Metrics endpoint** — `/metrics` (OpenMetrics text) when `MetricsEndpoint` is enabled.
- **Optional features** behind explicit opt-in: job scheduler, data ingestion, entity store, encryption-at-rest, audit sinks, rate limiting, security headers, transactional notification sinks, etc.

`Client.run` adds:
- The Elmish shell with sidebar navigation
- Built-in `Data Manager` module (file upload + per-data-type management) — auto-injected when modules declare data types
- Built-in `Team Manager` module — auto-injected when `ClientConfig.Surfaces` includes a `Team` profile
- Built-in `Platform Admin` sidebar group with role-management, health monitoring, and Platform KB administration modules (gated by `PlatformRole`)
- Built-in `Users` admin module — **opt-in** (default off). Set `ClientConfig.PlatformUsers = DefaultPlatformUsers` to add it to the Platform-Admin sidebar group. It lists every principal the substrate has evidence for (`IPlatformTenantApi.ListPrincipals` — a derived, read-only projection over memberships, `user-*` scopes, and sign-in audit), flags team-less accounts via a filter toggle, and drives the tenant-lifecycle offboard flow (preview → confirm → summary, honouring the offboard-confirmation-token mode) per user against the `user-<id>` scope. The default `NoPlatformUsers` omits it entirely, so an existing deployment is byte-for-byte unchanged; pair with `ServerConfig.TenantLifecycle = EnabledTenantLifecycle` server-side for the per-row offboard actions (the list still renders without it — the actions degrade to an error banner).
- Built-in `ToastCentre` — fixed-position toast renderer subscribing to `NotificationClient`
- Built-in `AI Settings` module — auto-injected when `ClientConfig.Surfaces` includes any non-Anonymous profile
- Notification client over SSE
- Processed-data context (modules consume processed data via `React.useContext`)

## How modules plug in

A module is a single F# project ([see `modules.md`](modules.md)) with four files: `SharedTypes.fs`, `Server.fs`, `ClientModel.fs`, `ClientView.fs`. The module fsproj compiles `Shared` + `Server`; the Client files are injected into the consumer's client project via the module's `.Client.props`.

A module is **one Elmish MVU**. A page is a sidebar-visible entry rendered against that MVU. Modules can be single-page (the default, returning a tuple `ReactElement * ReactElement` for the left/right split-panel layout) or multi-page (declaring multiple `PageConfig` entries with per-page views, each returning a `PageContent` value picking its layout shape).

Modules declare:
- **What they are** — `ClientModule.Name`, `ClientModule.Icon`
- **What they need** — `NeedsData` (which `DataType`s their `Init` consumes from the processed-data context)
- **What they provide** — `ProvidesProcessedData` (which `DataType`s they emit), `DataTypes` (server-side `DataType` records with detect + process functions)
- **How they behave** — `Init` / `Update` / `View`

The shell wires everything else — file uploads, persistence, scope resolution, AI tool registration, config storage, notification routing.

## Subject and scope resolution

Every request resolves a `Subject` (the four-case DU of `AnonymousSession` / `AuthenticatedUser` / `TeamMember` / `ClaimBearer`) via the registered `ISubjectResolver`. The default `DefaultSubjectResolver` runs a single four-step algorithm: a validated `ShareTokenClaim` stashed by `ShareTokenAuthMiddleware` → `ClaimBearer`; an authenticated user with active team scope → `TeamMember`; an authenticated user without team scope → `AuthenticatedUser`; otherwise → `AnonymousSession`. Team-scope probes cache for 5 minutes (sliding) with `MembershipChanged` invalidation.

`StorageScope` (`{ ScopeId; Container; Persist }`) falls out of the resolved `Subject` and the matching `SurfaceProfile`:

| Subject | `ScopeId` | `Container` | `Persist` |
|---|---|---|---|
| `AnonymousSession sid` | `sid` | `session-{sid}` | from `AnonymousConfig.Persistence` (default `Ephemeral`) |
| `AuthenticatedUser uid` | `uid` | `user-{uid}` | from `AuthenticatedUserConfig.Persistence` (`Persistent` for individual; `Ephemeral` for trial) |
| `TeamMember (_, tid)` | `tid` | `team-{tid}` | from `TeamConfig.Persistence` (almost always `Persistent`) |
| `ClaimBearer claim` | `claim.ScopeId` | `claim.ScopeId` | always `true` |

`SessionFileStore` uses `scope.Persist` to decide whether to write through to `IBlobStorage`. Per-scope blob containers keep data isolated. See [`surfaces.md`](surfaces.md#persistence-routing) for the full per-subject persistence routing.

## Access control

Every request resolves an `AccessContext`:

```fsharp
type AccessContext = {
    UserId: string
    TeamId: string option
    Subject: Subject
    ModulePermissions: Map<string, ModulePermission list>
    PlatformRole: PlatformRole option
}
```

`AccessContext.UserId` carries the session id for `AnonymousSession`, the user id for `AuthenticatedUser` / `TeamMember`, and the claim's `AttributedHandle` (or a synthetic `claim:{tokenId}` when unset) for `ClaimBearer`. Handlers that need team scope match on `match ctx.Subject with TeamMember (uid, tid) -> …` — the compiler refuses the three other cases, so team-scoped code structurally cannot forget to check for membership.

Currently the SDK does not enforce per-module permissions beyond the user's choice via `IPermissionStore`. Module APIs are wrapped in `makePermissionGuardedApi` which checks `ModulePermissions` before each call. Empty map = unrestricted. `PlatformRole.PlatformAdmin` is the deployment-wide admin role.

## Notifications

The SDK ships a single notification channel abstraction:

```fsharp skip=fragment
type INotificationChannel =
    abstract Publish: scopeId: string -> Notification -> Async<unit>
    abstract Subscribe: scopeId: string -> filter: (NotificationKind -> bool) -> handler: ... -> Async<Guid>
    abstract Unsubscribe: Guid -> Async<unit>
```

Notifications carry five kinds: `SystemMessage`, `JobProgress`, `JobComplete`, `RefreshData`, `CustomNotification`. Plus three transactional kinds (`TransactionalEmail`, `TransactionalSms`, `MobilePush`) that ride the same envelope but bypass the wire transport via `DispatchingNotificationChannel` so PII never crosses pub/sub topics.

The default in-memory channel works single-instance. The Redis companion (`ToolUp.NotificationChannels.Redis`) replaces it for distributed deployments. Per-scope topic isolation is structural (one topic per `ScopeId`), not a post-hoc filter.

A single SSE endpoint at `/api/notifications` serves all subscribers. The client-side `NotificationClient` opens one `EventSource`, routes named events to per-kind subscribers, and returns a dispose thunk.

## Events + audit

`IEventStore` provides append-only, queryable event storage:

```fsharp
type IEventStore =
    abstract Write: Event -> Async<unit>
    abstract ReadByType: SourceModule: string -> EventType: string -> Async<Event list>
    abstract ReadByCorrelation: CorrelationId: Guid -> Async<Event list>
```

Default `InMemoryEventStore` for dev; `PersistentEventStore` (blob-backed, optional retention policy) for production. Modules emit domain events; the SDK emits platform events under `_platform.*` source modules.

The `IAuditLog` interface sits on top of `IEventStore` and records `AuditEvent` cases under `SourceModule = "_platform.audit"`. The shipped events cover authentication (`UserLoggedIn`), team operations (`TeamCreated`, `MemberAdded`, etc.), file operations, encryption-key lifecycle, audit-sink delivery, health-state changes, and many more.

For compliance archival, the `IAuditSink` substrate replicates every `_platform.audit` event to one or more external sinks (Splunk HEC, Datadog Logs, S3 Object Lock archives). Replication is at-most-once steady-state, at-least-once across restart, with per-`(sinkName, scopeId)` cursors in `IBlobStorage`. See [`events.md`](events.md).

## Background jobs

Opt in via `ServerConfig.JobScheduler = InProcessJobScheduler`. The default scheduler is a `BackgroundService` ticking every minute aligned to wall clock, with per-`JobId` `SemaphoreSlim` for concurrent-tick safety. Jobs are defined by:

```fsharp
type JobDefinition = {
    JobId: JobId
    HandlerName: string
    Trigger: Trigger  // Cron expression | OnEvent | Manual
    Retry: JobRetryPolicy
    IdempotencyKey: IdempotencyKey option
    Precision: JobPrecision  // Minute (Second precision rejected at registration)
}
```

The cron parser supports `*` / values / commas / `*/N` (ranges + named months deferred). `OnEvent` triggers fire when matching events hit the event store (via the `JobNotifyEventStore` decorator). `Manual` triggers fire on explicit `TriggerOnce` calls.

Five lifecycle events emit to `IEventStore` under `_platform.jobs`. Dead-letter triggers a `SystemMessage`-Warning notification. See [`jobs.md`](jobs.md).

For multi-silo deployments, a distributed companion (Akka.NET / Orleans / Hangfire) is the future migration path; the single-instance default is fine for many deployments. The `IJobSchedulerContract` test pack is the conformance bar — any implementation passes the same 15 tests.

## Data ingestion

Opt in via `ServerConfig.DataIngestion = EnabledDataIngestion`. Substrate:

- `IDataSource` — connector contract (`Connect` / `ListTables` / `GetSchema` / `Query` over `DataSourceCallContext`).
- `IDataSourceConfigStore` — per-scope connector configurations (blob-backed default).
- `IDataIngestor` — orchestrator. Resolves config → matches connector by `Kind` → resolves credential via `ISecretStore` → calls `Connect` then `Query` → writes bytes through `IDataObjectStore.Save(..., Versioned)` so each refresh creates a new version → records `IngestionRun` + emits lifecycle events under `_platform.dataingestion`.

Triggered + scheduled refresh through the `IJobScheduler` (handler `"_platform.dataingestion.run"`). Admin API write paths gated by team role.

The default `InMemoryDataSource` (Kind `"InMemory"`) ships for the contract test pack + dev harness. Real connectors (BigQuery, Redshift, GA4, Strava, etc.) are deployment-specific companions consumers write themselves.

## Encryption at rest

The `EncryptedBlobStorage` decorator wraps any `IBlobStorage` and applies AES-GCM envelope encryption transparently. Envelope format: `[Magic:4 "TOBL"][KeyIdLen:1][KeyId:N][Nonce:12][Tag:16][Ciphertext:M]`.

Two shipped key resolvers:
- `SingleKeyResolver` — one platform-wide key.
- `PerScopeKeyResolver` — per-tenant; `IMemoryCache` with 5-min sliding TTL; `DestroyKey` for crypto-shred (tenant offboarding for GDPR / contract termination — complete on the serving replica when the call returns, minute-grain replica-fanout time across the fleet via an `INotificationChannel` broadcast).

Custom resolvers (per-`(scopeId, userId)`, BYOK, KMS-backed) plug in against the same interface. Provider-specific preflight validators (`AwsS3EncryptionAtRestValidator`, `AzureBlobEncryptionAtRestValidator`, `GcsEncryptionAtRestValidator`) confirm encryption-at-rest is enabled at the bucket level.

## Health + observability

- `/health` — liveness; runs probes with `HealthKind = Liveness`.
- `/ready` — readiness; runs all probes.

Probes implement `IHealthCheck`:

```fsharp
type IHealthCheck =
    abstract Name: string
    abstract Kind: HealthKind  // Liveness | Readiness
    abstract Timeout: TimeSpan
    abstract Check: unit -> Async<HealthResult>  // Healthy | Degraded of string | Unhealthy of string
```

Companions self-register via `services.AddSingleton<IHealthCheck>(instance)`; deployments wire them via `ServerApp.withHealthCheck`. The shipped companion probes cover Redis, AI providers (Claude / OpenAI), embedding providers, HNSW vector store, storage providers (AWS / Azure / GCS), and notification sinks (SMTP / SendGrid / Twilio / WebPush).

The `HealthStateTracker` (opt-in via `ServerConfig.HealthStateTracking = true`) runs probes on a wall-clock-aligned 1-min tick and emits `HealthStateChanged` audit events after 3 consecutive observations of a new status (single-observation flaps absorbed). Operator surface: the SDK-built-in `HealthMonitorUI` admin module.

The metrics layer (`IMetricsSink`) emits per-request and SDK-internal metrics. Default `PrometheusMetricsSink` exposes `/metrics` in OpenMetrics text format with per-metric cardinality cap. The OpenTelemetry companion (`ToolUp.Metrics.OpenTelemetry`) implements the same interface over BCL `System.Diagnostics.Metrics.Meter` for OTLP export.

For tracing, the `Logger.trace` API + per-source filter via `TOOLUP_TRACE_CATEGORIES` env var gives selective per-source log enablement.

## Dev diagnostics

The `/dev/inspect` endpoint (gated by `ServerConfig.EnableDevEndpoints`, default `false` in production builds; auto-enabled by the reference deployment in `#if DEBUG`) surfaces the caller's resolved `AccessContext` / `StorageScope`, registered modules + per-module data types, data-catalog summary, registered route handlers, `IServiceCollection` descriptor list, health-check snapshot, and config preflight snapshot. Caller-scope only — never enumerates across teams.

`IDevDiagnosticsContributor` is the extension point for companions wanting to surface their own internals (AI fast-path stats, ingestion queue depth, etc.).

## What this docs site does NOT cover

- **Source-level walkthroughs of every module** — that's better read from the source code with the type definitions in scope.
- **Phase-by-phase historical context** — irrelevant to a new reader. Architecture is described as it stands today.
- **Commercial use cases** — the SDK is sector-agnostic. Per-vertical commercial applications are downstream consumers and document themselves.

If something architectural feels under-explained, the source is the source-of-truth and the `samples/HelloWorld/` reference deployment is the worked example.
