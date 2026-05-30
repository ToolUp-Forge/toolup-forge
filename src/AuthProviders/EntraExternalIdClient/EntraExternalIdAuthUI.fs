// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.EntraExternalId.EntraExternalIdAuthUI

open Feliz
open ToolUp.Platform
open Toolup.UIToolkit
open ToolUp.AuthProviders.Oidc.OidcTypes
open ToolUp.AuthProviders.Oidc.OidcTokenStore
open ToolUp.AuthProviders.Oidc
open ToolUp.AuthProviders.EntraExternalId.EntraExternalIdClientConfig

// ─── Entra External ID sign-in shell ─────────────────────────────────
//
// Wraps the generic `ToolUp.AuthProviders.Oidc` client-side primitives
// (`beginSignIn`, `handleCallback`, `scheduleRefresh`, `signOut`) with
// an Entra-flavoured presentation:
//
//   - Default scopes include `offline_access` (required for refresh
//     tokens against External ID; the generic OIDC defaults omit it).
//   - When `SignUpPolicyId` is set, the sign-in screen surfaces a
//     "Sign up" button alongside "Sign in" that routes to the policy's
//     `oauth2/v2.0/authorize?p=<policyId>` endpoint.
//   - When `SignInPolicyId` is set, the sign-in button routes via that
//     policy explicitly.
//
// Wired into a deployment via `ClientConfig.AuthUI = CustomAuthUI {
// Wrap = EntraExternalIdAuthUI.wrap config }`. `CustomAuthUI` is the
// documented bypass extension point — no edit to the SDK `AuthUIMode`
// DU is required for this companion to be drop-in.

// ─── Presentation primitives ─────────────────────────────────────────

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

let private LoadingScreen () : ReactElement =
    pageFrame [
        Html.div [
            prop.className $"{Tokens.Text.secondary} text-sm"
            prop.text "Signing you in…"
        ]
    ]

let private SignInScreen
    (config: EntraExternalIdClientConfig)
    (onSignIn: unit -> unit)
    (onSignUp: unit -> unit)
    : ReactElement =
    let signUp =
        match config.SignUpPolicyId with
        | Some _ ->
            Html.button [
                prop.className $"{Tokens.Button.secondary} w-full text-center"
                prop.text "Sign up"
                prop.onClick (fun _ -> onSignUp ())
            ]
        | None -> Html.none

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
        signUp
    ]

let private ErrorScreen (err: AuthError) (onRetry: unit -> unit) : ReactElement =
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

[<ReactComponent>]
let EntraExternalIdShell (config: EntraExternalIdClientConfig) (shell: ReactElement) : ReactElement =
    let oidcConfig = EntraExternalIdClientConfig.toOidcUIConfig config
    let authState, setAuthState = React.useState Checking

    let enterSignedIn () =
        setAuthState SignedIn
        OidcClient.scheduleRefresh oidcConfig (fun () -> setAuthState SignedOut)

    React.useEffectOnce (fun () ->
        async {
            if OidcClient.isCallbackUrl oidcConfig then
                match! OidcClient.handleCallback oidcConfig with
                | Ok() -> enterSignedIn ()
                | Error e -> setAuthState (Failed e)
            else
                // Phase 3b.B — see OidcAuthUI for the rationale; the
                // External ID shell rides the same generic OIDC token
                // store, so the same stored-token-validity classification
                // applies. Stale token → refresh attempt → clear-and-
                // SignedOut on refresh failure.
                match OidcClient.classifyStoredToken oidcConfig with
                | OidcClient.NoToken -> setAuthState SignedOut
                | OidcClient.FreshJwt
                | OidcClient.OpaqueToken -> enterSignedIn ()
                | OidcClient.StaleJwt ->
                    match! OidcClient.refreshAccessToken oidcConfig with
                    | Ok() -> enterSignedIn ()
                    | Error _ ->
                        clearAll ()
                        setAuthState SignedOut
        }
        |> Async.StartImmediate)

    React.useEffectOnce (fun () -> (fun () -> OidcClient.cancelRefresh ()))

    // Phase 3d / Cluster B3 — sign-in routes through the configured
    // SignInPolicyId when set, dropping the URL through the same
    // PKCE + state + nonce machinery as the default authorize path.
    // Was previously silently ignored.
    let beginSignIn () =
        setAuthState Checking

        let extras =
            match config.SignInPolicyId with
            | Some policyId -> [ "p", policyId ]
            | None -> []

        async {
            match! OidcClient.beginSignInWithExtras oidcConfig extras with
            | Ok() -> ()
            | Error e -> setAuthState (Failed e)
        }
        |> Async.StartImmediate

    // Phase 3d / Cluster B2 — sign-up routes through the configured
    // SignUpPolicyId via the same beginSignInWithExtras machinery as
    // sign-in. Previously this was a static href to authorizeUrlForPolicy
    // which carried only `?p=<policyId>` — Entra rejected the request
    // for missing client_id / redirect_uri / etc. Now the full OAuth /
    // PKCE param set rides alongside the policy id, so the sign-up
    // user-flow completes end-to-end and the callback's id_token nonce
    // binds back to *this* sign-up attempt.
    let beginSignUp () =
        match config.SignUpPolicyId with
        | None -> ()
        | Some policyId ->
            setAuthState Checking

            async {
                match! OidcClient.beginSignInWithExtras oidcConfig [ "p", policyId ] with
                | Ok() -> ()
                | Error e -> setAuthState (Failed e)
            }
            |> Async.StartImmediate

    match authState with
    | Checking -> LoadingScreen()
    | SignedIn -> shell
    | SignedOut -> SignInScreen config beginSignIn beginSignUp
    | Failed e -> ErrorScreen e (fun () -> setAuthState SignedOut)

// ─── Public surface ──────────────────────────────────────────────────

/// Build a wrapper suitable for
/// `ClientConfig.AuthUI = CustomAuthUI { Wrap = EntraExternalIdAuthUI.wrap config }`.
/// The wrapper renders the SDK shell behind the Entra-flavoured
/// sign-in screen, surfacing a "Sign up" affordance when the
/// configured `SignUpPolicyId` is present.
let wrap (config: EntraExternalIdClientConfig) (shell: ReactElement) : ReactElement = EntraExternalIdShell config shell

/// Header sign-out trigger — small convenience component apps can
/// drop into a header when they want a dedicated sign-out button.
/// Delegates to the generic OIDC client's `signOut`.
[<ReactComponent>]
let UserMenu (config: EntraExternalIdClientConfig) : ReactElement =
    let oidcConfig = EntraExternalIdClientConfig.toOidcUIConfig config

    Html.button [
        prop.className $"{Tokens.Button.secondary} text-sm"
        prop.text "Sign out"
        prop.onClick (fun _ -> OidcClient.signOut oidcConfig |> Async.StartImmediate)
    ]