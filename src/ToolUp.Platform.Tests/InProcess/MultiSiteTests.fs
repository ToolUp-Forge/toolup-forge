module ToolUp.Platform.Tests.InProcess.MultiSiteTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose

// ─── Phase 114 — multi-host site registry ────────────────────────────
//
// Covers the spine: host → site resolution (case-insensitive, port
// ignored, unknown-host fallback), shared-layout inheritance, per-site
// page serving + sitemap origins, render-cache key namespacing across
// sites, and the startup validator's error/warning matrix.

// ─── Fixtures ────────────────────────────────────────────────────────

let private mkSiteRoot (title: string) : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-multisite-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore

    File.WriteAllText(Path.Combine(dir, "index.md"), sprintf "---\ntitle: %s\nlayout: page\n---\n\nHome body." title)

    dir

let private sharedLayouts: Map<LayoutName, PublicPage -> XmlNode> =
    Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

let private logger = ConsoleLogger.ConsoleLogger() :> ILogger

let private mkSite (name: string) (hosts: string list) (baseUrl: string) (title: string) : PublicSiteDef =
    PublicSite.create name hosts baseUrl (ContentRoot(mkSiteRoot title))

let private mkRegistry (sites: PublicSiteDef list) : SiteRegistry =
    SiteRegistry.build sites sharedLayouts logger false

let private mkContext (host: string) (path: string) (services: IServiceProvider option) : DefaultHttpContext =
    let ctx = DefaultHttpContext()

    ctx.RequestServices <-
        match services with
        | Some sp -> sp
        | None -> ServiceCollection().BuildServiceProvider()

    ctx.Request.Host <- HostString host
    ctx.Request.Path <- PathString path
    ctx.Response.Body <- new MemoryStream()
    ctx

let private finalFunc: Giraffe.Core.HttpFunc = fun c -> Task.FromResult(Some c)

let private responseBody (ctx: DefaultHttpContext) : string =
    ctx.Response.Body.Seek(0L, SeekOrigin.Begin) |> ignore
    use reader = new StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

let private runValidator (v: MultiSiteConfigValidator) : ValidationResult =
    (v :> IConfigValidator).Validate() |> Async.RunSynchronously

// ─── 1. Host resolution ──────────────────────────────────────────────

let private resolutionTests =
    testList "SiteRegistry.tryResolve" [
        testCase "resolves a claimed host case-insensitively"
        <| fun _ ->
            let registry =
                mkRegistry [ mkSite "a" [ "SiteA.example" ] "https://sitea.example" "Site A" ]

            let ctx = mkContext "sitea.EXAMPLE" "/" None

            match SiteRegistry.tryResolve ctx registry with
            | Some rt -> Expect.equal rt.Def.Name "a" "site a claimed the host"
            | None -> failtest "expected a site match"

        testCase "ignores the request port"
        <| fun _ ->
            let registry =
                mkRegistry [ mkSite "a" [ "sitea.example" ] "https://sitea.example" "Site A" ]

            let ctx = mkContext "sitea.example" "/" None
            ctx.Request.Host <- HostString("sitea.example", 8080)

            Expect.isSome (SiteRegistry.tryResolve ctx registry) "host matched with port present"

        testCase "unknown host → None (default-site fallback)"
        <| fun _ ->
            let registry =
                mkRegistry [ mkSite "a" [ "sitea.example" ] "https://sitea.example" "Site A" ]

            let ctx = mkContext "other.example" "/" None
            Expect.isNone (SiteRegistry.tryResolve ctx registry) "unmatched host falls through"

        testCase "a site with its own layouts keeps them; an empty map inherits the shared layouts"
        <| fun _ ->
            let bespoke: Map<LayoutName, PublicPage -> XmlNode> =
                Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str ("bespoke:" + p.Title) ] ]))]

            let siteA = mkSite "a" [ "a.example" ] "https://a.example" "A"

            let siteB = {
                mkSite "b" [ "b.example" ] "https://b.example" "B" with
                    Layouts = bespoke
            }

            let registry = mkRegistry [ siteA; siteB ]
            let rtA = registry.ByHost["a.example"]
            let rtB = registry.ByHost["b.example"]

            Expect.isTrue (obj.ReferenceEquals(rtA.Layouts, sharedLayouts)) "empty map inherits shared layouts"
            Expect.isFalse (obj.ReferenceEquals(rtB.Layouts, sharedLayouts)) "bespoke layouts are kept"
    ]

// ─── 2. Per-site page serving + sitemap ──────────────────────────────

let private servingTests =
    testList "per-site serving" [
        testCase "the same slug serves different content per site"
        <| fun _ ->
            let registry =
                mkRegistry [
                    mkSite "a" [ "a.example" ] "https://a.example" "Site A Home"
                    mkSite "b" [ "b.example" ] "https://b.example" "Site B Home"
                ]

            let serve (host: string) =
                let rt = registry.ByHost[host]
                let ctx = mkContext host "/" None

                (PublicPageHandler.handlerKeyed (Some rt.Def.Name) rt.Api rt.Layouts finalFunc ctx)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                responseBody ctx

            Expect.stringContains (serve "a.example") "Site A Home" "site a serves its own index"
            Expect.stringContains (serve "b.example") "Site B Home" "site b serves its own index"

        testCase "each site's sitemap emits its own BaseUrl origin"
        <| fun _ ->
            let registry =
                mkRegistry [
                    mkSite "a" [ "a.example" ] "https://a.example" "Site A Home"
                    mkSite "b" [ "b.example" ] "https://b.example" "Site B Home"
                ]

            let sitemap (host: string) =
                let rt = registry.ByHost[host]
                let ctx = mkContext host "/sitemap.xml" None

                (SitemapGenerator.handler rt.Def.BaseUrl rt.Api (fun () -> async.Return []) finalFunc ctx)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                responseBody ctx

            let a = sitemap "a.example"
            let b = sitemap "b.example"
            Expect.stringContains a "<loc>https://a.example/" "site a origin"
            Expect.stringContains b "<loc>https://b.example/" "site b origin"
            Expect.isFalse (a.Contains "https://b.example") "site a sitemap carries no site-b URLs"

        testCase "render-cache entries are namespaced per site (no cross-site bleed on a shared slug)"
        <| fun _ ->
            let registry =
                mkRegistry [
                    mkSite "a" [ "a.example" ] "https://a.example" "Site A Home"
                    mkSite "b" [ "b.example" ] "https://b.example" "Site B Home"
                ]

            let cache = InMemoryRenderCache.create ()
            let services = ServiceCollection()
            services.AddSingleton<IRenderCache>(cache) |> ignore

            services.AddSingleton<RenderCacheSettings>(
                {
                    RenderCacheSettings.defaults with
                        DefaultPolicy = CachePolicy.Cache(300, false)
                }
            )
            |> ignore

            let provider = services.BuildServiceProvider() :> IServiceProvider

            let serve (host: string) =
                let rt = registry.ByHost[host]
                let ctx = mkContext host "/" (Some provider)

                (PublicPageHandler.handlerKeyed (Some rt.Def.Name) rt.Api rt.Layouts finalFunc ctx)
                    .GetAwaiter()
                    .GetResult()
                |> ignore

                responseBody ctx

            let a = serve "a.example"
            // Site b serves after site a primed the cache for slug "index";
            // a shared (un-namespaced) key would replay site a's render.
            let b = serve "b.example"
            Expect.stringContains a "Site A Home" "site a render"
            Expect.stringContains b "Site B Home" "site b render is not site a's cached entry"

            let entryA =
                cache.TryGet {
                    Slug = "a::index"
                    ScopeId = "public"
                    ContentVersion = ""
                }
                |> Async.RunSynchronously

            Expect.isSome entryA "cache key carries the site-name namespace"
    ]

// ─── 3. Startup validator ────────────────────────────────────────────

let private validatorTests =
    testList "MultiSiteConfigValidator" [
        testCase "well-formed sites → Ok"
        <| fun _ ->
            let sites = [
                mkSite "a" [ "a.example" ] "https://a.example" "A"
                mkSite "b" [ "b.example" ] "https://b.example" "B"
            ]

            Expect.equal (runValidator (MultiSiteConfigValidator(sites, sharedLayouts))) Ok "valid set"

        testCase "a host claimed by two sites → Error"
        <| fun _ ->
            let sites = [
                mkSite "a" [ "shared.example" ] "https://a.example" "A"
                mkSite "b" [ "SHARED.example" ] "https://b.example" "B"
            ]

            match runValidator (MultiSiteConfigValidator(sites, sharedLayouts)) with
            | Error msg -> Expect.stringContains msg "shared.example" "names the ambiguous host"
            | other -> failtestf "expected Error, got %A" other

        testCase "a site with no hosts → Error"
        <| fun _ ->
            let sites = [ mkSite "a" [] "https://a.example" "A" ]

            match runValidator (MultiSiteConfigValidator(sites, sharedLayouts)) with
            | Error msg -> Expect.stringContains msg "no hosts" "unreachable site is an error"
            | other -> failtestf "expected Error, got %A" other

        testCase "no site layouts and no shared layouts → Error"
        <| fun _ ->
            let sites = [ mkSite "a" [ "a.example" ] "https://a.example" "A" ]

            match runValidator (MultiSiteConfigValidator(sites, Map.empty)) with
            | Error msg -> Expect.stringContains msg "layouts" "layout-less site is an error"
            | other -> failtestf "expected Error, got %A" other

        testCase "non-absolute BaseUrl → Warning"
        <| fun _ ->
            let sites = [ mkSite "a" [ "a.example" ] "not-a-url" "A" ]

            match runValidator (MultiSiteConfigValidator(sites, sharedLayouts)) with
            | Warning msg -> Expect.stringContains msg "absolute" "names the malformed base URL"
            | other -> failtestf "expected Warning, got %A" other

        testCase "missing content root → Warning"
        <| fun _ ->
            let missing =
                Path.Combine(Path.GetTempPath(), "toolup-multisite-tests", "does-not-exist")

            let site = {
                mkSite "a" [ "a.example" ] "https://a.example" "A" with
                    ContentRoot = ContentRoot missing
            }

            match runValidator (MultiSiteConfigValidator([ site ], sharedLayouts)) with
            | Warning msg -> Expect.stringContains msg "does not exist" "names the missing root"
            | other -> failtestf "expected Warning, got %A" other
    ]

// ─── 4. Phase 115 — SiteGate + per-site SEO/export surfaces ──────────

let private siteGateTests =
    testList "SiteGate (Phase 115)" [
        testCase "forSite runs the handler only on that site's hosts"
        <| fun _ ->
            let registry =
                mkRegistry [
                    mkSite "a" [ "a.example" ] "https://a.example" "A"
                    mkSite "b" [ "b.example" ] "https://b.example" "B"
                ]

            let marker: Giraffe.Core.HttpHandler =
                fun _next ctx -> ctx.WriteStringAsync "gated-content"

            let gated = SiteGate.forSite registry "a" marker

            let run (host: string) =
                let ctx = mkContext host "/feed.atom" None
                let result = (gated finalFunc ctx).GetAwaiter().GetResult()
                result, responseBody ctx

            let servedResult, servedBody = run "a.example"
            Expect.isSome servedResult "site a result is Some (handled)"
            Expect.stringContains servedBody "gated-content" "site a serves the gated handler"

            // Fall-through must be a `None` (skipPipeline) result so the
            // surrounding `choose` advances — a `next ctx` fall-through
            // would return Some with an empty body (a committed empty
            // 200) and shadow every handler mounted after the gate.
            Expect.isNone (fst (run "b.example")) "site b falls through with None, not an empty 200"
            Expect.isNone (fst (run "other.example")) "default-site host falls through with None"

        testCase "forDefaultSite runs the handler only on unclaimed hosts"
        <| fun _ ->
            let registry = mkRegistry [ mkSite "a" [ "a.example" ] "https://a.example" "A" ]

            let marker: Giraffe.Core.HttpHandler =
                fun _next ctx -> ctx.WriteStringAsync "default-content"

            let gated = SiteGate.forDefaultSite registry marker

            let run (host: string) =
                let ctx = mkContext host "/" None
                let result = (gated finalFunc ctx).GetAwaiter().GetResult()
                result, responseBody ctx

            let servedResult, servedBody = run "other.example"
            Expect.isSome servedResult "unclaimed host result is Some (handled)"
            Expect.stringContains servedBody "default-content" "unclaimed host serves the default"
            Expect.isNone (fst (run "a.example")) "satellite host falls through with None, not an empty 200"

        testCase "NarrativeExportHandler declines with None when no format parameter is present"
        <| fun _ ->
            // Regression guard for the empty-200 swallow the
            // MultiSitePublic sample exposed: the export handler is
            // mounted in the router's `choose` BEFORE the feed / preview /
            // page handlers, so its no-format decline must be a `None`
            // (skipPipeline) result. Returning `next ctx` instead commits
            // an empty 200 (the no-op finisher) and shadows every page on
            // the site.
            let registry = mkRegistry [ mkSite "a" [ "a.example" ] "https://a.example" "A" ]
            let rt = registry.ByHost["a.example"]
            let ctx = mkContext "a.example" "/" None

            let result =
                (NarrativeExportHandler.handler rt.Api finalFunc ctx).GetAwaiter().GetResult()

            Expect.isNone result "no ?format= → None so the page handler can serve the request"
            Expect.equal (responseBody ctx) "" "nothing written on the decline path"
    ]

let private indexNowKeyTests =
    testList "per-site IndexNow keys (Phase 115)" [
        testCase "host-seeded keys differ per site and stay stable per host"
        <| fun _ ->
            let options = IndexNowOptions.enabled

            let keyA =
                IndexNow.resolveKey options (IndexNow.hostFromBaseUrl "https://a.example")

            let keyA2 =
                IndexNow.resolveKey options (IndexNow.hostFromBaseUrl "https://a.example/")

            let keyB =
                IndexNow.resolveKey options (IndexNow.hostFromBaseUrl "https://b.example")

            Expect.equal keyA keyA2 "key is stable for the same host (trailing slash irrelevant)"
            Expect.notEqual keyA keyB "different hosts derive different keys"

        testCase "per-site submission universes are disjoint"
        <| fun _ ->
            let registry =
                mkRegistry [
                    mkSite "a" [ "a.example" ] "https://a.example" "Site A Home"
                    mkSite "b" [ "b.example" ] "https://b.example" "Site B Home"
                ]

            let urlsOf (host: string) =
                let rt = registry.ByHost[host]
                let pages = rt.Api.ListPages "" |> Async.RunSynchronously

                IndexNow.urlsFor rt.Def.BaseUrl (SitemapGenerator.entries pages [])
                |> Set.ofList

            let a = urlsOf "a.example"
            let b = urlsOf "b.example"
            Expect.isNonEmpty (Set.toList a) "site a universe non-empty"
            Expect.isTrue (Set.intersect a b |> Set.isEmpty) "universes share no URL"
    ]

let private exportTests =
    testList "exportStaticAll (Phase 115)" [
        testCase "default tree at root; one subtree per satellite, each with its own sitemap origin"
        <| fun _ ->
            let defaultRoot = mkSiteRoot "Default Home"

            let config = {
                ServerConfig.defaults with
                    PublicBaseUrl = Some "https://main.example"
                    PublicRendering = EnabledPublicRendering(ContentRoot defaultRoot)
            }

            let outDir =
                Path.Combine(Path.GetTempPath(), "toolup-multisite-tests", "export-" + Guid.NewGuid().ToString("N"))

            let app =
                PublicRenderingServerApp.create ()
                |> PublicRenderingServerApp.withConfig config
                |> PublicRenderingServerApp.withLayout (LayoutName "page") (fun p ->
                    html [] [ body [] [ str p.Title ] ])
                |> PublicRenderingServerApp.withSite (mkSite "siteb" [ "b.example" ] "https://b.example" "B Home")

            let total =
                PublicRenderingServerApp.exportStaticAll outDir app |> Async.RunSynchronously

            Expect.isGreaterThanOrEqual total 2 "both trees rendered pages"

            let rootIndex = File.ReadAllText(Path.Combine(outDir, "index.html"))
            Expect.stringContains rootIndex "Default Home" "default site at export root"

            let siteIndex =
                File.ReadAllText(Path.Combine(outDir, "sites", "siteb", "index.html"))

            Expect.stringContains siteIndex "B Home" "satellite tree under sites/<name>"

            let rootSitemap = File.ReadAllText(Path.Combine(outDir, "sitemap.xml"))
            Expect.stringContains rootSitemap "https://main.example/" "default sitemap origin"

            let siteSitemap =
                File.ReadAllText(Path.Combine(outDir, "sites", "siteb", "sitemap.xml"))

            Expect.stringContains siteSitemap "https://b.example/" "satellite sitemap origin"
            Expect.isFalse (siteSitemap.Contains "main.example") "satellite sitemap carries no default-site URLs"

        testCase "zero sites → exportStaticAll is the single-tree export (GP 11)"
        <| fun _ ->
            let defaultRoot = mkSiteRoot "Solo Home"

            let config = {
                ServerConfig.defaults with
                    PublicBaseUrl = Some "https://solo.example"
                    PublicRendering = EnabledPublicRendering(ContentRoot defaultRoot)
            }

            let outDir =
                Path.Combine(Path.GetTempPath(), "toolup-multisite-tests", "export-" + Guid.NewGuid().ToString("N"))

            let app =
                PublicRenderingServerApp.create ()
                |> PublicRenderingServerApp.withConfig config
                |> PublicRenderingServerApp.withLayout (LayoutName "page") (fun p ->
                    html [] [ body [] [ str p.Title ] ])

            PublicRenderingServerApp.exportStaticAll outDir app
            |> Async.RunSynchronously
            |> ignore

            Expect.isTrue (File.Exists(Path.Combine(outDir, "index.html"))) "single tree at root"
            Expect.isFalse (Directory.Exists(Path.Combine(outDir, "sites"))) "no sites/ subtree emitted"
    ]

let tests =
    testList "MultiSite (Phase 114)" [
        resolutionTests
        servingTests
        validatorTests
        siteGateTests
        indexNowKeyTests
        exportTests
    ]