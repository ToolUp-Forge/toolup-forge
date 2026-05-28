# Phase 57 — Static-prerender pass for Elmish SPAs (`ClientConfig.PrerenderRoutes`)

**What changes.** SDK consumers can declare a list of `PrerenderRoute` entries on `ClientConfig`; the build-time FAKE `Prerender` target emits a static `dist/{slug}.html` per route with the route's `<head>` metadata (title / description / OG tags / optional JSON-LD) populated and the `<meta name="toolup-prerendered" content="true">` marker injected. At runtime the client bootstrap detects the marker and mounts React via `hydrateRoot` instead of `createRoot`, preserving the prerendered first paint. A new server middleware maps incoming GET `/some/route` to the corresponding `dist/some-route.html` when one exists.

Net effect: the home page + declared SEO landing pages ship indexable HTML for crawlers (search engines, social-share scrapers, AdSense's contextual targeting) without surrendering the SPA shape for the rest of the app. Stock SPA deployments (`PrerenderRoutes = []`) are byte-for-byte unchanged.

## Why this exists

`ToolUp.Platform.Public` (Phase 38) covers **content-site shapes** — markdown-driven marketing sites rendered server-side via Giraffe.ViewEngine, full SSR per request. It does NOT cover **SPA prerendering** — running the existing Elmish view tree against declared routes at build time to emit indexable HTML for an SPA-shaped app.

Two different surfaces, both warranted:
- Phase 38 = SSR-only marketing site, no SPA hydration.
- Phase 57 = SPA + indexable home + landing pages, SPA hydrates on top.

Ads-monetised public-utility deployments need the Phase 57 shape: the home page + a handful of SEO landing pages must be indexable HTML, the rest of the app stays SPA. "SEO matters more for ad revenue than the ads themselves" — load-bearing for any public-utility deployment.

## Diff to apply

### 1. Declare prerender routes on `ClientConfig`

```fsharp
// src/Client/Client.fs (or wherever the consumer builds ClientConfig)

open ToolUp.Platform

let routes : PrerenderRoute list = [
    {
        Path = "/"
        InitStateKey = None
        Meta = {
            Title = "Acme Public Calculator — free online tool"
            Description = "Free calculator for X. No signup required."
            OpenGraph =
                Map.ofList [
                    "title", "Acme Public Calculator"
                    "description", "Free calculator for X."
                    "type", "website"
                    "image", "https://acme.example/og/home.png"
                ]
            JsonLd = None
        }
    }
    {
        Path = "/about"
        InitStateKey = None
        Meta = PrerenderMeta.basic "About — Acme" "What Acme does, who it serves, how it works."
    }
    // ... up to ~10 routes for v1; more is supported but the SEO win
    //     drops off fast once the long tail of pages requires the SPA
    //     anyway.
]

let config = {
    ClientConfig.defaults with
        Modes = ...
        AppName = "Acme Calculator"
        PrerenderRoutes = routes
}
```

### 2. Swap the client bootstrap to the hydration-aware one

```fsharp
// src/Client/Program.fs (or wherever Client.run is called today)

// Before:
ToolUp.Platform.Client.run config modules

// After:
ToolUp.Platform.Bootstrap.Hydration.run config modules
```

`Bootstrap.Hydration.run` is a drop-in replacement: when the prerender marker is absent (the byte-for-byte common case for any route NOT declared as a prerender target) it delegates to `Client.run` directly. When the marker is present, it mounts React via `hydrateRoot` against the existing DOM.

If you also want per-route metadata to update on in-SPA navigation between prerendered routes (recommended for AdSense contextual targeting):

```fsharp
// After `Bootstrap.Hydration.run config modules`:
ToolUp.Platform.Bootstrap.MetadataHook.install config.PrerenderRoutes
```

`MetadataHook.install` is idempotent and a no-op when `PrerenderRoutes = []`, so calling it unconditionally is safe.

### 3. Wire the server middleware

```fsharp
// src/Server/Server.fs

// Before — only UseStaticFiles serves dist/*.html, requested by their
// full filename:
app.UseStaticFiles(...)

// After — PrerenderedRoutesMiddleware sits ahead of UseStaticFiles so
// GET /some/route resolves to dist/some-route.html when one exists,
// short-circuiting before the SPA index.html fallback:
app.UseMiddleware<PrerenderedRoutesMiddleware>(config) |> ignore
app.UseStaticFiles(...)
```

Behaviour with no prerendered files present (the byte-for-byte SPA-only case): the middleware looks up the file, finds nothing, calls `next.Invoke(ctx)`, and the request falls through to the existing SPA-index fallback unchanged.

### 4. Wire the FAKE `Prerender` target into `Build.fs`

```fsharp
// Build.fs
open ToolUp.Platform.Build
open ToolUp.Platform.Prerender

let routes : PrerenderRoute list = [ ... ]  // your declared routes

[<EntryPoint>]
let main args =
    init args
    registerTargets buildConfig
    // Phase 57 follow-up — registers the `Prerender` FAKE target.
    // Defaults match the SDK's vite.config.mts conventions
    // (Fable output at `src/Client/output/Client.js`, dist at
    // `src/Client/dist`, script served at `/output/Client.js`).
    // Override via `{ PrerenderTargetOptions.defaults with ... }`
    // for non-standard layouts.
    registerTarget buildConfig PrerenderTargetOptions.defaults routes
    execute args
```

```fsharp
// src/Client/Client.fs — register the prerender entry-point so
// the FAKE target's Node script can reach the renderer. No-op in
// the browser; safe to call unconditionally.
open ToolUp.Platform.Bootstrap

PrerenderExport.installEntryPoint config modules

if PrerenderExport.isBrowser () then
    MetadataHook.install config.PrerenderRoutes
    Hydration.run config modules
```

Then `dotnet run -- Prerender` runs the chain `Build → Fable → Prerender`, writing one `dist/{slug}.html` per declared route.

**Status (as of the FAKE-target follow-up ship 2026-05-27):** the `ClientConfig.PrerenderRoutes` field, hydration bootstrap, metadata hook, server middleware, **and** the FAKE `Prerender` execution pass (`ToolUp.Platform.Prerender.registerTarget` factory + `Client/Bootstrap/PrerenderExport.fs` entry-point + self-contained Node script + `samples/PrerenderApp/` worked example + `Tests/InProcess/PrerenderDeterminismTests.fs` byte-stability + hydration-marker contract tests) all ship in the SDK.

### Alternative: Vite plugin path

For consumers preferring the JS toolchain seam over the FAKE seam, a Vite plugin can run prerendering as a post-build hook. The plugin reads the consumer's compiled Fable output, imports the SDK's prerender entry-point, invokes the renderer per route, and emits `dist/{slug}.html`.

Both paths produce equivalent output (same HTML structure, same marker, same metadata). Consumers pick by convention — `dotnet`-shop deployments tend to prefer the FAKE seam; consumers with deep `vite.config.mts` customisation may prefer the JS seam.

## Verification

After applying the diff:

1. **Build:** `dotnet build` should succeed without warnings. The new `ClientConfig.PrerenderRoutes` field has a default of `[]`, so existing consumers that don't reference it stay compatible.

2. **SPA-only behaviour preserved:** consumers who do NOT declare any `PrerenderRoutes` (or who declare `[]`) should see byte-for-byte identical runtime behaviour — the hydration bootstrap finds no marker, calls `Client.run` directly, and the server middleware looks up no files and falls through to `UseStaticFiles`.

3. **Prerendered route hydrates without warnings:** declare one route, run the prerender target, load the resulting page in a browser. Open DevTools Console; React should not log a hydration-mismatch warning. The DevTools Network panel should show the prerendered HTML as the first response.

4. **Metadata updates on SPA navigation:** with two prerendered routes declared and `MetadataHook.install` wired, navigate between them via the SPA. The `<title>` and `<meta>` tags should swap. The `og:` tags should update for AdSense / social-share scrapers.

5. **Googlebot fetch:** `curl -A "Googlebot/2.1" https://your-deploy/`. The response body should contain the prerendered metadata (title, description, OG tags) — not the empty SPA shell.

## Rollback

The phase is opt-in throughout. To roll back:

1. Set `ClientConfig.PrerenderRoutes = []` (or remove the field entirely; the default is `[]`).
2. Swap `Bootstrap.Hydration.run` back to `Client.run`.
3. Remove `MetadataHook.install` from the bootstrap.
4. Drop `app.UseMiddleware<PrerenderedRoutesMiddleware>` from the server pipeline.
5. Delete any `dist/*.html` files emitted by the FAKE target (other than the SPA `index.html`).

The runtime returns to byte-for-byte pure-SPA behaviour.

## Out of scope (not delivered by this phase)

- True request-time SSR (server renders fresh HTML per request, e.g. for deep-link routes like `/calculator/2009/50000`). Phase 38 covers content-site SSR; dynamic-SPA SSR is a larger future surface. Static prerender covers the SEO 80%.
- Per-route `robots.txt` configuration — consumers handle this via their static-asset host's standard mechanism.
- Internationalised prerender output (per-locale variants of each route). Composes with Phase 12a (i18n infrastructure) when both ship; out of v1 scope.
- AMP / accelerated mobile pages — not a 2026 SEO priority.
