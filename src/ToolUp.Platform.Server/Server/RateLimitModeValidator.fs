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
            let rateLimitOff = not (RateLimitConfig.isEnabled config.RateLimit)
            let escapeHatch = config.AcceptNoRateLimitWhenAuthRequired

            // Rule 1 (Phase 6l.G, retained) — an internet-facing
            // authenticated deployment running no limiter at all is a
            // trivial-cost DoS surface. Warning, not Error: behind-proxy
            // deployments legitimately run no in-process limiter; the
            // escape hatch silences it.
            let dosWarning =
                if requiresAuth && internetFacing && rateLimitOff && not escapeHatch then
                    Some(
                        sprintf
                            "ServerConfig.Surfaces = %s + (RequireHttps or TrustForwardedHeaders) + RateLimit = RateLimitConfig.none. An internet-facing authenticated deployment with no rate limit can be DoSed by a single client running a tight request loop — CPU, upload quota, and AI-token spend are all unbounded. Enable the SDK's per-subject-kind fixed-window limiter by setting ServerConfig.RateLimit = RateLimitConfig.uniform { PermitLimit = 100; WindowSeconds = 60; QueueLimit = 20 } (or RateLimitConfig.withOverrides for per-shape limits), or set ServerConfig.AcceptNoRateLimitWhenAuthRequired = true (TOOLUP_ACCEPT_NO_RATE_LIMIT_IN_AUTH_MODE=1) if your deployment sits behind a rate-limiting proxy that enforces per-client limits upstream."
                            (DeploymentConfig.surfacesLabel config)
                    )
                else
                    None

            // Rule 2 (Phase 66 Stream C.3, new) — a PerShape policy keyed
            // on a SubjectKind that no Surface admits is dead config: no
            // request ever resolves to that kind, so the policy never
            // fires. Almost always a copy-paste / surface-trim slip.
            let admittedKinds =
                config.Surfaces
                |> List.map (function
                    | SurfaceProfile.Anonymous _ -> AnonymousKind
                    | SurfaceProfile.AuthenticatedUser _ -> UserKind
                    | SurfaceProfile.Team _ -> TeamMemberKind
                    | SurfaceProfile.ClaimBearer _ -> ClaimBearerKind)
                |> Set.ofList

            let orphanKinds =
                config.RateLimit.PerShape
                |> Map.toList
                |> List.map fst
                |> List.filter (fun k -> not (Set.contains k admittedKinds))

            let orphanWarning =
                match orphanKinds with
                | [] -> None
                | kinds ->
                    let kindNames = kinds |> List.map (sprintf "%A") |> String.concat ", "

                    Some(
                        sprintf
                            "ServerConfig.RateLimit.PerShape carries a policy for subject kind(s) [%s] that no entry in ServerConfig.Surfaces (= %s) admits. These policies are dead config — no request resolves to a subject kind outside the surface list, so the limiter never fires for them. Remove the orphaned PerShape entries or add the matching SurfaceProfile."
                            kindNames
                            (DeploymentConfig.surfacesLabel config)
                    )

            match List.choose id [ dosWarning; orphanWarning ] with
            | [] -> return Ok
            | warnings -> return Warning(String.concat " " warnings)
        }

/// Investigate gaps 2026-06-12 (Platform Gap 7) — warns when the
/// anonymous ad-analytics ingest endpoints are enabled without a
/// Phase 56 `IRateLimitStore`. `AdAnalyticsApiHandler`'s per-IP
/// budget resolves the store from DI and silently skips the gate
/// when none is registered — correct degradation, but the operator
/// should know the unauthenticated `/api/_platform/ads/*` surface is
/// running with body-cap + field-validation gates only. Warning, not
/// Error: behind-proxy deployments may enforce per-client limits
/// upstream (same posture as `RateLimitModeValidator` Rule 1).
type AdAnalyticsRateLimitValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "ad-analytics-rate-limit"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.AdAnalytics, config.RateLimitStore with
            | EnabledAdAnalytics, NoRateLimitStore ->
                return
                    Warning(
                        "ServerConfig.AdAnalytics = EnabledAdAnalytics with RateLimitStore = NoRateLimitStore. "
                        + "The anonymous /api/_platform/ads/impression and /click endpoints accept unauthenticated "
                        + "posts into the _platform scope of the audit event store; without an IRateLimitStore their "
                        + "per-IP budget is inactive, leaving only the body-size cap and field/slot validation between "
                        + "a hostile client and unbounded event-store writes. Set ServerConfig.RateLimitStore = "
                        + "InMemoryRateLimitStore (single instance) or wire an external store companion, or accept the "
                        + "exposure if a rate-limiting proxy enforces per-client limits upstream."
                    )
            | _ -> return Ok
        }