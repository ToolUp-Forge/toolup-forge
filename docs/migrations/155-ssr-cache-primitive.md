# Migration — Phase 155: handler-agnostic SSR caching + conditional-GET primitive

**Status:** net-new opt-in surface + a pure internal extraction. A deployment that does nothing is byte-for-byte unchanged (GP 11). No consumer action required.

## What changes

The render cache ([Phase 84](../../README.md)) and the conditional-GET logic were welded to `PublicPageHandler`. Phase 155 lifts both into reusable, handler-agnostic primitives so a **programmatic-SSR** consumer — one rendering pages from domain data via `Giraffe.ViewEngine`, not the markdown content-file pipeline — can memoise expensive deterministic renders and serve `ETag` / `Last-Modified` / `304` without routing through `PublicPageHandler` / `IPublicContentApi`.

Three additions, all in `ToolUp.PublicRendering`:

1. **`ConditionalGet`** (`Server/ConditionalGet.fs`) — a standalone Giraffe combinator:
   - `ConditionalGet.cacheable etag lastModified cacheControl : HttpHandler` — emits the validators, then 304-or-continues to the wrapped body handler.
   - `ConditionalGet.immutableAsset fingerprint lastModified : HttpHandler` — the one-year-`immutable` convenience for fingerprinted assets.
   - `ConditionalGet.setValidators` / `isNotModified` — the lower-level helpers, called inline by `PublicPageHandler`.
2. **`RenderCache.getOrRender cache key policy render : Async<RenderedPage>`** (`Server/IRenderCache.fs`) — lookup + store + stale-while-revalidate over any `IRenderCache`, callable from any handler with a caller-supplied render thunk and key.
3. **`RenderKey.forKey slug scopeId contentVersion`** / **`RenderKey.forPublic slug`** — smart constructors. `RenderKey.Slug` is documented as an opaque cache discriminator (a programmatic consumer keys on its own composite, e.g. `"report/{tenant}/{quarter}"`); `ContentVersion` carries the consumer's single content-version stamp.

`PublicPageHandler` is refactored to consume `ConditionalGet` on its cached + uncached paths — **no behaviour change**: Phase 155 keeps the strong ETag and the `If-None-Match`-only gate exactly as before. (The hardening — weak ETag, the `If-Modified-Since` union, content-stable `Last-Modified` — lands in [Phase 147](147-conditional-get-hardening.md), through this same seam.)

## Diff to apply

None. Phase 155 ships entirely inside the SDK; consumers adopt by opting in.

### Programmatic-consumer pattern (the new capability)

```fsharp
open ToolUp.PublicRendering

// A deterministic render keyed on an arbitrary composite key.
let cache = InMemoryRenderCache.create ()                 // or BlobRenderCache.create storage
let contentVersion = "pkg-1.4.0+deploy-9f3c"              // your single content-version stamp

let reportRoute (tenant: string) (quarter: string) : HttpHandler =
    fun next ctx ->
        task {
            let key = RenderKey.forKey $"report/{tenant}/{quarter}" "public" contentVersion

            let! page =
                RenderCache.getOrRender cache key (CachePolicy.Cache(300, true)) (fun () -> async {
                    return renderExpensiveReportHtml tenant quarter      // your domain render
                })

            // Serve validators + 304 via the combinator; body handler writes the html.
            let body: HttpHandler = fun n c -> c.WriteStringAsync page.Html
            return! (ConditionalGet.cacheable page.ContentHash deployStampOrPublishDate "public, max-age=300" >=> body) next ctx
        }
```

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs`:
  - `ConditionalGet.cacheable` 304s a synthetic handler keyed on an arbitrary composite key, and passes through on a fresh request.
  - Phase 155 byte-for-byte pins: strong ETag, `If-None-Match`-only gate (a bare `If-Modified-Since` does **not** 304 pre-147).
  - `RenderCache.getOrRender` memoises within the TTL window, serves a stale entry while refreshing (SWR), and stores nothing under `NoCache`.
- `PublicPageHandler` cached + uncached behaviour is unchanged — the existing page-handler / cache contract tests stay green.

## Rollback

Revert the Phase 155 commit. `ConditionalGet.fs` is additive; the `PublicPageHandler` refactor is a pure delegation, so reverting restores the inlined helpers with no data-format or wire change. No persisted state is affected (`RenderedPage` shape is unchanged in Phase 155).
