// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcTokenStore

// ─── OIDC token storage ──────────────────────────────────────────────
//
// We keep two tokens in the browser:
//
// • Bearer token — short-lived (typical 15 min – 1 h). Stored via
//   `UserSession.setAuthToken` which is in the core SDK because
//   `UserSession.withRequestHeaders` attaches it to every Fable.Remoting
//   request. That storage lives in `localStorage` under
//   `"toolup-auth-token"` — shared state owned by the SDK, not the
//   OIDC companion.
//
//   WHICH token this is — the `access_token` or the `id_token` — is
//   the deployment's declared `BearerTokenKind`, resolved by
//   `OidcUIConfig.resolveBearerToken` and applied by the orchestration
//   in `OidcClient` before it calls `persistTokens`. This module holds
//   one slot and does not know or care which class of token is in it;
//   that is what keeps the classifier, the pre-expiry refresh timer and
//   the outgoing request header agreeing with each other for free
//   rather than by three separate rules.
//
// • Refresh token — longer-lived (typical 30 d – 90 d). Read only by
//   this companion's refresh logic; core SDK has no knowledge of it.
//   Stored in `localStorage` under `"toolup-oidc-refresh-token"`.
//
// XSS CAVEAT — refresh tokens in localStorage are vulnerable to XSS.
// The threat model here matches Clerk's browser-side model: we assume
// the app's content-security-policy blocks script injection. For
// deployments that need stronger isolation, the planned migration path
// is a backend-for-frontend (BFF) flow — the refresh token lives in an
// HttpOnly cookie on a same-origin auth-relay service and the browser
// only ever sees short-lived access tokens. This BFF flow is a
// tracked follow-up, not yet shipped.

open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcTypes

// `UserSession` in the core SDK exposes `setAuthToken` / `clearAuthToken`
// / `getAuthToken` for the access token only.
//
// Note: the core module is declared as `module UserSession` (no
// namespace prefix) in `src/ToolUp.Platform.Client/Client/UserSession.fs`.

let private refreshTokenKey = "toolup-oidc-refresh-token"

// ─── PKCE transaction state ──────────────────────────────────────────
//
// Between the authorize redirect and the callback return, we stash
// the `code_verifier` and `state` in `sessionStorage` (tab-scoped so
// two concurrent sign-in flows can't collide).

let private verifierKey = "toolup-oidc-code-verifier"
let private stateKey = "toolup-oidc-state"
let private nonceKey = "toolup-oidc-nonce"
let private correlationIdKey = "toolup-oidc-correlation-id"

let stashPendingSignIn (verifier: string) (state: string) (nonce: string) : unit =
    Browser.Dom.window.sessionStorage.setItem (verifierKey, verifier)
    Browser.Dom.window.sessionStorage.setItem (stateKey, state)
    Browser.Dom.window.sessionStorage.setItem (nonceKey, nonce)

let readPendingSignIn
    ()
    : {|
          Verifier: string option
          State: string option
          Nonce: string option
      |}
    =
    let readOpt key =
        match Browser.Dom.window.sessionStorage.getItem key with
        | null
        | "" -> None
        | v -> Some v

    {|
        Verifier = readOpt verifierKey
        State = readOpt stateKey
        Nonce = readOpt nonceKey
    |}

let clearPendingSignIn () : unit =
    Browser.Dom.window.sessionStorage.removeItem verifierKey
    Browser.Dom.window.sessionStorage.removeItem stateKey
    Browser.Dom.window.sessionStorage.removeItem nonceKey
    Browser.Dom.window.sessionStorage.removeItem correlationIdKey

// ─── Correlation id (for the AuthTracer) ─────────────────────────────
//
// A per-sign-in-flow correlation id is generated at `beginSignIn` and
// stashed alongside the PKCE state. Downstream tracer emits read it
// here so every line of one flow shares the same id without the
// orchestration code having to thread the value through every async
// helper's parameter list. Cleared with the rest of the PKCE state at
// `clearPendingSignIn` (callback success, sign-out, irrecoverable
// failure) — emits that fire after a clear (cold-start classify,
// refresh) read as `None`.

let stashCorrelationId (correlationId: string) : unit =
    Browser.Dom.window.sessionStorage.setItem (correlationIdKey, correlationId)

let readCorrelationId () : string option =
    match Browser.Dom.window.sessionStorage.getItem correlationIdKey with
    | null
    | "" -> None
    | v -> Some v

// ─── Access + refresh tokens ─────────────────────────────────────────

/// Persist both tokens after a successful token-endpoint exchange.
/// The bearer token flows through the core `UserSession` so
/// Fable.Remoting calls pick it up; the refresh token is ours alone.
///
/// `bearerToken` is whichever token the deployment's `BearerTokenKind`
/// selected — the caller has already decided (see
/// `OidcStateMachine.decideBearerToken`). Passing the access token
/// unconditionally is the historical behaviour and remains what a
/// config with no declared strategy produces.
let persistTokens (bearerToken: string) (refreshToken: string option) : unit =
    UserSession.setAuthToken bearerToken

    match refreshToken with
    | Some rt -> Browser.Dom.window.localStorage.setItem (refreshTokenKey, rt)
    | None -> ()

let getRefreshToken () : string option =
    match Browser.Dom.window.localStorage.getItem refreshTokenKey with
    | null
    | "" -> None
    | t -> Some t

let hasAccessToken () : bool =
    UserSession.getAuthToken () |> Option.isSome

/// The raw bearer token, when one is stored. Used by the pre-expiry
/// refresh timer to read the JWT `exp` claim, and by
/// `classifyStoredToken` to read `iss` + `exp`; the SDK core only
/// exposes presence (`hasAccessToken`) so this thin accessor keeps the
/// token read inside the store module rather than reaching into
/// `UserSession` from the orchestration layer.
///
/// The name predates the bearer strategy and is kept for source
/// compatibility. It returns the BEARER — the access token under the
/// default strategy, the id_token under `IdTokenBearer` — which is the
/// right value for both of its callers, since each is asking about the
/// credential the session is actually sending.
let getAccessToken () : string option = UserSession.getAuthToken ()

/// Clear all OIDC-related session state. Called on sign-out and on
/// irrecoverable auth errors.
let clearAll () : unit =
    UserSession.clearAuthToken ()
    Browser.Dom.window.localStorage.removeItem refreshTokenKey
    clearPendingSignIn ()

// ─── Error formatting helper ─────────────────────────────────────────

/// Phase 751 — the localised form. One arm per `AuthError` case, each
/// reading the sentence from the resolved catalog rather than authoring
/// it here, so a non-English deployment's sign-in failure is not the one
/// screen that stays in English.
///
/// The opaque branches stay opaque: `AuthErrorMessages` documents that a
/// translation must not name the withheld sub-cause either.
let describeErrorWith (msgs: AuthErrorMessages) (err: AuthError) : string =
    match err with
    | DiscoveryFailed m -> msgs.DiscoveryFailed m
    | InvalidState -> msgs.InvalidState
    | MissingCode -> msgs.MissingCode
    | IssuerError(code, desc) ->
        match desc with
        | Some d -> msgs.IssuerErrorDescribed code d
        | None -> msgs.IssuerError code
    | TokenExchangeFailed m -> msgs.TokenExchangeFailed m
    | NetworkError m -> msgs.NetworkError m
    | NonceMismatch -> msgs.NonceMismatch
    | MalformedIdToken -> msgs.MalformedIdToken
    | IdTokenSignatureInvalid -> msgs.SignatureInvalid
    | IdTokenIssuerInvalid -> msgs.IssuerInvalid
    | IdTokenAudienceInvalid -> msgs.AudienceInvalid
    | IdTokenExpired -> msgs.Expired

/// The pre-751 entry point, unchanged in arity and behaviour — a widened
/// arity would read as a REMOVAL in the public-API approval baseline and
/// would break every caller, for a parameter only a catalog-resolving
/// caller can supply (444's recorded `...With` pattern).
let describeError (err: AuthError) : string =
    describeErrorWith MessageCatalog.english.Auth.Errors err

// ─── Developer-facing diagnostic ─────────────────────────────────────
//
// `diagnose` is the structured counterpart to `describeError`. It
// returns a `{ Kind; SubCause; Hint }` record consumed by the auth
// tracer + structured log emission paths — never by the UI.
//
// User-facing strings (above) stay deliberately opaque on the
// security-sensitive branches (signature, nonce, issuer, audience
// validation) so a tampering attacker can't probe the validator by
// reading the rendered message. The developer log carries the
// withheld sub-cause and, where applicable, a hint referencing the
// provider quirk or app-registration knob most likely to be at fault.

let diagnose (err: AuthError) : AuthDiagnostic =
    match err with
    | DiscoveryFailed m -> {
        Kind = "DISCOVERY_FAILED"
        SubCause = Some m
        Hint =
            Some
                "Verify the issuer URL is correct and reachable from the browser. For workforce Entra the form is `https://login.microsoftonline.com/{tenantGuid}/v2.0` — tenant GUID, not domain."
      }
    | InvalidState -> {
        Kind = "PKCE_STATE_MISMATCH"
        SubCause =
            Some
                "The `state` query parameter on the callback URL did not match the value stashed in sessionStorage before the authorize redirect."
        Hint =
            Some
                "Indicates a tab-hop, an interrupted sign-in, sessionStorage eviction, or potential tampering. Restart sign-in from scratch."
      }
    | MissingCode -> {
        Kind = "CALLBACK_MISSING_CODE"
        SubCause = Some "Callback URL had no `code` query parameter."
        Hint =
            Some
                "Check the `error` and `error_description` query parameters on the callback URL — the issuer may have returned an error instead of a code."
      }
    | IssuerError(code, desc) ->
        let detail =
            match desc with
            | Some d -> sprintf "Issuer returned error code `%s`: %s" code d
            | None -> sprintf "Issuer returned error code `%s`." code

        {
            Kind = "ISSUER_RETURNED_ERROR"
            SubCause = Some detail
            Hint = Some "Consult the identity provider's documentation for the meaning of this error code."
        }
    | TokenExchangeFailed m -> {
        Kind = "TOKEN_EXCHANGE_FAILED"
        SubCause = Some m
        Hint =
            Some
                "The `/token` endpoint rejected the authorization code. Common causes: code already redeemed, code expired, `redirect_uri` mismatch with the app registration, `client_id` mismatch, PKCE verifier missing or wrong."
      }
    | NetworkError m -> {
        Kind = "NETWORK_ERROR"
        SubCause = Some m
        Hint = Some "The browser could not complete the fetch — CORS, DNS, offline, or the issuer is unreachable."
      }
    | NonceMismatch -> {
        Kind = "ID_TOKEN_NONCE_MISMATCH"
        SubCause =
            Some "The `nonce` claim on the returned id_token did not match the value sent on the authorize request."
        Hint =
            Some
                "Indicates a replay against a previously-issued token, or an issuer misbehaving around nonce. OIDC spec REQUIRES nonce validation — this is a hard fail."
      }
    | MalformedIdToken -> {
        Kind = "ID_TOKEN_MALFORMED"
        SubCause =
            Some
                "id_token did not parse as a JWS — not three base64url segments, payload not JSON, or header missing required fields."
        Hint = None
      }
    | IdTokenSignatureInvalid -> {
        Kind = "ID_TOKEN_SIGNATURE_INVALID"
        SubCause =
            Some
                "id_token signature did not verify against the JWKS key matching the header `kid` (or no key matched after a forced refresh, or the JWKS fetch itself failed)."
        Hint =
            Some
                "Check that the issuer's JWKS endpoint is reachable and the app registration's key set is current. The user-facing message is deliberately opaque (anti-tampering stance) — only this developer log carries the sub-cause."
      }
    | IdTokenIssuerInvalid -> {
        Kind = "ID_TOKEN_ISSUER_INVALID"
        SubCause = Some "id_token `iss` claim did not match the configured `OidcUIConfig.Issuer`."
        Hint =
            Some
                "For workforce Entra: confirm the issuer is `https://login.microsoftonline.com/{tenantGuid}/v2.0` (tenant GUID, not domain) and that the app registration's `requestedAccessTokenVersion` is 2."
      }
    | IdTokenAudienceInvalid -> {
        Kind = "ID_TOKEN_AUDIENCE_INVALID"
        SubCause = Some "id_token `aud` claim did not contain the configured `OidcUIConfig.ClientId`."
        Hint =
            Some
                "For workforce Entra: the `api://{clientId}/access_as_user` scope must be requested at sign-in time; without it the access token is addressed to Microsoft Graph and server-side audience validation fails."
      }
    | IdTokenExpired -> {
        Kind = "ID_TOKEN_EXPIRED"
        SubCause = Some "id_token `exp` claim is in the past (beyond the 60-second clock-skew tolerance)."
        Hint = Some "Check system clock drift on the device and the issuer's clock-skew policy."
      }