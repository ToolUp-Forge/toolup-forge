namespace ToolUp.PublicRendering

open System.IO
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Server

// ─── Build-time static export ────────────────────────────────────────
//
// `StaticExport.run` drives the same loader + `IPublicContentApi` the
// runtime `PublicRenderingCompose.run` path drives, but writes the
// rendered pages to disk instead of starting Kestrel. Output is a
// directory tree hostable on any static-file host (Azure Static Web
// Apps, Netlify, GitHub Pages, S3 + CloudFront) — no .NET runtime
// needed in production.
//
// The terminus shape matches what static hosts expect:
// - Each page lands at `<outputDir>/<slug>/index.html` (the root
//   `index` slug lands at `<outputDir>/index.html`). Directory-index
//   serving is universal across SWA / Netlify / S3 / GitHub Pages.
// - `<outputDir>/sitemap.xml` is written from the same
//   `SitemapGenerator.generate` the live handler uses, so the static
//   and SSR outputs are byte-identical for the same content tree.
// - The consumer's `ServerConfig.PublicPath` (typically `wwwroot/`)
//   is mirrored verbatim into `<outputDir>` so favicons, compiled
//   CSS, images, robots.txt, etc. all come along.
//
// Redirects (loaded from `<contentRoot>/redirects.csv`) are not
// translated into a static-host route file in v1 — different hosts
// use incompatible config formats. Consumers author their host-
// specific config (e.g. `staticwebapp.config.json`) by hand. The
// redirects are still loaded into the live SSR server for the same
// consumer in development.

module StaticExport =
    /// Layout resolution mirrors `PublicPageHandler.resolveLayout`:
    /// try the page's `Layout` name; fall back to the first-registered
    /// layout when the name is unknown.
    let private resolveLayout
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (page: PublicPage)
        : (PublicPage -> XmlNode) option =
        match Map.tryFind page.Layout layouts with
        | Some f -> Some f
        | None -> layouts |> Map.toSeq |> Seq.tryHead |> Option.map snd

    /// Slug → relative output path. The root `index` slug writes to
    /// `<outputDir>/index.html`; every other slug writes to
    /// `<outputDir>/<slug>/index.html`. Directory-index shape works
    /// across every static host without per-host rewrite rules.
    let private slugToRelativePath (slug: string) : string =
        if slug = "" || slug = "index" then
            "index.html"
        else
            Path.Combine(slug, "index.html")

    /// Recursively copy `source` into `target`, preserving directory
    /// structure. Used to mirror `ServerConfig.PublicPath` into the
    /// export root. Tolerates a missing source (no static assets is
    /// a valid deployment shape).
    let private copyDirRecursive (source: string) (target: string) =
        if Directory.Exists source then
            for src in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
                let relative = Path.GetRelativePath(source, src)
                let dst = Path.Combine(target, relative)
                let dstDir = Path.GetDirectoryName dst

                if not (Directory.Exists dstDir) then
                    Directory.CreateDirectory dstDir |> ignore

                File.Copy(src, dst, overwrite = true)

    /// Render every page exposed by the content api through the
    /// supplied layout map and write the resulting site under
    /// `outputDir`. Pre-existing contents of `outputDir` are removed
    /// before writing so successive runs don't accumulate stale files.
    ///
    /// Parameters are explicit rather than wrapped in
    /// `PublicRenderingServerApp` so this module sits below
    /// `PublicRenderingCompose` in the build graph. The thin record-
    /// shaped wrapper lives in `PublicRenderingServerApp.exportStatic`.
    ///
    /// Returns the count of pages successfully written. Pages whose
    /// `Layout` doesn't match any registered layout AND for which no
    /// fallback exists (i.e. no layouts registered at all) are logged
    /// as warnings and skipped.
    let run
        (config: ServerConfig)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (contentApiOverride: IPublicContentApi option)
        (contentSources: IContentSource list)
        (loggerOpt: ILogger option)
        (outputDir: string)
        : Async<int> =
        async {
            let logger =
                loggerOpt
                |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

            match config.PublicRendering with
            | NoPublicRendering ->
                logger.Warn "StaticExport: ServerConfig.PublicRendering is NoPublicRendering; nothing to export."

                return 0

            | EnabledPublicRendering root ->
                if Directory.Exists outputDir then
                    Directory.Delete(outputDir, recursive = true)

                Directory.CreateDirectory outputDir |> ignore

                // Same MarkdownContentLoader + PublicContentApiImpl the
                // runtime path builds. `hotReload = false` — no point
                // watching files for a one-shot build pass.
                let loader = new MarkdownContentLoader(root, logger, hotReload = false)

                // Static export is a context-free, build-time pass — it
                // enumerates pages via `ListPages` / `GetPage`, never
                // `GetPageInContext`, so request-time content sources
                // (which need an `AccessContext`) are intentionally absent.
                let api: IPublicContentApi =
                    match contentApiOverride with
                    | Some explicit -> explicit
                    // Phase 95 — pass the registered sources so the
                    // export can resolve their enumerated dynamic routes
                    // (via `GetPageInContext` with an anonymous context).
                    | None -> PublicContentApiImpl.create loader None contentSources

                let! allPages = api.ListPages ""

                // Phase 86 — static export emits only `Public` pages; a
                // gated (authenticated / tenant / client) slug is never
                // written to disk, and is excluded from the sitemap below.
                // A build-time export has no per-request principal, so a
                // gated page could never be authorised here anyway.
                let pages = allPages |> List.filter PublicPage.isPublic

                // Mirror PublicPath (typically wwwroot/) into the
                // export root before writing pages so a page-with-same-
                // slug-as-static-asset would still overwrite (pages
                // win — they're the canonical content).
                copyDirRecursive config.PublicPath outputDir

                let mutable rendered = 0
                let mutable skipped = 0

                for page in pages do
                    match resolveLayout layouts page with
                    | Some layout ->
                        let node = layout page
                        let html = RenderView.AsString.htmlDocument node
                        let slug = Slug.value page.Slug
                        let relative = slugToRelativePath slug
                        let full = Path.Combine(outputDir, relative)
                        let dir = Path.GetDirectoryName full

                        if not (Directory.Exists dir) then
                            Directory.CreateDirectory dir |> ignore

                        File.WriteAllText(full, html)
                        rendered <- rendered + 1
                    | None ->
                        logger.Warn(sprintf "StaticExport: no layout registered for page '%s'" (Slug.value page.Slug))

                        skipped <- skipped + 1

                // Phase 95 — render content-source-enumerated dynamic
                // routes (e.g. `/tag/{x}`) with an anonymous build-time
                // context. A source that gates some pages simply doesn't
                // enumerate them, so nothing private reaches disk.
                let! dynamicSlugs = ContentSource.enumerateAll contentSources
                let anonCtx = AccessContext.unrestricted (AnonymousSession "static-export")

                for Slug s in dynamicSlugs do
                    let! pageOpt = api.GetPageInContext(s, anonCtx)

                    match pageOpt with
                    | Some page ->
                        match resolveLayout layouts page with
                        | Some layout ->
                            let html = RenderView.AsString.htmlDocument (layout page)
                            let relative = slugToRelativePath s
                            let full = Path.Combine(outputDir, relative)
                            let dir = Path.GetDirectoryName full

                            if not (Directory.Exists dir) then
                                Directory.CreateDirectory dir |> ignore

                            File.WriteAllText(full, html)
                            rendered <- rendered + 1
                        | None ->
                            logger.Warn(sprintf "StaticExport: no layout registered for dynamic route '%s'" s)
                            skipped <- skipped + 1
                    | None -> ()

                let publicBaseUrl = config.PublicBaseUrl |> Option.defaultValue ""
                let sitemapXml = SitemapGenerator.generateWith publicBaseUrl pages dynamicSlugs
                File.WriteAllText(Path.Combine(outputDir, "sitemap.xml"), sitemapXml)

                logger.Info(
                    sprintf
                        "StaticExport: wrote %d pages (%d skipped, %d static assets) + sitemap.xml to %s"
                        rendered
                        skipped
                        (Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories)
                         |> Seq.length)
                        outputDir
                )

                return rendered
        }