// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityRegister

open Feliz
open ToolUp.Platform
open ToolUp.AuthProviders.Oidc
open ToolUp.AuthProviders.GoogleIdentity.GoogleIdentityConfig

// ─── Companion-exported AuthUI handler ───────────────────────────────
//
// The value-export registration model: the consumer adds
// `GoogleIdentityRegister.handler` to
// `ClientConfig.Handlers.AuthUIHandlers`, and the value reference
// pulls this module into the Fable import graph — no module-load side
// effect and no `init ()` anchor. `AuthUIProvider.gate` dispatches the
// vendor-neutral `ProviderAuthUI (tag, payload)` case on the tag, and
// the handler unboxes the payload (the sanctioned erasure boundary
// documented in `AuthUIProvider.fs`).
//
// No SDK core case is added for this companion: `ProviderAuthUI` is
// the neutral case that exists precisely so a new provider needs no
// edit to `AuthUIMode`, and `authUI` below is the smart constructor
// that keeps the boxing out of the consumer's config.

let private googleIdentityHandler (payload: obj) (shell: ReactElement) : ReactElement =
    let config = unbox<GoogleIdentityUIConfig> payload
    GoogleIdentityAuthUI.GoogleIdentityShell config shell

/// The tag this companion registers under in
/// `ClientConfig.Handlers.AuthUIHandlers` — the same key
/// `ProviderAuthUI` dispatches on.
[<Literal>]
let Tag = "google-identity"

/// Companion-exported AuthUI handler. Add to
/// `ClientConfig.Handlers.AuthUIHandlers` to enable the Google
/// Identity Services sign-in surface.
let handler: string * AuthUIHandler = Tag, googleIdentityHandler

/// Typed smart constructor for the vendor-neutral `ClientConfig.AuthUI`
/// case:
///
///   AuthUI = GoogleIdentityRegister.authUI (GoogleIdentityUIConfig.create clientId)
let authUI (config: GoogleIdentityUIConfig) : AuthUIMode = ProviderAuthUI(Tag, box config)

/// Companion-exported sign-out handler, for
/// `ClientHandlerRegistry.SignOutHandler`. Clears GIS's auto-select
/// state first so One Tap does not sign the user straight back in on
/// the next page load, then runs the shared OIDC sign-out — the same
/// call a redirect-flow deployment makes, against the same store.
let signOutHandler (config: GoogleIdentityUIConfig) : unit -> unit =
    let oidcConfig = GoogleIdentityUIConfig.toOidcUIConfig config

    fun () ->
        GoogleIdentityClient.disableAutoSelect ()
        OidcClient.signOut oidcConfig |> Async.StartImmediate