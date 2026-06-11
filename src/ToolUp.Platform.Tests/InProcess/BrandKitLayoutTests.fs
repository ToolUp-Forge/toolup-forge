module ToolUp.Platform.Tests.InProcess.BrandKitLayoutTests

open Expecto
open Giraffe.ViewEngine
open ToolUp.BrandKit
open ToolUp.BrandKit.Layouts

/// Phase 92 — BrandKit layout-library render-shape tests. The seven
/// layouts are pure `ChromeSpec -> spec -> XmlNode` functions; these
/// tests pin the markup contract — the accessibility baseline (skip
/// link, landmarks, single `<h1>`, focus target), the class hooks the
/// reference stylesheet sizes, and the optional-slot rule (a `None`
/// slot omits its wrapper entirely). Styling itself is consumer CSS
/// over the `--bk-*` variables, so nothing visual is asserted —
/// theming across contrasting brand-token sets is markup-invariant by
/// construction (the hooks are identical; only the consumer's `:root`
/// changes).

let private render (node: XmlNode) : string = RenderView.AsString.htmlNode node

let private countOf (needle: string) (haystack: string) : int = (haystack.Split needle).Length - 1

let private sentinel (name: string) : XmlNode = div [ _class ("sentinel-" + name) ] []

let private chrome = {
    Chrome.create "Test Page" with
        Description = Some "A test description"
        Stylesheets = [ "/brand.css" ]
        Header =
            Some(
                PageChrome.pageHeader {
                    Brand = PageChrome.Monogram("/m.svg", "Brand")
                    Nav = [ { Label = "Home"; Href = "/" } ]
                    Right = []
                }
            )
        Footer = Some(PageChrome.pageFooter { Copyright = "© Test"; Links = [] })
        BodyClass = Some "theme-test"
}

let private fullArticle: ArticleSpec = {
    Eyebrow = Some "NEWS"
    Title = "Headline"
    Lede = Some "The standfirst."
    Meta = [ Pill.pill "tag-one" ]
    Hero = Some(sentinel "hero")
    Body = sentinel "body"
    Aside = Some(sentinel "aside")
    Breadcrumb = Some(sentinel "crumbs")
}

let tests =
    testList "ToolUp.BrandKit — layout library (Phase 92)" [

        // ─── Shell baseline (shared by all seven) ─────────────

        testCase "shell: skip link is the first body element and targets #bk-main"
        <| fun _ ->
            let html = Layout.article chrome fullArticle |> render
            Expect.stringContains html "bk-skip-link" "skip-link hook present"
            Expect.stringContains html "href=\"#bk-main\"" "skip link targets the main id"
            Expect.stringContains html "id=\"bk-main\"" "main carries the target id"
            Expect.stringContains html "tabindex=\"-1\"" "main is programmatically focusable"

            let bodyStart = html.IndexOf "<body"
            let skipLinkAt = html.IndexOf "bk-skip-link"
            let headerAt = html.IndexOf "bk-header"
            Expect.isTrue (bodyStart < skipLinkAt && skipLinkAt < headerAt) "skip link precedes the header landmark"

        testCase "shell: chrome lands — lang, title, description, stylesheet, header, footer, body class"
        <| fun _ ->
            let html = Layout.article chrome fullArticle |> render
            Expect.stringContains html "lang=\"en\"" "html lang set"
            Expect.stringContains html "<title>Test Page</title>" "document title set"
            Expect.stringContains html "A test description" "meta description set"
            Expect.stringContains html "href=\"/brand.css\"" "stylesheet linked"
            Expect.stringContains html "bk-header" "header landmark rendered"
            Expect.stringContains html "bk-footer" "footer landmark rendered"
            Expect.stringContains html "bk-page theme-test" "body class extends bk-page"

        testCase "shell: minimal chrome omits description / stylesheets / header / footer"
        <| fun _ ->
            let bare = Chrome.create "Bare"
            let html = Layout.article bare { fullArticle with Aside = None } |> render
            Expect.isFalse (html.Contains "name=\"description\"") "no description meta"
            Expect.isFalse (html.Contains "stylesheet") "no stylesheet link"
            Expect.isFalse (html.Contains "bk-header") "no header landmark"
            Expect.isFalse (html.Contains "bk-footer") "no footer landmark"

        // ─── Article ──────────────────────────────────────────

        testCase "article: full slots render with their hooks, eyebrow via the BrandKit primitive"
        <| fun _ ->
            let html = Layout.article chrome fullArticle |> render
            Expect.stringContains html "bk-layout-article" "layout class on main"
            Expect.stringContains html "bk-eyebrow" "eyebrow uses the BrandKit primitive"
            Expect.stringContains html "<h1 class=\"bk-article-title\">Headline</h1>" "h1 title"
            Expect.stringContains html "bk-article-lede" "lede hook"
            Expect.stringContains html "bk-article-meta" "meta hook"
            Expect.stringContains html "bk-tag" "meta renders the supplied pill"
            Expect.stringContains html "bk-article-hero" "hero figure hook"
            Expect.stringContains html "sentinel-body" "body slot rendered"
            Expect.stringContains html "bk-article-aside" "aside hook"
            Expect.stringContains html "sentinel-crumbs" "breadcrumb slot rendered"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

        testCase "article: None slots omit their wrappers entirely"
        <| fun _ ->
            let minimal = {
                fullArticle with
                    Eyebrow = None
                    Lede = None
                    Meta = []
                    Hero = None
                    Aside = None
                    Breadcrumb = None
            }

            let html = Layout.article chrome minimal |> render
            Expect.isFalse (html.Contains "bk-eyebrow") "no eyebrow"
            Expect.isFalse (html.Contains "bk-article-lede") "no lede wrapper"
            Expect.isFalse (html.Contains "bk-article-meta") "no meta wrapper"
            Expect.isFalse (html.Contains "bk-article-hero") "no hero wrapper"
            Expect.isFalse (html.Contains "bk-article-aside") "no aside wrapper"

        // ─── Landing ──────────────────────────────────────────

        testCase "landing: hero copy + actions + visual + one wrapper per section"
        <| fun _ ->
            let spec: LandingSpec = {
                HeroEyebrow = Some "PLATFORM"
                HeroTitle = "Build it"
                HeroLede = Some "Faster."
                HeroActions = [ a [ _href "/start" ] [ str "Start" ] ]
                HeroVisual = Some(sentinel "visual")
                Sections = [ sentinel "s1"; sentinel "s2" ]
            }

            let html = Layout.landing chrome spec |> render
            Expect.stringContains html "bk-layout-landing" "layout class on main"
            Expect.stringContains html "bk-landing-hero" "hero hook"
            Expect.stringContains html "<h1 class=\"bk-landing-title\">Build it</h1>" "h1 title"
            Expect.stringContains html "bk-landing-actions" "actions hook"
            Expect.stringContains html "bk-landing-visual" "visual hook"
            Expect.equal (countOf "bk-landing-section" html) 2 "one wrapper per section"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

        // ─── Dashboard ────────────────────────────────────────

        testCase "dashboard: kpi + panel grids carry bk-grid; empty regions omit their wrappers"
        <| fun _ ->
            let spec: DashboardSpec = {
                Title = "Ops"
                Toolbar = [ sentinel "toolbar" ]
                Kpis = [ Card.cardTight [ str "42" ] ]
                Panels = [ Card.card [ sentinel "panel" ] ]
            }

            let html = Layout.dashboard chrome spec |> render
            Expect.stringContains html "bk-layout-dashboard" "layout class on main"
            Expect.stringContains html "bk-dashboard-kpis bk-grid" "kpi grid hook"
            Expect.stringContains html "bk-dashboard-panels bk-grid" "panel grid hook"
            Expect.stringContains html "bk-dashboard-toolbar" "toolbar hook"
            Expect.stringContains html "bk-card" "cards render inside the grids"

            let bareHtml =
                Layout.dashboard chrome { spec with Kpis = []; Toolbar = [] } |> render

            Expect.isFalse (bareHtml.Contains "bk-dashboard-kpis") "empty kpi region omitted"
            Expect.isFalse (bareHtml.Contains "bk-dashboard-toolbar") "empty toolbar omitted"

        // ─── Doc ──────────────────────────────────────────────

        testCase "doc: sidebar / toc / prev-next render and omit cleanly"
        <| fun _ ->
            let spec: DocSpec = {
                Title = "Install"
                Sidebar = Some(sentinel "sidebar")
                Toc = Some(sentinel "toc")
                Body = sentinel "docbody"
                Breadcrumb = Some(sentinel "crumbs")
                PrevNext = Some(sentinel "pager")
            }

            let html = Layout.doc chrome spec |> render
            Expect.stringContains html "bk-layout-doc" "layout class on main"
            Expect.stringContains html "bk-doc-sidebar" "sidebar hook"
            Expect.stringContains html "bk-doc-toc" "toc hook"
            Expect.stringContains html "bk-doc-prevnext" "prev-next hook"
            Expect.stringContains html "sentinel-docbody" "body slot rendered"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

            let bareHtml =
                Layout.doc chrome {
                    spec with
                        Sidebar = None
                        Toc = None
                        PrevNext = None
                }
                |> render

            Expect.isFalse (bareHtml.Contains "bk-doc-sidebar") "no sidebar wrapper"
            Expect.isFalse (bareHtml.Contains "bk-doc-toc") "no toc wrapper"
            Expect.isFalse (bareHtml.Contains "bk-doc-prevnext") "no prev-next wrapper"

        // ─── Gallery ──────────────────────────────────────────

        testCase "gallery: item grid carries bk-grid; pager optional"
        <| fun _ ->
            let spec: GallerySpec = {
                Title = "Shots"
                Intro = Some(sentinel "intro")
                Items = [ sentinel "item1"; sentinel "item2"; sentinel "item3" ]
                Pager = Some(sentinel "pager")
            }

            let html = Layout.gallery chrome spec |> render
            Expect.stringContains html "bk-layout-gallery" "layout class on main"
            Expect.stringContains html "bk-gallery-grid bk-grid" "grid hook"
            Expect.equal (countOf "sentinel-item" html) 3 "all items render"
            Expect.stringContains html "bk-gallery-pager" "pager hook"

            let bareHtml =
                Layout.gallery chrome { spec with Pager = None; Items = [] } |> render

            Expect.isFalse (bareHtml.Contains "bk-gallery-grid") "empty grid omitted"
            Expect.isFalse (bareHtml.Contains "bk-gallery-pager") "no pager wrapper"

        // ─── Video ────────────────────────────────────────────

        testCase "video: player figure + transcript in a details disclosure"
        <| fun _ ->
            let spec: VideoSpec = {
                Title = "Demo"
                Player = sentinel "player"
                Meta = [ Pill.pill "4 min" ]
                Description = Some(sentinel "desc")
                Transcript = Some(sentinel "transcript")
                Related = Some(sentinel "related")
            }

            let html = Layout.video chrome spec |> render
            Expect.stringContains html "bk-layout-video" "layout class on main"
            Expect.stringContains html "bk-video-player" "player figure hook"
            Expect.stringContains html "sentinel-player" "player slot rendered"
            Expect.stringContains html "<details class=\"bk-video-transcript\">" "transcript is a details disclosure"
            Expect.stringContains html "<summary>" "details carries a summary"
            Expect.stringContains html "bk-video-related" "related hook"

            let bareHtml =
                Layout.video chrome {
                    spec with
                        Transcript = None
                        Related = None
                }
                |> render

            Expect.isFalse (bareHtml.Contains "<details") "no transcript disclosure"
            Expect.isFalse (bareHtml.Contains "bk-video-related") "no related wrapper"

        // ─── Knowledge portal ─────────────────────────────────

        testCase "knowledgePortal: search / answer / browse / sidebar render and omit cleanly"
        <| fun _ ->
            let spec: KnowledgePortalSpec = {
                Title = "Knowledge"
                Intro = Some "Ask anything."
                Search = Some(sentinel "search")
                Answer = Some(sentinel "answer")
                Browse = Some(sentinel "browse")
                Sidebar = Some(sentinel "sidebar")
            }

            let html = Layout.knowledgePortal chrome spec |> render
            Expect.stringContains html "bk-layout-knowledge" "layout class on main"
            Expect.stringContains html "bk-knowledge-search" "search hook"
            Expect.stringContains html "bk-knowledge-answer" "answer hook"
            Expect.stringContains html "bk-knowledge-browse" "browse hook"
            Expect.stringContains html "bk-knowledge-sidebar" "sidebar hook"
            Expect.stringContains html "bk-knowledge-intro" "intro hook"
            Expect.equal (countOf "<h1" html) 1 "exactly one h1"

            let bareHtml =
                Layout.knowledgePortal chrome {
                    spec with
                        Search = None
                        Answer = None
                        Browse = None
                        Sidebar = None
                        Intro = None
                }
                |> render

            Expect.isFalse (bareHtml.Contains "bk-knowledge-search") "no search wrapper"
            Expect.isFalse (bareHtml.Contains "bk-knowledge-answer") "no answer wrapper"
            Expect.isFalse (bareHtml.Contains "bk-knowledge-browse") "no browse wrapper"
            Expect.isFalse (bareHtml.Contains "bk-knowledge-sidebar") "no sidebar wrapper"
    ]