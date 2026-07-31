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
| Atom feeds | `PublicSiteDef.Feeds` mount host-gated against the site's own content API; compose-level `withFeed` registrations serve the default site's hosts only |
| IndexNow (when composed) | Per-site declared host + stable host-seeded ownership key (`/{key}.txt` answered only on that site's hosts) + per-site startup submission over that site's page universe |
| Static export | `exportStaticAll` / `exportStaticAllWith` — default site at the output root, each satellite at `<out>/sites/<Name>/`, each tree with its own sitemap + host-config |

## Per-site SEO + export surfaces

```fsharp skip=fragment
// Feeds per site:
|> PublicRenderingServerApp.withSite
    { PublicSite.create "blog" [ "blog.example.net" ]
        "https://blog.example.net" (ContentRoot blogContent) with
        Feeds = [ { NarrativeFeedConfig.defaults with Title = "Blog"; SelfUrl = "/feed.atom" } ] }

// IndexNow — one `withIndexNow` activates the default site AND every satellite,
// each with its own host, key, and resumable submission state:
|> PublicRenderingServerApp.withIndexNow IndexNowOptions.enabled

// Multi-site static export (build-time terminus):
PublicRenderingServerApp.exportStaticAllWith options "dist" app
```

Per-site IndexNow notes: a satellite's host derives from its `BaseUrl`; its
ownership key is host-seeded with the same stability contract as the default
site's (an explicit `IndexNowOptions.Key` / `KeySeed` applies to every site).
Satellite submission state rides per-site file stores; an operator-supplied
`IndexNowOptions.StateStore` applies to the **default site only**. The
publish ping targets the default site (the publisher writes to the
default-site overlay).

## What stays on the default site (v1 scope)

The entity-store overlay (runtime-edited pages), request-time
`IContentSource`s, `/search`, `/tag/{slug}` taxonomy, and the AI publish
path remain **default-site** surfaces. Satellite sites are
markdown-file-backed — the website-class home-page / marketing-site shape.
If a satellite needs the CMS tier, give it its own deployment.

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

```fsharp skip=fragment
let myHandler: HttpHandler =
    fun next ctx ->
        match ctx.RequestServices.GetService(typeof<SiteRegistry>) with
        | :? SiteRegistry as registry ->
            match SiteRegistry.tryResolve ctx registry with
            | Some site -> // serve from site.Api / site.Layouts / site.Def.BaseUrl
            | None -> // default site
        | _ -> // single-site deployment
```
