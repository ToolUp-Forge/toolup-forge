module ToolUp.Platform.Server

open Giraffe
open System
open System.Globalization
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.FileManagement
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.WebhookDispatcher
open ToolUp.Platform.Usage
open ToolUp.Platform.Middleware
open ToolUp.Platform.SurfaceEnforcement
open ToolUp.Platform.Tracing
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.PlatformSchema
open ToolUp.Platform.PlatformApiHandler
open ToolUp.Platform.IDataExporter
open Microsoft.Extensions.Caching.Memory

open ToolUp.Platform.ConfigurePipeline
open ToolUp.Platform.BuildRouteHandlers
open ToolUp.Platform.ComposeAudit
open ToolUp.Platform.ComposeEncryption
open ToolUp.Platform.ComposeAuth
open ToolUp.Platform.ComposeNotifications
open ToolUp.Platform.ComposeJobs
open ToolUp.Platform.ComposeDevDiagnostics
open ToolUp.Platform.ComposeConfigValidators
open ToolUp.Platform.ComposeRuntimeServices
open ToolUp.Platform.ComposeStores
open ToolUp.Platform.ComposeTeamRuntime
open ToolUp.Platform.ComposeHealthSmoke
open ToolUp.Platform.ComposeScopeResolver
open ToolUp.Platform.ComposeBootstrap

/// Compose a list of pre-built Giraffe HttpHandlers into a Saturn application.
/// When dataTypes is non-empty, the file management API is auto-injected.
/// PlatformApi is always auto-injected (provides mode info and team management).
///
/// `extensions` is a hook for companion packages (e.g. ToolUp.AI) to
/// contribute their own handlers and DI registrations without the SDK
/// having to know about them. Applications that don't use companions
/// pass `ComposeExtensions.empty`.
///
/// `logger` lets the application supply its own `ILogger` instance (e.g.
/// a structured logger, or a `ConsoleLogger` the app also uses to emit
/// pre-compose startup diagnostics). When `None`, a fresh `ConsoleLogger`
/// is constructed here. The supplied instance is what DI hands out for
/// every `ILogger` resolution — this is the single-substitution-point
/// contract for logging.
///
/// `blobStorage` lets the application supply a cloud-backed
/// `IBlobStorage` (Azure / S3 / GCS via the `src/Storage/*` sub-
/// companions) or any other custom implementation. When `None`,
/// `LocalFileStorage` rooted at `./data` is used — the dev default.
/// Apps that construct their own storage should use the same instance
/// wherever they need blob access (e.g. a co-located `IUserAIConfigStore`);
/// passing it here also makes it the DI-registered singleton for
/// request handlers that resolve `IBlobStorage`.
///
/// `notificationChannel` lets the application supply a distributed
/// `INotificationChannel` (Redis pub/sub via
/// `src/NotificationChannels/Redis`, future NATS / Orleans streams).
/// When `None`, the in-process `InMemoryNotificationChannel` is used —
/// single-process dev default. The supplied instance becomes the
/// DI-registered singleton for every handler that resolves
/// `INotificationChannel`, including the auto-injected
/// `/api/notifications` SSE endpoint.
///
/// Reads SERVER_PORT from environment, falling back to config.Port.
/// Optional authProvider overrides the default (HeaderAuthProvider —
/// spoofable; safe only because the security-class
/// `HeaderAuthProviderModeValidator` refuses startup in any
/// auth-requiring Mode, and `SkipPreflight` cannot bypass it).
///
/// **Advanced.** Apps should use the `ServerApp` / `AIServerApp` /
/// `RAGServerApp` record-based fluent API instead — `compose` is the
/// low-level entry point and its positional signature changes whenever
/// new SDK features land. Hidden from IntelliSense via
/// `[<EditorBrowsable>]`.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
let compose
    (handlers: HttpHandler list)
    (dataTypes: DataType list)
    (config: ServerConfig)
    (authProvider: IAuthProvider option)
    (extensions: ComposeExtensions)
    (logger: ILogger option)
    (blobStorage: IBlobStorage option)
    (notificationChannel: INotificationChannel option)
    (queryHandlers: (string * ModuleQueryHandler) list)
    (dataTypeRegistrations: (string * DataType) list)
    (transactionalSinks: INotificationSink list)
    (healthChecks: HealthChecks.IHealthCheck list)
    (configValidators: ConfigValidation.IConfigValidator list)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    (entityRegistrations: (EntityStore.EntityRegistry -> unit) list)
    (quotaPolicyOverride: ITeamQuotaPolicy option)
    (companionMetricsSinks: Metrics.IMetricsSink list)
    (auditSinks: IAuditSink list)
    (auditReplicatorOptions: AuditReplicatorOptions option)
    (moduleMetricRegistrations: Metrics.MetricRegistration list)
    (activitySinkOverride: IActivitySink option)
    (rateLimitDescriptors: RateLimitDescriptor list)
    (smokeTests: SmokeTests.ISmokeTest list)
    (pendingInviteStoreOverride: IPendingInviteStore option)
    (subjectMigratorOverride: IAnonymousSessionMigrator option)
    (shareTokenStoreDecorators: (IShareTokenStore -> IShareTokenStore) list)
    (moduleSurfaceDefaults: (string * SurfaceRequirement) list)
    (routeSurfaceOverrides: ((string * string) * SurfaceRequirement) list)
    (scheduledJobDeclarations: ScheduledJobDeclaration list)
    =

    // Phase 9l — resolve the deployment's distributed-tracing sink.
    // Wrap of `OtelActivitySink` (or any custom companion) is opt-in
    // via `ServerApp.withActivitySink`; the unset default is
    // `NoOpActivitySink` so every seam that resolves `IActivitySink`
    // from DI gets a non-null reference and the call elides at zero
    // cost. Stored locally so the `WebhookDispatcher.create`,
    // `JobScheduler.create`, `TransactionalDispatcher` constructor,
    // and `InMemoryModuleQueryBus` constructor below can capture it
    // (each takes `IActivitySink` as a constructor parameter rather
    // than reaching into DI on every call).
    let resolvedActivitySink: IActivitySink =
        activitySinkOverride
        |> Option.defaultWith (fun () -> NoOpActivitySink() :> IActivitySink)

    // Phase 1g — resolve `ServerConfig.Notifications`. `NotificationsAuto`
    // (the default) flips to `InMemoryNotifications` whenever a feature
    // that publishes notifications is active: background jobs (dead-letter
    // notifications), `MultiTeam` mode (membership-change events feed the
    // client team-switch reset path), or a wrapping companion declared
    // itself a publisher via `extensions.NotificationConsumers`. Otherwise
    // the lightweight default flips to `NoNotifications` — no SSE route
    // is mounted and the channel resolves to `NoOpNotificationChannel`
    // so existing consumers (`TeamStore`, `JobScheduler`) don't crash on
    // a missing dependency. Apps that explicitly set `Notifications`
    // bypass the auto-detection. Resolution rules live in
    // `NotificationMode.resolve` so unit tests can pin them.
    let effectiveNotifications =
        NotificationMode.resolve
            config.Notifications
            config.JobScheduler
            (DeploymentConfig.hasMultiTeamSwitcher config)
            extensions.NotificationConsumers

    // Typed cells for the concrete `PersistentEventStore` /
    // `BlobJobStore` instances. `ref` (not `let mutable`) so
    // `buildRouteHandlers` can capture them by reference for the
    // MaintenanceApi thunks while the event-store / job-scheduler
    // builders below populate them. Remain `None` when the deployment
    // uses in-memory event store / no scheduler.
    let persistentEventStoreInstance: PersistentEventStore.PersistentEventStore option ref =
        ref None

    let blobJobStoreInstance: JobStore.BlobJobStore option ref = ref None

    let routeHandlers =
        buildRouteHandlers
            config
            handlers
            dataTypes
            extensions
            transactionalSinks
            encryptionKeyResolver
            effectiveNotifications
            persistentEventStoreInstance
            blobJobStoreInstance

    let serverPort =
        match Environment.GetEnvironmentVariable "SERVER_PORT" with
        | null
        | "" -> string config.Port
        | port -> port

    // Fail fast on an out-of-range / non-numeric port. Without this the
    // raw string flows into `UseUrls($"http://0.0.0.0:{serverPort}")` and
    // Kestrel rejects it with an opaque bind-time error far from the
    // actual cause (a typo'd SERVER_PORT env var or an out-of-range
    // ServerConfig.Port). Surfacing it here names the offending value.
    match Int32.TryParse serverPort with
    | true, p when p >= 1 && p <= 65535 -> ()
    | _ ->
        failwithf
            "SERVER_PORT / ServerConfig.Port = %s is not a valid TCP port. Expected an integer in 1-65535 (set the SERVER_PORT environment variable or ServerConfig.Port to a valid port)."
            serverPort

    let culture = CultureInfo "en-GB"
    CultureInfo.DefaultThreadCurrentCulture <- culture
    CultureInfo.DefaultThreadCurrentUICulture <- culture

    let auth = resolveAuthProvider authProvider

    // Raw ASP.NET Core composition — previously the Saturn `application { }`
    // DSL wrapped this same `WebApplication.CreateBuilder` / `Build` /
    // `Run` sequence; with Saturn removed the composition is spelled out
    // directly. Behaviour is identical to the prior Saturn version:
    // - `url` → `builder.WebHost.UseUrls`
    // - `memory_cache` → `services.AddMemoryCache`
    // - `use_static` → `app.UseStaticFiles` with a `PhysicalFileProvider`
    // - `service_config` → direct registrations on `builder.Services`
    // - `app_config` → direct middleware calls on `app`
    // - `use_gzip` → `AddResponseCompression` + `UseResponseCompression`
    // - `use_router` → `app.UseGiraffe router`
    //
    // Phase 16a — `ProcessProfile = WorkerOnly` silos serve no HTTP, so
    // the builder is a generic `Host.CreateApplicationBuilder()` with no
    // Kestrel listener at all (rather than a `WebApplication.CreateBuilder()`
    // bound to a non-routed port — the pre-refactor shape). Both shapes
    // expose `.Services` (`IServiceCollection`), so every DI registration
    // between this point and `.Build()` runs identically against either.
    // The tail of `compose` discriminates: `WebApplicationBuilder.Build()`
    // returns a `WebApplication` driven through `configurePipeline` +
    // `createWebHost`; `HostApplicationBuilder.Build()` returns a generic
    // `IHost` with no HTTP pipeline, returned through `createWorkerHost`
    // (whose `Invoke` raises — sibling `WebOnly` silos own the HTTP
    // surface).
    // Phase 16a — host builder shape per `ProcessProfile` (extracted to
    // `ComposeBootstrap.buildHostBuilder`). Exactly one of
    // `webBuilder` / `workerBuilder` is `Some`; the other is `None`.
    let webBuilder, workerBuilder = buildHostBuilder config serverPort

    // ─── Service configuration (previously Saturn's `service_config`) ─────
    let innerBlobStorage =
        blobStorage
        |> Option.defaultWith (fun () ->
            let storagePath = IO.Path.Combine(IO.Directory.GetCurrentDirectory(), "data")
            LocalFileStorage.LocalFileStorage(storagePath) :> IBlobStorage)

    // Phase 22 — apply the `EncryptedBlobStorage` decorator (extracted
    // to `ComposeEncryption.applyEncryptionDecorator`). Every downstream
    // consumer (DataObjectStore, EventStore, JobStore, WebhookRegistry,
    // TeamStore, PermissionStore, NotificationAddressBook, etc.) receives
    // the wrapped instance.
    let resolvedBlobStorage =
        applyEncryptionDecorator innerBlobStorage encryptionKeyResolver

    let secretStore = FileSecretStore.FileSecretStore() :> Secrets.ISecretStore

    let resolvedLogger =
        logger
        |> Option.defaultWith (fun () ->
            // Phase 6h follow-up — Workstream B. Pass the configured
            // log level + trace category whitelist so a deployment can
            // light up `ITraceLogger`-driven diagnostic streams without
            // recompiling. App-supplied loggers are responsible for
            // their own configuration; this is the default-impl path.
            //
            // Phase 9e.1 — opt into JSON-structured output for log
            // aggregation (Elasticsearch / CloudWatch / Datadog / Loki)
            // with `TOOLUP_LOG_FORMAT=json`. Unset / anything else keeps
            // the plain `ConsoleLogger` so existing deployments stay
            // byte-identical (GP 11/13). Mirrors the existing
            // `TOOLUP_LOG_LEVEL` / `TOOLUP_TRACE_CATEGORIES` env-override
            // model for default-logger configuration.
            let wantsJson =
                match System.Environment.GetEnvironmentVariable "TOOLUP_LOG_FORMAT" with
                | null -> false
                | s -> s.Trim().ToLowerInvariant() = "json"

            if wantsJson then
                JsonConsoleLogger.JsonConsoleLogger(config.LogLevel, config.TraceCategories) :> ILogger
            else
                ConsoleLogger.ConsoleLogger(config.LogLevel, config.TraceCategories) :> ILogger)

    // The resolved logger is later folded into `FileManagementRuntime`
    // (built and DI-registered further down in this function). The
    // post-save hook dispatch site reads it from the runtime.

    // Resolve event store per config.EventStore.
    // - InMemoryOnly: thread-safe list, lost on restart.
    // - PersistentBlobBacked: blob-backed append-only store using
    //   the registered IBlobStorage (LocalFileStorage on disk by
    //   default; Azure / S3 / GCS when the app opts in).
    // The `persistentEventStoreInstance` cell is hoisted to the top
    // of `compose` (alongside `blobJobStoreInstance`) so the
    // MaintenanceApi handler and the /dev/inspect index inspector
    // can both capture it via thunks. Populate here in the
    // `PersistentBlobBacked` branch.
    let innerEventStore: IEventStore =
        match config.EventStore with
        | InMemoryOnly -> InMemoryEventStore.InMemoryEventStore() :> _
        | PersistentBlobBacked retention ->
            let store =
                PersistentEventStore.PersistentEventStore(resolvedBlobStorage, retention)

            persistentEventStoreInstance.Value <- Some store
            store :> _

    // Phase 6d / Phase 1g webhook subsystem. Registry + delivery log
    // are blob-backed against the registered `IBlobStorage`; the
    // dispatcher is a `BackgroundService` that owns an in-process
    // retry/dead-letter state machine and the outbound `HttpClient`.
    // The dispatcher writes its own audit events
    // (`WebhookDeliveryFailed`, `WebhookSubscriptionAutoDisabled`)
    // directly to `innerEventStore` so a failing alerting webhook
    // can't cascade through the hook. `WebhookApiHandler` (subscription
    // create/update/delete) writes through the wrapped store below —
    // admins *can* alert on those.
    //
    // Phase 1g — `NoWebhooks` (the default) skips every registration:
    // no dispatcher `BackgroundService`, no `HookedEventStore`
    // decorator wrapping `IEventStore`, no `IWebhookRegistry` /
    // `IWebhookDeliveryLog` / `IWebhookDispatcher` DI services, and
    // no admin route. The lightweight default carries zero webhook
    // overhead.
    //
    // Single-instance limitation (Phase 9c follow-up): the queue is
    // in-process; running multiple silos with the same registry would
    // each consume the post-write hook and double-dispatch. Documented
    // in `TECHNICAL_GUIDE.md` — replacing with a durable queue is the
    // distributed migration path.
    // Phase 9g — audit replicator subsystem (extracted to
    // `ComposeAudit.buildAuditReplicatorSubsystem`). When `AuditSinks`
    // is non-empty, the replicator decorates the inner event store and
    // runs as a `BackgroundService`; the decorator is the innermost
    // wrap so webhook fan-out and job-notify sit outside it.
    let auditReplicatorSubsystem =
        buildAuditReplicatorSubsystem
            auditSinks
            auditReplicatorOptions
            config.AuditSamplingPolicy
            resolvedBlobStorage
            innerEventStore
            resolvedLogger

    let effectiveInnerEventStore =
        effectiveInnerEventStore innerEventStore auditReplicatorSubsystem

    // Phase 9v — chicken-and-egg break for the outbound rate limiter.
    // The real `IRateLimiter` is constructed further down (after
    // `auditLog` / `resolvedMetricsSink`, both of which transitively
    // depend on the post-webhook event-store decorator chain), but the
    // webhook dispatcher needs a stable reference now so its retry
    // loop can call `Wait` per attempt. Mirrors the `jobSchedulerCell`
    // pattern below: initialise with a pass-through (`NoOpRateLimiter`)
    // so any `Wait` call during compose is silent; overwrite with the
    // real instance once compose has built it. By the time the
    // dispatcher's `BackgroundService.ExecuteAsync` actually invokes
    // `Wait`, the cell holds the configured limiter.
    let rateLimiterCell: IRateLimiter ref = ref (NoOpRateLimiter() :> IRateLimiter)
    let rateLimiterLookup () = rateLimiterCell.Value

    let webhookSubsystem =
        buildWebhookSubsystem
            config
            resolvedBlobStorage
            effectiveInnerEventStore
            resolvedLogger
            resolvedActivitySink
            rateLimiterLookup

    // Phase 9b — chicken-and-egg break for the job substrate. The
    // notify-wrapper needs the scheduler to forward events to; the
    // scheduler needs the (notifying) event store for its own
    // emissions. Use a mutable cell that compose populates after
    // constructing the scheduler — every Write *during compose*
    // sees `None` and skips the notify, every Write *after compose*
    // sees the registered scheduler and dispatches OnEvent triggers.
    let jobSchedulerCell: IJobScheduler option ref = ref None
    let jobSchedulerLookup () = jobSchedulerCell.Value

    // Public `IEventStore` registered in DI — final decorator chain
    // (extracted to `ComposeJobs.applyEventStoreDecorators`):
    //   - With webhooks + job scheduler: `JobNotify -> WebhookHooked -> Inner`
    //   - With webhooks only:            `WebhookHooked -> Inner`
    //   - With job scheduler only:       `JobNotify -> Inner`
    //   - Lightweight default:           `Inner` (no decorators)
    let eventStore =
        applyEventStoreDecorators config effectiveInnerEventStore webhookSubsystem jobSchedulerLookup

    // Phase 114 — chicken-and-egg break for the audit-write-failure
    // metric. The `EventStoreAuditLog` is constructed inside
    // `buildNotificationStack` (below), but the resolved `IMetricsSink`
    // is built further down (after `services` exists). Same pattern as
    // `rateLimiterCell` / `jobSchedulerCell`: hand the audit log a
    // reader over a cell holding a `NoOpMetricsSink` now; overwrite the
    // cell with the resolved sink once compose builds it. The audit log
    // reads the cell lazily and only on the failure path, so by the time
    // a real write fails at runtime the cell holds the configured sink.
    let metricsSinkCell: Metrics.IMetricsSink ref =
        ref (Metrics.NoOpMetricsSink() :> Metrics.IMetricsSink)

    let metricsSinkLookup () = metricsSinkCell.Value

    // Build the notification-stack substrate (extracted to
    // `ComposeNotifications.buildNotificationStack`). Pre-DI
    // construction so the Phase 6f transactional dispatcher and the
    // share-token store see the same `auditLog` / `configStoreInstance`
    // instances DI hands out to other consumers.
    let notificationStack =
        buildNotificationStack
            config
            effectiveNotifications
            notificationChannel
            resolvedBlobStorage
            secretStore
            eventStore
            resolvedLogger
            resolvedActivitySink
            logger
            transactionalSinks
            metricsSinkLookup

    let sseConnectionManager = notificationStack.SseConnectionManager
    let baseNotificationChannel = notificationStack.BaseNotificationChannel
    let configStoreInstance = notificationStack.ConfigStoreInstance
    let auditLog = notificationStack.AuditLog
    // Phase 66 Stream A.7 / C.6 — apply the composable
    // `ShareTokenStoreDecorators` chain wrapped around the resolved
    // `IShareTokenStore`. Fold-left applies decorators outside-in so
    // the LAST `withShareTokenStoreDecorator` call wraps the others
    // (matching the docstring contract on the builder). No-op when
    // `ServerConfig.ShareTokenStore = NoShareTokenStore` — the
    // underlying instance is `None` so the decorator list is
    // structurally unreachable. `SurfaceCoherenceValidator` (Stream
    // B.2) warns at startup when decorators are registered but no
    // store is wired.
    let shareTokenStoreInstance =
        notificationStack.ShareTokenStoreInstance
        |> Option.map (fun inner ->
            shareTokenStoreDecorators
            |> List.fold (fun acc decorator -> decorator acc) inner)

    let transactionalDispatcher = notificationStack.TransactionalDispatcher
    let resolvedNotificationChannel = notificationStack.ResolvedNotificationChannel
    let narrativeStore = notificationStack.NarrativeStore

    // Phase 16a — `.Services` is `IServiceCollection` on both
    // `WebApplicationBuilder` and `HostApplicationBuilder`; the rest of
    // `compose` registers against it without knowing which builder shape
    // backs it. The match is exhaustive by construction — exactly one of
    // `webBuilder` / `workerBuilder` is `Some` (the other is `None`).
    let services =
        match webBuilder, workerBuilder with
        | Some b, _ -> b.Services
        | _, Some b -> b.Services
        | None, None -> failwith "unreachable: exactly one of webBuilder / workerBuilder is constructed"

    // Memory caches + response compression + Phase 9j DataProtection
    // key ring (extracted to
    // `ComposeRuntimeServices.registerCachingAndDataProtection`).
    registerCachingAndDataProtection services resolvedBlobStorage

    // Phase 1f — CORS service registration (extracted to
    // `ComposeRuntimeServices.registerCors`).
    registerCors services config resolvedLogger

    // `auditLog` is constructed earlier in `compose` (above the
    // transactional dispatcher) so step (b) of Phase 6f can wire
    // `IAuditLog.Record` into delivery outcomes. The original
    // location is documented inline next to that block.
    ()

    // Phase 7 / 7a — `IDataObjectStore` + `IDataCatalog` construction
    // (extracted to `ComposeStores.buildDataObjectStoreAndCatalog`).
    let dataObjectStore, dataCatalog =
        buildDataObjectStoreAndCatalog resolvedBlobStorage resolvedLogger dataTypeRegistrations

    // Phase 8 / 8a / 53 — optional state-store values (extracted to
    // `ComposeStores.buildOptionalStoreValues`). Returns `None` for
    // unset modes; downstream `registerOptionalStores` skips
    // registration so DI resolution returns `null` and consumers must
    // handle absence explicitly.
    let lineageStore, resultStore, conversationStore =
        buildOptionalStoreValues config eventStore dataObjectStore

    // Phase 22 — register the encryption resolver as a singleton when
    // the deployment opted in (extracted to
    // `ComposeEncryption.registerEncryptionResolver`).
    registerEncryptionResolver services encryptionKeyResolver

    // Phase 118 — degraded-capability registry. Registered
    // unconditionally (best-effort sites must be able to register into it)
    // but zero-cost when empty (GP 13): the `/health` writer skips the
    // section on `IsEmpty`. The `/dev/inspect` contributor renders the
    // same set; both gated by their existing endpoint gates.
    let degradedCapabilities = DegradedCapabilities.DegradedCapabilityRegistry()

    services.AddSingleton<DegradedCapabilities.DegradedCapabilityRegistry>(degradedCapabilities)
    |> ignore

    services.AddSingleton<IDevDiagnosticsContributor>(
        DegradedCapabilities.DegradedCapabilitiesDiagnosticsContributor(degradedCapabilities)
        :> IDevDiagnosticsContributor
    )
    |> ignore

    // Phase 120 — default IAuthAuditHook over the resolved IAuditLog.
    // Every authorization-denial emission point (surface enforcement,
    // share-token validation, …) resolves this from DI and writes a
    // uniform AuthorizationDenied row; the default coalesces probing
    // bursts via its per-(route,subject) flood guard. GP 13 — the backing
    // store is the existing audit log, no new infrastructure.
    services.AddSingleton<IAuthAuditHook>(AuthAuditHook.AuthAuditHook(auditLog, resolvedLogger) :> IAuthAuditHook)
    |> ignore

    // Gap audit #1 — wire `PerScopeKeyResolver` to the cross-process
    // notification channel for multi-instance cache coherence
    // (extracted to
    // `ComposeEncryption.wirePerScopeResolverToNotificationChannel`).
    // Phase 118 — first adopter: a failed subscribe now logs `Error` and
    // registers a `crypto-shred-cache-eviction` degraded entry instead of
    // swallowing the exception silently.
    wirePerScopeResolverToNotificationChannel
        encryptionKeyResolver
        resolvedNotificationChannel
        degradedCapabilities
        resolvedLogger

    // Phase 19 — entity-store registration (extracted to
    // `ComposeStores.registerEntityStore`). Phase 26 — when
    // `DeployPlane = SingleNodeDeployPlane`, the Tenant entity
    // registration is prepended automatically.
    registerEntityStore services config entityRegistrations

    // Phase 26 — Layer 3 deploy-plane substrate. Conditional on
    // `ServerConfig.DeployPlane`; `NoDeployPlane` (default) skips
    // registration entirely. `SingleNodeDeployPlane` wires
    // `IBuildOrchestrator` + `IDeployPipeline` + `ITenantFleet`
    // (extracted to `ComposeStores.registerDeployPlane`).
    // `IContainerScheduler` is consumer-supplied — the factories
    // raise at first-resolve when missing with a clear remediation
    // message.
    registerDeployPlane services config eventStore resolvedLogger

    // Phase 9d — usage metering substrate (extracted to
    // `ComposeRuntimeServices.registerUsageMetering`). Returns the
    // resolved `IUsageLog` so the FileManagementRuntime consumer
    // downstream picks up the same instance DI hands out. Also
    // registers `ServerConfig` singleton (Gap audit pass-2 #1) +
    // `IUserClaims` no-op default (Phase 62) + `ITeamQuotaPolicy`
    // (no-op or factory per config) + the flusher BackgroundService
    // gated by ProcessProfile.
    let usageLogInstance =
        registerUsageMetering services config resolvedBlobStorage eventStore resolvedLogger quotaPolicyOverride

    // Phase 9e / 9l / 12a — metrics + activity sink + locale resolver
    // (extracted to `ComposeRuntimeServices.registerMetricsAndObservability`).
    // Returns the resolved `IMetricsSink` so the outbound rate
    // limiter downstream picks up the same instance.
    let resolvedMetricsSink =
        registerMetricsAndObservability
            services
            config
            companionMetricsSinks
            moduleMetricRegistrations
            resolvedActivitySink
            resolvedLogger

    // Phase 114 — populate the deferred metrics cell now that the real
    // sink is resolved, so the audit log's write-failure counter
    // (`toolup.audit.write_failures_total`) flows to the configured sink
    // instead of the bootstrap no-op.
    metricsSinkCell.Value <- resolvedMetricsSink

    // Core SDK singleton registrations (extracted to
    // `ComposeRuntimeServices.registerCoreSdkSingletons`).
    // `INotificationAddressBook` defaults to
    // `BlobBackedNotificationAddressBook`; a deployment with a
    // directory-driven backend (LDAP / Okta) registers its own
    // post-`compose` and overrides it.
    registerCoreSdkSingletons
        services
        resolvedLogger
        dataTypes
        resolvedBlobStorage
        dataObjectStore
        dataCatalog
        eventStore
        auditLog
        auth
        secretStore
        sseConnectionManager
        resolvedNotificationChannel
        narrativeStore

    // Phase 21b — register the share-token store when enabled (extracted
    // to `ComposeNotifications.registerShareTokenStore`).
    registerShareTokenStore services shareTokenStoreInstance

    // Phase 9v — outbound `IRateLimiter` registration (extracted to
    // `ComposeRuntimeServices.registerOutboundRateLimiter`). Populates
    // `rateLimiterCell` so the webhook dispatcher's per-attempt `Wait`
    // calls (registered above with `rateLimiterLookup`) see the
    // configured limiter instead of the pass-through default.
    let rateLimiter =
        registerOutboundRateLimiter
            services
            config
            rateLimitDescriptors
            resolvedMetricsSink
            auditLog
            resolvedLogger
            rateLimiterCell

    // Phase 6h follow-up — SSE `IDevDiagnosticsContributor` (extracted
    // to `ComposeRuntimeServices.registerSseDevDiagnosticsContributor`).
    registerSseDevDiagnosticsContributor services config sseConnectionManager

    // Phase 1g — webhook DI registrations (extracted to
    // `ComposeJobs.registerWebhookSubsystem`).
    // Phase 16 — pass `config` so `ServerlessHost = ServerlessHost`
    // skips the dispatcher's `IHostedService` registration while
    // keeping the DI singletons that admin routes / sibling worker
    // silos need to resolve.
    registerWebhookSubsystem services config webhookSubsystem

    // Phase 6f — transactional dispatcher hosted service (extracted to
    // `ComposeNotifications.registerTransactionalDispatcher`).
    // Phase 16 — pass `config` so `ServerlessHost = ServerlessHost`
    // skips the dispatcher's `IHostedService` while keeping the
    // dispatcher singleton + sink registrations for sibling silos.
    registerTransactionalDispatcher services config transactionalDispatcher transactionalSinks

    // Phase 9g — audit replicator hosted service (extracted to
    // `ComposeAudit.registerAuditReplicatorHosting`).
    // Phase 16 — pass `config` so `ServerlessHost = ServerlessHost`
    // skips the replicator's `IHostedService` while keeping the
    // singleton + sink registrations for a sibling worker silo.
    registerAuditReplicatorHosting services config auditReplicatorSubsystem auditSinks

    // Phase 8 / 8a / 53 — conditional store registrations (extracted
    // to `ComposeStores.registerOptionalStores`).
    registerOptionalStores services config resultStore lineageStore conversationStore

    // Phase 9p — opt-in HealthStateTracker BackgroundService (extracted
    // to `ComposeRuntimeServices.registerHealthStateTracker`). Gated
    // on `config.HealthStateTracking` AND ProcessProfile matrix.
    registerHealthStateTracker services config auditLog resolvedLogger

    // Phase 9b — opt-in job scheduler (extracted to
    // `ComposeJobs.registerJobScheduler`). Populates `jobSchedulerCell`
    // so subsequent writes through the notify-wrapper forward to the
    // scheduler.
    let jobSchedulerInstance =
        registerJobScheduler
            services
            config
            resolvedBlobStorage
            eventStore
            resolvedNotificationChannel
            resolvedLogger
            resolvedActivitySink
            blobJobStoreInstance
            jobSchedulerCell

    // Phase 10 — opt-in data ingestion (extracted to
    // `ComposeJobs.registerDataIngestion`). Includes the Phase 10b
    // OAuth state store + cleanup service + the two OAuth-related
    // validators that are scoped to ingestion being active.
    registerDataIngestion services config resolvedBlobStorage secretStore dataObjectStore eventStore resolvedLogger

    // Phase 10h — opt-in OAuth token refresher substrate (extracted to
    // `ComposeJobs.registerOAuthRefresher`). Requires
    // `JobScheduler = InProcessJobScheduler`; misconfigured pair logs a
    // Warn and silently skips registration.
    registerOAuthRefresher
        services
        config
        jobSchedulerInstance
        secretStore
        auditLog
        resolvedMetricsSink
        rateLimiter
        resolvedLogger

    // Phase 9b.B — register and schedule module-/app-declared
    // `ScheduledJobDeclaration`s against the resolved scheduler. The
    // declarations were accumulated by `ServerApp.addModule` (from
    // `ServerModule.JobHandlers`) and `ServerApp.withScheduledJob`
    // (composition-root-level crons). Empty list (the default) is a
    // no-op — pre-9b.B behaviour where modules had to resolve
    // `IJobScheduler` from DI themselves after `Build`. A non-empty
    // list under `NoJobScheduler` emits one `Warn` and skips.
    registerScheduledJobDeclarations jobSchedulerInstance scheduledJobDeclarations resolvedLogger

    // Phase 9 / 9k / 9o — first-party + companion health checks +
    // opt-in smoke tests (extracted to
    // `ComposeHealthSmoke.registerHealthChecks` /
    // `ComposeHealthSmoke.registerSmokeTests`).
    registerHealthChecks services resolvedBlobStorage auth eventStore healthChecks

    registerSmokeTests
        services
        config
        resolvedBlobStorage
        resolvedNotificationChannel
        eventStore
        dataObjectStore
        auditLog
        jobSchedulerInstance
        smokeTests

    // First-party config validators + the one interleaved audit-chain
    // durability health check (extracted to
    // `ComposeConfigValidators.registerFirstPartyConfigValidators`).
    // Registration order is load-bearing — preserves the inline body
    // exactly. Companion-contributed `IConfigValidator` instances run
    // last so their preflight messages follow the first-party set.
    registerFirstPartyConfigValidators
        services
        config
        resolvedBlobStorage
        secretStore
        auth
        auditLog
        eventStore
        encryptionKeyResolver
        configValidators

    // Phase 9j — first-party CSP contributors (extracted to
    // `ComposeHealthSmoke.registerFirstPartyCspContributors`).
    registerFirstPartyCspContributors services config

    // Phase 9 + Phase 56 — rate-limit middleware substrates (extracted
    // to `ComposeRuntimeServices.registerRateLimitMiddleware`).
    registerRateLimitMiddleware services config

    // FileManagement runtime + Phase 1f extension (gap #10) eviction
    // TTL propagation (extracted to
    // `ComposeRuntimeServices.registerFileManagementRuntime`).
    registerFileManagementRuntime services config usageLogInstance resolvedLogger

    // Phase 4 / 5h — `ITeamStore` + `IPendingInviteStore` +
    // `IPermissionStore` registrations (extracted to
    // `ComposeTeamRuntime.registerTeamPermissionStores`). Returns the
    // optional `TeamStore` so downstream consumers (BootstrapTeam,
    // ComposeScopeResolver) share the same object.
    let teamStoreOpt =
        registerTeamPermissionStores
            services
            config
            resolvedBlobStorage
            resolvedNotificationChannel
            resolvedLogger
            pendingInviteStoreOverride

    // Phase 4b — Platform Admin store + one-shot bootstrap from
    // `TOOLUP_INITIAL_PLATFORM_ADMIN` (extracted to
    // `ComposeAuth.registerPlatformAdminStore`).
    let platformAdminStore =
        registerPlatformAdminStore services config resolvedBlobStorage auditLog resolvedLogger

    // Phase 5f / 3d / 66 Stream B.2 — post-`PlatformAdminStore` config
    // validators (extracted to
    // `ComposeTeamRuntime.registerPostBootstrapValidators`).
    registerPostBootstrapValidators
        services
        config
        authProvider
        platformAdminStore
        moduleSurfaceDefaults
        routeSurfaceOverrides
        (List.length shareTokenStoreDecorators)

    // Phase 5f — one-shot BootstrapTeam runtime (extracted to
    // `ComposeTeamRuntime.runBootstrapTeam`).
    runBootstrapTeam teamStoreOpt resolvedLogger auditLog

    // Phase 4b deferred follow-up — runtime PlatformKnowledgeBase
    // toggle (extracted to `ComposeAuth.registerPlatformRuntimeConfig`).
    let platformRuntimeConfig =
        registerPlatformRuntimeConfig services config resolvedBlobStorage resolvedLogger

    // Phase 4b dev-convenience contributor (extracted to
    // `ComposeAuth.registerPlatformAdminDevDiagnostics`).
    registerPlatformAdminDevDiagnostics services platformAdminStore platformRuntimeConfig

    // Phase 5a / 6b / 5c — `IConfigStore` + `IModuleQueryBus` +
    // `IFeatureFlagStore` / `FlagEvaluator` registrations (extracted
    // to `ComposeStores.registerConfigQueryAndFlagStores`).
    registerConfigQueryAndFlagStores
        services
        config
        configStoreInstance
        queryHandlers
        resolvedBlobStorage
        resolvedLogger
        resolvedActivitySink

    // Phase 54 — opt-in tenant-lifecycle hooks + snapshot (extracted to
    // `ComposeTenantLifecycle.registerTenantLifecycle`). Gated on
    // `config.TenantLifecycle = EnabledTenantLifecycle`; the default
    // `NoTenantLifecycle` is a no-op. Hooks resolve their substrate
    // lazily from the built provider at request time, so this runs
    // before companion registrations without ordering coupling.
    ComposeTenantLifecycle.registerTenantLifecycle services config

    // Companion DI registrations (AI, future distributed task
    // companions, etc.) run before scope resolution so they can
    // depend on SDK services being present.
    match extensions.ServiceConfig with
    | Some cfg -> cfg services |> ignore
    | None -> ()

    // Giraffe stock-helper DI defaults (extracted to
    // `ComposeBootstrap.registerGiraffeDefaults`) — `INegotiationConfig`
    // + `Json.ISerializer` (FableConverters-backed) + `Xml.ISerializer`,
    // so consumer handlers can use `RequestErrors.*` / `negotiate` /
    // `json` without a `Giraffe.MissingDependencyException`. Every
    // registration is `TryAdd`-semantics and this runs AFTER the
    // `extensions.ServiceConfig` hook, so a consumer-registered
    // serializer / negotiation config always wins.
    registerGiraffeDefaults services

    // Phase 15e tail — per-request scope/subject/surface DI wiring
    // (extracted to `ComposeScopeResolver.registerScopeResolution`).
    // Resolves `IStorageScopeResolver` / `ISubjectResolver` /
    // `SurfaceRequirementRegistry` / `IAnonymousSessionMigrator`
    // and registers them alongside the scoped `AccessContext` factory
    // + `IHttpContextAccessor` + session.
    registerScopeResolution
        services
        config
        teamStoreOpt
        resolvedNotificationChannel
        resolvedLogger
        moduleSurfaceDefaults
        routeSurfaceOverrides
        subjectMigratorOverride

    // Phase 9a — capture the `IServiceCollection` descriptors before
    // `Build()` (extracted to
    // `ComposeDevDiagnostics.buildDevDiagnosticsCapture`). The capture
    // is small (~few KB) and built unconditionally; the runtime gate at
    // `devDiagnosticsRoutes` is what keeps the report off the wire in
    // production.
    let devDiagnosticsCapture =
        buildDevDiagnosticsCapture
            config
            dataTypeRegistrations
            handlers
            extensions
            effectiveNotifications
            persistentEventStoreInstance
            blobJobStoreInstance
            services

    let devRoutes = routeHandlers.DevDiagnosticsRoutes devDiagnosticsCapture

    // Phase 9m — companion config preflight (extracted to
    // `ComposeBootstrap.runConfigPreflight`). Runs every registered
    // `IConfigValidator` in parallel and registers the outcome
    // `PreflightSnapshot` singleton. Must run after every companion has
    // had a chance to call `services.AddSingleton<IConfigValidator>(...)`
    // and before `HealthCheckAggregator.register`.
    runConfigPreflight services config resolvedLogger

    // Phase 9j — CSP aggregate registration (extracted to
    // `ComposeBootstrap.registerCspAggregate`). Must run after the
    // `extensions.ServiceConfig` hook (companions registered) and
    // before `builder.Build()` seals the collection.
    registerCspAggregate services config

    // Phase 9k — flush every registered `IHealthCheck` (first-party +
    // companion-contributed) into BCL's `AddHealthChecks` pipeline.
    // Must run after every companion has had a chance to call
    // `services.AddSingleton<IHealthCheck>(...)` and before
    // `builder.Build()` (which makes the service collection
    // immutable).
    HealthCheckAggregator.register services

    // Phase 16a — `Build()` branch by `ProcessProfile` + post-Build
    // ConfigDrift + configurePipeline + IServerHost return (extracted
    // to `ComposeBootstrap.buildAndRunHost`). The shared `ILogger`
    // resolution assertion + `ConfigDriftDetector` invocation run on
    // both branches; the Web path drives the middleware pipeline via
    // `configurePipeline` and returns `createWebHost`; the worker
    // path skips the pipeline and returns `createWorkerHost`.
    buildAndRunHost
        config
        webBuilder
        workerBuilder
        resolvedBlobStorage
        auditLog
        resolvedLogger
        resolvedMetricsSink
        extensions
        routeHandlers.Router
        devRoutes