// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Feliz pages the shipped trees replaced, kept as the reference the
/// migration kit's parity assertion and the browser leg's "before" arm read.
/// None of them is mounted by the SDK: each is the page exactly as it shipped
/// before its tree, so a parity difference names what the move changed.
module ToolUp.AI.Client.Tests.FelizReferenceViews

open Fuaran.MVU
open System
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

/// The team manager's page as Feliz (`TeamManagerUI`'s view before its tree).
module TeamManager =
    open TeamManagerUI

    let private teamRow
        (msgs: TeamManagerMessages)
        (team: TeamInfo)
        (isActive: bool)
        (onSelect: unit -> unit)
        (onSwitch: unit -> unit)
        =
        Html.div [
            prop.className [
                "flex items-center justify-between p-4 border rounded-lg mb-2"
                if isActive then
                    "border-brand bg-brand/5"
                else
                    "border-border"
            ]
            prop.children [
                Html.div [
                    prop.children [
                        Html.div [
                            prop.className "flex items-center gap-2"
                            prop.children [
                                Html.span [ prop.className "font-medium text-base"; prop.text team.Name ]
                                if isActive then
                                    Html.span [
                                        prop.className "text-xs text-brand font-medium px-2 py-0.5 rounded bg-brand/10"
                                        prop.text msgs.ActiveBadge
                                    ]
                            ]
                        ]
                        Html.span [
                            prop.className "text-xs text-muted"
                            prop.text (msgs.TeamIdLabel team.TeamId)
                        ]
                    ]
                ]
                Html.div [
                    prop.className "flex gap-2"
                    prop.children [
                        if not isActive then
                            Forms.Button.secondary msgs.Switch onSwitch
                        Forms.Button.secondary msgs.Manage onSelect
                    ]
                ]
            ]
        ]

    let private myTeamsView (msgs: TeamManagerMessages) (model: Model) (dispatch: Msg -> unit) =
        Html.div [
            prop.className "space-y-4"
            prop.children [
                Layout.Panel.panel msgs.MyTeamsPanel [
                    if model.Teams.IsEmpty then
                        Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.NoTeamsYet ]
                    else
                        Html.div [
                            for team in model.Teams do
                                teamRow
                                    msgs
                                    team
                                    (model.ActiveTeamId = Some team.TeamId)
                                    (fun () -> dispatch (SelectTeam team.TeamId))
                                    (fun () -> dispatch (SwitchActiveTeam team.TeamId))
                        ]
                ]
            ]
        ]


    /// A select over `roles`: each option is one of the DU's values, so a pick
    /// is the role itself, never a string parsed back into one.
    let private roleSelect (roles: TeamRole list) (selected: TeamRole) (onSelect: TeamRole -> unit) =
        Html.select [
            prop.className [
                "text-xs border border-border rounded px-2 py-1"
                "focus:outline-none focus:border-brand"
            ]
            prop.value (TeamRoles.displayName selected)
            prop.onChange (fun (v: string) ->
                roles
                |> List.tryFind (fun role -> TeamRoles.displayName role = v)
                |> Option.iter onSelect)
            prop.children [
                for role in roles do
                    Html.option [
                        prop.value (TeamRoles.displayName role)
                        prop.text (TeamRoles.displayName role)
                    ]
            ]
        ]

    /// A role-picker row of toggle buttons, for the invite forms.
    let private roleButtons (msgs: TeamManagerMessages) (roles: TeamRole list) (selected: TeamRole) onSelect =
        Html.div [
            prop.className "flex items-center gap-3 flex-wrap"
            prop.children [
                Html.span [ prop.className "text-sm text-muted"; prop.text msgs.RoleLabel ]
                for role in roles do
                    Html.button [
                        prop.className [
                            "px-3 py-1 rounded text-sm transition-colors border"
                            if selected = role then
                                "bg-brand/10 text-brand border-brand"
                            else
                                "bg-white text-text border-border hover:bg-gray-50"
                        ]
                        prop.text (TeamRoles.displayName role)
                        prop.onClick (fun _ -> onSelect role)
                    ]
            ]
        ]


    let private memberRow
        (msgs: TeamManagerMessages)
        (teamId: string)
        (selfId: string)
        (callerRole: TeamRole option)
        (directory: UserDirectory.Model)
        (membership: TeamMembership)
        (dispatch: Msg -> unit)
        =
        let isSelf = membership.UserId = selfId

        // Caller may act on this membership when they hold a role whose
        // `canManageRole` covers the membership's current role. Owner →
        // any non-Owner; Admin → Member only; everyone else → no.
        let canManageTarget =
            match callerRole with
            | Some r -> TeamRoles.canManageRole r membership.Role
            | None -> false

        // The caller's own row reads the session's name and email; other
        // members read the directory, falling back to the raw id.
        let primaryLabel, secondaryLabel =
            if isSelf then
                labelFor directory selfId membership.UserId, UserSession.getEmail ()
            else
                UserDirectory.labels directory membership.UserId

        Html.div [
            prop.className "flex items-center justify-between p-3 border border-border rounded-lg mb-2"
            prop.children [
                Html.div [
                    prop.className "flex items-center gap-3"
                    prop.children [
                        Html.div [
                            prop.className "flex flex-col"
                            prop.children [
                                Html.span [ prop.className "font-medium"; prop.text primaryLabel ]
                                match secondaryLabel with
                                | Some email -> Html.span [ prop.className "text-xs text-muted"; prop.text email ]
                                | None -> ()
                            ]
                        ]
                        if canManageTarget && not isSelf then
                            roleSelect (assignableRoles callerRole) membership.Role (fun newRole ->
                                if newRole <> membership.Role then
                                    dispatch (ChangeMemberRole(teamId, membership.UserId, newRole)))
                        else
                            Html.span [
                                prop.className [ "text-xs px-2 py-0.5 rounded"; Tokens.Text.muted; Tokens.Bg.subtle ]
                                prop.text (TeamRoles.displayName membership.Role)
                            ]
                        if isSelf then
                            Html.span [ prop.className "text-xs text-brand font-medium"; prop.text msgs.YouSuffix ]
                    ]
                ]
                if canManageTarget && not isSelf then
                    Forms.Button.secondary msgs.RemoveMember (fun () ->
                        dispatch (RemoveMember(teamId, membership.UserId)))
            ]
        ]

    let private addMemberForm
        (msgs: TeamManagerMessages)
        (teamId: string)
        (callerRole: TeamRole option)
        (model: Model)
        (dispatch: Msg -> unit)
        =
        Layout.Panel.panel msgs.InvitePanel [
            Html.div [
                prop.className "flex flex-col gap-3"
                prop.children [
                    Html.p [ prop.className "text-xs text-muted"; prop.text msgs.InviteHelp ]

                    UserDirectoryTypeahead.userTypeahead
                        model.AddMemberUserId
                        (fun v -> dispatch (SetAddMemberUserId v))
                        msgs.InviteIdentifierPlaceholder
                        UserDirectoryTypeahead.pickEmailPreferred

                    roleButtons msgs (assignableRoles callerRole) model.AddMemberRole (SetAddMemberRole >> dispatch)

                    Html.div [
                        // Busy while a previous add/invite is in flight, so a double-click can't fire two requests.
                        Forms.Button.primaryWith
                            (Forms.Button.stateOf model.AddMemberSubmitting msgs.Inviting false)
                            msgs.InviteMember
                            (fun () -> dispatch (AddMember teamId))
                    ]
                ]
            ]
        ]


    /// The two-step "Transfer ownership" modal. Step one filters the team's
    /// current members (never mints a new membership); picking one advances to
    /// step two, an explicit confirmation naming both parties before the API
    /// fires. Rendered only for the real team Owner (the caller opening it), so
    /// the candidate list always excludes the caller.
    let private transferOwnershipView
        (msgs: TeamManagerMessages)
        (state: TransferOwnership.Model)
        (model: Model)
        (dispatch: TransferOwnership.Msg -> unit)
        =
        let selfId = selfUserId ()
        let members = model.Members |> Map.tryFind state.TeamId |> Option.defaultValue []
        let teamName = teamNameOf model state.TeamId
        let label = labelFor model.Directory selfId

        // Candidates: every current member except the caller (the outgoing
        // Owner). Ownership can only move to someone already on the team.
        let candidates = members |> List.filter (fun m -> m.UserId <> selfId)

        let query = state.Query.Trim().ToLower()

        let filtered =
            if query = "" then
                candidates
            else
                candidates
                |> List.filter (fun m -> (label m.UserId).ToLower().Contains query || m.UserId.ToLower().Contains query)

        match state.Step with
        | TransferOwnership.PickNewOwner ->
            Modal.overlay msgs.TransferOwnership [
                Html.p [
                    prop.className "text-sm text-muted"
                    prop.text (msgs.TransferOwnershipHelp teamName)
                ]
                Forms.Input.text state.Query (TransferOwnership.SetQuery >> dispatch) msgs.TransferFilterPlaceholder
                if List.isEmpty candidates then
                    Html.p [
                        prop.className "text-sm text-muted py-2"
                        prop.text msgs.TransferNoOtherMembers
                    ]
                elif List.isEmpty filtered then
                    Html.p [ prop.className "text-sm text-muted py-2"; prop.text msgs.TransferNoMatches ]
                else
                    Html.div [
                        prop.className "max-h-64 overflow-y-auto flex flex-col gap-1"
                        prop.children [
                            for m in filtered do
                                Html.button [
                                    prop.className
                                        "flex items-center justify-between w-full text-left px-3 py-2 border border-border rounded-lg hover:bg-gray-50 transition-colors"
                                    prop.onClick (fun _ -> dispatch (TransferOwnership.SelectCandidate m.UserId))
                                    prop.children [
                                        Html.span [ prop.className "font-medium text-sm"; prop.text (label m.UserId) ]
                                        Html.span [
                                            prop.className "text-xs text-muted"
                                            prop.text (TeamRoles.displayName m.Role)
                                        ]
                                    ]
                                ]
                        ]
                    ]
            ] [
                Forms.Button.secondary msgs.Cancel (fun () -> dispatch TransferOwnership.Close)
            ]
        | TransferOwnership.ConfirmTransfer ->
            let newOwnerLabel =
                state.SelectedUserId
                |> Option.map label
                |> Option.defaultValue msgs.TransferSelectedMemberFallback

            Modal.overlay msgs.TransferConfirmHeading [
                Html.p [
                    prop.className "text-sm text-muted"
                    prop.text (msgs.TransferConfirmPrompt teamName (label selfId) newOwnerLabel)
                ]
                Html.p [
                    prop.className "text-sm text-muted"
                    prop.text (msgs.TransferRoleExplanation newOwnerLabel)
                ]
                match state.SubmitError with
                | Some msg -> Html.p [ prop.className [ "text-sm"; Tokens.Text.danger ]; prop.text msg ]
                | None -> Html.none
            ] [
                Forms.Button.secondary msgs.Back (fun () -> dispatch TransferOwnership.BackToPick)
                Forms.Button.primaryWith
                    (Forms.Button.stateOf state.Submitting msgs.Transferring false)
                    msgs.ConfirmTransfer
                    (fun () -> dispatch TransferOwnership.Submit)
            ]

    let private teamDetailsView (msgs: TeamManagerMessages) (teamId: string) (model: Model) (dispatch: Msg -> unit) =
        let members = model.Members |> Map.tryFind teamId |> Option.defaultValue []
        let callerRole = effectiveCallerRole model teamId

        let canManage =
            callerRole |> Option.map TeamRoles.canManageMembers |> Option.defaultValue false

        // Transfer ownership is Owner-only and, unlike the other management
        // controls, does NOT honour the Platform-Admin bypass — the server
        // gates on the caller's OWN membership role being Owner (the caller
        // is the outgoing Owner). Gate the affordance on the real membership
        // role so it stays hidden for Admins, Members, and Platform Admins
        // who aren't the team's Owner.
        let isRealOwner = (model.RoleInTeam |> Map.tryFind teamId) = Some Owner

        let selfId = selfUserId ()

        Html.div [
            prop.className "space-y-4"
            prop.children [
                Html.div [
                    prop.className "flex items-center gap-3"
                    prop.children [
                        Html.button [
                            prop.className "text-sm text-brand hover:underline"
                            prop.text msgs.BreadcrumbMyTeams
                            prop.onClick (fun _ -> dispatch BackToMyTeams)
                        ]
                        Html.span [ prop.className "text-muted"; prop.text "/" ]
                        Html.span [ prop.className "font-medium"; prop.text (teamNameOf model teamId) ]
                        if canManage then
                            Html.div [
                                prop.className "ml-auto flex gap-2"
                                prop.children [
                                    if isRealOwner then
                                        Forms.Button.secondary msgs.TransferOwnership (fun () ->
                                            dispatch (OpenTransferOwnership teamId))
                                    Forms.Button.secondary msgs.PendingInvites (fun () ->
                                        dispatch (NavigatePendingInvites teamId))
                                ]
                            ]
                    ]
                ]

                Layout.Panel.panel msgs.MembersPanel [
                    if members.IsEmpty then
                        Html.p [ prop.className "text-sm text-muted py-2"; prop.text msgs.NoMembers ]
                    else
                        Html.div [
                            for m in members do
                                memberRow msgs teamId selfId callerRole model.Directory m dispatch
                        ]
                ]

                if canManage then
                    addMemberForm msgs teamId callerRole model dispatch

                // The team's in-platform publication readers, for its real
                // Owner or Admin (no platform-admin bypass, as on the server).
                PublicationReadersAdminUI.panel
                    msgs
                    teamId
                    model.ActiveTeamId
                    (model.RoleInTeam
                     |> Map.tryFind teamId
                     |> Option.exists TeamRoles.canWriteTeamConfig)

                match model.Transfer with
                | Some state when state.TeamId = teamId ->
                    transferOwnershipView msgs state model (TransferMsg >> dispatch)
                | _ -> Html.none
            ]
        ]

    // ─── Pending Invites view (email sub-tab) ───────────────────────────

    /// The sub-tab inside the Pending Invites view. Only `EmailInvites` is
    /// populated today; a link-based tab would land beside it.
    type private PendingInvitesSubTab = EmailInvites

    let private pendingEmailRow
        (msgs: TeamManagerMessages)
        (teamId: string)
        (email: string)
        (entry: PendingInviteByEmail)
        (dispatch: Msg -> unit)
        =
        Html.div [
            prop.className "flex items-center justify-between p-3 border border-border rounded-lg mb-2"
            prop.children [
                Html.div [
                    prop.className "flex flex-col"
                    prop.children [
                        Html.span [ prop.className "font-medium"; prop.text email ]
                        Html.span [
                            prop.className "text-xs text-muted"
                            prop.text (
                                msgs.InviteExpires
                                    (TeamRoles.displayName entry.Role)
                                    (entry.ExpiresAt.ToString "yyyy-MM-dd")
                            )
                        ]
                    ]
                ]
                Forms.Button.secondary msgs.RevokeInvite (fun () -> dispatch (OpenRevokeByEmail(teamId, email)))
            ]
        ]

    /// One recently-expired invite, rendered greyed with an **Expired** badge
    /// and a one-click **Re-issue** action. The re-issue re-calls
    /// `IssuePendingInviteByEmail` with the original role and the default
    /// expiry; the row then migrates back to the live list above.
    let private expiredInviteRow
        (msgs: TeamManagerMessages)
        (teamId: string)
        (entry: TeamInviteExpiredPayload)
        (inFlight: bool)
        (dispatch: Msg -> unit)
        =
        Html.div [
            prop.className [
                "flex items-center justify-between p-3 border border-border rounded-lg mb-2 opacity-70"
                Tokens.Bg.subtle
            ]
            prop.children [
                Html.div [
                    prop.className "flex flex-col"
                    prop.children [
                        Html.div [
                            prop.className "flex items-center gap-2"
                            prop.children [
                                Html.span [ prop.className "font-medium text-muted"; prop.text entry.InviteeEmail ]
                                Html.span [
                                    prop.className [
                                        "text-xs font-medium px-2 py-0.5 rounded-full"
                                        Tokens.Bg.subtle
                                        Tokens.Text.secondary
                                    ]
                                    prop.text msgs.Expired
                                ]
                            ]
                        ]
                        Html.span [
                            prop.className "text-xs text-muted"
                            prop.text (
                                msgs.InviteExpired
                                    (TeamRoles.displayName entry.Role)
                                    (entry.ExpiredAt.ToString "yyyy-MM-dd")
                            )
                        ]
                    ]
                ]
                Forms.Button.secondaryWith (Forms.Button.stateOf inFlight msgs.Reissuing false) msgs.Reissue (fun () ->
                    dispatch (ReissueExpiredInvite(teamId, entry.InviteeEmail, entry.Role)))
            ]
        ]

    let private issueByEmailView
        (msgs: TeamManagerMessages)
        (callerRole: TeamRole option)
        (state: IssueByEmail.Model)
        (dispatch: IssueByEmail.Msg -> unit)
        =
        Modal.overlay msgs.InviteByEmailHeading [
            Html.p [ prop.className "text-sm text-muted"; prop.text msgs.InviteByEmailHelp ]
            Forms.Input.text state.Email (IssueByEmail.SetEmail >> dispatch) msgs.EmailPlaceholder
            // `Owner` is excluded everywhere — initial ownership is set via
            // Platform Management at team-create time. `Admin` is offered
            // only to callers who can assign Admin (Owners + Platform
            // Admins); Admin callers see only `Member`.
            roleButtons msgs (assignableRoles callerRole) state.Role (IssueByEmail.SetRole >> dispatch)
            Html.div [
                prop.className "flex items-center gap-3"
                prop.children [
                    Html.label [ prop.className "text-sm text-muted"; prop.text msgs.ExpiresInDays ]
                    Html.div [
                        prop.className "w-24"
                        prop.children [
                            Forms.Input.text
                                (string state.ExpiresInDays)
                                (fun v ->
                                    match Int32.TryParse v with
                                    | true, n -> dispatch (IssueByEmail.SetExpiresInDays n)
                                    | _ -> ())
                                ""
                        ]
                    ]
                ]
            ]
            match state.SubmitError with
            | Some msg -> Html.p [ prop.className [ "text-sm"; Tokens.Text.danger ]; prop.text msg ]
            | None -> Html.none
        ] [
            Forms.Button.secondary msgs.Cancel (fun () -> dispatch IssueByEmail.Close)
            Forms.Button.primaryWith
                (Forms.Button.stateOf state.Submitting msgs.Issuing false)
                msgs.IssueInvitation
                (fun () -> dispatch IssueByEmail.Submit)
        ]

    let private revokeByEmailView
        (msgs: TeamManagerMessages)
        (state: RevokeByEmail.Model)
        (model: Model)
        (dispatch: RevokeByEmail.Msg -> unit)
        =
        // The catalog has no "Revoking…" string: the confirm label doubles as the busy label.
        Modal.confirm
            {
                Title = msgs.RevokeInviteHeading
                Confirm = msgs.RevokeInvite
                Busy = msgs.RevokeInvite
                Cancel = msgs.Cancel
            }
            [
                Html.p [
                    prop.className "text-sm text-muted"
                    prop.text (msgs.RevokeInvitePrompt state.Email (teamNameOf model state.TeamId))
                ]
            ]
            (fun () -> dispatch RevokeByEmail.Confirm)
            (fun () -> dispatch RevokeByEmail.Cancel)
            state.Submitting

    let private pendingInvitesView (msgs: TeamManagerMessages) (teamId: string) (model: Model) (dispatch: Msg -> unit) =
        let entries = model.PendingByEmail |> Map.tryFind teamId
        let callerRole = effectiveCallerRole model teamId

        Html.div [
            prop.className "space-y-4"
            prop.children [
                Html.div [
                    prop.className "flex items-center gap-3"
                    prop.children [
                        Html.button [
                            prop.className "text-sm text-brand hover:underline"
                            prop.text msgs.BreadcrumbMembers
                            prop.onClick (fun _ -> dispatch (SelectTeam teamId))
                        ]
                        Html.span [ prop.className "text-muted"; prop.text "/" ]
                        Html.span [ prop.className "font-medium"; prop.text (teamNameOf model teamId) ]
                        Html.span [ prop.className "text-muted"; prop.text "/" ]
                        Html.span [ prop.className "font-medium"; prop.text msgs.PendingInvites ]
                    ]
                ]

                Tabs.bar [ EmailInvites, msgs.PendingInvitesPanel ] EmailInvites ignore

                Layout.Panel.panel msgs.PendingInvitesPanel [
                    Html.div [
                        prop.className "flex justify-end mb-3"
                        prop.children [
                            Forms.Button.primary msgs.InviteByEmail (fun () -> dispatch (OpenIssueByEmail teamId))
                        ]
                    ]
                    match entries with
                    | None ->
                        Html.p [
                            prop.className "text-sm text-muted py-2"
                            prop.text msgs.PendingInvitesLoading
                        ]
                    | Some [] -> Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.NoPendingInvites ]
                    | Some rows ->
                        Html.div [
                            for email, entry in rows do
                                pendingEmailRow msgs teamId email entry dispatch
                        ]

                    // Recently-expired invites (30-day window, read from the
                    // audit trail). Rendered only when there is something to
                    // show — an empty history is not worth a heading.
                    match model.ExpiredInvites |> Map.tryFind teamId with
                    | Some expired when not (List.isEmpty expired) ->
                        Html.div [
                            prop.className "mt-4"
                            prop.children [
                                Html.h4 [
                                    prop.className "text-sm font-medium text-muted mb-2"
                                    prop.text msgs.RecentlyExpired
                                ]
                                Html.div [
                                    for entry in expired do
                                        let inFlight =
                                            model.ReissueInFlight |> Set.contains (teamId, entry.InviteeEmail)

                                        expiredInviteRow msgs teamId entry inFlight dispatch
                                ]
                            ]
                        ]
                    | _ -> Html.none
                ]

                match model.IssueByEmail with
                | Some state when state.TeamId = teamId ->
                    issueByEmailView msgs callerRole state (IssueByEmailMsg >> dispatch)
                | _ -> Html.none

                match model.RevokeByEmail with
                | Some state when state.TeamId = teamId ->
                    revokeByEmailView msgs state model (RevokeByEmailMsg >> dispatch)
                | _ -> Html.none
            ]
        ]

    /// The page, over the catalog section the view hands it.
    let body (msgs: TeamManagerMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let body =
            match model.CurrentView with
            | MyTeams -> myTeamsView msgs model dispatch
            | TeamDetails teamId -> teamDetailsView msgs teamId model dispatch
            | PendingInvites teamId -> pendingInvitesView msgs teamId model dispatch

        // Settings-shape FullWidth render: the error notice stacks above the
        // body, immediately above the user's current context.
        Html.div [
            prop.className "flex flex-col gap-3"
            prop.children [
                StateViews.notice { Dismiss = msgs.Dismiss } model.Error (fun () -> dispatch DismissError)
                body
            ]
        ]

/// The module-visibility editor's page as Feliz (`ModuleVisibilityAdminUI`'s
/// view before its tree), its note held by the component's own hook.
module ModuleVisibilityAdmin =
    open ModuleVisibilityAdminUI

    let private kindButton (label: string) (description: string) (active: bool) (onClick: unit -> unit) =
        Html.button [
            prop.className [
                "flex-1 text-left px-3 py-2 rounded-lg border transition-colors"
                if active then
                    "border-brand bg-brand/5"
                else
                    "border-border bg-white hover:border-brand"
            ]
            prop.onClick (fun _ -> onClick ())
            prop.children [
                Html.div [ prop.className "text-sm font-semibold"; prop.text label ]
                Html.div [ prop.className ("text-xs " + Tokens.Text.muted); prop.text description ]
            ]
        ]

    /// One row of the candidate list: the module id, its membership of the
    /// edited rule, and — once the resolution has landed — whether the
    /// server currently admits it.
    let private moduleRow
        (msgs: ModuleVisibilityAdminMessages)
        (resolution: ModuleVisibilityResolution option)
        (selected: bool)
        (moduleId: string)
        (onToggle: unit -> unit)
        =
        let admittedBadge =
            match resolution with
            | None -> Html.none
            | Some r ->
                if ModuleVisibility.admitsModule r moduleId then
                    Html.span [
                        prop.className "text-xs px-2 py-0.5 rounded bg-green-100 text-green-700"
                        prop.text msgs.VisibleNow
                    ]
                else
                    Html.span [
                        prop.className ("text-xs px-2 py-0.5 rounded bg-gray-200 " + Tokens.Text.secondary)
                        prop.text msgs.HiddenNow
                    ]

        Html.label [
            prop.className "flex items-center gap-3 px-3 py-2 border-t border-border cursor-pointer hover:bg-gray-50"
            prop.children [
                Html.input [
                    prop.type' "checkbox"
                    prop.isChecked selected
                    prop.onChange (fun (_: bool) -> onToggle ())
                ]
                Html.span [ prop.className "font-mono text-xs flex-1 break-all"; prop.text moduleId ]
                admittedBadge
            ]
        ]

    /// The resolved answer, rendered beside the editable profile. See the
    /// header note — this is what makes "I allowed it and it still does not
    /// appear" answerable without leaving the page.
    let private resolvedPane (msgs: ModuleVisibilityAdminMessages) (model: Model) =
        let body =
            match model.Resolved with
            | None when not model.ResolvedLoaded ->
                Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
            | None ->
                Html.p [
                    prop.className ("text-sm " + Tokens.Text.secondary)
                    prop.text msgs.NoResolutionYet
                ]
            | Some r ->
                let list (label: int -> string) (items: string list) =
                    Html.div [
                        prop.className "mb-3"
                        prop.children [
                            Html.div [
                                prop.className ("text-xs font-medium mb-1 " + Tokens.Text.secondary)
                                prop.text (label (List.length items))
                            ]
                            if List.isEmpty items then
                                Html.p [ prop.className ("text-xs " + Tokens.Text.muted); prop.text msgs.NoItems ]
                            else
                                Html.div [
                                    prop.className "flex flex-wrap gap-1"
                                    prop.children [
                                        for item in items ->
                                            Html.span [
                                                prop.className (
                                                    "font-mono text-xs px-2 py-0.5 rounded bg-gray-100 break-all "
                                                    + Tokens.Text.secondary
                                                )
                                                prop.text item
                                            ]
                                    ]
                                ]
                        ]
                    ]

                Html.div [
                    prop.children [
                        list msgs.GovernedModules r.GovernedModuleIds
                        list msgs.SelectedAfterEveryLayer r.SelectedModuleIds
                        list msgs.ExcludedEntries r.ExcludedEntryIds
                        list msgs.ContributingScopes (r.ContributingScopes |> List.map FlagScope.slug)
                    ]
                ]

        Html.div [
            prop.className "bg-white rounded-lg border border-border p-4"
            prop.children [
                Html.h3 [
                    prop.className "text-sm font-semibold mb-1"
                    prop.text msgs.ResolvedForYouHeading
                ]
                Html.p [
                    prop.className ("text-xs mb-3 " + Tokens.Text.muted)
                    prop.text msgs.ResolvedForYouHelp
                ]
                body
            ]
        ]

    /// The note field plus the two mutating actions. The note is local React
    /// state, committed on Enter / blur, so free-typing does not round-trip
    /// through Elmish; `onSave` carries its value at submit time. Seeded once from
    /// `model.Note` on mount — a reload after a save must not overwrite what
    /// the operator is mid-way through typing.
    [<ReactComponent>]
    let private EditorForm (model: Model) (onSave: string -> unit) (onClear: unit -> unit) =
        let msgs = (MessageCatalogProvider.useMessages ()).ModuleVisibilityAdmin
        let note, setNote = React.useState model.Note

        Html.div [
            prop.children [
                Html.label [
                    prop.className ("block text-xs font-medium mb-1 " + Tokens.Text.secondary)
                    prop.text msgs.NoteLabel
                ]
                Html.div [
                    prop.className "mb-4"
                    prop.children [ Forms.Input.text note setNote msgs.NotePlaceholder ]
                ]

                Html.div [
                    prop.className "flex gap-2"
                    prop.children [
                        Forms.Button.primaryWith
                            (Forms.Button.stateOf model.Busy msgs.Working false)
                            msgs.SaveProfile
                            (fun () -> onSave note)
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf false msgs.Working (model.Busy || model.StoredProfile.IsNone))
                            msgs.ClearProfile
                            onClear
                    ]
                ]
            ]
        ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: ModuleVisibilityAdminMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let noticeMsgs = { Dismiss = msgs.Dismiss }

        let candidateRows =
            if List.isEmpty model.RegisteredModuleIds then
                [
                    Html.p [
                        prop.className ("px-3 py-3 text-xs " + Tokens.Text.muted)
                        prop.text msgs.NoCuratableModules
                    ]
                ]
            else
                [
                    for moduleId in model.RegisteredModuleIds ->
                        moduleRow msgs model.Resolved (List.contains moduleId model.Selection) moduleId (fun () ->
                            dispatch (ToggleModule moduleId))
                ]

        let candidateHeader =
            Html.div [
                prop.className ("px-3 py-2 bg-gray-50 text-xs font-medium " + Tokens.Text.secondary)
                prop.text (msgs.RegisteredModulesHeader model.RegisteredModuleIds.Length model.Selection.Length)
            ]

        let editor =
            if not model.Loaded then
                Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
            else
                Html.div [
                    prop.className "bg-white rounded-lg border border-border p-4 mb-4"
                    prop.children [
                        Html.h3 [
                            prop.className "text-sm font-semibold mb-1"
                            prop.text msgs.YourProfileHeading
                        ]
                        Html.p [
                            prop.className ("text-xs mb-3 " + Tokens.Text.muted)
                            prop.text msgs.YourProfileHelp
                        ]

                        Html.div [
                            prop.className "flex gap-2 mb-4"
                            prop.children [
                                kindButton
                                    msgs.AllowLabel
                                    msgs.AllowDescription
                                    (model.Kind = RuleKind.Allow)
                                    (fun () -> dispatch (SetKind RuleKind.Allow))
                                kindButton msgs.DenyLabel msgs.DenyDescription (model.Kind = RuleKind.Deny) (fun () ->
                                    dispatch (SetKind RuleKind.Deny))
                            ]
                        ]

                        Html.div [
                            prop.className "border border-border rounded-lg overflow-hidden mb-4"
                            prop.children (candidateHeader :: candidateRows)
                        ]

                        EditorForm model (fun note -> dispatch (Save note)) (fun () -> dispatch Clear)
                    ]
                ]

        Html.div [
            prop.className "p-6 max-w-3xl"
            prop.children [
                Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
                Html.p [
                    prop.className ("text-sm mb-4 " + Tokens.Text.secondary)
                    prop.text msgs.Subheading
                ]
                StateViews.notice noticeMsgs model.Error (fun () -> dispatch DismissError)
                StateViews.notice noticeMsgs model.Status (fun () -> dispatch DismissStatus)
                editor
                resolvedPane msgs model
            ]
        ]

/// Platform Management as Feliz (`PlatformAdminUI`'s view, and its two
/// confirmations' views, before its tree).
module PlatformAdmin =
    open ToolUp.Platform.PlatformAdmin
    open PlatformAdminUI

    let assignConfirm
        (msgs: PlatformAdminMessages)
        (directory: UserDirectory.Model)
        (model: AssignConfirm.Model)
        (dispatch: AssignConfirm.Msg -> unit)
        =
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
            (fun () -> dispatch AssignConfirm.Confirm)
            (fun () -> dispatch AssignConfirm.Cancel)
            model.InFlight

    let deleteTeamConfirm
        (msgs: PlatformAdminMessages)
        (model: ConfirmDeleteTeam.Model)
        (dispatch: ConfirmDeleteTeam.Msg -> unit)
        =
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
            (fun () -> dispatch ConfirmDeleteTeam.Confirm)
            (fun () -> dispatch ConfirmDeleteTeam.Cancel)
            model.InFlight

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
                        Forms.Button.dangerWith Forms.Button.Enabled msgs.Revoke (fun () ->
                            dispatch (RequestRevoke userId))
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
    let private teamActionsCell
        (msgs: PlatformAdminMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        (team: TeamSummary)
        =
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

    // The one change from the page as it shipped: the caller arrives as a
    // value, as the tree takes it, where the page read the session.
    let private teamsTabView (msgs: PlatformAdminMessages) (self: Caller) (model: Model) (dispatch: Msg -> unit) =
        let selfId = self.UserId
        let selfDisplay = self.Display

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

    /// The page, over the catalog sections the view hands it and the deployment's
    /// `PublicUtilityConfig`, which the public-utility widgets read.
    let internal body
        (config: PublicUtilityConfig)
        (self: Caller)
        (msgs: PlatformAdminMessages)
        (widgets: PublicUtilityWidgetsMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        : ReactElement =
        let content =
            match model.ActiveTab with
            | AdminsTab -> adminsTabView msgs model dispatch
            | TeamsTab -> teamsTabView msgs self model dispatch
            | SettingsTab -> settingsTabView msgs model dispatch

        let publicUtilitySection = PublicUtilityWidgets.render widgets config

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
                | Some confirm -> assignConfirm msgs model.Directory confirm (AssignConfirmMsg >> dispatch)
                | None -> Html.none
                match model.ConfirmDeleteTeam with
                | Some confirm -> deleteTeamConfirm msgs confirm (DeleteTeamMsg >> dispatch)
                | None -> Html.none
            ]
        ]

/// The session-security page as Feliz (`SessionSecurityUI`'s view before its tree).
module SessionSecurity =
    open SessionSecurityUI

    let private sessionRow
        (msgs: SessionSecurityMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        (now: DateTimeOffset)
        (record: SessionRecord)
        =
        let active = SessionRecord.isActive record

        let pending = model.PendingRevoke = Some record.SessionId

        Html.div [
            prop.key record.SessionId
            prop.className "flex items-start justify-between gap-4 px-4 py-3 border-b border-border last:border-b-0"
            prop.children [
                Html.div [
                    prop.className "min-w-0"
                    prop.children [
                        Html.p [
                            prop.className (
                                if active then
                                    "text-sm font-medium truncate"
                                else
                                    ("text-sm font-medium truncate line-through " + Tokens.Text.muted)
                            )
                            prop.text record.DeviceDescriptor
                        ]
                        Html.p [
                            prop.className [ "text-xs mt-0.5"; Tokens.Text.muted ]
                            prop.text (
                                if active then
                                    msgs.DeviceLastSeen
                                        record.AuthProvider
                                        (lastSeenLabelWith msgs now record.LastSeenAt)
                                else
                                    msgs.DeviceSignedOut record.AuthProvider
                            )
                        ]
                    ]
                ]
                Html.div [
                    prop.className "shrink-0"
                    prop.children [
                        if not active then
                            Html.span [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.Revoked ]
                        elif pending then
                            Html.div [
                                prop.className "flex gap-2"
                                prop.children [
                                    Forms.Button.dangerWith
                                        (Forms.Button.stateOf model.Busy msgs.Confirm false)
                                        msgs.Confirm
                                        (fun () -> dispatch (ConfirmRevoke record.SessionId))
                                    Forms.Button.secondaryWith Forms.Button.Enabled msgs.Cancel (fun () ->
                                        dispatch CancelRevoke)
                                ]
                            ]
                        else
                            Forms.Button.secondaryWith
                                (Forms.Button.stateOf false msgs.SignOut model.Busy)
                                msgs.SignOut
                                (fun () -> dispatch (AskRevoke record.SessionId))
                    ]
                ]
            ]
        ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: SessionSecurityMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let now = DateTimeOffset.UtcNow

        let noticeMsgs = { Dismiss = msgs.Dismiss }

        let body =
            if not model.Loaded then
                Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.Loading ]
            elif List.isEmpty model.Sessions then
                Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.EmptyState ]
            else
                Html.div [
                    prop.className "border border-border rounded-lg overflow-hidden bg-white"
                    prop.children (model.Sessions |> List.map (sessionRow msgs model dispatch now))
                ]

        let signOutEverywhere =
            if List.isEmpty (model.Sessions |> List.filter SessionRecord.isActive) then
                Html.none
            elif model.PendingRevokeAll then
                Html.div [
                    prop.className "mt-4 px-3 py-3 rounded border border-red-200 bg-red-50"
                    prop.children [
                        Html.p [
                            prop.className "text-sm text-red-800 mb-2"
                            prop.text msgs.SignOutEverywhereWarning
                        ]
                        Html.div [
                            prop.className "flex gap-2"
                            prop.children [
                                Forms.Button.dangerWith
                                    (Forms.Button.stateOf model.Busy msgs.SignOutEverywhere false)
                                    msgs.SignOutEverywhere
                                    (fun () -> dispatch ConfirmRevokeAll)
                                Forms.Button.secondaryWith Forms.Button.Enabled msgs.Cancel (fun () ->
                                    dispatch CancelRevokeAll)
                            ]
                        ]
                    ]
                ]
            else
                Html.div [
                    prop.className "mt-4"
                    prop.children [
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf false msgs.SignOutEverywhere model.Busy)
                            msgs.SignOutEverywhere
                            (fun () -> dispatch AskRevokeAll)
                    ]
                ]

        Html.div [
            prop.className "p-6 max-w-3xl"
            prop.children [
                Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
                Html.p [
                    prop.className [ "text-sm mb-4"; Tokens.Text.secondary ]
                    prop.text msgs.Subheading
                ]
                StateViews.notice noticeMsgs model.Error (fun () -> dispatch DismissError)
                StateViews.notice noticeMsgs model.Status (fun () -> dispatch DismissStatus)
                body
                signOutEverywhere
            ]
        ]

/// The permissions admin as Feliz (`PermissionsAdminUI`'s view before its tree).
module PermissionsAdmin =
    open PermissionsAdminUI

    let private tabBar (msgs: PermissionsAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        Html.div [
            prop.className "bg-white px-4"
            prop.children [
                Tabs.bar
                    [
                        TeamDefaultsTab, msgs.TabTeamDefaults
                        MembersTab, msgs.TabMembers
                        ModulesTab, msgs.TabModules
                    ]
                    model.ActiveTab
                    (SwitchTab >> dispatch)
            ]
        ]

    let private permCheckbox (label: string) (granted: bool) (disabled: bool) (onToggle: unit -> unit) =
        Html.label [
            prop.className "inline-flex items-center gap-1 text-xs select-none"
            prop.children [
                Html.input [
                    prop.type'.checkbox
                    prop.isChecked granted
                    prop.disabled disabled
                    prop.onChange (fun (_: bool) -> onToggle ())
                    prop.className "rounded border-gray-300 text-brand focus:ring-brand"
                ]
                Html.span [ prop.text label ]
            ]
        ]

    /// The column "Select all" / "Clear all" toggles of a permission matrix.
    /// `allOn` is computed by the caller (it needs the overlay/snapshot state
    /// to decide); `onToggle` receives `(level, add)` and either grants or
    /// clears the level across every row.
    type private PermColumnAction = {
        allOn: ModulePermission -> bool
        onToggle: ModulePermission * bool -> unit
    }

    /// A row of a permission matrix: the column-toggle row heading the body,
    /// then one row per module with its granted levels.
    type private PermRow =
        | ColumnToggles
        | ModuleRow of moduleName: string * perms: ModulePermission list

    /// The module × level permission matrix: a checkbox per cell, disabled
    /// while a save is in flight, with a column-toggle row first.
    let private permTable
        (msgs: PermissionsAdminMessages)
        (header: string)
        (rows: (string * ModulePermission list) list)
        (disabled: bool)
        (onToggle: string -> ModulePermission -> unit)
        (columnAction: PermColumnAction)
        =
        if List.isEmpty rows then
            Html.p [
                prop.className [ "text-sm italic"; Tokens.Text.muted ]
                prop.text msgs.NoManagedModules
            ]
        else
            let levelColumn (level: ModulePermission) : Data.Column<PermRow> = {
                Header = permLabel msgs level
                Align = Data.Left
                Cell =
                    function
                    | ColumnToggles ->
                        let on = columnAction.allOn level

                        Html.button [
                            prop.className "text-[10px] text-brand hover:underline"
                            prop.text (if on then msgs.ClearAll else msgs.SelectAll)
                            prop.onClick (fun _ -> columnAction.onToggle (level, not on))
                        ]
                    | ModuleRow(moduleName, perms) ->
                        permCheckbox (permLabel msgs level) (List.contains level perms) disabled (fun () ->
                            onToggle moduleName level)
            }

            let moduleColumn: Data.Column<PermRow> = {
                Header = header
                Align = Data.Left
                Cell =
                    function
                    | ColumnToggles -> Html.none
                    | ModuleRow(moduleName, _) -> Html.span [ prop.className "font-medium"; prop.text moduleName ]
            }

            Data.table (moduleColumn :: List.map levelColumn allPermLevels) (ColumnToggles :: List.map ModuleRow rows)

    // ─── Team defaults tab ───────────────────────────────────────────────

    /// The Reset + Save pair of an edit overlay: both inert until there is
    /// something to commit, Save showing its busy label while a save is in
    /// flight.
    let private editActions
        (msgs: PermissionsAdminMessages)
        (model: Model)
        (dirty: bool)
        (saveLabel: string)
        (onReset: unit -> unit)
        (onSave: unit -> unit)
        =
        Html.div [
            prop.className "flex items-center gap-2 shrink-0"
            prop.children [
                Forms.Button.secondaryWith
                    (if dirty && not model.SaveInFlight then
                         Forms.Button.Enabled
                     else
                         Forms.Button.Disabled)
                    msgs.Reset
                    onReset
                Forms.Button.primaryWith
                    (Forms.Button.stateOf model.SaveInFlight msgs.Saving (not dirty))
                    saveLabel
                    onSave
            ]
        ]

    let private teamDefaultsView
        (msgs: PermissionsAdminMessages)
        (model: Model)
        (snap: TeamView)
        (dispatch: Msg -> unit)
        =
        let edits = model.EditingDefaults |> Option.defaultValue snap.Permissions.Defaults
        let dirty = edits <> snap.Permissions.Defaults

        let permsOf (m: string) =
            Map.tryFind m edits |> Option.defaultValue []

        // The column toggles read the local `edits` overlay so they reflect
        // the operator's in-progress changes rather than the persisted snapshot.
        let columnAction = {
            allOn = fun level -> snap.Managed |> List.forall (fun m -> permsOf m |> List.contains level)
            onToggle =
                fun (level, add) ->
                    for m in snap.Managed do
                        if add <> List.contains level (permsOf m) then
                            dispatch (ToggleDefault(m, level))
        }

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.TeamDefaultsHeading ]
                                Html.p [
                                    prop.className [ "text-xs"; Tokens.Text.muted ]
                                    prop.text msgs.TeamDefaultsSubheading
                                ]
                            ]
                        ]
                        editActions
                            msgs
                            model
                            dirty
                            msgs.SaveDefaultsLabel
                            (fun () -> dispatch ResetDefaults)
                            (fun () -> dispatch SaveDefaults)
                    ]
                ]
                permTable
                    msgs
                    msgs.ColumnModule
                    (snap.Managed |> List.map (fun m -> m, permsOf m))
                    model.SaveInFlight
                    (fun m level -> dispatch (ToggleDefault(m, level)))
                    columnAction
            ]
        ]

    // ─── Members tab ─────────────────────────────────────────────────────

    /// A member's display label: for the signed-in user, the display name
    /// over the raw UserId; for other members the UserId. The page read the
    /// caller from the session store; the reference takes it, as the tree does.
    let private memberLabel (self: Caller) (userId: string) =
        if self.UserId = userId then
            self.DisplayName |> Option.defaultValue userId
        else
            userId

    let private memberListItem
        (msgs: PermissionsAdminMessages)
        (self: Caller)
        (model: Model)
        (snap: TeamView)
        (m: TeamMembership)
        (dispatch: Msg -> unit)
        =
        let selected = model.SelectedMember = Some m.UserId
        let hasOver = hasOverride snap.Permissions m.UserId

        let emailSubtitle = if self.UserId = m.UserId then self.Email else None

        Html.button [
            prop.className [
                "w-full text-left px-3 py-2 border-l-2 transition-colors"
                if selected then
                    "border-brand bg-blue-50"
                else
                    "border-transparent hover:bg-gray-50"
            ]
            prop.onClick (fun _ -> dispatch (SelectMember m.UserId))
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between"
                    prop.children [
                        Html.span [ prop.className "font-medium text-sm"; prop.text (memberLabel self m.UserId) ]
                        if hasOver then
                            Html.span [
                                prop.className
                                    "inline-block text-[10px] px-1.5 py-0.5 rounded border bg-purple-100 border-purple-200 text-purple-700"
                                prop.text msgs.OverrideBadge
                            ]
                    ]
                ]
                match emailSubtitle with
                | Some email -> Html.div [ prop.className [ "text-xs truncate"; Tokens.Text.muted ]; prop.text email ]
                | None -> ()
                Html.div [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text (string m.Role) ]
            ]
        ]

    let private memberOverrideEditor
        (msgs: PermissionsAdminMessages)
        (self: Caller)
        (model: Model)
        (snap: TeamView)
        (userId: string)
        (dispatch: Msg -> unit)
        =
        let effectivePerms = effectiveMemberPerms snap model.EditingMembers userId

        let overrideKeys =
            match Map.tryFind userId snap.Permissions.Members with
            | Some modMap -> Map.toList modMap |> List.map fst |> List.sort
            | None -> []

        let dirty = model.EditingMembers |> Map.exists (fun (uid, _) _ -> uid = userId)

        let columnAction = {
            allOn = fun level -> snap.Managed |> List.forall (fun m -> effectivePerms m |> List.contains level)
            onToggle = fun (level, add) -> dispatch (ToggleMemberColumn(userId, level, add))
        }

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "mb-4 flex items-start justify-between gap-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [
                                    prop.className "text-lg font-semibold"
                                    prop.text (msgs.OverridesHeading(memberLabel self userId))
                                ]
                                Html.p [
                                    prop.className [ "text-xs"; Tokens.Text.muted ]
                                    prop.text msgs.OverridesHelp
                                ]
                                if not (List.isEmpty overrideKeys) then
                                    Html.p [
                                        prop.className [ "text-xs mt-1"; Tokens.Text.muted ]
                                        prop.text (msgs.ActiveOverridesOn(String.concat ", " overrideKeys))
                                    ]
                            ]
                        ]
                        // Edits stay local until "Update" is pressed; no
                        // round-trip per checkbox click.
                        editActions msgs model dirty msgs.Update (fun () -> dispatch (ResetMembers userId)) (fun () ->
                            dispatch (SaveMembers userId))
                    ]
                ]
                permTable
                    msgs
                    msgs.ColumnModule
                    (snap.Managed |> List.map (fun m -> m, effectivePerms m))
                    model.SaveInFlight
                    (fun m level -> dispatch (ToggleMemberPermission(userId, m, level)))
                    columnAction
            ]
        ]

    let private membersView
        (msgs: PermissionsAdminMessages)
        (self: Caller)
        (model: Model)
        (snap: TeamView)
        (dispatch: Msg -> unit)
        =
        if List.isEmpty snap.Members then
            Html.div [
                prop.className "flex-1 p-6 overflow-y-auto"
                prop.children [
                    Html.p [
                        prop.className ("text-sm italic " + Tokens.Text.muted)
                        prop.text msgs.NoMembersYet
                    ]
                ]
            ]
        else
            Html.div [
                prop.className "flex-1 flex overflow-hidden"
                prop.children [
                    Html.aside [
                        prop.className "w-72 border-r border-border bg-white overflow-y-auto"
                        prop.children [
                            Html.div [
                                prop.className ("px-3 py-2 text-xs font-medium uppercase " + Tokens.Text.muted)
                                prop.text msgs.MembersListLabel
                            ]
                            for m in snap.Members do
                                memberListItem msgs self model snap m dispatch
                        ]
                    ]
                    Html.div [
                        prop.className "flex-1 overflow-y-auto"
                        prop.children [
                            match model.SelectedMember with
                            | None ->
                                Html.div [
                                    prop.className ("p-6 text-sm italic " + Tokens.Text.muted)
                                    prop.text msgs.SelectMemberPrompt
                                ]
                            | Some uid -> memberOverrideEditor msgs self model snap uid dispatch
                        ]
                    ]
                ]
            ]

    // ─── Modules tab ─────────────────────────────────────────────────────

    let private renderPerms (msgs: PermissionsAdminMessages) (ps: ModulePermission list) =
        if List.isEmpty ps then
            Html.span [
                prop.className [ "text-xs"; Tokens.Text.muted ]
                prop.text msgs.NoDefaultPermission
            ]
        else
            Html.span [
                prop.className "text-xs"
                // The middot separator is UI furniture joining already-
                // localized labels, not translatable content itself.
                prop.text (ps |> List.map (permLabel msgs) |> String.concat " · ")
            ]

    /// The exposure selector — a 3-way segmented control, visibility/
    /// availability only, orthogonal to the permission level.
    ///   Available   — in the sidebar + Home, data mappable.
    ///   Hidden      — off the sidebar + Home, data still mappable.
    ///   Unavailable — off the sidebar + Home, AND data mapping blocked.
    let private exposureSelector
        (msgs: PermissionsAdminMessages)
        (snap: TeamView)
        (dispatch: Msg -> unit)
        (moduleName: string)
        =
        let current = exposureOf snap.Permissions moduleName

        let exposureOption (state: ModuleExposure) (label: string) (activeCls: string) =
            let isActive = current = state

            Html.button [
                prop.className [
                    "px-2 py-1 text-xs font-medium border"
                    if isActive then
                        activeCls
                    else
                        "border-border bg-white hover:bg-gray-50 " + Tokens.Text.muted
                ]
                prop.text label
                prop.disabled isActive
                prop.onClick (fun _ ->
                    if not isActive then
                        dispatch (SetExposure(moduleName, current, state)))
            ]

        Html.div [
            prop.className "inline-flex rounded overflow-hidden"
            prop.children [
                exposureOption
                    ModuleExposure.Available
                    msgs.ExposureAvailable
                    "border-green-200 bg-green-50 text-green-700"
                exposureOption
                    ModuleExposure.Hidden
                    msgs.ExposureHidden
                    ("border-gray-300 bg-gray-100 " + Tokens.Text.secondary)
                exposureOption
                    ModuleExposure.Unavailable
                    msgs.ExposureUnavailable
                    "border-red-200 bg-red-50 text-red-700"
            ]
        ]

    let private modulesView (msgs: PermissionsAdminMessages) (snap: TeamView) (dispatch: Msg -> unit) =
        let column header align cell : Data.Column<string> = {
            Header = header
            Align = align
            Cell = cell
        }

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "mb-4"
                    prop.children [
                        Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.ModulesHeading ]
                        Html.p [
                            prop.className [ "text-xs"; Tokens.Text.muted ]
                            prop.text msgs.ModulesSubheading
                        ]
                    ]
                ]
                if List.isEmpty snap.Managed then
                    Html.p [
                        prop.className [ "text-sm italic"; Tokens.Text.muted ]
                        prop.text msgs.NoManagedModules
                    ]
                else
                    Data.table
                        [
                            column msgs.ColumnModule Data.Left (fun moduleName ->
                                Html.span [ prop.className "font-medium"; prop.text moduleName ])
                            column msgs.ColumnExposure Data.Left (exposureSelector msgs snap dispatch)
                            column msgs.ColumnTeamDefault Data.Left (fun moduleName ->
                                Map.tryFind moduleName snap.Permissions.Defaults
                                |> Option.defaultValue []
                                |> renderPerms msgs)
                            column msgs.ColumnOverrides Data.Right (fun moduleName ->
                                Html.span [
                                    prop.className [ "text-xs font-mono"; Tokens.Text.secondary ]
                                    prop.text (string (memberOverrideCount snap.Permissions moduleName))
                                ])
                        ]
                        snap.Managed
            ]
        ]

    // ─── View ────────────────────────────────────────────────────────────

    let private errorPane (msgs: PermissionsAdminMessages) (msg: string) (dispatch: Msg -> unit) =
        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                StateViews.noticeStatic (Notice.Problem msg)
                Html.button [
                    prop.className (
                        "mt-3 px-3 py-1.5 text-sm font-medium bg-white border border-border rounded hover:bg-gray-50 "
                        + Tokens.Text.secondary
                    )
                    prop.text msgs.Retry
                    prop.onClick (fun _ -> dispatch RefreshSnapshot)
                ]
            ]
        ]

    /// The module's page, over the catalog section the view hands it.
    let internal body
        (msgs: PermissionsAdminMessages)
        (self: Caller)
        (model: Model)
        (dispatch: Msg -> unit)
        : ReactElement =
        let content =
            match model.Snapshot with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ ->
                Html.div [
                    prop.className "flex-1 p-6 overflow-y-auto"
                    prop.children [
                        Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
                    ]
                ]
            | Remote.Failed msg -> errorPane msgs msg dispatch
            | Remote.Loaded snap ->
                match model.ActiveTab with
                | TeamDefaultsTab -> teamDefaultsView msgs model snap dispatch
                | MembersTab -> membersView msgs self model snap dispatch
                | ModulesTab -> modulesView msgs snap dispatch

        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [
                tabBar msgs model dispatch
                if model.Banner.IsSome then
                    Html.div [
                        prop.className "mx-6 mt-4"
                        prop.children [
                            StateViews.notice { Dismiss = msgs.Dismiss } model.Banner (fun () -> dispatch DismissBanner)
                        ]
                    ]
                content
            ]
        ]

/// The platform-users admin as Feliz (`PlatformUsersUI`'s view before its tree).
module PlatformUsers =
    open PlatformUsersUI

    /// Membership summary — "3 teams · Admin, Member" — or a team-less badge.
    let private membershipLabel (msgs: PlatformUsersMessages) (p: PrincipalSummary) : string =
        if p.TeamLess then
            msgs.NoTeams
        else
            let roles =
                p.Memberships
                |> List.map (fun (_, role) -> TeamRoles.displayName role)
                |> List.distinct
                |> String.concat ", "

            msgs.MembershipSummary (List.length p.Memberships) roles

    let private lastSeenLabel (p: PrincipalSummary) : string =
        match p.LastSeenAt with
        | Some at -> at.ToString "yyyy-MM-dd"
        | None -> "—"

    let private badge (cls: string) (label: string) =
        Html.span [
            prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
            prop.text label
        ]

    let private previewResultBadge (msgs: PlatformUsersMessages) (item: LifecyclePreviewItem) =
        if not item.HasPreview then
            badge ("bg-gray-100 " + Tokens.Text.muted) msgs.NoPreviewBadge
        elif item.WouldAffect = 0 then
            badge "bg-green-50 text-green-700" "0"
        else
            badge "bg-amber-50 text-amber-800" (string item.WouldAffect)

    let private summaryResultBadge (msgs: PlatformUsersMessages) (result: LifecycleHookResult) =
        match result with
        | LifecycleHookResult.Completed -> badge "bg-green-100 text-green-700" msgs.OutcomeCompleted
        | LifecycleHookResult.Skipped _ -> badge "bg-yellow-100 text-yellow-800" msgs.OutcomeSkipped
        | LifecycleHookResult.Failed _ -> badge "bg-red-100 text-red-700" msgs.OutcomeFailed

    let private summaryResultDetail (result: LifecycleHookResult) =
        match result with
        | LifecycleHookResult.Completed -> ""
        | LifecycleHookResult.Skipped reason -> reason
        | LifecycleHookResult.Failed error -> error

    // ─── Modal sub-views ─────────────────────────────────────────────────

    let private kindTitle (msgs: PlatformUsersMessages) =
        function
        | PlainOffboard -> msgs.OffboardTitle
        | ExportOffboard -> msgs.ExportOffboardTitle

    let private primaryActionLabel (msgs: PlatformUsersMessages) (state: OffboardModalState) =
        match state.Step, state.Kind with
        | Confirming, _ -> msgs.ConfirmOffboard
        | _, PlainOffboard -> msgs.OffboardAction
        | _, ExportOffboard -> msgs.ExportOffboardAction

    /// The destructive submit — busy (showing `Working`) while a call is in flight.
    let private primaryButton (msgs: PlatformUsersMessages) (state: OffboardModalState) (dispatch: Msg -> unit) =
        Forms.Button.dangerWith
            (Forms.Button.stateOf state.Busy msgs.Working false)
            (primaryActionLabel msgs state)
            (fun () -> dispatch SubmitOffboard)

    /// A quiet action that is inert while another call is in flight.
    let private secondaryAction (state: OffboardModalState) (label: string) (onClick: unit -> unit) =
        Forms.Button.secondaryWith
            (if state.Busy then
                 Forms.Button.Disabled
             else
                 Forms.Button.Enabled)
            label
            onClick

    let private fieldLabel (text: string) =
        Html.label [
            prop.className [ "block text-xs font-medium mb-1"; Tokens.Text.secondary ]
            prop.text text
        ]

    let private reasonField (msgs: PlatformUsersMessages) (state: OffboardModalState) (dispatch: Msg -> unit) =
        Html.div [
            prop.children [
                fieldLabel msgs.ReasonLabel
                Forms.Input.text state.Reason (SetOffboardReason >> dispatch) msgs.ReasonPlaceholder
            ]
        ]

    let private previewTable (msgs: PlatformUsersMessages) (preview: LifecyclePreview) =
        Data.table
            [
                {
                    Header = msgs.ColumnHook
                    Align = Data.Left
                    Cell =
                        fun (item: LifecyclePreviewItem) ->
                            Html.span [ prop.className "font-mono text-xs"; prop.text item.HookName ]
                }
                {
                    Header = msgs.ColumnWouldAffect
                    Align = Data.Left
                    Cell = previewResultBadge msgs
                }
                {
                    Header = msgs.ColumnDetail
                    Align = Data.Left
                    Cell =
                        fun item ->
                            Html.span [
                                prop.className [ "text-xs break-all"; Tokens.Text.secondary ]
                                prop.text item.Detail
                            ]
                }
            ]
            preview.Items

    let private summaryTable
        (msgs: PlatformUsersMessages)
        (summary: LifecycleSummary)
        (archive: LifecycleExportArchive option)
        =
        Html.div [
            prop.className "space-y-3"
            prop.children [
                Html.div [
                    prop.className "flex gap-2 flex-wrap"
                    prop.children [
                        badge
                            "bg-green-50 text-green-700"
                            (msgs.CompletedCount(LifecycleSummary.completedCount summary))
                        badge "bg-yellow-50 text-yellow-800" (msgs.SkippedCount(LifecycleSummary.skippedCount summary))
                        badge "bg-red-50 text-red-700" (msgs.FailedCount(LifecycleSummary.failedCount summary))
                        badge ("bg-gray-100 " + Tokens.Text.secondary) (sprintf "%d ms" (int summary.TotalElapsedMs))
                    ]
                ]

                match archive with
                | Some a ->
                    Html.div [
                        prop.className
                            "text-xs bg-blue-50 border border-blue-200 rounded-lg p-3 text-blue-800 break-all"
                        prop.children [
                            Html.p [
                                prop.className "font-medium mb-1"
                                prop.text (msgs.ExportArchiveWritten a.SegmentCount)
                            ]
                            Html.p [ prop.text (sprintf "%s/%s" a.Container a.BlobPath) ]
                            Html.p [
                                prop.className "font-mono text-blue-600 mt-1"
                                prop.text (sprintf "sha256: %s" a.ContentHash)
                            ]
                        ]
                    ]
                | None -> Html.none

                if List.isEmpty summary.Outcomes then
                    Html.p [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.NoHooksRan ]
                else
                    Data.table
                        [
                            {
                                Header = msgs.ColumnHook
                                Align = Data.Left
                                Cell =
                                    fun (outcome: LifecycleHookOutcome) ->
                                        Html.span [ prop.className "font-mono text-xs"; prop.text outcome.HookName ]
                            }
                            {
                                Header = msgs.ColumnResult
                                Align = Data.Left
                                Cell = fun outcome -> summaryResultBadge msgs outcome.Result
                            }
                            {
                                Header = msgs.ColumnDetail
                                Align = Data.Left
                                Cell =
                                    fun outcome ->
                                        Html.span [
                                            prop.className [ "text-xs break-all"; Tokens.Text.secondary ]
                                            prop.text (summaryResultDetail outcome.Result)
                                        ]
                            }
                        ]
                        summary.Outcomes
            ]
        ]

    let private offboardModalView
        (msgs: PlatformUsersMessages)
        (state: OffboardModalState)
        (label: string)
        (dispatch: Msg -> unit)
        =
        let heading =
            match state.Step with
            | Completed _ -> msgs.OffboardCompleteTitle
            | _ -> kindTitle msgs state.Kind

        let subject =
            Html.p [
                prop.className "text-sm text-muted"
                prop.children [
                    Html.span [ prop.text (msgs.SubjectLabel label) ]
                    Html.span [ prop.className "font-mono text-xs break-all"; prop.text state.ScopeId ]
                ]
            ]

        let modalError =
            state.Error
            |> Option.map StateViews.noticeStatic
            |> Option.defaultValue Html.none

        let cancel = Forms.Button.secondary msgs.Cancel (fun () -> dispatch CloseOffboard)

        match state.Step with
        | Compose ->
            Modal.overlay heading [ subject; reasonField msgs state dispatch; modalError ] [
                cancel
                secondaryAction state msgs.PreviewImpact (fun () -> dispatch RequestPreview)
                primaryButton msgs state dispatch
            ]

        | Previewed preview ->
            Modal.overlay heading [
                subject
                Html.p [
                    prop.className "text-sm text-muted"
                    prop.text (msgs.PreviewSummary preview.TotalWouldAffect)
                ]
                previewTable msgs preview
                reasonField msgs state dispatch
                modalError
            ] [ cancel; primaryButton msgs state dispatch ]

        | Confirming ->
            Modal.overlay heading [
                subject
                Html.div [
                    prop.className "text-sm bg-amber-50 border border-amber-200 rounded-lg p-3 text-amber-800"
                    prop.children [
                        Html.p [
                            prop.className "font-medium mb-1"
                            prop.text msgs.ConfirmationRequiredHeading
                        ]
                        Html.p [ prop.text msgs.ConfirmationRequiredBody ]
                        match state.Kind with
                        | ExportOffboard -> Html.p [ prop.className "mt-1"; prop.text msgs.ExportConfirmationNote ]
                        | PlainOffboard -> Html.none
                    ]
                ]
                Html.div [
                    prop.className "flex items-end gap-2"
                    prop.children [
                        Html.div [
                            prop.className "flex-1"
                            prop.children [
                                fieldLabel msgs.ConfirmationTokenLabel
                                Forms.Input.text
                                    state.Token
                                    (SetOffboardToken >> dispatch)
                                    msgs.ConfirmationTokenPlaceholder
                            ]
                        ]
                        secondaryAction state msgs.RequestToken (fun () -> dispatch RequestToken)
                    ]
                ]
                modalError
            ] [ cancel; primaryButton msgs state dispatch ]

        | Completed(summary, archive) ->
            Modal.overlay heading [ subject; summaryTable msgs summary archive ] [
                Forms.Button.primary msgs.Close (fun () -> dispatch CloseOffboard)
            ]

    // ─── List row ────────────────────────────────────────────────────────

    let private principalRow
        (msgs: PlatformUsersMessages)
        (directory: UserDirectory.Model)
        (p: PrincipalSummary)
        (dispatch: Msg -> unit)
        =
        let primaryLabel, secondaryLabel = UserDirectory.labels directory p.UserId

        Html.div [
            prop.className "flex items-center justify-between p-3 border border-border rounded-lg mb-2 gap-3 flex-wrap"
            prop.children [
                Html.div [
                    prop.className "flex flex-col min-w-0"
                    prop.children [
                        Html.div [
                            prop.className "flex items-center gap-2 flex-wrap"
                            prop.children [
                                Html.span [ prop.className "font-medium break-all"; prop.text primaryLabel ]
                                if p.TeamLess then
                                    badge "bg-orange-100 text-orange-700" msgs.TeamLessBadge
                                if p.HasUserScopeData then
                                    badge ("bg-gray-100 " + Tokens.Text.secondary) msgs.HasDataBadge
                            ]
                        ]
                        match secondaryLabel with
                        | Some email -> Html.span [ prop.className "text-xs text-muted break-all"; prop.text email ]
                        | None -> Html.none
                        Html.span [
                            prop.className "text-xs text-muted"
                            prop.text (msgs.RowSubtitle (membershipLabel msgs p) (lastSeenLabel p))
                        ]
                        Html.span [
                            prop.className [ "text-xs font-mono break-all"; Tokens.Text.muted ]
                            prop.text p.UserId
                        ]
                    ]
                ]
                Html.div [
                    prop.className "flex gap-2 flex-wrap"
                    prop.children [
                        Forms.Button.secondary msgs.PreviewAction (fun () ->
                            dispatch (OpenOffboard(p.UserId, PlainOffboard)))
                        Forms.Button.secondary msgs.ExportOffboardAction (fun () ->
                            dispatch (OpenOffboard(p.UserId, ExportOffboard)))
                        Html.button [
                            prop.className
                                "px-3 py-1.5 text-sm rounded-lg border border-red-200 text-red-700 hover:bg-red-50 transition-colors"
                            prop.text msgs.OffboardAction
                            prop.onClick (fun _ -> dispatch (OpenOffboard(p.UserId, PlainOffboard)))
                        ]
                    ]
                ]
            ]
        ]

    // ─── View ────────────────────────────────────────────────────────────

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: PlatformUsersMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let filterBar =
            Html.div [
                prop.className "flex items-center justify-between mb-4 flex-wrap gap-2"
                prop.children [
                    Html.label [
                        prop.className [ "flex items-center gap-2 text-sm cursor-pointer"; Tokens.Text.secondary ]
                        prop.children [
                            Html.input [
                                prop.type' "checkbox"
                                prop.isChecked model.TeamLessOnly
                                prop.onChange (fun (_: bool) -> dispatch ToggleTeamLessOnly)
                            ]
                            Html.span [ prop.text msgs.TeamLessOnly ]
                        ]
                    ]
                    Html.button [
                        prop.className "text-xs text-brand hover:underline"
                        prop.text msgs.Refresh
                        prop.onClick (fun _ -> dispatch LoadPrincipals)
                    ]
                ]
            ]

        let listPane =
            match model.Principals with
            | None ->
                Html.p [
                    prop.className [ "text-sm"; Tokens.Text.muted ]
                    prop.text msgs.LoadingPrincipals
                ]
            | Some principals ->
                let visible =
                    if model.TeamLessOnly then
                        principals |> List.filter _.TeamLess
                    else
                        principals

                if List.isEmpty principals then
                    // Empty / degraded — no principals, or the offboard
                    // substrate is disabled (the error banner carries the why).
                    Html.div [
                        prop.className "bg-white rounded-lg border border-border p-6 text-center"
                        prop.children [
                            Html.p [
                                prop.className [ "text-sm"; Tokens.Text.secondary ]
                                prop.text msgs.NoPrincipalsHeading
                            ]
                            Html.p [
                                prop.className [ "text-xs mt-1"; Tokens.Text.muted ]
                                prop.text msgs.NoPrincipalsBody
                            ]
                        ]
                    ]
                elif List.isEmpty visible then
                    Html.p [
                        prop.className [ "text-sm py-4"; Tokens.Text.muted ]
                        prop.text msgs.NoTeamLessPrincipals
                    ]
                else
                    Html.div [
                        prop.children [
                            for p in visible do
                                principalRow msgs model.Directory p dispatch
                        ]
                    ]

        let selfLabelForModal (state: OffboardModalState) =
            let primary, _ = UserDirectory.labels model.Directory state.UserId
            primary

        Html.div [
            prop.className "p-6 max-w-3xl"
            prop.children [
                Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
                Html.p [
                    prop.className [ "text-sm mb-4"; Tokens.Text.secondary ]
                    prop.text msgs.Subheading
                ]
                StateViews.notice { Dismiss = msgs.Dismiss } model.Error (fun () -> dispatch DismissError)
                filterBar
                listPane

                match model.Offboard with
                | Some state -> offboardModalView msgs state (selfLabelForModal state) dispatch
                | None -> Html.none
            ]
        ]

/// The audit-trail viewer as Feliz (`AuditLogUI`'s view before its tree).
module AuditLog =
    open AuditLogUI

    let private formatTimestamp (ts: DateTime) : string =
        ts.ToUniversalTime().ToString "yyyy-MM-dd HH:mm:ss"

    /// Pretty-print the recorded payload for the detail expansion. A
    /// payload that will not parse is shown as stored rather than hidden —
    /// a tombstoned row (the GDPR erasure marker) is exactly that case, and
    /// the fact that a row was redacted is itself audit-relevant.
    let private prettyPayload (payload: string) : string =
        try
            emitJsExpr payload "JSON.stringify(JSON.parse($0), null, 2)"
        with _ ->
            payload

    let private filterBar (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) =
        // Each visible <label> sits BESIDE its control rather than wrapping
        // it and this tier has no `for`/`id` pairing convention, so every
        // control carries its own accessible name read from the SAME
        // binding as the label — see `UsageDashboard.renderControls` for the
        // full rationale.
        let field (label: string) (control: ReactElement) =
            Html.div [
                prop.className "flex flex-col"
                prop.children [
                    Html.label [ prop.className ("text-xs mb-1 " + Tokens.Text.secondary); prop.text label ]
                    control
                ]
            ]

        Html.div [
            prop.className "flex flex-wrap items-end gap-4 mb-4"
            prop.children [
                field
                    msgs.FromLabel
                    (Html.input [
                        prop.type' "date"
                        prop.ariaLabel msgs.FromLabel
                        prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                        prop.value model.Draft.From
                        prop.onChange (SetFrom >> dispatch)
                    ])
                field
                    msgs.ToLabel
                    (Html.input [
                        prop.type' "date"
                        prop.ariaLabel msgs.ToLabel
                        prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                        prop.value model.Draft.To
                        prop.onChange (SetTo >> dispatch)
                    ])
                field
                    msgs.EventTypeLabel
                    (Html.select [
                        prop.ariaLabel msgs.EventTypeLabel
                        prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                        prop.value model.Draft.EventType
                        prop.onChange (SetEventType >> dispatch)
                        prop.children (
                            Html.option [ prop.value ""; prop.text msgs.AllEventTypes ]
                            :: (model.EventTypes
                                |> List.map (fun t -> Html.option [ prop.value t; prop.text t ]))
                        )
                    ])
                // The text input commits on Enter / blur, so it takes its
                // accessible name from a wrapping <label>.
                Html.label [
                    prop.className "flex flex-col"
                    prop.children [
                        Html.span [
                            prop.className [ "text-xs mb-1"; Tokens.Text.secondary ]
                            prop.text msgs.ActorLabel
                        ]
                        Forms.Input.text model.Draft.Actor (SetActor >> dispatch) msgs.ActorPlaceholder
                    ]
                ]
                Forms.Button.primaryWith Forms.Button.Enabled msgs.ApplyFilters (fun () -> dispatch ApplyFilters)
                Forms.Button.secondaryWith Forms.Button.Enabled msgs.ClearFilters (fun () -> dispatch ClearFilters)
                Forms.Button.secondaryWith Forms.Button.Enabled msgs.Refresh (fun () -> dispatch Refresh)
                Forms.Button.primaryWith
                    (Forms.Button.stateOf model.Exporting msgs.Exporting (List.isEmpty model.Events))
                    msgs.ExportCsv
                    (fun () -> dispatch ExportCsv)
            ]
        ]

    /// The recorded payload, shown under the row's summary while its detail is open.
    let private detail (msgs: AuditLogMessages) (view: AuditEventView) =
        Html.div [
            prop.className [ "mt-2 p-3 rounded"; Tokens.Bg.subtle ]
            prop.children [
                Html.div [
                    prop.className [ "text-xs font-semibold uppercase tracking-wider mb-1"; Tokens.Text.secondary ]
                    prop.text msgs.PayloadHeading
                ]
                Html.pre [
                    prop.className [ "text-xs font-mono whitespace-pre-wrap break-all"; Tokens.Text.secondary ]
                    prop.text (prettyPayload view.Payload)
                ]
            ]
        ]

    let private columns
        (msgs: AuditLogMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        : Data.Column<AuditEventView> list =
        let mono (text: string) =
            Html.span [ prop.className "font-mono whitespace-nowrap"; prop.text text ]

        [
            {
                Header = msgs.ColumnTime
                Align = Data.Left
                Cell = fun v -> mono (formatTimestamp v.OccurredAt)
            }
            {
                Header = msgs.ColumnEventType
                Align = Data.Left
                Cell = fun v -> mono v.EventType
            }
            {
                Header = msgs.ColumnActor
                Align = Data.Left
                Cell = fun v -> mono (v.Actor |> Option.defaultValue msgs.UnattributedActor)
            }
            {
                Header = msgs.ColumnSummary
                Align = Data.Left
                Cell =
                    fun v ->
                        Html.div [
                            prop.className "break-all"
                            prop.children [
                                Html.text v.Summary
                                if Set.contains v.Id model.Expanded then
                                    detail msgs v
                            ]
                        ]
            }
            {
                Header = ""
                Align = Data.Right
                Cell =
                    fun v ->
                        let isExpanded = Set.contains v.Id model.Expanded

                        Html.button [
                            prop.ariaExpanded isExpanded
                            prop.ariaLabel (if isExpanded then msgs.HideDetail else msgs.ShowDetail)
                            prop.className
                                "px-2 py-1 text-xs bg-gray-100 hover:bg-gray-200 border border-gray-300 rounded"
                            prop.onClick (fun _ -> dispatch (ToggleDetail v.Id))
                            prop.text (if isExpanded then "−" else "+")
                        ]
            }
        ]

    /// The two empty states are DIFFERENT states, not one message with two
    /// wordings: nothing recorded means the trail is off (or nothing
    /// audited has happened yet) and the remedy is server-side; nothing
    /// matching means the filters are too narrow and the remedy is right
    /// here. A single "no results" would send the operator to the wrong one
    /// half the time.
    let private emptyState (msgs: AuditLogMessages) (model: Model) =
        let filtered = model.Applied <> emptyFilters || not (List.isEmpty model.EventTypes)

        Html.div [
            prop.className ("p-8 text-center text-sm " + Tokens.Text.muted)
            prop.text (if filtered then msgs.NoMatches else msgs.NoEvents)
        ]

    let private eventTable (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) =
        if List.isEmpty model.Events then
            emptyState msgs model
        else
            Html.div [
                prop.children [
                    Data.table (columns msgs model dispatch) model.Events
                    Html.div [
                        prop.className "flex items-center justify-between mt-3"
                        prop.children [
                            Html.span [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text (msgs.ShowingCount (List.length model.Events) model.MatchedCount)
                            ]
                            match model.NextCursor with
                            | None -> Html.none
                            | Some _ ->
                                Forms.Button.secondaryWith
                                    (Forms.Button.stateOf model.LoadingMore msgs.Loading false)
                                    msgs.LoadMore
                                    (fun () -> dispatch LoadMore)
                        ]
                    ]
                ]
            ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        Html.div [
            prop.className "p-4"
            prop.children [
                Html.h2 [
                    prop.className ("text-lg font-semibold mb-2 " + Tokens.Text.primary)
                    prop.text msgs.Heading
                ]
                Html.p [
                    prop.className ("text-sm mb-4 " + Tokens.Text.secondary)
                    prop.text msgs.Subheading
                ]
                filterBar msgs model dispatch

                match model.Status with
                | Remote.Failed err -> StateViews.noticeStatic (Notice.Problem err)
                | Remote.NotAsked
                | Remote.Loading -> Html.div [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
                | Remote.Reloading()
                | Remote.Loaded() -> eventTable msgs model dispatch
            ]
        ]

/// The data-subject-request admin as Feliz (`DataSubjectRequestAdminUI`'s view before its tree).
module DataSubjectRequestAdmin =
    open ToolUp.Platform.DataSubjectRequestApi
    open DataSubjectRequestAdminUI

    let private tabBar (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        Html.div [
            prop.className [ "px-4"; Tokens.Bg.card ]
            prop.children [
                Tabs.bar [ ExportTab, msgs.TabExport; EraseTab, msgs.TabErase ] model.ActiveTab (SwitchTab >> dispatch)
            ]
        ]

    let private subjectFormRows (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        Html.div [
            prop.className "flex flex-col gap-3"
            prop.children [
                Forms.Input.text model.SubjectInput (fun v -> dispatch (SetSubjectInput v)) msgs.SubjectPlaceholder

                Forms.Input.text model.TeamInput (fun v -> dispatch (SetTeamInput v)) msgs.TeamPlaceholder

                Forms.Input.text model.ReasonInput (fun v -> dispatch (SetReasonInput v)) msgs.ReasonPlaceholder
            ]
        ]

    let private ticketStatusLabel (msgs: DataSubjectRequestAdminMessages) =
        function
        | ExportStatus.Preparing -> msgs.TicketPreparing
        | ExportStatus.Ready size -> msgs.TicketReady size
        | ExportStatus.Failed reason -> msgs.TicketFailed reason
        | ExportStatus.Cancelled -> msgs.TicketCancelled
        | ExportStatus.Expired -> msgs.TicketExpired
        | ExportStatus.Unknown -> msgs.TicketUnknown

    /// The in-flight background-export panel. Shows the active
    /// ticket's status (auto-polled) with a cancel control. Download happens
    /// automatically when the ticket flips to `Ready`.
    let private activeTicketPanel (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        match model.ActiveTicket, model.TicketStatus with
        | Some ticket, Some status ->
            Html.div [
                prop.className "mt-4 p-3 border border-border rounded bg-gray-50"
                prop.children [
                    Html.div [
                        prop.className "flex items-center justify-between"
                        prop.children [
                            Html.div [
                                prop.children [
                                    Html.div [
                                        prop.className "text-sm font-medium"
                                        prop.text msgs.BackgroundExportHeading
                                    ]
                                    Html.div [
                                        prop.className "text-xs text-muted"
                                        prop.text (msgs.TicketLine ticket (ticketStatusLabel msgs status))
                                    ]
                                ]
                            ]
                            match status with
                            | ExportStatus.Preparing ->
                                Forms.Button.secondaryWith Forms.Button.Enabled msgs.Cancel (fun () ->
                                    dispatch CancelAsyncExport)
                            | _ -> Html.none
                        ]
                    ]
                ]
            ]
        | _ -> Html.none

    let private exportTabView (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        let submitState =
            Forms.Button.stateOf (model.Busy = RunningExport) msgs.Exporting (model.Busy <> Idle)

        Layout.Panel.panel msgs.ExportPanelTitle [
            Html.p [
                prop.className "text-sm text-text-secondary mb-3"
                prop.text msgs.ExportPanelBody
            ]
            subjectFormRows msgs model dispatch
            Html.label [
                prop.className "mt-3 flex items-center gap-2 text-sm cursor-pointer"
                prop.children [
                    Html.input [
                        prop.type' "checkbox"
                        prop.isChecked model.AsyncMode
                        prop.onChange (fun (v: bool) -> dispatch (SetAsyncMode v))
                    ]
                    Html.span [ prop.text msgs.AsyncModeLabel ]
                ]
            ]
            Html.div [
                prop.className "mt-4 flex items-center gap-3"
                prop.children [
                    Forms.Button.primaryWith submitState msgs.RequestExport (fun () -> dispatch SubmitExport)
                    if model.Busy = RunningExport then
                        Html.span [ prop.className "text-xs text-muted"; prop.text msgs.AggregatingSegments ]
                ]
            ]
            activeTicketPanel msgs model dispatch
        ]

    let private policyRadio (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        let radio (selected: bool) (label: string) (description: string) (onSelect: unit -> unit) =
            Html.label [
                prop.className [
                    "flex items-start gap-2 p-3 border rounded cursor-pointer transition-colors"
                    if selected then
                        "border-brand bg-brand/5"
                    else
                        "border-border hover:bg-gray-50"
                ]
                prop.onClick (fun _ -> onSelect ())
                prop.children [
                    Html.input [
                        prop.type' "radio"
                        prop.isChecked selected
                        prop.readOnly true
                        prop.className "mt-1"
                    ]
                    Html.div [
                        prop.children [
                            Html.div [ prop.className "text-sm font-medium"; prop.text label ]
                            Html.div [ prop.className "text-xs text-muted"; prop.text description ]
                        ]
                    ]
                ]
            ]

        Html.div [
            prop.className "flex flex-col gap-2"
            prop.children [
                Html.span [ prop.className "text-xs text-muted"; prop.text msgs.OverridePolicyPrompt ]
                radio
                    (model.OverridePolicy = None)
                    msgs.UseDeploymentDefault
                    msgs.UseDeploymentDefaultDescription
                    (fun () -> dispatch (SetOverridePolicy None))
                for policy in
                    [
                        ErasurePolicy.HardDelete
                        ErasurePolicy.Tombstone
                        ErasurePolicy.RetainPerCompliance
                    ] do
                    radio
                        (model.OverridePolicy = Some policy)
                        (policyLabel msgs policy)
                        (policyDescription msgs policy)
                        (fun () -> dispatch (SetOverridePolicy(Some policy)))
            ]
        ]

    let private previewPanel
        (msgs: DataSubjectRequestAdminMessages)
        (preview: ErasurePreview)
        (model: Model)
        (dispatch: Msg -> unit)
        =
        let total = preview.PerHandlerCounts.Values |> Seq.sumBy _.RecordsAffected

        let confirmState =
            Forms.Button.stateOf (model.Busy = RunningConfirm) msgs.Confirming (model.Busy <> Idle)

        let cancelState =
            if model.Busy = RunningConfirm then
                Forms.Button.Disabled
            else
                Forms.Button.Enabled

        Layout.Panel.panel msgs.PreviewPanelTitle [
            Html.div [
                prop.className "flex items-center justify-between mb-3"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.div [ prop.className "text-sm"; prop.text (msgs.RequestIdLine preview.Request.Id) ]
                            Html.div [
                                prop.className "text-xs text-muted"
                                prop.text (
                                    msgs.PreviewSummaryLine
                                        (policyLabel msgs preview.Request.Policy)
                                        total
                                        preview.PerHandlerCounts.Count
                                )
                            ]
                        ]
                    ]
                    Forms.Button.secondaryWith cancelState msgs.Cancel (fun () -> dispatch CancelPreview)
                ]
            ]
            if preview.PerHandlerCounts.IsEmpty then
                Html.p [ prop.className "text-sm text-muted py-2"; prop.text msgs.PreviewEmpty ]
            else
                Html.div [
                    prop.className "flex flex-col gap-1 mb-4"
                    prop.children [
                        for KeyValue(name, summary) in preview.PerHandlerCounts do
                            Html.div [
                                prop.className
                                    "flex items-center justify-between p-2 border border-border rounded text-sm"
                                prop.children [
                                    Html.span [ prop.className "font-medium"; prop.text name ]
                                    Html.span [
                                        prop.className "text-xs text-muted"
                                        prop.text (msgs.HandlerRecordsAffected summary.RecordsAffected)
                                    ]
                                ]
                            ]
                    ]
                ]
            Html.div [
                prop.className "flex items-center gap-3"
                prop.children [
                    Forms.Button.dangerWith confirmState msgs.ConfirmErase (fun () -> dispatch SubmitConfirm)
                    Html.span [
                        prop.className "text-xs text-muted"
                        prop.text msgs.ConfirmIrreversibleFootnote
                    ]
                ]
            ]
        ]

    let private runSummaryPanel (msgs: DataSubjectRequestAdminMessages) (summary: ErasureRunSummary) =
        let perHandlerRow (name: string) (outcome: Result<ErasureSummary, ErasureError>) =
            let body, badgeClass =
                match outcome with
                | Result.Ok s -> msgs.HandlerRecordsAffected s.RecordsAffected, "bg-green-100 text-green-700"
                | Result.Error err -> ErasureError.toMessage err, "bg-red-100 text-red-700"

            Html.div [
                prop.className "flex items-center justify-between p-2 border border-border rounded text-sm"
                prop.children [
                    Html.span [ prop.className "font-medium"; prop.text name ]
                    Html.span [ prop.className $"text-xs px-2 py-0.5 rounded {badgeClass}"; prop.text body ]
                ]
            ]

        let started = summary.StartedAt.ToString "u"
        let completed = summary.CompletedAt.ToString "u"

        let overall =
            if summary.OverallSuccess then
                msgs.OverallSuccess
            else
                msgs.OverallPartialFailure

        Layout.Panel.panel msgs.RunSummaryPanelTitle [
            Html.div [
                prop.className "text-xs text-muted mb-2"
                prop.text (msgs.RunSummaryLine started completed overall)
            ]
            Html.div [
                prop.className "flex flex-col gap-1"
                prop.children [
                    for KeyValue(name, outcome) in summary.PerHandler do
                        perHandlerRow name outcome
                ]
            ]
        ]

    let private eraseTabView (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        let previewState =
            Forms.Button.stateOf (model.Busy = RunningPreview) msgs.Previewing (model.Busy <> Idle)

        Html.div [
            prop.className "flex flex-col gap-4"
            prop.children [
                Layout.Panel.panel msgs.ErasePanelTitle [
                    Html.p [
                        prop.className "text-sm text-text-secondary mb-3"
                        prop.text msgs.ErasePanelBody
                    ]
                    subjectFormRows msgs model dispatch
                    Html.div [ prop.className "mt-3"; prop.children [ policyRadio msgs model dispatch ] ]
                    Html.div [
                        prop.className "mt-4 flex items-center gap-3"
                        prop.children [
                            Forms.Button.primaryWith previewState msgs.PreviewErase (fun () -> dispatch SubmitPreview)
                            if model.PendingPreview.IsSome then
                                Html.span [ prop.className "text-xs text-muted"; prop.text msgs.PendingPreviewHint ]
                        ]
                    ]
                ]

                match model.PendingPreview with
                | Some preview -> previewPanel msgs preview model dispatch
                | None -> Html.none

                match model.LastRunSummary with
                | Some summary -> runSummaryPanel msgs summary
                | None -> Html.none
            ]
        ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: DataSubjectRequestAdminMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let body =
            Html.div [
                prop.className "flex flex-col"
                prop.children [
                    tabBar msgs model dispatch
                    Html.div [
                        prop.className "p-4"
                        prop.children [
                            match model.ActiveTab with
                            | ExportTab -> exportTabView msgs model dispatch
                            | EraseTab -> eraseTabView msgs model dispatch
                        ]
                    ]
                ]
            ]

        // 0.5.6 — FullWidth render. The notice stacks above the body.
        Html.div [
            prop.className "flex flex-col gap-3 h-full"
            prop.children [
                StateViews.notice { Dismiss = msgs.DismissBanner } model.Banner (fun () -> dispatch DismissBanner)
                body
            ]
        ]

/// The external-contact admin as Feliz (`ExternalContactManagerUI`'s view before its tree).
module ExternalContactManager =
    open Feliz.AgGrid
    open ExternalContactManagerUI

    /// The channels an operator can record a consent on. The wire strings
    /// are `NotificationKind.SinkKind.toWireString` values, which the
    /// server parses back with `tryParse` — one vocabulary, not two.
    let private channelOptions = [ "Email"; "Sms"; "Push.WebPush"; "Push.Fcm"; "Push.Apns" ]

    let private pill (label: string) (cls: string) =
        Html.span [
            prop.className $"inline-block text-xs px-2 py-0.5 rounded {cls}"
            prop.text label
        ]

    /// The consent summary shown in the grid row: the channels this contact
    /// is actually reachable on, or a plain statement that it is reachable
    /// on none. The empty case is spelled out rather than left blank —
    /// "no consent recorded" is the single most important fact about a
    /// contact and a blank cell reads as a rendering bug.
    let private consentSummary (msgs: ExternalContactMessages) (contact: ExternalContact) =
        let channels =
            contact.OptIns
            |> Map.toList
            |> List.map (fst >> NotificationKind.SinkKind.toWireString)

        if List.isEmpty channels then
            pill msgs.NoConsent ("bg-gray-100 " + Tokens.Text.secondary)
        else
            Html.div [
                prop.className "flex gap-1 flex-wrap"
                prop.children [ for channel in channels -> pill channel "bg-green-100 text-green-700" ]
            ]

    let private contactFormPanel
        (msgs: ExternalContactMessages)
        (busy: bool)
        (form: ContactForm)
        (dispatch: Msg -> unit)
        =
        let heading =
            match form.EditingId with
            | None -> msgs.NewContactHeading
            | Some _ -> msgs.EditContactHeading

        Layout.Panel.panel heading [
            Html.div [
                prop.className "flex flex-col gap-3"
                prop.children [
                    Html.p [ prop.className "text-xs text-muted"; prop.text msgs.ContactFormHelp ]

                    Forms.Field.field
                        msgs.FieldDisplayName
                        (Forms.Input.text
                            form.DisplayName
                            (fun v -> dispatch (SetFormField(fun f -> { f with DisplayName = v })))
                            msgs.PlaceholderDisplayName)

                    Forms.Field.field
                        msgs.FieldEmail
                        (Forms.Input.text
                            form.Email
                            (fun v -> dispatch (SetFormField(fun f -> { f with Email = v })))
                            msgs.PlaceholderEmail)

                    Forms.Field.field
                        msgs.FieldPhone
                        (Forms.Input.text
                            form.Phone
                            (fun v -> dispatch (SetFormField(fun f -> { f with Phone = v })))
                            msgs.PlaceholderPhone)

                    Forms.Field.field
                        msgs.FieldWhatsApp
                        (Forms.Input.text
                            form.WhatsApp
                            (fun v -> dispatch (SetFormField(fun f -> { f with WhatsApp = v })))
                            msgs.PlaceholderPhone)

                    Forms.Field.field
                        msgs.FieldTags
                        (Forms.Input.text
                            form.Tags
                            (fun v -> dispatch (SetFormField(fun f -> { f with Tags = v })))
                            msgs.PlaceholderTags)

                    Forms.Field.field
                        msgs.FieldNotes
                        (Forms.Input.text
                            form.Notes
                            (fun v -> dispatch (SetFormField(fun f -> { f with Notes = v })))
                            msgs.PlaceholderNotes)

                    Forms.Field.actions [
                        Forms.Button.primaryWith
                            (Forms.Button.stateOf busy msgs.SaveContact false)
                            msgs.SaveContact
                            (fun () -> dispatch SubmitForm)
                        Forms.Button.secondary msgs.Cancel (fun () -> dispatch CancelForm)
                    ]
                ]
            ]
        ]

    /// The consent panel: what this contact has agreed to, one row per
    /// channel with a withdrawal beside it, and the form that records a new
    /// one. The source box is the point of the panel — see the preamble.
    let private consentPanel
        (msgs: ExternalContactMessages)
        (model: Model)
        (contact: ExternalContact)
        (dispatch: Msg -> unit)
        =
        let recorded = contact.OptIns |> Map.toList

        Layout.Panel.panel msgs.ConsentHeading [
            Html.div [
                prop.className "flex flex-col gap-4"
                prop.children [
                    Html.p [ prop.className "text-xs text-muted"; prop.text msgs.ConsentHelp ]

                    if List.isEmpty recorded then
                        Html.p [ prop.className "text-sm text-muted py-2"; prop.text msgs.NoConsentBody ]
                    else
                        Html.div [
                            prop.className "flex flex-col gap-2"
                            prop.children [
                                for channel, record in recorded ->
                                    let wire = NotificationKind.SinkKind.toWireString channel

                                    Html.div [
                                        prop.className
                                            "flex items-center justify-between gap-3 p-2 rounded border border-gray-200"
                                        prop.children [
                                            Html.div [
                                                prop.className "flex flex-col"
                                                prop.children [
                                                    Html.span [ prop.className "text-sm font-medium"; prop.text wire ]
                                                    Html.span [
                                                        prop.className "text-xs text-muted"
                                                        prop.text (msgs.ConsentEvidence record.Source)
                                                    ]
                                                ]
                                            ]
                                            Forms.Button.secondary msgs.WithdrawConsent (fun () ->
                                                dispatch (WithdrawOptIn(contact.Id, wire)))
                                        ]
                                    ]
                            ]
                        ]

                    Html.div [
                        prop.className "flex flex-col gap-3 pt-2 border-t border-gray-200"
                        prop.children [
                            Html.p [ prop.className "text-xs text-muted"; prop.text msgs.RecordConsentHelp ]

                            Forms.Field.field
                                msgs.FieldChannel
                                (Html.select [
                                    prop.className "border rounded px-2 py-1 text-sm"
                                    prop.value model.OptIn.Channel
                                    prop.onChange (fun (v: string) -> dispatch (SetOptInChannel v))
                                    prop.children [
                                        for option in channelOptions ->
                                            Html.option [ prop.value option; prop.text option ]
                                    ]
                                ])

                            Forms.Field.field
                                msgs.FieldConsentSource
                                (Forms.Input.text
                                    model.OptIn.Source
                                    (fun v -> dispatch (SetOptInSource v))
                                    msgs.PlaceholderConsentSource)

                            Forms.Field.actions [
                                // Disabled rather than hidden while the source
                                // is blank: the operator should see that the
                                // control exists and what it is waiting for.
                                Forms.Button.primaryWith
                                    (Forms.Button.stateOf
                                        model.Busy
                                        msgs.RecordConsent
                                        (String.IsNullOrWhiteSpace model.OptIn.Source))
                                    msgs.RecordConsent
                                    (fun () -> dispatch (RecordOptIn contact.Id))
                            ]
                        ]
                    ]
                ]
            ]
        ]

    let private contactListView (msgs: ExternalContactMessages) (model: Model) (dispatch: Msg -> unit) =
        Html.div [
            prop.className "space-y-4"
            prop.children [
                Layout.Panel.panel msgs.ContactsPanel [
                    if model.Contacts.IsEmpty then
                        Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.NoContactsYet ]
                    else
                        AgGrid.grid [
                            AgGrid.baseTheme BaseTheme.themeBalham
                            AgGrid.domLayout AutoHeight
                            AgGrid.columnDefs [
                                ColumnDef.create [
                                    ColumnDef.headerName msgs.ColumnName
                                    ColumnDef.valueGetter (fun (c: ExternalContact) -> c.DisplayName)
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName msgs.ColumnEmail
                                    ColumnDef.valueGetter (fun (c: ExternalContact) ->
                                        c.OptionalEmailAddress |> Option.defaultValue "")
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName msgs.ColumnPhone
                                    ColumnDef.valueGetter (fun (c: ExternalContact) ->
                                        c.OptionalPhoneNumber |> Option.defaultValue "")
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName msgs.ColumnTags
                                    ColumnDef.valueGetter (fun (c: ExternalContact) -> c.Tags |> String.concat ", ")
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName msgs.ColumnConsent
                                    ColumnDef.cellRenderer (fun (p: ICellRendererParams<ExternalContact, obj>) ->
                                        match p.data with
                                        | Some contact -> consentSummary msgs contact
                                        | None -> Html.none)
                                ]
                                ColumnDef.create [
                                    ColumnDef.headerName ""
                                    ColumnDef.cellRenderer (fun (p: ICellRendererParams<ExternalContact, obj>) ->
                                        match p.data with
                                        | Some contact ->
                                            Forms.Button.secondary msgs.Manage (fun () ->
                                                dispatch (OpenContact contact.Id))
                                        | None -> Html.none)
                                ]
                            ]
                            AgGrid.rowData (List.toArray model.Contacts)
                        ]
                ]

                match model.Form with
                | Some form when form.EditingId.IsNone -> contactFormPanel msgs model.Busy form dispatch
                | _ ->
                    Html.div [
                        prop.children [ Forms.Button.primary msgs.AddContact (fun () -> dispatch StartAdd) ]
                    ]
            ]
        ]

    let private contactDetailView
        (msgs: ExternalContactMessages)
        (model: Model)
        (contactId: string)
        (dispatch: Msg -> unit)
        =
        match model.Contacts |> List.tryFind (fun c -> c.Id = contactId) with
        | None ->
            Html.div [
                prop.className "space-y-3"
                prop.children [
                    Html.p [ prop.className "text-sm text-muted"; prop.text msgs.ContactGone ]
                    Forms.Button.secondary msgs.BackToList (fun () -> dispatch BackToList)
                ]
            ]
        | Some contact ->
            Html.div [
                prop.className "space-y-4"
                prop.children [
                    Html.div [
                        prop.className "flex items-center justify-between gap-3"
                        prop.children [
                            Html.h3 [ prop.className "text-base font-semibold"; prop.text contact.DisplayName ]
                            Forms.Button.secondary msgs.BackToList (fun () -> dispatch BackToList)
                        ]
                    ]

                    match model.Form with
                    | Some form when form.EditingId = Some contact.Id -> contactFormPanel msgs model.Busy form dispatch
                    | _ ->
                        Layout.Panel.panel msgs.DetailsPanel [
                            Html.div [
                                prop.className "flex flex-col gap-2 text-sm"
                                prop.children [
                                    Html.div [
                                        prop.text (
                                            msgs.DetailEmail(contact.OptionalEmailAddress |> Option.defaultValue "—")
                                        )
                                    ]
                                    Html.div [
                                        prop.text (
                                            msgs.DetailPhone(contact.OptionalPhoneNumber |> Option.defaultValue "—")
                                        )
                                    ]
                                    Html.div [ prop.text (msgs.DetailTags(contact.Tags |> String.concat ", ")) ]
                                    Forms.Field.actions [
                                        Forms.Button.secondary msgs.EditContact (fun () ->
                                            dispatch (StartEdit contact.Id))
                                        Forms.Button.secondary msgs.DeleteContact (fun () ->
                                            dispatch (DeleteContact contact.Id))
                                    ]
                                ]
                            ]
                        ]

                    consentPanel msgs model contact dispatch
                ]
            ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: ExternalContactMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let body =
            if not model.Loaded then
                Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.Loading ]
            else
                match model.CurrentView with
                | ContactList -> contactListView msgs model dispatch
                | ContactDetail contactId -> contactDetailView msgs model contactId dispatch

        Html.div [
            prop.className "p-6 max-w-5xl"
            prop.children [
                Html.h2 [ prop.className "text-lg font-semibold mb-1"; prop.text msgs.Heading ]
                Html.p [
                    prop.className ("text-sm mb-4 " + Tokens.Text.secondary)
                    prop.text msgs.Subheading
                ]
                StateViews.notice { Dismiss = msgs.Dismiss } model.Error (fun () -> dispatch DismissError)
                body
            ]
        ]

/// The health monitor as Feliz (`HealthMonitorUI`'s view before its tree).
module HealthMonitor =
    open HealthMonitorUI

    // ─── Status-pill renderers ───────────────────────────────────────────

    /// Three-colour status pill mirroring the convention used elsewhere in
    /// the admin UI (`WebhookAdminUI.outcomeLabel`'s green / yellow / red
    /// idiom). Centralised so the live-health and preflight tables stay
    /// visually consistent — the same probe status (`Healthy`) and the same
    /// validator outcome (`Ok`) read identically green.
    let private statusPill (status: string) =
        let cls =
            match status with
            | "Healthy"
            | "Ok" -> "bg-green-100 text-green-700 border-green-200"
            | "Degraded"
            | "Warning" -> "bg-yellow-100 text-yellow-700 border-yellow-200"
            | "Unhealthy"
            | "Error" -> "bg-red-100 text-red-700 border-red-200"
            | _ -> ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)

        Html.span [
            prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
            prop.text status
        ]

    let private refreshButton (msgs: HealthMonitorMessages) (label: string) (onClick: unit -> unit) (loading: bool) =
        Forms.Button.secondaryWith (Forms.Button.stateOf loading msgs.Refreshing false) label onClick

    // ─── Live health tab ─────────────────────────────────────────────────

    // ─── Job-scheduler telemetry card ────────────────────────────────────

    /// Inline card on the Live tab. Suppressed entirely when no scheduler
    /// is registered (`HasScheduler = false`) so deployments without
    /// background jobs don't see a misleading zero counter. When a miss
    /// has been observed, the card pulls a warning border so operators
    /// scanning the page register the signal before reading the numbers.
    let private schedulerTelemetryCard (msgs: HealthMonitorMessages) (view: JobSchedulerTelemetryView) =
        if not view.HasScheduler then
            Html.none
        else
            let hasMiss = view.TickMissedCount60Min > 0 || view.LastTickMissedAt.IsSome

            let borderCls =
                if hasMiss then
                    "border-yellow-300 bg-yellow-50"
                else
                    "border-border bg-white"

            let countCls =
                if hasMiss then
                    "text-yellow-700 font-semibold"
                else
                    Tokens.Text.secondary

            let driftCell =
                match view.LastDriftMs with
                | Some ms -> Html.text $"{ms} ms"
                | None -> Html.span [ prop.className Tokens.Text.muted; prop.text "—" ]

            let missedAtCell =
                match view.LastTickMissedAt with
                | Some ts -> Html.text (ts.ToString "u")
                | None -> Html.span [ prop.className Tokens.Text.muted; prop.text "—" ]

            Html.div [
                prop.className $"border rounded-lg p-3 mb-4 text-sm {borderCls}"
                prop.children [
                    Html.div [
                        prop.className "flex items-baseline justify-between mb-1"
                        prop.children [
                            Html.h3 [ prop.className "text-sm font-semibold"; prop.text msgs.SchedulerDriftHeading ]
                            let generatedAtLabel = view.GeneratedAt.ToString "u"

                            Html.span [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text (msgs.AsOf generatedAtLabel)
                            ]
                        ]
                    ]
                    Html.p [
                        prop.className ("text-xs mb-2 " + Tokens.Text.secondary)
                        prop.text msgs.SchedulerLagHelp
                    ]
                    Html.div [
                        prop.className "grid grid-cols-3 gap-x-4 gap-y-1"
                        prop.children [
                            Html.div [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text msgs.SchedulerMissed60m
                            ]
                            Html.div [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text msgs.SchedulerLastDrift
                            ]
                            Html.div [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text msgs.SchedulerLastMissAt
                            ]
                            Html.div [
                                prop.className $"text-sm font-mono {countCls}"
                                prop.text (string view.TickMissedCount60Min)
                            ]
                            Html.div [ prop.className "text-sm font-mono"; prop.children [ driftCell ] ]
                            Html.div [ prop.className "text-sm font-mono"; prop.children [ missedAtCell ] ]
                        ]
                    ]
                ]
            ]

    // ─── Degraded-capability card ────────────────────────────────────────

    /// Inline card on the Live tab listing capabilities that wired
    /// best-effort and FAILED — the deployment is up but a capability is
    /// silently down (e.g. cross-silo crypto-shred cache eviction). Rendered
    /// ONLY when the set is non-empty (GP 13): a healthy deployment sees
    /// nothing, matching the byte-for-byte-unchanged `/health` payload. The
    /// red border signals this is a security/correctness degradation, not a
    /// transient probe blip.
    let private degradedCapabilitiesCard (msgs: HealthMonitorMessages) (entries: DegradedCapability list) =
        if List.isEmpty entries then
            Html.none
        else
            Html.div [
                prop.className "border border-red-300 bg-red-50 rounded-lg p-3 mb-4"
                prop.children [
                    Html.div [
                        prop.className "flex items-baseline justify-between mb-2"
                        prop.children [
                            Html.h3 [
                                prop.className "text-sm font-semibold text-red-800"
                                prop.text (msgs.DegradedCapabilities entries.Length)
                            ]
                        ]
                    ]
                    Html.p [
                        prop.className "text-xs text-red-700 mb-3"
                        prop.text msgs.DegradedCapabilitiesHelp
                    ]
                    Html.div [
                        prop.className "flex flex-col gap-3"
                        prop.children (
                            entries
                            |> List.map (fun d ->
                                Html.div [
                                    prop.className "border border-red-200 bg-white rounded p-3"
                                    prop.children [
                                        Html.div [
                                            prop.className "flex items-baseline justify-between mb-1"
                                            prop.children [
                                                Html.span [
                                                    prop.className "text-sm font-mono font-semibold text-red-800"
                                                    prop.text d.Capability
                                                ]
                                                Html.span [
                                                    prop.className ("text-xs " + Tokens.Text.muted)
                                                    prop.text (msgs.DegradedSince(string d.DegradedSince))
                                                ]
                                            ]
                                        ]
                                        Html.dl [
                                            prop.className "grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs"
                                            prop.children [
                                                Html.dt [
                                                    prop.className ("font-medium " + Tokens.Text.muted)
                                                    prop.text msgs.DegradedReason
                                                ]
                                                Html.dd [ prop.className Tokens.Text.secondary; prop.text d.Reason ]
                                                Html.dt [
                                                    prop.className ("font-medium " + Tokens.Text.muted)
                                                    prop.text msgs.DegradedImpact
                                                ]
                                                Html.dd [ prop.className Tokens.Text.secondary; prop.text d.Impact ]
                                                Html.dt [
                                                    prop.className ("font-medium " + Tokens.Text.muted)
                                                    prop.text msgs.Remediation
                                                ]
                                                Html.dd [
                                                    prop.className Tokens.Text.secondary
                                                    prop.text d.Remediation
                                                ]
                                            ]
                                        ]
                                    ]
                                ])
                        )
                    ]
                ]
            ]

    // ─── AI action-denial card ───────────────────────────────────────────

    /// Inline card on the Live tab showing the rolling AI action-denial
    /// rollup — the production-safe twin of the `EnableDevEndpoints`-gated
    /// `/dev/ai-allowlist` endpoint. Same `AIDenialRollup` record, same
    /// numbers; an operator watching a prompt-injection campaign in
    /// production does not have to turn the debug surface on to see it.
    ///
    /// **Suppressed entirely when the deployment has never recorded a
    /// denial** (GP 13) — an install with no AI, or one whose allowlist has
    /// never refused anything, sees no card at all. Once it HAS recorded
    /// one, the card stays and honestly reports a clean current window,
    /// because "it has subsided" is exactly what an operator who just
    /// received the alert needs to read.
    ///
    /// Renders only what the rollup carries: reasons are already
    /// control-stripped and truncated by the producing tier, and raw model
    /// arguments are never in the payload at all.
    let private aiDenialCard (msgs: HealthMonitorMessages) (rollup: AIDenialRollup) =
        if rollup.TotalDenialsAllTime = 0 then
            Html.none
        else
            let active = rollup.TotalDenialsInWindow > 0

            let shellCls =
                if active then
                    "border border-amber-300 bg-amber-50 rounded-lg p-3 mb-4"
                else
                    "border border-border bg-surface rounded-lg p-3 mb-4"

            let headingCls =
                if active then
                    "text-sm font-semibold text-amber-800"
                else
                    ("text-sm font-semibold " + Tokens.Text.secondary)

            let groupList (heading: string) (rows: AIDenialGroupCount list) =
                if List.isEmpty rows then
                    Html.none
                else
                    Html.div [
                        prop.children [
                            Html.div [
                                prop.className ("text-xs font-medium mb-1 " + Tokens.Text.muted)
                                prop.text heading
                            ]
                            Html.div [
                                prop.className "flex flex-col gap-0.5"
                                prop.children (
                                    rows
                                    |> List.truncate 5
                                    |> List.map (fun row ->
                                        Html.div [
                                            prop.className "flex items-baseline justify-between gap-3 text-xs"
                                            prop.children [
                                                Html.span [
                                                    prop.className ("font-mono " + Tokens.Text.secondary)
                                                    prop.text row.Key
                                                ]
                                                Html.span [
                                                    prop.className ("font-mono " + Tokens.Text.muted)
                                                    prop.text (string row.Count)
                                                ]
                                            ]
                                        ])
                                )
                            ]
                        ]
                    ]

            let recentRow (d: RecentAIDenial) =
                Html.div [
                    prop.className "text-xs bg-white border border-border rounded p-2"
                    prop.children [
                        Html.div [
                            prop.className "flex items-baseline justify-between gap-3"
                            prop.children [
                                Html.span [
                                    prop.className ("font-mono " + Tokens.Text.secondary)
                                    prop.text $"{d.ToolName} · {d.ActiveModule}"
                                ]
                                Html.span [ prop.className Tokens.Text.muted; prop.text (string d.OccurredAt) ]
                            ]
                        ]
                        Html.div [ prop.className ("mt-0.5 " + Tokens.Text.secondary); prop.text d.Reason ]
                    ]
                ]

            Html.div [
                prop.className shellCls
                prop.children [
                    Html.div [
                        prop.className "flex items-baseline justify-between mb-2"
                        prop.children [
                            Html.h3 [
                                prop.className headingCls
                                prop.text (msgs.AIDenials rollup.TotalDenialsInWindow)
                            ]
                            Html.span [
                                prop.className ("text-xs font-mono " + Tokens.Text.muted)
                                prop.text (
                                    msgs.AIDenialsRate rollup.WindowMinutes (sprintf "%.2f" rollup.DenialsPerMinute)
                                )
                            ]
                        ]
                    ]
                    Html.p [
                        prop.className ("text-xs mb-3 " + Tokens.Text.secondary)
                        prop.text msgs.AIDenialsHelp
                    ]

                    if not active then
                        Html.p [
                            prop.className ("text-xs " + Tokens.Text.muted)
                            prop.text msgs.AIDenialsQuiet
                        ]
                    else
                        Html.div [
                            prop.className "flex flex-col gap-3"
                            prop.children [
                                Html.div [
                                    prop.className "grid grid-cols-1 sm:grid-cols-2 gap-3"
                                    prop.children [
                                        groupList msgs.AIDenialsByTool rollup.ByToolName
                                        groupList msgs.AIDenialsByModule rollup.ByActiveModule
                                    ]
                                ]
                                if not (List.isEmpty rollup.RecentDenials) then
                                    Html.div [
                                        prop.children [
                                            Html.div [
                                                prop.className ("text-xs font-medium mb-1 " + Tokens.Text.muted)
                                                prop.text msgs.AIDenialsRecent
                                            ]
                                            Html.div [
                                                prop.className "flex flex-col gap-1"
                                                prop.children (
                                                    rollup.RecentDenials |> List.truncate 5 |> List.map recentRow
                                                )
                                            ]
                                        ]
                                    ]
                            ]
                        ]
                ]
            ]

    let private liveHealthHeader (msgs: HealthMonitorMessages) (snapshot: HealthSnapshot) =
        Html.div [
            prop.className ("text-xs " + Tokens.Text.muted)
            prop.text (msgs.GeneratedAt (string snapshot.GeneratedAt) snapshot.Probes.Length)
        ]

    let private column header (cell: 'row -> ReactElement) : Data.Column<'row> = {
        Header = header
        Align = Data.Left
        Cell = cell
    }

    let private quiet (classes: string) (value: string) =
        Html.span [ prop.className [ "text-xs"; classes ]; prop.text value ]

    /// A probe whose status flipped since the previous snapshot pulses its
    /// status pill, so the change is the first thing the eye lands on.
    let private probeColumns
        (msgs: HealthMonitorMessages)
        (recentlyFlipped: Set<string>)
        : Data.Column<HealthProbeView> list =
        [
            column msgs.ColumnStatus (fun row ->
                Html.span [
                    if recentlyFlipped |> Set.contains row.Name then
                        prop.className "inline-block animate-pulse"
                    prop.children [ statusPill row.Status ]
                ])
            column msgs.ColumnProbe (fun row -> Html.span [ prop.className "font-medium"; prop.text row.Name ])
            column msgs.ColumnKind (fun row -> quiet Tokens.Text.muted row.Kind)
            column msgs.ColumnTimeout (fun row -> quiet ("font-mono " + Tokens.Text.muted) $"{row.TimeoutMs} ms")
            column msgs.ColumnElapsed (fun row -> quiet ("font-mono " + Tokens.Text.muted) $"{row.ElapsedMs} ms")
            column msgs.ColumnMessage (fun row -> quiet Tokens.Text.secondary row.Message)
        ]

    let private probesTable
        (msgs: HealthMonitorMessages)
        (recentlyFlipped: Set<string>)
        (probes: HealthProbeView list)
        =
        if List.isEmpty probes then
            Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoProbes ]
        else
            Data.table (probeColumns msgs recentlyFlipped) probes

    let private liveHealthTabView (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
        let isLoading = Remote.isLoading model.Live

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.LiveHealthHeading ]
                                Html.p [
                                    prop.className ("text-xs " + Tokens.Text.muted)
                                    prop.text msgs.ProbesFootnote
                                ]
                            ]
                        ]
                        refreshButton msgs msgs.Refresh (fun () -> dispatch RefreshLive) isLoading
                    ]
                ]
                // Degraded capabilities first (most urgent: a
                // capability is down). Suppressed when the set is empty or
                // the load is in flight.
                match model.Degraded with
                | Remote.Loaded entries -> degradedCapabilitiesCard msgs entries
                | _ -> Html.none

                // AI action denials. `Loaded None` means no AI
                // companion is composed, so there is nothing to say; a
                // `Some` rollup suppresses itself when the deployment has
                // never recorded a denial.
                match model.AIDenials with
                | Remote.Loaded(Some rollup) -> aiDenialCard msgs rollup
                | _ -> Html.none

                // Surface scheduler drift above the probe
                // table. Suppressed when no scheduler is registered, and
                // when the load is in flight (no point flashing "Loading..."
                // for a side-counter).
                match model.SchedulerTelemetry with
                | Remote.Loaded view -> schedulerTelemetryCard msgs view
                | _ -> Html.none

                match model.Live with
                | Remote.NotAsked
                | Remote.Loading
                | Remote.Reloading _ ->
                    Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
                | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
                | Remote.Loaded snapshot ->
                    Html.div [
                        prop.children [
                            liveHealthHeader msgs snapshot
                            Html.div [
                                prop.className "mt-3"
                                prop.children [ probesTable msgs model.RecentlyFlipped snapshot.Probes ]
                            ]
                        ]
                    ]
            ]
        ]

    // ─── Preflight tab ───────────────────────────────────────────────────

    let private outcomeColumns (msgs: HealthMonitorMessages) : Data.Column<PreflightOutcomeView> list = [
        column msgs.ColumnStatus (fun row -> statusPill row.Status)
        column msgs.ColumnValidator (fun row -> Html.span [ prop.className "font-medium"; prop.text row.Name ])
        column msgs.ColumnElapsed (fun row -> quiet ("font-mono " + Tokens.Text.muted) $"{row.ElapsedMs} ms")
        column msgs.ColumnMessage (fun row -> quiet Tokens.Text.secondary row.Message)
    ]

    let private outcomesTable (msgs: HealthMonitorMessages) (outcomes: PreflightOutcomeView list) =
        if List.isEmpty outcomes then
            Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoValidators ]
        else
            Data.table (outcomeColumns msgs) outcomes

    let private preflightTabView (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
        let isLoading = Remote.isLoading model.Preflight

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.PreflightHeading ]
                                Html.p [
                                    prop.className ("text-xs " + Tokens.Text.muted)
                                    prop.text msgs.PreflightFootnote
                                ]
                            ]
                        ]
                        refreshButton msgs msgs.Refetch (fun () -> dispatch RefreshPreflight) isLoading
                    ]
                ]
                match model.Preflight with
                | Remote.NotAsked
                | Remote.Loading
                | Remote.Reloading _ ->
                    Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
                | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
                | Remote.Loaded snapshot ->
                    if not snapshot.HasSnapshot then
                        Html.p [
                            prop.className ("text-sm " + Tokens.Text.muted)
                            prop.text msgs.PreflightUnavailable
                        ]
                    else
                        outcomesTable msgs snapshot.Outcomes
            ]
        ]

    // ─── View ────────────────────────────────────────────────────────────

    let private tabBar (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
        Html.div [
            prop.className [ "px-4"; Tokens.Bg.card ]
            prop.children [
                Tabs.bar
                    [ LiveHealthTab, msgs.LiveHealthTab; PreflightTab, msgs.PreflightTab ]
                    model.ActiveTab
                    (SwitchTab >> dispatch)
            ]
        ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let content =
            match model.ActiveTab with
            | LiveHealthTab -> liveHealthTabView msgs model dispatch
            | PreflightTab -> preflightTabView msgs model dispatch

        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [ tabBar msgs model dispatch; content ]
        ]

/// The background-jobs admin as Feliz (`JobsAdminUI`'s view before its tree).
module JobsAdmin =
    open JobsAdminUI

    // ─── View ───────────────────────────────────────────────────────────

    let private standingLabel (msgs: JobsAdminMessages) (standing: JobStanding) =
        match standing with
        | JobStanding.PermanentlyFailed -> msgs.StandingPermanentlyFailed
        | JobStanding.FailingWillRetry -> msgs.StandingFailingWillRetry
        | JobStanding.InProgress -> msgs.StandingInProgress
        | JobStanding.Succeeded -> msgs.StandingSucceeded
        | JobStanding.AttemptCancelled -> msgs.StandingAttemptCancelled
        | JobStanding.NotYetRun -> msgs.StandingNotYetRun

    let private standingPill (msgs: JobsAdminMessages) (standing: JobStanding) =
        let cls =
            match standing with
            | JobStanding.PermanentlyFailed -> "bg-red-100 text-red-700 border-red-200"
            | JobStanding.FailingWillRetry -> "bg-yellow-100 text-yellow-700 border-yellow-200"
            | JobStanding.InProgress -> "bg-blue-100 text-blue-700 border-blue-200"
            | JobStanding.Succeeded -> "bg-green-100 text-green-700 border-green-200"
            | JobStanding.AttemptCancelled
            | JobStanding.NotYetRun -> ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)

        Html.span [
            prop.className $"inline-block px-2 py-0.5 text-xs rounded border {cls}"
            prop.text (standingLabel msgs standing)
        ]

    let private formatTime (time: DateTime) =
        time.ToString("yyyy-MM-dd HH:mm:ss") + " UTC"

    let private rerunCell (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (job: JobDefinition) =
        let progress =
            match model.Reruns.TryFind job.JobId with
            | Some RerunProgress.Starting -> Some msgs.RerunStarting
            | Some(RerunProgress.Awaiting _) -> Some msgs.RerunAwaiting
            | Some(RerunProgress.Landed standing) -> Some(msgs.RerunLanded(standingLabel msgs standing))
            | Some RerunProgress.StillRunning -> Some msgs.RerunStillRunning
            | Some(RerunProgress.Refused reason) -> Some(msgs.RerunRefused reason)
            | None -> None

        let inFlight =
            match model.Reruns.TryFind job.JobId with
            | Some RerunProgress.Starting
            | Some(RerunProgress.Awaiting _) -> true
            | _ -> false

        Html.div [
            prop.className "space-y-1"
            prop.children [
                match rerunOffer job with
                | RerunOffer.Offered ->
                    Forms.Button.secondaryWith
                        (Forms.Button.stateOf inFlight msgs.RerunButton false)
                        msgs.RerunButton
                        (fun () -> dispatch (RerunRequested job.JobId))
                | RerunOffer.RefusedDisabled ->
                    Html.p [
                        prop.className [ "text-xs"; Tokens.Text.muted ]
                        prop.text msgs.RerunUnavailableDisabled
                    ]
                | RerunOffer.RefusedCancelled ->
                    Html.p [
                        prop.className [ "text-xs"; Tokens.Text.muted ]
                        prop.text msgs.RerunUnavailableCancelled
                    ]
                match progress with
                | Some text -> Html.p [ prop.className [ "text-xs"; Tokens.Text.secondary ]; prop.text text ]
                | None -> Html.none
            ]
        ]

    let private attemptLabel (msgs: JobsAdminMessages) (reading: AttemptReading) =
        match reading with
        | AttemptReading.InProgress -> msgs.StandingInProgress
        | AttemptReading.RetryPending -> msgs.AttemptRetryPending
        | AttemptReading.Failed -> msgs.AttemptFailed
        | AttemptReading.Succeeded -> msgs.StandingSucceeded
        | AttemptReading.PermanentlyFailed -> msgs.StandingPermanentlyFailed
        | AttemptReading.Cancelled -> msgs.StandingAttemptCancelled

    let private attemptPill (msgs: JobsAdminMessages) (reading: AttemptReading) =
        let cls =
            match reading with
            | AttemptReading.PermanentlyFailed -> "bg-red-100 text-red-700 border-red-200"
            | AttemptReading.Failed -> "bg-yellow-100 text-yellow-700 border-yellow-200"
            | AttemptReading.InProgress
            | AttemptReading.RetryPending -> "bg-blue-100 text-blue-700 border-blue-200"
            | AttemptReading.Succeeded -> "bg-green-100 text-green-700 border-green-200"
            | AttemptReading.Cancelled -> ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)

        Html.span [
            prop.className $"inline-block px-2 py-0.5 text-xs rounded border {cls}"
            prop.text (attemptLabel msgs reading)
        ]

    /// A success's warning, shown in amber beside the status it qualifies.
    let private warningNote (warning: string) =
        Html.p [
            prop.className "mt-1 text-xs text-amber-700 font-mono break-all"
            prop.text warning
        ]

    let private attemptItem (msgs: JobsAdminMessages) (job: JobDefinition) (run: JobRun) =
        let reading = attemptReading job run

        Html.li [
            prop.key (string run.RunId)
            prop.className "py-1"
            prop.children [
                Html.div [
                    prop.className ("flex flex-wrap items-center gap-2 text-xs " + Tokens.Text.secondary)
                    prop.children [
                        Html.span [ prop.className "font-medium"; prop.text (msgs.AttemptNumber run.Attempt) ]
                        attemptPill msgs reading
                        Html.span [ prop.text (msgs.AttemptStarted(formatTime run.StartedAt)) ]
                        match attemptDuration run with
                        | Some span -> Html.span [ prop.text (msgs.AttemptTook(formatDuration span)) ]
                        | None -> Html.none
                    ]
                ]
                match run.Error with
                | Some error ->
                    Html.p [
                        prop.className "mt-0.5 text-xs text-red-700"
                        prop.children [
                            Html.span [ prop.className "font-medium"; prop.text msgs.ReasonLabel ]
                            Html.span [ prop.className "ml-1 font-mono break-all"; prop.text error ]
                        ]
                    ]
                | None -> Html.none
                match run.Status, run.Warning with
                | JobRunStatus.Succeeded, Some warning -> warningNote warning
                | _ -> Html.none
            ]
        ]

    let private attemptsList (msgs: JobsAdminMessages) (model: Model) (job: JobDefinition) =
        Html.div [
            prop.className [ "mt-2 px-3 py-2 rounded"; Tokens.Bg.subtle ]
            prop.children [
                match model.Attempts.TryFind job.JobId with
                | None
                | Some Remote.NotAsked
                | Some Remote.Loading ->
                    Html.p [
                        prop.className [ "text-xs"; Tokens.Text.muted ]
                        prop.text msgs.AttemptsLoading
                    ]
                | Some(Remote.Failed message) ->
                    Html.p [
                        prop.className "text-xs text-red-700"
                        prop.text (msgs.AttemptsLoadFailed message)
                    ]
                | Some(Remote.Reloading [])
                | Some(Remote.Loaded []) ->
                    Html.p [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.NoAttempts ]
                | Some(Remote.Reloading runs)
                | Some(Remote.Loaded runs) -> Html.ul (runs |> List.map (attemptItem msgs job))
            ]
        ]

    /// The job cell: handler, id, status, the attempts toggle, and the
    /// attempts list beneath it when expanded.
    let private jobCell (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (job: JobDefinition) =
        let expanded = model.Expanded.Contains job.JobId

        Html.div [
            Html.div [ prop.className "text-sm font-medium"; prop.text job.Handler ]
            Html.div [
                prop.className [ "text-xs font-mono"; Tokens.Text.muted ]
                prop.text (string job.JobId)
            ]
            match job.Status with
            | JobStatus.Disabled ->
                Html.span [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.JobDisabled ]
            | JobStatus.Cancelled ->
                Html.span [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.JobCancelled ]
            | JobStatus.Active -> Html.none
            Html.div [
                Html.button [
                    prop.className [ "text-xs underline"; Tokens.Text.secondary ]
                    prop.ariaExpanded expanded
                    prop.text (if expanded then msgs.HideAttempts else msgs.ShowAttempts)
                    prop.onClick (fun _ -> dispatch (ToggleAttempts job.JobId))
                ]
            ]
            if expanded then
                attemptsList msgs model job
        ]

    let private lastRunCell (msgs: JobsAdminMessages) (model: Model) (job: JobDefinition) =
        let newer =
            match model.Attempts.TryFind job.JobId with
            | Some(Remote.Loaded runs) -> newerThanRecord job runs
            | _ -> None

        Html.div [
            prop.className "text-sm"
            prop.children [
                Html.div [
                    prop.text (job.LastRunAt |> Option.map formatTime |> Option.defaultValue msgs.NeverRun)
                ]
                match newer with
                | Some run ->
                    Html.p [
                        prop.className "mt-1 text-xs text-blue-700"
                        prop.text (msgs.NewerAttempt run.Attempt (formatTime run.StartedAt))
                    ]
                | None -> Html.none
            ]
        ]

    let private statusCell (msgs: JobsAdminMessages) (job: JobDefinition) =
        let standing = standingOf job

        Html.div [
            standingPill msgs standing
            match standing, job.LastRunError with
            | (JobStanding.PermanentlyFailed | JobStanding.FailingWillRetry), Some error ->
                Html.p [
                    prop.className "mt-1 text-xs text-red-700"
                    prop.children [
                        Html.span [ prop.className "font-medium"; prop.text msgs.ReasonLabel ]
                        Html.span [ prop.className "ml-1 font-mono break-all"; prop.text error ]
                    ]
                ]
            | _ -> Html.none
            match warningOf job with
            | Some warning -> warningNote warning
            | None -> Html.none
        ]

    let private jobsTable (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) (jobs: JobDefinition list) =
        if jobs.IsEmpty then
            Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoJobs ]
        else
            Data.table
                [
                    {
                        Header = msgs.ColumnJob
                        Align = Data.Left
                        Cell = jobCell msgs model dispatch
                    }
                    {
                        Header = msgs.ColumnLastRun
                        Align = Data.Left
                        Cell = lastRunCell msgs model
                    }
                    {
                        Header = msgs.ColumnStatus
                        Align = Data.Left
                        Cell = statusCell msgs
                    }
                    {
                        Header = msgs.ColumnActions
                        Align = Data.Left
                        Cell = rerunCell msgs model dispatch
                    }
                ]
                (ordered jobs)

    /// The re-run confirmation, while one is pending.
    let private rerunConfirm (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        match model.Confirming |> Option.bind (findJob model) with
        | Some job ->
            Modal.confirm
                {
                    Title = msgs.RerunButton
                    Confirm = msgs.RerunConfirm
                    Busy = msgs.RerunStarting
                    Cancel = msgs.RerunCancel
                }
                [
                    Html.p [
                        prop.className [ "text-sm"; Tokens.Text.secondary ]
                        prop.text (msgs.RerunConfirmPrompt job.Handler)
                    ]
                ]
                (fun () -> dispatch (RerunConfirmed job.JobId))
                (fun () -> dispatch RerunCancelled)
                false
        | None -> Html.none

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: JobsAdminMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let loading = Remote.isLoading model.Jobs

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.Heading ]
                                Html.p [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.Subheading ]
                            ]
                        ]
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf loading msgs.Refreshing false)
                            msgs.Refresh
                            (fun () -> dispatch Refresh)
                    ]
                ]
                StateViews.notice { Dismiss = msgs.Dismiss } model.LastError (fun () -> dispatch DismissError)
                match model.Jobs with
                | Remote.NotAsked
                | Remote.Loading -> Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.Loading ]
                | Remote.Failed message -> StateViews.noticeStatic (Notice.Problem message)
                | Remote.Reloading jobs
                | Remote.Loaded jobs -> jobsTable msgs model dispatch jobs
                rerunConfirm msgs model dispatch
            ]
        ]

/// The service status board as Feliz (`ServiceStatusBoardUI`'s view before its tree).
module ServiceStatusBoard =
    open ServiceStatusBoardUI

    // ─── Renderers ───────────────────────────────────────────────────────

    let private severityPill (msgs: ServiceStatusBoardMessages) (summary: SectionSummary) =
        let label, cls =
            if summary.Disabled then
                msgs.SeverityDisabled, ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)
            else
                match summary.Severity with
                | StatusSeverity.Ok -> msgs.SeverityOk, "bg-green-100 text-green-700 border-green-200"
                | StatusSeverity.Warn -> msgs.SeverityWarn, "bg-yellow-100 text-yellow-700 border-yellow-200"
                | StatusSeverity.Error -> msgs.SeverityError, "bg-red-100 text-red-700 border-red-200"

        Html.span [
            prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
            prop.text label
        ]

    let private overallPill (msgs: ServiceStatusBoardMessages) (overall: OverallStatus) =
        let label, cls =
            match overall with
            | AllOk -> msgs.AllSystemsOk, "bg-green-100 text-green-700 border-green-200"
            | DegradedBy sections ->
                let joined = sections |> String.concat ", "
                msgs.DegradedBy joined, "bg-yellow-100 text-yellow-700 border-yellow-200"
            | UnhealthyBy sections ->
                let joined = sections |> String.concat ", "
                msgs.UnhealthyBy joined, "bg-red-100 text-red-700 border-red-200"

        Html.span [
            prop.className $"inline-block text-sm px-3 py-1 rounded border font-semibold {cls}"
            prop.text label
        ]

    let private sectionPanel
        (msgs: ServiceStatusBoardMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        (section: string)
        (summary: SectionSummary)
        : ReactElement =
        let isCollapsed = Set.contains section model.Collapsed
        let isRefreshing = Set.contains section model.Refreshing
        let isFlipped = Set.contains section model.RecentlyFlipped

        Html.div [
            prop.className [
                "border border-border rounded-lg overflow-hidden bg-white"
                if isFlipped then
                    "animate-pulse"
            ]
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between gap-3 px-4 py-3 bg-gray-50 border-b border-border"
                    prop.children [
                        Html.button [
                            prop.className "flex items-center gap-2 text-left flex-1"
                            prop.onClick (fun _ -> dispatch (ToggleCollapsed section))
                            prop.children [
                                Html.span [
                                    prop.className [ "text-xs font-mono w-4"; Tokens.Text.muted ]
                                    prop.text (if isCollapsed then "▸" else "▾")
                                ]
                                Html.span [
                                    prop.className [ "text-sm font-semibold"; Tokens.Text.primary ]
                                    prop.text section
                                ]
                                severityPill msgs summary
                            ]
                        ]
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf isRefreshing msgs.Refreshing false)
                            msgs.Refresh
                            (fun () -> dispatch (RefreshSection section))
                    ]
                ]
                if not isCollapsed then
                    Html.div [
                        prop.className "px-4 py-3"
                        prop.children [
                            Html.p [
                                prop.className [ "text-sm"; Tokens.Text.secondary ]
                                prop.text (
                                    if summary.Disabled then
                                        summary.DisabledReason
                                    else
                                        summary.Headline
                                )
                            ]
                            if not summary.Details.IsEmpty then
                                Html.ul [
                                    prop.className [ "mt-2 text-xs list-disc pl-5 space-y-1"; Tokens.Text.secondary ]
                                    prop.children (summary.Details |> List.map (fun d -> Html.li [ prop.text d ]))
                                ]
                        ]
                    ]
            ]
        ]

    // ─── View ────────────────────────────────────────────────────────────

    let private boardView (msgs: ServiceStatusBoardMessages) (model: Model) (dispatch: Msg -> unit) =
        let isLoading = Remote.isLoading model.Snapshot

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.Heading ]
                                Html.p [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.Subheading ]
                            ]
                        ]
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf isLoading msgs.Refreshing false)
                            msgs.RefreshAll
                            (fun () -> dispatch RefreshAll)
                    ]
                ]
                match model.Snapshot with
                | Remote.NotAsked
                | Remote.Loading
                | Remote.Reloading _ ->
                    Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.Loading ]
                | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
                | Remote.Loaded snap ->
                    Html.div [
                        prop.children [
                            Html.div [
                                prop.className
                                    "flex items-center justify-between p-3 mb-4 bg-white border border-border rounded-lg"
                                prop.children [
                                    overallPill msgs snap.Overall
                                    Html.span [
                                        prop.className [ "text-xs"; Tokens.Text.muted ]
                                        prop.text (msgs.GeneratedAt(snap.GeneratedAt.ToString "u"))
                                    ]
                                ]
                            ]
                            Html.div [
                                prop.className "flex flex-col gap-3"
                                prop.children [
                                    sectionPanel msgs model dispatch ServiceStatusSnapshot.HealthSection snap.Health
                                    sectionPanel
                                        msgs
                                        model
                                        dispatch
                                        ServiceStatusSnapshot.PreflightSection
                                        snap.Preflight
                                    sectionPanel msgs model dispatch ServiceStatusSnapshot.DriftSection snap.Drift
                                    sectionPanel
                                        msgs
                                        model
                                        dispatch
                                        ServiceStatusSnapshot.RateLimitSection
                                        snap.RateLimit
                                    sectionPanel msgs model dispatch ServiceStatusSnapshot.JobQueueSection snap.JobQueue
                                    sectionPanel
                                        msgs
                                        model
                                        dispatch
                                        ServiceStatusSnapshot.SmokeTestSection
                                        snap.SmokeTest
                                ]
                            ]
                        ]
                    ]
            ]
        ]

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: ServiceStatusBoardMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [ boardView msgs model dispatch ]
        ]

/// The data-migration admin as Feliz (`MigrationStatusUI`'s view before its tree).
module MigrationStatus =
    open MigrationStatusUI

    // ─── View ───────────────────────────────────────────────────────────

    let private statePill (msgs: MigrationStatusMessages) (state: MigrationRunState) =
        let label, cls =
            match state with
            | MigrationIdle -> msgs.NotYetRun, ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)
            | MigrationInProgress -> msgs.InProgressLabel, "bg-blue-100 text-blue-700 border-blue-200"
            | MigrationComplete -> msgs.UpToDate, "bg-green-100 text-green-700 border-green-200"
            | MigrationCompleteWithFailures ->
                msgs.CompletedWithFailures, "bg-yellow-100 text-yellow-700 border-yellow-200"
            | MigrationChainBlocked _ -> msgs.Blocked, "bg-red-100 text-red-700 border-red-200"

        Html.span [
            prop.className $"inline-block px-2 py-0.5 text-xs rounded border {cls}"
            prop.text label
        ]

    /// The progress sentence the phase asked for, in the module's own
    /// vocabulary: which version pair, and how far through the scope.
    let private progressText
        (msgs: MigrationStatusMessages)
        (info: MigrationDataTypeInfo)
        (status: MigrationStatus option)
        =
        match status with
        | None -> msgs.NoPassRecorded
        | Some s ->
            let done' = s.MigratedObjects + s.AlreadyCurrentObjects

            match s.State with
            | MigrationChainBlocked reason -> reason
            | MigrationIdle -> msgs.NoPassRecorded
            | MigrationInProgress -> msgs.InProgressText info.DisplayName s.TargetVersion done' s.TotalObjects
            | MigrationComplete -> msgs.CompleteText s.TotalObjects s.TargetVersion
            | MigrationCompleteWithFailures ->
                msgs.CompleteWithFailuresText done' s.TotalObjects s.TargetVersion s.FailedObjects

    let private failureRows (msgs: MigrationStatusMessages) (status: MigrationStatus) =
        if status.Failures.IsEmpty then
            Html.p [
                prop.className [ "text-xs"; Tokens.Text.muted ]
                prop.text msgs.NoFailuresRecorded
            ]
        else
            Html.div [
                prop.className "space-y-1"
                prop.children [
                    Html.p [
                        prop.className [ "text-xs"; Tokens.Text.muted ]
                        prop.text (msgs.FailuresSummary(List.length status.Failures))
                    ]
                    Html.ul [
                        prop.className "text-xs font-mono space-y-1"
                        prop.children (
                            status.Failures
                            |> List.map (fun failure ->
                                Html.li [
                                    prop.className "text-red-700"
                                    prop.text (msgs.FailureLine failure.ObjectId failure.AtVersion failure.Error)
                                ])
                        )
                    ]
                ]
            ]

    let private dataTypeCell (info: MigrationDataTypeInfo) =
        Html.div [
            Html.div [ prop.className "text-sm font-medium"; prop.text info.DisplayName ]
            Html.div [
                prop.className [ "text-xs font-mono"; Tokens.Text.muted ]
                prop.text info.DataTypeId
            ]
        ]

    /// The progress sentence, and the recorded failures beneath it while they
    /// are expanded.
    let private progressCell (msgs: MigrationStatusMessages) (model: Model) (info: MigrationDataTypeInfo) =
        let status = model.Statuses.TryFind info.DataTypeId

        Html.div [
            Html.p [
                prop.className [ "text-sm"; Tokens.Text.secondary ]
                prop.text (progressText msgs info status)
            ]
            match status with
            | Some s when model.ExpandedFailures = Some info.DataTypeId ->
                Html.div [
                    prop.className [ "mt-2 p-3 rounded"; Tokens.Bg.subtle ]
                    prop.children [ failureRows msgs s ]
                ]
            | _ -> Html.none
        ]

    let private actionsCell
        (msgs: MigrationStatusMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        (info: MigrationDataTypeInfo)
        =
        let triggering = model.Triggering.Contains info.DataTypeId
        let expanded = model.ExpandedFailures = Some info.DataTypeId

        let failureCount =
            model.Statuses.TryFind info.DataTypeId
            |> Option.map _.FailedObjects
            |> Option.defaultValue 0

        Html.div [
            prop.className "flex items-center justify-end gap-3"
            prop.children [
                match info.ChainProblem with
                | Some problem ->
                    Html.span [
                        prop.className "text-xs text-red-700"
                        prop.title problem
                        prop.text msgs.ChainIncomplete
                    ]
                | None ->
                    Forms.Button.secondaryWith
                        (Forms.Button.stateOf triggering msgs.Migrating false)
                        msgs.MigrateNow
                        (fun () -> dispatch (TriggerClicked info.DataTypeId))
                if failureCount > 0 then
                    Html.button [
                        prop.className "text-xs underline text-red-700"
                        prop.text (
                            if expanded then
                                msgs.HideFailures
                            else
                                msgs.FailuresButton failureCount
                        )
                        prop.onClick (fun _ -> dispatch (ToggleFailures info.DataTypeId))
                    ]
            ]
        ]

    let private dataTypesTable
        (msgs: MigrationStatusMessages)
        (model: Model)
        (dispatch: Msg -> unit)
        (dataTypes: MigrationDataTypeInfo list)
        =
        if dataTypes.IsEmpty then
            Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoDataTypes ]
        else
            Data.table
                [
                    {
                        Header = msgs.ColumnDataType
                        Align = Data.Left
                        Cell = dataTypeCell
                    }
                    {
                        Header = msgs.ColumnDeclared
                        Align = Data.Left
                        Cell = fun info -> Html.text (msgs.DeclaredVersion info.CurrentVersion)
                    }
                    {
                        Header = msgs.ColumnProgress
                        Align = Data.Left
                        Cell = progressCell msgs model
                    }
                    {
                        Header = msgs.ColumnState
                        Align = Data.Left
                        Cell =
                            fun info ->
                                match model.Statuses.TryFind info.DataTypeId with
                                | Some s -> statePill msgs s.State
                                | None -> statePill msgs MigrationIdle
                    }
                    {
                        Header = msgs.ColumnActions
                        Align = Data.Right
                        Cell = actionsCell msgs model dispatch
                    }
                ]
                dataTypes

    /// The module's page, over the catalog section the view hands it.
    let body (msgs: MigrationStatusMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
        let loading = Remote.isLoading model.DataTypes
        let noticeMsgs = { Dismiss = msgs.Dismiss }

        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between mb-4"
                    prop.children [
                        Html.div [
                            prop.children [
                                Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.Heading ]
                                Html.p [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.Subheading ]
                            ]
                        ]
                        Forms.Button.secondaryWith
                            (Forms.Button.stateOf loading msgs.Refreshing false)
                            msgs.Refresh
                            (fun () -> dispatch Refresh)
                    ]
                ]
                match model.LastError with
                | Some _ ->
                    Html.div [
                        prop.className "mb-4"
                        prop.children [
                            StateViews.notice noticeMsgs model.LastError (fun () -> dispatch DismissError)
                        ]
                    ]
                | None -> Html.none
                match model.DataTypes with
                | Remote.NotAsked
                | Remote.Loading
                | Remote.Reloading _ ->
                    Html.p [
                        prop.className [ "text-sm"; Tokens.Text.muted ]
                        prop.text msgs.LoadingDataTypes
                    ]
                | Remote.Failed message ->
                    StateViews.notice noticeMsgs (Some(Notice.Problem message)) (fun () -> dispatch DismissError)
                | Remote.Loaded dataTypes -> dataTypesTable msgs model dispatch dataTypes
            ]
        ]