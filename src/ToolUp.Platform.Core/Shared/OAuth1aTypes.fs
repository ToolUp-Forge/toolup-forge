// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── OAuth 1.0a substrate types (Phase 10g) ─────────────────────────────
//
// Value types for the OAuth 1.0a (RFC 5849) three-legged authorization
// substrate — the sibling of the Phase 10e OAuth 2.0 Authorization Code
// substrate, for the legacy SaaS APIs still on 1.0a (Garmin Connect,
// legacy Twitter v1, some financial-data providers). Unlike OAuth 2.0's
// bearer tokens, 1.0a signs every API call with HMAC-SHA1 over the
// canonical request; the access "token" is a credential *pair* (token +
// token secret) used to derive the per-call signing key.
//
// Identity-by-value on every type (GP 12 rule 1) — no live handles.

/// Consumer (application) credentials issued by a 1.0a provider when the
/// application is registered. The consumer secret is one half of the
/// per-call signing key; it is credential-grade and long-lived, so a
/// deployment persists it through `ISecretStore` (ideally the encrypted
/// decorator — Phase 22).
type OAuth1aConsumerCredentials = {
    ConsumerKey: string
    ConsumerSecret: string
}

/// A token credential pair. During the three-legged flow this is first
/// the *request*-token pair (temporary, exchanged after user consent),
/// then the *access*-token pair (permanent until the user revokes). Every
/// signed API call uses the access pair; `TokenSecret` is the second half
/// of the signing key.
type OAuth1aTokenPair = { Token: string; TokenSecret: string }

/// Signature method (RFC 5849 §3.4). `HmacSha1` covers ~99% of 1.0a APIs
/// in practice; RSA-SHA1 is deferred until a deployment asks, and
/// PLAINTEXT (§3.4.4) is deliberately unsupported (insecure).
[<RequireQualifiedAccess>]
type OAuth1aSignatureMethod = | HmacSha1

module OAuth1aSignatureMethod =
    /// The wire-format `oauth_signature_method` value.
    let wireName =
        function
        | OAuth1aSignatureMethod.HmacSha1 -> "HMAC-SHA1"

/// Typed errors returned by the OAuth 1.0a flow substrate. Case names are
/// distinct from the OAuth 2.0 `OAuthError` DU so a file that opens both
/// stays unambiguous. Messages never carry secret material.
type OAuth1aError =
    /// `ISecretStore.GetSecret` returned `None` for the configured
    /// consumer key / secret. Operator re-enters credentials.
    | ConsumerCredentialMissing of key: string
    /// The provider rejected the request-token fetch (leg 1).
    | RequestTokenRejected of message: string
    /// The provider rejected the access-token exchange (leg 3).
    | AccessTokenRejected of message: string
    /// The callback's `oauth_token` did not match a persisted request-token
    /// state entry, or the entry expired before the user returned.
    | StateTokenMismatch of message: string
    /// Network failure reaching the provider's OAuth endpoints.
    | NetworkFailure of message: string
    /// Request signing failed — typically a malformed persisted token pair.
    /// Surfaces distinctly for diagnostics.
    | SigningFailed of message: string
    /// Catch-all for unexpected failures (malformed response, etc.).
    | OAuth1aFlowFailed of message: string

module OAuth1aError =
    /// Render for log lines / admin-UI display. Never includes secrets.
    let toMessage (err: OAuth1aError) : string =
        match err with
        | ConsumerCredentialMissing key -> $"OAuth 1.0a consumer credential missing: {key}"
        | RequestTokenRejected msg -> $"OAuth 1.0a request-token rejected: {msg}"
        | AccessTokenRejected msg -> $"OAuth 1.0a access-token rejected: {msg}"
        | StateTokenMismatch msg -> $"OAuth 1.0a state-token mismatch: {msg}"
        | NetworkFailure msg -> $"OAuth 1.0a network error: {msg}"
        | SigningFailed msg -> $"OAuth 1.0a signing failed: {msg}"
        | OAuth1aFlowFailed msg -> $"OAuth 1.0a flow failed: {msg}"

/// A persisted request-token state entry (leg 1 → leg 3 correlation). The
/// substrate stashes the request-token *secret* keyed by the request
/// *token* when it fetches the request token, and reads it back on the
/// user-authorised callback to sign the access-token exchange. Scope-keyed
/// so one tenant's in-flight authorisation can never be resumed by
/// another (GP 4).
type OAuth1aRequestState = {
    /// The scope the authorisation was initiated under.
    ScopeId: string
    /// The flow name (provider) the request token belongs to.
    FlowName: string
    /// The request-token secret, needed to sign the access-token exchange.
    RequestTokenSecret: string
    /// UTC creation time — entries older than the store's TTL are treated
    /// as absent (the user took too long to authorise).
    CreatedAt: DateTime
}