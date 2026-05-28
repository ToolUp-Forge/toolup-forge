namespace ToolUp.Platform

open System
open System.IO
open Microsoft.AspNetCore.Http

// ─── Phase 57 — prerendered-routes handler ─────────────────────────
//
// Maps an incoming GET `/some/route` to a prerendered HTML file the
// FAKE `Prerender` target wrote under `ServerConfig.PublicPath`. The
// resolution is filesystem-mediated: the handler does not need a
// declared route list, it just checks whether a corresponding
// `.html` file exists.
//
// Path → file mapping (filesystem-preserving):
//   `/`                  → `{PublicPath}/index.html`
//   `/individual`        → `{PublicPath}/individual.html`
//   `/calc/2009`         → `{PublicPath}/calc/2009.html`
//   `/asset.png`         → handler skips (already has an extension —
//                          standard static-files middleware handles it)
//   `/foo/`              → `{PublicPath}/foo/index.html`
//
// When the resolved file does not exist the handler is a no-op and
// the request falls through to whatever middleware sits after it
// (typically `UseStaticFiles` for asset requests, the SPA shell
// fallback for everything else). Per Phase 57 acceptance: a deployment
// with `PrerenderRoutes = []` does not invoke the FAKE target, the
// `dist/` folder contains no `.html` files beyond `index.html`, and
// every SPA route falls through to the existing `dist/index.html`
// fallback byte-for-byte.
//
// Position in pipeline: ahead of `UseStaticFiles` so the rewrite
// short-circuits before the static-files middleware writes the
// (potentially-different) `index.html` for an SPA-style fallback.
// On Functions / Static Web Apps the static-asset host does the
// equivalent extension-completion; this middleware is the
// Kestrel-default path.

/// Pure helper. Maps a request path to the on-disk path (relative to
/// the configured `PublicPath`) of the corresponding prerendered
/// HTML file. Returns `None` for paths that should not be considered
/// prerender candidates (anything with an existing extension or with
/// path traversal characters).
module PrerenderedRoutesPath =

    let tryResolve (requestPath: string) : string option =
        if String.IsNullOrEmpty requestPath then
            None
        elif requestPath.Contains ".." then
            // Path traversal — never resolve. Static-files middleware
            // performs the same check; we belt-and-braces here so the
            // prerender lookup cannot reach outside PublicPath even if
            // it ran before UseStaticFiles' canonicalisation.
            None
        else
            // Strip leading `/`. The request path always starts with
            // `/` per ASP.NET Core's `PathString` contract.
            let trimmed = requestPath.TrimStart '/'

            let candidate =
                if trimmed = "" then
                    "index.html"
                elif trimmed.EndsWith '/' then
                    trimmed + "index.html"
                else
                    let lastSegment =
                        let idx = trimmed.LastIndexOf '/'

                        if idx < 0 then trimmed else trimmed.Substring(idx + 1)

                    // Skip when the last segment already carries an
                    // extension (e.g. `.png`, `.js`, `.css`, even
                    // `.html` requests routed directly). Those are
                    // standard static-asset requests and should pass
                    // through to UseStaticFiles unchanged.
                    if lastSegment.Contains '.' then "" else trimmed + ".html"

            if candidate = "" then None else Some candidate

type PrerenderedRoutesMiddleware(next: RequestDelegate, config: ServerConfig) =

    let publicPath = Path.GetFullPath config.PublicPath

    member _.InvokeAsync(ctx: HttpContext) = task {
        let shouldAttempt =
            HttpMethods.IsGet ctx.Request.Method || HttpMethods.IsHead ctx.Request.Method

        let mutable served = false

        if shouldAttempt then
            match PrerenderedRoutesPath.tryResolve (ctx.Request.Path.Value) with
            | Some relative ->
                let absolute =
                    Path.Combine(publicPath, relative.Replace('/', Path.DirectorySeparatorChar))

                // Defence-in-depth: confirm the resolved path is
                // physically inside `publicPath`. Belt-and-braces
                // against any traversal slip the string-level check
                // missed.
                let canonical = Path.GetFullPath absolute

                if
                    canonical.StartsWith(publicPath, StringComparison.Ordinal)
                    && File.Exists canonical
                then
                    ctx.Response.ContentType <- "text/html; charset=utf-8"
                    ctx.Response.StatusCode <- 200
                    do! ctx.Response.SendFileAsync canonical
                    served <- true
            | None -> ()

        if not served then
            do! next.Invoke(ctx)
    }