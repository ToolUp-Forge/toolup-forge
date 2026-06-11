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
    /// Phase 83 — ordered request-time `IContentSource` resolvers. The
    /// default impl consults these after the file + entity-overlay tiers
    /// (registration order, first `Some` wins) on `GetPageInContext`.
    /// Empty (default) → byte-for-byte identical to the pre-83
    /// file+overlay chain (GP 11). Ignored when `ContentApiOverride` is
    /// set — a supplied `IPublicContentApi` owns its own resolution.
    ContentSources: IContentSource list
    /// Phase 84 — opt-in SSR render cache (ISR tier). `None` (default) →
    /// the page handler runs the pre-84 pure-per-request path with no
    /// cache lookup and no HTTP cache headers (GP 11 / GP 13). `Some
    /// cache` registers the cache in DI, activating the cached path +
    /// `ETag` / `Last-Modified` / `Cache-Control` / `304` handling. Use
    /// `InMemoryRenderCache.create ()` for single-instance or
    /// `BlobRenderCache.create storage` for multi-instance.
    RenderCache: IRenderCache option
    /// Phase 84 — default cache policy applied to a resolved page that
    /// declares no explicit `cache:` frontmatter key. The lever for
    /// caching (frontmatter-less) dynamic content-source pages without
    /// annotating each one. Defaults to `NoCache`; ignored unless
    /// `RenderCache` is `Some`.
    RenderCacheDefaultPolicy: CachePolicy
    /// Phase 84 — optional explicit slug-purge surface for the publish /
    /// CMS invalidation hook. When `None` and `RenderCache` is a default
    /// impl (which implements `IRenderCacheInvalidation`), the cache
    /// itself is used as the invalidator. Supply this only when the
    /// deployment's `IRenderCache` doesn't carry its own slug-purge.
    RenderCacheInvalidation: IRenderCacheInvalidation option
    /// Phase 100 — when `true`, `composePublicRendering` registers the
    /// `TaxonomyHandler.tagIndexSource` (serving `/tag/{slug}`) wired to
    /// the default content API automatically, so a deployment doesn't
    /// hand-build the `listPages` thunk. `false` (default) = no tag-index
    /// source (GP 11). Set via `withTaxonomy`.
    TaxonomyEnabled: bool
    /// Phase 100 — the navigation tree loaded from a `nav.yaml` file by
    /// `withNav`, registered as a `NavCatalog` DI singleton for content
    /// sources / custom handlers to resolve. Empty (default) = no nav
    /// registered.
    Nav: NavNode list
    /// Phase 91 — opt-in semantic-search SSR endpoint config. `Some cfg`
    /// mounts a `/search?q=` handler that runs `IRetrievalPipeline.Retrieve`
    /// (resolved from DI) under the request scope and renders a ranked
    /// result list. `None` (default) → no `/search` route (GP 11 / GP 13).
    /// Even when `Some`, the route declines when no `IRetrievalPipeline`
    /// is composed.
    SemanticSearch: SemanticSearchConfig option
    /// Phase 91 — guardrails for the AI publishing path (`publish_narrative`).
    /// Defaults to `NarrativePublishGuardrails.defaults` (the Phase 80a
    /// immediate-publish behaviour, GP 11); set via `withAIPublishGuardrails`
    /// to force a Draft landing, constrain layouts / audience, or cap the
    /// document shape. Only consulted when `AIPublishEnabled = true`.
    AIPublishGuardrails: NarrativePublishGuardrails
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
        ContentSources = []
        RenderCache = None
        RenderCacheDefaultPolicy = CachePolicy.NoCache
        RenderCacheInvalidation = None
        TaxonomyEnabled = false
        Nav = []
        SemanticSearch = None
        AIPublishGuardrails = NarrativePublishGuardrails.defaults
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
        ContentSources = []
        RenderCache = None
        RenderCacheDefaultPolicy = CachePolicy.NoCache
        RenderCacheInvalidation = None
        TaxonomyEnabled = false
        Nav = []
        SemanticSearch = None
        AIPublishGuardrails = NarrativePublishGuardrails.defaults
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

    /// Phase 91 — constrain the AI publishing path (`publish_narrative`).
    /// Composes with `withAIPublishEnabled true`. Use
    /// `NarrativePublishGuardrails.aiHardened` to force every AI publish to
    /// land as a Draft (not publicly served until a human reviews it via
    /// the Phase 89 workflow), and the `NarrativePublishGuardrails.with*`
    /// helpers to add a layout allow-list, pin the audience, or cap the
    /// document shape. Defaults to the pre-91 immediate-publish behaviour
    /// (GP 11) when not set.
    let withAIPublishGuardrails
        (guardrails: NarrativePublishGuardrails)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                AIPublishGuardrails = guardrails
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

    /// Phase 97 — register a per-tag Atom feed: the taxonomy-axis
    /// extension of `withFeed`. Sets `config.Tag = Some tag` so the feed
    /// surfaces only pages carrying `tag`, then registers it like any
    /// other feed. Typically `withTagFeed "news" { NarrativeFeedConfig.defaults with Title = "News"; SelfUrl = "/tag/news/feed.atom" }`.
    let withTagFeed
        (tag: string)
        (config: NarrativeFeedConfig)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        withFeed { config with Tag = Some tag } app

    /// Phase 97 — register one per-tag feed for each tag in `tags` (the
    /// whole-taxonomy fan-out). Each feed is served at
    /// `/tag/{tag}/feed.atom` with the title `"{baseTitle}: {tag}"`,
    /// inheriting `MaxEntries` etc. from `template`.
    let withTagFeeds
        (baseTitle: string)
        (template: NarrativeFeedConfig)
        (tags: string list)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        tags
        |> List.fold
            (fun acc tag ->
                withTagFeed
                    tag
                    {
                        template with
                            Title = sprintf "%s: %s" baseTitle tag
                            SelfUrl = sprintf "/tag/%s/feed.atom" tag
                    }
                    acc)
            app

    /// Phase 83 — register a request-time `IContentSource`. The default
    /// impl consults registered sources after the file + entity-overlay
    /// tiers (registration order; first `Some` wins) on a per-request
    /// resolve. Multiple sources compose; call this once per source.
    ///
    /// Use `ContentSource.create` for a resolver that claims slugs by its
    /// own logic, or `ContentSource.ofRoute "services/{client}" resolver`
    /// for a source that claims a family of dynamic paths by pattern.
    ///
    /// Composes additively (GP 11): a pipeline with no `withContentSource`
    /// calls is byte-for-byte identical to the pre-83 file+overlay chain.
    /// Ignored when `withContentApi` supplies an explicit
    /// `IPublicContentApi` — a custom content API owns its own resolution.
    let withContentSource (source: IContentSource) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            ContentSources = app.ContentSources @ [ source ]
    }

    /// Phase 100 — enable the `/tag/{slug}` taxonomy surface with no
    /// hand-wired `listPages` thunk: `composePublicRendering` registers
    /// `TaxonomyHandler.tagIndexSource` against the default content API
    /// automatically (the wiring deferred in Phase 90). No-op when the
    /// deployment supplies its own `IPublicContentApi` via `withContentApi`
    /// (that impl owns its own resolution).
    let withTaxonomy (app: PublicRenderingServerApp) : PublicRenderingServerApp = { app with TaxonomyEnabled = true }

    /// Phase 100 — load a `nav.yaml` file at compose time and register the
    /// parsed tree as a `NavCatalog` DI singleton (resolvable by content
    /// sources / custom handlers). The tree is also stored on the record
    /// (`app.Nav`) so a compose-time consumer can capture it for a layout.
    /// A missing / empty file registers an empty nav (no error).
    let withNav (navYamlPath: string) (app: PublicRenderingServerApp) : PublicRenderingServerApp =
        let nav =
            if System.IO.File.Exists navYamlPath then
                NavTree.parseYaml (System.IO.File.ReadAllText navYamlPath)
            else
                []

        { app with Nav = nav }

    /// Phase 100 — register a pre-parsed nav tree directly (when the
    /// consumer built it in code rather than from a `nav.yaml` file).
    let withNavTree (nav: NavNode list) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Nav = nav
    }

    /// Phase 91 — mount a semantic-search SSR endpoint at `/search?q=`.
    /// The handler runs `IRetrievalPipeline.Retrieve` (resolved from DI —
    /// the RAG companion registers it) under the request's `AccessContext`
    /// and renders a ranked, indexable result list through the registered
    /// layouts. The query-size cap (`SemanticSearchConfig.MaxQueryChars`)
    /// refuses over-long queries; the route otherwise rides the
    /// deployment's general rate-limit partition (Phase 14y posture).
    ///
    /// Off by default (GP 11 / GP 13): a pipeline that never calls this has
    /// no `/search` route. Even when composed, the route declines (falls
    /// through to 404) when no `IRetrievalPipeline` is registered.
    let withSemanticSearch (config: SemanticSearchConfig) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            SemanticSearch = Some config
    }

    /// Phase 84 — opt into the SSR render cache (ISR tier). Registers the
    /// supplied `IRenderCache` in DI; the page handler then serves cached
    /// renders within their TTL window (serving stale + refreshing in the
    /// background under stale-while-revalidate) and emits `ETag` /
    /// `Last-Modified` / `Cache-Control` headers, honouring `If-None-Match`
    /// with a `304`.
    ///
    /// Off by default (GP 11 / GP 13): a pipeline that never calls this is
    /// byte-for-byte the pre-84 page handler — no lookup, no headers, no
    /// allocation. Per-route opt-in is via a page's `cache:` frontmatter
    /// (`cache: 300`; `cache: off` default); `withRenderCacheDefaultPolicy`
    /// sets the policy for pages that declare none (e.g. dynamic
    /// content-source pages with no frontmatter).
    ///
    ///     // single-instance:
    ///     |> PublicRenderingServerApp.withRenderCache (InMemoryRenderCache.create ())
    ///     // multi-instance (shared blob container):
    ///     |> PublicRenderingServerApp.withRenderCache (BlobRenderCache.create storage)
    let withRenderCache (cache: IRenderCache) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            RenderCache = Some cache
    }

    /// Phase 84 — set the default cache policy applied to a resolved page
    /// that declares no explicit `cache:` frontmatter key. Use this to
    /// cache (frontmatter-less) dynamic content-source pages — e.g.
    /// `withRenderCacheDefaultPolicy (Cache(300, true))` caches every
    /// uncached-by-frontmatter page for 5 minutes with
    /// stale-while-revalidate. No effect unless `withRenderCache` is also
    /// composed. Defaults to `NoCache` (pure per-request).
    let withRenderCacheDefaultPolicy (policy: CachePolicy) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            RenderCacheDefaultPolicy = policy
    }

    /// Phase 84 — supply an explicit slug-purge surface for the publish /
    /// CMS invalidation hook. The default cache impls
    /// (`InMemoryRenderCache` / `BlobRenderCache`) already implement
    /// `IRenderCacheInvalidation`, so `withRenderCache` alone wires the
    /// publish-purge path. Use this only when the deployment's custom
    /// `IRenderCache` doesn't carry its own slug-purge and a separate
    /// invalidator object owns it.
    let withRenderCacheInvalidation
        (invalidator: IRenderCacheInvalidation)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                RenderCacheInvalidation = Some invalidator
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
            let aiPublishGuardrails = app.AIPublishGuardrails // Phase 91
            let feeds = app.Feeds
            let contentSources = app.ContentSources
            let taxonomyEnabled = app.TaxonomyEnabled // Phase 100
            let navTree = app.Nav // Phase 100
            let semanticSearch = app.SemanticSearch // Phase 91
            let renderCache = app.RenderCache
            let renderCacheDefaultPolicy = app.RenderCacheDefaultPolicy

            // Resolve the slug-purge surface for the publish / CMS
            // invalidation hook (Phase 84). Prefer an explicit
            // `withRenderCacheInvalidation`; otherwise fall back to the
            // cache itself when it implements `IRenderCacheInvalidation`
            // (both default impls do). `None` → publishing doesn't purge
            // (no cache composed, or a custom cache with no slug-purge).
            let renderCacheInvalidator: IRenderCacheInvalidation option =
                match app.RenderCacheInvalidation with
                | Some inv -> Some inv
                | None ->
                    match renderCache with
                    | Some cache ->
                        match box cache with
                        | :? IRenderCacheInvalidation as inv -> Some inv
                        | _ -> None
                    | None -> None

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

                                // Phase 100 — `withTaxonomy` registers the
                                // tag-index source over a base API (file +
                                // overlay only, no source-tier recursion),
                                // so no hand-wired `listPages` thunk is
                                // needed.
                                let taxonomySources =
                                    if taxonomyEnabled then
                                        let baseApi = PublicContentApiImpl.create loader entityStore []
                                        [ TaxonomyHandler.tagIndexSourceFromApi baseApi ]
                                    else
                                        []

                                PublicContentApiImpl.create loader entityStore (contentSources @ taxonomySources))
                    )
                    .AddSingleton<ILayoutCatalog>(
                        // Phase 80b — always exposed when public
                        // rendering is enabled. Read-only view; no
                        // authorisation concern.
                        System.Func<System.IServiceProvider, ILayoutCatalog>(fun _sp ->
                            PublicRenderingLayoutCatalog.create registeredLayoutNames)
                    )
                    // Phase 100 — `withNav` registers the parsed nav tree
                    // as a NavCatalog singleton (content sources / custom
                    // handlers resolve it). Always registered when public
                    // rendering is enabled; empty when `withNav` unused.
                    .AddSingleton<NavCatalog>(
                        System.Func<System.IServiceProvider, NavCatalog>(fun _sp -> { Nav = navTree })
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

                                PublicRenderingNarrativePagePublisher.create
                                    entityStore
                                    registeredLayoutNames
                                    renderCacheInvalidator
                                    aiPublishGuardrails)
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
                |> fun s ->
                    // Phase 84 — render-cache registrations. When a cache
                    // is composed, register `IRenderCache` (the page
                    // handler activates its cached path on seeing it in
                    // DI), the `RenderCacheSettings` carrying the default
                    // policy, and the `IRenderCacheInvalidation` slug-purge
                    // surface (so Phase 91 / CMS code can resolve it). When
                    // no cache is composed, none of these are registered →
                    // the page handler runs the pre-84 path (GP 11 / 13).
                    match renderCache with
                    | None -> s
                    | Some cache ->
                        let s = s.AddSingleton<IRenderCache>(cache)

                        let s =
                            s.AddSingleton<RenderCacheSettings>(
                                {
                                    RenderCacheSettings.defaults with
                                        DefaultPolicy = renderCacheDefaultPolicy
                                }
                            )

                        match renderCacheInvalidator with
                        | Some inv -> s.AddSingleton<IRenderCacheInvalidation>(inv)
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

                    // Phase 95 — include content-source-enumerated dynamic
                    // routes (e.g. `/tag/{x}`) so crawlers discover them.
                    let enumerate () =
                        ContentSource.enumerateAll contentSources

                    SitemapGenerator.handler publicBaseUrl api enumerate next ctx

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

            // Phase 89 — token-gated preview route. Mounted at the fixed
            // `/preview` path so it short-circuits before the catch-all
            // page handler; serves an unpublished page only with a valid
            // share token (declines otherwise).
            let previewHandler: HttpHandler =
                fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    ContentPreview.previewHandler api layouts next ctx

            // Phase 91 — semantic-search SSR endpoint. Mounted before the
            // catch-all page handler so `/search` short-circuits. The
            // pipeline is resolved per-request from DI (the RAG companion
            // registers `IRetrievalPipeline`); when absent the handler
            // declines and `/search` falls through to the 404 path.
            let searchHandlers: HttpHandler list =
                match semanticSearch with
                | None -> []
                | Some cfg -> [
                    route "/search"
                    >=> fun next ctx ->
                        match ctx.RequestServices.GetService(typeof<IRetrievalPipeline.IRetrievalPipeline>) with
                        | :? IRetrievalPipeline.IRetrievalPipeline as pipeline ->
                            SemanticSearchHandler.handle cfg pipeline layouts next ctx
                        | _ -> next ctx
                  ]

            let publicRenderingHandlers =
                [ sitemapHandler; redirectHandler; exportHandler ]
                @ feedHandlers
                @ searchHandlers
                @ [ previewHandler; pageHandler ]

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
        StaticExport.run app.Base.Config app.Layouts app.ContentApiOverride app.ContentSources app.Base.Logger outputDir

    /// Phase 93 — static export with operational extensions: per-locale
    /// trees, host-config emission (`staticwebapp.config.json` / `_redirects`
    /// / `_headers`), and a build-time internal-link / asset check. Passes
    /// the compose-registered redirects (`app.Redirects`) through to the
    /// host-config emitter. `StaticExportOptions.defaults` reproduces the
    /// `exportStatic` single-tree behaviour.
    let exportStaticWith
        (options: StaticExportOptions)
        (outputDir: string)
        (app: PublicRenderingServerApp)
        : Async<int> =
        StaticExport.runWith
            options
            app.Redirects
            app.Base.Config
            app.Layouts
            app.ContentApiOverride
            app.ContentSources
            app.Base.Logger
            outputDir

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