module ToolUp.Platform.ComposeDevDiagnostics

open Giraffe
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── compose phase: dev-diagnostics capture ──────────────────────────
//
// Phase 9a — capture the `IServiceCollection` descriptors before
// `Build()`. After `Build()` the collection is sealed, so the descriptor
// snapshot has to happen here. Same pass also builds per-module
// summaries from the flat `dataTypeRegistrations` and `config.ModuleConfigs`
// lists. The capture is small (~few KB) and built unconditionally; the
// runtime gate at `devDiagnosticsRoutes` is what keeps the report off
// the wire in production. The previous compile-time `#if DEBUG` block
// was removed when `ToolUp.Platform` stopped carrying compile-time
// gates.
//
// Extracted from `compose` for the per-concern subdivision (Phase 15e
// follow-up). Takes the exact substrate values the inline definition
// captured and returns the same shape. Zero behaviour change.

let buildDevDiagnosticsCapture
    (config: ServerConfig)
    (dataTypeRegistrations: (string * DataType) list)
    (handlers: HttpHandler list)
    (extensions: ComposeExtensions)
    (effectiveNotifications: NotificationMode)
    (persistentEventStoreInstance: PersistentEventStore.PersistentEventStore option ref)
    (blobJobStoreInstance: JobStore.BlobJobStore option ref)
    (services: IServiceCollection)
    : DevDiagnosticsHandler.DevDiagnosticsCapture =

    let moduleSnapshots =
        config.ModuleNames
        |> List.map (fun name ->
            let dataTypeIds =
                dataTypeRegistrations
                |> List.filter (fun (m, _) -> m = name)
                |> List.map (fun (_, dt) -> dt.Info.Id)

            let hasConfig = config.ModuleConfigs |> List.exists (fun c -> c.ModuleKey = name)

            ({
                Name = name
                DataTypeIds = dataTypeIds
                HasConfigSchema = hasConfig
            }
            : DevDiagnosticsHandler.ModuleSnapshot))

    let inspectors = [
        match persistentEventStoreInstance.Value with
        | Some s -> yield (fun (scopeId: string) -> s.IndexConsistencyCheck(scopeId, 20))
        | None -> ()

        match blobJobStoreInstance.Value with
        | Some s -> yield (fun (scopeId: string) -> s.IndexConsistencyCheck(scopeId, 20))
        | None -> ()
    ]

    // Phase 1g — composition audit panel. Each entry names a gateable
    // feature, the resolved mode (after auto-detection), whether it's
    // currently active in this deployment, and the `ServerConfig` field
    // a deployment uses to change it. Single source of truth for
    // "what's running?" diagnostics.
    let lightweightFeatures: DevDiagnosticsHandler.LightweightFeatureEntry list =
        let entry
            (feature: string)
            (modeText: string)
            (active: bool)
            (configPath: string)
            : DevDiagnosticsHandler.LightweightFeatureEntry =
            {
                Feature = feature
                Mode = modeText
                Active = active
                ConfigPath = configPath
            }

        let webhookMode =
            match config.Webhooks with
            | NoWebhooks -> "NoWebhooks"
            | EnabledWebhooks -> "EnabledWebhooks"

        let auditMode =
            match config.AuditLog with
            | NoAuditLog -> "NoAuditLog"
            | EnabledAuditLog -> "EnabledAuditLog"

        let notificationsMode, notificationsActive =
            match config.Notifications, effectiveNotifications with
            | NotificationsAuto, InMemoryNotifications -> "InMemoryNotifications (auto)", true
            | NotificationsAuto, NoNotifications -> "NoNotifications (auto)", false
            | NotificationsAuto, _ -> "NotificationsAuto", true
            | NoNotifications, _ -> "NoNotifications", false
            | NoNotificationsExplicit, _ -> "NoNotificationsExplicit", false
            | InMemoryNotifications, _ -> "InMemoryNotifications", true
            | RedisNotifications _, _ -> "RedisNotifications", true

        let eventStoreMode =
            match config.EventStore with
            | InMemoryOnly -> "InMemoryOnly"
            | PersistentBlobBacked _ -> "PersistentBlobBacked"

        let jobMode =
            match config.JobScheduler with
            | NoJobScheduler -> "NoJobScheduler"
            | InProcessJobScheduler -> "InProcessJobScheduler"

        let resultMode =
            match config.ResultStore with
            | NoResultStore -> "NoResultStore"
            | InMemoryResultStore -> "InMemoryResultStore"
            | PersistentResultStore -> "PersistentResultStore"

        let lineageMode =
            match config.Lineage with
            | NoLineageStore -> "NoLineageStore"
            | EnabledLineageStore -> "EnabledLineageStore"

        let ingestionMode =
            match config.DataIngestion with
            | NoDataIngestion -> "NoDataIngestion"
            | EnabledDataIngestion -> "EnabledDataIngestion"

        let rateLimitActive = RateLimitConfig.isEnabled config.RateLimit

        [
            entry "Webhooks" webhookMode (config.Webhooks = EnabledWebhooks) "ServerConfig.Webhooks"
            entry "AuditLog" auditMode (config.AuditLog = EnabledAuditLog) "ServerConfig.AuditLog"
            entry "Notifications" notificationsMode notificationsActive "ServerConfig.Notifications"
            entry "EventStore" eventStoreMode (config.EventStore <> InMemoryOnly) "ServerConfig.EventStore"
            entry "JobScheduler" jobMode (config.JobScheduler <> NoJobScheduler) "ServerConfig.JobScheduler"
            entry "ResultStore" resultMode (config.ResultStore <> NoResultStore) "ServerConfig.ResultStore"
            entry "Lineage" lineageMode (config.Lineage <> NoLineageStore) "ServerConfig.Lineage"
            entry "DataIngestion" ingestionMode (config.DataIngestion <> NoDataIngestion) "ServerConfig.DataIngestion"
            entry
                "RateLimit"
                (if rateLimitActive then "Enabled" else "Disabled")
                rateLimitActive
                "ServerConfig.RateLimit"
            entry
                "DevEndpoints"
                (if config.EnableDevEndpoints then "Enabled" else "Disabled")
                config.EnableDevEndpoints
                "ServerConfig.EnableDevEndpoints"
        ]

    // Phase 1f composition-seam summary. Counts are read from the
    // `extensions` record we received; security-header keys are copied
    // verbatim, values stay in `config.SecurityHeaders` and are not
    // surfaced through the dev report (CSP / Permissions-Policy values
    // may carry deployment-private data).
    let compositionSeam: DevDiagnosticsHandler.CompositionSeamSummary = {
        PreMiddlewareCount = extensions.PreMiddleware.Length
        PostMiddlewareCount = extensions.PostMiddleware.Length
        SecurityHeaderKeys = config.SecurityHeaders |> Map.toList |> List.map fst
        CorsConfigured = config.Cors.IsSome
        NotificationConsumers = extensions.NotificationConsumers
    }

    {
        Modules = moduleSnapshots
        Services = DevDiagnosticsHandler.snapshotServices services
        TotalRouteHandlers = handlers.Length
        IndexInspectors = inspectors
        LightweightFeatures = lightweightFeatures
        CompositionSeam = compositionSeam
    }