namespace ToolUp.Platform

open System
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.Providers
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.TransientFault
open ToolUp.Platform.Tracing
open ToolUp.Platform.Usage
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.Server

// ─── ServerModule — per-module contribution bundle ───────────────────
//
// Each server-side module exposes a single `serverModule : ServerModule`
// value that consolidates everything the module contributes: its HTTP
// handlers (typically one Fable.Remoting api + permission guard),
// declared `DataType`s, optional RAG `VectorisationHandler`s, and an
// optional team-editable `ModuleConfigSchema`. The composition root
// lists these and `ServerApp.addModule` fans each record into the right
// collection.

/// A server module's full contribution to the application.
type ServerModule = {
    /// Unique module identifier — must match the client `ClientModule.Definition.Id`.
    /// Used as the permission key in `makePermissionGuardedApi` and populated
    /// into `ServerConfig.ModuleNames` for RBAC / sidebar enumeration.
    Name: string
    Handlers: HttpHandler list
    DataTypes: DataType list
    VectorisationHandlers: VectorisationHandler list
    ConfigSchema: ModuleConfigSchema option
    /// Handlers this module exposes to the cross-module query bus
    /// (Phase 6b). Accumulated across every registered `ServerModule`
    /// at `compose` time and handed to `InMemoryModuleQueryBus`.
    /// Modules that don't expose queries leave the list empty.
    QueryHandlers: ModuleQueryHandler list
    /// AI tools this module exposes to the agent loop. Each entry pairs
    /// an `AIToolDefinition` (metadata: name, description, parameter
    /// schema; declared in the module's `Server.fs` next to the routine
    /// being exposed) with an `HttpContext -> string -> Async<string>`
    /// executor (the runtime wiring that parses the JSON args, calls
    /// the module's pure routine, and returns a serialised JSON
    /// result). `composeWithAI` aggregates these across every
    /// registered module and registers them with the `AIToolRegistry`.
    /// Modules that don't expose tools leave the list empty.
    AITools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list
    /// Phase 9e — module-scoped metric declarations. Each
    /// `MetricDefinition` is auto-namespaced to
    /// `toolup.{moduleName}.{name}` at `addModule` time (the module
    /// declares the post-namespace name only; the `toolup.` prefix is
    /// reserved for the SDK and a module declaring it is rejected at
    /// sink construction). `compose` folds these into the in-process
    /// `PrometheusMetricsSink` alongside the SDK standard metrics.
    /// Modules that emit no custom metrics leave the list empty.
    MetricDefinitions: Metrics.MetricDefinition list
    /// Per-route latency ceilings for this module's endpoints. Each
    /// entry is a case-insensitive route-prefix → `TimeSpan` pair that
    /// `RequestTimingMiddleware` consults instead of the global
    /// `ServerConfig.SlowRequestThreshold`. Modules whose handlers have
    /// a happy-path latency genuinely above the 1s default (e.g. AI
    /// inference, large uploads, multi-stage ingestion) declare their
    /// own ceilings here rather than forcing the composition root to
    /// hardcode SDK route strings. `addModule` merges per-module
    /// overrides into the aggregate `ServerConfig.SlowRequestThresholdOverrides`
    /// at `run` time. Empty (the default) — modules without
    /// latency-sensitive endpoints leave it empty.
    SlowRequestThresholdOverrides: Map<string, TimeSpan>
    /// Phase 66 Stream B.3 — module-level default for the
    /// `SurfaceEnforcementMiddleware` matrix. Applied to every route
    /// prefix this module declares via `RoutePrefixes`. Defaults to
    /// `SurfaceRequirement.userOrTeam` (the strict global fallback per
    /// design §3.0 OQ6); modules serving anonymous public surfaces
    /// declare `withDefaultSurfaceRequirement SurfaceRequirement.public_`
    /// (or another admit set) explicitly. The default is opt-in only —
    /// modules that declare no `RoutePrefixes` are not entered into the
    /// `SurfaceRequirementRegistry.ModulePrefixes`, so behaviour stays
    /// byte-identical for modules predating B.3. Per-endpoint
    /// overrides live on `RouteSurfaceRequirements`.
    DefaultSurfaceRequirement: SurfaceRequirement
    /// Phase 66 Stream B.3 — route prefixes this module owns. Used by
    /// `addModule` to register the module's `DefaultSurfaceRequirement`
    /// against each prefix in the `SurfaceRequirementRegistry`.
    /// Empty by default; modules that publish a `RouteSurfaceRequirements`
    /// exact override do NOT need to also declare a prefix here.
    /// Case-insensitive `StartsWith`-style matching at resolution time
    /// (per `SurfaceRequirementRegistry.resolve`). Common shapes:
    /// `[ "/api/forms/admin/" ]` for an admin sub-tree;
    /// `[ "/api/forms/public/" ]` for a public-submit sub-tree
    /// (typically paired with `DefaultSurfaceRequirement =
    /// SurfaceRequirement.claimBearerOnly`).
    RoutePrefixes: string list
    /// Phase 66 Stream B.3 — exact `(method, path)` → `SurfaceRequirement`
    /// overrides for this module. Win over any matching `RoutePrefixes`
    /// default at resolution time. Use for one-off public endpoints
    /// inside an otherwise-authenticated module (e.g. a single
    /// `claimBearerOnly` POST on a `userOrTeam` admin module). Method
    /// is normalised to upper-case and path to lower-case in the
    /// registry — declarations match regardless of the casing used here.
    RouteSurfaceRequirements: ((string * string) * SurfaceRequirement) list
    /// Phase 9b.B — compose-time declarations of recurring / event-driven
    /// background jobs this module owns. Each entry pairs an
    /// `IJobHandler` with a `Trigger`; `addModule` accumulates them onto
    /// `ServerApp.ScheduledJobs` and `ComposeJobs.registerScheduledJobDeclarations`
    /// applies the `RegisterHandler` + per-scope `Schedule` against the
    /// resolved `IJobScheduler` after the singleton is built. Empty
    /// list (the default) is the pre-9b.B behaviour: a module wanting a
    /// cron job had to resolve `IJobScheduler` from the built
    /// `IServiceProvider` itself, with no app-level handle. When the
    /// deployment is on `ServerConfig.JobScheduler = NoJobScheduler`,
    /// declarations log a single `Warn` and skip registration — a module
    /// declaring jobs in an unscheduled deployment is a config mismatch,
    /// not a crash.
    JobHandlers: ScheduledJobDeclaration list
    /// Phase 165 — optional opt-in module-binding stamp this module
    /// presents to the `addModule` gate. `None` (the default) is an
    /// unstamped module: it loads unchanged unless a deployment-level
    /// policy requires stamps. A `Some` stamp must verify under a
    /// configured `IModuleBindingVerifier`'s trust anchors, else the
    /// module is dropped. Populated by a deploy-time stamper; forge only
    /// reads it here.
    BindingStamp: ModuleBindingStamp option
    /// Phase 279 — optional explicit stable `ComponentId` for this
    /// module. `None` (the default) derives the id from `Name` at first
    /// registration (`ComponentId.ofModule Name`, GP 11) — byte-for-byte
    /// unchanged for a module that declares nothing. `Some id` (set via
    /// `ServerModule.withComponentId`) makes the id independent of the
    /// display `Name`, so renaming `Name` does not churn the identity
    /// telemetry / introspection surfaces correlate against. Either way
    /// the resolved id is accumulated onto `ServerApp` and checked for
    /// uniqueness at compose time.
    ComponentId: ComponentId option
}

module ServerModule =
    let create (name: string) : ServerModule = {
        Name = name
        Handlers = []
        DataTypes = []
        VectorisationHandlers = []
        ConfigSchema = None
        QueryHandlers = []
        AITools = []
        MetricDefinitions = []
        SlowRequestThresholdOverrides = Map.empty
        DefaultSurfaceRequirement = SurfaceRequirement.userOrTeam
        RoutePrefixes = []
        RouteSurfaceRequirements = []
        JobHandlers = []
        BindingStamp = None
        ComponentId = None
    }

    /// Attach a permission-guarded Fable.Remoting api factory. Uses the
    /// module's `Name` as the RBAC key, so callers never duplicate it.
    ///
    /// Phase 69d.tail — the module-access gate (`canAccessModule`) is
    /// module-level RBAC and survives per-method attribute adoption;
    /// the wrapped record's METHODS must each carry an authorisation
    /// attribute or the startup classifier refuses to start (default-on).
    let withGuardedApi<'T> (apiBuilder: HttpContext -> 'T) (m: ServerModule) : ServerModule = {
        m with
            Handlers = m.Handlers @ [ RemotingHelpers.permissionGuardedApiCore m.Name apiBuilder ]
    }

    /// Attach an un-guarded handler (advanced — normally prefer `withGuardedApi`).
    let withHandlers (handlers: HttpHandler list) (m: ServerModule) : ServerModule = {
        m with
            Handlers = m.Handlers @ handlers
    }

    let withDataTypes (dts: DataType list) (m: ServerModule) : ServerModule = { m with DataTypes = dts }

    let withVectorisation (vhs: VectorisationHandler list) (m: ServerModule) : ServerModule = {
        m with
            VectorisationHandlers = vhs
    }

    let withConfig (schema: ModuleConfigSchema) (m: ServerModule) : ServerModule = { m with ConfigSchema = Some schema }

    /// Expose one or more cross-module query handlers (Phase 6b). Use
    /// `ModuleQueryHandler.typed` to keep request and
    /// response types strongly typed at the registration site. The
    /// module's `Name` becomes the routing key — callers ask for
    /// `("ThisModule", QueryKey)`.
    let withQueryHandlers (handlers: ModuleQueryHandler list) (m: ServerModule) : ServerModule = {
        m with
            QueryHandlers = m.QueryHandlers @ handlers
    }

    /// Expose one or more AI tools the agent loop can invoke. Each
    /// entry pairs metadata (declared near the routine being exposed)
    /// with an executor (the wiring that calls the routine with parsed
    /// args). `composeWithAI` aggregates tools across every registered
    /// module — apps no longer maintain a separate `AITools.fs`. A
    /// duplicate `Name` across modules raises a clear startup error.
    /// Helpers under `ToolUp.AI.ToolHelpers` (`requireString`,
    /// `requireDecimal`, `fableSerialize`, etc.) handle the recurring
    /// argument-extraction and serialisation boilerplate.
    let withAITools
        (tools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list)
        (m: ServerModule)
        : ServerModule =
        { m with AITools = m.AITools @ tools }

    /// Phase 9e — declare one or more module-scoped metrics. Each
    /// `MetricDefinition` is auto-namespaced to
    /// `toolup.{moduleName}.{name}` at `addModule` time (the module's
    /// `Name` is the namespace segment), so the module declares the
    /// post-namespace name only (`"jobs.processed"`, not
    /// `"toolup.mymod.jobs.processed"`). Declaring a name that already
    /// starts with the reserved `toolup.` prefix is rejected when the
    /// sink is constructed. Modules emit against the namespaced name by
    /// resolving `IMetricsSink` from DI. No-op unless the deployment
    /// opts into `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint`.
    let withMetrics (defs: Metrics.MetricDefinition list) (m: ServerModule) : ServerModule = {
        m with
            MetricDefinitions = m.MetricDefinitions @ defs
    }

    /// Declare a per-route latency ceiling for one of this module's
    /// endpoints. `routePrefix` is matched case-insensitively as a
    /// prefix by `RequestTimingMiddleware`; the longest prefix match
    /// wins, falling back to the global `ServerConfig.SlowRequestThreshold`.
    ///
    /// Use this when a happy-path response time is genuinely above the
    /// 1s default (AI inference, large uploads, multi-stage ingestion) —
    /// without an override, every successful call logs a
    /// `[WRN] slow request:` and drowns real latency-regression signal.
    /// Set the ceiling tighter than the observed p99 so a real
    /// regression still surfaces.
    ///
    /// Example:
    /// ```
    /// ServerModule.create "ChannelAnalysis"
    /// |> ServerModule.withSlowRequestThreshold "/api/ChannelAnalysisApi/GenerateInsight" (TimeSpan.FromSeconds 5.0)
    /// |> ServerModule.withGuardedApi channelAnalysisApi
    /// ```
    let withSlowRequestThreshold (routePrefix: string) (threshold: TimeSpan) (m: ServerModule) : ServerModule = {
        m with
            SlowRequestThresholdOverrides = m.SlowRequestThresholdOverrides |> Map.add routePrefix threshold
    }

    /// Phase 66 Stream B.3 — declare the module's default
    /// `SurfaceRequirement`. Applied (by `addModule`) to every prefix
    /// the module declares via `withRoutePrefix`. Use the named smart
    /// constructors on `SurfaceRequirement` (`public_`, `authenticated`,
    /// `userOrTeam`, `teamScoped`, `anonymousOnly`, `claimBearerOnly`)
    /// or compose a custom admit set.
    ///
    /// Calling this multiple times keeps the last value — the module
    /// has a single module-level default, not a stack. Per-endpoint
    /// overrides live on `withRouteSurfaceRequirement`.
    let withDefaultSurfaceRequirement (requirement: SurfaceRequirement) (m: ServerModule) : ServerModule = {
        m with
            DefaultSurfaceRequirement = requirement
    }

    /// Phase 66 Stream B.3 — register one route prefix this module
    /// owns. The module's `DefaultSurfaceRequirement` is applied to
    /// every request whose path matches the prefix
    /// (case-insensitive `StartsWith`). Longest-prefix wins at
    /// resolution time, so a module declaring `/api/forms/` does not
    /// shadow another module declaring `/api/forms/admin/`.
    ///
    /// Modules that publish only `withRouteSurfaceRequirement`-style
    /// exact overrides need not declare a prefix here; the global
    /// fail-closed default (`SurfaceRequirement.userOrTeam`) applies
    /// to non-overridden routes.
    let withRoutePrefix (prefix: string) (m: ServerModule) : ServerModule = {
        m with
            RoutePrefixes = m.RoutePrefixes @ [ prefix ]
    }

    /// Phase 66 Stream B.3 — declare an exact-match
    /// `SurfaceRequirement` override for one `(method, path)` pair.
    /// Wins over any matching `RoutePrefixes` default at resolution
    /// time. Method is normalised to upper-case and path to lower-case
    /// inside the registry; the declared values may use any casing.
    ///
    /// Use for a single public endpoint inside an otherwise-gated
    /// module — e.g. the Forms public-submit handler declares
    /// `withRouteSurfaceRequirement "POST" "/api/forms/public/submit"
    /// SurfaceRequirement.claimBearerOnly` while the module's
    /// `DefaultSurfaceRequirement` stays `userOrTeam` for its admin
    /// routes.
    let withRouteSurfaceRequirement
        (httpMethod: string)
        (path: string)
        (requirement: SurfaceRequirement)
        (m: ServerModule)
        : ServerModule =
        {
            m with
                RouteSurfaceRequirements = m.RouteSurfaceRequirements @ [ (httpMethod, path), requirement ]
        }

    /// Phase 9b.B — declare a recurring / event-driven background job
    /// this module owns. The SDK's compose pipeline registers `handler`
    /// against `IJobScheduler` under `handlerName` and schedules a
    /// `JobDefinition` with the given `trigger` for the default scope
    /// (`"_platform"`) — no post-`Build` resolution of `IJobScheduler`
    /// required. The tupled signature matches the canonical example:
    /// `ServerModule.withJobHandler ("overdue-scan", handler, CronTrigger "0 8 * * *")`.
    ///
    /// Uses `JobPrecision.Minute` (the in-process default's only
    /// supported precision), empty payload, default retry policy, and
    /// an auto-built per-scope idempotency key with a one-year TTL so
    /// restart re-registration is a no-op. Modules needing per-tenant
    /// fan-out across multiple scopes, a custom retry policy, or a
    /// non-empty payload use `withScheduledJob` with a fully-formed
    /// `ScheduledJobDeclaration` instead.
    ///
    /// When `ServerConfig.JobScheduler = NoJobScheduler`, declarations
    /// emit a single startup `Warn` and skip registration — a module
    /// declaring jobs in an unscheduled deployment is a config mismatch,
    /// not a crash.
    let withJobHandler (handlerName: string, handler: IJobHandler, trigger: Trigger) (m: ServerModule) : ServerModule =
        let declaration = ScheduledJobDeclaration.create handlerName handler trigger

        {
            m with
                JobHandlers = m.JobHandlers @ [ declaration ]
        }

    /// Phase 9b.B — declare a background job with full control over
    /// every `JobRegistration` knob (`Scopes`, `Payload`, `RetryPolicy`,
    /// `ShardKey`, `Precision`, `Idempotency`, `Tags`). Construct via
    /// `ScheduledJobDeclaration.create` + the fluent `with*` helpers;
    /// see `withJobHandler` for the common tupled shorthand.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (m: ServerModule) : ServerModule = {
        m with
            JobHandlers = m.JobHandlers @ [ declaration ]
    }

    /// Phase 165 — attach an opt-in module-binding stamp. The stamp is
    /// checked at `addModule` time against the deployment's configured
    /// `IModuleBindingVerifier` (see `ServerApp.withModuleBindingVerifier`):
    /// a stamp that verifies under a trust anchor loads the module, an
    /// unverifiable stamp drops it. Modules without a stamp leave this
    /// `None` and are unaffected.
    let withBindingStamp (stamp: ModuleBindingStamp) (m: ServerModule) : ServerModule = {
        m with
            BindingStamp = Some stamp
    }

    /// Phase 279 — declare an explicit stable `ComponentId` for this
    /// module. `declaredId` is a bare token (e.g. `"orders-service"`); it
    /// is namespaced under the `module:` slot via `ComponentId.ofModule`,
    /// so the resolved id is independent of the module's display `Name`.
    /// Declaring an explicit id lets a deployment rename the sidebar /
    /// header `Name` without churning the identity that telemetry
    /// correlation, hot-reload, and config-diffing key against. Omitting
    /// the call leaves the id name-derived (GP 11) — byte-for-byte
    /// unchanged. A duplicate resolved id across modules fails at compose
    /// time (`ServerApp.run`) with a readable error.
    let withComponentId (declaredId: string) (m: ServerModule) : ServerModule = {
        m with
            ComponentId = Some(ComponentId.ofModule declaredId)
    }

// ─── Phase 169 — module-load startup observability ───────────────────
//
// `addModule` resolves each module to exactly one outcome. The outcome is
// accumulated by value on `ServerApp` during the (pure) composition pass
// and emitted through `app.Logger` at `run` — a logger is reliably set by
// then, whereas at `addModule` time the composition order does not
// guarantee one. Quiet by default (GP 13): a normally-registered module
// logs at `Debug`, so a stock deployment (nothing filtered, no binding
// gate) prints nothing at the default `Info` level; only a *drop*
// (`ModuleFiltered` / `ModuleBindingRejected`) raises an `Info`/`Warn`.

/// How `addModule` resolved a single module. Machine-readable — a test
/// asserts on the accumulated `(moduleId, outcome)` list directly rather
/// than scraping log text.
type ModuleLoadOutcome =
    /// Passed the name filter and the binding gate (no gate active, or a
    /// stamp that verified) → registered.
    | ModuleRegistered
    /// Dropped by `ModuleFilter`; carries the active filter string.
    | ModuleFiltered of filter: string
    /// A binding verifier was configured, the module carried no stamp, and
    /// the verifier admitted it under its unbound policy.
    | ModuleUnboundAllowed
    /// Dropped by the binding gate; carries the neutral rejection reason
    /// (from the Phase 165 `BindingOutcome`).
    | ModuleBindingRejected of reason: string

// ─── ServerApp — record form of the `compose` arguments ──────────────
//
// `ServerApp` is a drop-in replacement for calling `compose` positionally.
// Fields default to `None`/empty; override only what's needed. `addModule`
// fans a `ServerModule`'s contributions into the right collections, so
// the composition root lists modules and lets the wiring follow.

/// Record form of the `compose` arguments.
type ServerApp = {
    Config: ServerConfig
    Handlers: HttpHandler list
    DataTypes: DataType list
    VectorisationHandlers: VectorisationHandler list
    Auth: IAuthProvider option
    Logger: ILogger option
    Storage: IBlobStorage option
    Notifications: INotificationChannel option
    Extensions: ComposeExtensions
    /// Accumulated from each `addModule` call. Populated into
    /// `ServerConfig.ModuleNames` at `run` time.
    ModuleNames: string list
    /// Accumulated from each `addModule` call that carries a schema.
    /// Populated into `ServerConfig.ModuleConfigs` at `run` time using
    /// the module's `Name` as the `ModuleConfigEntry.ModuleKey`.
    ModuleConfigs: ModuleConfigEntry list
    /// Accumulated `(moduleName, handler) list` across every registered
    /// `ServerModule`. Phase 6b — handed to `InMemoryModuleQueryBus` at
    /// `run` time to build the cross-module query registry.
    QueryHandlerRegistrations: (string * ModuleQueryHandler) list
    /// Accumulated `(moduleName, DataType) list` across every
    /// registered `ServerModule`. Phase 7a — handed to `DataCatalog`
    /// at `run` time so admin UIs and AI tools can enumerate
    /// available data types and their producer modules.
    DataTypeRegistrations: (string * DataType) list
    /// Accumulated AI tool registrations across every `ServerModule`.
    /// Each entry pairs an `AIToolDefinition` with its executor;
    /// `composeWithAI` reads this list (instead of taking a separate
    /// tools parameter) and registers them with the `AIToolRegistry`.
    /// Empty when no module exposes AI tools.
    AITools: (AIToolDefinition * (HttpContext -> string -> Async<string>)) list
    /// Phase 6f — transactional notification sinks. Apps register
    /// adapters for email / SMS / push via
    /// `ServerApp.withTransactionalSink`; `compose` validates that
    /// at most one sink is registered per `Kind`. Empty list (the
    /// default) skips the transactional dispatcher and leaves the
    /// notification channel un-decorated, so deployments without
    /// out-of-band delivery pay zero runtime cost.
    TransactionalSinks: INotificationSink list
    /// Phase 9k — companion-contributed health probes. Apps add one
    /// per backend (Redis multiplexer, AI provider key resolution,
    /// vector store load state, etc.) via
    /// `ServerApp.withHealthCheck`. The SDK aggregator (run near
    /// end-of-compose) feeds every registered probe into BCL's
    /// `MapHealthChecks` pipeline. Empty list = only the three
    /// first-party probes (storage, auth, event store) participate.
    HealthChecks: HealthChecks.IHealthCheck list
    /// Phase 9m — companion-contributed startup config validators.
    /// Apps add one per remote dependency (OIDC issuer, Redis,
    /// SMTP, etc.) via `ServerApp.withConfigValidator`. The SDK
    /// aggregator (run near end-of-compose) walks every registered
    /// validator and aborts startup with a single-line error if any
    /// returns `Error`. Empty list = only the two first-party
    /// validators (blob-storage round-trip, secret-store sentinel)
    /// participate.
    ConfigValidators: ConfigValidation.IConfigValidator list
    /// Phase 22 — optional encryption-key resolver. When `Some`, the
    /// registered `IBlobStorage` is wrapped with `EncryptedBlobStorage`
    /// so every Upload / Download passes through AES-GCM envelope
    /// encryption transparently. The resolver chooses the security
    /// model: `SingleKeyResolver` (platform-wide), `PerScopeKeyResolver`
    /// (per-tenant, with crypto-shred), or a custom impl. `None` (the
    /// default) leaves the storage layer un-decorated — deployments
    /// without app-level envelope encryption pay zero runtime cost.
    EncryptionKeyResolver: IBlobEncryptionKeyResolver option
    /// Phase 19 — entity-type registrations. Each closure receives the
    /// shared `EntityRegistry` and calls `Register<'T>(registration)`
    /// on it. The closure pattern preserves `'T` per registration
    /// without exposing erasure to consumers — they call
    /// `ServerApp.withEntity<'T>(reg)` and the closure carries the
    /// type witness through to compose time. Empty list = no entity
    /// types declared; only meaningful when
    /// `ServerConfig.EntityStore = EnabledEntityStore`.
    EntityRegistrations: (EntityStore.EntityRegistry -> unit) list
    /// Phase 9d — optional override for the per-team compute quota
    /// policy. When `None` (default), `compose` registers the SDK
    /// default — `BlobBackedTeamQuotaPolicy` reading from the reserved
    /// `_platform.usage` config schema when `UsageMetering =
    /// EnabledUsageMetering`, or `NoOpTeamQuotaPolicy` when
    /// `NoUsageMetering`. Deployments needing custom logic (e.g. a
    /// SaaS billing provider that gates by subscription tier rather
    /// than fixed thresholds) supply their own `ITeamQuotaPolicy`
    /// here.
    QuotaPolicy: ITeamQuotaPolicy option
    /// Phase 42.B / 43.A — optional canonical platform-wide BYOK
    /// provider-profile store. The deferred 42.B composition seam: the
    /// AI cutover (Phase 43.A) is its first real caller. When `Some`,
    /// `run` registers it as the `IProviderProfile` DI singleton so any
    /// BYOK consumer (the AI assistant via `AIServerApp`, a rental
    /// gateway, any future app) resolves "which provider / which key"
    /// without depending on the AI assistant companion. `None` (the
    /// default) registers nothing — GP 13 zero footprint; deployments
    /// with no BYOK surface pay no runtime cost. The AI path supplies
    /// this via `AIServerApp.create` / `RAGServerApp.create` (a required
    /// dependency there); non-AI BYOK consumers opt in via
    /// `ServerApp.withProviderProfile`.
    ProviderProfile: IProviderProfile option
    /// Phase 9e — companion-contributed metrics sinks. Each companion
    /// (OpenTelemetry exporter at `src/Metrics/OpenTelemetry/`, a
    /// future StatsD push companion, etc.) registers its sink via
    /// `ServerApp.withMetricsSink`; `compose` wraps the in-process
    /// `PrometheusMetricsSink` plus every companion sink in a
    /// `FanOutMetricsSink` so a single `Increment` call dispatches to
    /// every registered sink. Empty list = only the in-process
    /// Prometheus default participates. Only meaningful when
    /// `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint`;
    /// `NoMetricsEndpoint` ignores the list.
    MetricsSinks: Metrics.IMetricsSink list
    /// Phase 9g — audit-log external-export sinks. Apps register one
    /// per destination (Splunk HEC, Datadog Logs, S3 Object Lock
    /// archive, custom SIEM endpoint) via `ServerApp.withAuditSink`;
    /// `compose` rejects duplicate `Name`s with a fatal startup error.
    /// Empty list (the default) skips the audit replicator entirely —
    /// no `AuditReplicationHookedEventStore` decorator, no
    /// `AuditReplicator` `BackgroundService`, no cursor blobs. The
    /// lightweight default carries zero replication overhead;
    /// regulated-sector deployments needing SOC 2 / HIPAA / GDPR
    /// Article 30 / SOX-compliant audit log replication add sinks here.
    AuditSinks: IAuditSink list
    /// Phase 9g — optional override for the audit replicator's
    /// retry / batching / catch-up tuning. When `None` (default),
    /// `compose` registers `AuditReplicatorOptions.defaults`.
    /// Deployments running multiple sinks with conflicting latency /
    /// throughput needs override per deployment.
    AuditReplicator: AuditReplicatorOptions option
    /// Phase 9e — module-scoped metric registrations accumulated from
    /// each `addModule` call whose `ServerModule` declared metrics via
    /// `ServerModule.withMetrics`. Each entry carries `Module = Some
    /// moduleName` so the sink auto-namespaces it to
    /// `toolup.{moduleName}.{name}`. `compose` folds this list into the
    /// in-process `PrometheusMetricsSink` alongside
    /// `Metrics.StandardMetrics.registrations`. Empty (the default) =
    /// only the SDK standard metrics are registered.
    MetricRegistrations: Metrics.MetricRegistration list
    /// Phase 9l — optional distributed-tracing sink. `None` (default)
    /// registers `NoOpActivitySink` so every instrumented seam in the
    /// pipeline (`ScopeResolutionMiddleware`, `JobScheduler.dispatchOne`,
    /// `WebhookDispatcher.runDelivery`, `TransactionalDispatcher
    /// .runDelivery`, `IModuleQueryBus.Ask`) sees a non-null sink and
    /// elides at zero cost — no `ActivitySource`, no span allocations,
    /// no listener thread. Deployments wanting OTel-compatible
    /// distributed tracing (OTLP / Datadog APM / Honeycomb / Jaeger /
    /// Application Insights) wire `OtelActivitySink.create ()` from
    /// the `ToolUp.Platform.Metrics.OpenTelemetry` companion. Only
    /// one sink supported per deployment; multi-sink fan-out is not
    /// implemented because the OpenTelemetry SDK already supports
    /// multiple exporters per `TracerProvider`.
    ActivitySink: IActivitySink option
    /// Phase 9v — compose-time outbound rate-limit declarations. Each
    /// companion that issues calls to a quota-bearing upstream provider
    /// (data-source connector, AI provider, transactional sink,
    /// webhook dispatcher) registers one `RateLimitDescriptor` via
    /// `ServerApp.withRateLimitDescriptor`; `compose` feeds the
    /// accumulated list into the registered `IRateLimiter` at
    /// construction so `Wait` calls know which window to apply for
    /// each `RateLimitKey.Provider`. Empty list (the default) =
    /// `IRateLimiter` admits every call regardless of `RateLimiter`
    /// mode (a registered descriptor is required to actually gate
    /// emission sites). Only meaningful when
    /// `ServerConfig.RateLimiter = EnabledRateLimiter`;
    /// `NoRateLimiter` ignores the list and resolves to a pass-through.
    RateLimitDescriptors: RateLimitDescriptor list
    /// Phase 9o — companion-contributed post-deploy smoke tests. Apps
    /// add one per backend whose end-to-end integration needs deploy-
    /// time verification via `ServerApp.withSmokeTest`. The SDK
    /// dispatcher (`SmokeTestHandler`) feeds every registered probe
    /// — first-party plus companion — into the parallel run at
    /// `GET /api/_internal/smoke`. Empty list = only the SDK's first-
    /// party probes participate (blob storage, notification channel,
    /// job scheduler, event store, data-object store, audit log)
    /// when `ServerConfig.SmokeTest = EnabledSmokeTest`.
    SmokeTests: SmokeTests.ISmokeTest list
    /// Phase 9h — DSR data exporters. Each per-store exporter
    /// (`EventStoreErasureHandler.exporter`,
    /// `DataObjectStoreErasureHandler.exporter`, etc.) is added via
    /// `ServerApp.withDataExporter`. The IDataSubjectRequestApi mount
    /// resolves the list per-request from DI; `compose` folds it into
    /// `Extensions.ServiceConfig` so consumers don't need to touch
    /// the DI seam directly. Empty list = the
    /// `IDataSubjectRequestApi.RequestExport` call returns a zero-
    /// segment archive even when `DataSubjectRequests = Enabled _` —
    /// apps that enable the substrate without registering exporters
    /// have wiring drift, not a substrate bug.
    DataExporters: IDataExporter list
    /// Phase 9h — DSR erasure handlers. Same shape as `DataExporters`:
    /// `ServerApp.withErasureHandler` accumulates per-store handlers;
    /// `compose` folds them into `Extensions.ServiceConfig`; the route
    /// handler resolves the list per-request. Empty list = preview
    /// returns an empty per-handler map and confirm reports zero
    /// records affected.
    ErasureHandlers: IErasureHandler list
    /// Phase 5h — optional override for the email-keyed pending-invitation
    /// store (`IPendingInviteStore`). When `None` (default), `compose`
    /// registers `InMemoryPendingInviteStore` over the resolved
    /// `IBlobStorage` — the single-instance blob+lock+cache impl
    /// carried forward from Phase 3d. Distributed deployments wanting
    /// multi-instance correctness on the pending-by-email flow swap in
    /// `BlobPendingInviteStore` (lands once Phase 9c half-2's
    /// `IBlobStorage.UploadWithETag` surface ships); an optional
    /// `RedisPendingInviteCache` decorator may also bind here for
    /// cross-process cache invalidation under high read load. The
    /// interface is registered unconditionally so the team-invitation
    /// handler resolves it from DI without mode-conditional fallback.
    PendingInviteStore: IPendingInviteStore option
    /// Phase 66 Stream C.1 — optional anonymous→user migration hook
    /// invoked on the first authenticated request that follows an
    /// anonymous session in the same browser. When `None` (default),
    /// `compose` registers `NoOpAnonymousSessionMigrator` so the
    /// trigger-detection path in the middleware (deferred — lands in
    /// the C.1 continuation) can read the singleton uniformly without
    /// a None-branch. Deployments wanting guest-data migration
    /// implement `IAnonymousSessionMigrator` and wire via
    /// `ServerApp.withSubjectMigrator`; compose multiple per-module
    /// migrators with `AnonymousSessionMigrator.compose`.
    SubjectMigrator: IAnonymousSessionMigrator option
    /// Phase 66 Stream A.7 / C.6 — composable decorator chain wrapped
    /// around the registered `IShareTokenStore` (when one is wired).
    /// Each entry is a function that takes the inner store and returns
    /// a wrapped store; entries apply outside-in so the last
    /// `withShareTokenStoreDecorator` call wraps the others. Empty list
    /// (the default) leaves the registered store un-decorated. The
    /// canonical first consumer is the `RevokeOnIssuerRemoved`
    /// companion (Phase 66 Stream C.5) — additional decorators (per-
    /// token rate-limit, audit replication, etc.) compose via this
    /// list without restructuring the substrate registration.
    ShareTokenStoreDecorators: (IShareTokenStore -> IShareTokenStore) list
    /// Phase 66 Stream B.3 — accumulated module-level surface defaults.
    /// `(routePrefix, requirement)` pairs derived from each
    /// `ServerModule.RoutePrefixes × DefaultSurfaceRequirement` at
    /// `addModule` time. Folded into the per-process
    /// `SurfaceRequirementRegistry.ModulePrefixes` at `compose` time
    /// (see `SurfaceRequirementRegistry.merge`). Empty list (the
    /// default) leaves the registry's `ModulePrefixes` populated solely
    /// by `fromServerConfig`'s bridge — the pre-B.3 behaviour.
    ModuleSurfaceDefaults: (string * SurfaceRequirement) list
    /// Phase 66 Stream B.3 — accumulated per-route exact-match
    /// `SurfaceRequirement` overrides. `((method, path), requirement)`
    /// triples accumulated from each `ServerModule.RouteSurfaceRequirements`
    /// at `addModule` time. Folded into the per-process
    /// `SurfaceRequirementRegistry.Exact` map at `compose` time.
    RouteSurfaceOverrides: ((string * string) * SurfaceRequirement) list
    /// Phase 9b.B — accumulated scheduled-job declarations. Sourced from
    /// each `ServerModule.JobHandlers` at `addModule` time and from
    /// direct `withScheduledJob` calls on the app (for composition-root-
    /// owned crons that aren't tied to a single module). `compose`
    /// passes the list to `ComposeJobs.registerScheduledJobDeclarations`
    /// after the `IJobScheduler` singleton is built; each declaration
    /// is registered + scheduled per scope (or `["_platform"]` when
    /// `Scopes` is empty). Empty list (the default) is a no-op — the
    /// pre-9b.B behaviour, where modules wanting a cron resolved
    /// `IJobScheduler` from the built `IServiceProvider` themselves.
    ScheduledJobs: ScheduledJobDeclaration list
    /// 0.5.7 — optional directory-lookup substrate. When `Some`,
    /// `compose` registers the impl as the `IUserDirectory` DI
    /// singleton so `UserDirectoryApiHandler` can resolve it
    /// per-request and the SDK's `Forms.Input.userTypeahead` typeahead
    /// returns matching directory entries. `None` (default) leaves the
    /// substrate unregistered — the handler short-circuits every
    /// `SearchUsers` call to `Ok []` and the typeahead UI degrades to
    /// a plain text input. Wired via `ServerApp.withUserDirectory`
    /// (typically with `EntraDirectory.fromManagedIdentity` from the
    /// `ToolUp.AuthProviders.EntraDirectory` companion).
    UserDirectory: IUserDirectory option
    /// Phase 1h — dotted-name of every companion-set composition that
    /// opts into the duplicate-guard. Currently only
    /// `"ToolUp.Forms"` (see `FormsCompose.composeForms`). A second
    /// `withForms` on the same pipeline sees its own marker already
    /// present and fails fast via
    /// `ServerApp.ensureCompanionNotAlreadyComposed` instead of
    /// cascading into the duplicate-entity-registration /
    /// double-mounted-route failures the pre-Phase-1h shape would have
    /// produced at first request. Other companions (AI / RAG /
    /// Scheduling / Asset / PublicRendering) still rely on their
    /// existing duplicate-detection paths (metric-sink construction /
    /// DI-singleton replace / route-double-mount); they may opt in to
    /// the same marker convention in a follow-up by calling
    /// `ensureCompanionNotAlreadyComposed` at entry and
    /// `withCompanionMarker` before returning.
    ComposedCompanions: string list
    /// Phase 165 — optional opt-in module-binding verifier. When `Some`,
    /// `addModule` consults it as a second gate after the module-name
    /// filter: a module presenting a `BindingStamp` is dropped unless the
    /// stamp verifies under one of the verifier's trust anchors, and a
    /// stamped module is fail-closed (dropped) even when this is `None`.
    /// `None` (the default) with unstamped modules is byte-identical to
    /// the pre-165 pipeline (GP 13). Wired via
    /// `ServerApp.withModuleBindingVerifier` (e.g.
    /// `DefaultModuleBindingVerifier.create` from `ToolUp.ArtefactSigning`).
    ModuleBindingVerifier: IModuleBindingVerifier option
    /// Phase 176 — opt-in transient-fault resilience for the resolved
    /// `IBlobStorage`. `NoResilience` (the default) resolves to the bare
    /// storage — no decorator in the hot path, byte-for-byte unchanged
    /// (GP 13). `WithResiliencePolicy policy` wraps it in
    /// `ResilientBlobStorage` so every method retries transient faults /
    /// trips the breaker / honours the per-call timeout per the policy
    /// record (GP 12 rule 3, retry-as-data). Wired via
    /// `ServerApp.withStorageResilience`.
    StorageResilience: ResilienceMode
    /// Phase 176 — opt-in transient-fault resilience for the resolved
    /// `ISecretStore`. Same shape as `StorageResilience`; `NoResilience`
    /// (the default) leaves the secret store un-decorated. Wired via
    /// `ServerApp.withSecretResilience`.
    SecretResilience: ResilienceMode
    /// Phase 169 — per-module load outcome, accumulated in `addModule`
    /// order and emitted through `Logger` at `run`. Empty until the first
    /// `addModule`; a stock deployment accumulates only `ModuleRegistered`
    /// entries that log at `Debug` (silent at the default level — GP 13).
    ModuleLoadOutcomes: (string * ModuleLoadOutcome) list
    /// Phase 279 — resolved `(moduleName, ComponentId)` for every
    /// registered module, accumulated in `addModule` order. The id is the
    /// module's explicit `ComponentId` when declared (via
    /// `ServerModule.withComponentId`), else the name-derived default
    /// (`ComponentId.ofModule Name`). `run` checks this set for
    /// uniqueness at compose time — a duplicate resolved id is a
    /// compose-time failure, not a runtime surprise. Empty until the
    /// first `addModule`.
    ModuleComponentIds: (string * ComponentId) list
}

module ServerApp =
    let empty: ServerApp = {
        Config = ServerConfig.defaults
        Handlers = []
        DataTypes = []
        VectorisationHandlers = []
        Auth = None
        Logger = None
        Storage = None
        Notifications = None
        Extensions = ComposeExtensions.empty
        ModuleNames = []
        ModuleConfigs = []
        QueryHandlerRegistrations = []
        DataTypeRegistrations = []
        AITools = []
        TransactionalSinks = []
        HealthChecks = []
        ConfigValidators = []
        EncryptionKeyResolver = None
        EntityRegistrations = []
        MetricsSinks = []
        QuotaPolicy = None
        ProviderProfile = None
        AuditSinks = []
        AuditReplicator = None
        MetricRegistrations = []
        ActivitySink = None
        RateLimitDescriptors = []
        SmokeTests = []
        DataExporters = []
        ErasureHandlers = []
        PendingInviteStore = None
        SubjectMigrator = None
        ShareTokenStoreDecorators = []
        ModuleSurfaceDefaults = []
        RouteSurfaceOverrides = []
        ScheduledJobs = []
        UserDirectory = None
        ComposedCompanions = []
        ModuleBindingVerifier = None
        StorageResilience = NoResilience
        SecretResilience = NoResilience
        ModuleLoadOutcomes = []
        ModuleComponentIds = []
    }

    /// Phase 1h companion-conflict validator. Companion compose seams
    /// that opt into the marker convention call this at entry to refuse
    /// re-emitting onto a `ServerApp` that already carries the same
    /// companion's marker. `companionName` is the dotted package name
    /// (e.g. `"ToolUp.Forms"`); the marker also appears on
    /// `ComposedCompanions` after a successful emit so the validator
    /// catches the second call.
    ///
    /// On conflict, raises a clear single-line diagnostic naming the
    /// companion and the canonical resolution paths. Today only
    /// `FormsCompose.composeForms` calls this; the existing cascading
    /// failures (duplicate-metric-name at sink construction,
    /// duplicate-entity-registration at compose, double-mounted route
    /// at first request) remain the backstop for any companion that
    /// hasn't opted in.
    let ensureCompanionNotAlreadyComposed (companionName: string) (app: ServerApp) : unit =
        if List.contains companionName app.ComposedCompanions then
            failwithf
                "%s: companion already composed on this ServerApp pipeline. The same companion cannot be stacked twice (each call re-registers its DI services, re-appends its metric declarations, and re-mounts its routes — the cascading failures land at sink construction or first request). Combine all your %s configuration in a single call (e.g. one withForms invocation that builds up every schema/workflow/action), or rebuild the pipeline from ServerApp.empty. (Phase 1h)"
                companionName
                companionName

    /// Phase 1h — append a companion marker to `ComposedCompanions`
    /// after a successful compose emit. Paired with
    /// `ensureCompanionNotAlreadyComposed` at the top of the same
    /// compose seam.
    let withCompanionMarker (companionName: string) (app: ServerApp) : ServerApp = {
        app with
            ComposedCompanions = app.ComposedCompanions @ [ companionName ]
    }

    let withConfig (c: ServerConfig) (app: ServerApp) : ServerApp = { app with Config = c }
    let withAuth (a: IAuthProvider) (app: ServerApp) : ServerApp = { app with Auth = Some a }

    /// Phase 159 — select the durable per-subject consent-state store
    /// mode. `EntityBackedConsentStateStore` registers the durable store
    /// over `IEntityStore` (requires `EntityStore = EnabledEntityStore`;
    /// the compose path registers the `ConsentRecord` entity type
    /// automatically). Default `NoConsentStateStore` registers nothing
    /// (GP 13). Sugar over a `Config` update so the consent store reads
    /// the same as every other fluent opt-in.
    let withConsentStateStore (mode: ConsentStateStoreMode) (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    ConsentStateStore = mode
            }
    }

    /// Phase 165 — opt into the module-binding gate. Once a verifier is
    /// configured, `addModule` drops any module whose `BindingStamp` does
    /// not verify under one of the verifier's trust anchors. Compose with
    /// `DefaultModuleBindingVerifier.create anchors` from
    /// `ToolUp.ArtefactSigning`, or any custom `IModuleBindingVerifier`.
    let withModuleBindingVerifier (verifier: IModuleBindingVerifier) (app: ServerApp) : ServerApp = {
        app with
            ModuleBindingVerifier = Some verifier
    }

    let withLogger (l: ILogger) (app: ServerApp) : ServerApp = { app with Logger = Some l }
    let withStorage (s: IBlobStorage) (app: ServerApp) : ServerApp = { app with Storage = Some s }

    /// Phase 37 — register a path prefix as a peer-bearer-auth route.
    /// `PeerBearerAuthMiddleware` validates `Authorization: Bearer
    /// <token>` against `ISecretStore.GetSecret("_platform",
    /// $"peers/{peerName}/bearer")` for every request matching one of
    /// these prefixes, using the `X-Peer-Name` header to identify the
    /// caller. On success the middleware stamps `HttpContext.Items
    /// ["PeerName"]` so the handler can partition state per caller;
    /// on mismatch the response is 401 before the handler runs.
    ///
    /// Peer routes are exempt from `AuthEnforcementMiddleware`'s user-
    /// auth check — the bearer IS the authentication. Companions
    /// register their peer-shaped handler prefixes via this builder.
    ///
    /// Phase 18's richer `IPeerAuthProvider` (JWT, delegated
    /// assertions, capability handshake) coexists on different
    /// prefixes — a deployment can register `withPeerRoutePrefix
    /// "/api/peer/echo"` for the bearer flavour and a Phase 18
    /// substrate at `/api/peer/federated/` at the same time.
    let withPeerRoutePrefix (prefix: string) (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    PeerRoutePrefixes = app.Config.PeerRoutePrefixes @ [ prefix ]
            }
    }

    let withNotifications (n: INotificationChannel) (app: ServerApp) : ServerApp = { app with Notifications = Some n }

    /// 0.5.7 — register a directory-lookup companion. The impl is
    /// registered as the `IUserDirectory` DI singleton at compose
    /// time so `UserDirectoryApiHandler.userDirectoryApi` can resolve
    /// it per-request from `HttpContext.RequestServices`. The SDK's
    /// `TeamManagerUI` invite-form typeahead surfaces the substrate's
    /// matches to the operator typing into the "Invite a member"
    /// field. Without a directory companion, every `SearchUsers`
    /// call short-circuits to `Ok []` and the typeahead degrades to
    /// a plain text input (the operator still types the full email
    /// and the existing invite-by-email flow accepts it). Typical
    /// companion: `ToolUp.AuthProviders.EntraDirectory` —
    /// `EntraDirectory.fromManagedIdentity` reads the App Service
    /// managed identity to call Microsoft Graph's `/users` endpoint.
    let withUserDirectory (directory: IUserDirectory) (app: ServerApp) : ServerApp = {
        app with
            UserDirectory = Some directory
    }

    /// Phase 6f — register an out-of-band transactional notification
    /// sink (email / SMS / push). Each sink advertises a `Kind`
    /// matching one of `NotificationKind.SinkKind` constants;
    /// duplicate-`Kind` registrations are caught at compose time and
    /// fail the deployment. Sinks themselves come from
    /// `src/NotificationChannels/<Family>/<Vendor>/` companion
    /// packages — `withTransactionalSink (SmtpNotificationSink.create
    /// secretStore logger)`.
    ///
    /// Registering any sink causes `compose` to:
    ///   * wrap the `INotificationChannel` with
    ///     `DispatchingNotificationChannel` so transactional kinds
    ///     bypass the wire transport;
    ///   * register the SDK-shipped `_platform.notification_prefs`
    ///     schema so admins see the kill-switch tab;
    ///   * host `TransactionalDispatcher` as `IHostedService`.
    ///
    /// Apps without transactional delivery omit this call entirely
    /// — zero runtime cost.
    let withTransactionalSink (sink: INotificationSink) (app: ServerApp) : ServerApp = {
        app with
            TransactionalSinks = app.TransactionalSinks @ [ sink ]
    }

    /// Phase 178 — register a declarative alert rule. Each rule watches a
    /// metric (`IMetricsSink` accumulator) or a health probe
    /// (`IHealthCheck`) and delivers a notification when its
    /// `ThresholdCondition` holds for `ForDuration`. Registering any rule
    /// causes `compose` to host the `AlertRuleEngine` `BackgroundService`
    /// (subject to the `ProcessProfile` gate — `AllInOne` / `WorkerOnly`
    /// run it, `WebOnly` / `DispatcherOnly` / `ServerlessHost` skip). Apps
    /// without alerting omit the call entirely — zero runtime cost (GP 13).
    /// Rule `Name`s must be unique within a deployment (the engine keys its
    /// per-rule breach window on the name).
    let withAlertRule (rule: AlertRule) (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    AlertRules = app.Config.AlertRules @ [ rule ]
            }
    }

    /// Phase 178 — register several alert rules at once. Appends to any
    /// already registered; see `withAlertRule` for the hosting contract.
    let withAlertRules (rules: AlertRule list) (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    AlertRules = app.Config.AlertRules @ rules
            }
    }

    /// Phase 9g — register an audit-log external-export sink (Splunk
    /// HEC, Datadog Logs, S3 Object Lock archive, custom SIEM
    /// endpoint). Each sink advertises a deployment-unique `Name`;
    /// duplicate-`Name` registrations are caught at compose time and
    /// fail the deployment (mirrors Phase 6f duplicate-kind rejection
    /// on `INotificationSink`). Sinks themselves come from
    /// `src/AuditSinks/<Vendor>/` companion packages —
    /// `withAuditSink (S3ArchiveAuditSink.create blobStorage logger)`.
    ///
    /// Registering any sink causes `compose` to:
    ///   * wrap the inner `IEventStore` with
    ///     `AuditReplicationHookedEventStore` so audit events flow
    ///     into the replicator's per-sink bounded channels;
    ///   * host `AuditReplicator` as `IHostedService`;
    ///   * register every sink as an `IAuditSink` DI singleton (for
    ///     test / diagnostic visibility — the live dispatch path
    ///     holds them via constructor capture in `AuditReplicator`).
    ///
    /// Apps without external audit replication omit this call entirely
    /// — zero runtime cost.
    let withAuditSink (sink: IAuditSink) (app: ServerApp) : ServerApp = {
        app with
            AuditSinks = app.AuditSinks @ [ sink ]
    }

    /// Phase 9g — override the default audit replicator tuning
    /// (`AuditReplicatorOptions.defaults`). Deployments wanting larger
    /// batches, longer linger, faster catch-up sweeps, or tighter
    /// retry budgets supply their own `AuditReplicatorOptions` here.
    /// `None` (the default) uses the SDK defaults — see
    /// `AuditReplicatorOptions.defaults` for the trade-offs.
    let withAuditReplicatorOptions (options: AuditReplicatorOptions) (app: ServerApp) : ServerApp = {
        app with
            AuditReplicator = Some options
    }

    /// Phase 9e — register a companion-contributed metrics sink
    /// alongside the in-process Prometheus default. Each companion
    /// exposes its `IMetricsSink` instance via a small constructor
    /// (`OtelMetricsSink.create logger`, etc.) — the deployment wires
    /// it into `ServerApp` so `compose` can fold every registered
    /// sink into a `FanOutMetricsSink` that dispatches every emission
    /// to all wrapped sinks. Apps that only want the in-process
    /// Prometheus default omit this call entirely. The list is
    /// ignored when `ServerConfig.MetricsEndpoint = NoMetricsEndpoint`
    /// — emission sites resolve `IMetricsSink` to `NoOpMetricsSink`
    /// regardless of registered companion sinks, so the deployment
    /// must opt in to metrics through `MetricsEndpoint =
    /// EnabledMetricsEndpoint` first.
    let withMetricsSink (sink: Metrics.IMetricsSink) (app: ServerApp) : ServerApp = {
        app with
            MetricsSinks = app.MetricsSinks @ [ sink ]
    }

    /// Phase 9e — register companion-contributed `MetricRegistration`s
    /// alongside the module-scoped path (`ServerModule.withMetrics`). A
    /// first-party SDK companion (e.g. `ToolUp.AI` declaring latency
    /// histograms, `ToolUp.RAG` declaring retrieval counters) uses this
    /// to pre-register its `toolup.*`-prefixed metrics so the in-process
    /// `PrometheusMetricsSink` allocates the series at compose time and
    /// emissions flow rather than silently drop. Module-author metrics
    /// continue to use `ServerModule.withMetrics` (auto-namespaced
    /// to `toolup.{moduleName}.{name}`); companion metrics already
    /// carry the literal `toolup.*` name and bypass the namespace
    /// rewrite via `Module = None`. Idempotent across multiple calls —
    /// the same registration appearing twice fails fast at
    /// `PrometheusMetricsSink` construction with a clear duplicate-name
    /// error, so call once at compose-time per companion.
    let withMetricRegistrations (regs: Metrics.MetricRegistration list) (app: ServerApp) : ServerApp = {
        app with
            MetricRegistrations = app.MetricRegistrations @ regs
    }

    /// Phase 9l — register a distributed-tracing sink (peer to
    /// `withMetricsSink`). Each companion exposes its `IActivitySink`
    /// instance via a small constructor (`OtelActivitySink.create ()`
    /// from the `ToolUp.Platform.Metrics.OpenTelemetry` companion) —
    /// the deployment wires it into `ServerApp` so the SDK pipeline
    /// (`ScopeResolutionMiddleware`, `JobScheduler.dispatchOne`,
    /// `WebhookDispatcher.runDelivery`, `TransactionalDispatcher
    /// .runDelivery`, `IModuleQueryBus.Ask`) emits W3C-Trace-Context-
    /// compatible spans through the registered sink. Apps that don't
    /// want distributed tracing omit this call entirely; the SDK
    /// default `NoOpActivitySink` elides every emission at zero cost.
    /// Calling `withActivitySink` twice overrides the prior sink —
    /// only one tracing sink supported per deployment.
    let withActivitySink (sink: IActivitySink) (app: ServerApp) : ServerApp = { app with ActivitySink = Some sink }

    /// Phase 9v — declare an outbound rate-limit window for one
    /// upstream provider. Each companion that issues calls to a
    /// quota-bearing service (data-source connector, AI provider,
    /// transactional sink, webhook dispatcher) registers one
    /// `RateLimitDescriptor` naming its provider, the short-window
    /// `(count, duration)` pair, an optional long-window quota
    /// (typically a daily ceiling), and a fairness mode that decides
    /// whether the window is per-tenant (`PerScope`) or shared across
    /// the deployment (`PerProvider`).
    ///
    /// Descriptors are looked up by `Provider` at emission time —
    /// callers `do! ctx.RateLimiter.Wait(key)` before every outbound
    /// HTTP / SDK call, and the limiter applies the descriptor whose
    /// `Provider` matches `key.Provider`. Unregistered providers
    /// admit immediately (the SDK fails open on missing
    /// declarations rather than failing closed on every outbound
    /// call). Only meaningful when
    /// `ServerConfig.RateLimiter = EnabledRateLimiter`;
    /// `NoRateLimiter` (the default) ignores the registered
    /// descriptors and resolves `IRateLimiter` to `NoOpRateLimiter`.
    ///
    /// Calling twice with the same `Provider` keeps the last
    /// descriptor — the limiter constructs its lookup as a `Map`
    /// keyed by provider, so a later registration overrides an
    /// earlier one. Companion packages should expose one descriptor
    /// per upstream they consume; consumer apps pairing with a
    /// quota-bearing provider call `withRateLimitDescriptor` once
    /// per provider at compose time.
    let withRateLimitDescriptor (descriptor: RateLimitDescriptor) (app: ServerApp) : ServerApp = {
        app with
            RateLimitDescriptors = app.RateLimitDescriptors @ [ descriptor ]
    }

    /// Phase 9k — register a companion-contributed health probe.
    /// Each companion exposes its `IHealthCheck` instance via a small
    /// constructor (`RedisNotificationChannelHealth.create
    /// multiplexer`, `ClaudeAIProviderHealth.create secretStore`, etc.)
    /// — the consumer wires it into `ServerApp` so the SDK aggregator
    /// can pick it up at end-of-compose. Probes are auto-tagged with
    /// their `Kind` so `/health` (Liveness only) and `/ready` (both
    /// kinds) partition correctly. Apps that don't register a probe
    /// for a given subsystem pay nothing — the empty list flows
    /// through compose as a no-op.
    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: ServerApp) : ServerApp = {
        app with
            HealthChecks = app.HealthChecks @ [ check ]
    }

    /// Phase 9o — register a companion-contributed post-deploy smoke
    /// test. Each companion exposes its `ISmokeTest` instance via a
    /// small constructor (`RedisNotificationChannelSmoke.create
    /// multiplexer`, `S3StorageSmoke.create client`, etc.) — the
    /// consumer wires it into `ServerApp` so the SDK dispatcher at
    /// `GET /api/_internal/smoke` runs it alongside the first-party
    /// probes. Smoke tests register against the reserved sentinel
    /// scope `"_smoke"` and clean up after themselves. Apps that
    /// don't register a smoke for a given subsystem pay nothing —
    /// the empty list flows through compose as a no-op when
    /// `SmokeTest = NoSmokeTest`.
    let withSmokeTest (test: SmokeTests.ISmokeTest) (app: ServerApp) : ServerApp = {
        app with
            SmokeTests = app.SmokeTests @ [ test ]
    }

    /// Phase 9h — register a data-subject-request exporter. Each
    /// per-store implementation (`EventStoreErasureHandler.exporter`,
    /// `DataObjectStoreErasureHandler.exporter`,
    /// `LineageStoreErasureHandler.exporter`, etc.) contributes one
    /// `ExportSegment` per `RequestExport` call; the orchestrator
    /// concatenates segments alphabetically by exporter `Name` for
    /// deterministic byte output. Apps that don't opt into
    /// `ServerConfig.DataSubjectRequests = Enabled _` can still
    /// accumulate exporters — the registration is inert until the
    /// route is mounted, so calling this in a shared composition
    /// helper doesn't force the DSR substrate on at the same time.
    /// Mirrors `withAuditSink` / `withMetricsSink` / `withSmokeTest`
    /// shape: append-only accumulation, DI registration deferred to
    /// `run`. Multi-registration is the design — every store gets its
    /// own exporter.
    let withDataExporter (exporter: IDataExporter) (app: ServerApp) : ServerApp = {
        app with
            DataExporters = app.DataExporters @ [ exporter ]
    }

    /// Phase 9h — register a data-subject-request erasure handler.
    /// Each per-store implementation
    /// (`EventStoreErasureHandler.erasureHandler`,
    /// `DataObjectStoreErasureHandler.erasureHandler`, etc.)
    /// contributes one `ErasureSummary` per `PreviewErasure` /
    /// `ConfirmErasure` call. The orchestrator runs handlers
    /// alphabetically by `Name`; deployments that need a specific
    /// ordering register handlers with sort-aware names (`01-events`,
    /// `02-data`, `99-audit`). Multi-registration is the design —
    /// every store gets its own handler. Mirrors `withDataExporter`
    /// in shape and gating behaviour.
    let withErasureHandler (handler: IErasureHandler) (app: ServerApp) : ServerApp = {
        app with
            ErasureHandlers = app.ErasureHandlers @ [ handler ]
    }

    /// Phase 9m — register a companion-contributed startup config
    /// validator. Each companion exposes its `IConfigValidator`
    /// instance via a small constructor (`OidcAuthValidator.tryFromEnv`,
    /// `RedisNotificationChannelValidator.create multiplexer`,
    /// `SmtpNotificationSinkValidator.fromEnv`, etc.) — the consumer
    /// wires it into `ServerApp` so the SDK aggregator can run it at
    /// end-of-compose, before HTTP binds. Validators that return
    /// `Error` abort startup with a `ConfigPreflightFailedException`;
    /// `Warning`s log + continue. Apps that don't register a validator
    /// for a given subsystem pay nothing — the empty list flows
    /// through compose as a no-op.
    let withConfigValidator (validator: ConfigValidation.IConfigValidator) (app: ServerApp) : ServerApp = {
        app with
            ConfigValidators = app.ConfigValidators @ [ validator ]
    }

    /// Phase 9j — set the HTTP-surface hardening mode. Shorthand for
    /// `{ app with Config.SecurityHardening = mode }`. `NoSecurityHardening`
    /// (the default) leaves `CspMiddleware` / `CsrfMiddleware` inert and
    /// unmounts `/api/csrf-token`; `DefaultSecurityHardening` /
    /// `StrictSecurityHardening` opt the deployment in. The aggregated
    /// CSP reflects whatever `ICspContributor`s are registered, so a
    /// deployment that adds an AI / OIDC companion gets the right
    /// `connect-src` automatically.
    let withSecurityHardening (mode: SecurityHardeningMode) (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    SecurityHardening = mode
            }
    }

    /// Phase 9j — register a CSP contributor so its required origins
    /// are folded into the aggregated `Content-Security-Policy` at
    /// compose time. Threads the singleton registration through the
    /// shared `ComposeExtensions.ServiceConfig` (the same seam
    /// companions use directly), so `AIServerApp` / `RAGServerApp`
    /// inherit it without a per-wrapper forwarder. Use for a
    /// CDN-delivered bundle host (`AgGridCdnCspContributor`), a hosted
    /// embed origin, or any deployment-specific origin the first-party
    /// defaults don't cover.
    let withCspContributor (contributor: ICspContributor) (app: ServerApp) : ServerApp =
        let register (s: IServiceCollection) =
            s.AddSingleton<ICspContributor>(contributor)

        {
            app with
                Extensions = {
                    app.Extensions with
                        ServiceConfig =
                            match app.Extensions.ServiceConfig with
                            | None -> Some register
                            | Some baseFn -> Some(fun s -> register (baseFn s))
                }
        }

    /// Phase 156 — opt into a CSP source mode so the aggregated
    /// `Content-Security-Policy` can cover the deployment's own SSR-emitted
    /// inline `<script>` / `<style>` without `'unsafe-inline'`:
    ///
    ///   * `SecurityHardening.NonceCsp` — a per-request random nonce in
    ///     `script-src` / `style-src`, read by layouts via
    ///     `Csp.requestNonce` to stamp inline `<script nonce="…">`. For
    ///     DYNAMIC responses; not cache-safe (see the nonce↔cache
    ///     validator — pair it with `withRenderCache` and startup warns).
    ///   * `SecurityHardening.HashCsp inlineScripts` — `'sha256-…'` source
    ///     hashes over the declared inline-script bodies. Byte-stable
    ///     header, so it survives render-cache hits + `304`s. For CACHED /
    ///     DETERMINISTIC responses.
    ///
    /// Threads the singleton registration through the shared
    /// `ComposeExtensions.ServiceConfig` (like `withCspContributor`), so
    /// `aggregate` reads it and `AIServerApp` / `RAGServerApp` inherit it.
    /// Only meaningful alongside `withSecurityHardening` (the source mode
    /// shapes the hardening CSP that `CspMiddleware` stamps). Omitting this
    /// call leaves the resolved header byte-for-byte pre-156 (GP 11).
    let withCspSourceMode (mode: SecurityHardening.CspSourceMode) (app: ServerApp) : ServerApp =
        let register (s: IServiceCollection) =
            s.AddSingleton<SecurityHardening.CspSourceMode>(mode)

        {
            app with
                Extensions = {
                    app.Extensions with
                        ServiceConfig =
                            match app.Extensions.ServiceConfig with
                            | None -> Some register
                            | Some baseFn -> Some(fun s -> register (baseFn s))
                }
        }

    /// Phase 22 — opt into AES-GCM envelope encryption for the
    /// registered `IBlobStorage`. The resolver determines the
    /// security model:
    ///
    ///   * `SingleKeyResolver` — platform-wide single key. One
    ///     cryptographic boundary across all tenants.
    ///   * `PerScopeKeyResolver` — per-tenant keys with crypto-shred
    ///     via `DestroyKey`. Use for multi-tenant deployments hosting
    ///     independent practices, agencies, or businesses on one
    ///     instance.
    ///   * Custom — any `IBlobEncryptionKeyResolver` impl. KMS-backed
    ///     resolvers (AWS KMS / Azure Key Vault / GCP KMS) ship in
    ///     Phase 22a sub-companions.
    ///
    /// Apps without app-level envelope encryption omit this call —
    /// the registered `IBlobStorage` is used un-decorated (relying
    /// on cloud-provider at-rest encryption only).
    ///
    /// Calling this multiple times keeps the last resolver. Stacking
    /// encryption layers is not supported in v1.
    let withEncryptedBlobStorage (resolver: IBlobEncryptionKeyResolver) (app: ServerApp) : ServerApp = {
        app with
            EncryptionKeyResolver = Some resolver
    }

    /// Phase 176 — opt the resolved `IBlobStorage` into transient-fault
    /// resilience. The resolved storage (after any envelope-encryption
    /// decorator) is wrapped with `ResilientBlobStorage policy`, so every
    /// method retries genuinely-transient thrown faults / trips the
    /// breaker / honours the per-call timeout per the `TransientFaultPolicy`
    /// record. Deterministic `Result.Error` outcomes are values, not
    /// exceptions, so they are never retried. Omitting this call leaves
    /// the storage layer un-decorated and byte-for-byte unchanged (GP 13).
    /// Calling it multiple times keeps the last policy.
    let withStorageResilience (policy: TransientFaultPolicy) (app: ServerApp) : ServerApp = {
        app with
            StorageResilience = WithResiliencePolicy policy
    }

    /// Phase 176 — opt the resolved `ISecretStore` into transient-fault
    /// resilience. Same shape as `withStorageResilience`; the secret store
    /// is wrapped with `ResilientSecretStore policy`. Omitting this call
    /// leaves the secret store un-decorated (GP 13).
    let withSecretResilience (policy: TransientFaultPolicy) (app: ServerApp) : ServerApp = {
        app with
            SecretResilience = WithResiliencePolicy policy
    }

    /// Phase 9d — override the SDK default per-team compute quota
    /// policy. Deployments supply a custom `ITeamQuotaPolicy` to plug
    /// in a SaaS-billing-aware policy, a per-tier subscription gate, or
    /// a custom rate-limit shape; the SDK's `BlobBackedTeamQuotaPolicy`
    /// reads simple ceilings from the `_platform.usage` config schema
    /// and is sufficient for most deployments. Calling this multiple
    /// times keeps the last policy.
    let withQuotaPolicy (policy: ITeamQuotaPolicy) (app: ServerApp) : ServerApp = { app with QuotaPolicy = Some policy }

    /// Phase 42.B / 43.A — register the canonical platform-wide BYOK
    /// `IProviderProfile` store. This is the composition seam deferred
    /// in 42.B until it had a real caller; the Phase 43.A AI cutover is
    /// that caller (the AI assistant resolves providers through this
    /// store instead of the removed `IUserAIConfigStore` shim). When
    /// set, `run` registers the instance as the `IProviderProfile` DI
    /// singleton so any in-process BYOK consumer resolves it.
    ///
    /// The AI path supplies this through `AIServerApp.create` /
    /// `RAGServerApp.create` (a required dependency there, mirrored back
    /// onto this field by their `run`), so AI apps need not call this
    /// directly. Non-AI BYOK consumers (a rental gateway, a future app
    /// reading provider profiles without the AI assistant companion)
    /// opt in here. Calling this multiple times keeps the last store.
    /// Omit it entirely and `compose` registers nothing — GP 13 zero
    /// footprint for deployments with no BYOK surface.
    let withProviderProfile (store: IProviderProfile) (app: ServerApp) : ServerApp = {
        app with
            ProviderProfile = Some store
    }

    /// Phase 19 — register an entity type with the typed entity store.
    /// Each call captures the typed `EntityRegistration<'T>` in a
    /// closure that runs at compose time against the shared
    /// `EntityRegistry`. Compose-time evaluation means modules can
    /// register their entity types without seeing the registry
    /// directly.
    ///
    /// Requires `ServerConfig.EntityStore = EnabledEntityStore` to be
    /// set; otherwise the registrations are accumulated but never
    /// flushed. (No runtime warning — silent for ergonomic reasons,
    /// since modules call this at module-load time before the
    /// deployment knows which mode it's running in.)
    let withEntity<'T> (registration: EntityTypes.EntityRegistration<'T>) (app: ServerApp) : ServerApp = {
        app with
            EntityRegistrations =
                app.EntityRegistrations
                @ [ (fun registry -> registry.Register<'T>(registration)) ]
    }

    let withExtensions (e: ComposeExtensions) (app: ServerApp) : ServerApp = { app with Extensions = e }

    /// Phase 1f composition seam. Accumulates an `IApplicationBuilder`
    /// thunk that `compose` applies at the **pre** position — after
    /// CORS / security headers, before `ScopeResolutionMiddleware` /
    /// `AuthEnforcementMiddleware`. Use for IP allowlists, custom
    /// auth-precondition rejection, request-shape sanitisation, or
    /// any concern that should short-circuit before the SDK resolves
    /// the caller's scope. Thunks are applied in registration order;
    /// each must return the (potentially modified) builder.
    let withPreMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: ServerApp) : ServerApp = {
        app with
            Extensions.PreMiddleware = app.Extensions.PreMiddleware @ [ f ]
    }

    /// Phase 1f composition seam. Accumulates an `IApplicationBuilder`
    /// thunk that `compose` applies at the **post** position — AFTER
    /// `app.UseGiraffe router`, so consumers can register fallback
    /// handlers (custom 404 / catch-all rewrites / debug-only routes)
    /// that only fire when no Giraffe handler matched. Thunks are
    /// applied in registration order.
    let withPostMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: ServerApp) : ServerApp = {
        app with
            Extensions.PostMiddleware = app.Extensions.PostMiddleware @ [ f ]
    }

    /// Phase 5h — register a custom `IPendingInviteStore` implementation.
    /// When omitted, `compose` registers `InMemoryPendingInviteStore`
    /// over the resolved `IBlobStorage` — the single-instance blob+lock+
    /// cache impl carried forward from Phase 3d. Distributed deployments
    /// wanting multi-instance correctness on the pending-by-email flow
    /// call `withPendingInviteStore (BlobPendingInviteStore.create ...)`
    /// once that companion ships (depends on forge Phase 9c half-2's
    /// `IBlobStorage.UploadWithETag` substrate). Calling this multiple
    /// times keeps the last store.
    let withPendingInviteStore (store: IPendingInviteStore) (app: ServerApp) : ServerApp = {
        app with
            PendingInviteStore = Some store
    }

    /// Phase 66 Stream C.1 — register an anonymous→user session
    /// migrator invoked on the first authenticated request that
    /// follows an anonymous session in the same browser. When
    /// omitted, `compose` registers `NoOpAnonymousSessionMigrator` so
    /// the trigger-detection path in the middleware (deferred —
    /// lands in the C.1 continuation) reads the singleton uniformly.
    /// Compose multiple per-module migrators (Forms `FormDraftMigrator`,
    /// AI `ConversationDraftMigrator`, etc.) with
    /// `AnonymousSessionMigrator.compose` before calling this. Calling
    /// `withSubjectMigrator` multiple times keeps the last migrator —
    /// merge upstream of the call site instead.
    let withSubjectMigrator (migrator: IAnonymousSessionMigrator) (app: ServerApp) : ServerApp = {
        app with
            SubjectMigrator = Some migrator
    }

    /// Phase 66 Stream A.7 / C.6 — wrap the registered
    /// `IShareTokenStore` with a decorator that returns a new store.
    /// Composable: every call appends to the chain; decorators apply
    /// outside-in so the LAST `withShareTokenStoreDecorator` call
    /// wraps the others. The canonical first consumer is the
    /// `RevokeOnIssuerRemoved` companion (Phase 66 Stream C.5);
    /// further decorators (per-token rate-limit, audit replication,
    /// per-claim retention policies) compose via this list without
    /// restructuring the substrate registration.
    ///
    /// No-op when `ServerConfig.ShareTokenStore = NoShareTokenStore`
    /// — the underlying store is `None` so the decorators are never
    /// applied. `SurfaceCoherenceValidator` (Stream B.2) warns at
    /// startup when decorators are registered but no `ClaimBearer`
    /// surface exists.
    let withShareTokenStoreDecorator (decorator: IShareTokenStore -> IShareTokenStore) (app: ServerApp) : ServerApp = {
        app with
            ShareTokenStoreDecorators = app.ShareTokenStoreDecorators @ [ decorator ]
    }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// (one not tied to a single module). Same shape as
    /// `ServerModule.withJobHandler` — the SDK's compose pipeline
    /// registers `handler` against `IJobScheduler` under `handlerName`
    /// and schedules a `JobDefinition` with the given `trigger` for
    /// the default scope (`"_platform"`).
    ///
    /// Uses `JobPrecision.Minute`, empty payload, default retry, and
    /// an auto-built per-scope idempotency key with a one-year TTL.
    /// Use `withScheduledJob` (full `ScheduledJobDeclaration`) when
    /// the job needs a non-empty payload, custom retry policy, or
    /// per-tenant fan-out across multiple scopes.
    ///
    /// When `ServerConfig.JobScheduler = NoJobScheduler`, declarations
    /// log a single `Warn` and skip — declaring jobs in an unscheduled
    /// deployment is a config mismatch, not a crash.
    let withJobHandler (handlerName: string, handler: IJobHandler, trigger: Trigger) (app: ServerApp) : ServerApp =
        let declaration = ScheduledJobDeclaration.create handlerName handler trigger

        {
            app with
                ScheduledJobs = app.ScheduledJobs @ [ declaration ]
        }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// with full control over every `JobRegistration` knob (`Scopes`,
    /// `Payload`, `RetryPolicy`, `ShardKey`, `Precision`, `Idempotency`,
    /// `Tags`). Construct via `ScheduledJobDeclaration.create` + the
    /// fluent `with*` helpers; see `withJobHandler` for the common
    /// tupled shorthand.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (app: ServerApp) : ServerApp = {
        app with
            ScheduledJobs = app.ScheduledJobs @ [ declaration ]
    }

    /// Phase 9b.A — opt into back-fill of `OnEvent`-triggered jobs on
    /// detected scheduler tick drift. Default: off. When enabled, a
    /// detected drift (`now - expectedTickTime > 60s` on wake-up) re-
    /// fires each Active `OnEvent`-triggered job once after recording
    /// the `JobSchedulerTickMissed` audit. Cron jobs are NOT
    /// back-filled regardless — cron semantics expect "fire on the
    /// boundary" and re-firing a `*/5 * * * *` rollup three times after
    /// a 15-minute pause would conflate three roll-up windows.
    ///
    /// Operators opt in when their `OnEvent` work is safely re-entrant
    /// (idempotent inserts, dedup'd upserts, advisory side effects).
    /// Non-idempotent work should stay opt-out and either rely on
    /// upstream event redelivery or accept the missed dispatches as a
    /// trade-off for at-most-once semantics.
    let withBackfillMissedTicks (enabled: bool) (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    BackfillMissedTicks = enabled
            }
    }

    /// Phase 177 — opt into the deployment-readiness scorecard. Flips
    /// `ServerConfig.DeploymentReadiness` to `EnabledReadinessReport` so
    /// `compose` mounts the Platform-Admin-gated `IDeploymentReadinessApi`
    /// read that consolidates the `IConfigValidator` / `ISmokeTest` /
    /// `ConfigDrift` / `IHealthCheck` signals into one go/no-go verdict.
    /// Pure projection over signals that already exist — no new gate, no
    /// new control-plane behaviour. A deployment that never calls this
    /// stays `NoReadinessReport` and is byte-for-byte unchanged (GP 13).
    let withDeploymentReadiness (app: ServerApp) : ServerApp = {
        app with
            Config = {
                app.Config with
                    DeploymentReadiness = EnabledReadinessReport
            }
    }

    /// Fan a `ServerModule`'s contributions into the app's accumulating lists.
    /// Honours `app.Config.ModuleFilter` — modules whose name doesn't match
    /// are skipped silently, so `withConfig` should run before `addModules`
    /// (the documented pipeline order).
    /// Phase 169 — append a module-load outcome to the startup-observability
    /// accumulator (emitted through `Logger` at `run`).
    let private recordOutcome (moduleId: string) (outcome: ModuleLoadOutcome) (app: ServerApp) : ServerApp = {
        app with
            ModuleLoadOutcomes = app.ModuleLoadOutcomes @ [ moduleId, outcome ]
    }

    let addModule (m: ServerModule) (app: ServerApp) : ServerApp =
        if not (ModuleFilter.matches app.Config.ModuleFilter m.Name) then
            // Phase 169 — a name-filtered module is dropped, but the drop is
            // now observable: record the active filter so "why didn't my
            // module load?" is answerable from the startup log.
            let activeFilter = app.Config.ModuleFilter |> Option.defaultValue "(none)"
            recordOutcome m.Name (ModuleFiltered activeFilter) app
        else
            // Phase 165 — opt-in module-binding gate, the second check after
            // the name filter. The common case (no verifier configured AND
            // no stamp present) is a single cheap match arm → byte-identical
            // to the pre-165 pipeline (GP 13). A present stamp fails closed
            // even when no verifier is configured: a stamped module is
            // self-protecting on any deployment lacking the matching anchor.
            let bindingOutcome =
                match app.ModuleBindingVerifier, m.BindingStamp with
                | None, None -> Allowed
                | Some verifier, stamp -> verifier.Verify(m.Name, stamp)
                | None, Some _ ->
                    Rejected
                        "module presents a binding stamp but this deployment has no module-binding verifier configured"

            let bindingRejected =
                match bindingOutcome with
                | Rejected _ -> true
                | Allowed -> false

            // Drop a module that failed the binding gate. Phase 169 — the
            // drop now records its neutral reason for the startup log.
            if bindingRejected then
                let reason =
                    match bindingOutcome with
                    | Rejected r -> r
                    | Allowed -> "" // unreachable — bindingRejected implies Rejected

                recordOutcome m.Name (ModuleBindingRejected reason) app
            else

                // Phase 169 — registered vs. unbound-allowed. A configured
                // verifier admitting an unstamped module is the distinct
                // "unbound-allowed" outcome; everything else loading is a
                // plain "registered".
                let loadOutcome =
                    match app.ModuleBindingVerifier, m.BindingStamp with
                    | Some _, None -> ModuleUnboundAllowed
                    | _ -> ModuleRegistered

                let moduleConfigs =
                    match m.ConfigSchema with
                    | None -> app.ModuleConfigs
                    | Some schema ->
                        app.ModuleConfigs
                        @ [
                            {
                                ModuleKey = m.Name
                                DisplayName = m.Name
                                Schema = schema
                            }
                        ]

                let queryRegistrations = m.QueryHandlers |> List.map (fun h -> m.Name, h)
                let dataTypeRegistrations = m.DataTypes |> List.map (fun dt -> m.Name, dt)

                // Phase 283 — permit the `component_id` correlation
                // dimension on every module metric's tag allowlist so
                // per-component telemetry can be keyed by the stable
                // ComponentId across a display-name rename. Additive: the
                // allowlist merely *accepts* the id when an emission carries
                // it (via `Metrics.ComponentCorrelation.withComponentId`);
                // rendered output stays byte-identical until then (GP 11),
                // and no id dimension is allocated on the hot path when
                // unused (GP 13).
                let metricRegistrations: Metrics.MetricRegistration list =
                    m.MetricDefinitions
                    |> List.map (fun d -> {
                        Module = Some m.Name
                        Definition = Metrics.ComponentCorrelation.permitComponentIdDimension d
                    })

                let mergedSlowRequestOverrides =
                    m.SlowRequestThresholdOverrides
                    |> Map.fold (fun acc k v -> Map.add k v acc) app.Config.SlowRequestThresholdOverrides

                // Phase 66 Stream B.3 — fan a module's surface-requirement
                // declarations into the app-level accumulators. A module
                // with no `RoutePrefixes` contributes nothing to the
                // `ModulePrefixes` registry (its `DefaultSurfaceRequirement`
                // value is moot without a prefix to apply it to), so
                // pre-B.3 modules — which declare neither field — stay
                // byte-identical: `app.ModuleSurfaceDefaults` and
                // `app.RouteSurfaceOverrides` accumulate empty contributions.
                let surfaceDefaultsForModule =
                    m.RoutePrefixes |> List.map (fun prefix -> prefix, m.DefaultSurfaceRequirement)

                // Phase 279 — resolve the module's stable ComponentId
                // (explicit when declared, else name-derived) and
                // accumulate it for the compose-time uniqueness check in
                // `run`. A module that declares nothing resolves to
                // `ComponentId.ofModule Name`, byte-for-byte the pre-279
                // identity (GP 11).
                let resolvedComponentId =
                    m.ComponentId |> Option.defaultValue (ComponentId.ofModule m.Name)

                {
                    app with
                        Handlers = app.Handlers @ m.Handlers
                        DataTypes = app.DataTypes @ m.DataTypes
                        VectorisationHandlers = app.VectorisationHandlers @ m.VectorisationHandlers
                        ModuleNames = app.ModuleNames @ [ m.Name ]
                        ModuleConfigs = moduleConfigs
                        QueryHandlerRegistrations = app.QueryHandlerRegistrations @ queryRegistrations
                        DataTypeRegistrations = app.DataTypeRegistrations @ dataTypeRegistrations
                        AITools = app.AITools @ m.AITools
                        MetricRegistrations = app.MetricRegistrations @ metricRegistrations
                        ModuleSurfaceDefaults = app.ModuleSurfaceDefaults @ surfaceDefaultsForModule
                        RouteSurfaceOverrides = app.RouteSurfaceOverrides @ m.RouteSurfaceRequirements
                        // Phase 9b.B — fan module-level job declarations
                        // onto the app's accumulator. Modules pre-9b.B
                        // declare no `JobHandlers`, so their contribution
                        // is the empty list and behaviour stays byte-
                        // identical.
                        ScheduledJobs = app.ScheduledJobs @ m.JobHandlers
                        // Phase 169 — record the load outcome (registered /
                        // unbound-allowed) for the startup log.
                        ModuleLoadOutcomes = app.ModuleLoadOutcomes @ [ m.Name, loadOutcome ]
                        // Phase 279 — accumulate the resolved stable id for
                        // the compose-time uniqueness check in `run`.
                        ModuleComponentIds = app.ModuleComponentIds @ [ m.Name, resolvedComponentId ]
                        Config = {
                            app.Config with
                                SlowRequestThresholdOverrides = mergedSlowRequestOverrides
                        }
                }

    let addModules (modules: ServerModule list) (app: ServerApp) : ServerApp =
        modules |> List.fold (fun a m -> addModule m a) app

    /// Phase 280 — project the live composition registry into a read-only,
    /// machine-readable `CompositionManifest`: every registered module,
    /// companion slot, datatype, and tool by its stable Phase 279
    /// `ComponentId`, plus the config knobs that shaped composition.
    ///
    /// Derived entirely from the accumulators `addModule` and the `with*`
    /// builders populate on this record — the same live registry the
    /// config-drift detector snapshots — never a separately-declared list,
    /// so the manifest cannot drift from what was actually composed.
    ///
    /// Pure + on demand: a deployment that never calls this builds no
    /// manifest and pays nothing (GP 13); an app that adds the call is
    /// byte-for-byte unchanged until it does (GP 11). Companion slots are
    /// enumerated from the substrate the consumer explicitly composed onto
    /// the record — single-impl optionals contribute one slot entry when
    /// populated; multi-impl lists contribute one entry per impl, keyed by
    /// the impl's own sub-id (sink `Name` / `Kind`), never its position.
    let compositionManifest (app: ServerApp) : CompositionManifest =
        let modules = app.ModuleComponentIds |> List.map CompositionManifest.moduleEntry

        let dataTypes =
            app.DataTypeRegistrations
            |> List.map (fun (_, dt) -> CompositionManifest.dataTypeEntry dt.Id)
            |> List.distinct

        let tools =
            app.AITools
            |> List.map (fun (def, _) -> CompositionManifest.toolEntry def.Name)
            |> List.distinct

        let companionSlots = [
            match app.Auth with
            | Some _ -> CompositionManifest.companionSlotEntry "IAuthProvider"
            | None -> ()
            match app.Storage with
            | Some _ -> CompositionManifest.companionSlotEntry "IBlobStorage"
            | None -> ()
            match app.Notifications with
            | Some _ -> CompositionManifest.companionSlotEntry "INotificationChannel"
            | None -> ()
            match app.ProviderProfile with
            | Some _ -> CompositionManifest.companionSlotEntry "IProviderProfile"
            | None -> ()
            match app.UserDirectory with
            | Some _ -> CompositionManifest.companionSlotEntry "IUserDirectory"
            | None -> ()

            for sink in app.AuditSinks do
                CompositionManifest.companionImplEntry "IAuditSink" sink.Name
            for sink in app.TransactionalSinks do
                CompositionManifest.companionImplEntry
                    "INotificationSink"
                    (NotificationKind.SinkKind.toWireString sink.Kind)
            for check in app.HealthChecks do
                CompositionManifest.companionImplEntry "IHealthCheck" check.Name
            for validator in app.ConfigValidators do
                CompositionManifest.companionImplEntry "IConfigValidator" validator.Name
            for smoke in app.SmokeTests do
                CompositionManifest.companionImplEntry "ISmokeTest" smoke.Name
        ]

        // The `ServerConfig` switches that change *what* gets composed.
        // String-rendered DU case names — a stable, human-readable summary
        // for drift detection / dashboards without duplicating the config.
        let configKnobs = [
            CompositionManifest.knob "ProcessProfile" (string app.Config.ProcessProfile)
            CompositionManifest.knob "ConfigDriftDetection" (string app.Config.ConfigDriftDetection)
            CompositionManifest.knob "JobScheduler" (string app.Config.JobScheduler)
            CompositionManifest.knob "EntityStore" (string app.Config.EntityStore)
            CompositionManifest.knob "UsageMetering" (string app.Config.UsageMetering)
            CompositionManifest.knob "MetricsEndpoint" (string app.Config.MetricsEndpoint)
            CompositionManifest.knob "RateLimiter" (string app.Config.RateLimiter)
            CompositionManifest.knob "SmokeTest" (string app.Config.SmokeTest)
            CompositionManifest.knob "DataSubjectRequests" (string app.Config.DataSubjectRequests)
            CompositionManifest.knob "PeerSubstrate" (string app.Config.PeerSubstrate)
        ]

        CompositionManifest.build modules companionSlots dataTypes tools configKnobs

    /// Phase 283 — resolve a composed module's stable Phase 279 `ComponentId`
    /// from its display name (the label its audit events carry as
    /// `SourceModule`, and the dimension its metrics namespace under). This
    /// is the correlation join for the audit + telemetry paths: declare an
    /// explicit id via `ServerModule.withComponentId`, rename the display
    /// `Name` freely, and this still resolves the same id, so a component's
    /// audit trail and metric series correlate across the rename. A module
    /// that declares no explicit id resolves to `ComponentId.ofModule Name`
    /// (byte-for-byte the pre-283 identity, GP 11) — to survive a rename it
    /// must declare an explicit id. `None` when no module of that name is
    /// composed (a reserved `_platform.*` source, or an unregistered name).
    let componentIdForModule (moduleName: string) (app: ServerApp) : ComponentId option =
        app.ModuleComponentIds
        |> List.tryPick (fun (n, id) -> if n = moduleName then Some id else None)

    /// Assemble the final `ServerConfig` (merging accumulated module names and
    /// configs) and invoke the underlying `compose`. Returns the process exit
    /// code — `0` for graceful shutdown. Suitable as the body of an `[<EntryPoint>]`.
    let run (app: ServerApp) : int =
        // Phase 279 — fail fast on a duplicate module ComponentId before
        // anything binds. Identity collisions break every introspection /
        // telemetry-correlation surface that keys on the id, so they are a
        // compose-time error, not a runtime surprise. A pre-279 app whose
        // module ids are all distinct (the universal case — the id is the
        // permission / sidebar key) passes silently (GP 11).
        ComponentId.ensureUnique "module composition" (app.ModuleComponentIds |> List.map snd)

        // Phase 169 — emit the accumulated module-load outcomes through the
        // startup logger. Registered / unbound-allowed log at Debug (silent
        // at the default Info level — GP 13); a name-filter drop logs Info,
        // a binding rejection logs Warn, so a "missing module" is visible.
        app.Logger
        |> Option.iter (fun logger ->
            for moduleId, outcome in app.ModuleLoadOutcomes do
                match outcome with
                | ModuleRegistered -> logger.Debug(sprintf "module-load: %s registered" moduleId)
                | ModuleUnboundAllowed -> logger.Debug(sprintf "module-load: %s unbound-allowed" moduleId)
                | ModuleFiltered filter -> logger.Info(sprintf "module-load: %s filtered (filter: %s)" moduleId filter)
                | ModuleBindingRejected reason ->
                    logger.Warn(sprintf "module-load: %s binding-rejected: %s" moduleId reason))

        let config = {
            app.Config with
                ModuleNames =
                    if app.Config.ModuleNames.IsEmpty then
                        app.ModuleNames
                    else
                        app.Config.ModuleNames
                ModuleConfigs = app.Config.ModuleConfigs @ app.ModuleConfigs
        }

        // Phase 43.A — when a canonical BYOK provider-profile store is
        // registered (via `withProviderProfile`, or mirrored from
        // `AIServerApp.create` / `RAGServerApp.create`), fold its
        // `IProviderProfile` DI singleton into the extension's
        // `ServiceConfig` so in-process consumers resolve it. `None`
        // leaves `Extensions` untouched — zero footprint (GP 13).
        let withProviderProfileService =
            match app.ProviderProfile with
            | None -> app.Extensions
            | Some store ->
                let register (s: IServiceCollection) = s.AddSingleton<IProviderProfile>(store)

                {
                    app.Extensions with
                        ServiceConfig =
                            match app.Extensions.ServiceConfig with
                            | None -> Some register
                            | Some baseFn -> Some(fun s -> register (baseFn s))
                }

        // Phase 9h — DSR exporters / erasure handlers. Folded as
        // `services.AddSingleton<IDataExporter>(exp)` per registered
        // entry so `IDataSubjectRequestApiHandler` resolves
        // `seq<IDataExporter>` / `seq<IErasureHandler>` from
        // `HttpContext.RequestServices` per request — the
        // multi-implementation DI pattern ASP.NET Core ships out of
        // the box. Empty lists leave `Extensions` untouched; the route
        // mount itself is gated separately on
        // `ServerConfig.DataSubjectRequests = Enabled _` so a
        // deployment that registers exporters without enabling the
        // substrate pays only the DI-registration cost (~tens of
        // bytes per impl) and the route never reaches them.
        let extensions =
            let appendRegistration
                (current: ComposeExtensions)
                (register: IServiceCollection -> IServiceCollection)
                : ComposeExtensions =
                {
                    current with
                        ServiceConfig =
                            match current.ServiceConfig with
                            | None -> Some register
                            | Some baseFn -> Some(fun s -> register (baseFn s))
                }

            let withExporters =
                app.DataExporters
                |> List.fold
                    (fun acc exp -> appendRegistration acc (fun s -> s.AddSingleton<IDataExporter>(exp)))
                    withProviderProfileService

            let withErasureHandlers =
                app.ErasureHandlers
                |> List.fold
                    (fun acc h -> appendRegistration acc (fun s -> s.AddSingleton<IErasureHandler>(h)))
                    withExporters

            // 0.5.7 — fold the optional `IUserDirectory` companion into
            // the DI graph. When `None`, no registration is appended —
            // `UserDirectoryApiHandler.resolveDirectory` reads `None`
            // from DI and short-circuits every `SearchUsers` call to
            // `Ok []`. When `Some`, the companion is registered as a
            // singleton; the handler resolves it lazily per request.
            match app.UserDirectory with
            | None -> withErasureHandlers
            | Some directory ->
                appendRegistration withErasureHandlers (fun s -> s.AddSingleton<IUserDirectory>(directory))

        // Phase 16 — `compose` returns `IServerHost`. Kestrel default
        // chains `RunBlocking()` to preserve `int` exit code semantics.
        let host =
            compose
                app.Handlers
                app.DataTypes
                config
                app.Auth
                extensions
                app.Logger
                app.Storage
                app.Notifications
                app.QueryHandlerRegistrations
                app.DataTypeRegistrations
                app.TransactionalSinks
                app.HealthChecks
                app.ConfigValidators
                app.EncryptionKeyResolver
                app.EntityRegistrations
                app.QuotaPolicy
                app.MetricsSinks
                app.AuditSinks
                app.AuditReplicator
                app.MetricRegistrations
                app.ActivitySink
                app.RateLimitDescriptors
                app.SmokeTests
                app.PendingInviteStore
                app.SubjectMigrator
                app.ShareTokenStoreDecorators
                app.ModuleSurfaceDefaults
                app.RouteSurfaceOverrides
                app.ScheduledJobs
                app.StorageResilience
                app.SecretResilience

        host.RunBlocking()