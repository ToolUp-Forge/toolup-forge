// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformAdminUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Built-in Platform Admin module ──────────────────────────────────
//
// SDK-built-in admin module surfacing `PlatformAdminApi`'s role-
// management endpoints: list current Platform Admins, assign new
// admins, revoke existing ones. Auto-injected by the shell whenever
// the caller's `Model.PlatformRole = Some PlatformAdmin` (the sidebar
// gate hides the module's "Platform Management" group otherwise).
//
// **Three tabs:**
//   - "Admins" — the current admin list with assign / revoke controls.
//   - "Teams" — create a team with an initial owner; the all-teams table
//     with archive / restore / delete.
//   - "Settings" — deployment-wide runtime configuration.
//
// **Server-side gating.** Read methods (`ListPlatformAdmins`) are open
// to any authenticated caller. Write methods (Assign / Revoke) are
// gated server-side on `canModifyPlatformConfig`; the client-side gate
// (sidebar visibility) is UX-only — non-admin callers landing on the
// page directly via URL would still see the API refuse writes.
//
// The two confirmations (grant admin, delete team) are sub-models: each
// issues its own remote call and reports how it ended as an `Outcome`
// the parent reads after delegating to it.

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private platformAdminApi: PlatformAdminApi =
    Api.makeProxy<PlatformAdminApi> (customOptions = UserSession.withRequestHeaders)

let private teamApi: TeamApi =
    Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

// ─── Sub-models ──────────────────────────────────────────────────────

/// "Are you sure?" for `AssignPlatformAdmin`. Grants are effectively
/// irreversible once observed (the recipient may have already used the
/// new role to mutate deployment configuration), so the confirm step is a
/// deliberate brake on accidental fires — e.g. an operator typing-Enter on
/// the wrong autocomplete row.
module AssignConfirm =

    /// How the confirmation ended, read by the parent after each update.
    type Outcome =
        | Open
        | Cancelled
        | Done of Result<unit, string>

    type Model = {
        TargetUserId: string
        /// The grant call is in flight; the confirm button shows busy.
        InFlight: bool
        Outcome: Outcome
    }

    type Msg =
        | Confirm
        | Cancel
        | Resolved of Result<unit, string>

    let init (targetUserId: string) : Model = {
        TargetUserId = targetUserId
        InFlight = false
        Outcome = Open
    }

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | Confirm when model.InFlight -> model, Cmd.none
        | Confirm ->
            { model with InFlight = true },
            Cmd.OfRemoting.call platformAdminApi.AssignPlatformAdmin model.TargetUserId Resolved (fun ex ->
                Resolved(Error ex.Message))
        | Cancel when model.InFlight -> model, Cmd.none
        | Cancel -> { model with Outcome = Cancelled }, Cmd.none
        | Resolved result ->
            {
                model with
                    InFlight = false
                    Outcome = Done result
            },
            Cmd.none

    let view (msgs: PlatformAdminMessages) (directory: UserDirectory.Model) (model: Model) (dispatch: Msg -> unit) =
        let label = UserDirectory.email directory model.TargetUserId
        let resolved = label <> model.TargetUserId

        Modal.confirm
            ({
                Title = msgs.GrantHeading
                Confirm = msgs.GrantConfirm
                Busy = msgs.GrantConfirm
                Cancel = msgs.Cancel
            }
            : ConfirmMessages)
            [
                Html.p [
                    prop.className [ "text-sm"; Tokens.Text.secondary ]
                    prop.text msgs.GrantBody
                ]
                Html.div [
                    prop.className [ "rounded border border-border px-3 py-2"; Tokens.Bg.subtle ]
                    prop.children [
                        Html.span [
                            prop.className [ "text-xs"; Tokens.Text.secondary ]
                            prop.text (if resolved then msgs.UserLabel else msgs.UserIdLabel)
                        ]
                        Html.span [
                            prop.className [
                                "text-sm"
                                Tokens.Text.primary
                                if not resolved then
                                    "font-mono break-all"
                            ]
                            prop.text label
                        ]
                        // Keep the raw id visible for traceability when an
                        // email resolved above it.
                        if resolved then
                            Html.div [
                                prop.className [ "text-[10px] font-mono mt-1 break-all"; Tokens.Text.secondary ]
                                prop.text model.TargetUserId
                            ]
                    ]
                ]
            ]
            (fun () -> dispatch Confirm)
            (fun () -> dispatch Cancel)
            model.InFlight

/// Delete-team confirmation — only reachable from an archived row. Echoes
/// the team name as a brake on accidental destruction; the purge removes
/// the team record AND every membership row.
module ConfirmDeleteTeam =

    /// How the confirmation ended, read by the parent after each update.
    type Outcome =
        | Open
        | Cancelled
        | Done of Result<unit, string>

    type Model = {
        TeamId: string
        /// The name shown in the prompt (the id when the table lacks it).
        TeamName: string
        /// The delete call is in flight; the confirm button shows busy.
        InFlight: bool
        Outcome: Outcome
    }

    type Msg =
        | Confirm
        | Cancel
        | Resolved of Result<unit, string>

    let init (teamId: string) (teamName: string) : Model = {
        TeamId = teamId
        TeamName = teamName
        InFlight = false
        Outcome = Open
    }

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
        match msg with
        | Confirm when model.InFlight -> model, Cmd.none
        | Confirm ->
            { model with InFlight = true },
            Cmd.OfRemoting.call teamApi.DeleteTeamHard model.TeamId Resolved (fun ex -> Resolved(Error ex.Message))
        | Cancel when model.InFlight -> model, Cmd.none
        | Cancel -> { model with Outcome = Cancelled }, Cmd.none
        | Resolved result ->
            {
                model with
                    InFlight = false
                    Outcome = Done result
            },
            Cmd.none

    let view (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        Modal.confirm
            ({
                Title = msgs.DeleteTeamHeading
                Confirm = msgs.DeleteTeam
                Busy = msgs.DeleteTeam
                Cancel = msgs.Cancel
            }
            : ConfirmMessages)
            [
                Html.p [
                    prop.className [ "text-sm"; Tokens.Text.secondary ]
                    prop.text msgs.DeleteTeamBody
                ]
                Html.div [
                    prop.className [ "rounded border border-border px-3 py-2"; Tokens.Bg.subtle ]
                    prop.children [
                        Html.span [
                            prop.className [ "text-xs"; Tokens.Text.secondary ]
                            prop.text msgs.TeamLabel
                        ]
                        Html.span [
                            prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                            prop.text model.TeamName
                        ]
                    ]
                ]
            ]
            (fun () -> dispatch Confirm)
            (fun () -> dispatch Cancel)
            model.InFlight

// ─── Model ───────────────────────────────────────────────────────────

type Tab =
    | AdminsTab
    | TeamsTab
    | SettingsTab

type Model = {
    /// Currently rendered section.
    ActiveTab: Tab
    /// Live admin list from `ListPlatformAdmins`. Refreshed on shell
    /// init and after every successful Assign / Revoke.
    Admins: Remote<string list, string>
    /// The Assign Admin form's committed value. Kept on the Elmish model
    /// so the AssignError banner can pair with the last attempted value.
    AssignInput: string
    /// Inline error from the most recent Assign attempt — `None` when
    /// no attempt yet, or after a successful one. Cleared on input
    /// edit so the banner doesn't linger after the user retypes.
    AssignError: Notice option
    /// The open grant confirmation, `None` when closed.
    AssignConfirm: AssignConfirm.Model option
    /// Inline error from a Revoke attempt, keyed by user id so the
    /// banner can render next to the right row.
    RevokeError: Map<string, Notice>
    /// Create-team form: name input.
    NewTeamName: string
    /// Create-team form: initial-owner user id. Empty string = no
    /// selection yet; the operator can populate via the directory
    /// typeahead OR by clicking "Self" (which writes their own user
    /// id into this field).
    NewTeamOwnerUserId: string
    /// Inline error from the most recent CreateTeamWithOwner attempt.
    CreateTeamError: Notice option
    /// Tracks whether a CreateTeamWithOwner call is in flight, so
    /// the submit button can disable to prevent double-fire.
    CreateTeamInFlight: bool
    /// Live all-teams admin table from `ListAllTeams`. Loaded on shell
    /// init and after every successful archive / restore / delete /
    /// create. Platform-Admin only — non-admins never see this surface.
    Teams: Remote<TeamSummary list, string>
    /// Team ids with an archive / restore call in flight, so their row
    /// action buttons can disable to prevent double-fire.
    TeamActionPending: Set<string>
    /// The open delete-team confirmation, `None` when closed.
    ConfirmDeleteTeam: ConfirmDeleteTeam.Model option
    /// Inline error from a per-team archive / restore / delete attempt,
    /// keyed by team id so the banner renders against the right row.
    TeamActionError: Map<string, Notice>
    /// Current runtime PlatformKnowledgeBase state. Loaded on shell init
    /// via `GetPlatformKnowledgeBase`; updated after a successful `Set`.
    PlatformKnowledgeBase: Remote<FeatureMode, string>
    /// Inline error from a SetPlatformKnowledgeBase attempt.
    SettingsError: Notice option
    /// The reverse directory the admin list and teams table render ids
    /// through (emails in place of raw `oid`s).
    Directory: UserDirectory.Model
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: PlatformAdminMessages
}

type Msg =
    | SwitchTab of Tab
    | RefreshAdmins
    | AdminsLoaded of Result<string list, string>
    | UpdateAssignInput of string
    | RequestAssignConfirm
    | AssignConfirmMsg of AssignConfirm.Msg
    | RequestRevoke of targetUserId: string
    | RevokeResolved of targetUserId: string * Result<unit, string>
    | SetNewTeamName of string
    | SetNewTeamOwnerUserId of string
    | UseSelfAsTeamOwner
    | SubmitCreateTeam
    | CreateTeamResolved of Result<TeamInfo, string>
    | RefreshTeams
    | TeamsLoaded of Result<TeamSummary list, string>
    | SetTeamArchived of teamId: string * archived: bool
    | TeamArchiveResolved of teamId: string * archived: bool * Result<unit, string>
    | RequestDeleteTeam of teamId: string
    | DeleteTeamMsg of ConfirmDeleteTeam.Msg
    | PlatformKnowledgeBaseLoaded of Result<FeatureMode, string>
    | TogglePlatformKnowledgeBase of FeatureMode
    | TogglePlatformKnowledgeBaseResolved of requested: FeatureMode * Result<unit, string>
    | DirectoryMsg of UserDirectory.Msg

// ─── Init / commands ─────────────────────────────────────────────────

let private loadAdminsCmd () =
    Cmd.OfRemoting.call platformAdminApi.ListPlatformAdmins () (fun list -> AdminsLoaded(Ok list)) (fun ex ->
        AdminsLoaded(Error ex.Message))

let private revokeCmd (targetUserId: string) =
    Cmd.OfRemoting.call
        platformAdminApi.RevokePlatformAdmin
        targetUserId
        (fun result -> RevokeResolved(targetUserId, result))
        (fun ex -> RevokeResolved(targetUserId, Error ex.Message))

let private loadPlatformKnowledgeBaseCmd () =
    Cmd.OfRemoting.call
        platformAdminApi.GetPlatformKnowledgeBase
        ()
        (fun mode -> PlatformKnowledgeBaseLoaded(Ok mode))
        (fun ex -> PlatformKnowledgeBaseLoaded(Error ex.Message))

let private setPlatformKnowledgeBaseCmd (mode: FeatureMode) =
    Cmd.OfRemoting.call
        platformAdminApi.SetPlatformKnowledgeBase
        mode
        (fun result -> TogglePlatformKnowledgeBaseResolved(mode, result))
        (fun ex -> TogglePlatformKnowledgeBaseResolved(mode, Error ex.Message))

let private createTeamCmd (request: CreateTeamRequest) =
    Cmd.OfRemoting.call teamApi.CreateTeamWithOwner request CreateTeamResolved (fun ex ->
        CreateTeamResolved(Error ex.Message))

let private loadTeamsCmd () =
    Cmd.OfRemoting.call teamApi.ListAllTeams () TeamsLoaded (fun ex -> TeamsLoaded(Error ex.Message))

let private setArchivedCmd (teamId: string) (archived: bool) =
    let call =
        if archived then
            teamApi.ArchiveTeam
        else
            teamApi.RestoreTeam

    Cmd.OfRemoting.call call teamId (fun result -> TeamArchiveResolved(teamId, archived, result)) (fun ex ->
        TeamArchiveResolved(teamId, archived, Error ex.Message))

/// Ask the directory for `ids`; it skips blank and already-known ids.
let private resolveCmd (ids: string list) (model: Model) =
    UserDirectory.resolve ids model.Directory |> Cmd.map DirectoryMsg

/// Every user id the loaded admin list + teams table reference (Owners +
/// Admins) — what the directory should hold.
let private referencedUserIds (model: Model) =
    let adminIds = model.Admins |> Remote.withDefault []

    let teamIds =
        model.Teams
        |> Remote.map (List.collect (fun t -> t.Owners @ t.Admins))
        |> Remote.withDefault []

    adminIds @ teamIds

let init (ctx: ClientModuleContext) =
    let model = {
        ActiveTab = AdminsTab
        Admins = Remote.Loading
        AssignInput = ""
        AssignError = None
        AssignConfirm = None
        RevokeError = Map.empty
        NewTeamName = ""
        NewTeamOwnerUserId = ""
        CreateTeamError = None
        CreateTeamInFlight = false
        Teams = Remote.Loading
        TeamActionPending = Set.empty
        ConfirmDeleteTeam = None
        TeamActionError = Map.empty
        PlatformKnowledgeBase = Remote.Loading
        SettingsError = None
        Directory = UserDirectory.empty
        Messages = ctx.Messages.PlatformAdmin
    }

    model, Cmd.batch [ loadAdminsCmd (); loadTeamsCmd (); loadPlatformKnowledgeBaseCmd () ]

// ─── Update ──────────────────────────────────────────────────────────

/// Delegate to the open grant confirmation, then act on how it ended.
let private assignConfirmUpdate (msg: AssignConfirm.Msg) (model: Model) =
    match model.AssignConfirm with
    | None -> model, Cmd.none
    | Some sub ->
        let sub, subCmd = AssignConfirm.update msg sub
        let subCmd = Cmd.map AssignConfirmMsg subCmd

        match sub.Outcome with
        | AssignConfirm.Open -> { model with AssignConfirm = Some sub }, subCmd
        | AssignConfirm.Cancelled -> { model with AssignConfirm = None }, subCmd
        | AssignConfirm.Done(Ok()) ->
            {
                model with
                    AssignConfirm = None
                    AssignInput = ""
                    AssignError = None
                    Admins = Remote.Loading
            },
            Cmd.batch [ subCmd; loadAdminsCmd () ]
        | AssignConfirm.Done(Error e) ->
            {
                model with
                    AssignConfirm = None
                    AssignError = Some(Notice.Problem e)
            },
            subCmd

/// Delegate to the open delete-team confirmation, then act on how it ended.
let private deleteTeamUpdate (msg: ConfirmDeleteTeam.Msg) (model: Model) =
    match model.ConfirmDeleteTeam with
    | None -> model, Cmd.none
    | Some sub ->
        let sub, subCmd = ConfirmDeleteTeam.update msg sub
        let subCmd = Cmd.map DeleteTeamMsg subCmd

        match sub.Outcome with
        | ConfirmDeleteTeam.Open ->
            {
                model with
                    ConfirmDeleteTeam = Some sub
            },
            subCmd
        | ConfirmDeleteTeam.Cancelled -> { model with ConfirmDeleteTeam = None }, subCmd
        | ConfirmDeleteTeam.Done(Ok()) ->
            {
                model with
                    ConfirmDeleteTeam = None
                    Teams = Remote.Loading
            },
            Cmd.batch [ subCmd; loadTeamsCmd () ]
        | ConfirmDeleteTeam.Done(Error e) ->
            {
                model with
                    ConfirmDeleteTeam = None
                    TeamActionError = model.TeamActionError |> Map.add sub.TeamId (Notice.Problem e)
            },
            subCmd

let update (msg: Msg) (model: Model) =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | RefreshAdmins -> { model with Admins = Remote.Loading }, loadAdminsCmd ()

    | AdminsLoaded(Ok list) ->
        let model = {
            model with
                Admins = Remote.Loaded(list |> List.sort)
        }

        model, resolveCmd (referencedUserIds model) model

    | AdminsLoaded(Error msg) ->
        {
            model with
                Admins = Remote.Failed msg
        },
        Cmd.none

    | UpdateAssignInput input ->
        {
            model with
                AssignInput = input
                AssignError = None
        },
        Cmd.none

    | RequestAssignConfirm ->
        let trimmed = model.AssignInput.Trim()

        if String.length trimmed = 0 then
            {
                model with
                    AssignError = Some(Notice.Problem model.Messages.EnterUserId)
            },
            Cmd.none
        else
            {
                model with
                    AssignError = None
                    AssignConfirm = Some(AssignConfirm.init trimmed)
            },
            // Resolve the target so the confirmation can echo their email.
            resolveCmd [ trimmed ] model

    | AssignConfirmMsg subMsg -> assignConfirmUpdate subMsg model

    | RequestRevoke targetUserId ->
        {
            model with
                RevokeError = model.RevokeError |> Map.remove targetUserId
        },
        revokeCmd targetUserId

    | RevokeResolved(_, Ok()) -> { model with Admins = Remote.Loading }, loadAdminsCmd ()

    | RevokeResolved(targetUserId, Error msg) ->
        {
            model with
                RevokeError = model.RevokeError |> Map.add targetUserId (Notice.Problem msg)
        },
        Cmd.none

    | SetNewTeamName name ->
        {
            model with
                NewTeamName = name
                CreateTeamError = None
        },
        Cmd.none

    | SetNewTeamOwnerUserId userId ->
        {
            model with
                NewTeamOwnerUserId = userId
                CreateTeamError = None
        },
        // Resolve the committed id so the confirmation line can echo the
        // owner's email.
        resolveCmd [ userId.Trim() ] model

    | UseSelfAsTeamOwner ->
        {
            model with
                NewTeamOwnerUserId = UserSession.getUserId ()
                CreateTeamError = None
        },
        Cmd.none

    | SubmitCreateTeam ->
        let name = model.NewTeamName.Trim()
        let ownerId = model.NewTeamOwnerUserId.Trim()

        if String.length name = 0 then
            {
                model with
                    CreateTeamError = Some(Notice.Problem model.Messages.TeamNameRequired)
            },
            Cmd.none
        elif String.length ownerId = 0 then
            {
                model with
                    CreateTeamError = Some(Notice.Problem model.Messages.OwnerRequired)
            },
            Cmd.none
        else
            {
                model with
                    CreateTeamError = None
                    CreateTeamInFlight = true
            },
            createTeamCmd {
                Name = name
                InitialOwnerUserId = ownerId
            }

    | CreateTeamResolved(Ok _) ->
        {
            model with
                NewTeamName = ""
                NewTeamOwnerUserId = ""
                CreateTeamError = None
                CreateTeamInFlight = false
                Teams = Remote.Loading
        },
        // Refresh the admin table so the new team appears immediately.
        loadTeamsCmd ()

    | CreateTeamResolved(Error msg) ->
        {
            model with
                CreateTeamError = Some(Notice.Problem msg)
                CreateTeamInFlight = false
        },
        Cmd.none

    | RefreshTeams -> { model with Teams = Remote.Loading }, loadTeamsCmd ()

    | TeamsLoaded(Ok teams) ->
        let model = {
            model with
                Teams = Remote.Loaded(teams |> List.sortBy (fun t -> t.Name.ToLowerInvariant()))
        }

        model, resolveCmd (referencedUserIds model) model

    | TeamsLoaded(Error msg) -> { model with Teams = Remote.Failed msg }, Cmd.none

    | SetTeamArchived(teamId, archived) ->
        {
            model with
                TeamActionPending = model.TeamActionPending |> Set.add teamId
                TeamActionError = model.TeamActionError |> Map.remove teamId
        },
        setArchivedCmd teamId archived

    | TeamArchiveResolved(teamId, _, Ok()) ->
        {
            model with
                TeamActionPending = model.TeamActionPending |> Set.remove teamId
                Teams = Remote.Loading
        },
        loadTeamsCmd ()

    | TeamArchiveResolved(teamId, _, Error msg) ->
        {
            model with
                TeamActionPending = model.TeamActionPending |> Set.remove teamId
                TeamActionError = model.TeamActionError |> Map.add teamId (Notice.Problem msg)
        },
        Cmd.none

    | RequestDeleteTeam teamId ->
        let teamName =
            model.Teams
            |> Remote.toOption
            |> Option.bind (List.tryFind (fun t -> t.TeamId = teamId))
            |> Option.map _.Name
            |> Option.defaultValue teamId

        {
            model with
                ConfirmDeleteTeam = Some(ConfirmDeleteTeam.init teamId teamName)
                TeamActionError = model.TeamActionError |> Map.remove teamId
        },
        Cmd.none

    | DeleteTeamMsg subMsg -> deleteTeamUpdate subMsg model

    | PlatformKnowledgeBaseLoaded(Ok mode) ->
        {
            model with
                PlatformKnowledgeBase = Remote.Loaded mode
                SettingsError = None
        },
        Cmd.none

    | PlatformKnowledgeBaseLoaded(Error msg) ->
        {
            model with
                PlatformKnowledgeBase = Remote.Failed msg
        },
        Cmd.none

    | TogglePlatformKnowledgeBase requested ->
        { model with SettingsError = None }, setPlatformKnowledgeBaseCmd requested

    | TogglePlatformKnowledgeBaseResolved(requested, Ok()) ->
        {
            model with
                PlatformKnowledgeBase = Remote.Loaded requested
                SettingsError = None
        },
        Cmd.none

    | TogglePlatformKnowledgeBaseResolved(_, Error msg) ->
        {
            model with
                SettingsError = Some(Notice.Problem msg)
        },
        Cmd.none

    | DirectoryMsg subMsg ->
        let directory, cmd = UserDirectory.update subMsg model.Directory
        { model with Directory = directory }, Cmd.map DirectoryMsg cmd

// ─── View helpers ────────────────────────────────────────────────────

/// A user id rendered through the directory: the email when resolved (the
/// raw id on hover), the id in monospace when not.
let private userIdSpan (model: Model) (size: string) (userId: string) =
    let label = UserDirectory.email model.Directory userId

    Html.span [
        prop.className [
            size
            Tokens.Text.primary
            "break-all"
            if label = userId then
                "font-mono"
        ]
        prop.title userId
        prop.text label
    ]

/// A card's header row: its heading and a Refresh link.
let private cardHeader (heading: string) (refreshLabel: string) (onRefresh: unit -> unit) =
    Html.div [
        prop.className "flex items-center justify-between px-3 py-2 border-b border-border"
        prop.children [
            Html.h3 [
                prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                prop.text heading
            ]
            Html.button [
                prop.className "text-xs text-brand hover:underline"
                prop.text refreshLabel
                prop.onClick (fun _ -> onRefresh ())
            ]
        ]
    ]

let private noticeOf (notice: Notice option) =
    match notice with
    | Some notice -> StateViews.noticeStatic notice
    | None -> Html.none

let private adminRow (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) (userId: string) =
    Html.div [
        prop.className "py-2 px-3 border-b border-border last:border-0"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between gap-3"
                prop.children [
                    userIdSpan model "text-sm" userId
                    Forms.Button.dangerWith Forms.Button.Enabled msgs.Revoke (fun () -> dispatch (RequestRevoke userId))
                ]
            ]
            noticeOf (model.RevokeError |> Map.tryFind userId)
        ]
    ]

let private adminsTabView (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-4 flex flex-col gap-4"
        prop.children [
            // Assign form
            Html.div [
                prop.className "flex flex-col gap-2 p-3 rounded border border-border bg-white"
                prop.children [
                    Html.h3 [
                        prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                        prop.text msgs.AssignHeading
                    ]
                    Html.p [
                        prop.className [ "text-xs"; Tokens.Text.secondary ]
                        prop.text msgs.AssignHelp
                    ]
                    Html.div [
                        prop.className "flex gap-2 items-start"
                        prop.children [
                            Html.div [
                                prop.className "flex-1"
                                prop.children [
                                    UserDirectoryTypeahead.userTypeahead
                                        model.AssignInput
                                        (UpdateAssignInput >> dispatch)
                                        msgs.UserPickerPlaceholder
                                        UserDirectoryTypeahead.pickUserId
                                ]
                            ]
                            Forms.Button.primaryWith Forms.Button.Enabled msgs.Assign (fun () ->
                                dispatch RequestAssignConfirm)
                        ]
                    ]
                    noticeOf model.AssignError
                ]
            ]

            // Admin list
            Html.div [
                prop.className "rounded border border-border bg-white"
                prop.children [
                    cardHeader msgs.CurrentAdminsHeading msgs.Refresh (fun () -> dispatch RefreshAdmins)
                    match model.Admins with
                    | Remote.NotAsked
                    | Remote.Loading
                    | Remote.Reloading _ ->
                        Html.p [
                            prop.className [ "text-sm p-3"; Tokens.Text.secondary ]
                            prop.text msgs.Loading
                        ]
                    | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
                    | Remote.Loaded [] ->
                        Html.p [
                            prop.className [ "text-sm p-3"; Tokens.Text.secondary ]
                            prop.text msgs.NoAdmins
                        ]
                    | Remote.Loaded admins ->
                        Html.div [
                            prop.className "divide-y divide-border"
                            prop.children (admins |> List.map (adminRow msgs model dispatch))
                        ]
                ]
            ]
        ]
    ]

// ─── All-teams admin table ───────────────────────────────────────────

let private formatDate (d: System.DateTime) =
    // Explicit components, not `ToString(format)` — custom-format
    // DateTime rendering is unreliable under Fable across runtimes.
    sprintf "%04d-%02d-%02d" d.Year d.Month d.Day

/// One entry per line — emails read better stacked than comma-joined.
let private userIdList (model: Model) (ids: string list) =
    match ids with
    | [] -> Html.span [ prop.className Tokens.Text.secondary; prop.text "—" ]
    | _ ->
        Html.div [
            prop.className "flex flex-col gap-0.5"
            prop.children [ for id in ids -> userIdSpan model "text-xs" id ]
        ]

let private teamNameCell (msgs: PlatformAdminMessages) (team: TeamSummary) =
    Html.div [
        prop.children [
            Html.div [
                prop.className "flex items-center gap-2"
                prop.children [
                    Html.span [
                        prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                        prop.text team.Name
                    ]
                    if team.Archived then
                        Html.span [
                            prop.className
                                "px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide rounded bg-amber-100 text-amber-800"
                            prop.text msgs.ArchivedBadge
                        ]
                ]
            ]
            Html.span [
                prop.className [ "font-mono text-[10px] break-all"; Tokens.Text.secondary ]
                prop.text team.TeamId
            ]
        ]
    ]

/// The row's actions, with the row's last action error under them.
let private teamActionsCell (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) (team: TeamSummary) =
    let pending = model.TeamActionPending |> Set.contains team.TeamId

    let stateFor label =
        Forms.Button.stateOf pending label false

    Html.div [
        prop.className "flex flex-col gap-2 items-end"
        prop.children [
            Html.div [
                prop.className "flex flex-wrap gap-2 justify-end"
                prop.children [
                    if team.Archived then
                        Forms.Button.secondaryWith (stateFor msgs.Restore) msgs.Restore (fun () ->
                            dispatch (SetTeamArchived(team.TeamId, false)))

                        Forms.Button.dangerWith (stateFor msgs.Delete) msgs.Delete (fun () ->
                            dispatch (RequestDeleteTeam team.TeamId))
                    else
                        Forms.Button.secondaryWith (stateFor msgs.Archive) msgs.Archive (fun () ->
                            dispatch (SetTeamArchived(team.TeamId, true)))
                ]
            ]
            noticeOf (model.TeamActionError |> Map.tryFind team.TeamId)
        ]
    ]

let private teamsTableView (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let column header align cell : Data.Column<TeamSummary> = {
        Header = header
        Align = align
        Cell = cell
    }

    let columns = [
        column msgs.ColumnTeam Data.Left (teamNameCell msgs)
        column msgs.ColumnCreated Data.Left (fun team ->
            Html.span [
                prop.className [ "whitespace-nowrap"; Tokens.Text.secondary ]
                prop.text (formatDate team.CreatedAt)
            ])
        column msgs.ColumnMembers Data.Center (fun team -> Html.text (string team.MemberCount))
        column msgs.ColumnOwners Data.Left (fun team -> userIdList model team.Owners)
        column msgs.ColumnAdmins Data.Left (fun team -> userIdList model team.Admins)
        column msgs.ColumnActions Data.Right (teamActionsCell msgs model dispatch)
    ]

    Html.div [
        prop.className "rounded border border-border bg-white"
        prop.children [
            cardHeader msgs.AllTeamsHeading msgs.Refresh (fun () -> dispatch RefreshTeams)
            match model.Teams with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ ->
                Html.p [
                    prop.className [ "text-sm p-3"; Tokens.Text.secondary ]
                    prop.text msgs.LoadingTeams
                ]
            | Remote.Failed msg ->
                Html.div [
                    prop.className "p-3"
                    prop.children [ StateViews.noticeStatic (Notice.Problem msg) ]
                ]
            | Remote.Loaded [] ->
                Html.p [
                    prop.className [ "text-sm p-3"; Tokens.Text.secondary ]
                    prop.text msgs.NoTeamsYet
                ]
            | Remote.Loaded teams -> Data.table columns teams
        ]
    ]

let private fieldLabel (text: string) =
    Html.label [
        prop.className [ "text-xs font-medium"; Tokens.Text.secondary ]
        prop.text text
    ]

let private teamsTabView (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let selfId = UserSession.getUserId ()

    // Prefer the operator's email for the "Self" affordance; fall back to
    // display name, then the raw id.
    let selfDisplay =
        UserSession.getEmail ()
        |> Option.orElse (UserSession.getDisplayName ())
        |> Option.defaultValue selfId

    let ownerEntered = model.NewTeamOwnerUserId.Trim()
    let ownerIsSelf = ownerEntered = selfId

    Html.div [
        prop.className "p-4 flex flex-col gap-4"
        prop.children [
            Html.div [
                prop.className "flex flex-col gap-3 p-3 rounded border border-border bg-white"
                prop.children [
                    Html.h3 [
                        prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                        prop.text msgs.CreateTeamHeading
                    ]
                    Html.p [
                        prop.className [ "text-xs"; Tokens.Text.secondary ]
                        prop.text msgs.CreateTeamHelp
                    ]

                    Html.div [
                        prop.className "flex flex-col gap-1"
                        prop.children [
                            fieldLabel msgs.TeamNameLabel
                            Forms.Input.text model.NewTeamName (SetNewTeamName >> dispatch) msgs.TeamNamePlaceholder
                        ]
                    ]

                    Html.div [
                        prop.className "flex flex-col gap-1"
                        prop.children [
                            fieldLabel msgs.InitialOwnerLabel
                            Html.div [
                                prop.className "flex gap-2 items-start"
                                prop.children [
                                    Html.div [
                                        prop.className "flex-1"
                                        prop.children [
                                            UserDirectoryTypeahead.userTypeahead
                                                model.NewTeamOwnerUserId
                                                (SetNewTeamOwnerUserId >> dispatch)
                                                msgs.UserPickerPlaceholder
                                                UserDirectoryTypeahead.pickUserId
                                        ]
                                    ]
                                    Html.button [
                                        prop.className [
                                            "px-3 py-1 text-sm font-medium rounded border transition-colors"
                                            if ownerIsSelf then
                                                "bg-brand/10 text-brand border-brand"
                                            else
                                                "bg-white border-border hover:bg-gray-50 " + Tokens.Text.primary
                                        ]
                                        prop.title (msgs.SelfTooltip selfDisplay)
                                        prop.text (if ownerIsSelf then msgs.SelfChecked else msgs.SelfUnchecked)
                                        prop.onClick (fun _ -> dispatch UseSelfAsTeamOwner)
                                    ]
                                ]
                            ]
                            // Confirmation line — the resolved email, so the
                            // operator sees who the entered id belongs to. The
                            // input itself carries the stable user id (the
                            // CreateTeamWithOwner API keys on it).
                            if ownerIsSelf then
                                Html.span [
                                    prop.className [ "text-xs"; Tokens.Text.secondary ]
                                    prop.text (msgs.SelfOwnerConfirm selfDisplay)
                                ]
                            elif ownerEntered <> "" then
                                let label = UserDirectory.email model.Directory ownerEntered

                                if label <> ownerEntered then
                                    Html.span [
                                        prop.className [ "text-xs"; Tokens.Text.secondary ]
                                        prop.text (msgs.OwnerConfirm label)
                                    ]
                                else
                                    Html.none
                        ]
                    ]

                    Html.div [
                        prop.className "flex justify-end gap-2"
                        prop.children [
                            Forms.Button.primaryWith
                                (Forms.Button.stateOf model.CreateTeamInFlight msgs.Creating false)
                                msgs.CreateTeam
                                (fun () -> dispatch SubmitCreateTeam)
                        ]
                    ]

                    noticeOf model.CreateTeamError
                ]
            ]
            teamsTableView msgs model dispatch
        ]
    ]

let private platformKnowledgeBaseLabel (msgs: PlatformAdminMessages) (mode: FeatureMode) =
    match mode with
    | FeatureMode.Enabled -> msgs.Enabled
    | FeatureMode.Disabled -> msgs.Disabled

let private platformKnowledgeBaseToggle (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    let renderToggle (current: FeatureMode) =
        let toggle =
            match current with
            | FeatureMode.Enabled ->
                Forms.Button.dangerWith Forms.Button.Enabled msgs.DisableAction (fun () ->
                    dispatch (TogglePlatformKnowledgeBase FeatureMode.Disabled))
            | FeatureMode.Disabled ->
                Forms.Button.primaryWith Forms.Button.Enabled msgs.EnableAction (fun () ->
                    dispatch (TogglePlatformKnowledgeBase FeatureMode.Enabled))

        Html.div [
            prop.className "flex items-center justify-between gap-3"
            prop.children [
                Html.div [
                    prop.className "flex flex-col gap-0.5"
                    prop.children [
                        Html.span [
                            prop.className [ "text-sm font-medium"; Tokens.Text.primary ]
                            prop.text msgs.PlatformKnowledgeBaseHeading
                        ]
                        Html.span [
                            prop.className [ "text-xs"; Tokens.Text.secondary ]
                            prop.text (msgs.KnowledgeBaseStatus(platformKnowledgeBaseLabel msgs current))
                        ]
                    ]
                ]
                toggle
            ]
        ]

    Html.div [
        prop.className "rounded border border-border bg-white p-4"
        prop.children [
            Html.h3 [
                prop.className [ "text-sm font-medium mb-3"; Tokens.Text.primary ]
                prop.text msgs.PlatformKnowledgeBaseHeading
            ]
            match model.PlatformKnowledgeBase with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ ->
                Html.p [
                    prop.className [ "text-sm"; Tokens.Text.secondary ]
                    prop.text msgs.LoadingCurrentState
                ]
            | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
            | Remote.Loaded current -> renderToggle current
            noticeOf model.SettingsError
        ]
    ]

let private settingsTabView (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-4 flex flex-col gap-4"
        prop.children [
            platformKnowledgeBaseToggle msgs model dispatch
            Html.div [
                prop.className "rounded border border-border bg-white p-4"
                prop.children [
                    Html.h3 [
                        prop.className [ "text-sm font-medium mb-2"; Tokens.Text.primary ]
                        prop.text msgs.OtherSettingsHeading
                    ]
                    Html.p [
                        prop.className [ "text-sm"; Tokens.Text.secondary ]
                        prop.text msgs.OtherSettingsBody
                    ]
                ]
            ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

/// The module body as a React COMPONENT, for the same reason
/// `HealthMonitorUI.HealthMonitorBody` is one: a module's `view` is invoked
/// inline by the shell's own render (via `withFullWidthView`), so a hook
/// called there would join the shell's hook order and break the moment the
/// active module changed. A component of its own has a stable identity and
/// its own hook order.
[<ReactComponent>]
let private PlatformAdminBody (config: PublicUtilityConfig) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let msgs = (MessageCatalogProvider.useMessages ()).PlatformAdmin

    let content =
        match model.ActiveTab with
        | AdminsTab -> adminsTabView msgs model dispatch
        | TeamsTab -> teamsTabView msgs model dispatch
        | SettingsTab -> settingsTabView msgs model dispatch

    let publicUtilitySection =
        ToolUp.Platform.PlatformAdmin.PublicUtilityWidgets.render config

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [
            Html.div [
                prop.className "bg-white px-4"
                prop.children [
                    Tabs.bar
                        [
                            AdminsTab, msgs.AdminsTab
                            TeamsTab, msgs.TeamsTab
                            SettingsTab, msgs.SettingsTab
                        ]
                        model.ActiveTab
                        (SwitchTab >> dispatch)
                ]
            ]
            content
            publicUtilitySection
            match model.AssignConfirm with
            | Some confirm -> AssignConfirm.view msgs model.Directory confirm (AssignConfirmMsg >> dispatch)
            | None -> Html.none
            match model.ConfirmDeleteTeam with
            | Some confirm -> ConfirmDeleteTeam.view msgs confirm (DeleteTeamMsg >> dispatch)
            | None -> Html.none
        ]
    ]

/// View factory closed over the deployment's `PublicUtilityConfig` so the
/// public-utility widgets can read ad / premium substrate state. The
/// public-utility widgets append below the existing tab content when
/// `PlatformAdminProfile = PublicUtilityPlatformAdminProfile`.
///
/// Renders as a single full-width pane (via `withFullWidthView` in
/// `create`) rather than the legacy `body, Html.none` `SplitPanel`
/// tuple — the admin tabs (admin list, teams table, settings) have no
/// "controls left / output right" affordance and were being squashed
/// into the left third of the shell by the SplitPanel wrap.
let private viewWith (config: PublicUtilityConfig) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    PlatformAdminBody config model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.PlatformAdmin"

/// Create the Platform Management module as an `ErasedModule`.
/// Fills the `PlatformAdmin` shell slot (default `SlotFill.Default`,
/// admitted on every surface; the slot name is retained for backwards
/// compatibility; the display name + sidebar group both surface as
/// "Platform Management"). The shell's sidebar filter
/// (4f.2) hides the "Platform Management" group from non-admin
/// callers; the module itself does no client-side gating beyond the
/// standard API-error surface (server-side `canModifyPlatformConfig`
/// is the actual enforcement).
///
/// Module Id (`_sdk.PlatformAdmin`) is preserved so the server-side
/// `ServerConfig.ModuleNames` keys (RBAC permission entries) keyed
/// to it don't have to migrate. The display name and group label
/// are presentation only.
let create (config: ModuleLabel option) (publicUtility: PublicUtilityConfig) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Platform Management"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    ToolUp.Platform.ClientModule.create {
        Init = fun () -> init ToolUp.Platform.ClientModuleContext.empty
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withContextInit init
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withView (fun model ctx ->
        PageContent.FullWidth(viewWith publicUtility model ctx.Dispatch))
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register