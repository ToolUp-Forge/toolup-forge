// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HelloWorld.Client

open Fable.Core.JsInterop
open ToolUp.Platform

// HelloWorld client composition root: the SDK shell, the HelloWorld module
// (its client files arrive through `HelloWorld.Module.Client.props`) and the
// built-in admin modules the shell adds on its own.
//
// `fromBundleConstants` reads the Vite-injected constants over the override
// record. `vite.config.mts` injects the surfaces and the development identity
// only when the development identity profile is selected; otherwise the
// override record's `Surfaces.individual` stands, matching the server's
// `ServerConfigOverrides.referenceApp`.
//
// The slots are set to what the server composes, so nothing the shell opens
// calls a route the server does not mount: the module-visibility profile
// editor is on (the server composes `SurfacingModuleVisibility`), so are the
// session-security page (the server composes a session registry) and the
// platform-users admin (the server enables the tenant lifecycle), and the
// data manager is off (no module declares a data type, so the server mounts
// no file API). The Ctrl+K / Cmd+K command palette is client-only, over the
// caller's visible pages. The sample names itself, and takes the SDK's own
// mark as its logo, which the shell also uses as the favicon.

importSideEffects "./index.css"

let private logo: string = importDefault "../../../icon.png"

let private modules: ErasedModule list = [ HelloWorld.Module.ClientView.register () ]

let private config =
    let fromBundle =
        ClientConfigDefaults.fromBundleConstants {
            ClientConfigDefaults.ClientConfigOverrides.empty with
                AppName = Some "Hello World"
                AppLogo = Some logo
                Surfaces = Some Surfaces.individual
                DataManager = Some SlotFill.Empty
        }

    {
        fromBundle with
            Slots = {
                fromBundle.Slots with
                    ModuleVisibilityAdmin = SlotFill.Default
                    SessionSecurity = SlotFill.Default
            }
            Chrome = {
                fromBundle.Chrome with
                    PlatformUsers = ChromeFill.On()
            }
            Shell = {
                fromBundle.Shell with
                    CommandPalette = FeatureMode.Enabled
            }
    }

Client.run config modules