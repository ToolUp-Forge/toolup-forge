// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.EntraExternalId.EntraExternalIdClientConfig

open ToolUp.Platform

// ─── Browser-side Entra External ID config ───────────────────────────
//
// Companion-specific UI config that adapts the consumer-facing inputs
// (tenant id, app-registration client id, redirect URI, optional
// sign-up / sign-in policy ids) into the shape the generic
// `OidcUIConfig` expects, while preserving the policy ids the
// companion's shell needs to surface a "Sign up" affordance.
//
// `OidcUIConfig` itself has no notion of sign-up vs sign-in routing;
// the user-flow split is a CIAM-tier concept that the generic OIDC
// flow doesn't need to know about. Threading the policy ids through a
// companion-local config record lets the companion build the
// inner-OIDC config and the sign-up authorize-URL helper from one
// source of truth.

/// Inputs the consumer supplies in `ClientConfig.AuthUI =
/// CustomAuthUI { Wrap = EntraExternalIdAuthUI.wrap config }`.
/// Required: `Tenant`, `ClientId`, `RedirectUri`. Optional:
/// `CustomDomain`, `SignUpPolicyId`, `SignInPolicyId`,
/// `PostLogoutRedirectUri`. Scopes default to `openid profile email
/// offline_access` (the offline scope is required for refresh-token
/// rotation against External ID; the generic OIDC defaults omit it).
type EntraExternalIdClientConfig = {
    /// External ID tenant identifier. Embedded into both the issuer
    /// host (`<tenant>.ciamlogin.com` by default) and the v2.0 path
    /// segment.
    Tenant: string
    /// Optional custom-domain override. When set, replaces
    /// `<tenant>.ciamlogin.com` as the issuer host.
    CustomDomain: string option
    /// App-registration client id. Used as the OIDC `client_id`.
    ClientId: string
    /// Where the issuer redirects after sign-in. Must match a
    /// redirect URI registered on the External ID app registration.
    RedirectUri: string
    /// User-flow policy id used for the sign-up affordance. When set,
    /// the companion's shell surfaces a "Sign up" button alongside
    /// "Sign in" and routes through this policy's authorize endpoint.
    SignUpPolicyId: string option
    /// User-flow policy id used for the sign-in affordance. When set,
    /// the companion's "Sign in" button explicitly routes through this
    /// policy. Absent, the default authorize endpoint is used.
    SignInPolicyId: string option
    /// Optional override for the OIDC `post_logout_redirect_uri`.
    /// Forwarded to the underlying OIDC client.
    PostLogoutRedirectUri: string option
    /// Override for the requested scopes. Default
    /// `["openid"; "profile"; "email"; "offline_access"]`.
    Scopes: string list option
}

module EntraExternalIdClientConfig =
    /// Default scope set for Entra External ID. `offline_access` is
    /// included because External ID requires it for refresh-token
    /// issuance — the generic OIDC defaults omit it because not every
    /// IdP issues refresh tokens, but External ID does.
    let defaultScopes = [ "openid"; "profile"; "email"; "offline_access" ]

    /// Construct a minimal config with the three required inputs;
    /// optional fields default to `None` (sign-up disabled, default
    /// post-logout redirect, default scopes).
    let create tenant clientId redirectUri = {
        Tenant = tenant
        CustomDomain = None
        ClientId = clientId
        RedirectUri = redirectUri
        SignUpPolicyId = None
        SignInPolicyId = None
        PostLogoutRedirectUri = None
        Scopes = None
    }

    /// Issuer URL constructed from the tenant + optional custom
    /// domain. Always the v2.0 path — see the server-side
    /// `EntraExternalIdConfig.issuerUrl` rationale.
    let issuerUrl (config: EntraExternalIdClientConfig) : string =
        let host =
            config.CustomDomain
            |> Option.defaultWith (fun () -> sprintf "%s.ciamlogin.com" config.Tenant)

        sprintf "https://%s/%s/v2.0" host config.Tenant

    /// Authorize endpoint for an explicit user-flow policy. The shell
    /// uses this to construct the sign-up button's target URL when
    /// `SignUpPolicyId` is set.
    let authorizeUrlForPolicy (config: EntraExternalIdClientConfig) (policyId: string) : string =
        let issuer = issuerUrl config
        sprintf "%s/oauth2/v2.0/authorize?p=%s" issuer policyId

    /// Project to the shape the generic `OidcUIConfig` expects.
    /// Carries through `Issuer`, `ClientId`, `RedirectUri`, the
    /// effective scope list, and the optional post-logout redirect.
    /// `ValidateIdToken` defaults to `Some true` — client-side id_token
    /// validation (signature + iss + aud + exp via WebCrypto) runs on
    /// every Entra External ID callback. Defence-in-depth: a forged or
    /// stale id_token is caught at the boundary instead of being held
    /// in browser state until the next protected request 401s. Entra is
    /// a customer-facing CIAM surface where the failure mode of trusting
    /// a tampered token at the client edge is materially worse than the
    /// equivalent for an internal-only OIDC consumer — the default flips
    /// here even though the generic `OidcUIConfig.defaults` remains
    /// `None` (per the SDK's "default off until 0.4.x" convention for
    /// the generic case). Callers wanting to opt out (regression
    /// investigation, intentional fallback during a JWKS-fetch outage,
    /// etc.) set `ValidateIdToken = Some false` on the projected
    /// `OidcUIConfig` post-projection.
    let toOidcUIConfig (config: EntraExternalIdClientConfig) : OidcUIConfig = {
        Issuer = issuerUrl config
        ClientId = config.ClientId
        RedirectUri = config.RedirectUri
        Scopes = config.Scopes |> Option.defaultValue defaultScopes
        PostLogoutRedirectUri = config.PostLogoutRedirectUri
        ValidateIdToken = Some true
        // Entra External ID issues decodable v2 JWT access tokens, so
        // the default access-token bearer is correct and this stays
        // `None` — today's behaviour, byte for byte.
        BearerToken = None
        // `None` deliberately: this companion renders its OWN
        // dual-button sign-in screen from `SignUpPolicyId`, so the
        // generic shell's secondary-flow affordance must not also fire.
        // A consumer wanting that affordance on the standard shell uses
        // `OidcPresets.withEntraSignUpUserFlow` and drops the companion.
        SecondaryFlow = None
    }