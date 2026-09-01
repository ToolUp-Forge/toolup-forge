# Migration — sitemap honours `robots: noindex` (opt-in)

`SitemapGenerator` gains `SitemapUniverseOptions` — one flag, `ExcludeNoindex` — and `PublicRenderingServerApp` gains the compose knob that sets it, `withSitemapNoindexExclusion`. Opted in, a page whose `robots` frontmatter key resolves to noindex stops being advertised in `sitemap.xml`.

**Default behaviour is unchanged (GP 11).** `ExcludeNoindex = false` is the default everywhere — `SitemapUniverseOptions.defaults`, `SitemapHandlerOptions.defaults`, `StaticExportOptions.defaults`, and the `PublicRenderingServerApp` defaults record. Absent an explicit opt-in the `robots` key is not consulted at all, so the sitemap universe, the emitted `<urlset>`, the Phase 149 `ETag` and the Phase 109 IndexNow submission are byte-for-byte what they were. An existing deployment that upgrades and changes nothing sees no crawl-surface change of any kind, and is pinned as such by a byte-identity test.

## The symptom this closes

Google Search Console reports it as **"Submitted URL marked 'noindex'"**: the sitemap asks a crawler to index a URL, and the page it fetches refuses with `<meta name="robots" content="noindex">`. The two artefacts contradict each other, and the sitemap is the one that is wrong — a noindex page should not be advertised for crawling in the first place.

Until this release `SitemapGenerator.entriesAt` dropped a page for three reasons — a `sitemap: exclude` frontmatter key, a non-`Public` audience, and a not-yet-publicly-visible status — and never consulted the Phase 152 `robots` key. The Phase 212 conformance lint carried the finding as a pinned known-gap case; that pin is now a live assertion on the opted-in path.

## What you have to change

**Nothing, unless you construct one of two options records as a full record literal.** `SitemapHandlerOptions` gains `Universe`, and `StaticExportOptions` gains `SitemapUniverse`:

```fsharp skip=fragment
let opts: SitemapGenerator.SitemapHandlerOptions = {
    ResponseCache = None
    CacheControl = "public, max-age=0, must-revalidate"
    Sharding = SitemapGenerator.SitemapShardingOptions.defaults
    Universe = SitemapGenerator.SitemapUniverseOptions.defaults   // ← new; defaults = today's universe
}
```

Consumers using `SitemapHandlerOptions.defaults` / `StaticExportOptions.defaults`, a `{ defaults with … }` record update, `SitemapGenerator.handler`, `entries` / `entriesAt`, or the `PublicRenderingCompose` composition root need no change at all. `entriesAt` keeps its signature and is now defined as `entriesAtWith SitemapUniverseOptions.defaults`.

## What you can now do

```fsharp skip=fragment
// Compose root — one knob, applied to the runtime sitemap + its shards,
// the IndexNow push channel, and the static export's sitemap.xml.
app
|> PublicRenderingServerApp.withSitemapNoindexExclusion

// Or directly, if you mount the handler yourself.
let opts = {
    SitemapGenerator.SitemapHandlerOptions.defaults with
        Universe = { ExcludeNoindex = true }
}

SitemapGenerator.handlerWith opts baseUrl api enumerate
```

The three surfaces move together deliberately: they read one universe, so a configuration that excluded a noindex page from `sitemap.xml` while IndexNow went on pushing it for indexing would restate the same contradiction one layer down.

The page itself is unaffected — it is still served, still rendered into a static export, and still emits its own `<meta name="robots">`. What changes is only whether it is **advertised** for crawling.

## `noindex` and `sitemap: exclude` are a union, not alternatives

The documented pre-737 workaround was to set both keys on a page you wanted hidden from the sitemap:

```yaml
robots: noindex
sitemap: exclude
```

**That keeps working, unchanged, on both paths.** `sitemap: exclude` drops a page whatever its `robots` key says and whatever this flag says; opted in, a noindex `robots` key drops a page whatever its `sitemap` key says. So you can adopt the flag without first unpicking the workaround from your content, and neither mechanism weakens the other.

`none` counts as noindex — it is the standard shorthand for `noindex, nofollow`. Matching is over the comma-separated directive list, trimmed and case-insensitive, so `NoIndex`, `  noindex , nofollow  ` and `noindex,nofollow,noarchive` all resolve. `noarchive`, `nofollow` alone, an index-permitting directive, a blank value and an absent key do not.

## The `ETag` moves exactly once

Opting in changes the sitemap body, so it changes the Phase 149 weak `ETag` — deliberately, and with the body rather than ahead of it: the universe options are applied upstream of the digest, so a crawler is never served a stale validator over a changed body. The new universe is then stable, so the next poll conditional-`304`s exactly as before. `If-None-Match` and `If-Modified-Since` behaviour is unchanged on both the default and the opted-in path.

A deployment whose content declares no `robots` key at all sees **no `ETag` change**: the flag moves a validator only where it moves the universe. Both properties are pinned in `ToolUp.Platform.Tests`.

## Rollback

Remove the `withSitemapNoindexExclusion` call (or set `ExcludeNoindex = false`). The universe returns to the pre-opt-in set, the `ETag` moves back once, and crawlers re-fetch on the next conditional poll. No content edit, no data migration, no persisted state.

## See also

- Phase 152 — the `robots` frontmatter key and its `<meta name="robots">` emission.
- Phase 149 — the cacheable, conditional-GET sitemap whose `ETag` this moves.
- Phase 212 — the SEO / structured-data conformance lint that pinned the gap.
