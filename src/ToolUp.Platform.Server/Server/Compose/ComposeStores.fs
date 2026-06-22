module ToolUp.Platform.ComposeStores

open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.Tracing

// ─── compose phase: state stores ─────────────────────────────────────
//
// Conditional state-store registrations (`IEntityStore`,
// `IResultStore`, `ILineageStore`, `IConversationStore`,
// `IConfigStore`, `IModuleQueryBus`, `IFeatureFlagStore`,
// `FlagEvaluator`). Extracted from `compose` for the per-concern
// subdivision (Phase 15e follow-up tail). Each helper takes the
// substrate values its inline definition captured and returns either
// `unit` or the constructed substrate when downstream code needs it.
// Zero behaviour change.

/// Phase 7 / 7a — construct `IDataObjectStore` over the resolved
/// `IBlobStorage` and the `IDataCatalog` over the supplied
/// `(moduleName, DataType)` registrations. The catalog snapshots
/// produce-side context (schema, producer module, object lists);
/// `ListObjects` delegates to `IDataObjectStore`. Returned as a tuple
/// for the caller to capture for the downstream registration chain.
let buildDataObjectStoreAndCatalog
    (resolvedBlobStorage: IBlobStorage)
    (resolvedLogger: ILogger)
    (dataTypeRegistrations: (string * DataType) list)
    : IDataObjectStore * IDataCatalog =

    let dataObjectStore: IDataObjectStore =
        DataObjectStore.DataObjectStore(resolvedBlobStorage, resolvedLogger) :> _

    let dataCatalogRegistrations: DataCatalog.DataTypeRegistration list =
        dataTypeRegistrations
        |> List.map (fun (m, dt) -> { ModuleName = m; DataType = dt }: DataCatalog.DataTypeRegistration)

    let dataCatalog: IDataCatalog =
        DataCatalog.DataCatalog(dataCatalogRegistrations, dataObjectStore) :> _

    dataObjectStore, dataCatalog

/// Phase 8 / 8a / 53 — construct the optional state-store values
/// (`IResultStore`, `ILineageStore`, `IConversationStore`) per the
/// matching `ServerConfig` modes. The unset defaults return `None`;
/// downstream `registerOptionalStores` skips registration for any
/// `None` so DI resolution returns `null` and consumers must handle
/// absence explicitly. Lineage is constructed before result so the
/// persistent result variant can pick it up for auto-emit.
let buildOptionalStoreValues
    (config: ServerConfig)
    (eventStore: IEventStore)
    (dataObjectStore: IDataObjectStore)
    : ILineageStore option * IResultStore option * IConversationStore option =

    let lineageStore: ILineageStore option =
        match config.Lineage with
        | NoLineageStore -> None
        | EnabledLineageStore -> Some(LineageStore.EventStoreLineageStore(eventStore) :> _)

    let resultStore: IResultStore option =
        match config.ResultStore with
        | NoResultStore -> None
        | InMemoryResultStore ->
            Some(ResultStore.InMemoryResultStore(eventStore = eventStore, ?lineageStore = lineageStore) :> _)
        | PersistentResultStore ->
            Some(
                ResultStore.PersistentResultStore(
                    dataObjectStore,
                    eventStore = eventStore,
                    ?lineageStore = lineageStore
                )
                :> _
            )

    let conversationStore: IConversationStore option =
        match config.ConversationStore with
        | NoConversationStore -> None
        | EnabledConversationStore _ ->
            Some(ConversationStore.PersistentConversationStore(dataObjectStore, eventStore = eventStore) :> _)

    lineageStore, resultStore, conversationStore

/// Phase 19 — register the entity-store substrate when
/// `ServerConfig.EntityStore = EnabledEntityStore`. Constructs the
/// `EntityRegistry`, runs every accumulated registration closure
/// against it, then constructs the `BlobEntityStore` over the
/// resolved `IBlobStorage` and `IDataObjectStore`. `NoEntityStore`
/// (the default) skips both registrations entirely — entity store
/// costs zero runtime when unused.
let registerEntityStore
    (services: IServiceCollection)
    (config: ServerConfig)
    (entityRegistrations: (EntityStore.EntityRegistry -> unit) list)
    : unit =
    match config.EntityStore with
    | EnabledEntityStore ->
        let entityRegistry = EntityStore.EntityRegistry()

        // Phase 26 — when SingleNodeDeployPlane is set, prepend the
        // Tenant entity registration to the iteration list. Saves
        // consumers from having to call ServerApp.withEntity<Tenant>
        // manually when they opt in to the substrate's defaults. The
        // closure runs against the same registry as user-declared
        // registrations; the resulting registry binds all of them
        // into BlobEntityStore below.
        let effectiveRegistrations =
            match config.DeployPlane with
            | SingleNodeDeployPlane ->
                let tenantRegister (registry: EntityStore.EntityRegistry) =
                    registry.Register TenantEntity.Tenant.registration

                tenantRegister :: entityRegistrations
            | NoDeployPlane -> entityRegistrations

        // Phase 159 — when the durable consent store is entity-backed,
        // prepend its `ConsentRecord` registration so the entity store
        // knows the type before any consent write. Same auto-register
        // convenience as the Tenant prepend above; gated on the mode so
        // a deployment not using durable consent pays nothing.
        let effectiveRegistrations =
            match config.ConsentStateStore with
            | EntityBackedConsentStateStore ->
                let consentRegister (registry: EntityStore.EntityRegistry) =
                    registry.Register Consent.ConsentStateStore.ConsentRecord.registration

                consentRegister :: effectiveRegistrations
            | NoConsentStateStore
            | InMemoryConsentStateStore -> effectiveRegistrations

        for registerFn in effectiveRegistrations do
            registerFn entityRegistry

        services.AddSingleton<EntityStore.EntityRegistry>(entityRegistry) |> ignore

        services.AddSingleton<IEntityStore.IEntityStore>(fun (sp: System.IServiceProvider) ->
            let dos = sp.GetService(typeof<IDataObjectStore>) :?> IDataObjectStore
            let bs = sp.GetService(typeof<IBlobStorage>) :?> IBlobStorage
            // IAuditLog is always registered (Phase 9 ensures even
            // NoAuditLog mode resolves to NoOpAuditLog). Wrap in
            // Some/None for the BlobEntityStore constructor's
            // option parameter.
            let auditLogObj = sp.GetService(typeof<IAuditLog>)

            let auditLog =
                if isNull auditLogObj then
                    None
                else
                    Some(auditLogObj :?> IAuditLog)

            EntityStore.BlobEntityStore(dos, bs, entityRegistry, auditLog) :> IEntityStore.IEntityStore)
        |> ignore
    | NoEntityStore -> ()

/// Register the `IColumnMappingStore` substrate when
/// `ServerConfig.ColumnMapping = EnabledColumnMapping`. The default
/// store (`ColumnMappingStore.create`) wraps the resolved
/// `IDataObjectStore`, persisting reusable CSV→schema maps keyed by the
/// source CSV's column-structure fingerprint. `NoColumnMapping` (the
/// default) skips registration — the mapping-aware Data Manager and its
/// `IColumnMappingApi` route are not mounted, so the store is never
/// resolved and costs zero at runtime.
let registerColumnMappingStore (services: IServiceCollection) (config: ServerConfig) : unit =
    match config.ColumnMapping with
    | EnabledColumnMapping ->
        services.AddSingleton<IConversionStore>(fun (sp: System.IServiceProvider) ->
            let dos = sp.GetService(typeof<IDataObjectStore>) :?> IDataObjectStore

            let logger =
                match sp.GetService(typeof<ILogger>) with
                | :? ILogger as l -> Some l
                | _ -> None

            ColumnMappingStore.create dos logger)
        |> ignore
    | NoColumnMapping -> ()

/// Phase 159 — register the durable per-subject `IConsentStateStore`
/// when `ServerConfig.ConsentStateStore` opts in. `NoConsentStateStore`
/// (default) registers nothing — consent state lives only in the
/// browser via `IConsentProvider`, zero runtime cost (GP 13).
/// `InMemoryConsentStateStore` registers the dev-only single-instance
/// store. `EntityBackedConsentStateStore` registers the durable store
/// over the already-registered `IEntityStore` (+ `IAuditLog` for the
/// `Custom:ConsentGranted` / `Custom:ConsentWithdrawn` emission); the
/// `ConsentRecord` entity type is registered by `registerEntityStore`
/// under the same flag. Requires `EntityStore = EnabledEntityStore` —
/// the factory resolves `null` for `IEntityStore` otherwise and fails
/// loudly at first resolve.
let registerConsentStateStore (services: IServiceCollection) (config: ServerConfig) : unit =
    let resolveAuditLog (sp: System.IServiceProvider) : IAuditLog option =
        match sp.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as log -> Some log
        | _ -> None

    match config.ConsentStateStore with
    | NoConsentStateStore -> ()
    | InMemoryConsentStateStore ->
        services.AddSingleton<Consent.ConsentStateStore.IConsentStateStore>(fun (sp: System.IServiceProvider) ->
            Consent.ConsentStateStore.ConsentStateStore.inMemory (resolveAuditLog sp))
        |> ignore
    | EntityBackedConsentStateStore ->
        services.AddSingleton<Consent.ConsentStateStore.IConsentStateStore>(fun (sp: System.IServiceProvider) ->
            let entityStore =
                sp.GetService(typeof<IEntityStore.IEntityStore>) :?> IEntityStore.IEntityStore

            Consent.ConsentStateStore.ConsentStateStore.entityBacked entityStore (resolveAuditLog sp))
        |> ignore

/// Phase 8 / 8a / 53 — conditional store registrations. `IResultStore`
/// is registered only when `ServerConfig.ResultStore` opts in;
/// `ILineageStore` only when `ServerConfig.Lineage = EnabledLineageStore`;
/// `IConversationStore` (+ role interfaces + DSR erasure handler) only
/// when `ServerConfig.ConversationStore = EnabledConversationStore _`.
/// The unset defaults leave DI unaware so resolution returns `null`
/// and downstream consumers must handle absence explicitly.
let registerOptionalStores
    (services: IServiceCollection)
    (config: ServerConfig)
    (resultStore: IResultStore option)
    (lineageStore: ILineageStore option)
    (conversationStore: IConversationStore option)
    : unit =

    // Phase 8 conditional registration.
    match resultStore with
    | Some store -> services.AddSingleton<IResultStore>(store) |> ignore
    | None -> ()

    // Phase 8a conditional registration.
    match lineageStore with
    | Some store -> services.AddSingleton<ILineageStore>(store) |> ignore
    | None -> ()

    // Phase 53 conditional registration. `IConversationStore` is
    // registered only when `ServerConfig.ConversationStore =
    // EnabledConversationStore _`. The substrate also registers each
    // role interface separately so consumers can depend on the
    // narrowest interface they need without taking a transitive dep on
    // the others. When DSR is also enabled (`DataSubjectRequests =
    // Enabled _`), the conversation store contributes an
    // `IErasureHandler` via `ConversationEraseHandler.erasureHandler`
    // AND an `IDataExporter` via `ConversationExporter.exporter` —
    // the DSR pipeline picks both up automatically alongside the
    // other store handlers / exporters.
    match conversationStore with
    | Some store ->
        services.AddSingleton<IConversationStore>(store) |> ignore

        services.AddSingleton<IConversationReader>(store :> IConversationReader)
        |> ignore

        services.AddSingleton<IConversationWriter>(store :> IConversationWriter)
        |> ignore

        services.AddSingleton<IConversationEraser>(store :> IConversationEraser)
        |> ignore

        match config.DataSubjectRequests with
        | DataSubjectRequestMode.Enabled _ ->
            services.AddSingleton<IErasureHandler>(ConversationEraseHandler.erasureHandler store)
            |> ignore

            services.AddSingleton<IDataExporter>(ConversationExporter.exporter (store :> IConversationReader))
            |> ignore
        | DataSubjectRequestMode.Disabled -> ()
    | None -> ()

/// Phase 5a per-team (and per-user) configuration store, Phase 6b
/// module-to-module query bus, Phase 5c feature-flag store +
/// evaluator. `IConfigStore` is registered unconditionally —
/// `ConfigHandler` resolves the caller's scope per request and
/// short-circuits when no scope is available (Anonymous mode); the
/// instance is supplied by the caller (it was constructed pre-DI so
/// the Phase 6f transactional dispatcher and DI hand out the same
/// instance). `IModuleQueryBus` registry is built once from the
/// accumulated `(moduleName, handler)` pairs; the bus is stateless,
/// safe as a singleton. `IFeatureFlagStore` + `FlagEvaluator` are
/// always registered (declared = [] still produces a working
/// evaluator).
let registerConfigQueryAndFlagStores
    (services: IServiceCollection)
    (config: ServerConfig)
    (configStoreInstance: IConfigStore)
    (queryHandlers: (string * ModuleQueryHandler) list)
    (resolvedBlobStorage: IBlobStorage)
    (resolvedLogger: ILogger)
    (resolvedActivitySink: IActivitySink)
    : unit =

    services.AddSingleton<IConfigStore>(configStoreInstance) |> ignore

    let moduleQueryBus: IModuleQueryBus =
        let registry = ModuleQueryBus.buildRegistry queryHandlers
        ModuleQueryBus.InMemoryModuleQueryBus(registry, resolvedLogger, resolvedActivitySink) :> _

    services.AddSingleton<IModuleQueryBus>(moduleQueryBus) |> ignore

    let featureFlagStore =
        FeatureFlagStore.BlobFeatureFlagStore(resolvedBlobStorage) :> IFeatureFlagStore

    let flagEvaluator =
        FlagEvaluator.create featureFlagStore config.FeatureFlags (Some resolvedLogger)

    services.AddSingleton<IFeatureFlagStore>(featureFlagStore).AddSingleton<FlagEvaluator.FlagEvaluator>(flagEvaluator)
    |> ignore

/// Phase 26 — register the Layer 3 deploy-plane substrate when
/// `ServerConfig.DeployPlane = SingleNodeDeployPlane`. Wires three
/// singletons (`IBuildOrchestrator` → `JobSchedulerBuildOrchestrator`,
/// `IDeployPipeline` → `DefaultDeployPipeline`, `ITenantFleet` →
/// `EntityStoreTenantFleet`). `IContainerScheduler` is
/// **consumer-supplied** — operators register a backend (the dev-grade
/// `DockerLocalContainerScheduler` reference companion, or a
/// cloud-specific impl) before composing. `NoDeployPlane` (default)
/// skips every registration — the deploy plane costs zero runtime when
/// unused (GP 13).
///
/// **Resolution-time validation (defense-in-depth).** The factories
/// check for the required dependencies (`IJobScheduler` for the
/// orchestrator; `IContainerScheduler` for the pipeline and fleet;
/// `IEntityStore` for the fleet) and raise at first-resolve with a clear
/// remediation message rather than letting DI return `null`. These
/// first-resolve guards are now backstopped by `DeployPlaneDepsValidator`
/// (registered in `ComposeConfigValidators`), which surfaces the same
/// missing-dependency conditions at compose-time preflight as a `Warning`
/// — so a misconfigured deploy plane is visible at startup rather than
/// only at the first request that 500s. It is a Warning (not an abort)
/// because these services are lazy factories and a deployment may use only
/// a subset; the `failwith`s here remain the usage-accurate hard gate.
let registerDeployPlane
    (services: IServiceCollection)
    (config: ServerConfig)
    (eventStore: IEventStore)
    (resolvedLogger: ILogger)
    : unit =
    match config.DeployPlane with
    | NoDeployPlane -> ()
    | SingleNodeDeployPlane ->
        services.AddSingleton<IBuildOrchestrator>(fun (sp: System.IServiceProvider) ->
            let scheduler = sp.GetService(typeof<IJobScheduler>) :?> IJobScheduler

            if isNull (box scheduler) then
                failwith
                    "SingleNodeDeployPlane requires JobScheduler = InProcessJobScheduler (or a distributed scheduler companion) — register an IJobScheduler before consuming IBuildOrchestrator."

            JobSchedulerBuildOrchestrator.JobSchedulerBuildOrchestrator(scheduler, eventStore, resolvedLogger)
            :> IBuildOrchestrator)
        |> ignore

        services.AddSingleton<IDeployPipeline>(fun (sp: System.IServiceProvider) ->
            let buildOrch = sp.GetService(typeof<IBuildOrchestrator>) :?> IBuildOrchestrator

            let containerSched =
                sp.GetService(typeof<IContainerScheduler>) :?> IContainerScheduler

            if isNull (box containerSched) then
                failwith
                    "SingleNodeDeployPlane requires IContainerScheduler to be registered by a consumer companion (e.g. DockerLocalContainerScheduler from src/ContainerSchedulers/DockerLocal/, or a cloud-specific impl). The SDK does not ship a default — operators wire a backend separately via services.AddSingleton<IContainerScheduler>(...)."

            DefaultDeployPipeline.DefaultDeployPipeline(buildOrch, containerSched, eventStore, resolvedLogger)
            :> IDeployPipeline)
        |> ignore

        services.AddSingleton<ITenantFleet>(fun (sp: System.IServiceProvider) ->
            let entityStore =
                sp.GetService(typeof<IEntityStore.IEntityStore>) :?> IEntityStore.IEntityStore

            if isNull (box entityStore) then
                failwith
                    "SingleNodeDeployPlane requires EntityStore = EnabledEntityStore — the Tenant catalog lives behind IEntityStore. Set ServerConfig.EntityStore = EnabledEntityStore."

            let containerSched =
                sp.GetService(typeof<IContainerScheduler>) :?> IContainerScheduler

            if isNull (box containerSched) then
                failwith
                    "SingleNodeDeployPlane requires IContainerScheduler to be registered by a consumer companion before consuming ITenantFleet."

            let pipeline = sp.GetService(typeof<IDeployPipeline>) :?> IDeployPipeline

            EntityStoreTenantFleet(entityStore, containerSched, pipeline, resolvedLogger) :> ITenantFleet)
        |> ignore