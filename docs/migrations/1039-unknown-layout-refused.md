# An unknown page layout name is refused, not replaced

**Ships in:** ToolUp.PublicRendering (`LayoutRefusal`, `LayoutRefusedException`, `PublicLayouts`,
`PublicRenderingServerApp.DefaultLayout` / `withDefaultLayout`) (Phase 1039).

**Affected:** an app that registers two or more layouts; a page (markdown frontmatter, an overlay entity, a content
source, an AI publish) whose `Layout` names a layout nobody registered; code that builds `PublicRenderingServerApp` as a
full record literal. An app with exactly one layout whose pages name it, or name none, is unchanged.

## What changes

**1. One resolver, no map-order fallback.** Every place that frames a page (the page handler, `/preview`, `/search`,
static export) used to resolve `page.Layout` by name and, on a miss, take `layouts |> Map.toSeq |> Seq.tryHead`. `Map`
orders by key, so the fallback was the *alphabetically first* layout, not the first registered. They now all call
`PublicLayouts.resolve`: a page naming a layout gets that layout or a typed `LayoutRefusal`; a page naming none gets the
declared default.

**2. The default is declared.** `PublicRenderingServerApp.withDefaultLayout name` declares it. With exactly one layout
registered it is the default implicitly. With two or more and no declaration, composing raises
`LayoutRefusedException` naming the registered layouts, so the app does not start. A declared default that is not
registered raises the same way.

**3. Unknown names are refused.** At startup the `public-rendering-layout-name-resolved` preflight validator checks
every published, public markdown page and fails naming each page and layout. At request time a page that resolves to
an unknown name (a draft or gated page, an overlay entity, a content source, a hot-reloaded file) is a `500` naming the
refusal, never another layout's frame.
`StaticExport.run` / `runWith` raise `LayoutRefusedException` before writing anything. A `SemanticSearchConfig.Layout`
naming an unregistered layout is refused at compose.

**4. "Names none" is the empty name.** A markdown page with no `layout:` key now carries `LayoutName ""` (it carried
`"page"`), so it takes the declared default. The narrative publisher writes `LayoutName ""` for a publish without a
layout hint and refuses a hint naming an unregistered layout.

## Migrating

- **Two or more layouts:** add `|> PublicRenderingServerApp.withDefaultLayout (LayoutName "page")` (whichever layout
  your unnamed pages should take). To keep the old frame exactly, declare the alphabetically first name.
- **A page naming a layout you never registered:** fix the name, or register the layout. The startup error lists them.
- **Frontmatter-less markdown pages that relied on the `"page"` default with several layouts registered:** declare
  `"page"` as the default.
- **A satellite site (`PublicSiteDef`) with two or more layouts of its own:** bind its default under the empty name,
  `Layouts = Map.add PublicLayouts.unnamed pageLayout layouts`, or composing raises naming the site.
- **A consumer that emits pages naming no layout:** emit `LayoutName ""` (or `PublicLayouts.unnamed`); the page takes
  the declared default.
- **A full `PublicRenderingServerApp` literal:** add `DefaultLayout = None`, or build from `create ()` / `createFrom`.
- **Calling `PublicPageHandler.handler`, `ContentPreview.previewHandler`, `SemanticSearchHandler.handle` or
  `StaticExport.run` with your own map:** pass `PublicLayouts.withDefault (Some name) layouts` to declare a default.

## Verifying

Run the `PublicRendering — Phase 1039 layout name resolution` tests: two layouts registered `zeta` then `alpha`; an
unnamed page takes the declared default, an unknown name is refused at request time, in static export and at compose.

## Rollback

Revert the phase. There is no data migration: stored pages keep their `Layout` names.
