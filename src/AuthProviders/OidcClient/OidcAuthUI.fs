// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcAuthUI

open Feliz
open ToolUp.Platform
open Toolup.UIToolkit
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcTokenStore

// ─── Sign-in / loading / error screens ───────────────────────────────
//
// Three presentation states rendered by the shell wrapper:
//
//   Checking  → LoadingScreen (brief, on first render while we decide
//               whether this is a callback or a cold start)
//   SignedOut → SignInScreen (big button kicking off `beginSignIn`)
//   Failed e  → ErrorScreen (message + retry button back to SignedOut)
//
// The signed-in path renders the shell directly — there's no wrapper
// component for that case; the parent just returns `shell`.

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

let LoadingScreen () : ReactElement =
    pageFrame [
        Html.div [
            prop.className $"{Tokens.Text.secondary} text-sm"
            prop.text "Signing you in…"
        ]
    ]

let SignInScreen (onSignIn: unit -> unit) : ReactElement =
    pageFrame [
        Html.h1 [
            prop.className "text-2xl font-semibold text-brand font-['Umami']"
            prop.text "Welcome"
        ]
        Html.p [
            prop.className $"{Tokens.Text.secondary} text-center"
            prop.text "Sign in to continue."
        ]
        Html.button [
            prop.className $"{Tokens.Button.primary} w-full"
            prop.text "Sign in"
            prop.onClick (fun _ -> onSignIn ())
        ]
    ]

let ErrorScreen (err: AuthError) (onRetry: unit -> unit) : ReactElement =
    pageFrame [
        Html.h1 [
            prop.className "text-xl font-semibold text-brand font-['Umami']"
            prop.text "Sign-in failed"
        ]
        Html.p [
            prop.className $"{Tokens.Colours.error} text-center text-sm"
            prop.text (describeError err)
        ]
        Html.button [
            prop.className $"{Tokens.Button.primary} w-full"
            prop.text "Try again"
            prop.onClick (fun _ -> onRetry ())
        ]
    ]

// ─── Shell wrapper ──────────────────────────────────────────────────
//
// `OidcShell` is the component handed to the SDK shell via the
// `AuthUIProvider.register "oidc"` handler. It holds the `AuthState`
// in local React state so no Elmish model pollution, and runs the
// callback handler exactly once on first mount when the URL matches
// the configured redirect URI.

[<ReactComponent>]
let OidcShell (cfg: OidcUIConfig) (shell: ReactElement) : ReactElement =
    let authState, setAuthState = React.useState Checking

    // Enter the signed-in state and arm the pre-expiry refresh timer.
    // If a later scheduled refresh is rejected by the issuer, the timer
    // drops the shell back to the sign-in screen.
    let enterSignedIn () =
        setAuthState SignedIn
        OidcClient.scheduleRefresh cfg (fun () -> setAuthState SignedOut)

    React.useEffectOnce (fun () ->
        async {
            if OidcClient.isCallbackUrl cfg then
                match! OidcClient.handleCallback cfg with
                | Ok() -> enterSignedIn ()
                | Error e -> setAuthState (Failed e)
            else
                // Phase 3b.B — classify the stored token before assuming
                // SignedIn. A token left over from a previous OIDC
                // provider (Clerk → Entra swap, tenant migration) carries
                // the wrong `iss`; an unrefreshed token is past `exp`.
                // Either way the server-side validator rejects it on
                // every request, and the pre-fix presence-only check
                // would have rendered the shell into a 401 storm.
                // `Fresh` / `Opaque` short-circuit straight through;
                // `Stale` attempts a refresh against the current issuer
                // (Clerk-era refresh tokens will be rejected) and falls
                // to `SignedOut` if the refresh fails.
                match OidcClient.classifyStoredToken cfg with
                | OidcClient.NoToken -> setAuthState SignedOut
                | OidcClient.FreshJwt
                | OidcClient.OpaqueToken -> enterSignedIn ()
                | OidcClient.StaleJwt ->
                    match! OidcClient.refreshAccessToken cfg with
                    | Ok() -> enterSignedIn ()
                    | Error _ ->
                        clearAll ()
                        setAuthState SignedOut
        }
        |> Async.StartImmediate)

    // Cancel the refresh timer if the shell unmounts (SPA teardown).
    // Sign-out navigates the page away, which discards browser timers
    // on its own, so this only matters for in-app unmounts. The effect
    // returns a cleanup thunk (the `unit -> unit -> unit` useEffectOnce
    // overload) which React invokes on unmount.
    React.useEffectOnce (fun () -> (fun () -> OidcClient.cancelRefresh ()))

    let beginSignIn () =
        setAuthState Checking

        async {
            match! OidcClient.beginSignIn cfg with
            | Ok() -> ()
            | Error e -> setAuthState (Failed e)
        }
        |> Async.StartImmediate

    match authState with
    | Checking -> LoadingScreen()
    | SignedIn -> shell
    | SignedOut -> SignInScreen beginSignIn
    | Failed e -> ErrorScreen e (fun () -> setAuthState SignedOut)

// ─── UserMenu — header sign-out trigger ──────────────────────────────
//
// Small convenience component apps can drop into a header if they want
// a sign-out button. Not auto-injected by the shell — the shell has no
// opinion on header UX.

[<ReactComponent>]
let UserMenu (cfg: OidcUIConfig) : ReactElement =
    Html.button [
        prop.className $"{Tokens.Button.secondary} text-sm"
        prop.text "Sign out"
        prop.onClick (fun _ -> OidcClient.signOut cfg |> Async.StartImmediate)
    ]