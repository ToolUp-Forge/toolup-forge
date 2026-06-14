# Migration — Phase 149: cacheable, conditional-GET `sitemap.xml`

**Status:** additive headers (always on, no opt-in) + one net-new opt-in response cache. The sitemap body is byte-for-byte pre-149; a deployment that does nothing gains standard HTTP caching validators on `/sitemap.xml` exactly as `UseStaticFiles` already does for static assets. No consumer action required.

## What changes

`SitemapGenerator.handler` (the `/sitemap.xml` route) was an unconditional, uncached responder — it re-walked the universe and rebuilt the full XML on every request and emitted no caching headers, so a crawler polling the sitemap got a fresh 200 every time with no "nothing changed" signal.

It now:

1. **Emits conditional-GET validators (always on).** A weak `ETag` (`W/"<hash>"`) derived from `IndexNow.computeSignature` over the **same deduped universe** the body is built from — so the ETag rolls exactly when the sitemap content changes (a slug added/removed, a lastmod changed) and is stable otherwise. A content-stable `Last-Modified` — the latest page lastmod across the universe, falling back to `ConditionalGet.deployStamp` (the same single content-version stamp Phase 147 uses for the page validators, so the page-level and sitemap-level freshness signals never diverge). A `Cache-Control` (`public, max-age=0, must-revalidate` by default). The handler honours `If-None-Match` / `If-Modified-Since` with a `304` through the Phase 155 `ConditionalGet.cacheable` combinator (the union gate + weak comparison + second-granularity `Last-Modified` truncation all flow from that one seam).

   These are additive standard HTTP caching headers + an RFC-correct conditional `304`; they need no opt-in.

2. **Optional response cache (`withSitemapResponseCache`, default off — GP 11 / GP 13).** Memoises the generated XML keyed on the universe digest, so repeated polls within a content generation skip rebuilding the (potentially large) XML body. Off → the handler rebuilds per request exactly as pre-149. The cached body is **byte-identical** to the uncached one.

3. **Multi-site coherence.** Each per-site sitemap (Phase 146) gets the same validators, keyed on its own site's universe digest, and — when the response cache is composed — its own cache instance (so the single-slot memo never thrashes between sites).

The pure generators (`SitemapGenerator.entries` / `generateWith` / `generate`) are unchanged.

> **Compile-order note (in-tree only):** `SitemapGenerator.fs` moved below `IndexNow.fs` and `ConditionalGet.fs` in `ToolUp.PublicRendering.fsproj` because the handler now derives its ETag from `IndexNow.computeSignature` and serves through `ConditionalGet.cacheable`. No public surface changed.

## Diff to apply

None required for the validators (always on). To **opt into the response-body cache** for a large generated sitemap:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withSitemapResponseCache)        // ← opt in
|> ServerApp.run
```

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs` ("Phase 149 cacheable sitemap"):
  - handler emits a weak `ETag` + `Cache-Control`; a matching `If-None-Match` re-poll → `304`.
  - handler `304`s a bare `If-Modified-Since` re-crawl at/after the universe `Last-Modified`.
  - the sitemap ETag (digest over the universe) rolls on a lastmod change / an added slug, stable when the universe is unchanged.
  - `SitemapCache.GetOrBuild` memoises per digest and rebuilds on a digest change.
  - the sitemap body is byte-identical with and without the response cache.

## Rollback

Revert the Phase 149 commit. The validators are additive headers + a conditional `304`; `withSitemapResponseCache` is opt-in. Reverting drops the headers and the cache and restores the unconditional, uncached responder. No persisted state, no purge.
