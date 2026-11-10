module ToolUp.Platform.Compose.ConfigurePipeline

open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.FileProviders
open ToolUp.Platform
open ToolUp.Platform.Scope.AnonymousSessionMigration
open ToolUp.Platform.Compose.Middleware
open ToolUp.Platform.Scope.PlatformAdminAuthorization
open ToolUp.Platform.Scope.PeerBearerAuthMiddleware
open ToolUp.Platform.Scope.ShareTokenAuth
open ToolUp.Platform.Scope.SurfaceEnforcement
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Scope
open ToolUp.Platform.Audit

// ─── compose phase: middleware pipeline ──────────────────────────────
//
// The pipeline is a table: `stages` lists every middleware mount in
// pipeline order, each with the `ServerConfig` condition under which it
// mounts (`When`). `configurePipeline` folds the list over the built
// `WebApplication`; `/dev/inspect` renders it with each condition
// evaluated, and the composition manifest seals the names of the stages
// that mount. Ordering between the stages is load-bearing.
//
// `configurePipeline` does not call `app.Run()`. The caller
// (`SDK.Server.compose`) wraps the configured app in an `IServerHost`
// whose `RunBlocking()` method calls `app.Run()` for the Kestrel
// default. Serverless host adapters never call `RunBlocking()`; they
// invoke the configured pipeline per cloud invocation via
// `IServerHost.Invoke`.

/// Build the `ForwardedHeadersOptions` registered when
/// `TrustForwardedHeaders = true`, extracted so tests exercise the
/// trust scope directly. An empty `TrustedProxyCidrs` is the
/// trust-any-peer posture — `KnownIPNetworks` / `KnownProxies` cleared, the
/// listed headers honoured from any peer (a posture
/// `ForwardedHeadersTrustValidator` now gates in auth-requiring
/// modes). A populated allowlist scopes the trust instead:
/// `KnownIPNetworks` is populated from the parsed CIDRs, so
/// `X-Forwarded-*` from an out-of-range peer is ignored by the
/// middleware. A malformed entry fails loud here as the backstop for
/// `SkipPreflight` deployments (the preflight validator surfaces the
/// same message as an `Error` first otherwise).
let buildForwardedHeadersOptions (config: ServerConfig) : Microsoft.AspNetCore.Builder.ForwardedHeadersOptions =
    let opts = Microsoft.AspNetCore.Builder.ForwardedHeadersOptions()

    opts.ForwardedHeaders <-
        Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
        ||| Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto

    // Default known-network limits (127.0.0.1, ::1) are too narrow for
    // typical cloud load balancers — clear them, then re-populate from
    // the operator-declared allowlist when one is present.
    opts.KnownIPNetworks.Clear()
    opts.KnownProxies.Clear()

    match ForwardedHeadersTrustValidator.Cidr.parseAll config.SecurityPosture.TrustedProxyCidrs with
    | Ok [] -> () // trust-any-peer posture (validator-gated in auth-requiring modes)
    | Ok networks ->
        for network in networks do
            opts.KnownIPNetworks.Add network
    | Error bad -> failwith (ForwardedHeadersTrustValidator.Cidr.malformedMessage bad)

    opts

/// What a stage's mount reads: the built app and the composed values the
/// pipeline references.
type StageInputs = {
    App: WebApplication
    Config: ServerConfig
    Logger: ILogger
    MetricsSink: Metrics.IMetricsSink
    Extensions: ComposeExtensions
    Router: HttpHandler list -> HttpHandler
    DevRoutes: HttpHandler list
}

/// One middleware mount of the pipeline: its name, the configuration under
/// which it mounts, and the mount itself. A stage whose `When` is false is
/// not mounted and costs nothing (GP 13).
type Stage = {
    Name: string
    When: ServerConfig -> bool
    Mount: StageInputs -> unit
}

let private always (_: ServerConfig) = true

let private stage name condition mount : Stage = {
    Name = name
    When = condition
    Mount = mount
}

/// The pipeline, in order.
let stages: Stage list = [
    // Forwarded-headers and HTTPS redirection must run before scope
    // resolution: secure-cookie scoping, OIDC `RedirectUri` generation,
    // and any handler that calls `Url.IsAbsoluteUri` need
    // `Request.Scheme` to reflect the originating proxy hop, not the
    // proxy-to-origin scheme.
    stage "ForwardedHeaders" (fun config -> config.SecurityPosture.TrustForwardedHeaders) (fun i ->
        i.App.UseForwardedHeaders(buildForwardedHeadersOptions i.Config) |> ignore

        // Surface the trust posture explicitly at startup (not per
        // request, so the noise budget is bounded). Trust-any-peer
        // (empty allowlist) warns; a scoped allowlist logs the declared
        // networks at Info.
        if List.isEmpty i.Config.SecurityPosture.TrustedProxyCidrs then
            i.Logger.Warn
                "ServerConfig.SecurityPosture.TrustForwardedHeaders = true — `X-Forwarded-Proto` / `X-Forwarded-For` honoured from any peer (KnownIPNetworks + KnownProxies cleared; TrustedProxyCidrs is empty). Behind a single TLS-terminating ingress that strips client-supplied X-Forwarded-* headers this is correct. To scope the trust to your proxy's network(s), set `TOOLUP_TRUSTED_PROXY_CIDRS` (comma-separated CIDRs). To opt out entirely, set `TOOLUP_TRUST_FORWARDED_HEADERS=0`."
        else
            i.Logger.Info(
                "ServerConfig.SecurityPosture.TrustForwardedHeaders = true — `X-Forwarded-Proto` / `X-Forwarded-For` honoured only from TrustedProxyCidrs: "
                + String.concat ", " i.Config.SecurityPosture.TrustedProxyCidrs
                + "."
            ))

    // Exception-handler middleware. Without this, ASP.NET Core's
    // behaviour depends on the host's `EnvironmentName`: in
    // `Production` an unhandled exception returns an empty 500 (safe);
    // in `Development` (the default when `ASPNETCORE_ENVIRONMENT` is
    // unset on some hosts) the dev exception page renders stack traces
    // in the response body. The SDK can't trust `EnvironmentName` to be
    // correctly set for every deployment, so it installs a deliberate
    // exception handler that always returns a minimal JSON body
    // regardless of environment. The first unconditional stage, so
    // exceptions thrown by ANY downstream middleware are caught.
    stage "ExceptionHandler" always (fun i ->
        i.App.UseExceptionHandler(fun errApp ->
            errApp.Run(fun ctx ->
                ctx.Response.StatusCode <- 500
                ctx.Response.ContentType <- "application/json"
                // Minimal body — no exception detail in the response.
                // Operators correlate via logs / audit trail / request
                // id; the response side stays opaque.
                ctx.Response.WriteAsync("""{"error":"internal_server_error"}""")))
        |> ignore)

    stage "HttpsRedirection" (fun config -> config.SecurityPosture.RequireHttps) (fun i ->
        i.App.UseHttpsRedirection() |> ignore)

    // Request-id middleware. Generates a Guid-N per request (or accepts
    // a validated client-supplied X-Request-Id), stamps the response
    // header, stores the id on HttpContext.Items["ToolUp.RequestId"]
    // for downstream observability surfaces (RequestTimingMiddleware,
    // audit emissions, ConsoleLogger), and stamps the request's
    // `RouteClass`, which every later middleware reads. Runs before
    // ScopeResolution so both are available throughout the pipeline.
    stage "RequestId" always (fun i -> i.App.UseMiddleware<RequestIdMiddleware>() |> ignore)

    // Security headers stamp on every response, including 401 / 429 /
    // 404 short-circuits. Registered before any middleware that might
    // end the response so the `OnStarting` hook is in place for every
    // code path. The middleware stamps an always-on baseline floor
    // (X-Frame-Options / nosniff / Referrer-Policy, plus HSTS on HTTPS)
    // even when `SecurityHeaders = Map.empty`, with the consumer map
    // layered on top; per-route handler headers still win.
    stage "SecurityHeaders" always (fun i -> i.App.UseMiddleware<SecurityHeadersMiddleware>(i.Config) |> ignore)

    // Aggregated CSP header. Sits immediately after the static
    // `SecurityHeadersMiddleware` so a CSP from the `SecurityHeaders` map
    // (or a per-route handler) still wins (`CspMiddleware` only sets the
    // header when absent). Resolves the `ResolvedCspPolicy` singleton from
    // DI; no-ops when its header is empty (`NoSecurityHardening`), so
    // mounting it unconditionally is zero-cost.
    stage "Csp" always (fun i -> i.App.UseMiddleware<CspMiddleware>() |> ignore)

    // CORS middleware uses the default policy registered in
    // `services.AddCors`. Not mounted when
    // `config.SecurityPosture.Cors = None`.
    stage "Cors" (fun config -> config.SecurityPosture.Cors.IsSome) (fun i -> i.App.UseCors() |> ignore)

    stage "ResponseCompression" always (fun i -> i.App.UseResponseCompression() |> ignore)

    // Prerendered-routes middleware sits immediately ahead of
    // `UseStaticFiles` so an inbound GET `/some/route` resolves to
    // `{PublicPath}/some-route.html` (when one exists, written by the FAKE
    // `Prerender` target) and short-circuits before the static-files
    // middleware serves the SPA `index.html` shell. When no prerendered
    // file exists, the middleware falls through unchanged. Mounted
    // unconditionally because the lookup is filesystem-mediated and the
    // handler itself is a no-op when no `.html` files have been
    // prerendered.
    stage "PrerenderedRoutes" always (fun i -> i.App.UseMiddleware<PrerenderedRoutesMiddleware>(i.Config) |> ignore)

    // Serve files from `config.PublicPath` via a PhysicalFileProvider
    // rooted at that (absolute) path, then the SPA shell fallback.
    // Behaviour when the directory is missing is governed by
    // `config.StaticPathBehaviour`:
    //  - `Warn` (default, backward-compatible): log a warning and
    //    continue. Vite serves assets in dev so this is normal.
    //  - `RequireExist`: throw at startup. Production should pick this
    //    so a missing artefact crashes loudly.
    //  - `SkipSilent`: continue with no log. For pure API deployments.
    // The stage is always listed: whether the directory exists is a fact
    // about the host, not the configuration, so it is decided at mount.
    stage "StaticFiles" always (fun i ->
        let publicPath = System.IO.Path.GetFullPath(i.Config.PublicPath)

        if System.IO.Directory.Exists(publicPath) then
            i.App.UseStaticFiles(StaticFileOptions(FileProvider = new PhysicalFileProvider(publicPath)))
            |> ignore

            // SPA shell fallback. Runs immediately after `UseStaticFiles`
            // so real static assets (`/main.js`, `/logo.svg`, …)
            // short-circuit upstream; anything left that is a non-API,
            // no-extension GET resolves to `{PublicPath}/index.html` and
            // lets the SPA's client-side router handle the rest. Critical
            // for OIDC callbacks (`/auth/callback?code=…`) and SPA deep
            // links that would otherwise 404 before the SPA can load.
            i.App.UseMiddleware<SpaFallbackMiddleware>(i.Config) |> ignore
        else
            match i.Config.StaticPathBehaviour with
            | Warn ->
                i.Logger.Warn(
                    $"Static file path not found: {publicPath}. Skipping UseStaticFiles (dev mode — Vite serves assets)."
                )
            | RequireExist ->
                failwith
                    $"Static file path not found: {publicPath}. ServerConfig.StaticPathBehaviour = RequireExist demands the directory exist at startup."
            | SkipSilent -> ())

    stage "Session" always (fun i -> i.App.UseSession() |> ignore)

    // CSRF / cross-origin POST guard. Right after `UseSession` (it binds
    // the synchroniser token to the ASP.NET session) and ahead of the
    // pre-middleware seam so a forged state-changing request is rejected
    // before scope resolution / auth / handler work. No-ops on
    // `NoSecurityHardening` and exempts the token endpoint, peer-bearer
    // routes, and anonymous share-token prefixes.
    stage "Csrf" always (fun i -> i.App.UseMiddleware<CsrfMiddleware>(i.Config) |> ignore)

    // Pre-middleware seam. Runs after CORS / security headers / static
    // files, but BEFORE scope resolution and auth enforcement. Use for
    // IP allowlists, custom auth-precondition rejection, request-shape
    // sanitisation — anything that should short-circuit before the SDK
    // resolves the caller's scope. Thunks apply in registration order;
    // each returns the (potentially modified) builder.
    stage "PreMiddleware" always (fun i ->
        let mutable appBuilder: IApplicationBuilder = i.App :> IApplicationBuilder

        for thunk in i.Extensions.PreMiddleware do
            appBuilder <- thunk appBuilder)

    // Share-token authentication. Reads the inbound `?token=` query
    // parameter or `X-Share-Token` header, validates against
    // `IShareTokenStore`, and stashes the resolved `ShareTokenClaim` on
    // `HttpContext.Items` for `ScopeResolutionMiddleware` to read when
    // building the `SubjectResolutionRequest`. Pass-through when no token
    // is present. Ahead of `ScopeResolutionMiddleware` so the claim is in
    // `Items` before `ISubjectResolver` runs.
    stage "ShareTokenAuth" always (fun i -> i.App.UseMiddleware<ShareTokenAuthMiddleware>() |> ignore)

    // Service-account bearer tokens. Mounted ONLY when the deployment
    // opted in, so an unconfigured deployment's pipeline is unchanged
    // (GP 11 / GP 13) and nothing new inspects its `Authorization` header.
    //
    // Immediately after `ShareTokenAuthMiddleware` and before
    // `ScopeResolutionMiddleware` because it writes that middleware's
    // `ShareTokenClaimItemsKey` — a validated service-account token
    // resolves to `ClaimBearer`, so the claim must be in `Items` before
    // `ISubjectResolver` runs. The two never contend: a share token
    // arrives on `?token=` / `X-Share-Token`, a service-account token on
    // `Authorization: Bearer tusa_…`, and each middleware passes through
    // untouched when its own credential is absent.
    stage
        "ServiceAccountToken"
        (fun config ->
            match config.Stores.ServiceAccounts with
            | NoServiceAccounts -> false
            | EnabledServiceAccounts
            | CustomServiceAccountStore -> true)
        (fun i ->
            i.App.UseMiddleware<ServiceAccountTokenHandler.ServiceAccountTokenMiddleware>()
            |> ignore)

    stage "ScopeResolution" always (fun i -> i.App.UseMiddleware<ScopeResolutionMiddleware>(i.Config) |> ignore)

    // Admin structural backstop. Immediately after
    // `ScopeResolutionMiddleware` (which stamps the principal's
    // `PlatformRole`) and ahead of the router, so a non-`PlatformAdmin`
    // request to the raw `_platform` admin handlers is denied even if a
    // handler omits its in-line `canModifyPlatformConfig` check. No-ops
    // for non-admin paths, so mounting it unconditionally is cheap.
    stage "PlatformAdminAuthorization" always (fun i ->
        i.App.UseMiddleware<PlatformAdminAuthorizationMiddleware>() |> ignore)

    // Peer-bearer-auth middleware. Not mounted when `PeerRoutePrefixes` is
    // empty, so deployments that never accept peer calls pay zero runtime
    // cost. Ahead of `SurfaceEnforcementMiddleware` so peer-routed
    // requests get bearer-checked and stamped with
    // `HttpContext.Items["PeerName"]` before user-auth enforcement runs
    // (and exempts peer routes — the bearer IS the authentication).
    stage "PeerBearerAuth" (fun config -> not (List.isEmpty config.MultiInstance.PeerRoutePrefixes)) (fun i ->
        i.App.UseMiddleware<PeerBearerAuthMiddleware>(i.Config) |> ignore

        // One line per start when a registered static-bearer prefix
        // covers the `/peer/v1/` namespace the signed-JWT peer host
        // serves, so the weaker of the two peer-auth substrates gating
        // federation traffic is a visible choice rather than an
        // inference from which config field was set. Advisory only.
        advisePeerAuthPosture i.Logger i.Config)

    // Surface-requirement enforcement. Reads the resolved `Subject`
    // stashed by `ScopeResolutionMiddleware` and the per-route
    // `SurfaceRequirement` from the composition-time registry, then
    // applies the 7-row response-code matrix (design §3.1).
    stage "SurfaceEnforcement" always (fun i -> i.App.UseMiddleware<SurfaceEnforcementMiddleware>() |> ignore)

    // Opt-in module-visibility route hardening. Mounted ONLY under
    // `EnforcedModuleVisibility`, so the default and the surfacing-only
    // mode pay not even a delegate hop (GP 13).
    //
    // Immediately AFTER `SurfaceEnforcementMiddleware`: only a request
    // that already cleared the authentication/authorisation gate reaches a
    // curation decision, which keeps the response codes honest — an
    // anonymous caller gets 401 from the gate above rather than a 404 that
    // would imply the route does not exist for anyone. It must be after
    // `ScopeResolutionMiddleware` besides, since the scoped `AccessContext`
    // it resolves is built from that middleware's stashed items.
    stage
        "ModuleVisibility"
        (fun config ->
            match config.ModuleVisibility with
            | NoModuleVisibility
            | SurfacingModuleVisibility -> false
            | EnforcedModuleVisibility -> true)
        (fun i ->
            i.App.UseMiddleware<ModuleVisibilityRoutes.ModuleVisibilityRouteMiddleware>()
            |> ignore)

    // Revoked-session refusal. Mounted ONLY when
    // `ServerConfig.Stores.SessionRegistry` selects a backend, so the
    // default deployment pays not even a delegate hop (GP 13).
    //
    // After `SurfaceEnforcementMiddleware` for the reason the stage above
    // is: only a request that already cleared authentication reaches a
    // revocation decision — an unauthenticated caller is told they are not
    // authenticated, not that a session they do not have was revoked. It
    // must be after `ScopeResolutionMiddleware` besides, since the
    // `Subject` and `StorageScope` it reads are that middleware's stamps.
    stage
        "SessionRevocation"
        (fun config ->
            match config.Stores.SessionRegistry with
            | NoSessionRegistry -> false
            | BlobSessionRegistry _
            | CustomSessionRegistry _ -> true)
        (fun i ->
            i.App.UseMiddleware<SessionRevocation.SessionRevocationMiddleware>(i.Config)
            |> ignore)

    // Anonymous→authenticated session-migration trigger. AFTER
    // SurfaceEnforcementMiddleware so only requests that pass the
    // per-route surface gate can fire a migration (an anonymous caller
    // 401'd above never triggers one). Invokes the registered
    // `IAnonymousSessionMigrator` once per (user, session-id) pair, then
    // always continues the pipeline. No-op when the default
    // NoOpAnonymousSessionMigrator is wired.
    stage "AnonymousSessionMigration" always (fun i ->
        i.App.UseMiddleware<AnonymousSessionMigrationMiddleware>() |> ignore)

    // MetricsMiddleware — before RequestTimingMiddleware so it observes
    // the same downstream span. The two middlewares serve different
    // audiences (operator dashboards vs. log lines) and stay
    // independently togglable. Not mounted when MetricsEndpoint =
    // FeatureMode.Disabled, to avoid even the no-op call overhead on every
    // request.
    stage "Metrics" (fun config -> config.Observability.MetricsEndpoint = FeatureMode.Enabled) (fun i ->
        i.App.UseMiddleware<Metrics.MetricsMiddleware>(i.MetricsSink) |> ignore)

    // Request timing. Between auth enforcement (so unauthenticated
    // rejections are not timed) and the router (so the timer covers the
    // handler invocation, which is what an operator cares about).
    stage "RequestTiming" always (fun i -> i.App.UseMiddleware<RequestTimingMiddleware>(i.Config, i.Logger) |> ignore)

    // Inbound HTTP rate-limit middleware. Mounts when `RateLimits` is
    // non-empty AND an `IRateLimitStore` is registered. After auth
    // (identity resolved for `ByUserId` keys) and before the fixed-window
    // limiter so route-specific policies short-circuit first.
    stage
        "InboundRateLimit"
        (fun config ->
            not (List.isEmpty config.RateLimits)
            && config.Stores.RateLimitStore <> NoRateLimitStore)
        (fun i ->
            i.App.UseMiddleware<RateLimitMiddleware.InboundRateLimitMiddleware>(i.Config)
            |> ignore)

    // Per-subject-kind rate limit. After scope resolution (so the
    // `Subject` is resolved and the partition key is stable across a
    // session) and before the router (so a 429 short-circuits before any
    // body work). Not mounted when `ServerConfig.RateLimit` registers no
    // limiter (`RateLimitConfig.none`).
    stage "RateLimiter" (fun config -> RateLimitConfig.isEnabled config.RateLimit) (fun i ->
        i.App.UseRateLimiter() |> ignore)

    // Health endpoints. Mapped before the router so the ASP.NET Core
    // endpoint routing matches and short-circuits before the Giraffe
    // terminal middleware sees the request. `/health` is a liveness
    // probe — predicate selects only probes tagged `"Liveness"` (none
    // today, so the response is `200 OK` whenever the process is
    // alive). `/ready` runs every registered probe (both Liveness and
    // Readiness) and returns the worst-status aggregate. Both endpoints
    // use the JSON response writer so 503 responses include a body
    // listing the failing check.
    stage "HealthChecks" always (fun i ->
        let livenessOptions =
            Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions(
                Predicate = (fun reg -> reg.Tags.Contains "Liveness"),
                ResponseWriter = (fun ctx report -> HealthCheckResponseWriter.writeResponse ctx report)
            )

        let readyOptions =
            Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions(
                ResponseWriter = (fun ctx report -> HealthCheckResponseWriter.writeResponse ctx report)
            )

        i.App.MapHealthChecks("/health", livenessOptions) |> ignore
        i.App.MapHealthChecks("/ready", readyOptions) |> ignore)

    // The Giraffe router.
    stage "Router" always (fun i -> i.App.UseGiraffe(i.Router i.DevRoutes))

    // Post-middleware seam. Runs AFTER the router, so consumers can
    // register fallback handlers (custom 404 / catch-all rewrites /
    // debug-only routes) that only fire when no Giraffe handler matched.
    // Thunks apply in registration order.
    stage "PostMiddleware" always (fun i ->
        let mutable postBuilder: IApplicationBuilder = i.App :> IApplicationBuilder

        for thunk in i.Extensions.PostMiddleware do
            postBuilder <- thunk postBuilder)
]

/// The stages that mount under `config`, in pipeline order.
let mounted (config: ServerConfig) : Stage list =
    stages |> List.filter (fun s -> s.When config)

/// Mount the pipeline: every stage whose `When` holds for `config`, in order.
let configurePipeline
    (app: WebApplication)
    (config: ServerConfig)
    (resolvedLogger: ILogger)
    (resolvedMetricsSink: Metrics.IMetricsSink)
    (extensions: ComposeExtensions)
    (router: HttpHandler list -> HttpHandler)
    (devRoutes: HttpHandler list)
    : unit =
    let inputs = {
        App = app
        Config = config
        Logger = resolvedLogger
        MetricsSink = resolvedMetricsSink
        Extensions = extensions
        Router = router
        DevRoutes = devRoutes
    }

    for s in mounted config do
        s.Mount inputs