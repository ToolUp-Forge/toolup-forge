// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcPresets

open ToolUp.AuthProviders.Oidc.OidcAppConfig

// ─── Provider presets (0.4.0 — return OidcAppConfig) ──────────────────
//
// Smart constructors that emit a fully-formed `OidcAppConfig`,
// encoding the per-provider quirks consumers had to hand-roll until
// now. Each preset is the answer to "what knobs did you trip over
// the last time you wired this IdP from scratch?" captured as code
// instead of as a comment-essay.
//
// 0.4.0 BREAKING CHANGE — presets previously returned the tuple
// `(OidcUIConfig * PresetMetadata)`; they now return `OidcAppConfig`
// which carries the preset's identity in its `Preset: PresetKind
// option` field. The descriptive metadata that 0.3.x's
// `PresetMetadata` carried (issuer form, auto-added scopes, notes,
// expects-decodable-token flag) is now derived from `PresetKind` via
// helpers in `OidcAppConfig.PresetKind` — `label`, `issuerForm`,
// `autoAddedScopes`, `notes`, `expectsDecodableAccessToken`. Consumers
// adopt by replacing `let cfg, _ = OidcPresets.X` with `let cfg =
// OidcPresets.X` (drop the tuple destructure).
//
// Presets:
//
//   generic           — explicit issuer + clientId; no quirks applied.
//                       For IdPs the SDK doesn't yet have first-class
//                       provider knowledge for (Okta, Keycloak,
//                       custom, etc.).
//
//   entraWorkforce    — workforce Entra ID / Azure AD
//                       (login.microsoftonline.com). Auto-adds the
//                       `api://{clientId}/access_as_user` scope so
//                       Entra issues a decodable v2 JWT access token
//                       addressed to THIS app — without it Entra
//                       mints an opaque Microsoft Graph token and
//                       server-side audience validation rejects every
//                       request. The single most-commonly-misconfigured
//                       workforce-Entra knob; the preset is built
//                       around making it impossible to forget.
//
//   entraExternalId   — Entra External ID (CIAM, *.ciamlogin.com).
//                       Issuer follows the documented v2.0 path.
//                       Adds `offline_access` for refresh-token
//                       rotation (External ID requires it).
//                       `ValidateIdToken` defaults to `Some true`
//                       (defence-in-depth at the customer-facing
//                       boundary).
//
//   entraExternalIdWithDomain — same as above with a custom CIAM
//                       domain replacing the default
//                       `*.ciamlogin.com` host.
//
//   auth0             — Auth0. Default scope set with refresh-token
//                       support; issuer is the tenant URL (with
//                       trailing slash, per Auth0's `iss` claim
//                       shape).

let private genericDefaultScopes = [ "openid"; "profile"; "email" ]

let private workforceEntraScopes (clientId: string) = [
    "openid"
    "profile"
    "email"
    "offline_access"
    sprintf "api://%s/access_as_user" clientId
]

let private externalIdDefaultScopes = [ "openid"; "profile"; "email"; "offline_access" ]
let private auth0DefaultScopes = [ "openid"; "profile"; "email"; "offline_access" ]

/// Generic OIDC preset. Explicit issuer; the SDK applies no quirks.
/// For IdPs the SDK doesn't yet have first-class provider knowledge
/// for (Okta, Keycloak, custom OIDC providers, etc.). Scope set is
/// the OIDC-spec minimum (`openid profile email`); consumers
/// requiring `offline_access` for refresh tokens add it explicitly.
///
/// 0.4.3 — `ValidateIdToken` defaults to `Some true`. Without first-
/// class provider knowledge the SDK has no way to know whether the
/// chosen IdP is internal or customer-facing, so defence-in-depth
/// signature/iss/aud/exp validation is the safer default. Consumers
/// who explicitly trust the post-callback channel can set it back to
/// `None`; the coherence validator emits a warning in that case so
/// the opt-out is visible at startup.
let generic (issuer: string) (clientId: string) (redirectUri: string) : OidcAppConfig = {
    Issuer = issuer
    Audience = clientId
    ClientId = clientId
    Scopes = genericDefaultScopes
    RedirectUri = redirectUri
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    Preset = Some Generic
}

/// Workforce Entra ID / Azure AD preset. `tenantId` MUST be a tenant
/// GUID (or the literal `"common"` for multi-tenant apps), NOT a
/// tenant domain — the latter produces a different issuer form Entra
/// doesn't validate access tokens against.
///
/// Auto-adds the load-bearing `api://{clientId}/access_as_user`
/// scope so Entra issues a decodable v2 JWT access token. Without
/// it, Entra mints an opaque token addressed to Microsoft Graph and
/// the server's audience validation rejects every request after
/// sign-in. The app registration's `requestedAccessTokenVersion`
/// must also be 2 (v2 JWT format); the coherence validator's Rule
/// 11 INFO surfaces this hint at composition time.
let entraWorkforce (tenantId: string) (clientId: string) (redirectUri: string) : OidcAppConfig = {
    Issuer = sprintf "https://login.microsoftonline.com/%s/v2.0" tenantId
    Audience = clientId
    ClientId = clientId
    Scopes = workforceEntraScopes clientId
    RedirectUri = redirectUri
    PostLogoutRedirectUri = None
    ValidateIdToken = None
    Preset = Some EntraWorkforce
}

/// Entra External ID (CIAM) preset. Issuer follows the documented
/// v2.0 path:
/// `https://{tenantSubdomain}.ciamlogin.com/{tenantSubdomain}/v2.0`.
/// Includes `offline_access` for refresh-token rotation.
///
/// `ValidateIdToken` defaults to `Some true` — client-side id_token
/// validation (signature + iss + aud + exp via WebCrypto) runs on
/// every callback. Defence-in-depth at the customer-facing CIAM
/// boundary where a tampered token's failure mode is materially
/// worse than for an internal OIDC consumer.
///
/// For sign-up / sign-in user-flow policy routing, use the dedicated
/// `EntraExternalIdClient` companion (which exposes the sign-up
/// affordance separately); this preset is the single-call path for
/// the no-policy-split case.
let entraExternalId (tenantSubdomain: string) (clientId: string) (redirectUri: string) : OidcAppConfig = {
    Issuer = sprintf "https://%s.ciamlogin.com/%s/v2.0" tenantSubdomain tenantSubdomain
    Audience = clientId
    ClientId = clientId
    Scopes = externalIdDefaultScopes
    RedirectUri = redirectUri
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    Preset = Some EntraExternalId
}

/// Entra External ID preset with a custom-domain override. Use when
/// the tenant is configured with a custom CIAM domain replacing the
/// default `*.ciamlogin.com` host. The issuer becomes
/// `https://{customDomain}/{tenantSubdomain}/v2.0`; everything else
/// matches `entraExternalId`. `Preset` carries the custom domain so
/// the coherence validator renders the customised issuer form in
/// its findings.
let entraExternalIdWithDomain
    (tenantSubdomain: string)
    (customDomain: string)
    (clientId: string)
    (redirectUri: string)
    : OidcAppConfig =
    let baseCfg = entraExternalId tenantSubdomain clientId redirectUri

    {
        baseCfg with
            Issuer = sprintf "https://%s/%s/v2.0" customDomain tenantSubdomain
            Preset = Some(EntraExternalIdWithDomain customDomain)
    }

/// Auth0 preset. `domain` is the Auth0 tenant URL host (e.g.
/// `your-tenant.auth0.com` or `your-tenant.eu.auth0.com`). Default
/// scopes include `offline_access` for refresh-token rotation.
///
/// Auth0 access tokens are opaque by default — unless the consumer
/// configures an API audience in the Auth0 dashboard and supplies it
/// via the `audience` extra parameter (handled by
/// `beginSignInWithExtras`), `classifyStoredToken` will see the
/// stored token as `OpaqueToken` and defer validity to the server.
let auth0 (domain: string) (clientId: string) (redirectUri: string) : OidcAppConfig = {
    Issuer = sprintf "https://%s/" domain
    Audience = clientId
    ClientId = clientId
    Scopes = auth0DefaultScopes
    RedirectUri = redirectUri
    PostLogoutRedirectUri = None
    ValidateIdToken = None
    Preset = Some Auth0
}