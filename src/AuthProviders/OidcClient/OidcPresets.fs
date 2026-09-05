// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcPresets

open ToolUp.Platform
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
//
//   google            — Google (consumer accounts + Workspace).
//                       Fixed issuer `https://accounts.google.com`
//                       — no tenant parameter to get wrong. Scope
//                       set is the OIDC-spec minimum: Google's
//                       refresh token comes from the
//                       `access_type=offline` AUTHORIZE PARAMETER,
//                       not from an `offline_access` scope, so the
//                       preset documents the extras rather than
//                       auto-adding a scope Google ignores. Access
//                       tokens are always opaque.

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

/// Google's issuer is a fixed constant — there is no tenant,
/// subdomain, or region variant to parameterise, which is why
/// `google` takes only `clientId` + `redirectUri`.
let private googleIssuer = "https://accounts.google.com"

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
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
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
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
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
/// For the sign-up user flow, pipe the result through
/// `withEntraSignUpUserFlow` — the standard `OidcAuthUI` shell then
/// renders the dual-button "Sign in / Sign up" screen, with the
/// sign-up button carrying `p=<policyId>` on its authorize request.
/// The dedicated client-side Entra companion (removed at 0.23.0) is
/// no longer needed for that.
let entraExternalId (tenantSubdomain: string) (clientId: string) (redirectUri: string) : OidcAppConfig = {
    Issuer = sprintf "https://%s.ciamlogin.com/%s/v2.0" tenantSubdomain tenantSubdomain
    Audience = clientId
    ClientId = clientId
    Scopes = externalIdDefaultScopes
    RedirectUri = redirectUri
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    Preset = Some EntraExternalId
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
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
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
}

/// Google preset (consumer Google accounts and Workspace). Takes no
/// tenant parameter — the issuer is the fixed constant
/// `https://accounts.google.com`, so the whole class of
/// wrong-issuer misconfiguration the Entra presets guard against
/// cannot arise here. Scope set is the OIDC-spec minimum
/// (`openid profile email`).
///
/// Two Google-specific facts the preset encodes as knowledge rather
/// than as behaviour:
///
/// 1. **Refresh tokens are an authorize-parameter concern, not a
///    scope.** Google ignores `offline_access`; a refresh token is
///    issued only when the authorize request carries
///    `access_type=offline`, and — because Google returns one only
///    on a user's FIRST consent for a given client — usually also
///    `prompt=consent`, so a re-authorising user is not silently
///    left without one. Both ride `beginSignInWithExtras`, following
///    the Auth0 `audience` precedent: the SDK documents the extras,
///    it does not inject them, because whether an app wants offline
///    access is the consumer's decision and consent-re-prompting is
///    a user-visible one.
///
/// 2. **Access tokens are always opaque, so the preset selects the
///    id_token as the bearer.** Unlike Auth0 — where a
///    dashboard-configured API audience flips the access token to a
///    decodable JWT — Google has no such knob, so
///    `PresetKind.expectsDecodableAccessToken Google` is `false` and
///    no deployment-side action can change it. Sending the access
///    token as the bearer therefore signs in and then 401s on every
///    API call, which is why this preset's
///    `PresetKind.defaultBearerToken` is `IdTokenBearer`: the session
///    stores and sends the `id_token`, an ordinary RS256 JWT the
///    unchanged server-side `OidcAuthProvider` validates against
///    Google's JWKS with `aud` = the client id (which is what
///    `Audience` already holds). `classifyStoredToken` then reports
///    `FreshJwt` rather than `OpaqueToken`, and the pre-expiry refresh
///    timer keys off the id_token's own `exp`. A consumer who wants
///    the historical behaviour sets `BearerToken = Some
///    AccessTokenBearer` explicitly.
///
/// `ValidateIdToken` defaults to `Some true` — Google sign-in is a
/// customer-facing boundary, the same argument that flips the
/// default on `entraExternalId`.
let google (clientId: string) (redirectUri: string) : OidcAppConfig = {
    Issuer = googleIssuer
    Audience = clientId
    ClientId = clientId
    Scopes = genericDefaultScopes
    RedirectUri = redirectUri
    PostLogoutRedirectUri = None
    ValidateIdToken = Some true
    Preset = Some Google
    BearerToken = None
    SecondaryFlow = None
    RefreshPolicy = None
}

// ─── Secondary-flow attachments ──────────────────────────────────────
//
// A secondary flow is a SECOND button on the sign-in screen that
// starts the same OIDC sign-in with extra authorize-request
// parameters — the generic form of the dual-button
// "Sign in / Sign up" shell. These helpers attach one to an
// already-built config, so the preset constructors above stay
// single-purpose and every one of them keeps returning
// `SecondaryFlow = None` (GP 11).
//
// They are `cfg`-last so they compose in a pipeline:
//
//     OidcPresets.entraExternalId tenant clientId redirect
//     |> OidcPresets.withEntraSignUpUserFlow "B2C_1_signup"

/// Attach an arbitrary secondary flow: the button's label plus the
/// extra parameters its authorize request carries. Vendor-neutral —
/// the SDK never interprets the parameters.
///
/// Keys must not collide with the standard OAuth/PKCE set the client
/// emits itself (`SecondaryFlow.reservedAuthorizeParams`); the
/// coherence validator's rule 16 refuses a config that does, because a
/// duplicated authorize parameter has undefined issuer behaviour and
/// is invisible until a user presses the button.
///
/// Example — a Google deployment offering explicit re-consent so a
/// returning user can be re-issued a refresh token:
///
///     OidcPresets.google clientId redirectUri
///     |> OidcPresets.withSecondaryFlow "Re-consent" [ "prompt", "consent" ]
let withSecondaryFlow
    (label: string)
    (extraAuthorizeParams: (string * string) list)
    (cfg: OidcAppConfig)
    : OidcAppConfig =
    {
        cfg with
            SecondaryFlow = Some(SecondaryFlow.create label extraAuthorizeParams)
    }

/// The authorize-request parameter Entra External ID (and Azure AD
/// B2C before it) routes user flows on. Named here rather than spelled
/// inline so the parity test against the removed Entra client
/// companion's shell — which passed `[ "p", policyId ]` to
/// `beginSignInWithExtras` — has one value to pin.
[<Literal>]
let EntraUserFlowParameter = "p"

/// Attach the Entra External ID **sign-up user flow** as the sign-in
/// screen's secondary affordance: a "Sign up" button beside "Sign in"
/// that routes through the named user-flow policy.
///
/// This is the preset-path replacement for the removed Entra client
/// companion's dual-button shell. The parameter shape is ported from
/// that shell verbatim (`p=<policyId>` alongside the full OAuth / PKCE
/// param set), so the authorize request the sign-up button issues is
/// the one the companion issued — same redirect URI, same callback,
/// same nonce binding back to *this* attempt.
///
///     let cfg =
///         OidcPresets.entraExternalId "contoso" clientId redirectUri
///         |> OidcPresets.withEntraSignUpUserFlow "B2C_1_signup"
let withEntraSignUpUserFlow (policyId: string) (cfg: OidcAppConfig) : OidcAppConfig =
    withSecondaryFlow "Sign up" [ EntraUserFlowParameter, policyId ] cfg

// ─── Refresh-policy attachments (Phase 755) ──────────────────────────
//
// The pre-expiry refresh timer is ALWAYS-ON: a shell that quietly lets
// its bearer lapse is the worse default, so every preset above returns
// `RefreshPolicy = None` and `None` means "the built-in margins"
// (GP 11 — byte for byte what the timer shipped with).
//
// These helpers attach a policy to an already-built config, in the
// same `cfg`-last pipeline shape as the secondary-flow attachments
// above, so the preset constructors stay single-purpose:
//
//     OidcPresets.google clientId redirectUri
//     |> OidcPresets.withRefreshMargin 120.0

/// Attach an explicit refresh policy. Build one from
/// `OidcRefreshPolicy.none ()` and a record update so a future knob
/// does not break the call:
///
///     OidcPresets.auth0 domain clientId redirectUri
///     |> OidcPresets.withRefreshPolicy
///         { OidcRefreshPolicy.none () with
///             SafetyMarginSeconds = Some 120.0
///             FallbackSeconds = Some 600.0 }
let withRefreshPolicy (policy: OidcRefreshPolicy) (cfg: OidcAppConfig) : OidcAppConfig = {
    cfg with
        RefreshPolicy = Some policy
}

/// Move the refresh ahead of `exp` by `seconds` instead of the
/// built-in 60. Every other knob keeps its default.
///
/// Raise it for an issuer whose token endpoint is slow or aggressively
/// rate-limited — the refresh has to complete before `exp`, not merely
/// start before it.
let withRefreshMargin (seconds: float) (cfg: OidcAppConfig) : OidcAppConfig =
    let existing = cfg.RefreshPolicy |> Option.defaultWith OidcRefreshPolicy.none

    {
        cfg with
            RefreshPolicy =
                Some {
                    existing with
                        SafetyMarginSeconds = Some seconds
                }
    }

/// Set the cadence used when the bearer carries no readable `exp` —
/// an opaque access token, or an encrypted-payload JWT — instead of
/// the built-in 300 s.
///
/// This is the knob that matters for opaque-token providers (Google
/// always; Auth0 without an `audience` parameter), where the client
/// cannot read a lifetime off the token and has to pick one.
let withRefreshFallback (seconds: float) (cfg: OidcAppConfig) : OidcAppConfig =
    let existing = cfg.RefreshPolicy |> Option.defaultWith OidcRefreshPolicy.none

    {
        cfg with
            RefreshPolicy =
                Some {
                    existing with
                        FallbackSeconds = Some seconds
                }
    }

/// Turn the background refresh timer OFF for this deployment.
///
/// The deliberate opt-out, for an app that renews the bearer by some
/// other means — a host driving `OidcClient.refreshAccessToken`
/// itself, or a session cookie the SDK never sees. It is spelled as
/// its own function rather than left to a record update so that
/// disabling the timer reads as a decision in the config pipeline,
/// where a reviewer will see it.
let withoutAutoRefresh (cfg: OidcAppConfig) : OidcAppConfig =
    let existing = cfg.RefreshPolicy |> Option.defaultWith OidcRefreshPolicy.none

    {
        cfg with
            RefreshPolicy = Some { existing with Enabled = Some false }
    }

/// Stop a woken background tab from re-checking expiry immediately.
///
/// The wake path exists because browsers throttle timers in background
/// tabs, so a tab left in the background otherwise wakes with an
/// already-expired bearer. Turn it off only for an issuer whose token
/// endpoint cannot absorb a check per tab-focus; the armed timer still
/// fires (late), so this trades promptness for request volume rather
/// than disabling refresh.
let withoutRefreshOnWake (cfg: OidcAppConfig) : OidcAppConfig =
    let existing = cfg.RefreshPolicy |> Option.defaultWith OidcRefreshPolicy.none

    {
        cfg with
            RefreshPolicy =
                Some {
                    existing with
                        RefreshOnWake = Some false
                }
    }