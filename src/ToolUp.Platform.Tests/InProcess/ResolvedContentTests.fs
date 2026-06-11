module ToolUp.Platform.Tests.InProcess.ResolvedContentTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.PublicRendering

// ─── Phase 111 — resolved-content head-metadata tests ───────────────
//
// Four layers:
//   1. `PageHeadMetadata` frontmatter codec round-trip.
//   2. `PublicContentApiImpl` synthesis: a resolved-content source's
//      head metadata folds into the synthesised page; a bare
//      `ContentBody` source is byte-for-byte the pre-111 shape (GP 11).
//   3. `PageHeadInjection` emission + the full `PublicPageHandler`
//      path: canonical / og / meta / JSON-LD reach the served document,
//      and the Phase 84 render cache owns the fragment path
//      (cache-ownership rule vs an upstream renderer's own cache seam).
//   4. Sitemap reach: `ofRouteResolvedEnumerable` slugs surface via
//      `ContentSource.enumerateAll` (Phase 95 / 109 discovery).

let private sampleHead: PageHeadMetadata = {
    Title = Some "Quarterly Report"
    Description = Some "Q3 performance summary"
    Canonical = Some "https://example.com/reports/q3"
    OgImage = Some "https://example.com/img/q3.png"
    Meta = [ "author", "ToolUp"; "og:type", "article" ]
    JsonLd = [ """{"@type":"Article","headline":"Quarterly Report"}""" ]
}

// ─── 1. Codec ────────────────────────────────────────────────────────

let private codecTests =
    testList "PageHeadMetadata — frontmatter codec" [

        testCase "ofFrontmatter on a map with no head:* keys → None (GP 13)"
        <| fun _ ->
            Expect.isNone (PageHeadMetadata.ofFrontmatter Map.empty) "empty map"

            Expect.isNone
                (PageHeadMetadata.ofFrontmatter (
                    Map[("og:image", "x")
                        ("tags", "a,b")]
                ))
                "non-reserved keys only"

        testCase "toFrontmatter → ofFrontmatter round-trips canonical / og:image / meta / JSON-LD"
        <| fun _ ->
            let fm = PageHeadMetadata.toFrontmatter sampleHead Map.empty

            match PageHeadMetadata.ofFrontmatter fm with
            | None -> failtest "expected Some after writing the envelope"
            | Some got ->
                Expect.equal got.Canonical sampleHead.Canonical "canonical survives"
                Expect.equal got.OgImage sampleHead.OgImage "og:image survives"
                Expect.equal (List.sort got.Meta) (List.sort sampleHead.Meta) "meta pairs survive"
                Expect.equal got.JsonLd sampleHead.JsonLd "JSON-LD payloads survive in order"
                Expect.isNone got.Title "Title rides the page field, not the envelope"
                Expect.isNone got.Description "Description rides the page field, not the envelope"

        testCase "toFrontmatter preserves pre-existing non-reserved keys"
        <| fun _ ->
            let fm =
                PageHeadMetadata.toFrontmatter
                    sampleHead
                    (Map[("tags", "news")
                         ("author", "x")])

            Expect.equal (fm.TryFind "tags") (Some "news") "author frontmatter untouched"
            Expect.equal (fm.TryFind "author") (Some "x") "author frontmatter untouched"

        testCase "JSON-LD payload order is preserved across the round-trip"
        <| fun _ ->
            let head = {
                PageHeadMetadata.empty with
                    JsonLd = [ "{\"a\":1}"; "{\"b\":2}"; "{\"c\":3}" ]
            }

            let got =
                PageHeadMetadata.toFrontmatter head Map.empty |> PageHeadMetadata.ofFrontmatter

            Expect.equal (got |> Option.map _.JsonLd) (Some head.JsonLd) "ordered"
    ]

// ─── 2. Synthesis via PublicContentApiImpl ──────────────────────────

let private mkTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-resolved-content-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private mkApiWith (sources: IContentSource list) : IPublicContentApi =
    let dir = mkTempDir ()
    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    let loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)
    PublicContentApiImpl.create loader None sources

let private anonCtx = AccessContext.unrestricted (AnonymousSession "test")

let private resolvedSource (slug: string) : IContentSource =
    ContentSource.ofResolved (fun (Slug s) _ctx -> async {
        if s = slug then
            return
                Some {
                    Body = Html "<p>fragment</p>"
                    Head = Some sampleHead
                    Provenance = None
                }
        else
            return None
    })

let private synthesisTests =
    testList "PublicContentApiImpl — resolved-content synthesis" [

        testCaseAsync "head Title / Description fold into the synthesised page fields"
        <| async {
            let api = mkApiWith [ resolvedSource "report" ]
            let! page = api.GetPageInContext("report", anonCtx)

            match page with
            | None -> failtest "expected the source to claim the slug"
            | Some p ->
                Expect.equal p.Title "Quarterly Report" "Title from head metadata"
                Expect.equal p.Description "Q3 performance summary" "Description from head metadata"
        }

        testCaseAsync "head canonical / og / meta / JSON-LD land in the head:* envelope + og:image mirror"
        <| async {
            let api = mkApiWith [ resolvedSource "report" ]
            let! page = api.GetPageInContext("report", anonCtx)

            match page with
            | None -> failtest "expected the source to claim the slug"
            | Some p ->
                Expect.equal
                    (p.Frontmatter.TryFind "head:canonical")
                    (Some "https://example.com/reports/q3")
                    "canonical in envelope"

                Expect.equal
                    (p.Frontmatter.TryFind "og:image")
                    (Some "https://example.com/img/q3.png")
                    "og:image mirrored to the conventional key for StructuredDataHelpers"

                Expect.isSome (p.Frontmatter.TryFind "head:meta:author") "meta pair in envelope"
                Expect.isSome (p.Frontmatter.TryFind "head:jsonld:000") "JSON-LD in envelope"
        }

        testCaseAsync "provenance fills PublishedAt for a non-Narrative body"
        <| async {
            let stamp = DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)

            let source =
                ContentSource.ofResolved (fun (Slug s) _ -> async {
                    if s = "dated" then
                        return
                            Some {
                                Body = Html "<p>x</p>"
                                Head = None
                                Provenance =
                                    Some {
                                        ModuleId = "test"
                                        PageRoute = None
                                        GeneratedAt = stamp
                                        SettingsKey = "k"
                                        SettingsDisplay = []
                                    }
                            }
                    else
                        return None
                })

            let api = mkApiWith [ source ]
            let! page = api.GetPageInContext("dated", anonCtx)

            Expect.equal (page |> Option.bind _.PublishedAt) (Some stamp) "PublishedAt from provenance"
        }

        testCaseAsync "a bare ContentBody source synthesises the pre-111 shape byte-for-byte (GP 11)"
        <| async {
            let plain =
                ContentSource.create (fun (Slug s) _ -> async {
                    return if s = "plain" then Some(Html "<p>x</p>") else None
                })

            let api = mkApiWith [ plain ]
            let! page = api.GetPageInContext("plain", anonCtx)

            match page with
            | None -> failtest "expected the source to claim the slug"
            | Some p ->
                Expect.equal p.Title "" "no synthesised title (pre-111 shape)"
                Expect.equal p.Frontmatter Map.empty "no envelope keys (pre-111 shape)"
        }

        testCaseAsync "ofResolved still answers the plain IContentSource.Resolve surface with the bare body"
        <| async {
            let source = resolvedSource "report"
            let! body = source.Resolve (Slug "report") anonCtx
            Expect.equal body (Some(Html "<p>fragment</p>")) "bare body via the pre-111 interface"
        }
    ]

// ─── 3. Injection + handler integration ─────────────────────────────

let private layouts: Map<LayoutName, PublicPage -> XmlNode> =
    Map[(LayoutName "page",
         (fun (p: PublicPage) -> html [] [ head [] [ title [] [ str p.Title ] ]; body [] [ str "B" ] ]))]

let private runHandler
    (cache: IRenderCache option)
    (settings: RenderCacheSettings)
    (api: IPublicContentApi)
    (path: string)
    : int * string =
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

    let respBody = new MemoryStream()
    ctx.Response.Body <- respBody

    let finalFunc: HttpFunc = fun c -> Task.FromResult(Some c)
    let h = PublicPageHandler.handler api layouts
    (h finalFunc ctx).GetAwaiter().GetResult() |> ignore

    respBody.Position <- 0L
    let text = (new StreamReader(respBody)).ReadToEnd()
    ctx.Response.StatusCode, text

let private injectionTests =
    testList "PageHeadInjection + PublicPageHandler" [

        testCase "inject places tags before </head>"
        <| fun _ ->
            let html = "<html><head><title>T</title></head><body>B</body></html>"
            let out = PageHeadInjection.inject sampleHead html

            Expect.stringContains out "rel=\"canonical\"" "canonical link emitted"
            Expect.stringContains out "https://example.com/reports/q3" "canonical url emitted"
            Expect.stringContains out "property=\"og:image\"" "og:image meta emitted"
            Expect.stringContains out "name=\"author\"" "plain meta uses name="
            Expect.stringContains out "property=\"og:type\"" "og-family meta uses property="
            Expect.stringContains out "application/ld+json" "JSON-LD script emitted"

            let headEnd = out.IndexOf("</head>", StringComparison.OrdinalIgnoreCase)
            let canonicalAt = out.IndexOf "rel=\"canonical\""
            Expect.isTrue (canonicalAt < headEnd) "tags injected inside <head>"

        testCase "inject on a document with no </head> returns it unchanged"
        <| fun _ ->
            let html = "<div>fragment only</div>"
            Expect.equal (PageHeadInjection.inject sampleHead html) html "degrades safely"

        testCase "injectFromPage without the envelope returns the document untouched (GP 11)"
        <| fun _ ->
            let page = {
                Slug = Slug "x"
                Title = "T"
                Description = ""
                Body = Html "b"
                Layout = LayoutName "page"
                Frontmatter = Map[("tags", "a")]
                PublishedAt = None
                Collection = None
                Status = Published
                Audience = PageAudience.Public
            }

            let html = "<html><head></head><body></body></html>"
            Expect.equal (PageHeadInjection.injectFromPage page html) html "no envelope → no change"

        testCase "handler serves a resolved-content page SEO-complete (uncached path)"
        <| fun _ ->
            let api = mkApiWith [ resolvedSource "report" ]
            let status, body = runHandler None RenderCacheSettings.defaults api "/report"

            Expect.equal status 200 "served"
            Expect.stringContains body "<title>Quarterly Report</title>" "per-request title via the layout"
            Expect.stringContains body "rel=\"canonical\"" "canonical injected"
            Expect.stringContains body "application/ld+json" "JSON-LD injected"

        testCase "Phase 84 cache owns the fragment path: cached copy carries the injected head, purge invalidates"
        <| fun _ ->
            let mutable resolves = 0

            let source =
                ContentSource.ofResolved (fun (Slug s) _ -> async {
                    if s = "report" then
                        resolves <- resolves + 1

                        return
                            Some {
                                Body = Html "<p>fragment</p>"
                                Head = Some sampleHead
                                Provenance = None
                            }
                    else
                        return None
                })

            let api = mkApiWith [ source ]
            let cache = InMemoryRenderCache.create ()

            let settings: RenderCacheSettings = {
                DefaultPolicy = CachePolicy.Cache(300, true)
            }

            let _, body1 = runHandler (Some cache) settings api "/report"
            let _, body2 = runHandler (Some cache) settings api "/report"

            Expect.equal resolves 1 "second request served from the render cache (no re-resolve)"
            Expect.stringContains body2 "rel=\"canonical\"" "cached copy carries the injected head"
            Expect.equal body2 body1 "cache serves the identical injected document"

            // Publish-purge: the same PurgeSlug hook NarrativePagePublisher
            // calls invalidates the fragment entry, so the next request
            // re-resolves — forge's cache is the single owner of the
            // fragment path (the documented rule vs an upstream renderer's
            // own ETag/cache seam).
            match box cache with
            | :? IRenderCacheInvalidation as inv -> inv.PurgeSlug "report" |> Async.RunSynchronously
            | _ -> failtest "InMemoryRenderCache must support PurgeSlug"

            let _, _ = runHandler (Some cache) settings api "/report"
            Expect.equal resolves 2 "purge invalidated the cached fragment"
    ]

// ─── 4. Sitemap / enumeration reach ──────────────────────────────────

let private enumerationTests =
    testList "ofRouteResolvedEnumerable — discovery reach" [

        testCaseAsync "enumerated slugs surface via ContentSource.enumerateAll"
        <| async {
            let source =
                ContentSource.ofRouteResolvedEnumerable
                    "reports/{q}"
                    (fun captures _ -> async {
                        return
                            Some {
                                Body = Html $"""<p>{captures["q"]}</p>"""
                                Head = Some sampleHead
                                Provenance = None
                            }
                    })
                    (fun () -> async { return [ Slug "reports/q1"; Slug "reports/q2" ] })

            let! slugs = ContentSource.enumerateAll [ source ]
            Expect.equal slugs [ Slug "reports/q1"; Slug "reports/q2" ] "both dynamic slugs discovered"
        }

        testCaseAsync "route-shape gate applies to the resolved path"
        <| async {
            let source =
                ContentSource.ofRouteResolved "reports/{q}" (fun captures _ -> async {
                    return Some(ResolvedContent.ofBody (Html captures["q"]))
                })

            let api = mkApiWith [ source ]
            let! hit = api.GetPageInContext("reports/q3", anonCtx)
            let! miss = api.GetPageInContext("other/q3", anonCtx)

            Expect.isSome hit "matching slug resolves"
            Expect.isNone miss "non-matching slug falls through"
        }
    ]

let tests =
    testList "ResolvedContent (Phase 111)" [ codecTests; synthesisTests; injectionTests; enumerationTests ]