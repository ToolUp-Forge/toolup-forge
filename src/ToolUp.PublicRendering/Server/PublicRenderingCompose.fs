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
    /// Phase 109 — opt-in IndexNow push-indexing. Defaults to the disabled
    /// shape (`IndexNowOptions.defaults`) — no `/{key}.txt` route, no
    /// startup submission, no publish ping (GP 11 / GP 13). Set via
    /// `withIndexNow` to actively notify search engines (Bing / Yandex /
    /// Seznam / Naver) when content is published or the deploy changes the
    /// URL universe.
    IndexNow: IndexNowOptions
    /// Phase 114 — satellite websites served by this instance, matched by
    /// request host. Empty (default) → the pre-114 single-site behaviour,
    /// byte-for-byte (GP 11 / GP 13). Each registered `PublicSiteDef`
    /// claims a set of host names and brings its own content root,
    /// layouts (falling back to the shared ones), redirects, and base
    /// URL; every unmatched host serves the default (`ServerConfig`-level)
    /// site. Set via `withSite`. v1 scope: satellites are markdown-file-
    /// backed only — entity overlay, content sources, search, taxonomy,
    /// and AI publish remain default-site surfaces (Atom feeds went
    /// per-site in Phase 115 via `PublicSiteDef.Feeds`).
    Sites: PublicSiteDef list
    /// Phase 147 — opt-in cache-independent conditional-GET emission. `None`
    /// (default) → the uncached serve path is byte-for-byte the pre-147 path
    /// (no validators); `Some settings` makes a deterministic SSR site emit
    /// `ETag` / content-stable `Last-Modified` / `Cache-Control` and `304`
    /// on conditional re-requests WITHOUT a render cache (crawl-budget
    /// revalidation is orthogonal to ISR caching). Ignored when
    /// `RenderCache` is `Some` — the cached path already emits validators.
    /// Set via `withConditionalGet`.
    ConditionalGet: ConditionalGetSettings option
    /// Phase 148 — opt-in self-referencing canonical. `false` (default) →
    /// the rendered `<head>` is byte-for-byte the pre-148 output (GP 11);
    /// `true` wraps the resolved content API so every page that declares no
    /// explicit canonical (a Narrative `CanonicalUrl` or a `head:canonical`
    /// envelope) gains a self-referencing `<link rel="canonical">` resolving
    /// to its own absolute URL — satellite-aware (each site's `BaseUrl`).
    /// Set via `withSelfCanonical`.
    SelfCanonical: bool
    /// Phase 149 — opt-in sitemap response cache. `false` (default) → the
    /// `/sitemap.xml` handler rebuilds its XML body per request exactly as
    /// pre-149 (GP 11 / GP 13). `true` memoises the generated body keyed on
    /// the universe digest, so repeated crawler polls within a content
    /// generation skip rebuilding the (potentially large) XML; the body is
    /// byte-identical either way. The conditional-GET validators
    /// (`ETag` / `Last-Modified` / `Cache-Control` + `304`) are emitted
    /// unconditionally — they need no opt-in. Set via
    /// `withSitemapResponseCache`. Applies to the default site; each
    /// satellite gets its own cache instance.
    SitemapResponseCache: bool
    /// Phase 150 — universal-lastmod fallback. `None` (default) → entries
    /// with no `PublishedAt` (incl. Phase 95 dynamic slugs) emit no
    /// `<lastmod>`, byte-for-byte pre-150 (GP 11). `Some date` stamps every
    /// such entry with `date` (`yyyy-MM-dd`). Set via
    /// `withSitemapDefaultLastmod`.
    SitemapDefaultLastmod: System.DateTimeOffset option
    /// Phase 150 — URL-count threshold above which `sitemap.xml` becomes a
    /// `<sitemapindex>` + `sitemap-<name>.xml` shard files. Default 50,000
    /// (the sitemaps.org per-file cap). Set via `withSitemapShardThreshold`.
    SitemapShardThreshold: int
    /// Phase 150 — optional cluster key for logical-cluster shards (a
    /// changed cluster re-fetches only its own child sitemap). `None`
    /// (default) → deterministic numeric slices past the threshold. Set via
    /// `withSitemapClusterKey`.
    SitemapClusterKey: (Slug -> string) option
    /// Phase 157 — opt-in static client-search index endpoint. `None`
    /// (default) → no endpoint mounted, byte-for-byte pre-157 (GP 11 /
    /// GP 13). `Some config` mounts a compact content-versioned JSON index
    /// over the public universe at `config.Path`. Set via `withSearchIndex`.
    SearchIndex: SearchIndexConfig option
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
        IndexNow = IndexNowOptions.defaults
        Sites = []
        ConditionalGet = None
        SelfCanonical = false
        SitemapResponseCache = false
        SitemapDefaultLastmod = None
        SitemapShardThreshold = 50_000
        SitemapClusterKey = None
        SearchIndex = None
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
        IndexNow = IndexNowOptions.defaults
        Sites = []
        ConditionalGet = None
        SelfCanonical = false
        SitemapResponseCache = false
        SitemapDefaultLastmod = None
        SitemapShardThreshold = 50_000
        SitemapClusterKey = None
        SearchIndex = None
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

    /// Phase 92 — register a layout authored in the Feliz DSL
    /// (`Feliz.ViewEngine`). Slots into the same registry as Giraffe
    /// layouts via `FelizLayout.toGiraffe` — same naming, same
    /// fallback semantics, same render path (the adapter's output is
    /// raw pre-rendered HTML; the handler still adds the doctype and
    /// runs head injection). Giraffe.ViewEngine remains the default
    /// layout DSL; this helper is the opt-in for deployments sharing
    /// Feliz component code with a Fable SPA.
    let withFelizLayout
        (name: LayoutName)
        (layout: PublicPage -> Feliz.ViewEngine.ReactElement)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        withLayout name (FelizLayout.toGiraffe layout) app

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

    /// Phase 147 — opt into cache-independent conditional-GET emission.
    /// Makes a deterministic SSR site emit `ETag` (weak, content-hash) +
    /// content-stable `Last-Modified` + `Cache-Control` on the *uncached*
    /// serve path and honour `If-None-Match` / `If-Modified-Since` with a
    /// `304` — WITHOUT a render cache. Crawl-budget revalidation is
    /// orthogonal to ISR caching: a large deterministic catalog gets edge-
    /// cacheability + cheap re-crawls without paying for an ISR tier.
    ///
    /// Off by default (GP 11 / GP 13): a pipeline that never calls this has
    /// the pre-147 uncached path (no validators), byte-for-byte. No effect
    /// when `withRenderCache` is also composed — the cached path already
    /// emits validators. Uses `ConditionalGetSettings.defaults`
    /// (`Cache-Control: public, max-age=0, must-revalidate`); use
    /// `withConditionalGetCacheControl` for a custom `Cache-Control`.
    let withConditionalGet (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            ConditionalGet = Some ConditionalGetSettings.defaults
    }

    /// Phase 147 — `withConditionalGet` with an explicit `Cache-Control`
    /// for the uncached conditional path (e.g. `"public, max-age=300"` to
    /// let edges hold the page for 5 minutes between revalidations).
    let withConditionalGetCacheControl
        (cacheControl: string)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                ConditionalGet = Some { CacheControl = cacheControl }
        }

    /// Phase 109 — opt into IndexNow push-indexing. Registers an
    /// ownership-key endpoint at `/{key}.txt`, an `IIndexNowService` in DI
    /// (the manual / ops resubmit entry point + the per-slug publish ping),
    /// and — when `SubmitOnStartup` — an `IHostedService` that fires a
    /// resumable bulk submission of the whole public URL universe at
    /// startup (fire-and-forget; never blocks startup). On a successful
    /// publish through `publish_narrative` / the CMS publisher, the
    /// just-written slug is pushed immediately.
    ///
    /// Off by default (GP 11 / GP 13): a pipeline that never calls this is
    /// byte-for-byte the pre-109 shape — no route, no hosted service, no
    /// allocation. The submission rides the SAME URL universe the sitemap
    /// emits (`SitemapGenerator.entries`), so the push channel and the
    /// sitemap can never disagree.
    ///
    ///     // single-instance, host + key derived automatically:
    ///     |> PublicRenderingServerApp.withIndexNow
    ///         { IndexNowOptions.enabled with Host = Some "example.com" }
    ///     // multi-instance (shared blob-backed resume state):
    ///     |> PublicRenderingServerApp.withIndexNow
    ///         { IndexNowOptions.enabled with
    ///             StateStore = Some(BlobIndexNowStateStore.create storage) }
    let withIndexNow (options: IndexNowOptions) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            IndexNow = options
    }

    /// Phase 114 — register a satellite website served by this instance.
    /// The site claims its `Hosts` (case-insensitive, port ignored) and
    /// brings its own content root, base URL, layouts (empty → inherit
    /// the shared `withLayout` registrations), and redirects; requests on
    /// any other host — including every request in a zero-`withSite`
    /// composition — serve the default (`ServerConfig`-level) site
    /// unchanged (GP 11). Call once per site:
    ///
    ///     |> PublicRenderingServerApp.withSite
    ///         (PublicSite.create "docs" [ "docs.example.com" ]
    ///             "https://docs.example.com" (ContentRoot docsContent))
    ///
    /// v1 scope: satellites are markdown-file-backed (`content/**/*.md`
    /// + `redirects.csv`); entity overlay, content sources, search,
    /// taxonomy, and AI publish remain default-site surfaces (Atom
    /// feeds went per-site in Phase 115 via `PublicSiteDef.Feeds`).
    let withSite (site: PublicSiteDef) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            Sites = app.Sites @ [ site ]
    }

    /// Phase 148 — emit a self-referencing `<link rel="canonical">` for
    /// every rendered page (all body kinds), not just Narrative pages
    /// carrying an explicit `CanonicalUrl`. The canonical resolves to the
    /// page's own absolute URL under the site's base URL (satellite-aware:
    /// a page served on a `withSite` host self-canonicalises to that host's
    /// `BaseUrl`). An explicit canonical — a Narrative `CanonicalUrl` or a
    /// `head:canonical` envelope — always wins, so a page is never
    /// double-canonicalised.
    ///
    /// Implemented by wrapping the resolved `IPublicContentApi` so each
    /// resolved page gains a `head:canonical` frontmatter key (the Phase
    /// 111 envelope), which the page handler's existing head-injection step
    /// emits before `</head>` — so the tag reaches the wire without editing
    /// any layout. Off by default (GP 11): a pipeline that never calls this
    /// renders byte-for-byte the pre-148 head output.
    let withSelfCanonical (app: PublicRenderingServerApp) : PublicRenderingServerApp = { app with SelfCanonical = true }

    /// Phase 149 — opt into the sitemap response cache: the `/sitemap.xml`
    /// handler memoises its generated XML body keyed on the universe digest,
    /// so repeated crawler polls within a content generation skip rebuilding
    /// the (potentially large) body. The conditional-GET validators
    /// (`ETag` / `Last-Modified` / `Cache-Control` + `304`) are emitted
    /// unconditionally and need no opt-in; this only adds the body memo.
    ///
    /// Off by default (GP 11 / GP 13): a pipeline that never calls this
    /// rebuilds the body per request, byte-for-byte pre-149. The cached body
    /// is byte-identical to the uncached one. Applies to the default site;
    /// each satellite site gets its own cache instance.
    let withSitemapResponseCache (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            SitemapResponseCache = true
    }

    /// Phase 150 — give every sitemap URL a `<lastmod>` by stamping any
    /// entry with no `PublishedAt` (incl. Phase 95 dynamic slugs) with
    /// `date` (formatted `yyyy-MM-dd`; typically the deploy / content-version
    /// timestamp). Off by default → signal-less entries emit no `<lastmod>`,
    /// byte-for-byte pre-150 (GP 11). Applies to the runtime sitemap +
    /// shards and the static export.
    let withSitemapDefaultLastmod
        (date: System.DateTimeOffset)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        {
            app with
                SitemapDefaultLastmod = Some date
        }

    /// Phase 150 — set the URL-count threshold above which `sitemap.xml`
    /// becomes a `<sitemapindex>` + `sitemap-<name>.xml` shard files.
    /// Defaults to 50,000 (the sitemaps.org per-file cap). Below the
    /// threshold the sitemap stays a single `<urlset>`, byte-for-byte
    /// pre-150.
    let withSitemapShardThreshold (threshold: int) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            SitemapShardThreshold = threshold
    }

    /// Phase 150 — supply a cluster key so the sitemap index shards along
    /// logical content types (a changed cluster re-fetches only its own
    /// child sitemap) instead of blind numeric slices. `keyOf slug` names
    /// the shard a slug belongs to (e.g. the first path segment). Only takes
    /// effect past the threshold.
    let withSitemapClusterKey (keyOf: Slug -> string) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            SitemapClusterKey = Some keyOf
    }

    /// Phase 157 — mount the static client-search index endpoint with the
    /// default config (`/search-index.json` over the file-backed universe).
    /// Off by default → no endpoint (GP 11 / GP 13). Per-site aware: under a
    /// `SiteRegistry` each site indexes its own universe.
    let withSearchIndex (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            SearchIndex = Some SearchIndexConfig.defaults
    }

    /// Phase 157 — mount the search-index endpoint with an explicit config
    /// (custom path / `Cache-Control` / consumer `EntrySource`).
    let withSearchIndexConfig (config: SearchIndexConfig) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            SearchIndex = Some config
    }

    /// Toggle the dev-mode hot-reload watcher. Defaults to `true`.
    /// Production deployments typically set `false` since content
    /// is baked at deploy time and a long-lived watcher leaks file
    /// handles on read-only file systems.
    let withHotReload (enabled: bool) (app: PublicRenderingServerApp) : PublicRenderingServerApp = {
        app with
            HotReload = enabled
    }

    // ─── Preflight validators (Investigate-gaps #5 + #8) ───────────────

    /// Warn when an in-process `InMemoryRenderCache` is composed on a
    /// declared multi-instance deployment (`ReplicaCount > 1`). The
    /// cache dict is process-local, so each replica caches and
    /// invalidates SSR pages independently — one replica can serve stale
    /// HTML while another serves fresh, and a CMS slug-purge only clears
    /// the replica that handled it. Warning (not Error): a deployment may
    /// knowingly run a per-replica cache. Compose
    /// `BlobRenderCache.create storage` to share the cache across replicas.
    type private RenderCacheInstanceValidator(usingInMemory: bool, replicaCount: int) =
        interface ConfigValidation.IConfigValidator with
            member _.Name = "public-rendering-render-cache-instance"
            member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

            member _.Validate() = async {
                if usingInMemory && replicaCount > 1 then
                    return
                        ConfigValidation.Warning(
                            sprintf
                                "PublicRendering is composed with the in-process InMemoryRenderCache and ServerConfig.ReplicaCount = %d. The cache is process-local: each replica caches and invalidates SSR pages independently — one replica can serve stale HTML while another serves fresh, and a CMS slug-purge only clears the replica that handled it. Compose BlobRenderCache.create storage (shared across replicas) for multi-instance deployments."
                                replicaCount
                        )
                else
                    return ConfigValidation.Ok
            }

    /// Reject a non-positive sitemap shard threshold at startup. A
    /// `withSitemapShardThreshold 0` (or negative) is silently accepted
    /// at compose and only defensively clamped (`max 1`) at request time,
    /// so the misconfiguration is invisible until a crawler hits
    /// `/sitemap.xml`. Error: a non-positive threshold is never intended.
    type private SitemapShardThresholdValidator(threshold: int) =
        interface ConfigValidation.IConfigValidator with
            member _.Name = "public-rendering-sitemap-shard-threshold"
            member _.Timeout = ConfigValidation.IConfigValidator.defaultTimeout

            member _.Validate() = async {
                if threshold <= 0 then
                    return
                        ConfigValidation.Error(
                            sprintf
                                "PublicRenderingServerApp.withSitemapShardThreshold was set to %d. The shard threshold is the URL count above which sitemap.xml becomes a sitemap-index; it must be a positive integer (the sitemaps.org per-file cap is 50,000). Set a positive value or leave the default."
                                threshold
                        )
                else
                    return ConfigValidation.Ok
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

            // Preflight validators (Investigate-gaps #5 + #8). Registered
            // before the rest of compose so they ride the standard
            // startup aggregator: the render-cache one warns on a
            // process-local cache under declared multi-instance, the
            // sitemap one fails closed on a non-positive shard threshold.
            let usingInMemoryRenderCache =
                match app.RenderCache with
                | Some cache ->
                    match box cache with
                    | :? InMemoryRenderCache -> true
                    | _ -> false
                | None -> false

            let app =
                app
                |> withConfigValidator (
                    RenderCacheInstanceValidator(usingInMemoryRenderCache, app.Base.Config.ReplicaCount)
                    :> ConfigValidation.IConfigValidator
                )
                |> withConfigValidator (
                    SitemapShardThresholdValidator(app.SitemapShardThreshold) :> ConfigValidation.IConfigValidator
                )

            let layouts = app.Layouts
            let composeRedirects = app.Redirects
            let hotReload = app.HotReload
            let explicitApi = app.ContentApiOverride
            let selfCanonical = app.SelfCanonical // Phase 148
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
            let conditionalGetSettings = app.ConditionalGet // Phase 147
            let indexNowOptions = app.IndexNow // Phase 109
            let satelliteDefs = app.Sites // Phase 114
            let sitemapResponseCacheEnabled = app.SitemapResponseCache // Phase 149

            // Phase 150 — the sharding + universal-lastmod knobs, shared by
            // the runtime sitemap/shard handlers and the static export.
            let sitemapSharding: SitemapGenerator.SitemapShardingOptions = {
                Threshold = app.SitemapShardThreshold
                ClusterKey = app.SitemapClusterKey
                DefaultLastmod = app.SitemapDefaultLastmod |> Option.map (fun d -> d.ToString("yyyy-MM-dd"))
            }

            let searchIndexConfig = app.SearchIndex // Phase 157

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

            // Phase 114 — build the satellite-site registry once at compose
            // time (per-site loader + file-tier content API; layouts fall
            // back to the shared map) and register the startup validator.
            // `None` when no sites are composed — the handler chain below
            // then uses the pre-114 single-site construction byte-for-byte
            // (GP 11) and nothing here allocates (GP 13).
            let siteRegistry =
                if List.isEmpty satelliteDefs then
                    None
                else
                    Some(SiteRegistry.build satelliteDefs layouts prLogger hotReload)

            let appWithEntity =
                if List.isEmpty satelliteDefs then
                    appWithEntity
                else
                    ServerApp.withConfigValidator (MultiSiteConfigValidator(satelliteDefs, layouts)) appWithEntity

            // Phase 109 — resolve the IndexNow host + ownership key once at
            // compose time. Host falls back to the sitemap's public base URL;
            // the key is derived from a STABLE seed (the host) so the
            // `/{key}.txt` endpoint and every submission's keyLocation agree
            // and the key never churns on a content edit.
            let indexNowHost = IndexNow.resolveHost indexNowOptions publicBaseUrl
            let indexNowKey = IndexNow.resolveKey indexNowOptions indexNowHost

            // IndexNow needs a resolvable host (an explicit `Host` or a
            // `PublicBaseUrl` to derive one from) to produce the absolute
            // URLs + `keyLocation` the spec requires. Enabled-but-no-host is
            // a misconfiguration: warn once and treat as inactive rather than
            // emit relative URLs every engine would reject.
            let indexNowActive =
                indexNowOptions.Enabled && not (System.String.IsNullOrWhiteSpace indexNowHost)

            if indexNowOptions.Enabled && not indexNowActive then
                prLogger.Warn
                    "IndexNow: enabled but no host could be resolved (set IndexNowOptions.Host or ServerConfig.PublicBaseUrl); IndexNow is inactive."

            // Phase 115 — per-site IndexNow: each satellite gets its own
            // declared host (from its BaseUrl), its own stable ownership
            // key (host-seeded, same derivation contract as the default
            // site), and its own resumable submission over its own page
            // universe. Satellites with no resolvable host are skipped
            // (the multi-site validator already warns on a bad BaseUrl).
            let satelliteIndexNow: (SiteRuntime * string * string) list =
                match siteRegistry with
                | Some registry when indexNowActive ->
                    registry.Sites
                    |> List.choose (fun site ->
                        let siteHost = IndexNow.hostFromBaseUrl site.Def.BaseUrl

                        if System.String.IsNullOrWhiteSpace siteHost then
                            None
                        else
                            Some(site, siteHost, IndexNow.resolveKey indexNowOptions siteHost))
                | _ -> []

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
                            let api =
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

                                    PublicContentApiImpl.create loader entityStore (contentSources @ taxonomySources)

                            // Phase 148 — wrap so a self-referencing canonical
                            // reaches the wire for the default site without
                            // editing any layout (the wrap adds a
                            // `head:canonical` envelope the page handler emits).
                            if selfCanonical then
                                NarrativeLayout.SelfCanonical.wrap publicBaseUrl api
                            else
                                api)
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

                                // Phase 109 — resolve the IndexNow service (if
                                // composed) so a successful publish pings the
                                // slug. `None` when no IndexNow is wired.
                                let indexNowSvc =
                                    sp.GetService(typeof<IIndexNowService>)
                                    |> Option.ofObj
                                    |> Option.map (fun x -> x :?> IIndexNowService)

                                PublicRenderingNarrativePagePublisher.create
                                    entityStore
                                    registeredLayoutNames
                                    renderCacheInvalidator
                                    aiPublishGuardrails
                                    indexNowSvc)
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
                |> fun s ->
                    // Phase 147 — cache-independent conditional-GET. When
                    // `withConditionalGet` is composed (and no render cache is
                    // registered), the page handler's uncached path emits
                    // validators + honours 304 on seeing this in DI. Absent →
                    // the uncached path is byte-for-byte pre-147 (GP 11 / 13).
                    match conditionalGetSettings with
                    | Some cg -> s.AddSingleton<ConditionalGetSettings>(cg)
                    | None -> s
                |> fun s ->
                    // Phase 109 — IndexNow registrations. When active,
                    // register the `IIndexNowService` (the publish-ping +
                    // manual / ops resubmit surface) and — when
                    // `SubmitOnStartup` — an `IHostedService` that fires the
                    // resumable bulk submission once at startup. When inactive
                    // (not composed / disabled / no host), nothing is
                    // registered → no service, no hosted service (GP 11 / 13).
                    if not indexNowActive then
                        s
                    else
                        // One shared `HttpClient` for the whole submission
                        // surface (avoids socket exhaustion). Captured by the
                        // singleton service factory below.
                        let httpClient =
                            new System.Net.Http.HttpClient(Timeout = System.TimeSpan.FromSeconds 30.0)

                        let stateStore =
                            indexNowOptions.StateStore
                            |> Option.defaultWith (fun () -> FileIndexNowStateStore.create ())

                        let s =
                            s.AddSingleton<IIndexNowService>(
                                System.Func<System.IServiceProvider, IIndexNowService>(fun sp ->
                                    let metrics =
                                        match sp.GetService(typeof<ToolUp.Platform.Metrics.IMetricsSink>) with
                                        | :? ToolUp.Platform.Metrics.IMetricsSink as m -> m
                                        | _ ->
                                            ToolUp.Platform.Metrics.NoOpMetricsSink()
                                            :> ToolUp.Platform.Metrics.IMetricsSink

                                    let postFn body =
                                        IndexNow.postWith httpClient indexNowOptions.Endpoint body

                                    // Resolve the content API + sources at call
                                    // time so any decorator participates and a
                                    // republish since startup is reflected in
                                    // the submitted universe.
                                    let universe () = async {
                                        let api = sp.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                                        let! pages = api.ListPages ""
                                        let! dyn = ContentSource.enumerateAll contentSources
                                        return SitemapGenerator.entries pages dyn
                                    }

                                    IndexNowService(
                                        indexNowOptions,
                                        indexNowHost,
                                        indexNowKey,
                                        publicBaseUrl,
                                        stateStore,
                                        postFn,
                                        metrics,
                                        prLogger,
                                        universe
                                    )
                                    :> IIndexNowService)
                            )

                        let s =
                            if indexNowOptions.SubmitOnStartup then
                                s.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                                    System.Func<System.IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>
                                        (fun sp ->
                                            let svc = sp.GetService(typeof<IIndexNowService>) :?> IIndexNowService

                                            IndexNowStartupService(svc, prLogger)
                                            :> Microsoft.Extensions.Hosting.IHostedService)
                                )
                            else
                                s

                        // Phase 115 — one startup submission per satellite
                        // site, each over its own host / key / universe.
                        // Satellite state rides a per-site file store (an
                        // operator-supplied `StateStore` applies to the
                        // default site only — documented limitation).
                        if not indexNowOptions.SubmitOnStartup then
                            s
                        else
                            (s, satelliteIndexNow)
                            ||> List.fold (fun s (site, siteHost, siteKey) ->
                                let safeName =
                                    site.Def.Name
                                    |> String.map (fun c ->
                                        if System.Char.IsLetterOrDigit c || c = '-' then c else '-')

                                let siteStatePath =
                                    System.IO.Path.Combine(
                                        System.IO.Path.GetTempPath(),
                                        sprintf "toolup-indexnow-last-submission-%s.json" safeName
                                    )

                                s.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(
                                    System.Func<System.IServiceProvider, Microsoft.Extensions.Hosting.IHostedService>
                                        (fun sp ->
                                            let metrics =
                                                match sp.GetService(typeof<ToolUp.Platform.Metrics.IMetricsSink>) with
                                                | :? ToolUp.Platform.Metrics.IMetricsSink as m -> m
                                                | _ ->
                                                    ToolUp.Platform.Metrics.NoOpMetricsSink()
                                                    :> ToolUp.Platform.Metrics.IMetricsSink

                                            let postFn body =
                                                IndexNow.postWith httpClient indexNowOptions.Endpoint body

                                            let universe () = async {
                                                let! pages = site.Api.ListPages ""
                                                return SitemapGenerator.entries pages []
                                            }

                                            let svc =
                                                IndexNowService(
                                                    indexNowOptions,
                                                    siteHost,
                                                    siteKey,
                                                    site.Def.BaseUrl,
                                                    FileIndexNowStateStore.createAt siteStatePath,
                                                    postFn,
                                                    metrics,
                                                    prLogger,
                                                    universe
                                                )
                                                :> IIndexNowService

                                            IndexNowStartupService(svc, prLogger)
                                            :> Microsoft.Extensions.Hosting.IHostedService)
                                ))
                |> fun s ->
                    // Phase 114 — expose the satellite-site registry to
                    // consumer-supplied custom handlers. Not registered when
                    // no sites are composed (GP 13).
                    match siteRegistry with
                    | Some registry -> s.AddSingleton<SiteRegistry>(registry)
                    | None -> s

            // ─── Handler chain ───────────────────────────────────
            // Order: sitemap (specific route) → redirect (path-match
            // short-circuit) → export (?format= short-circuit) →
            // page (catch-all by slug, default HTML). All resolve
            // `IPublicContentApi` + `MarkdownContentLoader` per-request
            // from `ctx.RequestServices` so the DI singleton is shared.
            // Phase 149 — conditional-GET sitemap handler options. The
            // validators (ETag / Last-Modified / Cache-Control + 304) are
            // always emitted; the response cache is opt-in and gets one
            // instance per logical site (default site here; satellites get
            // their own below) so the single-slot memo never thrashes
            // between sites.
            let defaultSitemapOptions: SitemapGenerator.SitemapHandlerOptions = {
                SitemapGenerator.SitemapHandlerOptions.defaults with
                    ResponseCache =
                        if sitemapResponseCacheEnabled then
                            Some(SitemapGenerator.SitemapCache())
                        else
                            None
                    Sharding = sitemapSharding // Phase 150
            }

            // Phase 150 — per-site base carries the sharding knobs even when
            // the response cache is off; the per-site map below adds a cache
            // instance per site when the cache is enabled.
            let siteSitemapBaseOptions: SitemapGenerator.SitemapHandlerOptions = {
                SitemapGenerator.SitemapHandlerOptions.defaults with
                    Sharding = sitemapSharding
            }

            let siteSitemapOptions: Map<string, SitemapGenerator.SitemapHandlerOptions> =
                match siteRegistry with
                | Some registry when sitemapResponseCacheEnabled ->
                    registry.Sites
                    |> List.map (fun site ->
                        site.Def.Name,
                        {
                            siteSitemapBaseOptions with
                                ResponseCache = Some(SitemapGenerator.SitemapCache())
                        })
                    |> Map.ofList
                | _ -> Map.empty

            let sitemapHandler: HttpHandler =
                route "/sitemap.xml"
                >=> fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    // Phase 95 — include content-source-enumerated dynamic
                    // routes (e.g. `/tag/{x}`) so crawlers discover them.
                    let enumerate () =
                        ContentSource.enumerateAll contentSources

                    SitemapGenerator.handlerWith defaultSitemapOptions publicBaseUrl api enumerate next ctx

            // Phase 150 — shard-file handler (`/sitemap-<name>.xml`). Active
            // only past the threshold; declines (falls through) otherwise.
            let sitemapShardHandler: HttpHandler =
                fun next ctx ->
                    let api =
                        ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                    let enumerate () =
                        ContentSource.enumerateAll contentSources

                    SitemapGenerator.shardHandler defaultSitemapOptions publicBaseUrl api enumerate next ctx

            // Phase 157 — search-index endpoint. Mounted only when
            // `withSearchIndex` is composed; declines otherwise (no route).
            let searchIndexHandler: HttpHandler =
                match searchIndexConfig with
                | None -> fun _ _ -> skipPipeline
                | Some config ->
                    fun next ctx ->
                        let api =
                            ctx.RequestServices.GetService(typeof<IPublicContentApi>) :?> IPublicContentApi

                        let enumerate () =
                            ContentSource.enumerateAll contentSources

                        SearchIndexEmitter.handler config publicBaseUrl api enumerate next ctx

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
                        // Decline via `skipPipeline` (None) so the
                        // surrounding `choose` advances; `next ctx`
                        // would end the chain as an unwritten 200.
                        | _ -> skipPipeline
                  ]

            // Phase 109 — IndexNow ownership-key endpoint `/{key}.txt`.
            // Mounted before the catch-all page handler; it claims only the
            // exact key slug and declines (falls through) for any other
            // `/*.txt`. Empty when IndexNow is inactive (GP 11 / 13).
            let indexNowHandlers: HttpHandler list =
                if not indexNowActive then
                    []
                else
                    match siteRegistry with
                    | None -> [ IndexNowKeyHandler.routes indexNowKey ]
                    | Some registry ->
                        // Phase 115 — each satellite host answers its own
                        // ownership key (the keyLocation its submissions
                        // declare); unmatched hosts serve the default key.
                        let satelliteKeyHandlers =
                            satelliteIndexNow
                            |> List.map (fun (site, _, siteKey) ->
                                SiteGate.forSite registry site.Def.Name (IndexNowKeyHandler.routes siteKey))

                        satelliteKeyHandlers
                        @ [ SiteGate.forDefaultSite registry (IndexNowKeyHandler.routes indexNowKey) ]

            // Phase 114 — host-aware dispatch. When a satellite-site
            // registry exists, the per-request content surfaces (sitemap /
            // redirects / narrative export / preview / page) resolve the
            // site claiming the request's host and serve from its content
            // API + layouts + redirects + base URL; unmatched hosts fall
            // through to the default handlers built above. With no sites
            // registered, every handler below IS the default handler —
            // byte-for-byte the pre-114 chain (GP 11). Feeds, semantic
            // search, taxonomy, and IndexNow remain default-site surfaces
            // in v1 (Phase 115 lifts the SEO/export set per-site).
            let siteAware (forSite: SiteRuntime -> HttpHandler) (defaultHandler: HttpHandler) : HttpHandler =
                match siteRegistry with
                | None -> defaultHandler
                | Some registry ->
                    fun next ctx ->
                        match SiteRegistry.tryResolve ctx registry with
                        | Some site -> forSite site next ctx
                        | None -> defaultHandler next ctx

            let sitemapHandler =
                siteAware
                    (fun site ->
                        let opts =
                            siteSitemapOptions
                            |> Map.tryFind site.Def.Name
                            |> Option.defaultValue siteSitemapBaseOptions

                        route "/sitemap.xml"
                        >=> SitemapGenerator.handlerWith opts site.Def.BaseUrl site.Api (fun () -> async.Return []))
                    sitemapHandler

            // Phase 150 — per-site shard handler (host-aware). Each site
            // shards its own universe past the threshold; the default-site
            // shard handler serves unmatched hosts.
            let sitemapShardHandler =
                siteAware
                    (fun site ->
                        let opts =
                            siteSitemapOptions
                            |> Map.tryFind site.Def.Name
                            |> Option.defaultValue siteSitemapBaseOptions

                        SitemapGenerator.shardHandler opts site.Def.BaseUrl site.Api (fun () -> async.Return []))
                    sitemapShardHandler

            // Phase 157 — per-site search index (host-aware). Each site
            // indexes its own universe; unmatched hosts serve the default.
            let searchIndexHandler =
                match searchIndexConfig with
                | None -> searchIndexHandler
                | Some config ->
                    siteAware
                        (fun site ->
                            SearchIndexEmitter.handler config site.Def.BaseUrl site.Api (fun () -> async.Return []))
                        searchIndexHandler

            let redirectHandler =
                siteAware
                    (fun site ->
                        fun next ctx ->
                            // Per-request read so hot-reloaded redirects.csv
                            // edits flow through, matching the default path.
                            RedirectMap.handler (site.Loader.Redirects @ site.Def.Redirects) next ctx)
                    redirectHandler

            let exportHandler =
                siteAware (fun site -> NarrativeExportHandler.handler site.Api) exportHandler

            let previewHandler =
                siteAware (fun site -> ContentPreview.previewHandler site.Api site.Layouts) previewHandler

            let pageHandler =
                siteAware
                    (fun site ->
                        // Phase 148 — satellite self-canonical resolves to the
                        // satellite's own `BaseUrl`, so a page served on a
                        // `withSite` host self-canonicalises to that host.
                        let siteApi =
                            if selfCanonical then
                                NarrativeLayout.SelfCanonical.wrap site.Def.BaseUrl site.Api
                            else
                                site.Api

                        PublicPageHandler.handlerKeyed (Some site.Def.Name) siteApi site.Layouts)
                    pageHandler

            // Phase 115 — feeds become host-scoped when sites exist:
            // compose-level `withFeed` registrations serve the default
            // site's hosts only, and each site's `PublicSiteDef.Feeds`
            // mount host-gated against that site's own content API. With
            // no sites, feed handlers are untouched (GP 11).
            let feedHandlers =
                match siteRegistry with
                | None -> feedHandlers
                | Some registry -> feedHandlers |> List.map (SiteGate.forDefaultSite registry)

            let satelliteFeedHandlers: HttpHandler list =
                match siteRegistry with
                | None -> []
                | Some registry ->
                    registry.Sites
                    |> List.collect (fun site ->
                        site.Def.Feeds
                        |> List.map (fun feedConfig ->
                            let feedRoute =
                                route feedConfig.SelfUrl
                                >=> fun next ctx -> NarrativeFeedHandler.handler feedConfig site.Api next ctx

                            SiteGate.forSite registry site.Def.Name feedRoute))

            let publicRenderingHandlers =
                [ sitemapHandler; sitemapShardHandler ] // Phase 150 — shard route after the index
                @ indexNowHandlers
                @ [ redirectHandler; exportHandler ]
                @ feedHandlers
                @ satelliteFeedHandlers
                @ searchHandlers
                @ [ searchIndexHandler ] // Phase 157 — search-index endpoint (declines when not composed)
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
    /// Phase 150 — the compose-level sitemap sharding + universal-lastmod
    /// knobs (`withSitemapDefaultLastmod` / `withSitemapShardThreshold` /
    /// `withSitemapClusterKey`) as a `SitemapShardingOptions`, so the static
    /// export emits the index + shards on the same rules the runtime handler
    /// uses.
    let private appSitemapSharding (app: PublicRenderingServerApp) : SitemapGenerator.SitemapShardingOptions = {
        Threshold = app.SitemapShardThreshold
        ClusterKey = app.SitemapClusterKey
        DefaultLastmod = app.SitemapDefaultLastmod |> Option.map (fun d -> d.ToString("yyyy-MM-dd"))
    }

    let exportStaticWith
        (options: StaticExportOptions)
        (outputDir: string)
        (app: PublicRenderingServerApp)
        : Async<int> =
        StaticExport.runWith
            {
                options with
                    SitemapSharding = appSitemapSharding app
            } // Phase 150
            app.Redirects
            app.Base.Config
            app.Layouts
            app.ContentApiOverride
            app.ContentSources
            app.Base.Logger
            outputDir

    /// Phase 115 — multi-site static export. Writes the default site to
    /// `outputDir` (byte-for-byte `exportStaticWith` — GP 11) and each
    /// `withSite` satellite to `outputDir/sites/<Name>/`, every tree
    /// rendered with that site's content root + layouts (shared fallback)
    /// + redirects and a `sitemap.xml` on that site's `BaseUrl`. Host-
    /// config emission (`options.HostConfigs`) applies per tree from that
    /// tree's redirects. Satellites export file-tier content only (no
    /// entity overlay / content sources — the Phase 114 scope cut).
    /// Returns the total page count across every tree.
    let exportStaticAllWith
        (options: StaticExportOptions)
        (outputDir: string)
        (app: PublicRenderingServerApp)
        : Async<int> =
        async {
            let! total = exportStaticWith options outputDir app

            let mutable sum = total

            for site in app.Sites do
                let siteConfig = {
                    app.Base.Config with
                        PublicRendering = EnabledPublicRendering site.ContentRoot
                        PublicBaseUrl = Some site.BaseUrl
                }

                let siteLayouts =
                    if Map.isEmpty site.Layouts then
                        app.Layouts
                    else
                        site.Layouts

                let siteDir = System.IO.Path.Combine(outputDir, "sites", site.Name)

                let! n =
                    StaticExport.runWith
                        {
                            options with
                                SitemapSharding = appSitemapSharding app
                        } // Phase 150
                        site.Redirects
                        siteConfig
                        siteLayouts
                        None
                        []
                        app.Base.Logger
                        siteDir

                sum <- sum + n

            return sum
        }

    /// Phase 115 — `exportStaticAllWith` with default options. The
    /// multi-site analogue of `exportStatic`.
    let exportStaticAll (outputDir: string) (app: PublicRenderingServerApp) : Async<int> =
        exportStaticAllWith StaticExportOptions.defaults outputDir app

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