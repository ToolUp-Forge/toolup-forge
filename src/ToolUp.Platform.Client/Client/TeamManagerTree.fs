// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The team manager as a Fuaran-UI tree, built and drawn in a file of its own.
///
/// `TeamManagerUI` is in the shell's boot closure (the shell reads its
/// definition and `update` at boot), so its view mounts this file through
/// `TreeSurface.deferred`: the node vocabulary, its compute closure and the
/// renderer arrive together the first time the page is shown, and the shell's
/// first load pays nothing for them. Nothing imports this file statically.
///
/// The rows are kept (`Toolup.UIToolkit.Keep`), every row id comes from the
/// row's own identity and every row message carries it. What stays React is an
/// island: the directory typeahead and the transfer filter own a display-state
/// hook, and the publication-readers panel owns its own requests.
module internal TeamManagerTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open TeamManagerUI
open KeepScoped

/// A role-picker row of buttons, the picked role shown as the primary one.
let private roleButtons
    (id: string)
    (msgs: TeamManagerMessages)
    (roles: TeamRole list)
    (selected: TeamRole)
    (onPick: TeamRole -> Msg)
    : Node<Msg> =
    Nodes.Layout.row id [
        Nodes.text (Nodes.Ids.child id "label") msgs.RoleLabel
        for role in roles do
            let name = TeamRoles.displayName role

            Nodes.Actions.button
                (Nodes.Ids.keyed id name)
                (if role = selected then
                     ButtonVariant.Primary
                 else
                     ButtonVariant.Secondary)
                name
                false
                (onPick role)
    ]

/// A notice above the page, dismissed through `update`.
let private notice (id: string) (msgs: TeamManagerMessages) (error: Notice option) (dismiss: Msg) : Node<Msg> list =
    match error with
    | Some(Notice.Problem text) -> [ Nodes.Actions.banner id ToneVariant.Critical text msgs.Dismiss dismiss ]
    | Some(Notice.Info text) -> [ Nodes.Actions.banner id ToneVariant.Info text msgs.Dismiss dismiss ]
    | None -> []

let private failure (id: string) (error: string option) : Node<Msg> list =
    match error with
    | Some text -> [
        Fuaran.callout id {
            Defaults.callout with
                Body = Nodes.literal text
                Tone = ToneVariant.Critical
        }
      ]
    | None -> []

let private modal (id: string) (heading: string) (onDismiss: Msg) (children: Node<Msg> list) : Node<Msg> =
    Fuaran.modal id {
        Defaults.modal with
            Open = Binding.Static(Some true)
            Heading = Some(Nodes.literal heading)
            OnDismiss = Some(Action.dispatch onDismiss)
            Children = children
    }

// ─── My teams ────────────────────────────────────────────────────────

type private TeamsContext = {
    Msgs: TeamManagerMessages
    Active: string option
}

let private teamRows =
    Rows(
        (fun (a: TeamsContext) b -> sameObject a.Msgs b.Msgs && a.Active = b.Active),
        fun context (team: TeamInfo) ->
            let msgs = context.Msgs
            let active = context.Active = Some team.TeamId
            let id = Nodes.Ids.keyed "team" team.TeamId

            Nodes.Layout.row id [
                Nodes.Layout.column (Nodes.Ids.child id "about") [
                    Nodes.text (Nodes.Ids.child id "name") team.Name
                    if active then
                        Nodes.text (Nodes.Ids.child id "active") msgs.ActiveBadge
                    Nodes.text (Nodes.Ids.child id "id") (msgs.TeamIdLabel team.TeamId)
                ]
                Nodes.Layout.row (Nodes.Ids.child id "actions") [
                    if not active then
                        Nodes.Actions.secondary (Nodes.Ids.child id "switch") msgs.Switch (SwitchActiveTeam team.TeamId)
                    Nodes.Actions.secondary (Nodes.Ids.child id "manage") msgs.Manage (SelectTeam team.TeamId)
                ]
            ]
    )

let private myTeams (msgs: TeamManagerMessages) (model: Model) : Node<Msg> =
    Nodes.Panel.card "my-teams" msgs.MyTeamsPanel [
        if model.Teams.IsEmpty then
            Nodes.text "my-teams-none" msgs.NoTeamsYet
        else
            yield!
                teamRows.List
                    {
                        Msgs = msgs
                        Active = model.ActiveTeamId
                    }
                    model.Teams
    ]

// ─── Team details ────────────────────────────────────────────────────

type private MembersContext = {
    Msgs: TeamManagerMessages
    TeamId: string
    SelfId: string
    CallerRole: TeamRole option
    Directory: UserDirectory.Model
}

let private memberRows =
    Rows(
        (fun (a: MembersContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Directory b.Directory
            && a.TeamId = b.TeamId
            && a.SelfId = b.SelfId
            && a.CallerRole = b.CallerRole),
        fun context (membership: TeamMembership) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "member" membership.UserId
            let isSelf = membership.UserId = context.SelfId

            // The caller may act on a membership whose role its own role
            // manages: Owner any non-Owner, Admin Member only.
            let canManageTarget =
                not isSelf
                && (context.CallerRole
                    |> Option.exists (fun r -> TeamRoles.canManageRole r membership.Role))

            let primary, secondary =
                if isSelf then
                    labelFor context.Directory context.SelfId membership.UserId, UserSession.getEmail ()
                else
                    UserDirectory.labels context.Directory membership.UserId

            let roles = assignableRoles context.CallerRole

            Nodes.Layout.row id [
                Nodes.Layout.column (Nodes.Ids.child id "about") [
                    Nodes.text (Nodes.Ids.child id "name") primary
                    match secondary with
                    | Some email -> Nodes.text (Nodes.Ids.child id "email") email
                    | None -> ()
                ]
                if canManageTarget then
                    Nodes.Forms.dropdown
                        (Nodes.Ids.child id "role")
                        msgs.RoleLabel
                        [
                            for role in roles do
                                let name = TeamRoles.displayName role
                                name, name
                        ]
                        (TeamRoles.displayName membership.Role)
                        (fun chosen ->
                            let role =
                                roles
                                |> List.tryFind (fun r -> TeamRoles.displayName r = chosen)
                                |> Option.defaultValue membership.Role

                            ChangeMemberRole(context.TeamId, membership.UserId, role))
                else
                    Nodes.text (Nodes.Ids.child id "role") (TeamRoles.displayName membership.Role)
                if isSelf then
                    Nodes.text (Nodes.Ids.child id "you") msgs.YouSuffix
                if canManageTarget then
                    Nodes.Actions.secondary
                        (Nodes.Ids.child id "remove")
                        msgs.RemoveMember
                        (RemoveMember(context.TeamId, membership.UserId))
            ]
    )

let private addMember
    (msgs: TeamManagerMessages)
    (teamId: string)
    (callerRole: TeamRole option)
    (model: Model)
    (dispatch: Msg -> unit)
    : Node<Msg> =
    Nodes.Panel.card "add-member" msgs.InvitePanel [
        Nodes.text "add-member-help" msgs.InviteHelp
        // The typeahead's input carries only a placeholder, so a label that
        // only assistive technology reads wraps it and names it.
        TreeLowering.island "add-member-user" (fun () ->
            Feliz.Html.label [
                Feliz.prop.className "block"
                Feliz.prop.children [
                    Feliz.Html.span [
                        Feliz.prop.className "sr-only"
                        Feliz.prop.text msgs.InviteIdentifierPlaceholder
                    ]
                    UserDirectoryTypeahead.userTypeahead
                        model.AddMemberUserId
                        (SetAddMemberUserId >> dispatch)
                        msgs.InviteIdentifierPlaceholder
                        UserDirectoryTypeahead.pickEmailPreferred
                ]
            ])
        roleButtons "add-member-role" msgs (assignableRoles callerRole) model.AddMemberRole SetAddMemberRole
        // Inert while a previous add is in flight, so a double click cannot fire two requests.
        Nodes.Actions.button
            "add-member-submit"
            ButtonVariant.Primary
            (if model.AddMemberSubmitting then
                 msgs.Inviting
             else
                 msgs.InviteMember)
            model.AddMemberSubmitting
            (AddMember teamId)
    ]

/// The two-step ownership transfer: step one filters the team's current
/// members (never mints a membership); picking one advances to an explicit
/// confirmation naming both parties before the request fires. Shown only to
/// the team's real Owner, so the candidates never include the caller.
let private transfer
    (msgs: TeamManagerMessages)
    (state: TransferOwnership.Model)
    (model: Model)
    (dispatch: Msg -> unit)
    : Node<Msg> =
    let send msg = TransferMsg msg
    let selfId = selfUserId ()
    let members = model.Members |> Map.tryFind state.TeamId |> Option.defaultValue []
    let teamName = teamNameOf model state.TeamId
    let label = labelFor model.Directory selfId
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
        modal "transfer" msgs.TransferOwnership (send TransferOwnership.Close) [
            Nodes.text "transfer-help" (msgs.TransferOwnershipHelp teamName)
            TreeLowering.island "transfer-filter" (fun () ->
                Forms.Input.text
                    state.Query
                    (TransferOwnership.SetQuery >> send >> dispatch)
                    msgs.TransferFilterPlaceholder)
            if List.isEmpty candidates then
                Nodes.text "transfer-none" msgs.TransferNoOtherMembers
            elif List.isEmpty filtered then
                Nodes.text "transfer-no-match" msgs.TransferNoMatches
            else
                Nodes.Layout.column "transfer-candidates" [
                    for m in filtered do
                        Nodes.Actions.secondary
                            (Nodes.Ids.keyed "transfer-candidate" m.UserId)
                            (label m.UserId + " · " + TeamRoles.displayName m.Role)
                            (send (TransferOwnership.SelectCandidate m.UserId))
                ]
            Nodes.Actions.secondary "transfer-cancel" msgs.Cancel (send TransferOwnership.Close)
        ]
    | TransferOwnership.ConfirmTransfer ->
        let incoming =
            state.SelectedUserId
            |> Option.map label
            |> Option.defaultValue msgs.TransferSelectedMemberFallback

        modal "transfer" msgs.TransferConfirmHeading (send TransferOwnership.Close) [
            Nodes.text "transfer-prompt" (msgs.TransferConfirmPrompt teamName (label selfId) incoming)
            Nodes.text "transfer-role" (msgs.TransferRoleExplanation incoming)
            yield! failure "transfer-error" state.SubmitError
            Nodes.Layout.row "transfer-actions" [
                Nodes.Actions.secondary "transfer-back" msgs.Back (send TransferOwnership.BackToPick)
                Nodes.Actions.button
                    "transfer-confirm"
                    ButtonVariant.Primary
                    (if state.Submitting then
                         msgs.Transferring
                     else
                         msgs.ConfirmTransfer)
                    state.Submitting
                    (send TransferOwnership.Submit)
            ]
        ]

let private teamDetails (msgs: TeamManagerMessages) (teamId: string) (model: Model) (dispatch: Msg -> unit) =
    let members = model.Members |> Map.tryFind teamId |> Option.defaultValue []
    let callerRole = effectiveCallerRole model teamId

    let canManage =
        callerRole |> Option.map TeamRoles.canManageMembers |> Option.defaultValue false

    // Transfer is the real Owner's alone: unlike the other controls it does
    // not take the platform-admin bypass, because the server gates on the
    // caller's own membership role.
    let isRealOwner = (model.RoleInTeam |> Map.tryFind teamId) = Some Owner

    Nodes.Layout.column "team-details" [
        Nodes.Layout.row "team-details-crumbs" [
            Nodes.Actions.button "team-details-back" ButtonVariant.Tertiary msgs.BreadcrumbMyTeams false BackToMyTeams
            Nodes.text "team-details-team" (teamNameOf model teamId)
            if canManage then
                Nodes.Layout.row "team-details-actions" [
                    if isRealOwner then
                        Nodes.Actions.secondary
                            "team-details-transfer"
                            msgs.TransferOwnership
                            (OpenTransferOwnership teamId)
                    Nodes.Actions.secondary "team-details-pending" msgs.PendingInvites (NavigatePendingInvites teamId)
                ]
        ]
        Nodes.Panel.card "members" msgs.MembersPanel [
            if members.IsEmpty then
                Nodes.text "members-none" msgs.NoMembers
            else
                yield!
                    memberRows.List
                        {
                            Msgs = msgs
                            TeamId = teamId
                            SelfId = selfUserId ()
                            CallerRole = callerRole
                            Directory = model.Directory
                        }
                        members
        ]
        if canManage then
            addMember msgs teamId callerRole model dispatch
        // The team's in-platform publication readers, for its real Owner or
        // Admin (no platform-admin bypass, as on the server).
        TreeLowering.island "publication-readers" (fun () ->
            PublicationReadersAdminUI.panel
                msgs
                teamId
                model.ActiveTeamId
                (model.RoleInTeam
                 |> Map.tryFind teamId
                 |> Option.exists TeamRoles.canWriteTeamConfig))
        match model.Transfer with
        | Some state when state.TeamId = teamId -> transfer msgs state model dispatch
        | _ -> ()
    ]

// ─── Pending invites ─────────────────────────────────────────────────

type private InvitesContext = {
    Msgs: TeamManagerMessages
    TeamId: string
}

let private pendingRows =
    Rows(
        (fun (a: InvitesContext) b -> sameObject a.Msgs b.Msgs && a.TeamId = b.TeamId),
        fun context (email: string, entry: PendingInviteByEmail) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "pending" email

            Nodes.Layout.row id [
                Nodes.Layout.column (Nodes.Ids.child id "about") [
                    Nodes.text (Nodes.Ids.child id "email") email
                    Nodes.text
                        (Nodes.Ids.child id "expires")
                        (msgs.InviteExpires (TeamRoles.displayName entry.Role) (entry.ExpiresAt.ToString "yyyy-MM-dd"))
                ]
                Nodes.Actions.secondary
                    (Nodes.Ids.child id "revoke")
                    msgs.RevokeInvite
                    (OpenRevokeByEmail(context.TeamId, email))
            ]
    )

type private ExpiredContext = {
    Msgs: TeamManagerMessages
    TeamId: string
    InFlight: Set<string * string>
}

/// A recently expired invite with its one-click re-issue, which re-sends the
/// original role with the default lifetime; the row then moves back to the
/// live list.
let private expiredRows =
    Rows(
        (fun (a: ExpiredContext) b ->
            sameObject a.Msgs b.Msgs
            && a.TeamId = b.TeamId
            && sameObject a.InFlight b.InFlight),
        fun context (entry: TeamInviteExpiredPayload) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "expired" entry.InviteeEmail

            let inFlight = context.InFlight |> Set.contains (context.TeamId, entry.InviteeEmail)

            Nodes.Layout.row id [
                Nodes.Layout.column (Nodes.Ids.child id "about") [
                    Nodes.text (Nodes.Ids.child id "email") entry.InviteeEmail
                    Nodes.text (Nodes.Ids.child id "badge") msgs.Expired
                    Nodes.text
                        (Nodes.Ids.child id "when")
                        (msgs.InviteExpired (TeamRoles.displayName entry.Role) (entry.ExpiredAt.ToString "yyyy-MM-dd"))
                ]
                Nodes.Actions.button
                    (Nodes.Ids.child id "reissue")
                    ButtonVariant.Secondary
                    (if inFlight then msgs.Reissuing else msgs.Reissue)
                    inFlight
                    (ReissueExpiredInvite(context.TeamId, entry.InviteeEmail, entry.Role))
            ]
    )

let private issueByEmail
    (msgs: TeamManagerMessages)
    (callerRole: TeamRole option)
    (state: IssueByEmail.Model)
    : Node<Msg> =
    let send msg = IssueByEmailMsg msg

    modal "issue-by-email" msgs.InviteByEmailHeading (send IssueByEmail.Close) [
        Nodes.text "issue-by-email-help" msgs.InviteByEmailHelp
        // `Owner` is never offered: initial ownership is set when the team
        // is created. `Admin` is offered to callers who may assign it.
        roleButtons "issue-by-email-role" msgs (assignableRoles callerRole) state.Role (IssueByEmail.SetRole >> send)
        Nodes.Forms.form
            "issue-by-email-form"
            (if state.Submitting then
                 msgs.Issuing
             else
                 msgs.IssueInvitation)
            state.Submitting
            (send IssueByEmail.Submit)
            [
                Nodes.Forms.text "email" msgs.EmailPlaceholder state.Email (IssueByEmail.SetEmail >> send)
                |> Nodes.Forms.required
                Nodes.Forms.number "expires" msgs.ExpiresInDays (float state.ExpiresInDays) (fun days ->
                    send (IssueByEmail.SetExpiresInDays(int days)))
            ]
        yield! failure "issue-by-email-error" state.SubmitError
        Nodes.Actions.secondary "issue-by-email-cancel" msgs.Cancel (send IssueByEmail.Close)
    ]

let private revokeByEmail (msgs: TeamManagerMessages) (state: RevokeByEmail.Model) (model: Model) : Node<Msg> =
    let send msg = RevokeByEmailMsg msg

    modal "revoke-by-email" msgs.RevokeInviteHeading (send RevokeByEmail.Cancel) [
        Nodes.text "revoke-by-email-prompt" (msgs.RevokeInvitePrompt state.Email (teamNameOf model state.TeamId))
        Nodes.Layout.row "revoke-by-email-actions" [
            Nodes.Actions.secondary "revoke-by-email-cancel" msgs.Cancel (send RevokeByEmail.Cancel)
            // The catalog has no "Revoking…" string: the confirm label doubles as the busy label.
            Nodes.Actions.button
                "revoke-by-email-confirm"
                ButtonVariant.Destructive
                msgs.RevokeInvite
                state.Submitting
                (send RevokeByEmail.Confirm)
        ]
    ]

let private pendingInvites (msgs: TeamManagerMessages) (teamId: string) (model: Model) : Node<Msg> =
    let callerRole = effectiveCallerRole model teamId

    Nodes.Layout.column "pending" [
        Nodes.Layout.row "pending-crumbs" [
            Nodes.Actions.button "pending-back" ButtonVariant.Tertiary msgs.BreadcrumbMembers false (SelectTeam teamId)
            Nodes.text "pending-team" (teamNameOf model teamId)
            Nodes.text "pending-page" msgs.PendingInvites
        ]
        Nodes.Panel.card "pending-invites" msgs.PendingInvitesPanel [
            Nodes.Actions.primary "pending-invite" msgs.InviteByEmail (OpenIssueByEmail teamId)
            match Map.tryFind teamId model.PendingByEmail with
            | None -> Nodes.text "pending-loading" msgs.PendingInvitesLoading
            | Some [] -> Nodes.text "pending-none" msgs.NoPendingInvites
            | Some rows -> yield! pendingRows.List { Msgs = msgs; TeamId = teamId } rows
            // The last 30 days' expired invites, read from the audit trail;
            // an empty history is not worth a heading.
            match model.ExpiredInvites |> Map.tryFind teamId with
            | Some expired when not (List.isEmpty expired) ->
                Nodes.heading "pending-expired" 3 msgs.RecentlyExpired

                yield!
                    expiredRows.List
                        {
                            Msgs = msgs
                            TeamId = teamId
                            InFlight = model.ReissueInFlight
                        }
                        expired
            | _ -> ()
        ]
        match model.IssueByEmail with
        | Some state when state.TeamId = teamId -> issueByEmail msgs callerRole state
        | _ -> ()
        match model.RevokeByEmail with
        | Some state when state.TeamId = teamId -> revokeByEmail msgs state model
        | _ -> ()
    ]

/// The page: the notice above the current sub-view. `dispatch` reaches the
/// islands only; every node sends its message through the tree.
let tree (msgs: TeamManagerMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "team-manager" [
        yield! notice "team-manager-notice" msgs model.Error DismissError
        match model.CurrentView with
        | MyTeams -> myTeams msgs model
        | TeamDetails teamId -> teamDetails msgs teamId model dispatch
        | PendingInvites teamId -> pendingInvites msgs teamId model
    ]

/// The deferred component `TeamManagerUI`'s view mounts: the tree built and
/// drawn in one render, through the renderer directly.
[<Feliz.ReactComponent>]
let Surface (props: TeamManagerUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )