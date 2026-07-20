module ToolUp.Platform.ComposeJobs

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.Tracing
open ToolUp.Platform.WebhookDispatcher

// ─── compose phase: jobs, webhooks, data ingestion, OAuth refresher ──
//
// The "jobs" cluster in compose covers the substrate values whose
// natural shape is "background work scheduled against IJobScheduler":
//
//   - Webhook subsystem (delivery is a job-shaped retry loop). Builds
//     `IWebhookRegistry` / `IWebhookDeliveryLog` / `IWebhookDispatcher`
//     and the event-store decorator (`HookedEventStore`) that flows
//     emitted events into webhook fan-out.
//   - Event-store decorator chain. Wraps the post-webhook event store
//     with `JobNotifyEventStore` when a scheduler is configured so
//     writes trigger `OnEvent` job dispatch.
//   - Job scheduler. Registers `IJobStore` + `IJobScheduler` + the
//     in-process scheduler hosted service when
//     `JobScheduler = InProcessJobScheduler`.
//   - Data ingestion. Registers `IDataSourceConfigStore` +
//     `IDataIngestor` + OAuth state-store substrate (Phase 10b) +
//     OAuth flow validator + OAuth state-store instance validator.
//   - OAuth token refresher. Registers `IOAuthTokenRefresher` +
//     `OAuthRefreshJobHandler` with the scheduler + a hosted-service
//     `Recover` call at startup (Phase 10h).
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

/// Build the webhook subsystem. Returns `None` when
/// `Webhooks = NoWebhooks` — no dispatcher BackgroundService, no
/// `HookedEventStore` decorator, no `IWebhookRegistry` /
/// `IWebhookDeliveryLog` / `IWebhookDispatcher` DI services. The
/// lightweight default carries zero webhook overhead.
///
/// `rateLimiterLookup` is a thunk that reads the eventual
/// `IRateLimiter` from a mutable cell populated later in `compose`
/// (chicken-and-egg break: the dispatcher constructs before the
/// limiter, but only invokes `Wait` at request time, by which point the
/// cell holds the configured limiter).
let buildWebhookSubsystem
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (effectiveInnerEventStore: IEventStore)
    (resolvedLogger: ILogger)
    (resolvedActivitySink: IActivitySink)
    (secretStore: Secrets.ISecretStore)
    (rateLimiterLookup: unit -> IRateLimiter)
    : (IWebhookRegistry * IWebhookDeliveryLog * WebhookDispatcher.WebhookDispatcherService * IEventStore) option =
    match config.Webhooks with
    | NoWebhooks -> None
    | EnabledWebhooks ->
        let registry = WebhookRegistry.createRegistry resolvedBlobStorage
        let deliveryLog = WebhookRegistry.createDeliveryLog resolvedBlobStorage
        let httpClient = new System.Net.Http.HttpClient()

        // Phase 6d.A — opt-in one-shot secret-at-rest migration. Runs
        // synchronously at compose (before preflight) so a deployment
        // upgrading from a pre-6d.A version can move plaintext secrets
        // into ISecretStore and pass the WebhookSecretAtRestValidator on
        // the same boot. Idempotent — a no-op once every blob is
        // migrated (GP 11/13: default off, byte-for-byte unchanged unless
        // the flag is set).
        if config.MigrateWebhookSecretsAtRest then
            WebhookSecretMigration.migrate resolvedBlobStorage secretStore resolvedLogger
            |> Async.RunSynchronously
            |> ignore

        // Same SSRF allowlist the registration-time guard uses
        // (WebhookApiHandler) — threaded into the dispatcher so delivery-time
        // re-validation honours operator-allowlisted internal hosts too.
        let urlPolicy: WebhookUrlValidator.WebhookUrlPolicy = {
            AllowedHosts = config.WebhookUrlAllowedHosts
        }

        let dispatcher =
            WebhookDispatcher.create
                registry
                deliveryLog
                effectiveInnerEventStore
                httpClient
                WebhookRetryPolicy.defaults
                resolvedLogger
                resolvedActivitySink
                secretStore
                rateLimiterLookup
                urlPolicy

        // Wrap the inner event store so every event written through the
        // DI-registered `IEventStore` triggers webhook fan-out. The
        // dispatcher itself holds `effectiveInnerEventStore` (above) so
        // its own audit writes don't re-enter the webhook hook but DO
        // flow through the audit-replication decorator if registered.
        let hookedStore: IEventStore =
            HookedEventStore.HookedEventStore(effectiveInnerEventStore, dispatcher :> IWebhookDispatcher) :> _

        Some(registry, deliveryLog, dispatcher, hookedStore)

/// Apply the post-webhook decorator chain to land on the final
/// `IEventStore` registered in DI. Decorator order:
///   - With webhooks + job scheduler: `JobNotify -> WebhookHooked -> Inner`
///   - With webhooks only:            `WebhookHooked -> Inner`
///   - With job scheduler only:       `JobNotify -> Inner`
///   - Lightweight default:           `Inner` (no decorators)
let applyEventStoreDecorators
    (config: ServerConfig)
    (effectiveInnerEventStore: IEventStore)
    (webhookSubsystem:
        (IWebhookRegistry * IWebhookDeliveryLog * WebhookDispatcher.WebhookDispatcherService * IEventStore) option)
    (jobSchedulerLookup: unit -> IJobScheduler option)
    (jobTriggerWatermark: JobTriggerWatermark.JobTriggerWatermark option)
    : IEventStore =
    let eventStoreAfterWebhooks: IEventStore =
        match webhookSubsystem with
        | None -> effectiveInnerEventStore
        | Some(_, _, _, hooked) -> hooked

    match config.JobScheduler with
    | NoJobScheduler -> eventStoreAfterWebhooks
    | InProcessJobScheduler ->
        // Phase 598 — the same watermark instance the scheduler scans
        // against; the notify-wrapper advances it after each live
        // dispatch. `None` when `EventTriggerCatchUp` is off.
        JobNotifyEventStore.JobNotifyEventStore(
            eventStoreAfterWebhooks,
            jobSchedulerLookup,
            ?watermark = jobTriggerWatermark
        )
        :> _

/// Phase 1g — webhook DI registrations are conditional on
/// `ServerConfig.Webhooks`. `NoWebhooks` skips all four (registry,
/// delivery log, dispatcher interface, dispatcher hosted-service) so the
/// lightweight default registers zero webhook services.
///
/// Phase 16 — when `ServerConfig.ServerlessHost = ServerlessHost`, the
/// dispatcher's `IHostedService` is NOT registered. The DI singletons
/// (registry, delivery log, dispatcher object) still register so a
/// sibling worker silo can resolve them through the same composition
/// or admin routes can list subscriptions. Outbound webhook delivery
/// itself is performed by the sibling worker silo (`ProcessProfile =
/// WorkerOnly`) or skipped entirely for serverless deployments that
/// don't need outbound webhooks.
let registerWebhookSubsystem
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (webhookSubsystem:
        (IWebhookRegistry * IWebhookDeliveryLog * WebhookDispatcher.WebhookDispatcherService * IEventStore) option)
    : unit =
    match webhookSubsystem with
    | None -> ()
    | Some(registry, deliveryLog, dispatcher, _hookedStore) ->
        services
            .AddSingleton<IWebhookRegistry>(registry)
            .AddSingleton<IWebhookDeliveryLog>(deliveryLog)
            .AddSingleton<IWebhookDispatcher>(dispatcher :> IWebhookDispatcher)
        |> ignore

        // Phase 6d.A — refuse startup if any persisted subscription still
        // carries a plaintext signing secret inline (a half-migrated or
        // tampered deployment). Security-class, scoped to webhooks being
        // active.
        services.AddSingleton<ConfigValidation.IConfigValidator>(
            WebhookSecretValidator.WebhookSecretAtRestValidator(resolvedBlobStorage)
            :> ConfigValidation.IConfigValidator
        )
        |> ignore

        // Phase 16 + 16a — gate dispatcher BackgroundService on the
        // centralised process-profile matrix. ServerlessHost / WebOnly
        // skip; AllInOne / WorkerOnly / DispatcherOnly register.
        if ProcessProfileGate.shouldRegisterBackgroundService config WebhookDispatcherSubsystem then
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                dispatcher :> Microsoft.Extensions.Hosting.IHostedService
            )
            |> ignore

/// Phase 9b — opt-in job scheduler. `NoJobScheduler` (default) skips
/// registration entirely — no `IJobStore`, no `IJobScheduler`, no
/// scheduler tick, no `_platform/jobs/` blob layout. Apps that resolve
/// `IJobScheduler` from DI then receive `null` and must handle absence
/// explicitly.
///
/// `blobJobStoreInstance` is populated here in the `InProcessJobScheduler`
/// branch so the MaintenanceApi thunks (captured in `buildRouteHandlers`)
/// can read it. `jobSchedulerCell` is populated so subsequent writes
/// through the notify-wrapper see the scheduler and dispatch `OnEvent`
/// triggers.
let registerJobScheduler
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (eventStore: IEventStore)
    (resolvedNotificationChannel: INotificationChannel)
    (resolvedLogger: ILogger)
    (resolvedActivitySink: IActivitySink)
    (blobJobStoreInstance: JobStore.BlobJobStore option ref)
    (jobSchedulerCell: IJobScheduler option ref)
    (jobTriggerWatermark: JobTriggerWatermark.JobTriggerWatermark option)
    : IJobScheduler option =
    match config.JobScheduler with
    | NoJobScheduler -> None
    | InProcessJobScheduler ->
        let blobJobStore = JobStore.BlobJobStore(resolvedBlobStorage, eventStore)
        blobJobStoreInstance.Value <- Some blobJobStore
        let jobStore = blobJobStore :> IJobStore

        let scheduler =
            // Phase 598 — hand the scheduler the shared trigger
            // watermark when the deployment opted into catch-up;
            // `JobNotifyEventStore` holds the same instance.
            match jobTriggerWatermark with
            | Some watermark ->
                JobScheduler.createWithCatchUp
                    jobStore
                    eventStore
                    resolvedNotificationChannel
                    config
                    resolvedLogger
                    resolvedActivitySink
                    watermark
            | None ->
                JobScheduler.create
                    jobStore
                    eventStore
                    resolvedNotificationChannel
                    config
                    resolvedLogger
                    resolvedActivitySink

        jobSchedulerCell.Value <- Some(scheduler :> IJobScheduler)

        services.AddSingleton<IJobStore>(jobStore) |> ignore
        services.AddSingleton<IJobScheduler>(scheduler :> IJobScheduler) |> ignore
        services.AddSingleton<JobScheduler.InProcessJobScheduler>(scheduler) |> ignore

        // Phase 9b.A — register the scheduler as the deployment's
        // `IJobSchedulerTelemetry` source (the in-process default
        // implements both interfaces) and add the `/dev/inspect` Job
        // scheduler panel contributor. A future distributed companion
        // (Akka, Orleans Reminders, Hangfire) provides its own
        // `IJobSchedulerTelemetry` impl; the contributor is wired here
        // because it depends on the in-process scheduler's telemetry.
        services.AddSingleton<IJobSchedulerTelemetry>(scheduler :> IJobSchedulerTelemetry)
        |> ignore

        services.AddSingleton<IDevDiagnosticsContributor>(
            JobSchedulerDiagnosticsContributor.JobSchedulerDiagnosticsContributor(scheduler :> IJobSchedulerTelemetry)
            :> IDevDiagnosticsContributor
        )
        |> ignore

        // Phase 16 + 16a — gate scheduler BackgroundService on the
        // centralised matrix. ServerlessHost / WebOnly / DispatcherOnly
        // skip; AllInOne / WorkerOnly register. `JobApiHandler` can
        // still schedule jobs through `IJobStore` even when the
        // scheduler isn't running locally — a sibling `WorkerOnly`
        // silo picks them up.
        if ProcessProfileGate.shouldRegisterBackgroundService config JobSchedulerSubsystem then
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                scheduler :> Microsoft.Extensions.Hosting.IHostedService
            )
            |> ignore

        Some(scheduler :> IJobScheduler)

/// Phase 10 — opt-in data ingestion. `NoDataIngestion` (default) skips
/// registration entirely — no `IDataIngestor`, no
/// `IDataSourceConfigStore`, no `_platform/data-sources/` blob layout,
/// no `IDataIngestionApi` route. Connectors register as `IDataSource`
/// via DI from companion packages; the ingestor's factory below
/// resolves them lazily after `Build()`.
///
/// Phase 10b — OAuth Authorization Code substrate. Always register the
/// state store + cleanup service when data ingestion is enabled —
/// connectors that don't use OAuth (service-account, bearer-token) pay
/// nothing because no `/authorize` traffic flows through. The companion
/// package registering an `IOAuthCredentialFlow` triggers the routes
/// (mounted unconditionally below) to start dispatching.
let registerDataIngestion
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (secretStore: Secrets.ISecretStore)
    (dataObjectStore: IDataObjectStore)
    (eventStore: IEventStore)
    (resolvedLogger: ILogger)
    : unit =
    match config.DataIngestion with
    | NoDataIngestion -> ()
    | EnabledDataIngestion ->
        let configStoreInstance = DataSourceConfigStore.create resolvedBlobStorage
        services.AddSingleton<IDataSourceConfigStore>(configStoreInstance) |> ignore

        services.AddSingleton<IDataIngestor>(fun sp ->
            let connectors =
                sp.GetServices(typeof<IDataSource>) |> Seq.cast<IDataSource> |> List.ofSeq

            let ingestor =
                DataIngestor.create
                    configStoreInstance
                    secretStore
                    dataObjectStore
                    eventStore
                    connectors
                    resolvedBlobStorage
                    resolvedLogger

            // When the job scheduler is also enabled, register the
            // ingestion handler against it so `IDataIngestionApi.
            // TriggerRefresh` can submit `Manual`-trigger jobs. Done
            // lazily here (inside the ingestor factory) so the scheduler
            // is fully built before we touch it.
            match sp.GetService(typeof<IJobScheduler>) with
            | :? IJobScheduler as scheduler ->
                let handler = DataIngestionJobHandler.create ingestor resolvedLogger
                scheduler.RegisterHandler(DataIngestionJobHandler.JobHandlerName, handler)
            | _ -> ()

            ingestor)
        |> ignore

        let oauthStateStore = InMemoryOAuthStateStore() :> IOAuthStateStore
        services.AddSingleton<IOAuthStateStore>(oauthStateStore) |> ignore

        // Phase 16 + 16a — gate state-store cleanup BackgroundService on
        // the centralised matrix. ServerlessHost / WebOnly / DispatcherOnly
        // skip; AllInOne / WorkerOnly register. Per-invocation flows
        // expire their own state-store entries; the periodic sweep is
        // a long-running-process convenience, not a correctness gate.
        if ProcessProfileGate.shouldRegisterBackgroundService config OAuthStateCleanupSubsystem then
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun _ ->
                new OAuthStateCleanupService(oauthStateStore) :> Microsoft.Extensions.Hosting.IHostedService)
            |> ignore

        // Phase 9m / Phase 10b — redirect-base validator. Registered
        // unconditionally as an instance with DataIngestion enabled. The
        // prior factory-pattern registration (gating on at-compose-time
        // presence of an `IOAuthCredentialFlow`) fought with
        // `ConfigValidatorAggregator`'s instance-only contract — the
        // aggregator walks every `AddSingleton<IConfigValidator>` and
        // throws on factory descriptors it cannot introspect. The
        // validator is harmless to instantiate without OAuth flows: it
        // just reads `TOOLUP_OAUTH_REDIRECT_BASE` and warns when unset.
        // For deployments that haven't wired a flow yet, that warning is
        // benign (and is desirable for deployments planning to add one).
        // If the no-flow Warning ever becomes problematic, gate by
        // checking registered `IOAuthCredentialFlow`s inside `Validate()`
        // rather than at registration time.
        services.AddSingleton<ConfigValidation.IConfigValidator>(
            OAuthFlowValidator.OAuthFlowValidator(config) :> ConfigValidation.IConfigValidator
        )
        |> ignore

        // Refuse the in-memory OAuth state store under multi-instance.
        // Registered here (not in the generic validator block) so it is
        // scoped to the OAuth substrate being active — `oauthStateStore`
        // is in scope and DataIngestion-gated, so non-ingestion
        // deployments never see a false-positive refusal.
        services.AddSingleton<ConfigValidation.IConfigValidator>(
            OAuthStateStoreInstanceValidator.OAuthStateStoreInstanceValidator(config, oauthStateStore)
            :> ConfigValidation.IConfigValidator
        )
        |> ignore

        // Phase 138 — refuse an authenticated OAuth-connector deployment
        // whose ISecretStore does not encrypt at rest (connector refresh
        // tokens would persist in plaintext). Registered here (not in the
        // generic validator block) so it is scoped to the OAuth substrate
        // being active — same pattern as OAuthStateStoreInstanceValidator
        // above. Store-type-aware, complementing the env-var-only
        // EncryptedSecretStoreModeValidator.
        services.AddSingleton<ConfigValidation.IConfigValidator>(
            OAuthSecretEncryptionModeValidator.OAuthSecretEncryptionModeValidator(config, secretStore)
            :> ConfigValidation.IConfigValidator
        )
        |> ignore

/// Phase 10h — opt-in OAuth token refresher substrate.
/// `NoOAuthRefresher` (default) skips registration entirely: connectors
/// using OAuth Authorization Code (Phase 10e) refresh synchronously per
/// API call via `IOAuthCredentialFlow.RefreshAccessToken`.
/// `EnabledOAuthRefresher` registers `InProcessOAuthTokenRefresher` as
/// the singleton `IOAuthTokenRefresher` + registers the
/// `OAuthRefreshJobHandler` with the scheduler under
/// `_platform.oauth.refresh` + schedules a startup-time `Recover` to
/// rebuild the in-memory descriptor registry from persisted
/// JobDefinitions.
///
/// Requires `JobScheduler = InProcessJobScheduler` (or a future
/// distributed scheduler companion) — the refresh substrate schedules
/// background jobs and has no way to dispatch them without a scheduler.
/// When the pair is misconfigured we emit a `Warn`-level log at compose
/// and silently skip registration; a future `OAuthRefresherDepsValidator`
/// will gate this at preflight time.
let registerOAuthRefresher
    (services: IServiceCollection)
    (config: ServerConfig)
    (jobSchedulerInstance: IJobScheduler option)
    (secretStore: Secrets.ISecretStore)
    (auditLog: IAuditLog)
    (resolvedMetricsSink: Metrics.IMetricsSink)
    (rateLimiter: IRateLimiter)
    (resolvedLogger: ILogger)
    : unit =
    match config.OAuthRefresher with
    | NoOAuthRefresher -> ()
    | EnabledOAuthRefresher ->
        match jobSchedulerInstance with
        | None ->
            resolvedLogger.Warn(
                "[Phase 10h] OAuthRefresher = EnabledOAuthRefresher but JobScheduler = NoJobScheduler — refresher not registered. Pair with JobScheduler = InProcessJobScheduler."
            )
        | Some scheduler ->
            // Dedicated `HttpClient` instance for refresh-token POSTs.
            // Mirrors the `webhookSubsystem` pattern — long-lived
            // singleton owned by the SDK; no per-call construction, no
            // socket-exhaustion risk.
            let refreshHttpClient = new System.Net.Http.HttpClient()

            let refresher =
                InProcessOAuthTokenRefresher.create
                    scheduler
                    secretStore
                    auditLog
                    resolvedMetricsSink
                    rateLimiter
                    refreshHttpClient
                    resolvedLogger

            services.AddSingleton<IOAuthTokenRefresher>(refresher :> IOAuthTokenRefresher)
            |> ignore

            services.AddSingleton<InProcessOAuthTokenRefresher.Impl>(refresher) |> ignore

            let jobHandler =
                OAuthRefreshJobHandler.create refresher secretStore auditLog resolvedLogger

            scheduler.RegisterHandler(InProcessOAuthTokenRefresher.HandlerName, jobHandler)

            // Schedule descriptor recovery at startup via an
            // `IHostedService`. `Recover` walks every persisted
            // `_platform.oauth.refresh` JobDefinition + repopulates the
            // in-memory descriptor cache so admin-UI reads
            // (`GetDescriptor` / `ListDescriptors`) see existing
            // descriptors immediately after process restart.
            //
            // Phase 16 + 16a — gate the startup-Recover IHostedService on
            // the centralised matrix. ServerlessHost / WebOnly /
            // DispatcherOnly skip; AllInOne / WorkerOnly register.
            // Serverless invocations are stateless per-call; descriptor
            // cache rebuilding is a long-running-process optimisation.
            if ProcessProfileGate.shouldRegisterBackgroundService config OAuthRefresherRecoverSubsystem then
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun _ ->
                    { new Microsoft.Extensions.Hosting.IHostedService with
                        member _.StartAsync(_ct) =
                            refresher.Recover() |> Async.StartAsTask :> System.Threading.Tasks.Task

                        member _.StopAsync(_ct) =
                            System.Threading.Tasks.Task.CompletedTask
                    })
                |> ignore

/// Phase 449 — register the model-fit envelope when
/// `ServerConfig.ModelFitting = EnabledModelFitting`. Indexes every
/// DI-registered `IModelFitProvider` into a `ModelFitProviderRegistry`
/// (duplicate `Kind` rejected at construction) and binds the
/// `ModelFitJobHandler` to `_platform.modelfit.run`. The provider list is
/// only resolvable from the *built* container, so the registry is
/// constructed + validated and the handler is registered with the
/// scheduler inside a startup `IHostedService` (mirrors the OAuth-refresher
/// `Recover` pattern) — a duplicate-`Kind` deployment fails to start loudly.
/// `NoModelFitting` (the default) registers nothing — zero runtime cost
/// when unused (GP 13); modelling math never lives in forge (plan D10).
let registerModelFitting
    (services: IServiceCollection)
    (config: ServerConfig)
    (jobSchedulerInstance: IJobScheduler option)
    (auditLog: IAuditLog)
    (resolvedLogger: ILogger)
    : unit =
    match config.ModelFitting with
    | NoModelFitting -> ()
    | EnabledModelFitting ->
        match jobSchedulerInstance with
        | None ->
            resolvedLogger.Warn(
                "[Phase 449] ModelFitting = EnabledModelFitting but JobScheduler = NoJobScheduler — fit envelope not registered. Pair with JobScheduler = InProcessJobScheduler."
            )
        | Some scheduler ->
            // Lazy singleton: the registry (and its duplicate-`Kind` guard)
            // is not constructed until first resolved — at StartAsync below.
            services.AddSingleton<ModelFitProviderRegistry>(fun (sp: System.IServiceProvider) ->
                let providers = sp.GetServices<IModelFitProvider>() |> List.ofSeq
                ModelFitProviderRegistry(providers))
            |> ignore

            // Register the handler with the scheduler at startup, once the
            // container (and every IModelFitProvider) is built. Resolving the
            // registry here triggers the duplicate-`Kind` validation, so a
            // misconfigured deployment fails on startup.
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun (sp: System.IServiceProvider) ->
                { new Microsoft.Extensions.Hosting.IHostedService with
                    member _.StartAsync(_ct) =
                        let registry = sp.GetRequiredService<ModelFitProviderRegistry>()
                        let handler = ModelFitJobHandler.create registry auditLog resolvedLogger
                        scheduler.RegisterHandler(ModelFitJobHandler.HandlerName, handler)

                        resolvedLogger.Info(
                            sprintf
                                "[Phase 449] ModelFit envelope registered — %d provider(s): %s"
                                registry.Providers.Length
                                (String.concat ", " registry.Kinds)
                        )

                        System.Threading.Tasks.Task.CompletedTask

                    member _.StopAsync(_ct) =
                        System.Threading.Tasks.Task.CompletedTask
                })
            |> ignore

/// Phase 9b.B — register and schedule module-/app-declared
/// `ScheduledJobDeclaration`s against the resolved `IJobScheduler`.
/// Called once at the end of compose, after the scheduler singleton
/// is built — registration is data-driven (no post-`Build` resolution
/// of `IJobScheduler` is required by consumers).
///
/// Two outcomes:
///   * `JobScheduler = NoJobScheduler` AND non-empty declarations →
///     emit one `Warn` summarising the count + handler names skipped
///     and return. A module declaring jobs in an unscheduled
///     deployment is a config mismatch (declarations are dead code),
///     not a crash — the warning is loud enough to be caught at the
///     first startup grep.
///   * `JobScheduler = InProcessJobScheduler` (or any future
///     distributed companion) → for each declaration:
///       1. Call `RegisterHandler(name, handler)` (idempotent at the
///          scheduler — re-registration overwrites the same name).
///       2. Default `Scopes = []` to `["_platform"]` (the reserved
///          scope SDK-internal handlers use).
///       3. Build a `JobRegistration` per scope with a stable
///          idempotency key (`module-{handlerName}-{scopeId}`, one-
///          year TTL) so process restart returns the existing
///          `JobId` rather than creating a duplicate definition.
///       4. `Schedule` synchronously — the in-process scheduler's
///          `Schedule` is fast (blob write + in-memory cache update);
///          any failure is logged at `Warn` (the deployment still
///          boots — a single misconfigured cron should not take down
///          the process).
let registerScheduledJobDeclarations
    (jobSchedulerInstance: IJobScheduler option)
    (declarations: ScheduledJobDeclaration list)
    (resolvedLogger: ILogger)
    : unit =
    if List.isEmpty declarations then
        ()
    else
        match jobSchedulerInstance with
        | None ->
            let handlerNames = declarations |> List.map _.HandlerName |> String.concat ", "

            resolvedLogger.Warn(
                sprintf
                    "[Phase 9b.B] %d scheduled-job declaration(s) skipped — JobScheduler = NoJobScheduler. Handlers: %s. Pair declarations with JobScheduler = InProcessJobScheduler (or a distributed scheduler companion) to schedule them at compose time."
                    declarations.Length
                    handlerNames
            )
        | Some scheduler ->
            for declaration in declarations do
                scheduler.RegisterHandler(declaration.HandlerName, declaration.Handler)

                let scopes =
                    if List.isEmpty declaration.Scopes then
                        [ "_platform" ]
                    else
                        declaration.Scopes

                for scopeId in scopes do
                    let idempotency =
                        match declaration.Idempotency with
                        | Some key -> Some key
                        | None ->
                            Some {
                                Key = sprintf "module-%s-%s" declaration.HandlerName scopeId
                                TtlSeconds = 60 * 60 * 24 * 365
                            }

                    let registration: JobRegistration = {
                        ScopeId = scopeId
                        Handler = declaration.HandlerName
                        Payload = declaration.Payload
                        Trigger = declaration.Trigger
                        Idempotency = idempotency
                        RetryPolicy = declaration.RetryPolicy
                        ShardKey = declaration.ShardKey
                        Precision = declaration.Precision
                        CreatedBy = "_platform"
                        Tags = declaration.Tags |> Map.add "source" "compose-time"
                    }

                    let result = scheduler.Schedule registration |> Async.RunSynchronously

                    match result with
                    | Ok _ -> ()
                    | Error err ->
                        resolvedLogger.Warn(
                            sprintf
                                "[Phase 9b.B] Failed to schedule %s in scope %s: %A"
                                declaration.HandlerName
                                scopeId
                                err
                        )

/// Phase 9h.A — register the background DSR export/erasure substrate.
/// Gated on `DataSubjectRequests = Enabled { Async = true }`:
///   * registers `IBackgroundExportStore` (blob-backed default) as a
///     singleton so the per-request API mount can resolve it;
///   * registers the `DSRExportJobHandler` + `DSRErasureJobHandler` on
///     the scheduler at startup (via an `IHostedService` so the
///     registered `IDataExporter` / `IErasureHandler` lists are
///     resolvable from the built provider). `RegisterHandler` is
///     idempotent and export jobs are only scheduled at request time
///     (post-startup), so startup registration is race-free.
///
/// `DataSubjectRequests = Enabled { Async = true }` with
/// `JobScheduler = NoJobScheduler` is a config mismatch — logged at
/// `Warn`; `RequestExportAsync` then returns `Error` (the synchronous
/// `RequestExport` path is unaffected).
let registerDataSubjectRequestJobs
    (services: IServiceCollection)
    (config: ServerConfig)
    (resolvedBlobStorage: IBlobStorage)
    (jobSchedulerInstance: IJobScheduler option)
    (auditLog: IAuditLog)
    (notificationChannel: INotificationChannel)
    (resolvedLogger: ILogger)
    : unit =
    match config.DataSubjectRequests with
    | DataSubjectRequestMode.Enabled cfg when cfg.Async ->
        match jobSchedulerInstance with
        | None ->
            resolvedLogger.Warn(
                "[Phase 9h.A] DataSubjectRequests = Enabled { Async = true } but JobScheduler = NoJobScheduler — background export not registered; RequestExportAsync will return Error. Pair with JobScheduler = InProcessJobScheduler (or a distributed scheduler companion)."
            )
        | Some scheduler ->
            let store = BlobBackedBackgroundExportStore.create resolvedBlobStorage
            services.AddSingleton<IBackgroundExportStore>(store) |> ignore

            let audit = DataSubjectRequestApiHandler.auditToLog auditLog

            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(fun sp ->
                { new Microsoft.Extensions.Hosting.IHostedService with
                    member _.StartAsync(_ct) =
                        let exporters =
                            sp.GetServices(typeof<IDataExporter>) |> Seq.cast<IDataExporter> |> List.ofSeq

                        let handlers =
                            sp.GetServices(typeof<IErasureHandler>)
                            |> Seq.cast<IErasureHandler>
                            |> List.ofSeq

                        scheduler.RegisterHandler(
                            DataSubjectRequestApiHandler.DsrJobs.ExportHandler,
                            DSRExportJobHandler.createWithNotifications
                                store
                                exporters
                                audit
                                (Some notificationChannel)
                                resolvedLogger
                        )

                        scheduler.RegisterHandler(
                            DataSubjectRequestApiHandler.DsrJobs.ErasureHandler,
                            DSRErasureJobHandler.create handlers audit resolvedLogger
                        )

                        resolvedLogger.Info(
                            sprintf
                                "[Phase 9h.A] Registered DSR background export/erasure job handlers (%d exporter(s), %d erasure handler(s))."
                                exporters.Length
                                handlers.Length
                        )

                        System.Threading.Tasks.Task.CompletedTask

                    member _.StopAsync(_ct) =
                        System.Threading.Tasks.Task.CompletedTask
                })
            |> ignore
    | _ -> ()