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