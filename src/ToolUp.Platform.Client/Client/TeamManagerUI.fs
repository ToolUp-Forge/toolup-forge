// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TeamManagerUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── API proxies ─────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private teamApi: TeamApi =
    Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

// `PlatformAdminApi.IsPlatformAdmin` drives the Platform-Admin bypass on the management controls.
let private platformAdminApi: PlatformAdminApi =
    Api.makeProxy<PlatformAdminApi> (customOptions = UserSession.withRequestHeaders)

// `ITeamInviteApi.{Issue,List,Revoke}PendingInviteByEmail` drive the Pending Invites view. Routes live under
// `/api/team-invite/*` per `TeamInviteApi.routeBuilder`.
let private inviteApi: ITeamInviteApi =
    Api.makeProxy<ITeamInviteApi> (
        routeBuilder = TeamInviteApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Sub-models ──────────────────────────────────────────────────────

/// The "Invite by email" modal: an email, a role and a lifetime, issued as a
/// pending invite that the invitee's first sign-in claims.
module IssueByEmail =
    type Model = {
        TeamId: string
        Email: string
        Role: TeamRole
        /// Lifetime in days; the request carries it as a `TimeSpan`.
        ExpiresInDays: int
        /// Shown when the email is blank — the module catalog's string, held so `update` needs no parent.
        EmailRequired: string
        /// The handler's error from the last submit; the modal stays open for a retry.
        SubmitError: string option
        Submitting: bool
    }

    type Msg =
        | SetEmail of string
        | SetRole of TeamRole
        | SetExpiresInDays of int
        | Submit
        | Submitted of Result<unit, string>
        | Close

    /// What the parent does after a step.
    type Outcome =
        | Stay
        | Closed
        /// The invite was issued: close, and reload the team's pending invites.
        | Issued of teamId: string

    let init (teamId: string) (emailRequired: string) : Model = {
        TeamId = teamId
        Email = ""
        Role = Member
        ExpiresInDays = 7
        EmailRequired = emailRequired
        SubmitError = None
        Submitting = false
    }

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> * Outcome =
        match msg with
        | SetEmail email -> { model with Email = email }, Cmd.none, Stay
        | SetRole role -> { model with Role = role }, Cmd.none, Stay
        | SetExpiresInDays days ->
            {
                model with
                    ExpiresInDays = max 1 days
            },
            Cmd.none,
            Stay
        | Submit when model.Submitting -> model, Cmd.none, Stay
        | Submit when model.Email.Trim() = "" ->
            {
                model with
                    SubmitError = Some model.EmailRequired
            },
            Cmd.none,
            Stay
        | Submit ->
            let request: PendingInviteIssueRequest = {
                TeamId = model.TeamId
                Email = model.Email.Trim()
                Role = model.Role
                ExpiresIn = Some(TimeSpan.FromDays(float model.ExpiresInDays))
            }

            {
                model with
                    Submitting = true
                    SubmitError = None
            },
            Cmd.OfRemoting.call inviteApi.IssuePendingInviteByEmail request Submitted (fun e ->
                Submitted(Error e.Message)),
            Stay
        | Submitted(Ok()) -> model, Cmd.none, Issued model.TeamId
        | Submitted(Error e) ->
            {
                model with
                    Submitting = false
                    SubmitError = Some e
            },
            Cmd.none,
            Stay
        | Close -> model, Cmd.none, Closed

/// The two-step ownership transfer. Step one picks the incoming Owner from
/// the team's current members through a filter; step two confirms, naming
/// the outgoing and the incoming Owner, before the API fires.
module TransferOwnership =
    type Step =
        | PickNewOwner
        | ConfirmTransfer

    type Model = {
        TeamId: string
        /// The filter over the team's current members (step one).
        Query: string
        /// The chosen incoming Owner; picking one advances to `ConfirmTransfer`.
        SelectedUserId: string option
        Step: Step
        Submitting: bool
        /// The handler's error from the last submit; the confirm step stays open so the operator sees why.
        SubmitError: string option
    }

    type Msg =
        | SetQuery of string
        | SelectCandidate of userId: string
        | BackToPick
        | Submit
        | Done of Result<unit, string>
        | Close

    /// What the parent does after a step.
    type Outcome =
        | Stay
        | Closed
        /// Ownership moved: close, and reload the members so every role badge shows it.
        | Transferred of teamId: string

    let init (teamId: string) : Model = {
        TeamId = teamId
        Query = ""
        SelectedUserId = None
        Step = PickNewOwner
        Submitting = false
        SubmitError = None
    }

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> * Outcome =
        match msg with
        | SetQuery query -> { model with Query = query }, Cmd.none, Stay
        | SelectCandidate userId ->
            {
                model with
                    SelectedUserId = Some userId
                    Step = ConfirmTransfer
                    SubmitError = None
            },
            Cmd.none,
            Stay
        | BackToPick ->
            {
                model with
                    Step = PickNewOwner
                    SubmitError = None
            },
            Cmd.none,
            Stay
        | Submit ->
            match model.SelectedUserId with
            | Some target when not model.Submitting ->
                {
                    model with
                        Submitting = true
                        SubmitError = None
                },
                Cmd.OfRemoting.call teamApi.TransferOwnership (model.TeamId, target) Done (fun e ->
                    Done(Error e.Message)),
                Stay
            | _ -> model, Cmd.none, Stay
        | Done(Ok()) -> model, Cmd.none, Transferred model.TeamId
        | Done(Error e) ->
            {
                model with
                    Submitting = false
                    SubmitError = Some e
            },
            Cmd.none,
            Stay
        | Close -> model, Cmd.none, Closed

/// The confirmation before a pending email invite is revoked.
module RevokeByEmail =
    type Model = {
        TeamId: string
        Email: string
        Submitting: bool
    }

    type Msg =
        | Confirm
        | Done of Result<unit, string>
        | Cancel

    /// What the parent does after a step.
    type Outcome =
        | Stay
        | Closed
        /// Revoked: close, and reload the team's pending invites.
        | Revoked of teamId: string
        /// The revoke failed: close, and show the error on the page.
        | Failed of error: string

    let init (teamId: string) (email: string) : Model = {
        TeamId = teamId
        Email = email
        Submitting = false
    }

    let update (msg: Msg) (model: Model) : Model * Cmd<Msg> * Outcome =
        match msg with
        | Confirm when model.Submitting -> model, Cmd.none, Stay
        | Confirm ->
            { model with Submitting = true },
            Cmd.OfRemoting.call inviteApi.RevokePendingInviteByEmail model.Email Done (fun e -> Done(Error e.Message)),
            Stay
        | Done(Ok()) -> model, Cmd.none, Revoked model.TeamId
        | Done(Error e) -> model, Cmd.none, Failed e
        | Cancel -> model, Cmd.none, Closed

// ─── Model ───────────────────────────────────────────────────────────

/// Which sub-view is currently active inside the team manager page.
type View =
    | MyTeams
    | TeamDetails of teamId: string
    /// The pending-invite admin surface for one team.
    | PendingInvites of teamId: string

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
    /// A single page-level error notice. The whole page dismisses on
    /// action — simpler than per-form error state for an admin UI.
    Error: Notice option
    /// Add-member form state for a given teamId.
    AddMemberUserId: string
    AddMemberRole: TeamRole
    /// In-flight guard for the page-level "Add a member" form, so a
    /// double-click cannot fire two invites / adds.
    AddMemberSubmitting: bool
    /// Shell-supplied callback fired when the active team changes.
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
    /// Pending-by-email entries per team, loaded on entry to the
    /// `PendingInvites` view. `Map.tryFind` `None` = not loaded yet; an
    /// empty list = the empty-state UI.
    PendingByEmail: Map<string, (string * PendingInviteByEmail) list>
    /// The "Invite by email" modal. `None` = closed.
    IssueByEmail: IssueByEmail.Model option
    /// The revoke confirmation. `None` = closed.
    RevokeByEmail: RevokeByEmail.Model option
    /// Recently-expired pending-by-email invites per team, loaded
    /// alongside `PendingByEmail` (30-day window, read from the audit
    /// trail). `Map.tryFind` `None` = not loaded yet; an empty list hides
    /// the section.
    ExpiredInvites: Map<string, TeamInviteExpiredPayload list>
    /// Re-issue calls in flight, keyed `(teamId, email)`. Disables that
    /// row's Re-issue button so a double-click cannot fire two issues.
    ReissueInFlight: Set<string * string>
    /// The ownership-transfer modal. `None` = closed.
    Transfer: TransferOwnership.Model option
    /// The member ids resolved to names and emails. An unresolved id
    /// renders raw, so the member list stays functional without a
    /// directory companion.
    Directory: UserDirectory.Model
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: TeamManagerMessages
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
    /// The "Add a member" form's email path, issued as a pending invite.
    | AddMemberInvited of teamId: string * Result<unit, string>
    | RemoveMember of teamId: string * userId: string
    | MemberRemoved of Result<unit, string>
    | ChangeMemberRole of teamId: string * userId: string * newRole: TeamRole
    | MemberRoleChanged of Result<unit, string>
    | IsPlatformAdminLoaded of bool
    | OpenTransferOwnership of teamId: string
    | TransferMsg of TransferOwnership.Msg
    | NavigatePendingInvites of teamId: string
    | LoadPendingByEmail of teamId: string
    | PendingByEmailLoaded of teamId: string * (string * PendingInviteByEmail) list
    | OpenIssueByEmail of teamId: string
    | IssueByEmailMsg of IssueByEmail.Msg
    | OpenRevokeByEmail of teamId: string * email: string
    | RevokeByEmailMsg of RevokeByEmail.Msg
    | LoadExpiredInvites of teamId: string
    | ExpiredInvitesLoaded of teamId: string * TeamInviteExpiredPayload list
    | ReissueExpiredInvite of teamId: string * email: string * role: TeamRole
    | ExpiredInviteReissued of teamId: string * email: string * Result<unit, string>
    | DirectoryMsg of UserDirectory.Msg
    | ApiError of string
    | DismissError

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
        IssueByEmail = None
        RevokeByEmail = None
        ExpiredInvites = Map.empty
        ReissueInFlight = Set.empty
        Transfer = None
        Directory = UserDirectory.empty
        Messages = ctx.Messages.TeamManager
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

/// Reload the members of the team on screen, if one is.
let private refreshCurrentTeam (model: Model) =
    match model.CurrentView with
    | TeamDetails teamId -> Cmd.ofMsg (LoadMembers teamId)
    | _ -> Cmd.none

/// Reload both invite lists of a team: a change to one moves an email between them.
let private reloadInvites (teamId: string) =
    Cmd.batch [ Cmd.ofMsg (LoadPendingByEmail teamId); Cmd.ofMsg (LoadExpiredInvites teamId) ]

/// The "Add a member" form: an email becomes a pending invite, anything else a direct add.
let private addMember (teamId: string) (model: Model) =
    let raw = model.AddMemberUserId.Trim()

    if raw = "" then
        {
            model with
                Error = Some(Notice.Problem model.Messages.IdentifierRequired)
        },
        Cmd.none
    // An email goes through the pending-invite flow, never straight
    // to `AddTeamMember`: a membership keyed by the email string would
    // never match the invitee's token, whose user id is the identity
    // provider's (`oid` / `sub`), so the team would stay invisible to
    // them. A pending entry keyed by email is instead claimed at
    // sign-in, when `ScopeResolutionMiddleware` matches the email claim
    // and creates the membership under the resolved user id.
    //
    // Detection: a simple `@` + dot heuristic catches the common
    // email shape. An identifier that is not an email (a UUID-shaped
    // `oid`, a base64url `sub`) takes the direct-add path — useful for
    // SSO replication scripts that already know the resolved id.
    elif raw.Contains('@') && raw.IndexOf('.', raw.IndexOf('@')) > 0 then
        let request: PendingInviteIssueRequest = {
            TeamId = teamId
            Email = raw
            Role = model.AddMemberRole
            ExpiresIn = Some(TimeSpan.FromDays 7.0)
        }

        {
            model with
                AddMemberSubmitting = true
        },
        Cmd.OfRemoting.call inviteApi.IssuePendingInviteByEmail request (fun r -> AddMemberInvited(teamId, r)) (fun e ->
            AddMemberInvited(teamId, Error e.Message))
    else
        {
            model with
                AddMemberSubmitting = true
        },
        Cmd.OfRemoting.call teamApi.AddTeamMember (teamId, raw, model.AddMemberRole) MemberAdded (fun e ->
            ApiError e.Message)

/// Route a step of the transfer modal and act on its outcome.
let private routeTransfer (subMsg: TransferOwnership.Msg) (model: Model) =
    match model.Transfer with
    | None -> model, Cmd.none
    | Some transfer ->
        let transfer, cmd, outcome = TransferOwnership.update subMsg transfer
        let cmd = Cmd.map TransferMsg cmd

        match outcome with
        | TransferOwnership.Stay -> { model with Transfer = Some transfer }, cmd
        | TransferOwnership.Closed -> { model with Transfer = None }, cmd
        | TransferOwnership.Transferred teamId ->
            {
                model with
                    Transfer = None
                    Error = None
            },
            Cmd.batch [ cmd; Cmd.ofMsg (LoadMembers teamId) ]

/// Route a step of the invite-by-email modal and act on its outcome.
let private routeIssueByEmail (subMsg: IssueByEmail.Msg) (model: Model) =
    match model.IssueByEmail with
    | None -> model, Cmd.none
    | Some issue ->
        let issue, cmd, outcome = IssueByEmail.update subMsg issue
        let cmd = Cmd.map IssueByEmailMsg cmd

        match outcome with
        | IssueByEmail.Stay -> { model with IssueByEmail = Some issue }, cmd
        | IssueByEmail.Closed -> { model with IssueByEmail = None }, cmd
        | IssueByEmail.Issued teamId ->
            {
                model with
                    IssueByEmail = None
                    Error = None
            },
            Cmd.batch [ cmd; Cmd.ofMsg (LoadPendingByEmail teamId) ]

/// Route a step of the revoke confirmation and act on its outcome.
let private routeRevokeByEmail (subMsg: RevokeByEmail.Msg) (model: Model) =
    match model.RevokeByEmail with
    | None -> model, Cmd.none
    | Some revoke ->
        let revoke, cmd, outcome = RevokeByEmail.update subMsg revoke
        let cmd = Cmd.map RevokeByEmailMsg cmd

        match outcome with
        | RevokeByEmail.Stay ->
            {
                model with
                    RevokeByEmail = Some revoke
            },
            cmd
        | RevokeByEmail.Closed -> { model with RevokeByEmail = None }, cmd
        | RevokeByEmail.Revoked teamId ->
            { model with RevokeByEmail = None }, Cmd.batch [ cmd; Cmd.ofMsg (LoadPendingByEmail teamId) ]
        | RevokeByEmail.Failed e ->
            {
                model with
                    RevokeByEmail = None
                    Error = Some(Notice.Problem e)
            },
            cmd

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
        let roleMap =
            match roleFor (selfUserId ()) members with
            | Some r -> model.RoleInTeam |> Map.add teamId r
            | None -> model.RoleInTeam

        {
            model with
                Members = model.Members |> Map.add teamId members
                RoleInTeam = roleMap
        },
        UserDirectory.resolve (members |> List.map _.UserId) model.Directory
        |> Cmd.map DirectoryMsg

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

    | SetAddMemberUserId id -> { model with AddMemberUserId = id }, Cmd.none

    | SetAddMemberRole role -> { model with AddMemberRole = role }, Cmd.none

    // Ignore a second click while an add/invite is already in flight.
    | AddMember _ when model.AddMemberSubmitting -> model, Cmd.none

    | AddMember teamId -> addMember teamId model

    | MemberAdded(Ok()) ->
        {
            model with
                AddMemberUserId = ""
                AddMemberSubmitting = false
                Error = None
        },
        refreshCurrentTeam model

    | AddMemberInvited(teamId, Ok()) ->
        {
            model with
                AddMemberUserId = ""
                AddMemberSubmitting = false
                Error = None
        },
        Cmd.ofMsg (LoadPendingByEmail teamId)

    | MemberAdded(Error e)
    | AddMemberInvited(_, Error e) ->
        {
            model with
                AddMemberSubmitting = false
                Error = Some(Notice.Problem e)
        },
        Cmd.none

    | RemoveMember(teamId, userId) ->
        model, Cmd.OfRemoting.call teamApi.RemoveTeamMember (teamId, userId) MemberRemoved (fun e -> ApiError e.Message)

    | ChangeMemberRole(teamId, targetUserId, newRole) ->
        model,
        Cmd.OfRemoting.call teamApi.ChangeMemberRole (teamId, targetUserId, newRole) MemberRoleChanged (fun e ->
            ApiError e.Message)

    | MemberRemoved(Ok())
    | MemberRoleChanged(Ok()) -> { model with Error = None }, refreshCurrentTeam model

    | IsPlatformAdminLoaded isAdmin ->
        {
            model with
                IsPlatformAdmin = Some isAdmin
        },
        Cmd.none

    | OpenTransferOwnership teamId ->
        {
            model with
                Transfer = Some(TransferOwnership.init teamId)
        },
        Cmd.none

    | TransferMsg subMsg -> routeTransfer subMsg model

    | NavigatePendingInvites teamId ->
        {
            model with
                CurrentView = PendingInvites teamId
        },
        reloadInvites teamId

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

    | OpenIssueByEmail teamId ->
        {
            model with
                IssueByEmail = Some(IssueByEmail.init teamId model.Messages.EmailRequired)
        },
        Cmd.none

    | IssueByEmailMsg subMsg -> routeIssueByEmail subMsg model

    | OpenRevokeByEmail(teamId, email) ->
        {
            model with
                RevokeByEmail = Some(RevokeByEmail.init teamId email)
        },
        Cmd.none

    | RevokeByEmailMsg subMsg -> routeRevokeByEmail subMsg model

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
        // server-side).
        let request: PendingInviteIssueRequest = {
            TeamId = teamId
            Email = email
            Role = role
            ExpiresIn = None
        }

        {
            model with
                ReissueInFlight = model.ReissueInFlight |> Set.add (teamId, email)
        },
        Cmd.OfRemoting.call
            inviteApi.IssuePendingInviteByEmail
            request
            (fun r -> ExpiredInviteReissued(teamId, email, r))
            (fun e -> ExpiredInviteReissued(teamId, email, Error e.Message))

    | ExpiredInviteReissued(teamId, email, result) ->
        let cleared = {
            model with
                ReissueInFlight = model.ReissueInFlight |> Set.remove (teamId, email)
        }

        match result with
        // The email gains a live pending entry, which also removes it from the server's expired projection.
        | Ok() -> cleared, reloadInvites teamId
        | Error e ->
            {
                cleared with
                    Error = Some(Notice.Problem e)
            },
            Cmd.none

    | DirectoryMsg subMsg ->
        let directory, cmd = UserDirectory.update subMsg model.Directory
        { model with Directory = directory }, Cmd.map DirectoryMsg cmd

    | ActiveTeamSwitched(_, Error e)
    | MemberRemoved(Error e)
    | MemberRoleChanged(Error e)
    | ApiError e ->
        {
            model with
                Error = Some(Notice.Problem e)
        },
        Cmd.none

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
let private assignableRoles (callerRole: TeamRole option) : TeamRole list =
    match callerRole with
    | Some Owner -> [ Member; Admin ]
    | Some Admin -> [ Member ]
    | Some Member
    | None -> []

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

/// The caller's own id labels with the session's display name; every other
/// id with the directory's label.
let private labelFor (directory: UserDirectory.Model) (selfId: string) (userId: string) : string =
    if userId = selfId then
        UserSession.getDisplayName () |> Option.defaultValue userId
    else
        UserDirectory.label directory userId

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

let private teamNameOf (model: Model) (teamId: string) =
    model.Teams
    |> List.tryFind (fun t -> t.TeamId = teamId)
    |> Option.map _.Name
    |> Option.defaultValue teamId

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
            | Some state when state.TeamId = teamId -> transferOwnershipView msgs state model (TransferMsg >> dispatch)
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
                                    let inFlight = model.ReissueInFlight |> Set.contains (teamId, entry.InviteeEmail)
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
            | Some state when state.TeamId = teamId -> revokeByEmailView msgs state model (RevokeByEmailMsg >> dispatch)
            | _ -> Html.none
        ]
    ]

/// The module body as a React COMPONENT, so it has a hook site from which
/// to read the resolved catalog. See `HealthMonitorUI`'s equivalent for
/// why a module's `view` cannot hold the hook itself.
[<ReactComponent>]
let private TeamManagerBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).TeamManager

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

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = TeamManagerBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
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

/// The administration-landing tile of this built-in's
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