# Multi-site public rendering (one instance, N domains)

One `ToolUp.PublicRendering` instance can serve several independent websites on
several domains from a single process. Each registered site claims a set of
host names and brings its own content root, public base URL, and (optionally)
layouts and redirects; requests are matched on the `Host` header at the
handler chain. Hosts matching no registered site — including every request in
a deployment that registers no sites — serve the **default site**: the
`ServerConfig`-level `EnabledPublicRendering` content root, the
compose-registered layouts, and `ServerConfig.PublicBaseUrl`. The feature is
strictly additive: a pipeline that never calls `withSite` behaves
byte-for-byte as before (GP 11) and allocates nothing for it (GP 13).

## Registering sites

```fsharp
open ToolUp.Platform
open ToolUp.PublicRendering

let config = {
    ServerConfig.defaults with
        Port = 4010
        // The DEFAULT site — served on every host no satellite claims.
        PublicBaseUrl = Some "https://example.com"
        PublicRendering = EnabledPublicRendering (ContentRoot mainContent)
}

PublicRenderingServerApp.create ()
|> PublicRenderingServerApp.withConfig config
|> PublicRenderingServerApp.withLayout (LayoutName "page") Layouts.page
// Satellite 1 — its own content + base URL, inheriting the shared layouts.
|> PublicRenderingServerApp.withSite
    (PublicSite.create "docs" [ "docs.example.org"; "www.docs.example.org" ]
        "https://docs.example.org" (ContentRoot docsContent))
// Satellite 2 — bespoke layouts.
|> PublicRenderingServerApp.withSite
    { PublicSite.create "blog" [ "blog.example.net" ]
        "https://blog.example.net" (ContentRoot blogContent) with
        Layouts = Map [ LayoutName "page", Layouts.blogPage ] }
|> PublicRenderingServerApp.run
```

Front the process with any host-routing reverse proxy (Caddy, nginx,
Cloudflare, IIS) — or none, for a single-port deployment where DNS for every
domain points at the same listener.

## What is per-site

| Surface | Behaviour |
|---|---|
| Page resolution (`GET /{slug}`) | Per-site content root (`content/**/*.md`) |
| Layouts | Site's own when declared; otherwise the shared `withLayout` registrations |
| Redirects | Site's `redirects.csv` + `PublicSiteDef.Redirects` |
| `sitemap.xml` | Per-site page universe, absolute `<loc>` on the site's `BaseUrl` |
| Narrative export (`?format=`) | Per-site |
| Preview (`/preview`) | Per-site |
| Render cache (when composed) | Shared `IRenderCache`, entries namespaced by site name — two sites sharing a slug never share an entry |

## What stays on the default site (v1 scope)

The entity-store overlay (runtime-edited pages), request-time
`IContentSource`s, Atom feeds, `/search`, `/tag/{slug}` taxonomy, IndexNow,
static export, and the AI publish path remain **default-site** surfaces.
Satellite sites are markdown-file-backed — the website-class home-page /
marketing-site shape. If a satellite needs the CMS tier, give it its own
deployment (or wait for the per-site SEO/export extension, which lifts
IndexNow / static export / feeds per-site).

## Host matching

- Case-insensitive on `HttpContext.Request.Host.Host`; the port never
  participates.
- List `www.` variants explicitly (`[ "example.com"; "www.example.com" ]`).
- A host claimed by two sites fails startup preflight
  (`public-rendering-multi-site` validator) — dispatch would be ambiguous.
- The validator also errors on a site with no hosts or no resolvable layouts,
  and warns on a non-absolute `BaseUrl` or a missing content-root directory.

## Resolving the active site in custom handlers

The compose registers the `SiteRegistry` as a DI singleton when sites exist:

```fsharp
let myHandler: HttpHandler =
    fun next ctx ->
        match ctx.RequestServices.GetService(typeof<SiteRegistry>) with
        | :? SiteRegistry as registry ->
            match SiteRegistry.tryResolve ctx registry with
            | Some site -> // serve from site.Api / site.Layouts / site.Def.BaseUrl
            | None -> // default site
        | _ -> // single-site deployment
```
