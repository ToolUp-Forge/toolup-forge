// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Narrative

// ─── Phase 80b — Site-wide Atom feed aggregator ──────────────────
//
// `?format=atom` (Phase 80a) gives one Atom `<entry>` per page. The
// feed-level surface is the next step: a `<feed>` containing the
// most-recently-published Narrative-bodied pages across the site,
// surfaced at `/feed.atom` (or a per-collection variant at
// `/{collection}.atom`).
//
// v1 sources:
// - `IPublicContentApi.ListPages("")` — file-loaded markdown pages.
//   Filtered down to Narrative-bodied pages, which means *zero*
//   results for a pure markdown content site (those don't have
//   Narrative bodies). The handler still emits a valid empty feed.
// - `IEntityStore.ListAll<PublicPageEntity>` — entity-store-overlay
//   pages, which is where `publish_narrative` writes. Per-ref fetch
//   then filter to Narrative bodies + optional collection match.
//
// Sorting: PublishedAt descending, None last. Cap at `MaxEntries`
// (default 20). For v1 this is acceptable for the typical feed
// consumer (RSS reader, Slack bot, IFTTT). Higher-scale deployments
// will want a denormalised index — a follow-on.
//
// Non-Narrative bodies are skipped rather than synthesised. Building
// a NarrativeDocument from a Markdown / Html body needs a parsing
// pass that's its own design choice — feed item authors should
// publish Narrative-bodied versions when they want feed coverage.

/// Compose-time feed configuration. `Title` is the feed's
/// `<title>`; `SelfUrl` is the URL the feed itself is served from
/// (`<link rel="self">`); `AlternateUrl` is the human-readable
/// site URL (`<link rel="alternate">`); `Collection` filters to a
/// single PublicPage Collection when set (None = whole site);
/// `MaxEntries` caps the entry count (default 20).
type NarrativeFeedConfig = {
    Title: string
    SelfUrl: string
    AlternateUrl: string
    Collection: string option
    MaxEntries: int
    /// Phase 97 — when `Some tag`, the feed surfaces only pages carrying
    /// that tag (the taxonomy-axis feed, `/tag/{x}/feed.atom`). `None`
    /// (default) = no tag filter, so an existing whole-site / per-
    /// collection feed is unaffected (GP 11). Composes with `Collection`
    /// (both filters apply).
    Tag: string option
}

module NarrativeFeedConfig =
    /// Sensible default: whole-site feed at `/feed.atom`, 20 entries.
    /// Caller must override `Title` / `SelfUrl` / `AlternateUrl` to
    /// reflect their deployment.
    let defaults: NarrativeFeedConfig = {
        Title = "Feed"
        SelfUrl = "/feed.atom"
        AlternateUrl = "/"
        Collection = None
        MaxEntries = 20
        Tag = None
    }

module NarrativeFeedHandler =
    let private extractNarrative (page: PublicPage) : (PublicPage * NarrativeDocument) option =
        match page.Body with
        | Narrative doc -> Some(page, doc)
        | _ -> None

    let private collectionMatches (filter: string option) (page: PublicPage) : bool =
        match filter, page.Collection with
        | None, _ -> true
        | Some f, Some c -> f = c
        | Some _, None -> false

    /// The pure feed-entry selection: public, collection-matched,
    /// tag-matched (Phase 97), Narrative-bodied pages, newest-first,
    /// capped at `MaxEntries`. Extracted so the filter is unit-testable
    /// without an HTTP context.
    let selectEntries (config: NarrativeFeedConfig) (pages: PublicPage list) : NarrativeDocument list =
        pages
        // Phase 86 — gated (non-`Public`) pages never surface in a feed.
        |> List.filter PublicPage.isPublic
        |> List.filter (collectionMatches config.Collection)
        // Phase 97 — taxonomy-axis filter.
        |> List.filter (fun p ->
            match config.Tag with
            | Some tag -> PublicPage.hasTag tag p
            | None -> true)
        |> List.choose extractNarrative
        |> List.sortByDescending (fun (page, _) ->
            page.PublishedAt
            |> Option.defaultValue (System.DateTimeOffset(System.DateTime.MinValue, System.TimeSpan.Zero)))
        |> List.truncate config.MaxEntries
        |> List.map snd

    let private gatherEntityStorePages (entityStore: IEntityStore) : Async<PublicPage list> = async {
        // ListAll returns refs; for v1 we fetch the head version of
        // each ref. Cap the listing at 200 to keep the worst case
        // bounded even before filtering (the per-handler MaxEntries
        // cuts it further after sorting).
        let! refs =
            entityStore.ListAll<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, 0, 200)

        let! envelopes =
            refs
            |> List.map (fun ref ->
                entityStore.Get<PublicPageEntity>(
                    PublicPageEntity.PublicScope,
                    PublicPageEntity.EntityTypeName,
                    ref.Id
                ))
            |> Async.Sequential

        return
            envelopes
            |> Array.toList
            |> List.choose (function
                | Ok envelope -> Some envelope.Page
                | Error _ -> None)
    }

    let handler (config: NarrativeFeedConfig) (api: IPublicContentApi) : HttpHandler =
        fun _next (ctx: HttpContext) -> task {
            let! filePages = api.ListPages ""

            let entityStore =
                ctx.RequestServices.GetService(typeof<IEntityStore>)
                |> Option.ofObj
                |> Option.map (fun x -> x :?> IEntityStore)

            let! storePages =
                match entityStore with
                | Some store -> gatherEntityStorePages store
                | None -> async { return [] }

            let allPages = filePages @ storePages
            let narrativePages = selectEntries config allPages

            let xml =
                NarrativeAtom.renderFeed config.Title config.SelfUrl config.AlternateUrl narrativePages

            ctx.Response.ContentType <- "application/atom+xml; charset=utf-8"
            return! ctx.WriteStringAsync xml
        }