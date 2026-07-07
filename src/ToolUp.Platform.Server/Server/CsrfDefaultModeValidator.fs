module ToolUp.Platform.CsrfDefaultModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 129 — CSRF off-by-default under cookie auth ──────────────
//
// `SecurityHardening` defaults to `NoSecurityHardening` (GP 13 — zero
// cost when unused), and `CsrfMiddleware` short-circuits to pass-through
// in that mode. A deployment that opts into cookie-based auth — the
// `SseAuthMode = CookieRequired` path, where the browser authenticates
// SSE (and same-origin XHR) off a cookie — but never calls
// `withSecurityHardening` therefore has NO server-side CSRF check: the
// only protection is the client cookie's `SameSite=Strict`, which is
// browser-version-dependent and subdomain-bypassable.
//
// This validator surfaces that exposure at preflight as an **Error**
// (Phase 129d) — a hard startup refusal. The secure-by-default inversion:
// when the resolved shape is auth-requiring + cookie-authenticated, the
// safe posture (server-side CSRF) is the no-config outcome and the
// dangerous one (SameSite-only) must be consciously opted into. The
// escape hatch is `ServerConfig.AcceptSameSiteOnlyCsrfWhenAuthRequired =
// true` (`TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE=1`), which
// downgrades the refusal to `Ok` for a deployment that manages CSRF out
// of band — the standard `Accept…WhenAuthRequired` escape-hatch idiom
// (cf. `AcceptQueryParamSseAuthWhenAuthRequired`,
// `AcceptNoRateLimitWhenAuthRequired`). The Error names BOTH fixes
// (`withSecurityHardening` preferred; the acknowledgement flag for the
// genuine SameSite-only case) and is security-class so it survives the
// `SkipPreflight` bypass lever.
//
// Scope note: the ServerConfig-visible cookie-auth signal is
// `SseAuthMode = CookieRequired`. A bespoke `AuthConfig.TokenLocation =
// Cookie / BearerOrCookie` (wired on the auth provider, not visible on
// ServerConfig here) is the same exposure class; the migration doc
// (`docs/migrations/129-secure-by-default-request-edge.md`) documents it.

/// Phase 129d — refuse startup when cookie-authenticated mutations have
/// no server-side CSRF check (`SseAuthMode = CookieRequired` +
/// `SecurityHardening = NoSecurityHardening` in an auth-requiring mode),
/// unless the operator has acknowledged the SameSite-only posture via
/// `ServerConfig.AcceptSameSiteOnlyCsrfWhenAuthRequired`.
type CsrfDefaultModeValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // Cookie auth without server-side CSRF is a request-forgery
    // surface — security-class, so its warning must survive SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "csrf-default-mode"
        member _.Timeout = timeout

        member _.Validate() =
            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    config.SseAuthMode = CookieRequired
                    && config.SecurityHardening = NoSecurityHardening
                    && not config.AcceptSameSiteOnlyCsrfWhenAuthRequired)
                (fun () ->
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s with SseAuthMode = CookieRequired but SecurityHardening = NoSecurityHardening. Cookie-authenticated mutations have NO server-side CSRF check — the only protection is the client cookie's SameSite=Strict, which is browser-version-dependent and subdomain-bypassable. Call withSecurityHardening (DefaultSecurityHardening or StrictSecurityHardening) to enable the server-side double-submit CSRF check. If you deliberately rely on SameSite-only / out-of-band CSRF protection, set ServerConfig.AcceptSameSiteOnlyCsrfWhenAuthRequired = true (TOOLUP_ACCEPT_SAMESITE_ONLY_CSRF_IN_AUTH_MODE=1) to acknowledge the posture and start. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    ))