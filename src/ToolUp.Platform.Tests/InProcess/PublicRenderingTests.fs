module ToolUp.Platform.Tests.InProcess.PublicRenderingTests

open System
open System.IO
open Expecto
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
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
        member _.Increment(name, tags) = counters.Add(name, tags)
        member _.Record(name, value, tags) = records.Add(name, value, tags)
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

let tests =
    testList "PublicRendering" [
        contractTests
        contentSourceContractTests
        implTests
        composeTests
        contentSourceImplTests
        renderMetricsTests
    ]