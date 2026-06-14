# Migration — Phase 147: public-rendering conditional-GET hardening

**Status:** one net-new opt-in capability + two patch-class correctness fixes inside the existing opt-in render-cache path. A deployment that composes neither `withRenderCache` nor `withConditionalGet` is byte-for-byte pre-147 (GP 11). No consumer action required.

## What changes

`PublicPageHandler`'s conditional revalidation is made correct + useful for crawl-budget on large SSR catalogs. Four coupled gaps, all fixed through the [Phase 155](155-ssr-cache-primitive.md) `ConditionalGet` combinator so both the page handler and any programmatic consumer benefit:

1. **`If-Modified-Since` union.** The 304 gate now short-circuits when EITHER `If-None-Match` matches the ETag OR `If-Modified-Since` is at/after the resource's `Last-Modified` (RFC 7232, second-granularity; a malformed header is ignored → full response). Googlebot predominantly revalidates with `If-Modified-Since`, so re-crawls now 304 instead of returning full 200s.
2. **Content-stable `Last-Modified`.** Derived from a content-stable signal — the page's `PublishedAt` when present, otherwise a process-stable deploy-generation stamp (`ConditionalGet.deployStamp`) — NOT the wall-clock render moment. A deterministic page now presents the same `Last-Modified` across refreshes / restarts / stale-while-revalidate, so `If-Modified-Since` can actually 304. Stored on the cache entry (`RenderedPage.LastModified`) so a cache hit reproduces exactly what the original render emitted.
3. **Cache-independent emission (`withConditionalGet`, opt-in, default off).** A new compose knob makes the **uncached** serve path emit `ETag` + content-stable `Last-Modified` + `Cache-Control` and honour the conditional gates — with NO render cache registered. Conditional revalidation is orthogonal to ISR caching; a deterministic SSR site gets crawl-budget 304s + edge-cacheability without paying for an ISR tier. Default off → `serveUncached` is byte-for-byte pre-147.
4. **Weak ETags.** `ETag` is now `W/"<hash>"` (weak) on both paths, since the SDK pipeline runs `UseResponseCompression` (gzip / br / identity) and RFC 7232 requires the weak form under content-coding variance. `If-None-Match` comparison uses the weak comparison function (strips `W/` + quotes), so a client echoing back either form still matches.

The cached path's `If-Modified-Since` + strong→weak ETag changes are **patch-class correctness fixes** within the already-opt-in `withRenderCache` path (a conditional request receiving a 304 is RFC-correct, not a behaviour surprise).

## Diff to apply

None required. To **opt a cache-less deterministic SSR site into conditional revalidation**:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withConditionalGet)            // ← opt in
|> ServerApp.run
```

`withConditionalGet` defaults `Cache-Control` to `public, max-age=0, must-revalidate` (edge/browser-cacheable, always revalidated → cheap conditional re-crawls). Use `withConditionalGetCacheControl "public, max-age=300"` to let edges hold the page between revalidations.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs`:
  - combinator: bare `If-Modified-Since` re-crawl → 304; `If-Modified-Since` predating `Last-Modified` → full body; malformed `If-Modified-Since` → full body; weak ETag syntax + weak comparison against a strong-form `If-None-Match`; second-granularity `Last-Modified` truncation.
  - page handler: without `withConditionalGet`, the uncached path emits no validators (pre-147 byte-for-byte); with it, a weak ETag + content-stable `Last-Modified` are emitted, a bare `If-Modified-Since` re-crawl 304s, and `Last-Modified` is identical across renders.
- A deployment composing neither `withRenderCache` nor `withConditionalGet` — existing page-handler tests stay green (no validators emitted).

## Rollback

Revert the Phase 147 commit. `withConditionalGet` is additive; the cached-path changes (weak ETag, `If-Modified-Since`, content-stable `Last-Modified`) revert to the pre-147 strong-ETag / `If-None-Match`-only / render-wall-clock behaviour. `RenderedPage` gains a `LastModified` field — old cached blobs written pre-147 deserialise it to the default and the handler falls back to `RenderedAt` for those entries, so no purge is required; reverting simply ignores the field.
