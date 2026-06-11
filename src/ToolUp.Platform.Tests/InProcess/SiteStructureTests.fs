// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SiteStructureTests

open System
open Expecto
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Phase 90 — site structure: nav tree + taxonomy ─────────────────
//
// Pure-data model + pure render helpers, exercised directly. Coverage:
// nav-tree audience filtering (GP 4), breadcrumb derivation (from a
// nested slug and from the tree), auto-nav from a collection, nav.yaml
// parsing, tag-index content-source resolution, related-by-tag ranking,
// faceted tag counts, and the frontmatter-derived tag helpers.

let private anonCtx = AccessContext.unrestricted (AnonymousSession "s1")
let private teamCtx = AccessContext.unrestricted (TeamMember("u1", "t1"))
let private userCtx = AccessContext.unrestricted (AuthenticatedUser "u1")

let private mkPage (slug: string) (title: string) (tagCsv: string) : PublicPage = {
    Slug = Slug slug
    Title = title
    Description = ""
    Body = Markdown ""
    Layout = LayoutName "page"
    Frontmatter =
        (if tagCsv = "" then
             Map.empty
         else
             Map.ofList [ "tags", tagCsv ])
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

let private navYaml =
    "- label: Home\n\
     \x20 slug: home\n\
     - label: Services\n\
     \x20 slug: services\n\
     \x20 children:\n\
     \x20\x20\x20 - label: Consulting\n\
     \x20\x20\x20\x20\x20 slug: services/consulting\n\
     \x20\x20\x20 - label: Partners\n\
     \x20\x20\x20\x20\x20 url: https://example.com\n\
     \x20\x20\x20\x20\x20 audience: authenticated"

let tests =
    testList "Phase 90 site structure" [

        // ─── Nav-tree audience filtering (GP 4) ──────────────────────
        test "filter hides a team-only node (and subtree) from non-team viewers" {
            let tree = [
                NavTree.leaf "Home" "home"
                NavTree.node "Team" (NavSection "team") [ NavTree.leaf "Roster" "team/roster" ]
                |> NavTree.withAudience NavTeamOnly
            ]

            let anonNodes = NavTree.filter anonCtx tree
            let teamNodes = NavTree.filter teamCtx tree
            Expect.equal (List.length anonNodes) 1 "anonymous viewer sees only the public node"
            Expect.equal (List.length teamNodes) 2 "team member sees the gated node too"
            Expect.equal (List.head anonNodes).Label "Home" "the surviving anon node is the public one"
        }

        test "filter recurses — a gated child is dropped but its public parent stays" {
            let tree = [
                NavTree.node "Docs" (NavSection "docs") [
                    NavTree.leaf "Intro" "docs/intro"
                    NavTree.leaf "Admin" "docs/admin" |> NavTree.withAudience NavAuthenticated
                ]
            ]

            let filtered = NavTree.filter anonCtx tree
            let parent = List.head filtered
            Expect.equal (List.length parent.Children) 1 "only the public child survives"
            Expect.equal (List.head parent.Children).Label "Intro" "the surviving child is the public one"
        }

        // ─── Breadcrumbs ─────────────────────────────────────────────
        test "breadcrumbFromSlug derives accumulated-path crumbs from a nested slug" {
            let crumbs = NavTree.breadcrumbFromSlug (Slug "services/consulting/uk")

            Expect.equal
                crumbs
                [
                    "services", "/services"
                    "consulting", "/services/consulting"
                    "uk", "/services/consulting/uk"
                ]
                "each segment accumulates the path"
        }

        test "findTrail returns the root-first node path to a targeted slug" {
            let tree = [
                NavTree.node "Services" (NavSection "services") [ NavTree.leaf "Consulting" "services/consulting" ]
            ]

            let trail = NavTree.findTrail (Slug "services/consulting") tree
            let labels = trail |> List.map (fun n -> n.Label)
            Expect.equal labels [ "Services"; "Consulting" ] "trail is Services → Consulting"
        }

        // ─── Auto-nav from a collection ──────────────────────────────
        test "ofCollection builds a flat leaf menu labelled by page title" {
            let pages = [ mkPage "docs/a" "Chapter A" ""; mkPage "docs/b" "Chapter B" "" ]
            let menu = NavTree.ofCollection pages
            let n0 = List.head menu
            Expect.equal (List.length menu) 2 "one node per page"
            Expect.equal n0.Label "Chapter A" "labelled by title"
            Expect.equal n0.Target (NavSlug(Slug "docs/a")) "targets the page slug"
        }

        // ─── nav.yaml loader ─────────────────────────────────────────
        test "parseYaml parses nested items, targets, and audience" {
            let tree = NavTree.parseYaml navYaml
            Expect.equal (List.length tree) 2 "two top-level nodes"
            let services = tree[1]
            Expect.equal services.Label "Services" "second node label"
            Expect.equal (List.length services.Children) 2 "Services has two children"
            let consulting = services.Children[0]
            let partners = services.Children[1]
            Expect.equal consulting.Target (NavSlug(Slug "services/consulting")) "child slug target"
            Expect.equal partners.Target (NavUrl "https://example.com") "child url target"
            Expect.equal partners.Audience NavAuthenticated "child audience parsed"
        }

        // ─── Tag helpers (frontmatter-derived) ───────────────────────
        test "PublicPage.tags / hasTag parse the comma-separated frontmatter key" {
            let p = mkPage "p" "P" "News, Product , launch"
            Expect.equal (PublicPage.tags p) [ "News"; "Product"; "launch" ] "trimmed, empties dropped"
            Expect.isTrue (PublicPage.hasTag "product" p) "hasTag is case-insensitive"
            Expect.isFalse (PublicPage.hasTag "missing" p) "absent tag is false"
            Expect.equal (PublicPage.tags (mkPage "q" "Q" "")) [] "no frontmatter key → no tags"
        }

        // ─── Tag-index content source ────────────────────────────────
        test "tagIndexSource serves /tag/{slug} listing same-tagged pages" {
            let pages = [
                mkPage "post-a" "Post A" "news,product"
                mkPage "post-b" "Post B" "news"
                mkPage "post-c" "Post C" "other"
            ]

            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return pages })
            let resolved = source.Resolve (Slug "tag/news") anonCtx |> Async.RunSynchronously

            match resolved with
            | Some(Narrative doc) ->
                let html = NarrativeHtml.render doc
                Expect.stringContains html "Post A" "tagged page A listed"
                Expect.stringContains html "Post B" "tagged page B listed"
                Expect.isFalse (html.Contains "Post C") "untagged page C not listed"
                Expect.stringContains html "/post-a" "links to the page slug"
            | other -> failtestf "expected a Narrative tag-index body, got %A" other
        }

        test "tagIndexSource falls through (None) for a non-tag route" {
            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return [] })
            let resolved = source.Resolve (Slug "about") anonCtx |> Async.RunSynchronously
            Expect.isNone resolved "a slug outside tag/{slug} is not claimed"
        }

        test "tagIndexSource degrades to an empty-state callout for an unknown tag" {
            let source =
                TaxonomyHandler.tagIndexSource (fun () -> async { return [ mkPage "p" "P" "news" ] })

            let resolved = source.Resolve (Slug "tag/ghost") anonCtx |> Async.RunSynchronously

            match resolved with
            | Some(Narrative doc) ->
                let html = NarrativeHtml.render doc
                Expect.stringContains html "No pages are tagged" "empty-state message"
            | other -> failtestf "expected an empty-state Narrative, got %A" other
        }

        // ─── Related content + facets ────────────────────────────────
        test "relatedByTag ranks by shared-tag count and excludes self" {
            let pages = [
                mkPage "self" "Self" "a,b,c"
                mkPage "two" "Two" "a,b"
                mkPage "one" "One" "a"
                mkPage "none" "None" "x"
            ]

            let self = List.head pages
            let related = TaxonomyHandler.relatedByTag pages self
            let titles = related |> List.map (fun p -> p.Title)
            Expect.equal titles [ "Two"; "One" ] "ranked by shared count desc; self + unrelated excluded"
        }

        test "tagCounts produces case-insensitive facet counts, count-desc then tag-asc" {
            let pages = [
                mkPage "a" "A" "News,Product"
                mkPage "b" "B" "news"
                mkPage "c" "C" "product"
            ]

            let counts = TaxonomyHandler.tagCounts pages
            Expect.equal counts [ "news", 2; "product", 2 ] "lower-cased, count-desc then tag-asc"
        }

        // ─── Render helpers ──────────────────────────────────────────
        test "NavLayout.menu emits current highlight, dropdown markup, and class hooks" {
            let tree = [
                NavTree.leaf "Home" "home"
                NavTree.node "Services" (NavSection "services") [ NavTree.leaf "Consulting" "services/consulting" ]
            ]

            let html = RenderView.AsString.htmlNode (NavLayout.menu (Some(Slug "home")) tree)
            Expect.stringContains html "tu-nav__list" "menu list class hook"
            Expect.stringContains html "tu-nav__item--current" "current-page highlight class"
            Expect.stringContains html "aria-current=\"page\"" "current-page aria"
            Expect.stringContains html "tu-nav__submenu" "multi-level dropdown markup"
            Expect.stringContains html "href=\"/services/consulting\"" "child link href"
        }

        test "NavLayout.menuFor filters by audience before rendering" {
            let tree = [
                NavTree.leaf "Home" "home"
                NavTree.leaf "Members" "members" |> NavTree.withAudience NavAuthenticated
            ]

            let anonHtml = RenderView.AsString.htmlNode (NavLayout.menuFor anonCtx None tree)
            let userHtml = RenderView.AsString.htmlNode (NavLayout.menuFor userCtx None tree)
            Expect.isFalse (anonHtml.Contains "Members") "gated item absent for anonymous viewer"
            Expect.stringContains userHtml "Members" "gated item present for authenticated viewer"
        }

        test "NavLayout.breadcrumb marks the last crumb current and links the rest" {
            let crumbs = NavTree.breadcrumbFromSlug (Slug "services/consulting")
            let html = RenderView.AsString.htmlNode (NavLayout.breadcrumb crumbs)
            Expect.stringContains html "tu-breadcrumb__list" "breadcrumb list class hook"
            Expect.stringContains html "href=\"/services\"" "ancestor crumb is a link"
            Expect.stringContains html "tu-breadcrumb__item--current" "last crumb marked current"
        }
    ]