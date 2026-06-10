# Migration — Phase 84: SSR render cache + incremental regeneration + HTTP cache headers

**Status:** additive, opt-in. A deployment that never calls `withRenderCache` is **byte-for-byte identical** to the pre-84 page handler — no cache lookup, no HTTP cache headers, no allocation (GP 11 / GP 13).

## What changes

`ToolUp.PublicRendering` gains an opt-in **render cache** (ISR — incremental-static-regeneration) tier plus standard HTTP caching headers. Before Phase 84, every request — including every crawler hit — re-ran a data-bound page's backing query ([Phase 83](83-icontentsource.md) `IContentSource`) and re-rendered. For a page that runs an analytics projection or a retrieval query, that is expensive under crawl load.

Phase 84 stores the rendered HTML keyed by `(slug, scope, content-version)` with a TTL and optional `stale-while-revalidate`. Within the TTL window the resolution chain runs **once**, not once per request; HTTP `ETag` / `Last-Modified` / `Cache-Control` headers let clients and CDNs revalidate cheaply (`If-None-Match` → `304`).

New public surface (all in `ToolUp.PublicRendering`):

| Symbol | Where | Purpose |
|---|---|---|
| `IRenderCache` | `Server/IRenderCache.fs` | `TryGet` / `Set` / `Invalidate` — the cache contract (six-rule clean) |
| `IRenderCacheInvalidation` | `Server/IRenderCache.fs` | `PurgeSlug` — slug-level purge for the publish/CMS hook |
| `RenderKey` / `RenderedPage` / `CachePolicy` | `Server/IRenderCache.fs` | Cache key, stored value, per-route policy |
| `RenderCacheSettings` | `Server/IRenderCache.fs` | Compose-time default policy |
| `InMemoryRenderCache.create` | `Server/RenderCacheImpl.fs` | Single-instance default impl |
| `BlobRenderCache.create` | `Server/RenderCacheImpl.fs` | `IBlobStorage`-backed multi-instance impl |
| `PublicRenderingServerApp.withRenderCache` | `Server/PublicRenderingCompose.fs` | Register the cache |
| `…withRenderCacheDefaultPolicy` | `Server/PublicRenderingCompose.fs` | Policy for pages with no `cache:` frontmatter |
| `…withRenderCacheInvalidation` | `Server/PublicRenderingCompose.fs` | Explicit invalidator (custom caches only) |

## Adopting it

Compose a cache and (optionally) a default policy. Per-route opt-in is via a page's `cache:` frontmatter:

- `cache: 300` → cache 300 s with stale-while-revalidate (the default)
- `cache: 300;no-swr` → 300 s, no stale serving
- `cache: off` (or absent) → pure per-request (the default)

```fsharp
open ToolUp.PublicRendering

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
// single-instance:
|> PublicRenderingServerApp.withRenderCache (InMemoryRenderCache.create ())
// cache frontmatter-less dynamic content-source pages for 5 minutes:
|> PublicRenderingServerApp.withRenderCacheDefaultPolicy (Cache(300, true))
|> PublicRenderingServerApp.run
```

Multi-instance deployments (every replica must share the cache, and a publish on one replica must invalidate the rest) use the blob-backed impl over the SDK's configured `IBlobStorage`:

```fsharp
|> PublicRenderingServerApp.withRenderCache (BlobRenderCache.create storage)
```

**Scope isolation (GP 4).** Cache keys carry the requesting principal's storage scope (`"public"` for anonymous; the team/user container otherwise), derived from the resolved `AccessContext` — never from caller input. Team A's request can only ever address its own cache entries; it cannot name team B's.

**Publish invalidation.** When a cache is composed, `publish_narrative` ([Phase 80b](../COMPLETED_PHASES.md)) purges the slug's cached render on a successful publish, so republished content is served immediately rather than waiting out the prior TTL. The default impls implement `IRenderCacheInvalidation`; supply `withRenderCacheInvalidation` only for a custom `IRenderCache` that lacks its own slug-purge.

## Breaking change

None. `IRenderCache` is opt-in DI; the page handler activates its cached path only when an `IRenderCache` is registered. Existing `IPublicContentApi` implementors are unaffected.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `RenderCache (Phase 84)` suite covers the `IRenderCache` contract pack (bound to both impls), `CachePolicy.parse` / `RenderedPage.hash`, and `PublicPageHandler` integration: miss→hit (resolve-once), `If-None-Match` → `304`, header emission, the `cache: off` default, the **no-cache-composed pre-84 path** (no headers, GP 11/13), default-policy caching, and stale-while-revalidate serving.

## Rollback

Remove the `withRenderCache` registration. With no cache in DI the page handler runs the pre-84 path exactly — no headers, no lookup. The `IRenderCache.fs` / `RenderCacheImpl.fs` files can remain in place harmlessly (no runtime cost when unused).

## See also

- [`docs/migrations/83-icontentsource.md`](83-icontentsource.md) — the data-bound SSR resolution chain the cache fronts.
- [`docs/migrations/80c-with-public-rendering-additive-composition.md`](80c-with-public-rendering-additive-composition.md) — the `withPublicRendering` additive composition seam (`withRenderCache` composes the same way).
