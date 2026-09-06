// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityAuthUI

open Feliz
open ToolUp.Platform
open Toolup.UIToolkit
open ToolUp.AuthProviders.Oidc
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcTokenStore
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig

// ─── Google Identity Services sign-in shell ──────────────────────────
//
// Same four presentation states as `OidcAuthUI` — Checking / SignedIn /
// SignedOut / Failed — deliberately, because a deployment may run both
// entry points and a user should not be able to tell which shell they
// landed in until the sign-in control itself renders.
//
// What differs is only the SignedOut screen: instead of a "Sign in"
// button that redirects, GIS renders its own branded button into a
// container this component owns, and (opt-in) shows the One Tap prompt.
// The signed-in path is identical, because the session is identical —
// see the bridge note in `GoogleIdentityClient.fs`.

let private pageFrame (children: ReactElement list) : ReactElement =
    Html.div [
        prop.className "w-screen min-h-screen bg-bg-light font-sans flex items-center justify-center"
        prop.children [
            Html.div [
                prop.className
                    "bg-white rounded-lg shadow-md px-10 py-8 max-w-md w-full flex flex-col items-center gap-4"
                prop.children children
            ]
        ]
    ]

// The `…With` / bare pairing below is Phase 444's recorded pattern and
// `OidcAuthUI`'s: the `…With` variant takes the resolved catalog section,
// the bare arity delegates to it with the built-in English catalog, and
// no existing arity widens (a widened arity reads as a baseline removal).
// Phase 751.C swept the other four auth packages this way; this one was
// missed, and Phase 758's gate is what found it.

let LoadingScreenWith (msgs: AuthMessages) : ReactElement =
    pageFrame [
        Html.div [ prop.className $"{Tokens.Text.secondary} text-sm"; prop.text msgs.SigningIn ]
    ]

let LoadingScreen () : ReactElement =
    LoadingScreenWith MessageCatalog.english.Auth

let ErrorScreenWith (msgs: AuthMessages) (err: AuthError) (onRetry: unit -> unit) : ReactElement =
    pageFrame [
        Html.h1 [
            prop.className "text-xl font-semibold text-brand font-[Umami]"
            prop.text msgs.SignInFailedHeading
        ]
        Html.p [
            prop.className $"{Tokens.Colours.error} text-center text-sm"
            prop.text (describeErrorWith msgs.Errors err)
        ]
        Html.button [
            prop.className $"{Tokens.Button.primary} w-full"
            prop.text msgs.TryAgain
            prop.onClick (fun _ -> onRetry ())
        ]
    ]

let ErrorScreen (err: AuthError) (onRetry: unit -> unit) : ReactElement =
    ErrorScreenWith MessageCatalog.english.Auth err onRetry

/// User-facing note when the GIS library itself could not load. The
/// developer-facing advisory (which names the CSP contributor to
/// compose) goes to the console from the script loader — this text
/// stays deliberately non-diagnostic, matching the SDK's stance on
/// auth-surface error strings.
let private scriptUnavailableNote (msgs: AuthMessages) : ReactElement =
    Html.p [
        prop.className $"{Tokens.Colours.error} text-center text-sm"
        prop.text msgs.ProviderUnavailable
    ]

// ─── Shell wrapper ──────────────────────────────────────────────────

[<ReactComponent>]
let GoogleIdentityShell (config: GoogleIdentityUIConfig) (shell: ReactElement) : ReactElement =
    let oidcConfig = GoogleIdentityUIConfig.toOidcUIConfig config
    let msgs = (MessageCatalogProvider.useMessages ()).Auth
    let authState, setAuthState = React.useState Checking
    let scriptFailed, setScriptFailed = React.useState false
    let buttonRef = React.useElementRef ()

    let enterSignedIn () =
        setAuthState SignedIn
        // The timer arms exactly as it does for a redirect-flow
        // session. For a GIS-only session its attempt at expiry finds
        // no refresh token and drops the shell to SignedOut — which is
        // the correct behaviour for a credential that cannot be
        // renewed, and is why no separate expiry path exists here.
        OidcClient.scheduleRefresh oidcConfig (fun () -> setAuthState SignedOut)

    React.useEffectOnce (fun () ->
        async {
            // Cold start. Identical classification to `OidcAuthUI`: a
            // token left by a previous provider carries the wrong
            // `iss`, an unrefreshed one is past `exp`, and either way
            // the server rejects it on every request.
            match OidcClient.classifyStoredToken oidcConfig with
            | OidcClient.NoToken -> setAuthState SignedOut
            | OidcClient.FreshJwt
            | OidcClient.OpaqueToken -> enterSignedIn ()
            | OidcClient.StaleJwt ->
                // A deployment running BOTH entry points may hold a
                // refresh token from a redirect-flow sign-in, so the
                // refresh is attempted rather than assumed impossible.
                // A GIS-only session has none and lands in the Error
                // branch immediately.
                match! OidcClient.refreshAccessToken oidcConfig with
                | Ok() -> enterSignedIn ()
                | Error _ ->
                    clearAll ()
                    setAuthState SignedOut
        }
        |> Async.StartImmediate)

    React.useEffectOnce (fun () -> (fun () -> OidcClient.cancelRefresh ()))

    // Bootstrap GIS only once the sign-in screen is actually on
    // screen: a returning user with a live session never loads the
    // library at all, and a deployment that never composes this
    // companion never reaches this file (GP 13, one layer down).
    React.useEffect (
        (fun () ->
            match authState with
            | SignedOut ->
                GoogleIdentityScriptLoader.ensureLoaded
                    (fun () ->
                        GoogleIdentityClient.initialize config (fun credential ->
                            match GoogleIdentityClient.acceptCredential oidcConfig config.Nonce credential with
                            | Ok() -> enterSignedIn ()
                            | Error err -> setAuthState (Failed err))

                        match buttonRef.current with
                        | Some node -> GoogleIdentityClient.renderButton (node :> obj) config
                        | None -> ()

                        if config.OneTap then
                            GoogleIdentityClient.promptOneTap ())
                    (fun () -> setScriptFailed true)
            | _ -> ()),
        [| box authState |]
    )

    let signInScreen () =
        pageFrame [
            Html.h1 [
                prop.className "text-2xl font-semibold text-brand font-[Umami]"
                prop.text (config.Heading |> Option.defaultValue msgs.Welcome)
            ]
            Html.p [
                prop.className $"{Tokens.Text.secondary} text-center"
                prop.text (config.Subheading |> Option.defaultValue msgs.SignInPrompt)
            ]
            // GIS renders its own markup into this container; the SDK
            // supplies position and nothing else.
            Html.div [ prop.className "flex justify-center w-full"; prop.ref buttonRef ]
            if scriptFailed then
                scriptUnavailableNote msgs
        ]

    match authState with
    | Checking -> LoadingScreenWith msgs
    | SignedIn -> shell
    | SignedOut -> signInScreen ()
    | Failed err ->
        ErrorScreenWith msgs err (fun () ->
            setScriptFailed false
            setAuthState SignedOut)

// ─── Public surface ──────────────────────────────────────────────────

/// Build a wrapper suitable for
/// `ClientConfig.AuthUI = CustomAuthUI { Wrap = GoogleIdentityAuthUI.wrap config }`.
/// The registry route (`GoogleIdentityRegister.authUI` +
/// `GoogleIdentityRegister.handler`) is preferred — this exists for a
/// deployment already composing a custom wrapper chain.
let wrap (config: GoogleIdentityUIConfig) (shell: ReactElement) : ReactElement = GoogleIdentityShell config shell

/// Header sign-out trigger. Clears GIS's auto-select state before the
/// shared sign-out so a returning visitor is not silently signed back
/// in by One Tap on the next page load — the ordering matters, and
/// getting it wrong makes sign-out look broken.
[<ReactComponent>]
let UserMenu (config: GoogleIdentityUIConfig) : ReactElement =
    let oidcConfig = GoogleIdentityUIConfig.toOidcUIConfig config
    let msgs = (MessageCatalogProvider.useMessages ()).Auth

    Html.button [
        prop.className $"{Tokens.Button.secondary} text-sm"
        prop.text msgs.SignOut
        prop.onClick (fun _ ->
            GoogleIdentityClient.disableAutoSelect ()
            OidcClient.signOut oidcConfig |> Async.StartImmediate)
    ]