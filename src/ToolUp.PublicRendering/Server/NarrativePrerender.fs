// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform
open ToolUp.Platform.Narrative

// ─── Phase 80 — Narrative ↔ Phase-57 prerender bridge ────────────
//
// `PrerenderMeta` (Phase 57) drives the `<head>` of build-time
// prerendered SPA routes. When a deployment composes a page whose body
// is a `NarrativeDocument`, the title / subtitle / provenance fields
// already carry every piece of metadata the prerender pass needs —
// duplicating them in a hand-authored `PrerenderRoute.Meta` is the
// kind of drift Phase 80 exists to prevent.
//
// `NarrativePrerender.fromDocument` / `fromPage` derive a
// `PrerenderMeta` from a Narrative document in one place; route
// authors consume the result via `ClientConfig.PrerenderRoutes`
// alongside (or in place of) hand-authored entries.

module NarrativePrerender =
    /// Build a `PrerenderMeta` from a stand-alone `NarrativeDocument`.
    /// `description` defaults to the document's `Subtitle` when
    /// present, an empty string otherwise — `PrerenderMeta` requires a
    /// non-null description so callers wanting a richer fallback
    /// (first-paragraph excerpt, frontmatter override) should
    /// post-process the returned record.
    ///
    /// `Provenance.GeneratedAt` contributes
    /// `og:article:published_time` and the corresponding
    /// schema.org Article JSON-LD via
    /// `StructuredDataHelpers.articleFromNarrative` with a
    /// synthesised page (deployments wanting the full
    /// `articleFromNarrative` shape with frontmatter overrides should
    /// use `fromPage` instead).
    let fromDocument (doc: NarrativeDocument) : PrerenderMeta =
        let description = doc.Subtitle |> Option.defaultValue ""

        let openGraph =
            let baseEntries = [ "title", doc.Title; "type", "article"; "description", description ]

            let withPublished =
                match doc.Provenance with
                | Some prov -> ("article:published_time", prov.GeneratedAt.ToString("o")) :: baseEntries
                | None -> baseEntries

            Map.ofList withPublished

        let jsonLd =
            match doc.Provenance with
            | Some _ ->
                // Use a minimal synthesised PublicPage so the helper has a
                // page-shaped value to read Title / Description off.
                // Layouts that want frontmatter-driven overrides should
                // call `fromPage` instead.
                let synthetic: PublicPage = {
                    Slug = Slug ""
                    Title = doc.Title
                    Description = description
                    Body = Narrative doc
                    Layout = LayoutName ""
                    Frontmatter = Map.empty
                    PublishedAt = doc.Provenance |> Option.map _.GeneratedAt
                    Collection = None
                    Status = Published
                }

                StructuredDataHelpers.articleFromNarrative synthetic doc
            | None -> None

        {
            Title = doc.Title
            Description = description
            OpenGraph = openGraph
            JsonLd = jsonLd
        }

    /// Build a `PrerenderMeta` from a `PublicPage` whose body is a
    /// `NarrativeDocument`. Falls back to `fromDocument` when the
    /// body is not Narrative-shaped (the returned meta uses the
    /// `page.Title` / `page.Description` overrides regardless).
    let fromPage (page: PublicPage) (doc: NarrativeDocument) : PrerenderMeta =
        let baseMeta = fromDocument doc

        let title =
            if System.String.IsNullOrWhiteSpace page.Title then
                baseMeta.Title
            else
                page.Title

        let description =
            if System.String.IsNullOrWhiteSpace page.Description then
                baseMeta.Description
            else
                page.Description

        let openGraph =
            baseMeta.OpenGraph |> Map.add "title" title |> Map.add "description" description

        let jsonLd = StructuredDataHelpers.articleFromNarrative page doc

        {
            Title = title
            Description = description
            OpenGraph = openGraph
            JsonLd = jsonLd
        }