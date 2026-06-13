module ToolUp.Platform.SecurityHeadersValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.K / 129d — thin SecurityHeaders on internet-facing auth ─
//
// Phase 129d gave `SecurityHeadersMiddleware` an always-on baseline floor
// (`X-Frame-Options: DENY` / `nosniff` / `Referrer-Policy`, plus HSTS when
// `RequireHttps`), so a stock deployment is no longer framable/sniffable
// by default. This validator's remaining job is to flag the gap the floor
// deliberately does NOT close: an internet-facing authenticated deployment
// with neither a `Content-Security-Policy` (the floor ships none — a
// default CSP breaks real apps) nor the richer `productionDefaults`
// posture (preload HSTS + `includeSubDomains` + a baseline CSP). The
// remaining gap exists only when `SecurityHeaders = Map.empty` AND
// `SecurityHardening = NoSecurityHardening` (otherwise `CspMiddleware`
// stamps an aggregated CSP, so no warning is warranted).
//
// `Warning` (not `Error`): (1) some deployments serve only API responses
// (no browser surface) and don't need a CSP; (2) SDK consumers might emit
// a CSP via a custom middleware ahead of this one. The warning surfaces in
// HealthMonitorUI / `/dev/inspect` so the operator can set
// `ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults`,
// call `withSecurityHardening`, or knowingly ignore.
//
// Heuristic for "internet-facing": `Mode != Anonymous` AND
// (`RequireHttps = true` OR `TrustForwardedHeaders = true`). Mirrors
// `RateLimitModeValidator` — the TLS-terminated-at-LB topology keeps
// `RequireHttps = false` but sets `TrustForwardedHeaders = true`, so
// keying only on `RequireHttps` missed the most common internet-facing
// deployment shape.

/// Phase 6l.K / 129d — config validator that warns when an
/// internet-facing authenticated deployment ships no CSP: empty
/// `SecurityHeaders` AND `NoSecurityHardening` (the 129d floor covers the
/// other baseline headers, so this flags only the remaining CSP gap).
type SecurityHeadersValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "security-headers-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let internetFacing = DeploymentConfig.isInternetFacing config
            let headersEmpty = config.SecurityHeaders.IsEmpty
            let noHardening = config.SecurityHardening = NoSecurityHardening

            if requiresAuth && internetFacing && headersEmpty && noHardening then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + (RequireHttps or TrustForwardedHeaders) + SecurityHeaders = Map.empty + SecurityHardening = NoSecurityHardening. Phase 129d auto-stamps a baseline (X-Frame-Options DENY / nosniff / Referrer-Policy strict-origin-when-cross-origin, plus HSTS when RequireHttps), but this deployment still ships no Content-Security-Policy (XSS surface widened) and no preload/includeSubDomains HSTS posture. Set ServerConfig.SecurityHeaders = SecurityHeaders.productionDefaults for the SDK's recommended baseline (preload HSTS / nosniff / X-Frame-Options DENY / Referrer-Policy / baseline CSP), merge productionDefaults with your own per-deployment overrides, or call withSecurityHardening for an auto-generated companion-aware CSP. API-only deployments that legitimately don't need browser-targeted headers can ignore this warning."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }