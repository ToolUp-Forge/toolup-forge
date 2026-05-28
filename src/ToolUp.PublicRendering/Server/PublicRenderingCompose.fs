module ToolUp.PublicRendering.PublicRenderingCompose

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Server

// ─── Phase 38 — PublicRenderingServerApp composition root ───────────
//
// `PublicRenderingServerApp` mirrors `FormsServerApp` / `AIServerApp`
// shape: it wraps a base `ServerApp` and adds `with*` helpers for
// public-rendering-specific compose-time registrations (layouts,
// redirects, JSON-LD builders, content-api override, hot-reload toggle).
//
// **Required substrate**: none, beyond what `ServerApp` itself needs.
// The companion is self-contained — it brings its own
// `MarkdownContentLoader` + `PublicContentApiImpl` and mounts handlers
// onto the SDK's route chain via `ComposeExtensions.Handlers`.
//
// **Strip-imports guarantee**: when
// `ServerConfig.PublicRendering = NoPublicRendering`, `run` short-
// circuits to `ServerApp.run app.Base` — no DI registrations, no
// handlers, no hosted services. Byte-for-byte equivalent to a base
// `ServerApp.run` of the same `Base`.

/// Record form of compose arguments. Wraps a base `ServerApp` and
/// carries compose-time-registered maps for layouts, redirects, and
/// JSON-LD builders. The required `ContentRoot` comes from
/// `ServerConfig.PublicRendering` (set via `withConfig`); the
/// architecture deliberately reads it from `ServerConfig` rather than
/// a separate field on this record so a single `ServerConfig.defaults`
/// override controls the strip-imports gate.
type PublicRenderingServerApp = {
    Base: ServerApp
    /// Compose-time-registered layouts keyed by `LayoutName`. The
    /// page's `Layout` field selects one; unknown names fall back to
    /// the first-registered layout at render time.
    Layouts: Map<LayoutName, PublicPage -> XmlNode>
    /// Additional redirects on top of any read from
    /// `<contentRoot>/redirects.csv` at startup. Compose-registered
    /// entries are appended after file-loaded entries (file wins on
    /// duplicate `From`).
    Redirects: Redirect list
    /// JSON-LD builders registered by name. Layouts call them
    /// directly; the registry is supplied so a deployment can swap
    /// the built-in `article` / `person` / `event` / `organization` /
    /// `breadcrumb` shapes for custom variants.
    StructuredDataBuilders: Map<string, PublicPage -> string>
    /// Dev-mode hot-reload toggle. `true` (default) enables the
    /// `FileSystemWatcher`. Production deployments typically set
    /// `false` since content is baked at deploy time.
    HotReload: bool
    /// Optional override for the `IPublicContentApi` impl. When
    /// `None`, `run` constructs the default
    /// `PublicContentApiImpl` over the `MarkdownContentLoader`.
    /// Deployments wanting the runtime-edited overlay
    /// (`IEntityStore<PublicPage>`) supply their own impl here.
    ContentApiOverride: IPublicContentApi option
}

module PublicRenderingServerApp =

    let create () : PublicRenderingServerApp = {
        Base = ServerApp.empty
        Layouts = Map.empty
        Redirects = []
        StructuredDataBuilders = Map.empty
        HotReload = true
        ContentApiOverride = None
    }

    // ─── Delegating helpers (mirror every `ServerApp.with*`) ─────

    let withConfig (c: ServerConfig) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withConfig c app.Base
    }

    let withAuth (a: IAuthProvider) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withAuth a app.Base
    }

    let withLogger (l: ILogger) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withLogger l app.Base
    }

    let withStorage (s: IBlobStorage) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withStorage s app.Base
    }

    let withNotifications (n: INotificationChannel) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withNotifications n app.Base
    }

    let withTransactionalSink (sink: INotificationSink) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withTransactionalSink sink app.Base
    }

    let withHealthCheck (check: HealthChecks.IHealthCheck) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.withHealthCheck check app.Base
    }

    let withConfigValidator
        (validator: ConfigValidation.IConfigValidator)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                Base = ServerApp.withConfigValidator validator app.Base
        }

    let withEncryptedBlobStorage
        (resolver: IBlobEncryptionKeyResolver)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                Base = ServerApp.withEncryptedBlobStorage resolver app.Base
        }

    let withEntity<'T>
        (registration: EntityTypes.EntityRegistration<'T>)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                Base = ServerApp.withEntity registration app.Base
        }

    let withPreMiddleware
        (f: IApplicationBuilder -> IApplicationBuilder)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                Base = ServerApp.withPreMiddleware f app.Base
        }

    let withPostMiddleware
        (f: IApplicationBuilder -> IApplicationBuilder)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                Base = ServerApp.withPostMiddleware f app.Base
        }

    let addModule (m: ServerModule) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.addModule m app.Base
    }

    let addModules (modules: ServerModule list) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Base = ServerApp.addModules modules app.Base
    }

    // ─── Public-rendering-specific helpers ───────────────────────

    /// Register a layout function. At least one layout must be
    /// registered when `PublicRendering = EnabledPublicRendering`;
    /// the first-registered layout becomes the fallback for any
    /// page whose `Layout` doesn't match a registered name.
    let withLayout
        (name: LayoutName)
        (layout: PublicPage -> XmlNode)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                Layouts = Map.add name layout app.Layouts
        }

    /// Register additional redirects (in addition to whatever is
    /// loaded from `<contentRoot>/redirects.csv` at startup).
    /// File-loaded entries take precedence on duplicate `From`.
    let withRedirects (redirects: Redirect list) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Redirects = app.Redirects @ redirects
    }

    /// Register a JSON-LD builder by name. Built-ins (`article` /
    /// `person` / `event` / `organization` / `breadcrumb`) live in
    /// `StructuredDataHelpers`; this helper is for layout-specific
    /// custom builders. Names collide → last registered wins.
    let withStructuredDataBuilder
        (name: string)
        (builder: PublicPage -> string)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                StructuredDataBuilders = Map.add name builder app.StructuredDataBuilders
        }

    /// Supply an explicit `IPublicContentApi` impl. The default
    /// (`None`) constructs `PublicContentApiImpl` over a
    /// `MarkdownContentLoader` keyed off
    /// `ServerConfig.PublicRendering`. Use this hook to wrap the
    /// default with an `IEntityStore<PublicPage>` overlay or
    /// substitute a custom backing store entirely.
    let withContentApi (api: IPublicContentApi) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            ContentApiOverride = Some api
    }

    /// Toggle the dev-mode hot-reload watcher. Defaults to `true`.
    /// Production deployments typically set `false` since content
    /// is baked at deploy time and a long-lived watcher leaks file
    /// handles on read-only file systems.
    let withHotReload (enabled: bool) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            HotReload = enabled
    }

    /// Drive the final composition. When `ServerConfig.PublicRendering`
    /// is `NoPublicRendering`, short-circuits to `ServerApp.run` —
    /// byte-for-byte the same shape as the pre-Phase-38 base path.
    /// When `EnabledPublicRendering root`, constructs the loader,
    /// builds the API impl (using `ContentApiOverride` when supplied),
    /// appends the sitemap / redirect / page handlers to the route
    /// chain, registers DI singletons, and delegates to
    /// `ServerApp.run`.
    let run (app: PublicRenderingServerApp) : int =
        match app.Base.Config.PublicRendering with
        | NoPublicRendering ->
            // Strip-imports path: zero contribution to the base
            // `ServerApp.run`. Same shape as if the consumer never
            // imported the companion.
            ServerApp.run app.Base
        | EnabledPublicRendering root ->
            let layouts = app.Layouts
            let composeRedirects = app.Redirects
            let hotReload = app.HotReload
            let explicitApi = app.ContentApiOverride

            // Auto-register `PublicPageEntity` against the base
            // `ServerApp` so the default impl's entity-store fallthrough
            // on `GetPage` has a recognised type. Idempotent against
            // consumer-supplied `withEntity` calls (the registry
            // dedupes by `EntityType` string).
            let appWithEntity = ServerApp.withEntity PublicPageEntity.registration app.Base

            // Capture the public base URL for the sitemap handler.
            // Fall back to an empty string when unset — search engines
            // get relative-shaped `<loc>` entries which are still
            // valid for same-host fetches.
            let publicBaseUrl = appWithEntity.Config.PublicBaseUrl |> Option.defaultValue ""

            // Resolve the SDK logger from the base `ServerApp.Logger`
            // (set via `withLogger`); fall back to `ConsoleLogger`
            // matching the pattern in `RAGCompose` so the loader's
            // diagnostics surface even when `withLogger` was skipped.
            let prLogger =
                appWithEntity.Logger
                |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

            // ─── DI registrations ────────────────────────────────
            let publicRenderingServiceConfig (services: IServiceCollection) =
                services
                    .AddSingleton<MarkdownContentLoader>(
                        System.Func<System.IServiceProvider, MarkdownContentLoader>(fun _sp ->
                            new MarkdownContentLoader(root, prLogger, hotReload))
                    )
                    .AddSingleton<IPublicContentApi>(
                        System.Func<System.IServiceProvider, IPublicContentApi>(fun sp ->
                            match explicitApi with
                            | Some api -> api
                            | None ->
                                let loader = sp.GetService(typeof<MarkdownContentLoader>) :?> MarkdownContentLoader

                                let entityStore =
                                    sp.GetService(typeof<IEntityStore>)
                                    |> Option.ofObj
                                    |> Option.map (fun x -> x :?> IEntityStore)

                                PublicContentApiImpl.create loader entityStore)
                    )

            // ─── Handler chain ───────────────────────────────────
            // Order: sitemap (specific route) → redirect (path-match
            // short-circuit) → page (catch-all by slug). All three
            // resolve `IPublicContentApi` + `MarkdownContentLoader`
            // per-request from `ctx.RequestServices` so the DI
            // singleton is shared.
            let sitemapHandler: HttpHandler =
                route "/sitemap.xml"
                >=> fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    SitemapGenerator.handler publicBaseUrl api next ctx

            let redirectHandler: HttpHandler =
                fun next ctx ->
                    let loader =
                        ctx.RequestServices.GetService(typeof<MarkdownContentLoader>) :?> MarkdownContentLoader

                    let allRedirects = loader.Redirects @ composeRedirects
                    RedirectMap.handler allRedirects next ctx

            let pageHandler: HttpHandler =
                fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    PublicPageHandler.handler api layouts next ctx

            let publicRenderingHandlers = [ sitemapHandler; redirectHandler; pageHandler ]

            let baseExt = appWithEntity.Extensions

            let mergedExt: ComposeExtensions = {
                baseExt with
                    Handlers = baseExt.Handlers @ publicRenderingHandlers
                    ServiceConfig =
                        match baseExt.ServiceConfig with
                        | None -> Some publicRenderingServiceConfig
                        | Some baseFn -> Some(fun s -> publicRenderingServiceConfig (baseFn s))
            }

            let final = {
                appWithEntity with
                    Extensions = mergedExt
            }

            ServerApp.run final