module MyApp.Client

open Fable.Core.JsInterop
open ToolUp.Platform

importSideEffects "./index.css"

let private modules: ErasedModule list = []

// Phase 11.G — `ClientConfigDefaults.fromBundleConstants` reads the
// three Vite-injected constants (`__TOOLUP_MODULE__`,
// `__AG_GRID_LICENSE__`, `__CLERK_PUBLISHABLE_KEY__`) via
// `BundleConstants.fs` and applies overrides on top. The default
// `ClientConfigOverrides.empty` keeps every field at its
// `ClientConfig.defaults` value. Both names live *inside* the
// `ClientConfigDefaults` module, so the overrides record stays
// module-qualified even under `open ToolUp.Platform`.

let private config =
    ClientConfigDefaults.fromBundleConstants ClientConfigDefaults.ClientConfigOverrides.empty

Client.run config modules