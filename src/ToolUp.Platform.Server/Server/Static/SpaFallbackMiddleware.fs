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

        if shouldServe then
            ctx.Response.ContentType <- "text/html; charset=utf-8"
            ctx.Response.StatusCode <- 200
            do! ctx.Response.SendFileAsync indexPath
        else
            do! next.Invoke(ctx)
    }