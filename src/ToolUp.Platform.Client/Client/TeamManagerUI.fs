// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TeamManagerUI

open System
open ToolUp.Elmish
open Feliz
open Fuaran.UI
open Fuaran.UI.Types
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
//
// Phase 1160: the surface is a Fuaran-UI tree, a pure function of `Model`,
// lowered once in `TeamManagerBody`. Two regions stay React behind islands
// because they own hooks of their own: the directory typeahead and the
// publication-readers panel.

let private teamNameOf (teamId: string) (model: Model) =
    model.Teams
    |> List.tryFind (fun t -> t.TeamId = teamId)
    |> Option.map _.Name
    |> Option.defaultValue teamId

let private teamRow (msgs: TeamManagerMessages) (team: TeamInfo) (isActive: bool) : Node<Msg> =
    let id = "team-" + team.TeamId

    TreeLowering.row id [
        TreeLowering.text (id + "-name") team.Name
        if isActive then
            Fuaran.badge (id + "-active") {
                Defaults.badge with
                    Label = TreeLowering.literal msgs.ActiveBadge
                    Variant = BadgeVariant.Brand
            }
        TreeLowering.text (id + "-id") (msgs.TeamIdLabel team.TeamId)
        if not isActive then
            TreeLowering.button
                (id + "-switch")
                ButtonVariant.Secondary
                msgs.Switch
                false
                (SwitchActiveTeam team.TeamId)
        TreeLowering.button (id + "-manage") ButtonVariant.Secondary msgs.Manage false (SelectTeam team.TeamId)
    ]

let private myTeamsView (msgs: TeamManagerMessages) (model: Model) : Node<Msg> =
    TreeLowering.card "my-teams" msgs.MyTeamsPanel [
        if model.Teams.IsEmpty then
            TreeLowering.text "no-teams" msgs.NoTeamsYet
        else
            for team in model.Teams do
                teamRow msgs team (model.ActiveTeamId = Some team.TeamId)
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

let private assignableTo (callerRole: TeamRole option) : TeamRole list =
    callerRole |> Option.map assignableRoles |> Option.defaultValue []

let private roleOptions (roles: TeamRole list) : Binding<SelectOption list> =
    Binding.Static(
        Some [
            for role in roles do
                let name = TeamRoles.displayName role in ({ Label = name; Value = name }: SelectOption)
        ]
    )

/// The role picker both invite forms use. `Owner` is excluded everywhere —
/// initial ownership is set via Platform Management at team-create time.
let private roleField
    (msgs: TeamManagerMessages)
    (roles: TeamRole list)
    (current: TeamRole)
    (onPick: TeamRole -> Msg)
    : FormField<Msg> =
    {
        Id = "role"
        Kind =
            FormFieldKind.segmentedChoiceControl (
                roleOptions roles,
                Binding.Static(Some(TeamRoles.displayName current)),
                onChange =
                    (fun value ->
                        value
                        |> Option.map (roleOfString >> onPick >> Action.dispatch)
                        |> Option.defaultValue Action.none)
            )
        Label = TreeLowering.literal msgs.RoleLabel
        Required = true
        Help = None
        Rule = None
    }

/// Best-effort human labels for a user id: the JWT display name for the caller
/// themselves, else the directory-resolved name and email, else the raw id.
let private labelsFor (directory: Map<string, UserSummary>) (selfId: string) (userId: string) =
    if userId = selfId then
        UserSession.getDisplayName () |> Option.defaultValue userId, UserSession.getEmail ()
    else
        match Map.tryFind userId directory with
        | Some s ->
            let name = s.DisplayName |> Option.filter (System.String.IsNullOrWhiteSpace >> not)
            let email = s.Email |> Option.filter (System.String.IsNullOrWhiteSpace >> not)

            match name, email with
            | Some n, _ -> n, email
            | None, Some e -> e, None
            | None, None -> userId, None
        | None -> userId, None

let private displayNameForId (directory: Map<string, UserSummary>) (selfId: string) (userId: string) : string =
    fst (labelsFor directory selfId userId)

let private memberRow
    (msgs: TeamManagerMessages)
    (teamId: string)
    (selfId: string)
    (callerRole: TeamRole option)
    (directory: Map<string, UserSummary>)
    (membership: TeamMembership)
    : Node<Msg> =
    let id = "member-" + membership.UserId
    let isSelf = membership.UserId = selfId

    // Caller may act on this membership when they hold a role whose
    // `canManageRole` covers the membership's current role. Owner →
    // any non-Owner; Admin → Member only; everyone else → no.
    let canManageTarget =
        match callerRole with
        | Some r -> TeamRoles.canManageRole r membership.Role
        | None -> false

    let primaryLabel, secondaryLabel = labelsFor directory selfId membership.UserId

    TreeLowering.row id [
        TreeLowering.text (id + "-name") primaryLabel

        match secondaryLabel with
        | Some email -> TreeLowering.text (id + "-email") email
        | None -> ()

        if canManageTarget && not isSelf then
            Fuaran.select (id + "-role") {
                Defaults.select with
                    Label = TreeLowering.literal msgs.RoleLabel
                    Source = roleOptions (assignableTo callerRole)
                    Value = Some(Binding.Static(Some(TeamRoles.displayName membership.Role)))
                    OnChange =
                        Some(fun value ->
                            let newRole =
                                value |> Option.map roleOfString |> Option.defaultValue membership.Role

                            if newRole <> membership.Role then
                                Action.dispatch (ChangeMemberRole(teamId, membership.UserId, newRole))
                            else
                                Action.none)
            }
        else
            Fuaran.badge (id + "-role") {
                Defaults.badge with
                    Label = TreeLowering.literal (TeamRoles.displayName membership.Role)
            }

        if isSelf then
            TreeLowering.text (id + "-you") msgs.YouSuffix

        if canManageTarget && not isSelf then
            TreeLowering.button
                (id + "-remove")
                ButtonVariant.Secondary
                msgs.RemoveMember
                false
                (RemoveMember(teamId, membership.UserId))
    ]

let private addMemberForm
    (msgs: TeamManagerMessages)
    (teamId: string)
    (callerRole: TeamRole option)
    (model: Model)
    (dispatch: Msg -> unit)
    : Node<Msg> =
    TreeLowering.card "add-member" msgs.InvitePanel [
        TreeLowering.text "add-member-help" msgs.InviteHelp

        TreeLowering.island "add-member-typeahead" (fun () ->
            UserDirectoryTypeahead.userTypeahead
                model.AddMemberUserId
                (fun v -> dispatch (SetAddMemberUserId v))
                msgs.InviteIdentifierPlaceholder
                UserDirectoryTypeahead.pickEmailPreferred)

        Fuaran.form "add-member-form" {
            Defaults.form with
                Fields = [
                    roleField msgs (assignableTo callerRole) model.AddMemberRole SetAddMemberRole
                ]
                OnSubmit = Action.dispatch (AddMember teamId)
                // Disabled while a previous add/invite is in flight, so a
                // double-click can't fire two requests.
                SubmitLabel =
                    TreeLowering.literal (
                        if model.AddMemberSubmitting then
                            msgs.Inviting
                        else
                            msgs.InviteMember
                    )
                Disabled = Some(Binding.Static(Some model.AddMemberSubmitting))
        }
    ]

let private modal (id: string) (heading: string) (onDismiss: Msg) (children: Node<Msg> list) : Node<Msg> =
    Fuaran.modal id {
        Defaults.modal with
            Open = Binding.Static(Some true)
            Heading = Some(TreeLowering.literal heading)
            OnDismiss = Some(Action.dispatch onDismiss)
            Children = children
    }

let private submitError (id: string) (error: string option) : Node<Msg> list =
    match error with
    | Some text -> [
        Fuaran.callout id {
            Defaults.callout with
                Body = TreeLowering.literal text
                Tone = ToneVariant.Critical
        }
      ]
    | None -> []

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
    : Node<Msg> =
    let selfId = selfUserId ()
    let members = model.Members |> Map.tryFind teamId |> Option.defaultValue []
    let teamName = teamNameOf teamId model

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

    match state.Step with
    | PickNewOwner ->
        modal "transfer" msgs.TransferOwnership CloseTransferOwnership [
            TreeLowering.text "transfer-help" (msgs.TransferOwnershipHelp teamName)

            Fuaran.form "transfer-filter" {
                Defaults.form with
                    Fields = [
                        {
                            Id = "transfer-query"
                            Kind =
                                FormFieldKind.textControl (
                                    Binding.Static(Some state.Query),
                                    onChange = (SetTransferQuery >> Action.dispatch)
                                )
                            Label = TreeLowering.literal msgs.TransferFilterPlaceholder
                            Required = false
                            Help = None
                            Rule = None
                        }
                    ]
                    SubmitLabel = TreeLowering.literal msgs.Cancel
                    OnSubmit = Action.dispatch CloseTransferOwnership
            }

            if List.isEmpty candidates then
                TreeLowering.text "transfer-none" msgs.TransferNoOtherMembers
            elif List.isEmpty filtered then
                TreeLowering.text "transfer-no-match" msgs.TransferNoMatches
            else
                for m in filtered do
                    TreeLowering.button
                        ("transfer-" + m.UserId)
                        ButtonVariant.Secondary
                        (displayNameForId model.Directory selfId m.UserId
                         + " · "
                         + TeamRoles.displayName m.Role)
                        false
                        (SelectTransferCandidate m.UserId)
        ]
    | ConfirmTransfer ->
        let newOwnerLabel =
            state.SelectedUserId
            |> Option.map (displayNameForId model.Directory selfId)
            |> Option.defaultValue msgs.TransferSelectedMemberFallback

        let outgoingLabel = displayNameForId model.Directory selfId selfId

        modal "transfer" msgs.TransferConfirmHeading CloseTransferOwnership [
            TreeLowering.text "transfer-prompt" (msgs.TransferConfirmPrompt teamName outgoingLabel newOwnerLabel)
            TreeLowering.text "transfer-role" (msgs.TransferRoleExplanation newOwnerLabel)
            yield! submitError "transfer-error" state.SubmitError

            TreeLowering.row "transfer-actions" [
                TreeLowering.button "transfer-back" ButtonVariant.Secondary msgs.Back false BackToTransferPick
                TreeLowering.button
                    "transfer-confirm"
                    ButtonVariant.Primary
                    (if state.Submitting then
                         msgs.Transferring
                     else
                         msgs.ConfirmTransfer)
                    state.Submitting
                    SubmitTransferOwnership
            ]
        ]

let private teamDetailsView
    (msgs: TeamManagerMessages)
    (teamId: string)
    (model: Model)
    (dispatch: Msg -> unit)
    : Node<Msg> =
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

    TreeLowering.column "team-details" [
        TreeLowering.row "breadcrumb" [
            TreeLowering.button "breadcrumb-teams" ButtonVariant.Tertiary msgs.BreadcrumbMyTeams false BackToMyTeams
            TreeLowering.text "breadcrumb-team" (teamNameOf teamId model)

            if canManage then
                if isRealOwner then
                    TreeLowering.button
                        "open-transfer"
                        ButtonVariant.Secondary
                        msgs.TransferOwnership
                        false
                        (OpenTransferOwnership teamId)

                TreeLowering.button
                    "open-pending"
                    ButtonVariant.Secondary
                    msgs.PendingInvites
                    false
                    (NavigatePendingInvites teamId)
        ]

        TreeLowering.card "members" msgs.MembersPanel [
            if members.IsEmpty then
                TreeLowering.text "no-members" msgs.NoMembers
            else
                for m in members do
                    memberRow msgs teamId selfId callerRole model.Directory m
        ]

        if canManage then
            addMemberForm msgs teamId callerRole model dispatch

        // Phase 1107 — the team's in-platform publication readers, for
        // its real Owner or Admin (no platform-admin bypass, as on the
        // server).
        TreeLowering.island "publication-readers" (fun () ->
            PublicationReadersAdminUI.panel
                msgs
                teamId
                model.ActiveTeamId
                (model.RoleInTeam
                 |> Map.tryFind teamId
                 |> Option.exists TeamRoles.canWriteTeamConfig))

        match model.TransferOwnershipModal with
        | Some state when state.TeamId = teamId -> transferOwnershipModalView msgs teamId state model
        | _ -> ()
    ]

// ─── Phase 3d.A — Pending Invites view (email sub-tab) ──────────────

let private pendingEmailRow
    (msgs: TeamManagerMessages)
    (teamId: string)
    (email: string)
    (entry: PendingInviteByEmail)
    : Node<Msg> =
    let id = "pending-" + email

    TreeLowering.row id [
        TreeLowering.text (id + "-email") email
        TreeLowering.text
            (id + "-expires")
            (msgs.InviteExpires (TeamRoles.displayName entry.Role) (entry.ExpiresAt.ToString "yyyy-MM-dd"))
        TreeLowering.button
            (id + "-revoke")
            ButtonVariant.Secondary
            msgs.RevokeInvite
            false
            (OpenRevokeByEmailConfirm(teamId, email))
    ]

/// Phase 547.B — one recently-expired invite, with an **Expired** badge and a
/// one-click **Re-issue** action. The re-issue re-calls
/// `IssuePendingInviteByEmail` with the original role and the default expiry;
/// the row then migrates back to the live list above.
let private expiredInviteRow
    (msgs: TeamManagerMessages)
    (teamId: string)
    (entry: TeamInviteExpiredPayload)
    (inFlight: bool)
    : Node<Msg> =
    let id = "expired-" + entry.InviteeEmail

    TreeLowering.row id [
        TreeLowering.text (id + "-email") entry.InviteeEmail
        Fuaran.badge (id + "-badge") {
            Defaults.badge with
                Label = TreeLowering.literal msgs.Expired
        }
        TreeLowering.text
            (id + "-when")
            (msgs.InviteExpired (TeamRoles.displayName entry.Role) (entry.ExpiredAt.ToString "yyyy-MM-dd"))
        TreeLowering.button
            (id + "-reissue")
            ButtonVariant.Secondary
            (if inFlight then msgs.Reissuing else msgs.Reissue)
            inFlight
            (ReissueExpiredInvite(teamId, entry.InviteeEmail, entry.Role))
    ]

/// The validated form this phase names: a required, email-formatted address,
/// a role and a positive lifetime. The renderer's submit gate (Phase 864 on the
/// language side) holds an invalid form back and marks the unmet field; the
/// server-bound check in `update` stays, because a tree is a view and `update`
/// is the module's contract.
let private issueByEmailModalView
    (msgs: TeamManagerMessages)
    (callerRole: TeamRole option)
    (state: IssueByEmailModalState)
    : Node<Msg> =
    modal "issue-by-email" msgs.InviteByEmailHeading CloseIssueByEmailModal [
        TreeLowering.text "issue-help" msgs.InviteByEmailHelp

        Fuaran.form "issue-form" {
            Defaults.form with
                Fields = [
                    {
                        Id = "email"
                        Kind =
                            FormFieldKind.textControl (
                                Binding.Static(Some state.Email),
                                onChange = (SetIssueByEmailEmail >> Action.dispatch)
                            )
                        Label = TreeLowering.literal msgs.EmailPlaceholder
                        Required = true
                        Help = None
                        Rule =
                            Some {
                                Compare = None
                                Format = Some TextFormat.Email
                                MaxLength = None
                                Message = Some(TreeLowering.literal msgs.EmailRequired)
                                MinLength = None
                                Pattern = None
                            }
                    }
                    roleField msgs (assignableTo callerRole) state.Role SetIssueByEmailRole
                    {
                        Id = "expires"
                        Kind =
                            FormFieldKind.rangedNumberControl (
                                Binding.Static(Some(float state.ExpiresInDays)),
                                onChange = (int >> SetIssueByEmailExpiresInDays >> Action.dispatch),
                                min = 1.0
                            )
                        Label = TreeLowering.literal msgs.ExpiresInDays
                        Required = true
                        Help = None
                        Rule = None
                    }
                ]
                OnSubmit = Action.dispatch SubmitIssueByEmail
                SubmitLabel =
                    TreeLowering.literal (
                        if state.Submitting then
                            msgs.Issuing
                        else
                            msgs.IssueInvitation
                    )
                Disabled = Some(Binding.Static(Some state.Submitting))
        }

        yield! submitError "issue-error" state.SubmitError
        TreeLowering.button "issue-cancel" ButtonVariant.Secondary msgs.Cancel false CloseIssueByEmailModal
    ]

let private revokeByEmailConfirmView
    (msgs: TeamManagerMessages)
    (teamId: string)
    (email: string)
    (model: Model)
    : Node<Msg> =
    modal "revoke" msgs.RevokeInviteHeading CancelRevokeByEmail [
        TreeLowering.text "revoke-prompt" (msgs.RevokeInvitePrompt email (teamNameOf teamId model))
        TreeLowering.row "revoke-actions" [
            TreeLowering.button "revoke-cancel" ButtonVariant.Secondary msgs.Cancel false CancelRevokeByEmail
            TreeLowering.button "revoke-confirm" ButtonVariant.Primary msgs.RevokeInvite false ConfirmRevokeByEmail
        ]
    ]

let private pendingInvitesView (msgs: TeamManagerMessages) (teamId: string) (model: Model) : Node<Msg> =
    let entries = model.PendingByEmail |> Map.tryFind teamId
    let callerRole = effectiveCallerRole model teamId

    TreeLowering.column "pending-invites" [
        TreeLowering.row "breadcrumb" [
            TreeLowering.button
                "breadcrumb-members"
                ButtonVariant.Tertiary
                msgs.BreadcrumbMembers
                false
                (SelectTeam teamId)
            TreeLowering.text "breadcrumb-team" (teamNameOf teamId model)
            TreeLowering.text "breadcrumb-page" msgs.PendingInvites
        ]

        TreeLowering.card "pending" msgs.PendingInvitesPanel [
            TreeLowering.button
                "invite-by-email"
                ButtonVariant.Primary
                msgs.InviteByEmail
                false
                (OpenIssueByEmailModal teamId)

            match entries with
            | None -> TreeLowering.text "pending-loading" msgs.PendingInvitesLoading
            | Some [] -> TreeLowering.text "pending-none" msgs.NoPendingInvites
            | Some rows ->
                for email, entry in rows do
                    pendingEmailRow msgs teamId email entry

            // Phase 547.B — recently-expired invites (30-day window, read
            // from the audit trail). Rendered only when there is something to
            // show — an empty history is not worth a heading.
            match model.ExpiredInvites |> Map.tryFind teamId with
            | Some expired when not (List.isEmpty expired) ->
                TreeLowering.heading "expired-heading" 4 msgs.RecentlyExpired

                for entry in expired do
                    let inFlight =
                        model.ReissueInFlight
                        |> Set.contains (sprintf "%s|%s" teamId entry.InviteeEmail)

                    expiredInviteRow msgs teamId entry inFlight
            | _ -> ()
        ]

        match model.IssueByEmailModal with
        | Some state when state.TeamId = teamId -> issueByEmailModalView msgs callerRole state
        | _ -> ()

        match model.RevokeByEmailConfirm with
        | Some(tid, email) when tid = teamId -> revokeByEmailConfirmView msgs tid email model
        | _ -> ()
    ]

/// The whole surface as a value.
let internal surface (msgs: TeamManagerMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    TreeLowering.column "team-manager" [
        match model.Error with
        | Some text -> TreeLowering.banner "error" ToneVariant.Critical text msgs.Dismiss DismissError
        | None -> ()

        match model.CurrentView with
        | MyTeams -> myTeamsView msgs model
        | TeamDetails teamId -> teamDetailsView msgs teamId model dispatch
        | PendingInvites teamId -> pendingInvitesView msgs teamId model
    ]

/// Phase 444 — the module body as a React COMPONENT, so it has a hook
/// site from which to read the resolved catalog. See `HealthMonitorUI`'s
/// equivalent for why a module's `view` cannot hold the hook itself.
[<ReactComponent>]
let private TeamManagerBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).TeamManager
    TreeLowering.lower HostBindingSources.empty dispatch (surface msgs model dispatch)

let internal view (model: Model) (dispatch: Msg -> unit) : ReactElement = TeamManagerBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.TeamManager"

/// Create the built-in team manager as an `ErasedModule`. It fills the
/// `TeamManager` shell slot (Default / Configured), which the shell
/// admits on any `Team` surface.
let create (config: ModuleLabel option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Teams"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.users

    let route = "/" + name.ToLower().Replace(" ", "-")

    // SDK-built-in module — `Id` is reserved under the `_sdk.` namespace so
    // it can never collide with an app's RBAC-managed `ServerConfig.ModuleNames`
    // key. A deployment's own module in the slot carries its own Id.
    // `init` here is `ClientModuleContext -> Model * Cmd<Msg>` so we use
    // `withContextInit` to override `create`'s unit-init default.
    ToolUp.Platform.ClientModule.create {
        Init = fun () -> init ToolUp.Platform.ClientModuleContext.empty
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withContextInit init
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// Phase 573.B — the administration-landing tile of this built-in's
/// slot (see `HealthMonitorUI.adminTile` for the full rationale): built
/// for whichever module fills the slot (`owner`), so a replacement keeps
/// the tile, with its own id, name and icon.
///
/// Team-scoped, so it carries the lightest weight of the SDK tiles and
/// leads the grid — mirroring the rail, where the "Team Management"
/// group sits above "Platform Management". Supply `"_sdk.admin.teams"`
/// from an `IHomeWidgetDataProvider` to lead with a member count.
let adminTile (owner: ModuleDefinition) : AdminTile = {
    OwnerModuleId = owner.Id
    Widget = {
        Id = "_sdk.tile.teams"
        Title = owner.Name
        Icon = owner.Icon
        Weight = 10
        Body =
            AdminTileBody.summary "_sdk.admin.teams" "Membership, invitations and roles for the teams you administer."
    }
}