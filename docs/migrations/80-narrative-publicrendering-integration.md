# Phase 80 — Narrative ⊕ PublicRendering integration

**Status:** Shipped.
**Scope:** Schema-breaking extension to the Narrative package; new `ContentBody.Narrative` variant in PublicRendering; new layout / prerender / structured-data / RSS helpers; new Atom renderer.

## What changes

### 1. `InlineSpan` (BREAKING)

Adds three cases:

| New case | Shape | Purpose |
|---|---|---|
| `Link` | `Link of href: string * spans: InlineSpan list` | Inline anchor. `spans` are the visible content (so a link can wrap emphasised / coded / metric runs); `href` is the URL. |
| `Image` | `Image of src: string * alt: string * title: string option` | Inline image. `alt` is mandatory (plaintext / RSS / a11y fallback). |
| `Br` | `Br` | Hard line break inside a paragraph or bullet. |

Every existing pattern-match on `InlineSpan` (including external `INarrativeRenderer` implementations) gains an incomplete-match warning until the new cases are handled.

### 2. `NarrativeElement` (BREAKING)

Adds three cases:

| New case | Shape | Purpose |
|---|---|---|
| `Heading` | `Heading of level: int * spans: InlineSpan list` | Sub-section heading (H3–H6 — the document title is implicit H1, the section heading is H2). Levels are clamped to 3..6. |
| `CodeBlock` | `CodeBlock of language: string option * content: string` | Fenced code block. `language` is an optional syntax-highlighter hint surfaced as `class="language-fsharp"` in HTML and a ```` ```fsharp ```` fence in markdown. |
| `Blockquote` | `Blockquote of citation: string option * spans: InlineSpan list` | Block quotation (testimonials / pull quotes). Semantically distinct from `Callout` (severity-keyed advisories). |

⚠️ `Blockquote` collides with `NarrativeMarkdown.AdmonitionStyle.Blockquote` in scope. Inside `NarrativeMarkdown.fs`, the inner match on `AdmonitionStyle` now qualifies as `| AdmonitionStyle.Blockquote ->` and the outer match qualifies as `| NarrativeElement.Blockquote(citation, spans) ->`. Consumers do not need to update their own match patterns — the qualification is only required where both types are in scope.

### 3. `NarrativeDocument` (BREAKING)

Adds two optional fields:

| New field | Type | Purpose |
|---|---|---|
| `Lang` | `string option` | BCP-47 language tag (`"en-GB"`, `"fr"`). Drives `<html lang="...">` and `og:locale`. |
| `CanonicalUrl` | `string option` | Canonical absolute URL. Drives `<link rel="canonical">`, `og:url`, and Atom `<link rel="alternate">`. |

Every record-literal construction of `NarrativeDocument` (`{ Title = ...; Subtitle = ...; Sections = ...; Provenance = ... }`) must add the two new fields:

```fsharp
let sectionDoc: NarrativeDocument = {
    Title = e.Document.Title
    Subtitle = e.Document.Subtitle
    Sections = [ section ]
    Provenance = e.Document.Provenance
    Lang = e.Document.Lang             // NEW
    CanonicalUrl = e.Document.CanonicalUrl  // NEW
}
```

Internal SDK consumers ([`NarrativeTools.fs`](../../src/ToolUp.AI.Server/Server/NarrativeTools.fs), [`KnowledgeBase/Server/Api/Narrative.fs`](../../src/ToolUp.KnowledgeBase.Server/Server/Api/Narrative.fs)) are updated as part of this phase.

`Narrative.create` in [`NarrativeBuilder.fs`](../../src/ToolUp.Platform.Core/Shared/NarrativeBuilder.fs) initialises both fields to `None`, so consumers using the builder pipeline are unaffected. Two new pipeline helpers:

```fsharp
let doc =
    Narrative.create "Quarterly outlook"
    |> Narrative.subtitle "Q3 FY26"
    |> Narrative.withLang "en-GB"
    |> Narrative.withCanonicalUrl "https://example.com/q3-fy26"
    |> Narrative.section ...
```

### 4. `ContentBody` (BREAKING, PublicRendering only)

Adds `Narrative of NarrativeDocument`:

```fsharp
type ContentBody =
    | Markdown of source: string
    | Html of fragment: string
    | Narrative of document: NarrativeDocument   // NEW
```

Pages whose body is a typed `NarrativeDocument` — programmatic pages, AI-emitted pages, analytical posts whose body uses the Narrative element set natively — now have a first-class path through the PublicRendering pipeline. Layouts inspect `Body` and dispatch.

## New surface

### Pure rendering

- **[`NarrativeHtml.RenderOptions`](../../src/ToolUp.Platform.Core/Shared/NarrativeHtml.fs)** — `ImageLoading` (Eager / Lazy → emits `loading="lazy"`), `ExternalLinkRel` (auto-applied `rel="noopener nofollow"` shape for absolute-URL links), `HeadingLevelOffset` (for nested embedding), `EmitSectionAnchors` (toggle the `<section>` wrapper). Defaults preserve byte-for-byte historical output (GP 11). Use `NarrativeHtml.renderWith options doc`; `NarrativeHtml.render doc` still calls defaults.
- **[`NarrativeHtml.tableOfContents`](../../src/ToolUp.Platform.Core/Shared/NarrativeHtml.fs)** — `<nav class="narrative-toc">` listing sections (and optionally H3/H4 headings within them) as anchor links to `section.Id`. Pure string output.
- **[`NarrativeAtom`](../../src/ToolUp.Platform.Core/Shared/NarrativeAtom.fs)** — Atom 1.0 renderer. `renderEntry doc` for a single `<entry>`, `renderFeed feedTitle feedSelfUrl feedAlternateUrl docs` for a complete `<feed>`. Registered in [`NarrativeRenderers.defaults`](../../src/ToolUp.Platform.Core/Shared/NarrativeRenderers.fs) under `application/atom+xml`.

### PublicRendering layout helpers

- **[`NarrativeLayout.renderBody`](../../src/ToolUp.PublicRendering/Server/NarrativeLayout.fs)** — projects a `PublicPage` body into a Giraffe.ViewEngine `XmlNode`. Markdown / Html bodies pass through; Narrative bodies render through `NarrativeHtml.render`.
- **`NarrativeLayout.articleJsonLd`** — schema.org Article JSON-LD for the page (Some when body is Narrative + has Provenance).
- **`NarrativeLayout.prerenderMeta`** — `PrerenderMeta` derived from the Narrative body, ready for `ClientConfig.PrerenderRoutes`.
- **`NarrativeLayout.tableOfContents`** — `<nav>` ToC as an `XmlNode`. None for non-Narrative bodies.
- **`NarrativeLayout.headTags`** — bundled `<head>` SEO fragment: `<link rel="canonical">`, JSON-LD, Open Graph, Twitter card. Empty list for non-Narrative bodies, so layouts can unconditionally `yield! NarrativeLayout.headTags page` inside `head [ ... ]`.

### Structured-data helpers (extends [`StructuredDataHelpers.fs`](../../src/ToolUp.PublicRendering/Server/StructuredDataHelpers.fs))

- **`articleFromNarrative page doc`** — schema.org Article JSON-LD blob (string option).
- **`openGraphFromNarrative page doc`** — `(property, content) list` of Open Graph meta tags.
- **`twitterCardFromNarrative page doc`** — `(name, content) list` of Twitter card meta tags.

### Prerender bridge

- **[`NarrativePrerender.fromDocument`](../../src/ToolUp.PublicRendering/Server/NarrativePrerender.fs)** — `NarrativeDocument → PrerenderMeta` (uses synthesised page shape).
- **`NarrativePrerender.fromPage page doc`** — frontmatter-aware variant.

## Diff to apply (consumer side)

### A. Pattern-matches on `InlineSpan` / `NarrativeElement`

Add cases for the new variants. Example for a custom `INarrativeRenderer`:

```fsharp
let private renderSpan (span: InlineSpan) =
    match span with
    | Text t -> ...
    | Emphasis t -> ...
    | Strong t -> ...
    | Metric(label, value) -> ...
    | Code t -> ...
    // NEW cases:
    | Link(href, spans) -> ...
    | Image(src, alt, title) -> ...
    | Br -> ...

let private renderElement = function
    | Paragraph spans -> ...
    | Heading(level, spans) -> ...           // NEW
    | BulletList items -> ...
    | OrderedList items -> ...
    | KeyValueGrid pairs -> ...
    | Table(columns, rows) -> ...
    | Callout(severity, spans) -> ...
    | CodeBlock(language, content) -> ...    // NEW
    | Blockquote(citation, spans) -> ...     // NEW
    | Divider -> ...
```

### B. Record-literal `NarrativeDocument` constructors

Add the two new fields (typically `Lang = None; CanonicalUrl = None` for analytical narratives that don't need them):

```fsharp
let doc: NarrativeDocument = {
    Title = ...
    Subtitle = ...
    Sections = [...]
    Provenance = ...
    Lang = None              // NEW
    CanonicalUrl = None      // NEW
}
```

Consumers using `Narrative.create |> Narrative.section ...` need no change.

### C. Layout authors adopting Narrative-bodied pages (additive)

```fsharp
let myLayout (page: PublicPage) : XmlNode =
    html [ _lang (page.Body |> function | Narrative d -> d.Lang |> Option.defaultValue "en" | _ -> "en") ] [
        head [] [
            title [] [ encodedText page.Title ]
            yield! NarrativeLayout.headTags page   // canonical + JSON-LD + OG + Twitter
            // ... your own <head> additions
        ]
        body [] [
            // ... your own header / nav
            NarrativeLayout.renderBody page
            match NarrativeLayout.tableOfContents false page with
            | Some toc -> aside [ _class "toc" ] [ toc ]
            | None -> rawText ""
            // ... your own footer
        ]
    ]
```

## Verification

1. **`dotnet build ToolUp.Forge.sln`** — clean. All four core renderers (HTML / Markdown / Plaintext / Feliz client) + Atom renderer + internal consumers (AI tool registry, KB ingestion) compile.
2. **Byte-for-byte parity check** — render a pre-Phase-80 `NarrativeDocument` (no Link / Image / Br / Heading / CodeBlock / Blockquote / Lang / CanonicalUrl) through `NarrativeHtml.render` / `NarrativeMarkdown.render` / `NarrativePlaintext.render`. Output must match the pre-Phase-80 byte-for-byte (GP 11).
3. **PublicRendering layout smoke test** — a `PublicPage` with `Body = Narrative doc` rendered through a layout calling `NarrativeLayout.renderBody` produces an `<article>` with the document content; `NarrativeLayout.headTags` adds canonical + OG + Twitter + JSON-LD when provenance is present.
4. **Atom feed verification** — `NarrativeAtom.renderFeed` produces a parseable Atom 1.0 document (verify with `xmllint --noout feed.xml` or a feed validator).

## Rollback

Revert the commit. The schema additions are all backwards-incompatible — there is no graceful runtime rollback because record-literal constructors and pattern-matches against the extended types will fail to compile against the pre-Phase-80 shape. If a downstream consumer cannot adopt the schema bump on its own timeline, pin to the prior forge SDK version (`ToolUp.Sdk.Version` prior to this phase's release) until they catch up.

## Why this lands together

The five schema additions (Link / Image / Heading / CodeBlock / Blockquote / Br on InlineSpan-and-Element + Lang / CanonicalUrl on Document) all require the same migration shape — every external `INarrativeRenderer` impl recompiles, every record-literal constructor recompiles. Bundling them as one phase costs one migration doc + one adoption-matrix row + one consumer-side audit. Splitting them into N phases pays N×each.

The PublicRendering integration (ContentBody.Narrative variant + layout helpers + Atom renderer + RenderOptions + ToC + OG/Twitter helpers + prerender bridge) is the user-facing capability that motivates the schema changes — together they make `NarrativeDocument` a credible marketing-page primitive, programmatic page source, and AI-emitted-page target, not just an analytics output shape.
