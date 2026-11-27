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