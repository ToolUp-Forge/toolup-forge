# Migration — Phase 150: sitemap-index sharding + universal `<lastmod>`

**Status:** sharding auto-activates only past a configurable threshold (default 50,000 URLs); the universal-lastmod fallback is opt-in. A deployment whose sitemap is below the threshold and which doesn't compose `withSitemapDefaultLastmod` is byte-for-byte pre-150 (GP 11). No consumer action required.

## What changes

`SitemapGenerator` emitted a single `<urlset>`, which the sitemaps.org spec caps at 50,000 URLs / 50 MB uncompressed per file — a hard ceiling for large generated catalogs. Separately, dynamic slugs (and pages with no `PublishedAt`) shipped with no `<lastmod>`, weakening Google's crawl-scheduling signal.

Phase 150 adds:

1. **Auto-sharding + sitemap index (cluster-aware).** Past a configurable threshold (`withSitemapShardThreshold`, default 50,000), `sitemap.xml` becomes a `<sitemapindex>` pointing at `sitemap-<name>.xml` shard files (served by a new `routef "/sitemap-%s.xml"` handler). With a cluster key (`withSitemapClusterKey`), shards group by logical content type (a changed cluster re-fetches only its own child sitemap), each over-threshold cluster sub-sliced numerically; with no cluster key, deterministic numeric slices (`sitemap-1.xml` … `sitemap-N.xml`). Shard membership is **stable across deploys** for a given content set. Below the threshold, `sitemap.xml` stays a single `<urlset>` — byte-for-byte pre-150.

2. **Universal `<lastmod>` (opt-in).** `withSitemapDefaultLastmod date` stamps any entry whose `PublishedAt` is `None` — including dynamic slugs — with `date` (`yyyy-MM-dd`). Default off → signal-less entries emit no `<lastmod>` exactly as today.

3. **Static-export + multi-site coherence.** `StaticExport` writes the index + `sitemap-<name>.xml` shard files (per locale tree, per satellite) when a tree's universe crosses the threshold; the compose-level sharding + default-lastmod knobs flow into the export.

## Diff to apply

None required below the threshold. To opt in:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    // shard along the first path segment past 50k URLs:
    |> PublicRenderingServerApp.withSitemapClusterKey (fun (Slug s) -> s.Split('/').[0])
    // give every URL (incl. dynamic slugs) a lastmod:
    |> PublicRenderingServerApp.withSitemapDefaultLastmod (System.DateTimeOffset.UtcNow)
    // (optional) lower the shard threshold:
    |> PublicRenderingServerApp.withSitemapShardThreshold 25_000)
|> ServerApp.run
```

`withSitemapClusterKey` / `withSitemapShardThreshold` only take effect past the threshold; below it the sitemap stays a single `<urlset>`.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `SitemapSearchIndexTests.fs` ("Phase 150 sitemap sharding + universal lastmod"):
  - below the threshold the handler body is byte-for-byte the pre-150 single `<urlset>`.
  - past the threshold `sitemap.xml` is a `<sitemapindex>` of exactly ⌈N/threshold⌉ numeric shards, each ≤ threshold.
  - `shardUniverse` — deterministic membership across two runs; covers every URL exactly once; cluster-key grouping by content type.
  - `applyDefaultLastmod` fills only `None` lastmods when set; identity when unset.
  - `generateSitemapIndex` emits `<sitemapindex>` + shard `<loc>` + the shard's latest `<lastmod>`.
  - `shardHandler` serves a shard `<urlset>` past the threshold and declines (falls through) below it / for an unknown shard.

## Rollback

Revert the Phase 150 commits. Sharding only ever activates past the threshold and the universal-lastmod fallback is opt-in, so reverting restores the single-`<urlset>` generator. No persisted state; shard files are regenerated, never stored.
