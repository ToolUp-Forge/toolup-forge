namespace ToolUp.PublicRendering

open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
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
// ─── Phase 93 — operational extensions (opt-in via StaticExportOptions) ─
//
// - **Per-locale export** — `Locales` with ≥2 entries renders each page
//   into its own `<outputDir>/<locale>/…` subtree (with a per-locale
//   `<html lang>` on Narrative bodies + per-locale sitemap); a single /
//   empty locale list is byte-for-byte the pre-93 single-tree export
//   (GP 11).
// - **Host-config emission** — `HostConfigs` writes `staticwebapp.config.json`
//   (Azure SWA) and/or `_redirects` + `_headers` (Netlify / Cloudflare)
//   from the redirect map + the export's default cache policy, so a
//   static-hosted export carries its redirects + cache headers.
// - **Build-time link/asset check** — `CheckLinks` validates that every
//   internal `href` / `src` in the emitted HTML resolves to a file on
//   disk, logging dead links before deploy.
//
// Gated pages (Phase 86 — `Audience <> Public`) and unpublished ones
// (Phase 89 — `Draft` / `Archived` / not-yet-`Scheduled`) are excluded
// from every tree, the sitemap, and the host-config: `pages` is filtered
// through `PublicPage.isPubliclyDiscoverable` (Phase 38), which conjoins
// both gates. A static export is served by a dumb file host with no
// publish gate of its own, so anything written to disk here is
// permanently and anonymously readable.

/// Static-host targets for host-config emission.
type HostConfigTarget =
    /// `staticwebapp.config.json` — Azure Static Web Apps.
    | AzureStaticWebApps
    /// `_redirects` + `_headers` — Netlify.
    | Netlify
    /// `_redirects` + `_headers` — Cloudflare Pages (same file shapes).
    | Cloudflare

/// Opt-in extensions to the static export. `defaults` reproduces the
/// pre-93 single-tree, no-host-config, no-link-check behaviour (GP 11).
type StaticExportOptions = {
    /// Locales to emit. `[]` or a single entry → one tree at `outputDir`
    /// (unchanged). Two or more → one `<outputDir>/<locale>/…` subtree per
    /// locale, each with a per-locale `<html lang>` (on Narrative bodies)
    /// and sitemap.
    Locales: string list
    /// Host-config files to emit (from the redirect map + `CachePolicy`).
    /// `[]` → none emitted (unchanged).
    HostConfigs: HostConfigTarget list
    /// Default cache policy written into the emitted host-config cache
    /// headers. Defaults to `NoCache` (no `Cache-Control` emitted).
    CachePolicy: CachePolicy
    /// When `true`, validate internal links + asset refs after writing and
    /// log every dead one. Default `false`.
    CheckLinks: bool
    /// Phase 150 — sitemap sharding + universal-lastmod knobs applied to
    /// the emitted `sitemap.xml`. Defaults reproduce the single-`<urlset>`
    /// export byte-for-byte for any universe below the threshold (GP 11);
    /// past it, the export writes a `<sitemapindex>` + `sitemap-<name>.xml`
    /// shard files instead of one oversized file.
    SitemapSharding: SitemapGenerator.SitemapShardingOptions
}

module StaticExportOptions =
    let defaults: StaticExportOptions = {
        Locales = []
        HostConfigs = []
        CachePolicy = CachePolicy.NoCache
        CheckLinks = false
        SitemapSharding = SitemapGenerator.SitemapShardingOptions.defaults
    }

    let withLocales (locales: string list) (o: StaticExportOptions) : StaticExportOptions = { o with Locales = locales }

    let withHostConfigs (targets: HostConfigTarget list) (o: StaticExportOptions) : StaticExportOptions = {
        o with
            HostConfigs = targets
    }

    let withCachePolicy (policy: CachePolicy) (o: StaticExportOptions) : StaticExportOptions = {
        o with
            CachePolicy = policy
    }

    let withLinkCheck (o: StaticExportOptions) : StaticExportOptions = { o with CheckLinks = true }

    /// Phase 150 — set the sitemap sharding + universal-lastmod knobs for
    /// the export (mirrors the runtime `withSitemapShardThreshold` /
    /// `withSitemapClusterKey` / `withSitemapDefaultLastmod` compose knobs).
    let withSitemapSharding
        (sharding: SitemapGenerator.SitemapShardingOptions)
        (o: StaticExportOptions)
        : StaticExportOptions =
        { o with SitemapSharding = sharding }

/// Pure generators for static-host config files. Each takes the public
/// redirect map (gated slugs already excluded by the caller) + the
/// export's default cache policy and produces the file contents.
module StaticHostConfig =

    /// The `Cache-Control` string a policy maps to, or `None` for
    /// `NoCache` (no header emitted).
    let cacheControl (policy: CachePolicy) : string option =
        match policy with
        | CachePolicy.NoCache -> None
        | CachePolicy.Cache(ttl, swr) ->
            if swr then
                Some(sprintf "public, max-age=%d, stale-while-revalidate=%d" ttl ttl)
            else
                Some(sprintf "public, max-age=%d" ttl)

    /// Azure Static Web Apps `staticwebapp.config.json` — a `routes`
    /// array (one redirect each) plus optional `globalHeaders`.
    let azureStaticWebApps (redirects: Redirect list) (policy: CachePolicy) : string =
        let routes = JsonArray()

        for r in redirects do
            let route = JsonObject()
            route["route"] <- JsonValue.Create r.From
            route["redirect"] <- JsonValue.Create r.To
            route["statusCode"] <- JsonValue.Create r.StatusCode
            routes.Add route

        let root = JsonObject()
        root["routes"] <- routes

        match cacheControl policy with
        | Some cc ->
            let headers = JsonObject()
            headers["Cache-Control"] <- JsonValue.Create cc
            root["globalHeaders"] <- headers
        | None -> ()

        root.ToJsonString(System.Text.Json.JsonSerializerOptions(WriteIndented = true))

    /// Netlify / Cloudflare `_redirects` — one `from to statusCode` line
    /// per redirect.
    let redirectsFile (redirects: Redirect list) : string =
        redirects
        |> List.map (fun r -> sprintf "%s %s %d" r.From r.To r.StatusCode)
        |> String.concat "\n"

    /// Netlify / Cloudflare `_headers` — a single `/*` block carrying the
    /// cache policy. Empty string when the policy is `NoCache`.
    let headersFile (policy: CachePolicy) : string =
        match cacheControl policy with
        | Some cc -> sprintf "/*\n  Cache-Control: %s\n" cc
        | None -> ""

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
    /// `<root>/index.html`; every other slug writes to
    /// `<root>/<slug>/index.html`. Directory-index shape works
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

    /// Apply a locale to a page for per-locale export: stamps the
    /// `<html lang>` on a Narrative body so each locale tree differs
    /// observably. Non-Narrative bodies (Markdown / Html) are unchanged —
    /// per-locale content translation is the deployment's concern (wire a
    /// locale-specific content API); this stamps the language only.
    let private withLocale (locale: string) (page: PublicPage) : PublicPage =
        if locale = "" then
            page
        else
            match page.Body with
            | Narrative doc -> {
                page with
                    Body = Narrative { doc with Lang = Some locale }
              }
            | _ -> page

    /// Render every public page + every enumerated dynamic route into the
    /// tree rooted at `troot`, plus a `sitemap.xml`. Returns the count of
    /// pages written. Shared by the single-tree and per-locale paths.
    let private renderTree
        (publicBaseUrl: string)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (api: IPublicContentApi)
        (pages: PublicPage list)
        (dynamicSlugs: Slug list)
        (locale: string)
        (troot: string)
        (sharding: SitemapGenerator.SitemapShardingOptions)
        (logger: ILogger)
        : Async<int> =
        async {
            Directory.CreateDirectory troot |> ignore
            let mutable rendered = 0
            let mutable skipped = 0

            let writePage (page: PublicPage) (slug: string) =
                let localised = withLocale locale page

                match resolveLayout layouts localised with
                | Some layout ->
                    // Phase 111 — inject any per-request head metadata so
                    // the exported document matches what the live handler
                    // serves byte-for-byte.
                    let html =
                        RenderView.AsString.htmlDocument (layout localised)
                        |> PageHeadInjection.injectFromPage localised

                    let full = Path.Combine(troot, slugToRelativePath slug)
                    let dir = Path.GetDirectoryName full

                    if not (Directory.Exists dir) then
                        Directory.CreateDirectory dir |> ignore

                    File.WriteAllText(full, html)
                    rendered <- rendered + 1
                | None ->
                    logger.Warn(sprintf "StaticExport: no layout registered for '%s'" slug)
                    skipped <- skipped + 1

            for page in pages do
                writePage page (Slug.value page.Slug)

            // Phase 95 — render content-source-enumerated dynamic routes
            // with an anonymous build-time context (a source that gates
            // some pages doesn't enumerate them, so nothing private leaks).
            let anonCtx = AccessContext.unrestricted (AnonymousSession "static-export")

            // Phase 38 — a content source synthesises its own pages, so an
            // enumerated route can still resolve to a `Draft` / `Archived`
            // / future-`Scheduled` one. Gate the write the same way the
            // file-backed set above was gated; the anonymous context
            // covers audience, not publish status.
            let dynamicNow = System.DateTimeOffset.UtcNow

            for Slug s in dynamicSlugs do
                let! pageOpt = api.GetPageInContext(s, anonCtx)

                match pageOpt |> Option.filter (PublicPage.isPubliclyDiscoverable dynamicNow) with
                | Some page -> writePage page s
                | None -> ()

            // Phase 150 — past the threshold, write a `<sitemapindex>` +
            // `sitemap-<name>.xml` shard files instead of one oversized
            // file; below it, a single `<urlset>` byte-for-byte pre-150.
            // The universal-lastmod fallback (when set) applies to either
            // shape.
            let sitemapUniverse =
                SitemapGenerator.applyDefaultLastmod
                    sharding.DefaultLastmod
                    (SitemapGenerator.entries pages dynamicSlugs)

            if List.length sitemapUniverse <= sharding.Threshold then
                File.WriteAllText(
                    Path.Combine(troot, "sitemap.xml"),
                    SitemapGenerator.generateUrlSetFrom publicBaseUrl sitemapUniverse
                )
            else
                let shards = SitemapGenerator.shardUniverse sharding sitemapUniverse

                File.WriteAllText(
                    Path.Combine(troot, "sitemap.xml"),
                    SitemapGenerator.generateSitemapIndex publicBaseUrl shards
                )

                for name, shardEntries in shards do
                    File.WriteAllText(
                        Path.Combine(troot, sprintf "sitemap-%s.xml" name),
                        SitemapGenerator.generateUrlSetFrom publicBaseUrl shardEntries
                    )

            if skipped > 0 then
                logger.Warn(sprintf "StaticExport: skipped %d page(s) with no registered layout in '%s'" skipped troot)

            return rendered
        }

    /// Internal-link / asset targets in an HTML string: `href` / `src`
    /// values that are same-origin (rooted `/…` or relative), skipping
    /// external (`http`, `//`), `#`, `mailto:` / `tel:`, and `data:`.
    let internalRefs (html: string) : string list =
        Regex.Matches(html, "(?:href|src)\\s*=\\s*[\"']([^\"']+)[\"']")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Groups[1].Value.Trim())
        |> Seq.map (fun r ->
            // strip query + fragment
            let noHash = r.Split('#')[0]
            noHash.Split('?')[0])
        |> Seq.filter (fun r ->
            r <> ""
            && not (r.StartsWith "http://")
            && not (r.StartsWith "https://")
            && not (r.StartsWith "//")
            && not (r.StartsWith "mailto:")
            && not (r.StartsWith "tel:")
            && not (r.StartsWith "data:"))
        |> Seq.distinct
        |> List.ofSeq

    /// True when a rooted (`/…`) ref resolves to a file under `outputDir`,
    /// trying the directory-index shapes a static host serves: `/x` →
    /// `x`, `x/index.html`, or `x.html`.
    let private refResolves (outputDir: string) (ref: string) : bool =
        if not (ref.StartsWith "/") then
            // Relative refs are unusual in the absolute-URL layouts the
            // SDK emits; treat them as resolvable (not flagged) to avoid
            // false positives.
            true
        else
            let rel = ref.TrimStart('/')

            let candidates = [
                Path.Combine(outputDir, rel)
                Path.Combine(outputDir, rel, "index.html")
                Path.Combine(outputDir, rel + ".html")
            ]

            candidates |> List.exists File.Exists

    /// Scan every emitted `.html` file under `outputDir` and return the
    /// distinct internal refs that don't resolve to a file on disk.
    let checkLinks (outputDir: string) : string list =
        Directory.EnumerateFiles(outputDir, "*.html", SearchOption.AllDirectories)
        |> Seq.collect (fun f -> internalRefs (File.ReadAllText f))
        |> Seq.distinct
        |> Seq.filter (fun r -> not (refResolves outputDir r))
        |> List.ofSeq

    let private writeHostConfigs (outputDir: string) (options: StaticExportOptions) (redirects: Redirect list) =
        for target in options.HostConfigs |> List.distinct do
            match target with
            | AzureStaticWebApps ->
                File.WriteAllText(
                    Path.Combine(outputDir, "staticwebapp.config.json"),
                    StaticHostConfig.azureStaticWebApps redirects options.CachePolicy
                )
            | Netlify
            | Cloudflare ->
                File.WriteAllText(Path.Combine(outputDir, "_redirects"), StaticHostConfig.redirectsFile redirects)
                let headers = StaticHostConfig.headersFile options.CachePolicy

                if headers <> "" then
                    File.WriteAllText(Path.Combine(outputDir, "_headers"), headers)

    /// Phase 93 — the full export with operational extensions. `run` is
    /// the back-compat wrapper (default options, no compose-registered
    /// redirects).
    let runWith
        (options: StaticExportOptions)
        (composeRedirects: Redirect list)
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

                let api: IPublicContentApi =
                    match contentApiOverride with
                    | Some explicit -> explicit
                    | None -> PublicContentApiImpl.create loader None contentSources

                // Phase 38 — one clock for the whole export pass, so a
                // `Scheduled` page cannot land in the sitemap but miss the
                // page write (or vice versa) because the wall clock crossed
                // its publish instant mid-run.
                let exportNow = System.DateTimeOffset.UtcNow

                // Phase 86 — emit only `Public` pages; a gated slug is
                // never written to disk and is excluded from the sitemap +
                // host-config below. Phase 38 — and only *published* ones:
                // a static export is served by a dumb file host with no
                // publish gate of its own, so a `Draft` written to disk is
                // permanently, anonymously readable. Phase 632 — the gate
                // now rides the seam, so the ungated set never enters this
                // scope at all and cannot be reached by a later edit.
                let! pages = api.ListPagesPublic(exportNow, "")

                let! dynamicSlugs = ContentSource.enumerateAll contentSources

                // Static assets + host-config sit at the export root (one
                // copy, shared across locale subtrees).
                copyDirRecursive config.PublicPath outputDir

                // Host-config from the public redirect map (file-loaded +
                // compose-registered). No gated slugs appear in redirects.
                let redirects = loader.Redirects @ composeRedirects
                writeHostConfigs outputDir options redirects

                let publicBaseUrl = config.PublicBaseUrl |> Option.defaultValue ""

                // Single-tree (default) when 0/1 locales; per-locale
                // subtrees when ≥2 (GP 11 — single-locale is unchanged).
                let targets =
                    match options.Locales with
                    | []
                    | [ _ ] -> [ "", outputDir ]
                    | locales -> locales |> List.map (fun l -> l, Path.Combine(outputDir, l))

                let mutable total = 0

                for locale, troot in targets do
                    let! n =
                        renderTree
                            publicBaseUrl
                            layouts
                            api
                            pages
                            dynamicSlugs
                            locale
                            troot
                            options.SitemapSharding
                            logger

                    total <- total + n

                if options.CheckLinks then
                    let dead = checkLinks outputDir

                    for d in dead do
                        logger.Warn(sprintf "StaticExport: dead internal link / asset reference: %s" d)

                    if not (List.isEmpty dead) then
                        logger.Warn(sprintf "StaticExport: %d dead internal reference(s) found" (List.length dead))

                logger.Info(
                    sprintf
                        "StaticExport: wrote %d page(s) across %d tree(s) + sitemap to %s"
                        total
                        (List.length targets)
                        outputDir
                )

                return total
        }

    /// Render every page exposed by the content api through the
    /// supplied layout map and write the resulting site under
    /// `outputDir`. Pre-existing contents of `outputDir` are removed
    /// before writing so successive runs don't accumulate stale files.
    /// Back-compat single-tree export — `runWith` adds per-locale trees,
    /// host-config emission, and the link check.
    ///
    /// Returns the count of pages successfully written.
    let run
        (config: ServerConfig)
        (layouts: Map<LayoutName, PublicPage -> XmlNode>)
        (contentApiOverride: IPublicContentApi option)
        (contentSources: IContentSource list)
        (loggerOpt: ILogger option)
        (outputDir: string)
        : Async<int> =
        runWith StaticExportOptions.defaults [] config layouts contentApiOverride contentSources loggerOpt outputDir