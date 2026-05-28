// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.ClerkRegister

open Feliz
open ToolUp.Platform

// ─── Phase 13a — value export replaces module-load registration ──────
//
// Was (legacy):
//   do AuthUIProvider.register "clerk" clerkHandler   // module-load
//   let init () = ()                                   // import anchor
//
// Now:
//   let handler : string * AuthUIHandler = "clerk", clerkHandler
//
// Consumer migration:
//   // was: do ToolUp.AuthProviders.ClerkRegister.init ()
//   // now: Handlers = { ClientHandlerRegistry.empty with
//   //                       AuthUIHandlers = [ ClerkRegister.handler ] }
//
// The value reference from the consumer's `ClientConfig` pulls this
// module into the Fable import graph automatically.

let private clerkHandler (payload: obj) (shell: ReactElement) : ReactElement =
    let cfg = unbox<ClerkUIConfig> payload
    ClerkAuthUI.wrapApp cfg.PublishableKey shell

/// Companion-exported AuthUI handler. Add to
/// `ClientConfig.Handlers.AuthUIHandlers` to enable Clerk sign-in.
let handler: string * AuthUIHandler = "clerk", clerkHandler