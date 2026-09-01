namespace ToolUp.PublicRendering

open System
open System.Text
open Giraffe
open Microsoft.AspNetCore.Http

/// `/sitemap.xml` generator. Walks `IPublicContentApi.ListPages ""`
/// and emits a `<urlset>` containing every page whose frontmatter
/// does NOT set `sitemap = "exclude"`. Page `<lastmod>` derives from
/// `PublishedAt` when present.
///
/// Search-engine consumers expect absolute URLs in `<loc>`, so the
/// handler is constructed against a base URL — either
/// `ServerConfig.PublicBaseUrl` (preferred — same value used by
/// Phase 21b's public-form share-link tokens) or an explicit
/// override at compose time.
///
/// ─── Phase 149 — cacheable, conditional-GET sitemap ─────────────────
///
/// The handler emits `ETag` / `Last-Modified` / `Cache-Control` and
/// honours `If-None-Match` / `If-Modified-Since` with a `304` through
/// the Phase 155 `ConditionalGet` combinator. The validators are
/// additive standard HTTP caching headers (no opt-in needed — it
/// matches how `UseStaticFiles` already behaves for static assets) and
/// the body is byte-for-byte the pre-149 output. The weak ETag derives
/// from `IndexNow.computeSignature` over the SAME deduped universe the
/// body is built from, so it rolls exactly when the sitemap content
/// changes; `Last-Modified` is the latest page lastmod across the
/// universe, falling back to the content-version stamp
/// (`ConditionalGet.deployStamp`) — the same single content-version
/// stamp Phase 147 uses for the page validators, so the page-level and
/// sitemap-level freshness signals never diverge.
///
/// An optional `SitemapCache` (compose flag, default off — GP 11)
/// memoises the generated XML keyed on the universe digest, so repeated
/// polls within a content generation skip rebuilding the (potentially
/// large) XML body; off → the handler rebuilds per request exactly as
/// pre-149. The body is byte-identical with or without the cache.
///
/// ─── Phase 737 — opt-in `robots: noindex` exclusion ──────────────────
///
/// `SitemapUniverseOptions.ExcludeNoindex` (compose flag, default off —
/// GP 11) additionally drops a page whose Phase 152 `robots` key resolves
/// to noindex, closing the "Submitted URL marked 'noindex'" contradiction
/// between the crawl invitation and the page's own directive. It is a
/// UNION with `sitemap = "exclude"`, never a replacement: the documented
/// pre-737 workaround of setting both keys keeps working untouched. Absent
/// the flag the universe — and therefore the body and its ETag — is
/// byte-for-byte pre-737.
module SitemapGenerator =
    let private xmlEscape (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;")

    // ─── Phase 737 — opt-in `robots: noindex` sitemap exclusion ──────

    /// Universe-construction knobs (Phase 737). `defaults` reproduces the
    /// pre-737 universe byte-for-byte, so a deployment that never opts in
    /// sees the identical `<urlset>` — and therefore the identical Phase
    /// 149 ETag — it saw before (GP 11 / GP 13).
    type SitemapUniverseOptions = {
        /// Opt in to honouring the Phase 152 `robots` frontmatter key: a
        /// page whose directive list resolves to noindex is not
        /// advertised in `sitemap.xml`. `false` (default) → the `robots`
        /// key is not consulted at all and a `robots: noindex` page is
        /// still advertised, exactly as every deployment already sees it.
        ///
        /// Closing that contradiction is what the flag buys: a sitemap
        /// asks a crawler to index a URL the page itself refuses, which
        /// Search Console reports as the "Submitted URL marked 'noindex'"
        /// coverage error.
        ///
        /// **The two exclusion mechanisms are a UNION, not alternatives.**
        /// `sitemap = "exclude"` drops a page whatever its `robots` key
        /// and whatever this flag says; opted in, a noindex `robots` key
        /// drops a page whatever its `sitemap` key. So the documented
        /// pre-737 workaround — setting both keys on the same page —
        /// keeps working unchanged, and a deployment can adopt the flag
        /// without first unpicking that workaround from its content.
        ///
        /// Opting in changes the sitemap body, and therefore the Phase 149
        /// ETag, exactly once: the universe moves at opt-in and is stable
        /// thereafter, so conditional `304`s resume on the very next poll.
        ExcludeNoindex: bool
    }

    module SitemapUniverseOptions =
        let defaults: SitemapUniverseOptions = { ExcludeNoindex = false }

    /// Does a page's Phase 152 `robots` frontmatter directive list resolve
    /// to noindex? The value is a comma-separated directive list, so it is
    /// split, trimmed and compared case-insensitively; both `noindex` and
    /// `none` count — `none` is the documented shorthand for
    /// `noindex, nofollow`, and a reader that only matched the literal
    /// `noindex` would advertise a page that is just as firmly excluded.
    /// An absent, blank, or index-permitting key is `false`.
    let resolvesToNoindex (page: PublicPage) : bool =
        match page.Frontmatter |> Map.tryFind "robots" with
        | None -> false
        | Some value ->
            value.Split(',')
            |> Array.exists (fun token ->
                let directive = token.Trim()

                directive.Equals("noindex", StringComparison.OrdinalIgnoreCase)
                || directive.Equals("none", StringComparison.OrdinalIgnoreCase))

    /// The public URL universe as a deduped `(Slug * lastmod)` list — the
    /// single source of truth shared by `sitemap.xml` and the Phase 109
    /// IndexNow push channel, so the two can never disagree about what
    /// exists. Pages whose frontmatter sets `sitemap = "exclude"`, non-
    /// `Public` (gated) pages, and pages not yet publicly visible at `now`
    /// (Phase 38 — `Draft` / `Archived` / future-`Scheduled`) are dropped;
    /// a crawler must not discover an authenticated / tenant-private slug,
    /// nor an unpublished one. Phase 737 adds a fourth, OPT-IN reason —
    /// `universeOptions.ExcludeNoindex` drops a page whose `robots` key
    /// resolves to noindex — off by default, so the default universe is
    /// byte-for-byte pre-737. The surviving pages carry their
    /// `PublishedAt` (formatted `yyyy-MM-dd`) as the lastmod. Phase 95
    /// `dynamicSlugs` (content-source-enumerated routes — e.g. `/tag/{x}`)
    /// are appended with no lastmod, deduped against the page slugs.
    /// Order: pages first (input order), then dynamic routes.
    ///
    /// `entriesAt` is this function at `SitemapUniverseOptions.defaults`;
    /// `entries` is `entriesAt` at the current wall clock.
    let entriesAtWith
        (universeOptions: SitemapUniverseOptions)
        (now: DateTimeOffset)
        (pages: PublicPage list)
        (dynamicSlugs: Slug list)
        : (Slug * string option) list =
        let pageEntries =
            pages
            |> List.choose (fun page ->
                let excluded =
                    not (PublicPage.isPubliclyDiscoverable now page)
                    || page.Frontmatter
                       |> Map.tryFind "sitemap"
                       |> Option.exists (fun v -> v.Equals("exclude", StringComparison.OrdinalIgnoreCase))
                    // Phase 737 — opt-in, and a UNION with the key above:
                    // neither mechanism weakens the other.
                    || (universeOptions.ExcludeNoindex && resolvesToNoindex page)

                if excluded then
                    None
                else
                    Some(page.Slug, page.PublishedAt |> Option.map (fun d -> d.ToString("yyyy-MM-dd"))))

        let pageSlugs = pageEntries |> List.map (fun (Slug s, _) -> s) |> Set.ofList

        let dynamicEntries =
            dynamicSlugs
            |> List.choose (fun (Slug s) -> if pageSlugs.Contains s then None else Some(Slug s, None))
            |> List.distinct

        pageEntries @ dynamicEntries

    /// `entriesAtWith` at `SitemapUniverseOptions.defaults` — the pinned-
    /// clock shape every pre-737 caller uses. Signature and output
    /// unchanged from Phase 149 (GP 11).
    let entriesAt
        (now: DateTimeOffset)
        (pages: PublicPage list)
        (dynamicSlugs: Slug list)
        : (Slug * string option) list =
        entriesAtWith SitemapUniverseOptions.defaults now pages dynamicSlugs

    /// `entriesAt` at the current wall clock — the shape every existing
    /// caller (sitemap handler + shards, search index, static export, the
    /// IndexNow universe thunk) uses. Signature unchanged from Phase 149.
    let entries (pages: PublicPage list) (dynamicSlugs: Slug list) : (Slug * string option) list =
        entriesAt DateTimeOffset.UtcNow pages dynamicSlugs

    /// Build the `<urlset>` body for a precomputed universe + base URL.
    /// Trailing slashes on `baseUrl` are normalised away. Shared by the
    /// public `generateWith`, the per-shard generation, and the static
    /// export (Phase 150). `internal` — same-assembly callers only.
    let internal generateUrlSetFrom (baseUrl: string) (universe: (Slug * string option) list) : string =
        let normalisedBase = baseUrl.TrimEnd('/')
        let sb = StringBuilder()

        let emit (slug: string) (lastmod: string option) =
            let url = normalisedBase + "/" + slug |> xmlEscape
            sb.AppendLine("  <url>") |> ignore
            sb.AppendLine(sprintf "    <loc>%s</loc>" url) |> ignore

            match lastmod with
            | Some d -> sb.AppendLine(sprintf "    <lastmod>%s</lastmod>" d) |> ignore
            | None -> ()

            sb.AppendLine("  </url>") |> ignore

        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""") |> ignore

        sb.AppendLine("""<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""")
        |> ignore

        for Slug s, lastmod in universe do
            emit s lastmod

        sb.AppendLine("</urlset>") |> ignore
        sb.ToString()

    /// Build the sitemap XML body for a page list + base URL, plus any
    /// Phase 95 dynamic routes (content-source-enumerated slugs — e.g.
    /// `/tag/{x}` taxonomy pages). Pages whose frontmatter sets
    /// `sitemap = "exclude"` and non-`Public` (gated) pages are skipped;
    /// `dynamicSlugs` are emitted verbatim (a source is responsible for
    /// only enumerating public slugs) with no `<lastmod>`. Trailing
    /// slashes on `baseUrl` are normalised away.
    let generateWith (baseUrl: string) (pages: PublicPage list) (dynamicSlugs: Slug list) : string =
        // Phase 109 — both the sitemap and the IndexNow push channel walk
        // the same deduped universe (`entries`), so a slug can never appear
        // in one and not the other.
        generateUrlSetFrom baseUrl (entries pages dynamicSlugs)

    /// Build the sitemap XML body for a page list + base URL (no dynamic
    /// routes). Back-compat shim over `generateWith`.
    let generate (baseUrl: string) (pages: PublicPage list) : string = generateWith baseUrl pages []

    // ─── Phase 149 — conditional-GET freshness signals ──────────────

    /// The content-stable `Last-Modified` for a sitemap universe: the
    /// latest page lastmod present (parsed from the `yyyy-MM-dd` strings
    /// the universe carries), or the deploy-generation stamp
    /// (`ConditionalGet.deployStamp`) when no entry carries one. Never a
    /// wall-clock render moment — a deterministic universe must present
    /// the same `Last-Modified` across polls so `If-Modified-Since` can
    /// `304`.
    let lastModifiedOf (universe: (Slug * string option) list) : DateTimeOffset =
        let parsed =
            universe
            |> List.choose (fun (_, lastmod) -> lastmod)
            |> List.choose (fun s ->
                match
                    DateTimeOffset.TryParse(
                        s,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal
                    )
                with
                | true, d -> Some d
                | _ -> None)

        match parsed with
        | [] -> ConditionalGet.deployStamp
        | dates -> List.max dates

    // ─── Phase 150 — sitemap-index sharding + universal lastmod ──────

    /// Sharding + universal-lastmod knobs (Phase 150). `defaults`
    /// reproduces the pre-150 single-`<urlset>` behaviour for any universe
    /// below `Threshold`, with no default lastmod (GP 11).
    type SitemapShardingOptions = {
        /// URL count above which `sitemap.xml` becomes a `<sitemapindex>`
        /// and the universe is split into shard files. sitemaps.org caps a
        /// single file at 50,000 URLs / 50 MB; the default mirrors the URL
        /// cap.
        Threshold: int
        /// Cluster key: groups URLs into logical child sitemaps so a changed
        /// cluster only re-fetches its own shard. `None` → deterministic
        /// numeric slices (`sitemap-1.xml` …). `Some f` → `f slug` names the
        /// shard a slug belongs to (cluster-aware); names are sanitised to
        /// `[a-z0-9-]` for the `sitemap-<name>.xml` route + over-large
        /// clusters are sub-sliced numerically.
        ClusterKey: (Slug -> string) option
        /// Fallback lastmod (formatted `yyyy-MM-dd`) applied to any entry
        /// whose own lastmod is `None` — including Phase 95 dynamic slugs.
        /// `None` (default) → signal-less entries emit no `<lastmod>`,
        /// exactly as pre-150 (GP 11).
        DefaultLastmod: string option
    }

    module SitemapShardingOptions =
        let defaults: SitemapShardingOptions = {
            Threshold = 50_000
            ClusterKey = None
            DefaultLastmod = None
        }

    /// Apply a fallback lastmod to entries whose lastmod is `None`
    /// (Phase 150 universal-lastmod). `None` fallback → identity, so the
    /// universe is byte-for-byte pre-150 (GP 11).
    let applyDefaultLastmod
        (fallback: string option)
        (universe: (Slug * string option) list)
        : (Slug * string option) list =
        match fallback with
        | None -> universe
        | Some d ->
            universe
            |> List.map (fun (slug, lastmod) -> slug, lastmod |> Option.orElse (Some d))

    /// Sanitise a cluster name into the `[a-z0-9-]` charset the
    /// `sitemap-<name>.xml` shard route + `<loc>` can carry safely. Any
    /// other character collapses to `-`; an empty result falls back to
    /// `shard`.
    let internal sanitizeShardName (name: string) : string =
        let mapped =
            name
            |> Seq.map (fun c ->
                let lower = System.Char.ToLowerInvariant c

                if (lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9') || lower = '-' then
                    lower
                else
                    '-')
            |> Seq.toArray
            |> System.String

        if System.String.IsNullOrEmpty mapped then
            "shard"
        else
            mapped

    /// Partition the universe into named shards (Phase 150) with stable
    /// membership for a given content set. Cluster-aware when `ClusterKey`
    /// is set: groups by `f slug` (cluster order sorted by name; entries
    /// sorted by slug within), each over-threshold cluster sub-sliced
    /// numerically (`news-1`, `news-2`). With no cluster key, deterministic
    /// numeric slices over the slug-sorted universe (`1`, `2`, …). Shard
    /// names are the `sitemap-<name>.xml` basename suffix.
    let shardUniverse
        (options: SitemapShardingOptions)
        (universe: (Slug * string option) list)
        : (string * (Slug * string option) list) list =
        let threshold = max 1 options.Threshold

        match options.ClusterKey with
        | None ->
            universe
            |> List.sortBy (fun (Slug s, _) -> s)
            |> List.chunkBySize threshold
            |> List.mapi (fun i chunk -> string (i + 1), chunk)
        | Some keyOf ->
            universe
            |> List.groupBy (fun (slug, _) -> sanitizeShardName (keyOf slug))
            |> List.sortBy fst
            |> List.collect (fun (clusterName, items) ->
                let sorted = items |> List.sortBy (fun (Slug s, _) -> s)

                if List.length sorted <= threshold then
                    [ clusterName, sorted ]
                else
                    sorted
                    |> List.chunkBySize threshold
                    |> List.mapi (fun i chunk -> sprintf "%s-%d" clusterName (i + 1), chunk))

    /// The `<lastmod>` for a shard / child sitemap: the latest lastmod
    /// string among its entries (ISO `yyyy-MM-dd` strings sort
    /// lexicographically), or `None` when no entry carries one.
    let internal shardLastmod (shardEntries: (Slug * string option) list) : string option =
        shardEntries
        |> List.choose (fun (_, lastmod) -> lastmod)
        |> function
            | [] -> None
            | dates -> Some(List.max dates)

    /// Build a `<sitemapindex>` listing each shard as a child sitemap at
    /// `baseUrl/sitemap-<name>.xml`, each with the shard's `<lastmod>`
    /// (Phase 150).
    let generateSitemapIndex (baseUrl: string) (shards: (string * (Slug * string option) list) list) : string =
        let normalisedBase = baseUrl.TrimEnd('/')
        let sb = StringBuilder()
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""") |> ignore

        sb.AppendLine("""<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">""")
        |> ignore

        for name, shardEntries in shards do
            let loc = normalisedBase + "/sitemap-" + name + ".xml" |> xmlEscape
            sb.AppendLine("  <sitemap>") |> ignore
            sb.AppendLine(sprintf "    <loc>%s</loc>" loc) |> ignore

            match shardLastmod shardEntries with
            | Some d -> sb.AppendLine(sprintf "    <lastmod>%s</lastmod>" d) |> ignore
            | None -> ()

            sb.AppendLine("  </sitemap>") |> ignore

        sb.AppendLine("</sitemapindex>") |> ignore
        sb.ToString()

    /// Optional memoisation of the generated sitemap body keyed on the
    /// universe digest (Phase 149). Off by default — composing
    /// `withSitemapResponseCache` constructs one. A re-poll within the
    /// same content generation reuses the built XML instead of rebuilding
    /// it; a digest change rebuilds. The body is byte-identical with or
    /// without the cache. One slot is sufficient: the digest changes only
    /// when content changes, and a deployment serves one logical universe
    /// per cache instance (the compose layer gives each site its own).
    ///
    /// GP 5 documented exception — a per-deployment response memo, guarded
    /// by a lock; not domain state.
    type SitemapCache() =
        let gate = obj ()
        let mutable slot: (string * string) option = None

        /// Return the memoised body when its digest matches `signature`;
        /// otherwise `build ()`, store, and return it.
        member _.GetOrBuild (signature: string) (build: unit -> string) : string =
            lock gate (fun () ->
                match slot with
                | Some(s, xml) when s = signature -> xml
                | _ ->
                    let xml = build ()
                    slot <- Some(signature, xml)
                    xml)

    /// Compose-time knobs for the conditional-GET sitemap handler
    /// (Phase 149). `defaults` reproduces the always-on conditional-GET
    /// behaviour with no response cache.
    type SitemapHandlerOptions = {
        /// Optional XML-body memo keyed on the universe digest. `None`
        /// (default) → rebuild per request (GP 11 / GP 13).
        ResponseCache: SitemapCache option
        /// `Cache-Control` emitted alongside the validators. Defaults to
        /// `public, max-age=0, must-revalidate` — edge/browser-cacheable
        /// but always revalidated, so re-polls are cheap conditional
        /// `304`s.
        CacheControl: string
        /// Phase 150 — sharding + universal-lastmod knobs. Defaults below
        /// the threshold reproduce the single-`<urlset>` behaviour
        /// byte-for-byte (GP 11).
        Sharding: SitemapShardingOptions
        /// Phase 737 — universe-construction knobs (the opt-in
        /// `robots: noindex` exclusion). Defaults reproduce the pre-737
        /// universe byte-for-byte, so the body and its ETag are unchanged
        /// for a deployment that does not opt in (GP 11).
        Universe: SitemapUniverseOptions
    }

    module SitemapHandlerOptions =
        let defaults: SitemapHandlerOptions = {
            ResponseCache = None
            CacheControl = "public, max-age=0, must-revalidate"
            Sharding = SitemapShardingOptions.defaults
            Universe = SitemapUniverseOptions.defaults
        }

    /// Conditional-GET sitemap handler (Phase 149). Walks the universe,
    /// derives a weak ETag from `IndexNow.computeSignature` over it + a
    /// content-stable `Last-Modified`, and serves the `<urlset>` body
    /// through the Phase 155 `ConditionalGet.cacheable` combinator — so a
    /// conditional re-poll `304`s when the universe is unchanged. The XML
    /// body is memoised when `options.ResponseCache` is set.
    let handlerWith
        (options: SitemapHandlerOptions)
        (baseUrl: string)
        (api: IPublicContentApi)
        (enumerate: unit -> Async<Slug list>)
        : HttpHandler =
        fun next (ctx: HttpContext) -> task {
            // Phase 632 — one clock for the whole response, and the GATED
            // enumeration. `entriesAt` re-applies the same predicate (the
            // gate is idempotent), so the body is byte-for-byte unchanged;
            // what changes is that the ungated set never reaches this
            // handler at all, so a future edit here cannot leak by
            // forgetting a filter.
            let now = DateTimeOffset.UtcNow
            let! pages = api.ListPagesPublic(now, "")
            let! dynamicSlugs = enumerate ()

            // Phase 150 — apply the universal-lastmod fallback before the
            // digest so the ETag rolls when the fallback date changes; the
            // signature + Last-Modified are taken over the post-applied
            // universe.
            //
            // Phase 737 — the universe knobs are applied HERE, upstream of
            // the digest, so opting the noindex exclusion in moves the ETag
            // once (honestly, with the body) rather than serving a stale
            // validator over a changed body.
            let universe =
                applyDefaultLastmod
                    options.Sharding.DefaultLastmod
                    (entriesAtWith options.Universe now pages dynamicSlugs)

            let signature = IndexNow.computeSignature universe

            // Phase 150 — past the threshold, `sitemap.xml` becomes a
            // `<sitemapindex>` pointing at the shard routes; below it (the
            // default), a single `<urlset>` byte-for-byte pre-150.
            let buildBody () =
                if List.length universe <= options.Sharding.Threshold then
                    generateUrlSetFrom baseUrl universe
                else
                    generateSitemapIndex baseUrl (shardUniverse options.Sharding universe)

            let xml =
                match options.ResponseCache with
                | Some cache -> cache.GetOrBuild signature buildBody
                | None -> buildBody ()

            let lastModified = lastModifiedOf universe

            let body: HttpHandler =
                fun _ (c: HttpContext) ->
                    c.Response.ContentType <- "application/xml; charset=utf-8"
                    c.WriteStringAsync xml

            return! (ConditionalGet.cacheable signature lastModified options.CacheControl >=> body) next ctx
        }

    /// Giraffe handler at `/sitemap.xml`. Reads pages via the supplied
    /// `IPublicContentApi` and the Phase 95 dynamic routes via
    /// `enumerate` (typically `ContentSource.enumerateAll` over the
    /// registered sources), then emits the generated XML body with the
    /// Phase 149 conditional-GET validators. Back-compat shim over
    /// `handlerWith` with default options (no response cache).
    let handler (baseUrl: string) (api: IPublicContentApi) (enumerate: unit -> Async<Slug list>) : HttpHandler =
        handlerWith SitemapHandlerOptions.defaults baseUrl api enumerate

    /// Phase 150 — shard-file handler at `routef "/sitemap-%s.xml"`. When
    /// the universe is over the threshold, serves the requested child
    /// sitemap (`/sitemap-<name>.xml`) as a `<urlset>` through the Phase 155
    /// conditional-GET combinator (ETag over the shard's own sub-universe;
    /// `Last-Modified` = the shard's latest lastmod). Below the threshold,
    /// or for an unknown shard name, declines (`skipPipeline`) so the
    /// request falls through to the catch-all page handler (→ 404). Mounted
    /// alongside `/sitemap.xml`; the two routes are distinct (the shard
    /// route always carries the `-` infix).
    let shardHandler
        (options: SitemapHandlerOptions)
        (baseUrl: string)
        (api: IPublicContentApi)
        (enumerate: unit -> Async<Slug list>)
        : HttpHandler =
        routef "/sitemap-%s.xml" (fun shardName ->
            fun next (ctx: HttpContext) -> task {
                // Phase 632 — gated enumeration, one pinned clock (a shard
                // and its parent index must agree about what exists).
                let now = DateTimeOffset.UtcNow
                let! pages = api.ListPagesPublic(now, "")
                let! dynamicSlugs = enumerate ()

                let universe =
                    applyDefaultLastmod
                        options.Sharding.DefaultLastmod
                        (entriesAtWith options.Universe now pages dynamicSlugs)

                if List.length universe <= options.Sharding.Threshold then
                    return! skipPipeline
                else
                    let shards = shardUniverse options.Sharding universe

                    match shards |> List.tryFind (fun (name, _) -> name = shardName) with
                    | None -> return! skipPipeline
                    | Some(_, shardEntries) ->
                        let xml = generateUrlSetFrom baseUrl shardEntries
                        let signature = IndexNow.computeSignature shardEntries
                        let lastModified = lastModifiedOf shardEntries

                        let body: HttpHandler =
                            fun _ (c: HttpContext) ->
                                c.Response.ContentType <- "application/xml; charset=utf-8"
                                c.WriteStringAsync xml

                        return!
                            (ConditionalGet.cacheable signature lastModified options.CacheControl >=> body) next ctx
            })