namespace ToolUp.PublicRendering

open System
open System.Text.Json.Nodes
open Giraffe
open Microsoft.AspNetCore.Http

// ─── Phase 157 — static client-search index emitter ──────────────────
//
// A lightweight, RAG-free search-index emitter over the public URL
// universe: enumerate the indexable URLs into a compact JSON document at
// a configurable endpoint with a content-version `ETag`, so a site can
// offer instant client-side search (fetch-once, tokenise in the browser)
// without standing up embeddings / a vector store / `IRetrievalPipeline`.
//
// The counterpart to the heavyweight semantic-search SSR path for sites
// that want type-ahead over a known URL set rather than meaning-based
// retrieval, and the data source a `WebSite`/`SearchAction` JSON-LD points
// a search engine at.
//
// The enumeration walks the SAME deduped universe `sitemap.xml` uses
// (`SitemapGenerator.entries`), and the `ETag` folds the SAME content
// version (`IndexNow.computeSignature`), so the index, the sitemap, and
// the IndexNow push channel can never disagree about what exists. Only the
// URL universe is domain-specific; a programmatic-SSR consumer that
// enumerates its own URL space supplies entries directly via `EntrySource`.
//
// Off by default (GP 11 / GP 13): no endpoint is mounted until
// `withSearchIndex` is composed. Pure BCL JSON — no vendor search
// dependency (GP 1).

/// One indexable URL in the client-search index (Phase 157).
type SearchIndexEntry = {
    /// Absolute URL of the page.
    Url: string
    /// Display title (page `Title`, falling back to the slug).
    Title: string
    /// Coarse content class — the page's `Collection` (`"news"` /
    /// `"events"` / …) or `"page"` for standalone / dynamic routes.
    Kind: string
    /// Search keywords — the page's `keywords` frontmatter (comma-split),
    /// falling back to its `tags`. `[]` when neither is present.
    Keywords: string list
}

/// How the `keywords` field is serialised per entry (Phase 157 follow-up).
/// The default `KeywordsArray` preserves the original wire shape; a consumer
/// whose client expects a single space- (or other-) joined token string opts
/// into `KeywordsJoined`.
type SearchIndexKeywordFormat =
    /// `"keywords":["a","b"]` — the default, byte-for-byte the original 157
    /// output (GP 11).
    | KeywordsArray
    /// `"keywords":"a b"` — the per-entry keyword list joined by `separator`
    /// into one string, for clients that tokenise a flat keyword string.
    | KeywordsJoined of separator: string

/// Compose-time config for the search-index endpoint (Phase 157).
/// `defaults` serves the file-backed universe at `/search-index.json`.
type SearchIndexConfig = {
    /// Endpoint path. Default `/search-index.json`.
    Path: string
    /// Optional consumer entry source: a programmatic-SSR consumer that
    /// enumerates its own URL space (not file-backed pages) supplies entries
    /// directly. `None` (default) → the file-backed universe (the same one
    /// `sitemap.xml` walks).
    EntrySource: (unit -> Async<SearchIndexEntry list>) option
    /// `Cache-Control` for the index response. Default
    /// `public, max-age=300, stale-while-revalidate=86400` — cache for five
    /// minutes, serve stale up to a day while revalidating.
    CacheControl: string
    /// `keywords` field shape. Default `KeywordsArray` → byte-for-byte the
    /// original 157 wire output (GP 11).
    KeywordFormat: SearchIndexKeywordFormat
}

module SearchIndexConfig =
    let defaults: SearchIndexConfig = {
        Path = "/search-index.json"
        EntrySource = None
        CacheControl = "public, max-age=300, stale-while-revalidate=86400"
        KeywordFormat = KeywordsArray
    }

    let withPath (path: string) (c: SearchIndexConfig) : SearchIndexConfig = { c with Path = path }

    /// Serialise `keywords` as a joined string (e.g. `withKeywordsJoined " "`
    /// for a space-separated token string a client tokenises), instead of the
    /// default JSON array.
    let withKeywordFormat (format: SearchIndexKeywordFormat) (c: SearchIndexConfig) : SearchIndexConfig = {
        c with
            KeywordFormat = format
    }

    let withEntrySource (source: unit -> Async<SearchIndexEntry list>) (c: SearchIndexConfig) : SearchIndexConfig = {
        c with
            EntrySource = Some source
    }

    let withCacheControl (cacheControl: string) (c: SearchIndexConfig) : SearchIndexConfig = {
        c with
            CacheControl = cacheControl
    }

/// Emitter + endpoint handler for the client-search index.
module SearchIndexEmitter =

    /// The page's search keywords: the `keywords` frontmatter key
    /// (comma-separated, trimmed, empties dropped), falling back to the
    /// page's `tags`. `[]` when neither is present.
    let internal keywordsOf (page: PublicPage) : string list =
        match page.Frontmatter.TryFind "keywords" with
        | Some raw ->
            raw.Split(',')
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> s <> "")
            |> Array.toList
        | None -> PublicPage.tags page

    /// Project the deduped sitemap universe into search-index entries,
    /// pulling `Title` / `Kind` / `Keywords` from the matching `PublicPage`
    /// (dynamic slugs with no backing page fall back to the slug + `"page"`
    /// + no keywords). Order follows the universe.
    let entriesFromUniverse
        (baseUrl: string)
        (pages: PublicPage list)
        (universe: (Slug * string option) list)
        : SearchIndexEntry list =
        let normalisedBase = baseUrl.TrimEnd('/')
        let pageBySlug = pages |> List.map (fun p -> Slug.value p.Slug, p) |> Map.ofList

        universe
        |> List.map (fun (Slug s, _) ->
            let url = normalisedBase + "/" + s

            match Map.tryFind s pageBySlug with
            | Some page -> {
                Url = url
                Title =
                    (if String.IsNullOrWhiteSpace page.Title then
                         s
                     else
                         page.Title)
                Kind = page.Collection |> Option.defaultValue "page"
                Keywords = keywordsOf page
              }
            | None -> {
                Url = url
                Title = s
                Kind = "page"
                Keywords = []
              })

    /// Walk the file-backed universe (the same one `sitemap.xml` uses) into
    /// search-index entries.
    let entriesFromPages (baseUrl: string) (pages: PublicPage list) (dynamicSlugs: Slug list) : SearchIndexEntry list =
        entriesFromUniverse baseUrl pages (SitemapGenerator.entries pages dynamicSlugs)

    /// Serialise the entries to a compact JSON array (escaped, minimal
    /// whitespace) — pure BCL `System.Text.Json` (GP 1). Fields:
    /// `url` / `title` / `kind` / `keywords`. `keywordFormat` selects the
    /// `keywords` shape (array vs joined string).
    let toJsonWith (keywordFormat: SearchIndexKeywordFormat) (entries: SearchIndexEntry list) : string =
        let arr = JsonArray()

        for entry in entries do
            let o = JsonObject()
            o["url"] <- JsonValue.Create entry.Url
            o["title"] <- JsonValue.Create entry.Title
            o["kind"] <- JsonValue.Create entry.Kind

            match keywordFormat with
            | KeywordsArray ->
                let kw = JsonArray()

                for k in entry.Keywords do
                    kw.Add(JsonValue.Create k)

                o["keywords"] <- kw
            | KeywordsJoined separator -> o["keywords"] <- JsonValue.Create(String.concat separator entry.Keywords)

            arr.Add o

        arr.ToJsonString()

    /// Serialise the entries with the default `keywords` JSON-array shape —
    /// byte-for-byte the original 157 output (GP 11).
    let toJson (entries: SearchIndexEntry list) : string = toJsonWith KeywordsArray entries

    /// Giraffe handler for the search-index endpoint (Phase 157). Mounts at
    /// `config.Path`, emits a compact content-versioned JSON index over the
    /// public universe (or the consumer's `EntrySource`), with a weak ETag
    /// folding the content version (`IndexNow.computeSignature`, the same
    /// digest the sitemap uses) and a `304` on a conditional re-fetch via the
    /// Phase 155 `ConditionalGet.cacheable` combinator.
    let handler
        (config: SearchIndexConfig)
        (baseUrl: string)
        (api: IPublicContentApi)
        (enumerate: unit -> Async<Slug list>)
        : HttpHandler =
        route config.Path
        >=> fun next (ctx: HttpContext) -> task {
            let! entries, signature, lastModified = task {
                match config.EntrySource with
                | Some source ->
                    let! es = source ()
                    // Content version over the supplied entries' URLs
                    // (no per-entry lastmod → the deploy stamp is the
                    // freshness floor).
                    let signature =
                        IndexNow.computeSignature (es |> List.map (fun e -> Slug e.Url, None))

                    return es, signature, ConditionalGet.deployStamp
                | None ->
                    let! pages = api.ListPages ""
                    let! dynamicSlugs = enumerate ()
                    let universe = SitemapGenerator.entries pages dynamicSlugs

                    return
                        entriesFromUniverse baseUrl pages universe,
                        IndexNow.computeSignature universe,
                        SitemapGenerator.lastModifiedOf universe
            }

            let json = toJsonWith config.KeywordFormat entries

            let body: HttpHandler =
                fun _ (c: HttpContext) ->
                    c.Response.ContentType <- "application/json; charset=utf-8"
                    c.WriteStringAsync json

            return! (ConditionalGet.cacheable signature lastModified config.CacheControl >=> body) next ctx
        }