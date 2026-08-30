// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityClient

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcTokenStore
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig

// ─── GIS credential → OIDC session bridge ────────────────────────────
//
// A Google Identity Services sign-in returns ONE value: a `credential`
// field on the callback payload holding an **id_token** — an RS256 JWT
// signed by Google, carrying `iss` / `aud` / `exp` / `sub` / `email`.
// There is no access token and, decisively, no refresh token: the
// credential flow has no token endpoint to exchange against.
//
// The bridge admits that id_token to the SAME store the redirect flow
// writes (`OidcTokenStore.persistTokens`, i.e. `UserSession.setAuthToken`
// → `localStorage["toolup-auth-token"]` → the bearer on every
// Fable.Remoting request). That is the whole of the "one session shape"
// requirement: `classifyStoredToken`, `signOut`, and the pre-expiry
// refresh timer take the projected `OidcUIConfig` and behave exactly as
// they do for a redirect-flow session. No parallel session machinery
// exists, and none is wanted.
//
// TWO CONSEQUENCES worth stating plainly, because they are properties
// of Google's flow rather than of this code:
//
//  1. The bearer is a REAL JWT, so `classifyStoredToken` reports
//     `FreshJwt` for a GIS session where the Google redirect flow
//     reports `OpaqueToken` (Google's access tokens are always opaque —
//     `PresetKind.expectsDecodableAccessToken Google` is `false`). The
//     server validates it against Google's JWKS with no extra wiring.
//  2. There is NO refresh token, so a GIS session cannot be renewed
//     silently. The refresh timer arms as usual, its attempt at expiry
//     finds no refresh token, and the shell drops to the sign-in screen
//     — a re-prompt roughly hourly. A deployment that needs long-lived
//     sessions uses the redirect flow with `access_type=offline`, which
//     is where Google issues refresh tokens. This is a documented
//     constraint of the credential flow, not a limitation the SDK can
//     engineer away.
//
// The validation below is deliberately the same shape as the redirect
// flow's id_token checks (issuer / audience / expiry / nonce), reusing
// `AuthError` so the UI renders one vocabulary of failure. It is a
// CLAIMS check, not a signature check: the credential arrives over an
// in-page callback from a script served by Google's own origin, and the
// server re-validates the signature against Google's JWKS on the first
// protected request. A claims mismatch here means the credential was
// never for this application, which is worth refusing before it becomes
// the bearer.

/// The claims the bridge reads out of a GIS credential. A subset, in
/// the `OidcClient.JwtClaimsExtract` spirit: decoders return this
/// instead of a parsed payload so the decision logic is exercisable
/// from .NET-side Expecto without `atob` / `JSON.parse` / a browser.
type GoogleCredentialClaims = {
    Iss: string option
    Aud: string option
    Exp: float option
    Nonce: string option
}

/// What the bridge decided about a credential response.
type CredentialOutcome =
    /// The credential is for this application and is live: admit it to
    /// the shared token store as the session bearer.
    | AcceptCredential of idToken: string
    /// The credential was absent, unreadable, or not addressed to this
    /// application. Carries the same `AuthError` vocabulary the
    /// redirect flow raises so one error screen serves both.
    | RejectCredential of error: AuthError

/// Clock-skew tolerance on the `exp` check, in seconds. Matches the
/// redirect flow's id_token validator so a credential is not accepted
/// at one entry point and refused at the other.
[<Literal>]
let clockSkewSeconds = 60.0

/// Normalise an issuer for comparison. Google's `iss` claim is emitted
/// BOTH as `https://accounts.google.com` and as the bare
/// `accounts.google.com` depending on the surface and its vintage, and
/// both are correct per Google's own documentation — so a scheme-strict
/// comparison rejects live, valid credentials. Trailing slashes are
/// tolerated for the same reason `classifyStoredToken` tolerates them.
let private normaliseIssuer (issuer: string) =
    let trimmed = issuer.Trim().TrimEnd('/')

    if trimmed.StartsWith "https://" then trimmed.Substring 8
    elif trimmed.StartsWith "http://" then trimmed.Substring 7
    else trimmed

/// Decide whether a GIS credential response may become this session's
/// bearer. Pure: every input the decision needs is a parameter, so the
/// rules are pinned by .NET-side tests over a stubbed GIS callback
/// rather than only by a browser someone remembered to open.
///
/// `expectedNonce` is the value passed to GIS `initialize`; `None`
/// means no nonce was sent and none is checked (Google does not
/// require one for the credential flow). `tryDecode` returns `None`
/// when the payload could not be base64-decoded and JSON-parsed at
/// all — unlike the stored-token classifier, which defers an
/// undecodable token to the server, an undecodable CREDENTIAL is
/// refused: we are deciding what to make the bearer, and a value we
/// cannot read is one we cannot bind to this application.
let evaluateCredentialWith
    (cfg: OidcUIConfig)
    (expectedNonce: string option)
    (nowEpochSeconds: float)
    (credential: string option)
    (tryDecode: string -> GoogleCredentialClaims option)
    : CredentialOutcome =
    match credential with
    | None -> RejectCredential MissingCode
    | Some raw when System.String.IsNullOrWhiteSpace raw -> RejectCredential MissingCode
    | Some raw ->
        let token = raw.Trim()

        if token.Split('.').Length < 3 then
            RejectCredential MalformedIdToken
        else
            match tryDecode token with
            | None -> RejectCredential MalformedIdToken
            | Some claims ->
                let issuerOk =
                    match claims.Iss with
                    | None -> false
                    | Some iss -> normaliseIssuer iss = normaliseIssuer cfg.Issuer

                let audienceOk =
                    match claims.Aud with
                    | None -> false
                    | Some aud -> aud = cfg.ClientId

                let live =
                    match claims.Exp with
                    | None -> false
                    | Some exp -> exp + clockSkewSeconds > nowEpochSeconds

                let nonceOk =
                    match expectedNonce with
                    | None -> true
                    | Some expected -> claims.Nonce = Some expected

                if not issuerOk then
                    RejectCredential IdTokenIssuerInvalid
                elif not audienceOk then
                    RejectCredential IdTokenAudienceInvalid
                elif not live then
                    RejectCredential IdTokenExpired
                elif not nonceOk then
                    RejectCredential NonceMismatch
                else
                    AcceptCredential token

// ─── Browser wrappers ────────────────────────────────────────────────

[<Emit("Date.now()")>]
let private nowMs () : float = jsNative

[<Emit("atob($0)")>]
let private atob (s: string) : string = jsNative

[<Emit("$0 === null || $0 === undefined")>]
let private isNullOrUndefined (value: obj) : bool = jsNative

/// Decode a GIS credential's payload segment in the browser. Mirrors
/// `OidcClient.classifyStoredToken`'s decoder: base64url → base64 with
/// padding restored, then `JSON.parse`.
let private decodeClaims (token: string) : GoogleCredentialClaims option =
    try
        let parts = token.Split('.')

        if parts.Length < 2 then
            None
        else
            let payload = parts[1].Replace('-', '+').Replace('_', '/')
            let pad = (4 - payload.Length % 4) % 4
            let json = atob (payload + System.String('=', pad))
            let parsed = JS.JSON.parse json

            let readString (name: string) =
                let v = parsed?(name)

                if isNullOrUndefined v then None else Some(unbox<string> v)

            let exp =
                let v = parsed?exp

                if isNullOrUndefined v then None else Some(unbox<float> v)

            Some {
                Iss = readString "iss"
                Aud = readString "aud"
                Exp = exp
                Nonce = readString "nonce"
            }
    with _ ->
        None

/// Browser entry point for `evaluateCredentialWith` — supplies the
/// clock and the base64/JSON decoder.
let evaluateCredential (cfg: OidcUIConfig) (expectedNonce: string option) (credential: string) : CredentialOutcome =
    evaluateCredentialWith cfg expectedNonce (nowMs () / 1000.0) (Some credential) decodeClaims

/// Admit a GIS credential to the shared OIDC token store as this
/// session's bearer, or return the typed reason it was refused. The
/// refresh-token slot is left empty — the credential flow issues none
/// (see the header note), so a later refresh attempt correctly reports
/// "signed out" rather than pretending to renew.
let acceptCredential (cfg: OidcUIConfig) (expectedNonce: string option) (credential: string) : Result<unit, AuthError> =
    match evaluateCredential cfg expectedNonce credential with
    | AcceptCredential idToken ->
        persistTokens idToken None
        Ok()
    | RejectCredential err -> Error err

// ─── GIS library interop ─────────────────────────────────────────────
//
// Every call below assumes `GoogleIdentityScriptLoader.ensureLoaded`
// has already invoked its `onReady` continuation; nothing here probes
// for the namespace, because a caller that skipped the loader has a
// wiring bug the SDK should not paper over.

[<Emit("window.google.accounts.id.initialize($0)")>]
let private gisInitialize (options: obj) : unit = jsNative

[<Emit("window.google.accounts.id.renderButton($0, $1)")>]
let private gisRenderButton (parent: obj) (options: obj) : unit = jsNative

[<Emit("$0.replaceChildren()")>]
let private clearChildren (parent: obj) : unit = jsNative

[<Emit("window.google.accounts.id.prompt()")>]
let private gisPrompt () : unit = jsNative

[<Emit("window.google && window.google.accounts && window.google.accounts.id ? window.google.accounts.id.disableAutoSelect() : undefined")>]
let private gisDisableAutoSelect () : unit = jsNative

/// Configure the GIS client and register the credential callback.
/// `onCredential` receives the raw credential string; the caller
/// decides what to do with it (the shell runs it through
/// `acceptCredential`).
let initialize (config: GoogleIdentityUIConfig) (onCredential: string -> unit) : unit =
    let baseFields: (string * obj) list = [
        "client_id" ==> config.ClientId
        "callback"
        ==> (fun (response: obj) -> onCredential (unbox<string> response?credential))
        "auto_select" ==> config.AutoSelect
        "cancel_on_tap_outside" ==> config.CancelOneTapOnTapOutside
        "use_fedcm_for_prompt" ==> config.UseFedCm
    ]

    let fields =
        match config.Nonce with
        | Some nonce -> baseFields @ [ "nonce" ==> nonce ]
        | None -> baseFields

    gisInitialize (createObj fields)

/// Render Google's branded button into `parent` (a DOM node). GIS
/// owns the markup — Google's brand guidelines require their rendered
/// button rather than a look-alike, which is the entire reason this
/// companion loads a vendor script at all.
///
/// The container is cleared first, because GIS APPENDS. Any path that
/// reaches the sign-in screen twice against a reused container —
/// Failed → "Try again" → SignedOut, or React's development-mode
/// double-invoked effects — would otherwise stack a second button
/// under the first.
let renderButton (parent: obj) (config: GoogleIdentityUIConfig) : unit =
    clearChildren parent

    let baseFields: (string * obj) list = [
        "type" ==> "standard"
        "theme" ==> GoogleIdentityUIConfig.themeValue config.ButtonTheme
        "size" ==> GoogleIdentityUIConfig.sizeValue config.ButtonSize
        "text" ==> GoogleIdentityUIConfig.textValue config.ButtonText
        "shape" ==> GoogleIdentityUIConfig.shapeValue config.ButtonShape
    ]

    let fields =
        match config.ButtonWidthPx with
        | Some width -> baseFields @ [ "width" ==> width ]
        | None -> baseFields

    gisRenderButton parent (createObj fields)

/// Show the One Tap prompt. Only called when `GoogleIdentityUIConfig.OneTap`
/// is set — the SDK never auto-prompts a deployment's users by default.
let promptOneTap () : unit = gisPrompt ()

/// Clear GIS's auto-select state so a subsequent visit does not sign
/// the user straight back in. Called on sign-out; inert when the GIS
/// library was never loaded.
let disableAutoSelect () : unit = gisDisableAutoSelect ()