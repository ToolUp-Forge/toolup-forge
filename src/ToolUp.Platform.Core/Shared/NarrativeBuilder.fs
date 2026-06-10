// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Fluent helpers for assembling a `NarrativeDocument` without spelling
/// out every record field. The underlying type is unchanged — these
/// helpers compose values into the existing shape so authors can mix and
/// match builder-style and record-literal style freely.
///
/// ```fsharp
/// open ToolUp.Platform.Narrative
///
/// let doc =
///     Narrative.create "Sales analysis"
///     |> Narrative.subtitle "Q3 FY26"
///     |> Narrative.section "Headline" "headline" [
///         Narrative.paragraph [ Narrative.text "Revenue up "; Narrative.strong "12%" ]
///         Narrative.callout Warning [ Narrative.text "One outlier excluded." ]
///     ]
///     |> Narrative.section "Detail" "detail" [
///         Narrative.bullets [
///             [ Narrative.text "North region above plan" ]
///             [ Narrative.text "South region flat" ]
///         ]
///     ]
/// ```
module ToolUp.Platform.Narrative.Narrative

open System

// ─── Inline span constructors ────────────────────────────────────

let text (s: string) : InlineSpan = Text s
let emphasis (s: string) : InlineSpan = Emphasis s
let strong (s: string) : InlineSpan = Strong s
let metric (label: string) (value: string) : InlineSpan = Metric(label, value)
let code (s: string) : InlineSpan = Code s

/// Inline link. `spans` are the visible content of the link (so a link
/// can wrap emphasised / coded runs); `href` is the URL the link points
/// to. `Narrative.link "https://..." [ Narrative.text "see docs" ]`.
let link (href: string) (spans: InlineSpan list) : InlineSpan = Link(href, spans)

/// Convenience: a link whose visible content is a single text span.
/// `Narrative.linkText "see docs" "https://..."`.
let linkText (label: string) (href: string) : InlineSpan = Link(href, [ Text label ])

/// Inline image. `alt` is mandatory — fallback for plaintext / RSS /
/// screen-reader contexts; renderers that cannot show images emit the
/// alt text in brackets. `title` is an optional hover hint surfaced in
/// HTML's `title=` attribute and markdown's `![alt](src "title")` form.
let image (src: string) (alt: string) : InlineSpan = Image(src, alt, None)

/// Image with an optional `title` hint.
let imageWith (src: string) (alt: string) (title: string) : InlineSpan = Image(src, alt, Some title)

// ─── Element constructors ───────────────────────────────────────

let paragraph (spans: InlineSpan list) : NarrativeElement = Paragraph spans

/// Sub-section heading inside the current section. `level` is the HTML
/// heading level — pass 3 or 4 for the common case (sections are H2; the
/// document title is H1). Out-of-range levels are clamped to 3..6 by
/// every renderer so a misplaced value can't produce illegal HTML.
let heading (level: int) (spans: InlineSpan list) : NarrativeElement = Heading(level, spans)

/// Convenience: H3 heading with a single text span. The most common
/// long-form-page shape.
let h3 (label: string) : NarrativeElement = Heading(3, [ Text label ])

/// Convenience: H4 heading with a single text span.
let h4 (label: string) : NarrativeElement = Heading(4, [ Text label ])

let bullets (items: InlineSpan list list) : NarrativeElement = BulletList items

let ordered (items: InlineSpan list list) : NarrativeElement = OrderedList items

let keyValues (pairs: (string * InlineSpan list) list) : NarrativeElement = KeyValueGrid pairs

let table (columns: (string * TableAlignment) list) (rows: InlineSpan list list list) : NarrativeElement =
    Table(columns, rows)

let callout (severity: Severity) (spans: InlineSpan list) : NarrativeElement = Callout(severity, spans)

/// Hard line break inside a paragraph or bullet.
let br: InlineSpan = Br

/// Fenced code block with an optional language hint.
/// `Narrative.codeBlock (Some "fsharp") "let x = 1"`.
let codeBlock (language: string option) (content: string) : NarrativeElement = CodeBlock(language, content)

/// Untagged fenced code block. Equivalent to `codeBlock None`.
let codeBlockOf (content: string) : NarrativeElement = CodeBlock(None, content)

/// Block quotation with an optional citation. The citation renders as
/// `<cite>...</cite>` in HTML and as a `— citation` attribution line
/// in markdown / plaintext.
let blockquote (citation: string option) (spans: InlineSpan list) : NarrativeElement = Blockquote(citation, spans)

let divider: NarrativeElement = Divider

// ─── Phase 87 — media + layout block constructors ───────────────

/// A media `<source>` with an optional MIME type hint.
let mediaSource (src: string) (mimeType: string option) : MediaSource = { Src = src; Type = mimeType }

/// A timed-text track. `kind` is `"captions"` / `"subtitles"` /
/// `"descriptions"` / `"chapters"`; `label` is the player-menu name.
let mediaTrack (kind: string) (src: string) (label: string) : MediaTrack = {
    Src = src
    Kind = kind
    Label = label
    SrcLang = None
    IsDefault = false
}

/// A `Video` block from an explicit spec.
let video (spec: VideoSpec) : NarrativeElement = Video spec

/// Convenience: a single-source `Video` with no poster / tracks. The
/// caption doubles as the accessible description + plaintext fallback.
let videoOf (src: string) (mimeType: string option) (caption: string option) : NarrativeElement =
    Video {
        Sources = [ { Src = src; Type = mimeType } ]
        Poster = None
        Tracks = []
        Caption = caption
    }

/// An `Audio` block from an explicit spec.
let audio (spec: AudioSpec) : NarrativeElement = Audio spec

/// Convenience: a single-source `Audio` with no tracks.
let audioOf (src: string) (mimeType: string option) (caption: string option) : NarrativeElement =
    Audio {
        Sources = [ { Src = src; Type = mimeType } ]
        Tracks = []
        Caption = caption
    }

/// A gallery image. `alt` is mandatory accessibility text; caption and
/// lightbox href are optional.
let galleryImage (src: string) (alt: string) : ImageSpec = {
    Src = src
    Alt = alt
    Caption = None
    Href = None
}

/// An `ImageGallery` block from a list of image specs.
let imageGallery (images: ImageSpec list) : NarrativeElement = ImageGallery images

/// An `Embed` block. `title` is mandatory (iframe `title=` +
/// placeholder link text); aspect ratio is an optional `"16:9"` hint.
let embed (url: string) (title: string) (aspectRatio: string option) : NarrativeElement =
    Embed {
        Url = url
        Title = title
        AspectRatio = aspectRatio
    }

/// A `Card` block from an explicit spec.
let card (spec: CardSpec) : NarrativeElement = Card spec

/// Convenience: a `Card` with an optional heading and a body, no image.
let cardOf (heading: string option) (body: NarrativeElement list) : NarrativeElement =
    Card {
        Heading = heading
        Image = None
        Body = body
    }

/// An `Accordion` block — ordered `(heading, body)` panels.
let accordion (panels: (string * NarrativeElement list) list) : NarrativeElement = Accordion panels

/// A `Tabs` block — ordered `(label, body)` panels.
let tabs (panels: (string * NarrativeElement list) list) : NarrativeElement = Tabs panels

/// A generic `Component` block resolved by name through the rendering
/// layer's registered component-renderer map. `props` is a flat string
/// map; an unregistered name degrades to a safe placeholder.
let component' (name: string) (props: Map<string, string>) : NarrativeElement = Component(name, props)

// ─── Document constructors ──────────────────────────────────────

/// Create an empty document with the given title. Sections are added via
/// `Narrative.section`; subtitle via `Narrative.subtitle`; provenance via
/// `Narrative.withProvenance`.
let create (title: string) : NarrativeDocument = {
    Title = title
    Subtitle = None
    Sections = []
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

/// Set the document's subtitle.
let subtitle (s: string) (doc: NarrativeDocument) : NarrativeDocument = { doc with Subtitle = Some s }

/// Set the document's BCP-47 language tag (`"en-GB"`, `"fr"`, etc.).
/// Drives `<html lang="...">` and `og:locale` on SSR-rendered pages.
let withLang (tag: string) (doc: NarrativeDocument) : NarrativeDocument = { doc with Lang = Some tag }

/// Set the document's canonical absolute URL. Renders as
/// `<link rel="canonical" href="...">` and `og:url`.
let withCanonicalUrl (url: string) (doc: NarrativeDocument) : NarrativeDocument = { doc with CanonicalUrl = Some url }

/// Append a section to the document. `id` is the stable anchor used by
/// renderers that support deep-linking; `heading` is the human-visible
/// title. Use `sectionWith` to add a subheading.
let section
    (heading: string)
    (id: string)
    (elements: NarrativeElement list)
    (doc: NarrativeDocument)
    : NarrativeDocument =
    let s = {
        Id = id
        Heading = heading
        Subheading = None
        Elements = elements
    }

    {
        doc with
            Sections = doc.Sections @ [ s ]
    }

/// Append a section with a subheading.
let sectionWith
    (heading: string)
    (id: string)
    (subheading: string)
    (elements: NarrativeElement list)
    (doc: NarrativeDocument)
    : NarrativeDocument =
    let s = {
        Id = id
        Heading = heading
        Subheading = Some subheading
        Elements = elements
    }

    {
        doc with
            Sections = doc.Sections @ [ s ]
    }

/// Stamp provenance directly. The composition-root helper
/// `NarrativePublisher.publishWithProvenance` does the same thing at
/// publish time; use this when you need the stamp before handing the
/// document somewhere else.
let withProvenance
    (moduleId: string)
    (pageRoute: string option)
    (settingsKey: string)
    (settingsDisplay: (string * string) list)
    (doc: NarrativeDocument)
    : NarrativeDocument =
    {
        doc with
            Provenance =
                Some {
                    ModuleId = moduleId
                    PageRoute = pageRoute
                    GeneratedAt = DateTimeOffset.UtcNow
                    SettingsKey = settingsKey
                    SettingsDisplay = settingsDisplay
                }
    }