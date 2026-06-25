// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NoActiveTeamLandingUI

open Feliz
open ToolUp.Elmish

// SDK built-in no-active-team landing module — the parameterized
// alternative to a consumer hand-rolling a landing module and pointing
// `ClientConfig.NoActiveTeamLandingModuleId` at it. `prepareModules`
// (SDK.Client.fs) injects this when `ClientConfig.NoActiveTeamLanding` is
// set on a team-scoped deployment with no explicit custom module id; the
// gate (`ClientConfig.effectiveNoActiveTeamLandingId`) resolves to
// `NoActiveTeamLanding.moduleId`. `Visibility.visibleTo [ UserKind ]`
// hides the entry once an active team upgrades the subject to
// `TeamMemberKind`, so it never appears in a team member's sidebar even
// though the deployment-wide gate is then inert.

type private Model = unit

type private Msg = NoOp

let private init () : Model * Cmd<Msg> = (), Cmd.none

let private update (_: Msg) (model: Model) : Model * Cmd<Msg> = model, Cmd.none

let private landingView (cfg: NoActiveTeamLandingConfig) : ReactElement =
    Html.div [
        prop.className "flex items-center justify-center h-full p-8"
        prop.children [
            Html.div [
                prop.className "max-w-md text-center space-y-4"
                prop.children [
                    Html.h2 [ prop.className "text-xl font-semibold text-gray-800"; prop.text cfg.Title ]
                    Html.p [ prop.className "text-sm text-gray-600 leading-relaxed"; prop.text cfg.Body ]
                ]
            ]
        ]
    ]

/// Build the SDK built-in no-active-team landing `ErasedModule` from its
/// parameterized config. `cfg.Icon = None` falls back to `Icons.home`.
/// Registered under `NoActiveTeamLanding.moduleId` with
/// `Visibility.visibleTo [ UserKind ]` so it disappears once a team is
/// active. Public so a consumer can register it directly (and point
/// `NoActiveTeamLandingModuleId` at the same id) if needed, though the
/// normal path is just setting `ClientConfig.NoActiveTeamLanding`.
let create (cfg: NoActiveTeamLandingConfig) : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = cfg.Label
        Icon = cfg.Icon |> Option.defaultValue Icons.home
    }
    |> ClientModule.withId NoActiveTeamLanding.moduleId
    |> ClientModule.withGroup cfg.Label
    |> ClientModule.withVisibility (Visibility.visibleTo [ UserKind ])
    |> ClientModule.withFullWidthView (fun _ _ -> landingView cfg)
    |> ClientModule.register