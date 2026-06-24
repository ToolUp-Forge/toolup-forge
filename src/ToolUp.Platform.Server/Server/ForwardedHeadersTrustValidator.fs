module ToolUp.Platform.ForwardedHeadersTrustValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.K — TrustForwardedHeaders without RequireHttps ────────
//
// `TrustForwardedHeaders = true` registers `app.UseForwardedHeaders()`
// with `KnownIPNetworks` cleared — the SDK trusts `X-Forwarded-Proto`,
// `X-Forwarded-For`, `X-Forwarded-Host` from any peer. That's correct
// behind a TLS-terminating cloud load balancer (ALB / Cloudflare /
// nginx) where the proxy is the only hop and replaces the client's
// headers.
//
// In a deployment where `RequireHttps = false`, the SDK doesn't
// require TLS at all — Kestrel listens on plain HTTP. Combine those
// two and an attacker sending a plain-HTTP request with
// `X-Forwarded-Proto: https` makes ASP.NET Core's `Request.IsHttps`
// return `true`. Any code branching on `Request.IsHttps` (cookie
// security flags, OIDC `RedirectUri` generation, etc.) believes it's
// on TLS when it isn't. Identity confusion territory.
//
// Phase 6l.K emits `Warning` (not `Error`) — staging deployments
// genuinely run TLS-terminated upstream while leaving Kestrel HTTP-
// only and don't want a refusal. Operators get the warning at startup
// and can either set `RequireHttps = true` or accept the posture.

/// Phase 6l.K — config validator that warns when
/// `TrustForwardedHeaders = true` is paired with `RequireHttps = false`
/// in an authenticated platform mode (the spoof surface).
type ForwardedHeadersTrustValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "forwarded-headers-trust"
        member _.Timeout = timeout

        member _.Validate() = async {
            let trustForwarded = config.TrustForwardedHeaders
            let httpsRequired = config.RequireHttps

            // Fire whenever forwarded headers are trusted — NOT only in auth
            // mode and NOT only when RequireHttps = false. `UseForwardedHeaders`
            // is registered with KnownIPNetworks/KnownProxies cleared, so ANY
            // peer's X-Forwarded-* is honoured. The X-Forwarded-For spoof
            // (rate-limit bypass + audit/access-log poisoning) exists in every
            // mode — including anonymous internet-facing deployments, which the
            // old `requiresAuth` gate left with no signal at all — and persists
            // even when RequireHttps = true. The X-Forwarded-Proto / IsHttps
            // spoof is the additional consequence specific to RequireHttps = false.
            if not trustForwarded then
                return Ok
            else
                let httpsClause =
                    if httpsRequired then
                        ""
                    else
                        " With RequireHttps = false, a plain-HTTP request carrying X-Forwarded-Proto: https also makes Request.IsHttps return true, fooling cookie-secure flags / OIDC RedirectUri / any TLS branch — set TOOLUP_REQUIRE_HTTPS=1 if a TLS terminator runs upstream."

                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + TrustForwardedHeaders = true, but KnownIPNetworks / KnownProxies are cleared — the SDK trusts X-Forwarded-For / X-Forwarded-Proto from ANY peer, not only your proxy. A caller rotating X-Forwarded-For bypasses IP rate limiting and poisons audit / access logs (rate limiting keys on the post-rewrite RemoteIpAddress); this applies in every mode, including anonymous internet-facing deployments.%s Front the deployment with a single trusted proxy that strips client-supplied X-Forwarded-* headers. A trusted-proxy CIDR allowlist that scopes this trust to known proxies is planned hardening."
                            (DeploymentConfig.surfacesLabel config)
                            httpsClause
                    )
        }