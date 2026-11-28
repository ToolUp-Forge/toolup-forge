// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NoActiveTeamLandingUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop

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
// ─── The invite check ────────────────────────────────────────────────
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
let private check: Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "ITeamInviteApi.CheckMyInvites"
            inviteApi.CheckMyInvites
            ()
            (RemoteEffect.flatten >> InvitesChecked)
    )
]

let internal init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    ({
        State = CheckState.Checking
        OnTeamSwitched = ctx.OnTeamSwitched
     },
     check)

/// The transitions, with their remote calls as values.
let internal update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | CheckInvites ->
        {
            model with
                State = CheckState.Checking
        },
        check

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
        []

    | InvitesChecked(Ok None) ->
        {
            model with
                State = CheckState.NothingPending
        },
        []

    | InvitesChecked(Error message) ->
        {
            model with
                State = CheckState.Failed message
        },
        []

/// What the deferred tree component takes. The heading and body stay
/// `cfg`-supplied: they are the DEPLOYMENT's words about its own product, not
/// SDK chrome.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: NoActiveTeamLandingMessages
    Title: string
    Body: string
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./NoActiveTeamLandingTree.fs")

let private landingView (cfg: NoActiveTeamLandingConfig) (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "NoActiveTeamLandingTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.NoActiveTeamLanding
            Title = cfg.Title
            Body = cfg.Body
            Locale = ctx.Locale
        |}
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