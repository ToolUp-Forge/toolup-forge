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

    /// Phase 152 — the per-page `<meta name="robots">` tag, read from the
    /// `robots` frontmatter key (e.g. `noindex`, `noindex,nofollow`,
    /// `noarchive`). `[]` when the key is absent or blank, so a page that
    /// declares nothing is byte-for-byte pre-152 (GP 11). This is the
    /// `<head>` half of the per-page robots directive; the `X-Robots-Tag`
    /// response header (the crawler-honoured, non-HTML-covering half) is set
    /// by the page handler. Audience-denied responses keep their
    /// unconditional `noindex` regardless of this key (GP 4).
    let robotsMetaTags (page: PublicPage) : XmlNode list =
        match page.Frontmatter.TryFind "robots" with
        | Some value when not (System.String.IsNullOrWhiteSpace value) -> [
            meta [ _name "robots"; _content (value.Trim()) ]
          ]
        | _ -> []

    // ─── Phase 154 — hreflang / rel=alternate multi-locale head ──────
    //
    // A multi-locale site emits `<link rel="alternate" hreflang="…">`
    // clusters so search engines serve the right-language URL and don't
    // treat translations as duplicates. These helpers are pure and
    // resolver-neutral (GP 13): the alternate set is supplied either by a
    // page frontmatter convention (the `hreflang` key) or computed by the
    // layout and passed to `alternates` directly — no i18n routing scheme
    // is baked into the SDK. Reciprocity (every alternate in a cluster
    // pointing back) is the consumer's responsibility — guidance, not
    // enforced.

    /// Emit one `<link rel="alternate" hreflang="{lang}" href="{url}">`
    /// per `(lang, url)` entry. An `x-default` cluster member is simply an
    /// entry whose lang is `"x-default"`. An empty list emits no tags
    /// (GP 11). Attribute values are escaped by Giraffe.ViewEngine.
    let alternates (entries: (string * string) list) : XmlNode list =
        entries
        |> List.map (fun (lang, url) -> link [ _rel "alternate"; attr "hreflang" lang; _href url ])

    /// Parse the `hreflang` frontmatter convention into a `(lang, url)`
    /// list. Format: comma- / semicolon- / newline-separated `lang=url`
    /// pairs, e.g. `en=https://x/a, fr=https://x/fr/a, x-default=https://x/a`.
    /// Entries with no `=`, or a blank lang / url, are dropped. Order is
    /// preserved (the authored order).
    let parseAlternates (raw: string) : (string * string) list =
        raw.Split([| ','; ';'; '\n' |])
        |> Array.map (fun s -> s.Trim())
        |> Array.filter (fun s -> s <> "")
        |> Array.choose (fun pair ->
            let idx = pair.IndexOf '='

            if idx <= 0 then
                None
            else
                let lang = pair.Substring(0, idx).Trim()
                let url = pair.Substring(idx + 1).Trim()
                if lang = "" || url = "" then None else Some(lang, url))
        |> Array.toList

    /// Read the `hreflang` frontmatter convention off a page → `(lang,
    /// url)` list (`[]` when the key is absent). The resolver-neutral
    /// source for the alternates cluster: a markdown author writes the
    /// pairs in frontmatter; a programmatic page sets the same key.
    let alternatesFromFrontmatter (page: PublicPage) : (string * string) list =
        match page.Frontmatter.TryFind "hreflang" with
        | Some raw -> parseAlternates raw
        | None -> []

    /// Build the `<head>` SEO block from a page. For a `Narrative` body:
    /// `<link rel="canonical">`, schema.org Article JSON-LD, and the
    /// OpenGraph + Twitter card meta tags. For ANY body: the per-page
    /// `<meta name="robots">` (Phase 152) when a `robots` frontmatter key is
    /// present, and the `<link rel="alternate" hreflang>` cluster (Phase
    /// 154) when a `hreflang` frontmatter key is present. Returns the list
    /// of `XmlNode`s the caller splices into `head [ ... ]`. A page with no
    /// `robots` / `hreflang` keys and a non-`Narrative` body returns an
    /// empty list (byte-for-byte pre-152) so the caller can unconditionally
    /// `yield! NarrativeLayout.headTags`.
    let headTags (page: PublicPage) : XmlNode list =
        // Phase 152 — robots directive applies to every body kind; absent
        // key → no tag (GP 11).
        let robots = robotsMetaTags page

        // Phase 154 — hreflang alternates from the frontmatter convention;
        // absent key → no tags (GP 11).
        let alts = alternates (alternatesFromFrontmatter page)

        let bodyTags =
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

        robots @ alts @ bodyTags

    // ─── Phase 148 — self-referencing canonical for every body kind ──
    //
    // `headTags` above emits a `<link rel="canonical">` only for a
    // `Narrative` body carrying an explicit `CanonicalUrl`; `Markdown` /
    // `Html` bodies (and `Narrative` bodies without one) get none. A
    // self-referencing canonical (`baseUrl` + slug) is the cheapest
    // defence against duplicate-content dilution from query-string
    // variants. These helpers build it for ANY body kind; an explicit
    // canonical always wins (GP 11).

    /// Normalise a base URL for canonical / alternate composition: drop
    /// any trailing slash so `baseUrl + "/" + slug` never doubles it.
    /// Mirrors `SitemapGenerator`'s base-URL handling.
    let internal normaliseBaseUrl (baseUrl: string) = baseUrl.TrimEnd('/')

    /// The absolute self-referencing canonical URL for a page: the
    /// (trailing-slash-normalised) `baseUrl` joined to the page slug. The
    /// root / `index` slug canonicalises to `baseUrl/`.
    let canonicalUrlFor (baseUrl: string) (page: PublicPage) : string =
        let b = normaliseBaseUrl baseUrl
        let slug = (Slug.value page.Slug).Trim('/')

        if slug = "" || slug = "index" then
            b + "/"
        else
            b + "/" + slug

    /// A self-referencing `<link rel="canonical">` for any body kind,
    /// resolving to the page's own absolute URL under `baseUrl`.
    let canonicalFor (baseUrl: string) (page: PublicPage) : XmlNode =
        link [ _rel "canonical"; _href (canonicalUrlFor baseUrl page) ]

    /// Whether the page already declares an explicit canonical — a
    /// `Narrative` `CanonicalUrl`, or a `head:canonical` frontmatter
    /// envelope (Phase 111). The self-referencing canonical defers to it
    /// so a page is never double-canonicalised (GP 11).
    let hasExplicitCanonical (page: PublicPage) : bool =
        (match page.Body with
         | Narrative doc -> doc.CanonicalUrl |> Option.isSome
         | _ -> false)
        || page.Frontmatter.ContainsKey "head:canonical"

    /// `headTags` with an optional self-referencing canonical for layouts
    /// that drive their head off this helper and want the Phase 148 tag
    /// without the compose-level `withSelfCanonical` injection. When
    /// `selfCanonicalBaseUrl` is `Some b` and the page declares no
    /// explicit canonical, a self-referencing `<link rel="canonical">`
    /// (origin `b`) is prepended to the standard `headTags` output;
    /// otherwise the output is exactly `headTags page` (GP 11).
    let headTagsWith (selfCanonicalBaseUrl: string option) (page: PublicPage) : XmlNode list =
        let selfCanonical =
            match selfCanonicalBaseUrl with
            | Some b when not (hasExplicitCanonical page) -> [ canonicalFor b page ]
            | _ -> []

        selfCanonical @ headTags page

    /// Phase 148 — self-referencing canonical injection over the content
    /// API. `PublicRenderingServerApp.withSelfCanonical` wraps the
    /// resolved `IPublicContentApi` so every resolved page that declares
    /// no explicit canonical gains a `head:canonical` frontmatter key (the
    /// Phase 111 envelope) — which the handler's existing
    /// `PageHeadInjection` step emits before `</head>`. A self-referencing
    /// canonical thus reaches the wire WITHOUT editing any layout, for all
    /// body kinds. The origin is the site's own base URL, so the compose
    /// passes each satellite's `BaseUrl` (Phase 145) and a page served on
    /// a satellite host self-canonicalises to that host.
    module SelfCanonical =
        /// Add a `head:canonical` frontmatter key to a page that declares
        /// no explicit canonical; pages already carrying one are returned
        /// unchanged (explicit wins, GP 11).
        let enrichPage (baseUrl: string) (page: PublicPage) : PublicPage =
            if hasExplicitCanonical page then
                page
            else
                {
                    page with
                        Frontmatter = page.Frontmatter |> Map.add "head:canonical" (canonicalUrlFor baseUrl page)
                }

        /// Wrap an `IPublicContentApi` so single-page resolutions carry a
        /// self-referencing canonical. `ListPages` / `ListPagesPublic` /
        /// `GetCollection` pass through unchanged — sitemap / listing
        /// surfaces need no per-entry canonical and must stay
        /// byte-for-byte (GP 11).
        let wrap (baseUrl: string) (inner: IPublicContentApi) : IPublicContentApi =
            { new IPublicContentApi with
                member _.GetPage slug = async {
                    let! p = inner.GetPage slug
                    return p |> Option.map (enrichPage baseUrl)
                }

                member _.ListPages prefix = inner.ListPages prefix

                // Phase 632 — delegate rather than re-gate, so a decorated
                // impl whose store pushes the predicate down keeps doing
                // so.
                member _.ListPagesPublic(now, prefix) = inner.ListPagesPublic(now, prefix)

                member _.GetCollection collectionId = inner.GetCollection collectionId

                member _.GetPageInContext(slug, ctx) = async {
                    let! p = inner.GetPageInContext(slug, ctx)
                    return p |> Option.map (enrichPage baseUrl)
                }
            }

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