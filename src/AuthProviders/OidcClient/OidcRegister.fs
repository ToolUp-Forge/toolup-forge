// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcRegister

open Feliz
open ToolUp.Platform

// ─── Phase 13a — value export replaces module-load registration ──────
//
// Was (legacy):
//   do AuthUIProvider.register "oidc" oidcHandler   // module-load
//   let init () = ()                                 // import anchor
//
// Now:
//   let handler : string * AuthUIHandler = "oidc", oidcHandler
//
// Consumer migration:
//   // was: do ToolUp.AuthProviders.Oidc.OidcRegister.init ()
//   // now: Handlers = { ClientHandlerRegistry.empty with
//   //                       AuthUIHandlers = [ OidcRegister.handler ] }
//
// The value reference from the consumer's `ClientConfig` pulls this
// module into the Fable import graph automatically — no separate
// `init ()` anchor needed.

let private oidcHandler (payload: obj) (shell: ReactElement) : ReactElement =
    let cfg = unbox<OidcUIConfig> payload
    OidcAuthUI.OidcShell cfg shell

/// Companion-exported AuthUI handler. Add to
/// `ClientConfig.Handlers.AuthUIHandlers` to enable OIDC sign-in.
let handler: string * AuthUIHandler = "oidc", oidcHandler

/// 0.5.7 — Companion-exported sign-out handler. The consumer pairs
/// this with `OidcRegister.handler` at compose time so the SDK shell
/// renders a "Sign out" affordance in the page header alongside the
/// team switcher / app's own header chrome. The thunk wraps
/// `OidcClient.signOut`'s async into a fire-and-forget unit-returning
/// call — the shell's render path stays Async-free.
///
/// Sign-out clears `localStorage[toolup-auth-token]`, the JWT cookie,
/// the OIDC refresh token, and any pending PKCE state, then triggers
/// the OIDC provider's end-session flow. If you want a confirmation
/// modal, wire your own button in `ExtraChrome.HeaderAction` and call
/// `OidcClient.signOut cfg |> Async.StartImmediate` from your handler
/// instead of registering this default.
///
/// Usage:
/// ```fsharp
/// Handlers = {
///     ClientHandlerRegistry.empty with
///         AuthUIHandlers = [ OidcRegister.handler ]
///         SignOutHandler = Some(OidcRegister.signOutHandler cfg)
/// }
/// ```
let signOutHandler (cfg: OidcUIConfig) : unit -> unit =
    fun () -> OidcClient.signOut cfg |> Async.StartImmediate