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
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let trustForwarded = config.TrustForwardedHeaders
            let httpsRequired = config.RequireHttps

            if requiresAuth && trustForwarded && not httpsRequired then
                return
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s + TrustForwardedHeaders = true + RequireHttps = false. The SDK trusts X-Forwarded-Proto from any peer (KnownIPNetworks is cleared) — an attacker sending plain HTTP with X-Forwarded-Proto: https makes Request.IsHttps return true, fooling cookie-secure flags / OIDC RedirectUri / any code branching on TLS. Set TOOLUP_REQUIRE_HTTPS=1 if your TLS terminator runs upstream and the SDK should refuse plain HTTP. Staging deployments that legitimately run plaintext Kestrel behind upstream TLS can ignore this warning."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }