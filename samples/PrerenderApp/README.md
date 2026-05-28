# PrerenderApp — Phase 57 worked example

Minimal end-to-end demonstration of `ClientConfig.PrerenderRoutes` + the FAKE `Prerender` target. Shows how an ads-monetised public-utility SPA wires:

1. **Declared prerender routes** — `/`, `/individual`, `/family`, `/company` plus five SEO landing pages.
2. **Hydration-aware bootstrap** — `Bootstrap.Hydration.run` replacing `Client.run`, with `MetadataHook.install` for SPA navigation.
3. **Prerender entry-point** — `Bootstrap.PrerenderExport.installEntryPoint` so the FAKE target's Node script can reach the renderer.
4. **Server-side middleware** — `PrerenderedRoutesMiddleware` registered ahead of `UseStaticFiles` (already wired by the SDK pipeline; nothing the sample needs to add).
5. **FAKE Prerender target** — `Prerender.registerTarget config options routes` in `Build.fs`.
6. **Sitemap.xml** — a sibling `Sitemap` FAKE target that emits `dist/sitemap.xml` from the same route list, demonstrating how crawl-discovery composes with the prerender pass.

## Layout

```
samples/PrerenderApp/
├── README.md                                 — this file
├── Build.fs                                  — FAKE driver registering Prerender + Sitemap
├── Build.fsproj                              — Build executable
├── PrerenderApp.sln                          — solution wiring
├── src/
│   ├── Shared/
│   │   ├── SharedTypes.fs                    — route declarations (the single source of truth)
│   │   └── Shared.fsproj
│   ├── Server/
│   │   ├── Server.fs                         — minimal Giraffe server composition
│   │   └── Server.fsproj
│   └── Client/
│       ├── Client.fs                         — ClientConfig + bootstrap + prerender entry
│       └── Client.fsproj
└── (no node_modules / dist / output committed — generated at build time)
```

## Run

The sample is illustrative. To take it from skeleton to runnable in a fresh consumer:

```powershell
# From the consumer repo root
dotnet tool restore                                       # restore fable + fantomas
dotnet run --project Build.fsproj -- Build                # restore + compile .NET
cd src/Client && npm install                              # restore Vite + React deps
cd ../..
dotnet run --project Build.fsproj -- Prerender            # emit dist/*.html
dotnet run --project Build.fsproj -- Sitemap              # emit dist/sitemap.xml
```

## Ports (website class)

The sample binds `13930` (server) and `23930` (Vite), declared in its `launchSettings.json` / `vite.config.mts`. Change them to whatever is free on your machine if they clash.

## Sitemap.xml integration

Search engines discover prerendered routes through one of:
- Internal links in the prerendered home page (crawler-friendly anchor tags).
- An XML sitemap referenced from `/robots.txt`.

The sample emits `dist/sitemap.xml` as a sibling FAKE target — same route list, transformed into the `<urlset>` format crawlers expect. See `Build.fs` `Sitemap` target.

## What the sample does NOT cover

- **Real ad-panel wiring** — `AdPanel` substrate is Phase 60; this sample focuses on the indexable-first-paint surface only.
- **`robots.txt`** — consumers manage that file directly via their static-asset host's mechanisms.
- **Per-locale prerender** — composes with Phase 12a (i18n) when both ship; out of v1 Phase 57 scope.
- **Dynamic-SPA SSR per request** — Phase 57 covers static prerender only.

## See also

- [`docs/platform/prerender.md`](../../docs/platform/prerender.md) — narrative + authoring rules.
- [`docs/migrations/57-static-prerender.md`](../../docs/migrations/57-static-prerender.md) — diff-to-apply + verification + rollback.
- [`src/ToolUp.Platform.Client/Client/Bootstrap/PrerenderExport.fs`](../../src/ToolUp.Platform.Client/Client/Bootstrap/PrerenderExport.fs) — SDK-side renderer entry-point.
- [`src/ToolUp.Platform.Build/Build/SDK.PrerenderTarget.fs`](../../src/ToolUp.Platform.Build/Build/SDK.PrerenderTarget.fs) — FAKE target factory + Node script.
