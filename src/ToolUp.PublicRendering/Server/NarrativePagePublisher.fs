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

type PublicRenderingNarrativePagePublisher(entityStore: IEntityStore, registeredLayouts: LayoutName list) =

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

    interface INarrativePagePublisher with
        member _.PublishAsync(slug, titleOverride, descriptionOverride, layoutHint, document) = async {
            let canonicalSlug = sanitiseSlug slug

            if System.String.IsNullOrWhiteSpace canonicalSlug then
                return PublishFailed "slug is required (received an empty / whitespace-only value)"
            else
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
                }

                let envelope = PublicPageEntity.fromPage page

                let! result = entityStore.Save<PublicPageEntity>(PublicPageEntity.PublicScope, envelope)

                match result with
                | Ok _ -> return PublishSucceeded canonicalSlug
                | Error err -> return PublishFailed(sprintf "entity store rejected the save: %A" err)
        }

module PublicRenderingNarrativePagePublisher =
    /// Factory invoked from `PublicRenderingCompose.run` once the
    /// registered layout map is known. Returns the interface-typed
    /// value so the DI registration carries the abstraction.
    let create (entityStore: IEntityStore) (layoutNames: LayoutName list) : INarrativePagePublisher =
        PublicRenderingNarrativePagePublisher(entityStore, layoutNames) :> INarrativePagePublisher