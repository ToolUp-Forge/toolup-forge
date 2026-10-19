module MyDataPlatform.Client

open Fable.Core.JsInterop
open ToolUp.Platform

importSideEffects "./index.css"

// The trading module is the deployment's data manager: it fills the shell's
// data-manager slot, so every other module sees the deliveries it ingests.
// Add further modules to this list as you scaffold them with
// `dotnet new platformsdk-module`.
let private modules: ErasedModule list = []

let private config =
    let defaults =
        ClientConfigDefaults.fromBundleConstants ClientConfigDefaults.ClientConfigOverrides.empty

    {
        defaults with
            Slots = {
                defaults.Slots with
                    DataManager = SlotFill.External(DailyTrade.ClientView.register ())
            }
    }

Client.run config modules