// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TagFeedTests

open System
open Expecto
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose

// ─── Phase 97 — per-tag Atom feeds ──────────────────────────────────
//
// The taxonomy-axis extension of the Phase 80b feed surface. Coverage:
// the pure selectEntries filter (tag / collection / back-compat / cap +
// sort) and the withTagFeed / withTagFeeds compose helpers.

let private mkPage (slug: string) (tags: string) (collection: string option) (publishedDay: int option) : PublicPage = {
    Slug = Slug slug
    Title = slug
    Description = ""
    Body = Narrative(Narrative.create slug) // Title = slug, so we can identify it
    Layout = LayoutName "p"
    Frontmatter = (if tags = "" then Map.empty else Map.ofList [ "tags", tags ])
    PublishedAt =
        publishedDay
        |> Option.map (fun d -> DateTimeOffset(DateTime(2026, 6, d), TimeSpan.Zero))
    Collection = collection
    Status = Published
    Audience = PageAudience.Public
}

let private cfg = NarrativeFeedConfig.defaults

let tests =
    testList "Phase 97 per-tag Atom feeds" [

        test "selectEntries with a Tag filters to pages carrying that tag" {
            let pages = [
                mkPage "a" "news,product" None (Some 1)
                mkPage "b" "events" None (Some 2)
                mkPage "c" "news" None (Some 3)
            ]

            let titles =
                NarrativeFeedHandler.selectEntries { cfg with Tag = Some "news" } pages
                |> List.map (fun d -> d.Title)

            Expect.equal (List.sort titles) [ "a"; "c" ] "only news-tagged pages, newest-first"
        }

        test "selectEntries with no Tag is unchanged (back-compat)" {
            let pages = [ mkPage "a" "news" None (Some 1); mkPage "b" "" None (Some 2) ]

            let titles =
                NarrativeFeedHandler.selectEntries cfg pages |> List.map (fun d -> d.Title)

            Expect.equal (List.sort titles) [ "a"; "b" ] "all Narrative pages when no tag filter"
        }

        test "Tag and Collection filters both apply (AND)" {
            let pages = [
                mkPage "a" "news" (Some "blog") (Some 1)
                mkPage "b" "news" (Some "press") (Some 2)
            ]

            let titles =
                NarrativeFeedHandler.selectEntries
                    {
                        cfg with
                            Tag = Some "news"
                            Collection = Some "blog"
                    }
                    pages
                |> List.map (fun d -> d.Title)

            Expect.equal titles [ "a" ] "only the news page in the blog collection"
        }

        test "selectEntries sorts newest-first and caps at MaxEntries" {
            let pages = [
                mkPage "old" "news" None (Some 1)
                mkPage "new" "news" None (Some 9)
                mkPage "mid" "news" None (Some 5)
            ]

            let titles =
                NarrativeFeedHandler.selectEntries
                    {
                        cfg with
                            Tag = Some "news"
                            MaxEntries = 2
                    }
                    pages
                |> List.map (fun d -> d.Title)

            Expect.equal titles [ "new"; "mid" ] "newest two, descending"
        }

        test "withTagFeed registers a feed with the tag set" {
            let app =
                PublicRenderingServerApp.create ()
                |> PublicRenderingServerApp.withTagFeed "news" {
                    cfg with
                        SelfUrl = "/tag/news/feed.atom"
                }

            Expect.equal (List.length app.Feeds) 1 "one feed registered"
            Expect.equal (List.head app.Feeds).Tag (Some "news") "feed carries the tag filter"
        }

        test "withTagFeeds fans out one feed per tag at /tag/{x}/feed.atom" {
            let app =
                PublicRenderingServerApp.create ()
                |> PublicRenderingServerApp.withTagFeeds "Topics" cfg [ "news"; "events" ]

            let urls = app.Feeds |> List.map (fun f -> f.SelfUrl) |> List.sort
            Expect.equal urls [ "/tag/events/feed.atom"; "/tag/news/feed.atom" ] "one feed per tag"
            Expect.isTrue (app.Feeds |> List.forall (fun f -> Option.isSome f.Tag)) "every feed is tag-filtered"
        }
    ]