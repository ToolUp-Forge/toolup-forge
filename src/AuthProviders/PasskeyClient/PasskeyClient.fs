// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyClient

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── 443.C — WebAuthn ceremony client (zero npm deps) ────────────────
//
// The browser half of the passkey flow. Drives `navigator.credentials`
// (a WebCrypto-era browser API — no npm package) against the server
// companion's ceremony endpoints, exactly mirroring the OidcClient
// precedent (native browser primitives via `[<Emit>]` + `Async.AwaitPromise`,
// no third-party runtime).
//
// Flow (registration): POST {ApiBase}/register/begin → the server's Fido2
// options JSON → convert its base64url buffers → navigator.credentials
// .create → serialise the attestation → POST /register/complete?challenge=
// → the minted platform session JWT → `UserSession.setAuthToken`. Assertion
// is symmetric over /assert/{begin,complete} + navigator.credentials.get.
//
// The base64url <-> ArrayBuffer conversions and the raw-response
// serialisation live in small `[<Emit>]` shims because they operate on
// live `ArrayBuffer` / `PublicKeyCredential` objects the WebAuthn API
// hands back; everything above them is ordinary F# orchestration.

// ─── Auth state (React-local, mirrors OidcClient's AuthState) ────────

type PasskeyAuthState =
    | Checking
    | SignedIn
    | SignedOut
    | Failed of string

// ─── Native browser shims ────────────────────────────────────────────

[<Emit("fetch($0, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: $1 })")>]
let private fetchPost (url: string) (body: string) : JS.Promise<obj> = jsNative

[<Emit("navigator.credentials.create({ publicKey: $0 })")>]
let private credentialsCreate (options: obj) : JS.Promise<obj> = jsNative

[<Emit("navigator.credentials.get({ publicKey: $0 })")>]
let private credentialsGet (options: obj) : JS.Promise<obj> = jsNative

/// True when this browser exposes the WebAuthn API at all.
[<Emit("(typeof window !== 'undefined' && !!(window.PublicKeyCredential) && !!(navigator.credentials))")>]
let isSupported () : bool = jsNative

// Parse the server's Fido2 registration-options JSON and convert the
// base64url `challenge` / `user.id` / `excludeCredentials[].id` fields
// into the `Uint8Array` buffers `navigator.credentials.create` requires.
[<Emit("""(function(j){var o=JSON.parse(j);var d=function(s){return Uint8Array.from(atob(String(s).replace(/-/g,'+').replace(/_/g,'/')),function(c){return c.charCodeAt(0)})};o.challenge=d(o.challenge);if(o.user&&o.user.id){o.user.id=d(o.user.id)}if(o.excludeCredentials){o.excludeCredentials=o.excludeCredentials.map(function(c){c.id=d(c.id);return c})}return o})($0)""")>]
let private prepareCreateOptions (optionsJson: string) : obj = jsNative

// Parse the server's assertion-options JSON and convert its base64url
// buffers for `navigator.credentials.get`.
[<Emit("""(function(j){var o=JSON.parse(j);var d=function(s){return Uint8Array.from(atob(String(s).replace(/-/g,'+').replace(/_/g,'/')),function(c){return c.charCodeAt(0)})};o.challenge=d(o.challenge);if(o.allowCredentials){o.allowCredentials=o.allowCredentials.map(function(c){c.id=d(c.id);return c})}return o})($0)""")>]
let private prepareRequestOptions (optionsJson: string) : obj = jsNative

// Serialise a `PublicKeyCredential` from an attestation ceremony into the
// `AuthenticatorAttestationRawResponse` wire shape Fido2NetLib expects
// (base64url buffers).
[<Emit("""(function(c){var e=function(b){return btoa(String.fromCharCode.apply(null,new Uint8Array(b))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'')};return JSON.stringify({id:c.id,rawId:e(c.rawId),type:c.type,extensions:c.getClientExtensionResults(),response:{attestationObject:e(c.response.attestationObject),clientDataJSON:e(c.response.clientDataJSON)}})})($0)""")>]
let private serializeAttestation (credential: obj) : string = jsNative

// Serialise a `PublicKeyCredential` from an assertion ceremony into the
// `AuthenticatorAssertionRawResponse` wire shape.
[<Emit("""(function(c){var e=function(b){return b?btoa(String.fromCharCode.apply(null,new Uint8Array(b))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,''):null};return JSON.stringify({id:c.id,rawId:e(c.rawId),type:c.type,extensions:c.getClientExtensionResults(),response:{authenticatorData:e(c.response.authenticatorData),clientDataJSON:e(c.response.clientDataJSON),signature:e(c.response.signature),userHandle:e(c.response.userHandle)}})})($0)""")>]
let private serializeAssertion (credential: obj) : string = jsNative

// Build the begin-ceremony request body, including only the fields the
// caller supplied (empty / null values omitted so the server's option
// fields resolve to `None`). Keys are case-insensitive server-side.
[<Emit("""JSON.stringify(Object.fromEntries(Object.entries({username:$0,displayName:$1,email:$2,bootstrapToken:$3}).filter(function(e){return e[1]!=null&&e[1]!==''})))""")>]
let private registerBody (username: string) (displayName: string) (email: string) (bootstrapToken: string) : string =
    jsNative

[<Emit("""JSON.stringify(Object.fromEntries(Object.entries({username:$0}).filter(function(e){return e[1]!=null&&e[1]!==''})))""")>]
let private assertBody (username: string) : string = jsNative

// ─── Response helpers ────────────────────────────────────────────────

let private responseOk (resp: obj) : bool = unbox resp?ok

let private errorFrom (resp: obj) : Async<string> = async {
    try
        let! json = resp?json () |> Async.AwaitPromise
        let message: string = unbox json?error

        return
            if System.String.IsNullOrEmpty message then
                "The passkey ceremony was rejected."
            else
                message
    with _ ->
        return "The passkey ceremony was rejected."
}

// ─── Ceremony orchestration ──────────────────────────────────────────

let private runCeremony
    (apiBase: string)
    (beginPath: string)
    (completePath: string)
    (beginBody: string)
    (isRegistration: bool)
    : Async<Result<unit, string>> =
    async {
        try
            if not (isSupported ()) then
                return Error "This browser does not support passkeys (WebAuthn)."
            else
                let! beginResp = fetchPost (apiBase + beginPath) beginBody |> Async.AwaitPromise

                if not (responseOk beginResp) then
                    let! msg = errorFrom beginResp
                    return Error msg
                else
                    let! beginJson = beginResp?json () |> Async.AwaitPromise
                    let challengeId: string = unbox beginJson?ChallengeId
                    let optionsJson: string = unbox beginJson?OptionsJson

                    let! credential =
                        if isRegistration then
                            credentialsCreate (prepareCreateOptions optionsJson) |> Async.AwaitPromise
                        else
                            credentialsGet (prepareRequestOptions optionsJson) |> Async.AwaitPromise

                    let responseJson =
                        if isRegistration then
                            serializeAttestation credential
                        else
                            serializeAssertion credential

                    let completeUrl = apiBase + completePath + "?challenge=" + challengeId
                    let! completeResp = fetchPost completeUrl responseJson |> Async.AwaitPromise

                    if not (responseOk completeResp) then
                        let! msg = errorFrom completeResp
                        return Error msg
                    else
                        let! tokenJson = completeResp?json () |> Async.AwaitPromise
                        let token: string = unbox tokenJson?Token
                        UserSession.setAuthToken token
                        return Ok()
        with ex ->
            // navigator.credentials rejects (user cancelled, no
            // authenticator, timeout) land here — surface a generic,
            // non-probing message.
            return
                Error(
                    if System.String.IsNullOrEmpty ex.Message then
                        "Passkey ceremony failed or was cancelled."
                    else
                        ex.Message
                )
    }

/// Enrol a new passkey for `username` (server-gated: invite / session /
/// bootstrap). On success the browser is signed in (session token
/// stored). `bootstrapToken` is empty unless bootstrapping a fresh
/// deployment's first credential.
let register
    (cfg: PasskeyUIConfig)
    (username: string)
    (displayName: string)
    (email: string)
    (bootstrapToken: string)
    : Async<Result<unit, string>> =
    runCeremony
        cfg.ApiBase
        "/register/begin"
        "/register/complete"
        (registerBody username displayName email bootstrapToken)
        true

/// Sign in with an existing passkey. `username` may be empty for a
/// discoverable-credential (usernameless) flow.
let signIn (cfg: PasskeyUIConfig) (username: string) : Async<Result<unit, string>> =
    runCeremony cfg.ApiBase "/assert/begin" "/assert/complete" (assertBody username) false

/// True when the browser already holds a session token — the shell can
/// render directly without a ceremony.
let hasSession () : bool =
    UserSession.getAuthToken () |> Option.isSome

/// Clear the stored session token (sign out).
let signOut () : unit = UserSession.clearAuthToken ()