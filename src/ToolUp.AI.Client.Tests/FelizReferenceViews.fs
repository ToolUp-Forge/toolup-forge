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