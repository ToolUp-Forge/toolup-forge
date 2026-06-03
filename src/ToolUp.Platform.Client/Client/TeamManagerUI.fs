// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TeamManagerUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Model ───────────────────────────────────────────────────────────

/// Which sub-view is currently active inside the team manager page.
type View =
    | MyTeams
    | TeamDetails of teamId: string
    /// Phase 3d.A — pending-invite admin surface for one team.
    /// Currently exposes only the "Pending email invites" sub-tab;
    /// link-based invitation UI lands as a sibling sub-tab when that
    /// follow-on phase ships.
    | PendingInvites of teamId: string

/// Phase 3d.A — open-modal state for the "Invite by email (no link)"
/// admin affordance. `None` means the modal is closed; opening seeds
/// it with the team-id context + default role + default expiry.
type IssueByEmailModalState = {
    TeamId: string
    Email: string
    Role: TeamRole
    /// Lifetime in days — `IssuePendingInviteByEmail` expects a
    /// `TimeSpan option`; we surface days because that's how the
    /// existing share-token expiry UX talks. `None` falls through to
    /// `TeamInviteTypes.DefaultExpiry` (7 days) server-side; this
    /// model defaults to `Some 7` so the displayed value matches the
    /// applied value.
    ExpiresInDays: int
    /// Inline error from the most recent submit attempt — surfaces
    /// the typed handler `Error` so the operator can fix and retry
    /// without losing the form contents.
    SubmitError: string option
    /// Disable the submit button while a call is in flight; prevents
    /// double-submit at the cost of an extra Model field.
    Submitting: bool
}

type Model = {
    /// Teams the signed-in user belongs to. Loaded on init.
    Teams: TeamInfo list
    /// Currently-active team (drives scope for other modules).
    ActiveTeamId: string option
    /// Current sub-view (list of teams vs. a specific team's detail).
    CurrentView: View
    /// Members of each team we've looked at, keyed by teamId.
    Members: Map<string, TeamMembership list>
    /// The caller's role in each team they belong to. Drives the
    /// Admin-only UI (Add / Remove / Role change).
    RoleInTeam: Map<string, TeamRole>
    /// A single error message bubble at the bottom of the page. The
    /// whole page dismisses on action — simpler than per-form error
    /// state for an admin UI.
    Error: string option
    /// New-team-name input for the create form.
    NewTeamName: string
    /// Add-member form state for a given teamId.
    AddMemberUserId: string
    AddMemberRole: TeamRole
    /// Shell-supplied callback fired when the active team changes
    /// (`ActiveTeamSwitched(_, Ok())` or `TeamCreated(Ok _)`).
    /// Populated from `ClientModuleContext.OnTeamSwitched` at init —
    /// `Some` in `Team` / `MultiTeam` modes, `None` otherwise. The
    /// shell uses this to run the global `TeamSwitched` reset
    /// (re-fetch configs / flags / RBAC, re-init the active module)
    /// so the rest of the UI swaps to the new team's data.
    OnTeamSwitched: (string -> unit) option
    /// Phase 5f — the deployment's team-creation policy. `None`
    /// while loading; `Some _` once the server has responded. Drives
    /// whether the Create form renders for non-admin callers.
    Policy: TeamCreationPolicy option
    /// Phase 5f — whether the signed-in caller holds
    /// `PlatformRole.PlatformAdmin`. `None` while loading; `Some _`
    /// once the server has responded. Pairs with `Policy` to decide
    /// the form gate — `PlatformAdminOnly` + non-admin hides Create.
    IsPlatformAdmin: bool option
    /// Phase 3d.A — pending-by-email entries per team, populated on
    /// entry to the `PendingInvites` view. Keyed by teamId; the value
    /// is the `(email, entry)` pair list returned by
    /// `ListPendingInvitesByEmail`. Empty list = empty-state UI; the
    /// `None` of `Map.tryFind` means "not loaded yet".
    PendingByEmail: Map<string, (string * PendingInviteByEmail) list>
    /// Phase 3d.A — issue-by-email modal state. `None` = closed.
    IssueByEmailModal: IssueByEmailModalState option
    /// Phase 3d.A — revoke confirmation modal. `None` = closed;
    /// `Some (teamId, email)` = a confirmation dialog is open for
    /// that team's pending entry.
    RevokeByEmailConfirm: (string * string) option
}

type Msg =
    | LoadTeams
    | TeamsLoaded of TeamInfo list
    | LoadActiveTeam
    | ActiveTeamLoaded of string option
    | LoadMembers of teamId: string
    | MembersLoaded of teamId: string * TeamMembership list
    | SelectTeam of teamId: string
    | BackToMyTeams
    | SwitchActiveTeam of teamId: string
    | ActiveTeamSwitched of teamId: string * Result<unit, string>
    | CreateTeam
    | SetNewTeamName of string
    | TeamCreated of Result<TeamInfo, string>
    | SetAddMemberUserId of string
    | SetAddMemberRole of TeamRole
    | AddMember of teamId: string
    | MemberAdded of Result<unit, string>
    | RemoveMember of teamId: string * userId: string
    | MemberRemoved of Result<unit, string>
    | ChangeMemberRole of teamId: string * userId: string * newRole: TeamRole
    | MemberRoleChanged of Result<unit, string>
    | PolicyLoaded of TeamCreationPolicy
    | IsPlatformAdminLoaded of bool
    // ─── Phase 3d.A — pending-invite admin surface ────────────────
    | NavigatePendingInvites of teamId: string
    | LoadPendingByEmail of teamId: string
    | PendingByEmailLoaded of teamId: string * (string * PendingInviteByEmail) list
    | OpenIssueByEmailModal of teamId: string
    | CloseIssueByEmailModal
    | SetIssueByEmailEmail of string
    | SetIssueByEmailRole of TeamRole
    | SetIssueByEmailExpiresInDays of int
    | SubmitIssueByEmail
    | IssueByEmailSubmitted of teamId: string * Result<unit, string>
    | OpenRevokeByEmailConfirm of teamId: string * email: string
    | CancelRevokeByEmail
    | ConfirmRevokeByEmail
    | RevokeByEmailDone of teamId: string * email: string * Result<unit, string>
    | ApiError of string
    | DismissError

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private teamApi: TeamApi =
    Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

// Phase 5f — `PlatformAdminApi.IsPlatformAdmin` drives the Create-form gate.
let private platformAdminApi: PlatformAdminApi =
    Api.makeProxy<PlatformAdminApi> (customOptions = UserSession.withRequestHeaders)

// Phase 3d.A — `ITeamInviteApi.{Issue,List,Revoke}PendingInviteByEmail`
// drive the Pending Invites view. Routes live under
// `/api/team-invite/*` per `TeamInviteApi.routeBuilder`.
let private inviteApi: ITeamInviteApi =
    Api.makeProxy<ITeamInviteApi> (
        routeBuilder = TeamInviteApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Init ────────────────────────────────────────────────────────────

let init (ctx: ClientModuleContext) =
    let model = {
        Teams = []
        ActiveTeamId = None
        CurrentView = MyTeams
        Members = Map.empty
        RoleInTeam = Map.empty
        Error = None
        NewTeamName = ""
        AddMemberUserId = ""
        AddMemberRole = Member
        OnTeamSwitched = ctx.OnTeamSwitched
        Policy = None
        IsPlatformAdmin = None
        PendingByEmail = Map.empty
        IssueByEmailModal = None
        RevokeByEmailConfirm = None
    }

    let loadTeams =
        Cmd.OfRemoting.call teamApi.GetMyTeams () TeamsLoaded (fun e -> ApiError e.Message)

    let loadActive =
        Cmd.OfRemoting.call teamApi.GetActiveTeam () ActiveTeamLoaded (fun e -> ApiError e.Message)

    // Phase 5f — fetch the deployment policy + the caller's admin
    // status in parallel so the Create form can decide whether to
    // render. Both are pure reads; failures fall through to
    // `ApiError` and leave the gate closed (Policy / IsPlatformAdmin
    // = None → form hidden — fail-closed UX, matching the server
    // gate's fail-closed posture).
    let loadPolicy =
        Cmd.OfRemoting.call teamApi.GetTeamCreationPolicy () PolicyLoaded (fun e -> ApiError e.Message)

    let loadAdminStatus =
        Cmd.OfRemoting.call platformAdminApi.IsPlatformAdmin () IsPlatformAdminLoaded (fun e -> ApiError e.Message)

    model, Cmd.batch [ loadTeams; loadActive; loadPolicy; loadAdminStatus ]

// ─── Helpers ─────────────────────────────────────────────────────────

/// Pull the caller's role in each team from the members lists as they
/// stream in. Used to gate Admin-only UI.
let private roleFor (userId: string) (members: TeamMembership list) =
    members |> List.tryFind (fun m -> m.UserId = userId) |> Option.map _.Role

let private selfUserId () = UserSession.getUserId ()

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | LoadTeams -> model, Cmd.OfRemoting.call teamApi.GetMyTeams () TeamsLoaded (fun e -> ApiError e.Message)

    | TeamsLoaded teams ->
        let cmds =
            teams
            |> List.map (fun t ->
                Cmd.OfRemoting.call
                    teamApi.GetTeamMembers
                    t.TeamId
                    (fun members -> MembersLoaded(t.TeamId, members))
                    (fun e -> ApiError e.Message))

        { model with Teams = teams }, Cmd.batch cmds

    | LoadActiveTeam ->
        model, Cmd.OfRemoting.call teamApi.GetActiveTeam () ActiveTeamLoaded (fun e -> ApiError e.Message)

    | ActiveTeamLoaded t -> { model with ActiveTeamId = t }, Cmd.none

    | LoadMembers teamId ->
        model,
        Cmd.OfRemoting.call teamApi.GetTeamMembers teamId (fun members -> MembersLoaded(teamId, members)) (fun e ->
            ApiError e.Message)

    | MembersLoaded(teamId, members) ->
        let selfRole = roleFor (selfUserId ()) members

        let roleMap =
            match selfRole with
            | Some r -> model.RoleInTeam |> Map.add teamId r
            | None -> model.RoleInTeam

        {
            model with
                Members = model.Members |> Map.add teamId members
                RoleInTeam = roleMap
        },
        Cmd.none

    | SelectTeam teamId ->
        {
            model with
                CurrentView = TeamDetails teamId
        },
        Cmd.ofMsg (LoadMembers teamId)

    | BackToMyTeams -> { model with CurrentView = MyTeams }, Cmd.none

    | SwitchActiveTeam teamId ->
        model,
        Cmd.OfRemoting.call teamApi.SetActiveTeam teamId (fun r -> ActiveTeamSwitched(teamId, r)) (fun e ->
            ApiError e.Message)

    | ActiveTeamSwitched(teamId, Ok()) ->
        // Notify the shell so it re-inits every module against the
        // new team's data. `OnTeamSwitched = None` (non-team-scoped
        // mode, or shell didn't wire it) → no-op, server-side switch
        // still persisted but the UI keeps the previous view's data.
        model.OnTeamSwitched |> Option.iter (fun f -> f teamId)

        {
            model with
                ActiveTeamId = Some teamId
                Error = None
        },
        Cmd.none

    | ActiveTeamSwitched(_, Error e) -> { model with Error = Some e }, Cmd.none

    | CreateTeam ->
        if model.NewTeamName.Trim() = "" then
            {
                model with
                    Error = Some "Team name can't be empty"
            },
            Cmd.none
        else
            model,
            Cmd.OfRemoting.call teamApi.CreateTeam (model.NewTeamName.Trim()) TeamCreated (fun e -> ApiError e.Message)

    | SetNewTeamName name -> { model with NewTeamName = name }, Cmd.none

    | TeamCreated(Ok team) ->
        // Creator is automatically Owner and SetActiveTeam'd to this team
        // (server-side `CreateTeam` handles both). Notify the shell so
        // every module re-inits against the new team's (empty) data —
        // matching the `ActiveTeamSwitched` path.
        model.OnTeamSwitched |> Option.iter (fun f -> f team.TeamId)

        {
            model with
                NewTeamName = ""
                ActiveTeamId = Some team.TeamId
                Error = None
        },
        Cmd.ofMsg LoadTeams

    | TeamCreated(Error e) -> { model with Error = Some e }, Cmd.none

    | SetAddMemberUserId id -> { model with AddMemberUserId = id }, Cmd.none

    | SetAddMemberRole role -> { model with AddMemberRole = role }, Cmd.none

    | AddMember teamId ->
        if model.AddMemberUserId.Trim() = "" then
            {
                model with
                    Error = Some "User ID can't be empty"
            },
            Cmd.none
        else
            model,
            Cmd.OfRemoting.call
                teamApi.AddTeamMember
                (teamId, model.AddMemberUserId.Trim(), model.AddMemberRole)
                MemberAdded
                (fun e -> ApiError e.Message)

    | MemberAdded(Ok()) ->
        let refresh =
            match model.CurrentView with
            | TeamDetails teamId -> Cmd.ofMsg (LoadMembers teamId)
            | _ -> Cmd.none

        {
            model with
                AddMemberUserId = ""
                Error = None
        },
        refresh

    | MemberAdded(Error e) -> { model with Error = Some e }, Cmd.none

    | RemoveMember(teamId, userId) ->
        model, Cmd.OfRemoting.call teamApi.RemoveTeamMember (teamId, userId) MemberRemoved (fun e -> ApiError e.Message)

    | MemberRemoved(Ok()) ->
        let refresh =
            match model.CurrentView with
            | TeamDetails teamId -> Cmd.ofMsg (LoadMembers teamId)
            | _ -> Cmd.none

        { model with Error = None }, refresh

    | MemberRemoved(Error e) -> { model with Error = Some e }, Cmd.none

    | ChangeMemberRole(teamId, targetUserId, newRole) ->
        model,
        Cmd.OfRemoting.call teamApi.ChangeMemberRole (teamId, targetUserId, newRole) MemberRoleChanged (fun e ->
            ApiError e.Message)

    | MemberRoleChanged(Ok()) ->
        let refresh =
            match model.CurrentView with
            | TeamDetails teamId -> Cmd.ofMsg (LoadMembers teamId)
            | _ -> Cmd.none

        { model with Error = None }, refresh

    | MemberRoleChanged(Error e) -> { model with Error = Some e }, Cmd.none

    | PolicyLoaded policy -> { model with Policy = Some policy }, Cmd.none

    | IsPlatformAdminLoaded isAdmin ->
        {
            model with
                IsPlatformAdmin = Some isAdmin
        },
        Cmd.none

    // ─── Phase 3d.A — pending-invite admin surface ────────────────

    | NavigatePendingInvites teamId ->
        {
            model with
                CurrentView = PendingInvites teamId
        },
        Cmd.ofMsg (LoadPendingByEmail teamId)

    | LoadPendingByEmail teamId ->
        let onLoad (result: Result<(string * PendingInviteByEmail) list, string>) =
            match result with
            | Ok entries -> PendingByEmailLoaded(teamId, entries)
            | Error e -> ApiError e

        model, Cmd.OfRemoting.call inviteApi.ListPendingInvitesByEmail teamId onLoad (fun e -> ApiError e.Message)

    | PendingByEmailLoaded(teamId, entries) ->
        {
            model with
                PendingByEmail = model.PendingByEmail |> Map.add teamId entries
        },
        Cmd.none

    | OpenIssueByEmailModal teamId ->
        {
            model with
                IssueByEmailModal =
                    Some {
                        TeamId = teamId
                        Email = ""
                        Role = Member
                        ExpiresInDays = 7
                        SubmitError = None
                        Submitting = false
                    }
        },
        Cmd.none

    | CloseIssueByEmailModal -> { model with IssueByEmailModal = None }, Cmd.none

    | SetIssueByEmailEmail email ->
        {
            model with
                IssueByEmailModal = model.IssueByEmailModal |> Option.map (fun m -> { m with Email = email })
        },
        Cmd.none

    | SetIssueByEmailRole role ->
        {
            model with
                IssueByEmailModal = model.IssueByEmailModal |> Option.map (fun m -> { m with Role = role })
        },
        Cmd.none

    | SetIssueByEmailExpiresInDays days ->
        let clamped = max 1 days

        {
            model with
                IssueByEmailModal =
                    model.IssueByEmailModal
                    |> Option.map (fun m -> { m with ExpiresInDays = clamped })
        },
        Cmd.none

    | SubmitIssueByEmail ->
        match model.IssueByEmailModal with
        | None -> model, Cmd.none
        | Some m when m.Email.Trim() = "" ->
            {
                model with
                    IssueByEmailModal =
                        Some {
                            m with
                                SubmitError = Some "Email can't be empty"
                        }
            },
            Cmd.none
        | Some m ->
            let request = {
                TeamId = m.TeamId
                Email = m.Email.Trim()
                Role = m.Role
                ExpiresIn = Some(TimeSpan.FromDays(float m.ExpiresInDays))
            }

            {
                model with
                    IssueByEmailModal =
                        Some {
                            m with
                                Submitting = true
                                SubmitError = None
                        }
            },
            Cmd.OfRemoting.call
                inviteApi.IssuePendingInviteByEmail
                request
                (fun r -> IssueByEmailSubmitted(m.TeamId, r))
                (fun e -> IssueByEmailSubmitted(m.TeamId, Error e.Message))

    | IssueByEmailSubmitted(teamId, Ok()) ->
        // Modal closes on success; refresh the list so the new
        // entry appears.
        { model with IssueByEmailModal = None }, Cmd.ofMsg (LoadPendingByEmail teamId)

    | IssueByEmailSubmitted(_, Error e) ->
        {
            model with
                IssueByEmailModal =
                    model.IssueByEmailModal
                    |> Option.map (fun m -> {
                        m with
                            Submitting = false
                            SubmitError = Some e
                    })
        },
        Cmd.none

    | OpenRevokeByEmailConfirm(teamId, email) ->
        {
            model with
                RevokeByEmailConfirm = Some(teamId, email)
        },
        Cmd.none

    | CancelRevokeByEmail ->
        {
            model with
                RevokeByEmailConfirm = None
        },
        Cmd.none

    | ConfirmRevokeByEmail ->
        match model.RevokeByEmailConfirm with
        | None -> model, Cmd.none
        | Some(teamId, email) ->
            {
                model with
                    RevokeByEmailConfirm = None
            },
            Cmd.OfRemoting.call
                inviteApi.RevokePendingInviteByEmail
                email
                (fun r -> RevokeByEmailDone(teamId, email, r))
                (fun e -> RevokeByEmailDone(teamId, email, Error e.Message))

    | RevokeByEmailDone(teamId, _, Ok()) -> model, Cmd.ofMsg (LoadPendingByEmail teamId)

    | RevokeByEmailDone(_, _, Error e) -> { model with Error = Some e }, Cmd.none

    | ApiError message -> { model with Error = Some message }, Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View ────────────────────────────────────────────────────────────

let private teamRow (team: TeamInfo) (isActive: bool) (onSelect: unit -> unit) (onSwitch: unit -> unit) =
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
                                    prop.text "active"
                                ]
                        ]
                    ]
                    Html.span [ prop.className "text-xs text-muted"; prop.text $"Team ID: {team.TeamId}" ]
                ]
            ]
            Html.div [
                prop.className "flex gap-2"
                prop.children [
                    if not isActive then
                        Forms.Button.secondary "Switch" onSwitch
                    Forms.Button.secondary "Manage" onSelect
                ]
            ]
        ]
    ]

let private createTeamForm (model: Model) (dispatch: Msg -> unit) =
    Layout.Panel.panel "Create a new team" [
        Html.div [
            prop.className "flex items-end gap-3"
            prop.children [
                Html.div [
                    prop.className "flex-1"
                    prop.children [
                        Forms.Input.text model.NewTeamName (fun name -> dispatch (SetNewTeamName name)) "Team name"
                    ]
                ]
                Forms.Button.primary "Create" (fun () -> dispatch CreateTeam)
            ]
        ]
    ]

/// Phase 5f — should the Create-team affordance render? Hidden when
/// `Policy = PlatformAdminOnly` and the caller is known to not be a
/// Platform Admin. Also hidden while either probe is still loading,
/// so a non-admin doesn't briefly see a form that's about to vanish.
/// Server-side `CreateTeam` is the real enforcement; this is UX-only.
let private canShowCreateForm (model: Model) : bool =
    match model.Policy, model.IsPlatformAdmin with
    | Some AnyAuthenticatedUser, _ -> true
    | Some PlatformAdminOnly, Some true -> true
    | _ -> false

let private noTeamsEmptyText (model: Model) =
    match model.Policy, model.IsPlatformAdmin with
    | Some PlatformAdminOnly, Some false ->
        "You're not a member of any team yet. Ask a Platform Admin to add you to one — this deployment doesn't allow non-admin team creation."
    | _ -> "You're not a member of any team yet. Create one below to get started."

let private myTeamsView (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "space-y-4"
        prop.children [
            Layout.Panel.panel "My teams" [
                if model.Teams.IsEmpty then
                    Html.p [ prop.className "text-sm text-muted py-4"; prop.text (noTeamsEmptyText model) ]
                else
                    Html.div [
                        for team in model.Teams do
                            teamRow
                                team
                                (model.ActiveTeamId = Some team.TeamId)
                                (fun () -> dispatch (SelectTeam team.TeamId))
                                (fun () -> dispatch (SwitchActiveTeam team.TeamId))
                    ]
            ]
            if canShowCreateForm model then
                createTeamForm model dispatch
        ]
    ]

let private roleOfString =
    function
    | "Owner" -> Owner
    | "Admin" -> Admin
    | _ -> Member

let private memberRow
    (teamId: string)
    (selfId: string)
    (canManage: bool)
    (membership: TeamMembership)
    (dispatch: Msg -> unit)
    =
    let isSelf = membership.UserId = selfId

    let roleBadge =
        Html.span [
            prop.className "text-xs text-muted px-2 py-0.5 rounded bg-gray-100"
            prop.text (TeamRoles.displayName membership.Role)
        ]

    let roleDropdown =
        Html.select [
            prop.className [
                "text-xs border border-border rounded px-2 py-1"
                "focus:outline-none focus:border-brand"
            ]
            prop.value (TeamRoles.displayName membership.Role)
            prop.onChange (fun (v: string) ->
                let newRole = roleOfString v

                if newRole <> membership.Role then
                    dispatch (ChangeMemberRole(teamId, membership.UserId, newRole)))
            prop.children [
                for role in [ Member; Admin; Owner ] do
                    Html.option [
                        prop.value (TeamRoles.displayName role)
                        prop.text (TeamRoles.displayName role)
                    ]
            ]
        ]

    // 0.5.6 — render the current user's display name + email when the
    // JWT carried `name` / `email` (or `preferred_username`) claims.
    // Other members fall back to the raw UserId — a full per-member
    // profile-lookup substrate is tracked separately (would require
    // an `IUserProfileStore` server-side companion that mirrors Entra
    // Graph or equivalent for the active deployment).
    let primaryLabel, secondaryLabel =
        if isSelf then
            let name = UserSession.getDisplayName () |> Option.defaultValue membership.UserId

            let email = UserSession.getEmail ()
            name, email
        else
            membership.UserId, None

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
                    if canManage && not isSelf then roleDropdown else roleBadge
                    if isSelf then
                        Html.span [ prop.className "text-xs text-brand font-medium"; prop.text "(you)" ]
                ]
            ]
            if canManage && not isSelf then
                Forms.Button.secondary "Remove" (fun () -> dispatch (RemoveMember(teamId, membership.UserId)))
        ]
    ]

let private addMemberForm (teamId: string) (model: Model) (dispatch: Msg -> unit) =
    Layout.Panel.panel "Add a member" [
        Html.div [
            prop.className "flex flex-col gap-3"
            prop.children [
                Forms.Input.text
                    model.AddMemberUserId
                    (fun v -> dispatch (SetAddMemberUserId v))
                    "User ID (e.g. email or identity-provider `sub` claim)"

                Html.div [
                    prop.className "flex items-center gap-3"
                    prop.children [
                        Html.span [ prop.className "text-sm text-muted"; prop.text "Role:" ]
                        for role in [ Member; Admin; Owner ] do
                            Html.button [
                                prop.className [
                                    "px-3 py-1 rounded text-sm transition-colors"
                                    if model.AddMemberRole = role then
                                        "bg-brand text-brand-text"
                                    else
                                        "bg-gray-100 text-text hover:bg-gray-200"
                                ]
                                prop.text (TeamRoles.displayName role)
                                prop.onClick (fun _ -> dispatch (SetAddMemberRole role))
                            ]
                    ]
                ]

                Html.div [ Forms.Button.primary "Add member" (fun () -> dispatch (AddMember teamId)) ]
            ]
        ]
    ]

let private teamDetailsView (teamId: string) (model: Model) (dispatch: Msg -> unit) =
    let teamOpt = model.Teams |> List.tryFind (fun t -> t.TeamId = teamId)
    let members = model.Members |> Map.tryFind teamId |> Option.defaultValue []

    let canManage =
        model.RoleInTeam
        |> Map.tryFind teamId
        |> Option.map TeamRoles.canManageMembers
        |> Option.defaultValue false

    let selfId = selfUserId ()

    Html.div [
        prop.className "space-y-4"
        prop.children [
            Html.div [
                prop.className "flex items-center gap-3"
                prop.children [
                    Html.button [
                        prop.className "text-sm text-brand hover:underline"
                        prop.text "← My teams"
                        prop.onClick (fun _ -> dispatch BackToMyTeams)
                    ]
                    Html.span [ prop.className "text-muted"; prop.text "/" ]
                    Html.span [
                        prop.className "font-medium"
                        prop.text (teamOpt |> Option.map _.Name |> Option.defaultValue teamId)
                    ]
                    if canManage then
                        Html.div [
                            prop.className "ml-auto"
                            prop.children [
                                Forms.Button.secondary "Pending invites" (fun () ->
                                    dispatch (NavigatePendingInvites teamId))
                            ]
                        ]
                ]
            ]

            Layout.Panel.panel "Members" [
                if members.IsEmpty then
                    Html.p [
                        prop.className "text-sm text-muted py-2"
                        prop.text "No members (loading, or team was deleted)."
                    ]
                else
                    Html.div [
                        for m in members do
                            memberRow teamId selfId canManage m dispatch
                    ]
            ]

            if canManage then
                addMemberForm teamId model dispatch
        ]
    ]

// ─── Phase 3d.A — Pending Invites view (email sub-tab) ──────────────

/// Phase 3d.A — sub-tab tag inside the Pending Invites view. Only
/// `EmailInvites` is populated today; the link-based companion tab
/// lands as a sibling here when the follow-on UI phase ships.
type private PendingInvitesSubTab = EmailInvites

let private pendingEmailRow (teamId: string) (email: string) (entry: PendingInviteByEmail) (dispatch: Msg -> unit) =
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
                            sprintf
                                "%s · expires %s"
                                (TeamRoles.displayName entry.Role)
                                (entry.ExpiresAt.ToString "yyyy-MM-dd")
                        )
                    ]
                ]
            ]
            Forms.Button.secondary "Revoke" (fun () -> dispatch (OpenRevokeByEmailConfirm(teamId, email)))
        ]
    ]

let private emptyPendingEmailState =
    Html.p [
        prop.className "text-sm text-muted py-4"
        prop.text
            "No pending email invitations. Use 'Invite by email' to add one — the recipient will auto-join the team on their first sign-in matching the email."
    ]

let private issueByEmailModalView (state: IssueByEmailModalState) (dispatch: Msg -> unit) =
    let roleOption (role: TeamRole) =
        Html.button [
            prop.className [
                "px-3 py-1 rounded text-sm transition-colors"
                if state.Role = role then
                    "bg-brand text-brand-text"
                else
                    "bg-gray-100 text-text hover:bg-gray-200"
            ]
            prop.text (TeamRoles.displayName role)
            prop.onClick (fun _ -> dispatch (SetIssueByEmailRole role))
        ]

    Html.div [
        prop.className "fixed inset-0 bg-black/40 flex items-center justify-center z-50"
        prop.children [
            Html.div [
                prop.className "bg-white rounded-lg shadow-lg p-6 w-full max-w-md space-y-4"
                prop.children [
                    Html.h3 [
                        prop.className "text-lg font-semibold"
                        prop.text "Invite by email (no link)"
                    ]
                    Html.p [
                        prop.className "text-sm text-muted"
                        prop.text
                            "The recipient joins automatically on their first sign-in matching the email. No invitation link is generated."
                    ]
                    // Plain `Html.input` (not `Forms.Input.text`) — the
                    // toolkit's text input commits on Enter / blur only,
                    // and the blur path resets the displayed value to the
                    // parent model's value. In a single-input modal where
                    // the operator is expected to type + click "Issue
                    // invitation", that idiom drops the typed email.
                    // Per-keystroke binding here keeps the model in sync
                    // with the visible text.
                    Html.input [
                        prop.type' "email"
                        prop.placeholder "Email address"
                        prop.value state.Email
                        prop.onChange (fun (v: string) -> dispatch (SetIssueByEmailEmail v))
                        prop.className [
                            "border border-border"
                            "rounded-lg"
                            "px-4 py-2"
                            "focus:outline-none focus:border-brand"
                            "transition-colors"
                            "w-full"
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-center gap-3"
                        prop.children [
                            Html.span [ prop.className "text-sm text-muted"; prop.text "Role:" ]
                            // `Owner` is explicitly excluded — the substrate
                            // refuses Owner on the no-link path (mirrors
                            // the link-based flow). UI doesn't offer it.
                            roleOption Member
                            roleOption Admin
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-center gap-3"
                        prop.children [
                            Html.label [ prop.className "text-sm text-muted"; prop.text "Expires in (days):" ]
                            Html.input [
                                prop.type' "number"
                                prop.min 1
                                prop.value state.ExpiresInDays
                                prop.onChange (fun (v: string) ->
                                    match Int32.TryParse v with
                                    | true, n -> dispatch (SetIssueByEmailExpiresInDays n)
                                    | _ -> ())
                                prop.className
                                    "border border-border rounded px-3 py-1 w-24 focus:outline-none focus:border-brand"
                            ]
                        ]
                    ]
                    match state.SubmitError with
                    | Some msg -> Html.p [ prop.className "text-sm text-red-600"; prop.text msg ]
                    | None -> Html.none
                    Html.div [
                        prop.className "flex justify-end gap-3 pt-2"
                        prop.children [
                            Forms.Button.secondary "Cancel" (fun () -> dispatch CloseIssueByEmailModal)
                            Html.button [
                                prop.disabled state.Submitting
                                prop.className [
                                    "px-4 py-2 rounded-lg text-sm font-medium transition-colors"
                                    if state.Submitting then
                                        "bg-gray-300 text-gray-500 cursor-not-allowed"
                                    else
                                        "bg-brand text-brand-text hover:bg-brand-dark"
                                ]
                                prop.text (if state.Submitting then "Issuing…" else "Issue invitation")
                                prop.onClick (fun _ -> dispatch SubmitIssueByEmail)
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private revokeByEmailConfirmView (teamId: string) (email: string) (model: Model) (dispatch: Msg -> unit) =
    let teamName =
        model.Teams
        |> List.tryFind (fun t -> t.TeamId = teamId)
        |> Option.map _.Name
        |> Option.defaultValue teamId

    Html.div [
        prop.className "fixed inset-0 bg-black/40 flex items-center justify-center z-50"
        prop.children [
            Html.div [
                prop.className "bg-white rounded-lg shadow-lg p-6 w-full max-w-sm space-y-4"
                prop.children [
                    Html.h3 [
                        prop.className "text-lg font-semibold"
                        prop.text "Revoke pending invitation?"
                    ]
                    Html.p [
                        prop.className "text-sm text-muted"
                        prop.text (sprintf "%s will no longer auto-join %s on sign-in." email teamName)
                    ]
                    Html.div [
                        prop.className "flex justify-end gap-3 pt-2"
                        prop.children [
                            Forms.Button.secondary "Cancel" (fun () -> dispatch CancelRevokeByEmail)
                            Forms.Button.primary "Revoke" (fun () -> dispatch ConfirmRevokeByEmail)
                        ]
                    ]
                ]
            ]
        ]
    ]

let private pendingInvitesView (teamId: string) (model: Model) (dispatch: Msg -> unit) =
    let teamName =
        model.Teams
        |> List.tryFind (fun t -> t.TeamId = teamId)
        |> Option.map _.Name
        |> Option.defaultValue teamId

    let entries = model.PendingByEmail |> Map.tryFind teamId

    let subTab = EmailInvites

    Html.div [
        prop.className "space-y-4"
        prop.children [
            Html.div [
                prop.className "flex items-center gap-3"
                prop.children [
                    Html.button [
                        prop.className "text-sm text-brand hover:underline"
                        prop.text "← Members"
                        prop.onClick (fun _ -> dispatch (SelectTeam teamId))
                    ]
                    Html.span [ prop.className "text-muted"; prop.text "/" ]
                    Html.span [ prop.className "font-medium"; prop.text teamName ]
                    Html.span [ prop.className "text-muted"; prop.text "/" ]
                    Html.span [ prop.className "font-medium"; prop.text "Pending invites" ]
                ]
            ]

            Layout.Tabs.tabGroup [ EmailInvites, "Pending email invites" ] subTab (fun _ -> ())

            Layout.Panel.panel "Pending email invites" [
                Html.div [
                    prop.className "flex justify-end mb-3"
                    prop.children [
                        Forms.Button.primary "Invite by email" (fun () -> dispatch (OpenIssueByEmailModal teamId))
                    ]
                ]
                match entries with
                | None ->
                    Html.p [
                        prop.className "text-sm text-muted py-2"
                        prop.text "Loading pending invitations…"
                    ]
                | Some [] -> emptyPendingEmailState
                | Some rows ->
                    Html.div [
                        for email, entry in rows do
                            pendingEmailRow teamId email entry dispatch
                    ]
            ]

            match model.IssueByEmailModal with
            | Some state when state.TeamId = teamId -> issueByEmailModalView state dispatch
            | _ -> Html.none

            match model.RevokeByEmailConfirm with
            | Some(tid, email) when tid = teamId -> revokeByEmailConfirmView tid email model dispatch
            | _ -> Html.none
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let errorBanner =
        match model.Error with
        | Some msg ->
            Html.div [
                prop.className
                    "p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text msg ]
                    Html.button [
                        prop.className "text-xs text-red-600 hover:underline"
                        prop.text "dismiss"
                        prop.onClick (fun _ -> dispatch DismissError)
                    ]
                ]
            ]
        | None -> Html.none

    let body =
        match model.CurrentView with
        | MyTeams -> myTeamsView model dispatch
        | TeamDetails teamId -> teamDetailsView teamId model dispatch
        | PendingInvites teamId -> pendingInvitesView teamId model dispatch

    // 0.5.6 — settings-shape FullWidth render. Error banner stacks
    // above the body so it's immediately above the user's current
    // context instead of in a separate squashed right pane.
    Html.div [ prop.className "flex flex-col gap-3"; prop.children [ errorBanner; body ] ]

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in team manager as an `ErasedModule`. The shell's
/// `prepareModules` in SDK.Client.fs injects this when
/// `ClientConfig.Surfaces` declares a single-team `Team` surface
/// (`Switching = NoSwitcher`) and `ClientConfig.TeamManager` is not
/// `NoTeamManager`.
let create (config: TeamManagerConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Teams"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    let route = "/" + name.ToLower().Replace(" ", "-")

    // SDK-built-in module — `Id` is reserved under the `_sdk.` namespace so
    // it can never collide with an app's RBAC-managed `ServerConfig.ModuleNames`
    // key. Apps that swap in an `ExternalTeamManager` set their own Id.
    // `init` here is `ClientModuleContext -> Model * Cmd<Msg>` so we use
    // `withContextInit` to override `create`'s unit-init default.
    ToolUp.Platform.ClientModule.create {
        Init = fun () -> init ToolUp.Platform.ClientModuleContext.empty
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.TeamManager"
    |> ToolUp.Platform.ClientModule.withContextInit init
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register