// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open Giraffe.ViewEngine
open ToolUp.Platform.Narrative

// ─── Phase 80 — Narrative ↔ PublicRendering integration ──────────
//
// Layout helpers that bridge a `PublicPage` carrying
// `ContentBody.Narrative` into Giraffe.ViewEngine `XmlNode` trees, plus
// the small `<head>` builder that derives schema.org JSON-LD from a
// `NarrativeDocument.Provenance` when the layout wants the structured-
// data tag attached automatically.
//
// The helpers are layout-shape (`PublicPage -> XmlNode` and similar)
// so they compose cleanly with `PublicRenderingServerApp.withLayout`.
// Layouts can pick and mix: render the body via `Body.render`, the
// JSON-LD via `Body.jsonLd`, and assemble the final `<html>` shell
// per their own site conventions.

module NarrativeLayout =
    /// Render a `PublicPage`'s body into a Giraffe.ViewEngine fragment.
    /// Markdown / Html bodies fall through to `rawText` — the markdown
    /// loader already pre-renders markdown to HTML, and `Html` bodies
    /// are HTML by definition. `Narrative` bodies run through
    /// `NarrativeHtml.render` and the resulting `<article>` fragment
    /// becomes the body content.
    ///
    /// The output is always wrapped in `rawText` because every branch
    /// produces already-escaped HTML — Narrative escapes inline content
    /// before embedding it, Markdown is escaped by the pre-render pass,
    /// and Html is the caller's responsibility (same trust contract as
    /// `MarkdownContentLoader`).
    let renderBody (page: PublicPage) : XmlNode =
        match page.Body with
        | Markdown source ->
            // Markdown is pre-rendered to HTML by the loader; layouts
            // typically don't reach this helper for Markdown pages, but
            // we degrade safely if they do — surface the source as a
            // pre-formatted block so a misrouted page is visibly wrong
            // rather than invisibly empty.
            pre [] [ encodedText source ]
        | Html fragment -> rawText fragment
        | Narrative doc -> rawText (NarrativeHtml.render doc)

    /// Build a schema.org `Article` JSON-LD blob from a
    /// `NarrativeDocument`'s `Provenance`. Returns `None` when the
    /// document has no provenance (every layout-driven JSON-LD
    /// requires `datePublished` and an identifier, both of which come
    /// from provenance).
    ///
    /// Layout authors embed the returned string as
    /// `script [ _type "application/ld+json" ] [ rawText json ]`
    /// inside `<head>`.
    let articleJsonLd (page: PublicPage) : string option =
        match page.Body with
        | Narrative doc -> StructuredDataHelpers.articleFromNarrative page doc
        | _ -> None

    /// Convenience: project a `PublicPage` whose body is a
    /// `NarrativeDocument` into a `PrerenderMeta` suitable for the
    /// Phase 57 prerender route system. Title / Description come from
    /// the page; the document's `Provenance.GeneratedAt` (if present)
    /// feeds `article:published_time` in the OpenGraph map. JSON-LD is
    /// the Article blob from `articleJsonLd`.
    ///
    /// Returns `None` when the page body is not a `Narrative` — the
    /// caller should fall back to its own `PrerenderMeta` construction
    /// for Markdown / Html pages.
    let prerenderMeta (page: PublicPage) : ToolUp.Platform.PrerenderMeta option =
        match page.Body with
        | Narrative doc -> Some(NarrativePrerender.fromPage page doc)
        | _ -> None

    /// Build a `<nav class="narrative-toc">` Giraffe.ViewEngine fragment
    /// listing every section of a Narrative-bodied page as an anchor
    /// link to its `id`. Returns `None` for non-Narrative bodies (the
    /// section model belongs to `NarrativeDocument`, not to
    /// `PublicPage`). When `includeHeadings = true`, walks each
    /// section's H3/H4 elements and renders them as nested entries.
    let tableOfContents (includeHeadings: bool) (page: PublicPage) : XmlNode option =
        match page.Body with
        | Narrative doc -> Some(rawText (NarrativeHtml.tableOfContents includeHeadings doc))
        | _ -> None

    /// Build the `<head>` SEO block from a Narrative-bodied page:
    /// `<link rel="canonical">`, `<html lang="...">`-ready `Lang`
    /// override (caller applies to `<html>`), schema.org Article
    /// JSON-LD, and the OpenGraph + Twitter card meta tags.
    /// Returns the list of `XmlNode`s the caller splices into
    /// `head [ ... ]`. Non-Narrative bodies return an empty list so
    /// the caller can unconditionally `yield! NarrativeLayout.headTags`.
    let headTags (page: PublicPage) : XmlNode list =
        match page.Body with
        | Narrative doc ->
            let canonical =
                match doc.CanonicalUrl with
                | Some url -> [ link [ _rel "canonical"; _href url ] ]
                | None -> []

            let jsonLd =
                match StructuredDataHelpers.articleFromNarrative page doc with
                | Some payload -> [ script [ _type "application/ld+json" ] [ rawText payload ] ]
                | None -> []

            let openGraph =
                StructuredDataHelpers.openGraphFromNarrative page doc
                |> List.map (fun (property, content) -> meta [ _property property; _content content ])

            let twitter =
                StructuredDataHelpers.twitterCardFromNarrative page doc
                |> List.map (fun (name, content) -> meta [ _name name; _content content ])

            canonical @ jsonLd @ openGraph @ twitter
        | _ -> []

    // ─── Phase 87 — rich-content rendering policy ────────────────────
    //
    // The `Embed` and `Component` narrative blocks (Phase 87) need a
    // deployment-level rendering policy that the document itself must
    // not carry: an allowlist of embed origins (CSP discipline) and a
    // registry of custom component renderers. Both live here, at the
    // rendering layer, so the same `NarrativeDocument` renders safely
    // under any deployment's policy.
    //
    // Component renderers are authored as pure `props -> XmlNode`
    // functions — the natural Giraffe.ViewEngine shape — and bridged
    // into the SDK's string-producing `NarrativeHtml.RenderOptions`
    // seam by `componentResolver`. This is the one sanctioned narrative
    // type-erasure boundary (see toolup-forge/CLAUDE.md "Type erasure
    // boundaries"): a deployment ships new block kinds without forking
    // the `NarrativeElement` DU.

    /// A custom component renderer: a pure map of `props` to a
    /// Giraffe.ViewEngine node. Registered by block name in a
    /// `ComponentRegistry`. Must be deterministic (same props → same
    /// node) so prerendered and hydrated output agree.
    type ComponentRenderer = Map<string, string> -> XmlNode

    /// Name-keyed registry of `ComponentRenderer`s. A `Component(name,
    /// props)` block resolves its renderer by `name`; an unregistered
    /// name degrades to the SDK's safe placeholder.
    type ComponentRegistry = Map<string, ComponentRenderer>

    /// The empty component registry — no custom blocks. The default for
    /// a deployment that authors no `Component` blocks.
    let noComponents: ComponentRegistry = Map.empty

    /// Bridge a `props -> XmlNode` `ComponentRegistry` into the SDK's
    /// `NarrativeHtml.RenderOptions.ComponentRenderer` seam (a
    /// `string -> Map -> string option` resolver). A registered name
    /// renders its node to an HTML string; an unregistered name returns
    /// `None`, so the SDK falls through to the safe placeholder.
    let componentResolver (registry: ComponentRegistry) : string -> Map<string, string> -> string option =
        fun name props ->
            match registry.TryFind name with
            | Some render -> Some(RenderView.AsString.htmlNode (render props))
            | None -> None

    /// Build `NarrativeHtml.RenderOptions` carrying a deployment's
    /// embed-origin allowlist and component registry. Every other
    /// option keeps its `RenderOptions.Default` value, so existing
    /// (non-rich) narratives render byte-for-byte unchanged (GP 11).
    ///
    /// `allowedEmbedOrigins` entries are `scheme://host[:port]`,
    /// lowercased — e.g. `"https://www.youtube.com"`,
    /// `"https://player.vimeo.com"`. An `Embed` whose URL origin is not
    /// in the set degrades to a safe placeholder link.
    let richRenderOptions
        (allowedEmbedOrigins: Set<string>)
        (registry: ComponentRegistry)
        : NarrativeHtml.RenderOptions =
        {
            NarrativeHtml.RenderOptions.Default with
                AllowedEmbedOrigins = allowedEmbedOrigins
                ComponentRenderer = componentResolver registry
        }

    /// Render a `PublicPage`'s body with a rich-content rendering
    /// policy (embed allowlist + component registry). Identical to
    /// `renderBody` for Markdown / Html bodies; `Narrative` bodies run
    /// through `NarrativeHtml.renderWith` so `Embed` / `Component`
    /// blocks honour the supplied policy. Use this in place of
    /// `renderBody` when a layout serves pages that carry Phase 87
    /// media / layout blocks.
    let renderBodyWith (allowedEmbedOrigins: Set<string>) (registry: ComponentRegistry) (page: PublicPage) : XmlNode =
        match page.Body with
        | Markdown source -> pre [] [ encodedText source ]
        | Html fragment -> rawText fragment
        | Narrative doc -> rawText (NarrativeHtml.renderWith (richRenderOptions allowedEmbedOrigins registry) doc)