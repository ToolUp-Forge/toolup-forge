# Migration — Phase 109: IndexNow push-indexing for `ToolUp.PublicRendering`

**Status:** additive, opt-in. A deployment that never calls `withIndexNow` is **byte-for-byte identical** to the pre-109 pipeline — no `/{key}.txt` route, no hosted service, no publish ping, no allocation (GP 11 / GP 13).

## What changes

`ToolUp.PublicRendering` gains **push indexing** via [IndexNow](https://www.indexnow.org/). The companion already gives crawlers everything to *discover* content passively (sitemap, static export, prerender, breadcrumb/site-nav JSON-LD, per-tag Atom feeds). Phase 109 adds the *active* signal: when content is published or a deploy changes the URL universe, participating engines (Bing, Yandex, Seznam, Naver — and indirectly Google via Bing's index-share signal) are notified immediately instead of waiting out the passive-crawl window. For a site with tens of thousands of long-tail SSR pages, this collapses the months-long wait.

Two pieces:

1. **Ownership-key endpoint** at `/{key}.txt` (body = the key) so IndexNow can verify host ownership. A non-matching `/*.txt` request falls through to the next handler.
2. **Resumable batched submission** — POST `{host,key,keyLocation,urlList}` to `https://api.indexnow.org/IndexNow` in batches of ≤10,000, with **per-batch success persistence** so an app restart resumes only the failed batches (never re-submits batches that already landed).

Two triggers: a fire-and-forget **startup bulk submission** (`IHostedService`, never blocks startup) and a **per-slug ping on publish** (hooked where the render cache is purged in `PublicRenderingNarrativePagePublisher`).

New public surface (all in `ToolUp.PublicRendering`):

| Symbol | Where | Purpose |
|---|---|---|
| `IndexNowOptions` (+ `defaults` / `enabled` / `with*`) | `Server/IndexNow.fs` | Compose-time options |
| `PublicRenderingServerApp.withIndexNow` | `Server/PublicRenderingCompose.fs` | Opt in |
| `IIndexNowStateStore` | `Server/IndexNow.fs` | Resumable-state seam (six-rule clean) |
| `FileIndexNowStateStore.create` / `createAt` | `Server/IndexNow.fs` | File-backed default (temp dir) |
| `BlobIndexNowStateStore.create` | `Server/IndexNow.fs` | `IBlobStorage`-backed multi-instance impl |
| `IIndexNowService` (`SubmitAll` / `PingSlug`) | `Server/IndexNow.fs` | Manual/ops resubmit + publish-ping, registered in DI |
| `SitemapGenerator.entries` | `Server/SitemapGenerator.fs` | Shared `(Slug * lastmod)` URL universe (sitemap + push share it) |

## Adopting it

Compose `withIndexNow`. The host defaults from `ServerConfig.PublicBaseUrl` (the value the sitemap already uses); the key derives from a stable host-based seed unless you pin one.

```fsharp
open ToolUp.PublicRendering

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config   // PublicBaseUrl = Some "https://example.com"
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withIndexNow IndexNowOptions.enabled
|> PublicRenderingServerApp.run
```

Pin the host / key, or wire the multi-instance state store, with the record / `with*` helpers:

```fsharp
|> PublicRenderingServerApp.withIndexNow
    { IndexNowOptions.enabled with
        Host = Some "example.com"
        StateStore = Some(BlobIndexNowStateStore.create storage) }   // multi-instance resume state
```

`IndexNowOptions` fields: `Enabled`, `Host` (default derived), `Key` / `KeySeed` (default stable host-derived key), `BatchSize` (default 10,000), `Endpoint` (override for tests/staging), `SubmitOnStartup` / `PingOnPublish` (default both on), `StateStore` (default file-backed).

**Manual / ops resubmit.** Resolve `IIndexNowService` from DI and call `SubmitAll ()` (resumable — it skips batches already recorded for the current deploy signature). No admin UI ships in this phase.

## Key design notes

- **The deploy signature is content-based** — SHA-256 over the sorted `slug@lastmod` universe — so it rolls exactly when the rendered content set or any page's lastmod changes. A matching signature with the cleared "fully-done" sentinel skips the whole submission on restart; a changed signature re-submits.
- **The key is stable, not content-derived.** The `/{key}.txt` endpoint and every submission's `keyLocation` must agree, and the key must not churn on each content edit, so the fallback seed is the **host** (stable), not the deploy signature. The production-proven reference seeded off a package-version signature; the generic SDK has no package version, so a host-derived seed gives the same stable-across-deploys / no-rotation guarantee with zero config. Pin `KeySeed` / `Key` to fix a key across host changes.
- **The URL universe is the sitemap universe.** Both the sitemap and the push channel walk `SitemapGenerator.entries` (file/overlay pages through `PublicPage.isPublic` + `sitemap = exclude`, unioned with `IContentSource.EnumerateRoutes`, deduped), so they can never disagree about what exists.
- **The file state store survives restarts but resets on container redeploy** (fresh temp dir) — the desired trigger for a clean re-push. Multi-instance deployments use the blob-backed store (`_platform` reserved container) so replicas share resume state.

## Breaking change

None. `withIndexNow` is opt-in; nothing is registered unless composed with `Enabled = true` and a resolvable host. `PublicRenderingNarrativePagePublisher.create` gained a trailing `indexNowService: IIndexNowService option` parameter — the only in-tree caller is `PublicRenderingCompose`; external callers constructing the publisher directly pass `None`.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `Phase 109 IndexNow` suite covers key derivation/resolution, host derivation, the content-based deploy signature, `IndexNowSubmissionState` round-trip + corrupt/legacy tolerance, the resumable submission state machine (fresh, partial-failure persistence, restart resumes only failures, new-signature wipe, fully-done sentinel skip, POST-exception = failure), the file state store (round-trip + corrupt-file tolerance), the `/{key}.txt` endpoint (match + fall-through), the publish-hook single-URL ping (service + publisher), and the compose gate (enabled adds exactly one route; not composed / no host adds none).

## Rollback

Remove the `withIndexNow` call. With nothing in DI the pipeline runs the pre-109 path exactly — no route, no hosted service, no ping. The `IndexNow.fs` file can remain in place harmlessly (no runtime cost when unused).

## See also

- [`docs/migrations/84-ssr-render-cache.md`](84-ssr-render-cache.md) — the publish hook (`PublicRenderingNarrativePagePublisher`) the ping rides on, and the file/blob impl-split pattern the state store mirrors.
- [`docs/platform/dynamic-ssr.md`](../platform/dynamic-ssr.md) — the `IContentSource.EnumerateRoutes` universe the push channel submits.
- IndexNow protocol: <https://www.indexnow.org/>.
