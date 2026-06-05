module PublicSiteWithModules.Program

open System
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.PublicRendering

/// Phase 82 — the canonical hybrid composition root. Combines:
///
///   • ToolUp.Platform.Server — base `ServerApp` carrying the Notes
///     domain module (its SSR `/notes/{slug}` detail-page route +
///     its in-memory store).
///   • ToolUp.PublicRendering — `withPublicRendering` (Phase 80c
///     additive composition) layers the markdown-driven landing
///     page at `/` over the same pipeline.
///   • ToolUp.BrandKit — both the landing page and the Notes detail
///     page render with BrandKit primitives (wordmark / eyebrow /
///     card / page chrome), parameterised by the CSS variable set
///     in `wwwroot/css/brand-tokens.css`.
///
/// The composition root is the load-bearing demonstration of the
/// Wave 13 pattern: a single `ServerApp` carrying both a
/// PublicRendering surface AND a domain module that mounts its own
/// routes, sharing infrastructure. Single binary, single port,
/// composes by piping.

[<EntryPoint>]
let main _ =
    // Seed the in-memory Notes store so both surfaces have content
    // to render on first boot.
    Notes.seedNotes ()

    // Resolve the absolute path to ./content/ relative to the
    // running executable. The markdown files are
    // CopyToOutputDirectory = PreserveNewest so they land next to
    // the binary at build time.
    let contentPath = IO.Path.Combine(AppContext.BaseDirectory, "content")

    let config = {
        ServerConfig.defaults with
            Port = 4020
            PublicBaseUrl = Some "http://localhost:4020"
            PublicRendering = EnabledPublicRendering(ContentRoot contentPath)
    }

    let logger = ConsoleLogger.ConsoleLogger() :> ILogger

    // ─── Hybrid composition (Phase 80c shape) ─────────────────
    //
    // Build the base ServerApp first — config, logger, the Notes
    // module that mounts the `/notes/{slug}` handler. Then layer
    // PublicRendering over the top via the additive extension. The
    // Phase 80c `ComposedCompanions` marker convention guards
    // against accidental double-composition.
    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withLogger logger
    |> ServerApp.addModule Notes.serverModule
    |> PublicRenderingCompose.withPublicRendering (fun pr ->
        pr
        |> PublicRenderingCompose.PublicRenderingServerApp.withLayout (LayoutName "page") Layouts.landingPage)
    |> ServerApp.run