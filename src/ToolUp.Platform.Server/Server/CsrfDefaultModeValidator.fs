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
// This validator surfaces that exposure at preflight. It is a
// **Warning**, not a hard refusal: existing cookie-auth deployments run
// with `NoSecurityHardening` today and may manage CSRF out of band, so a
// startup refusal would be a breaking change (GP 11). Escalating to an
// `Error` gated on an explicit `Accept…`-style acknowledgement flag is a
// tracked follow-up. The Warning names the one-line fix
// (`withSecurityHardening`) and is security-class so it survives the
// `SkipPreflight` bypass lever (the operator still sees it).
//
// Scope note: the ServerConfig-visible cookie-auth signal is
// `SseAuthMode = CookieRequired`. A bespoke `AuthConfig.TokenLocation =
// Cookie / BearerOrCookie` (wired on the auth provider, not visible on
// ServerConfig here) is the same exposure class; documenting it is part
// of the same follow-up.

/// Phase 129 — warn when cookie-authenticated mutations have no
/// server-side CSRF check (`SseAuthMode = CookieRequired` +
/// `SecurityHardening = NoSecurityHardening` in an auth-requiring mode).
type CsrfDefaultModeValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "csrf-default-mode"
        member _.Timeout = timeout

        member _.Validate() =
            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    config.SseAuthMode = CookieRequired
                    && config.SecurityHardening = NoSecurityHardening)
                (fun () ->
                    Warning(
                        sprintf
                            "ServerConfig.Surfaces = %s with SseAuthMode = CookieRequired but SecurityHardening = NoSecurityHardening. Cookie-authenticated mutations have NO server-side CSRF check — the only protection is the client cookie's SameSite=Strict, which is browser-version-dependent and subdomain-bypassable. Call withSecurityHardening (DefaultSecurityHardening or StrictSecurityHardening) to enable the server-side double-submit CSRF check. If you deliberately rely on SameSite-only / out-of-band CSRF protection, this warning documents the posture. After enabling hardening, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    ))