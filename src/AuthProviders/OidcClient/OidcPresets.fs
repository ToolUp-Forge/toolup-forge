// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcPresets

open ToolUp.Platform

// ─── Provider presets ──────────────────────────────────────────────
//
// Smart constructors that emit a typed `OidcUIConfig` + provenance
// `PresetMetadata`, encoding the per-provider quirks consumers had to
// hand-roll until now. Each preset is the answer to "what knobs did
// you trip over the last time you wired this IdP from scratch?"
// captured as code instead of as a comment-essay.
//
// Currently shipped:
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
//                       addressed to THIS app — without it, Entra
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
//   auth0             — Auth0. Default scope set with refresh-token
//                       support; issuer is the tenant URL (with
//                       trailing slash, per Auth0's `iss` claim
//                       shape).
//
// Each preset returns `(OidcUIConfig * PresetMetadata)`. The metadata
// carries provenance for the coherence validator (Phase C) to
// surface useful WARN / INFO findings, and is otherwise inspectable
// by tests. Consumers that don't care about it can pattern-match it
// out: `let cfg, _ = OidcPresets.entraWorkforce ...`.
//
// Coexists with the existing `OidcUIConfig.defaults issuer clientId
// redirectUri` constructor in `ToolUp.Platform`. Both shapes ship
// together for one minor cycle; the unified `OidcAppConfig` (server
// + client from one declaration) lands at the coordinated 0.X.0
// minor bump.

/// Provenance + invariant declarations for a preset. Carried beside
/// the OidcUIConfig the preset emits so the coherence validator can
/// surface "you said `entraWorkforce` but your issuer looks like a
/// tenant domain not a GUID" findings, and so a tracer can group
/// emits by provider.
type PresetMetadata = {
    /// Short stable identifier — `"entra-workforce"`,
    /// `"entra-external-id"`, `"auth0"`, `"generic"`. Used by the
    /// coherence validator (Phase C) as a machine-readable tag for
    /// findings and by tracer emits for per-provider grouping. NEVER
    /// rename without a CHANGELOG entry — downstream consumers tag
    /// metrics by this string.
    Name: string
    /// Expected issuer URL form — human-readable description for
    /// operator-facing validator output (NOT a regex). The validator
    /// (Phase C) renders this when an issuer URL doesn't look like
    /// what the preset's quirks were tuned for.
    IssuerForm: string
    /// Scopes the preset added on top of the consumer's request /
    /// preset defaults. Empty for `generic` (no provider quirks);
    /// non-empty for presets that auto-add load-bearing scopes.
    AutoAddedScopes: string list
    /// Whether the preset expects a decodable JWT access token.
    /// `entraWorkforce` + `entraExternalId` + auth0-with-explicit-API
    /// audience → true. Opaque-token-by-default flows (auth0 default,
    /// generic) → false. Affects classifier expectations and the
    /// coherence validator's hints around audience binding.
    ExpectsDecodableAccessToken: bool
    /// Free-text notes for log + validator output. Each line is a
    /// self-contained sentence; the validator may surface any subset.
    Notes: string list
}

let private genericDefaultScopes = [ "openid"; "profile"; "email" ]
let private workforceEntraBaseScopes = [ "openid"; "profile"; "email"; "offline_access" ]
let private externalIdDefaultScopes = [ "openid"; "profile"; "email"; "offline_access" ]
let private auth0DefaultScopes = [ "openid"; "profile"; "email"; "offline_access" ]

/// Generic OIDC preset. Explicit issuer; the SDK applies no quirks.
/// For IdPs the SDK doesn't yet have first-class provider knowledge
/// for (Okta, Keycloak, custom OIDC providers, etc.). Scope set is
/// the OIDC-spec minimum (`openid profile email`); consumers
/// requiring `offline_access` for refresh tokens add it explicitly.
let generic (issuer: string) (clientId: string) (redirectUri: string) : OidcUIConfig * PresetMetadata =
    let cfg: OidcUIConfig = {
        Issuer = issuer
        ClientId = clientId
        RedirectUri = redirectUri
        Scopes = genericDefaultScopes
        PostLogoutRedirectUri = None
        ValidateIdToken = None
    }

    let meta: PresetMetadata = {
        Name = "generic"
        IssuerForm = "explicit issuer URL — no SDK-side provider quirks applied"
        AutoAddedScopes = []
        ExpectsDecodableAccessToken = false
        Notes = [
            "Scope set is `openid profile email`; if your IdP requires `offline_access` for refresh tokens, add it explicitly."
            "ValidateIdToken defaults to None (off). For customer-facing surfaces consider Some true to defence-in-depth a tampered id_token at the boundary."
        ]
    }

    cfg, meta

/// Workforce Entra ID / Azure AD preset. `tenantId` MUST be a tenant
/// GUID (or the literal `"common"` for multi-tenant apps), NOT a
/// tenant domain — the latter produces a different issuer form Entra
/// doesn't validate access tokens against.
///
/// The `api://{clientId}/access_as_user` scope is auto-added so Entra
/// issues a decodable v2 JWT access token. Without it, Entra mints an
/// opaque token addressed to Microsoft Graph and the server's
/// audience validation rejects every request. This is the single
/// most-commonly-misconfigured workforce-Entra knob; the preset is
/// built around making it impossible to forget.
///
/// Requires `requestedAccessTokenVersion: 2` on the Entra app
/// registration's manifest. The coherence validator (Phase C) will
/// surface this as an INFO hint alongside the preset's adopted
/// metadata.
let entraWorkforce (tenantId: string) (clientId: string) (redirectUri: string) : OidcUIConfig * PresetMetadata =
    let issuer = sprintf "https://login.microsoftonline.com/%s/v2.0" tenantId
    let accessAsUserScope = sprintf "api://%s/access_as_user" clientId

    let cfg: OidcUIConfig = {
        Issuer = issuer
        ClientId = clientId
        RedirectUri = redirectUri
        Scopes = workforceEntraBaseScopes @ [ accessAsUserScope ]
        PostLogoutRedirectUri = None
        ValidateIdToken = None
    }

    let meta: PresetMetadata = {
        Name = "entra-workforce"
        IssuerForm = "https://login.microsoftonline.com/{tenantGuid}/v2.0"
        AutoAddedScopes = [ accessAsUserScope ]
        ExpectsDecodableAccessToken = true
        Notes = [
            "Tenant identifier MUST be a GUID (or `common` for multi-tenant apps), NOT a tenant domain — Entra rejects access-token audience validation under the domain-form issuer."
            "The `api://{clientId}/access_as_user` scope is load-bearing — without it, Entra mints an opaque token addressed to Microsoft Graph."
            "The app registration's `requestedAccessTokenVersion` MUST be 2 (v2 JWT format)."
        ]
    }

    cfg, meta

/// Entra External ID (CIAM) preset. Issuer follows the documented
/// v2.0 path: `https://{tenantSubdomain}.ciamlogin.com/{tenantSubdomain}/v2.0`.
/// Includes `offline_access` for refresh-token rotation. Mirrors the
/// shape `EntraExternalIdClientConfig.toOidcUIConfig` produces in the
/// dedicated External ID companion (which additionally surfaces a
/// sign-up affordance via user-flow policy routing — out of scope for
/// a one-call preset; use the companion when you need the split).
///
/// `ValidateIdToken` defaults to `Some true` — client-side id_token
/// validation (signature + iss + aud + exp via WebCrypto) runs on
/// every callback. Defence-in-depth at the customer-facing CIAM
/// boundary where a tampered token's failure mode is materially worse
/// than for an internal OIDC consumer.
let entraExternalId (tenantSubdomain: string) (clientId: string) (redirectUri: string) : OidcUIConfig * PresetMetadata =
    let issuer =
        sprintf "https://%s.ciamlogin.com/%s/v2.0" tenantSubdomain tenantSubdomain

    let cfg: OidcUIConfig = {
        Issuer = issuer
        ClientId = clientId
        RedirectUri = redirectUri
        Scopes = externalIdDefaultScopes
        PostLogoutRedirectUri = None
        ValidateIdToken = Some true
    }

    let meta: PresetMetadata = {
        Name = "entra-external-id"
        IssuerForm = "https://{tenantSubdomain}.ciamlogin.com/{tenantSubdomain}/v2.0"
        AutoAddedScopes = [ "offline_access" ]
        ExpectsDecodableAccessToken = true
        Notes = [
            "Tenant subdomain is the External ID tenant name (left of `.ciamlogin.com`), embedded into both the issuer host AND the v2.0 path segment."
            "`offline_access` is included by default — External ID requires it for refresh-token rotation."
            "ValidateIdToken defaults to `Some true` (client-side signature + iss + aud + exp via WebCrypto). Customer-facing CIAM surface — defence-in-depth at the boundary is materially more valuable than for an internal OIDC consumer."
            "For sign-up / sign-in user-flow policy routing, use the dedicated `EntraExternalIdClient` companion (this preset returns a single OidcUIConfig without the policy-routing surface)."
        ]
    }

    cfg, meta

/// Entra External ID preset with a custom-domain override. Use when
/// the tenant is configured with a custom CIAM domain replacing the
/// default `*.ciamlogin.com` host. The issuer becomes
/// `https://{customDomain}/{tenantSubdomain}/v2.0`; metadata is
/// otherwise identical to `entraExternalId`.
let entraExternalIdWithDomain
    (tenantSubdomain: string)
    (customDomain: string)
    (clientId: string)
    (redirectUri: string)
    : OidcUIConfig * PresetMetadata =
    let cfg, meta = entraExternalId tenantSubdomain clientId redirectUri
    let issuer = sprintf "https://%s/%s/v2.0" customDomain tenantSubdomain

    let metaWithCustomNote = {
        meta with
            IssuerForm = sprintf "https://%s/{tenantSubdomain}/v2.0  (custom-domain override)" customDomain
    }

    { cfg with Issuer = issuer }, metaWithCustomNote

/// Auth0 preset. `domain` is the Auth0 tenant URL host (e.g.
/// `your-tenant.auth0.com` or `your-tenant.eu.auth0.com`). Default
/// scopes include `offline_access` for refresh-token rotation.
///
/// Auth0 access tokens are opaque by default — unless the consumer
/// configures an API audience in the Auth0 dashboard and supplies it
/// via the `audience` extra parameter (handled by
/// `beginSignInWithExtras`), `classifyStoredToken` will see the
/// stored token as `OpaqueToken` and defer validity to the server.
let auth0 (domain: string) (clientId: string) (redirectUri: string) : OidcUIConfig * PresetMetadata =
    let issuer = sprintf "https://%s/" domain

    let cfg: OidcUIConfig = {
        Issuer = issuer
        ClientId = clientId
        RedirectUri = redirectUri
        Scopes = auth0DefaultScopes
        PostLogoutRedirectUri = None
        ValidateIdToken = None
    }

    let meta: PresetMetadata = {
        Name = "auth0"
        IssuerForm = "https://{tenant}.auth0.com/  (or regional variant — *.eu.auth0.com / *.us.auth0.com / ...)"
        AutoAddedScopes = [ "offline_access" ]
        ExpectsDecodableAccessToken = false
        Notes = [
            "Auth0 access tokens are opaque by default. Pass an `audience` extra parameter via `beginSignInWithExtras` to receive a decodable JWT addressed to your configured Auth0 API."
            "`offline_access` is included by default for refresh-token rotation."
            "Issuer trailing slash matters — Auth0 issues `iss` claims WITH the trailing slash; `classifyStoredToken`'s normalisation handles both shapes, but downstream server-side validators may not."
        ]
    }

    cfg, meta