// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcAppConfig

open ToolUp.Platform

// ─── Unified OIDC app config (0.4.0) ────────────────────────────────
//
// `OidcAppConfig` is the one declaration both sides of an OIDC
// deployment project from — client and server read their respective
// fields off a single record so the manual hand-sync class of bug
// (mismatched issuer between client and server, drifted scope list,
// out-of-step audience) is gone by construction.
//
// Replaces the prior 0.3.x pattern where:
//   * Client built an `OidcUIConfig` (in `ToolUp.Platform`) and
//     passed it to `OidcAuthUI`.
//   * Server built an `OidcAuthProviderConfig` and passed it to
//     the Oidc auth-provider factory.
//   * Consumers manually kept the two in agreement (issuer must
//     match exactly; audience must match the client's expected
//     access-token `aud`; ClientId must match across).
//
// At 0.4.0 the consumer writes one `OidcAppConfig`; the SDK projects
// to per-side configs via `toClientConfig` / `toServerConfig`. The
// previously-distinct preset return type `OidcUIConfig *
// PresetMetadata` collapses to a single `OidcAppConfig` carrying its
// own `Preset` provenance field.
//
// `OidcUIConfig` remains in `ToolUp.Platform.Client/Client/SDK.ClientTypes.fs`
// (the client-tier shape Fable code consumes); it is projected from
// `OidcAppConfig` via `toClientConfig` below. Consumers writing the
// app config directly never see `OidcUIConfig` — the SDK handles the
// projection internally.

/// Stable machine-readable identifier for the preset that produced
/// this config (when one did). Used by the coherence validator as a
/// tag for preset-specific findings, and by the auth tracer for
/// per-provider event grouping. Stable — the string surface is the
/// metric-tag wire format downstream consumers key off.
type PresetKind =
    /// Explicit issuer; no provider-specific quirks applied. For IdPs
    /// the SDK doesn't have first-class provider knowledge for.
    | Generic
    /// Workforce Entra ID / Azure AD
    /// (`login.microsoftonline.com/{tenantGuid}/v2.0`). Auto-applies
    /// the `api://{clientId}/access_as_user` scope so Entra issues a
    /// decodable v2 JWT access token.
    | EntraWorkforce
    /// Entra External ID (CIAM, `*.ciamlogin.com`). Auto-applies
    /// `offline_access` for refresh-token rotation.
    | EntraExternalId
    /// Entra External ID with a custom CIAM domain replacing
    /// `*.ciamlogin.com`. Carries the custom domain in the payload
    /// so the coherence validator can render the customised issuer
    /// form in its findings.
    | EntraExternalIdWithDomain of customDomain: string
    /// Auth0 — tenant URL with trailing slash. Access tokens are
    /// opaque by default; a decodable JWT requires an `audience`
    /// extra parameter on the authorize request.
    | Auth0
    /// Google — the fixed issuer `https://accounts.google.com` (no
    /// tenant/domain parameter). Access tokens are ALWAYS opaque —
    /// unlike Auth0 there is no dashboard knob that makes them
    /// decodable. Refresh tokens come from the `access_type=offline`
    /// authorize parameter, not from an `offline_access` scope.
    | Google

module PresetKind =
    /// Short stable label — used as a metric tag, log key, and
    /// validator finding identifier. Pinned by tests so the surface
    /// can't silently rename.
    let label (kind: PresetKind) : string =
        match kind with
        | Generic -> "generic"
        | EntraWorkforce -> "entra-workforce"
        | EntraExternalId -> "entra-external-id"
        | EntraExternalIdWithDomain _ -> "entra-external-id"
        | Auth0 -> "auth0"
        | Google -> "google"

    /// Human-readable description of the expected issuer URL form
    /// for the preset. Rendered by the coherence validator when an
    /// issuer URL doesn't fit the preset's tuned shape.
    let issuerForm (kind: PresetKind) : string =
        match kind with
        | Generic -> "explicit issuer URL — no SDK-side provider quirks applied"
        | EntraWorkforce -> "https://login.microsoftonline.com/{tenantGuid}/v2.0"
        | EntraExternalId -> "https://{tenantSubdomain}.ciamlogin.com/{tenantSubdomain}/v2.0"
        | EntraExternalIdWithDomain customDomain ->
            sprintf "https://%s/{tenantSubdomain}/v2.0  (custom-domain override)" customDomain
        | Auth0 -> "https://{tenant}.auth0.com/  (or regional variant — *.eu.auth0.com / *.us.auth0.com / ...)"
        | Google -> "https://accounts.google.com  (fixed — no tenant or domain parameter)"

    /// Whether the preset expects a decodable JWT access token vs an
    /// opaque token. Affects classifier expectations and the
    /// coherence validator's audience-binding hints.
    let expectsDecodableAccessToken (kind: PresetKind) : bool =
        match kind with
        | Generic -> false
        | EntraWorkforce -> true
        | EntraExternalId -> true
        | EntraExternalIdWithDomain _ -> true
        | Auth0 -> false
        // Google access tokens are opaque, always. Unlike Auth0
        // there is no configured-API-audience knob that flips them
        // to a decodable JWT — so this is a fixed property of the
        // provider, not a deployment choice.
        | Google -> false

    /// The bearer strategy the preset selects when the consumer has
    /// not stated one. `AccessTokenBearer` for every preset but
    /// `Google`.
    ///
    /// **This is deliberately NOT `not (expectsDecodableAccessToken
    /// kind)`, and the difference is the whole point of the helper.**
    /// Three presets answer `false` to that question, for three
    /// different reasons:
    ///
    ///   * `Generic` — the SDK has no provider knowledge, so it cannot
    ///     claim the access token is decodable. The token may well be a
    ///     perfectly good JWT; the SDK simply does not know. Flipping
    ///     these deployments to the id_token would change working
    ///     behaviour on an absence of information.
    ///   * `Auth0` — opaque *by default*, and fixable by configuration:
    ///     set an API audience in the Auth0 dashboard and pass it as
    ///     the `audience` extra parameter, and the access token becomes
    ///     a decodable JWT addressed to that API. The remedy is a
    ///     configuration knob, so the SDK must not pre-empt it.
    ///   * `Google` — opaque *always*, with **no knob that changes
    ///     it**. There is no dashboard setting, no scope, and no
    ///     authorize parameter that makes a Google access token
    ///     decodable. The access-token strategy cannot be made to work
    ///     here by any deployment-side action, which is what
    ///     distinguishes this case from the other two and what makes an
    ///     SDK-chosen default correct rather than presumptuous.
    ///
    /// A consumer's explicit `OidcAppConfig.BearerToken` always wins
    /// over this default — see `OidcAppConfig.resolveBearerToken`.
    let defaultBearerToken (kind: PresetKind) : BearerTokenKind =
        match kind with
        | Generic -> AccessTokenBearer
        | EntraWorkforce -> AccessTokenBearer
        | EntraExternalId -> AccessTokenBearer
        | EntraExternalIdWithDomain _ -> AccessTokenBearer
        | Auth0 -> AccessTokenBearer
        | Google -> IdTokenBearer

    /// Whether the preset's access-token opacity is a fixed property of
    /// the provider rather than a deployment choice. `true` only where
    /// no configuration — dashboard setting, scope, or authorize
    /// parameter — can make the access token decodable, so leaving the
    /// deployment on `AccessTokenBearer` is a guaranteed post-sign-in
    /// 401 rather than a possible one. Read by the coherence validator
    /// to decide whether an access-token strategy is worth warning
    /// about; kept beside `defaultBearerToken` because the two encode
    /// the same fact for two different consumers.
    let opaqueAccessTokenIsUnfixable (kind: PresetKind) : bool =
        match kind with
        | Generic
        | EntraWorkforce
        | EntraExternalId
        | EntraExternalIdWithDomain _
        | Auth0 -> false
        | Google -> true

    /// Scopes the preset auto-adds on top of the OIDC-spec minimum
    /// (`openid profile email`). Some entries depend on `clientId`
    /// (workforce-Entra's `api://{clientId}/access_as_user`), so the
    /// helper is a function of both kind + clientId.  The coherence
    /// validator uses this to detect consumer overrides that
    /// inadvertently dropped a load-bearing scope.
    let autoAddedScopes (kind: PresetKind) (clientId: string) : string list =
        match kind with
        | Generic -> []
        | EntraWorkforce -> [ "offline_access"; sprintf "api://%s/access_as_user" clientId ]
        | EntraExternalId
        | EntraExternalIdWithDomain _ -> [ "offline_access" ]
        | Auth0 -> [ "offline_access" ]
        // Deliberately empty. Google's refresh token rides the
        // `access_type=offline` AUTHORIZE PARAMETER, not a scope —
        // adding `offline_access` here would encode a scope Google
        // ignores and give Rule 10 a false regression to report.
        | Google -> []

    /// Operator-facing hints for the preset.  Each note is a single
    /// self-contained sentence the coherence validator may surface
    /// in its findings, and the future `/dev/inspect` validators
    /// panel renders for the boot-log preset-applied INFO row.
    /// Calling out the load-bearing knobs that consumers most often
    /// miss when adopting the preset.
    let notes (kind: PresetKind) : string list =
        match kind with
        | Generic -> [
            "Scope set is `openid profile email`; if your IdP requires `offline_access` for refresh tokens, add it explicitly."
            "ValidateIdToken defaults to None (off). For customer-facing surfaces consider Some true to defence-in-depth a tampered id_token at the boundary."
          ]
        | EntraWorkforce -> [
            "Tenant identifier MUST be a GUID (or `common` for multi-tenant apps), NOT a tenant domain — Entra rejects access-token audience validation under the domain-form issuer."
            "The `api://{clientId}/access_as_user` scope is load-bearing — without it, Entra mints an opaque token addressed to Microsoft Graph."
            "The app registration's `requestedAccessTokenVersion` MUST be 2 (v2 JWT format)."
          ]
        | EntraExternalId
        | EntraExternalIdWithDomain _ -> [
            "Tenant subdomain is the External ID tenant name (left of `.ciamlogin.com`), embedded into both the issuer host AND the v2.0 path segment."
            "`offline_access` is included by default — External ID requires it for refresh-token rotation."
            "ValidateIdToken defaults to `Some true` (client-side signature + iss + aud + exp via WebCrypto). Customer-facing CIAM surface — defence-in-depth at the boundary is materially more valuable than for an internal OIDC consumer."
            "For the sign-up user flow, pipe the config through `OidcPresets.withEntraSignUpUserFlow <policyId>` — the standard shell then renders a `Sign up` button beside `Sign in`, carrying `p=<policyId>` on its authorize request."
          ]
        | Auth0 -> [
            "Auth0 access tokens are opaque by default. Pass an `audience` extra parameter via `beginSignInWithExtras` to receive a decodable JWT addressed to your configured Auth0 API."
            "`offline_access` is included by default for refresh-token rotation."
            "Issuer trailing slash matters — Auth0 issues `iss` claims WITH the trailing slash; `classifyStoredToken`'s normalisation handles both shapes, but downstream server-side validators may not."
          ]
        | Google -> [
            "Refresh tokens require the `access_type=offline` authorize parameter — passed via `beginSignInWithExtras`, NOT an `offline_access` scope, which Google ignores. Pair it with `prompt=consent`: Google issues a refresh token only on the first consent for a given client/user unless consent is re-prompted, so a re-authorising user otherwise silently gets none."
            "Google access tokens are ALWAYS opaque (never JWTs). Unlike Auth0 there is no dashboard audience knob that flips them to a decodable token, and no deployment-side action changes it — so this preset defaults `BearerToken` to `IdTokenBearer` and the session sends the `id_token`, an ordinary RS256 JWT the server validates against Google's JWKS with `aud` = the client id. Leaving the deployment on `AccessTokenBearer` signs in successfully and then 401s on every API call."
            "Issuer is the fixed `https://accounts.google.com` — no tenant or domain parameter. Restricting sign-in to a Workspace domain rides the `hd` authorize parameter (another `beginSignInWithExtras` extra) and is a hint, not a guarantee: verify the `hd` claim server-side."
            "ValidateIdToken defaults to `Some true` — consumer Google sign-in is a customer-facing boundary, so id_token signature / iss / aud / exp are re-checked on every callback (same argument as the Entra External ID preset)."
          ]

/// Helpers over `OidcSecondaryFlow` — the optional second sign-in
/// affordance (the "Sign up" button beside "Sign in").
///
/// **They live here rather than beside the type** (which has to sit in
/// the client tier, because `OidcUIConfig` carries it): a module-level
/// *value* in `SDK.ClientTypes.fs` triggers that whole file's startup
/// initialisation, which reaches the AG Grid Fable `import` stubs and
/// throws "You've hit dummy code used for Fable bindings" the moment
/// .NET-side code touches it. That would make the reserved-parameter
/// list unreadable from the Expecto runner and from the coherence
/// validator's own tests — i.e. unenforceable exactly where it
/// matters. This file has no such neighbours.
module SecondaryFlow =
    /// The authorize-request parameter names the OIDC client emits for
    /// itself on every flow, primary or secondary. A secondary flow
    /// that repeats one of these produces a DUPLICATED query
    /// parameter, and an issuer's handling of a duplicate is undefined
    /// — so a collision is a configuration defect, not an override
    /// mechanism. Pinned against the client's own parameter builder
    /// (`OidcStateMachine.authorizeParams`) by test, so the two cannot
    /// drift.
    let reservedAuthorizeParams = [
        "response_type"
        "client_id"
        "redirect_uri"
        "scope"
        "state"
        "nonce"
        "code_challenge"
        "code_challenge_method"
    ]

    /// Construct a secondary flow from its button label + the extra
    /// authorize parameters that route it.
    let create (label: string) (extraAuthorizeParams: (string * string) list) : OidcSecondaryFlow = {
        Label = label
        ExtraAuthorizeParams = extraAuthorizeParams
    }

    /// The flow's extra-parameter keys that collide with the reserved
    /// set, in declaration order. Empty for a well-formed flow.
    let collidingParams (flow: OidcSecondaryFlow) : string list =
        flow.ExtraAuthorizeParams
        |> List.map fst
        |> List.filter (fun key -> List.contains key reservedAuthorizeParams)

/// The unified one-declaration OIDC config. Both client and server
/// sides project their needed fields from a single value. New in
/// 0.4.0; replaces the per-side `OidcUIConfig` + `OidcAuthProviderConfig`
/// hand-sync pattern.
///
/// Construct via an `OidcPresets.*` smart constructor when targeting
/// a known provider (workforce-Entra / External ID / Auth0) so the
/// SDK applies the load-bearing quirks for you. Fall through to
/// direct record construction for generic / custom IdPs.
type OidcAppConfig = {
    /// OIDC issuer URL — base used for metadata discovery at
    /// `{issuer}/.well-known/openid-configuration`.
    Issuer: string

    /// Access-token audience the server-side validator binds against.
    /// Defaults to `ClientId` for most providers (the access token's
    /// `aud` claim equals the client id), but differs for Auth0
    /// (`audience` is the configured API identifier set in the Auth0
    /// dashboard) and for workforce-Entra deployments that present
    /// an API audience separately from the registered client id.
    Audience: string

    /// OIDC client id registered at the issuer for this application.
    ClientId: string

    /// Scopes requested at the authorize endpoint. Includes
    /// preset-auto-added entries (e.g. `api://{clientId}/access_as_user`
    /// for `EntraWorkforce`, `offline_access` for `EntraExternalId`
    /// and `Auth0`).
    Scopes: string list

    /// URL the issuer redirects to after sign-in. Must match a
    /// redirect URI registered at the issuer.
    RedirectUri: string

    /// Post-sign-out redirect URL, passed as `post_logout_redirect_uri`
    /// to the issuer's end-session endpoint. `None` falls back to the
    /// app origin at sign-out time.
    PostLogoutRedirectUri: string option

    /// Opt-in to client-side `id_token` validation at the callback
    /// boundary (Phase 3b.A — signature + issuer + audience + expiry).
    /// `None` resolves to `false` (the generic-IdP-safe default);
    /// preset constructors flip the default to `Some true` for
    /// customer-facing surfaces (`EntraExternalId`).
    ValidateIdToken: bool option

    /// Provenance — `Some kind` when constructed via an
    /// `OidcPresets.*` smart constructor; `None` when assembled by
    /// hand. The coherence validator (and the auth tracer's
    /// per-provider grouping) keys off this field.
    Preset: PresetKind option

    /// Which token this deployment sends as its HTTP bearer. `None`
    /// (the default) defers to the preset's own answer via
    /// `PresetKind.defaultBearerToken`, and to `AccessTokenBearer` when
    /// there is no preset — so a hand-built config is byte-for-byte
    /// today's behaviour (GP 11) and a preset the SDK has first-class
    /// knowledge of picks the strategy that actually works for its
    /// provider without consumer ceremony.
    ///
    /// Set explicitly to override the preset: `Some AccessTokenBearer`
    /// on a Google config (a deployment that validates Google's opaque
    /// token by some other means) or `Some IdTokenBearer` on a generic
    /// config (an IdP the SDK has no preset for whose access tokens are
    /// opaque) are both honoured verbatim. See
    /// `OidcAppConfig.resolveBearerToken`.
    BearerToken: BearerTokenKind option

    /// An optional SECOND sign-in affordance — a button rendered
    /// beside "Sign in" that starts the same OIDC sign-in with extra
    /// authorize-request parameters. The generic form of the
    /// dual-button "Sign in / Sign up" shell: same client id, same
    /// redirect URI, same PKCE / state / nonce machinery, same
    /// callback, same token path — only the authorize request differs.
    ///
    /// `None` (the default on every preset and on
    /// `OidcAppConfig.create`) renders today's single-button shell
    /// byte for byte (GP 11).
    ///
    /// Attach one with `OidcPresets.withSecondaryFlow`, or with the
    /// provider-specific `OidcPresets.withEntraSignUpUserFlow` for an
    /// Entra External ID sign-up user flow.
    SecondaryFlow: OidcSecondaryFlow option

    /// Phase 755 — knobs for the client companion's automatic
    /// pre-expiry refresh timer: the safety margin ahead of `exp`, the
    /// cadence used when the bearer carries no readable `exp`, whether
    /// a woken background tab re-checks, and an outright opt-out.
    ///
    /// `None` (the default on every preset and on
    /// `OidcAppConfig.create`) arms the timer with the margins it
    /// shipped with, byte for byte (GP 11) — the timer is ALWAYS-ON by
    /// default, because a shell that quietly lets its bearer lapse is
    /// the worse default.
    ///
    /// Attach one with `OidcPresets.withRefreshPolicy`, or the two
    /// single-knob helpers `OidcPresets.withRefreshMargin` /
    /// `OidcPresets.withoutAutoRefresh`.
    RefreshPolicy: OidcRefreshPolicy option
}

module OidcAppConfig =
    /// Minimal manual constructor — for IdPs the SDK doesn't have a
    /// preset for. Audience defaults to ClientId; consumers using a
    /// different audience (Auth0 API identifier, custom workforce
    /// API audience) override post-construction with a record update.
    let create (issuer: string) (clientId: string) (redirectUri: string) : OidcAppConfig = {
        Issuer = issuer
        Audience = clientId
        ClientId = clientId
        Scopes = [ "openid"; "profile"; "email" ]
        RedirectUri = redirectUri
        PostLogoutRedirectUri = None
        ValidateIdToken = None
        Preset = None
        BearerToken = None
        SecondaryFlow = None
        RefreshPolicy = None
    }

    /// The effective bearer strategy for a config: the consumer's
    /// explicit choice if they made one, else the preset's default if
    /// there is a preset, else `AccessTokenBearer`.
    ///
    /// The precedence is the load-bearing part. A consumer who states a
    /// strategy is stating it about their own deployment, and knows
    /// something the preset cannot — that they front Google behind a
    /// token-exchange proxy, say. The preset's answer is a default, not
    /// a policy.
    let resolveBearerToken (cfg: OidcAppConfig) : BearerTokenKind =
        match cfg.BearerToken with
        | Some explicitChoice -> explicitChoice
        | None ->
            match cfg.Preset with
            | Some kind -> PresetKind.defaultBearerToken kind
            | None -> AccessTokenBearer

    /// Project to the client-tier `OidcUIConfig` shape consumed by
    /// `OidcAuthUI.OidcShell` and the `OidcClient` orchestration.
    /// Used internally by the AuthUIProvider wiring; consumers
    /// writing `OidcAppConfig` never call this directly.
    ///
    /// The bearer strategy is **resolved** here rather than passed
    /// through: the client tier reads a decided value, so the
    /// preset-default rule lives in exactly one place and the browser
    /// orchestration never has to know what a `PresetKind` is.
    ///
    /// The secondary flow, by contrast, is projected VERBATIM — there
    /// is nothing to resolve. No preset supplies one by default (a
    /// second button is a product decision, not a provider quirk), so
    /// `None` here means exactly what `None` meant on the app config:
    /// the single-button shell.
    ///
    /// The refresh policy is projected VERBATIM for the same reason,
    /// and deliberately NOT resolved here: `OidcRefreshPolicy.resolve`
    /// runs at the point of use in the browser, so the `None`-means-
    /// shipped-defaults guarantee is stated once, at the tier where the
    /// timer actually runs, rather than baked into a projection whose
    /// output a consumer never reads.
    let toClientConfig (cfg: OidcAppConfig) : OidcUIConfig = {
        Issuer = cfg.Issuer
        ClientId = cfg.ClientId
        RedirectUri = cfg.RedirectUri
        Scopes = cfg.Scopes
        PostLogoutRedirectUri = cfg.PostLogoutRedirectUri
        ValidateIdToken = cfg.ValidateIdToken
        BearerToken = Some(resolveBearerToken cfg)
        SecondaryFlow = cfg.SecondaryFlow
        RefreshPolicy = cfg.RefreshPolicy
    }