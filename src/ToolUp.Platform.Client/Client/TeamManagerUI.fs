// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module TeamManagerUI

open Fuaran.MVU
open System
open Fable.Core
open Fable.Core.JsInterop
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

    /// The transitions, with the remote call as a value.
    let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list * Outcome =
        match msg with
        | SetEmail email -> { model with Email = email }, [], Stay
        | SetRole role -> { model with Role = role }, [], Stay
        | SetExpiresInDays days ->
            {
                model with
                    ExpiresInDays = max 1 days
            },
            [],
            Stay
        | Submit when model.Submitting -> model, [], Stay
        | Submit when model.Email.Trim() = "" ->
            {
                model with
                    SubmitError = Some model.EmailRequired
            },
            [],
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
            [
                Do(
                    RemoteEffect.call
                        "ITeamInviteApi.IssuePendingInviteByEmail"
                        inviteApi.IssuePendingInviteByEmail
                        request
                        (RemoteEffect.flatten >> Submitted)
                )
            ],
            Stay
        | Submitted(Ok()) -> model, [], Issued model.TeamId
        | Submitted(Error e) ->
            {
                model with
                    Submitting = false
                    SubmitError = Some e
            },
            [],
            Stay
        | Close -> model, [], Closed

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

    /// The transitions, with the remote call as a value.
    let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list * Outcome =
        match msg with
        | SetQuery query -> { model with Query = query }, [], Stay
        | SelectCandidate userId ->
            {
                model with
                    SelectedUserId = Some userId
                    Step = ConfirmTransfer
                    SubmitError = None
            },
            [],
            Stay
        | BackToPick ->
            {
                model with
                    Step = PickNewOwner
                    SubmitError = None
            },
            [],
            Stay
        | Submit ->
            match model.SelectedUserId with
            | Some target when not model.Submitting ->
                {
                    model with
                        Submitting = true
                        SubmitError = None
                },
                [
                    Do(
                        RemoteEffect.call
                            "TeamApi.TransferOwnership"
                            teamApi.TransferOwnership
                            (model.TeamId, target)
                            (RemoteEffect.flatten >> Done)
                    )
                ],
                Stay
            | _ -> model, [], Stay
        | Done(Ok()) -> model, [], Transferred model.TeamId
        | Done(Error e) ->
            {
                model with
                    Submitting = false
                    SubmitError = Some e
            },
            [],
            Stay
        | Close -> model, [], Closed

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

    /// The transitions, with the remote call as a value.
    let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list * Outcome =
        match msg with
        | Confirm when model.Submitting -> model, [], Stay
        | Confirm ->
            { model with Submitting = true },
            [
                Do(
                    RemoteEffect.call
                        "ITeamInviteApi.RevokePendingInviteByEmail"
                        inviteApi.RevokePendingInviteByEmail
                        model.Email
                        (RemoteEffect.flatten >> Done)
                )
            ],
            Stay
        | Done(Ok()) -> model, [], Revoked model.TeamId
        | Done(Error e) -> model, [], Failed e
        | Cancel -> model, [], Closed

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

/// A call's reply, with a failure sent to the screen's one error branch.
let private orApiError (ofAnswer: 'result -> Msg) (reply: Result<'result, exn>) : Msg =
    match reply with
    | Ok answer -> ofAnswer answer
    | Error ex -> ApiError ex.Message

/// A child's steps in this screen's message type.
let private lift
    (wrap: 'child -> Msg)
    (steps: Step<'child, RemoteEffect<'child>> list)
    : Step<Msg, RemoteEffect<Msg>> list =
    steps |> Step.mapAll wrap (RemoteEffect.map wrap)

let private loadTeams: Step<Msg, RemoteEffect<Msg>> =
    Do(RemoteEffect.call "TeamApi.GetMyTeams" teamApi.GetMyTeams () (orApiError TeamsLoaded))

let private loadActiveTeam: Step<Msg, RemoteEffect<Msg>> =
    Do(RemoteEffect.call "TeamApi.GetActiveTeam" teamApi.GetActiveTeam () (orApiError ActiveTeamLoaded))

let private loadMembers (teamId: string) : Step<Msg, RemoteEffect<Msg>> =
    Do(
        RemoteEffect.call
            "TeamApi.GetTeamMembers"
            teamApi.GetTeamMembers
            teamId
            (orApiError (fun members -> MembersLoaded(teamId, members)))
    )

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
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

    // Fetch the caller's platform-admin status so the management
    // controls can apply the "Platform Admin has full rights across
    // all teams" bypass. Failure falls through to `ApiError` and
    // leaves the bypass disabled (`IsPlatformAdmin = None`) — server
    // gates remain the real enforcement.
    let loadAdminStatus =
        Do(
            RemoteEffect.call
                "PlatformAdminApi.IsPlatformAdmin"
                platformAdminApi.IsPlatformAdmin
                ()
                (orApiError IsPlatformAdminLoaded)
        )

    (model, [ loadTeams; loadActiveTeam; loadAdminStatus ])

// ─── Helpers ─────────────────────────────────────────────────────────

/// Pull the caller's role in each team from the members lists as they
/// stream in. Used to gate Admin-only UI.
let private roleFor (userId: string) (members: TeamMembership list) =
    members |> List.tryFind (fun m -> m.UserId = userId) |> Option.map _.Role

let internal selfUserId () = UserSession.getUserId ()

/// Reload the members of the team on screen, if one is.
let private refreshCurrentTeam (model: Model) : Step<Msg, RemoteEffect<Msg>> list =
    match model.CurrentView with
    | TeamDetails teamId -> [ Then(LoadMembers teamId) ]
    | _ -> []

/// Reload both invite lists of a team: a change to one moves an email between them.
let private reloadInvites (teamId: string) : Step<Msg, RemoteEffect<Msg>> list = [
    Then(LoadPendingByEmail teamId)
    Then(LoadExpiredInvites teamId)
]

/// The "Add a member" form: an email becomes a pending invite, anything else a direct add.
let private addMember (teamId: string) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    let raw = model.AddMemberUserId.Trim()

    if raw = "" then
        {
            model with
                Error = Some(Notice.Problem model.Messages.IdentifierRequired)
        },
        []
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
        [
            Do(
                RemoteEffect.call
                    "ITeamInviteApi.IssuePendingInviteByEmail"
                    inviteApi.IssuePendingInviteByEmail
                    request
                    (fun reply -> AddMemberInvited(teamId, RemoteEffect.flatten reply))
            )
        ]
    else
        {
            model with
                AddMemberSubmitting = true
        },
        [
            Do(
                RemoteEffect.call
                    "TeamApi.AddTeamMember"
                    teamApi.AddTeamMember
                    (teamId, raw, model.AddMemberRole)
                    (orApiError MemberAdded)
            )
        ]

/// Route a step of the transfer modal and act on its outcome.
let private routeTransfer (subMsg: TransferOwnership.Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match model.Transfer with
    | None -> model, []
    | Some transfer ->
        let transfer, steps, outcome = TransferOwnership.update subMsg transfer
        let steps = lift TransferMsg steps

        match outcome with
        | TransferOwnership.Stay -> { model with Transfer = Some transfer }, steps
        | TransferOwnership.Closed -> { model with Transfer = None }, steps
        | TransferOwnership.Transferred teamId ->
            {
                model with
                    Transfer = None
                    Error = None
            },
            steps @ [ Then(LoadMembers teamId) ]

/// Route a step of the invite-by-email modal and act on its outcome.
let private routeIssueByEmail (subMsg: IssueByEmail.Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match model.IssueByEmail with
    | None -> model, []
    | Some issue ->
        let issue, steps, outcome = IssueByEmail.update subMsg issue
        let steps = lift IssueByEmailMsg steps

        match outcome with
        | IssueByEmail.Stay -> { model with IssueByEmail = Some issue }, steps
        | IssueByEmail.Closed -> { model with IssueByEmail = None }, steps
        | IssueByEmail.Issued teamId ->
            {
                model with
                    IssueByEmail = None
                    Error = None
            },
            steps @ [ Then(LoadPendingByEmail teamId) ]

/// Route a step of the revoke confirmation and act on its outcome.
let private routeRevokeByEmail (subMsg: RevokeByEmail.Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match model.RevokeByEmail with
    | None -> model, []
    | Some revoke ->
        let revoke, steps, outcome = RevokeByEmail.update subMsg revoke
        let steps = lift RevokeByEmailMsg steps

        match outcome with
        | RevokeByEmail.Stay ->
            {
                model with
                    RevokeByEmail = Some revoke
            },
            steps
        | RevokeByEmail.Closed -> { model with RevokeByEmail = None }, steps
        | RevokeByEmail.Revoked teamId ->
            { model with RevokeByEmail = None }, steps @ [ Then(LoadPendingByEmail teamId) ]
        | RevokeByEmail.Failed e ->
            {
                model with
                    RevokeByEmail = None
                    Error = Some(Notice.Problem e)
            },
            steps

// ─── Update ──────────────────────────────────────────────────────────

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | LoadTeams -> model, [ loadTeams ]

    | TeamsLoaded teams -> { model with Teams = teams }, teams |> List.map (fun t -> loadMembers t.TeamId)

    | LoadActiveTeam -> model, [ loadActiveTeam ]

    | ActiveTeamLoaded t -> { model with ActiveTeamId = t }, []

    | LoadMembers teamId -> model, [ loadMembers teamId ]

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
        |> lift DirectoryMsg

    | SelectTeam teamId ->
        {
            model with
                CurrentView = TeamDetails teamId
        },
        [ Then(LoadMembers teamId) ]

    | BackToMyTeams -> { model with CurrentView = MyTeams }, []

    | SwitchActiveTeam teamId ->
        model,
        [
            Do(
                RemoteEffect.call
                    "TeamApi.SetActiveTeam"
                    teamApi.SetActiveTeam
                    teamId
                    (orApiError (fun r -> ActiveTeamSwitched(teamId, r)))
            )
        ]

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
        []

    | SetAddMemberUserId id -> { model with AddMemberUserId = id }, []

    | SetAddMemberRole role -> { model with AddMemberRole = role }, []

    // Ignore a second click while an add/invite is already in flight.
    | AddMember _ when model.AddMemberSubmitting -> model, []

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
        [ Then(LoadPendingByEmail teamId) ]

    | MemberAdded(Error e)
    | AddMemberInvited(_, Error e) ->
        {
            model with
                AddMemberSubmitting = false
                Error = Some(Notice.Problem e)
        },
        []

    | RemoveMember(teamId, userId) ->
        model,
        [
            Do(
                RemoteEffect.call
                    "TeamApi.RemoveTeamMember"
                    teamApi.RemoveTeamMember
                    (teamId, userId)
                    (orApiError MemberRemoved)
            )
        ]

    | ChangeMemberRole(teamId, targetUserId, newRole) ->
        model,
        [
            Do(
                RemoteEffect.call
                    "TeamApi.ChangeMemberRole"
                    teamApi.ChangeMemberRole
                    (teamId, targetUserId, newRole)
                    (orApiError MemberRoleChanged)
            )
        ]

    | MemberRemoved(Ok())
    | MemberRoleChanged(Ok()) -> { model with Error = None }, refreshCurrentTeam model

    | IsPlatformAdminLoaded isAdmin ->
        {
            model with
                IsPlatformAdmin = Some isAdmin
        },
        []

    | OpenTransferOwnership teamId ->
        {
            model with
                Transfer = Some(TransferOwnership.init teamId)
        },
        []

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

        model,
        [
            Do(
                RemoteEffect.call
                    "ITeamInviteApi.ListPendingInvitesByEmail"
                    inviteApi.ListPendingInvitesByEmail
                    teamId
                    (orApiError onLoad)
            )
        ]

    | PendingByEmailLoaded(teamId, entries) ->
        {
            model with
                PendingByEmail = model.PendingByEmail |> Map.add teamId entries
        },
        []

    | OpenIssueByEmail teamId ->
        {
            model with
                IssueByEmail = Some(IssueByEmail.init teamId model.Messages.EmailRequired)
        },
        []

    | IssueByEmailMsg subMsg -> routeIssueByEmail subMsg model

    | OpenRevokeByEmail(teamId, email) ->
        {
            model with
                RevokeByEmail = Some(RevokeByEmail.init teamId email)
        },
        []

    | RevokeByEmailMsg subMsg -> routeRevokeByEmail subMsg model

    | LoadExpiredInvites teamId ->
        let onLoad (result: Result<TeamInviteExpiredPayload list, string>) =
            match result with
            | Ok entries -> ExpiredInvitesLoaded(teamId, entries)
            | Error e -> ApiError e

        model,
        [
            Do(
                RemoteEffect.call
                    "ITeamInviteApi.ListRecentlyExpiredInvites"
                    inviteApi.ListRecentlyExpiredInvites
                    teamId
                    (orApiError onLoad)
            )
        ]

    | ExpiredInvitesLoaded(teamId, entries) ->
        {
            model with
                ExpiredInvites = model.ExpiredInvites |> Map.add teamId entries
        },
        []

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
        [
            Do(
                RemoteEffect.call
                    "ITeamInviteApi.IssuePendingInviteByEmail"
                    inviteApi.IssuePendingInviteByEmail
                    request
                    (fun reply -> ExpiredInviteReissued(teamId, email, RemoteEffect.flatten reply))
            )
        ]

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
            []

    | DirectoryMsg subMsg ->
        let directory, steps = UserDirectory.update subMsg model.Directory
        { model with Directory = directory }, lift DirectoryMsg steps

    | ActiveTeamSwitched(_, Error e)
    | MemberRemoved(Error e)
    | MemberRoleChanged(Error e)
    | ApiError e ->
        {
            model with
                Error = Some(Notice.Problem e)
        },
        []

    | DismissError -> { model with Error = None }, []

// ─── What the tree reads ─────────────────────────────────────────────

/// Effective team role for the caller on the supplied team. Returns
/// `Some Owner` when the caller is a Platform Admin — the
/// "Platform Admins have complete rights across all teams" rule —
/// regardless of whether they actually hold a membership row. Falls
/// back to the team store's view otherwise. Server-side gates apply
/// the same bypass; this is the client-side mirror that keeps the
/// management controls visible for PAs.
let internal effectiveCallerRole (model: Model) (teamId: string) : TeamRole option =
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
let internal assignableRoles (callerRole: TeamRole option) : TeamRole list =
    match callerRole with
    | Some Owner -> [ Member; Admin ]
    | Some Admin -> [ Member ]
    | Some Member
    | None -> []

/// The caller's own id labels with the session's display name; every other
/// id with the directory's label.
let internal labelFor (directory: UserDirectory.Model) (selfId: string) (userId: string) : string =
    if userId = selfId then
        UserSession.getDisplayName () |> Option.defaultValue userId
    else
        UserDirectory.label directory userId

let internal teamNameOf (model: Model) (teamId: string) =
    model.Teams
    |> List.tryFind (fun t -> t.TeamId = teamId)
    |> Option.map _.Name
    |> Option.defaultValue teamId

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: TeamManagerMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./TeamManagerTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "TeamManagerTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.TeamManager
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.TeamManager"

/// The built-in team manager (`ShellSlots.builtIn` builds its module). It fills the
/// `TeamManager` shell slot (Default / Configured), which the shell
/// admits on any `Team` surface.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Teams"
        Icon = ToolUp.Platform.Icons.users
    }
    Group = Some "Team Management"
    NavRole = Some ToolUp.Platform.NavRole.TeamOwnerAdmin
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = init
    Update = update
    View = BuiltInView.Page view
    Extend = id
}

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