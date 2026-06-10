# Narrative elements — the block vocabulary

A `NarrativeDocument` (in `ToolUp.Platform.Core`, namespace
`ToolUp.Platform.Narrative`) is the SDK's structured page-body model: a
title, optional subtitle, and a list of `NarrativeSection`s, each a list
of block-level `NarrativeElement`s. The same document renders to HTML
([`NarrativeHtml`](../../src/ToolUp.Platform.Core/Shared/NarrativeHtml.fs)),
Markdown
([`NarrativeMarkdown`](../../src/ToolUp.Platform.Core/Shared/NarrativeMarkdown.fs)),
plaintext
([`NarrativePlaintext`](../../src/ToolUp.Platform.Core/Shared/NarrativePlaintext.fs)),
Atom
([`NarrativeAtom`](../../src/ToolUp.Platform.Core/Shared/NarrativeAtom.fs)),
and a Feliz client tree
([`NarrativeRenderer`](../../src/ToolUp.Platform.Client/Client/NarrativeRenderer.fs)) —
every renderer is pure (`NarrativeDocument -> string`, or `-> ReactElement`
on the client), deterministic, and prerender-safe.

The element set has two layers: the original **analytical-prose** blocks
(Phase 80) and the **media + layout** blocks added in Phase 87 that lift
Narrative from "analytical prose" to "CMS page body". This page is the
block reference for both, with emphasis on the rich-content additions.

## Authoring

Build documents with record literals or the fluent
[`Narrative`](../../src/ToolUp.Platform.Core/Shared/NarrativeBuilder.fs)
builder module (smart constructors for every element). Example:

```fsharp
open ToolUp.Platform.Narrative

let doc =
    Narrative.create "Launch recap"
    |> Narrative.section "intro" "Highlights" [
        Narrative.paragraph [ Narrative.text "A strong quarter." ]
        Narrative.videoOf "/media/recap.mp4" (Some "video/mp4") (Some "Quarterly recap")
        Narrative.imageGallery [
            { Narrative.galleryImage "/img/a.jpg" "Stage shot" with Caption = Some "On stage" }
        ]
        Narrative.accordion [
            "What shipped", [ Narrative.paragraph [ Narrative.text "Three features." ] ]
        ]
    ]
```

## Analytical-prose blocks (Phase 80)

| Element | HTML | Markdown | Plaintext |
|---|---|---|---|
| `Paragraph of InlineSpan list` | `<p>` | paragraph | line |
| `Heading of level * spans` | `<h3>`..`<h6>` (level clamped 3–6) | `###`.. | underlined / indented |
| `BulletList` / `OrderedList` | `<ul>` / `<ol>` | `-` / `1.` | `  - ` / `  1. ` |
| `KeyValueGrid` | `<dl>` | `- **k:** v` | aligned `k: v` |
| `Table of columns * rows` | `<table>` | pipe table | aligned columns |
| `Callout of Severity * spans` | `<aside>` | blockquote / `:::` directive | `[SEVERITY] ` |
| `CodeBlock of lang option * content` | `<pre><code>` | fenced ` ``` ` | 4-space indent |
| `Blockquote of citation option * spans` | `<blockquote>`+`<cite>` | `> ` + `— cite` | indented + `— cite` |
| `Divider` | `<hr />` | `---` | `---` |

Inline runs (`InlineSpan`): `Text`, `Emphasis`, `Strong`, `Metric`,
`Code`, `Link`, `Image`, `Br`.

## Media + layout blocks (Phase 87)

All Phase 87 cases are **additive DU cases** (GP 11): a renderer that
predates them leaves an older document byte-for-byte unchanged, and the
shipped renderers handle every case. Specs carry accessibility metadata
(alt text / captions / track labels) first-class.

### `Video of VideoSpec` / `Audio of AudioSpec`

```fsharp
type MediaSource = { Src: string; Type: string option }              // <source src type>
type MediaTrack =                                                     // <track>
    { Src: string; Kind: string; Label: string; SrcLang: string option; IsDefault: bool }
type VideoSpec = { Sources: MediaSource list; Poster: string option; Tracks: MediaTrack list; Caption: string option }
type AudioSpec = { Sources: MediaSource list; Tracks: MediaTrack list; Caption: string option }
```

- **HTML** — `<figure class="narrative-video">` wrapping a `<video controls>`
  (with `poster=` when set) / `<audio controls>`, one `<source>` per
  `MediaSource` (declared order = preference order for adaptive bitrate /
  format fallback), one `<track>` per `MediaTrack`, and a `<figcaption>`
  when `Caption` is set.
- **Markdown** — degrades to the poster image (`![caption](poster)`) plus
  a link to the first source (`[▶ caption](src)`); audio degrades to a
  source link.
- **Plaintext** — caption only: `[Video: caption]` / `[Audio: caption]`.

The `Video` element is the surface [Phase 88's `IMediaLibrary`](#) serves
into (range-streaming, signed URLs).

### `ImageGallery of ImageSpec list`

```fsharp
type ImageSpec = { Src: string; Alt: string; Caption: string option; Href: string option }
```

- **HTML** — `<div class="narrative-gallery">` of `<figure>` items, each
  image wrapped in `<a class="narrative-gallery__lightbox" href="…">`
  (href falls back to `Src` when no `Href`) — a lightbox class hook the
  layout's CSS / JS activates; a no-JS reader still opens the image.
  `Alt` is mandatory and always emitted.
- **Markdown** — one `![alt](src)` per image, caption as `_italic_`.
- **Plaintext** — `[alt]` per image, `[alt] — caption` when captioned.

### `Embed of EmbedSpec` — CSP-aware

```fsharp
type EmbedSpec = { Url: string; Title: string; AspectRatio: string option }
```

An embed only renders as an `<iframe>` when its URL **origin**
(`scheme://host[:port]`) is on the renderer's allowlist; any other
origin (or a malformed URL) degrades to a safe placeholder link. The
allowlist lives with the **rendering layer**, never on the document, so
the same document renders safely under any deployment's policy:

- `NarrativeHtml.RenderOptions.AllowedEmbedOrigins: Set<string>` —
  defaults to the **empty set** (deny-all, secure-by-default). Add
  origins like `"https://www.youtube.com"`, `"https://player.vimeo.com"`.
- In PublicRendering, build options via
  [`NarrativeLayout.richRenderOptions`](../../src/ToolUp.PublicRendering/Server/NarrativeLayout.fs)
  and render with `NarrativeLayout.renderBodyWith`.

The emitted iframe carries `sandbox`, `referrerpolicy`, `loading="lazy"`,
and an `AspectRatio` class hook (`"16:9"` → `narrative-embed--16-9`).
Markdown and plaintext always degrade an embed to a link (`[title](url)`
/ `[Embed: title] (url)`).

### Layout blocks — `Card`, `Accordion`, `Tabs`

```fsharp
type CardSpec = { Heading: string option; Image: ImageSpec option; Body: NarrativeElement list }
//  | Card of CardSpec
//  | Accordion of (string * NarrativeElement list) list   // (heading, body)
//  | Tabs of (string * NarrativeElement list) list        // (label, body)
```

Bodies are **recursively-nested** `NarrativeElement` lists — blocks
contain blocks (a card inside an accordion inside a section).

- **`Card`** — `<article class="narrative-card">` with an optional lead
  `<img>`, an optional `<h3>` heading, and a `<div class="narrative-card__body">`.
- **`Accordion`** — `<details>` / `<summary>` panels (interactive with
  **no JS**, prerender-safe).
- **`Tabs`** — an ARIA tablist: `role="tablist"` of `role="tab"` buttons
  (first `aria-selected="true"`) + `role="tabpanel"` panels. Every panel
  renders (no `hidden`) so a no-JS / prerendered reader sees all content;
  the layout's progressive-enhancement JS hides inactive panels. Tab /
  panel ids derive from the label slug — deterministic, unique when
  labels are unique within the set.

Markdown / plaintext degrade `Accordion` and `Tabs` to labelled sections
with **every panel visible** (there is no interactive affordance to hide
behind, so hiding would drop content).

### `Component of name * props` — the custom-block escape hatch

```fsharp
//  | Component of name: string * props: Map<string, string>
```

A deployment-defined block resolved by `name` through a registered
renderer map, so new block kinds ship **without forking the
`NarrativeElement` DU**. `props` is a flat `Map<string,string>` — the
wire stays stringly-typed and serialisable. This is the one sanctioned
narrative **type-erasure boundary** (see the SDK
[`CLAUDE.md` "Type erasure boundaries"](../../CLAUDE.md)).

- Component renderers are authored as pure `props -> XmlNode` functions
  registered in
  [`NarrativeLayout.ComponentRegistry`](../../src/ToolUp.PublicRendering/Server/NarrativeLayout.fs)
  and bridged into `NarrativeHtml.RenderOptions.ComponentRenderer`
  (a `string -> Map -> string option` resolver) by
  `NarrativeLayout.componentResolver`.
- An **unregistered** name (the default — no components registered)
  degrades to a safe placeholder:
  `<div class="narrative-component narrative-component--unresolved" data-component="…">`.
- Markdown degrades to an HTML comment marker; plaintext to
  `[component: name]`; the Feliz client tree to an inert placeholder.

## Determinism + prerender safety

Every renderer is pure and deterministic — no `DateTime.Now`, no
randomness, no culture-sensitive formatting (slugs use an invariant
lowercase + non-alphanumeric collapse). The same `NarrativeDocument`
renders byte-identical across runs, which is what makes Narrative bodies
**prerender- and hydration-safe** (see
[`prerender.md`](prerender.md) §"State must be deterministic").

## Tests

[`NarrativeElementTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/NarrativeElementTests.fs)
covers every Phase 87 block across all three string renderers, the GP 11
byte-identity guard for legacy documents, the embed allowlist (allowed →
iframe; unknown / deny-all → placeholder), the component registry
(registered → HTML; unregistered → placeholder), recursive nesting, and
cross-run determinism.
