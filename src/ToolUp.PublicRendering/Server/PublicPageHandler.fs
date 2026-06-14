namespace ToolUp.PublicRendering

open System
open Giraffe
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open ToolUp.Platform
open ToolUp.Platform.Metrics

/// Catch-all `GET /{slug}` handler. The slug derives from the
/// request path with leading `/` stripped; root path `/` resolves
/// against the page slug `"index"`. Resolution goes through
/// `IPublicContentApi.GetPageInContext` (Phase 83) so request-time
/// `IContentSource` resolvers are consulted after the file + entity-
/// overlay tiers — the per-request `AccessContext` is resolved from
/// `ctx.RequestServices` and handed to each source. When resolution
/// returns `None`, the handler falls through to `next` — the
/// `RedirectMap` handler is expected to own the 301 fall-through
/// before a 404 lands.
///
/// Layout resolution: looks up `PublicPage.Layout` in the registered
/// layout map; falls back to the first-registered layout when the
/// named layout is unknown. A page with no layouts at all returns
/// a 500 ("no layout registered") — compose-time invariant rather
/// than a runtime expectation, surfaced as a hard error so a
/// mis-registered layout map can't masquerade as an empty page.
///
/// Phase 84 — when an `IRenderCache` is registered in DI (the deployment
/// called `withRenderCache`), the handler activates the ISR tier: it
/// looks up the cached render, serves a fresh hit without re-resolving,
/// serves a stale hit while refreshing in the background
/// (stale-while-revalidate), and emits `ETag` / `Last-Modified` /
/// `Cache-Control` headers — honouring `If-None-Match` with a `304`. When
/// no cache is registered the handler is byte-for-byte the pre-84 path
/// (no headers, no lookup, no allocation) per GP 11 / GP 13.
module PublicPageHandler =
    let private resolveLayout
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (page: PublicPage)
        : (PublicPage -> XmlNode) option =
        match Map.tryFind page.Layout layouts with
        | Some f -> Some f
        | None -> layouts |> Map.toSeq |> Seq.tryHead |> Option.map snd

    // ─── Phase 84 — render-cache helpers ─────────────────────────────

    /// Outcome of resolving + rendering a slug, independent of the
    /// `HttpContext` so a stale-while-revalidate background refresh can
    /// run it after the request has completed (it captures only `api`,
    /// `layouts`, the slug, and the `AccessContext` — all values).
    type private RenderOutcome =
        | Rendered of html: string * page: PublicPage
        | NoLayoutRegistered
        | PageNotFound
        /// Phase 86 — audience requires a resolved principal (→ 401).
        | Unauthorized
        /// Phase 86 — principal failed the audience role / relationship
        /// gate (→ 403).
        | AccessForbidden

    /// Resolve a slug through the Phase 83 chain, apply the Phase 89
    /// publish-visibility filter, run the Phase 86 audience authorization
    /// gate, and render the layout to an HTML document. Pure with respect
    /// to the `HttpContext` (used both on the request thread and on the SWR
    /// background path). The audience gate runs *before* rendering so a
    /// forbidden request never pays the render cost.
    let private resolveAndRender
        (api: IPublicContentApi)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (slug: string)
        (accessContext: AccessContext)
        : Async<RenderOutcome> =
        async {
            let! pageOpt = api.GetPageInContext(slug, accessContext)

            let visiblePage =
                pageOpt |> Option.filter (PublicPage.isPubliclyVisible DateTimeOffset.UtcNow)

            match visiblePage with
            | Some page ->
                match AudienceGate.evaluate accessContext page.Audience with
                | AudienceDecision.RequireAuthentication -> return Unauthorized
                | AudienceDecision.Forbidden -> return AccessForbidden
                | AudienceDecision.Allow ->
                    match resolveLayout layouts page with
                    | Some layout ->
                        // Phase 111 — inject any per-request head metadata
                        // (the `head:*` frontmatter envelope a resolved-
                        // content source attached) into the rendered
                        // document. Pages without the envelope pass
                        // through untouched (GP 11), and because this
                        // runs inside `resolveAndRender` the cached /
                        // stale-while-revalidate copies store the
                        // injected document — forge's render cache owns
                        // the fragment path end to end.
                        let html =
                            layout page
                            |> RenderView.AsString.htmlDocument
                            |> PageHeadInjection.injectFromPage page

                        return Rendered(html, page)
                    | None -> return NoLayoutRegistered
            | None -> return PageNotFound
        }

    /// Resolve the cache policy for a freshly-rendered page: an explicit
    /// `cache:` frontmatter key wins; otherwise the compose-level default
    /// (which is `NoCache` unless the deployment raised it).
    let private policyForPage (settings: RenderCacheSettings) (page: PublicPage) : CachePolicy =
        match page.Frontmatter.TryFind "cache" with
        | Some raw -> CachePolicy.parse (Some raw)
        | None -> settings.DefaultPolicy

    /// The storage-scope id a request caches under. Anonymous requests
    /// share the `"public"` partition; authenticated / team / claim
    /// requests cache under their resolved scope container so one
    /// deployment's tenants never share a cache entry (GP 4).
    let private scopeIdOf (accessContext: AccessContext) : string =
        AccessContext.configScope accessContext
        |> Option.map _.Container
        |> Option.defaultValue "public"

    // Phase 155 — `etagOf` / the `If-None-Match` gate / the validator-
    // header emission moved to the handler-agnostic `ConditionalGet`
    // combinator; the page handler now calls
    // `ConditionalGet.isNotModified` / `ConditionalGet.setValidators`
    // inline (see the cached + uncached serve paths below).

    /// Phase 147 — the content-stable `Last-Modified` for a page: its
    /// explicit `PublishedAt` when present, otherwise the deploy-generation
    /// stamp — NOT the wall-clock render moment. Truncated to whole seconds
    /// so the emitted header round-trips equal against the
    /// `If-Modified-Since` a crawler echoes back, and coherent with the
    /// sitemap `<lastmod>` (which also derives from `PublishedAt`). A
    /// deterministic page must present the same `Last-Modified` across
    /// refreshes / restarts or `If-Modified-Since` can never `304`.
    let private contentStableLastModified (page: PublicPage) : DateTimeOffset =
        page.PublishedAt
        |> Option.defaultValue ConditionalGet.deployStamp
        |> ConditionalGet.toHttpSeconds

    /// Phase 147 — the `Last-Modified` a stored entry presents: the
    /// content-stable value persisted with it. Entries written before
    /// Phase 147 deserialise `LastModified` to the default (pre-epoch);
    /// fall back to `RenderedAt` for those so an upgraded deployment's
    /// pre-existing cache entries still emit a sane header.
    let private entryLastModified (entry: RenderedPage) : DateTimeOffset =
        if entry.LastModified < DateTimeOffset.UnixEpoch then
            entry.RenderedAt
        else
            entry.LastModified

    let private cacheControlValue (policy: CachePolicy) : string =
        match policy with
        | CachePolicy.NoCache -> "no-cache"
        | CachePolicy.Cache(ttl, swr) ->
            if swr then
                $"public, max-age=%d{ttl}, stale-while-revalidate=%d{ttl}"
            else
                $"public, max-age=%d{ttl}"

    /// Reconstruct the wire `Cache-Control` a stored entry was written
    /// under (the cache never stores a `NoCache` policy, so the entry's
    /// remaining TTL window + SWR flag reproduce its policy).
    let private cacheControlForEntry (entry: RenderedPage) : string =
        let ttl = (entry.ExpiresAt - entry.RenderedAt).TotalSeconds |> max 0.0 |> int

        cacheControlValue (CachePolicy.Cache(ttl, entry.StaleWhileRevalidate))

    let private emitCacheMetric (metrics: IMetricsSink) (outcome: string) = RenderMetrics.emitCache metrics outcome

    /// Phase 86 — write a bare audience-denial response. Gated pages set
    /// `X-Robots-Tag: noindex` and `Cache-Control: no-store` so a denied
    /// response is never indexed or shared-cached.
    let private writeDenied
        (ctx: HttpContext)
        (code: int)
        (body: string)
        : System.Threading.Tasks.Task<HttpContext option> =
        ctx.Response.StatusCode <- code
        ctx.Response.Headers["X-Robots-Tag"] <- StringValues "noindex"
        ctx.Response.Headers["Cache-Control"] <- StringValues "no-store"
        ctx.WriteStringAsync body

    /// The pre-84 path: resolve, filter, render, write — no cache lookup,
    /// no cache headers. Byte-for-byte identical to the handler before
    /// Phase 84, so a deployment that never composes `withRenderCache`
    /// is unchanged (GP 11 / GP 13).
    let private serveUncached
        (api: IPublicContentApi)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (slug: string)
        (accessContext: AccessContext)
        (ctx: HttpContext)
        : System.Threading.Tasks.Task<HttpContext option> =
        task {
            let! outcome = resolveAndRender api layouts slug accessContext

            match outcome with
            | Rendered(html, _page) ->
                ctx.Response.ContentType <- "text/html; charset=utf-8"
                return! ctx.WriteStringAsync html
            | NoLayoutRegistered ->
                ctx.Response.StatusCode <- 500
                return! ctx.WriteStringAsync "PublicRendering: no layout registered"
            | Unauthorized -> return! writeDenied ctx 401 "Unauthorized"
            | AccessForbidden -> return! writeDenied ctx 403 "Forbidden"
            | PageNotFound -> return None
        }

    /// Phase 147 — the cache-independent conditional-GET path: resolve,
    /// filter, render, then emit the conditional-GET validators (weak
    /// `ETag` + content-stable `Last-Modified` + `Cache-Control`) and
    /// honour `If-None-Match` / `If-Modified-Since` with a `304` — with NO
    /// render cache registered. Active only when the deployment composed
    /// `withConditionalGet` (a `ConditionalGetSettings` is in DI); without
    /// it, the handler runs `serveUncached`, byte-for-byte the pre-147 path
    /// (GP 11). Crawl-budget revalidation is orthogonal to ISR caching and
    /// available without it.
    let private serveUncachedConditional
        (api: IPublicContentApi)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (settings: ConditionalGetSettings)
        (slug: string)
        (accessContext: AccessContext)
        (ctx: HttpContext)
        : System.Threading.Tasks.Task<HttpContext option> =
        task {
            let! outcome = resolveAndRender api layouts slug accessContext

            match outcome with
            | Rendered(html, page) ->
                let hash = RenderedPage.hash html
                let lastModified = contentStableLastModified page

                if ConditionalGet.isNotModified ctx hash lastModified then
                    ConditionalGet.setValidators ctx hash lastModified settings.CacheControl
                    ctx.Response.StatusCode <- 304
                    return Some ctx
                else
                    ConditionalGet.setValidators ctx hash lastModified settings.CacheControl
                    ctx.Response.ContentType <- "text/html; charset=utf-8"
                    return! ctx.WriteStringAsync html
            | NoLayoutRegistered ->
                ctx.Response.StatusCode <- 500
                return! ctx.WriteStringAsync "PublicRendering: no layout registered"
            | Unauthorized -> return! writeDenied ctx 401 "Unauthorized"
            | AccessForbidden -> return! writeDenied ctx 403 "Forbidden"
            | PageNotFound -> return None
        }

    /// The Phase 84 cached path: cache lookup, stale-while-revalidate,
    /// HTTP cache headers, and `If-None-Match` / `If-Modified-Since` →
    /// `304`.
    let private serveCached
        (api: IPublicContentApi)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (cache: IRenderCache)
        (settings: RenderCacheSettings)
        (metrics: IMetricsSink)
        (cacheKeySlug: string)
        (slug: string)
        (accessContext: AccessContext)
        (ctx: HttpContext)
        : System.Threading.Tasks.Task<HttpContext option> =
        task {
            // Phase 114 — `cacheKeySlug` may carry a per-site prefix so two
            // sites sharing a slug (e.g. "index") never share a cache
            // entry; single-site pipelines pass the slug through unchanged.
            let key: RenderKey = {
                Slug = cacheKeySlug
                ScopeId = scopeIdOf accessContext
                ContentVersion = ""
            }

            let! cached = cache.TryGet key

            match cached with
            | Some entry ->
                // Phase 86 — re-run the audience gate on the cache hit using
                // the entry's stored audience. Entries are keyed by scope,
                // so a gated page is cached per-scope; re-gating here still
                // enforces role differentiation *within* a scope (two team
                // members where only one holds the gating role) on every
                // hit. `Public` entries always `Allow`, so a public cached
                // page is unaffected.
                match AudienceGate.evaluate accessContext entry.Audience with
                | AudienceDecision.RequireAuthentication -> return! writeDenied ctx 401 "Unauthorized"
                | AudienceDecision.Forbidden -> return! writeDenied ctx 403 "Forbidden"
                | AudienceDecision.Allow ->
                    let stale = DateTimeOffset.UtcNow >= entry.ExpiresAt
                    emitCacheMetric metrics (if stale then "stale" else "hit")

                    // Stale-while-revalidate: serve the stale render now and
                    // refresh the entry on a detached background task. The
                    // refresh captures only values (api / layouts / key /
                    // settings / accessContext) so it is safe after the
                    // request's `HttpContext` is gone.
                    if stale && entry.StaleWhileRevalidate then
                        Async.Start(
                            async {
                                try
                                    match! resolveAndRender api layouts slug accessContext with
                                    | Rendered(html, page) ->
                                        // Phase 147 — the refresh recomputes the same
                                        // content-stable `Last-Modified` for the page, so
                                        // a stale-while-revalidate refresh never churns
                                        // the validator.
                                        let refreshed = {
                                            RenderedPage.forStore html DateTimeOffset.UtcNow with
                                                Audience = page.Audience
                                                LastModified = contentStableLastModified page
                                        }

                                        do! cache.Set key refreshed (policyForPage settings page)
                                    | _ -> ()
                                with _ ->
                                    ()
                            }
                        )

                    let entryLm = entryLastModified entry

                    if ConditionalGet.isNotModified ctx entry.ContentHash entryLm then
                        ConditionalGet.setValidators ctx entry.ContentHash entryLm (cacheControlForEntry entry)
                        ctx.Response.StatusCode <- 304
                        return Some ctx
                    else
                        ConditionalGet.setValidators ctx entry.ContentHash entryLm (cacheControlForEntry entry)
                        ctx.Response.ContentType <- "text/html; charset=utf-8"
                        return! ctx.WriteStringAsync entry.Html

            | None ->
                emitCacheMetric metrics "miss"
                let! outcome = resolveAndRender api layouts slug accessContext

                match outcome with
                | Rendered(html, page) ->
                    let renderedAt = DateTimeOffset.UtcNow
                    let policy = policyForPage settings page

                    // Phase 86 — carry the page's audience into the stored
                    // entry so a cache hit can re-gate without re-resolving.
                    // Phase 147 — stamp the content-stable `Last-Modified`
                    // so a later cache hit reproduces this render's exact
                    // validator (and a SWR refresh never churns it).
                    let lastModified = contentStableLastModified page

                    let rendered = {
                        RenderedPage.forStore html renderedAt with
                            Audience = page.Audience
                            LastModified = lastModified
                    }

                    // Store only when the policy opts in (Set is a no-op
                    // for NoCache, but skip the await to keep the off
                    // path allocation-light).
                    match policy with
                    | CachePolicy.NoCache -> ()
                    | CachePolicy.Cache _ -> do! cache.Set key rendered policy

                    if ConditionalGet.isNotModified ctx rendered.ContentHash lastModified then
                        ConditionalGet.setValidators ctx rendered.ContentHash lastModified (cacheControlValue policy)
                        ctx.Response.StatusCode <- 304
                        return Some ctx
                    else
                        ConditionalGet.setValidators ctx rendered.ContentHash lastModified (cacheControlValue policy)
                        ctx.Response.ContentType <- "text/html; charset=utf-8"
                        return! ctx.WriteStringAsync html
                | NoLayoutRegistered ->
                    ctx.Response.StatusCode <- 500
                    return! ctx.WriteStringAsync "PublicRendering: no layout registered"
                | Unauthorized -> return! writeDenied ctx 401 "Unauthorized"
                | AccessForbidden -> return! writeDenied ctx 403 "Forbidden"
                | PageNotFound -> return None
        }

    /// Phase 114 — handler variant whose render-cache entries are
    /// namespaced by `cacheKeyPrefix` (the multi-site compose passes the
    /// site name). `None` reproduces the pre-114 key exactly, so
    /// single-site deployments' cache entries are untouched (GP 11).
    let handlerKeyed
        (cacheKeyPrefix: string option)
        (api: IPublicContentApi)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        : HttpHandler =
        fun _next (ctx: HttpContext) ->
            let rawPath = ctx.Request.Path.Value
            let slug = rawPath.TrimStart('/')
            let slugOrIndex = if slug = "" then "index" else slug

            let cacheKeySlug =
                match cacheKeyPrefix with
                | Some prefix -> prefix + "::" + slugOrIndex
                | None -> slugOrIndex

            // Phase 83 — resolve the per-request `AccessContext` from DI
            // (registered scoped by the SDK middleware). Fall back to an
            // unrestricted anonymous context when absent, matching the
            // convention in `BuildRouteHandlers` — a public content site
            // running without auth resolves every request as anonymous.
            let accessContext =
                match ctx.RequestServices.GetService(typeof<AccessContext>) with
                | :? AccessContext as ac -> ac
                | _ -> AccessContext.unrestricted (AnonymousSession "anonymous")

            // Phase 93 — render observability. Resolve the sink once
            // (NoOp when none composed → every emit is free, GP 13) and
            // wrap the serve with a render-duration + outcome metric. The
            // Stopwatch is the only always-on cost (negligible; the same
            // trade-off as RequestTimingMiddleware).
            let metrics =
                match ctx.RequestServices.GetService(typeof<IMetricsSink>) with
                | :? IMetricsSink as m -> m
                | _ -> NoOpMetricsSink() :> IMetricsSink

            let sw = System.Diagnostics.Stopwatch.StartNew()

            task {
                // Phase 84 — the render cache is active only when registered
                // (the deployment called `withRenderCache`). Absent → the
                // pre-84 path runs unchanged (GP 11 / GP 13).
                let! result =
                    match ctx.RequestServices.GetService(typeof<IRenderCache>) with
                    | :? IRenderCache as cache ->
                        let settings =
                            match ctx.RequestServices.GetService(typeof<RenderCacheSettings>) with
                            | :? RenderCacheSettings as s -> s
                            | _ -> RenderCacheSettings.defaults

                        serveCached api layouts cache settings metrics cacheKeySlug slugOrIndex accessContext ctx
                    | _ ->
                        // Phase 147 — no render cache, but the deployment may
                        // have opted into cache-independent conditional-GET
                        // (`withConditionalGet`). When a `ConditionalGetSettings`
                        // is in DI, emit validators + honour 304 on the uncached
                        // path; otherwise the pre-147 path runs unchanged (GP 11).
                        match ctx.RequestServices.GetService(typeof<ConditionalGetSettings>) with
                        | :? ConditionalGetSettings as cgSettings ->
                            serveUncachedConditional api layouts cgSettings slugOrIndex accessContext ctx
                        | _ -> serveUncached api layouts slugOrIndex accessContext ctx

                sw.Stop()
                let outcomeTag = RenderMetrics.classifyOutcome ctx result
                RenderMetrics.emitRender metrics outcomeTag sw.Elapsed.TotalMilliseconds

                // Phase 153 — crawler / SEO observability. Gate the
                // User-Agent classification + the new emits on a live sink
                // so the substring scan + UA read are free under the NoOp
                // sink (GP 13). The conditional-GET counter is emitted only
                // for a real page response (200 / 304); not-found
                // fall-throughs increment the soft-404 counter instead.
                if not (metrics :? NoOpMetricsSink) then
                    let ua = ctx.Request.Headers.UserAgent.ToString()
                    RenderMetrics.emitAgent metrics (RenderMetrics.classifyAgent ua)

                    match outcomeTag with
                    | RenderMetrics.OutcomeNotModified ->
                        RenderMetrics.emitConditionalGet metrics RenderMetrics.CondGet304
                    | RenderMetrics.OutcomeRendered -> RenderMetrics.emitConditionalGet metrics RenderMetrics.CondGet200
                    | RenderMetrics.OutcomeNotFound -> RenderMetrics.emitNotFound metrics
                    | _ -> ()

                return result
            }

    let handler (api: IPublicContentApi) (layouts: Map<LayoutName, PublicPage -> XmlNode>) : HttpHandler =
        handlerKeyed None api layouts