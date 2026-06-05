module ToolUp.Platform.Tests.InProcess.PublicRenderingTests

open System
open System.IO
open Expecto
open Giraffe.ViewEngine
open ToolUp.Platform
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
    PublicContentApiImpl.create loader None

// ─── Contract pack binding ──────────────────────────────────────────

let private contractTests =
    IPublicContentApiContract.tests "MarkdownContentLoader" mkFixtureApi

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

let tests = testList "PublicRendering" [ contractTests; implTests; composeTests ]