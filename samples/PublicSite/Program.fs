module PublicSite.Program

open System
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.PublicRendering

// Phase 38 canonical reference composition root. Website-class
// deployment: SSR-only, no modules, no AI. Sample binds to ports
// 4010–4019 (website class). The 10-page marketing fixture plus
// 3 news articles plus a 20-entry `redirects.csv` exercise every
// code path of `ToolUp.PublicRendering` end-to-end.

[<EntryPoint>]
let main _ =
    // Resolve the absolute path to ./content/ relative to the
    // running executable. Markdown files are CopyToOutputDirectory =
    // PreserveNewest so they land next to the binary at build time;
    // a dev rebuild updates them in lockstep with source.
    let contentPath = IO.Path.Combine(AppContext.BaseDirectory, "content")

    let config = {
        ServerConfig.defaults with
            Port = 4010
            PublicBaseUrl = Some "http://localhost:4010"
            PublicRendering = EnabledPublicRendering(ContentRoot contentPath)
    }

    let logger = ConsoleLogger.ConsoleLogger() :> ILogger

    PublicRenderingCompose.PublicRenderingServerApp.create ()
    |> PublicRenderingCompose.PublicRenderingServerApp.withConfig config
    |> PublicRenderingCompose.PublicRenderingServerApp.withLogger logger
    |> PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") Layouts.PageLayout.render
    |> PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "article") Layouts.ArticleLayout.render
    // Phase 92 — BrandKit layout library (the ~20-line brand-correct
    // page) + a Feliz-DSL layout via the dual-registration helper.
    |> PublicRenderingCompose.PublicRenderingServerApp.withLayout
        (LayoutName "bk-article")
        Layouts.BrandKitArticleLayout.render
    |> PublicRenderingCompose.PublicRenderingServerApp.withFelizLayout
        (LayoutName "feliz")
        Layouts.FelizPageLayout.render
    |> PublicRenderingCompose.PublicRenderingServerApp.run