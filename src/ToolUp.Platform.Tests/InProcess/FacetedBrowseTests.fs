// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FacetedBrowseTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Phase 99 — faceted multi-tag browse ────────────────────────────

let private anonCtx = AccessContext.unrestricted (AnonymousSession "s")

let private mkPage (slug: string) (tags: string) (status: PublishStatus) : PublicPage = {
    Slug = Slug slug
    Title = slug
    Description = ""
    Body = Markdown ""
    Layout = LayoutName "p"
    Frontmatter = (if tags = "" then Map.empty else Map.ofList [ "tags", tags ])
    PublishedAt = None
    Collection = None
    Status = status
    Audience = PageAudience.Public
}

let private pages = [
    mkPage "a" "news,product" Published
    mkPage "b" "news" Published
    mkPage "c" "product" Published
    mkPage "d" "events" Published
]

let tests =
    testList "Phase 99 faceted browse" [

        test "facetedBrowse with matchAll requires every tag (AND)" {
            let results, _ = TaxonomyHandler.facetedBrowse true [ "news"; "product" ] pages
            Expect.equal (results |> List.map (fun p -> p.Title)) [ "a" ] "only the page with both tags"
        }

        test "facetedBrowse with matchAll=false requires any tag (OR)" {
            let results, _ = TaxonomyHandler.facetedBrowse false [ "news"; "events" ] pages
            Expect.equal (results |> List.map (fun p -> p.Title) |> List.sort) [ "a"; "b"; "d" ] "any matching tag"
        }

        test "facet counts narrow over the filtered set" {
            let _, facets = TaxonomyHandler.facetedBrowse true [ "news" ] pages
            // Among news pages (a, b): news=2, product=1.
            Expect.equal facets [ "news", 2; "product", 1 ] "facets reflect the filtered set"
        }

        test "empty tag list returns every page" {
            let results, _ = TaxonomyHandler.facetedBrowse true [] pages
            Expect.equal (List.length results) 4 "no filter → all pages"
        }

        test "facetedBrowseSource serves browse/{tags} with AND semantics" {
            let source = TaxonomyHandler.facetedBrowseSource (fun () -> async { return pages })

            let resolved =
                source.Resolve (Slug "browse/news+product") anonCtx |> Async.RunSynchronously

            match resolved with
            | Some(Narrative doc) ->
                let html = NarrativeHtml.render doc
                Expect.stringContains html "/a" "the matching page is listed"
                Expect.isFalse (html.Contains ">b<") "a single-tag page is excluded by AND"
            | other -> failtestf "expected a Narrative browse body, got %A" other
        }

        test "facetedBrowseSource falls through for a non-browse route" {
            let source = TaxonomyHandler.facetedBrowseSource (fun () -> async { return [] })
            let resolved = source.Resolve (Slug "about") anonCtx |> Async.RunSynchronously
            Expect.isNone resolved "a slug outside browse/{tags} is not claimed"
        }

        test "an impossible combination degrades to an empty-state body" {
            let source = TaxonomyHandler.facetedBrowseSource (fun () -> async { return pages })

            let resolved =
                source.Resolve (Slug "browse/news+events") anonCtx |> Async.RunSynchronously

            match resolved with
            | Some(Narrative doc) ->
                Expect.stringContains (NarrativeHtml.render doc) "No pages match" "graceful empty state"
            | other -> failtestf "expected a Narrative body, got %A" other
        }

        test "facetedBrowse scopes out non-public pages via the source" {
            let mixed = [ mkPage "pub" "news" Published; mkPage "draft" "news" Draft ]
            let source = TaxonomyHandler.facetedBrowseSource (fun () -> async { return mixed })
            let resolved = source.Resolve (Slug "browse/news") anonCtx |> Async.RunSynchronously

            match resolved with
            | Some(Narrative doc) ->
                let html = NarrativeHtml.render doc
                Expect.stringContains html "/pub" "public page listed"
                Expect.isFalse (html.Contains "/draft") "draft (non-public) page excluded"
            | other -> failtestf "expected a Narrative body, got %A" other
        }
    ]