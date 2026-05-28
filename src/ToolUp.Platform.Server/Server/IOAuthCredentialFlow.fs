namespace ToolUp.Platform

open System

// ─── OAuth Authorization Code substrate (Phase 10b) ──────────────
//
// Server-side substrate for connectors whose credentials are minted
// by an upstream Authorization Code with offline-access flow —
// Google (GA4, Sheets, Drive), Microsoft (Graph, Azure AD), GitHub,
// Slack, Stripe, HubSpot, Salesforce, Dropbox, etc. The substrate is
// provider-agnostic; one `IOAuthCredentialFlow` implementation per
// upstream provider lives in a sibling companion package
// (`src/DataSources/<Provider>/`, `src/AuthFlows/<Provider>/`) and is
// injected via DI at compose time.
//
// **Why this is platform infrastructure, not connector-specific.**
// The HTTP plumbing — generating CSRF state, persisting it for callback
// correlation, exposing /authorize and /callback endpoints, persisting
// the resulting refresh token in `ISecretStore` keyed by data-source id,
// and emitting audit events — is identical across providers. Hand-rolling
// it per connector would duplicate state-store logic, audit emission,
// and RBAC gating. Keeping it in core means a new SaaS connector
// implements two interfaces (`IDataSource` + `IOAuthCredentialFlow`)
// and the wiring is automatic.
//
// **Statelessness.** Implementations are stateless across calls.
// All inputs come through the per-call context records; provider-
// specific configuration (tenant id, region, etc.) is read from the
// `DataSourceConfig.ConnectionScope` map, and credentials are read
// from `ISecretStore` per call (mirrors the AI provider thunk
// pattern; supports rotation without provider reconstruction).
// Phase 9c rule 4.
//
// **Identity by value.** `Name` is the deployment-unique flow name
// (`"google-analytics"`, `"microsoft-graph"`). The substrate keys
// state-store entries and HTTP route segments by it. Stable across
// the deployment lifetime — renaming a flow strands any in-flight
// state tokens (acceptable: TTL is 10 minutes). Phase 9c rule 1.
//
// **Async at every boundary.** Every method returns `Async<Result<_, _>>`.
// HTTP calls to upstream token endpoints, ISecretStore reads, and
// state-store writes all participate. Phase 9c rule 2.
//
// **Errors as data.** `OAuthError` discriminates so the substrate
// handler can map each case to the right HTTP status + audit-event
// kind without introspecting strings. Phase 9c rule 3.

/// Display metadata for the admin-UI per-flow plugin. The `Name`
/// flow identifier is the dispatch key; this record is purely for
/// the user-facing label, scope description, and per-provider help
/// link.
type OAuthFlowDescriptor = {
    /// Human-readable provider name shown in the admin UI ("Google
    /// Analytics", "Microsoft Graph"). Distinct from `Name` which is
    /// the URL-segment identifier.
    DisplayName: string
    /// Comma-separated list of upstream OAuth scopes the flow will
    /// request. Surfaces in the admin UI so the user knows what they
    /// are about to consent to. Authoritative for the `scope`
    /// parameter in `BuildAuthorizeUrl`'s URL.
    Scopes: string list
    /// Optional URL pointing at the provider's setup documentation
    /// (e.g. the Google Cloud Console redirect-URI registration page).
    /// Admin UI renders as a "Setup help" link.
    HelpUrl: string option
}

/// Per-call context handed to every flow method. Carries the caller's
/// scope (so flows can resolve `secretStore.GetSecret(ScopeId, key)`
/// for ClientId / ClientSecret) and the persisted `DataSourceConfig`
/// for connector-specific settings (`ConnectionScope` map). The
/// substrate populates this from the request's `AccessContext` plus
/// `IDataSourceConfigStore.Get`.
type OAuthFlowContext = {
    /// Caller-resolved storage scope (team-scope id in Team /
    /// MultiTeam mode, user id otherwise). Flows use it as the scope
    /// argument to `ISecretStore.GetSecret` / `SetSecret`.
    ScopeId: string
    /// The data source the flow is operating on. Used as the cursor
    /// key for state-store correlation and as the secret-key suffix
    /// for the persisted refresh token (`{flowName}-refresh-{DataSourceId}`).
    DataSourceId: DataSourceId
    /// Persisted `DataSourceConfig` (read-only). Flows read connector-
    /// specific settings from `Config.ConnectionScope` — Microsoft
    /// providers read `tenant_id`, Salesforce reads `instance_url`,
    /// etc. `None` when the data source is mid-creation and config has
    /// not yet been saved (admin UI calls `BuildAuthorizeUrl` before
    /// persisting).
    Config: DataSourceConfig option
}

/// Credentials minted by `ExchangeCode`. The substrate writes
/// `RefreshToken` to `ISecretStore.SetSecret(scope, "{flowName}-refresh-{dataSourceId}")`
/// and discards the access token (connectors mint a fresh one per call
/// via `RefreshAccessToken`). The optional `AccessToken` /
/// `ExpiresAt` fields are surfaced so flows that mint a longer-lived
/// session (Microsoft Graph) can short-circuit the first refresh
/// round-trip.
type OAuthCredentials = {
    /// Long-lived refresh token. Persisted; survives deployment
    /// restart. Required.
    RefreshToken: string
    /// Optional short-lived access token. Connectors typically don't
    /// store it — `RefreshAccessToken` mints a fresh one per call.
    AccessToken: string option
    /// UTC expiry of `AccessToken`. Connectors use this to decide
    /// when to refresh (a few minutes before expiry).
    ExpiresAt: DateTime option
    /// Optional ID token (OpenID Connect). Captured for providers
    /// that mint one alongside the access token (Google) so admin
    /// UIs can show the connected user's email without an extra
    /// /userinfo call.
    IdToken: string option
}

/// Short-lived access token + expiry minted by `RefreshAccessToken`.
/// Connectors hold this in memory for the duration of a single API
/// call; it is never persisted by the substrate.
type OAuthAccessToken = {
    Token: string
    /// UTC time at which the token will be rejected by the upstream.
    /// Connectors must mint a new token a safety-margin before this.
    ExpiresAt: DateTime
}

/// Typed errors returned by every `IOAuthCredentialFlow` method. The
/// substrate maps cases to HTTP status codes (`StateMismatch` →
/// 400, `ProviderRejected` → 502, `NetworkError` → 503), and to
/// audit-event kinds (`OAuthRefreshFailed` audit case carries the
/// case discriminator name + message).
type OAuthError =
    /// `ISecretStore.GetSecret` returned `None` for the configured
    /// `ClientId` / `ClientSecret` key. Operator action: re-enter
    /// credentials in the admin UI.
    | ClientCredentialMissing of key: string
    /// CSRF state token did not match the persisted entry, or the
    /// entry expired before the callback fired. Surface as 400 to
    /// the user-agent without leaking which case.
    | StateMismatch of message: string
    /// Upstream provider rejected the authorization code or refresh
    /// token. Inner message is the provider's own diagnostic
    /// (`invalid_grant`, `invalid_client`, etc.) — admin UI shows it
    /// verbatim because operators understand provider error codes
    /// faster than translated text.
    | ProviderRejected of message: string
    /// Network failure reaching the upstream token endpoint.
    /// Indicates infrastructure trouble, not a credential problem.
    | NetworkError of message: string
    /// Provider does not expose a revocation endpoint — `Revoke`
    /// returns this so the substrate falls back to local secret
    /// deletion only. Not surfaced to the user; logged at Info.
    | RevocationUnsupported
    /// Catch-all for unexpected failures (malformed JSON in token
    /// response, etc.). Wraps the inner message. Named distinctly
    /// from `IngestionError.UnexpectedFailure` so F# type inference
    /// in shared compile contexts disambiguates without explicit
    /// type qualification.
    | OAuthFlowFailed of message: string

module OAuthError =
    /// Render an `OAuthError` for log lines and admin-UI display.
    /// Never includes secret material — implementations must not put
    /// access tokens, refresh tokens, or client secrets in error
    /// messages.
    let toMessage (err: OAuthError) : string =
        match err with
        | ClientCredentialMissing key -> $"OAuth client credential missing: {key}"
        | StateMismatch msg -> $"OAuth state mismatch: {msg}"
        | ProviderRejected msg -> $"OAuth provider rejected: {msg}"
        | NetworkError msg -> $"OAuth network error: {msg}"
        | RevocationUnsupported -> "OAuth revocation unsupported by provider"
        | OAuthFlowFailed msg -> $"OAuth flow failed: {msg}"

type IOAuthCredentialFlow =
    /// Stable name discriminator. Used as the `{flowName}` segment in
    /// `/api/oauth/{flowName}/authorize` + `/api/oauth/{flowName}/callback`,
    /// as the cursor key prefix for state-store correlation, and as
    /// the secret-key prefix for the persisted refresh token. Stable
    /// across the deployment lifetime — renaming a flow invalidates
    /// stored refresh tokens. Convention: kebab-case, e.g.
    /// `"google-analytics"`, `"microsoft-graph"`, `"github"`.
    abstract Name: string

    /// Display metadata for admin-UI per-flow plugin rendering.
    abstract Descriptor: OAuthFlowDescriptor

    /// Build the upstream provider's authorize URL for a fresh
    /// consent request. Implementations:
    ///   1. Read `ClientId` from `ISecretStore.GetSecret(ctx.ScopeId, "{flowName}-client-id-{ctx.DataSourceId}")`
    ///   2. Construct the provider's authorize endpoint URL with
    ///      `client_id`, `redirect_uri = redirectUri`, `state = state`,
    ///      `scope = Descriptor.Scopes`, plus provider-specific
    ///      params (Google: `access_type=offline`, `prompt=consent`).
    ///   3. Return the absolute URL the substrate will redirect to.
    /// The substrate generates `state` (CSRF token) and persists it in
    /// the state store before calling this method; flows MUST forward
    /// `state` unchanged.
    abstract BuildAuthorizeUrl:
        ctx: OAuthFlowContext * state: string * redirectUri: string -> Async<Result<string, OAuthError>>

    /// Exchange an authorization code for credentials. Called by the
    /// substrate's callback handler after CSRF state validation has
    /// passed. Implementations:
    ///   1. Read `ClientId` + `ClientSecret` from `ISecretStore` per
    ///      the `{flowName}-client-{id|secret}-{ctx.DataSourceId}`
    ///      naming convention.
    ///   2. POST to the upstream token endpoint with `code`,
    ///      `redirect_uri = redirectUri` (must match the URI from the
    ///      authorize step exactly — providers validate strict equality),
    ///      `client_id`, `client_secret`, `grant_type=authorization_code`.
    ///   3. Return `OAuthCredentials` with at least `RefreshToken` set;
    ///      `AccessToken` / `ExpiresAt` / `IdToken` are best-effort.
    abstract ExchangeCode:
        ctx: OAuthFlowContext * code: string * redirectUri: string -> Async<Result<OAuthCredentials, OAuthError>>

    /// Mint an access token from a stored refresh token. Connectors
    /// call this on every API call that needs fresh credentials
    /// (mirrors the AI provider thunk pattern). Implementations:
    ///   1. Read `ClientId` + `ClientSecret` from `ISecretStore`.
    ///   2. POST to the token endpoint with
    ///      `refresh_token = refreshToken`, `grant_type=refresh_token`,
    ///      `client_id`, `client_secret`.
    ///   3. Return `OAuthAccessToken` with the new short-lived token
    ///      and its expiry.
    /// Returns `ProviderRejected "invalid_grant"` (or equivalent)
    /// when the refresh token has been revoked upstream — the
    /// substrate maps this to `CredentialStatus.NeedsReauthorization`
    /// and emits an `OAuthRefreshFailed` audit event.
    abstract RefreshAccessToken:
        ctx: OAuthFlowContext * refreshToken: string -> Async<Result<OAuthAccessToken, OAuthError>>

    /// Best-effort upstream revocation. Called when the user clicks
    /// "Disconnect" in the admin UI. Implementations:
    ///   1. Read `ClientId` + `ClientSecret` (where the provider's
    ///      revocation endpoint authenticates the client).
    ///   2. POST to the provider's revocation endpoint with the
    ///      refresh token.
    ///   3. Return `Ok ()` on 200/204, or `RevocationUnsupported` if
    ///      the provider has no revocation endpoint.
    /// The substrate deletes the local refresh token regardless of
    /// this method's result — `Revoke` is a courtesy to the upstream.
    abstract Revoke: ctx: OAuthFlowContext * refreshToken: string -> Async<Result<unit, OAuthError>>