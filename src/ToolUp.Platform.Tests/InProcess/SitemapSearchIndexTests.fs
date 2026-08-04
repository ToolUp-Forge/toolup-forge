module ToolUp.Platform.Tests.InProcess.SitemapSearchIndexTests

open System
open System.IO
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Sitemap (Phase 149/150) + search index (Phase 157) ─────────────
//
// Self-contained tests over the SitemapGenerator + SearchIndexEmitter
// surfaces — a minimal in-memory `IPublicContentApi` + a synthetic
// Giraffe HttpContext, so the handlers + pure generators are exercised
// without the markdown content-file pipeline.

let private mkPage (slug: string) (lastmod: DateTimeOffset option) : PublicPage = {
    Slug = Slug slug
    Title = slug
    Description = ""
    Body = Html ""
    Layout = LayoutName "page"
    Frontmatter = Map.empty
    PublishedAt = lastmod
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

/// Minimal in-memory `IPublicContentApi` over a fixed page list.
let private fakeApi (pages: PublicPage list) : IPublicContentApi =
    { new IPublicContentApi with
        member _.GetPage(slug: string) = async { return pages |> List.tryFind (fun p -> Slug.value p.Slug = slug) }

        member _.ListPages(_: string) = async { return pages }

        // Phase 632 — the gated enumeration, via the shipped default body.
        // Deliberately NOT hand-gated here: the Phase 38 surface cases
        // below assert over what the handlers emit, so the fake must be
        // the honest ungated store plus the real gate, not a store that
        // has already had the drafts removed.
        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix

        member _.GetCollection(collectionId: string) = async {
            return pages |> List.filter (fun p -> p.Collection = Some collectionId)
        }

        member _.GetPageInContext(slug: string, _: AccessContext) = async {
            return pages |> List.tryFind (fun p -> Slug.value p.Slug = slug)
        }
    }

let private noDynamic () : Async<Slug list> = async { return [] }

let private mkCtxWith (headers: (string * string) list) : HttpContext =
    let ctx = DefaultHttpContext()

    for (k, v) in headers do
        ctx.Request.Headers[k] <- Microsoft.Extensions.Primitives.StringValues v

    ctx :> HttpContext

/// A context with `Request.Path` set (for `route` / `routef` handlers).
let private mkCtxPath (path: string) : HttpContext =
    let ctx = mkCtxWith []
    ctx.Request.Path <- PathString path
    ctx

/// Run a handler; returns `(304?, statusCode, captured-body)`.
let private runHandler (handler: Giraffe.Core.HttpHandler) (ctx: HttpContext) : int * string =
    use ms = new MemoryStream()
    ctx.Response.Body <- ms

    let next: Giraffe.Core.HttpFunc =
        fun c -> System.Threading.Tasks.Task.FromResult(Some c)

    handler next ctx |> Async.AwaitTask |> Async.RunSynchronously |> ignore
    ms.Position <- 0L
    use sr = new StreamReader(ms)
    ctx.Response.StatusCode, sr.ReadToEnd()

// ─── Phase 149 — cacheable, conditional-GET sitemap ─────────────────

let private sitemap149Tests =
    testList "PublicRendering — Phase 149 cacheable sitemap" [

        testCase "handler emits a weak ETag + Cache-Control; a matching If-None-Match re-poll 304s"
        <| fun _ ->
            let api =
                fakeApi [ mkPage "a" (Some(DateTimeOffset.Parse "2026-05-22")); mkPage "b" None ]

            let handler = SitemapGenerator.handler "https://example.com" api noDynamic

            let ctx1 = mkCtxWith []
            runHandler handler ctx1 |> ignore
            let etag = ctx1.Response.Headers["ETag"].ToString()

            Expect.isTrue (etag.StartsWith "W/\"") "weak ETag emitted (W/-prefixed)"
            Expect.stringContains (ctx1.Response.Headers["Cache-Control"].ToString()) "max-age" "Cache-Control emitted"
            Expect.notEqual ctx1.Response.StatusCode 304 "first crawl is a full response"

            let ctx2 = mkCtxWith [ "If-None-Match", etag ]
            let status2, _ = runHandler handler ctx2
            Expect.equal status2 304 "a matching If-None-Match re-poll 304s"

        testCase "handler 304s a bare If-Modified-Since re-crawl at/after the universe Last-Modified"
        <| fun _ ->
            let api = fakeApi [ mkPage "a" (Some(DateTimeOffset.Parse "2026-05-22")) ]
            let handler = SitemapGenerator.handler "https://example.com" api noDynamic
            let since = DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero)
            let ctx = mkCtxWith [ "If-Modified-Since", since.UtcDateTime.ToString("R") ]
            let status, _ = runHandler handler ctx
            Expect.equal status 304 "304 from the If-Modified-Since gate"

        testCase "the sitemap ETag (digest over the universe) rolls on change, stable otherwise"
        <| fun _ ->
            let sigOf pages =
                IndexNow.computeSignature (SitemapGenerator.entries pages [])

            let p1 = mkPage "a" (Some(DateTimeOffset.Parse "2026-01-01"))
            let sig1 = sigOf [ p1 ]
            Expect.equal sig1 (sigOf [ mkPage "a" (Some(DateTimeOffset.Parse "2026-01-01")) ]) "stable when unchanged"

            Expect.notEqual
                sig1
                (sigOf [ mkPage "a" (Some(DateTimeOffset.Parse "2026-02-02")) ])
                "rolls on lastmod change"

            Expect.notEqual sig1 (sigOf [ p1; mkPage "b" None ]) "rolls when a slug is added"

        testCase "SitemapCache.GetOrBuild memoises per digest and rebuilds on a digest change"
        <| fun _ ->
            let cache = SitemapGenerator.SitemapCache()
            let mutable builds = 0

            let build v =
                (fun () ->
                    builds <- builds + 1
                    v)

            Expect.equal (cache.GetOrBuild "s1" (build "A")) "A" "built"
            Expect.equal (cache.GetOrBuild "s1" (build "A")) "A" "memoised"
            Expect.equal builds 1 "build runs once per digest"
            Expect.equal (cache.GetOrBuild "s2" (build "B")) "B" "rebuilds on a new digest"
            Expect.equal builds 2 "rebuilt once on the new digest"

        testCase "sitemap body is byte-identical with and without the response cache"
        <| fun _ ->
            let api =
                fakeApi [ mkPage "a" (Some(DateTimeOffset.Parse "2026-05-22")); mkPage "b" None ]

            let bodyOf (opts: SitemapGenerator.SitemapHandlerOptions) =
                let _, body =
                    runHandler (SitemapGenerator.handlerWith opts "https://example.com" api noDynamic) (mkCtxWith [])

                body

            let uncached = bodyOf SitemapGenerator.SitemapHandlerOptions.defaults

            let cached =
                bodyOf {
                    SitemapGenerator.SitemapHandlerOptions.defaults with
                        ResponseCache = Some(SitemapGenerator.SitemapCache())
                }

            Expect.stringContains uncached "<urlset" "uncached body is a urlset"
            Expect.equal cached uncached "cached body byte-identical to the uncached body"
    ]

// ─── Phase 150 — sitemap-index sharding + universal lastmod ─────────

let private mkUniverse (n: int) : (Slug * string option) list = [ for i in 1..n -> Slug(sprintf "p%05d" i), None ]

let private sitemap150Tests =
    testList "PublicRendering — Phase 150 sitemap sharding + universal lastmod" [

        testCase "below the threshold the handler body is byte-for-byte the pre-150 single urlset"
        <| fun _ ->
            let pages = [ mkPage "a" (Some(DateTimeOffset.Parse "2026-05-22")); mkPage "b" None ]
            let api = fakeApi pages
            // Default threshold (50k) >> 2 pages → single urlset.
            let _, body =
                runHandler (SitemapGenerator.handler "https://example.com" api noDynamic) (mkCtxWith [])

            Expect.equal
                body
                (SitemapGenerator.generateWith "https://example.com" pages [])
                "byte-for-byte generateWith"

        testCase "past the threshold sitemap.xml becomes a <sitemapindex> of N numeric shards, each <= threshold"
        <| fun _ ->
            let opts = {
                SitemapGenerator.SitemapHandlerOptions.defaults with
                    Sharding = {
                        SitemapGenerator.SitemapShardingOptions.defaults with
                            Threshold = 10
                    }
            }

            let pages = [ for i in 1..25 -> mkPage (sprintf "p%02d" i) None ]
            let api = fakeApi pages

            let _, body =
                runHandler (SitemapGenerator.handlerWith opts "https://example.com" api noDynamic) (mkCtxWith [])

            Expect.stringContains body "<sitemapindex" "emits a sitemapindex past the threshold"
            Expect.stringContains body "/sitemap-1.xml" "lists shard 1"
            Expect.stringContains body "/sitemap-3.xml" "lists shard 3 (ceil 25/10 = 3)"
            Expect.isFalse (body.Contains "/sitemap-4.xml") "exactly ceil(N/threshold) shards"

        testCase "shardUniverse: numeric slices are deterministic + each <= threshold + cover the universe"
        <| fun _ ->
            let opts = {
                SitemapGenerator.SitemapShardingOptions.defaults with
                    Threshold = 10
            }

            let universe = mkUniverse 25
            let run1 = SitemapGenerator.shardUniverse opts universe
            let run2 = SitemapGenerator.shardUniverse opts universe
            Expect.equal run1 run2 "stable membership across two runs of the same content"
            Expect.equal (List.length run1) 3 "ceil(25/10) = 3 shards"
            Expect.isTrue (run1 |> List.forall (fun (_, e) -> List.length e <= 10)) "each shard <= threshold"

            let allSlugs = run1 |> List.collect snd |> List.map fst |> Set.ofList
            Expect.equal (Set.count allSlugs) 25 "every URL is covered exactly once"

        testCase "shardUniverse: cluster key groups by logical content type with stable membership"
        <| fun _ ->
            let clusterOf (Slug s: Slug) = s.Split('/')[0]

            let opts = {
                SitemapGenerator.SitemapShardingOptions.defaults with
                    Threshold = 1000
                    ClusterKey = Some clusterOf
            }

            let universe = [
                Slug "news/a", None
                Slug "news/b", None
                Slug "products/x", None
                Slug "about", None
            ]

            let shards = SitemapGenerator.shardUniverse opts universe
            let names = shards |> List.map fst
            Expect.contains names "news" "a 'news' cluster shard"
            Expect.contains names "products" "a 'products' cluster shard"
            Expect.contains names "about" "an 'about' cluster shard"
            Expect.equal shards (SitemapGenerator.shardUniverse opts universe) "stable membership across runs"

        testCase "applyDefaultLastmod fills only None lastmods when set; identity when unset"
        <| fun _ ->
            let universe = [ Slug "a", Some "2026-01-01"; Slug "b", None ]
            Expect.equal (SitemapGenerator.applyDefaultLastmod None universe) universe "unset → identity (pre-150)"

            let filled = SitemapGenerator.applyDefaultLastmod (Some "2026-06-14") universe
            Expect.equal (filled |> List.find (fun (Slug s, _) -> s = "a") |> snd) (Some "2026-01-01") "existing kept"
            Expect.equal (filled |> List.find (fun (Slug s, _) -> s = "b") |> snd) (Some "2026-06-14") "None filled"

        testCase "generateSitemapIndex emits sitemapindex with shard <loc> + <lastmod>"
        <| fun _ ->
            let shards = [
                "news", [ Slug "news/a", Some "2026-05-01"; Slug "news/b", Some "2026-05-22" ]
            ]

            let xml = SitemapGenerator.generateSitemapIndex "https://example.com" shards
            Expect.stringContains xml "<sitemapindex" "sitemapindex root"
            Expect.stringContains xml "https://example.com/sitemap-news.xml" "child sitemap loc"
            Expect.stringContains xml "<lastmod>2026-05-22</lastmod>" "shard lastmod is the latest entry"

        testCase "shardHandler serves a shard urlset past the threshold and declines below / for unknown shards"
        <| fun _ ->
            let opts = {
                SitemapGenerator.SitemapHandlerOptions.defaults with
                    Sharding = {
                        SitemapGenerator.SitemapShardingOptions.defaults with
                            Threshold = 10
                    }
            }

            let pages = [ for i in 1..25 -> mkPage (sprintf "p%02d" i) None ]
            let api = fakeApi pages
            let handler = SitemapGenerator.shardHandler opts "https://example.com" api noDynamic

            // Known shard → urlset body.
            let status1, body1 = runHandler handler (mkCtxPath "/sitemap-1.xml")
            Expect.equal status1 200 "known shard serves 200"
            Expect.stringContains body1 "<urlset" "shard body is a urlset"

            // Unknown shard → declines (no body written → not 200-with-urlset).
            let _, body99 = runHandler handler (mkCtxPath "/sitemap-99.xml")
            Expect.isFalse (body99.Contains "<urlset") "unknown shard declines (no urlset body)"

        testCase "below the threshold shardHandler declines (no shards exist)"
        <| fun _ ->
            let pages = [ mkPage "a" None; mkPage "b" None ]
            let api = fakeApi pages

            let handler =
                SitemapGenerator.shardHandler
                    SitemapGenerator.SitemapHandlerOptions.defaults
                    "https://example.com"
                    api
                    noDynamic

            let _, body = runHandler handler (mkCtxPath "/sitemap-1.xml")
            Expect.isFalse (body.Contains "<urlset") "no shard served below the threshold"
    ]

// ─── Phase 157 — static client-search index emitter ─────────────────

let private parseEntries (json: string) : JsonElement[] =
    // NB: do not dispose the document — the returned JsonElement values are
    // views into it; disposing would throw ObjectDisposedException on read.
    let doc = JsonDocument.Parse json
    doc.RootElement.EnumerateArray() |> Seq.toArray

let private searchIndex157Tests =
    testList "PublicRendering — Phase 157 search index emitter" [

        testCase "emitter produces valid compact JSON over the file universe"
        <| fun _ ->
            let pages = [
                {
                    mkPage "news/launch" (Some(DateTimeOffset.Parse "2026-05-22")) with
                        Title = "Launch"
                        Collection = Some "news"
                        Frontmatter = Map.ofList [ "keywords", "product, launch" ]
                }
                mkPage "about" None
            ]

            let entries = SearchIndexEmitter.entriesFromPages "https://example.com" pages []
            let json = SearchIndexEmitter.toJson entries
            let parsed = parseEntries json
            Expect.equal parsed.Length 2 "one JSON object per page"

            let launch =
                parsed
                |> Array.find (fun e -> e.GetProperty("url").GetString().EndsWith "/news/launch")

            Expect.equal (launch.GetProperty("title").GetString()) "Launch" "title pulled from the page"
            Expect.equal (launch.GetProperty("kind").GetString()) "news" "kind from the collection"

            let kw =
                launch.GetProperty("keywords").EnumerateArray()
                |> Seq.map (fun e -> e.GetString())
                |> Seq.toList

            Expect.equal kw [ "product"; "launch" ] "keywords from the frontmatter"
            Expect.isFalse (json.Contains "\n") "compact JSON — no newlines"

        testCase "KeywordFormat: default array is unchanged; KeywordsJoined flattens to a string"
        <| fun _ ->
            let entries = [
                {
                    Url = "https://example.com/x"
                    Title = "X"
                    Kind = "doc"
                    Keywords = [ "alpha"; "beta" ]
                }
            ]

            // Default (array) — byte-for-byte the original 157 shape (GP 11).
            Expect.equal
                (SearchIndexEmitter.toJson entries)
                (SearchIndexEmitter.toJsonWith KeywordsArray entries)
                "toJson == array default"

            Expect.stringContains
                (SearchIndexEmitter.toJson entries)
                "\"keywords\":[\"alpha\",\"beta\"]"
                "default emits a JSON array"

            // Joined — a single space-separated keyword string for clients
            // that tokenise a flat string.
            let joined = SearchIndexEmitter.toJsonWith (KeywordsJoined " ") entries
            Expect.stringContains joined "\"keywords\":\"alpha beta\"" "joined emits one string"
            Expect.isFalse (joined.Contains "[\"alpha\"") "no array in joined form"

        testCase "non-ASCII title passes through unescaped (relaxed encoder)"
        <| fun _ ->
            // A musical sharp in a title must stay UTF-8, not become ♯ —
            // so the emitter is byte-compatible with a minimal hand-rolled
            // escaper.
            let entries = [
                {
                    Url = "https://example.com/x"
                    Title = "C♯ major on Piano"
                    Kind = "scale"
                    Keywords = []
                }
            ]

            let json = SearchIndexEmitter.toJson entries
            Expect.stringContains json "C♯ major" "sharp passes through verbatim"
            Expect.isFalse (json.Contains "\\u266f" || json.Contains "\\u266F") "no escaped sharp"

        testCase "handler honours config.KeywordFormat"
        <| fun _ ->
            let custom =
                fun () -> async {
                    return [
                        {
                            Url = "https://example.com/x"
                            Title = "X"
                            Kind = "doc"
                            Keywords = [ "alpha"; "beta" ]
                        }
                    ]
                }

            let config =
                SearchIndexConfig.defaults
                |> SearchIndexConfig.withEntrySource custom
                |> SearchIndexConfig.withKeywordFormat (KeywordsJoined " ")

            let api = fakeApi [ mkPage "ignored" None ]
            let handler = SearchIndexEmitter.handler config "https://example.com" api noDynamic
            let ctx = mkCtxWith []
            ctx.Request.Path <- PathString "/search-index.json"
            let status, body = runHandler handler ctx
            Expect.equal status 200 "served"
            Expect.stringContains body "\"keywords\":\"alpha beta\"" "endpoint emits the joined keyword string"

        testCase "a custom EntrySource overrides the file-backed universe"
        <| fun _ ->
            let custom =
                fun () -> async {
                    return [
                        {
                            Url = "https://example.com/x"
                            Title = "X"
                            Kind = "doc"
                            Keywords = [ "k" ]
                        }
                    ]
                }

            let config = SearchIndexConfig.defaults |> SearchIndexConfig.withEntrySource custom
            let api = fakeApi [ mkPage "ignored" None ]
            let handler = SearchIndexEmitter.handler config "https://example.com" api noDynamic
            let ctx = mkCtxWith []
            ctx.Request.Path <- PathString "/search-index.json"
            let status, body = runHandler handler ctx
            Expect.equal status 200 "served"
            let parsed = parseEntries body
            Expect.equal parsed.Length 1 "consumer entries, not the file universe"
            Expect.equal (parsed[0].GetProperty("title").GetString()) "X" "consumer-supplied entry surfaces"

        testCase "endpoint ETag folds the content version: rolls on change, 304s on a conditional re-fetch"
        <| fun _ ->
            let api1 = fakeApi [ mkPage "a" (Some(DateTimeOffset.Parse "2026-01-01")) ]

            let handler1 =
                SearchIndexEmitter.handler SearchIndexConfig.defaults "https://example.com" api1 noDynamic

            let ctx1 = mkCtxWith []
            ctx1.Request.Path <- PathString "/search-index.json"
            runHandler handler1 ctx1 |> ignore
            let etag1 = ctx1.Response.Headers["ETag"].ToString()
            Expect.isTrue (etag1.StartsWith "W/\"") "weak ETag emitted"

            // Conditional re-fetch with the same content → 304.
            let ctx2 = mkCtxWith [ "If-None-Match", etag1 ]
            ctx2.Request.Path <- PathString "/search-index.json"
            let status2, _ = runHandler handler1 ctx2
            Expect.equal status2 304 "304 on a conditional re-fetch of unchanged content"

            // Different universe → different ETag.
            let api2 = fakeApi [ mkPage "a" (Some(DateTimeOffset.Parse "2026-02-02")) ]

            let handler2 =
                SearchIndexEmitter.handler SearchIndexConfig.defaults "https://example.com" api2 noDynamic

            let ctx3 = mkCtxWith []
            ctx3.Request.Path <- PathString "/search-index.json"
            runHandler handler2 ctx3 |> ignore

            Expect.notEqual
                (ctx3.Response.Headers["ETag"].ToString())
                etag1
                "ETag rolls when the content version changes"

        testCase "the endpoint only answers its configured path"
        <| fun _ ->
            let api = fakeApi [ mkPage "a" None ]

            let handler =
                SearchIndexEmitter.handler SearchIndexConfig.defaults "https://example.com" api noDynamic

            let ctx = mkCtxWith []
            ctx.Request.Path <- PathString "/not-the-index"
            let _, body = runHandler handler ctx
            Expect.isFalse (body.Contains "[") "declines a non-matching path (no JSON body)"
    ]

// ─── Phase 38 — publish-status gate on the discovery/egress surfaces ──
//
// `PublicPageHandler` has always filtered the page READ path through
// `PublicPage.isPubliclyVisible`, so fetching an unpublished slug 404s.
// The ENUMERATION surfaces — sitemap.xml (+ shards), the Atom feed, the
// JSON search index, IndexNow, static export — filtered only on
// `PublicPage.isPublic` (Phase 86 audience), which is an orthogonal
// axis. A `Draft` / `Archived` / not-yet-`Scheduled` page therefore had
// `Audience = Public` and sailed through: its slug reached crawlers (and
// IndexNow actively PUSHED it to search engines), its title and
// description reached the search index, and — worst — the feed carried
// its full rendered body.
//
// These cases pin the gate. Each is paired with a published control, so
// a regression that over-corrects into "nothing is served" fails too.

let private mkPageStatus (slug: string) (status: PublishStatus) : PublicPage = {
    mkPage slug (Some(DateTimeOffset.Parse "2026-05-22")) with
        Status = status
}

let private phase38EgressTests =
    testList "PublicRendering — Phase 38 publish gate on egress surfaces" [

        // The negative control, stated as a measurement rather than a
        // claim: the PRE-38 predicate (`isPublic` alone, which is what
        // every egress surface used) ADMITS a draft. That is the leak,
        // asserted directly — so if someone reverts the egress filters to
        // `isPublic`, this case documents exactly what they restore, and
        // the surface cases below go red.
        testCase "the pre-38 audience-only filter admits a draft; the Phase 38 gate rejects it"
        <| fun _ ->
            let now = DateTimeOffset.Parse "2026-06-01"
            let draft = mkPageStatus "secret-launch" PublishStatus.Draft

            Expect.isTrue
                (PublicPage.isPublic draft)
                "PRE-38 GATE LEAKS: a draft is `Audience = Public`, so an audience-only egress filter admits it"

            Expect.isFalse (PublicPage.isPubliclyDiscoverable now draft) "the Phase 38 gate rejects the same page"

            // …and it still rejects for the right reason on each axis.
            let gated = {
                mkPage "gated" None with
                    Audience = PageAudience.Authenticated
            }

            Expect.isFalse (PublicPage.isPubliclyDiscoverable now gated) "audience axis still gates"

            Expect.isTrue
                (PublicPage.isPubliclyDiscoverable now (mkPageStatus "live" Published))
                "CONTROL: a published, public page is discoverable"

        testCase "sitemap.xml omits draft / archived / future-scheduled and keeps published + due-scheduled"
        <| fun _ ->
            let now = DateTimeOffset.Parse "2026-06-01"

            let pages = [
                mkPageStatus "live" Published
                mkPageStatus "secret-launch" PublishStatus.Draft
                mkPageStatus "retired" Archived
                mkPageStatus "embargoed" (Scheduled(now.AddDays 7.0))
                mkPageStatus "released" (Scheduled(now.AddDays -7.0))
            ]

            let slugs =
                SitemapGenerator.entriesAt now pages [] |> List.map (fun (Slug s, _) -> s)

            Expect.contains slugs "live" "CONTROL: a published page is in the sitemap"
            Expect.contains slugs "released" "CONTROL: a scheduled page past its date is in the sitemap"
            Expect.isFalse (List.contains "secret-launch" slugs) "a draft slug never reaches a crawler"
            Expect.isFalse (List.contains "retired" slugs) "an archived slug never reaches a crawler"
            Expect.isFalse (List.contains "embargoed" slugs) "a future-scheduled slug never reaches a crawler"

        testCase "the rendered sitemap body carries no unpublished slug"
        <| fun _ ->
            let api =
                fakeApi [
                    mkPageStatus "live" Published
                    mkPageStatus "secret-launch" PublishStatus.Draft
                ]

            let handler = SitemapGenerator.handler "https://example.com" api noDynamic
            let _, body = runHandler handler (mkCtxWith [])

            Expect.stringContains body "https://example.com/live" "CONTROL: the published URL is emitted"
            Expect.isFalse (body.Contains "secret-launch") "the draft URL is absent from the emitted XML"

        testCase "the search index leaks neither the slug nor the title of an unpublished page"
        <| fun _ ->
            let draft = {
                mkPageStatus "secret-launch" PublishStatus.Draft with
                    Title = "Project Nightingale"
            }

            let api = fakeApi [ mkPageStatus "live" Published; draft ]

            let handler =
                SearchIndexEmitter.handler SearchIndexConfig.defaults "https://example.com" api noDynamic

            let _, body = runHandler handler (mkCtxPath "/search-index.json")

            Expect.stringContains body "/live" "CONTROL: the published page is indexed"
            Expect.isFalse (body.Contains "secret-launch") "the draft slug is absent from the index"
            Expect.isFalse (body.Contains "Project Nightingale") "the draft TITLE is absent from the index"

        testCase "the Atom feed carries no unpublished entry (the body-leak case)"
        <| fun _ ->
            let now = DateTimeOffset.Parse "2026-06-01"

            let docOf (title: string) : NarrativeDocument = {
                Title = title
                Subtitle = None
                Sections = []
                Provenance = None
                Lang = None
                CanonicalUrl = None
            }

            let withDoc (slug: string) (status: PublishStatus) (title: string) = {
                mkPageStatus slug status with
                    Body = Narrative(docOf title)
            }

            let entries =
                NarrativeFeedHandler.selectEntriesAt now NarrativeFeedConfig.defaults [
                    withDoc "live" Published "Published Post"
                    withDoc "secret-launch" PublishStatus.Draft "Project Nightingale"
                    withDoc "embargoed" (Scheduled(now.AddDays 7.0)) "Embargoed Announcement"
                ]

            let titles = entries |> List.map _.Title

            Expect.contains titles "Published Post" "CONTROL: the published entry is syndicated"

            Expect.isFalse
                (List.contains "Project Nightingale" titles)
                "the draft's title AND body never reach the feed"

            Expect.isFalse (List.contains "Embargoed Announcement" titles) "a future-scheduled entry is not syndicated"

        testCase "a scheduled page crosses into the sitemap exactly at its publish instant"
        <| fun _ ->
            let at = DateTimeOffset.Parse "2026-06-01T12:00:00Z"
            let page = mkPageStatus "embargoed" (Scheduled at)

            let slugsAt (now: DateTimeOffset) =
                SitemapGenerator.entriesAt now [ page ] [] |> List.map (fun (Slug s, _) -> s)

            Expect.isFalse (List.contains "embargoed" (slugsAt (at.AddSeconds -1.0))) "absent one second before"
            Expect.contains (slugsAt at) "embargoed" "present exactly at the publish instant"
            Expect.contains (slugsAt (at.AddSeconds 1.0)) "embargoed" "present after"
    ]

let tests =
    testList "PublicRendering — sitemap & search index (Phase 149/150/157)" [
        sitemap149Tests
        sitemap150Tests
        searchIndex157Tests
        phase38EgressTests
    ]