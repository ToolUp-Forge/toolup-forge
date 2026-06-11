// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.EnumerableRoutesTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.PublicRendering

// ─── Phase 95 — declared enumerable routes for IContentSource ───────
//
// A source MAY also implement IEnumerableContentSource to advertise the
// concrete slugs it produces, so sitemap.xml / static export / prerender
// discover its dynamic pages. Coverage: TaxonomyHandler enumerates tag
// routes, ContentSource.enumerateAll collects + dedupes + skips
// non-enumerable sources, and SitemapGenerator.generateWith emits the
// dynamic routes (deduped against file/overlay pages).

let private mkPage (slug: string) (tagCsv: string) : PublicPage = {
    Slug = Slug slug
    Title = slug
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

let private pages = [
    mkPage "post-a" "news,product"
    mkPage "post-b" "news"
    mkPage "post-c" "events"
]

let tests =
    testList "Phase 95 enumerable routes" [

        test "tagIndexSource implements IEnumerableContentSource and enumerates one route per distinct tag" {
            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return pages })

            match source with
            | :? IEnumerableContentSource as e ->
                let routes = e.EnumerateRoutes() |> Async.RunSynchronously
                let slugs = routes |> List.map Slug.value |> List.sort

                Expect.equal
                    slugs
                    [ "tag/events"; "tag/news"; "tag/product" ]
                    "one /tag/{x} per distinct tag, lower-cased"
            | _ -> failtest "tagIndexSource should also implement IEnumerableContentSource"
        }

        test "enumerateAll collects across sources, dedupes, and skips non-enumerable sources" {
            let tagSource = TaxonomyHandler.tagIndexSource (fun () -> async { return pages })
            // A plain ofRoute source is NOT enumerable — contributes nothing.
            let plain = ContentSource.ofRoute "x/{y}" (fun _ _ -> async { return None })

            let routes =
                ContentSource.enumerateAll [ tagSource; plain ] |> Async.RunSynchronously

            let slugs = routes |> List.map Slug.value |> List.sort

            Expect.equal
                slugs
                [ "tag/events"; "tag/news"; "tag/product" ]
                "tag routes only; plain source contributes nothing"
        }

        test "a plain ofRoute source is not enumerable" {
            let plain = ContentSource.ofRoute "x/{y}" (fun _ _ -> async { return None })
            Expect.isFalse (plain :? IEnumerableContentSource) "ofRoute does not implement the enumerable capability"
            let routes = ContentSource.enumerateAll [ plain ] |> Async.RunSynchronously
            Expect.isEmpty routes "no routes from a non-enumerable source"
        }

        test "SitemapGenerator.generateWith emits dynamic routes, deduped against page slugs" {
            let dynamic = [ Slug "tag/news"; Slug "tag/events"; Slug "post-a" ]

            let xml =
                SitemapGenerator.generateWith "https://example.com" [ mkPage "post-a" "news" ] dynamic

            Expect.stringContains xml "<loc>https://example.com/tag/news</loc>" "dynamic tag route emitted"
            Expect.stringContains xml "<loc>https://example.com/tag/events</loc>" "second dynamic route emitted"
            // post-a appears once (it's a real page); the dynamic dup is skipped.
            let occurrences = (xml.Split([| "/post-a<" |], StringSplitOptions.None)).Length - 1
            Expect.equal occurrences 1 "page slug not duplicated by a dynamic route"
        }

        test "generate (no dynamic routes) is unchanged" {
            let xml = SitemapGenerator.generate "https://example.com" [ mkPage "about" "" ]
            Expect.stringContains xml "<loc>https://example.com/about</loc>" "page emitted"
            Expect.isFalse (xml.Contains "tag/") "no dynamic routes without enumeration"
        }
    ]