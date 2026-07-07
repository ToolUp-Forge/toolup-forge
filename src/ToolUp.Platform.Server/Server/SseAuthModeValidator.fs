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
// In authenticated modes the same fallback is live stream
// eavesdropping (Phase 117 sharpened this from the original
// "PII-grade identifier" framing — the exposure is worse than that):
//   - `userId` is now a real (server-resolved) user id
//   - An unauthenticated caller who knows or captures a userId can
//     `GET /api/notifications?userId=<victim>` and receive the
//     victim's live notification stream — membership changes, job
//     completions, system messages — for as long as the connection
//     stays open. No cookie, no JWT, no challenge.
//   - The id itself rides in the URL — visible in browser history,
//     web-server access logs, CDN access logs, `Referer` headers on
//     subsequent navigations, and any reverse-proxy log line that
//     captures the query string — so "knows or captures" is cheap.
//   - The same connection spam is a DoS-subscribe vector against the
//     per-scope connection cap.
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

    // QueryParamFallback SSE in an auth Mode leaks userId via URL — an
    // identity-leak hole; security-class, so it survives SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "sse-auth-mode"
        member _.Timeout = timeout

        member _.Validate() =
            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    config.SseAuthMode = QueryParamFallback
                    && not config.AcceptQueryParamSseAuthWhenAuthRequired)
                (fun () ->
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s but SseAuthMode = QueryParamFallback. In this mode any caller who knows a userId can open /api/notifications?userId=<victim> WITHOUT a cookie or JWT and receive that user's live notification stream (membership changes, job completions, system messages) — live stream eavesdropping, not just identifier leakage. The userId also rides in the URL (CDN logs, web-server logs, browser history, Referer headers), so capturing one is cheap. Set TOOLUP_SSE_AUTH=cookie (production default for auth modes — requires the client's IAuthBridge to write the JWT cookie) or set ServerConfig.AcceptQueryParamSseAuthWhenAuthRequired = true (TOOLUP_ACCEPT_QUERYPARAM_SSE_AUTH_IN_AUTH_MODE=1) ONLY for dev / CI / behind-query-stripping-proxy deployments where you accept that exposure. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    ))