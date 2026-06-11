# Migration — Phase 111: server-rendered-fragment page-body source + per-request head-metadata seam

**Status:** additive, opt-in. An `IContentSource` returning a bare `ContentBody` (including `ContentBody.Html`) is served **byte-for-byte** as pre-111; a pipeline registering no sources pays nothing (GP 11 / GP 13).

## What changes

`ToolUp.PublicRendering` already hosted server-rendered HTML body fragments (`ContentBody.Html` through [Phase 83](83-icontentsource.md) `IContentSource`), but a request-time fragment page was SEO-incomplete: no channel for per-request `<head>` metadata (canonical / `og:image` / extra meta / JSON-LD). Phase 111 adds the typed channel plus the cache-ownership rule for externally-rendered fragments.

New public surface (all in `ToolUp.PublicRendering`):

| Symbol | Where | Purpose |
|---|---|---|
| `PageHeadMetadata` | `Shared/PublicContentTypes.fs` | Typed per-request head metadata (Title / Description / Canonical / OgImage / Meta / JsonLd) + the reserved `head:*` frontmatter codec |
| `ResolvedContent` | `Shared/PublicContentTypes.fs` | `{ Body; Head; Provenance }` — the richer resolve result |
| `IResolvedContentSource` | `Server/IContentSource.fs` | Optional capability interface (preferred by the resolution chain when present) |
| `ContentSource.ofResolved` / `ofRouteResolved` / `ofRouteResolvedEnumerable` | `Server/IContentSource.fs` | Constructors (the enumerable variant reaches sitemap / static export / IndexNow) |
| `PageHeadInjection` | `Server/PageHeadInjection.fs` | Emits the envelope's tags before `</head>` (used by the handler + static export) |

## Adopting it

Return a `ResolvedContent` instead of a bare body — see the worked example in [`docs/platform/dynamic-ssr.md`](../platform/dynamic-ssr.md) § "Per-request head metadata". `Head.Title`/`Description` fold into the synthesised page's fields; the rest is injected into the rendered document's `<head>` by the handler (and identically by `StaticExport`).

**Reserved frontmatter prefix.** The codec owns `head:*` keys on `PublicPage.Frontmatter`. If your deployment authored frontmatter keys with that prefix (unlikely — it was never documented), rename them before upgrading.

**Cache ownership (load-bearing for external renderers).** When a fragment produced by an external renderer is hosted under PublicRendering, forge's [Phase 84](84-ssr-render-cache.md) `IRenderCache` owns ETag / `Cache-Control` / purge-on-publish for the page. Disable the renderer's own HTTP-cache seam for that path — two caches over one response double-cache and disagree on invalidation.

**Whole page vs block.** `ResolvedContent` is the whole-page seam; embedding a rendered *block* inside a Narrative document stays on the Phase 87 `Component` registry. Both documented in `dynamic-ssr.md`.

## Breaking change

None. `IResolvedContentSource` is an optional secondary interface; the existing `IContentSource.Resolve` surface, the `PublicPage` record shape, and every existing construction site are unchanged.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `ResolvedContent (Phase 111)` suite covers the codec round-trip, synthesis (incl. the bare-body GP 11 parity case), head injection through `PublicPageHandler` (uncached + cached + purge), and enumerable discovery reach.

## Rollback

Stop returning `ResolvedContent` (or revert to the `ofRoute` constructors). Pages without the `head:*` envelope skip injection entirely; the new types are inert when unused.

## See also

- [`docs/platform/dynamic-ssr.md`](../platform/dynamic-ssr.md) — worked example + cache-ownership rule + whole-page-vs-block guidance.
