# MultiSitePublic — multi-host public rendering worked sample

One process, three independent markdown-file-backed websites, matched on the
request's `Host` header (Phase 114/115 — see
[`docs/platform/multi-site-public-rendering.md`](../../docs/platform/multi-site-public-rendering.md)
for the full compose shape):

| Site | Hosts | Content root | Layout | Feed |
|---|---|---|---|---|
| Default | every unclaimed host (incl. `localhost`) | `content/default/` | shared `withLayout` | compose-level `withFeed` ("Default Site — Feed") |
| Site A | `sitea.example`, `www.sitea.example` | `content/sitea/` | inherits the shared layout | `PublicSiteDef.Feeds` ("Site A — Releases") |
| Site B | `siteb.example` | `content/siteb/` | bespoke (`PublicSiteDef.Layouts`) | `PublicSiteDef.Feeds` ("Site B — Field Notes") |

SSR-only: no Fable client, no AI, no modules. Binds port `13950`
(samples band `13950–13959` — see [`samples/CLAUDE.md`](../CLAUDE.md)).

## Run

```
dotnet run --project samples/MultiSitePublic/MultiSitePublic.fsproj
```

## Verify (curl, pinning the Host header)

Per-host **pages** — same listener, three different sites:

```
curl -s http://localhost:13950/                              # "Default Site"
curl -s -H "Host: sitea.example" http://localhost:13950/     # "Site A Home", data-layout="shared"
curl -s -H "Host: siteb.example" http://localhost:13950/     # "Site B Home", data-layout="siteb"
```

Per-site **slug universes** — a slug on one site 404s on the others:

```
curl -s -H "Host: sitea.example" http://localhost:13950/getting-started   # 200, site A only
curl -s -H "Host: siteb.example" http://localhost:13950/getting-started   # 404
curl -s -H "Host: siteb.example" http://localhost:13950/products          # 200, site B only
```

Per-host **sitemaps** — each on its own absolute `BaseUrl` origin, with no
cross-site URLs:

```
curl -s http://localhost:13950/sitemap.xml                            # http://localhost:13950/... locs
curl -s -H "Host: sitea.example" http://localhost:13950/sitemap.xml   # https://sitea.example/... locs
curl -s -H "Host: siteb.example" http://localhost:13950/sitemap.xml   # https://siteb.example/... locs
```

Per-host **Atom feeds** — `/feed.atom` answers with a different feed document
per host (per-site `<title>` / `<link rel="self">`); the compose-level feed
serves the default site's hosts only:

```
curl -s http://localhost:13950/feed.atom                            # <title>Default Site — Feed</title>
curl -s -H "Host: sitea.example" http://localhost:13950/feed.atom   # <title>Site A — Releases</title>
curl -s -H "Host: siteb.example" http://localhost:13950/feed.atom   # <title>Site B — Field Notes</title>
```

> **Feed entries.** Atom `<entry>` elements surface **Narrative-bodied** pages
> only (the CMS / `publish_narrative` publishing path). File-loaded markdown
> pages carry `Html` bodies, so in this purely file-backed sample each feed is
> a valid but entry-empty document — the per-host dispatch, per-site titles,
> and self links are what the sample demonstrates. See the v1 notes in
> `NarrativeFeedHandler.fs`.

The `www.` variant is claimed explicitly (host matching is exact,
case-insensitive, port-ignored):

```
curl -s -H "Host: WWW.SITEA.EXAMPLE" http://localhost:13950/   # still site A
```
