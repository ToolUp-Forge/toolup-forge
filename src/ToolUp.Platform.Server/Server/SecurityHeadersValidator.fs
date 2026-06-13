module ToolUp.Platform.SecurityHeadersValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.K — empty SecurityHeaders on internet-facing auth-mode ─
//
// `ServerConfig.SecurityHeaders = Map.empty` is the default; the
// `SecurityHeadersMiddleware` short-circuits and stamps no headers.
// Correct for local dev. Wrong for an internet-facing authenticated
// deployment — no HSTS, no CSP, no X-Frame-Options, no nosniff.
//
// Phase 6l.K emits `Warning` (not `Error`) for two reasons: (1) some
// deployments serve only API responses (no browser surface) and don't
// need security headers; (2) SDK consumers might emit headers via a
// custom middleware ahead of this one. A startup warning surfaces in
// HealthMonitorUI / `/dev/inspect` so the operator can either set
// `ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults`
// or knowingly ignore.
//
// Heuristic for "internet-facing": `Mode != Anonymous` AND
// (`RequireHttps = true` OR `TrustForwardedHeaders = true`). Mirrors
// `RateLimitModeValidator` — the TLS-terminated-at-LB topology keeps
// `RequireHttps = false` but sets `TrustForwardedHeaders = true`, so
// keying only on `RequireHttps` missed the most common internet-facing
// deployment shape.

/// Phase 6l.K — config validator that warns when an authenticated,
/// HTTPS-required deployment runs with `SecurityHeaders = Map.empty`.
type SecurityHeadersValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "security-headers-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let internetFacing = DeploymentConfig.isInternetFacing config
            let headersEmpty = config.SecurityHeaders.IsEmpty

            if requiresAuth && internetFacing && headersEmpty then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + (RequireHttps or TrustForwardedHeaders) + SecurityHeaders = Map.empty. An internet-facing authenticated deployment with no security headers is missing HSTS (first-visit downgrade attacks unprotected), CSP (XSS surface widened), X-Frame-Options (clickjacking surface open), and nosniff (MIME-confusion). Set ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults for the SDK's recommended baseline (HSTS / nosniff / X-Frame-Options DENY / Referrer-Policy strict-origin-when-cross-origin / baseline CSP), or merge productionDefaults with your own per-deployment overrides. API-only deployments that legitimately don't need browser-targeted headers can ignore this warning."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }