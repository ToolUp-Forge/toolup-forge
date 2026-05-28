# ToolUp.PublicRendering

Server-side-rendered public-facing pages for ToolUp.Platform: marketing sites, content portals, public landing pages. Companion package — apps that leave `ServerConfig.PublicRendering = NoPublicRendering` (the default) pay zero runtime cost.

**Phase 38 status: in progress.** Six-rule portability audit clean; strip-imports byte-for-byte verified.

## What this is

The SDK collapses the "render markdown pages, emit a sitemap, honour legacy URLs, ship structured-data tags" loop into one composable companion. Source-of-truth content lives as `content/**/*.md` files in the deployment's repo (or `IEntityStore<PublicPage>` records for runtime-edited entries); rendering uses Giraffe.ViewEngine layouts that the consumer authors per-deployment. Styling stays opinion-free (Tailwind / Pico CSS / hand-CSS all compose cleanly because templates are plain `Html.div [ prop.className "..." ]`).

| Concern | File | Module |
|---|---|---|
| Content types | [`Shared/PublicContentTypes.fs`](Shared/PublicContentTypes.fs) | `Slug`, `LayoutName`, `ContentBody`, `PublicPage`, `ContentRoot`, `Redirect`, `PublicRenderingMode` |
| Server interface | [`Server/IPublicContentApi.fs`](Server/IPublicContentApi.fs) | `IPublicContentApi` — six-rule portable |
| Markdown loader | [`Server/MarkdownContentLoader.fs`](Server/MarkdownContentLoader.fs) | walks `content/**/*.md`; dev-mode `FileSystemWatcher` for hot-reload |
| Default impl | [`Server/PublicContentApiImpl.fs`](Server/PublicContentApiImpl.fs) | file-first; optional `IEntityStore<PublicPage>` overlay |
| Redirect middleware | [`Server/RedirectMap.fs`](Server/RedirectMap.fs) | `redirects.csv`-driven 301s; query strings preserved |
| Sitemap | [`Server/SitemapGenerator.fs`](Server/SitemapGenerator.fs) | `/sitemap.xml`; `Frontmatter["sitemap"] = "exclude"` honoured |
| JSON-LD | [`Server/StructuredDataHelpers.fs`](Server/StructuredDataHelpers.fs) | `Article` / `Person` / `Event` / `Organization` / `BreadcrumbList` emitters |
| Page handler | [`Server/PublicPageHandler.fs`](Server/PublicPageHandler.fs) | catch-all `GET /{slug}` → resolve → render → redirect → 404 |
| Compose pipeline | [`Server/PublicRenderingCompose.fs`](Server/PublicRenderingCompose.fs) | `PublicRenderingServerApp` record + `run` |

## Minimal compose

```fsharp
open ToolUp.Platform
open ToolUp.PublicRendering
open Giraffe.ViewEngine

let baseLayout (page: PublicPage) : XmlNode =
    html [] [
        head [] [
            title [] [ str page.Title ]
            meta [ _name "description"; _content page.Description ]
        ]
        body [] [
            // page body slot — layouts choose how to render `page.Body`
        ]
    ]

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig
    { ServerConfig.defaults with
        PublicRendering = EnabledPublicRendering (ContentRoot "content") }
|> PublicRenderingServerApp.withLayout (LayoutName "base") baseLayout
|> PublicRenderingServerApp.run
```

## Six portability rules

`IPublicContentApi` satisfies all six rules. See the interface docstring in [`Server/IPublicContentApi.fs`](Server/IPublicContentApi.fs) for the per-rule audit.

## Strip-imports guarantee

`ServerConfig.PublicRendering = NoPublicRendering` (the default) strips the entire surface: no `/sitemap.xml` handler, no markdown-watcher hosted service, no redirect middleware, no JSON-LD helpers loaded. Byte-for-byte equivalent to a base `ServerApp.run` (per `IncludePlatformDefaults`-style discipline).
