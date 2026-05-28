// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── OAuth token refresh substrate (Phase 10h) ───────────────────────
//
// Shared types for the generic OAuth 2.0 Authorization Code refresh-
// token lifecycle substrate. Sits one layer above
// [Phase 10e](`IOAuthCredentialFlow`): 10e shipped the *acquisition*
// path (Authorization Code → refresh token); 10h ships the *runtime*
// refresh lifecycle on top — scheduling refreshes ahead of expiry,
// rotating tokens through `ISecretStore`, emitting per-attempt audit
// events, dead-lettering on permanent failure. Connectors stop hand-
// rolling expiry-detection + retry-timing + secret-persistence per
// `Kind`; they declare a descriptor at `Connect` and the substrate
// handles the rest.
//
// **Six-rule portability audit (Guiding Principle 12 / Phase 9c).**
//
//   1. Identity by value      — `OAuthRefreshDescriptor` keys by
//                                `(Provider, ConfigId)` strings;
//                                `OAuthRefreshResult` is a DU of
//                                strings + `DateTimeOffset`. No live
//                                handles, no `IActorRef`-shaped
//                                identity.
//   2. Async at boundary      — `IOAuthTokenRefresher` (sibling file
//                                in the Server tier) returns `Async`
//                                at every method.
//   3. Retry as data          — `OAuthRefreshResult` discriminates
//                                `TransientError` (retry-eligible)
//                                from `PermanentError` (dead-letter).
//                                Callers branch on the tag; no
//                                `OnFailure` callback escapes the
//                                contract.
//   4. Stateless handler      — `OAuthRefreshJobHandler` receives the
//                                descriptor identity in `JobContext.Payload`
//                                and resolves the descriptor + secrets
//                                from DI / `ISecretStore` on every
//                                dispatch attempt. No in-memory state
//                                survives across calls; an Orleans
//                                deactivation or Akka.Persistence
//                                restart is invisible to the contract.
//   5. No cross-shard ordering — refresh ordering is per-descriptor.
//                                Two refreshes for different
//                                `(Provider, ConfigId)` pairs may
//                                resume in any order; no cross-key
//                                ordering is promised.
//   6. Precision at lower bound — refresh jobs run under
//                                `JobPrecision.Minute` (the
//                                in-process scheduler's floor).
//                                `ScheduleAheadOfExpiry` defaults to
//                                5 minutes — generous enough to
//                                absorb scheduler tick latency plus
//                                a transient retry before expiry.

/// Stable descriptor pointing the refresher at where to fetch new
/// tokens. Identity is `(Provider, ConfigId)` — equivalent records
/// constructed at different sites map to the same descriptor.
///
/// **Secrets are referenced by key, never embedded.** The descriptor
/// crosses the wire (admin UI, audit log, persisted job payload) as
/// data; anything sensitive (client secret, refresh token, access
/// token) lives behind an `ISecretStore` key. The refresher reads
/// secrets per dispatch attempt so secret rotation flows through
/// without re-registration.
///
/// **Access-token persistence.** Phase 10h extends the Phase 10e
/// persistence model: 10e persisted only the refresh token (callers
/// minted a fresh access token per API call via `RefreshAccessToken`);
/// 10h additionally persists the *access token* + its expiry under
/// keys derived from `RefreshTokenKey` (`{RefreshTokenKey}.access` /
/// `{RefreshTokenKey}.expires-at`). Consumers read the cached access
/// token directly from `ISecretStore` until expiry, eliminating the
/// per-call refresh round-trip.
type OAuthRefreshDescriptor = {
    /// Stable provider name matching the connector's
    /// `IOAuthCredentialFlow.Name` ("google-analytics", "salesforce",
    /// "hubspot", etc.). Per-provider audit tag + metrics tag.
    /// Never embeds secret material.
    Provider: string
    /// Per-instance identifier within the provider — typically the
    /// connector's `DataSourceId`. Distinguishes multiple connections
    /// to the same provider (e.g. two GA4 accounts under one team).
    ConfigId: string
    /// Storage scope the descriptor lives under (typically a team
    /// scope id or the reserved `_platform` scope). Pinned at
    /// registration time so cron-scheduled refreshes that run with no
    /// user online still resolve the right scope when reading and
    /// writing secrets via `ISecretStore`.
    ScopeId: string
    /// Upstream OAuth 2.0 token-refresh endpoint. The substrate POSTs
    /// `grant_type=refresh_token` here when invoking `RefreshNow`.
    TokenEndpoint: string
    /// OAuth `client_id` registered at the upstream. Plaintext — by
    /// the OAuth spec the `client_id` is the application's public
    /// identifier (equivalent to a username), not a secret.
    ClientId: string
    /// `ISecretStore` key under which the OAuth `client_secret` is
    /// stored. The refresher reads it via
    /// `ISecretStore.GetSecret(ScopeId, ClientSecretKey)` on every
    /// dispatch.
    ClientSecretKey: string
    /// `ISecretStore` key under which the long-lived refresh token is
    /// stored. The refresher reads it via
    /// `ISecretStore.GetSecret(ScopeId, RefreshTokenKey)` before
    /// every dispatch; on `Refreshed` the rotated refresh token (if
    /// the upstream rotated it) is written back under the same key.
    /// Access-token cache + expiry are persisted under derived keys
    /// — see the type-level comment.
    RefreshTokenKey: string
    /// Lead time before the access-token expiry to fire the refresh
    /// job. The refresher schedules the dispatch at
    /// `Expiry - ScheduleAheadOfExpiry`. Default 5 min via
    /// `OAuthRefreshDescriptor.withDefaults` — generous enough to
    /// absorb the `JobPrecision.Minute` tick latency plus one
    /// transient retry before expiry.
    ScheduleAheadOfExpiry: TimeSpan
}

module OAuthRefreshDescriptor =
    /// Default lead time (5 minutes) used by `withDefaults`. Tuned to
    /// the `JobPrecision.Minute` floor of the in-process scheduler
    /// (worst-case tick latency + a transient retry inside the
    /// expiry window).
    let DefaultScheduleAheadOfExpiry: TimeSpan = TimeSpan.FromMinutes 5.0

    /// Construct a descriptor with `ScheduleAheadOfExpiry` defaulted
    /// to 5 minutes. Callers wanting a different lead time set the
    /// field on the returned record.
    let withDefaults
        (provider: string)
        (configId: string)
        (scopeId: string)
        (tokenEndpoint: string)
        (clientId: string)
        (clientSecretKey: string)
        (refreshTokenKey: string)
        : OAuthRefreshDescriptor =
        {
            Provider = provider
            ConfigId = configId
            ScopeId = scopeId
            TokenEndpoint = tokenEndpoint
            ClientId = clientId
            ClientSecretKey = clientSecretKey
            RefreshTokenKey = refreshTokenKey
            ScheduleAheadOfExpiry = DefaultScheduleAheadOfExpiry
        }

    /// Stable composite key identifying a descriptor — used by the
    /// in-process refresher as a dictionary key, by the job handler
    /// as the `JobRegistration.Idempotency.Key`, and by the metrics /
    /// audit emission sites as the `(Provider, ConfigId)` correlation
    /// pair rendered as a single string.
    let key (descriptor: OAuthRefreshDescriptor) : string =
        sprintf "%s:%s" descriptor.Provider descriptor.ConfigId

    /// Derived `ISecretStore` key under which the substrate caches
    /// the most recent access token. Convention: refresh-token key
    /// suffixed with `.access`. Callers read this to consume cached
    /// access tokens; consumers that fail with a 401 should call
    /// `IOAuthTokenRefresher.RefreshNow` to force a fresh dispatch.
    let accessTokenKey (descriptor: OAuthRefreshDescriptor) : string = descriptor.RefreshTokenKey + ".access"

    /// Derived `ISecretStore` key under which the substrate caches
    /// the access-token expiry (ISO-8601 UTC string). Convention:
    /// refresh-token key suffixed with `.expires-at`.
    let accessExpiryKey (descriptor: OAuthRefreshDescriptor) : string =
        descriptor.RefreshTokenKey + ".expires-at"

/// Outcome of a single refresh attempt. Callers branch on the tag —
/// no callbacks, no `OnFailure` parameter (portability rule 3).
///
/// `Refreshed` is the happy path: the substrate persisted the new
/// access token + expiry (and the rotated refresh token, when the
/// upstream rotated it) before returning. `TokenInvalidatedByProvider`
/// is a terminal failure that requires fresh user consent —
/// distinguished from `PermanentError` so the substrate can emit the
/// dedicated `OAuthRefreshTokenInvalidated` audit + flip the
/// connector's credential status to `NeedsReauthorization`.
/// `TransientError` is retry-eligible (the job handler reschedules
/// per `JobRetryPolicy`); on retry-exhaustion the handler routes to
/// `PermanentError` + dead-letter.
type OAuthRefreshResult =
    /// Refresh succeeded. The substrate has already written the new
    /// access token, its expiry, and (if rotated) the new refresh
    /// token to `ISecretStore`. `newExpiry` is the UTC instant at
    /// which the access token will be rejected by the upstream —
    /// the scheduler uses it to compute the next dispatch time.
    | Refreshed of newExpiry: DateTimeOffset
    /// Upstream provider rejected the refresh token (`invalid_grant`
    /// or equivalent). The refresh token cannot be recovered without
    /// fresh user-driven consent. The substrate emits
    /// `OAuthRefreshTokenInvalidated`, transitions the connector's
    /// credential status to `NeedsReauthorization`, and pushes an
    /// admin-UI notification. Terminal — no further dispatches.
    | TokenInvalidatedByProvider
    /// Refresh failed in a way that may recover on retry — network
    /// blip, upstream 5xx, `IRateLimiter` `Refused`, transient DNS
    /// failure. The handler reschedules per `JobRetryPolicy` (5
    /// attempts over ~15 min by Phase 10h default). On retry
    /// exhaustion the outcome becomes a `PermanentError` dead-letter.
    | TransientError of reason: string
    /// Refresh failed permanently — the substrate cannot recover by
    /// retrying (missing `ClientSecretKey` in `ISecretStore`,
    /// malformed token-endpoint response, descriptor declares a
    /// `TokenEndpoint` the substrate cannot reach). The handler
    /// dead-letters + audits + notifies; no further dispatches.
    | PermanentError of reason: string