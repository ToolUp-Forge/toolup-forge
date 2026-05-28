module ToolUp.Platform.Tests.Contracts.IPublicContentApiContract

open System
open Expecto
open ToolUp.PublicRendering

// ─── IPublicContentApi contract pack ────────────────────────────────
//
// Parametrised tests for any `IPublicContentApi` implementation. The
// factory yields a pre-populated API instance whose content matches
// the canonical fixture documented below. In-process bindings set up
// the fixture via real markdown files; distributed-store bindings
// (e.g. an `IEntityStore<PublicPage>`-backed impl) seed equivalent
// records.
//
// **Canonical fixture** — every binding must arrange these pages:
//
//   slug                              | layout   | collection | date
//   ──────────────────────────────────┼──────────┼────────────┼──────────────
//   about                             | page     | (none)     | (none)
//   services/consulting               | page     | (none)     | (none)
//   news/2026-05-22-launch            | article  | news       | 2026-05-22
//   news/2026-05-15-announcement      | article  | news       | 2026-05-15
//   draft                             | page     | (none)     | sitemap=exclude
//
// Tests reference these by slug; bindings pre-populate before
// handing the API instance back.

let tests (name: string) (factory: unit -> IPublicContentApi) =

    testList $"{name} — IPublicContentApi contract" [

        // ─── GetPage ──────────────────────────────────────────────

        testCaseAsync "GetPage returns Some for a known slug with fields populated"
        <| async {
            let api = factory ()
            let! pageOpt = api.GetPage "about"

            match pageOpt with
            | Some page ->
                Expect.equal (Slug.value page.Slug) "about" "Slug echoed"
                Expect.equal (LayoutName.value page.Layout) "page" "Layout from frontmatter"
                Expect.equal page.Collection None "Top-level page has no collection"
            | None -> failtest "Expected Some about-page; got None"
        }

        testCaseAsync "GetPage returns None for an unknown slug (404 fall-through)"
        <| async {
            let api = factory ()
            let! pageOpt = api.GetPage "does-not-exist"
            Expect.isNone pageOpt "Unknown slug must return None"
        }

        testCaseAsync "GetPage resolves nested slugs (directory shape)"
        <| async {
            let api = factory ()
            let! pageOpt = api.GetPage "services/consulting"
            Expect.isSome pageOpt "Nested slug should resolve"

            let page = pageOpt.Value
            Expect.equal (Slug.value page.Slug) "services/consulting" "Slug preserves nesting"
            Expect.equal page.Collection None "services/ subdir without 'pages/' prefix is still slug-only"
        }

        // ─── ListPages ────────────────────────────────────────────

        testCaseAsync "ListPages with empty prefix returns every page"
        <| async {
            let api = factory ()
            let! pages = api.ListPages ""
            Expect.isGreaterThan pages.Length 4 "Fixture has at least five pages"
            let slugs = pages |> List.map (fun p -> Slug.value p.Slug)
            Expect.contains slugs "about" "Top-level page surfaces"
            Expect.contains slugs "news/2026-05-22-launch" "News page surfaces"
        }

        testCaseAsync "ListPages filters by prefix"
        <| async {
            let api = factory ()
            let! pages = api.ListPages "news/"

            let allMatchPrefix =
                pages |> List.forall (fun p -> (Slug.value p.Slug).StartsWith "news/")

            Expect.isTrue allMatchPrefix "Every result must start with the prefix"
            Expect.equal pages.Length 2 "Fixture has two news pages"
        }

        // ─── GetCollection ────────────────────────────────────────

        testCaseAsync "GetCollection returns pages newest-first by PublishedAt"
        <| async {
            let api = factory ()
            let! news = api.GetCollection "news"
            Expect.equal news.Length 2 "Fixture has two news entries"

            let dates = news |> List.map (fun p -> p.PublishedAt |> Option.map _.UtcDateTime)

            match dates with
            | [ Some first; Some second ] -> Expect.isGreaterThan first second "Newest first"
            | _ -> failtestf "Expected two dated news pages, got %A" dates
        }

        testCaseAsync "GetCollection returns empty list for an unknown collection"
        <| async {
            let api = factory ()
            let! pages = api.GetCollection "no-such-collection"
            Expect.equal pages.Length 0 "Unknown collection yields empty list, not an error"
        }
    ]