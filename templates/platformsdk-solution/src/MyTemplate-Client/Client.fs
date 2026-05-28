module MyTemplate.Client

open Fable.Core.JsInterop
open ToolUp.Platform

importSideEffects "./index.css"

// Add modules to this list as you scaffold them with `dotnet new
// platformsdk-module`. The bundled Starter module ships as a separate
// project under src/Modules/Starter — register it here once you wire it.
//
//   let private modules = [ Starter.ClientView.register () ]
let private modules: ErasedModule list = []

// Phase 11.G — `ClientConfigDefaults.fromBundleConstants` reads the
// three Vite-injected constants (`__TOOLUP_MODULE__`,
// `__AG_GRID_LICENSE__`, `__CLERK_PUBLISHABLE_KEY__`) via
// `BundleConstants.fs`. The default `ClientConfigOverrides.empty` keeps
// every field at its `ClientConfig.defaults` value.
let private config =
    ClientConfigDefaults.fromBundleConstants ClientConfigOverrides.empty

Client.run config modules