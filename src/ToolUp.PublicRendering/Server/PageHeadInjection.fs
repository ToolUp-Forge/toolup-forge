// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open Giraffe.ViewEngine

// ─── Phase 111 — `<head>` tag emission for resolved-content pages ────
//
// Layouts own the whole document (`PublicPage -> XmlNode`, rendered via
// `RenderView.AsString.htmlDocument`), so an existing layout cannot know
// about per-request head metadata a `ResolvedContent` source attached.
// Rather than change every layout signature (breaking) the handler
// injects the metadata's tags into the rendered document immediately
// before the closing `</head>` — additive markup an existing layout
// never emits itself (canonical / og / extra meta / JSON-LD; `<title>`
// and the description ride the synthesised page's own `Title` /
// `Description` fields through the layout exactly as a static page's
// do). Pages with no `head:*` frontmatter keys pay one map scan and are
// served byte-for-byte as pre-111 (GP 11 / GP 13).

/// Emits a `PageHeadMetadata`'s tags into a rendered HTML document.
/// Used by `PublicPageHandler` (request path, including the cached /
/// stale-while-revalidate renders) and `StaticExport` (build path) so
/// both serve identical documents.
module PageHeadInjection =

    /// True when `name` is an Open-Graph-family meta name that must be
    /// emitted with a `property=` attribute rather than `name=`.
    let private isPropertyName (name: string) =
        name.StartsWith "og:"
        || name.StartsWith "article:"
        || name.StartsWith "twitter:"

    /// Render the metadata's head tags as an HTML string. Attribute
    /// values are escaped by Giraffe.ViewEngine; JSON-LD payloads are
    /// emitted raw (trusted server-produced content).
    let tagsHtml (head: PageHeadMetadata) : string =
        let nodes = [
            match head.Canonical with
            | Some url -> link [ _rel "canonical"; _href url ]
            | None -> ()

            match head.OgImage with
            | Some img -> meta [ attr "property" "og:image"; _content img ]
            | None -> ()

            for name, content in head.Meta do
                if isPropertyName name then
                    meta [ attr "property" name; _content content ]
                else
                    meta [ _name name; _content content ]

            for payload in head.JsonLd do
                script [ _type "application/ld+json" ] [ rawText payload ]
        ]

        RenderView.AsString.htmlNodes nodes

    /// Insert the metadata's tags immediately before the document's
    /// first `</head>` (case-insensitive). A document with no `</head>`
    /// is returned unchanged — degrade safely rather than corrupt
    /// markup.
    let inject (head: PageHeadMetadata) (html: string) : string =
        let idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase)

        if idx < 0 then html else html.Insert(idx, tagsHtml head)

    /// Inject the page's `head:*` frontmatter envelope (written by the
    /// Phase 111 resolved-content synthesis) into the rendered document.
    /// Pages with no envelope return `html` untouched.
    let injectFromPage (page: PublicPage) (html: string) : string =
        match PageHeadMetadata.ofFrontmatter page.Frontmatter with
        | Some head -> inject head html
        | None -> html