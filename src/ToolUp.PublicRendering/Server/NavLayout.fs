// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 90 — navigation render helpers.
///
/// Giraffe.ViewEngine fragments a layout composes: a multi-level menu
/// (`menu` / `menuFor`) and a breadcrumb trail (`breadcrumb`). Every
/// element carries a stable BrandKit-style class hook (`tu-nav__*` /
/// `tu-breadcrumb__*`) so a deployment styles the chrome with its own
/// CSS / CSS-variables without the SDK shipping opinionated styling. The
/// renderers are pure (`NavNode list -> XmlNode`) and deterministic — two
/// renders of the same tree produce identical markup (prerender-safe).
namespace ToolUp.PublicRendering

open Giraffe.ViewEngine
open ToolUp.Platform

module NavLayout =

    let private space (parts: string list) =
        parts |> List.filter (fun s -> s <> "") |> String.concat " "

    let rec private renderNode (current: Slug option) (node: NavNode) : XmlNode =
        let href = NavTree.targetHref node.Target

        let isCurrent =
            match current, node.Target with
            | Some(Slug c), NavSlug(Slug s) -> c = s
            | _ -> false

        let hasChildren = not (List.isEmpty node.Children)

        let liClass =
            space [
                "tu-nav__item"
                if hasChildren then "tu-nav__item--has-children" else ""
                if isCurrent then "tu-nav__item--current" else ""
            ]

        let linkAttrs = [
            _href href
            _class "tu-nav__link"
            if isCurrent then
                KeyValue("aria-current", "page")
        ]

        li [ _class liClass ] [
            a linkAttrs [ encodedText node.Label ]
            if hasChildren then
                ul [ _class "tu-nav__submenu" ] (node.Children |> List.map (renderNode current))
        ]

    /// Render a nav tree as a `<nav><ul>` menu with multi-level dropdown
    /// markup, current-page highlighting (`tu-nav__item--current` +
    /// `aria-current="page"`), and BrandKit class hooks. Pass the
    /// already-audience-filtered tree (see `menuFor` to filter inline).
    /// `current` is the slug of the page being rendered, for highlighting.
    let menu (current: Slug option) (nodes: NavNode list) : XmlNode =
        nav [ _class "tu-nav"; KeyValue("aria-label", "Primary") ] [
            ul [ _class "tu-nav__list" ] (nodes |> List.map (renderNode current))
        ]

    /// `menu`, filtering the tree to the viewer's audience first (GP 4) —
    /// a gated node (and its subtree) is absent from the rendered markup
    /// for a viewer who can't see it. The common one-call shape for a
    /// layout that has the request `AccessContext` in hand.
    let menuFor (ctx: AccessContext) (current: Slug option) (nodes: NavNode list) : XmlNode =
        menu current (NavTree.filter ctx nodes)

    /// Render a breadcrumb trail (a `(label, href)` list — typically from
    /// `NavTree.breadcrumbFromSlug`) as an ordered `<nav><ol>` with the
    /// last crumb marked current and rendered as plain text (no link).
    /// Empty input renders an empty `<nav>` so a layout can splice it
    /// unconditionally.
    let breadcrumb (crumbs: (string * string) list) : XmlNode =
        let lastIndex = List.length crumbs - 1

        let item i (label: string, href: string) =
            if i = lastIndex then
                li [
                    _class "tu-breadcrumb__item tu-breadcrumb__item--current"
                    KeyValue("aria-current", "page")
                ] [ encodedText label ]
            else
                li [ _class "tu-breadcrumb__item" ] [
                    a [ _href href; _class "tu-breadcrumb__link" ] [ encodedText label ]
                ]

        nav [ _class "tu-breadcrumb"; KeyValue("aria-label", "Breadcrumb") ] [
            ol [ _class "tu-breadcrumb__list" ] (crumbs |> List.mapi item)
        ]

    // ─── Phase 98 — pagination control ───────────────────────────────

    /// Render a pager (`Previous` / page-number / `Next`) for a
    /// `PageSlice`. `pageHref n` builds the URL for page `n` — the caller
    /// chooses the scheme (path `/tag/news/2`, query `?page=2`, …). The
    /// current page renders as plain text; disabled prev/next render as
    /// non-link spans. A single-page slice renders an empty `<nav>` so a
    /// layout can splice it unconditionally. `tu-pager__*` class hooks.
    let pager (pageHref: int -> string) (slice: PageSlice<'a>) : XmlNode =
        if slice.PageCount <= 1 then
            nav [ _class "tu-pager" ] []
        else
            let prev =
                if slice.HasPrev then
                    a [ _href (pageHref (slice.Page - 1)); _class "tu-pager__prev" ] [ encodedText "Previous" ]
                else
                    span [ _class "tu-pager__prev tu-pager__prev--disabled" ] [ encodedText "Previous" ]

            let next =
                if slice.HasNext then
                    a [ _href (pageHref (slice.Page + 1)); _class "tu-pager__next" ] [ encodedText "Next" ]
                else
                    span [ _class "tu-pager__next tu-pager__next--disabled" ] [ encodedText "Next" ]

            let pageItem n =
                if n = slice.Page then
                    li [
                        _class "tu-pager__page tu-pager__page--current"
                        KeyValue("aria-current", "page")
                    ] [ encodedText (string n) ]
                else
                    li [ _class "tu-pager__page" ] [
                        a [ _href (pageHref n); _class "tu-pager__link" ] [ encodedText (string n) ]
                    ]

            nav [ _class "tu-pager"; KeyValue("aria-label", "Pagination") ] [
                prev
                ol [ _class "tu-pager__pages" ] [ for n in 1 .. slice.PageCount -> pageItem n ]
                next
            ]

    // ─── Phase 96 — structured data (JSON-LD) ────────────────────────

    /// Make an internal href absolute against `baseUrl`; external
    /// (absolute) URLs pass through verbatim.
    let private absolutise (baseUrl: string) (href: string) : string =
        if href.StartsWith "http://" || href.StartsWith "https://" then
            href
        else
            baseUrl.TrimEnd('/') + href

    /// Flatten a nav tree into a depth-first `(label, href)` list.
    let rec private flattenNav (nodes: NavNode list) : (string * string) list =
        nodes
        |> List.collect (fun n -> (n.Label, NavTree.targetHref n.Target) :: flattenNav n.Children)

    /// Build the `<head>` JSON-LD blocks for a page from the Phase 90
    /// nav / breadcrumb model: a `BreadcrumbList` derived from the
    /// current page's nested slug (when `currentSlug` is `Some`), and a
    /// `SiteNavigationElement` `ItemList` from the AUDIENCE-FILTERED nav
    /// tree — so the structured data omits the same gated items the
    /// rendered menu omits (GP 4, no leak). URLs are absolutised against
    /// `baseUrl`. Returns the `<script type="application/ld+json">` nodes
    /// a layout splices into `<head>`; `[]` when there's nothing to emit,
    /// so a layout can `yield!` it unconditionally.
    let headStructuredData
        (baseUrl: string)
        (ctx: AccessContext)
        (currentSlug: Slug option)
        (nav: NavNode list)
        : XmlNode list =
        let ld (json: string) =
            script [ _type "application/ld+json" ] [ rawText json ]

        let breadcrumbLd =
            match currentSlug with
            | Some slug ->
                let crumbs =
                    NavTree.breadcrumbFromSlug slug
                    |> List.map (fun (label, href) -> label, absolutise baseUrl href)

                if List.isEmpty crumbs then
                    []
                else
                    [ ld (StructuredDataHelpers.breadcrumb crumbs) ]
            | None -> []

        let navLd =
            let items =
                NavTree.filter ctx nav
                |> flattenNav
                |> List.map (fun (label, href) -> label, absolutise baseUrl href)

            if List.isEmpty items then
                []
            else
                [ ld (StructuredDataHelpers.siteNavigation items) ]

        breadcrumbLd @ navLd