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

        for page in pages do
            let excluded =
                // Phase 86 — gated (non-`Public`) pages never appear in the
                // sitemap, so a crawler can't discover an authenticated /
                // tenant-private slug.
                not (PublicPage.isPublic page)
                || page.Frontmatter
                   |> Map.tryFind "sitemap"
                   |> Option.exists (fun v -> v.Equals("exclude", StringComparison.OrdinalIgnoreCase))

            if not excluded then
                emit (Slug.value page.Slug) (page.PublishedAt |> Option.map (fun d -> d.ToString("yyyy-MM-dd")))

        // Phase 95 — content-source-enumerated dynamic routes (deduped
        // against the file/overlay pages already emitted).
        let pageSlugs = pages |> List.map (fun p -> Slug.value p.Slug) |> Set.ofList

        for Slug s in dynamicSlugs do
            if not (pageSlugs.Contains s) then
                emit s None

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