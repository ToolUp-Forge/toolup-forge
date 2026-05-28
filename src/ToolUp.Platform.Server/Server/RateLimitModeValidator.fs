module ToolUp.Platform.RateLimitModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.G — rate-limit absent on internet-facing auth-mode ────
//
// `ServerConfig.RateLimit = None` is the default, which is correct for
// local dev and behind-proxy deployments where the proxy enforces its
// own per-client limits. Internet-facing authenticated deployments
// without a rate-limit are a trivial-cost DoS surface: one curl loop
// against `/api/<module>/<expensive>` saturates CPU, fills the upload
// quota, or runs LLM tokens dry depending on what the module does.
//
// Phase 6l ships this as a `Warning` (not `Error`) — single-tenant
// deployments behind their own rate-limited proxy legitimately want
// `RateLimit = None` and we don't want to refuse those. The warning
// surfaces in the startup log + HealthMonitorUI Preflight tab + (Debug)
// `/dev/inspect` so an operator can choose to suppress it explicitly.
//
// Heuristic for "internet-facing": `Mode != Anonymous` AND
// (`RequireHttps = true` OR `TrustForwardedHeaders = true`). The
// canonical production topology terminates TLS at a load balancer and
// speaks plaintext to the origin — that keeps `RequireHttps = false`
// but sets `TrustForwardedHeaders = true`. Keying only on
// `RequireHttps` inverted the heuristic for exactly that (most common)
// internet-facing shape, so the proxied case is now covered too.
//
// Escape hatch: `ServerConfig.AcceptNoRateLimitWhenAuthRequired`
// (default `false`). Operators behind a rate-limiting proxy set
// `TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1` to silence the warning.

/// Phase 6l.G — config validator that warns when an authenticated,
/// HTTPS-required deployment runs without `RateLimit` configured.
/// Warning, not Error: legitimate deployments behind a rate-limiting
/// proxy explicitly want `RateLimit = None`. The escape hatch silences
/// the warning when the operator has made an informed decision.
type RateLimitModeValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "rate-limit-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let internetFacing = config.RequireHttps || config.TrustForwardedHeaders
            let rateLimitOff = config.RateLimit.IsNone
            let escapeHatch = config.AcceptNoRateLimitWhenAuthRequired

            if requiresAuth && internetFacing && rateLimitOff && not escapeHatch then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + (RequireHttps or TrustForwardedHeaders) + RateLimit = None. An internet-facing authenticated deployment with no rate limit can be DoSed by a single client running a tight request loop — CPU, upload quota, and AI-token spend are all unbounded. Enable the SDK's per-scope fixed-window limiter by setting ServerConfig.RateLimit = Some { PermitLimit = <reqs>; WindowSeconds = <window>; QueueLimit = <queued> } (e.g. { PermitLimit = 100; WindowSeconds = 60; QueueLimit = 20 }), or set ServerConfig.AcceptNoRateLimitWhenAuthRequired = true (TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1) if your deployment sits behind a rate-limiting proxy that enforces per-client limits upstream."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }