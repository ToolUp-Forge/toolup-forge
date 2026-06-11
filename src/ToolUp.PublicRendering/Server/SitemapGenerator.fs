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

    /// Build the sitemap XML body for a page list + base URL. Pages
    /// whose frontmatter sets `sitemap = "exclude"` are skipped.
    /// Trailing slashes on `baseUrl` are normalised away so callers
    /// can pass either `https://example.com` or `https://example.com/`.
    let generate (baseUrl: string) (pages: PublicPage list) : string =
        let normalisedBase = baseUrl.TrimEnd('/')
        let sb = StringBuilder()
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
                let slug = Slug.value page.Slug
                let url = normalisedBase + "/" + slug |> xmlEscape
                sb.AppendLine("  <url>") |> ignore
                sb.AppendLine(sprintf "    <loc>%s</loc>" url) |> ignore

                match page.PublishedAt with
                | Some d ->
                    sb.AppendLine(sprintf "    <lastmod>%s</lastmod>" (d.ToString("yyyy-MM-dd")))
                    |> ignore
                | None -> ()

                sb.AppendLine("  </url>") |> ignore

        sb.AppendLine("</urlset>") |> ignore
        sb.ToString()

    /// Giraffe handler at `/sitemap.xml`. Reads pages via the
    /// supplied `IPublicContentApi` and emits the generated XML
    /// body.
    let handler (baseUrl: string) (api: IPublicContentApi) : HttpHandler =
        fun next (ctx: HttpContext) -> task {
            let! pages = api.ListPages ""
            let xml = generate baseUrl pages
            ctx.Response.ContentType <- "application/xml; charset=utf-8"
            return! ctx.WriteStringAsync xml
        }