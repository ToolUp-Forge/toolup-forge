// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Platform Management as a Fuaran-UI tree, built and drawn in a file of its
/// own, which `PlatformAdminUI`'s view mounts through `TreeSurface.deferred`
/// (the shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// The admin list and the team rows are kept, every row id comes from the
/// row's identity and every row message carries it. What stays React is an
/// island: the team name's input and the two directory typeaheads own a
/// display-state hook (the typeaheads their own requests too), and the
/// public-utility widgets their own state and requests.
module internal PlatformAdminTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open PlatformAdminUI
open KeepScoped

/// A notice under the control it reports on; it clears with the next attempt.
let private noticeOf (id: string) (notice: Notice option) : Node<'Msg> list =
    match notice with
    | Some(Notice.Problem text) -> [
        Fuaran.callout id {
            Defaults.callout with
                Body = Nodes.literal text
                Tone = ToneVariant.Critical
        }
      ]
    | Some(Notice.Info text) -> [
        Fuaran.callout id {
            Defaults.callout with
                Body = Nodes.literal text
                Tone = ToneVariant.Info
        }
      ]
    | None -> []

/// A card's header: its heading, at the level the cards' headings take, and
/// a Refresh button.
let private cardHeader (id: string) (heading: string) (refresh: string) (onRefresh: 'Msg) : Node<'Msg> =
    Nodes.Layout.row id [
        Nodes.heading (Nodes.Ids.child id "title") 3 heading
        Nodes.Actions.secondary (Nodes.Ids.child id "refresh") refresh onRefresh
    ]

/// A directory typeahead, an island: its input carries only a placeholder, so a
/// label that only assistive technology reads wraps it and names it.
let private userPicker (id: string) (label: string) (placeholder: string) (value: string) (onPick: string -> unit) =
    TreeLowering.island id (fun () ->
        Feliz.Html.label [
            Feliz.prop.className "block flex-1"
            Feliz.prop.children [
                Feliz.Html.span [ Feliz.prop.className "sr-only"; Feliz.prop.text label ]
                UserDirectoryTypeahead.userTypeahead value onPick placeholder UserDirectoryTypeahead.pickUserId
            ]
        ])

let private modal (id: string) (heading: string) (onDismiss: 'Msg) (children: Node<'Msg> list) : Node<'Msg> =
    Fuaran.modal id {
        Defaults.modal with
            Open = Binding.Static(Some true)
            Heading = Some(Nodes.literal heading)
            OnDismiss = Some(Action.dispatch onDismiss)
            Children = children
    }

// ─── The two confirmations ───────────────────────────────────────────

/// "Are you sure?" before a grant, naming who receives it.
let assignConfirm
    (msgs: PlatformAdminMessages)
    (directory: UserDirectory.Model)
    (state: AssignConfirm.Model)
    (send: AssignConfirm.Msg -> 'Msg)
    : Node<'Msg> =
    let label = UserDirectory.email directory state.TargetUserId
    let resolved = label <> state.TargetUserId

    modal "assign-confirm" msgs.GrantHeading (send AssignConfirm.Cancel) [
        Nodes.text "assign-confirm-body" msgs.GrantBody
        Nodes.Layout.column "assign-confirm-who" [
            Nodes.text "assign-confirm-who-label" (if resolved then msgs.UserLabel else msgs.UserIdLabel)
            Nodes.text "assign-confirm-who-name" label
            // The raw id stays visible for traceability when an email resolved above it.
            if resolved then
                Nodes.text "assign-confirm-who-id" state.TargetUserId
        ]
        Nodes.Layout.row "assign-confirm-actions" [
            Nodes.Actions.button
                "assign-confirm-cancel"
                ButtonVariant.Secondary
                msgs.Cancel
                state.InFlight
                (send AssignConfirm.Cancel)
            Nodes.Actions.button
                "assign-confirm-confirm"
                ButtonVariant.Primary
                msgs.GrantConfirm
                state.InFlight
                (send AssignConfirm.Confirm)
        ]
    ]

/// The delete-team confirmation, echoing the team's name.
let deleteTeamConfirm
    (msgs: PlatformAdminMessages)
    (state: ConfirmDeleteTeam.Model)
    (send: ConfirmDeleteTeam.Msg -> 'Msg)
    : Node<'Msg> =
    modal "delete-team-confirm" msgs.DeleteTeamHeading (send ConfirmDeleteTeam.Cancel) [
        Nodes.text "delete-team-confirm-body" msgs.DeleteTeamBody
        Nodes.Layout.column "delete-team-confirm-team" [
            Nodes.text "delete-team-confirm-team-label" msgs.TeamLabel
            Nodes.text "delete-team-confirm-team-name" state.TeamName
        ]
        Nodes.Layout.row "delete-team-confirm-actions" [
            Nodes.Actions.button
                "delete-team-confirm-cancel"
                ButtonVariant.Secondary
                msgs.Cancel
                state.InFlight
                (send ConfirmDeleteTeam.Cancel)
            Nodes.Actions.button
                "delete-team-confirm-confirm"
                ButtonVariant.Destructive
                msgs.DeleteTeam
                state.InFlight
                (send ConfirmDeleteTeam.Confirm)
        ]
    ]

// ─── Admins ──────────────────────────────────────────────────────────

type private AdminsContext = {
    Msgs: PlatformAdminMessages
    Admins: Remote<string list, string>
    RevokeError: Map<string, Notice>
    Directory: UserDirectory.Model
}

/// The admin list. Its rows are user ids, which cannot key a `Keep`, so the
/// list is kept whole on what it shows.
let private admins =
    One(
        (fun (a: AdminsContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Admins b.Admins
            && sameObject a.RevokeError b.RevokeError
            && sameObject a.Directory b.Directory),
        fun context ->
            let msgs = context.Msgs

            Nodes.Layout.column "admins-list" [
                cardHeader "admins-list-header" msgs.CurrentAdminsHeading msgs.Refresh RefreshAdmins
                match context.Admins with
                | Remote.NotAsked
                | Remote.Loading
                | Remote.Reloading _ -> Nodes.text "admins-list-loading" msgs.Loading
                | Remote.Failed error -> yield! noticeOf "admins-list-error" (Some(Notice.Problem error))
                | Remote.Loaded [] -> Nodes.text "admins-list-none" msgs.NoAdmins
                | Remote.Loaded ids ->
                    for userId in ids do
                        let id = Nodes.Ids.keyed "admin" userId

                        Nodes.Layout.column id [
                            Nodes.Layout.row (Nodes.Ids.child id "row") [
                                Nodes.text (Nodes.Ids.child id "name") (UserDirectory.email context.Directory userId)
                                Nodes.Actions.danger (Nodes.Ids.child id "revoke") msgs.Revoke (RequestRevoke userId)
                            ]
                            yield! noticeOf (Nodes.Ids.child id "error") (context.RevokeError |> Map.tryFind userId)
                        ]
            ]
    )

let private adminsTab (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "admins" [
        Nodes.Panel.section "assign" msgs.AssignHeading [
            Nodes.text "assign-help" msgs.AssignHelp
            Nodes.Layout.row "assign-pick" [
                userPicker
                    "assign-user"
                    msgs.UserPickerPlaceholder
                    msgs.UserPickerPlaceholder
                    model.AssignInput
                    (UpdateAssignInput >> dispatch)
                Nodes.Actions.primary "assign-submit" msgs.Assign RequestAssignConfirm
            ]
            yield! noticeOf "assign-error" model.AssignError
        ]
        admins.Node {
            Msgs = msgs
            Admins = model.Admins
            RevokeError = model.RevokeError
            Directory = model.Directory
        }
    ]

// ─── Teams ───────────────────────────────────────────────────────────

/// A date as its explicit components: custom-format `DateTime` rendering is
/// unreliable under Fable across runtimes.
let private formatDate (d: System.DateTime) =
    sprintf "%04d-%02d-%02d" d.Year d.Month d.Day

type private TeamsContext = {
    Msgs: PlatformAdminMessages
    Directory: UserDirectory.Model
    Pending: Set<string>
    Errors: Map<string, Notice>
}

let private teamRows =
    Rows(
        (fun (a: TeamsContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Directory b.Directory
            && sameObject a.Pending b.Pending
            && sameObject a.Errors b.Errors),
        fun context (team: TeamSummary) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "team" team.TeamId
            let pending = context.Pending |> Set.contains team.TeamId

            // A labelled value; a list of user ids stacks, one per line.
            let field (part: string) (label: string) (values: string list) =
                let fieldId = Nodes.Ids.child id part

                Nodes.Layout.column fieldId [
                    Nodes.text (Nodes.Ids.child fieldId "label") label
                    match values with
                    | [] -> Nodes.text (Nodes.Ids.child fieldId "none") "—"
                    | _ ->
                        for value in values do
                            Nodes.text (Nodes.Ids.keyed fieldId value) value
                ]

            let emails = List.map (UserDirectory.email context.Directory)

            Nodes.Layout.column id [
                Nodes.Layout.row (Nodes.Ids.child id "row") [
                    Nodes.Layout.column (Nodes.Ids.child id "about") [
                        Nodes.text (Nodes.Ids.child id "name") team.Name
                        if team.Archived then
                            Nodes.text (Nodes.Ids.child id "archived") msgs.ArchivedBadge
                        Nodes.text (Nodes.Ids.child id "id") team.TeamId
                    ]
                    field "created" msgs.ColumnCreated [ formatDate team.CreatedAt ]
                    field "members" msgs.ColumnMembers [ string team.MemberCount ]
                    field "owners" msgs.ColumnOwners (emails team.Owners)
                    field "admins" msgs.ColumnAdmins (emails team.Admins)
                    Nodes.Layout.row (Nodes.Ids.child id "actions") [
                        if team.Archived then
                            Nodes.Actions.button
                                (Nodes.Ids.child id "restore")
                                ButtonVariant.Secondary
                                msgs.Restore
                                pending
                                (SetTeamArchived(team.TeamId, false))

                            Nodes.Actions.button
                                (Nodes.Ids.child id "delete")
                                ButtonVariant.Destructive
                                msgs.Delete
                                pending
                                (RequestDeleteTeam team.TeamId)
                        else
                            Nodes.Actions.button
                                (Nodes.Ids.child id "archive")
                                ButtonVariant.Secondary
                                msgs.Archive
                                pending
                                (SetTeamArchived(team.TeamId, true))
                    ]
                ]
                yield! noticeOf (Nodes.Ids.child id "error") (context.Errors |> Map.tryFind team.TeamId)
            ]
    )

let private teamsList (msgs: PlatformAdminMessages) (model: Model) : Node<Msg> =
    Nodes.Layout.column "teams-list" [
        cardHeader "teams-list-header" msgs.AllTeamsHeading msgs.Refresh RefreshTeams
        match model.Teams with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "teams-list-loading" msgs.LoadingTeams
        | Remote.Failed error -> yield! noticeOf "teams-list-error" (Some(Notice.Problem error))
        | Remote.Loaded [] -> Nodes.text "teams-list-none" msgs.NoTeamsYet
        | Remote.Loaded teams ->
            yield!
                teamRows.List
                    {
                        Msgs = msgs
                        Directory = model.Directory
                        Pending = model.TeamActionPending
                        Errors = model.TeamActionError
                    }
                    teams
    ]

/// Create a team: its name, the initial owner (a typeahead, or the caller
/// through "Self"), then the submit.
let private createTeam (msgs: PlatformAdminMessages) (self: Caller) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    let selfId = self.UserId
    let selfDisplay = self.Display

    let ownerEntered = model.NewTeamOwnerUserId.Trim()
    let ownerIsSelf = ownerEntered = selfId

    Nodes.Panel.section "create-team" msgs.CreateTeamHeading [
        Nodes.text "create-team-help" msgs.CreateTeamHelp
        // The name is display state its input holds until the operator leaves
        // it or presses Enter, so free typing does not round-trip through
        // `update`: an island, named by the label wrapping it.
        TreeLowering.island "create-team-name" (fun () ->
            Feliz.Html.label [
                Feliz.prop.className "flex flex-col gap-1"
                Feliz.prop.children [
                    Feliz.Html.span [
                        Feliz.prop.className "text-xs font-medium"
                        Feliz.prop.text msgs.TeamNameLabel
                    ]
                    Forms.Input.text model.NewTeamName (SetNewTeamName >> dispatch) msgs.TeamNamePlaceholder
                ]
            ])
        Nodes.Layout.row "create-team-owner" [
            userPicker
                "create-team-owner-user"
                msgs.InitialOwnerLabel
                msgs.UserPickerPlaceholder
                model.NewTeamOwnerUserId
                (SetNewTeamOwnerUserId >> dispatch)
            Nodes.Actions.button
                "create-team-owner-self"
                (if ownerIsSelf then
                     ButtonVariant.Primary
                 else
                     ButtonVariant.Secondary)
                (if ownerIsSelf then msgs.SelfChecked else msgs.SelfUnchecked)
                false
                UseSelfAsTeamOwner
        ]
        // The resolved email, so the operator sees who the entered id belongs
        // to; the field itself carries the stable user id.
        if ownerIsSelf then
            Nodes.text "create-team-owner-confirm" (msgs.SelfOwnerConfirm selfDisplay)
        elif ownerEntered <> "" then
            let label = UserDirectory.email model.Directory ownerEntered

            if label <> ownerEntered then
                Nodes.text "create-team-owner-confirm" (msgs.OwnerConfirm label)
        Nodes.Actions.button
            "create-team-submit"
            ButtonVariant.Primary
            (if model.CreateTeamInFlight then
                 msgs.Creating
             else
                 msgs.CreateTeam)
            model.CreateTeamInFlight
            SubmitCreateTeam
        yield! noticeOf "create-team-error" model.CreateTeamError
    ]

let private teamsTab (msgs: PlatformAdminMessages) (self: Caller) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "teams" [ createTeam msgs self model dispatch; teamsList msgs model ]

// ─── Settings ────────────────────────────────────────────────────────

let private settingsTab (msgs: PlatformAdminMessages) (model: Model) : Node<Msg> =
    Nodes.Layout.column "settings" [
        Nodes.Panel.section "knowledge-base" msgs.PlatformKnowledgeBaseHeading [
            match model.PlatformKnowledgeBase with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ -> Nodes.text "knowledge-base-loading" msgs.LoadingCurrentState
            | Remote.Failed error -> yield! noticeOf "knowledge-base-error" (Some(Notice.Problem error))
            | Remote.Loaded current ->
                let label =
                    match current with
                    | FeatureMode.Enabled -> msgs.Enabled
                    | FeatureMode.Disabled -> msgs.Disabled

                Nodes.Layout.row "knowledge-base-state" [
                    Nodes.text "knowledge-base-status" (msgs.KnowledgeBaseStatus label)
                    match current with
                    | FeatureMode.Enabled ->
                        Nodes.Actions.danger
                            "knowledge-base-toggle"
                            msgs.DisableAction
                            (TogglePlatformKnowledgeBase FeatureMode.Disabled)
                    | FeatureMode.Disabled ->
                        Nodes.Actions.primary
                            "knowledge-base-toggle"
                            msgs.EnableAction
                            (TogglePlatformKnowledgeBase FeatureMode.Enabled)
                ]
            yield! noticeOf "knowledge-base-settings-error" model.SettingsError
        ]
        Nodes.Panel.section "other-settings" msgs.OtherSettingsHeading [
            Nodes.text "other-settings-body" msgs.OtherSettingsBody
        ]
    ]

// ─── The page ────────────────────────────────────────────────────────

let private tabs = [ AdminsTab; TeamsTab; SettingsTab ]

/// The page: the three tabs, the active one's panel, the public-utility
/// widgets and an open confirmation. `dispatch` reaches the islands only;
/// every node sends its message through the tree.
let tree
    (msgs: PlatformAdminMessages)
    (widgets: PublicUtilityWidgetsMessages)
    (config: PublicUtilityConfig)
    (self: Caller)
    (model: Model)
    (dispatch: Msg -> unit)
    : Node<Msg> =
    let panel tab =
        if tab <> model.ActiveTab then
            Nodes.Layout.column (sprintf "tab-%A" tab) []
        else
            match tab with
            | AdminsTab -> adminsTab msgs model dispatch
            | TeamsTab -> teamsTab msgs self model dispatch
            | SettingsTab -> settingsTab msgs model

    Nodes.Layout.column "platform-admin" [
        Fuaran.tabs "platform-admin-tabs" {
            Defaults.tabs with
                ActiveIndex = Binding.Static(Some(List.findIndex ((=) model.ActiveTab) tabs))
                OnSelect = Some(fun i -> Action.dispatch (SwitchTab tabs[i]))
                TabHeaders =
                    Some [
                        for label in [ msgs.AdminsTab; msgs.TeamsTab; msgs.SettingsTab ] ->
                            {
                                Label = Nodes.literal label
                                Icon = None
                                Disabled = None
                            }
                    ]
                Children = List.map panel tabs
        }
        TreeLowering.island "public-utility" (fun () -> PlatformAdmin.PublicUtilityWidgets.render widgets config)
        match model.AssignConfirm with
        | Some confirm -> assignConfirm msgs model.Directory confirm AssignConfirmMsg
        | None -> ()
        match model.ConfirmDeleteTeam with
        | Some confirm -> deleteTeamConfirm msgs confirm DeleteTeamMsg
        | None -> ()
    ]

let private hostOf (locale: string) = {
    TreeLowering.TreeHost.ofSources HostBindingSources.empty with
        Locale = locale
}

/// The deferred component `PlatformAdminUI`'s view mounts.
[<Feliz.ReactComponent>]
let Surface (props: PlatformAdminUI.SurfaceProps) : Fable.React.ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps
            (hostOf props.Locale)
            props.Dispatch
            (tree props.Messages props.Widgets props.Config (PlatformAdminUI.caller ()) props.Model props.Dispatch)
    )

/// The deferred component `PlatformAdminUI.AssignConfirm.view` mounts.
[<Feliz.ReactComponent>]
let AssignConfirmSurface (props: PlatformAdminUI.AssignConfirm.SurfaceProps) : Fable.React.ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps
            (TreeLowering.TreeHost.ofSources HostBindingSources.empty)
            props.Dispatch
            (assignConfirm props.Messages props.Directory props.Model id)
    )

/// The deferred component `PlatformAdminUI.ConfirmDeleteTeam.view` mounts.
[<Feliz.ReactComponent>]
let DeleteTeamSurface (props: PlatformAdminUI.ConfirmDeleteTeam.SurfaceProps) : Fable.React.ReactElement =
    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps
            (TreeLowering.TreeHost.ofSources HostBindingSources.empty)
            props.Dispatch
            (deleteTeamConfirm props.Messages props.Model id)
    )