// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcTypes

// ─── Error DU ────────────────────────────────────────────────────────
//
// `AuthError` surfaces every failure mode the OIDC flow can produce so
// the UI layer can render a useful message without `try` / `catch`
// sprawl. Keep it flat — the UI matches by case, not by pattern-matching
// into nested records.

type AuthError =
    /// Discovery document fetch failed (network error, non-2xx, or
    /// malformed JSON). The payload is the underlying message for
    /// debugging; UIs should render a generic "can't reach identity
    /// provider" message.
    | DiscoveryFailed of string
    /// The callback URL's `state` parameter did not match the value
    /// stashed before sign-in. Indicates the sign-in attempt was
    /// interrupted, tab-hopped, or potentially tampered with; the
    /// only safe action is to restart sign-in from scratch.
    | InvalidState
    /// The callback URL had no `code` parameter. Usually means the
    /// issuer returned an error (check the `error` / `error_description`
    /// query parameters — surfaced by `IssuerError` below).
    | MissingCode
    /// The issuer reported an error on the callback URL
    /// (`error` / `error_description` query parameters).
    | IssuerError of code: string * description: string option
    /// Token-endpoint exchange (code -> tokens, or refresh_token ->
    /// new access_token) failed.
    | TokenExchangeFailed of string
    /// Generic network / browser error — `fetch` rejected for reasons
    /// unrelated to OIDC semantics (CORS, DNS, offline).
    | NetworkError of string
    /// The `nonce` claim on the returned id_token did not match the
    /// value stashed before sign-in. Indicates the id_token was not
    /// issued in response to *this* authorize request — either a
    /// replay attack against a previously-issued token, or an issuer
    /// misbehaving. The only safe action is to clear local state and
    /// restart sign-in from scratch (the handler does this before
    /// returning). OIDC spec REQUIRES nonce validation when nonce was
    /// sent on the authorize request; this case represents the
    /// rejection.
    | NonceMismatch
    /// The id_token wasn't a parseable JWS (not three base64url
    /// segments, payload not JSON, header missing required fields).
    /// Phase 3b.A — surfaces from the client-side validation pipeline
    /// when `OidcUIConfig.ValidateIdToken = Some true`.
    | MalformedIdToken
    /// The id_token's signature failed verification against the JWKS
    /// key matching the header's `kid` (or no key matched after a
    /// forced refresh, or the JWKS fetch itself failed). Phase 3b.A.
    | IdTokenSignatureInvalid
    /// The id_token's `iss` claim did not match `OidcUIConfig.Issuer`.
    /// Phase 3b.A.
    | IdTokenIssuerInvalid
    /// The id_token's `aud` claim did not contain
    /// `OidcUIConfig.ClientId`. Phase 3b.A.
    | IdTokenAudienceInvalid
    /// The id_token's `exp` claim is in the past (beyond the 60s
    /// clock-skew tolerance). Phase 3b.A.
    | IdTokenExpired

// ─── In-memory auth state ────────────────────────────────────────────
//
// The OIDC shell component holds a single `AuthState` in React state.
// It is NOT part of the Elmish model — keeping it in React local state
// avoids leaking sign-in mechanics into every module's `update`.

type AuthState =
    /// Initial render — we haven't yet decided whether the user is
    /// signed in, completed a callback, or needs to sign in. Renders a
    /// loading screen.
    | Checking
    /// Signed in with a valid access token. Shell is rendered.
    | SignedIn
    /// Not signed in. Sign-in screen is rendered.
    | SignedOut
    /// An error occurred during callback handling or refresh. Renders
    /// an error screen with a retry button that resets to `SignedOut`.
    | Failed of AuthError