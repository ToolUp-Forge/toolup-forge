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
//   SignedOut → SignInScreen (big button kicking off `beginSignIn`,
//               plus a second button when the config declares an
//               `OidcSecondaryFlow` — the "Sign up" affordance)
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

// Phase 751 — every screen here gained an additive `…With` variant
// taking the resolved `AuthMessages`, and the pre-751 entry point
// delegates to it with the built-in English catalog. The arities are
// deliberately NOT widened: these are public non-component render
// functions, so a widened arity reads as a REMOVAL in the public-API
// approval baseline and breaks every caller, for a parameter only a
// catalog-resolving caller can supply (444's recorded pattern, as
// `BootDegradation.bannerWith` / `ModuleBoundary.wrapWith` did).
//
// `OidcShell` below is a component, so it resolves the catalog with the
// ordinary hook and calls the `…With` forms.

let LoadingScreenWith (msgs: AuthMessages) : ReactElement =
    pageFrame [
        Html.div [ prop.className $"{Tokens.Text.secondary} text-sm"; prop.text msgs.SigningIn ]
    ]

let LoadingScreen () : ReactElement =
    LoadingScreenWith MessageCatalog.english.Auth

/// The sign-in screen with an optional SECOND button beside "Sign in"
/// — the generic dual-button ("Sign in / Sign up") affordance. The
/// secondary flow starts the same OIDC sign-in with extra
/// authorize-request parameters, so `onSecondary` is wired to the same
/// machinery as `onSignIn` and the callback path is untouched.
///
/// `None` renders exactly what `SignInScreen` renders — the secondary
/// button is not a hidden element, it is not emitted at all (GP 11).
let SignInScreenWith
    (msgs: AuthMessages)
    (secondary: OidcSecondaryFlow option)
    (onSignIn: unit -> unit)
    (onSecondary: unit -> unit)
    : ReactElement =
    let secondaryButton =
        match secondary with
        | Some flow ->
            Html.button [
                prop.className $"{Tokens.Button.secondary} w-full text-center"
                // The secondary flow's label is the DEPLOYMENT's own
                // wording, supplied on `OidcSecondaryFlow` — the catalog
                // does not own it and must not override it.
                prop.text flow.Label
                prop.onClick (fun _ -> onSecondary ())
            ]
        | None -> Html.none

    pageFrame [
        Html.h1 [
            prop.className "text-2xl font-semibold text-brand font-[Umami]"
            prop.text msgs.Welcome
        ]
        Html.p [
            prop.className $"{Tokens.Text.secondary} text-center"
            prop.text msgs.SignInPrompt
        ]
        Html.button [
            prop.className $"{Tokens.Button.primary} w-full"
            prop.text msgs.SignIn
            prop.onClick (fun _ -> onSignIn ())
        ]
        secondaryButton
    ]

let SignInScreenWithSecondary
    (secondary: OidcSecondaryFlow option)
    (onSignIn: unit -> unit)
    (onSecondary: unit -> unit)
    : ReactElement =
    SignInScreenWith MessageCatalog.english.Auth secondary onSignIn onSecondary

/// The single-button sign-in screen. Retained as the no-secondary-flow
/// entry point (and so a consumer rendering it directly is unaffected
/// by the affordance landing).
let SignInScreen (onSignIn: unit -> unit) : ReactElement =
    SignInScreenWithSecondary None onSignIn ignore

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

// ─── Shell wrapper ──────────────────────────────────────────────────
//
// `OidcShell` is the component handed to the SDK shell via the
// `AuthUIProvider.register "oidc"` handler. It holds the `AuthState`
// in local React state so no Elmish model pollution, and runs the
// callback handler exactly once on first mount when the URL matches
// the configured redirect URI.

[<ReactComponent>]
let OidcShell (cfg: OidcUIConfig) (shell: ReactElement) : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).Auth
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

    // One entry point for both buttons. The flows differ ONLY in the
    // extra authorize-request parameters: same PKCE / state / nonce
    // machinery, same redirect URI, same callback, same token path — so
    // a secondary flow needs no second code path here and nothing
    // downstream of the redirect can tell the two apart.
    //
    // `beginSignIn` passes `[]`, which is what `OidcClient.beginSignIn`
    // is defined as, so a config with no secondary flow behaves exactly
    // as it did before the affordance existed (GP 11).
    let beginFlow (extraAuthorizeParams: (string * string) list) =
        setAuthState Checking

        async {
            match! OidcClient.beginSignInWithExtras cfg extraAuthorizeParams with
            | Ok() -> ()
            | Error e -> setAuthState (Failed e)
        }
        |> Async.StartImmediate

    let beginSignIn () = beginFlow []

    let beginSecondaryFlow () =
        match cfg.SecondaryFlow with
        | Some flow -> beginFlow flow.ExtraAuthorizeParams
        | None -> ()

    match authState with
    | Checking -> LoadingScreenWith msgs
    | SignedIn -> shell
    | SignedOut -> SignInScreenWith msgs cfg.SecondaryFlow beginSignIn beginSecondaryFlow
    | Failed e -> ErrorScreenWith msgs e (fun () -> setAuthState SignedOut)

// ─── UserMenu — header sign-out trigger ──────────────────────────────
//
// Small convenience component apps can drop into a header if they want
// a sign-out button. Not auto-injected by the shell — the shell has no
// opinion on header UX.

[<ReactComponent>]
let UserMenu (cfg: OidcUIConfig) : ReactElement =
    // A component, so it reads the catalog with the ordinary hook rather
    // than needing a `…With` variant — and it is dropped INSIDE the app's
    // own header, where the shell's provider is already mounted.
    let msgs = (MessageCatalogProvider.useMessages ()).Auth

    Html.button [
        prop.className $"{Tokens.Button.secondary} text-sm"
        prop.text msgs.SignOut
        prop.onClick (fun _ -> OidcClient.signOut cfg |> Async.StartImmediate)
    ]