// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NoActiveTeamLandingUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit

// SDK built-in no-active-team landing module — the parameterized
// alternative to a consumer hand-rolling a landing module and pointing
// `ClientConfig.Chrome.NoActiveTeamLanding` (`NoActiveTeamLandingFill.Module`) at it. `prepareModules`
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
type internal CheckState =
    | Idle
    | Checking
    | NothingPending
    | Joined of teamName: string
    | Failed of message: string

type internal Model = {
    State: CheckState
    /// Populated from `ClientModuleContext.OnTeamSwitched` at init —
    /// `None` on a non-team-shaped surface, where this module is not
    /// rendered anyway.
    OnTeamSwitched: (string -> unit) option
}

type internal Msg =
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

/// The page. The heading, body and rail label stay `cfg`-supplied: they
/// are the DEPLOYMENT's words about its own product, not SDK chrome.
let private landingView (cfg: NoActiveTeamLandingConfig) (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    let msgs = ctx.Messages.NoActiveTeamLanding
    let dispatch = ctx.Dispatch

    PageContent.FullWidth(

        Html.div [
            prop.className "flex items-center justify-center h-full p-8"
            prop.children [
                Html.div [
                    prop.className "max-w-md text-center space-y-4"
                    prop.children [
                        Html.h2 [
                            prop.className [ "text-xl font-semibold"; Tokens.Text.primary ]
                            prop.text cfg.Title
                        ]
                        Html.p [
                            prop.className [ "text-sm leading-relaxed"; Tokens.Text.secondary ]
                            prop.text cfg.Body
                        ]
                        statusLine msgs model.State
                        Forms.Button.primaryWith
                            (Forms.Button.stateOf (model.State = CheckState.Checking) msgs.Checking false)
                            msgs.CheckForInvitations
                            (fun () -> dispatch CheckInvites)
                    ]
                ]
            ]
        ]
    )

/// The SDK built-in no-active-team landing from its parameterized config
/// (`ShellSlots.builtIn` builds it when `Chrome.NoActiveTeamLanding` is
/// `ChromeFill.On(NoActiveTeamLandingFill.BuiltIn cfg)`). `cfg.Icon = None`
/// falls back to `Icons.home`. Registered under
/// `NoActiveTeamLanding.moduleId`, in the group the config names, with
/// `Visibility.visibleTo [ UserKind ]` so it disappears once a team is
/// active.
let internal builtIn (cfg: NoActiveTeamLandingConfig) : BuiltIn<Model, Msg> = {
    Id = NoActiveTeamLanding.moduleId
    Defaults = {
        Name = cfg.Label
        Icon = cfg.Icon |> Option.defaultValue Icons.home
    }
    Group = Some cfg.Label
    NavRole = None
    Area = Product
    Visibility = Visibility.visibleTo [ UserKind ]
    Init = init
    Update = update
    View = BuiltInView.Page(landingView cfg)
    Extend = id
}