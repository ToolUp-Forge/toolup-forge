module ToolUp.Platform.OidcAudienceBindingValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── OIDC audience binding in authenticated modes ───────────────────
//
// `AuthConfig.Audience` is `string option`; `None` skips the `aud`
// claim check entirely (`OidcAuthProvider.validateAudience`). The OIDC
// companion reads `TOOLUP_OIDC_AUDIENCE` to populate it — when that env
// var is unset the provider accepts ANY token the configured issuer
// minted, including a token issued for a different relying party that
// shares the same IdP (Auth0 tenant, Azure AD directory, a shared
// Keycloak realm). That is a confused-deputy / token-reuse hole: the
// signature is valid, the issuer matches, but the token was never
// intended for this application.
//
// The SDK already refuses startup for the two adjacent silent-insecure
// auth defaults — `HeaderAuthProviderModeValidator` (spoofable header
// identity) and `SseAuthModeValidator` (userId in the URL). The unbound
// OIDC audience is the same shape of defect with no matching guard;
// this validator closes that asymmetry.
//
// Anonymous / non-auth modes are exempt (`requiresAuth = false`) — with
// no real identity there is nothing to confuse. Escape hatch:
// `ServerConfig.AcceptUnboundAudienceWhenAuthRequired = true`
// (`TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE=1`) for a single-app
// issuer where no other relying party shares it, or where the IdP does
// not yet provision an audience claim.
//
// Refusal shape, not warning. Token reuse is identity-spoofing-
// adjacent: a valid-but-wrong-audience token authenticates as its
// subject against this app. Mirrors Phase 6l.A / 6l.I.

[<Literal>]
let private AuthModeEnvVar = "TOOLUP_AUTH_MODE"

[<Literal>]
let private OidcAudienceEnvVar = "TOOLUP_OIDC_AUDIENCE"

let private envValue (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some(v.Trim())

/// Config validator that refuses an authenticated `Mode` running OIDC
/// (`TOOLUP_AUTH_MODE=oidc`) without `TOOLUP_OIDC_AUDIENCE`, unless the
/// operator has explicitly opted in via
/// `ServerConfig.AcceptUnboundAudienceWhenAuthRequired`.
type OidcAudienceBindingValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "oidc-audience-binding"
        member _.Timeout = timeout

        member _.Validate() = async {
            let requiresAuth = DeploymentConfig.requiresAnyAuth config

            let authMode = envValue AuthModeEnvVar |> Option.map _.ToLowerInvariant()

            let oidcAudience = envValue OidcAudienceEnvVar
            let escapeHatch = config.AcceptUnboundAudienceWhenAuthRequired

            if requiresAuth && authMode = Some "oidc" && oidcAudience.IsNone && not escapeHatch then
                return
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s with TOOLUP_AUTH_MODE=oidc but TOOLUP_OIDC_AUDIENCE unset. The OIDC provider's audience check is skipped when no audience is configured, so any token the issuer minted is accepted — including a token issued for a different application that shares the same IdP (same Auth0 tenant / Azure AD directory / Keycloak realm). The signature and issuer are valid; the token was simply never intended for this app, and it authenticates as its subject anyway (confused-deputy / token reuse). Set TOOLUP_OIDC_AUDIENCE to this application's audience / client id, or set ServerConfig.AcceptUnboundAudienceWhenAuthRequired = true (TOOLUP_ACCEPT_UNBOUND_AUDIENCE_IN_AUTH_MODE=1) for a single-app issuer no other relying party shares. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }