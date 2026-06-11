# StaticExport for ToolUp.PublicRendering (consumer migration)

**What changes.** `ToolUp.PublicRendering` gains a build-time static-export terminus alongside the existing live-server `run` path. `PublicRenderingServerApp.exportStatic outputDir` renders every page exposed by `IPublicContentApi.ListPages ""` through the same layout map the live server uses and writes a static HTML tree under `outputDir` — hostable on Azure Static Web Apps, Netlify, GitHub Pages, S3 + CloudFront, or any other static-file host with no .NET runtime in production.

**Scope.** Additive only. The existing `PublicRenderingServerApp.run` path is unchanged byte-for-byte; consumers that don't call `exportStatic` see zero behavioural change. The new module sits in `ToolUp.PublicRendering` (no new package, no new dependency).

## Diff to apply

The compose chain mirrors `run` exactly — the only divergence is the terminus.

```fsharp
// Before — live SSR server only:
PublicRenderingCompose.PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLogger logger
|> PublicRenderingServerApp.withLayout (LayoutName "page") PageLayout.render
|> PublicRenderingServerApp.withLayout (LayoutName "doc") DocLayout.render
|> PublicRenderingServerApp.run
```

```fsharp
// After — same chain, build-time static export:
PublicRenderingCompose.PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLogger logger
|> PublicRenderingServerApp.withLayout (LayoutName "page") PageLayout.render
|> PublicRenderingServerApp.withLayout (LayoutName "doc") DocLayout.render
|> PublicRenderingServerApp.exportStatic "dist"
|> Async.RunSynchronously
|> printfn "Static export: %d pages written"
```

Typically wired as a FAKE target in the consumer's `Build.fs`:

```fsharp
Target.create "StaticExport" (fun _ ->
    let outputDir = Path.GetFullPath "dist"
    let config = (* same ServerConfig used at runtime *)
    let logger = ConsoleLogger.ConsoleLogger() :> ILogger
    PublicRenderingCompose.PublicRenderingServerApp.create ()
    |> PublicRenderingServerApp.withConfig config
    |> PublicRenderingServerApp.withLogger logger
    |> PublicRenderingServerApp.withLayout (LayoutName "page") Layouts.PageLayout.render
    |> PublicRenderingServerApp.withLayout (LayoutName "doc") Layouts.DocLayout.render
    |> PublicRenderingServerApp.exportStatic outputDir
    |> Async.RunSynchronously
    |> printfn "Static export: %d pages written")

"SyncDocs" ==> "Tailwind" ==> "StaticExport" |> ignore
```

## Output layout

```
<outputDir>/
├── index.html                       — root `index` slug
├── <slug>/index.html                — every other page (directory-index shape)
├── docs/forms/concepts/index.html   — nested slugs preserve their tree
├── sitemap.xml                      — byte-identical to live SitemapGenerator output
├── favicon.ico, css/, img/, ...     — verbatim copy of ServerConfig.PublicPath
└── ...
```

Directory-index shape (`/<slug>/index.html`) works across every static host without per-host rewrite rules — Azure SWA, Netlify, GitHub Pages, S3 + CloudFront all serve `/<slug>/` from `/<slug>/index.html` by default.

## Verification

1. `dotnet build` clean — `StaticExport.fs` compiles alongside the rest of `ToolUp.PublicRendering`.
2. Wire `exportStatic` into the consumer's `Build.fs` per the snippet above; run `dotnet run --project Build.fsproj -- StaticExport`.
3. Inspect `dist/` — every page in `content/` has an `index.html`; `dist/sitemap.xml` exists and is byte-identical to the live SSR `/sitemap.xml` for the same content tree (`curl http://localhost:<port>/sitemap.xml > sitemap-live.xml; diff dist/sitemap.xml sitemap-live.xml` produces no output).
4. Serve `dist/` locally (`npx serve dist` or `python -m http.server`) and navigate every section page — output should be visually identical to the live SSR server.
5. Static hosts: deploy `dist/` to Azure Static Web Apps (or chosen host) — every page renders, navigation works, sitemap is reachable.

## Phase 93 — operational extensions (`exportStaticWith` / `runWith`)

`exportStatic` stays the byte-compatible single-tree default. `PublicRenderingServerApp.exportStaticWith options outputDir` (and the lower-level `StaticExport.runWith`) add opt-in operational capabilities via a `StaticExportOptions` record — `StaticExportOptions.defaults` reproduces the single-tree behaviour exactly (GP 11):

- **Per-locale export** — `withLocales [ "en"; "fr" ]` renders each page into its own `<outputDir>/<locale>/…` subtree (per-locale `<html lang>` on Narrative bodies + per-locale `sitemap.xml`). A single / empty locale list is the unchanged single tree. Content *translation* is the deployment's concern (wire a locale-specific content API); this stamps the language and lays out the trees — closing the "per-locale prerender" gap below.
- **Host-config emission** — `withHostConfigs [ AzureStaticWebApps; Netlify ]` writes `staticwebapp.config.json` (Azure SWA) and/or `_redirects` + `_headers` (Netlify / Cloudflare) at the export root, derived from the redirect map (file-loaded `redirects.csv` + compose-registered redirects) and the export's default `withCachePolicy`. So a static-hosted export carries its redirects + cache headers — closing the "redirects not translated" gap below.
- **Build-time link / asset check** — `withLinkCheck` scans the emitted HTML for internal `href` / `src` references that don't resolve to a file on disk and logs each dead one (catches broken links before deploy).
- **Gated-page exclusion** — unchanged from [Phase 86](86-gated-ssr.md): only `Audience = Public` pages are written, to every tree, the sitemap, and the host-config.

```fsharp
let options =
    StaticExportOptions.defaults
    |> StaticExportOptions.withLocales [ "en"; "fr" ]
    |> StaticExportOptions.withHostConfigs [ AzureStaticWebApps ]
    |> StaticExportOptions.withCachePolicy (Cache(300, true))
    |> StaticExportOptions.withLinkCheck

app |> PublicRenderingServerApp.exportStaticWith options "dist"
```

Verify: `dotnet run --project Build.fsproj -- VerifyAll` — the `PublicRendering — Phase 93 static export` suite covers the host-config generators, internal-ref extraction, dead-link detection, the end-to-end per-locale + gated-exclusion + host-config run, and the default single-tree (GP 11) path.

## What this still does NOT include

- **Per-locale content translation.** The per-locale export lays out the trees + stamps `<html lang>`; translating the *content* is the deployment's responsibility (supply a locale-specific `IPublicContentApi`).
- **Dynamic island injection.** Static export emits whatever the layout function produces, which is layout-author choice.

## Rollback

Drop the `StaticExport` FAKE target from `Build.fs`; revert any deployment-pipeline edits (e.g. switch CI/CD back to a server-bundle publish + App Service deploy). The SDK addition is additive — no consumer-side rollback needed beyond removing the call site.

## Consumers

The migration applies only to SSR-only **website-class** deployments — consumers whose `ServerConfig.PublicRendering = EnabledPublicRendering _` AND who want to host on a static-file CDN instead of an always-on App Service / container. Application-class deployments (auth, AI streaming, per-user state) are intrinsically dynamic and cannot static-export.

`toolup-forge-io` is the first consumer; it adopts in the same change set that lands this SDK addition.
