module ToolUp.Platform.ComposeRuntimeServices

open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FileManagement
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Tracing
open ToolUp.Platform.Usage

// ─── compose phase: runtime services ─────────────────────────────────
//
// Usage metering, metrics sink, activity sink, locale resolver,
// outbound rate limiter, health-state tracker, inbound rate-limit
// substrate, FileManagement runtime. Extracted from `compose` for the
// per-concern subdivision (Phase 15e follow-up tail). Each helper
// takes the substrate values its inline definition captured and
// returns either `unit` or the constructed substrate value when
// downstream code needs it. Zero behaviour change.

/// Register the ASP.NET Core caches + response compression + Phase 9j
/// DataProtection key ring. The cache pair (memory + distributed-
/// memory) is what `AddSession()` later depends on (Saturn's
/// `memory_cache` registered both shapes; raw ASP.NET Core needs the
/// distributed-cache registration explicitly). The DataProtection ring
/// is persisted through the resolved `IBlobStorage` with a stable
/// application name — the stateless CSRF token (`CsrfMiddleware`) is a
/// DataProtection seal, so persisting + sharing the ring is what
/// makes the hardened CSRF posture correct multi-instance (cloud blob
/// = shared across replicas) and restart-safe (local file blob) with
/// no session store / sticky LB. Harmless under `NoSecurityHardening`
/// (nothing seals anything). The resolved logger rides into
/// `BlobXmlRepository` (Phase 329) so a key-ring read failure emits a
/// `Warn` instead of being silently indistinguishable from an empty
/// first-boot ring.
let registerCachingAndDataProtection
    (services: IServiceCollection)
    (resolvedBlobStorage: IBlobStorage)
    (resolvedLogger: ILogger)
    : unit =
    services.AddMemoryCache() |> ignore
    services.AddDistributedMemoryCache() |> ignore
    services.AddResponseCompression() |> ignore

    services.AddDataProtection().SetApplicationName "ToolUp.Platform" |> ignore

    services.Configure<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>
        (fun (o: Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions) ->
            o.XmlRepository <-
                (BlobXmlRepository(resolvedBlobStorage, resolvedLogger)
                :> Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository))
    |> ignore

/// Phase 6h follow-up — Workstream B. `IDevDiagnosticsContributor`
/// for SSE broadcast / registered-scope visibility. Gated on
/// `EnableDevEndpoints` so production deployments don't pay the
/// (negligible) ring-buffer write cost on every broadcast for a panel
/// they never read. Registering against the same DI shape as
/// `IHealthCheck` / `IConfigValidator` aggregators.
let registerSseDevDiagnosticsContributor
    (services: IServiceCollection)
    (config: ServerConfig)
    (sseConnectionManager: SSEConnectionManager)
    : unit =
    if config.EnableDevEndpoints then
        let sseContrib =
            SseTraceContributor.SseTraceContributor sseConnectionManager :> IDevDiagnosticsContributor

        services.AddSingleton<IDevDiagnosticsContributor>(sseContrib) |> ignore

/// Register the core SDK singletons that every consumer resolves
/// from DI: logger, dataTypes, blob storage, data-object store, data
/// catalog, event store, audit log, auth provider, secret store, SSE
/// connection manager, notification channel, narrative store, and the
/// (Phase 6f) `INotificationAddressBook`. The chain is kept
/// fluent-style so the inline body remains a single side-effect on
/// `services`. `INotificationAddressBook` defaults to
/// `BlobBackedNotificationAddressBook` over the resolved
/// `IBlobStorage`; deployments with a directory-driven backend
/// (LDAP / Okta) register their own post-`compose` and override this.
let registerCoreSdkSingletons
    (services: IServiceCollection)
    (resolvedLogger: ILogger)
    (dataTypes: DataType list)
    (resolvedBlobStorage: IBlobStorage)
    (dataObjectStore: IDataObjectStore)
    (dataCatalog: IDataCatalog)
    (eventStore: IEventStore)
    (auditLog: IAuditLog)
    (auth: IAuthProvider)
    (secretStore: Secrets.ISecretStore)
    (sseConnectionManager: SSEConnectionManager)
    (resolvedNotificationChannel: INotificationChannel)
    (narrativeStore: INarrativeStore)
    : unit =
    services
        .AddSingleton<ILogger>(resolvedLogger)
        .AddSingleton<DataType list>(dataTypes)
        .AddSingleton<IBlobStorage>(resolvedBlobStorage)
        .AddSingleton<IDataObjectStore>(dataObjectStore)
        .AddSingleton<IDataCatalog>(dataCatalog)
        .AddSingleton<IEventStore>(eventStore)
        .AddSingleton<IAuditLog>(auditLog)
        .AddSingleton<IAuthProvider>(auth)
        .AddSingleton<Secrets.ISecretStore>(secretStore)
        .AddSingleton<SSEConnectionManager>(sseConnectionManager)
        .AddSingleton<INotificationChannel>(resolvedNotificationChannel)
        .AddSingleton<INarrativeStore>(narrativeStore)
        .AddSingleton<INotificationAddressBook>(
            NotificationAddressBook.BlobBackedNotificationAddressBook(resolvedBlobStorage, Some resolvedLogger)
        )
    |> ignore

/// Phase 1f — CORS service registration. `None` (default) skips both
/// the service and the middleware so deployments without
/// cross-origin needs carry zero CORS overhead. The supplied
/// `CorsConfig` becomes the default policy; consumers wanting
/// per-route policies or dynamic-origin validation use
/// `ServerApp.withPreMiddleware` instead.
///
/// Warns + falls back to non-credentialed mode when
/// `AllowCredentials = true` is combined with a wildcard origin
/// (incompatible per the CORS spec).
let registerCors (services: IServiceCollection) (config: ServerConfig) (resolvedLogger: ILogger) : unit =
    match config.Cors with
    | None -> ()
    | Some cors ->
        let credentialsConflict = cors.AllowCredentials && cors.Origins |> List.contains "*"

        if credentialsConflict then
            resolvedLogger.Warn(
                "[CORS] AllowCredentials=true cannot combine with wildcard origins; falling back to non-credentialed mode."
            )

        services.AddCors(fun options ->
            options.AddDefaultPolicy(fun (policy: Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder) ->
                let isWildcardList xs =
                    xs |> List.exists (fun (s: string) -> s = "*")

                if isWildcardList cors.Origins then
                    policy.AllowAnyOrigin() |> ignore
                else
                    policy.WithOrigins(List.toArray cors.Origins) |> ignore

                if isWildcardList cors.Methods then
                    policy.AllowAnyMethod() |> ignore
                else
                    policy.WithMethods(List.toArray cors.Methods) |> ignore

                if isWildcardList cors.Headers then
                    policy.AllowAnyHeader() |> ignore
                else
                    policy.WithHeaders(List.toArray cors.Headers) |> ignore

                if cors.AllowCredentials && not credentialsConflict then
                    policy.AllowCredentials() |> ignore))
        |> ignore

/// Phase 9d — usage metering substrate. `NoUsageMetering` (default)
/// resolves `IUsageLog` / `ITeamQuotaPolicy` to no-ops and skips the
/// flusher BackgroundService + the `IUsageQueryApi` route.
/// `EnabledUsageMetering` registers the blob-backed defaults; the
/// policy is registered as a factory because
/// `BlobBackedTeamQuotaPolicy` needs both `IUsageLog` and
/// `IConfigStore` (the latter is registered later in `compose`, so
/// factory-resolution defers to post-Build).
///
/// Also registers the live `ServerConfig` singleton (Gap audit pass-2
/// #1 — handlers like `WebhookApiHandler` need it for runtime
/// `WebhookUrlAllowedHosts` checks) and the Phase 62 `IUserClaims`
/// no-op default (deployments wiring a real provider-specific claim
/// store replace it via the `ComposeExtensions.ServiceConfig`
/// callback).
///
/// Returns the resolved `IUsageLog` instance so the
/// `FileManagementRuntime` consumer downstream picks up the same
/// instance DI hands out.
let registerUsageMetering
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (eventStore: IEventStore)
    (resolvedLogger: ILogger)
    (quotaPolicyOverride: ITeamQuotaPolicy option)
    : IUsageLog =

    let (usageLogInstance: IUsageLog), (usageBatchFlusher: UsageLog.UsageBatchFlusher option) =
        match config.UsageMetering with
        | NoUsageMetering -> NoOpUsageLog() :> IUsageLog, None
        | EnabledUsageMetering ->
            let flusher =
                new UsageLog.UsageBatchFlusher(
                    resolvedBlobStorage,
                    BatchFlushPolicy.defaults,
                    resolvedLogger,
                    Some eventStore
                )

            let log = UsageLog.BlobUsageLog(resolvedBlobStorage, flusher) :> IUsageLog
            log, Some flusher

    services.AddSingleton<IUsageLog>(usageLogInstance) |> ignore

    // Gap audit pass-2 #1 — register the live ServerConfig so handlers
    // can resolve it for runtime checks.
    services.AddSingleton<ServerConfig>(config) |> ignore

    // Phase 62 — IUserClaims substrate. Default `NoOpUserClaims`
    // returns `NotPremium` for every user and short-circuits grant /
    // revoke without writing through to any provider.
    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
        IUserClaims
     >(
        services,
        NoOpUserClaims() :> IUserClaims
    )

    match quotaPolicyOverride with
    | Some explicit -> services.AddSingleton<ITeamQuotaPolicy>(explicit) |> ignore
    | None ->
        match config.UsageMetering with
        | NoUsageMetering ->
            services.AddSingleton<ITeamQuotaPolicy>(NoOpTeamQuotaPolicy() :> ITeamQuotaPolicy)
            |> ignore
        | EnabledUsageMetering ->
            services.AddSingleton<ITeamQuotaPolicy>(
                System.Func<System.IServiceProvider, ITeamQuotaPolicy>(fun sp ->
                    let cs = sp.GetService(typeof<IConfigStore>) :?> IConfigStore
                    let ul = sp.GetService(typeof<IUsageLog>) :?> IUsageLog
                    TeamQuotaPolicy.BlobBackedTeamQuotaPolicy(cs, ul) :> ITeamQuotaPolicy)
            )
            |> ignore

    match usageBatchFlusher with
    | None -> ()
    | Some flusher ->
        services.AddSingleton<UsageLog.UsageBatchFlusher>(flusher) |> ignore

        // Phase 16 + 16a — gate flusher BackgroundService on the
        // centralised matrix. ServerlessHost / WebOnly / DispatcherOnly
        // skip; AllInOne / WorkerOnly register. Usage records still emit
        // into the in-process queue (the `IUsageLog` singleton stays
        // registered); a sibling worker silo drains the queue.
        if ProcessProfileGate.shouldRegisterBackgroundService config UsageBatchFlusherSubsystem then
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                flusher :> Microsoft.Extensions.Hosting.IHostedService
            )
            |> ignore

    usageLogInstance

/// Phase 9e — `IMetricsSink`. `NoMetricsEndpoint` (default) registers
/// `NoOpMetricsSink` so emission sites in middleware / scheduler / SSE
/// manager / FileManagement never get `null` when they resolve the
/// service. `EnabledMetricsEndpoint` registers `PrometheusMetricsSink`
/// pre-loaded with the SDK standard metrics. Companion sinks
/// (OpenTelemetry exporter etc.) self-register an additional
/// `IMetricsSink` alongside this default; multiple sinks compose
/// through fan-out — see `FanOutMetricsSink`.
///
/// Also registers `IActivitySink` (Phase 9l — `NoOpActivitySink`
/// default) and the default `ILocaleResolver` (Phase 12a — bespoke
/// resolvers register their own post-`compose`; `TryAddSingleton`
/// cedes to the override when one is supplied).
///
/// Returns the resolved `IMetricsSink` instance so the outbound
/// `IRateLimiter` constructor downstream picks up the same instance.
let registerMetricsAndObservability
    (services: IServiceCollection)
    (config: ServerConfig)
    (companionMetricsSinks: Metrics.IMetricsSink list)
    (moduleMetricRegistrations: Metrics.MetricRegistration list)
    (resolvedActivitySink: IActivitySink)
    (resolvedLogger: ILogger)
    : Metrics.IMetricsSink =

    let (resolvedMetricsSink: Metrics.IMetricsSink), (prometheusSink: Metrics.PrometheusMetricsSink option) =
        match config.MetricsEndpoint with
        | NoMetricsEndpoint -> Metrics.NoOpMetricsSink() :> Metrics.IMetricsSink, None
        | EnabledMetricsEndpoint ->
            let promSink =
                Metrics.PrometheusMetricsSink(
                    config.MetricsSink,
                    Metrics.StandardMetrics.registrations @ moduleMetricRegistrations,
                    resolvedLogger
                )

            // Companion sinks (e.g. OtelMetricsSink) fold into a
            // FanOutMetricsSink alongside the in-process default so a
            // single emission dispatches to every sink. The Prometheus
            // sink is always at the head of the list so /metrics keeps
            // returning current values even if a companion sink fails.
            let consumerSink: Metrics.IMetricsSink =
                if List.isEmpty companionMetricsSinks then
                    promSink :> Metrics.IMetricsSink
                else
                    let sinks = (promSink :> Metrics.IMetricsSink) :: companionMetricsSinks
                    Metrics.FanOutMetricsSink(sinks, resolvedLogger) :> Metrics.IMetricsSink

            consumerSink, Some promSink

    services.AddSingleton<Metrics.IMetricsSink>(resolvedMetricsSink) |> ignore

    // Phase 9l — register IActivitySink. NoOpActivitySink is the
    // default (zero overhead); deployments wire OtelActivitySink (or
    // any custom companion) via `ServerApp.withActivitySink`.
    services.AddSingleton<IActivitySink>(resolvedActivitySink) |> ignore

    // Phase 12a — register the default `ILocaleResolver`. Deployments
    // that want bespoke locale resolution register their own
    // implementation post-`compose`; `TryAddSingleton` cedes to the
    // override when one was supplied.
    Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<
        ILocaleResolver
     >(
        services,
        LocaleResolver.create ()
    )

    match prometheusSink with
    | Some sink -> services.AddSingleton<Metrics.PrometheusMetricsSink>(sink) |> ignore
    | None -> ()

    resolvedMetricsSink

/// Phase 9v — outbound `IRateLimiter` registration. Mirrors the
/// `NoOpUsageLog` / `NoOpAuditLog` / `NoOpMetricsSink` pattern so
/// emission sites resolve the service unconditionally and the call
/// elides at zero cost when the deployment hasn't opted in.
/// `NoRateLimiter` (the default) registers `NoOpRateLimiter`;
/// `EnabledRateLimiter` registers the SDK-shipped sliding-window
/// default fed with the compose-time `rateLimitDescriptors`,
/// `auditLog`, `resolvedMetricsSink`, and the configured
/// `SlowRateLimitThreshold`.
///
/// Populates `rateLimiterCell` so the webhook dispatcher's per-attempt
/// `Wait` calls (registered upstream with the cell's lookup) see the
/// configured limiter instead of the pass-through default.
///
/// Returns the resolved `IRateLimiter` so downstream registrations
/// (e.g. the OAuth refresher in `ComposeJobs`) can capture it.
let registerOutboundRateLimiter
    (services: IServiceCollection)
    (config: ServerConfig)
    (rateLimitDescriptors: RateLimitDescriptor list)
    (resolvedMetricsSink: Metrics.IMetricsSink)
    (auditLog: IAuditLog)
    (resolvedLogger: ILogger)
    (rateLimiterCell: IRateLimiter ref)
    : IRateLimiter =

    let rateLimiter: IRateLimiter =
        match config.RateLimiter with
        | NoRateLimiter -> NoOpRateLimiter() :> IRateLimiter
        | EnabledRateLimiter ->
            InProcessRateLimiter(
                rateLimitDescriptors,
                resolvedMetricsSink,
                auditLog,
                config.SlowRateLimitThreshold,
                resolvedLogger
            )
            :> IRateLimiter

    rateLimiterCell.Value <- rateLimiter

    services.AddSingleton<IRateLimiter>(rateLimiter) |> ignore

    rateLimiter

/// Phase 9p — opt-in `HealthStateTracker` BackgroundService. When
/// `HealthStateTracking = true`, polls every registered IHealthCheck
/// once per minute (wall-clock-aligned, mirroring JobScheduler) and
/// emits a `HealthStateChanged` audit event when a probe's stable
/// state changes (3 consecutive observations of a new status).
/// Default `false` (GP 13) — deployments that don't enable it
/// register no service and pay no tick cost.
///
/// Registered as a factory so the captured `IServiceProvider` can
/// resolve `IHealthCheck`s lazily after `Build()` (companion probes
/// self-register up to end-of-compose). The factory closes over
/// `auditLog` and `resolvedLogger` (both local instances above) so
/// the tracker uses the same audit-log impl wired through DI.
///
/// Phase 16 + 16a — gate the tracker BackgroundService on the
/// centralised matrix. ServerlessHost / WebOnly / DispatcherOnly skip;
/// AllInOne / WorkerOnly register. The `/health` + `/ready` HTTP
/// endpoints still aggregate registered `IHealthCheck` instances
/// on-demand; only the periodic-poll + audit-emit BackgroundService
/// is gated.
let registerHealthStateTracker
    (services: IServiceCollection)
    (config: ServerConfig)
    (auditLog: IAuditLog)
    (resolvedLogger: ILogger)
    : unit =
    if
        config.HealthStateTracking
        && ProcessProfileGate.shouldRegisterBackgroundService config HealthStateTrackerSubsystem
    then
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            System.Func<System.IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                new HealthStateTracker.HealthStateTrackerService(sp, auditLog, resolvedLogger)
                :> Microsoft.Extensions.Hosting.IHostedService)
        )
        |> ignore

/// Phase 178 — opt-in alert-rule / threshold engine BackgroundService.
/// Hosted only when `config.AlertRules` is non-empty (GP 13 — an empty
/// set registers no service and pays no tick cost) AND the ProcessProfile
/// matrix admits it (`AllInOne` / `WorkerOnly` run it; `WebOnly` /
/// `DispatcherOnly` / `ServerlessHost` skip). The `notificationChannel`
/// passed here is the compose-resolved (decorated) channel, so `ViaSink`
/// deliveries route through `DispatchingNotificationChannel` to the
/// registered `INotificationSink`. Registered as a factory so the
/// captured `IServiceProvider` resolves the metric-read tap
/// (`PrometheusMetricsSink`) + `IHealthCheck` set lazily per tick.
let registerAlertRuleEngine
    (services: IServiceCollection)
    (config: ServerConfig)
    (notificationChannel: INotificationChannel)
    (resolvedLogger: ILogger)
    : unit =
    if
        not (List.isEmpty config.AlertRules)
        && ProcessProfileGate.shouldRegisterBackgroundService config AlertRuleEngineSubsystem
    then
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
            System.Func<System.IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                new AlertRuleEngine.AlertRuleEngineService(sp, notificationChannel, config.AlertRules, resolvedLogger)
                :> Microsoft.Extensions.Hosting.IHostedService)
        )
        |> ignore

/// Phase 9 + Phase 56 — rate-limit middleware substrates. Phase 9 is a
/// single fixed-window team-keyed policy (opt-in via
/// `ServerConfig.RateLimit`); Phase 56 is a portable per-route-policy
/// substrate with pluggable storage (opt-in via
/// `ServerConfig.RateLimitStore`). The two can coexist — Phase 56
/// middleware runs FIRST so a route-specific cap short-circuits before
/// the Phase 9 limiter consumes a token.
let registerRateLimitMiddleware (services: IServiceCollection) (config: ServerConfig) : unit =
    // Phase 9 rate limiting. Opt-in via `ServerConfig.RateLimit`.
    // `RateLimitConfig.none` (default) registers no limiter and the
    // middleware won't run, preserving backward-compatibility for
    // deployments that don't want a per-scope cap (GP 11). Phase 66
    // Stream C.3: any default or per-shape policy enables it.
    if RateLimitConfig.isEnabled config.RateLimit then
        services.AddRateLimiter(fun options -> RateLimiting.configure config.RateLimit options)
        |> ignore

    // Phase 56 — inbound rate-limit substrate. Distinct from the
    // Phase 9 fixed-window team-keyed `RateLimit` above:
    //   - `RateLimit` (Phase 9) is a single fixed-window policy on
    //     every authenticated request, partitioned by team/user/IP.
    //   - `RateLimitStore` + `RateLimits` (Phase 56) is a portable
    //     per-route-policy substrate supporting per-IP / per-user /
    //     composite keys, multiple windows, and pluggable storage
    //     (in-memory / Redis / Azure Table Storage / Cosmos).
    //
    // Registration is opt-in via `RateLimitStore`. `NoRateLimitStore`
    // (default) strips the entire substrate — no `IRateLimitStore` in
    // DI, no middleware mount. `InMemoryRateLimitStore` activates the
    // single-instance default. `ExternalRateLimitStore` is the
    // companion-driven branch — the operator's
    // `ComposeExtensions.ServiceConfig` (or a sub-companion's
    // composition extension) registers the external impl as the
    // `IRateLimitStore` singleton.
    match config.RateLimitStore with
    | NoRateLimitStore -> ()
    | ToolUp.Platform.RateLimitStoreMode.InMemoryRateLimitStore ->
        services.AddSingleton<IRateLimitStore>(InMemoryRateLimitStore.create ())
        |> ignore
    | ExternalRateLimitStore ->
        // Companion package wires its own
        // `services.AddSingleton<IRateLimitStore>(...)` via
        // `ComposeExtensions.ServiceConfig`. Compose doesn't register
        // anything here — the companion's `IConfigValidator` refuses
        // startup if `RateLimitStore = ExternalRateLimitStore` but no
        // `IRateLimitStore` is in DI by the time `app.Build()` runs.
        ()

/// Build the immutable `FileManagementRuntime` consumed by every
/// `SessionFileStore` instance. Drains companion-registered post-save
/// hooks (RAG vectorisation in particular), folds in the
/// compose-resolved logger / quota / usage-log, and registers the
/// record as a DI singleton. Replaces the prior pattern of four
/// module-level mutables that `compose` set via `configure*` setters.
///
/// Phase 1f extension (gap #10) — also propagates the
/// deployment-configured ephemeral-store eviction TTL to
/// FileManagement's mutable global. Runtime eviction picks up the new
/// value on the next 10-minute timer tick.
let registerFileManagementRuntime
    (services: IServiceCollection)
    (config: ServerConfig)
    (usageLogInstance: IUsageLog)
    (resolvedLogger: ILogger)
    : unit =

    // Phase 9 storage quota resolver. Reads the deployment-wide
    // default from `ServerConfig.DefaultTeamStorageQuotaBytes`. A
    // future per-team override can plug into this same hook by calling
    // `IConfigStore` from the resolver — the signature is
    // `scopeId -> Async<int64 option>` so no public change.
    let quotaResolver: (string -> Async<int64 option>) option =
        match config.DefaultTeamStorageQuotaBytes with
        | Some _ as limit -> Some(fun _scopeId -> async { return limit })
        | None -> None

    // Per-file upload ceiling: `TOOLUP_MAX_FILE_BYTES` (positive int64) overrides
    // the generous default; `0` / negative disables the ceiling; unset/garbage
    // falls back to the default. Operators raising the limit for large uploads no
    // longer need a code change + repack.
    let maxFileBytes: int64 option =
        match System.Environment.GetEnvironmentVariable "TOOLUP_MAX_FILE_BYTES" with
        | null
        | "" -> FileManagement.FileManagementRuntime.empty.MaxFileBytes
        | raw ->
            match System.Int64.TryParse raw with
            | true, n when n > 0L -> Some n
            | true, _ -> None // 0 / negative disables the ceiling
            | false, _ -> FileManagement.FileManagementRuntime.empty.MaxFileBytes

    let fileManagementRuntime: FileManagement.FileManagementRuntime = {
        PostSaveHooks = FileManagement.drainPendingPostSaveHooks ()
        PostSaveHooksLogger = Some resolvedLogger
        QuotaResolver = quotaResolver
        UsageLog = Some usageLogInstance
        MaxFileBytes = maxFileBytes
    }

    services.AddSingleton<FileManagement.FileManagementRuntime>(fileManagementRuntime)
    |> ignore

    FileManagement.configureEvictionMinutes config.EphemeralStoreEvictionMinutes