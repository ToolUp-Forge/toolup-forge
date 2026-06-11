# Migration — multi-host site registry (`withSite`)

## What changes

`ToolUp.PublicRendering` gains an opt-in multi-site tier: one instance can
serve N independent websites on N domains. New surface:

- `PublicSiteDef` + `PublicSite.create` — a satellite site (hosts, base URL,
  content root, optional layouts/redirects).
- `PublicRenderingServerApp.withSite` — register a satellite.
- `SiteRegistry` (DI singleton when sites exist) — host → site resolution for
  custom handlers.
- `PublicPageHandler.handlerKeyed` — page handler with a site-namespaced
  render-cache key (`handler` delegates with no prefix).
- `MultiSiteConfigValidator` (`public-rendering-multi-site`) — startup
  preflight over the registered site set.

**No consumer action is required.** Zero `withSite` calls → byte-for-byte the
prior single-site behaviour (GP 11): same handlers, same DI registrations,
same render-cache keys, nothing extra allocated (GP 13).

## Diff to apply (only if adopting multi-site)

```fsharp
// Before — one site per process:
PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config        // PublicBaseUrl = main origin
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.run

// After — same default site, plus satellites:
PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
|> PublicRenderingServerApp.withSite
    (PublicSite.create "docs" [ "docs.example.org" ]
        "https://docs.example.org" (ContentRoot docsContent))
|> PublicRenderingServerApp.run
```

Per-site scope: page resolution, layouts (shared fallback), redirects,
`sitemap.xml`, narrative export, preview, render-cache namespacing. Feeds,
search, taxonomy, IndexNow, static export, entity overlay, content sources,
and AI publish stay default-site in v1. See
[`docs/platform/multi-site-public-rendering.md`](../platform/multi-site-public-rendering.md).

## Verification

1. `dotnet build ToolUp.Forge.sln` — clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "MultiSite"` — green.
3. Single-site deployments: startup log byte-for-byte vs prior version; no
   `public-rendering-multi-site` validator line appears.
4. Multi-site deployments: `curl -H "Host: docs.example.org" http://localhost:<port>/`
   serves the satellite index; `curl -H "Host: unknown.example" …` serves the
   default site; `/sitemap.xml` per host carries that host's origin in `<loc>`.

## Rollback

Remove the `withSite` calls — the registry, validator, and host dispatch all
disappear (they exist only when sites are registered). No data migration:
satellite content roots are plain markdown directories.
