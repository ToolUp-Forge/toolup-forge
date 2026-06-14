# Migration — multi-host SEO + export surfaces (per-site IndexNow / static export / feeds)

_Roadmap phase 146 (renumbered from 115 on the 2026-06-14 sync — 114/115 were claimed upstream by the audit-event-registry / index-lifecycle phases; the implementing forge commit subjects still read "Phase 115")._

## What changes

The multi-site tier (`withSite`) gains the outbound / build-time surfaces:

- **Per-site IndexNow.** When `withIndexNow` is composed alongside `withSite`
  registrations, each satellite gets its own declared host (from its
  `BaseUrl`), its own stable host-seeded ownership key — `/{key}.txt`
  answered only on that site's hosts — and its own resumable startup
  submission over its own page universe (per-site file-backed state).
  The `IIndexNowService` DI singleton remains the default site's service;
  an operator-supplied `StateStore` applies to the default site only.
- **Multi-site static export.** `PublicRenderingServerApp.exportStaticAll` /
  `exportStaticAllWith`: the default site exports to the output root
  (byte-for-byte `exportStaticWith`), each satellite to
  `<out>/sites/<Name>/` with its own content, layouts (shared fallback),
  redirects, sitemap origin, and host-config emission.
- **Per-site Atom feeds.** `PublicSiteDef.Feeds` mount host-gated against the
  site's own content API. Compose-level `withFeed` registrations now serve
  the **default site's hosts only** when sites exist (with no sites they are
  untouched).
- **`SiteGate`** (`forSite` / `forDefaultSite`) — reusable host-gating
  combinators over the `SiteRegistry`, exposed for consumer handlers.

**No consumer action is required.** With zero `withSite` calls, IndexNow,
static export, and feeds behave byte-for-byte as before (GP 11 / GP 13).

## Diff to apply (only if adopting)

```fsharp
// Per-site feed + one IndexNow call covering every site:
|> PublicRenderingServerApp.withSite
    { PublicSite.create "docs" [ "docs.example.org" ]
        "https://docs.example.org" (ContentRoot docsContent) with
        Feeds = [ { NarrativeFeedConfig.defaults with Title = "Docs"; SelfUrl = "/feed.atom" } ] }
|> PublicRenderingServerApp.withIndexNow IndexNowOptions.enabled

// Build-time multi-site export (replaces exportStaticWith when sites exist):
PublicRenderingServerApp.exportStaticAllWith options "dist" app
```

## Verification

1. `dotnet build ToolUp.Forge.sln` — clean.
2. `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "MultiSite"` — green.
3. Multi-site + IndexNow: `curl -H "Host: docs.example.org" http://localhost:<port>/<siteKey>.txt`
   returns that site's key; the default key answers only on unclaimed hosts.
4. `exportStaticAllWith` output: `<out>/index.html` (default) +
   `<out>/sites/<name>/index.html` per satellite; each tree's `sitemap.xml`
   carries that site's origin in `<loc>`.

## Rollback

Remove the `withSite` calls (all per-site surfaces disappear) or pin the
prior package version. Per-site IndexNow state files
(`%TEMP%/toolup-indexnow-last-submission-<site>.json`) are disposable.
