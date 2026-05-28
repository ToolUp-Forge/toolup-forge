// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcTokenStore

// ─── OIDC token storage ──────────────────────────────────────────────
//
// We keep two tokens in the browser:
//
// • Access token — short-lived (typical 15 min – 1 h). Stored via
//   `UserSession.setAuthToken` which is in the core SDK because
//   `UserSession.withRequestHeaders` attaches it to every Fable.Remoting
//   request. That storage lives in `localStorage` under
//   `"toolup-auth-token"` — shared state owned by the SDK, not the
//   OIDC companion.
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

// ─── Access + refresh tokens ─────────────────────────────────────────

/// Persist both tokens after a successful token-endpoint exchange.
/// The access token flows through the core `UserSession` so
/// Fable.Remoting calls pick it up; the refresh token is ours alone.
let persistTokens (accessToken: string) (refreshToken: string option) : unit =
    UserSession.setAuthToken accessToken

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

/// The raw access token, when one is stored. Used by the pre-expiry
/// refresh timer to read the JWT `exp` claim; the SDK core only exposes
/// presence (`hasAccessToken`) so this thin accessor keeps the token
/// read inside the store module rather than reaching into `UserSession`
/// from the orchestration layer.
let getAccessToken () : string option = UserSession.getAuthToken ()

/// Clear all OIDC-related session state. Called on sign-out and on
/// irrecoverable auth errors.
let clearAll () : unit =
    UserSession.clearAuthToken ()
    Browser.Dom.window.localStorage.removeItem refreshTokenKey
    clearPendingSignIn ()

// ─── Error formatting helper ─────────────────────────────────────────

let describeError (err: AuthError) : string =
    match err with
    | DiscoveryFailed m -> sprintf "Could not reach the identity provider (%s)." m
    | InvalidState -> "Sign-in state mismatch. Please try again."
    | MissingCode -> "No authorization code received from the identity provider."
    | IssuerError(code, desc) ->
        match desc with
        | Some d -> sprintf "Identity provider returned %s: %s." code d
        | None -> sprintf "Identity provider returned %s." code
    | TokenExchangeFailed m -> sprintf "Token exchange failed (%s)." m
    | NetworkError m -> sprintf "Network error (%s)." m
    | NonceMismatch -> "Sign-in token did not match the original request. Please try again."
    | MalformedIdToken -> "Sign-in token was malformed. Please try again."
    | IdTokenSignatureInvalid -> "Sign-in token signature could not be verified. Please try again."
    | IdTokenIssuerInvalid -> "Sign-in token came from an unexpected identity provider. Please try again."
    | IdTokenAudienceInvalid -> "Sign-in token was not issued for this application. Please try again."
    | IdTokenExpired -> "Sign-in token has expired. Please try again."