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

/// Phase 304 — the two-step ownership-transfer flow. Step one picks the
/// incoming Owner from the team's current members via a typeahead filter;
/// step two is an explicit "are you sure?" confirmation naming both the
/// outgoing (the caller) and incoming Owner before the API fires.
type TransferStep =
    | PickNewOwner
    | ConfirmTransfer

/// Phase 304 — open-modal state for "Transfer ownership". `None` = closed.
type TransferOwnershipModalState = {
    TeamId: string
    /// Typeahead filter over the team's current members (step one).
    Query: string
    /// The chosen incoming Owner's user id — `Some` once a candidate is
    /// picked (which advances the modal to `ConfirmTransfer`).
    SelectedUserId: string option
    Step: TransferStep
    /// Disable the confirm button while the transfer call is in flight.
    Submitting: bool
    /// Inline error from the most recent submit — keeps the confirm step
    /// open so the operator sees why (e.g. a concurrent role change).
    SubmitError: string option
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
    /// Add-member form state for a given teamId.
    AddMemberUserId: string
    AddMemberRole: TeamRole
    /// In-flight guard for the page-level "Add a member" form (the
    /// no-modal email-detect / direct-add paths). The modal flow tracks
    /// its own `Submitting` inside `IssueByEmailModal`; this covers the
    /// inline form, which previously had no busy state and so allowed a
    /// double-click to fire two invites / adds.
    AddMemberSubmitting: bool
    /// Shell-supplied callback fired when the active team changes
    /// (`ActiveTeamSwitched(_, Ok())` or `TeamCreated(Ok _)`).
    /// Populated from `ClientModuleContext.OnTeamSwitched` at init —
    /// `Some` in `Team` / `MultiTeam` modes, `None` otherwise. The
    /// shell uses this to run the global `TeamSwitched` reset
    /// (re-fetch configs / flags / RBAC, re-init the active module)
    /// so the rest of the UI swaps to the new team's data.
    OnTeamSwitched: (string -> unit) option
    /// Whether the signed-in caller holds
    /// `PlatformRole.PlatformAdmin`. `None` while loading; `Some _`
    /// once the server has responded. Drives the client-side
    /// "Platform Admin has full rights across all teams" bypass on
    /// member-management controls — the server gate is the real
    /// enforcement; this just keeps the controls visible.
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
    /// Phase 547.B — recently-expired pending-by-email invites per
    /// team, loaded alongside `PendingByEmail` on entry to the
    /// `PendingInvites` view via
    /// `ITeamInviteApi.ListRecentlyExpiredInvites` (30-day window,
    /// read from the audit trail). `Map.tryFind` `None` = not loaded
    /// yet; an empty list hides the section.
    ExpiredInvites: Map<string, TeamInviteExpiredPayload list>
    /// Phase 547.B — re-issue calls in flight, keyed
    /// `"{teamId}|{email}"`. Disables that row's Re-issue button so a
    /// double-click cannot fire two issues.
    ReissueInFlight: Set<string>
    /// Phase 304 — ownership-transfer modal state. `None` = closed.
    TransferOwnershipModal: TransferOwnershipModalState option
    /// Resolved directory entries keyed by user id (id → display name +
    /// email), populated lazily via `IUserDirectoryApi.ResolveUsers`
    /// after a team's members load. Ids absent from the map (directory
    /// companion unwired, or a guest/deleted id) render as the raw id —
    /// the member list stays functional, just less friendly. Mirrors
    /// `PlatformAdminUI.Directory`.
    Directory: Map<string, UserSummary>
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
    | SetAddMemberUserId of string
    | SetAddMemberRole of TeamRole
    | AddMember of teamId: string
    | MemberAdded of Result<unit, string>
    | RemoveMember of teamId: string * userId: string
    | MemberRemoved of Result<unit, string>
    | ChangeMemberRole of teamId: string * userId: string * newRole: TeamRole
    | MemberRoleChanged of Result<unit, string>
    | IsPlatformAdminLoaded of bool
    // ─── Phase 304 — ownership transfer ───────────────────────────
    | OpenTransferOwnership of teamId: string
    | CloseTransferOwnership
    | SetTransferQuery of string
    | SelectTransferCandidate of userId: string
    | BackToTransferPick
    | SubmitTransferOwnership
    | TransferOwnershipDone of teamId: string * Result<unit, string>
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
    // ─── Phase 547.B — expired-invite visibility + re-issue ───────
    | LoadExpiredInvites of teamId: string
    | ExpiredInvitesLoaded of teamId: string * TeamInviteExpiredPayload list
    | ReissueExpiredInvite of teamId: string * email: string * role: TeamRole
    | ExpiredInviteReissued of teamId: string * email: string * Result<unit, string>
    /// Reverse directory resolution (id → name/email) completed for the
    /// most recently loaded member set.
    | DirectoryResolved of Result<UserSummary list, string>
    | ApiError of string
    | DismissError

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
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

// Reverse directory lookup (id → email/name) for the member list. No-op
// for an empty list or when no directory companion is wired (degrades to
// raw ids). Mirrors `PlatformAdminUI`'s directory wiring.
let private directoryApi: IUserDirectoryApi =
    Api.makeProxy<IUserDirectoryApi> (customOptions = UserSession.withRequestHeaders)

/// Errors fold to a silent `DirectoryResolved(Error _)` — a directory
/// hiccup just leaves the raw ids on screen, never blocks the page.
let private resolveDirectoryCmd (ids: string list) =
    let cleaned =
        ids |> List.filter (fun id -> not (System.String.IsNullOrWhiteSpace id))

    if List.isEmpty cleaned then
        Cmd.none
    else
        Cmd.OfRemoting.call directoryApi.ResolveUsers cleaned DirectoryResolved (fun ex ->
            DirectoryResolved(Error ex.Message))

// ─── Init ────────────────────────────────────────────────────────────

let init (ctx: ClientModuleContext) =
    let model = {
        Teams = []
        ActiveTeamId = None
        CurrentView = MyTeams
        Members = Map.empty
        RoleInTeam = Map.empty
        Error = None
        AddMemberUserId = ""
        AddMemberRole = Member
        AddMemberSubmitting = false
        OnTeamSwitched = ctx.OnTeamSwitched
        IsPlatformAdmin = None
        PendingByEmail = Map.empty
        IssueByEmailModal = None
        RevokeByEmailConfirm = None
        ExpiredInvites = Map.empty
        ReissueInFlight = Set.empty
        TransferOwnershipModal = None
        Directory = Map.empty
    }

    let loadTeams =
        Cmd.OfRemoting.call teamApi.GetMyTeams () TeamsLoaded (fun e -> ApiError e.Message)

    let loadActive =
        Cmd.OfRemoting.call teamApi.GetActiveTeam () ActiveTeamLoaded (fun e -> ApiError e.Message)

    // Fetch the caller's platform-admin status so the management
    // controls can apply the "Platform Admin has full rights across
    // all teams" bypass. Failure falls through to `ApiError` and
    // leaves the bypass disabled (`IsPlatformAdmin = None`) — server
    // gates remain the real enforcement.
    let loadAdminStatus =
        Cmd.OfRemoting.call platformAdminApi.IsPlatformAdmin () IsPlatformAdminLoaded (fun e -> ApiError e.Message)

    model, Cmd.batch [ loadTeams; loadActive; loadAdminStatus ]

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

        // Resolve the member ids we don't already have a directory entry
        // for (id → name/email). Errors are silent — the list still
        // renders with raw ids on a directory miss.
        let unresolved =
            members
            |> List.map _.UserId
            |> List.filter (fun id -> not (Map.containsKey id model.Directory))
            |> List.distinct

        {
            model with
                Members = model.Members |> Map.add teamId members
                RoleInTeam = roleMap
        },
        resolveDirectoryCmd unresolved

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

    | SetAddMemberUserId id -> { model with AddMemberUserId = id }, Cmd.none

    | SetAddMemberRole role -> { model with AddMemberRole = role }, Cmd.none

    // Ignore a second click while an add/invite is already in flight —
    // the page-level form had no busy guard, so a double-click fired two
    // invites / adds.
    | AddMember _ when model.AddMemberSubmitting -> model, Cmd.none

    | AddMember teamId ->
        let raw = model.AddMemberUserId.Trim()

        if raw = "" then
            {
                model with
                    Error = Some(MessageCatalog.english.TeamManager.IdentifierRequired)
            },
            Cmd.none
        // 0.5.7 — Detect email format and route through the
        // pending-invite flow. Pre-0.5.7 the form passed the raw input
        // straight to `AddTeamMember`, which stored it as the literal
        // `TeamMembership.UserId`. An operator typing an email got a
        // membership keyed by the email string — but the invitee's
        // JWT carries their `oid` (post-0.5.4) or `sub`, so
        // `GetMyTeams(jwtUserId)` returned `[]` and the invitee
        // landed as `UserKind` with a `user-<jwtUserId>` scope. The
        // team's storage scope (`team-<teamId>`) stayed invisible to
        // them — they could sign in but couldn't see any team data,
        // modules, or permissions.
        //
        // `IssuePendingInviteByEmail` instead stores a pending entry
        // keyed by email. When the invitee signs in,
        // `ScopeResolutionMiddleware` matches the email claim against
        // pending entries and auto-creates a `TeamMembership` keyed
        // by the JWT's resolved user-id (oid for Entra). The team
        // becomes visible immediately.
        //
        // Detection: a simple `@` + dot heuristic catches the common
        // email shape. Operators with a real oid (UUID-shaped or
        // base64url `sub`) bypass the heuristic and land on the
        // direct-add path — useful for SSO replication scripts that
        // already know the identity-provider-resolved id.
        elif raw.Contains('@') && raw.IndexOf('.', raw.IndexOf('@')) > 0 then
            {
                model with
                    AddMemberSubmitting = true
            },
            Cmd.OfRemoting.call
                inviteApi.IssuePendingInviteByEmail
                {
                    TeamId = teamId
                    Email = raw
                    Role = model.AddMemberRole
                    ExpiresIn = Some(TimeSpan.FromDays 7.0)
                }
                (fun r -> IssueByEmailSubmitted(teamId, r))
                (fun e -> IssueByEmailSubmitted(teamId, Error e.Message))
        else
            {
                model with
                    AddMemberSubmitting = true
            },
            Cmd.OfRemoting.call teamApi.AddTeamMember (teamId, raw, model.AddMemberRole) MemberAdded (fun e ->
                ApiError e.Message)

    | MemberAdded(Ok()) ->
        let refresh =
            match model.CurrentView with
            | TeamDetails teamId -> Cmd.ofMsg (LoadMembers teamId)
            | _ -> Cmd.none

        {
            model with
                AddMemberUserId = ""
                AddMemberSubmitting = false
                Error = None
        },
        refresh

    | MemberAdded(Error e) ->
        {
            model with
                AddMemberSubmitting = false
                Error = Some e
        },
        Cmd.none

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

    | IsPlatformAdminLoaded isAdmin ->
        {
            model with
                IsPlatformAdmin = Some isAdmin
        },
        Cmd.none

    // ─── Phase 304 — ownership transfer ───────────────────────────

    | OpenTransferOwnership teamId ->
        {
            model with
                TransferOwnershipModal =
                    Some {
                        TeamId = teamId
                        Query = ""
                        SelectedUserId = None
                        Step = PickNewOwner
                        Submitting = false
                        SubmitError = None
                    }
        },
        Cmd.none

    | CloseTransferOwnership ->
        {
            model with
                TransferOwnershipModal = None
        },
        Cmd.none

    | SetTransferQuery query ->
        {
            model with
                TransferOwnershipModal = model.TransferOwnershipModal |> Option.map (fun m -> { m with Query = query })
        },
        Cmd.none

    // Picking a candidate advances to the confirmation step — the
    // two-step "typeahead then are-you-sure" contract.
    | SelectTransferCandidate userId ->
        {
            model with
                TransferOwnershipModal =
                    model.TransferOwnershipModal
                    |> Option.map (fun m -> {
                        m with
                            SelectedUserId = Some userId
                            Step = ConfirmTransfer
                            SubmitError = None
                    })
        },
        Cmd.none

    | BackToTransferPick ->
        {
            model with
                TransferOwnershipModal =
                    model.TransferOwnershipModal
                    |> Option.map (fun m -> {
                        m with
                            Step = PickNewOwner
                            SubmitError = None
                    })
        },
        Cmd.none

    | SubmitTransferOwnership ->
        match model.TransferOwnershipModal with
        | Some m when m.SelectedUserId.IsSome && not m.Submitting ->
            let target = m.SelectedUserId.Value

            {
                model with
                    TransferOwnershipModal =
                        Some {
                            m with
                                Submitting = true
                                SubmitError = None
                        }
            },
            Cmd.OfRemoting.call
                teamApi.TransferOwnership
                (m.TeamId, target)
                (fun r -> TransferOwnershipDone(m.TeamId, r))
                (fun e -> TransferOwnershipDone(m.TeamId, Error e.Message))
        | _ -> model, Cmd.none

    | TransferOwnershipDone(teamId, Ok()) ->
        // Ownership moved — the caller is now an Admin. Close the modal
        // and reload members so every role badge (incl. the caller's own)
        // reflects the new state.
        {
            model with
                TransferOwnershipModal = None
                Error = None
        },
        Cmd.ofMsg (LoadMembers teamId)

    | TransferOwnershipDone(_, Error e) ->
        {
            model with
                TransferOwnershipModal =
                    model.TransferOwnershipModal
                    |> Option.map (fun m -> {
                        m with
                            Submitting = false
                            SubmitError = Some e
                    })
        },
        Cmd.none

    // ─── Phase 3d.A — pending-invite admin surface ────────────────

    | NavigatePendingInvites teamId ->
        {
            model with
                CurrentView = PendingInvites teamId
        },
        // Phase 547.B — the expired list loads alongside the live one so
        // the view renders both sections from one navigation.
        Cmd.batch [ Cmd.ofMsg (LoadPendingByEmail teamId); Cmd.ofMsg (LoadExpiredInvites teamId) ]

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
                                SubmitError = Some(MessageCatalog.english.TeamManager.EmailRequired)
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
        // entry appears. 0.5.7 — also clear the AddMemberUserId
        // field, since the "Add a member" form's email-detect path
        // routes through this same Msg without going through the
        // modal flow.
        {
            model with
                IssueByEmailModal = None
                AddMemberUserId = ""
                AddMemberSubmitting = false
                Error = None
        },
        Cmd.ofMsg (LoadPendingByEmail teamId)

    | IssueByEmailSubmitted(_, Error e) ->
        // 0.5.7 — when triggered from the modal, surface the error
        // inside the modal (modal stays open for retry). When
        // triggered from the email-detect path of "Add a member" (no
        // modal), surface as the page-level Error banner so the
        // operator sees what happened.
        match model.IssueByEmailModal with
        | Some m ->
            {
                model with
                    IssueByEmailModal =
                        Some {
                            m with
                                Submitting = false
                                SubmitError = Some e
                        }
            },
            Cmd.none
        | None ->
            {
                model with
                    AddMemberSubmitting = false
                    Error = Some e
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

    // ─── Phase 547.B — expired-invite visibility + re-issue ───────

    | LoadExpiredInvites teamId ->
        let onLoad (result: Result<TeamInviteExpiredPayload list, string>) =
            match result with
            | Ok entries -> ExpiredInvitesLoaded(teamId, entries)
            | Error e -> ApiError e

        model, Cmd.OfRemoting.call inviteApi.ListRecentlyExpiredInvites teamId onLoad (fun e -> ApiError e.Message)

    | ExpiredInvitesLoaded(teamId, entries) ->
        {
            model with
                ExpiredInvites = model.ExpiredInvites |> Map.add teamId entries
        },
        Cmd.none

    | ReissueExpiredInvite(teamId, email, role) ->
        // One-click re-issue: same email, the original role, the default
        // expiry (`ExpiresIn = None` → `TeamInviteTypes.DefaultExpiry`
        // server-side). Reuses the existing issue path verbatim.
        let request = {
            TeamId = teamId
            Email = email
            Role = role
            ExpiresIn = None
        }

        {
            model with
                ReissueInFlight = model.ReissueInFlight |> Set.add (sprintf "%s|%s" teamId email)
        },
        Cmd.OfRemoting.call
            inviteApi.IssuePendingInviteByEmail
            request
            (fun r -> ExpiredInviteReissued(teamId, email, r))
            (fun e -> ExpiredInviteReissued(teamId, email, Error e.Message))

    | ExpiredInviteReissued(teamId, email, result) ->
        let cleared = {
            model with
                ReissueInFlight = model.ReissueInFlight |> Set.remove (sprintf "%s|%s" teamId email)
        }

        match result with
        | Ok() ->
            // Both lists shift: the email gains a live pending entry (which
            // also removes it from the server's expired projection).
            cleared, Cmd.batch [ Cmd.ofMsg (LoadPendingByEmail teamId); Cmd.ofMsg (LoadExpiredInvites teamId) ]
        | Error e -> { cleared with Error = Some e }, Cmd.none

    | DirectoryResolved(Ok summaries) ->
        let directory =
            summaries
            |> List.fold (fun acc (s: UserSummary) -> Map.add s.UserId s acc) model.Directory

        { model with Directory = directory }, Cmd.none

    // Silent — a directory failure (companion unwired, Graph hiccup) just
    // leaves the raw ids on screen; never blocks the member list.
    | DirectoryResolved(Error _) -> model, Cmd.none

    | ApiError message -> { model with Error = Some message }, Cmd.none

    | DismissError -> { model with Error = None }, Cmd.none

// ─── View ────────────────────────────────────────────────────────────

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

let private roleOfString =
    function
    | "Owner" -> Owner
    | "Admin" -> Admin
    | _ -> Member

/// Effective team role for the caller on the supplied team. Returns
/// `Some Owner` when the caller is a Platform Admin — the
/// "Platform Admins have complete rights across all teams" rule —
/// regardless of whether they actually hold a membership row. Falls
/// back to the team store's view otherwise. Server-side gates apply
/// the same bypass; this is the client-side mirror that keeps the
/// management controls visible for PAs.
let private effectiveCallerRole (model: Model) (teamId: string) : TeamRole option =
    if model.IsPlatformAdmin = Some true then
        Some Owner
    else
        model.RoleInTeam |> Map.tryFind teamId

/// Roles the caller may ASSIGN via the add-member form or
/// change-role dropdown. Owner is never assignable here — initial
/// Owner is set once at team-creation time via Platform
/// Management; transferring ownership is a deliberately separate
/// concern. Members cannot assign anything (the management UI is
/// hidden for them anyway, but the predicate keeps the helper
/// total).
let private assignableRoles (callerRole: TeamRole) : TeamRole list =
    match callerRole with
    | Owner -> [ Member; Admin ]
    | Admin -> [ Member ]
    | Member -> []

let private memberRow
    (msgs: TeamManagerMessages)
    (teamId: string)
    (selfId: string)
    (callerRole: TeamRole option)
    (directory: Map<string, UserSummary>)
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

    let roleBadge =
        Html.span [
            prop.className "text-xs text-muted px-2 py-0.5 rounded bg-gray-100"
            prop.text (TeamRoles.displayName membership.Role)
        ]

    let roleDropdown =
        let optionRoles =
            match callerRole with
            | Some r -> assignableRoles r
            | None -> []

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
                for role in optionRoles do
                    Html.option [
                        prop.value (TeamRoles.displayName role)
                        prop.text (TeamRoles.displayName role)
                    ]
            ]
        ]

    // Render the current user's display name + email from the JWT
    // (`name` / `email` / `preferred_username` claims). Other members
    // resolve via the directory companion (`IUserDirectoryApi`): display
    // name + email when known, email alone, else the raw UserId as the
    // last-resort fallback (no companion wired, or an unresolved id).
    let primaryLabel, secondaryLabel =
        if isSelf then
            let name = UserSession.getDisplayName () |> Option.defaultValue membership.UserId

            let email = UserSession.getEmail ()
            name, email
        else
            match Map.tryFind membership.UserId directory with
            | Some s ->
                let name = s.DisplayName |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
                let email = s.Email |> Option.filter (System.String.IsNullOrWhiteSpace >> not)

                match name, email with
                | Some n, _ -> n, email
                | None, Some e -> e, None
                | None, None -> membership.UserId, None
            | None -> membership.UserId, None

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
                        roleDropdown
                    else
                        roleBadge
                    if isSelf then
                        Html.span [ prop.className "text-xs text-brand font-medium"; prop.text msgs.YouSuffix ]
                ]
            ]
            if canManageTarget && not isSelf then
                Forms.Button.secondary msgs.RemoveMember (fun () -> dispatch (RemoveMember(teamId, membership.UserId)))
        ]
    ]

let private addMemberForm
    (msgs: TeamManagerMessages)
    (teamId: string)
    (callerRole: TeamRole option)
    (model: Model)
    (dispatch: Msg -> unit)
    =
    let allowedRoles =
        match callerRole with
        | Some r -> assignableRoles r
        | None -> []

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

                Html.div [
                    prop.className "flex items-center gap-3 flex-wrap"
                    prop.children [
                        Html.span [ prop.className "text-sm text-muted"; prop.text msgs.RoleLabel ]
                        for role in allowedRoles do
                            let isSelected = model.AddMemberRole = role

                            Html.button [
                                prop.className [
                                    "px-3 py-1 rounded text-sm transition-colors border"
                                    if isSelected then
                                        "bg-brand/10 text-brand border-brand"
                                    else
                                        "bg-white text-text border-border hover:bg-gray-50"
                                ]
                                prop.text (TeamRoles.displayName role)
                                prop.onClick (fun _ -> dispatch (SetAddMemberRole role))
                            ]
                    ]
                ]

                Html.div [
                    if model.AddMemberSubmitting then
                        // Disabled while a previous add/invite is in flight,
                        // so a double-click can't fire two requests.
                        Html.button [
                            prop.disabled true
                            prop.className [
                                Tokens.Colours.brand
                                Tokens.Colours.brandText
                                Tokens.Spacing.buttonPaddingX
                                Tokens.Spacing.buttonPaddingY
                                Tokens.Typography.buttonText
                                "rounded-lg"
                                "opacity-50"
                                "cursor-not-allowed"
                            ]
                            prop.text msgs.Inviting
                        ]
                    else
                        Forms.Button.primary msgs.InviteMember (fun () -> dispatch (AddMember teamId))
                ]
            ]
        ]
    ]

/// Best-effort human label for a user id, mirroring `memberRow`'s
/// name resolution: the JWT display name for the caller themselves, else
/// the directory-resolved name / email, else the raw id. Used by the
/// Phase 304 transfer modal for both the candidate list and the
/// confirmation copy.
let private displayNameForId (directory: Map<string, UserSummary>) (selfId: string) (userId: string) : string =
    if userId = selfId then
        UserSession.getDisplayName () |> Option.defaultValue userId
    else
        match Map.tryFind userId directory with
        | Some s ->
            let name = s.DisplayName |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
            let email = s.Email |> Option.filter (System.String.IsNullOrWhiteSpace >> not)

            match name, email with
            | Some n, _ -> n
            | None, Some e -> e
            | None, None -> userId
        | None -> userId

/// Phase 304 — the two-step "Transfer ownership" modal. Step one filters
/// the team's current members (never mints a new membership); picking one
/// advances to step two, an explicit confirmation naming both parties
/// before the API fires. Rendered only for the real team Owner (the
/// caller opening it), so the candidate list always excludes the caller.
let private transferOwnershipModalView
    (msgs: TeamManagerMessages)
    (teamId: string)
    (state: TransferOwnershipModalState)
    (model: Model)
    (dispatch: Msg -> unit)
    =
    let selfId = selfUserId ()
    let members = model.Members |> Map.tryFind teamId |> Option.defaultValue []

    let teamName =
        model.Teams
        |> List.tryFind (fun t -> t.TeamId = teamId)
        |> Option.map _.Name
        |> Option.defaultValue teamId

    // Candidates: every current member except the caller (the outgoing
    // Owner). Ownership can only move to someone already on the team.
    let candidates = members |> List.filter (fun m -> m.UserId <> selfId)

    let query = state.Query.Trim().ToLower()

    let filtered =
        if query = "" then
            candidates
        else
            candidates
            |> List.filter (fun m ->
                let label = (displayNameForId model.Directory selfId m.UserId).ToLower()
                label.Contains query || m.UserId.ToLower().Contains query)

    let modalShell (children: ReactElement list) =
        Html.div [
            prop.className "fixed inset-0 bg-black/40 flex items-center justify-center z-50"
            prop.children [
                Html.div [
                    prop.className "bg-white rounded-lg shadow-lg p-6 w-full max-w-md space-y-4"
                    prop.children children
                ]
            ]
        ]

    match state.Step with
    | PickNewOwner ->
        modalShell [
            Html.h3 [ prop.className "text-lg font-semibold"; prop.text msgs.TransferOwnership ]
            Html.p [
                prop.className "text-sm text-muted"
                prop.text (msgs.TransferOwnershipHelp teamName)
            ]
            Html.input [
                prop.type' "text"
                prop.placeholder msgs.TransferFilterPlaceholder
                prop.value state.Query
                prop.onChange (fun (v: string) -> dispatch (SetTransferQuery v))
                prop.className [
                    "border border-border"
                    "rounded-lg"
                    "px-4 py-2"
                    "focus:outline-none focus:border-brand"
                    "transition-colors"
                    "w-full"
                ]
            ]
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
                            let label = displayNameForId model.Directory selfId m.UserId

                            Html.button [
                                prop.className
                                    "flex items-center justify-between w-full text-left px-3 py-2 border border-border rounded-lg hover:bg-gray-50 transition-colors"
                                prop.onClick (fun _ -> dispatch (SelectTransferCandidate m.UserId))
                                prop.children [
                                    Html.span [ prop.className "font-medium text-sm"; prop.text label ]
                                    Html.span [
                                        prop.className "text-xs text-muted"
                                        prop.text (TeamRoles.displayName m.Role)
                                    ]
                                ]
                            ]
                    ]
                ]
            Html.div [
                prop.className "flex justify-end gap-3 pt-2"
                prop.children [
                    Forms.Button.secondary msgs.Cancel (fun () -> dispatch CloseTransferOwnership)
                ]
            ]
        ]
    | ConfirmTransfer ->
        let newOwnerLabel =
            state.SelectedUserId
            |> Option.map (displayNameForId model.Directory selfId)
            |> Option.defaultValue "the selected member"

        let outgoingLabel = displayNameForId model.Directory selfId selfId

        modalShell [
            Html.h3 [
                prop.className "text-lg font-semibold"
                prop.text msgs.TransferConfirmHeading
            ]
            Html.p [
                prop.className "text-sm text-muted"
                prop.text (msgs.TransferConfirmPrompt teamName outgoingLabel newOwnerLabel)
            ]
            Html.p [
                prop.className "text-sm text-muted"
                prop.text (msgs.TransferRoleExplanation newOwnerLabel)
            ]
            match state.SubmitError with
            | Some msg -> Html.p [ prop.className "text-sm text-red-600"; prop.text msg ]
            | None -> Html.none
            Html.div [
                prop.className "flex justify-end gap-3 pt-2"
                prop.children [
                    Forms.Button.secondary msgs.Back (fun () -> dispatch BackToTransferPick)
                    Html.button [
                        prop.disabled state.Submitting
                        prop.className [
                            "px-4 py-2 rounded-lg text-sm font-medium transition-colors"
                            if state.Submitting then
                                "bg-gray-300 text-gray-500 cursor-not-allowed"
                            else
                                "bg-brand text-brand-text hover:bg-brand-dark"
                        ]
                        prop.text (
                            if state.Submitting then
                                msgs.Transferring
                            else
                                msgs.ConfirmTransfer
                        )
                        prop.onClick (fun _ -> dispatch SubmitTransferOwnership)
                    ]
                ]
            ]
        ]

let private teamDetailsView (msgs: TeamManagerMessages) (teamId: string) (model: Model) (dispatch: Msg -> unit) =
    let teamOpt = model.Teams |> List.tryFind (fun t -> t.TeamId = teamId)
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
                    Html.span [
                        prop.className "font-medium"
                        prop.text (teamOpt |> Option.map _.Name |> Option.defaultValue teamId)
                    ]
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

            match model.TransferOwnershipModal with
            | Some state when state.TeamId = teamId -> transferOwnershipModalView msgs teamId state model dispatch
            | _ -> Html.none
        ]
    ]

// ─── Phase 3d.A — Pending Invites view (email sub-tab) ──────────────

/// Phase 3d.A — sub-tab tag inside the Pending Invites view. Only
/// `EmailInvites` is populated today; the link-based companion tab
/// lands as a sibling here when the follow-on UI phase ships.
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
            Forms.Button.secondary msgs.RevokeInvite (fun () -> dispatch (OpenRevokeByEmailConfirm(teamId, email)))
        ]
    ]

/// Phase 547.B — one recently-expired invite, rendered greyed with an
/// **Expired** badge and a one-click **Re-issue** action. The re-issue
/// re-calls `IssuePendingInviteByEmail` with the original role and the
/// default expiry; the row then migrates back to the live list above.
let private expiredInviteRow
    (msgs: TeamManagerMessages)
    (teamId: string)
    (entry: TeamInviteExpiredPayload)
    (inFlight: bool)
    (dispatch: Msg -> unit)
    =
    Html.div [
        prop.className
            "flex items-center justify-between p-3 border border-border rounded-lg mb-2 bg-gray-50 opacity-70"
        prop.children [
            Html.div [
                prop.className "flex flex-col"
                prop.children [
                    Html.div [
                        prop.className "flex items-center gap-2"
                        prop.children [
                            Html.span [ prop.className "font-medium text-muted"; prop.text entry.InviteeEmail ]
                            Html.span [
                                prop.className "text-xs font-medium px-2 py-0.5 rounded-full bg-gray-200 text-gray-600"
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
            Html.button [
                prop.disabled inFlight
                prop.className [
                    "px-3 py-1.5 rounded-lg text-sm font-medium transition-colors border"
                    if inFlight then
                        "bg-gray-200 text-gray-500 border-border cursor-not-allowed"
                    else
                        "bg-white text-brand border-brand hover:bg-brand/10"
                ]
                prop.text (if inFlight then msgs.Reissuing else msgs.Reissue)
                prop.onClick (fun _ -> dispatch (ReissueExpiredInvite(teamId, entry.InviteeEmail, entry.Role)))
            ]
        ]
    ]

let private emptyPendingEmailState (msgs: TeamManagerMessages) =
    Html.p [ prop.className "text-sm text-muted py-4"; prop.text msgs.NoPendingInvites ]

let private issueByEmailModalView
    (msgs: TeamManagerMessages)
    (callerRole: TeamRole option)
    (state: IssueByEmailModalState)
    (dispatch: Msg -> unit)
    =
    let allowedRoles =
        match callerRole with
        | Some r -> assignableRoles r
        | None -> []

    let roleOption (role: TeamRole) =
        let isSelected = state.Role = role

        Html.button [
            prop.className [
                "px-3 py-1 rounded text-sm transition-colors border"
                if isSelected then
                    "bg-brand/10 text-brand border-brand"
                else
                    "bg-white text-text border-border hover:bg-gray-50"
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
                    Html.h3 [ prop.className "text-lg font-semibold"; prop.text msgs.InviteByEmailHeading ]
                    Html.p [ prop.className "text-sm text-muted"; prop.text msgs.InviteByEmailHelp ]
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
                        prop.placeholder msgs.EmailPlaceholder
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
                        prop.className "flex items-center gap-3 flex-wrap"
                        prop.children [
                            Html.span [ prop.className "text-sm text-muted"; prop.text msgs.RoleLabel ]
                            // `Owner` is excluded everywhere — initial
                            // ownership is set via Platform Management at
                            // team-create time. `Admin` is offered only
                            // to callers who can assign Admin (Owners +
                            // Platform Admins); Admin callers see only
                            // `Member`.
                            for role in allowedRoles do
                                roleOption role
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-center gap-3"
                        prop.children [
                            Html.label [ prop.className "text-sm text-muted"; prop.text msgs.ExpiresInDays ]
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
                            Forms.Button.secondary msgs.Cancel (fun () -> dispatch CloseIssueByEmailModal)
                            Html.button [
                                prop.disabled state.Submitting
                                prop.className [
                                    "px-4 py-2 rounded-lg text-sm font-medium transition-colors"
                                    if state.Submitting then
                                        "bg-gray-300 text-gray-500 cursor-not-allowed"
                                    else
                                        "bg-brand text-brand-text hover:bg-brand-dark"
                                ]
                                prop.text (
                                    if state.Submitting then
                                        msgs.Issuing
                                    else
                                        msgs.IssueInvitation
                                )
                                prop.onClick (fun _ -> dispatch SubmitIssueByEmail)
                            ]
                        ]
                    ]
                ]
            ]
        ]
    ]

let private revokeByEmailConfirmView
    (msgs: TeamManagerMessages)
    (teamId: string)
    (email: string)
    (model: Model)
    (dispatch: Msg -> unit)
    =
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
                    Html.h3 [ prop.className "text-lg font-semibold"; prop.text msgs.RevokeInviteHeading ]
                    Html.p [
                        prop.className "text-sm text-muted"
                        prop.text (sprintf "%s will no longer auto-join %s on sign-in." email teamName)
                    ]
                    Html.div [
                        prop.className "flex justify-end gap-3 pt-2"
                        prop.children [
                            Forms.Button.secondary msgs.Cancel (fun () -> dispatch CancelRevokeByEmail)
                            Forms.Button.primary msgs.RevokeInvite (fun () -> dispatch ConfirmRevokeByEmail)
                        ]
                    ]
                ]
            ]
        ]
    ]

let private pendingInvitesView (msgs: TeamManagerMessages) (teamId: string) (model: Model) (dispatch: Msg -> unit) =
    let teamName =
        model.Teams
        |> List.tryFind (fun t -> t.TeamId = teamId)
        |> Option.map _.Name
        |> Option.defaultValue teamId

    let entries = model.PendingByEmail |> Map.tryFind teamId
    let callerRole = effectiveCallerRole model teamId

    let subTab = EmailInvites

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
                    Html.span [ prop.className "font-medium"; prop.text teamName ]
                    Html.span [ prop.className "text-muted"; prop.text "/" ]
                    Html.span [ prop.className "font-medium"; prop.text msgs.PendingInvites ]
                ]
            ]

            Layout.Tabs.tabGroup [ EmailInvites, msgs.PendingInvitesPanel ] subTab (fun _ -> ())

            Layout.Panel.panel msgs.PendingInvitesPanel [
                Html.div [
                    prop.className "flex justify-end mb-3"
                    prop.children [
                        Forms.Button.primary msgs.InviteByEmail (fun () -> dispatch (OpenIssueByEmailModal teamId))
                    ]
                ]
                match entries with
                | None ->
                    Html.p [
                        prop.className "text-sm text-muted py-2"
                        prop.text msgs.PendingInvitesLoading
                    ]
                | Some [] -> emptyPendingEmailState msgs
                | Some rows ->
                    Html.div [
                        for email, entry in rows do
                            pendingEmailRow msgs teamId email entry dispatch
                    ]

                // Phase 547.B — recently-expired invites (30-day window,
                // read from the audit trail). Rendered only when there is
                // something to show — an empty history is not worth a
                // heading. Each row is greyed with an Expired badge and a
                // one-click Re-issue.
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
                                        model.ReissueInFlight
                                        |> Set.contains (sprintf "%s|%s" teamId entry.InviteeEmail)

                                    expiredInviteRow msgs teamId entry inFlight dispatch
                            ]
                        ]
                    ]
                | _ -> Html.none
            ]

            match model.IssueByEmailModal with
            | Some state when state.TeamId = teamId -> issueByEmailModalView msgs callerRole state dispatch
            | _ -> Html.none

            match model.RevokeByEmailConfirm with
            | Some(tid, email) when tid = teamId -> revokeByEmailConfirmView msgs tid email model dispatch
            | _ -> Html.none
        ]
    ]

/// Phase 444 — the module body as a React COMPONENT, so it has a hook
/// site from which to read the resolved catalog. See `HealthMonitorUI`'s
/// equivalent for why a module's `view` cannot hold the hook itself.
[<ReactComponent>]
let private TeamManagerBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).TeamManager

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
                        prop.text msgs.Dismiss
                        prop.onClick (fun _ -> dispatch DismissError)
                    ]
                ]
            ]
        | None -> Html.none

    let body =
        match model.CurrentView with
        | MyTeams -> myTeamsView msgs model dispatch
        | TeamDetails teamId -> teamDetailsView msgs teamId model dispatch
        | PendingInvites teamId -> pendingInvitesView msgs teamId model dispatch

    // 0.5.6 — settings-shape FullWidth render. Error banner stacks
    // above the body so it's immediately above the user's current
    // context instead of in a separate squashed right pane.
    Html.div [ prop.className "flex flex-col gap-3"; prop.children [ errorBanner; body ] ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = TeamManagerBody model dispatch

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
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// Phase 573.B — the administration-landing tile this built-in
/// contributes (see `HealthMonitorUI.adminTile` for the full
/// rationale). Team-scoped, so it carries the lightest weight of the
/// SDK tiles and leads the grid — mirroring the rail, where the
/// "Team Management" group sits above "Platform Management". Supply
/// `"_sdk.admin.teams"` from an `IHomeWidgetDataProvider` to lead with
/// a member count.
let adminTile (config: TeamManagerConfig option) : AdminTile =
    let name = config |> Option.map _.Name |> Option.defaultValue "Teams"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    {
        OwnerModuleId = "_sdk.TeamManager"
        Widget = {
            Id = "_sdk.tile.teams"
            Title = name
            Icon = icon
            Weight = 10
            Body =
                AdminTileBody.summary
                    "_sdk.admin.teams"
                    "Membership, invitations and roles for the teams you administer."
        }
    }