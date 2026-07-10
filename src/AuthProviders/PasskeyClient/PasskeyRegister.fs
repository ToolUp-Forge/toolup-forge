// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.PasskeyRegister

open Feliz
open ToolUp.Platform
open ToolUp.AuthProviders.Passkey

// ─── Phase 443 — companion-exported AuthUI handler ───────────────────
//
// Value-export registration (the Phase 13a model): the consumer adds
// `PasskeyRegister.handler` to `ClientConfig.Handlers.AuthUIHandlers`,
// and the value reference pulls this module into the Fable import graph
// automatically — no module-load side effect, no `init ()` anchor. The
// SDK's `AuthUIProvider.gate` dispatches `PasskeyAuthUI cfg` to this
// handler by the `"passkey"` tag.

let private passkeyHandler (payload: obj) (shell: ReactElement) : ReactElement =
    let cfg = unbox<PasskeyUIConfig> payload
    PasskeyAuthUI.PasskeyShell cfg shell

/// Companion-exported AuthUI handler. Add to
/// `ClientConfig.Handlers.AuthUIHandlers` to enable passkey sign-in:
///
/// ```fsharp
/// AuthUI = PasskeyAuthUI PasskeyUIConfig.defaults
/// Handlers = { ClientHandlerRegistry.empty with AuthUIHandlers = [ PasskeyRegister.handler ] }
/// ```
let handler: string * AuthUIHandler = "passkey", passkeyHandler