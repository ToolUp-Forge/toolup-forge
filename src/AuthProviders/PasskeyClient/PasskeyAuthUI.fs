// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Passkey.PasskeyAuthUI

open Feliz
open ToolUp.Platform
open Toolup.UIToolkit
open ToolUp.AuthProviders.Passkey.PasskeyClient

// ─── Sign-in / register / loading / error screens ────────────────────
//
// Presentation states rendered by the shell wrapper, mirroring
// OidcAuthUI. There is no password field anywhere — the whole point of
// the passkey flow is passwordless, phishing-resistant sign-in.

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

// Phase 751 — the two plain public render functions gained additive
// `…With` variants rather than a widened arity, which would read as a
// REMOVAL in the public-API approval baseline (444's recorded pattern).
// `SignInScreen` needs neither: it is already a component, so it reads
// the catalog with the ordinary hook and its arity is untouched.

let LoadingScreenWith (msgs: AuthMessages) : ReactElement =
    pageFrame [
        Html.div [ prop.className $"{Tokens.Text.secondary} text-sm"; prop.text msgs.SigningIn ]
    ]

let LoadingScreen () : ReactElement =
    LoadingScreenWith MessageCatalog.english.Auth

[<ReactComponent>]
let SignInScreen
    (cfg: PasskeyUIConfig)
    (onSignIn: string -> unit)
    (onRegister: string -> string -> unit)
    : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).Auth
    let username, setUsername = React.useState ""
    let bootstrapToken, setBootstrapToken = React.useState ""

    pageFrame [
        Html.h1 [
            prop.className "text-2xl font-semibold text-brand font-[Umami]"
            prop.text msgs.Welcome
        ]
        Html.p [
            prop.className $"{Tokens.Text.secondary} text-center"
            prop.text msgs.Passkey.SignInPrompt
        ]
        Html.input [
            prop.className "border rounded px-3 py-2 w-full"
            prop.placeholder msgs.Passkey.UsernamePlaceholder
            prop.value username
            prop.onChange (fun (v: string) -> setUsername v)
        ]
        Html.button [
            prop.className $"{Tokens.Button.primary} w-full"
            prop.text msgs.Passkey.SignIn
            prop.onClick (fun _ -> onSignIn username)
        ]
        if cfg.AllowRegistration then
            Html.div [
                prop.className "w-full flex flex-col gap-2 border-t pt-4 mt-2"
                prop.children [
                    Html.p [
                        prop.className $"{Tokens.Text.secondary} text-xs text-center"
                        prop.text msgs.Passkey.RegisterPrompt
                    ]
                    Html.input [
                        prop.className "border rounded px-3 py-2 w-full text-sm"
                        prop.placeholder msgs.Passkey.BootstrapTokenPlaceholder
                        prop.value bootstrapToken
                        prop.onChange (fun (v: string) -> setBootstrapToken v)
                    ]
                    Html.button [
                        prop.className $"{Tokens.Button.secondary} w-full"
                        prop.text msgs.Passkey.Register
                        prop.onClick (fun _ -> onRegister username bootstrapToken)
                    ]
                ]
            ]
    ]

let ErrorScreenWith (msgs: AuthMessages) (message: string) (onRetry: unit -> unit) : ReactElement =
    pageFrame [
        Html.h1 [
            prop.className "text-xl font-semibold text-brand font-[Umami]"
            prop.text msgs.SignInFailedHeading
        ]
        Html.p [
            prop.className $"{Tokens.Colours.error} text-center text-sm"
            prop.text message
        ]
        Html.button [
            prop.className $"{Tokens.Button.primary} w-full"
            prop.text msgs.TryAgain
            prop.onClick (fun _ -> onRetry ())
        ]
    ]

let ErrorScreen (message: string) (onRetry: unit -> unit) : ReactElement =
    ErrorScreenWith MessageCatalog.english.Auth message onRetry

// ─── Shell wrapper ───────────────────────────────────────────────────
//
// `PasskeyShell` is the component handed to the SDK shell via the
// `AuthUIProvider` "passkey" handler. Holds `PasskeyAuthState` in React-
// local state (no Elmish model pollution) and drives the ceremonies.

[<ReactComponent>]
let PasskeyShell (cfg: PasskeyUIConfig) (shell: ReactElement) : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).Auth
    let authState, setAuthState = React.useState Checking

    React.useEffectOnce (fun () ->
        // Cold start: a stored session token short-circuits straight to
        // the shell; the server rejects a stale/expired token on the
        // first request, which the shell's own 401 handling surfaces.
        if hasSession () then
            setAuthState SignedIn
        else
            setAuthState SignedOut)

    let doSignIn (username: string) =
        setAuthState Checking

        async {
            match! signIn cfg username with
            | Ok() -> setAuthState SignedIn
            | Error e -> setAuthState (Failed e)
        }
        |> Async.StartImmediate

    let doRegister (username: string) (bootstrapToken: string) =
        setAuthState Checking

        async {
            match! register cfg username username "" bootstrapToken with
            | Ok() -> setAuthState SignedIn
            | Error e -> setAuthState (Failed e)
        }
        |> Async.StartImmediate

    match authState with
    | Checking -> LoadingScreenWith msgs
    | SignedIn -> shell
    | SignedOut -> SignInScreen cfg doSignIn doRegister
    | Failed e -> ErrorScreenWith msgs e (fun () -> setAuthState SignedOut)

// ─── UserMenu — header sign-out trigger ──────────────────────────────

[<ReactComponent>]
let UserMenu () : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).Auth

    Html.button [
        prop.className $"{Tokens.Button.secondary} text-sm"
        prop.text msgs.SignOut
        prop.onClick (fun _ ->
            signOut ()
            Browser.Dom.window.location.reload ())
    ]