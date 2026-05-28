namespace ToolUp.Platform

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Security headers middleware (Phase 1f) ──────────────────────────
//
// Stamps the deployment-configured response headers (CSP, HSTS,
// X-Frame-Options, Referrer-Policy, Permissions-Policy, …) onto every
// response. Reads `ServerConfig.SecurityHeaders` at construction; an
// empty map keeps the registration cheap (the OnStarting hook still
// fires per request but does no work) so adding the middleware
// unconditionally is fine.
//
// **Per-route overrides.** A handler that already wrote a same-name
// header before `OnStarting` runs wins — the middleware only sets
// headers not already present. Lets handlers tighten CSP for an
// admin route, override `X-Frame-Options` for an embed iframe, or
// drop a header entirely on a specific endpoint without touching the
// middleware config.
//
// **Position.** Registered ahead of every other middleware in
// `compose` so the `OnStarting` hook is wired before any short-circuit
// (auth rejection, rate limit, scope resolution failure) gets a chance
// to write the response. Headers therefore stamp on every status code,
// including `429` / `401` / `404`.

type SecurityHeadersMiddleware(next: RequestDelegate, config: ServerConfig) =
    member _.InvokeAsync(ctx: HttpContext) = task {
        if not config.SecurityHeaders.IsEmpty then
            ctx.Response.OnStarting(fun () ->
                for KeyValue(k, v) in config.SecurityHeaders do
                    if not (ctx.Response.Headers.ContainsKey k) then
                        ctx.Response.Headers[k] <- Microsoft.Extensions.Primitives.StringValues v

                System.Threading.Tasks.Task.CompletedTask)

        do! next.Invoke(ctx)
    }