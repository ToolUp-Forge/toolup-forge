namespace ToolUp.Platform

open System

// ─── OAuth state store (Phase 10b) ───────────────────────────────
//
// Short-lived persistence for in-flight OAuth Authorization Code
// flows. Bridges the gap between `/api/oauth/{flowName}/authorize`
// (which generates the CSRF `state` parameter and redirects the user
// to the upstream provider) and `/api/oauth/{flowName}/callback`
// (which validates the returned `state` and exchanges the code).
//
// **Lifecycle.** The substrate writes one state entry per
// `/authorize` redirect. The user spends seconds-to-minutes on the
// upstream consent screen. The callback reads-and-deletes the entry
// atomically. Entries are one-shot — replay attempts on the callback
// URL fail because the state has been consumed.
//
// **TTL.** 10 minutes. Long enough for slow human consent (multi-
// account picker, MFA), short enough that a stale state from a
// half-completed flow doesn't pollute the store indefinitely. Lazy
// eviction in `TryConsume` plus a periodic sweep for entries that
// are never consumed.
//
// **Single-instance limitation.** The shipped `InMemoryOAuthStateStore`
// keeps state in a `ConcurrentDictionary`. A multi-instance deployment
// behind a load balancer that pins requests differently between
// `/authorize` and `/callback` will fail. Phase 9c half 2 ships a
// distributed companion (Redis-backed, mirrors the
// `src/NotificationChannels/Redis/` pattern) — `IOAuthStateStore` is
// the portability seam.
//
// **Phase 9c portability.**
//   - Rule 1 (identity by value): `Token` is `string`; no live handle.
//   - Rule 2 (async at every boundary): every method returns `Async<_>`.
//   - Rule 3 (retry as data): N/A (no retries — state expiry is the
//     failure mode).
//   - Rule 4 (stateless handlers): the store is the state; consumers
//     are stateless across calls.
//   - Rule 5 (no cross-shard ordering): per-token operations only.
//   - Rule 6 (precision): TTL is documented in minutes; no sub-
//     second guarantee.

/// One in-flight OAuth flow's state entry. Persisted between
/// `/authorize` and `/callback`. Single-use — `TryConsume` removes
/// the entry as it returns it.
type OAuthFlowState = {
    /// CSRF state token. The substrate generates it via BCL
    /// `RandomNumberGenerator`; flows forward it to the provider
    /// unchanged. Callback handlers look up entries by this value.
    Token: string
    /// `IOAuthCredentialFlow.Name` of the flow that created this
    /// entry. The callback handler validates the returned `state`
    /// against this — a state token issued for one flow must not
    /// validate against another.
    FlowName: string
    /// Data source the flow is operating on. The substrate uses it
    /// to compute the secret-key suffix for the persisted refresh
    /// token after `ExchangeCode` succeeds.
    DataSourceId: DataSourceId
    /// Bare scope identifier of the caller who initiated the flow
    /// (team UUID in `Team` / `MultiTeam` mode, user id in
    /// `Individual` / `AuthenticatedEphemeral`). Matches the value
    /// used by `IDataSourceConfigStore.Get` and credential-metadata
    /// blob paths under `_platform/data-sources/{scopeId}/...`.
    /// Pinned at /authorize time so a team-switch during consent
    /// doesn't redirect persistence to the wrong scope.
    ScopeId: string
    /// Storage container suffix for the caller's scope (`team-{teamId}`
    /// in `Team` / `MultiTeam` mode, `user-{userId}` in `Individual` /
    /// `AuthenticatedEphemeral`). Used as the `scope` argument for
    /// `ISecretStore.SetSecret` / `GetSecret` when persisting the
    /// refresh token. Pinned alongside `ScopeId` at /authorize so
    /// /callback's secret-store calls don't need to re-resolve the
    /// caller's scope through middleware.
    Container: string
    /// User id of the caller who initiated the flow. Used for audit
    /// emission (`OAuthConnected.ActorUserId`).
    UserId: string
    /// UTC creation time. TTL eviction compares against this.
    CreatedAt: DateTime
    /// Absolute redirect URI sent to the provider in the /authorize
    /// step. Stamped here so the /callback handler passes the same
    /// value to `ExchangeCode` (Google validates exact-match).
    RedirectUri: string
    /// Optional PKCE code verifier (RFC 7636). Flows that opt into
    /// PKCE generate the verifier in `BuildAuthorizeUrl` and stash
    /// it here; the substrate forwards it to `ExchangeCode` so the
    /// provider can validate the verifier-challenge pair.
    CodeVerifier: string option
}

type IOAuthStateStore =
    /// Persist a fresh state entry. Returns `Error` on duplicate
    /// `Token` (the substrate must not reuse tokens — collisions are
    /// a programming error in the random generator). Distributed
    /// implementations should fail-fast on collision rather than
    /// silently overwrite.
    abstract Save: state: OAuthFlowState -> Async<Result<unit, string>>

    /// Atomically read-and-remove the entry for `token`. Returns
    /// `Some` only when the entry exists AND has not expired (per
    /// the deployment's configured TTL). Subsequent calls with the
    /// same `token` return `None` — single-use semantics.
    abstract TryConsume: token: string -> Async<OAuthFlowState option>

    /// Remove all entries older than `ttl`. Returns the count
    /// removed. Called by the substrate's periodic sweep timer
    /// (every minute, mirrors `JobScheduler` cadence) plus opportu-
    /// nistically on `TryConsume` for entries that match the token
    /// but have expired.
    abstract Cleanup: ttl: TimeSpan -> Async<int>