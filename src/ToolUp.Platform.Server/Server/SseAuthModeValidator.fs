module ToolUp.Platform.SseAuthModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.I — QueryParamFallback in authenticated modes ─────────
//
// `SseAuthMode = QueryParamFallback` (the default) lets the browser's
// `EventSource` connect to `/api/notifications?userId=<guid>` and
// `/api/ai/events?userId=<guid>` without going through cookie-bearing
// auth. The query-param userId then becomes the connection's identity.
// In Anonymous mode that's correct — there is no real identity, the
// userId is a per-tab session marker.
//
// In authenticated modes the same fallback is identity exposure:
//   - `userId` is now a real (server-resolved) user id
//   - It rides in the URL — visible in browser history, web-server
//     access logs, CDN access logs, `Referer` headers on subsequent
//     navigations, and any reverse-proxy log line that captures the
//     query string
//   - An attacker capturing the URL gets the userId for free. They
//     can't impersonate (no session cookie / JWT) but they have a
//     PII-grade identifier they shouldn't have, and they can DoS-
//     subscribe by spamming `EventSource` connections under it.
//
// Operators who provision Individual / Team / MultiTeam mode but
// don't read DEPLOYMENT.md carefully end up with this fallback active.
// Phase 6l.I refuses startup unless the operator has explicitly
// chosen to keep the fallback (escape hatch — for dev / CI runs of
// authenticated mode where IAuthBridge isn't wired yet, OR for
// deployments where the browser surface is genuinely behind a
// query-string-stripping proxy).
//
// Refusal shape, not warning. Userid leakage is identity-spoofing-
// adjacent: the browser path is the trust boundary for SSE, and the
// fallback bypasses it. Mirrors Phase 6l.A.

/// Phase 6l.I — config validator that refuses
/// `SseAuthMode = QueryParamFallback` in authenticated platform modes
/// unless the operator has opted in via
/// `ServerConfig.AcceptQueryParamSseAuthWhenAuthRequired`.
type SseAuthModeValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "sse-auth-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config
            let queryParamFallback = config.SseAuthMode = QueryParamFallback
            let escapeHatch = config.AcceptQueryParamSseAuthWhenAuthRequired

            if requiresAuth && queryParamFallback && not escapeHatch then
                return
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s but SseAuthMode = QueryParamFallback. The browser's EventSource will connect to /api/notifications?userId=<guid> with the userId in the URL — visible in CDN logs, web-server logs, browser history, and Referer headers on subsequent navigations. In an authenticated mode that userId is a real identity, not a per-session marker. Set TOOLUP_SSE_AUTH=cookie (production default for auth modes — requires the client's IAuthBridge to write the JWT cookie) or set ServerConfig.AcceptQueryParamSseAuthWhenAuthRequired = true (TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE=1) for dev / CI / behind-query-stripping-proxy deployments where you've made an informed decision. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }