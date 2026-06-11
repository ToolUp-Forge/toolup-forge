# Server layouts — the BrandKit layout library + the Feliz.ViewEngine option

Phase 92. Two related surfaces:

1. **`ToolUp.BrandKit.Layouts`** — seven ready-made SSR layouts
   (`article`, `landing`, `dashboard`, `doc`, `gallery`, `video`,
   `knowledgePortal`) built on the BrandKit primitives, so a consumer
   composes a brand-correct page in ~20 lines instead of hand-rolling
   `XmlNode` trees.
2. **`PublicRenderingServerApp.withFelizLayout`** — registers a layout
   authored in the Feliz DSL (`Feliz.ViewEngine`) into the same layout
   registry as Giraffe layouts, for deployments sharing presentational
   component code with a Fable SPA. See
   [`docs/design/feliz-viewengine-evaluation.md`](../design/feliz-viewengine-evaluation.md)
   for the adopt decision and the shared-component subset.

Giraffe.ViewEngine remains the zero-extra-dependency default layout DSL
everywhere; nothing in this page is required for an existing deployment
(GP 11).

## The layout model

A registered layout is a pure `PublicPage -> XmlNode`. The BrandKit
layouts deliberately do **not** know about `PublicPage` (BrandKit stays
a leaf package with no content-system dependency) — they take neutral
**slots** (`XmlNode` / `XmlNode list` / strings), and the consumer's
layout function is the glue:

```fsharp
open ToolUp.BrandKit
open ToolUp.BrandKit.Layouts
open ToolUp.PublicRendering

let private chrome = {
    Chrome.create "My Site" with
        Stylesheets = [ "/brand.css" ]
        Header = Some (PageChrome.pageHeader { Brand = …; Nav = …; Right = [] })
        Footer = Some (PageChrome.pageFooter { Copyright = "© 2026 …"; Links = … })
}

let articleLayout (page: PublicPage) =
    Layout.article
        { chrome with Title = page.Title; Description = Some page.Description }
        {
            Eyebrow = page.Collection |> Option.map _.ToUpperInvariant()
            Title = page.Title
            Lede = None
            Meta = page.PublishedAt |> Option.map (fun d -> Pill.pill (d.ToString "yyyy-MM-dd")) |> Option.toList
            Hero = None
            Body = NarrativeLayout.renderBody page   // markdown / HTML / Narrative incl. rich blocks
            Aside = None
            Breadcrumb = None
        }

// composition root:
|> PublicRenderingServerApp.withLayout (LayoutName "bk-article") articleLayout
```

`NarrativeLayout.renderBody` (or `renderBodyWith` for an embed
allowlist + component registry) renders every Narrative element —
including the rich media + layout blocks — into the `Body` slot; nav
helpers (`NavLayout.menu` / `breadcrumb` / `pager`) produce nodes for
the `Breadcrumb` / `Sidebar` / `Pager` slots. **Optional slots omit
their wrapper entirely when `None`** — an unused region costs nothing
in the markup.

## The seven layouts

All live in `ToolUp.BrandKit.Layouts` as `Layout.<name> : ChromeSpec ->
<Name>Spec -> XmlNode`. Working composition: the
`samples/PublicSite` sample registers `bk-article` (BrandKit) and
`feliz` (Feliz DSL) alongside its hand-rolled layouts.

| Layout | Use for | Key slots |
|---|---|---|
| `Layout.article` | News / blog / case study | `Eyebrow` `Title` `Lede` `Meta` `Hero` `Body` `Aside` `Breadcrumb` |
| `Layout.landing` | Marketing / product front door | `HeroEyebrow/Title/Lede` `HeroActions` `HeroVisual` `Sections` |
| `Layout.dashboard` | Analytics / status page | `Title` `Toolbar` `Kpis` `Panels` (both grids) |
| `Layout.doc` | Documentation page | `Title` `Sidebar` `Toc` `Body` `Breadcrumb` `PrevNext` |
| `Layout.gallery` | Media grid | `Title` `Intro` `Items` (grid) `Pager` |
| `Layout.video` | Video page | `Title` `Player` `Meta` `Description` `Transcript` (a `<details>` disclosure) `Related` |
| `Layout.knowledgePortal` | KB / search portal | `Title` `Intro` `Search` `Answer` `Browse` `Sidebar` |

`ChromeSpec` (shared by all seven) carries `Lang`, `Title`,
`Description`, `Stylesheets`, `HeadExtra` (canonical / JSON-LD / OG
nodes), `Header`, `Footer`, `SkipLinkLabel`, `BodyClass`. Build it once
per deployment with `Chrome.create` + record update; per-page fields
(`Title`, `Description`) are overridden in the glue function.

## Accessibility contract

Every layout renders through the same shell (`LayoutShell.page`), which
guarantees:

- **Skip link first.** `a.bk-skip-link[href="#bk-main"]` is the first
  element in `<body>`, before the header landmark. Label localisable
  via `ChromeSpec.SkipLinkLabel`.
- **Landmarks.** Consumer-supplied `<header>` / `<footer>` (use
  `PageChrome.pageHeader` / `pageFooter`) around exactly one
  `<main id="bk-main" tabindex="-1">`; the `tabindex` makes the
  skip-link target programmatically focusable in every browser.
- **One `<h1>` per page**, always the layout's `Title`.
- **Focus order = DOM order.** Layouts introduce no tabindex other than
  the `-1` on `<main>`, no reordering CSS requirement.
- Semantic regions: `<article>` for article/doc content, `<aside>` for
  asides/related, `<figure>` for hero/player, `<details>/<summary>`
  for the video transcript, `<section>` for landing/dashboard regions.

Hide the skip link accessibly in CSS (visible on focus):

```css
.bk-skip-link { position: absolute; left: -999px; top: 0; }
.bk-skip-link:focus { left: 0; background: var(--bk-accent); color: var(--bk-on-dark-text); padding: .5rem 1rem; }
```

## Responsive baseline + class-hook reference

Like every BrandKit primitive the layouts ship **zero CSS** — class
hooks only, themed by the consumer's `--bk-*` custom properties (see
[`docs/brandkit-tokens.md`](../brandkit-tokens.md) for the token set).
Hooks follow `bk-<layout>-<region>`; `<main>` carries
`bk-main bk-layout-<name>`; repeated-item regions additionally carry
the shared **`bk-grid`** hook. Reference responsive rules:

```css
/* shared responsive grid — mobile-first, no horizontal scroll */
.bk-grid { display: grid; gap: 1rem; grid-template-columns: 1fr; }
@media (min-width: 640px)  { .bk-grid { grid-template-columns: repeat(2, 1fr); } }
@media (min-width: 1024px) {
  .bk-dashboard-kpis  { grid-template-columns: repeat(4, 1fr); }
  .bk-dashboard-panels { grid-template-columns: repeat(2, 1fr); }
  .bk-gallery-grid     { grid-template-columns: repeat(3, 1fr); }
}

/* two-column regions collapse to one column on narrow viewports */
.bk-article-columns, .bk-doc-columns, .bk-knowledge-columns { display: grid; gap: 2rem; }
@media (min-width: 1024px) {
  .bk-article-columns   { grid-template-columns: minmax(0, 2.5fr) minmax(0, 1fr); }
  .bk-doc-columns       { grid-template-columns: 16rem minmax(0, 1fr) 14rem; }
  .bk-knowledge-columns { grid-template-columns: minmax(0, 3fr) minmax(0, 1fr); }
}
```

### Theming across brand-token sets

The markup is theme-invariant: switching brands means switching the
`:root` (or `ChromeSpec.BodyClass`-scoped) `--bk-*` declarations, never
the layout code. Two contrasting sets that both style the same render:

```css
/* warm editorial */
:root { --bk-font-display: 'Newsreader', Georgia, serif;
        --bk-ink: #2B2638; --bk-paper: #F3EEE4; --bk-panel: #FBF8F2;
        --bk-rule: #E4DBCB; --bk-accent: #6B5FBF; --bk-radius-lg: 16px; }

/* dark technical (scoped via Chrome BodyClass = Some "theme-midnight") */
.theme-midnight { --bk-font-display: 'Inter', system-ui, sans-serif;
        --bk-ink: #E7E2D8; --bk-paper: #14121A; --bk-panel: #1E1B26;
        --bk-rule: #2E2A3A; --bk-accent: #8FE3C0; --bk-radius-lg: 6px; }
```

## Feliz-DSL layouts (`withFelizLayout`)

```fsharp
// layout authored in the Feliz DSL — PublicPage -> ReactElement
|> PublicRenderingServerApp.withFelizLayout (LayoutName "feliz") FelizPageLayout.render
```

Same naming, fallback, caching, head-injection and gating semantics as
`withLayout` — the adapter (`FelizLayout.toGiraffe`) renders the
element without a doctype and the page handler adds the single
`<!DOCTYPE html>` as usual. Author shared presentational components
once behind `#if FABLE_COMPILER` (worked example:
`samples/PublicSite/Layouts/FelizPageLayout.fs`); keep hooks, state and
event handlers client-side.

## Verification

- `BrandKitLayoutTests` pins the markup contract: a11y baseline (skip
  link first + `#bk-main` target + single `<h1>`), per-layout class
  hooks, the optional-slot omission rule.
- `FelizLayoutAdapterTests` pins the adapter: exactly one doctype,
  registry round-trip, the shared prop subset (className / style with
  CSS variables / aria).
