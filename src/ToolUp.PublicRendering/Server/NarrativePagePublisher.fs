// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Narrative

// ─── Phase 80a — Default INarrativePagePublisher implementation ────
//
// Bridges the AI tool `publish_narrative` (defined in
// `ToolUp.AI.Server/Server/NarrativeTools.fs`) to PublicRendering's
// `IEntityStore<PublicPageEntity>` backing store. Registered in DI by
// `PublicRenderingCompose.run` when public rendering is enabled; AI
// tools resolve it from the request services at publish time.
//
// The implementation is deliberately thin — every interesting concern
// (overrides, document mutation, scope resolution) is already handled
// by the caller. The publisher's job is just to construct the page
// envelope, encode the slug, and write it through `IEntityStore.Save`.

/// Optional layouts hint passed by `PublicRenderingCompose.run` so the
/// publisher can fall back to the first-registered layout when the
/// caller's `layoutHint` is `None` or doesn't match. Empty when the
/// deployment has registered no layouts (an unusual but valid
/// configuration when the deployment's pages all use a single
/// implicit shape — Save still succeeds, the PublicPageHandler will
/// surface "no layout registered" at render time).
type internal LayoutHint = LayoutHint of LayoutName list

type PublicRenderingNarrativePagePublisher
    (
        entityStore: IEntityStore,
        registeredLayouts: LayoutName list,
        renderCacheInvalidator: IRenderCacheInvalidation option
    ) =

    let resolveLayout (hint: string option) : LayoutName =
        match hint with
        | Some name when registeredLayouts |> List.contains (LayoutName name) -> LayoutName name
        | _ ->
            match registeredLayouts with
            | first :: _ -> first
            // No layouts registered — write a placeholder layout name.
            // PublicPageHandler will surface the missing-layout error
            // at render time (a more useful place to fail than at
            // publish time, since the operator can register a layout
            // and immediately make published pages work).
            | [] -> LayoutName ""

    let sanitiseSlug (raw: string) : string =
        // Strip a leading slash (caller may pass with or without) and
        // trim whitespace. We don't lowercase or rewrite — slugs are
        // case-sensitive (filesystem convention) and the caller owns
        // their canonical form.
        raw.TrimStart('/').Trim()

    let slugExists (slug: string) : Async<bool> = async {
        let! result =
            entityStore.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)

        match result with
        | Ok _ -> return true
        | Error _ -> return false
    }

    /// Walk `slug`, `slug-2`, `slug-3`, … until a free slug is found.
    /// Caps at 100 attempts to avoid a runaway when a misconfigured
    /// IEntityStore reports every slug as occupied.
    let rec findFreeSlug (baseSlug: string) (attempt: int) : Async<string option> = async {
        if attempt > 100 then
            return None
        else
            let candidate =
                if attempt = 1 then
                    baseSlug
                else
                    sprintf "%s-%d" baseSlug attempt

            let! exists = slugExists candidate

            if exists then
                return! findFreeSlug baseSlug (attempt + 1)
            else
                return Some candidate
    }

    interface INarrativePagePublisher with
        member _.PublishAsync(slug, titleOverride, descriptionOverride, layoutHint, collisionPolicy, document) = async {
            let requestedSlug = sanitiseSlug slug

            if System.String.IsNullOrWhiteSpace requestedSlug then
                return PublishFailed "slug is required (received an empty / whitespace-only value)"
            else
                // Resolve the target slug per collision policy. Reject
                // and AutoSuffix both consult the entity store first;
                // OverwriteExisting skips the check (the existing Save
                // semantics already handle the overwrite).
                let! resolvedSlug = async {
                    match collisionPolicy with
                    | OverwriteExisting -> return Some requestedSlug
                    | RejectIfExists ->
                        let! exists = slugExists requestedSlug
                        if exists then return None else return Some requestedSlug
                    | AutoSuffix -> return! findFreeSlug requestedSlug 1
                }

                match resolvedSlug with
                | None ->
                    match collisionPolicy with
                    | RejectIfExists ->
                        return
                            PublishFailed(
                                sprintf
                                    "slug '%s' is already occupied (RejectIfExists policy); pass a different slug or use OverwriteExisting / AutoSuffix to proceed"
                                    requestedSlug
                            )
                    | _ ->
                        return
                            PublishFailed(
                                sprintf
                                    "could not find a free slug starting from '%s' within 100 attempts"
                                    requestedSlug
                            )
                | Some canonicalSlug ->
                    let title =
                        titleOverride
                        |> Option.defaultValue (
                            if System.String.IsNullOrWhiteSpace document.Title then
                                "(untitled)"
                            else
                                document.Title
                        )

                    let description =
                        descriptionOverride |> Option.orElse document.Subtitle |> Option.defaultValue ""

                    let page: PublicPage = {
                        Slug = Slug canonicalSlug
                        Title = title
                        Description = description
                        Body = Narrative document
                        Layout = resolveLayout layoutHint
                        Frontmatter = Map.empty
                        PublishedAt = Some DateTimeOffset.UtcNow
                        Collection = None
                        Status = Published
                        // Phase 86 — AI-published pages land Public; gated
                        // authoring is a Phase 91 hardening concern.
                        Audience = PageAudience.Public
                    }

                    let envelope = PublicPageEntity.fromPage page

                    let! result = entityStore.Save<PublicPageEntity>(PublicPageEntity.PublicScope, envelope)

                    match result with
                    | Ok _ ->
                        // Phase 84 — purge any cached render of this slug so
                        // the republished content is served immediately
                        // rather than waiting out the prior entry's TTL. A
                        // no-op when no render cache is composed.
                        match renderCacheInvalidator with
                        | Some inv -> do! inv.PurgeSlug canonicalSlug
                        | None -> ()

                        return PublishSucceeded canonicalSlug
                    | Error err -> return PublishFailed(sprintf "entity store rejected the save: %A" err)
        }

module PublicRenderingNarrativePagePublisher =
    /// Factory invoked from `PublicRenderingCompose.run` once the
    /// registered layout map is known. Returns the interface-typed
    /// value so the DI registration carries the abstraction.
    /// `renderCacheInvalidator` (Phase 84) purges a slug's cached render
    /// on a successful publish; pass `None` when no render cache is
    /// composed.
    let create
        (entityStore: IEntityStore)
        (layoutNames: LayoutName list)
        (renderCacheInvalidator: IRenderCacheInvalidation option)
        : INarrativePagePublisher =
        PublicRenderingNarrativePagePublisher(entityStore, layoutNames, renderCacheInvalidator)
        :> INarrativePagePublisher

// ─── Phase 80b — Layout catalog ────────────────────────────────

/// `ILayoutCatalog` impl backed by the layouts registered on
/// `PublicRenderingServerApp` at compose time. Read-only; the
/// catalog snapshot is captured at construction and doesn't track
/// later additions (compose-time registration is final after
/// `PublicRenderingCompose.run` runs).
type PublicRenderingLayoutCatalog(layoutNames: string list) =
    interface ILayoutCatalog with
        member _.ListLayoutNames() = layoutNames

module PublicRenderingLayoutCatalog =
    let create (layoutNames: LayoutName list) : ILayoutCatalog =
        let strings = layoutNames |> List.map LayoutName.value
        PublicRenderingLayoutCatalog(strings) :> ILayoutCatalog