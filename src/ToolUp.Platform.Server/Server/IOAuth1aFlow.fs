// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Net.Http

// ─── OAuth 1.0a flow interface (Phase 10g) ──────────────────────────────
//
// The sibling of `IOAuthCredentialFlow` (Phase 10e OAuth 2.0). One
// implementation per 1.0a provider lives in a companion / connector
// package and is injected at compose time. The substrate's flow handler
// drives the three-legged (request-token → user-authorise → access-token)
// dance and persists the resulting access-token pair via `ISecretStore`;
// connectors sign each outbound API call with `Sign`.
//
// Six-rule portability audit (GP 12): identity by value (`Name` string,
// credential pairs are records), async at every boundary, errors as
// `OAuth1aError` data, stateless between calls (credentials read per call
// from `ISecretStore`; no in-memory session), no cross-shard ordering
// (each authorisation is independent), precision N/A.

/// Admin-UI display metadata for a 1.0a flow.
type OAuth1aFlowDescriptor = {
    /// Human-readable provider name ("Garmin Connect").
    DisplayName: string
    /// Optional link to the provider's app-registration docs.
    HelpUrl: string option
}

/// Per-call context handed to flow methods. `ScopeId` is the caller's
/// resolved storage scope (for `ISecretStore` reads of the consumer
/// credentials); `ResourceId` identifies the specific connection (the
/// secret-key suffix for the persisted access-token pair).
type OAuth1aFlowContext = { ScopeId: string; ResourceId: string }

/// Result of leg 1 (request-token fetch): the provider's user-authorisation
/// URL to redirect to (carrying `oauth_token`), plus the request-token pair
/// the substrate stashes in the `IOAuth1aStateStore` to complete leg 3.
type OAuth1aRequestTokenResult = {
    /// The absolute URL to redirect the user's browser to for consent.
    AuthorizeUrl: string
    /// The temporary request token (echoed back on the callback).
    RequestToken: string
    /// The request-token secret — the substrate stashes it (never the
    /// browser) to sign the leg-3 access-token exchange.
    RequestTokenSecret: string
}

type IOAuth1aFlow =
    /// Stable kebab-case name discriminator ("garmin"). URL-segment key
    /// for the flow's routes and the `ISecretStore` key prefix.
    abstract Name: string

    /// Admin-UI display metadata.
    abstract Descriptor: OAuth1aFlowDescriptor

    /// Leg 1 — fetch a request token from the provider (a server-to-server
    /// call signed with the consumer credentials and an empty token secret)
    /// and return the user-authorisation URL plus the request-token pair to
    /// stash. The substrate persists the pair via `IOAuth1aStateStore`
    /// before redirecting the user to `AuthorizeUrl`.
    abstract BuildRequestTokenUrl:
        ctx: OAuth1aFlowContext * callbackUri: string -> Async<Result<OAuth1aRequestTokenResult, OAuth1aError>>

    /// Leg 3 — after the user authorises and the provider redirects back
    /// with `oauth_token` + `oauth_verifier`, exchange the (now-authorised)
    /// request token for the permanent access-token pair. The substrate
    /// persists the returned pair via `ISecretStore` and discards the
    /// request-token state.
    abstract ExchangeRequestTokenForAccess:
        ctx: OAuth1aFlowContext * requestToken: string * requestTokenSecret: string * verifier: string ->
            Async<Result<OAuth1aTokenPair, OAuth1aError>>

    /// Sign an outbound API request in place — attaches the
    /// `Authorization: OAuth …` HMAC-SHA1 header derived from the flow's
    /// consumer credentials and the supplied access-token pair. Per-call;
    /// no bearer-token caching (1.0a signs every request).
    abstract Sign: request: HttpRequestMessage * tokenPair: OAuth1aTokenPair -> Async<unit>