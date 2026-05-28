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