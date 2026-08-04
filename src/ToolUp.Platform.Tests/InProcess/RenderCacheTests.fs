module ToolUp.Platform.Tests.InProcess.RenderCacheTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.PublicRendering
open ToolUp.Platform.Tests.Contracts

// ─── Phase 84 — render-cache tests ──────────────────────────────────
//
// Three layers:
//   1. IRenderCacheContract bound to both shipped impls
//      (InMemoryRenderCache + BlobRenderCache over an in-memory blob
//      store).
//   2. CachePolicy.parse + RenderedPage.hash unit tests.
//   3. PublicPageHandler integration over a DefaultHttpContext: cache
//      miss → hit (no re-resolve), 304 on If-None-Match, header
//      emission, the cache: off default, the no-cache-composed (pre-84)
//      path, and stale-while-revalidate serving.

// ─── 1. Contract bindings ───────────────────────────────────────────

let private contractTests =
    testList "IRenderCache — contract bindings" [
        IRenderCacheContract.tests "InMemoryRenderCache" InMemoryRenderCache.create
        IRenderCacheContract.tests "BlobRenderCache" (fun () ->
            BlobRenderCache.create (InMemoryBlobStorage.InMemoryBlobStorage()))
    ]

// ─── 2. Policy + hash unit tests ────────────────────────────────────

let private policyTests =
    testList "CachePolicy.parse + RenderedPage.hash" [

        testCase "parse None / off / none / 0 / empty → NoCache"
        <| fun _ ->
            Expect.equal (CachePolicy.parse None) CachePolicy.NoCache "absent key"
            Expect.equal (CachePolicy.parse (Some "off")) CachePolicy.NoCache "off"
            Expect.equal (CachePolicy.parse (Some "none")) CachePolicy.NoCache "none"
            Expect.equal (CachePolicy.parse (Some "0")) CachePolicy.NoCache "zero"
            Expect.equal (CachePolicy.parse (Some "  ")) CachePolicy.NoCache "blank"

        testCase "parse a positive integer → Cache(ttl, swr=true) (SWR on by default)"
        <| fun _ -> Expect.equal (CachePolicy.parse (Some "300")) (CachePolicy.Cache(300, true)) "300s, SWR default on"

        testCase "parse '300;no-swr' disables stale-while-revalidate"
        <| fun _ ->
            Expect.equal (CachePolicy.parse (Some "300;no-swr")) (CachePolicy.Cache(300, false)) "explicit no-swr"

        testCase "parse '300;swr' keeps SWR on"
        <| fun _ -> Expect.equal (CachePolicy.parse (Some "300;swr")) (CachePolicy.Cache(300, true)) "explicit swr"

        testCase "parse a non-numeric / negative value → NoCache (fail-safe)"
        <| fun _ ->
            Expect.equal (CachePolicy.parse (Some "abc")) CachePolicy.NoCache "garbage degrades to NoCache"
            Expect.equal (CachePolicy.parse (Some "-5")) CachePolicy.NoCache "negative degrades to NoCache"

        testCase "hash is deterministic and content-sensitive"
        <| fun _ ->
            Expect.equal
                (RenderedPage.hash "<html>a</html>")
                (RenderedPage.hash "<html>a</html>")
                "same input → same hash"

            Expect.notEqual
                (RenderedPage.hash "<html>a</html>")
                (RenderedPage.hash "<html>b</html>")
                "different input → different hash"
    ]

// ─── 3. Handler integration ─────────────────────────────────────────

let private layouts: Map<LayoutName, PublicPage -> XmlNode> =
    Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

let private mkPage (slug: string) (cacheFm: string option) : PublicPage = {
    Slug = Slug slug
    Title = $"Title-{slug}"
    Description = ""
    Body = Html $"body-{slug}"
    Layout = LayoutName "page"
    Frontmatter =
        match cacheFm with
        | Some c -> Map["cache", c]
        | None -> Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

/// Content-API stub counting `GetPageInContext` calls so a test can
/// assert "resolved once, served from cache thereafter".
let private mkApi (pages: Map<string, PublicPage>) (resolveCount: int ref) : IPublicContentApi =
    { new IPublicContentApi with
        member _.GetPage slug = async { return Map.tryFind slug pages }
        member _.ListPages _ = async { return pages |> Map.toList |> List.map snd }

        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix

        member _.GetCollection _ = async { return [] }

        member _.GetPageInContext(slug, _ctx) = async {
            resolveCount.Value <- resolveCount.Value + 1
            return Map.tryFind slug pages
        }
    }

type private HResult = {
    Status: int
    Body: string
    ETag: string option
    CacheControl: string option
    LastModified: string option
}

let private runReq
    (cache: IRenderCache option)
    (settings: RenderCacheSettings)
    (api: IPublicContentApi)
    (path: string)
    (ifNoneMatch: string option)
    : HResult =
    let services = ServiceCollection()
    services.AddSingleton<IMetricsSink>(NoOpMetricsSink() :> IMetricsSink) |> ignore

    match cache with
    | Some c ->
        services.AddSingleton<IRenderCache>(c) |> ignore
        services.AddSingleton<RenderCacheSettings>(settings) |> ignore
    | None -> ()

    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Request.Path <- PathString(path)

    match ifNoneMatch with
    | Some v -> ctx.Request.Headers["If-None-Match"] <- StringValues v
    | None -> ()

    let respBody = new MemoryStream()
    ctx.Response.Body <- respBody

    let finalFunc: HttpFunc = fun c -> Task.FromResult(Some c)
    let h = PublicPageHandler.handler api layouts
    (h finalFunc ctx).GetAwaiter().GetResult() |> ignore

    respBody.Position <- 0L
    let text = (new StreamReader(respBody)).ReadToEnd()

    let hdr (k: string) =
        match ctx.Response.Headers.TryGetValue k with
        | true, v -> Some(v.ToString())
        | _ -> None

    {
        Status = ctx.Response.StatusCode
        Body = text
        ETag = hdr "ETag"
        CacheControl = hdr "Cache-Control"
        LastModified = hdr "Last-Modified"
    }

let private noSettings = RenderCacheSettings.defaults

let private handlerTests =
    testList "PublicPageHandler — Phase 84 cache integration" [

        testCase "no cache composed → pre-84 path: 200, body, NO cache headers (GP 11/13)"
        <| fun _ ->
            let count = ref 0
            let api = mkApi (Map["about", mkPage "about" (Some "300")]) count
            let r = runReq None noSettings api "/about" None

            Expect.equal r.Status 200 "served"
            Expect.stringContains r.Body "Title-about" "body rendered"
            Expect.isNone r.ETag "no ETag header when no cache is composed"
            Expect.isNone r.CacheControl "no Cache-Control header when no cache is composed"

        testCase "cache: 300 → miss then hit: resolves once, second request served from cache"
        <| fun _ ->
            let count = ref 0
            let api = mkApi (Map["about", mkPage "about" (Some "300")]) count
            let cache = InMemoryRenderCache.create ()

            let r1 = runReq (Some cache) noSettings api "/about" None
            let r2 = runReq (Some cache) noSettings api "/about" None

            Expect.equal r1.Status 200 "first request 200"
            Expect.equal r2.Status 200 "second request 200"
            Expect.equal count.Value 1 "backing content resolved exactly once (second served from cache)"
            Expect.stringContains r2.Body "Title-about" "cached body served"
            Expect.isSome r1.ETag "ETag emitted on the cached path"

            match r1.CacheControl with
            | Some cc -> Expect.stringContains cc "max-age=300" "Cache-Control carries the TTL"
            | None -> failtest "expected a Cache-Control header"

        testCase "If-None-Match matching the ETag → 304 with empty body"
        <| fun _ ->
            let count = ref 0
            let api = mkApi (Map["about", mkPage "about" (Some "300")]) count
            let cache = InMemoryRenderCache.create ()

            let r1 = runReq (Some cache) noSettings api "/about" None
            let etag = r1.ETag.Value
            let r2 = runReq (Some cache) noSettings api "/about" (Some etag)

            Expect.equal r2.Status 304 "matching If-None-Match → 304"
            Expect.equal r2.Body "" "304 carries no body"
            Expect.equal r2.ETag (Some etag) "304 still echoes the ETag"

        testCase "cache: off (default) with cache composed → 200, headers emitted, but never stored"
        <| fun _ ->
            let count = ref 0
            // No `cache:` frontmatter and default policy NoCache.
            let api = mkApi (Map["about", mkPage "about" None]) count
            let cache = InMemoryRenderCache.create ()

            let r1 = runReq (Some cache) noSettings api "/about" None
            let r2 = runReq (Some cache) noSettings api "/about" None

            Expect.equal r1.Status 200 "served"
            Expect.equal count.Value 2 "an off route re-resolves every request (nothing cached)"
            Expect.isSome r1.ETag "ETag still emitted for an off route (enables 304 on file/overlay pages)"
            Expect.equal r1.CacheControl (Some "no-cache") "off route advertises no-cache"

        testCase "withRenderCacheDefaultPolicy caches a frontmatter-less page"
        <| fun _ ->
            let count = ref 0
            let api = mkApi (Map["dyn", mkPage "dyn" None]) count
            let cache = InMemoryRenderCache.create ()

            let settings = {
                RenderCacheSettings.defaults with
                    DefaultPolicy = CachePolicy.Cache(120, true)
            }

            let _ = runReq (Some cache) settings api "/dyn" None
            let _ = runReq (Some cache) settings api "/dyn" None

            Expect.equal count.Value 1 "default policy caches a page with no cache: frontmatter"

        testCase "a pre-seeded stale SWR entry is served stale (200, stale body)"
        <| fun _ ->
            let count = ref 0
            let api = mkApi (Map["about", mkPage "about" (Some "300")]) count
            let cache = InMemoryRenderCache.create ()

            // Seed an already-expired SWR entry directly.
            let key: RenderKey = {
                Slug = "about"
                ScopeId = "public"
                ContentVersion = ""
            }

            let stale =
                RenderedPage.forStore "<stale-marker/>" (DateTimeOffset.UtcNow.AddSeconds(-30.0))

            cache.Set key stale (CachePolicy.Cache(1, true)) |> Async.RunSynchronously

            let r = runReq (Some cache) noSettings api "/about" None

            Expect.equal r.Status 200 "stale entry is still served"
            Expect.stringContains r.Body "stale-marker" "the stale body is served (background refresh is detached)"

        testCase "scope isolation — anonymous requests share the public partition"
        <| fun _ ->
            // Two anonymous requests resolve to the same "public" scope key,
            // so the second is a cache hit (resolve count stays 1).
            let count = ref 0
            let api = mkApi (Map["about", mkPage "about" (Some "300")]) count
            let cache = InMemoryRenderCache.create ()
            let _ = runReq (Some cache) noSettings api "/about" None
            let _ = runReq (Some cache) noSettings api "/about" None
            Expect.equal count.Value 1 "anonymous requests share one public cache entry"
    ]

let tests =
    testList "RenderCache (Phase 84)" [ contractTests; policyTests; handlerTests ]