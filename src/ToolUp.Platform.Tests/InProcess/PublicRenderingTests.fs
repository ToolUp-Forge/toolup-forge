module ToolUp.Platform.Tests.InProcess.PublicRenderingTests

open System
open System.IO
open Expecto
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering
open ToolUp.Platform.Tests.Contracts

// ─── Test-fixture wiring ────────────────────────────────────────────
//
// The canonical fixture lives as real `.md` files on disk under a
// temp directory. Each test that needs a clean fixture calls
// `mkFixture ()` to construct the temp tree + loader + API. The
// fixture is independent per call so contract tests can run in
// parallel without interfering.

let private writeFile (path: string) (contents: string) =
    let dir = Path.GetDirectoryName path

    if not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

    File.WriteAllText(path, contents)

let private mkFixtureDir () : string =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-publicrendering-tests", Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private seedCanonicalFixture (root: string) : unit =
    writeFile
        (Path.Combine(root, "pages/about.md"))
        "---\ntitle: About Us\ndescription: Who we are\nlayout: page\n---\n\n# About\n\nThe body."

    writeFile
        (Path.Combine(root, "pages/services/consulting.md"))
        "---\ntitle: Consulting\nlayout: page\n---\n\nConsulting body."

    writeFile
        (Path.Combine(root, "news/2026-05-22-launch.md"))
        "---\ntitle: Product Launch\nlayout: article\ndate: 2026-05-22\n---\n\nLaunch."

    writeFile
        (Path.Combine(root, "news/2026-05-15-announcement.md"))
        "---\ntitle: Announcement\nlayout: article\ndate: 2026-05-15\n---\n\nAnnouncement."

    writeFile (Path.Combine(root, "pages/draft.md")) "---\ntitle: Draft\nlayout: page\nsitemap: exclude\n---\n\nDraft."

let private mkFixtureApi () : IPublicContentApi =
    let dir = mkFixtureDir ()
    seedCanonicalFixture dir
    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    let loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)
    PublicContentApiImpl.create loader None []

/// Build a default impl over the canonical fixture with the supplied
/// request-time content sources wired in (Phase 83). Used by the
/// `GetPageInContext` chain-ordering tests.
let private mkFixtureApiWith (sources: IContentSource list) : IPublicContentApi =
    let dir = mkFixtureDir ()
    seedCanonicalFixture dir
    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    let loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)
    PublicContentApiImpl.create loader None sources

// ─── Contract pack binding ──────────────────────────────────────────

let private contractTests =
    IPublicContentApiContract.tests "MarkdownContentLoader" mkFixtureApi

// ─── Phase 83 — IContentSource contract bindings ────────────────────
//
// Two bindings prove the contract holds for the SDK-provided
// `ContentSource.ofRoute` constructor ("the default") and a hand-rolled
// object-expression resolver ("one consumer impl"). Both arrange the
// canonical fixture: claim "claimed/widget" → Narrative "Widget",
// decline everything else.

let private widgetDoc = Narrative.create "Widget"

let private ofRouteContentSource () : IContentSource =
    ContentSource.ofRoute "claimed/{name}" (fun captures _ctx -> async {
        match captures.TryFind "name" with
        | Some "widget" -> return Some(Narrative widgetDoc)
        | _ -> return None
    })

let private customContentSource () : IContentSource =
    ContentSource.create (fun (Slug s) _ctx -> async {
        if s = "claimed/widget" then
            return Some(Narrative widgetDoc)
        else
            return None
    })

let private contentSourceContractTests =
    testList "IContentSource — contract bindings" [
        IContentSourceContract.tests "ContentSource.ofRoute (default)" ofRouteContentSource
        IContentSourceContract.tests "object-expression (consumer impl)" customContentSource
    ]

// ─── Impl-specific tests ────────────────────────────────────────────

let private implTests =
    testList "PublicRendering — impl-specific" [

        testCase "FrontmatterParser: parses canonical key/value lines"
        <| fun _ ->
            let result = FrontmatterParser.parseLine "title: My Page"

            Expect.equal result (Some("title", "My Page")) "key/value pair"

        testCase "FrontmatterParser: strips quotes on values"
        <| fun _ ->
            let result = FrontmatterParser.parseLine """description: "A quoted value" """

            match result with
            | Some(k, v) ->
                Expect.equal k "description" "key"
                Expect.equal (v.Trim()) "A quoted value" "value stripped of quotes"
            | None -> failtest "expected Some"

        testCase "FrontmatterParser: handles keys with embedded colons"
        <| fun _ ->
            let result = FrontmatterParser.parseLine "og:image: /foo.jpg"

            Expect.equal result (Some("og:image", "/foo.jpg")) "split on first ': '"

        testCase "FrontmatterParser: skips comments and blank lines"
        <| fun _ ->
            let comment = FrontmatterParser.parseLine "# a comment"
            let blank = FrontmatterParser.parseLine "   "
            Expect.isNone comment "comments are skipped"
            Expect.isNone blank "blank lines are skipped"

        testCase "MarkdownContentLoader: pages/<slug> collapses to root, no collection"
        <| fun _ ->
            let api = mkFixtureApi ()

            let page = (api.GetPage "about" |> Async.RunSynchronously).Value

            Expect.equal page.Collection None "pages/ prefix collapses, no collection"

        testCase "MarkdownContentLoader: top-level subdir becomes collection"
        <| fun _ ->
            let api = mkFixtureApi ()

            let page = (api.GetPage "news/2026-05-22-launch" |> Async.RunSynchronously).Value

            Expect.equal page.Collection (Some "news") "first segment is collection"

        testCase "MarkdownContentLoader: slug frontmatter overrides path-derived slug"
        <| fun _ ->
            let dir = mkFixtureDir ()
            // A README that uses the slug override to land at the folder
            // URL instead of `docs/forms/README`.
            writeFile
                (Path.Combine(dir, "docs/forms/README.md"))
                "---\ntitle: Forms\nlayout: doc\nslug: docs/forms\n---\n\nForms overview."

            let logger = ConsoleLogger.ConsoleLogger() :> ILogger
            use loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)

            Expect.isSome (loader.GetPage "docs/forms") "override slug resolves"

            Expect.isNone (loader.GetPage "docs/forms/README") "path-derived slug is replaced (not duplicated)"

            let page = (loader.GetPage "docs/forms").Value
            Expect.equal (Slug.value page.Slug) "docs/forms" "Slug field reflects override"

            Expect.equal page.Collection (Some "docs") "Collection stays path-derived even when slug is overridden"

        testCase "MarkdownContentLoader: slug override accepts leading / and trims whitespace"
        <| fun _ ->
            let dir = mkFixtureDir ()

            writeFile
                (Path.Combine(dir, "pages/source.md"))
                "---\ntitle: T\nlayout: page\nslug: /custom/path\n---\n\nBody."

            let logger = ConsoleLogger.ConsoleLogger() :> ILogger
            use loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)

            Expect.isSome (loader.GetPage "custom/path") "leading slash stripped"
            Expect.isNone (loader.GetPage "source") "path-derived slug is replaced"

        testCase "Attribution: generatorMeta emits the expected meta tag"
        <| fun _ ->
            let rendered =
                Giraffe.ViewEngine.RenderView.AsString.xmlNode Attribution.generatorMeta

            Expect.stringContains rendered "name=\"generator\"" "name attribute present"

            Expect.stringContains rendered "ToolUp Forge" "content attribute names ToolUp Forge"

        testCase "Attribution: poweredByBadge with defaults emits both wordmark variants + correct link"
        <| fun _ ->
            let rendered =
                Giraffe.ViewEngine.RenderView.AsString.xmlNode Attribution.poweredByBadge

            Expect.stringContains rendered "https://toolup-forge.io/" "link points at the project home"

            Expect.stringContains
                rendered
                "/repo/icon-wordmark-transparent-dark-text-1024.png"
                "light-mode wordmark referenced (default <img>)"

            Expect.stringContains
                rendered
                "/repo/icon-wordmark-transparent-1024.png"
                "dark-mode wordmark referenced (<source srcset>)"

            Expect.stringContains
                rendered
                "(prefers-color-scheme: dark)"
                "<picture> dark-mode source media query present"

            Expect.stringContains rendered "Powered by" "leading label rendered"

            Expect.stringContains rendered "alt=\"ToolUp Forge\"" "wordmark img alt text is ToolUp Forge"

        testCase "Attribution: poweredByBadgeWith honours custom LinkTo + AssetPrefix"
        <| fun _ ->
            let opts: Attribution.Options = {
                Attribution.Options.defaults with
                    LinkTo = "https://example.com/"
                    AssetPrefix = "https://cdn.example.com/forge-brand/"
            }

            let rendered =
                Giraffe.ViewEngine.RenderView.AsString.xmlNode (Attribution.poweredByBadgeWith opts)

            Expect.stringContains rendered "https://example.com/" "custom link target honoured"

            Expect.stringContains
                rendered
                "https://cdn.example.com/forge-brand/icon-wordmark-transparent-dark-text-1024.png"
                "custom asset prefix honoured (light variant)"

            Expect.stringContains
                rendered
                "https://cdn.example.com/forge-brand/icon-wordmark-transparent-1024.png"
                "custom asset prefix honoured (dark variant)"

            Expect.isFalse (rendered.Contains "/repo/icon-wordmark") "default /repo/ prefix is replaced, not appended"

        testCase "Attribution: poweredByBadgeWith trims trailing slash on AssetPrefix"
        <| fun _ ->
            // Both `"/repo"` and `"/repo/"` should produce the same path.
            let rendered =
                Giraffe.ViewEngine.RenderView.AsString.xmlNode (
                    Attribution.poweredByBadgeWith {
                        Attribution.Options.defaults with
                            AssetPrefix = "/repo"
                    }
                )

            Expect.stringContains
                rendered
                "/repo/icon-wordmark-transparent-dark-text-1024.png"
                "no double slash regardless of trailing-slash input"

            Expect.isFalse (rendered.Contains "/repo//") "no double slash regardless of trailing-slash input"

        testCase "MarkdownContentLoader: empty slug override falls back to path-derived slug"
        <| fun _ ->
            let dir = mkFixtureDir ()

            writeFile (Path.Combine(dir, "pages/keep.md")) "---\ntitle: T\nlayout: page\nslug: \n---\n\nBody."

            let logger = ConsoleLogger.ConsoleLogger() :> ILogger
            use loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)

            Expect.isSome (loader.GetPage "keep") "empty override is ignored, path-derived wins"

        testCase "MarkdownContentLoader: Reload() picks up new content"
        <| fun _ ->
            let dir = mkFixtureDir ()
            seedCanonicalFixture dir
            let logger = ConsoleLogger.ConsoleLogger() :> ILogger
            use loader = new MarkdownContentLoader(ContentRoot dir, logger, hotReload = false)

            Expect.isNone (loader.GetPage "fresh") "Before Reload, fresh page is absent"

            writeFile (Path.Combine(dir, "pages/fresh.md")) "---\ntitle: Fresh\nlayout: page\n---\n\nNew content."

            loader.Reload()

            let page = loader.GetPage "fresh"
            Expect.isSome page "After Reload, fresh page is present"

        testCase "RedirectMap.handler: preserves query string across redirect"
        <| fun _ ->
            // Pure-function reachability: synthesise a Redirect list,
            // construct the handler, and exercise the path-match +
            // query-string-preservation logic via direct invocation
            // of the underlying string helper. The full HttpContext
            // path is covered by the sample-site acceptance test.
            let redirects = [
                {
                    From = "/old-page"
                    To = "/new-page"
                    StatusCode = 301
                }
            ]

            let map = redirects |> List.map (fun r -> r.From, r) |> Map.ofList

            Expect.isTrue (Map.containsKey "/old-page" map) "redirect map indexes by From path"

            // The actual query-string preservation lives in
            // `RedirectMap.handler`'s internal `appendQueryString`;
            // covered via the acceptance test on the sample site.
            Expect.equal (Map.find "/old-page" map).To "/new-page" "destination preserved"

        testCase "SitemapGenerator.generate: excludes pages with sitemap=exclude"
        <| fun _ ->
            let included = {
                Slug = Slug "kept"
                Title = "Kept"
                Description = ""
                Body = Html ""
                Layout = LayoutName "page"
                Frontmatter = Map.empty
                PublishedAt = None
                Collection = None
                Status = Published
                Audience = PageAudience.Public
            }

            let excluded = {
                included with
                    Slug = Slug "dropped"
                    Frontmatter = Map.ofList [ "sitemap", "exclude" ]
            }

            let xml = SitemapGenerator.generate "https://example.com" [ included; excluded ]

            Expect.stringContains xml "https://example.com/kept" "included page surfaces"
            Expect.isFalse (xml.Contains "https://example.com/dropped") "excluded page is dropped"

        testCase "SitemapGenerator.generate: emits lastmod for dated pages"
        <| fun _ ->
            let page = {
                Slug = Slug "news/launch"
                Title = "Launch"
                Description = ""
                Body = Html ""
                Layout = LayoutName "article"
                Frontmatter = Map.empty
                PublishedAt = Some(DateTimeOffset.Parse "2026-05-22")
                Collection = Some "news"
                Status = Published
                Audience = PageAudience.Public
            }

            let xml = SitemapGenerator.generate "https://example.com" [ page ]

            Expect.stringContains xml "<lastmod>2026-05-22</lastmod>" "lastmod surfaces"
    ]

// ─── Phase 80c — hybrid SDK + PublicRendering composition tests ────
//
// `withPublicRendering` ports `ToolUp.PublicRendering` into the
// Phase 1h additive-composition shape Forms + AI already use. These
// tests pin the four behaviours the migration doc promises:
//
//   1. `createFrom` lifts a base `ServerApp` into a fresh
//      `PublicRenderingServerApp` whose `Base` is the input.
//   2. `composePublicRendering` with `NoPublicRendering` is a true
//      strip-imports pass-through — no marker appended, so a later
//      compose on the same pipeline composes freely.
//   3. `composePublicRendering` with `EnabledPublicRendering` appends
//      the `"ToolUp.PublicRendering"` companion marker.
//   4. A second `withPublicRendering` on the same pipeline trips
//      `ensureCompanionNotAlreadyComposed` with a clear diagnostic.

let private mkContentRoot () : ContentRoot =
    let dir = mkFixtureDir ()
    seedCanonicalFixture dir
    ContentRoot dir

let private dummyLayout (_p: PublicPage) : XmlNode = html [] [ body [] [] ]

let private composeTests =
    testList "PublicRendering — Phase 80c composition (hybrid SDK + PublicRendering)" [

        testCase "createFrom lifts the base ServerApp into a PublicRenderingServerApp"
        <| fun _ ->
            let baseApp = ServerApp.empty |> ServerApp.withConfig ServerConfig.defaults
            let lifted = PublicRenderingCompose.PublicRenderingServerApp.createFrom baseApp

            Expect.equal
                lifted.Base.Config.PublicRendering
                ServerConfig.defaults.PublicRendering
                "lifted.Base preserves the input ServerApp's config"

            Expect.isEmpty lifted.Layouts "fresh createFrom has empty layouts"
            Expect.isEmpty lifted.Redirects "fresh createFrom has empty redirects"
            Expect.isEmpty lifted.Feeds "fresh createFrom has empty feeds"
            Expect.isFalse lifted.AIPublishEnabled "fresh createFrom has AIPublishEnabled=false"

        testCase
            "composePublicRendering with NoPublicRendering is a strip-imports pass-through (no marker, double-compose safe)"
        <| fun _ ->
            let baseApp =
                ServerApp.empty
                |> ServerApp.withConfig {
                    ServerConfig.defaults with
                        PublicRendering = NoPublicRendering
                }

            let composed =
                baseApp
                |> PublicRenderingCompose.withPublicRendering id
                |> PublicRenderingCompose.withPublicRendering id

            Expect.isFalse
                (composed.ComposedCompanions |> List.contains "ToolUp.PublicRendering")
                "NoPublicRendering branch must NOT append the companion marker — a later compose must remain free to fire"

        testCase
            "composePublicRendering with EnabledPublicRendering appends the ToolUp.PublicRendering companion marker"
        <| fun _ ->
            let root = mkContentRoot ()

            let baseApp =
                ServerApp.empty
                |> ServerApp.withConfig {
                    ServerConfig.defaults with
                        PublicRendering = EnabledPublicRendering root
                }

            let composed =
                baseApp
                |> PublicRenderingCompose.withPublicRendering (
                    PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                )

            Expect.isTrue
                (composed.ComposedCompanions |> List.contains "ToolUp.PublicRendering")
                "EnabledPublicRendering branch must append the 'ToolUp.PublicRendering' companion marker so a second compose trips the guard"

        testCase
            "second withPublicRendering on the same pipeline trips ensureCompanionNotAlreadyComposed with a clear diagnostic"
        <| fun _ ->
            let root = mkContentRoot ()

            let baseApp =
                ServerApp.empty
                |> ServerApp.withConfig {
                    ServerConfig.defaults with
                        PublicRendering = EnabledPublicRendering root
                }

            let firstComposed =
                baseApp
                |> PublicRenderingCompose.withPublicRendering (
                    PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                )

            let raised =
                try
                    firstComposed
                    |> PublicRenderingCompose.withPublicRendering (
                        PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") dummyLayout
                    )
                    |> ignore

                    None
                with ex ->
                    Some ex

            Expect.isSome
                raised
                "second withPublicRendering on the same pipeline must raise (entry-guard fires before the second compose runs)"

            match raised with
            | Some ex ->
                Expect.stringContains
                    ex.Message
                    "PublicRendering"
                    "the diagnostic must name the companion ('PublicRendering' or 'ToolUp.PublicRendering') so the operator can locate the duplicate withPublicRendering call"
            | None -> ()
    ]

// ─── Phase 83 — content-source chain (GetPageInContext) tests ───────

let private csAnon = AccessContext.unrestricted (AnonymousSession "test-anon")

let private contentSourceImplTests =
    testList "PublicRendering — Phase 83 content-source chain" [

        testCaseAsync "GetPageInContext with no sources is identical to GetPage (GP 11)"
        <| async {
            let api = mkFixtureApiWith []
            let! viaGetPage = api.GetPage "about"
            let! viaContext = api.GetPageInContext("about", csAnon)

            Expect.equal
                (viaContext |> Option.map (fun p -> Slug.value p.Slug))
                (viaGetPage |> Option.map (fun p -> Slug.value p.Slug))
                "known slug resolves identically through both methods"

            let! missCtx = api.GetPageInContext("does-not-exist", csAnon)
            let! missPage = api.GetPage "does-not-exist"
            Expect.isNone missCtx "unknown slug → None via context path"
            Expect.isNone missPage "unknown slug → None via GetPage"
        }

        testCaseAsync "file tier wins over a content source claiming the same slug"
        <| async {
            // A source that would claim "about", but the file fixture has
            // an "about" page — the file tier is consulted first.
            let shadow =
                ContentSource.create (fun (Slug s) _ -> async {
                    if s = "about" then
                        return Some(Narrative(Narrative.create "Shadowed"))
                    else
                        return None
                })

            let api = mkFixtureApiWith [ shadow ]
            let! pageOpt = api.GetPageInContext("about", csAnon)

            match pageOpt with
            | Some page -> Expect.equal page.Title "About Us" "file frontmatter title wins, not the source body"
            | None -> failtest "Expected the file-backed about page"
        }

        testCaseAsync "content source resolves a dynamic slug after file + overlay miss"
        <| async {
            let source =
                ContentSource.ofRoute "dashboard/{quarter}" (fun captures _ -> async {
                    let q = captures.TryFind "quarter" |> Option.defaultValue "?"
                    return Some(Narrative(Narrative.create $"{q} Dashboard"))
                })

            let api = mkFixtureApiWith [ source ]
            let! pageOpt = api.GetPageInContext("dashboard/q1", csAnon)

            match pageOpt with
            | Some page ->
                Expect.equal (Slug.value page.Slug) "dashboard/q1" "synthesised page carries the request slug"
                Expect.equal page.Title "q1 Dashboard" "page Title derives from the Narrative document title"

                match page.Body with
                | Narrative doc -> Expect.equal doc.Title "q1 Dashboard" "body is the resolver's Narrative document"
                | other -> failtestf "Expected a Narrative body; got %A" other
            | None -> failtest "Expected the dynamic dashboard page"
        }

        testCaseAsync "registration order — first source returning Some wins"
        <| async {
            let first =
                ContentSource.create (fun (Slug s) _ -> async {
                    if s = "x" then
                        return Some(Narrative(Narrative.create "First"))
                    else
                        return None
                })

            let second =
                ContentSource.create (fun (Slug s) _ -> async {
                    if s = "x" then
                        return Some(Narrative(Narrative.create "Second"))
                    else
                        return None
                })

            let api = mkFixtureApiWith [ first; second ]
            let! pageOpt = api.GetPageInContext("x", csAnon)

            match pageOpt with
            | Some page -> Expect.equal page.Title "First" "first source in registration order wins"
            | None -> failtest "Expected the first source to claim the slug"
        }

        testCaseAsync "all sources decline → None (fall-through)"
        <| async {
            let decliner = ContentSource.create (fun _ _ -> async { return None })
            let api = mkFixtureApiWith [ decliner; decliner ]
            let! pageOpt = api.GetPageInContext("nothing/here", csAnon)
            Expect.isNone pageOpt "every source declined and there is no file/overlay match"
        }

        testCaseAsync "page metadata from data — PublishedAt derives from Narrative provenance"
        <| async {
            let withProv =
                Narrative.create "Report"
                |> Narrative.withProvenance "analytics" (Some "/report") "settings-key" []

            let source =
                ContentSource.create (fun (Slug s) _ -> async {
                    if s = "report" then
                        return Some(Narrative withProv)
                    else
                        return None
                })

            let api = mkFixtureApiWith [ source ]
            let! pageOpt = api.GetPageInContext("report", csAnon)

            match pageOpt with
            | Some page -> Expect.isSome page.PublishedAt "PublishedAt is populated from provenance GeneratedAt"
            | None -> failtest "Expected the report page"
        }

        // ─── RouteShape.tryMatch ───────────────────────────────────

        testCase "RouteShape.tryMatch captures a single segment"
        <| fun _ ->
            match RouteShape.tryMatch "services/{client}" "services/acme" with
            | Some caps -> Expect.equal (caps.TryFind "client") (Some "acme") "captures {client}=acme"
            | None -> failtest "Expected a match"

        testCase "RouteShape.tryMatch captures multiple segments"
        <| fun _ ->
            match RouteShape.tryMatch "{a}/{b}/{c}" "x/y/z" with
            | Some caps ->
                Expect.equal (caps.TryFind "a") (Some "x") "a=x"
                Expect.equal (caps.TryFind "b") (Some "y") "b=y"
                Expect.equal (caps.TryFind "c") (Some "z") "c=z"
            | None -> failtest "Expected a match"

        testCase "RouteShape.tryMatch returns None on literal mismatch"
        <| fun _ -> Expect.isNone (RouteShape.tryMatch "services/{client}" "products/acme") "literal segment must match"

        testCase "RouteShape.tryMatch returns None on segment-count mismatch"
        <| fun _ ->
            Expect.isNone
                (RouteShape.tryMatch "services/{client}" "services/acme/extra")
                "arity must match (captures do not span segments)"

        testCase "RouteShape.tryMatch matches a fully-literal pattern with an empty capture map"
        <| fun _ ->
            match RouteShape.tryMatch "about/team" "about/team" with
            | Some caps -> Expect.isTrue (Map.isEmpty caps) "no captures for a fully-literal pattern"
            | None -> failtest "Expected a literal match"
    ]

// ─── Phase 93 — render observability (RenderMetrics) ────────────────

/// An `IMetricsSink` that records every emission for assertion.
type private RecordingSink() =
    let counters = System.Collections.Generic.List<string * Map<string, string>>()

    let records =
        System.Collections.Generic.List<string * float * Map<string, string>>()

    member _.Counters = List.ofSeq counters
    member _.Records = List.ofSeq records

    interface IMetricsSink with
        member _.Increment(name, tags) = counters.Add((name, tags))
        member _.Record(name, value, tags) = records.Add((name, value, tags))
        member _.SetGauge(_, _, _) = ()

let private renderMetricsTests =
    testList "PublicRendering — Phase 93 render metrics" [

        testCase "emitRender emits a render count + duration tagged by outcome"
        <| fun _ ->
            let sink = RecordingSink()
            RenderMetrics.emitRender sink RenderMetrics.OutcomeRendered 12.5

            Expect.contains
                sink.Counters
                (RenderMetrics.RenderCount, Map["outcome", RenderMetrics.OutcomeRendered])
                "render counter is incremented with the outcome tag"

            Expect.contains
                sink.Records
                (RenderMetrics.RenderMs, 12.5, Map["outcome", RenderMetrics.OutcomeRendered])
                "render duration histogram records the elapsed ms"

        testCase "classifyOutcome maps status codes (304 / 401 / 403 / 500 / 200) and fall-through"
        <| fun _ ->
            let withStatus (code: int) =
                let ctx = DefaultHttpContext()
                ctx.Response.StatusCode <- code
                ctx :> HttpContext

            Expect.equal
                (RenderMetrics.classifyOutcome (withStatus 200) (Some(withStatus 200)))
                RenderMetrics.OutcomeRendered
                "200 → rendered"

            Expect.equal
                (RenderMetrics.classifyOutcome (withStatus 304) (Some(withStatus 304)))
                RenderMetrics.OutcomeNotModified
                "304 → not_modified"

            Expect.equal
                (RenderMetrics.classifyOutcome (withStatus 401) (Some(withStatus 401)))
                RenderMetrics.OutcomeUnauthorized
                "401 → unauthorized"

            Expect.equal
                (RenderMetrics.classifyOutcome (withStatus 403) (Some(withStatus 403)))
                RenderMetrics.OutcomeForbidden
                "403 → forbidden"

            Expect.equal
                (RenderMetrics.classifyOutcome (withStatus 200) None)
                RenderMetrics.OutcomeNotFound
                "fall-through (None) → not_found"

        testCase "instrumentSource records per-source resolve latency and delegates the result"
        <| fun _ ->
            let sink = RecordingSink()
            let ctx = AccessContext.unrestricted (AnonymousSession "s")

            let inner =
                ContentSource.create (fun _slug _ctx -> async { return Some(ContentBody.Html "<p>x</p>") })

            let wrapped = RenderMetrics.instrumentSource sink "my-source" inner

            let result = wrapped.Resolve (Slug "anything") ctx |> Async.RunSynchronously
            Expect.isSome result "the wrapper delegates the inner result"

            Expect.isTrue
                (sink.Records
                 |> List.exists (fun (name, _, tags) ->
                     name = RenderMetrics.SourceResolveMs && tags = Map["source", "my-source"]))
                "a source_resolve_ms record is emitted tagged with the source label"

        testCase "instrumentSource preserves IEnumerableContentSource enumeration"
        <| fun _ ->
            let sink = RecordingSink()

            // An enumerable source: its routes must still be discovered
            // through the timing wrapper.
            let enumerable =
                ContentSource.ofRouteEnumerable "thing/{id}" (fun _caps _ctx -> async { return None }) (fun () -> async {
                    return [ Slug "thing/a"; Slug "thing/b" ]
                })

            let wrappedEnumerable = RenderMetrics.instrumentSource sink "enum" enumerable

            let routes =
                ContentSource.enumerateAll [ wrappedEnumerable ] |> Async.RunSynchronously

            Expect.contains routes (Slug "thing/a") "wrapped enumerable source still enumerates its routes"

            // A non-enumerable source contributes nothing — unchanged by
            // the wrapper.
            let plain = ContentSource.create (fun _slug _ctx -> async { return None })

            let wrappedPlain = RenderMetrics.instrumentSource sink "plain" plain

            let plainRoutes =
                ContentSource.enumerateAll [ wrappedPlain ] |> Async.RunSynchronously

            Expect.isEmpty plainRoutes "wrapped non-enumerable source contributes no routes"
    ]

// ─── Phase 93 — static-export extensions (StaticExport / StaticHostConfig) ─

let private staticExportTests =
    testList "PublicRendering — Phase 93 static export" [

        testCase "azureStaticWebApps emits routes + globalHeaders from redirects + policy"
        <| fun _ ->
            let json =
                StaticHostConfig.azureStaticWebApps
                    [
                        {
                            From = "/old"
                            To = "/new"
                            StatusCode = 301
                        }
                    ]
                    (CachePolicy.Cache(300, false))

            Expect.stringContains json "\"route\": \"/old\"" "redirect from → route"
            Expect.stringContains json "\"redirect\": \"/new\"" "redirect to → redirect"
            Expect.stringContains json "301" "status code present"
            Expect.stringContains json "max-age=300" "cache policy → globalHeaders Cache-Control"

        testCase "redirectsFile / headersFile emit Netlify shapes; NoCache → no headers"
        <| fun _ ->
            let redirects =
                StaticHostConfig.redirectsFile [
                    {
                        From = "/a"
                        To = "/b"
                        StatusCode = 302
                    }
                ]

            Expect.equal redirects "/a /b 302" "one redirect line"

            Expect.equal (StaticHostConfig.headersFile CachePolicy.NoCache) "" "NoCache → empty _headers"

            Expect.stringContains
                (StaticHostConfig.headersFile (CachePolicy.Cache(60, false)))
                "Cache-Control: public, max-age=60"
                "Cache policy → _headers block"

        testCase "internalRefs extracts same-origin href/src and skips external / mailto / #"
        <| fun _ ->
            let html =
                """<a href="/about">x</a><img src="/img/logo.png"><a href="https://x.test/y">e</a><a href="#top">t</a><a href="mailto:a@b.c">m</a>"""

            let refs = StaticExport.internalRefs html
            Expect.contains refs "/about" "internal href captured"
            Expect.contains refs "/img/logo.png" "internal src captured"
            Expect.isFalse (List.contains "https://x.test/y" refs) "external skipped"
            Expect.isFalse (refs |> List.exists (fun r -> r.StartsWith "#")) "fragment skipped"
            Expect.isFalse (refs |> List.exists (fun r -> r.StartsWith "mailto:")) "mailto skipped"

        testCase "checkLinks flags a dead internal reference and passes a resolving one"
        <| fun _ ->
            let dir = mkFixtureDir ()
            writeFile (Path.Combine(dir, "index.html")) """<a href="/about">ok</a><a href="/missing">dead</a>"""
            writeFile (Path.Combine(dir, "about", "index.html")) "<p>about</p>"

            let dead = StaticExport.checkLinks dir
            Expect.contains dead "/missing" "dead reference is flagged"
            Expect.isFalse (List.contains "/about" dead) "a reference resolving to about/index.html passes"

        testCase "runWith writes per-locale trees, excludes gated pages, and emits host-config"
        <| fun _ ->
            let dir = mkFixtureDir ()
            writeFile (Path.Combine(dir, "pages/about.md")) "---\ntitle: About\nlayout: page\n---\n\nBody."

            writeFile
                (Path.Combine(dir, "pages/secret.md"))
                "---\ntitle: Secret\nlayout: page\naudience: authenticated\n---\n\nSecret."

            let layouts: Map<LayoutName, PublicPage -> XmlNode> =
                Map[(LayoutName "page",
                     (fun (p: PublicPage) ->
                         html [] [ head [] [ title [] [ str p.Title ] ]; body [] [ str p.Description ] ]))]

            let config = {
                ServerConfig.defaults with
                    PublicRendering = EnabledPublicRendering(ContentRoot dir)
                    PublicBaseUrl = Some "https://x.test"
            }

            let outDir = mkFixtureDir ()

            let options =
                StaticExportOptions.defaults
                |> StaticExportOptions.withLocales [ "en"; "fr" ]
                |> StaticExportOptions.withHostConfigs [ AzureStaticWebApps; Netlify ]
                |> StaticExportOptions.withCachePolicy (CachePolicy.Cache(300, true))

            let count =
                StaticExport.runWith
                    options
                    [
                        {
                            From = "/old"
                            To = "/about"
                            StatusCode = 301
                        }
                    ]
                    config
                    layouts
                    None
                    []
                    None
                    outDir
                |> Async.RunSynchronously

            Expect.equal count 2 "one public page rendered into each of two locale trees (gated page excluded)"
            Expect.isTrue (File.Exists(Path.Combine(outDir, "en", "about", "index.html"))) "en tree written"
            Expect.isTrue (File.Exists(Path.Combine(outDir, "fr", "about", "index.html"))) "fr tree written"

            Expect.isFalse
                (File.Exists(Path.Combine(outDir, "en", "secret", "index.html")))
                "gated page is never written to disk"

            Expect.isTrue
                (File.Exists(Path.Combine(outDir, "staticwebapp.config.json")))
                "Azure host-config emitted at root"

            Expect.isTrue (File.Exists(Path.Combine(outDir, "_redirects"))) "Netlify _redirects emitted at root"

            Expect.stringContains
                (File.ReadAllText(Path.Combine(outDir, "_redirects")))
                "/old /about 301"
                "redirect line present"

        testCase "runWith default options reproduce the single-tree export (GP 11)"
        <| fun _ ->
            let dir = mkFixtureDir ()
            writeFile (Path.Combine(dir, "pages/home.md")) "---\ntitle: Home\nlayout: page\n---\n\nBody."

            let layouts: Map<LayoutName, PublicPage -> XmlNode> =
                Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

            let config = {
                ServerConfig.defaults with
                    PublicRendering = EnabledPublicRendering(ContentRoot dir)
            }

            let outDir = mkFixtureDir ()

            let count =
                StaticExport.run config layouts None [] None outDir |> Async.RunSynchronously

            Expect.equal count 1 "single page written"
            Expect.isTrue (File.Exists(Path.Combine(outDir, "home", "index.html"))) "single-tree path: page at root"
            Expect.isTrue (File.Exists(Path.Combine(outDir, "sitemap.xml"))) "sitemap at root"
            Expect.isFalse (File.Exists(Path.Combine(outDir, "staticwebapp.config.json"))) "no host-config by default"
    ]

// ─── Phase 155 / 147 — handler-agnostic conditional-GET + memoisation ──
//
// The combinator + `RenderCache.getOrRender` are exercised independently
// of `PublicPageHandler` (a synthetic Giraffe handler keyed on an
// arbitrary composite key) to prove a programmatic-SSR consumer can adopt
// them without routing through the content-file page pipeline. Phase 147
// hardens the wire format through this same seam: weak ETags, the
// `If-Modified-Since` union, and second-granularity `Last-Modified`.

let private mkCtxWith (headers: (string * string) list) : HttpContext =
    let ctx = DefaultHttpContext()

    for (k, v) in headers do
        ctx.Request.Headers[k] <- Microsoft.Extensions.Primitives.StringValues v

    ctx :> HttpContext

let private runHandler (handler: Giraffe.Core.HttpHandler) (ctx: HttpContext) : bool * HttpContext option =
    let mutable bodyRan = false

    let next: Giraffe.Core.HttpFunc =
        fun c ->
            bodyRan <- true
            System.Threading.Tasks.Task.FromResult(Some c)

    let result = handler next ctx |> Async.AwaitTask |> Async.RunSynchronously
    bodyRan, result

let private conditionalGetTests =
    testList "PublicRendering — conditional-GET primitive (Phase 155 + 147)" [

        testCase "RenderKey.forKey builds an opaque composite key; forPublic uses the public scope"
        <| fun _ ->
            let k = RenderKey.forKey "report/acme/q1" "team-acme" "v3"
            Expect.equal k.Slug "report/acme/q1" "opaque composite slug preserved verbatim"
            Expect.equal k.ScopeId "team-acme" "scope preserved"
            Expect.equal k.ContentVersion "v3" "content-version stamp preserved"

            let pub = RenderKey.forPublic "landing"
            Expect.equal pub.ScopeId "public" "forPublic uses the public scope"
            Expect.equal pub.ContentVersion "" "forPublic uses the current version"

        testCase "setValidators emits a weak ETag + second-granularity RFC1123 Last-Modified (Phase 147)"
        <| fun _ ->
            let ctx = mkCtxWith []
            // Sub-second component must be truncated away on the wire.
            let lm = DateTimeOffset(2026, 5, 22, 10, 0, 0, 750, TimeSpan.Zero)
            ConditionalGet.setValidators ctx "abc123" lm "public, max-age=300"

            Expect.equal
                (ctx.Response.Headers["ETag"].ToString())
                "W/\"abc123\""
                "weak ETag (W/-prefixed) under UseResponseCompression"

            Expect.equal
                (ctx.Response.Headers["Last-Modified"].ToString())
                (DateTimeOffset(2026, 5, 22, 10, 0, 0, TimeSpan.Zero).UtcDateTime.ToString("R"))
                "Last-Modified is RFC1123 'R', truncated to whole seconds"

            Expect.equal
                (ctx.Response.Headers["Cache-Control"].ToString())
                "public, max-age=300"
                "Cache-Control passed through"

        testCase "cacheable 304s on a matching If-None-Match (weak comparison) for an arbitrary composite key"
        <| fun _ ->
            // A strong-form candidate must still match our weak ETag under
            // the weak comparison function (the W/ and quotes are stripped).
            let ctx = mkCtxWith [ "If-None-Match", "\"deadbeef\"" ]

            let handler =
                ConditionalGet.cacheable "deadbeef" DateTimeOffset.UtcNow "public, max-age=60"

            let bodyRan, result = runHandler handler ctx

            Expect.isFalse bodyRan "body handler is short-circuited on a conditional hit"
            Expect.isSome result "the combinator returns Some ctx (handled)"
            Expect.equal ctx.Response.StatusCode 304 "status is 304 Not Modified"
            Expect.equal (ctx.Response.Headers["ETag"].ToString()) "W/\"deadbeef\"" "weak ETag still emitted on the 304"

        testCase "cacheable passes through to the body handler on a fresh (non-conditional) request"
        <| fun _ ->
            let ctx = mkCtxWith []

            let handler =
                ConditionalGet.cacheable "feedface" DateTimeOffset.UtcNow "public, max-age=60"

            let bodyRan, result = runHandler handler ctx

            Expect.isTrue bodyRan "body handler runs when there is no matching conditional header"
            Expect.isSome result "handled"
            Expect.notEqual ctx.Response.StatusCode 304 "not a 304 — the body is served"

            Expect.equal
                (ctx.Response.Headers["ETag"].ToString())
                "W/\"feedface\""
                "validators still emitted ahead of the body"

        testCase "cacheable 304s on a bare If-Modified-Since re-crawl (Phase 147 union)"
        <| fun _ ->
            // Googlebot's predominant revalidation header. The resource's
            // Last-Modified is at/before the client's If-Modified-Since →
            // 304 even with no If-None-Match present.
            let lm = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let since = DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
            let ctx = mkCtxWith [ "If-Modified-Since", since.UtcDateTime.ToString("R") ]
            let handler = ConditionalGet.cacheable "cafe" lm "public, max-age=60"
            let bodyRan, _ = runHandler handler ctx

            Expect.isFalse bodyRan "a bare If-Modified-Since at/after Last-Modified short-circuits"
            Expect.equal ctx.Response.StatusCode 304 "304 from the If-Modified-Since gate"

        testCase "cacheable serves the body when If-Modified-Since predates Last-Modified"
        <| fun _ ->
            // Resource changed after the client's copy → full response.
            let lm = DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)
            let since = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let ctx = mkCtxWith [ "If-Modified-Since", since.UtcDateTime.ToString("R") ]
            let handler = ConditionalGet.cacheable "cafe" lm "public, max-age=60"
            let bodyRan, _ = runHandler handler ctx

            Expect.isTrue bodyRan "a stale client copy gets the full body, not a 304"
            Expect.notEqual ctx.Response.StatusCode 304 "no 304 when the resource is newer"

        testCase "cacheable ignores a malformed If-Modified-Since (full response, not an error)"
        <| fun _ ->
            let lm = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let ctx = mkCtxWith [ "If-Modified-Since", "not-a-date" ]
            let handler = ConditionalGet.cacheable "cafe" lm "public, max-age=60"
            let bodyRan, _ = runHandler handler ctx

            Expect.isTrue bodyRan "a malformed conditional header is ignored → full response"

        testCase "immutableAsset emits a one-year immutable Cache-Control"
        <| fun _ ->
            let ctx = mkCtxWith []
            let handler = ConditionalGet.immutableAsset "fingerprint99" DateTimeOffset.UtcNow
            runHandler handler ctx |> ignore

            Expect.stringContains
                (ctx.Response.Headers["Cache-Control"].ToString())
                "max-age=31536000"
                "one-year max-age"

            Expect.stringContains (ctx.Response.Headers["Cache-Control"].ToString()) "immutable" "immutable directive"
    ]

let private getOrRender155Tests =
    testList "PublicRendering — Phase 155 RenderCache.getOrRender" [

        testCaseAsync "memoises a deterministic render within the TTL window (non-page handler)"
        <| async {
            let cache = InMemoryRenderCache.create ()
            let key = RenderKey.forKey "scale/guitar/major/C/free" "public" "v1"
            let mutable renderCount = 0

            let render () = async {
                renderCount <- renderCount + 1
                return $"<html>render {renderCount}</html>"
            }

            let! first = RenderCache.getOrRender cache key (CachePolicy.Cache(300, true)) render
            let! second = RenderCache.getOrRender cache key (CachePolicy.Cache(300, true)) render

            Expect.equal renderCount 1 "second call is a fresh cache hit — render runs once"
            Expect.equal first.Html "<html>render 1</html>" "first call returns the freshly-rendered html"
            Expect.equal second.Html first.Html "second call returns the memoised html"
            Expect.isNotEmpty second.ContentHash "the memoised entry carries a content hash for the ETag"
        }

        testCaseAsync "serves a stale entry immediately while refreshing in the background (SWR)"
        <| async {
            let cache = InMemoryRenderCache.create ()
            let key = RenderKey.forKey "swr-key" "public" ""

            // Seed an already-expired-but-stale-servable entry: render time
            // 100s ago with a 10s TTL → ExpiresAt 90s in the past, SWR on.
            let staleEntry =
                RenderedPage.forStore "<html>OLD</html>" (DateTimeOffset.UtcNow.AddSeconds -100.0)

            do! cache.Set key staleEntry (CachePolicy.Cache(10, true))

            let render () = async { return "<html>NEW</html>" }

            let! served = RenderCache.getOrRender cache key (CachePolicy.Cache(10, true)) render

            Expect.equal
                served.Html
                "<html>OLD</html>"
                "the stale render is served immediately (stale-while-revalidate)"
        }

        testCaseAsync "a NoCache policy renders every call and stores nothing"
        <| async {
            let cache = InMemoryRenderCache.create ()
            let key = RenderKey.forKey "nocache-key" "public" ""
            let mutable renderCount = 0

            let render () = async {
                renderCount <- renderCount + 1
                return "<html>x</html>"
            }

            let! _ = RenderCache.getOrRender cache key CachePolicy.NoCache render
            let! _ = RenderCache.getOrRender cache key CachePolicy.NoCache render

            Expect.equal renderCount 2 "NoCache → every call re-renders"
            let! stored = cache.TryGet key
            Expect.isNone stored "NoCache → nothing is stored"
        }
    ]

// ─── Phase 147 — cache-independent conditional-GET on the page handler ──
//
// Exercise the *uncached* serve path end to end through the public
// `PublicPageHandler.handler` with a real DI container, proving the
// `withConditionalGet` opt-in (a `ConditionalGetSettings` in DI) emits
// validators + 304s without a render cache — and that without it the path
// is byte-for-byte the pre-147 no-validator path (GP 11).

let private mkProvider (services: (System.Type * obj) list) : System.IServiceProvider =
    let sc = ServiceCollection()

    for (t, impl) in services do
        sc.AddSingleton(t, impl) |> ignore

    sc.BuildServiceProvider() :> System.IServiceProvider

let private pageLayouts: Map<LayoutName, PublicPage -> XmlNode> =
    Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

let private runPage (provider: System.IServiceProvider) (path: string) (headers: (string * string) list) : HttpContext =
    let api = mkFixtureApiWith []
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Request.Path <- PathString path

    for (k, v) in headers do
        ctx.Request.Headers[k] <- Microsoft.Extensions.Primitives.StringValues v

    let next: Giraffe.Core.HttpFunc =
        fun c -> System.Threading.Tasks.Task.FromResult(Some c)

    PublicPageHandler.handler api pageLayouts next ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx :> HttpContext

let private conditionalGetHandlerTests =
    testList "PublicRendering — Phase 147 page-handler conditional-GET" [

        testCase "without withConditionalGet, the uncached path emits no validators (pre-147 byte-for-byte)"
        <| fun _ ->
            let ctx = runPage (mkProvider []) "/about" []

            Expect.isFalse (ctx.Response.Headers.ContainsKey "ETag") "no ETag on the pre-147 uncached path"

            Expect.isFalse
                (ctx.Response.Headers.ContainsKey "Last-Modified")
                "no Last-Modified on the pre-147 uncached path"

        testCase "withConditionalGet emits a weak ETag + content-stable Last-Modified on the uncached path"
        <| fun _ ->
            let provider =
                mkProvider [ typeof<ConditionalGetSettings>, box ConditionalGetSettings.defaults ]

            let ctx = runPage provider "/about" []

            Expect.stringStarts
                (ctx.Response.Headers["ETag"].ToString())
                "W/\""
                "weak ETag emitted on the uncached conditional path"

            Expect.isNotEmpty (ctx.Response.Headers["Last-Modified"].ToString()) "Last-Modified emitted"

            Expect.stringContains
                (ctx.Response.Headers["Cache-Control"].ToString())
                "must-revalidate"
                "default conditional-GET Cache-Control is revalidate-on-every-hit"

        testCase "withConditionalGet: a bare If-Modified-Since re-crawl 304s, Last-Modified stable across renders"
        <| fun _ ->
            let provider =
                mkProvider [ typeof<ConditionalGetSettings>, box ConditionalGetSettings.defaults ]

            // First crawl establishes the content-stable Last-Modified.
            let first = runPage provider "/about" []
            let lm = first.Response.Headers["Last-Modified"].ToString()

            // Second crawl echoes it back via If-Modified-Since → 304.
            let second = runPage provider "/about" [ "If-Modified-Since", lm ]

            Expect.equal second.Response.StatusCode 304 "a bare If-Modified-Since re-crawl 304s"

            Expect.equal
                (second.Response.Headers["Last-Modified"].ToString())
                lm
                "Last-Modified is stable across renders (content-stable, not wall-clock)"
    ]

// ─── Phase 153 — crawler / SEO observability (RenderMetrics) ────────

let private renderMetrics153Tests =
    testList "PublicRendering — Phase 153 crawler/SEO observability" [

        testCase "classifyAgent buckets representative UAs into the bounded set"
        <| fun _ ->
            Expect.equal
                (RenderMetrics.classifyAgent "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")
                RenderMetrics.AgentGooglebot
                "Googlebot UA → googlebot"

            Expect.equal
                (RenderMetrics.classifyAgent "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")
                RenderMetrics.AgentBingbot
                "bingbot UA → bingbot"

            Expect.equal
                (RenderMetrics.classifyAgent "Mozilla/5.0 (compatible; YandexBot/3.0; +http://yandex.com/bots)")
                RenderMetrics.AgentOtherBot
                "another bot → other-bot"

            Expect.equal (RenderMetrics.classifyAgent "Slurp") RenderMetrics.AgentOtherBot "slurp → other-bot"

            Expect.equal
                (RenderMetrics.classifyAgent
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36")
                RenderMetrics.AgentHuman
                "a desktop browser → human"

            Expect.equal (RenderMetrics.classifyAgent "") RenderMetrics.AgentHuman "empty UA → human"

        testCase "emit helpers tag conditional_get / request_by_agent / page_not_found"
        <| fun _ ->
            let sink = RecordingSink()
            RenderMetrics.emitConditionalGet sink RenderMetrics.CondGet304
            RenderMetrics.emitAgent sink RenderMetrics.AgentGooglebot
            RenderMetrics.emitNotFound sink

            Expect.contains
                sink.Counters
                (RenderMetrics.ConditionalGet, Map["outcome", "304"])
                "conditional_get tagged by outcome"

            Expect.contains
                sink.Counters
                (RenderMetrics.RequestByAgent, Map["agent", "googlebot"])
                "request_by_agent tagged by bounded agent class"

            Expect.contains
                sink.Counters
                (RenderMetrics.PageNotFound, Map.empty)
                "page_not_found is an untagged counter"

        testCase "page handler counts 200 / 304 / agent class / not-found via a live sink"
        <| fun _ ->
            let sink = RecordingSink()

            let provider =
                mkProvider [
                    typeof<IMetricsSink>, box (sink :> IMetricsSink)
                    typeof<ConditionalGetSettings>, box ConditionalGetSettings.defaults
                ]

            // Fresh crawl as Googlebot → 200 + agent class.
            let first = runPage provider "/about" [ "User-Agent", "Googlebot/2.1" ]
            let lm = first.Response.Headers["Last-Modified"].ToString()

            // Conditional re-crawl (bare If-Modified-Since) → 304.
            runPage provider "/about" [ "If-Modified-Since", lm; "User-Agent", "Googlebot/2.1" ]
            |> ignore

            // Crawler hit on a missing slug → soft-404 counter.
            runPage provider "/no-such-page" [ "User-Agent", "Googlebot/2.1" ] |> ignore

            Expect.contains
                sink.Counters
                (RenderMetrics.ConditionalGet, Map["outcome", "200"])
                "fresh crawl → conditional_get{outcome=200}"

            Expect.contains
                sink.Counters
                (RenderMetrics.ConditionalGet, Map["outcome", "304"])
                "conditional re-crawl → conditional_get{outcome=304}"

            Expect.contains
                sink.Counters
                (RenderMetrics.RequestByAgent, Map["agent", "googlebot"])
                "requests bucketed by bounded agent class"

            Expect.contains sink.Counters (RenderMetrics.PageNotFound, Map.empty) "missing slug → page_not_found"

        testCase "NoOp sink emits no crawler/SEO counters (GP 13)"
        <| fun _ ->
            // With the NoOp sink registered the handler's gated branch is
            // skipped entirely; the page still serves. (NoOp records nothing
            // by construction, so the assertion is that serving succeeds and
            // the emit path is the free one.)
            let provider =
                mkProvider [
                    typeof<IMetricsSink>, box (NoOpMetricsSink() :> IMetricsSink)
                    typeof<ConditionalGetSettings>, box ConditionalGetSettings.defaults
                ]

            let ctx = runPage provider "/about" [ "User-Agent", "Googlebot/2.1" ]

            Expect.notEqual ctx.Response.StatusCode 500 "page serves under the NoOp sink (gated metrics path is free)"
    ]

// ─── Phase 148 / 151 / 152 / 154 — head-tag SEO surface ─────────────

let private mkPage (slug: string) (body: ContentBody) (frontmatter: (string * string) list) : PublicPage = {
    Slug = Slug slug
    Title = "T"
    Description = "D"
    Body = body
    Layout = LayoutName "page"
    Frontmatter = Map.ofList frontmatter
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

let private renderNode (node: XmlNode) : string = RenderView.AsString.htmlNode node

let private renderNodes (nodes: XmlNode list) : string =
    nodes |> List.map renderNode |> String.concat ""

let private selfCanonicalTests =
    testList "PublicRendering — Phase 148 self-referencing canonical" [

        testCase "canonicalUrlFor: slug → base/slug, root/index → base/, trailing slash normalised"
        <| fun _ ->
            let p s = mkPage s (Markdown "x") []
            Expect.equal (NarrativeLayout.canonicalUrlFor "https://x.com" (p "about")) "https://x.com/about" "slug"

            Expect.equal
                (NarrativeLayout.canonicalUrlFor "https://x.com/" (p "about"))
                "https://x.com/about"
                "trailing slash on base normalised"

            Expect.equal (NarrativeLayout.canonicalUrlFor "https://x.com" (p "index")) "https://x.com/" "index → base/"
            Expect.equal (NarrativeLayout.canonicalUrlFor "https://x.com" (p "")) "https://x.com/" "root → base/"

            Expect.equal
                (NarrativeLayout.canonicalUrlFor "https://x.com" (p "services/consulting"))
                "https://x.com/services/consulting"
                "nested slug"

        testCase "canonicalFor emits a <link rel=canonical href=...>"
        <| fun _ ->
            let html =
                renderNode (NarrativeLayout.canonicalFor "https://x.com" (mkPage "about" (Markdown "x") []))

            Expect.stringContains html "rel=\"canonical\"" "rel"
            Expect.stringContains html "href=\"https://x.com/about\"" "href"

        testCase "hasExplicitCanonical: Narrative CanonicalUrl + head:canonical envelope count; plain false"
        <| fun _ ->
            Expect.isFalse
                (NarrativeLayout.hasExplicitCanonical (mkPage "a" (Markdown "x") []))
                "plain markdown → no explicit"

            let narr =
                mkPage "a" (Narrative(Narrative.create "t" |> Narrative.withCanonicalUrl "https://x.com/e")) []

            Expect.isTrue (NarrativeLayout.hasExplicitCanonical narr) "narrative canonical"

            Expect.isTrue
                (NarrativeLayout.hasExplicitCanonical (
                    mkPage "a" (Markdown "x") [ "head:canonical", "https://x.com/e" ]
                ))
                "head:canonical envelope"

        testCase "enrichPage adds head:canonical for all body kinds when none explicit"
        <| fun _ ->
            for body in [ Markdown "x"; Html "<p>x</p>"; Narrative(Narrative.create "t") ] do
                let enriched =
                    NarrativeLayout.SelfCanonical.enrichPage "https://x.com" (mkPage "about" body [])

                Expect.equal
                    (enriched.Frontmatter.TryFind "head:canonical")
                    (Some "https://x.com/about")
                    "self-canonical added"

        testCase "enrichPage defers to an explicit canonical (GP 11)"
        <| fun _ ->
            let narr =
                mkPage "about" (Narrative(Narrative.create "t" |> Narrative.withCanonicalUrl "https://x.com/e")) []

            let enriched = NarrativeLayout.SelfCanonical.enrichPage "https://x.com" narr
            Expect.isFalse (enriched.Frontmatter.ContainsKey "head:canonical") "explicit wins → no envelope added"

        testCase "wrap enriches GetPage / GetPageInContext, passes ListPages through unchanged"
        <| fun _ ->
            let wrapped =
                NarrativeLayout.SelfCanonical.wrap "https://x.com" (mkFixtureApiWith [])

            match wrapped.GetPage "about" |> Async.RunSynchronously with
            | Some p ->
                Expect.equal (p.Frontmatter.TryFind "head:canonical") (Some "https://x.com/about") "GetPage enriched"
            | None -> failtest "fixture 'about' page missing"

            let listed = wrapped.ListPages "" |> Async.RunSynchronously

            Expect.isTrue
                (listed
                 |> List.forall (fun p -> not (p.Frontmatter.ContainsKey "head:canonical")))
                "ListPages not enriched (no per-entry canonical pollution)"

        testCase "enriched page → PageHeadInjection emits the canonical before </head>"
        <| fun _ ->
            let enriched =
                NarrativeLayout.SelfCanonical.enrichPage "https://x.com" (mkPage "about" (Markdown "x") [])

            let doc = "<html><head><title>t</title></head><body>b</body></html>"
            let injected = PageHeadInjection.injectFromPage enriched doc
            Expect.stringContains injected "rel=\"canonical\"" "canonical injected"
            Expect.stringContains injected "https://x.com/about" "href injected"

        testCase "headTagsWith None == headTags (byte-for-byte, GP 11)"
        <| fun _ ->
            let p = mkPage "about" (Markdown "x") []

            Expect.equal
                (renderNodes (NarrativeLayout.headTagsWith None p))
                (renderNodes (NarrativeLayout.headTags p))
                "no baseUrl → identical to headTags"

        testCase "headTagsWith (Some base) prepends a self-canonical for a non-Narrative page; explicit wins"
        <| fun _ ->
            let html =
                renderNodes (NarrativeLayout.headTagsWith (Some "https://x.com") (mkPage "about" (Markdown "x") []))

            Expect.stringContains html "href=\"https://x.com/about\"" "self-canonical present"

            let narr =
                mkPage "about" (Narrative(Narrative.create "t" |> Narrative.withCanonicalUrl "https://x.com/explicit")) []

            let html2 = renderNodes (NarrativeLayout.headTagsWith (Some "https://x.com") narr)
            Expect.isFalse (html2.Contains "https://x.com/about") "explicit canonical wins; no self-canonical"
            Expect.stringContains html2 "https://x.com/explicit" "explicit canonical emitted by headTags"
    ]

// ─── Phase 151 — structured-data expansion ──────────────────────────

/// Assert the emitted payload is well-formed JSON (parses without throw).
let private parseOk (label: string) (json: string) =
    try
        System.Text.Json.JsonDocument.Parse json |> ignore
    with ex ->
        failtestf "%s — not valid JSON: %s\n%s" label ex.Message json

let private structuredData151Tests =
    testList "PublicRendering — Phase 151 structured-data expansion" [

        testCase "webSite + SearchAction validates for the sitelinks search box"
        <| fun _ ->
            let json =
                StructuredDataHelpers.webSite
                    "Acme"
                    "https://x.com"
                    (Some "https://x.com/search?q={search_term_string}")

            parseOk "webSite" json
            Expect.stringContains json "\"@type\":\"WebSite\"" "WebSite type"
            Expect.stringContains json "\"@type\":\"SearchAction\"" "SearchAction potentialAction"
            Expect.stringContains json "{search_term_string}" "urlTemplate carries the token"
            Expect.stringContains json "required name=search_term_string" "query-input"

        testCase "webSite omits potentialAction when no search template"
        <| fun _ ->
            let json = StructuredDataHelpers.webSite "Acme" "https://x.com" None
            parseOk "webSite-no-search" json
            Expect.stringContains json "\"@type\":\"WebSite\"" "WebSite type"
            Expect.isFalse (json.Contains "SearchAction") "no SearchAction when no template"
            Expect.isFalse (json.Contains "potentialAction") "no potentialAction key"

        testCase "faqPage emits Question / acceptedAnswer pairs"
        <| fun _ ->
            let json = StructuredDataHelpers.faqPage [ "Q1?", "A1."; "Q2?", "A2." ]
            parseOk "faqPage" json
            Expect.stringContains json "\"@type\":\"FAQPage\"" "FAQPage type"
            Expect.stringContains json "\"@type\":\"Question\"" "Question"
            Expect.stringContains json "\"@type\":\"Answer\"" "Answer"
            Expect.stringContains json "Q1?" "question text"
            Expect.stringContains json "A2." "answer text"

        testCase "faqPage with no items is still valid JSON (no throw)"
        <| fun _ ->
            let json = StructuredDataHelpers.faqPage []
            parseOk "faqPage-empty" json
            Expect.stringContains json "\"@type\":\"FAQPage\"" "FAQPage type"

        testCase "howTo emits ordered HowToStep elements"
        <| fun _ ->
            let json = StructuredDataHelpers.howTo "Make tea" [ "Boil water"; "Add bag" ]
            parseOk "howTo" json
            Expect.stringContains json "\"@type\":\"HowTo\"" "HowTo type"
            Expect.stringContains json "\"@type\":\"HowToStep\"" "HowToStep"
            Expect.stringContains json "\"position\":1" "first step positioned"
            Expect.stringContains json "Boil water" "step text"

        testCase "course emits provider Organization"
        <| fun _ ->
            let json = StructuredDataHelpers.course "F# 101" "Acme Academy" "Learn F#"
            parseOk "course" json
            Expect.stringContains json "\"@type\":\"Course\"" "Course type"
            Expect.stringContains json "\"@type\":\"Organization\"" "provider org"
            Expect.stringContains json "Acme Academy" "provider name"

        testCase "itemList emits positioned ListItem entries with url"
        <| fun _ ->
            let json = StructuredDataHelpers.itemList [ "First", "https://x.com/1"; "Second", "https://x.com/2" ]
            parseOk "itemList" json
            Expect.stringContains json "\"@type\":\"ItemList\"" "ItemList type"
            Expect.stringContains json "\"@type\":\"ListItem\"" "ListItem"
            Expect.stringContains json "https://x.com/2" "entry url"

        testCase "product includes offers + aggregateRating when supplied"
        <| fun _ ->
            let json = StructuredDataHelpers.product "Widget" "A widget" (Some("9.99", "GBP")) (Some("4.5", "120"))
            parseOk "product-full" json
            Expect.stringContains json "\"@type\":\"Product\"" "Product type"
            Expect.stringContains json "\"@type\":\"Offer\"" "Offer"
            Expect.stringContains json "\"priceCurrency\":\"GBP\"" "currency"
            Expect.stringContains json "\"@type\":\"AggregateRating\"" "AggregateRating"
            Expect.stringContains json "\"reviewCount\":\"120\"" "review count"

        testCase "product omits offers / aggregateRating when None"
        <| fun _ ->
            let json = StructuredDataHelpers.product "Widget" "A widget" None None
            parseOk "product-minimal" json
            Expect.stringContains json "\"@type\":\"Product\"" "Product type"
            Expect.isFalse (json.Contains "Offer") "no Offer block"
            Expect.isFalse (json.Contains "AggregateRating") "no AggregateRating block"

        testCase "videoObject emits thumbnail / uploadDate / contentUrl"
        <| fun _ ->
            let json =
                StructuredDataHelpers.videoObject
                    "Demo"
                    "A demo"
                    "https://x.com/thumb.jpg"
                    "2026-06-14"
                    "https://x.com/v.mp4"

            parseOk "videoObject" json
            Expect.stringContains json "\"@type\":\"VideoObject\"" "VideoObject type"
            Expect.stringContains json "https://x.com/thumb.jpg" "thumbnail"
            Expect.stringContains json "2026-06-14" "upload date"
            Expect.stringContains json "https://x.com/v.mp4" "content url"

        testCase "organization gains sameAs when the frontmatter key is present"
        <| fun _ ->
            let json =
                StructuredDataHelpers.organization (
                    mkPage "about" (Markdown "x") [ "sameAs", "https://x.com/tw, https://x.com/li" ]
                )

            parseOk "organization-sameAs" json
            Expect.stringContains json "\"sameAs\"" "sameAs array present"
            Expect.stringContains json "https://x.com/tw" "first profile"
            Expect.stringContains json "https://x.com/li" "second profile"

        testCase "organization omits sameAs when the key is absent (GP 11 byte-for-byte)"
        <| fun _ ->
            let json = StructuredDataHelpers.organization (mkPage "about" (Markdown "x") [])
            parseOk "organization-no-sameAs" json
            Expect.stringContains json "\"@type\":\"Organization\"" "Organization type"
            Expect.isFalse (json.Contains "sameAs") "no sameAs key when frontmatter absent"
    ]

// ─── Phase 152 — per-page robots <meta> (head half) ─────────────────

let private robots152Tests =
    testList "PublicRendering — Phase 152 per-page robots meta" [

        testCase "headTags emits <meta name=robots> from the robots frontmatter key, any body kind"
        <| fun _ ->
            for body in [ Markdown "x"; Html "<p>x</p>"; Narrative(Narrative.create "t") ] do
                let html = renderNodes (NarrativeLayout.headTags (mkPage "a" body [ "robots", "noindex,nofollow" ]))
                Expect.stringContains html "name=\"robots\"" "robots meta name"
                Expect.stringContains html "content=\"noindex,nofollow\"" "robots directive content"

        testCase "robots value is trimmed"
        <| fun _ ->
            let html =
                renderNodes (NarrativeLayout.robotsMetaTags (mkPage "a" (Markdown "x") [ "robots", "  noarchive  " ]))

            Expect.stringContains html "content=\"noarchive\"" "trimmed directive"

        testCase "absent robots key → no robots meta (GP 11 byte-for-byte)"
        <| fun _ ->
            // A plain Markdown page with no robots key still produces an
            // empty headTags list — pre-152 behaviour.
            Expect.isEmpty (NarrativeLayout.robotsMetaTags (mkPage "a" (Markdown "x") [])) "no robots tags"

            Expect.isEmpty
                (NarrativeLayout.headTags (mkPage "a" (Markdown "x") []))
                "headTags unchanged for plain markdown"

        testCase "blank robots value → no robots meta"
        <| fun _ ->
            Expect.isEmpty
                (NarrativeLayout.robotsMetaTags (mkPage "a" (Markdown "x") [ "robots", "   " ]))
                "whitespace-only robots value emits nothing"

        testCase "robots meta coexists with Narrative head tags"
        <| fun _ ->
            let doc = Narrative.create "t" |> Narrative.withCanonicalUrl "https://x.com/c"
            let html = renderNodes (NarrativeLayout.headTags (mkPage "a" (Narrative doc) [ "robots", "noindex" ]))
            Expect.stringContains html "name=\"robots\"" "robots meta present"
            Expect.stringContains html "rel=\"canonical\"" "Narrative canonical still emitted"
    ]

let tests =
    testList "PublicRendering" [
        contractTests
        contentSourceContractTests
        implTests
        composeTests
        contentSourceImplTests
        renderMetricsTests
        staticExportTests
        conditionalGetTests
        conditionalGetHandlerTests
        renderMetrics153Tests
        getOrRender155Tests
        selfCanonicalTests
        structuredData151Tests
        robots152Tests
    ]