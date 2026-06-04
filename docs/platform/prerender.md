# SEO for SPA-shaped forge apps

ToolUp Platform supports two distinct surfaces for indexable, crawlable content:

| Surface | Use when | First paint | Hydration |
|---|---|---|---|
| **`IPublicContentApi` SSR** (the `ToolUp.PublicRendering` companion) | Marketing site, docs site, blog — content is markdown-driven, no SPA shell on the page | Server-rendered per request via Giraffe.ViewEngine | None — there is no SPA on the page |
| **`ClientConfig.PrerenderRoutes`** (build-time static prerender) | Public-utility app with an SPA shell — home page + landing pages need to be indexable, but the rest of the app stays SPA | Build-time prerender of declared routes (static `dist/*.html`) | React `hydrateRoot` mounts the SPA on top of the prerendered DOM |

Both surfaces are opt-in. SPA-only deployments (most internal / tenant-shaped apps) stay byte-for-byte unchanged.

## When this matters

Most ToolUp deployments are internal multi-tenant apps where SEO is irrelevant — the user signs in, the SPA renders, search engines never see it. For those, ignore this chapter; `PrerenderRoutes = []` (the default) preserves the pure-SPA path.

This chapter applies to **ads-monetised public-utility deployments** — the public-facing single-page calculators, free-tool sites, and consumer-grade utility apps that depend on organic traffic for the floor of their ad revenue. For those, the home page and a small set of SEO landing pages MUST be indexable HTML. Without prerendering the indexable first paint is `<div id="elmish-app"></div>` — empty, no metadata, no chance of ranking.

> SEO matters more for ad revenue than the ads themselves. A 30% organic-traffic lift from indexable landing pages compounds against the AdSense RPM; lose the SEO floor and the ad-panel optimisation matters very little.

## Build-time prerender architecture

```
build time                                         runtime
──────────                                         ───────

src/Client/Client.fs                                Browser GET /individual
    │                                                 │
    │  ClientConfig.PrerenderRoutes = [ ... ]          ▼
    │                                              ┌─────────────────────────────────┐
    ▼                                              │ PrerenderedRoutesMiddleware     │
dotnet run -- Prerender                            │ — maps /individual              │
    │                                              │   → dist/individual.html        │
    │  FAKE target reads PrerenderRoutes,           │ — short-circuits before the    │
    │  invokes react-dom/server's renderToString    │   UseStaticFiles SPA fallback  │
    │  on each route's Elmish view tree,             └────────────────┬────────────────┘
    │  writes dist/{slug}.html with                                  │
    │  <head> populated + the                                        ▼
    │  <meta name="toolup-prerendered">                          dist/individual.html
    ▼                                                  ┌─────────────────────────────────┐
dist/                                                  │ <head>                          │
├── index.html         ← / (home)                      │   <title>...</title>            │
├── individual.html    ← /individual                   │   <meta name="description"...> │
├── family.html        ← /family                       │   <meta property="og:..."...>   │
└── ...                                                │   <meta name="toolup-prerendered" content="true"> │
                                                       │ </head>                         │
                                                       │ <body>                          │
                                                       │   <div id="elmish-app">         │
                                                       │     ... prerendered React tree...│
                                                       │   </div>                        │
                                                       │   <script src="/output/Client.js"></script>│
                                                       │ </body>                         │
                                                       └────────────────┬────────────────┘
                                                                        │
                                                                        ▼
                                                       ┌─────────────────────────────────┐
                                                       │ Client bundle loads.            │
                                                       │ Bootstrap.Hydration.run finds   │
                                                       │ the prerender marker.           │
                                                       │ Mounts via React's hydrateRoot  │
                                                       │ (NOT createRoot).               │
                                                       │ Bootstrap.MetadataHook.install  │
                                                       │ wires title / meta updates for  │
                                                       │ subsequent SPA navigation.       │
                                                       └─────────────────────────────────┘
```

## Authoring rules

**1. Routes are declarative.** Routes are not pulled from your Elmish router; they are an explicit `PrerenderRoute list` you maintain. Each entry pins the URL path, the optional init-state key for the renderer (so the view tree can be parameterised), and the `<head>` metadata.

```fsharp
let prerenderRoutes : PrerenderRoute list = [
    {
        Path = "/"
        InitStateKey = None
        Meta = {
            Title = "Free Tax Calculator — quick estimates with no signup"
            Description = "Estimate your 2025–26 tax bill in 30 seconds. No login required."
            OpenGraph = Map.ofList [
                "title", "Free Tax Calculator"
                "type", "website"
                "image", "https://your-deploy/og/home.png"
                "url", "https://your-deploy/"
            ]
            JsonLd = None
        }
    }
    // ... declare every route you want indexable.
]
```

The list is opt-in per `ClientConfig` field; an empty list (default) preserves SPA-only behaviour.

**2. Keep the prerender set small.** Aim for ≤ 10 prerendered routes per deployment. The SEO payoff peaks fast — the home page + 3–5 high-intent landing pages capture the majority of indexable surface. The deeper / personalised / dynamic routes stay SPA-only; users discover them via crawler-friendly internal links from the prerendered set.

**3. State must be deterministic.** The prerender pass runs the same view function the SPA does, but at build time, with the declared `InitStateKey`. Any non-determinism in the view (`DateTime.Now`, `Math.random`, locale-dependent formatting at module-init time) will produce different HTML across runs AND will trigger React hydration mismatch when the SPA mounts on top with the runtime-actual values. Pin time-of-day formatting, avoid randomness in init, use locale-stable string conversion until after hydration.

**4. Metadata is per-route, not global.** Avoid factoring metadata to a single `defaults` record everyone shares. AdSense, Twitter, Facebook all crawl per-page metadata; the SEO win evaporates if every page declares the same title.

**5. Don't prerender token-gated routes.** Routes registered via `ClientConfig.PublicEntryDispatchers` (e.g. `/r/{token}` for publishable form embeds) are token-specific and should not appear in `PrerenderRoutes`. The hydration bootstrap skips dispatcher routes on the prerender branch by design.

## FAKE seam vs Vite seam

The prerender substrate supports two equivalent build-time execution paths. Both produce identical output (same HTML, same marker, same metadata). Pick by deployment convention.

### FAKE seam — `dotnet run -- Prerender`

```fsharp
// Build.fs
open ToolUp.Platform.Build

let config = { BuildConfig.defaults with Port = 5000 }

[<EntryPoint>]
let main args =
    init args
    registerTargets config
    Prerender.registerTarget config prerenderRoutes  // prerender FAKE target factory
    execute args
```

`dotnet run -- Prerender` runs after the standard `Fable` target. The factory:
- Spawns Node.js with a small script that imports the consumer's Fable output.
- Imports the SDK's prerender entry-point (which calls `react-dom/server`'s `renderToString` against the Elmish view tree).
- Iterates `prerenderRoutes`, writes `dist/{slug}.html` per route, populated `<head>` + marker.

Best when your deployment pipeline is `dotnet`-driven (Azure DevOps, Octopus, GitHub Actions with `dotnet run` steps). Composes cleanly with the rest of the FAKE pipeline (`Bundle`, `Verify`, `Pack`, `Deploy-CD`).

### Vite seam — post-build plugin

```ts
// vite.config.mts
import { defineConfig } from "vite";
import { toolupPrerender } from "./scripts/toolup-prerender";  // your wrapper

export default defineConfig({
    plugins: [
        toolupPrerender({
            routes: [
                { path: "/", title: "...", description: "..." },
                // ...
            ],
            entry: "output/Client.js",
            outDir: "dist",
        }),
    ],
});
```

Best when your deployment pipeline is Vite-driven (Vercel-style "build, push, deploy" with no `dotnet` step in the deploy stage). The Vite plugin runs prerendering in `closeBundle()` after the standard Fable + Vite chain has populated `dist/`.

Both seams call into the same SDK-side prerender entry-point — the `<head>` content, the marker, the body HTML are byte-for-byte identical regardless of which seam invoked the renderer.

## Compose-time wiring

```fsharp
// src/Server/Server.fs — server-side middleware

app.UseMiddleware<PrerenderedRoutesMiddleware>(config) |> ignore
app.UseStaticFiles(...)  // standard SDK pipeline — keep as is
```

```fsharp
// src/Client/Program.fs — client-side bootstrap

// Replaces `Client.run config modules`:
ToolUp.Platform.Bootstrap.Hydration.run config modules

// Optional but recommended for ads-monetised deployments:
ToolUp.Platform.Bootstrap.MetadataHook.install config.PrerenderRoutes
```

`Bootstrap.Hydration.run` is a drop-in replacement for `Client.run`: it delegates directly when the prerender marker is absent (most routes for most deployments), and mounts via `hydrateRoot` only when the marker is present. `MetadataHook.install` is idempotent and a no-op when `PrerenderRoutes = []`.

## Verification

After wiring up:

1. **Build clean.** `dotnet build` succeeds. New default `PrerenderRoutes = []` keeps existing consumers compiling unchanged.

2. **Prerender produces files.** `dotnet run -- Prerender` writes one HTML file per declared route into `dist/`. Spot-check `dist/index.html` — `<head>` carries title + description + OG tags + `<meta name="toolup-prerendered" content="true">`.

3. **Browser hydrates without mismatch warnings.** Load a prerendered route. Open DevTools Console — React should not log `Warning: Text content did not match` or `Warning: Expected server HTML to contain a matching <div>`. The DevTools Network panel shows the prerendered HTML as the first response (populated body, not empty SPA shell).

4. **SPA navigation updates metadata.** With at least two prerendered routes, navigate from one to the other via the SPA (sidebar / link). Inspect `<title>` and OG tags — they should swap to the destination route's metadata.

5. **Googlebot sees indexable HTML.** `curl -A "Googlebot/2.1" https://your-deploy/`. Response body contains the populated `<head>` + the prerendered body content — not the empty SPA shell.

6. **Stock-SPA behaviour preserved.** A deployment with `PrerenderRoutes = []` should produce no extra `.html` files beyond `dist/index.html`, and every route serves the standard SPA shell (the `PrerenderedRoutesMiddleware` falls through unchanged).

## See also

- [`docs/migrations/57-static-prerender.md`](../migrations/57-static-prerender.md) — diff to apply, rollback procedure.
- [`docs/migrations/38-public-rendering-surface-ipubliccontentapi-ssr.md`](../migrations/) (when added) — the orthogonal full-SSR content-site surface (`IPublicContentApi`). `IPublicContentApi` is the right answer for marketing / docs sites with no SPA; `PrerenderRoutes` is the right answer for SPA-shaped apps that need indexable landing pages.
- [`Client/Bootstrap/Hydration.fs`](../../src/ToolUp.Platform.Client/Client/Bootstrap/Hydration.fs) — the hydration-aware bootstrap.
- [`Client/Bootstrap/MetadataHook.fs`](../../src/ToolUp.Platform.Client/Client/Bootstrap/MetadataHook.fs) — the per-route metadata updater.
- [`Server/Static/PrerenderedRoutesHandler.fs`](../../src/ToolUp.Platform.Server/Server/Static/PrerenderedRoutesHandler.fs) — the Kestrel handler.
