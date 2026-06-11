namespace ToolUp.BrandKit.Layouts

open Giraffe.ViewEngine

// ─── Phase 92 — BrandKit layout library: the seven ready layouts ────
//
// `article` / `landing` / `dashboard` / `doc` / `gallery` / `video` /
// `knowledgePortal` — each a pure `ChromeSpec -> <spec> -> XmlNode`
// composing `LayoutShell.page` with a per-layout content structure
// built on the BrandKit primitives (eyebrows, rules, page chrome).
//
// Content arrives as **slots** (`XmlNode` / `XmlNode list`), not as
// domain types — the library has no dependency on any content system.
// A `ToolUp.PublicRendering` consumer glues a slot to its content in
// one line (`Body = NarrativeLayout.renderBody page`); any other SSR
// host passes whatever nodes it has. Optional slots omit their wrapper
// entirely when `None`, so unused regions cost nothing in the markup.
//
// Class-hook conventions (full contract in `docs/platform/layouts.md`):
//   - `<main>` carries `bk-main bk-layout-<name>` (from `LayoutShell`)
//   - regions are `bk-<name>-<region>` (e.g. `bk-article-lede`)
//   - repeated-item regions additionally carry `bk-grid`, the shared
//     responsive-grid hook the reference stylesheet sizes per layout

module private Slot =
    /// Render an optional slot through a wrapper; omit entirely on `None`.
    let wrap (wrapper: XmlNode -> XmlNode) (slot: XmlNode option) : XmlNode list =
        slot |> Option.map wrapper |> Option.toList

    /// Render an optional string through a builder; omit on `None`.
    let text (builder: string -> XmlNode) (slot: string option) : XmlNode list =
        slot |> Option.map builder |> Option.toList

    /// Wrap a non-empty node list; omit the wrapper when the list is empty.
    let list (wrapper: XmlNode list -> XmlNode) (slot: XmlNode list) : XmlNode list =
        match slot with
        | [] -> []
        | nodes -> [ wrapper nodes ]

/// Long-form editorial page — news article, blog post, case study.
///   `Eyebrow`    — kicker above the title (BrandKit eyebrow)
///   `Title`      — the page `<h1>`
///   `Lede`       — standfirst paragraph under the title
///   `Meta`       — byline / date / tag nodes (pills, persona signature)
///   `Hero`       — full-width hero media above the body
///   `Body`       — the rendered content
///   `Aside`      — related links / table of contents beside the body
///   `Breadcrumb` — breadcrumb trail node (e.g. a nav-helper render)
type ArticleSpec = {
    Eyebrow: string option
    Title: string
    Lede: string option
    Meta: XmlNode list
    Hero: XmlNode option
    Body: XmlNode
    Aside: XmlNode option
    Breadcrumb: XmlNode option
}

/// Marketing / product landing page — hero block + stacked sections.
///   `HeroEyebrow` / `HeroTitle` / `HeroLede` — hero copy
///   `HeroActions` — call-to-action nodes (buttons / links)
///   `HeroVisual`  — hero media beside / under the copy
///   `Sections`    — full-width content sections, stacked in order
type LandingSpec = {
    HeroEyebrow: string option
    HeroTitle: string
    HeroLede: string option
    HeroActions: XmlNode list
    HeroVisual: XmlNode option
    Sections: XmlNode list
}

/// Analytics / status dashboard — KPI strip + panel grid.
///   `Title`   — the page `<h1>`
///   `Toolbar` — filter / action nodes pinned beside the title
///   `Kpis`    — stat-card nodes (responsive grid)
///   `Panels`  — chart / table card nodes (responsive grid)
type DashboardSpec = {
    Title: string
    Toolbar: XmlNode list
    Kpis: XmlNode list
    Panels: XmlNode list
}

/// Documentation page — sidebar navigation + prose + on-page TOC.
///   `Title`      — the page `<h1>`
///   `Sidebar`    — section navigation node (e.g. a nav-menu render)
///   `Toc`        — on-page table-of-contents node
///   `Body`       — the rendered content
///   `Breadcrumb` — breadcrumb trail node
///   `PrevNext`   — previous / next pager node
type DocSpec = {
    Title: string
    Sidebar: XmlNode option
    Toc: XmlNode option
    Body: XmlNode
    Breadcrumb: XmlNode option
    PrevNext: XmlNode option
}

/// Media gallery — responsive grid of figures.
///   `Title` — the page `<h1>`
///   `Intro` — introductory node above the grid
///   `Items` — figure nodes (responsive grid)
///   `Pager` — pagination node under the grid
type GallerySpec = {
    Title: string
    Intro: XmlNode option
    Items: XmlNode list
    Pager: XmlNode option
}

/// Video page — player + metadata + transcript + related list.
///   `Title`       — the page `<h1>`
///   `Player`      — the player node (e.g. a rendered Video block)
///   `Meta`        — date / duration / tag nodes
///   `Description` — prose under the player
///   `Transcript`  — transcript node (rendered inside `<details>`)
///   `Related`     — related-videos node beside / under the player
type VideoSpec = {
    Title: string
    Player: XmlNode
    Meta: XmlNode list
    Description: XmlNode option
    Transcript: XmlNode option
    Related: XmlNode option
}

/// Knowledge portal — search + cited answer + browsable documents.
///   `Title`   — the page `<h1>`
///   `Intro`   — introductory copy under the title
///   `Search`  — search form node (e.g. posting to a search endpoint)
///   `Answer`  — cited-answer panel node
///   `Browse`  — browsable document-list node
///   `Sidebar` — collection navigation node
type KnowledgePortalSpec = {
    Title: string
    Intro: string option
    Search: XmlNode option
    Answer: XmlNode option
    Browse: XmlNode option
    Sidebar: XmlNode option
}

[<RequireQualifiedAccess>]
module Layout =
    module BkText = ToolUp.BrandKit.Text

    /// Long-form editorial page.
    let article (chrome: ChromeSpec) (spec: ArticleSpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-article" [
            yield! Slot.wrap id spec.Breadcrumb
            // `tag "article"` rather than the `article` element helper —
            // inside `module Layout` the bare name resolves to the layout
            // function defined here, not the Giraffe element.
            yield
                tag "article" [ _class "bk-article" ] [
                    yield
                        tag "header" [ _class "bk-article-header" ] [
                            yield! Slot.text BkText.eyebrow spec.Eyebrow
                            yield h1 [ _class "bk-article-title" ] [ str spec.Title ]
                            yield! Slot.text (fun lede -> p [ _class "bk-article-lede" ] [ str lede ]) spec.Lede
                            yield! Slot.list (div [ _class "bk-article-meta" ]) spec.Meta
                        ]
                    yield! Slot.wrap (fun h -> figure [ _class "bk-article-hero" ] [ h ]) spec.Hero
                    yield
                        div [ _class "bk-article-columns" ] [
                            yield div [ _class "bk-article-body" ] [ spec.Body ]
                            yield! Slot.wrap (fun a -> aside [ _class "bk-article-aside" ] [ a ]) spec.Aside
                        ]
                ]
        ]

    /// Marketing / product landing page.
    let landing (chrome: ChromeSpec) (spec: LandingSpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-landing" [
            yield
                section [ _class "bk-landing-hero" ] [
                    yield
                        div [ _class "bk-landing-hero-copy" ] [
                            yield! Slot.text BkText.eyebrow spec.HeroEyebrow
                            yield h1 [ _class "bk-landing-title" ] [ str spec.HeroTitle ]
                            yield! Slot.text (fun lede -> p [ _class "bk-landing-lede" ] [ str lede ]) spec.HeroLede
                            yield! Slot.list (div [ _class "bk-landing-actions" ]) spec.HeroActions
                        ]
                    yield! Slot.wrap (fun v -> div [ _class "bk-landing-visual" ] [ v ]) spec.HeroVisual
                ]
            yield!
                spec.Sections
                |> List.map (fun s -> section [ _class "bk-landing-section" ] [ s ])
        ]

    /// Analytics / status dashboard.
    let dashboard (chrome: ChromeSpec) (spec: DashboardSpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-dashboard" [
            yield
                tag "header" [ _class "bk-dashboard-header" ] [
                    yield h1 [ _class "bk-dashboard-title" ] [ str spec.Title ]
                    yield! Slot.list (div [ _class "bk-dashboard-toolbar" ]) spec.Toolbar
                ]
            yield! Slot.list (section [ _class "bk-dashboard-kpis bk-grid" ]) spec.Kpis
            yield! Slot.list (section [ _class "bk-dashboard-panels bk-grid" ]) spec.Panels
        ]

    /// Documentation page.
    let doc (chrome: ChromeSpec) (spec: DocSpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-doc" [
            yield! Slot.wrap id spec.Breadcrumb
            yield
                div [ _class "bk-doc-columns" ] [
                    yield! Slot.wrap (fun s -> div [ _class "bk-doc-sidebar" ] [ s ]) spec.Sidebar
                    yield
                        tag "article" [ _class "bk-doc-content" ] [
                            yield h1 [ _class "bk-doc-title" ] [ str spec.Title ]
                            yield div [ _class "bk-doc-body" ] [ spec.Body ]
                            yield! Slot.wrap (fun pn -> div [ _class "bk-doc-prevnext" ] [ pn ]) spec.PrevNext
                        ]
                    yield! Slot.wrap (fun t -> aside [ _class "bk-doc-toc" ] [ t ]) spec.Toc
                ]
        ]

    /// Media gallery.
    let gallery (chrome: ChromeSpec) (spec: GallerySpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-gallery" [
            yield
                tag "header" [ _class "bk-gallery-header" ] [
                    yield h1 [ _class "bk-gallery-title" ] [ str spec.Title ]
                    yield! Slot.wrap (fun i -> div [ _class "bk-gallery-intro" ] [ i ]) spec.Intro
                ]
            yield! Slot.list (div [ _class "bk-gallery-grid bk-grid" ]) spec.Items
            yield! Slot.wrap (fun p -> div [ _class "bk-gallery-pager" ] [ p ]) spec.Pager
        ]

    /// Video page.
    let video (chrome: ChromeSpec) (spec: VideoSpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-video" [
            yield h1 [ _class "bk-video-title" ] [ str spec.Title ]
            yield figure [ _class "bk-video-player" ] [ spec.Player ]
            yield! Slot.list (div [ _class "bk-video-meta" ]) spec.Meta
            yield! Slot.wrap (fun d -> div [ _class "bk-video-description" ] [ d ]) spec.Description
            yield!
                Slot.wrap
                    (fun t ->
                        tag "details" [ _class "bk-video-transcript" ] [ tag "summary" [] [ str "Transcript" ]; t ])
                    spec.Transcript
            yield! Slot.wrap (fun r -> aside [ _class "bk-video-related" ] [ r ]) spec.Related
        ]

    /// Knowledge portal.
    let knowledgePortal (chrome: ChromeSpec) (spec: KnowledgePortalSpec) : XmlNode =
        LayoutShell.page chrome "bk-layout-knowledge" [
            yield
                tag "header" [ _class "bk-knowledge-header" ] [
                    yield h1 [ _class "bk-knowledge-title" ] [ str spec.Title ]
                    yield! Slot.text (fun i -> p [ _class "bk-knowledge-intro" ] [ str i ]) spec.Intro
                ]
            yield! Slot.wrap (fun s -> div [ _class "bk-knowledge-search" ] [ s ]) spec.Search
            yield
                div [ _class "bk-knowledge-columns" ] [
                    yield
                        div [ _class "bk-knowledge-main" ] [
                            yield! Slot.wrap (fun a -> section [ _class "bk-knowledge-answer" ] [ a ]) spec.Answer
                            yield! Slot.wrap (fun b -> section [ _class "bk-knowledge-browse" ] [ b ]) spec.Browse
                        ]
                    yield! Slot.wrap (fun s -> aside [ _class "bk-knowledge-sidebar" ] [ s ]) spec.Sidebar
                ]
        ]