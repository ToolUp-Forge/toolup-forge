module ToolUp.Platform.CsrfHardeningValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 9j — hardening + split-origin preflight ───────────────────
//
// The client request-guard (`CsrfClient.installRequestGuard`) attaches
// the identity + `X-CSRF-Token` headers only to requests whose origin
// is same-origin to the page OR the explicitly-configured API origin
// (`ClientRequestSeam.ApiOrigin`). The shipped topology serves the SPA and
// the API from the same origin (relative `/api/...` routes), so the
// default is correct.
//
// But a deployment that serves the SPA from a different origin than the
// API (CDN/static host + API host; or any reverse-proxy split) is a
// split-origin deployment. The server cannot see the SPA origin, but it
// CAN see `PublicBaseUrl` — operators set that when the externally
// visible base differs from the bind address, which strongly correlates
// with a split-origin (or proxied) topology. If `SecurityHardening` is
// on in that shape and the composition root has NOT supplied
// an `ClientRequestSeam.ApiOrigin` provider, every state-changing call 403s
// silently (the guard's origin check excludes them). This validator
// turns that silent runtime failure into a loud startup warning — the
// preflight that was missing when this whole class of bug was hard to
// find.
//
// `Warning` (not `Error`): same-origin reverse proxies set
// `PublicBaseUrl` too and are perfectly fine; only genuinely
// split-origin deployments need an explicit API origin. Operators who know
// their topology is same-origin can ignore it.

/// Phase 9j — warns when `SecurityHardening` is enabled and
/// `PublicBaseUrl` is set (a split-origin / proxied-topology signal),
/// reminding the operator that a cross-origin SPA must supply
/// `ClientRequestSeam.ApiOrigin` or the client request-guard will withhold
/// the CSRF + identity headers and every mutating call 403s silently.
type CsrfHardeningValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "csrf-hardening-split-origin"
        member _.Timeout = timeout

        member _.Validate() = async {
            let hardened = config.SecurityHardening <> NoSecurityHardening

            match hardened, config.PublicBaseUrl with
            | true, Some baseUrl ->
                return
                    Warning(
                        sprintf
                            "SecurityHardening is enabled and ServerConfig.PublicBaseUrl = %s. If the SPA is served from a DIFFERENT origin than this API (split-origin: CDN/static host + API host, or a consumer that calls Remoting.withBaseUrl), the client request-guard attaches the identity + X-CSRF-Token headers only to same-origin or explicitly-configured-origin requests — so every state-changing /api/* call 403s SILENTLY. The composition root must call CsrfClient.setApiOrigin \"<api-origin>\" in that topology. If the SPA and API are same-origin (a same-origin reverse proxy also sets PublicBaseUrl), no action is needed and this warning can be ignored."
                            baseUrl
                    )
            | _ -> return Ok
        }