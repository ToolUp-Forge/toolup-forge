namespace ToolUp.Platform

open System
open System.IO
open Microsoft.AspNetCore.Http

// ─── Phase 3b.D — SPA shell fallback middleware ────────────────────
//
// For any GET request that:
//   - is not an API route (`/api/...`)
//   - is not a liveness probe (`/health`, `/ready`)
//   - is not a dev-inspector route (`/dev/...`)
//   - does not carry a file extension (i.e. is an SPA-style route,
//     not a static-asset request)
//   - and was not already served by `PrerenderedRoutesMiddleware` (a
//     prerendered `.html` matched) or by `UseStaticFiles` (a real
//     static file matched on disk)
//
// ... serves `{PublicPath}/index.html` so the SPA's client-side
// router can take over.
//
// Without this fallback, OIDC callback URLs (e.g.
// `/auth/callback?code=...&state=...`) 404 at Kestrel before the SPA
// can run its PKCE handler — sign-in completes at the issuer but the
// browser never reaches the callback page that would persist the
// access token. Plain-deep-link SPA routes (`/teams`, `/dashboard`,
// etc.) have the same shape and were broken in the same way until
// this middleware shipped.
//
// Position in pipeline: after `UseStaticFiles` (so real static
// assets short-circuit first), before any API router. The
// `Path.HasExtension` check keeps the middleware inert for asset
// requests — `/missing-asset.png` still 404s rather than silently
// serving an HTML shell with `Content-Type: text/html` (which would
// confuse the browser into rendering it).
//
// No-op when `{PublicPath}/index.html` is missing. Dev-mode runs
// (Vite serves the index) skip this middleware entirely because
// `UseStaticFiles` isn't registered when `PublicPath` doesn't exist —
// so the SPA fallback's `next.Invoke` path is never reached.

// Phase 988 — precedence against server-rendered routes. The shell-first
// order above is right for a pure SPA: every extensionless path is the
// client router's. It is wrong once the deployment also serves pages from
// the server — SSR slugs, gated pages, a server-side sign-in callback —
// because those are extensionless GETs too, and the shell answered them
// before the router ever ran: with a client bundle shipped, no SSR page
// was reachable. A composition that serves such routes registers
// `SpaFallbackPrecedence.RouterFirst` in DI, and the fallback then runs
// the rest of the pipeline first and serves the shell only to a GET that
// nothing downstream answered (an unstarted 404). Unregistered, the
// shell-first behaviour is unchanged (GP 11). `/api`, the probes, `/dev`
// and paths with an extension are excluded in both orders, and static
// files are served upstream of this middleware in both.

/// Phase 988 — which answers an extensionless GET first: the SPA shell or
/// the server's router. Registered in DI by a composition that serves
/// server-rendered routes (`ToolUp.PublicRendering`, the interactive SSR
/// sign-in); absent means `ShellFirst`.
type SpaFallbackPrecedence =
    /// The shell answers every candidate path (the pre-988 behaviour).
    | ShellFirst
    /// The router answers first; the shell is served only where nothing
    /// downstream did.
    | RouterFirst

module SpaFallbackPrecedence =
    open Microsoft.Extensions.DependencyInjection
    open Microsoft.Extensions.DependencyInjection.Extensions

    /// Register `RouterFirst`. Idempotent: several compositions that serve
    /// server-rendered routes may each call it.
    let useRouterFirst (services: IServiceCollection) : IServiceCollection =
        services.RemoveAll<SpaFallbackPrecedence>() |> ignore
        services.AddSingleton<SpaFallbackPrecedence>(RouterFirst)

type SpaFallbackMiddleware(next: RequestDelegate, config: ServerConfig) =
    let publicPath = Path.GetFullPath config.PublicPath
    let indexPath = Path.Combine(publicPath, "index.html")

    let isExcluded (path: string) =
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/ready", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/dev/", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/dev", StringComparison.OrdinalIgnoreCase)

    member _.InvokeAsync(ctx: HttpContext) = task {
        let path = ctx.Request.Path.Value
        let isGet = HttpMethods.IsGet ctx.Request.Method

        let shouldServe =
            isGet
            && not (String.IsNullOrEmpty path)
            && not (isExcluded path)
            && not (Path.HasExtension path)
            && File.Exists indexPath

        let routerFirst =
            match ctx.RequestServices with
            | null -> false
            | services ->
                match services.GetService typeof<SpaFallbackPrecedence> with
                | :? SpaFallbackPrecedence as precedence -> precedence = RouterFirst
                | _ -> false

        let serveShell () =
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            ctx.Response.StatusCode <- 200
            ctx.Response.SendFileAsync indexPath

        if shouldServe && routerFirst then
            do! next.Invoke(ctx)

            // Nothing downstream answered: the path is the client router's.
            if not ctx.Response.HasStarted && ctx.Response.StatusCode = 404 then
                do! serveShell ()
        elif shouldServe then
            do! serveShell ()
        else
            do! next.Invoke(ctx)
    }