namespace ToolUp.BrandKit.Layouts

open Giraffe.ViewEngine

// ─── Phase 92 — BrandKit layout library: shared page shell ──────────
//
// `ChromeSpec` carries the page-level chrome every layout shares
// (document language, head metadata, stylesheets, header / footer
// nodes); `LayoutShell.page` scaffolds the `<html>` document around a
// layout's main content with the accessibility baseline applied:
//
//   - a skip link (`.bk-skip-link`, first focusable element) targeting
//     `#bk-main`
//   - semantic landmarks — consumer-supplied `<header>` / `<footer>`
//     (typically `PageChrome.pageHeader` / `pageFooter`) around a
//     single `<main id="bk-main" tabindex="-1">`
//   - focus order = DOM order; the `tabindex="-1"` on `<main>` makes
//     the skip-link target programmatically focusable in every browser
//
// Like every BrandKit primitive the shell ships ZERO opinionated
// styling — class hooks only, themed by the consumer's `--bk-*` CSS
// custom properties. The documented contract (hooks, responsive grid,
// reference stylesheet) lives in `docs/platform/layouts.md`.

/// Page-level chrome shared by every layout in the library.
///   `Lang`          — `<html lang>` value (default `"en"`)
///   `Title`         — document `<title>`
///   `Description`   — optional `<meta name="description">`
///   `Stylesheets`   — hrefs linked in order; the consumer's brand CSS
///                     (the `--bk-*` variable declarations) goes here
///   `HeadExtra`     — extra head nodes (canonical link, JSON-LD,
///                     OpenGraph tags, favicons, …)
///   `Header`        — optional page header landmark (typically
///                     `PageChrome.pageHeader`)
///   `Footer`        — optional page footer landmark (typically
///                     `PageChrome.pageFooter`)
///   `SkipLinkLabel` — skip-link text (localise per deployment)
///   `BodyClass`     — optional extra class on `<body>` (theme switch
///                     hook, e.g. a per-brand `theme-midnight`)
type ChromeSpec = {
    Lang: string
    Title: string
    Description: string option
    Stylesheets: string list
    HeadExtra: XmlNode list
    Header: XmlNode option
    Footer: XmlNode option
    SkipLinkLabel: string
    BodyClass: string option
}

[<RequireQualifiedAccess>]
module Chrome =
    /// Minimal chrome: English, titled, no description / stylesheets /
    /// header / footer. Override fields per deployment.
    let create (title: string) : ChromeSpec = {
        Lang = "en"
        Title = title
        Description = None
        Stylesheets = []
        HeadExtra = []
        Header = None
        Footer = None
        SkipLinkLabel = "Skip to content"
        BodyClass = None
    }

[<RequireQualifiedAccess>]
module LayoutShell =
    /// The id every layout's `<main>` carries; the skip link targets it.
    [<Literal>]
    let MainId = "bk-main"

    /// Scaffold a full `<html>` document around a layout's main
    /// content. `layoutClass` lands on `<main>` alongside `bk-main` so
    /// per-layout CSS scopes cleanly (`.bk-main.bk-layout-article …`).
    /// The node renders via Giraffe's `RenderView.AsString.htmlDocument`
    /// (which prepends the doctype) — don't emit a doctype here.
    let page (chrome: ChromeSpec) (layoutClass: string) (mainContent: XmlNode list) : XmlNode =
        let bodyClass =
            match chrome.BodyClass with
            | Some extra -> "bk-page " + extra
            | None -> "bk-page"

        html [ _lang chrome.Lang ] [
            head [] [
                yield meta [ _charset "utf-8" ]
                yield meta [ _name "viewport"; _content "width=device-width, initial-scale=1" ]
                yield title [] [ str chrome.Title ]
                yield!
                    (chrome.Description
                     |> Option.map (fun d -> meta [ _name "description"; _content d ])
                     |> Option.toList)
                yield!
                    (chrome.Stylesheets
                     |> List.map (fun href -> link [ _rel "stylesheet"; _href href ]))
                yield! chrome.HeadExtra
            ]
            body [ _class bodyClass ] [
                yield a [ _class "bk-skip-link"; _href ("#" + MainId) ] [ str chrome.SkipLinkLabel ]
                yield! Option.toList chrome.Header
                yield main [ _id MainId; _class ("bk-main " + layoutClass); attr "tabindex" "-1" ] mainContent
                yield! Option.toList chrome.Footer
            ]
        ]