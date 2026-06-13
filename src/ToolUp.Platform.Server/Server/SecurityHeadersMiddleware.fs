namespace ToolUp.Platform

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── Security headers middleware (Phase 1f / 129d) ───────────────────
//
// Stamps response headers (CSP, HSTS, X-Frame-Options, Referrer-Policy,
// Permissions-Policy, …) onto every response. The effective set is the
// Phase 129d always-on `SecurityHeaders.baseline` floor (X-Frame-Options
// DENY / nosniff / Referrer-Policy, plus HSTS when `RequireHttps`) with
// the deployment's `ServerConfig.SecurityHeaders` map layered on top.
// Resolved once at construction via `SecurityHeaders.effective`.
//
// **Floor vs map vs handler.** Three priority tiers, lowest to highest:
//   1. the 129d baseline floor (always present),
//   2. the consumer's `SecurityHeaders` map (wins over the floor — merged
//      in `SecurityHeaders.effective`),
//   3. a per-route handler that already wrote the header before
//      `OnStarting` runs (wins over both — the middleware only sets a key
//      not already present on the response).
// So a stock deployment is non-framable by default (GP 9), a consumer
// that set its own value is byte-for-byte unchanged (GP 11), and a
// handler can still tighten CSP for an admin route, allow framing for an
// embed iframe, or drop a header on a specific endpoint.
//
// **Position.** Registered ahead of every other middleware in
// `compose` so the `OnStarting` hook is wired before any short-circuit
// (auth rejection, rate limit, scope resolution failure) gets a chance
// to write the response. Headers therefore stamp on every status code,
// including `429` / `401` / `404`.

type SecurityHeadersMiddleware(next: RequestDelegate, config: ServerConfig) =
    // Phase 129d — baseline floor (+ HSTS on HTTPS) merged under the
    // consumer's map, resolved once. Never empty, so the OnStarting hook
    // always stamps the floor on a stock deployment.
    let effectiveHeaders =
        SecurityHeaders.effective config.RequireHttps config.SecurityHeaders

    member _.InvokeAsync(ctx: HttpContext) = task {
        ctx.Response.OnStarting(fun () ->
            for KeyValue(k, v) in effectiveHeaders do
                if not (ctx.Response.Headers.ContainsKey k) then
                    ctx.Response.Headers[k] <- Microsoft.Extensions.Primitives.StringValues v

            System.Threading.Tasks.Task.CompletedTask)

        do! next.Invoke(ctx)
    }