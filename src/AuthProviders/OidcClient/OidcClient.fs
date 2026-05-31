// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcClient

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcDiscovery
open ToolUp.AuthProviders.Oidc.OidcIdTokenValidator
open ToolUp.AuthProviders.Oidc.OidcPkce
open ToolUp.AuthProviders.Oidc.OidcTokenStore
open ToolUp.AuthProviders.Oidc.AuthTracer
open ToolUp.AuthProviders.Oidc.OidcStateMachine

// ─── OIDC Authorization Code + PKCE orchestration ────────────────────
//
// Three phases:
//
// 1. `beginSignIn` — called from the sign-in UI. Generates PKCE
//    verifier + challenge, stashes verifier + state in sessionStorage,
//    then redirects to the issuer's authorize endpoint. Never returns
//    (the page navigates away).
//
// 2. `handleCallback` — called from the shell's initial render when
//    the current URL is the callback URL. Extracts `code` + `state`
//    from query string, validates state against the stashed value,
//    POSTs to the token endpoint with the verifier, and on success
//    persists access + refresh tokens, clears sessionStorage, and
//    navigates the document to the app root (`location.replace` to
//    `origin + "/"`) so the shell re-boots with the token in place and
//    off the callback route.
//
// 3. `refreshAccessToken` — called proactively (not yet — Phase 3b
//    ships the plumbing but not the background timer) when the access
//    token is near expiry. POSTs refresh_token to the token endpoint,
//    persists the new access token and optionally a rotated refresh
//    token.

// ─── fetch helpers ───────────────────────────────────────────────────

[<Emit("fetch($0, $1)")>]
let private fetchPost (url: string) (opts: obj) : JS.Promise<obj> = jsNative

[<Emit("new URLSearchParams($0).toString()")>]
let private urlEncode (form: obj) : string = jsNative

// Read a single query-string parameter. `URLSearchParams.get` returns
// `string | null`; we hand it back as `obj` so the call site can
// null-check before unboxing. MUST be an `[<Emit>]` (not
// `createNew (jsNative?URLSearchParams) …`) — `jsNative` is Fable's
// "throws if evaluated" placeholder, so dereferencing it emits a stub
// that throws `A function supposed to be replaced by native code has
// been called` the instant the OAuth callback is parsed, aborting
// sign-in before the code/state are read.
[<Emit("new URLSearchParams($0).get($1)")>]
let private searchParamValue (search: string) (name: string) : obj = jsNative

[<Emit("window.location.origin")>]
let private pageOrigin () : string = jsNative

let private getStringField (o: obj) (field: string) : string option =
    let value = o?(field)

    if isNullOrUndefined value then
        None
    else
        Some(unbox<string> value)

let private getIntField (o: obj) (field: string) : int option =
    let value = o?(field)

    if isNullOrUndefined value then
        None
    else
        Some(unbox<int> value)

// ─── Callback URL detection ──────────────────────────────────────────

/// True when the current page URL is the redirect URI for this
/// sign-in configuration. The shell checks this on first render to
/// decide whether to handle a callback or show the sign-in screen.
///
/// We compare pathname only — ignoring query string — so issuers that
/// append `?code=…&state=…` don't throw the match off.
let isCallbackUrl (cfg: OidcUIConfig) : bool =
    // Fable interop: construct a JS URL object directly via emitJsExpr to
    // avoid relying on Browser.Dom's `URL.Create` overload resolution
    // (which is type-context-sensitive and breaks when this file
    // compiles standalone in OidcClient.dll instead of being source-
    // injected into the consumer's compilation unit). We only need the
    // pathname, which is a string property on the constructed URL.
    let redirectPathname: string = emitJsExpr cfg.RedirectUri "new URL($0).pathname"
    redirectPathname = Browser.Dom.window.location.pathname

// ─── Phase 1: redirect to authorize endpoint ─────────────────────────

/// Start an OIDC sign-in with arbitrary extra query parameters
/// appended after the standard OAuth/PKCE param set. Used by
/// companions that need to thread provider-specific knobs through
/// the authorize redirect — e.g. Entra External ID's `p=<policyId>`
/// user-flow-policy selector for sign-up vs sign-in routing. Without
/// this, companions either rebuild the entire authorize URL (losing
/// PKCE / state / nonce) or fork the OIDC client wholesale.
///
/// `extraParams` is merged into the query string in iteration order;
/// keys MUST NOT collide with the standard set (`response_type`,
/// `client_id`, `redirect_uri`, `scope`, `state`, `nonce`,
/// `code_challenge`, `code_challenge_method`) — `urlEncode` would
/// emit duplicates and the issuer's behaviour is undefined. The
/// helper does not enforce this; callers are responsible for
/// choosing provider-specific keys outside the standard set.
let beginSignInWithExtras (cfg: OidcUIConfig) (extraParams: (string * string) list) : Async<Result<unit, AuthError>> = async {
    let corrId = generateState ()
    stashCorrelationId corrId
    emitOk (Some corrId) "begin-sign-in" None

    match! fetchDiscovery cfg.Issuer with
    | Error e ->
        emitErr (Some corrId) "discovery-failed" (diagnose e)
        return Error e
    | Ok doc ->
        let verifier = generateCodeVerifier ()
        let! challenge = generateCodeChallenge verifier |> Async.AwaitPromise
        let state = generateState ()
        let nonce = generateState ()
        stashPendingSignIn verifier state nonce

        let scopes = String.concat " " cfg.Scopes

        let standardParams: (string * obj) list = [
            "response_type", box "code"
            "client_id", box cfg.ClientId
            "redirect_uri", box cfg.RedirectUri
            "scope", box scopes
            "state", box state
            "nonce", box nonce
            "code_challenge", box challenge
            "code_challenge_method", box "S256"
        ]

        let extras: (string * obj) list = extraParams |> List.map (fun (k, v) -> k, box v)

        let query = createObj (standardParams @ extras)

        let url = doc.AuthorizationEndpoint + "?" + urlEncode query
        emitOk (Some corrId) "authorize-redirect" None
        Browser.Dom.window.location.assign url
        // `location.assign` navigates away — this Async never resumes.
        return Ok()
}

/// Start an OIDC sign-in with the standard OAuth/PKCE param set.
/// Equivalent to `beginSignInWithExtras cfg []`.
let beginSignIn (cfg: OidcUIConfig) : Async<Result<unit, AuthError>> = beginSignInWithExtras cfg []

// ─── Phase 2: handle callback ────────────────────────────────────────

let private readCallbackParams
    ()
    : {|
          Code: string option
          State: string option
          Error: string option
          ErrorDescription: string option
      |}
    =
    let search = Browser.Dom.window.location.search

    let getParam (name: string) : string option =
        let v = searchParamValue search name

        if isNullOrUndefined v then None else Some(unbox<string> v)

    {|
        Code = getParam "code"
        State = getParam "state"
        Error = getParam "error"
        ErrorDescription = getParam "error_description"
    |}

/// POST `code` + `verifier` to the token endpoint. Returns the parsed
/// response or an error.
let private exchangeCodeForTokens
    (tokenEndpoint: string)
    (clientId: string)
    (redirectUri: string)
    (code: string)
    (verifier: string)
    : Async<
          Result<
              {|
                  AccessToken: string
                  RefreshToken: string option
                  ExpiresIn: int option
                  IdToken: string option
              |},
              AuthError
           >
       >
    =
    async {
        let corrId = readCorrelationId ()
        emitOk corrId "token-exchange-start" None

        try
            let form =
                createObj [
                    "grant_type" ==> "authorization_code"
                    "client_id" ==> clientId
                    "redirect_uri" ==> redirectUri
                    "code" ==> code
                    "code_verifier" ==> verifier
                ]

            let opts =
                createObj [
                    "method" ==> "POST"
                    "headers"
                    ==> createObj [ "Content-Type" ==> "application/x-www-form-urlencoded" ]
                    "body" ==> urlEncode form
                ]

            let! response = fetchPost tokenEndpoint opts |> Async.AwaitPromise
            let ok = unbox<bool> response?ok
            let! json = response?json () |> Async.AwaitPromise

            if not ok then
                let err =
                    getStringField json "error"
                    |> Option.defaultValue (sprintf "HTTP %d" (unbox<int> response?status))

                let e = TokenExchangeFailed err
                emitErr corrId "token-exchange-failed" (diagnose e)
                return Error e
            else
                match getStringField json "access_token" with
                | None ->
                    let e = TokenExchangeFailed "token endpoint response missing access_token"
                    emitErr corrId "token-exchange-failed" (diagnose e)
                    return Error e
                | Some at ->
                    emitOk corrId "token-exchange-ok" None

                    return
                        Ok {|
                            AccessToken = at
                            RefreshToken = getStringField json "refresh_token"
                            ExpiresIn = getIntField json "expires_in"
                            IdToken = getStringField json "id_token"
                        |}
        with ex ->
            let e = NetworkError ex.Message
            emitErr corrId "token-exchange-network-error" (diagnose e)
            return Error e
    }

// `atob` (base64 decoder) is browser-native; also reused by the
// scheduleRefresh block below to read the access-token `exp` claim.
// Declared here so the id_token nonce check at the top of the file
// resolves it before the F# compiler's declaration-order check
// reaches scheduleRefresh.
[<Emit("atob($0)")>]
let private atob (s: string) : string = jsNative

/// Read the `nonce` claim from an id_token's payload. Returns `None`
/// when the token is malformed, the payload is unparseable, or the
/// claim is absent. The signature is NOT verified here — the
/// substrate trusts that the access_token validation upstream (server-
/// side) gates request authority; this client-side nonce check
/// binds the id_token's *issuance* to the originating sign-in
/// request (replay defence), which is what the OIDC spec mandates
/// when nonce is sent on the authorize endpoint.
let private readIdTokenNonce (idToken: string) : string option =
    try
        let parts = idToken.Split('.')

        if parts.Length < 2 then
            None
        else
            let payload = parts[1].Replace('-', '+').Replace('_', '/')
            let pad = (4 - payload.Length % 4) % 4
            let json = atob (payload + System.String('=', pad))
            let parsed = JS.JSON.parse json
            let nonceValue = parsed?nonce

            if isNullOrUndefined nonceValue then
                None
            else
                Some(unbox<string> nonceValue)
    with _ ->
        None

/// Fixed-time string equality. Avoid leaking nonce comparison timing
/// via JS string `==`. Returns `false` on any length / character
/// mismatch; loops over the whole input to make timing independent
/// of where the first difference occurs.
let private fixedTimeStringEquals (a: string) (b: string) : bool =
    if isNull a || isNull b || a.Length <> b.Length then
        false
    else
        let mutable diff = 0

        for i in 0 .. a.Length - 1 do
            diff <- diff ||| (int a[i] ^^^ int b[i])

        diff = 0

// ─── Phase 3b.A: client-side id_token validation ─────────────────────
//
// Defence-in-depth on top of the nonce-binding check above. With
// `OidcUIConfig.ValidateIdToken = Some true`, the callback handler
// validates the id_token's signature + issuer + audience + expiry
// BEFORE persisting the access token. The signature step uses
// WebCrypto's `crypto.subtle.verify` (pure browser-native; no npm
// deps). Pure F# claim validation lives in
// `OidcIdTokenValidator`; the WebCrypto importKey + verify path lives
// here because it depends on `Fable.Core.JsInterop`. The orchestrator
// `validateIdTokenWith` is factored to take its signature verifier +
// JWKS fetcher + now-clock as parameters so contract tests can stub
// them out in .NET.

[<Emit("crypto.subtle.importKey($0, $1, $2, $3, $4)")>]
let private subtleImportKey
    (format: string)
    (keyData: obj)
    (algorithm: obj)
    (extractable: bool)
    (keyUsages: string[])
    : JS.Promise<obj> =
    jsNative

[<Emit("crypto.subtle.verify($0, $1, $2, $3)")>]
let private subtleVerify (algorithm: obj) (key: obj) (signature: obj) (data: obj) : JS.Promise<obj> = jsNative

/// Wrap a `byte[]` in a `Uint8Array` view WebCrypto can consume. Fable
/// emits F# byte arrays as JS `Uint8Array` natively, but the
/// `crypto.subtle.*` signature shapes want the typed array explicitly.
[<Emit("new Uint8Array($0)")>]
let private toUint8Array (bytes: byte[]) : obj = jsNative

/// Map a JOSE algorithm string to the WebCrypto algorithm descriptor
/// used by both `importKey` and `verify`. Mirrors the server-side
/// Phase 3.A `JwsAlgorithm` dispatch shape; Phase 3b.A ships RS256
/// only (the universal OIDC default), with ES256 / RS384 / RS512 /
/// PS256 as additive follow-ons tracked alongside the server-side
/// algorithm-list expansion. `None` here surfaces upstream as
/// `IdTokenSignatureInvalid` — the operator's algorithm trust set
/// does not include the token's `alg`.
let private webCryptoAlgorithm (alg: string) : obj option =
    match alg with
    | "RS256" ->
        Some(
            createObj [
                "name" ==> "RSASSA-PKCS1-v1_5"
                "hash" ==> createObj [ "name" ==> "SHA-256" ]
            ]
        )
    | _ -> None

/// Verify an id_token signature via WebCrypto. Returns `false` on any
/// failure (key import, verify mismatch, JS exception) — the caller
/// surfaces this as `IdTokenSignatureInvalid` without distinguishing
/// the sub-cause. Distinguishing them would leak the validation path
/// to a tampering attacker; the audit log on the server side already
/// records the access-token rejection on the next protected request.
let private webCryptoVerify (alg: string) (jwk: Jwk) (signedBytes: byte[]) (signature: byte[]) : Async<bool> = async {
    match webCryptoAlgorithm alg with
    | None -> return false
    | Some algorithmDescriptor ->
        try
            let! key =
                subtleImportKey "jwk" jwk.RawJwk algorithmDescriptor false [| "verify" |]
                |> Async.AwaitPromise

            let! result =
                subtleVerify algorithmDescriptor key (toUint8Array signature) (toUint8Array signedBytes)
                |> Async.AwaitPromise

            return unbox<bool> result
        with _ ->
            return false
}

/// Resolve a JWK by `kid`, refreshing the cache once if the kid is
/// absent. Mirrors the server-side `OidcAuthProvider.validate` pattern
/// — handles key rotation between sign-ins on the same session.
let private resolveJwk (jwksUrl: string) (kid: string) : Async<Result<Jwk, AuthError>> = async {
    match! fetchJwks jwksUrl false with
    | Error _ -> return Error IdTokenSignatureInvalid
    | Ok keys ->
        match keys.TryFind kid with
        | Some jwk -> return Ok jwk
        | None ->
            match! fetchJwks jwksUrl true with
            | Error _ -> return Error IdTokenSignatureInvalid
            | Ok refreshed ->
                match refreshed.TryFind kid with
                | Some jwk -> return Ok jwk
                | None -> return Error IdTokenSignatureInvalid
}

/// Now in Unix seconds. Wraps `Date.now()` so tests can inject a
/// fixed clock via `validateIdTokenWith`.
[<Emit("Math.floor(Date.now() / 1000)")>]
let private nowEpochSeconds () : int64 = jsNative

/// Orchestrator with the runtime-specific verifier, JWKS fetcher, and
/// clock injected. Production wires `webCryptoVerify` + a JWKS fetcher
/// that resolves through OIDC discovery + `Date.now()`. Tests inject
/// stubs to drive the validation pipeline in .NET.
///
/// Pipeline mirrors the server-side `OidcAuthProvider.validate`:
/// parse → resolve key by kid → verify signature → claim chain.
let validateIdTokenWith
    (signatureVerifier: string -> Jwk -> byte[] -> byte[] -> Async<bool>)
    (jwksResolver: string -> Async<Result<Jwk, AuthError>>)
    (nowEpoch: int64)
    (cfg: OidcUIConfig)
    (idToken: string)
    : Async<Result<unit, AuthError>> =
    async {
        match parseIdToken idToken with
        | Error e -> return Error e
        | Ok parsed ->
            match parsed.Header.Kid with
            | None -> return Error IdTokenSignatureInvalid
            | Some kid ->
                match! jwksResolver kid with
                | Error e -> return Error e
                | Ok jwk ->
                    let! verified = signatureVerifier parsed.Header.Algorithm jwk parsed.SignedBytes parsed.Signature

                    if not verified then
                        return Error IdTokenSignatureInvalid
                    else
                        return
                            validateClaims
                                cfg.Issuer
                                cfg.ClientId
                                OidcIdTokenValidator.defaultClockSkewSeconds
                                nowEpoch
                                parsed.Claims
    }

/// Validate an id_token end-to-end against the issuer's JWKS via
/// WebCrypto. Production entry point — wires the Fable-only WebCrypto
/// verifier and the sessionStorage-cached JWKS fetcher. Returns
/// `Ok ()` on a fully-validated token, or a typed `AuthError` on the
/// first failure. The caller (`handleCallback`) clears local state on
/// any `Error`.
let validateIdToken (cfg: OidcUIConfig) (idToken: string) : Async<Result<unit, AuthError>> = async {
    match! fetchDiscovery cfg.Issuer with
    | Error e -> return Error e
    | Ok doc ->
        match doc.JwksUri with
        | None -> return Error IdTokenSignatureInvalid
        | Some jwksUrl ->
            let resolver = resolveJwk jwksUrl
            return! validateIdTokenWith webCryptoVerify resolver (nowEpochSeconds ()) cfg idToken
}

let handleCallback (cfg: OidcUIConfig) : Async<Result<unit, AuthError>> = async {
    // Stage walk: `Booting → InCallback`. The shell's `useEffectOnce`
    // jumps straight here when `isCallbackUrl` matched; `Booting` is
    // the brief render before that decision; tracing the edge makes
    // the entry visible alongside the cold-start arm.
    walk Booting InCallback

    let callback = readCallbackParams ()
    let pending = readPendingSignIn ()

    let inputs: CallbackInputs = {
        UrlCode = callback.Code
        UrlState = callback.State
        UrlError = callback.Error
        UrlErrorDescription = callback.ErrorDescription
        StashedVerifier = pending.Verifier
        StashedState = pending.State
        StashedNonce = pending.Nonce
    }

    // Pure callback-start decision: issuer-error / missing code /
    // state mismatch / missing PKCE verifier. Exhaustively covered
    // by `OidcStateMachineTests`.
    match decideCallbackStart inputs with
    | Error e ->
        walk InCallback (Stage.Failed e)
        return Error e
    | Ok start ->
        match! fetchDiscovery cfg.Issuer with
        | Error e ->
            walk InCallback (Stage.Failed e)
            return Error e
        | Ok doc ->
            walk InCallback ExchangingCode

            match! exchangeCodeForTokens doc.TokenEndpoint cfg.ClientId cfg.RedirectUri start.Code start.Verifier with
            | Error e ->
                walk ExchangingCode (Stage.Failed e)
                return Error e
            | Ok tokens ->
                walk ExchangingCode ValidatingIdToken

                // Pure nonce-binding decision (Phase 3b / Cluster B1).
                // OIDC spec mandates that when nonce was sent on the
                // authorize request (we always do), the id_token's
                // `nonce` claim MUST match. Cases:
                //   (a) stashed nonce + matching id_token nonce: ok.
                //   (b) stashed nonce + mismatched or missing id_token
                //       nonce: reject (replay defence).
                //   (c) stashed nonce + no id_token at all: reject
                //       (issuer spec violation — we requested
                //       `openid`).
                // `decideNonceValidity` is injected with
                // `fixedTimeStringEquals` so the comparison resists
                // timing attacks; tests pass plain `(=)`.
                let nonceInputs: NonceInputs = {
                    StashedNonce = start.ExpectedNonce
                    IdTokenPresent = Option.isSome tokens.IdToken
                    IdTokenNonce = tokens.IdToken |> Option.bind readIdTokenNonce
                }

                match decideNonceValidity fixedTimeStringEquals nonceInputs with
                | Error e ->
                    clearAll ()
                    walk ValidatingIdToken (Stage.Failed e)
                    return Error e
                | Ok() ->
                    // Phase 3b.A — opt-in client-side id_token
                    // validation. When `cfg.ValidateIdToken = Some
                    // true` AND the issuer returned an id_token, verify
                    // signature + issuer + audience + expiry before
                    // persisting any local state. Failure → clear +
                    // surface the typed error; success / opt-out falls
                    // through to the persist path.
                    let! idTokenResult = async {
                        match cfg.ValidateIdToken, tokens.IdToken with
                        | Some true, Some idToken ->
                            let! r = validateIdToken cfg idToken
                            return r
                        | _ -> return Ok()
                    }

                    match idTokenResult with
                    | Error e ->
                        clearAll ()
                        walk ValidatingIdToken (Stage.Failed e)
                        return Error e
                    | Ok() ->
                        walk ValidatingIdToken PersistingTokens
                        persistTokens tokens.AccessToken tokens.RefreshToken
                        clearPendingSignIn ()

                        // Stage walk: `PersistingTokens → RebootingToRoot`.
                        // The reboot is a NAMED EDGE of the state machine,
                        // not a hidden side-effect at function exit. The
                        // target MUST be `origin + "/"`, not the current
                        // pathname: the pathname IS the redirect URI
                        // (`/auth/callback`), so reloading onto it
                        // re-satisfies `isCallbackUrl` on remount, the
                        // shell re-enters `handleCallback` with the
                        // `?code` already consumed, and the second pass
                        // fails with `MissingCode`. Landing on `/` makes
                        // the reboot take the `classifyStoredToken`
                        // branch instead, see the just-persisted token
                        // as fresh, and enter `Established`.
                        //
                        // A full-document reload is the serialisation
                        // point for the Elmish init fetches (ListModules,
                        // perms, flags) — a same-document
                        // `history.replaceState` leaves them having
                        // already raced ahead of `persistTokens` with no
                        // Bearer (401, never retried). `.replace` (not
                        // `.assign`) keeps the now-stale `?code` URL out
                        // of session history.
                        walk PersistingTokens RebootingToRoot
                        let cleanUrl = Browser.Dom.window.location.origin + "/"
                        Browser.Dom.window.location.replace cleanUrl
                        return Ok()
}

// ─── Phase 3: sign-out ───────────────────────────────────────────────

let signOut (cfg: OidcUIConfig) : Async<unit> = async {
    emitOk None "sign-out" None
    clearAll ()

    match! fetchDiscovery cfg.Issuer with
    | Ok doc ->
        match doc.EndSessionEndpoint with
        | Some endSessionEp ->
            let postLogoutUri = cfg.PostLogoutRedirectUri |> Option.defaultWith pageOrigin

            let query =
                createObj [ "client_id" ==> cfg.ClientId; "post_logout_redirect_uri" ==> postLogoutUri ]

            let url = endSessionEp + "?" + urlEncode query
            Browser.Dom.window.location.assign url
        | None ->
            // Issuer doesn't expose end_session_endpoint — local
            // sign-out only. Reload the origin so the shell re-renders
            // without a token.
            Browser.Dom.window.location.assign (pageOrigin ())
    | Error _ ->
        // Can't reach issuer, but local state is cleared. Fall back to
        // a reload so the shell re-renders.
        Browser.Dom.window.location.assign (pageOrigin ())
}

// ─── Phase 3: refresh access token ───────────────────────────────────

/// Exchange the stored refresh token for a fresh access token.
/// Returns `Error` with the refresh-token slot cleared if the issuer
/// rejects the refresh (the caller should treat this as "signed out").
///
/// Not called automatically yet — Phase 3b follow-up will add a
/// timer that refreshes before `expires_in` elapses. Callers that
/// observe a 401 from the server can invoke this manually in the
/// meantime.
let refreshAccessToken (cfg: OidcUIConfig) : Async<Result<unit, AuthError>> = async {
    emitOk None "refresh-start" None

    match getRefreshToken () with
    | None ->
        let e = TokenExchangeFailed "no refresh token stored"
        emitErr None "refresh-no-token" (diagnose e)
        return Error e
    | Some refreshToken ->
        match! fetchDiscovery cfg.Issuer with
        | Error e ->
            emitErr None "refresh-discovery-failed" (diagnose e)
            return Error e
        | Ok doc ->
            try
                let form =
                    createObj [
                        "grant_type" ==> "refresh_token"
                        "client_id" ==> cfg.ClientId
                        "refresh_token" ==> refreshToken
                    ]

                let opts =
                    createObj [
                        "method" ==> "POST"
                        "headers"
                        ==> createObj [ "Content-Type" ==> "application/x-www-form-urlencoded" ]
                        "body" ==> urlEncode form
                    ]

                let! response = fetchPost doc.TokenEndpoint opts |> Async.AwaitPromise
                let ok = unbox<bool> response?ok
                let! json = response?json () |> Async.AwaitPromise

                if not ok then
                    // Refresh rejected — clear refresh token so the
                    // next sign-in attempt is fresh.
                    clearAll ()
                    let err = getStringField json "error" |> Option.defaultValue "refresh rejected"
                    let e = TokenExchangeFailed err
                    emitErr None "refresh-rejected" (diagnose e)
                    return Error e
                else
                    match getStringField json "access_token" with
                    | None ->
                        let e = TokenExchangeFailed "refresh response missing access_token"
                        emitErr None "refresh-missing-access-token" (diagnose e)
                        return Error e
                    | Some at ->
                        // Some issuers rotate the refresh token on
                        // each refresh; honour it if present.
                        let newRefresh = getStringField json "refresh_token"
                        persistTokens at newRefresh
                        emitOk None "refresh-ok" None
                        return Ok()
            with ex ->
                let e = NetworkError ex.Message
                emitErr None "refresh-network-error" (diagnose e)
                return Error e
}

// ─── Phase 3b follow-up: automatic pre-expiry refresh timer ───────────
//
// `refreshAccessToken` above is the mechanism; this is the policy. A
// single browser timer is armed whenever the shell enters its signed-in
// state and re-arms itself after every successful refresh, so the
// access token is renewed at `exp − safetyMargin` instead of waiting
// for a request to 401. The expiry is read from the access token's own
// `exp` claim rather than the token endpoint's `expires_in`, so the
// cold-start path (token restored from a previous session, no
// `expires_in` in hand) is covered by the same code as the
// callback / post-refresh paths.

// `atob` declared near the top of the file (id_token nonce reader
// needs it before the F# compiler reaches this block).

[<Emit("setTimeout($0, $1)")>]
let private jsSetTimeout (cb: unit -> unit) (ms: float) : float = jsNative

[<Emit("clearTimeout($0)")>]
let private jsClearTimeout (handle: float) : unit = jsNative

[<Emit("Date.now()")>]
let private nowMs () : float = jsNative

[<Literal>]
let private refreshSafetyMarginSeconds = 60.0

[<Literal>]
let private minRefreshDelaySeconds = 5.0

[<Literal>]
let private fallbackRefreshSeconds = 300.0

/// Unix-seconds `exp` from a JWT access token, if it can be read. Pure
/// best-effort: any malformed segment / missing claim yields `None` and
/// the caller falls back to a fixed cadence.
let private tokenExpiryEpochSeconds (token: string) : float option =
    try
        let parts = token.Split('.')

        if parts.Length < 2 then
            None
        else
            let payload = parts[1].Replace('-', '+').Replace('_', '/')
            let pad = (4 - payload.Length % 4) % 4
            let json = atob (payload + System.String('=', pad))
            let parsed = JS.JSON.parse json
            let expValue = parsed?exp

            if isNullOrUndefined expValue then
                None
            else
                Some(unbox<float> expValue)
    with _ ->
        None

// ─── Phase 3b.B — stored-token validity probe ────────────────────────
//
// Pre-Phase 3b.B the shells short-circuited to `SignedIn` whenever
// `OidcTokenStore.hasAccessToken ()` returned true — presence only,
// no validity check. A consumer that switched OIDC providers (Clerk →
// Entra, issuer rotation, tenant migration) left every previously
// signed-in user with a token the new server-side validator rejects
// on every request, but the client still rendered the shell — yielding
// a 401 storm behind a misleadingly populated UI. `classifyStoredToken`
// extracts the `iss` + `exp` claims from the stored token (if it is a
// JWT) and reports `StaleJwt` when they don't match the active config;
// callers refresh-or-clear-and-resignin in response. Opaque (non-JWT)
// access tokens carry no claims and fall through as `OpaqueToken` —
// the server's auth provider validates them via introspection / JWKS
// and will 401 a stale opaque token at request time, same as today.
//
// The check is local-only: no network round-trip, ~µs per render.

type StoredTokenState =
    | NoToken
    | OpaqueToken
    | FreshJwt
    | StaleJwt

/// Subset of JWT payload claims the freshness check needs. Decoders
/// return this in lieu of the full parsed payload so the test-doubles
/// entry below can drive the decision logic from .NET-side Expecto
/// without dragging in a browser base64 / JSON shim.
type JwtClaimsExtract = {
    Iss: string option
    Exp: float option
}

/// Test-doubles entry point for `classifyStoredToken`. Takes the stored
/// token + a payload-decoding function explicitly so .NET-side tests
/// can drive the decision logic without browser APIs (`atob`,
/// `JS.JSON.parse`, `localStorage`-backed `getAccessToken`). The
/// production browser wrapper below supplies a decoder built from
/// `atob` + `JS.JSON.parse`; tests supply a stub.
///
/// The decoder returns `Some` when the token's payload was successfully
/// base64-decoded *and* JSON-parsed (regardless of whether the claims
/// of interest are present — those are nested option fields on the
/// extract record). It returns `None` when the payload could not be
/// decoded at all. The canonical case is Microsoft Graph's encrypted
/// "nord" access tokens — three dot-segments (so a naive segment-count
/// check sees a JWT shape) but the middle segment is encrypted, so
/// base64-then-JSON-parse fails. A 3-segment-but-non-decodable token
/// is classified as `OpaqueToken` rather than `StaleJwt`: the
/// server-side validator decides validity, and the client does not
/// trigger a doomed refresh against a token whose claims it never read.
///
/// Issuer comparison trims trailing slashes on both sides so a config
/// authored without `/` doesn't mis-match a token whose `iss` includes
/// it (and vice versa) — a known Entra / Auth0 quirk.
let classifyStoredTokenWith
    (cfg: OidcUIConfig)
    (nowEpochSeconds: float)
    (storedToken: string option)
    (tryDecodePayload: string -> JwtClaimsExtract option)
    : StoredTokenState =
    match storedToken with
    | None -> NoToken
    | Some token ->
        let parts = token.Split('.')

        if parts.Length < 3 then
            OpaqueToken
        else
            match tryDecodePayload token with
            | None ->
                // 3-segment-shaped but payload not decodable —
                // encrypted-body JWE / Microsoft Graph "nord" tokens /
                // arbitrary opaque tokens that happen to contain dots.
                // Defer validity to the server rather than triggering a
                // doomed refresh.
                OpaqueToken
            | Some claims ->
                let issuerOk =
                    match claims.Iss with
                    | None -> false
                    | Some iss -> iss.TrimEnd('/') = cfg.Issuer.TrimEnd('/')

                let expOk =
                    match claims.Exp with
                    | None -> false
                    | Some e -> e > nowEpochSeconds

                if issuerOk && expOk then FreshJwt else StaleJwt

/// Classify the access token currently in `OidcTokenStore` against the
/// active `OidcUIConfig`. Returns `NoToken` when nothing is stored,
/// `OpaqueToken` for non-JWT shapes (handed off to the server),
/// `FreshJwt` when both `iss` matches the configured issuer AND `exp`
/// is in the future, `StaleJwt` otherwise. Shells use this on mount
/// to decide between SignedIn (Fresh / Opaque), refresh attempt
/// (Stale), or SignedOut (None).
let classifyStoredToken (cfg: OidcUIConfig) : StoredTokenState =
    let decodePayload (token: string) : JwtClaimsExtract option =
        try
            let parts = token.Split('.')

            if parts.Length < 2 then
                None
            else
                let payload = parts[1].Replace('-', '+').Replace('_', '/')
                let pad = (4 - payload.Length % 4) % 4
                let json = atob (payload + System.String('=', pad))
                let parsed = JS.JSON.parse json

                let iss =
                    let v = parsed?iss

                    if isNullOrUndefined v then None else Some(unbox<string> v)

                let exp =
                    let v = parsed?exp

                    if isNullOrUndefined v then None else Some(unbox<float> v)

                Some { Iss = iss; Exp = exp }
        with _ ->
            None

    let result =
        classifyStoredTokenWith cfg (nowMs () / 1000.0) (getAccessToken ()) decodePayload

    let label =
        match result with
        | NoToken -> "no-token"
        | OpaqueToken -> "opaque"
        | FreshJwt -> "fresh-jwt"
        | StaleJwt -> "stale-jwt"

    emitOk (readCorrelationId ()) (sprintf "classify-stored:%s" label) None
    result

// OIDC pre-expiry refresh timer — a single browser timer handle.
// Documented client-side mutable: a background-timer handle is not
// Elmish model state; it must survive React re-renders to stay
// cancellable, and at most one refresh loop may be in flight per page.
let mutable private refreshTimer: float option = None

/// Cancel any armed refresh timer. Idempotent; safe to call when none
/// is armed (sign-out, shell unmount, hard refresh failure).
let cancelRefresh () : unit =
    match refreshTimer with
    | Some handle ->
        jsClearTimeout handle
        refreshTimer <- None
    | None -> ()

/// Arm (or re-arm) the pre-expiry refresh timer for the currently
/// stored access token. `onExpired` is invoked if a scheduled refresh
/// fails irrecoverably (the issuer rejected the refresh token) so the
/// shell can drop back to the sign-in screen. No-op when no access
/// token is stored.
let rec scheduleRefresh (cfg: OidcUIConfig) (onExpired: unit -> unit) : unit =
    cancelRefresh ()

    match getAccessToken () with
    | None -> ()
    | Some token ->
        let delaySeconds =
            match tokenExpiryEpochSeconds token with
            | Some expEpoch ->
                let untilExpiry = expEpoch - nowMs () / 1000.0
                max (untilExpiry - refreshSafetyMarginSeconds) minRefreshDelaySeconds
            | None -> fallbackRefreshSeconds - refreshSafetyMarginSeconds

        let handle =
            jsSetTimeout
                (fun () ->
                    async {
                        match! refreshAccessToken cfg with
                        | Ok() -> scheduleRefresh cfg onExpired
                        | Error _ ->
                            cancelRefresh ()
                            onExpired ()
                    }
                    |> Async.StartImmediate)
                (delaySeconds * 1000.0)

        refreshTimer <- Some handle