// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TeamProviderUI

open Feliz
open ToolUp.Elmish
open ToolUp.Platform

// ─── Phase 44a — the "Team AI providers" surface ─────────────────
//
// This is a MOUNT of the Phase 44 `ProviderProfileUI`, not a second
// editor. The phase's task says to reuse that component with a team
// scope if it had shipped by then; it has, so what is added here is
// exactly the two things the shared component cannot know and must not
// assume:
//
//   * **Which scope it is editing.** Nothing is added to the wire for
//     this, and that is the point. `IProviderProfileApi` deliberately
//     carries no scope parameter — every method targets the caller's
//     own `AccessContext.configScope` — and for a `TeamMember` subject
//     that scope IS the team. So a team member reaching this surface
//     is editing the team-owned profile already, through the same
//     transport, with the server's Owner/Admin gate already applied.
//     Adding a scope argument to the contract to say "the team one"
//     would have handed the wire a choice the server must then refuse,
//     which is strictly worse than having no choice to make.
//
//   * **Whether this caller may write it.** The server gate is the
//     boundary (GP 12) and stays so — this only stops a Member being
//     offered controls whose every use ends in a refusal. The role is
//     supplied by the host rather than fetched here, because the host
//     already resolved the active team to render its own chrome, and a
//     second fetch would be a second answer that can disagree.
//
// **An unknown role RENDERS, and renders writable.** Same fail-open
// posture `NavRole.TeamOwnerAdmin` documents and for the same reasons:
// the pre-44a shape of this surface was ungated client-side (GP 11), a
// role still in flight must not silently strip an Owner of their own
// tools, and the server refuses a write the UI should not have offered
// anyway. A UI that fails closed on an unresolved fetch is a UI that
// looks broken every time the network is slow.

/// Copy this surface renders around the shared panel. Fields rather
/// than literals so a host can localise them; the defaults are the
/// English strings.
type TeamProviderMessages = {
    /// Heading above the panel.
    Title: string
    /// One line under the heading, naming what the surface edits.
    Subtitle: string
    /// Shown to a caller who may read but not write.
    ReadOnlyNotice: string
}

module TeamProviderMessages =
    let defaults = {
        Title = "Team AI providers"
        Subtitle = "Providers configured here apply to everyone on this team, unless a member has configured their own."
        ReadOnlyNotice = "Only team owners and admins can change these providers. You can see what the team uses."
    }

/// The surface's composition: the shared component's own config, the
/// caller's role on the active team, and the copy.
type TeamProviderConfig = {
    /// The Phase 44 component's config — transport, optional verify
    /// delegate, surface keys. Built with `ProviderProfileConfig.create`
    /// or `.forApi` exactly as a personal BYOK surface would build it.
    Profile: ProviderProfileUI.ProviderProfileConfig
    /// The caller's role on the ACTIVE team. `None` = not yet known;
    /// see the fail-open note in the module header.
    Role: TeamRole option
    /// The team's display name, woven into the subtitle when present.
    TeamName: string option
    Messages: TeamProviderMessages
}

module TeamProviderConfig =
    /// Compose over an already-built component config.
    let forProfile (profile: ProviderProfileUI.ProviderProfileConfig) : TeamProviderConfig = {
        Profile = profile
        Role = None
        TeamName = None
        Messages = TeamProviderMessages.defaults
    }

    /// The common case: the standard proxy over the app's surface keys.
    let create (surfaces: string list) : TeamProviderConfig =
        forProfile (ProviderProfileUI.ProviderProfileConfig.create surfaces)

    /// Declare the caller's role on the active team.
    let withRole (role: TeamRole) (config: TeamProviderConfig) = { config with Role = Some role }

    /// Name the team, for the subtitle.
    let withTeamName (name: string) (config: TeamProviderConfig) = { config with TeamName = Some name }

    /// Replace the copy — the localisation hook.
    let withMessages (messages: TeamProviderMessages) (config: TeamProviderConfig) = { config with Messages = messages }

/// Whether this caller may write the team's provider configuration.
/// The SAME predicate the server gates on (`TeamRoles.canWriteTeamConfig`),
/// which is what `TeamRoles` living in Shared is for — a client greying
/// a control and a server refusing a request must not be able to drift
/// into two different rules.
let canEdit (config: TeamProviderConfig) : bool =
    config.Role
    |> Option.map TeamRoles.canWriteTeamConfig
    |> Option.defaultValue true

/// The mutating half of the shared component's `Msg`. A caller who may
/// not write never reaches the server with one of these — not because
/// the server would accept it (it refuses, and that refusal is the real
/// boundary), but because offering a control whose every use ends in an
/// error is a worse surface than not offering it.
let private isMutating (msg: ProviderProfileUI.Msg) =
    match msg with
    | ProviderProfileUI.Save _
    | ProviderProfileUI.Remove _
    | ProviderProfileUI.SetRoute _
    | ProviderProfileUI.ClearRoute _
    | ProviderProfileUI.SetFallback _ -> true
    | _ -> false

let init (config: TeamProviderConfig) : ProviderProfileUI.Model * Cmd<ProviderProfileUI.Msg> =
    ProviderProfileUI.init config.Profile

let update
    (config: TeamProviderConfig)
    (msg: ProviderProfileUI.Msg)
    (model: ProviderProfileUI.Model)
    : ProviderProfileUI.Model * Cmd<ProviderProfileUI.Msg> =
    if isMutating msg && not (canEdit config) then
        // Dropped, not refused-with-an-error: the read-only notice is
        // already on screen saying why, and a second message would be
        // telling the caller something the surface has been telling
        // them the whole time.
        model, Cmd.none
    else
        ProviderProfileUI.update config.Profile msg model

[<ReactComponent>]
let TeamProviderPanel
    (config: TeamProviderConfig)
    (model: ProviderProfileUI.Model)
    (dispatch: ProviderProfileUI.Msg -> unit)
    =
    let subtitle =
        match config.TeamName with
        | Some name -> $"{name} — {config.Messages.Subtitle}"
        | None -> config.Messages.Subtitle

    Html.div [
        prop.className "space-y-4"
        prop.children [
            Html.div [
                prop.children [
                    Html.h2 [
                        prop.className "text-lg font-semibold text-slate-900"
                        prop.text config.Messages.Title
                    ]
                    Html.p [ prop.className "text-sm text-slate-600"; prop.text subtitle ]
                ]
            ]

            if not (canEdit config) then
                Html.div [
                    prop.className "rounded border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800"
                    prop.text config.Messages.ReadOnlyNotice
                ]
            else
                Html.none

            ProviderProfileUI.view config.Profile model dispatch
        ]
    ]

/// Plain `view` alias — the `init` / `update` / `view` triple the
/// module convention expects.
let view (config: TeamProviderConfig) (model: ProviderProfileUI.Model) (dispatch: ProviderProfileUI.Msg -> unit) =
    TeamProviderPanel config model dispatch