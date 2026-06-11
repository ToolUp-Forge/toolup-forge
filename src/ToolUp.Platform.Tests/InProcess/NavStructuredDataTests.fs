// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NavStructuredDataTests

open Expecto
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.PublicRendering

// ─── Phase 96 — breadcrumb + site-nav structured data (JSON-LD) ─────
//
// NavLayout.headStructuredData derives BreadcrumbList (from the page's
// nested slug) + SiteNavigationElement (from the audience-filtered nav
// tree) JSON-LD, absolutised against the base URL. Coverage: shape,
// audience consistency (gated items omitted), absolute URLs, empty input.

let private anonCtx = AccessContext.unrestricted (AnonymousSession "s")
let private userCtx = AccessContext.unrestricted (AuthenticatedUser "u")

let private nav = [
    NavTree.leaf "Home" "home"
    NavTree.node "Services" (NavSection "services") [ NavTree.leaf "Consulting" "services/consulting" ]
    NavTree.leaf "Members" "members" |> NavTree.withAudience NavAuthenticated
]

let private render (nodes: XmlNode list) =
    nodes |> List.map RenderView.AsString.htmlNode |> String.concat "\n"

let tests =
    testList "Phase 96 nav structured data" [

        test "headStructuredData emits BreadcrumbList + SiteNavigationElement with absolute URLs" {
            let html =
                NavLayout.headStructuredData "https://example.com" anonCtx (Some(Slug "services/consulting")) nav
                |> render

            Expect.stringContains html "\"BreadcrumbList\"" "breadcrumb JSON-LD present"
            Expect.stringContains html "\"SiteNavigationElement\"" "site-nav JSON-LD present"
            Expect.stringContains html "https://example.com/services/consulting" "breadcrumb uses absolute URLs"
            Expect.stringContains html "application/ld+json" "emitted as ld+json script blocks"
        }

        test "site-nav JSON-LD is audience-filtered — a gated item is absent for an anonymous viewer" {
            let anonHtml =
                NavLayout.headStructuredData "https://example.com" anonCtx None nav |> render

            let userHtml =
                NavLayout.headStructuredData "https://example.com" userCtx None nav |> render

            Expect.isFalse (anonHtml.Contains "Members") "gated nav item omitted from anon structured data"
            Expect.stringContains userHtml "Members" "gated nav item present for an authenticated viewer"
            Expect.stringContains anonHtml "Home" "public items still present"
        }

        test "empty nav + no current slug emits nothing (yield!-safe)" {
            let nodes = NavLayout.headStructuredData "https://example.com" anonCtx None []
            Expect.isEmpty nodes "no JSON-LD when there's nothing to describe"
        }

        test "siteNavigation builder emits an ItemList of SiteNavigationElement" {
            let json =
                StructuredDataHelpers.siteNavigation [ "Home", "https://x/"; "About", "https://x/about" ]

            Expect.stringContains json "\"ItemList\"" "wraps in an ItemList"
            Expect.stringContains json "\"SiteNavigationElement\"" "items are nav elements"
            Expect.stringContains json "\"position\":1" "positions are 1-based"
        }
    ]