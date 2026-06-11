module MultiSitePublic.Program

open System
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.PublicRendering

// Phase 115 worked sample. One process serves three independent
// markdown-file-backed websites, matched on the request's Host
// header (`docs/platform/multi-site-public-rendering.md` carries
// the full compose shape):
//
//   * the DEFAULT site — every host no satellite claims, including
//     a bare `localhost` browser hit (content/default, shared layout,
//     `ServerConfig.PublicBaseUrl`).
//   * sitea.example    — satellite; own content root + base URL +
//     Atom feed, inheriting the shared layout (content/sitea).
//   * siteb.example    — satellite with a bespoke layout on top
//     (content/siteb).
//
// Pages, sitemaps, and feeds all dispatch per host. Verify by
// pinning the Host header against the localhost listener:
//
//   curl -s http://localhost:13950/                                      # default site
//   curl -s -H "Host: sitea.example" http://localhost:13950/             # site A page (shared layout)
//   curl -s -H "Host: siteb.example" http://localhost:13950/             # site B page (bespoke layout)
//   curl -s -H "Host: sitea.example" http://localhost:13950/sitemap.xml  # https://sitea.example origins only
//   curl -s -H "Host: siteb.example" http://localhost:13950/feed.atom    # site B's own feed document
//
// Sample binds to port 13950 (samples band 13950-13959 — see
// ../CLAUDE.md).

[<EntryPoint>]
let main _ =
    // Content trees are CopyToOutputDirectory = PreserveNewest, so
    // they land next to the binary; resolve them absolutely.
    let contentDir sub =
        IO.Path.Combine(AppContext.BaseDirectory, "content", sub)

    let config = {
        ServerConfig.defaults with
            Port = 13950
            // The DEFAULT site — served on every host no satellite
            // claims.
            PublicBaseUrl = Some "http://localhost:13950"
            PublicRendering = EnabledPublicRendering(ContentRoot(contentDir "default"))
    }

    let logger = ConsoleLogger.ConsoleLogger() :> ILogger

    // Satellite 1 — own content root + base URL. Declares no layouts,
    // so it inherits the shared compose-registered "page" layout.
    // Its Atom feed is mounted host-gated: `/feed.atom` answers with
    // this document only when the request's Host is one of the
    // site's claimed names.
    let siteA = {
        PublicSite.create
            "sitea"
            [ "sitea.example"; "www.sitea.example" ]
            "https://sitea.example"
            (ContentRoot(contentDir "sitea")) with
            Feeds = [
                {
                    NarrativeFeedConfig.defaults with
                        Title = "Site A — Releases"
                        SelfUrl = "/feed.atom"
                        AlternateUrl = "https://sitea.example/"
                }
            ]
    }

    // Satellite 2 — bespoke layout map: pages on siteb.example render
    // through SiteBLayout instead of the shared layout.
    let siteB = {
        PublicSite.create "siteb" [ "siteb.example" ] "https://siteb.example" (ContentRoot(contentDir "siteb")) with
            Layouts = Map [ LayoutName "page", Layouts.SiteBLayout.render ]
            Feeds = [
                {
                    NarrativeFeedConfig.defaults with
                        Title = "Site B — Field Notes"
                        SelfUrl = "/feed.atom"
                        AlternateUrl = "https://siteb.example/"
                }
            ]
    }

    PublicRenderingCompose.PublicRenderingServerApp.create ()
    |> PublicRenderingCompose.PublicRenderingServerApp.withConfig config
    |> PublicRenderingCompose.PublicRenderingServerApp.withLogger logger
    |> PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") Layouts.SharedLayout.render
    // Compose-level feed — once satellites exist this serves the
    // DEFAULT site's hosts only (Phase 115); the satellites' own
    // `Feeds` lists above are mounted host-gated against each site's
    // content API.
    |> PublicRenderingCompose.PublicRenderingServerApp.withFeed {
        NarrativeFeedConfig.defaults with
            Title = "Default Site — Feed"
            AlternateUrl = "http://localhost:13950/"
    }
    |> PublicRenderingCompose.PublicRenderingServerApp.withSite siteA
    |> PublicRenderingCompose.PublicRenderingServerApp.withSite siteB
    |> PublicRenderingCompose.PublicRenderingServerApp.run