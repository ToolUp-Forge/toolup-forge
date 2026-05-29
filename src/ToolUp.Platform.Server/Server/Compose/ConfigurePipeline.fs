module ToolUp.Platform.ConfigurePipeline

open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open ToolUp.Platform
open ToolUp.Platform.Middleware
open ToolUp.Platform.PeerBearerAuthMiddleware
open ToolUp.Platform.ShareTokenAuth
open ToolUp.Platform.SurfaceEnforcement

// ─── compose phase: middleware pipeline ──────────────────────────────
//
// Extracted from the tail of `compose` (previously Saturn's
// `app_config`). Pure side effects on the built `WebApplication`; takes
// exactly the locals the pipeline references. Ordering between the
// `app.Use*` calls is load-bearing — the body preserves it exactly.
//
// Phase 16 — this function NO LONGER calls `app.Run()`. The caller
// (`SDK.Server.compose`) wraps the configured app in an `IServerHost`
// whose `RunBlocking()` method calls `app.Run()` for the Kestrel
// default. Serverless host adapters never call `RunBlocking()`; they
// invoke the configured pipeline per cloud invocation via
// `IServerHost.Invoke`.
let configurePipeline
    (app: WebApplication)
    (config: ServerConfig)
    (resolvedLogger: ILogger)
    (resolvedMetricsSink: Metrics.IMetricsSink)
    (extensions: ComposeExtensions)
    (router: HttpHandler list -> HttpHandler)
    (devRoutes: HttpHandler list)
    : unit =

    // Forwarded-headers and HTTPS redirection must run before scope
    // resolution: secure-cookie scoping, OIDC `RedirectUri` generation,
    // and any handler that calls `Url.IsAbsoluteUri` need
    // `Request.Scheme` to reflect the originating proxy hop, not the
    // proxy-to-origin scheme.
    if config.TrustForwardedHeaders then
        let opts = Microsoft.AspNetCore.Builder.ForwardedHeadersOptions()

        opts.ForwardedHeaders <-
            Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
            ||| Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto

        // Default known-network limits (127.0.0.1, ::1) are too narrow
        // for typical cloud load balancers — clear them so the listed
        // headers are honoured from any peer. Deployments with strict
        // network controls can override by registering a custom
        // `ForwardedHeadersOptions` post-`compose`.
        opts.KnownIPNetworks.Clear()
        opts.KnownProxies.Clear()
        app.UseForwardedHeaders(opts) |> ignore

    // Exception-handler middleware. Without this, ASP.NET Core's
    // behaviour depends on the host's `EnvironmentName`: in
    // `Production` an unhandled exception returns an empty 500 (safe);
    // in `Development` (the default when `ASPNETCORE_ENVIRONMENT` is
    // unset on some hosts) the dev exception page renders stack traces
    // in the response body. The SDK can't trust `EnvironmentName` to be
    // correctly set for every deployment, so we install a deliberate
    // exception handler that always returns a minimal JSON body
    // regardless of environment. Registered as the very first
    // middleware so exceptions thrown by ANY downstream middleware are
    // caught.
    app.UseExceptionHandler(fun errApp ->
        errApp.Run(fun ctx ->
            ctx.Response.StatusCode <- 500
            ctx.Response.ContentType <- "application/json"
            // Minimal body — no exception detail in the response.
            // Operators correlate via logs / audit trail / request id;
            // the response side stays opaque.
            ctx.Response.WriteAsync("""{"error":"internal_server_error"}""")))
    |> ignore

    if config.RequireHttps then
        app.UseHttpsRedirection() |> ignore

    // Request-id middleware. Generates a Guid-N per request (or accepts
    // a validated client-supplied X-Request-Id), stamps the response
    // header, and stores the id on HttpContext.Items["ToolUp.RequestId"]
    // for downstream observability surfaces (RequestTimingMiddleware,
    // audit emissions, ConsoleLogger). Runs before ScopeResolution so
    // the request id is available throughout the pipeline.
    app.UseMiddleware<RequestIdMiddleware>() |> ignore

    // Security headers stamp on every response, including 401 / 429 /
    // 404 short-circuits. Registered before any middleware that might
    // end the response so the `OnStarting` hook is in place for every
    // code path. The middleware itself no-ops when `SecurityHeaders` is
    // empty (the default), so registering unconditionally is zero-cost.
    app.UseMiddleware<SecurityHeadersMiddleware>(config) |> ignore

    // Phase 9j — aggregated CSP header. Sits immediately after the
    // static `SecurityHeadersMiddleware` so a CSP from the
    // `SecurityHeaders` map (or a per-route handler) still wins
    // (`CspMiddleware` only sets the header when absent). Resolves the
    // `ResolvedCspPolicy` singleton from DI; no-ops when its header is
    // empty (`NoSecurityHardening`), so registering unconditionally is
    // zero-cost — same contract as `SecurityHeadersMiddleware`.
    app.UseMiddleware<CspMiddleware>() |> ignore

    // CORS middleware uses the default policy registered in
    // `services.AddCors` above. Skipped entirely when
    // `config.Cors = None`.
    if config.Cors.IsSome then
        app.UseCors() |> ignore

    app.UseResponseCompression() |> ignore // `use_gzip`

    // Phase 57 follow-up — prerendered-routes middleware sits
    // immediately ahead of `UseStaticFiles` so an inbound GET
    // `/some/route` resolves to `{PublicPath}/some-route.html`
    // (when one exists, written by the FAKE `Prerender` target)
    // and short-circuits before the static-files middleware
    // serves the SPA `index.html` shell. When no prerendered file
    // exists, the middleware falls through unchanged — stock SPA
    // behaviour is byte-for-byte preserved per Phase 57 acceptance.
    // Registered unconditionally because the lookup is filesystem-
    // mediated (no declared route list needed) and the handler
    // itself is a no-op when no `.html` files have been prerendered
    // (the `PrerenderRoutes = []` default).
    app.UseMiddleware<PrerenderedRoutesMiddleware>(config) |> ignore

    // `use_static` — serve files from `config.PublicPath` via a
    // PhysicalFileProvider rooted at that (absolute) path. Behaviour
    // when the directory is missing is governed by
    // `config.StaticPathBehaviour`:
    //  - `Warn` (default, backward-compatible): log a warning and
    //    continue. Vite serves assets in dev so this is normal.
    //  - `RequireExist`: throw at startup. Production should pick this
    //    so a missing artefact crashes loudly.
    //  - `SkipSilent`: continue with no log. For pure API deployments.
    let publicPath = System.IO.Path.GetFullPath(config.PublicPath)

    if System.IO.Directory.Exists(publicPath) then
        app.UseStaticFiles(StaticFileOptions(FileProvider = new PhysicalFileProvider(publicPath)))
        |> ignore
    else
        match config.StaticPathBehaviour with
        | Warn ->
            resolvedLogger.Warn(
                $"Static file path not found: {publicPath}. Skipping UseStaticFiles (dev mode — Vite serves assets)."
            )
        | RequireExist ->
            failwith
                $"Static file path not found: {publicPath}. ServerConfig.StaticPathBehaviour = RequireExist demands the directory exist at startup."
        | SkipSilent -> ()

    app.UseSession() |> ignore

    // Phase 9j — CSRF / cross-origin POST guard. Registered right
    // after `UseSession` (it binds the synchroniser token to the
    // ASP.NET session) and ahead of the pre-middleware seam so a
    // forged state-changing request is rejected before scope
    // resolution / auth / handler work. No-ops on `NoSecurityHardening`
    // and exempts the token endpoint, peer-bearer routes, and
    // anonymous share-token prefixes.
    app.UseMiddleware<CsrfMiddleware>(config) |> ignore

    // Pre-middleware seam. Runs after CORS / security headers / static
    // files, but BEFORE scope resolution and auth enforcement. Use for
    // IP allowlists, custom auth-precondition rejection, request-shape
    // sanitisation — anything that should short-circuit before the SDK
    // resolves the caller's scope. Thunks apply in registration order;
    // each returns the (potentially modified) builder.
    let mutable appBuilder: IApplicationBuilder = app :> IApplicationBuilder

    for thunk in extensions.PreMiddleware do
        appBuilder <- thunk appBuilder

    // Phase 66 Stream A.6 — share-token authentication. Reads the
    // inbound `?token=` query parameter or `X-Share-Token` header,
    // validates against `IShareTokenStore`, and stashes the resolved
    // `ShareTokenClaim` on `HttpContext.Items` for
    // `ScopeResolutionMiddleware` to read when building the
    // `SubjectResolutionRequest`. Pass-through when no token is
    // present so the overwhelming-majority "no token" path pays only
    // a header / query lookup. Registered ahead of
    // `ScopeResolutionMiddleware` so the claim is in `Items` before
    // `ISubjectResolver` runs.
    app.UseMiddleware<ShareTokenAuthMiddleware>() |> ignore

    app.UseMiddleware<ScopeResolutionMiddleware>(config) |> ignore

    // Peer-bearer-auth middleware. Strip-imports clean: when
    // `PeerRoutePrefixes` is empty the middleware is not registered at
    // all, so deployments that never accept peer calls pay zero runtime
    // cost. Sits ahead of `SurfaceEnforcementMiddleware` so peer-routed
    // requests get bearer-checked and stamped with
    // `HttpContext.Items["PeerName"]` before user-auth enforcement runs
    // (and exempts peer routes — the bearer IS the authentication).
    if not (List.isEmpty config.PeerRoutePrefixes) then
        app.UseMiddleware<PeerBearerAuthMiddleware>(config) |> ignore

    // Phase 66 Stream A.6 — surface-requirement enforcement. Replaces
    // the retired `AuthEnforcementMiddleware`. Reads the resolved
    // `Subject` stashed by `ScopeResolutionMiddleware` and the per-
    // route `SurfaceRequirement` from the composition-time registry,
    // then applies the 7-row response-code matrix (design §3.1).
    app.UseMiddleware<SurfaceEnforcementMiddleware>() |> ignore
    // MetricsMiddleware — sits before RequestTimingMiddleware so it
    // observes the same downstream span. The two middlewares serve
    // different audiences (operator dashboards vs. log lines) and stay
    // independently togglable: NoMetricsEndpoint resolves IMetricsSink
    // to NoOpMetricsSink so this middleware is a no-op try/finally;
    // EnabledMetricsEndpoint records the standard request-level counter
    // / latency / error metrics. Skipped entirely when MetricsEndpoint =
    // NoMetricsEndpoint to avoid even the no-op call overhead on every
    // request.
    if config.MetricsEndpoint = EnabledMetricsEndpoint then
        app.UseMiddleware<Metrics.MetricsMiddleware>(resolvedMetricsSink) |> ignore
    // Request timing. Sits between auth enforcement (so we don't time
    // unauthenticated rejections) and the remoting body normaliser (so
    // the timer covers the handler invocation, which is what an
    // operator cares about).
    app.UseMiddleware<RequestTimingMiddleware>(config, resolvedLogger) |> ignore
    // Phase 56 — inbound HTTP rate-limit middleware. Mounts when
    // `RateLimits` is non-empty AND an `IRateLimitStore` is registered.
    // Sits after auth (identity resolved for `ByUserId` keys) and
    // before the Phase 9 fixed-window limiter so route-specific
    // policies short-circuit first. Skipped silently when no policies
    // are declared.
    if
        not (List.isEmpty config.RateLimits)
        && config.RateLimitStore <> NoRateLimitStore
    then
        app.UseMiddleware<RateLimitMiddleware.InboundRateLimitMiddleware>(config)
        |> ignore

    // Per-subject-kind rate limit. Sits after scope resolution (so the
    // `Subject` is resolved and the partition key is stable across a
    // session) and before the remoting body normaliser (so a 429
    // short-circuits before any body work). Skipped when
    // `ServerConfig.RateLimit` registers no limiter (`RateLimitConfig.none`).
    if RateLimitConfig.isEnabled config.RateLimit then
        app.UseRateLimiter() |> ignore

    app.UseMiddleware<RemotingBodyNormalizationMiddleware>() |> ignore

    // Health endpoints. Mapped before `UseGiraffe` so the ASP.NET Core
    // endpoint routing matches and short-circuits before the Giraffe
    // terminal middleware sees the request. `/health` is a liveness
    // probe — predicate selects only probes tagged `"Liveness"` (none
    // today, so the response is `200 OK` whenever the process is
    // alive). `/ready` runs every registered probe (both Liveness and
    // Readiness) and returns the worst-status aggregate. Both endpoints
    // use the JSON response writer so 503 responses include a body
    // listing the failing check.
    let livenessOptions =
        Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions(
            Predicate = (fun reg -> reg.Tags.Contains "Liveness"),
            ResponseWriter = (fun ctx report -> HealthCheckResponseWriter.writeResponse ctx report)
        )

    let readyOptions =
        Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions(
            ResponseWriter = (fun ctx report -> HealthCheckResponseWriter.writeResponse ctx report)
        )

    app.MapHealthChecks("/health", livenessOptions) |> ignore
    app.MapHealthChecks("/ready", readyOptions) |> ignore

    // Mount the Giraffe router (previously Saturn's `use_router`).
    app.UseGiraffe(router devRoutes)

    // Post-middleware seam. Runs AFTER `UseGiraffe`, so consumers can
    // register fallback handlers (custom 404 / catch-all rewrites /
    // debug-only routes) that only fire when no Giraffe handler
    // matched. Thunks apply in registration order.
    let mutable postBuilder: IApplicationBuilder = app :> IApplicationBuilder

    for thunk in extensions.PostMiddleware do
        postBuilder <- thunk postBuilder
// `app.Run()` is invoked by the caller via `IServerHost.RunBlocking()`
// (Phase 16). For the Kestrel default, `ServerApp.run` chains it as the
// final step. Serverless host adapters never invoke `RunBlocking` —
// they drive the configured pipeline per cloud invocation via
// `IServerHost.Invoke`. The middleware registration above is otherwise
// identical to the prior inline composition.