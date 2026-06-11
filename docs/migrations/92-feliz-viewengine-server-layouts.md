# Migration — Phase 92: Feliz.ViewEngine server layouts + BrandKit layout library

**Status:** additive, opt-in. No existing API changes; a deployment
that registers only Giraffe layouts is byte-for-byte unchanged (GP 11).
No consumer action required.

## What changes

1. **BrandKit layout library** — `ToolUp.BrandKit.Layouts` adds
   `ChromeSpec` / `Chrome.create` / `LayoutShell.page` and seven ready
   layouts (`Layout.article` / `landing` / `dashboard` / `doc` /
   `gallery` / `video` / `knowledgePortal`) with neutral `XmlNode`
   content slots, a built-in accessibility baseline (skip link,
   landmarks, single `<h1>`, focusable `#bk-main`) and `bk-*` /
   `bk-grid` class hooks. Zero CSS shipped, as with all BrandKit
   primitives.
2. **Feliz-DSL layouts** — `ToolUp.PublicRendering` adds
   `FelizLayout.toGiraffe` and
   `PublicRenderingServerApp.withFelizLayout`, registering a
   `PublicPage -> Feliz.ViewEngine.ReactElement` layout into the same
   registry as Giraffe layouts. New package dependency on
   `ToolUp.PublicRendering` only: `Feliz.ViewEngine` 1.0.3 (MIT,
   FSharp.Core-only). Adopt decision + shared-component subset:
   [`docs/design/feliz-viewengine-evaluation.md`](../design/feliz-viewengine-evaluation.md).

## Adopting it (optional)

```fsharp
open ToolUp.BrandKit.Layouts

// brand-correct page from a ready layout (~20-line glue; full example
// in samples/PublicSite/Layouts/BrandKitArticleLayout.fs):
|> PublicRenderingServerApp.withLayout (LayoutName "bk-article") (fun page ->
    Layout.article
        { chrome with Title = page.Title }
        { Eyebrow = None; Title = page.Title; Lede = None; Meta = []
          Hero = None; Body = NarrativeLayout.renderBody page
          Aside = None; Breadcrumb = None })

// layout authored in the Feliz DSL (shared-component story):
|> PublicRenderingServerApp.withFelizLayout (LayoutName "feliz") FelizPageLayout.render
```

Style via the `--bk-*` CSS variables + the documented hooks
([`docs/platform/layouts.md`](../platform/layouts.md) carries the
reference responsive/skip-link stylesheet).

## Breaking change

None. New module + two new functions + one new (companion-local)
package reference.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the
  `ToolUp.BrandKit — layout library (Phase 92)` and
  `Feliz.ViewEngine layout adapter (Phase 92)` suites cover the a11y
  baseline, class hooks, optional-slot omission, single-doctype
  rendering and registry round-trip.
- `samples/PublicSite` registers both layout kinds and serves
  `/brandkit-demo` + `/feliz-demo`.

## Rollback

Don't call `withFelizLayout` / don't reference the BrandKit layouts —
both surfaces are inert when unused. Removing the `Feliz.ViewEngine`
package requires only deleting `Server/FelizLayoutAdapter.fs` + the
`withFelizLayout` helper.
