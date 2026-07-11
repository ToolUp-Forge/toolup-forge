// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Narrative

open System

/// Severity hint for a `Callout` element. Renderers map severity to colour
/// and iconography; serialisers may map to prefixes like "WARNING:" or
/// markdown admonition blocks.
type Severity =
    | Info
    | Notice
    | Warning
    | Critical

/// Inline text run. Deliberately non-recursive: a `Paragraph` is a flat list
/// of spans, not a tree. `Metric` is the single concession to structure —
/// both consumers format labelled numeric values (`r = 0.42`, `γ = 1.04`)
/// frequently enough that renderers benefit from styling them distinctly.
///
/// `Metric` additionally carries an optional `factRef` (Phase 521) — the
/// content-addressed id of the fact this number is quoted from, so every
/// number in a narrative can be a live pointer into the fact base rather
/// than a copied value. `None` is the fact-free default: a metric span
/// without a ref renders byte-identically to before (GP 11) and costs a
/// fact-free deployment nothing (GP 13). When `Some`, renderers pass the
/// ref through as a machine-readable annotation (an HTML `data-fact`
/// attribute / a Markdown annotation comment); plaintext is unchanged
/// because it has no attribute channel. The ref is an opaque id string —
/// this file takes no dependency on the fact companion's types.
///
/// `Link` and `Image` extend the original analytics-shaped set with the
/// two inline primitives marketing-shape pages need (Phase 80). `Link`
/// carries its visible spans separately from its `href` so a link can
/// contain emphasised / coded / metric runs. `Image` carries mandatory
/// `alt` (accessibility + plaintext / RSS fallback) and an optional
/// `title` (the `title=` attribute on HTML output; the `"title"` field in
/// markdown `![alt](src "title")` form).
type InlineSpan =
    | Text of string
    | Emphasis of string
    | Strong of string
    | Metric of label: string * value: string * factRef: string option
    | Code of string
    | Link of href: string * spans: InlineSpan list
    | Image of src: string * alt: string * title: string option
    /// Hard line break inside a paragraph. Renders as `<br />` (HTML),
    /// trailing-two-spaces + newline (markdown), or newline (plaintext).
    /// Useful for addresses, multi-line callouts, and marketing copy
    /// where the visual break matters but a new paragraph would over-
    /// stress the prose hierarchy.
    | Br

/// Per-column horizontal alignment for `Table` cells. Renderers that can't
/// express alignment (e.g. Feliz text tables on narrow viewports) may ignore
/// the value, but markdown and aligned-plaintext renderers honour it.
type TableAlignment =
    | Left
    | Right
    | Center

// ─── Phase 87 — media + layout block specs ───────────────────────────
//
// The records below back the rich-content `NarrativeElement` cases
// (`Video`, `Audio`, `ImageGallery`, `Embed`, `Card`) added in Phase 87.
// They sit above `NarrativeElement` because the DU references them; the
// one record that must reference `NarrativeElement` back (`CardSpec`,
// whose body is itself a list of elements) is defined in the `and`
// chain with the DU below.
//
// Every record is an immutable value (GP 5), carries accessibility
// metadata (alt text / captions / track labels) first-class — mirroring
// the `IAssetStore` alt-text discipline (Phase 39) — and holds no
// server-only or framework type, so the whole set Fable-compiles into
// the client bundle.

/// One `<source>` for an adaptive-bitrate / format-fallback `Video` or
/// `Audio` element. `Src` is the media URL; `Type` is the MIME hint
/// emitted as `type="..."` (e.g. `"video/mp4"`, `"video/webm"`,
/// `"application/x-mpegURL"` for an HLS manifest) so the browser can
/// skip a source it can't decode. Sources render in declared order —
/// list the most-preferred / highest-fidelity encoding first.
type MediaSource = { Src: string; Type: string option }

/// A timed-text track (captions / subtitles / descriptions / chapters)
/// for a `Video` or `Audio` element. Maps directly to the HTML
/// `<track>` element. `Src` is the WebVTT URL; `Kind` is the
/// `kind="..."` attribute (`"captions"`, `"subtitles"`,
/// `"descriptions"`, `"chapters"`); `Label` is the human-readable name
/// shown in the player's track menu; `SrcLang` is the BCP-47 language
/// tag; `IsDefault` marks the track shown without explicit user action.
///
/// Captions are not optional polish: a media block with no caption
/// track is inaccessible, so the renderers surface every track they're
/// given and the plaintext renderer falls back to track labels when a
/// block carries no visible caption.
type MediaTrack = {
    Src: string
    Kind: string
    Label: string
    SrcLang: string option
    IsDefault: bool
}

/// Specification for a `Video` block. `Sources` is the ordered
/// adaptive-bitrate / fallback set (a single MP4 is the common case);
/// `Poster` is the still frame shown before playback; `Tracks` carries
/// captions / subtitles; `Caption` is the visible `<figcaption>` shown
/// beneath the player. `Caption` doubles as the plaintext / Markdown
/// degradation text, so author it as a meaningful description rather
/// than decorative chrome.
type VideoSpec = {
    Sources: MediaSource list
    Poster: string option
    Tracks: MediaTrack list
    Caption: string option
}

/// Specification for an `Audio` block. Same shape as `VideoSpec`
/// without a poster — `Sources` is the ordered fallback set, `Tracks`
/// carries transcripts / descriptions, `Caption` is the visible label
/// and the plaintext / Markdown degradation text.
type AudioSpec = {
    Sources: MediaSource list
    Tracks: MediaTrack list
    Caption: string option
}

/// One image inside an `ImageGallery`. `Src` is the display image;
/// `Alt` is mandatory accessibility text (the universally-portable
/// plaintext / RSS fallback); `Caption` is an optional visible
/// `<figcaption>`; `Href` is an optional full-resolution / lightbox
/// target (`None` means the lightbox, if the layout wires one, uses
/// `Src`). The HTML renderer emits lightbox class hooks; the layout's
/// own CSS / JS decides whether to activate them.
type ImageSpec = {
    Src: string
    Alt: string
    Caption: string option
    Href: string option
}

/// Specification for an `Embed` block — a third-party oEmbed provider
/// or sandboxed iframe (YouTube, Vimeo, a map, a CodePen). `Url` is the
/// iframe `src`; `Title` is the mandatory `title="..."` (accessibility)
/// and the visible link text of the safe placeholder shown when the
/// URL's origin is not on the renderer's allowlist; `AspectRatio` is an
/// optional `"16:9"` / `"4:3"`-style hint mapped to a CSS class hook
/// (`None` leaves sizing to the layout's CSS).
///
/// Embeds are CSP-sensitive: a renderer only emits the `<iframe>` when
/// the embed origin is on its configured allowlist, otherwise it
/// degrades to a plain link. The allowlist itself lives with the
/// rendering layer (the SDK's `NarrativeHtml.RenderOptions` /
/// `NarrativeLayout` component registry), not on the document — the
/// same document must render safely under any deployment's policy.
type EmbedSpec = {
    Url: string
    Title: string
    AspectRatio: string option
}

/// A single block-level element inside a section. `BulletList` and
/// `OrderedList` take `InlineSpan list list` — each inner list is the spans
/// of one bullet. `KeyValueGrid` is for labelled pairs; a Feliz renderer
/// can lay them out as a two-column grid, a markdown renderer as a
/// definition list. `Table` is a first-class tabular element: each column
/// has a header and alignment; each row is a list of cells; each cell is
/// a list of inline spans.
///
/// `Heading` carries a level (3 or 4 — the document's own title is the
/// implicit H1 and the section's `Heading` field is H2, so sub-section
/// headings inside a section start at H3) and inline spans. Added in
/// Phase 80 to give Narrative a path to long-form / marketing-shape
/// pages whose bodies want nested headings without breaking out of the
/// section model. Renderers clamp out-of-range levels to 3..6 so an
/// author who passes 5/6 keeps semantic HTML and an author who passes
/// 1/2 doesn't collide with the document or section heading.
type NarrativeElement =
    | Paragraph of InlineSpan list
    | Heading of level: int * spans: InlineSpan list
    | BulletList of InlineSpan list list
    | OrderedList of InlineSpan list list
    | KeyValueGrid of (string * InlineSpan list) list
    | Table of columns: (string * TableAlignment) list * rows: InlineSpan list list list
    | Callout of Severity * InlineSpan list
    /// Fenced code block. `language` is an optional syntax-highlighter
    /// hint surfaced as `class="language-fsharp"` in HTML and a
    /// ```` ```fsharp ```` fence in markdown. Distinct from `InlineSpan.Code`
    /// (which is a single-token inline run); use a `CodeBlock` whenever
    /// the content includes newlines or wants a fixed-width frame.
    | CodeBlock of language: string option * content: string
    /// Block quotation — testimonials, pull quotes, cited prose.
    /// Semantically distinct from `Callout` (severity-keyed advisories);
    /// `Blockquote` carries an optional `citation` (renders as `<cite>`
    /// in HTML, attribution prefix in markdown). Inline spans inside the
    /// quote can themselves use `Link` / `Emphasis` / `Metric` etc.
    | Blockquote of citation: string option * spans: InlineSpan list
    | Divider
    // ─── Phase 87 — media + layout blocks (additive; GP 11) ──────────
    // Every case below is appended, never an edit to an existing case,
    // so a renderer that predates Phase 87 leaves an older narrative
    // byte-for-byte unchanged. The blocks lift Narrative from
    // "analytical prose" to "CMS page body".
    /// Block-level video player. See `VideoSpec`. Renders `<figure>` +
    /// `<video>` with sources / poster / caption tracks in HTML;
    /// degrades to a poster image + source link in Markdown; degrades
    /// to caption / track-label text in plaintext.
    | Video of VideoSpec
    /// Block-level audio player. See `AudioSpec`.
    | Audio of AudioSpec
    /// A gallery of images with lightbox class hooks. Each `ImageSpec`
    /// carries mandatory alt text and an optional caption + lightbox
    /// href. Renders a `<figure>`-per-image grid in HTML, a list of
    /// `![alt](src)` images in Markdown, bracketed alt text in plaintext.
    | ImageGallery of ImageSpec list
    /// A third-party oEmbed / sandboxed iframe. See `EmbedSpec`. The
    /// renderer only emits the iframe when the embed origin is on its
    /// configured allowlist; an unknown origin degrades to a safe
    /// placeholder link (CSP discipline).
    | Embed of EmbedSpec
    /// A self-contained card — optional heading, optional lead image,
    /// and a recursively-nested body of elements. See `CardSpec`.
    | Card of CardSpec
    /// A vertical accordion: an ordered list of `(heading, body)`
    /// panels, each body a recursively-nested element list. Renders
    /// `<details>` / `<summary>` in HTML (no JS needed), heading +
    /// indented body in Markdown / plaintext.
    | Accordion of (string * NarrativeElement list) list
    /// A tab set: an ordered list of `(label, body)` panels, each body
    /// a recursively-nested element list. Renders an ARIA tablist in
    /// HTML; degrades to labelled sections in Markdown / plaintext
    /// (every panel visible — plaintext / Markdown have no interactive
    /// affordance, so hiding panels would drop content).
    | Tabs of (string * NarrativeElement list) list
    /// A deployment-defined custom block, resolved by `name` through a
    /// registered component-renderer map (`NarrativeHtml.RenderOptions`
    /// / `NarrativeLayout`). `props` is a flat string map — the wire is
    /// deliberately stringly-typed so the case stays Fable-safe and
    /// serialisable, and so a deployment can ship new blocks without
    /// forking this DU. An unregistered `name` degrades to a safe
    /// placeholder. This is the one sanctioned narrative type-erasure
    /// boundary (see toolup-forge/CLAUDE.md "Type erasure boundaries").
    | Component of name: string * props: Map<string, string>

/// Body of a `Card` block — an optional heading, an optional lead
/// image, and a recursively-nested list of elements. Defined in the
/// `and` chain because its `Body` field refers back to
/// `NarrativeElement`. Immutable (GP 5).
and CardSpec = {
    Heading: string option
    Image: ImageSpec option
    Body: NarrativeElement list
}

/// A document section with a stable Id (used as an anchor by renderers
/// that support linking), a Heading, and an optional Subheading.
type NarrativeSection = {
    Id: string
    Heading: string
    Subheading: string option
    Elements: NarrativeElement list
}

/// Provenance metadata attached server-side after generation. Identifies
/// the module and page that produced the document, when it was generated,
/// and the analysis settings that produced it — used by the Knowledge Base
/// to deduplicate stored narratives and by the UI to show "Save to KB".
///
/// `SettingsKey` is canonical and deterministic: a stable ordering of the
/// analysis inputs (not display options) collapsed into one string. Two
/// runs with the same inputs must produce the same `SettingsKey` so a
/// re-run can be detected as a duplicate of the earlier stored version.
///
/// `SettingsDisplay` is a human-readable rendering of the same inputs for
/// the UI: label / value pairs preserving source order.
type NarrativeProvenance = {
    ModuleId: string
    PageRoute: string option
    GeneratedAt: DateTimeOffset
    SettingsKey: string
    SettingsDisplay: (string * string) list
}

/// The top-level narrative document returned by a module's narrative
/// generator. Crosses the server/client boundary via ToolUp.Remoting.
///
/// `Provenance` is optional because pure narrative libraries produce
/// documents without knowing the request context. The composition root
/// (`Server.fs`) attaches provenance after generation, before the doc
/// is persisted or returned to the client.
type NarrativeDocument = {
    Title: string
    Subtitle: string option
    Sections: NarrativeSection list
    Provenance: NarrativeProvenance option
    /// BCP-47 language tag the document is authored in (e.g. `"en-GB"`,
    /// `"fr"`). Feeds `<html lang="...">` in SSR renders and Open Graph
    /// `og:locale`. `None` defers to the surrounding page's locale
    /// (analytical narratives within a single-locale deployment leave
    /// this unset).
    Lang: string option
    /// Canonical absolute URL the document should be indexed under.
    /// Drives `<link rel="canonical" href="...">` emitted by SSR
    /// layouts and the `og:url` Open Graph tag. Critical for any
    /// deployment that cross-publishes (the same document surfaced
    /// at multiple paths, syndicated to a partner site, or rendered
    /// both as HTML and as an Atom feed entry).
    CanonicalUrl: string option
}

/// Per-scope retention policy for `INarrativeStore` implementations.
/// Carried on `ServerConfig.NarrativeRetention` so deployments can bound
/// long-running scopes without dropping into store-specific configuration.
///
/// `MaxPerScope` caps the entry count per scope; once exceeded, the
/// oldest entries are evicted first. `None` disables the count cap (the
/// implementation may still apply a hard ceiling — the in-process stores
/// historically defaulted to 100).
///
/// `MaxAge` evicts entries whose `PublishedAt` is older than `now -
/// MaxAge`. `None` disables age-based eviction. Stores enforce age
/// eviction lazily on writes — a quiet scope retains old entries until
/// the next `Publish` runs the sweep.
type NarrativeRetentionPolicy = {
    MaxPerScope: int option
    MaxAge: TimeSpan option
}

module NarrativeRetentionPolicy =
    /// Default policy: cap at 100 entries per scope, no age limit.
    /// Matches the historical in-process behaviour (GP 11).
    let defaults: NarrativeRetentionPolicy = {
        MaxPerScope = Some 100
        MaxAge = None
    }

    /// Unbounded policy. Stores never evict on a count or age basis;
    /// long-running deployments must wipe scopes explicitly via
    /// `DeleteScope`. Use with caution — the persisted-store layer's
    /// per-scope blob list grows linearly under this policy.
    let unbounded: NarrativeRetentionPolicy = { MaxPerScope = None; MaxAge = None }