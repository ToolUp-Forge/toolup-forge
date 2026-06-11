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

    let private etagOf (hash: string) = "\"" + hash + "\""

    /// True when the request's `If-None-Match` matches the current ETag
    /// (or is the wildcard `*`). Honours a comma-separated list and an
    /// unquoted hash for tolerance.
    let private ifNoneMatchMatches (ctx: HttpContext) (hash: string) : bool =
        match ctx.Request.Headers.TryGetValue "If-None-Match" with
        | true, values ->
            let etag = etagOf hash

            values
            |> Seq.collect (fun v -> (v: string).Split(',') |> Array.map _.Trim())
            |> Seq.exists (fun candidate -> candidate = etag || candidate = hash || candidate = "*")
        | _ -> false

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

    let private setCacheHeaders (ctx: HttpContext) (hash: string) (cacheControl: string) (renderedAt: DateTimeOffset) =
        ctx.Response.Headers["ETag"] <- StringValues(etagOf hash)
        ctx.Response.Headers["Last-Modified"] <- StringValues(renderedAt.UtcDateTime.ToString("R"))
        ctx.Response.Headers["Cache-Control"] <- StringValues cacheControl

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

    /// The Phase 84 cached path: cache lookup, stale-while-revalidate,
    /// HTTP cache headers, and `If-None-Match` → `304`.
    let private serveCached
        (api: IPublicContentApi)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (cache: IRenderCache)
        (settings: RenderCacheSettings)
        (metrics: IMetricsSink)
        (slug: string)
        (accessContext: AccessContext)
        (ctx: HttpContext)
        : System.Threading.Tasks.Task<HttpContext option> =
        task {
            let key: RenderKey = {
                Slug = slug
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
                                        let refreshed = {
                                            RenderedPage.forStore html DateTimeOffset.UtcNow with
                                                Audience = page.Audience
                                        }

                                        do! cache.Set key refreshed (policyForPage settings page)
                                    | _ -> ()
                                with _ ->
                                    ()
                            }
                        )

                    if ifNoneMatchMatches ctx entry.ContentHash then
                        setCacheHeaders ctx entry.ContentHash (cacheControlForEntry entry) entry.RenderedAt
                        ctx.Response.StatusCode <- 304
                        return Some ctx
                    else
                        setCacheHeaders ctx entry.ContentHash (cacheControlForEntry entry) entry.RenderedAt
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
                    let rendered = {
                        RenderedPage.forStore html renderedAt with
                            Audience = page.Audience
                    }

                    // Store only when the policy opts in (Set is a no-op
                    // for NoCache, but skip the await to keep the off
                    // path allocation-light).
                    match policy with
                    | CachePolicy.NoCache -> ()
                    | CachePolicy.Cache _ -> do! cache.Set key rendered policy

                    if ifNoneMatchMatches ctx rendered.ContentHash then
                        setCacheHeaders ctx rendered.ContentHash (cacheControlValue policy) renderedAt
                        ctx.Response.StatusCode <- 304
                        return Some ctx
                    else
                        setCacheHeaders ctx rendered.ContentHash (cacheControlValue policy) renderedAt
                        ctx.Response.ContentType <- "text/html; charset=utf-8"
                        return! ctx.WriteStringAsync html
                | NoLayoutRegistered ->
                    ctx.Response.StatusCode <- 500
                    return! ctx.WriteStringAsync "PublicRendering: no layout registered"
                | Unauthorized -> return! writeDenied ctx 401 "Unauthorized"
                | AccessForbidden -> return! writeDenied ctx 403 "Forbidden"
                | PageNotFound -> return None
        }

    let handler (api: IPublicContentApi) (layouts: Map<LayoutName, PublicPage -> XmlNode>) : HttpHandler =
        fun _next (ctx: HttpContext) ->
            let rawPath = ctx.Request.Path.Value
            let slug = rawPath.TrimStart('/')
            let slugOrIndex = if slug = "" then "index" else slug

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

                        serveCached api layouts cache settings metrics slugOrIndex accessContext ctx
                    | _ -> serveUncached api layouts slugOrIndex accessContext ctx

                sw.Stop()
                RenderMetrics.emitRender metrics (RenderMetrics.classifyOutcome ctx result) sw.Elapsed.TotalMilliseconds
                return result
            }