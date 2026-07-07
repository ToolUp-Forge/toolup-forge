// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PaginationTests

open Expecto
open Giraffe.ViewEngine
open ToolUp.PublicRendering

// ─── Phase 98 — listing pagination ──────────────────────────────────

let private mkPage (slug: string) : PublicPage = {
    Slug = Slug slug
    Title = slug
    Description = ""
    Body = Markdown ""
    Layout = LayoutName "p"
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

let tests =
    testList "Phase 98 pagination" [

        test "paginate slices a page and reports the page-count contract" {
            let s = Pagination.paginate 2 1 [ 1; 2; 3; 4; 5 ]
            Expect.equal s.Items [ 1; 2 ] "first two items"
            Expect.equal s.Page 1 "page 1"
            Expect.equal s.PageCount 3 "5 items / 2 = 3 pages"
            Expect.isFalse s.HasPrev "no previous on page 1"
            Expect.isTrue s.HasNext "has next"
        }

        test "paginate clamps an out-of-range page to the last page" {
            let s = Pagination.paginate 2 99 [ 1; 2; 3; 4; 5 ]
            Expect.equal s.Page 3 "clamped to last page"
            Expect.equal s.Items [ 5 ] "final page has the remainder"
            Expect.isFalse s.HasNext "no next on the last page"
        }

        test "pageSize <= 0 returns the whole list as a single page (unchanged)" {
            let s = Pagination.paginate 0 1 [ 1; 2; 3 ]
            Expect.equal s.Items [ 1; 2; 3 ] "whole list"
            Expect.equal s.PageCount 1 "one page"
        }

        test "an empty list is page 1 of 1 with no items" {
            let s = Pagination.paginate 10 1 ([]: int list)
            Expect.isEmpty s.Items "no items"
            Expect.equal s.PageCount 1 "one (empty) page"
        }

        test "ofCollectionPaged slices the collection into nav nodes" {
            let pages = [ mkPage "a"; mkPage "b"; mkPage "c" ]
            let s = NavTree.ofCollectionPaged 2 2 pages
            Expect.equal (s.Items |> List.map _.Label) [ "c" ] "page 2 has the remaining node"
            Expect.equal s.PageCount 2 "two pages"
        }

        test "pager renders prev/next + numbered links with current-page highlight" {
            let slice = Pagination.paginate 1 2 [ 1; 2; 3 ]

            let html =
                RenderView.AsString.htmlNode (NavLayout.pager (sprintf "/list?page=%d") slice)

            Expect.stringContains html "tu-pager__pages" "page list class hook"
            Expect.stringContains html "tu-pager__page--current" "current page highlighted"
            Expect.stringContains html "/list?page=1" "previous link present"
            Expect.stringContains html "/list?page=3" "next link present"
        }

        test "pager for a single-page slice renders an empty nav" {
            let html =
                RenderView.AsString.htmlNode (NavLayout.pager (sprintf "/p/%d") (Pagination.paginate 10 1 [ 1 ]))

            Expect.isFalse (html.Contains "tu-pager__pages") "no page list when there's only one page"
        }
    ]