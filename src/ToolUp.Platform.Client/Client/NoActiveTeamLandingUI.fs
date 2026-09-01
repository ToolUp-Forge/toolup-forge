// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NoActiveTeamLandingUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit

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
//
// ─── Phase 548 — the invite check ────────────────────────────────────
//
// This page is exactly where a stuck invitee sits: signed in, on no
// team, waiting for an invitation that has already been issued.
// Pending-by-email consumption used to fire only from
// `ScopeResolutionMiddleware`'s first-request-of-a-session-window
// trigger, so the wait could be twenty minutes and the folk remedy was
// "sign out and back in". The module now calls
// `ITeamInviteApi.CheckMyInvites` on mount and from an explicit
// "Check for invitations" button.
//
// On a successful join it invokes `ClientModuleContext.OnTeamSwitched`,
// the sanctioned module→shell bridge the shell wires to
// `dispatch (TeamSwitched (Some teamId))` on team-shaped surfaces —
// the same reset-and-re-init path `MembershipActiveTeamSet` routes to,
// so the shell re-inits against the new team with no page reload.
// (The server-side consumption ALSO publishes
// `MembershipChanged.ActiveTeamSet`, which reaches a connected client
// as `MembershipActiveTeamSet`; the shell's own guard makes the second
// arrival a no-op when the ids agree. Calling the hook directly is
// what makes the switch deterministic in a deployment whose
// notification stream is not connected.)

let private inviteApi: ITeamInviteApi =
    Api.makeProxy<ITeamInviteApi> (
        routeBuilder = TeamInviteApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

/// Outcome of the most recent `CheckMyInvites` call. `Idle` exists only
/// for the (unreachable in practice) case of a render before the mount
/// command lands; `Joined` is terminal — the shell is already tearing
/// this module down via the team switch.
[<RequireQualifiedAccess>]
type private CheckState =
    | Idle
    | Checking
    | NothingPending
    | Joined of teamName: string
    | Failed of message: string

type private Model = {
    State: CheckState
    /// Populated from `ClientModuleContext.OnTeamSwitched` at init —
    /// `None` on a non-team-shaped surface, where this module is not
    /// rendered anyway.
    OnTeamSwitched: (string -> unit) option
}

type private Msg =
    | CheckInvites
    | InvitesChecked of Result<TeamInfo option, string>

/// Errors fold into the model rather than throwing — a transport
/// failure leaves the page usable with the button still available.
let private checkCmd =
    Cmd.OfRemoting.call inviteApi.CheckMyInvites () InvitesChecked (fun ex -> InvitesChecked(Error ex.Message))

let private init (ctx: ClientModuleContext) : Model * Cmd<Msg> =
    {
        State = CheckState.Checking
        OnTeamSwitched = ctx.OnTeamSwitched
    },
    checkCmd

let private update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | CheckInvites ->
        {
            model with
                State = CheckState.Checking
        },
        checkCmd

    | InvitesChecked(Ok(Some team)) ->
        // Hand off to the shell's team-switch reset path. Direct
        // invocation mirrors `TeamManagerUI`'s `OnTeamSwitched` call
        // and `PermissionsAdminUI`'s `OnAccessibleModulesChanged` — the
        // established shape for the shell bridges.
        model.OnTeamSwitched |> Option.iter (fun switch -> switch team.TeamId)

        {
            model with
                State = CheckState.Joined team.Name
        },
        Cmd.none

    | InvitesChecked(Ok None) ->
        {
            model with
                State = CheckState.NothingPending
        },
        Cmd.none

    | InvitesChecked(Error message) ->
        {
            model with
                State = CheckState.Failed message
        },
        Cmd.none

let private statusLine (msgs: NoActiveTeamLandingMessages) (state: CheckState) : ReactElement =
    match state with
    | CheckState.Idle -> Html.none
    | CheckState.Checking -> Html.p [ prop.className $"{Tokens.Text.secondary} text-sm"; prop.text msgs.Checking ]
    | CheckState.NothingPending ->
        Html.p [
            prop.className $"{Tokens.Text.secondary} text-sm"
            prop.text msgs.NothingPending
        ]
    | CheckState.Joined teamName ->
        Html.p [
            prop.className $"{Tokens.Text.secondary} text-sm"
            prop.text (msgs.Joined teamName)
        ]
    // The failure text is the server's own message, echoed — not a
    // literal this file authors, so there is nothing here to translate.
    | CheckState.Failed message -> Html.p [ prop.className $"{Tokens.Colours.error} text-sm"; prop.text message ]

/// Phase 751 — a component, so it has a hook site for the catalog. The
/// heading, body and rail label stay `cfg`-supplied: they are the
/// DEPLOYMENT's words about its own product, not SDK chrome.
[<ReactComponent>]
let private NoActiveTeamLandingBody
    (cfg: NoActiveTeamLandingConfig)
    (model: Model)
    (dispatch: Msg -> unit)
    : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).NoActiveTeamLanding

    Html.div [
        prop.className "flex items-center justify-center h-full p-8"
        prop.children [
            Html.div [
                prop.className "max-w-md text-center space-y-4"
                prop.children [
                    Html.h2 [ prop.className "text-xl font-semibold text-gray-800"; prop.text cfg.Title ]
                    Html.p [ prop.className "text-sm text-gray-600 leading-relaxed"; prop.text cfg.Body ]
                    statusLine msgs model.State
                    Html.button [
                        prop.className Tokens.Button.primary
                        prop.text msgs.CheckForInvitations
                        prop.disabled (model.State = CheckState.Checking)
                        prop.onClick (fun _ -> dispatch CheckInvites)
                    ]
                ]
            ]
        ]
    ]

let private landingView (cfg: NoActiveTeamLandingConfig) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    NoActiveTeamLandingBody cfg model dispatch

/// Build the SDK built-in no-active-team landing `ErasedModule` from its
/// parameterized config. `cfg.Icon = None` falls back to `Icons.home`.
/// Registered under `NoActiveTeamLanding.moduleId` with
/// `Visibility.visibleTo [ UserKind ]` so it disappears once a team is
/// active. Public so a consumer can register it directly (and point
/// `NoActiveTeamLandingModuleId` at the same id) if needed, though the
/// normal path is just setting `ClientConfig.NoActiveTeamLanding`.
let create (cfg: NoActiveTeamLandingConfig) : ErasedModule =
    // `init` is `ClientModuleContext -> Model * Cmd<Msg>` (Phase 548 —
    // it reads `OnTeamSwitched`); seed `create` with the empty context,
    // then override with the real context-aware init via
    // `withContextInit`, the same wiring `PermissionsAdminUI` uses.
    ClientModule.create {
        Init = fun () -> init ClientModuleContext.empty
        Update = update
        Name = cfg.Label
        Icon = cfg.Icon |> Option.defaultValue Icons.home
    }
    |> ClientModule.withId NoActiveTeamLanding.moduleId
    |> ClientModule.withContextInit init
    |> ClientModule.withGroup cfg.Label
    |> ClientModule.withVisibility (Visibility.visibleTo [ UserKind ])
    |> ClientModule.withFullWidthView (landingView cfg)
    |> ClientModule.register