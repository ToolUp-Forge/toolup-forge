module ToolUp.Platform.BuildRouteHandlers

open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.DataSubjectRequestApiHandler
open ToolUp.Platform.FileManagement
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.PlatformApiHandler
open ToolUp.Platform.PlatformSchema
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Teams
open ToolUp.Platform.Usage

// ─── compose phase: route handlers ───────────────────────────────────
//
// The `router` thunk and the `devDiagnosticsRoutes` builder, returned
// for `compose` to mount once `app` is built. The block is pure list
// construction (no DI side effects); the two store-instance `ref`s are
// captured by the MaintenanceApi thunks and populated later by the
// infra/scheduler builders.
type RouteHandlers = {
    Router: HttpHandler list -> HttpHandler
    DevDiagnosticsRoutes: DevDiagnosticsHandler.DevDiagnosticsCapture -> HttpHandler list
}

// 0.5.14 — module-level resolver. Surfaces unregistered services as a
// named-type Exception instead of the generic NullReferenceException
// the F# `:?>` cast emits when boxing-then-UnboxGeneric a null. F#
// inner-let bindings can't carry explicit type parameters, so this
// lives at module scope.
let inline private requireService<'t> (ctx: HttpContext) : 't =
    match ctx.RequestServices.GetService(typeof<'t>) with
    | null ->
        failwithf
            "Required DI service '%s' is not registered. Check the consumer's \
             ServerConfig composition — the SDK auto-injects ITeamStore + \
             IShareTokenStore + IAuditLog by default, but a consumer override may \
             have removed one."
            typeof<'t>.FullName
    | svc -> svc :?> 't

let buildRouteHandlers
    (config: ServerConfig)
    (handlers: HttpHandler list)
    (dataTypes: DataType list)
    (extensions: ComposeExtensions)
    (transactionalSinks: INotificationSink list)
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    (effectiveNotifications: NotificationMode)
    (persistentEventStoreInstance: PersistentEventStore.PersistentEventStore option ref)
    (blobJobStoreInstance: JobStore.BlobJobStore option ref)
    : RouteHandlers =

    let fileManagementHandler =
        if dataTypes.IsEmpty then
            []
        else
            [ makeApi FileManagement.fileManagementApi ]

    // Configuration API is always auto-injected; the handler itself
    // short-circuits on empty `ModuleConfigs` (ListModules returns
    // []) and on Anonymous mode (no config scope), so registering it
    // unconditionally is zero-cost when unused. The SDK's default
    // `_platform` schema is merged in here so `currencySymbol` (and
    // any future platform-level field) is visible in the admin UI
    // without every app having to declare it. When any transactional
    // sink is registered, the SDK-shipped `_platform.notification_prefs`
    // schema is merged in too so the admin UI surfaces team-wide kill
    // switches for email / SMS / push without each app re-declaring
    // the schema.
    let mergedModuleConfigs =
        let withPlatform =
            if config.IncludePlatformDefaults then
                mergePlatformSchema config.ModuleConfigs
            else
                // Opt-out path: skip the SDK-shipped _platform schema
                // prepend. Apps that opt out and supply their own
                // `_platform` entry get plain pass-through (no merge
                // with the SDK default fields).
                config.ModuleConfigs

        let withPrefs =
            if List.isEmpty transactionalSinks then
                withPlatform
            else
                mergeNotificationPrefsSchema withPlatform

        // When usage metering is enabled, the admin UI surfaces the
        // per-team quota tab without requiring every app to re-declare
        // the schema. NoUsageMetering deployments skip the merge so the
        // tab does not appear at all.
        match config.UsageMetering with
        | NoUsageMetering -> withPrefs
        | EnabledUsageMetering -> mergeUsageSchema withPrefs

    let configHandler = [ makeApi (ConfigHandler.configApi mergedModuleConfigs) ]

    // Feature-flag API is always auto-injected; the handler short-
    // circuits on empty `FeatureFlags` (returns an empty map) so
    // registering unconditionally is zero-cost when unused.
    let featureFlagHandler = [ makeApi (FeatureFlagHandler.featureFlagApi config.FeatureFlags) ]

    // Webhook admin API. Mounted only when the deployment opted in via
    // `ServerConfig.Webhooks = EnabledWebhooks`; the lightweight
    // `NoWebhooks` default skips the route entirely so the proxy
    // surface returns 404 and no `IWebhookRegistry` is resolved.
    let webhookHandler: HttpHandler list =
        match config.Webhooks with
        | NoWebhooks -> []
        | EnabledWebhooks -> [ makeApi WebhookApiHandler.webhookApi ]

    // Cross-module query bus API. Auto-injected so client-side modules
    // can reach the bus without extra wiring. The server resolves
    // `AccessContext` from DI per request and forwards to the
    // in-process `IModuleQueryBus`; permission checks happen inside the
    // bus (not here) so the same `Ask` path covers both client→server
    // and server→server calls. No permission guard at the HTTP layer
    // because the bus returns `PermissionDenied` as a typed error in
    // the response body — clients branch on it without parsing a 403.
    let moduleQueryBusHandler: HttpHandler list = [
        makeApi (fun (ctx: HttpContext) ->
            let bus =
                ctx.RequestServices.GetService(typeof<IModuleQueryBus>) :?> IModuleQueryBus

            let accessContext =
                match ctx.RequestServices.GetService(typeof<AccessContext>) with
                | :? AccessContext as ac -> ac
                | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

            {
                Ask = fun request -> bus.Ask(accessContext, request)
            }
            : IModuleQueryBusApi)
    ]

    // Generic real-time notification SSE endpoint. Resolves the channel
    // and the shared `SSEConnectionManager` from DI per request so that
    // app-replaced `INotificationChannel` implementations (Redis, NATS,
    // future Orleans / Akka) plug in without touching the route.
    //
    // Skipped entirely when `effectiveNotifications = NoNotifications`
    // so the lightweight default mounts no SSE endpoint. The DI
    // registration falls through to the no-op channel (below) so
    // `TeamStore` / `JobScheduler` constructors still work even though
    // no client can subscribe.
    let notificationRoutes: HttpHandler list =
        match effectiveNotifications with
        | NoNotifications
        | NoNotificationsExplicit -> []
        | _ -> [
            route "/api/notifications"
            >=> fun next ctx ->
                let channel =
                    ctx.RequestServices.GetService(typeof<INotificationChannel>) :?> INotificationChannel

                let manager =
                    ctx.RequestServices.GetService(typeof<SSEConnectionManager>) :?> SSEConnectionManager

                // Phase 117 — SseAuthMode threads through so the handler's
                // shared scope resolution can refuse unauthenticated
                // connects under CookieRequired instead of trusting a
                // client-supplied ?userId=.
                NotificationHandler.notificationHandler channel manager config.SseAuthMode next ctx
          ]

    // Dev diagnostics endpoint (`/dev/inspect`). Runtime-gated via
    // `ServerConfig.EnableDevEndpoints` (default `false`); deployments
    // that don't opt in get a clean 404 from the Giraffe terminal
    // middleware. The capture (module snapshots, service descriptor
    // list, total handler counts) is built later, after `services` is
    // fully populated but before `app = builder.Build()`.
    //
    // The Phase 9n `/dev/bundle` endpoint composes the same capture
    // (plus the audit log + event store + preflight snapshot + health
    // probes resolved per-request) into a one-shot tar archive.
    // Appended here so the same `EnableDevEndpoints` gate covers both
    // surfaces — no second runtime config flag for what is the same
    // operator workflow.
    let devDiagnosticsRoutes (capture: DevDiagnosticsHandler.DevDiagnosticsCapture) : HttpHandler list =
        if config.EnableDevEndpoints then
            DevDiagnosticsHandler.routes config capture
            @ [ DiagnosticBundleHandler.route config capture ]
            // Phase 120 — /dev/auth-denials rollup under the same gate.
            @ [ AuthDenialsDiagnosticsHandler.route ]
        else
            []

    // Encryption admin handler (POST
    // /api/_platform/encryption/destroy-scope-key/{scopeId}). Mounted
    // only when an `IBlobEncryptionKeyResolver` is registered. The
    // handler internally checks whether the resolver supports per-scope
    // destruction (only `PerScopeKeyResolver` does); other resolvers
    // return 400 with a clear message. Token-gated via
    // `TOOLUP_ADMIN_TOKEN` env var so this surface is locked down even
    // on deployments that opt into encryption.
    let encryptionAdminHandler =
        match encryptionKeyResolver with
        | Some _ -> EncryptionAdminHandler.routes
        | None -> []

    // Job API auto-injected when the scheduler is enabled.
    // `NoJobScheduler` deployments skip the route entirely; clients
    // calling the JobApi proxy on such a deployment receive a 404
    // from the Giraffe terminal middleware (the proxy surface is
    // optional, callers tolerate absence).
    let jobApiHandler: HttpHandler list =
        match config.JobScheduler with
        | NoJobScheduler -> []
        | InProcessJobScheduler -> [ makeApi JobApiHandler.jobApi ]

    // MaintenanceApi always registered. Each method reads the
    // typed-store cells at request time via the thunk arguments and
    // returns a clear error when the corresponding store mode is
    // disabled. Owner/Admin gated server-side.
    let maintenanceApiHandler: HttpHandler list = [
        makeApi (
            MaintenanceApiHandler.maintenanceApi
                (fun () -> persistentEventStoreInstance.Value |> Option.map _.Rebuild)
                (fun () -> blobJobStoreInstance.Value |> Option.map _.Rebuild)
        )
    ]

    // Data ingestion API auto-injected when the substrate is enabled.
    // Mirrors the JobApi route's opt-in shape — disabled deployments
    // return 404 to the proxy surface.
    let dataIngestionApiHandler: HttpHandler list =
        match config.DataIngestion with
        | NoDataIngestion -> []
        | EnabledDataIngestion -> [ makeApi DataIngestionApiHandler.dataIngestionApi ]

    // OAuth Authorization Code endpoints. Same opt-in shape as the
    // data-ingestion API; disabled deployments produce an empty handler
    // list so /api/oauth/* paths 404 from the Giraffe terminal
    // middleware. Companion `IOAuthCredentialFlow` implementations are
    // resolved per-request inside the handler by URL `{flowName}`
    // segment, so no route construction here depends on the flow
    // registration order.
    let oauthFlowRoutes: HttpHandler list =
        match config.DataIngestion with
        | NoDataIngestion -> []
        | EnabledDataIngestion -> OAuthFlowHandler.routes

    // HealthMonitor admin API. Always auto-injected; request-scoped
    // Owner/Admin gate inside the handler short-circuits Anonymous and
    // Member-role callers, so unconditional registration is zero-cost
    // when the deployment doesn't enable the HealthMonitor sidebar
    // entry. The handler resolves the per-request `AccessContext` from
    // `HttpContext.Items` (Subject-first model), so no deployment-wide
    // mode needs to be closed over here.
    let healthMonitorApiHandler: HttpHandler list = [ makeApi HealthMonitorApiHandler.healthMonitorApi ]

    // Phase 9p.A — ServiceStatusBoard composite admin API. Always
    // auto-injected; request-scoped Platform-Admin gate inside the
    // handler short-circuits non-admin callers, so unconditional
    // registration is zero-cost when the deployment doesn't enable the
    // ServiceStatusBoard sidebar entry. The whole `ServerConfig` is
    // closed over so the handler reads the per-section mode fields
    // (`JobScheduler` / `RateLimiter` / `ConfigDriftDetection` /
    // `SmokeTest`) at compose time rather than re-resolving them per
    // request.
    let serviceStatusBoardApiHandler: HttpHandler list = [
        makeApi (ServiceStatusBoardApiHandler.serviceStatusBoardApi config)
    ]

    // Usage admin route. Auto-injected unconditionally so the client
    // dashboard's `IUsageQueryApi` proxy never 404s in mode-mismatched
    // deployments. The default `ClientConfig.UsageDashboard =
    // DefaultUsageDashboard` renders the sidebar entry in any
    // non-Anonymous mode; the default `ServerConfig.UsageMetering =
    // NoUsageMetering` resolves `IUsageLog` to `NoOpUsageLog` so the
    // handler returns empty results — admin UI shows the "no usage
    // records" state until a deployment opts in to
    // `EnabledUsageMetering`. Owner / Admin gating is enforced inside
    // the handler (`ensureReadAllowed`). Route shape:
    // `/api/_platform/usage/*` via the shared `UsageQueryApi.routeBuilder`
    // so admin clients can discover the endpoint by path alone.
    let usageQueryApiHandler: HttpHandler list = [
        Api.make (UsageQueryApiHandler.usageQueryApi, routeBuilder = UsageQueryApi.routeBuilder)
    ]

    // Phase 171 — Home / Overview landing route. Auto-injected
    // unconditionally so the client Home module's `IHomeOverviewApi`
    // proxy never 404s; the module itself is opt-in client-side
    // (`ClientConfig.HomeModule = EnabledHomeModule`), so a deployment
    // that doesn't enable it simply never calls this route (GP 13).
    // Scope + `RequiresClaim "scope"` gating is enforced by the
    // dispatcher + handler. Route shape: `/api/_platform/home/*` via
    // the shared `HomeOverviewApi.routeBuilder`.
    let homeOverviewApiHandler: HttpHandler list = [
        Api.make (HomeOverviewApiHandler.homeOverviewApi, routeBuilder = HomeOverviewApi.routeBuilder)
    ]

    // /metrics route. Mounted only when EnabledMetricsEndpoint.
    // NoMetricsEndpoint produces an empty list so the route does not
    // exist on the routing table; deployments without metrics enabled
    // get a clean 404 from the Giraffe terminal middleware, not an
    // empty 200 response. The handler itself resolves
    // PrometheusMetricsSink from DI per-request so route construction
    // here doesn't depend on the sink-registration block which lives
    // later in compose.
    let metricsRoutes: HttpHandler list =
        match config.MetricsEndpoint with
        | EnabledMetricsEndpoint -> MetricsEndpoint.routes
        | NoMetricsEndpoint -> []

    let platformAdminApiHandler: HttpHandler list = [ makeApi PlatformAdminApiHandler.platformAdminApi ]

    // 0.5.7 — IUserDirectoryApi mount. Always-on so the client typeahead
    // resolves at runtime regardless of whether a directory companion
    // is wired; the handler short-circuits to Ok [] when DI has no
    // IUserDirectory registered, so deployments without a companion pay
    // a single per-request DI miss and return an empty suggestion list.
    let userDirectoryApiHandler: HttpHandler list = [ makeApi UserDirectoryApiHandler.userDirectoryApi ]

    // Phase 3d — ITeamInviteApi mount. Auto-injected unconditionally
    // so the client `Api.makeProxy<ITeamInviteApi>` (used by the
    // `/invite/{token}` accept page and `TeamManagerUI`'s invite UI)
    // resolves at runtime. The handler itself gates on Owner/Admin
    // for issue/revoke/list and on authenticated-not-anonymous for
    // accept; Anonymous-mode callers reach the route via the existing
    // `AuthEnforcementMiddleware` carve-out so the accept page can
    // surface a "Sign in to accept" UI when the visitor is not yet
    // authenticated. Resolves per-request: ITeamStore, IShareTokenStore,
    // IAuditLog, ServerConfig (for PublicBaseUrl) all flow through DI.
    let teamInvitationApiHandler: HttpHandler list = [
        Api.make (
            (fun (ctx: HttpContext) ->
                let shareTokenStore = requireService<IShareTokenStore> ctx
                let teamStore = requireService<ITeamStore> ctx
                let auditLog = requireService<IAuditLog> ctx

                TeamInvitationHandler.teamInvitationApi shareTokenStore teamStore auditLog config ctx),
            routeBuilder = TeamInviteApi.routeBuilder
        )
    ]

    // Phase 9j — `GET /api/csrf-token`. Mounted only when hardening is
    // opted in; `NoSecurityHardening` produces an empty list so the
    // path 404s from the Giraffe terminal middleware (the client
    // pre-fetch tolerates the absence — its cache stays empty and no
    // header is attached, preserving today's behaviour).
    let csrfTokenRoutes: HttpHandler list =
        match config.SecurityHardening with
        | NoSecurityHardening -> []
        | _ -> [ Csrf.tokenRoute ]

    // Phase 133 — BFF-style server-set auth-cookie reflection endpoint
    // (`POST` / `DELETE /api/auth/session`). Mounted only when
    // `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance`; the
    // default `NoAuthCookieIssuance` produces an empty list so the path
    // 404s from the Giraffe terminal middleware and an existing
    // deployment is byte-for-byte unchanged (GP 11). When enabled, a
    // client on `ClientConfig.AuthTokenStorage = ServerSetHttpOnlyCookie`
    // posts its freshly-acquired JWT here once; the handler validates it
    // via the registered `IAuthProvider` and reflects it into an
    // `HttpOnly; Secure; SameSite=Strict` cookie so the bearer never
    // lives in JS-readable storage.
    let authSessionRoutes: HttpHandler list =
        match config.AuthCookieIssuance with
        | NoAuthCookieIssuance -> []
        | EnabledAuthCookieIssuance -> AuthSession.routes

    // Phase 9o — post-deploy smoke-test endpoint
    // (`GET /api/_internal/smoke`). Mounted only when
    // `ServerConfig.SmokeTest = EnabledSmokeTest`; the default
    // `NoSmokeTest` produces an empty list so the route does not
    // exist on the routing table and deployments without smoke
    // enabled get a clean 404 from the Giraffe terminal middleware.
    // The handler itself gates on `TOOLUP_SMOKE_TOKEN` so even an
    // enabled-smoke deployment is closed to anyone without the
    // deploy script's shared secret.
    let smokeTestRoutes: HttpHandler list =
        match config.SmokeTest with
        | NoSmokeTest -> []
        | EnabledSmokeTest -> SmokeTestHandler.routes

    // Phase 59 — consent-audit endpoint. Mounted only when
    // `ServerConfig.ConsentAudit = EnabledConsentAudit`; default
    // `NoConsentAudit` produces an empty list so the path 404s and
    // client-side consent capture stays purely browser-local
    // (localStorage / CMP-host state).
    let consentAuditRoutes: HttpHandler list =
        match config.ConsentAudit with
        | NoConsentAudit -> []
        | EnabledConsentAudit -> ConsentApiHandler.routes

    // Phase 60 — ad analytics endpoints (impression + click). Mounted
    // only when `ServerConfig.AdAnalytics = EnabledAdAnalytics`; the
    // default `NoAdAnalytics` produces an empty list so client-side
    // `ServerSinkAdAnalytics` posts 404 (the sink swallows the error,
    // matching its best-effort contract).
    let adAnalyticsRoutes: HttpHandler list =
        match config.AdAnalytics with
        | NoAdAnalytics -> []
        | EnabledAdAnalytics -> AdAnalyticsApiHandler.routes

    // Phase 62 — premium claim endpoints (read + grant + revoke).
    // Always mounted — the read endpoint short-circuits to NotPremium
    // for anonymous callers, the write endpoints gate themselves on
    // Platform-Admin role inside the handler. The default
    // NoOpUserClaims impl makes the writes succeed without touching
    // any provider, so audit-trail captures operator intent even on
    // deployments that haven't wired a concrete IUserClaims.
    let premiumApiRoutes: HttpHandler list = GrantPremiumApiHandler.routes

    // Phase 61 — PlatformAdmin public-utility admin endpoints.
    // `AdUnitConfigApi` is mounted only when EntityStore is enabled
    // (the CRUD persists to `IEntityStore<AdSlotEntity>`); disabled
    // deployments 404 from the Giraffe terminal middleware so the
    // client widget's substrate-stub keeps surfacing the right
    // diagnostic. The other two endpoints always mount because their
    // substrate (`IRateLimitStore`, `IUserClaims`) ships with a
    // default in-process / no-op impl — the handlers gate themselves
    // on Platform-Admin role and degrade to empty / configurable
    // results when the deployment hasn't wired a concrete impl.
    let adUnitConfigRoutes: HttpHandler list =
        match config.EntityStore with
        | NoEntityStore -> []
        | EnabledEntityStore -> AdUnitConfigApi.routes

    let rateLimitEventApiRoutes: HttpHandler list = RateLimitEventApi.routes

    let premiumUserApiRoutes: HttpHandler list = PremiumUserApi.routes

    // Phase 9h — IDataSubjectRequestApi mount. Gated on
    // `ServerConfig.DataSubjectRequests = Enabled <policy>`; the
    // default `Disabled` skips the route entirely so the proxy
    // surface returns 404 and `ClientConfig`'s auto-injection (which
    // gates on the same config field) leaves the admin module out of
    // the sidebar — Client + Server stay in sync without a runtime
    // signal flowing between them.
    //
    // Per-request construction: the registered exporter / handler
    // lists are resolved from DI per request (singletons accumulated
    // via `ServerApp.withDataExporter` / `withErasureHandler` and
    // folded into `Extensions.ServiceConfig` by `ServerApp.run`). The
    // scope is resolved from the caller's `AccessContext` via
    // `configScope` — Team / MultiTeam get their team scope,
    // Individual / AuthenticatedEphemeral get their user scope.
    // Anonymous mode has no scope, falls back to the user id (which
    // is `"anonymous"`) so the handler can still run for tests; in
    // practice an unauthenticated caller can't reach the API anyway
    // (auth middleware short-circuits first).
    //
    // The audit callback resolves `IAuditLog` from DI and writes one
    // `AuditEvent.DataSubjectRequest` row per transition, recorded
    // under the resolved scope. `IAuditLog`'s best-effort contract
    // means a sink failure cannot roll back the DSR run — the audit
    // trail and the orchestrator outcome are durable independently.
    let dataSubjectRequestApiHandler: HttpHandler list =
        match config.DataSubjectRequests with
        | DataSubjectRequestMode.Disabled -> []
        | DataSubjectRequestMode.Enabled dsrConfig -> [
            makeApi (fun (ctx: HttpContext) ->
                // `IEnumerable<T>` resolution is built into MS DI — zero
                // registered impls yields an empty enumerable, not null.
                let exporters =
                    ctx.RequestServices.GetService(typeof<seq<IDataExporter>>) :?> seq<IDataExporter>
                    |> List.ofSeq

                let handlers =
                    ctx.RequestServices.GetService(typeof<seq<IErasureHandler>>) :?> seq<IErasureHandler>
                    |> List.ofSeq

                let accessContext =
                    match ctx.RequestServices.GetService(typeof<AccessContext>) with
                    | :? AccessContext as ac -> ac
                    | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

                let scopeId =
                    accessContext
                    |> AccessContext.configScope
                    |> Option.map _.ScopeId
                    |> Option.defaultValue accessContext.UserId

                let auditLog = ctx.RequestServices.GetService(typeof<IAuditLog>) :?> IAuditLog
                let audit = DataSubjectRequestApiHandler.auditToLog auditLog

                // Phase 9h.A — async export deps. Wired only when the
                // deployment opted into `Async` AND both an
                // `IBackgroundExportStore` (registered by
                // `ComposeJobs.registerDataSubjectRequestJobs`) and an
                // `IJobScheduler` are composed. Absent either, the async
                // methods return `Error`; the synchronous `RequestExport`
                // is unaffected.
                let asyncDeps =
                    if dsrConfig.Async then
                        match
                            ctx.RequestServices.GetService(typeof<IBackgroundExportStore>),
                            ctx.RequestServices.GetService(typeof<IJobScheduler>)
                        with
                        | (:? IBackgroundExportStore as store), (:? IJobScheduler as scheduler) ->
                            let channel =
                                match ctx.RequestServices.GetService(typeof<INotificationChannel>) with
                                | :? INotificationChannel as ch -> Some ch
                                | _ -> None

                            let notify (ticket: ExportTicket) (status: ExportStatus) : Async<unit> = async {
                                match channel with
                                | None -> return ()
                                | Some ch ->
                                    let json = $"{{\"ticket\":\"{ticket}\",\"status\":\"{ExportStatus.name status}\"}}"

                                    try
                                        do!
                                            ch.Publish(
                                                scopeId,
                                                CustomNotification(DsrNotifications.ExportProgressKey, json)
                                            )
                                    with _ ->
                                        return ()
                            }

                            Some {
                                DataSubjectRequestApiHandler.DsrAsyncDeps.Store = store
                                Scheduler = scheduler
                                Notify = notify
                            }
                        | _ -> None
                    else
                        None

                DataSubjectRequestApiHandler.create
                    exporters
                    handlers
                    dsrConfig.Policy
                    scopeId
                    accessContext.UserId
                    audit
                    asyncDeps)
          ]

    // Phase 54 — IPlatformTenantApi mount. Gated on
    // `ServerConfig.TenantLifecycle = EnabledTenantLifecycle`; the
    // default `NoTenantLifecycle` skips the route entirely so the proxy
    // surface 404s and no `ITenantLifecycle` hooks are resolved (they're
    // also absent from DI — `ComposeTenantLifecycle` gates on the same
    // field). Owner / Platform-Admin gating is enforced inside the
    // handler (`canModifyPlatformConfig`). Route shape:
    // `/api/_platform/tenants/*` via `PlatformTenantApi.routeBuilder`.
    let platformTenantApiHandler: HttpHandler list =
        match config.TenantLifecycle with
        | NoTenantLifecycle -> []
        | EnabledTenantLifecycle -> [
            Api.make (PlatformTenantApiHandler.platformTenantApi, routeBuilder = PlatformTenantApi.routeBuilder)
          ]

    let router (devRoutes: HttpHandler list) =
        choose (
            [
                platformInfoApiHandler config
                teamApiHandler config
                permissionApiHandler config
                accessibilityApiHandler config
                dataCatalogApiHandler config
            ]
            @ platformAdminApiHandler
            @ userDirectoryApiHandler
            @ teamInvitationApiHandler
            @ configHandler
            @ featureFlagHandler
            @ webhookHandler
            @ moduleQueryBusHandler
            @ fileManagementHandler
            @ jobApiHandler
            @ maintenanceApiHandler
            @ dataIngestionApiHandler
            @ oauthFlowRoutes
            @ healthMonitorApiHandler
            @ serviceStatusBoardApiHandler
            @ usageQueryApiHandler
            @ homeOverviewApiHandler
            @ encryptionAdminHandler
            @ metricsRoutes
            @ notificationRoutes
            @ csrfTokenRoutes
            @ authSessionRoutes
            @ smokeTestRoutes
            @ consentAuditRoutes
            @ adAnalyticsRoutes
            @ premiumApiRoutes
            @ adUnitConfigRoutes
            @ rateLimitEventApiRoutes
            @ premiumUserApiRoutes
            @ dataSubjectRequestApiHandler
            @ platformTenantApiHandler
            @ devRoutes
            @ extensions.Handlers
            @ handlers
        )

    {
        Router = router
        DevDiagnosticsRoutes = devDiagnosticsRoutes
    }