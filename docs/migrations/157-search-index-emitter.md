# Migration — Phase 157: static client-search index emitter

**Status:** net-new opt-in endpoint. Off by default → no route is mounted, byte-for-byte pre-157 (GP 11 / GP 13). No consumer action required.

## What changes

A lightweight, RAG-free search-index emitter over the public URL universe: it enumerates the indexable URLs into a compact JSON document at a configurable endpoint with a content-version `ETag`, so a site can offer instant client-side search (fetch-once, tokenise in the browser) without standing up embeddings / a vector store / `IRetrievalPipeline`.

It's the counterpart to the heavyweight semantic-search SSR path for sites that want type-ahead over a *known URL set* rather than meaning-based retrieval, and the data source a [Phase 151](151-structured-data-website-searchaction.md) `WebSite`/`SearchAction` JSON-LD points a search engine at.

- **Model + emitter.** `SearchIndexEntry { Url; Title; Kind; Keywords }`. `SearchIndexEmitter.entriesFromPages` walks the **same deduped universe** `sitemap.xml` uses (`SitemapGenerator.entries`), pulling `Title` / `Kind` (the page's `Collection`, else `"page"`) / `Keywords` (the `keywords` frontmatter, comma-split, falling back to `tags`) per page. `toJson` serialises a compact JSON array via pure BCL `System.Text.Json` (GP 1 — no vendor search dependency).
- **Endpoint + conditional-GET.** Served at a configurable path (default `/search-index.json`) with a weak `ETag` folding the content version (`IndexNow.computeSignature`, the same digest the sitemap uses), `Cache-Control` + `stale-while-revalidate`, and `If-None-Match` / `If-Modified-Since` → `304` through the Phase 155 `ConditionalGet.cacheable` combinator.
- **Consumer entry source.** An optional `EntrySource : unit -> Async<SearchIndexEntry list>` lets a programmatic-SSR consumer that enumerates its own URL space supply entries directly; the file-backed universe is the default.
- **Compose opt-in.** `PublicRenderingServerApp.withSearchIndex` (default off → no endpoint). Per-site aware where a `SiteRegistry` is active (each site indexes its own universe).

## Diff to apply

None required. To mount the endpoint:

```fsharp
ServerApp.empty
|> ServerApp.withConfig config
|> PublicRenderingCompose.withPublicRendering (fun pr ->
    pr
    |> PublicRenderingServerApp.withLayout (LayoutName "page") pageLayout
    |> PublicRenderingServerApp.withSearchIndex)                       // ← /search-index.json
|> ServerApp.run
```

For a custom path / `Cache-Control` / consumer entry source, use `withSearchIndexConfig`:

```fsharp
|> PublicRenderingServerApp.withSearchIndexConfig
    (SearchIndexConfig.defaults
     |> SearchIndexConfig.withPath "/api/search/index.json"
     |> SearchIndexConfig.withEntrySource myEnumerator)
```

Pair it with a Phase 151 `WebSite`/`SearchAction` JSON-LD whose `urlTemplate` targets the index for the sitelinks search box.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `SitemapSearchIndexTests.fs` ("Phase 157 search index emitter"):
  - the emitter produces valid compact JSON (no newlines) over a file universe; `title` / `kind` / `keywords` are pulled from the page.
  - a custom `EntrySource` overrides the file-backed universe.
  - the endpoint ETag folds the content version (rolls when the universe changes, `304`s on a conditional re-fetch of unchanged content).
  - the endpoint only answers its configured path.

## Rollback

Revert the Phase 157 commits. `withSearchIndex` is opt-in; reverting drops the endpoint with no other effect. No persisted state.
