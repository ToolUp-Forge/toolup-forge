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
module SitemapGenerator =
    let private xmlEscape (s: string) =
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;")

    /// The public URL universe as a deduped `(Slug * lastmod)` list — the
    /// single source of truth shared by `sitemap.xml` and the Phase 109
    /// IndexNow push channel, so the two can never disagree about what
    /// exists. Pages whose frontmatter sets `sitemap = "exclude"` and
    /// non-`Public` (gated) pages are dropped (Phase 86 — a crawler must
    /// not discover an authenticated / tenant-private slug); the surviving
    /// pages carry their `PublishedAt` (formatted `yyyy-MM-dd`) as the
    /// lastmod. Phase 95 `dynamicSlugs` (content-source-enumerated routes —
    /// e.g. `/tag/{x}`) are appended with no lastmod, deduped against the
    /// page slugs. Order: pages first (input order), then dynamic routes.
    let entries (pages: PublicPage list) (dynamicSlugs: Slug list) : (Slug * string option) list =
        let pageEntries =
            pages
            |> List.choose (fun page ->
                let excluded =
                    not (PublicPage.isPublic page)
                    || page.Frontmatter
                       |> Map.tryFind "sitemap"
                       |> Option.exists (fun v -> v.Equals("exclude", StringComparison.OrdinalIgnoreCase))

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

    /// Build the sitemap XML body for a page list + base URL, plus any
    /// Phase 95 dynamic routes (content-source-enumerated slugs — e.g.
    /// `/tag/{x}` taxonomy pages). Pages whose frontmatter sets
    /// `sitemap = "exclude"` and non-`Public` (gated) pages are skipped;
    /// `dynamicSlugs` are emitted verbatim (a source is responsible for
    /// only enumerating public slugs) with no `<lastmod>`. Trailing
    /// slashes on `baseUrl` are normalised away.
    let generateWith (baseUrl: string) (pages: PublicPage list) (dynamicSlugs: Slug list) : string =
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

        // Phase 109 — both the sitemap and the IndexNow push channel walk
        // the same deduped universe (`entries`), so a slug can never appear
        // in one and not the other.
        for Slug s, lastmod in entries pages dynamicSlugs do
            emit s lastmod

        sb.AppendLine("</urlset>") |> ignore
        sb.ToString()

    /// Build the sitemap XML body for a page list + base URL (no dynamic
    /// routes). Back-compat shim over `generateWith`.
    let generate (baseUrl: string) (pages: PublicPage list) : string = generateWith baseUrl pages []

    /// Giraffe handler at `/sitemap.xml`. Reads pages via the supplied
    /// `IPublicContentApi` and the Phase 95 dynamic routes via
    /// `enumerate` (typically `ContentSource.enumerateAll` over the
    /// registered sources), then emits the generated XML body.
    let handler (baseUrl: string) (api: IPublicContentApi) (enumerate: unit -> Async<Slug list>) : HttpHandler =
        fun next (ctx: HttpContext) -> task {
            let! pages = api.ListPages ""
            let! dynamicSlugs = enumerate ()
            let xml = generateWith baseUrl pages dynamicSlugs
            ctx.Response.ContentType <- "application/xml; charset=utf-8"
            return! ctx.WriteStringAsync xml
        }