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
    ///
    /// **Phase 632 — this source gates the provider's list itself, on
    /// both arms.** It is a public, anonymous listing surface reached
    /// through a caller-supplied thunk, i.e. exactly the shape whose
    /// safety cannot be established locally; so rather than document a
    /// requirement on the thunk it applies `PublicPage.isPubliclyDiscoverable`
    /// here. This is not hypothetical tidiness. Between Phase 100 (which
    /// compose-wired `tagIndexSourceFromApi` to the raw `ListPages`) and
    /// this phase, a `Draft` page carrying the default `Audience = Public`
    /// had its TITLE and SLUG rendered into `/tag/{its-tag}`, and — worse,
    /// because the enumerate arm feeds `SitemapGenerator`'s `dynamicSlugs`,
    /// which Phase 38 appends WITHOUT re-gating — a tag that existed only
    /// on unpublished pages was enumerated as a live route into
    /// `sitemap.xml`, the static export, and the IndexNow push. Phase 38
    /// fixed five surfaces; this was the sixth, and it is why the gate is
    /// now on the seam rather than in five (six) callers.
    let tagIndexSource (listPages: unit -> Async<PublicPage list>) : IContentSource =
        let discoverable () = async {
            let! pages = listPages ()
            return PublicContentApi.gateAt System.DateTimeOffset.UtcNow pages
        }

        ContentSource.ofRouteEnumerable
            "tag/{slug}"
            (fun captures _ctx -> async {
                let tag = captures.TryFind "slug" |> Option.defaultValue ""
                let! pages = discoverable ()
                let matching = pages |> List.filter (PublicPage.hasTag tag)
                return Some(Narrative(buildTagDoc tag matching))
            })
            // Phase 95 — enumerate one `/tag/{slug}` route per distinct
            // tag (lower-cased, deduped), so sitemap.xml / static export /
            // prerender discover the tag-index pages this source produces.
            // Phase 632 — over the DISCOVERABLE pages only: an enumerated
            // route is appended to the sitemap universe un-regated, so a
            // tag drawn from a draft would be pushed to crawlers as a live
            // URL.
            (fun () -> async {
                let! pages = discoverable ()

                return
                    pages
                    |> List.collect (fun p -> PublicPage.tags p |> List.map (fun t -> t.ToLowerInvariant()))
                    |> List.distinct
                    |> List.map (fun t -> Slug("tag/" + t))
            })

    /// Convenience: `tagIndexSource` over an `IPublicContentApi`'s
    /// **gated** enumeration (Phase 632 `ListPagesPublic`) across the file
    /// + entity-overlay tiers — never the source tier, so no resolution
    /// recursion. This is what `withTaxonomy` composes. Use when the
    /// deployment holds an `IPublicContentApi` reference (e.g. a custom
    /// `withContentApi` impl).
    let tagIndexSourceFromApi (api: IPublicContentApi) : IContentSource =
        tagIndexSource (fun () -> PublicContentApi.listPagesPublicNow api "")

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

    // ─── Phase 99 — faceted multi-tag browse ─────────────────────────

    /// Filter `pages` by multiple tags and compute the facet counts over
    /// the filtered set. `matchAll = true` requires every tag (AND);
    /// `false` requires any (OR). An empty `tags` list returns every page
    /// (with facets over all). Case-insensitive. The facet counts narrow
    /// as tags are added, so a sidebar can show how many pages each
    /// further tag would leave. Pure / deterministic.
    let facetedBrowse
        (matchAll: bool)
        (tags: string list)
        (pages: PublicPage list)
        : PublicPage list * (string * int) list =
        let norm (t: string) = t.ToLowerInvariant()
        let wanted = tags |> List.map norm |> Set.ofList

        let matches (p: PublicPage) =
            let pageTags = PublicPage.tags p |> List.map norm |> Set.ofList

            if Set.isEmpty wanted then true
            elif matchAll then Set.isSubset wanted pageTags
            else not (Set.isEmpty (Set.intersect wanted pageTags))

        let filtered = pages |> List.filter matches
        filtered, tagCounts filtered

    let private buildBrowseDoc
        (tags: string list)
        (results: PublicPage list)
        (facets: (string * int) list)
        : NarrativeDocument =
        let title =
            if List.isEmpty tags then
                "Browse"
            else
                sprintf "Browse: %s" (String.concat ", " tags)

        let resultsSection =
            match results with
            | [] -> [
                Narrative.callout Info [ Narrative.text "No pages match this combination of tags." ]
              ]
            | _ -> [
                Narrative.bullets (
                    results
                    |> List.map (fun p -> [ Narrative.linkText p.Title ("/" + Slug.value p.Slug) ])
                )
              ]

        let facetSection =
            facets
            |> List.map (fun (tag, count) -> tag, [ Narrative.text (string count) ])
            |> Narrative.keyValues

        Narrative.create title
        |> Narrative.section "Results" "results" resultsSection
        |> Narrative.section "Filter by tag" "facets" [ facetSection ]

    /// An `IContentSource` for faceted browse at `browse/{tags}`, where
    /// `{tags}` is a `+`-separated tag list (e.g. `browse/news+product`,
    /// AND semantics). Renders the matching pages plus a facet sidebar
    /// (each remaining tag + the count it would yield). Scoped to the
    /// requesting principal via the provider + a public filter (GP 4).
    /// Composes with [Phase 98](Pagination.fs) — a layout paginates the
    /// results list.
    let facetedBrowseSource (listPages: unit -> Async<PublicPage list>) : IContentSource =
        ContentSource.ofRoute "browse/{tags}" (fun captures _ctx -> async {
            let raw = captures.TryFind "tags" |> Option.defaultValue ""

            let tags = raw.Split('+') |> Array.toList |> List.filter (fun s -> s <> "")

            let! pages = listPages ()
            // Exclude gated (non-public audience) AND non-published
            // (draft / scheduled-not-yet / archived) pages from the
            // browse listing (GP 4 + publish lifecycle). Phase 632 —
            // expressed through the one shared gate rather than a
            // hand-conjoined pair, so this surface cannot drift from the
            // others if the predicate gains a third axis.
            let scoped = PublicContentApi.gateAt System.DateTimeOffset.UtcNow pages

            let results, facets = facetedBrowse true tags scoped
            return Some(Narrative(buildBrowseDoc tags results facets))
        })