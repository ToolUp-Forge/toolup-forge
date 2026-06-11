// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 90 — taxonomy serving: tag-index pages + related content + facets.
///
/// `tagIndexSource` is an `IContentSource` ([Phase 83](IContentSource.fs))
/// that serves `/tag/{slug}` pages listing every page carrying that tag
/// (tags are frontmatter-derived — see `PublicPage.tags`). It composes
/// through the existing `PublicRenderingServerApp.withContentSource` seam;
/// no new compose plumbing is needed. `relatedByTag` and `tagCounts` are
/// pure helpers a layout / module uses for related-content blocks and
/// faceted-browse sidebars.
///
/// The tag-index body is a `NarrativeDocument` (rendered through the
/// existing Phase 80 renderers), so a tag page is HTML / Markdown / Atom
/// without any hand-rolled markup — the same pattern as
/// [`NarrativeFromData`](NarrativeFromData.fs).
namespace ToolUp.PublicRendering

open ToolUp.Platform
open ToolUp.Platform.Narrative

module TaxonomyHandler =

    /// Build the tag-index `NarrativeDocument` for `tag` over the matching
    /// pages. Empty matches degrade to a thoughtful empty-state callout
    /// rather than a bare title.
    let private buildTagDoc (tag: string) (pages: PublicPage list) : NarrativeDocument =
        let title = sprintf "Tagged “%s”" tag

        match pages with
        | [] ->
            Narrative.create title
            |> Narrative.section "Results" "results" [
                Narrative.callout Info [ Narrative.text (sprintf "No pages are tagged “%s” yet." tag) ]
            ]
        | _ ->
            let count = List.length pages

            let items =
                pages
                |> List.map (fun p -> [ Narrative.linkText p.Title ("/" + Slug.value p.Slug) ])

            Narrative.create title
            |> Narrative.subtitle (sprintf "%d page%s" count (if count = 1 then "" else "s"))
            |> Narrative.section "Results" "results" [ Narrative.bullets items ]

    /// An `IContentSource` claiming `tag/{slug}` and listing every page
    /// the supplied provider returns that carries the captured tag. The
    /// provider (`listPages`) enumerates the candidate page set — pass
    /// `fun () -> api.ListPages ""` for the default content API, or a
    /// scoped subset. The source claims only `tag/...` slugs; any other
    /// slug falls through (`None`) without invoking the provider.
    let tagIndexSource (listPages: unit -> Async<PublicPage list>) : IContentSource =
        ContentSource.ofRouteEnumerable
            "tag/{slug}"
            (fun captures _ctx -> async {
                let tag = captures.TryFind "slug" |> Option.defaultValue ""
                let! pages = listPages ()
                let matching = pages |> List.filter (PublicPage.hasTag tag)
                return Some(Narrative(buildTagDoc tag matching))
            })
            // Phase 95 — enumerate one `/tag/{slug}` route per distinct
            // tag (lower-cased, deduped), so sitemap.xml / static export /
            // prerender discover the tag-index pages this source produces.
            (fun () -> async {
                let! pages = listPages ()

                return
                    pages
                    |> List.collect (fun p -> PublicPage.tags p |> List.map (fun t -> t.ToLowerInvariant()))
                    |> List.distinct
                    |> List.map (fun t -> Slug("tag/" + t))
            })

    /// Convenience: `tagIndexSource` enumerating via an
    /// `IPublicContentApi`'s `ListPages ""` (the file + entity-overlay
    /// tiers — never the source tier, so no resolution recursion). Use
    /// when the deployment holds an `IPublicContentApi` reference (e.g. a
    /// custom `withContentApi` impl).
    let tagIndexSourceFromApi (api: IPublicContentApi) : IContentSource =
        tagIndexSource (fun () -> api.ListPages "")

    /// Pages sharing at least one tag with `page` (case-insensitive),
    /// excluding `page` itself, ranked by shared-tag count descending
    /// (ties keep input order — `List.sortByDescending` is stable).
    /// `[]` when `page` has no tags. The pure-data path; when RAG is
    /// composed a deployment can layer semantic-related on top.
    let relatedByTag (allPages: PublicPage list) (page: PublicPage) : PublicPage list =
        let norm (t: string) = t.ToLowerInvariant()
        let mine = PublicPage.tags page |> List.map norm |> Set.ofList

        if Set.isEmpty mine then
            []
        else
            allPages
            |> List.filter (fun p -> Slug.value p.Slug <> Slug.value page.Slug)
            |> List.choose (fun p ->
                let shared =
                    PublicPage.tags p
                    |> List.map norm
                    |> Set.ofList
                    |> Set.intersect mine
                    |> Set.count

                if shared > 0 then Some(p, shared) else None)
            |> List.sortByDescending snd
            |> List.map fst

    /// Faceted-browse tag counts: every distinct tag across `pages` with
    /// its page count, sorted by count descending then tag ascending (a
    /// deterministic, stable facet order). Tags are compared / grouped
    /// case-insensitively; the display form is the lower-cased tag.
    let tagCounts (pages: PublicPage list) : (string * int) list =
        pages
        |> List.collect (fun p -> PublicPage.tags p |> List.map (fun t -> t.ToLowerInvariant()))
        |> List.countBy id
        |> List.sortBy (fun (tag, count) -> -count, tag)