module ToolUp.Platform.StaticPathBehaviourValidator

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Gap #5 — StaticPathBehaviour=Warn in prod-shaped deployment ────
//
// `ServerConfig.StaticPathBehaviour` defaults to `Warn` — correct for
// `dotnet run` development where the Vite dev server is serving assets
// and `deploy/public` is empty. Wrong for production deployments
// shipping their own static assets: `compose` skips
// `app.UseStaticFiles()`, the SPA shell loads, and every static asset
// returns 404. The Warn line is buried in startup logs.
//
// This validator catches the gap at preflight: when `RequireHttps =
// true` (production-shape signal), `StaticPathBehaviour = Warn`, AND
// the `PublicPath` directory exists on disk (the build artefact has
// landed, so the deployment DOES intend to serve assets), warn the
// operator with the exact env var to set.
//
// Severity is `Warning` (not `Error`) because:
//   1. Pure-API deployments without browser surface legitimately have
//      `PublicPath` empty AND `RequireHttps = true`. The `RequireExist`
//      mode would produce a noisy startup; `SkipSilent` is the right
//      choice for them, but their Warn → SkipSilent migration shouldn't
//      be a hard refusal.
//   2. Some deployments serve assets via an external CDN with
//      `RequireHttps` enabled at the SDK layer for the API surface.
//      Same `SkipSilent` recommendation; not a hard refusal.

/// Gap #5 — config validator that warns when a production-shaped
/// deployment leaves `StaticPathBehaviour = Warn` despite a populated
/// `PublicPath` directory. Production deployments should choose
/// `RequireExist` (fail loudly on a missing asset bundle) or
/// `SkipSilent` (pure-API / external-CDN deployments).
type StaticPathBehaviourValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "static-path-behaviour"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isProductionShape = config.RequireHttps
            let isWarn = config.StaticPathBehaviour = StaticPathBehaviour.Warn

            let publicPathPopulated =
                try
                    Directory.Exists config.PublicPath
                    && (Directory.EnumerateFileSystemEntries config.PublicPath
                        |> Seq.tryHead
                        |> Option.isSome)
                with _ ->
                    false

            if isProductionShape && isWarn && publicPathPopulated then
                return
                    Warning(
                        sprintf
                            "TOOLUP_REQUIRE_HTTPS=1 (production shape) and ServerConfig.PublicPath=%s contains assets, but ServerConfig.StaticPathBehaviour defaults to Warn — `compose` skips app.UseStaticFiles() and every request to /static/* / /assets/* returns 404. Set TOOLUP_STATIC_PATH_BEHAVIOUR=require for SDK-served assets (fails loudly if the artefact is missing), or =skip for pure-API / external-CDN deployments. Default of Warn is appropriate only for `dotnet run` dev where Vite serves assets."
                            config.PublicPath
                    )
            else
                return Ok
        }