namespace ToolUp.PublicRendering

open System
open Giraffe.ViewEngine
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 114 — multi-host public rendering (site registry) ────────
//
// One `ToolUp.PublicRendering` instance can serve N independent
// websites on N domains: each registered `PublicSiteDef` claims a set
// of host names and brings its own content root, layouts, redirects,
// and public base URL. Requests are matched per-host at the handler
// chain; hosts matching no registered site — including every request
// in a zero-`withSite` composition — serve the *default site* (the
// `ServerConfig`-level content root + compose-registered layouts +
// `PublicBaseUrl`), so the feature is strictly additive (GP 11) and a
// pipeline that never registers a site allocates nothing here (GP 13).
//
// **v1 scope (deliberate):** satellite sites are markdown-file-backed
// only (`content/**/*.md` + `redirects.csv`). The entity-store
// overlay (runtime-edited pages), request-time `IContentSource`s,
// Atom feeds, `/search`, `/tag/{x}` taxonomy, and the AI publish path
// remain default-site-only. Lifting the CMS tier per-site is future
// work; the home-page / marketing-site shape this targets doesn't
// need it.

/// A satellite website served by this instance, keyed by host name.
/// Register via `PublicRenderingServerApp.withSite`. The default site
/// (the `ServerConfig`-level `EnabledPublicRendering` content root +
/// compose-registered layouts + `PublicBaseUrl`) is NOT a
/// `PublicSiteDef` — it is the fallback every unmatched host serves.
type PublicSiteDef = {
    /// Diagnostic name — appears in validator output and (Phase 115)
    /// names the site's static-export subtree. Must be unique across
    /// registered sites.
    Name: string
    /// Host names this site claims, e.g. `["example.com"; "www.example.com"]`.
    /// Matched case-insensitively against `ctx.Request.Host.Host`
    /// (the port never participates). List `www.` variants explicitly.
    Hosts: string list
    /// Absolute public origin for this site, no trailing slash —
    /// e.g. `"https://example.com"`. Used for the site's
    /// `sitemap.xml` `<loc>` entries (and Phase 115 IndexNow / export).
    BaseUrl: string
    /// The site's markdown content root (`content/**/*.md` +
    /// optional `redirects.csv`), independent of the default site's.
    ContentRoot: ContentRoot
    /// Per-site layouts. `Map.empty` (the `PublicSite.create`
    /// default) inherits the shared compose-registered layouts, so a
    /// satellite with no bespoke design reuses the default site's.
    Layouts: Map<LayoutName, PublicPage -> XmlNode>
    /// Additional redirects appended after the site's own
    /// `redirects.csv` entries (file wins on duplicate `From`) —
    /// mirrors the default site's `withRedirects` semantics.
    Redirects: Redirect list
}

module PublicSite =
    /// Smart constructor: layouts default to empty (→ inherit the
    /// shared layouts), redirects default to the site's
    /// `redirects.csv` only.
    let create (name: string) (hosts: string list) (baseUrl: string) (contentRoot: ContentRoot) : PublicSiteDef = {
        Name = name
        Hosts = hosts
        BaseUrl = baseUrl
        ContentRoot = contentRoot
        Layouts = Map.empty
        Redirects = []
    }

/// Compose-built per-site runtime: the satellite's own loader +
/// file-tier content API + effective (fallback-resolved) layouts.
type SiteRuntime = {
    Def: PublicSiteDef
    Loader: MarkdownContentLoader
    Api: IPublicContentApi
    /// The site's own layouts when non-empty, else the shared
    /// compose-registered layouts (resolved once at compose time).
    Layouts: Map<LayoutName, PublicPage -> XmlNode>
}

/// Host → site lookup built once at compose time and registered as a
/// DI singleton so consumer-supplied custom handlers can resolve the
/// active site too.
type SiteRegistry = {
    /// Lowercased host name → runtime.
    ByHost: Map<string, SiteRuntime>
    /// Every satellite runtime, registration order (Phase 115 walks
    /// this for per-site IndexNow / static export).
    Sites: SiteRuntime list
}

module SiteRegistry =
    let private normaliseHost (h: string) = h.Trim().ToLowerInvariant()

    /// Build the registry: one `MarkdownContentLoader` + file-tier
    /// `PublicContentApiImpl` per satellite (no entity overlay, no
    /// content sources — the v1 scope cut documented above), layouts
    /// falling back to the shared map when the site declares none.
    let build
        (sites: PublicSiteDef list)
        (sharedLayouts: Map<LayoutName, PublicPage -> XmlNode>)
        (logger: ILogger)
        (hotReload: bool)
        : SiteRegistry =
        let runtimes =
            sites
            |> List.map (fun def ->
                let loader = new MarkdownContentLoader(def.ContentRoot, logger, hotReload)
                let api = PublicContentApiImpl.create loader None []

                let layouts =
                    if Map.isEmpty def.Layouts then
                        sharedLayouts
                    else
                        def.Layouts

                {
                    Def = def
                    Loader = loader
                    Api = api
                    Layouts = layouts
                })

        let byHost =
            runtimes
            |> List.collect (fun rt -> rt.Def.Hosts |> List.map (fun h -> normaliseHost h, rt))
            |> Map.ofList

        { ByHost = byHost; Sites = runtimes }

    /// Resolve the satellite claiming the request's host, if any.
    /// `None` → the request belongs to the default site.
    let tryResolve (ctx: HttpContext) (registry: SiteRegistry) : SiteRuntime option =
        let host = ctx.Request.Host.Host

        if String.IsNullOrWhiteSpace host then
            None
        else
            Map.tryFind (normaliseHost host) registry.ByHost

// ─── Phase 114 — startup preflight ───────────────────────────────────

/// Validates the registered satellite-site set at startup:
///   * Error  — a host claimed by two sites (ambiguous dispatch), a
///     site with no hosts (unreachable), a duplicate site name, or a
///     site whose layouts AND the shared layouts are both empty
///     (every page would 500).
///   * Warning — a whitespace / non-absolute `BaseUrl` (sitemap
///     `<loc>` entries would be relative or malformed), or a missing
///     content-root directory (every request on that host 404s).
/// Not registered when no sites are composed (GP 13).
type MultiSiteConfigValidator
    (sites: PublicSiteDef list, sharedLayouts: Map<LayoutName, PublicPage -> XmlNode>, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "public-rendering-multi-site"
        member _.Timeout = timeout

        member _.Validate() = async {
            let errors = ResizeArray<string>()
            let warnings = ResizeArray<string>()

            let duplicateNames =
                sites
                |> List.countBy _.Name
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map fst

            for name in duplicateNames do
                errors.Add(sprintf "site name '%s' is registered more than once." name)

            let duplicateHosts =
                sites
                |> List.collect (fun s -> s.Hosts |> List.map (fun h -> h.Trim().ToLowerInvariant(), s.Name))
                |> List.groupBy fst
                |> List.filter (fun (_, claims) -> (claims |> List.map snd |> List.distinct |> List.length) > 1)

            for host, claims in duplicateHosts do
                errors.Add(
                    sprintf
                        "host '%s' is claimed by more than one site (%s) — dispatch would be ambiguous."
                        host
                        (claims |> List.map snd |> List.distinct |> String.concat ", ")
                )

            for site in sites do
                if List.isEmpty site.Hosts then
                    errors.Add(sprintf "site '%s' declares no hosts — it is unreachable." site.Name)

                if Map.isEmpty site.Layouts && Map.isEmpty sharedLayouts then
                    errors.Add(
                        sprintf
                            "site '%s' has no layouts and no shared layouts are registered — every page would 500."
                            site.Name
                    )

                if String.IsNullOrWhiteSpace site.BaseUrl then
                    warnings.Add(
                        sprintf "site '%s' has a whitespace BaseUrl — sitemap <loc> entries will be relative." site.Name
                    )
                elif not (Uri.IsWellFormedUriString(site.BaseUrl, UriKind.Absolute)) then
                    warnings.Add(
                        sprintf
                            "site '%s' BaseUrl '%s' is not an absolute URI — sitemap <loc> entries will be malformed."
                            site.Name
                            site.BaseUrl
                    )

                let (ContentRoot rootPath) = site.ContentRoot

                if not (IO.Directory.Exists rootPath) then
                    warnings.Add(
                        sprintf
                            "site '%s' content root '%s' does not exist — every request on its hosts will 404."
                            site.Name
                            rootPath
                    )

            if errors.Count > 0 then
                return Error(String.concat "; " (List.ofSeq errors))
            elif warnings.Count > 0 then
                return Warning(String.concat "; " (List.ofSeq warnings))
            else
                return Ok
        }