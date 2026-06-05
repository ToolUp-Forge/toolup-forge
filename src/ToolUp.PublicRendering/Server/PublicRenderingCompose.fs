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
    /// Phase 80b — gate exposing the `publish_narrative` AI tool.
    /// When `false` (default), `INarrativePagePublisher` is not
    /// registered in DI and the AI tool returns its graceful-
    /// degradation error ("no publisher registered"). Set to `true`
    /// only when the deployment has decided who can publish (either
    /// via `AIPublishAuthoriser` below, or by accepting that all AI
    /// users in the deployment can publish freely).
    AIPublishEnabled: bool
    /// Phase 80b — optional per-request authoriser for AI publishing.
    /// When `Some f`, the AI tool calls `f ctx` before invoking
    /// `INarrativePagePublisher.PublishAsync`; a `false` return causes
    /// the tool to refuse with an "unauthorised" error. `None`
    /// (default) means "allow whenever AIPublishEnabled = true" —
    /// fine for trusted single-user deployments, dangerous in
    /// multi-tenant ones.
    AIPublishAuthoriser: AIPublishAuthoriser option
    /// Phase 80b — optional Atom feed registrations. Each entry is
    /// mounted as a route handler at its `SelfUrl`. Empty (default)
    /// → no feeds emitted; the renderer registry's per-page
    /// `?format=atom` still works.
    Feeds: NarrativeFeedConfig list
}

module PublicRenderingServerApp =

    let create () : PublicRenderingServerApp = {
        Base = ServerApp.empty
        Layouts = Map.empty
        Redirects = []
        StructuredDataBuilders = Map.empty
        HotReload = true
        ContentApiOverride = None
        AIPublishEnabled = false
        AIPublishAuthoriser = None
        Feeds = []
    }

    /// Phase 80c composition seam — lift an existing `ServerApp` into a
    /// `PublicRenderingServerApp` so the additive
    /// `PublicRenderingCompose.withPublicRendering` extension can stack
    /// public-rendering contributions onto whatever the input
    /// `ServerApp` already carries. The input `ServerApp` becomes the
    /// `Base` field; all public-rendering-specific fields initialise
    /// empty (the configurator passed to `withPublicRendering`
    /// populates them via `withLayout` / `withRedirects` / etc.).
    let createFrom (baseApp: ServerApp) : PublicRenderingServerApp = {
        Base = baseApp
        Layouts = Map.empty
        Redirects = []
        StructuredDataBuilders = Map.empty
        HotReload = true
        ContentApiOverride = None
        AIPublishEnabled = false
        AIPublishAuthoriser = None
        Feeds = []
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

    /// Phase 80b — expose `publish_narrative` to AI tool callers.
    /// Default `false` (the publisher isn't registered, the AI tool
    /// gracefully degrades). Set to `true` ONLY after deciding who
    /// can publish — either by composing `withAIPublishAuthoriser`
    /// alongside this toggle, or by accepting that every AI user in
    /// the deployment can write to the public-page surface.
    let withAIPublishEnabled (enabled: bool) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            AIPublishEnabled = enabled
    }

    /// Phase 80b — register a per-request authoriser the AI tool
    /// consults before calling `INarrativePagePublisher.PublishAsync`.
    /// `authoriser ctx` returning `false` causes the tool to refuse
    /// with an unauthorised error. Typical implementations check the
    /// resolved `Subject` against an RBAC role or a per-team
    /// permission. Composes with `withAIPublishEnabled true` — both
    /// must be set for gated publish to work.
    let withAIPublishAuthoriser
        (authoriser: Microsoft.AspNetCore.Http.HttpContext -> Async<bool>)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                AIPublishAuthoriser = Some(AIPublishAuthoriser authoriser)
        }

    /// Phase 80b — register an Atom feed at the supplied
    /// `NarrativeFeedConfig.SelfUrl`. Mount one per collection
    /// (or one whole-site feed with `Collection = None`). Feeds
    /// surface every Narrative-bodied page matching the
    /// `Collection` filter, sorted by `PublishedAt` descending,
    /// capped at `MaxEntries`.
    let withFeed (config: NarrativeFeedConfig) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Feeds = app.Feeds @ [ config ]
    }

    /// Toggle the dev-mode hot-reload watcher. Defaults to `true`.
    /// Production deployments typically set `false` since content
    /// is baked at deploy time and a long-lived watcher leaks file
    /// handles on read-only file systems.
    let withHotReload (enabled: bool) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            HotReload = enabled
    }

    /// Phase 80c composition seam — apply every public-rendering-specific
    /// contribution (DI registrations, sitemap + redirect + export +
    /// feed + page handlers, `PublicPageEntity` registration, optional
    /// `INarrativePagePublisher` / `AIPublishAuthoriser` /
    /// `ILayoutCatalog` registrations) onto the inner `ServerApp`,
    /// returning the composed result without driving it.
    /// `PublicRenderingServerApp.run` calls this and then
    /// `ServerApp.run`; the `PublicRenderingCompose.withPublicRendering`
    /// builder calls it without invoking `ServerApp.run`, so the same
    /// PublicRendering contributions can stack with Forms / AI / RAG
    /// contributions onto one composition root (Phase 1h pattern
    /// extended to PublicRendering — see migration doc 80c).
    ///
    /// When `ServerConfig.PublicRendering = NoPublicRendering`, returns
    /// `app.Base` unchanged — zero contribution, byte-for-byte
    /// equivalent to a base `ServerApp` of the same `Base`. The
    /// companion marker is NOT appended in the strip-imports case so a
    /// later `withPublicRendering` on the same pipeline still composes
    /// freely.
    ///
    /// **Advanced.** Consumers should use `PublicRenderingServerApp.run`
    /// (or `PublicRenderingCompose.withPublicRendering` for hybrid
    /// composition) unless they are wrapping the composed `ServerApp`
    /// for further transformation. Hidden from IntelliSense via
    /// `[<EditorBrowsable>]`.
    [<System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)>]
    let composePublicRendering (app: PublicRenderingServerApp) : ServerApp =
        match app.Base.Config.PublicRendering with
        | NoPublicRendering ->
            // Strip-imports path: zero contribution. Same shape as if
            // the consumer never imported the companion. No companion
            // marker — a later `withPublicRendering` on the same
            // pipeline composes freely.
            app.Base
        | EnabledPublicRendering root ->
            // Phase 80c conflict validator — fail fast if PublicRendering
            // has already been composed onto this pipeline (e.g.
            // `withPublicRendering ... |> withPublicRendering ...`).
            // Pre-empts the cascading duplicate-route-mount /
            // duplicate-entity-registration failures that the second
            // composition would otherwise surface deep inside
            // `compose` or at first request.
            ServerApp.ensureCompanionNotAlreadyComposed "ToolUp.PublicRendering" app.Base

            let layouts = app.Layouts
            let composeRedirects = app.Redirects
            let hotReload = app.HotReload
            let explicitApi = app.ContentApiOverride
            let aiPublishEnabled = app.AIPublishEnabled
            let aiPublishAuthoriser = app.AIPublishAuthoriser
            let feeds = app.Feeds

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
            let registeredLayoutNames = layouts |> Map.toList |> List.map fst

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
                    .AddSingleton<ILayoutCatalog>(
                        // Phase 80b — always exposed when public
                        // rendering is enabled. Read-only view; no
                        // authorisation concern.
                        System.Func<System.IServiceProvider, ILayoutCatalog>(fun _sp ->
                            PublicRenderingLayoutCatalog.create registeredLayoutNames)
                    )
                |> fun s ->
                    // Phase 80b — INarrativePagePublisher registration
                    // is conditional on AIPublishEnabled. When off,
                    // the slot stays empty and the AI tool's DI
                    // resolution returns null → tool gracefully
                    // degrades with the "no publisher registered"
                    // error.
                    if aiPublishEnabled then
                        s.AddSingleton<INarrativePagePublisher>(
                            System.Func<System.IServiceProvider, INarrativePagePublisher>(fun sp ->
                                // Resolve the entity store at request time
                                // rather than at registration so any decorator
                                // wired by the consumer (encrypted store,
                                // audit-logged store, etc.) participates.
                                let entityStore = sp.GetService(typeof<IEntityStore>) :?> IEntityStore

                                PublicRenderingNarrativePagePublisher.create entityStore registeredLayoutNames)
                        )
                    else
                        s
                |> fun s ->
                    // Phase 80b — AIPublishAuthoriser registration.
                    // When unset, the AI tool's DI resolution returns
                    // null and the tool treats every request as
                    // authorised (subject to AIPublishEnabled gating).
                    match aiPublishAuthoriser with
                    | Some a -> s.AddSingleton<AIPublishAuthoriser>(a)
                    | None -> s

            // ─── Handler chain ───────────────────────────────────
            // Order: sitemap (specific route) → redirect (path-match
            // short-circuit) → export (?format= short-circuit) →
            // page (catch-all by slug, default HTML). All resolve
            // `IPublicContentApi` + `MarkdownContentLoader` per-request
            // from `ctx.RequestServices` so the DI singleton is shared.
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

            let exportHandler: HttpHandler =
                fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    NarrativeExportHandler.handler api next ctx

            let pageHandler: HttpHandler =
                fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    PublicPageHandler.handler api layouts next ctx

            // Phase 80b — one handler per registered feed, each
            // mounted at its configured SelfUrl. The route check uses
            // the standard Giraffe `route` combinator so feed URLs
            // short-circuit before the catch-all page handler.
            let feedHandlers: HttpHandler list =
                feeds
                |> List.map (fun feedConfig ->
                    route feedConfig.SelfUrl
                    >=> fun next ctx ->
                        let api =
                            ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                        NarrativeFeedHandler.handler feedConfig api next ctx)

            let publicRenderingHandlers =
                [ sitemapHandler; redirectHandler; exportHandler ]
                @ feedHandlers
                @ [ pageHandler ]

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

            // Phase 80c — append the PublicRendering marker so a
            // second `withPublicRendering` on the same pipeline trips
            // the entry-guard above.
            final |> ServerApp.withCompanionMarker "ToolUp.PublicRendering"

    /// Drive the final composition. Registers the sitemap / redirect /
    /// export / feed / page handlers, wires the `IPublicContentApi` +
    /// `MarkdownContentLoader` + optional `INarrativePagePublisher`
    /// into DI, and delegates to `ServerApp.run`. When
    /// `ServerConfig.PublicRendering = NoPublicRendering`, short-
    /// circuits to `ServerApp.run app.Base` — byte-for-byte the same
    /// shape as the pre-Phase-38 base path. Phase 80c — implementation
    /// is now `composePublicRendering >> ServerApp.run`; consumers
    /// needing to stack PublicRendering with Forms / AI / RAG
    /// companions on one composition root call
    /// `PublicRenderingCompose.withPublicRendering` directly instead.
    let run (app: PublicRenderingServerApp) : int =
        composePublicRendering app |> ServerApp.run

    /// Build-time terminus. Mirrors `run` but writes the rendered
    /// site to disk under `outputDir` instead of starting Kestrel,
    /// producing a static HTML tree hostable on Azure Static Web Apps,
    /// Netlify, GitHub Pages, S3 + CloudFront, etc. See
    /// `StaticExport.run` for the output layout and behaviour.
    ///
    /// Requires `ServerConfig.PublicRendering = EnabledPublicRendering`
    /// and at least one registered layout — same invariants as `run`.
    let exportStatic (outputDir: string) (app: PublicRenderingServerApp) : Async<int> =
        StaticExport.run app.Base.Config app.Layouts app.ContentApiOverride app.Base.Logger outputDir

// ─── Additive companion-set extension `withPublicRendering` (Phase 80c) ──
//
// Stack PublicRendering contributions onto an existing `ServerApp` pipeline
// alongside Forms / AI / RAG / future companions, without forcing the
// deployment to commit to `PublicRenderingServerApp.run` as the terminal
// call. Mirrors Phase 1h's `FormsCompose.withForms` / `AICompose.withAI`
// shape exactly; see the Phase 1h migration doc + the Phase 80c migration
// doc for the hybrid-composition pattern.

/// Phase 80c — stack PublicRendering contributions onto an existing
/// `ServerApp` pipeline. The `configure` function builds
/// public-rendering-specific state (layouts, redirects, JSON-LD builders,
/// hot-reload toggle, content-API override, AI-publish gating, Atom feeds)
/// on a fresh `PublicRenderingServerApp` whose `Base` is the input
/// `ServerApp`. The configurator should call only PublicRendering-specific
/// helpers (`PublicRenderingServerApp.withLayout` / `withRedirects` /
/// `withFeed` / etc.); the delegating helpers (`withConfig` / `withAuth` /
/// …) exist on `PublicRenderingServerApp` for backcompat but calling them
/// inside the configurator overwrites the base `ServerApp`'s existing
/// configuration. Set base configuration on the outer pipeline before
/// calling `withPublicRendering`.
///
/// Calling `withPublicRendering` twice on the same pipeline composes
/// PublicRendering twice — the Phase 80c conflict validator (mirroring the
/// Phase 1h convention via `ServerApp.ensureCompanionNotAlreadyComposed`)
/// surfaces this at compose time with a clear single-line diagnostic
/// naming the companion + resolution paths, instead of cascading into
/// double-mounted-route / duplicate-entity-registration failures.
///
/// When `ServerConfig.PublicRendering = NoPublicRendering`, the call is
/// a no-op pass-through — same strip-imports guarantee as
/// `PublicRenderingServerApp.run`. The companion marker is NOT appended
/// in that case, so a later `withPublicRendering` on the same pipeline
/// composes freely (a deployment can opt into PublicRendering by flipping
/// the `ServerConfig` field at startup without changing its composition
/// root).
///
/// Example — PublicRendering + a domain module on one pipeline:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> ServerApp.addModule myAdminModule
///     |> PublicRenderingCompose.withPublicRendering (fun pr ->
///         pr
///         |> PublicRenderingServerApp.withLayout "page" pageLayout
///         |> PublicRenderingServerApp.withLayout "article" articleLayout
///         |> PublicRenderingServerApp.withFeed myAtomFeed)
///     |> ServerApp.run
let withPublicRendering (configure: PublicRenderingServerApp -> PublicRenderingServerApp) (app: ServerApp) : ServerApp =
    PublicRenderingServerApp.createFrom app
    |> configure
    |> PublicRenderingServerApp.composePublicRendering