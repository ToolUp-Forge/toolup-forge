module MyApp.Client

open Fable.Core.JsInterop
open ToolUp.Platform

importSideEffects "./index.css"

// ─── Module registry ────────────────────────────────────────────────────
//
// Each module's `register ()` returns an `ErasedModule` — heterogeneous-
// list element with `'Model`/`'Msg` boxed (the one sanctioned `box`/`unbox`
// boundary in forge per `docs/platform/...`). The shell unboxes inside
// each module's update / view callback so module code never sees `obj`.

let private modules: ErasedModule list = [ Chat.ClientView.register () ]

// ─── Client config ──────────────────────────────────────────────────────
//
// `ClientConfigDefaults.fromBundleConstants` reads the Vite-injected
// `__TOOLUP_*__` constants via `BundleConstants.fs` and applies overrides
// on top. The default `ClientConfigOverrides.empty` keeps every field at
// its `ClientConfig.defaults` value — no AI, no AG Grid, no Clerk; just
// the chat module and the SDK shell's navigation / module sidebar.

let private config =
    ClientConfigDefaults.fromBundleConstants ClientConfigOverrides.empty

Client.run config modules