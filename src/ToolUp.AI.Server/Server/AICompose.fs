module ToolUp.AI.AICompose

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.Usage
open ToolUp.Platform.Secrets
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.Server
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.AIAssistantHandler
open ToolUp.AI.AISettingsHandler
open ToolUp.AI.SSEHandler
open ToolUp.AI.SystemPromptBuilder

// ─── AIServerApp — record-based superset of `ServerApp` with AI ─────
//
// `AIServerApp` is a flat superset of `ServerApp`: every `ServerApp.with*`
// helper has a delegating counterpart on `AIServerApp` that lifts the
// operation onto the inner `Base` field, so the user writes a single
// fluent pipeline regardless of which companion is in play. AI-specific
// fields (provider factory, config store, tool registry, branding,
// module contexts) sit alongside.
//
// Required fields (`AIProviderFactory`, `ProviderProfile`) are
// constructor parameters on `AIServerApp.create` rather than mutable
// record fields — the SDK refuses to start without both, so making
// them required at compile time replaces a runtime `failwith`. Phase
// 43.A: the second required dependency is now the canonical platform
// `IProviderProfile` store (was the removed `IUserAIConfigStore`).
//
// Composition root pattern (single-superset, terminal `run`):
//
//     AIServerApp.create factory providerProfile
//     |> AIServerApp.withConfig config         // delegates to base
//     |> AIServerApp.withAuth auth             // delegates to base
//     |> AIServerApp.withStorage blobStorage   // delegates to base
//     |> AIServerApp.addModules modules        // delegates to base
//     |> AIServerApp.withAIConfig assistant    // AI-specific
//     |> AIServerApp.run
//
// Composition root pattern (Phase 1h — stack AI with Forms / RAG):
//
//     ServerApp.empty
//     |> ServerApp.withConfig config
//     |> ServerApp.withStorage blobStorage
//     |> FormsCompose.withForms (fun f -> f |> FormsServerApp.withFormSchema schema)
//     |> AICompose.withAI factory providerProfile (fun ai -> ai |> AIServerApp.withAIConfig assistant)
//     |> ServerApp.run

/// Record form of AI compose arguments. Wraps a base `ServerApp` and
/// adds AI-specific extension points. The required AI fields
/// (`AIProviderFactory`, `ProviderProfile`) are constructor parameters
/// on `AIServerApp.create`, not optional fields. AI tools come from
/// each module's `ServerModule.withAITools` and are aggregated on the
/// inner `Base.AITools` — there is no AI-level tools field.
type AIServerApp = {
    Base: ServerApp
    AIProviderFactory: IAIProviderFactory
    /// Canonical platform-wide BYOK store (Phase 43.A). The AI factory
    /// resolves providers against this; the settings handler + usage-
    /// metering wrapper read it. Mirrored onto `Base.ProviderProfile`
    /// by `create` / `createFrom` so a non-AI handler in the same app
    /// can resolve it from DI too.
    ProviderProfile: IProviderProfile
    /// Phase 70 — Platform-Admin-managed AI key store. `None` lets the
    /// composer auto-promote `BlobPlatformAIKeyStore.create secretStore`
    /// at compose time when `ISecretStore` is registered in DI. Setting
    /// `Some store` overrides with a custom backing implementation
    /// (in-memory test double, future custom companion). The store is
    /// registered as `IPlatformAIKeyStore` in DI; the factory's
    /// resolution chain consumes it via the consumer's
    /// `DefaultAIProviderFactory.create` call, and the Platform Admin
    /// keys handler (Phase 70 Stream D) resolves it from DI.
    PlatformKeyStore: IPlatformAIKeyStore option
    /// Phase 70 A.5 — declarative accumulator for the platform providers
    /// wired into `AIProviderFactory`. Populated additively via
    /// `withPlatformProvider`. Today the consumer still constructs the
    /// factory directly (passing the same list to
    /// `DefaultAIProviderFactory.create`); `composeAI` validates that
    /// the accumulator and `factory.PlatformDescriptors` agree, failing
    /// loudly when they diverge (catches the "wired the builder but
    /// forgot to pass them to the factory" footgun). An empty list
    /// disables the validator — the factory is the source of truth.
    ///
    /// The field shape is also the seat for a future `composeAI`-
    /// internal factory construction (deferred — no consumer migration
    /// target has surfaced; downstream consumers use the existing
    /// pattern unchanged). When that refactor lands, this field becomes
    /// the input to `DefaultAIProviderFactory.create` rather than the
    /// validator's reference set.
    PlatformProviders: DefaultAIProviderFactory.AIPlatformProvider list
    AIConfig: AIAssistantServerConfig option
    ModuleAIContexts: ModuleAIContext list
}

// ─── composeAI — AI-specific contribution layer ───────────────────────
//
// Phase 1h seam: `composeAI : AIServerApp -> ServerApp` lifts every
// AI-specific contribution (DI registrations, agent-loop handlers,
// tool registry, dev endpoints, notification-consumer declaration,
// SDK config validators) onto the inner `ServerApp.Extensions`,
// returning the composed `ServerApp` without driving it.
//
// `AIServerApp.run` is now `composeAI >> ServerApp.run`; the additive
// `withAI` extension calls `composeAI` from inside a `ServerApp`-
// shaped pipeline so AI contributions stack with Forms / RAG / future
// companions on one composition root.
//
// **`SSEConnectionManager`** is registered by `compose` in core (it is
// shared with the generic `INotificationChannel` path) and resolved
// from DI here — AI owns only the serialisation of `AIStreamEvent`
// and the `/api/ai/events` route, not the transport plumbing.
//
// The factory (rather than a single `IAIProvider`) enables per-request
// provider resolution. The canonical platform `IProviderProfile` store
// backs the settings API — users and team admins use it to register
// their own provider instances. Deployments that don't want BYOK pass
// `DefaultAIProviderFactory.singleProvider` as the factory; the
// settings API returns empty `Available` in that case (PlatformOnly
// policy), hiding the configuration surface in the UI.

/// Apply every AI-specific contribution (DI registrations, agent-loop
/// handlers, tool registry, dev endpoints, notification-consumer
/// declaration, SDK config validators) onto the inner `ServerApp`,
/// returning the composed result without driving it. `AIServerApp.run`
/// calls this and then `ServerApp.run`; the `withAI` additive
/// extension calls this from inside a `ServerApp`-shaped pipeline so
/// AI contributions stack with Forms / RAG contributions onto one
/// composition root (Phase 1h goal).
///
/// **Advanced.** Consumers should use `AIServerApp.run` unless they
/// are stacking multiple companion supersets — in which case use the
/// `withAI` additive extension. Hidden from IntelliSense via
/// `[<EditorBrowsable>]`.
[<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
let composeAI (app: AIServerApp) : ServerApp =
    let b = app.Base
    let config = b.Config
    let baseExtensions = b.Extensions
    let aiProviderFactory = app.AIProviderFactory
    let providerProfile = app.ProviderProfile
    let moduleTools = b.AITools
    let aiConfig = app.AIConfig
    let moduleAIContexts = app.ModuleAIContexts

    // Validate tool name uniqueness across modules AND against the
    // platform-reserved built-in tool names (`NarrativeTools.builtInTools`
    // + Phase 36.B's `PlatformAITools.builtIn`). Duplicates fail loudly
    // at compose time with a clear message, before any agent turn runs.
    // Before this check included built-ins, a module tool named e.g.
    // `list_narratives` passed compose validation and silently lost
    // lookup to the prepended built-in at `registry.RegisterAll` (the
    // agent then saw two tools with the same name and hit a provider-side
    // 400 at runtime).
    let builtInToolNames =
        (NarrativeTools.builtInTools @ PlatformAITools.builtIn)
        |> List.map _.Definition.Name

    let moduleToolNames = moduleTools |> List.map (fun (def, _) -> def.Name)

    let duplicateNames =
        (builtInToolNames @ moduleToolNames)
        |> List.groupBy id
        |> List.choose (fun (n, occurrences) -> if occurrences.Length > 1 then Some n else None)

    if not duplicateNames.IsEmpty then
        failwithf
            "AI tool name collision: %s. Each tool must have a unique Name across the deployment. The SDK reserves the platform built-in tool names [%s] — rename any module-declared tools that collide."
            (System.String.Join(", ", duplicateNames))
            (System.String.Join(", ", builtInToolNames))

    // Phase 70 A.5 — when the consumer declared platform providers via
    // `AIServerApp.withPlatformProvider`, verify the accumulated list
    // matches what `factory.PlatformDescriptors` reports. A divergence
    // means the consumer wired providers via the builder but passed a
    // different list (or none) to `DefaultAIProviderFactory.create` —
    // a class of bug that otherwise surfaces only at first request as
    // a mysterious `NoProviderConfigured`. Empty `app.PlatformProviders`
    // disables the check (back-compat with consumers that haven't
    // adopted the builder).
    if not app.PlatformProviders.IsEmpty then
        let declaredIds = app.PlatformProviders |> List.map _.Descriptor.Id
        let factoryIds = aiProviderFactory.PlatformDescriptors |> List.map _.Id

        // Compare as sets — the validator's purpose is to catch
        // "declared providers do not match factory-wired providers", a
        // set membership question; the declaration *order* the consumer
        // used (calls to `withPlatformProvider`) doesn't have to match
        // the order `DefaultAIProviderFactory.create` exposes via
        // `PlatformDescriptors`. Two valid orderings of the same set
        // previously tripped this failure with a misleading "wired the
        // builder but forgot" message.
        if Set.ofList declaredIds <> Set.ofList factoryIds then
            failwithf
                "AI platform-provider declaration mismatch. AIServerApp.withPlatformProvider declared [%s] but the supplied IAIProviderFactory reports PlatformDescriptors = [%s]. Either pass the same providers to DefaultAIProviderFactory.create, or drop the withPlatformProvider calls — the factory is the source of truth for runtime resolution. (Phase 70 A.5)"
                (System.String.Join(", ", declaredIds))
                (System.String.Join(", ", factoryIds))

    // Convert per-module `(definition, executor)` tuples into the
    // companion's `RegisteredTool` shape and prepend the SDK's built-in
    // tools (narrative + Phase 36.B's `_platform.ai.*` cross-module read
    // family). Modules contribute their tools via
    // `ServerModule.withAITools`; `composeAI` aggregates them via
    // `ServerApp.AITools` and registers them here. Built-ins reserve
    // platform-prefixed names so they cannot collide with module tools.
    let registeredModuleTools =
        moduleTools |> List.map (fun (def, exec) -> createTool def exec)

    let registry = AIToolRegistry()
    registry.RegisterAll(NarrativeTools.builtInTools @ PlatformAITools.builtIn @ registeredModuleTools)

    let moduleAIContextMap =
        moduleAIContexts |> List.map (fun c -> c.ModuleName, c) |> Map.ofList

    // `SSEConnectionManager` is registered by core `compose` — resolve
    // it per-request (or per-handler) from DI. The AI routes pull it
    // out of `ctx.RequestServices` at construction time so they share
    // the same instance as the generic `/api/notifications` endpoint.
    let resolveManager (ctx: HttpContext) =
        ctx.RequestServices.GetService(typeof<SSEConnectionManager>) :?> SSEConnectionManager

    // Phase 6g.A: per-process registry of pending client-resident tool
    // calls. Singleton — same instance resolved by `aiAssistantApi`
    // (which suspends agent-loop tool calls on it) and by
    // `clientToolResultHandler` (which completes them when the browser
    // POSTs back).
    let dispatchRegistry = ClientToolDispatch.ClientToolDispatchRegistry()

    // Phase 6h: per-task `CancellationTokenSource` registry for the
    // cancel-mid-stream feature. `aiAssistantApi` registers a CTS
    // when starting the agent loop; `cancelHandler` cancels it when
    // the user clicks Cancel; the handler unregisters on natural
    // completion via try/finally.
    let cancellationRegistry = AICancellationRegistry.AICancellationRegistry()

    // Phase 6j.A + 6i.A: dev-only telemetry endpoints. Runtime-gated via
    // `ServerConfig.EnableDevEndpoints` (default `false`). The previous
    // `#if DEBUG` belt-and-suspenders gate was removed when ToolUp.Platform
    // and OSS-bound companions stopped carrying compile-time gates. Two
    // endpoints share the gate:
    //   * `/dev/ai-fastpath` — Phase 6j.A Tier-1 hit-rate stats
    //   * `/dev/ai-latency`  — Phase 6i.A per-turn latency rollup
    let fastPathDevHandlers =
        if config.EnableDevEndpoints then
            FastPathTelemetryHandler.routes @ AILatencyHandler.routes
        else
            []

    let aiHandlers =
        [
            makeApi (fun ctx -> aiAssistantApi aiConfig moduleAIContextMap (resolveManager ctx) ctx |> fst)
            // Phase 69c.tail A — typed streaming companion (StreamChatV2).
            // Same per-request closure; the dispatcher auto-frames its
            // IAsyncEnumerable<AIStreamEvent> method as SSE. Legacy
            // SubmitMessage + /api/ai/events (below) stay mounted unchanged.
            makeApi (fun ctx -> aiAssistantApi aiConfig moduleAIContextMap (resolveManager ctx) ctx |> snd)
            makeApi (aiSettingsApi aiProviderFactory providerProfile)
            // Phase 70 — Platform Admin AI keys API. Every method
            // is gated server-side on canModifyPlatformConfig; the
            // client-side module is hidden from non-admin sidebars by
            // ClientModule.Visibility.platformAdminOnly.
            makeApi (PlatformAIKeysHandler.platformAIKeysApi aiProviderFactory)
            route "/api/ai/events"
            >=> fun next ctx -> sseHandler (resolveManager ctx) config.SseAuthMode next ctx
            // Phase 6g.A: client-resident tool result POST endpoint.
            POST
            >=> route "/api/ai/tool-result"
            >=> ClientToolDispatch.clientToolResultHandler
            // Phase 6h: cancel-mid-stream endpoint.
            POST >=> routef "/api/ai/cancel/%O" AICancellationRegistry.cancelHandler
            // Phase 6j.A: fast-path audit beacon.
            POST >=> route "/api/ai/fastpath/beacon" >=> FastPathBeaconHandler.beaconHandler
            // Phase 6j.G: sequenced fast-path beacons.
            POST
            >=> route "/api/ai/fastpath/sequenced-clause-beacon"
            >=> FastPathBeaconHandler.sequencedClauseBeaconHandler
            POST
            >=> route "/api/ai/fastpath/sequence-outcome-beacon"
            >=> FastPathBeaconHandler.sequenceOutcomeBeaconHandler
            // Phase 6h.A: conversation-export audit beacon.
            POST
            >=> route "/api/ai/conversation/export-audit"
            >=> ConversationExportAuditHandler.exportAuditHandler
            // Phase 6g.E: client UI decoder-error sink.
            POST
            >=> route "/api/ai/ui-decode-error"
            >=> UIDecodeErrorHandler.uiDecodeErrorHandler
        ]
        @ fastPathDevHandlers

    let aiServiceConfig (s: IServiceCollection) =
        // Phase 9d (usage metering) + Phase 9 compute-quota. The
        // delegate factory resolves `IUsageLog` and `ITeamQuotaPolicy`
        // from DI per request and stacks the wrappers over the
        // composition-supplied raw factory. Same shape as RAGCompose
        // via the shared `AIProviderUsageMiddleware.wrapFactoryForDI`
        // helper — keeps RAG-using deployments from silently bypassing
        // both subsystems on AI calls.
        let s =
            s
                .AddSingleton<IAIProviderFactory>(
                    AIProviderUsageMiddleware.wrapFactoryForDI config aiProviderFactory providerProfile
                )
                .AddSingleton<IProviderProfile>(providerProfile)
                // Phase 171 — expose the active AI provider/model to the
                // platform-tier Home overview via the Core IActiveAiProbe
                // seam (GP 1: Platform.Server stays AI-dependency-free).
                .AddSingleton<IActiveAiProbe>(ActiveAiProbe.create aiProviderFactory)
                // Phase 70 — register the Platform-Admin-managed AI key
                // store. When the consumer passed `withPlatformAIKeyStore`
                // explicitly, register that instance directly. When
                // omitted, register a Func-resolver that lazily builds
                // `BlobPlatformAIKeyStore.create secretStore` from
                // whichever `ISecretStore` is in DI at request time.
                // Platform-side handlers (Phase 70 Stream D's
                // `PlatformAIKeysHandler`) resolve via DI.
                .AddSingleton<IPlatformAIKeyStore>(
                    System.Func<IServiceProvider, IPlatformAIKeyStore>(fun sp ->
                        match app.PlatformKeyStore with
                        | Some store -> store
                        | None ->
                            let secretStore = sp.GetService(typeof<ISecretStore>) :?> ISecretStore
                            BlobPlatformAIKeyStore.create secretStore)
                )
                .AddSingleton<AIToolRegistry>(registry)
                .AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(dispatchRegistry)
                .AddSingleton<AICancellationRegistry.AICancellationRegistry>(cancellationRegistry)
                // Warn at startup when AI runs multi-instance with the
                // in-process cancel / client-tool-dispatch registries
                // (no cross-instance routing yet).
                .AddSingleton<ConfigValidation.IConfigValidator>(
                    AICancellationDispatchInstanceValidator.AICancellationDispatchInstanceValidator(config)
                    :> ConfigValidation.IConfigValidator
                )
                // Phase 9m.A — catch operator-typo'd TOOLUP_AI_PROVIDER /
                // TOOLUP_AI_MODEL env vars at startup. Both validators self-
                // skip with Ok when the corresponding env var is unset
                // (GP 13 — zero cost for deployments that do not rely on
                // the env vars).
                .AddSingleton<ConfigValidation.IConfigValidator>(AIProviderEnvValidator.create aiProviderFactory)
                .AddSingleton<ConfigValidation.IConfigValidator>(AIModelEnvValidator.create aiProviderFactory)

        // Phase 9m.A — opt-in startup probe (default OFF). Registered
        // only when TOOLUP_AI_PROBE_ON_STARTUP=1 — pays nothing for
        // deployments that haven't opted in (GP 13). When enabled,
        // probes the resolved provider's models endpoint with the
        // API key from the provider's documented env var
        // (ANTHROPIC_API_KEY / OPENAI_API_KEY / GEMINI_API_KEY).
        match AIProviderProbeValidator.tryFromEnv aiProviderFactory with
        | None -> s
        | Some probe -> s.AddSingleton<ConfigValidation.IConfigValidator>(probe)

    // Merge AI-specific contributions onto whatever the base
    // `ServerApp.Extensions` already accumulated. Pre/post middleware
    // thunks the consumer registered via `ServerApp.withPreMiddleware` /
    // `withPostMiddleware` flow through unchanged; AI's handlers / DI /
    // notification consumers append.
    let extensions: ComposeExtensions = {
        Handlers = baseExtensions.Handlers @ aiHandlers
        ServiceConfig =
            match baseExtensions.ServiceConfig with
            | None -> Some aiServiceConfig
            | Some baseFn -> Some(fun s -> aiServiceConfig (baseFn s))
        // Phase 1g — AI tool completions and SSE-driven chat publish
        // through `INotificationChannel`; declaring the dependency
        // here flips `compose`'s `NotificationsAuto` resolution to
        // `InMemoryNotifications` so the SSE endpoint mounts and
        // `InMemoryNotificationChannel` registers automatically.
        NotificationConsumers = baseExtensions.NotificationConsumers @ [ "AI" ]
        PreMiddleware = baseExtensions.PreMiddleware
        PostMiddleware = baseExtensions.PostMiddleware
    }

    { b with Extensions = extensions }

module AIServerApp =
    /// Construct an `AIServerApp` from scratch with the two required AI
    /// dependencies: the provider factory and the canonical platform
    /// `IProviderProfile` BYOK store (Phase 43.A — replaces the removed
    /// `IUserAIConfigStore` shim). The store is mirrored onto
    /// `Base.ProviderProfile` so it is also the `IProviderProfile` DI
    /// singleton. All other fields default to the empty / `None`
    /// values; chain `with*` helpers (delegating to `ServerApp` or
    /// AI-specific) to configure further.
    let create (factory: IAIProviderFactory) (providerProfile: IProviderProfile) : AIServerApp = {
        Base =
            ServerApp.empty
            |> ServerApp.withProviderProfile providerProfile
            |> ServerApp.withMetricRegistrations AILatencyMetrics.registrations
        AIProviderFactory = factory
        ProviderProfile = providerProfile
        PlatformKeyStore = None
        PlatformProviders = []
        AIConfig = None
        ModuleAIContexts = []
    }

    /// Phase 1h composition seam — lift an existing `ServerApp` into an
    /// `AIServerApp` so the additive `AICompose.withAI` extension can
    /// stack AI contributions onto whatever the input `ServerApp`
    /// already carries. `Base.ProviderProfile` is mirrored to the
    /// supplied store (overriding any previous `withProviderProfile`
    /// on the same pipeline — last-write-wins), and
    /// `AILatencyMetrics.registrations` are appended (calling `withAI`
    /// twice on the same pipeline would re-append and fail at sink
    /// construction — the Phase 1h conflict validator catches this).
    let createFrom
        (factory: IAIProviderFactory)
        (providerProfile: IProviderProfile)
        (baseApp: ServerApp)
        : AIServerApp =
        {
            Base =
                baseApp
                |> ServerApp.withProviderProfile providerProfile
                |> ServerApp.withMetricRegistrations AILatencyMetrics.registrations
            AIProviderFactory = factory
            ProviderProfile = providerProfile
            PlatformKeyStore = None
            PlatformProviders = []
            AIConfig = None
            ModuleAIContexts = []
        }

    // ─── Delegating helpers (mirror every `ServerApp.with*` / `add*`) ───

    let withConfig (c: ServerConfig) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    /// Register an `IUserDirectory` for invite-form typeahead + email-
    /// address resolution. Delegates to `ServerApp.withUserDirectory` so
    /// a RAG/AI composition root no longer has to reach into the raw
    /// `withExtensions` DI seam to register one.
    let withUserDirectory (directory: IUserDirectory) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withUserDirectory directory app.Base
    }

    /// Register an additional `ICspContributor` (a webfont / CDN / embed
    /// origin the first-party CSP defaults don't cover). Delegates to
    /// `ServerApp.withCspContributor`.
    let withCspContributor (contributor: ICspContributor) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withCspContributor contributor app.Base
    }

    /// Phase 6f — register an out-of-band transactional notification
    /// sink. Delegates to `ServerApp.withTransactionalSink`. See that
    /// helper's docstring for the full contract; this mirror exists
    /// so the AI fluent pipeline reads the same as the core one.
    let withTransactionalSink (sink: INotificationSink) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withTransactionalSink sink app.Base
    }

    /// Phase 9k — register a companion-contributed health probe.
    /// Delegates to `ServerApp.withHealthCheck`.
    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    /// Phase 9m — register a companion-contributed startup config
    /// validator. Delegates to `ServerApp.withConfigValidator`.
    let withConfigValidator (validator: ConfigValidation.IConfigValidator) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withConfigValidator validator app.Base
    }

    /// Phase 22 — opt into AES-GCM envelope encryption for the
    /// registered IBlobStorage. Delegates to
    /// `ServerApp.withEncryptedBlobStorage`. See that helper's
    /// docstring for the resolver-choice contract.
    let withEncryptedBlobStorage (resolver: IBlobEncryptionKeyResolver) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withEncryptedBlobStorage resolver app.Base
    }

    /// Phase 9e — register a companion-contributed metrics sink
    /// (e.g. OpenTelemetry exporter) alongside the in-process
    /// Prometheus default. Delegates to `ServerApp.withMetricsSink`.
    let withMetricsSink (sink: Metrics.IMetricsSink) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withMetricsSink sink app.Base
    }

    /// Phase 9v — declare an outbound rate-limit window for one
    /// upstream provider. Delegates to `ServerApp.withRateLimitDescriptor`.
    let withRateLimitDescriptor (descriptor: RateLimitDescriptor) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withRateLimitDescriptor descriptor app.Base
    }

    /// Phase 19 — register an entity type with the typed entity store.
    /// Delegates to `ServerApp.withEntity`.
    let withEntity<'T> (registration: EntityTypes.EntityRegistration<'T>) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withEntity registration app.Base
    }

    /// Phase 9b.B — declare a composition-root-owned background job.
    /// Delegates to `ServerApp.withJobHandler`. See that helper's
    /// docstring for the full contract; this mirror exists so the AI
    /// fluent pipeline reads the same as the core one.
    let withJobHandler (handlerName: string, handler: IJobHandler, trigger: Trigger) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withJobHandler (handlerName, handler, trigger) app.Base
    }

    /// Phase 9b.B — declare a composition-root-owned background job
    /// with full control over every `JobRegistration` knob. Delegates
    /// to `ServerApp.withScheduledJob`.
    let withScheduledJob (declaration: ScheduledJobDeclaration) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withScheduledJob declaration app.Base
    }

    /// Phase 9b.A — opt into back-fill of `OnEvent` jobs on detected
    /// scheduler tick drift. Delegates to `ServerApp.withBackfillMissedTicks`.
    let withBackfillMissedTicks (enabled: bool) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withBackfillMissedTicks enabled app.Base
    }

    /// Phase 598 — opt into the event-trigger catch-up watermark.
    /// Delegates to `ServerApp.withEventTriggerCatchUp`.
    let withEventTriggerCatchUp (enabled: bool) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withEventTriggerCatchUp enabled app.Base
    }

    let withExtensions (e: ComposeExtensions) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withExtensions e app.Base
    }

    let withPreMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withPreMiddleware f app.Base
    }

    let withPostMiddleware (f: IApplicationBuilder -> IApplicationBuilder) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.withPostMiddleware f app.Base
    }

    let addModule (m: ServerModule) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: AIServerApp) : AIServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── AI-specific helpers ───────────────────────────────────────────

    let withAIConfig (config: AIAssistantServerConfig) (app: AIServerApp) : AIServerApp = {
        app with
            AIConfig = Some config
    }

    let withModuleAIContexts (contexts: ModuleAIContext list) (app: AIServerApp) : AIServerApp = {
        app with
            ModuleAIContexts = contexts
    }

    /// Phase 523 — opt into the numeric-fidelity answer gate with a custom
    /// verifier (the seat for the future LLM-judge qualitative tier, which
    /// implements the same `IAnswerVerifier` seam). Registers the composed
    /// `AnswerVerifier.AnswerGate` as a DI singleton (resolved by the chat
    /// handler's post-response stage) and appends the verified/unmatched
    /// counter registrations so the metrics sink pre-allocates their series.
    ///
    /// `AnswerGateOff` (or not calling this at all) leaves the answer path
    /// byte-for-byte pre-523 — no DI registration, no metric series, no
    /// per-turn verification (GP 11 / GP 13). Threads the singleton through
    /// the shared `ComposeExtensions.ServiceConfig` exactly like
    /// `ServerApp.withCspContributor`, so `RAGServerApp` inherits it.
    let withAnswerVerifier
        (mode: AnswerVerifier.AnswerGateMode)
        (verifier: AnswerVerifier.IAnswerVerifier)
        (app: AIServerApp)
        : AIServerApp =
        match mode with
        | AnswerVerifier.AnswerGateOff -> app
        | _ ->
            let gate: AnswerVerifier.AnswerGate = { Mode = mode; Verifier = verifier }

            let register (s: IServiceCollection) =
                s.AddSingleton<AnswerVerifier.AnswerGate>(gate)

            let baseWithMetrics =
                app.Base |> ServerApp.withMetricRegistrations AnswerVerifier.registrations

            {
                app with
                    Base = {
                        baseWithMetrics with
                            Extensions = {
                                baseWithMetrics.Extensions with
                                    ServiceConfig =
                                        match baseWithMetrics.Extensions.ServiceConfig with
                                        | None -> Some register
                                        | Some baseFn -> Some(fun s -> register (baseFn s))
                            }
                    }
            }

    /// Phase 523 — opt into the numeric-fidelity answer gate with the
    /// default deterministic verifier (`NumericFidelityVerifier`). See
    /// `withAnswerVerifier` for the gate semantics; `AnswerGateOff` is the
    /// byte-identical no-op default (GP 13).
    let withNumericFidelityGate (mode: AnswerVerifier.AnswerGateMode) (app: AIServerApp) : AIServerApp =
        withAnswerVerifier mode (AnswerVerifier.NumericFidelityVerifier() :> AnswerVerifier.IAnswerVerifier) app

    /// Phase 43.A — swap the canonical BYOK provider-profile store
    /// after `create`. Mirrors `ServerApp.withProviderProfile` (the
    /// deferred 42.B seam) onto the AI superset: rebinds both the
    /// required AI field (what `composeAI` threads to the factory's
    /// settings handler + metering wrapper) and `Base.ProviderProfile`
    /// (the DI singleton) so the two never diverge.
    let withProviderProfile (store: IProviderProfile) (app: AIServerApp) : AIServerApp = {
        app with
            ProviderProfile = store
            Base = ServerApp.withProviderProfile store app.Base
    }

    /// Phase 70 — supply a Platform-Admin-managed AI key store. When
    /// omitted, `composeAI` auto-promotes `BlobPlatformAIKeyStore.create`
    /// over the registered `ISecretStore` so the Platform Admin keys
    /// module works out of the box. Override when you want a custom
    /// backing store (in-memory test double, Azure Key Vault wrapper,
    /// AWS Secrets Manager wrapper, etc.) — the store is registered as
    /// `IPlatformAIKeyStore` in DI and surfaces to the factory's
    /// resolution chain via the consumer's `DefaultAIProviderFactory.create`
    /// call.
    let withPlatformAIKeyStore (store: IPlatformAIKeyStore) (app: AIServerApp) : AIServerApp = {
        app with
            PlatformKeyStore = Some store
    }

    /// Phase 70 A.5 — additively declare a platform provider on the
    /// `AIServerApp` pipeline. Each call appends to
    /// `app.PlatformProviders`; `composeAI` cross-checks the
    /// accumulated list against `factory.PlatformDescriptors` at
    /// compose time and fails with a clear diagnostic when they
    /// diverge (the consumer wired a provider via the builder but
    /// passed a different list to `DefaultAIProviderFactory.create`).
    ///
    /// **Today.** The consumer still constructs the factory directly
    /// — this builder is a *declaration* helper, not a *construction*
    /// helper. The validator catches the common drift between the
    /// declared shape and the factory's actual shape.
    ///
    /// **Future.** When `composeAI` takes over factory construction
    /// (deferred per Phase 70 A.5 — no consumer migration target has
    /// surfaced), this list becomes the direct input to
    /// `DefaultAIProviderFactory.create`. The builder's call shape and
    /// semantics stay byte-identical across that transition; consumers
    /// adopting the builder today are forward-compatible.
    let withPlatformProvider (provider: DefaultAIProviderFactory.AIPlatformProvider) (app: AIServerApp) : AIServerApp = {
        app with
            PlatformProviders = app.PlatformProviders @ [ provider ]
    }

    /// Drive the final composition. Returns the process exit code.
    /// Phase 1h — implementation is now `composeAI >> ServerApp.run`;
    /// consumers needing to stack AI with Forms / RAG companions on
    /// one composition root use the additive `AICompose.withAI`
    /// extension instead.
    let run (app: AIServerApp) : int = composeAI app |> ServerApp.run

// ─── Additive companion-set extension `withAI` (Phase 1h) ───────────
//
// Stack AI contributions onto an existing `ServerApp` pipeline
// alongside Forms / RAG / future companions, without forcing the
// deployment to commit to `AIServerApp.run` as the terminal call.

/// Phase 1h — stack the AI assistant onto an existing `ServerApp`
/// pipeline. Consumes the required AI dependencies (`factory` +
/// `providerProfile`) and a `configure` function that builds AI-
/// specific state (assistant config, module AI contexts) on a fresh
/// `AIServerApp` whose `Base` is the input `ServerApp`.
///
/// The configurator should call only AI-specific helpers
/// (`AIServerApp.withAIConfig` / `withModuleAIContexts`); the
/// delegating helpers (`withConfig` / `withAuth` / …) exist on
/// `AIServerApp` for backcompat but calling them inside the
/// configurator overwrites the base `ServerApp`'s existing
/// configuration. Set base configuration on the outer pipeline before
/// calling `withAI`.
///
/// Calling `withAI` twice on the same pipeline re-appends
/// `AILatencyMetrics.registrations` and re-registers AI's DI services;
/// the Phase 1h conflict validator (task 4) surfaces this at compose
/// time.
///
/// Example — Forms + AI in one composition root:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> FormsCompose.withForms (fun f ->
///         f |> FormsServerApp.withFormSchema mySchema)
///     |> AICompose.withAI factory providerProfile (fun ai ->
///         ai |> AIServerApp.withAIConfig assistant)
///     |> ServerApp.run
let withAI
    (factory: IAIProviderFactory)
    (providerProfile: IProviderProfile)
    (configure: AIServerApp -> AIServerApp)
    (app: ServerApp)
    : ServerApp =
    AIServerApp.createFrom factory providerProfile app |> configure |> composeAI