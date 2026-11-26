// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PlatformAdminUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── Built-in Platform Admin module ──────────────────────────────────
//
// SDK-built-in admin module surfacing `PlatformAdminApi`'s role-
// management endpoints: list current Platform Admins, assign new
// admins, revoke existing ones. Auto-injected by the shell whenever
// the caller's `Model.PlatformRole = Some PlatformAdmin` (the sidebar
// gate hides the module's "Platform Management" group otherwise).
//
// **Three tabs:**
//   - "Admins" — the current admin list with assign / revoke controls.
//   - "Teams" — create a team with an initial owner; the all-teams table
//     with archive / restore / delete.
//   - "Settings" — deployment-wide runtime configuration.
//
// **Server-side gating.** Read methods (`ListPlatformAdmins`) are open
// to any authenticated caller. Write methods (Assign / Revoke) are
// gated server-side on `canModifyPlatformConfig`; the client-side gate
// (sidebar visibility) is UX-only — non-admin callers landing on the
// page directly via URL would still see the API refuse writes.
//
// The two confirmations (grant admin, delete team) are sub-models: each
// issues its own remote call and reports how it ended as an `Outcome`
// the parent reads after delegating to it.

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private platformAdminApi: PlatformAdminApi =
    Api.makeProxy<PlatformAdminApi> (customOptions = UserSession.withRequestHeaders)

let private teamApi: TeamApi =
    Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

// ─── Sub-models ──────────────────────────────────────────────────────

/// "Are you sure?" for `AssignPlatformAdmin`. Grants are effectively
/// irreversible once observed (the recipient may have already used the
/// new role to mutate deployment configuration), so the confirm step is a
/// deliberate brake on accidental fires — e.g. an operator typing-Enter on
/// the wrong autocomplete row.
module AssignConfirm =

    /// How the confirmation ended, read by the parent after each update.
    type Outcome =
        | Open
        | Cancelled
        | Done of Result<unit, string>

    type Model = {
        TargetUserId: string
        /// The grant call is in flight; the confirm button shows busy.
        InFlight: bool
        Outcome: Outcome
    }

    type Msg =
        | Confirm
        | Cancel
        | Resolved of Result<unit, string>

    let init (targetUserId: string) : Model = {
        TargetUserId = targetUserId
        InFlight = false
        Outcome = Open
    }

    /// The transitions, with the call as a value.
    let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
        match msg with
        | Confirm when model.InFlight -> model, []
        | Confirm ->
            { model with InFlight = true },
            [
                Do(
                    RemoteEffect.call
                        "PlatformAdminApi.AssignPlatformAdmin"
                        platformAdminApi.AssignPlatformAdmin
                        model.TargetUserId
                        (RemoteEffect.flatten >> Resolved)
                )
            ]
        | Cancel when model.InFlight -> model, []
        | Cancel -> { model with Outcome = Cancelled }, []
        | Resolved result ->
            {
                model with
                    InFlight = false
                    Outcome = Done result
            },
            []

    /// What the deferred confirmation takes.
    type internal SurfaceProps = {|
        Messages: PlatformAdminMessages
        Directory: UserDirectory.Model
        Model: Model
        Dispatch: Msg -> unit
    |}

    [<Emit("$0.then(m => m.AssignConfirmSurface)")>]
    let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

    /// The confirmation, drawn by `PlatformAdminTree` when it is first shown.
    let view (msgs: PlatformAdminMessages) (directory: UserDirectory.Model) (model: Model) (dispatch: Msg -> unit) =
        TreeSurface.deferred
            "PlatformAdminTree.AssignConfirmSurface"
            (fun () -> surfaceOf (importDynamic "./PlatformAdminTree.fs"))
            {|
                Messages = msgs
                Directory = directory
                Model = model
                Dispatch = dispatch
            |}

/// Delete-team confirmation — only reachable from an archived row. Echoes
/// the team name as a brake on accidental destruction; the purge removes
/// the team record AND every membership row.
module ConfirmDeleteTeam =

    /// How the confirmation ended, read by the parent after each update.
    type Outcome =
        | Open
        | Cancelled
        | Done of Result<unit, string>

    type Model = {
        TeamId: string
        /// The name shown in the prompt (the id when the table lacks it).
        TeamName: string
        /// The delete call is in flight; the confirm button shows busy.
        InFlight: bool
        Outcome: Outcome
    }

    type Msg =
        | Confirm
        | Cancel
        | Resolved of Result<unit, string>

    let init (teamId: string) (teamName: string) : Model = {
        TeamId = teamId
        TeamName = teamName
        InFlight = false
        Outcome = Open
    }

    /// The transitions, with the call as a value.
    let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
        match msg with
        | Confirm when model.InFlight -> model, []
        | Confirm ->
            { model with InFlight = true },
            [
                Do(
                    RemoteEffect.call
                        "TeamApi.DeleteTeamHard"
                        teamApi.DeleteTeamHard
                        model.TeamId
                        (RemoteEffect.flatten >> Resolved)
                )
            ]
        | Cancel when model.InFlight -> model, []
        | Cancel -> { model with Outcome = Cancelled }, []
        | Resolved result ->
            {
                model with
                    InFlight = false
                    Outcome = Done result
            },
            []

    /// What the deferred confirmation takes.
    type internal SurfaceProps = {|
        Messages: PlatformAdminMessages
        Model: Model
        Dispatch: Msg -> unit
    |}

    [<Emit("$0.then(m => m.DeleteTeamSurface)")>]
    let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

    /// The confirmation, drawn by `PlatformAdminTree` when it is first shown.
    let view (msgs: PlatformAdminMessages) (model: Model) (dispatch: Msg -> unit) =
        TreeSurface.deferred
            "PlatformAdminTree.DeleteTeamSurface"
            (fun () -> surfaceOf (importDynamic "./PlatformAdminTree.fs"))
            {|
                Messages = msgs
                Model = model
                Dispatch = dispatch
            |}

// ─── Model ───────────────────────────────────────────────────────────

type Tab =
    | AdminsTab
    | TeamsTab
    | SettingsTab

type Model = {
    /// Currently rendered section.
    ActiveTab: Tab
    /// Live admin list from `ListPlatformAdmins`. Refreshed on shell
    /// init and after every successful Assign / Revoke.
    Admins: Remote<string list, string>
    /// The Assign Admin form's committed value. Kept on the Elmish model
    /// so the AssignError banner can pair with the last attempted value.
    AssignInput: string
    /// Inline error from the most recent Assign attempt — `None` when
    /// no attempt yet, or after a successful one. Cleared on input
    /// edit so the banner doesn't linger after the user retypes.
    AssignError: Notice option
    /// The open grant confirmation, `None` when closed.
    AssignConfirm: AssignConfirm.Model option
    /// Inline error from a Revoke attempt, keyed by user id so the
    /// banner can render next to the right row.
    RevokeError: Map<string, Notice>
    /// Create-team form: name input.
    NewTeamName: string
    /// Create-team form: initial-owner user id. Empty string = no
    /// selection yet; the operator can populate via the directory
    /// typeahead OR by clicking "Self" (which writes their own user
    /// id into this field).
    NewTeamOwnerUserId: string
    /// Inline error from the most recent CreateTeamWithOwner attempt.
    CreateTeamError: Notice option
    /// Tracks whether a CreateTeamWithOwner call is in flight, so
    /// the submit button can disable to prevent double-fire.
    CreateTeamInFlight: bool
    /// Live all-teams admin table from `ListAllTeams`. Loaded on shell
    /// init and after every successful archive / restore / delete /
    /// create. Platform-Admin only — non-admins never see this surface.
    Teams: Remote<TeamSummary list, string>
    /// Team ids with an archive / restore call in flight, so their row
    /// action buttons can disable to prevent double-fire.
    TeamActionPending: Set<string>
    /// The open delete-team confirmation, `None` when closed.
    ConfirmDeleteTeam: ConfirmDeleteTeam.Model option
    /// Inline error from a per-team archive / restore / delete attempt,
    /// keyed by team id so the banner renders against the right row.
    TeamActionError: Map<string, Notice>
    /// Current runtime PlatformKnowledgeBase state. Loaded on shell init
    /// via `GetPlatformKnowledgeBase`; updated after a successful `Set`.
    PlatformKnowledgeBase: Remote<FeatureMode, string>
    /// Inline error from a SetPlatformKnowledgeBase attempt.
    SettingsError: Notice option
    /// The reverse directory the admin list and teams table render ids
    /// through (emails in place of raw `oid`s).
    Directory: UserDirectory.Model
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: PlatformAdminMessages
}

type Msg =
    | SwitchTab of Tab
    | RefreshAdmins
    | AdminsLoaded of Result<string list, string>
    | UpdateAssignInput of string
    | RequestAssignConfirm
    | AssignConfirmMsg of AssignConfirm.Msg
    | RequestRevoke of targetUserId: string
    | RevokeResolved of targetUserId: string * Result<unit, string>
    | SetNewTeamName of string
    | SetNewTeamOwnerUserId of string
    | UseSelfAsTeamOwner
    | SubmitCreateTeam
    | CreateTeamResolved of Result<TeamInfo, string>
    | RefreshTeams
    | TeamsLoaded of Result<TeamSummary list, string>
    | SetTeamArchived of teamId: string * archived: bool
    | TeamArchiveResolved of teamId: string * archived: bool * Result<unit, string>
    | RequestDeleteTeam of teamId: string
    | DeleteTeamMsg of ConfirmDeleteTeam.Msg
    | PlatformKnowledgeBaseLoaded of Result<FeatureMode, string>
    | TogglePlatformKnowledgeBase of FeatureMode
    | TogglePlatformKnowledgeBaseResolved of requested: FeatureMode * Result<unit, string>
    | DirectoryMsg of UserDirectory.Msg

// ─── Init / commands ─────────────────────────────────────────────────

type internal Steps = Step<Msg, RemoteEffect<Msg>> list

let internal loadAdmins () : Steps = [
    Do(
        RemoteEffect.call
            "PlatformAdminApi.ListPlatformAdmins"
            platformAdminApi.ListPlatformAdmins
            ()
            (Result.mapError _.Message >> AdminsLoaded)
    )
]

let internal revoke (targetUserId: string) : Steps = [
    Do(
        RemoteEffect.call
            "PlatformAdminApi.RevokePlatformAdmin"
            platformAdminApi.RevokePlatformAdmin
            targetUserId
            (fun reply -> RevokeResolved(targetUserId, RemoteEffect.flatten reply))
    )
]

let internal loadPlatformKnowledgeBase () : Steps = [
    Do(
        RemoteEffect.call
            "PlatformAdminApi.GetPlatformKnowledgeBase"
            platformAdminApi.GetPlatformKnowledgeBase
            ()
            (Result.mapError _.Message >> PlatformKnowledgeBaseLoaded)
    )
]

let internal setPlatformKnowledgeBase (mode: FeatureMode) : Steps = [
    Do(
        RemoteEffect.call
            "PlatformAdminApi.SetPlatformKnowledgeBase"
            platformAdminApi.SetPlatformKnowledgeBase
            mode
            (fun reply -> TogglePlatformKnowledgeBaseResolved(mode, RemoteEffect.flatten reply))
    )
]

let internal createTeam (request: CreateTeamRequest) : Steps = [
    Do(
        RemoteEffect.call
            "TeamApi.CreateTeamWithOwner"
            teamApi.CreateTeamWithOwner
            request
            (RemoteEffect.flatten >> CreateTeamResolved)
    )
]

let internal loadTeams () : Steps = [
    Do(RemoteEffect.call "TeamApi.ListAllTeams" teamApi.ListAllTeams () (RemoteEffect.flatten >> TeamsLoaded))
]

let internal setArchived (teamId: string) (archived: bool) : Steps =
    let methodName, call =
        if archived then
            "TeamApi.ArchiveTeam", teamApi.ArchiveTeam
        else
            "TeamApi.RestoreTeam", teamApi.RestoreTeam

    [
        Do(
            RemoteEffect.call methodName call teamId (fun reply ->
                TeamArchiveResolved(teamId, archived, RemoteEffect.flatten reply))
        )
    ]

/// The directory's steps in this screen's message type.
let private lift (steps: Step<UserDirectory.Msg, RemoteEffect<UserDirectory.Msg>> list) : Steps =
    Step.mapAll DirectoryMsg (RemoteEffect.map DirectoryMsg) steps

/// Ask the directory for `ids`; it skips blank and already-known ids.
let internal resolve (ids: string list) (model: Model) : Steps =
    UserDirectory.resolve ids model.Directory |> lift

/// Every user id the loaded admin list + teams table reference (Owners +
/// Admins) — what the directory should hold.
let private referencedUserIds (model: Model) =
    let adminIds = model.Admins |> Remote.withDefault []

    let teamIds =
        model.Teams
        |> Remote.map (List.collect (fun t -> t.Owners @ t.Admins))
        |> Remote.withDefault []

    adminIds @ teamIds

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    let model = {
        ActiveTab = AdminsTab
        Admins = Remote.Loading
        AssignInput = ""
        AssignError = None
        AssignConfirm = None
        RevokeError = Map.empty
        NewTeamName = ""
        NewTeamOwnerUserId = ""
        CreateTeamError = None
        CreateTeamInFlight = false
        Teams = Remote.Loading
        TeamActionPending = Set.empty
        ConfirmDeleteTeam = None
        TeamActionError = Map.empty
        PlatformKnowledgeBase = Remote.Loading
        SettingsError = None
        Directory = UserDirectory.empty
        Messages = ctx.Messages.PlatformAdmin
    }

    (model, loadAdmins () @ loadTeams () @ loadPlatformKnowledgeBase ())

// ─── Update ──────────────────────────────────────────────────────────

/// Delegate to the open grant confirmation, then act on how it ended.
let private assignConfirmStep (msg: AssignConfirm.Msg) (model: Model) : Model * Steps =
    match model.AssignConfirm with
    | None -> model, []
    | Some sub ->
        let sub, subSteps = AssignConfirm.update msg sub

        let subSteps =
            Step.mapAll AssignConfirmMsg (RemoteEffect.map AssignConfirmMsg) subSteps

        match sub.Outcome with
        | AssignConfirm.Open -> { model with AssignConfirm = Some sub }, subSteps
        | AssignConfirm.Cancelled -> { model with AssignConfirm = None }, subSteps
        | AssignConfirm.Done(Ok()) ->
            {
                model with
                    AssignConfirm = None
                    AssignInput = ""
                    AssignError = None
                    Admins = Remote.Loading
            },
            subSteps @ loadAdmins ()
        | AssignConfirm.Done(Error e) ->
            {
                model with
                    AssignConfirm = None
                    AssignError = Some(Notice.Problem e)
            },
            subSteps

/// Delegate to the open delete-team confirmation, then act on how it ended.
let private deleteTeamStep (msg: ConfirmDeleteTeam.Msg) (model: Model) : Model * Steps =
    match model.ConfirmDeleteTeam with
    | None -> model, []
    | Some sub ->
        let sub, subSteps = ConfirmDeleteTeam.update msg sub
        let subSteps = Step.mapAll DeleteTeamMsg (RemoteEffect.map DeleteTeamMsg) subSteps

        match sub.Outcome with
        | ConfirmDeleteTeam.Open ->
            {
                model with
                    ConfirmDeleteTeam = Some sub
            },
            subSteps
        | ConfirmDeleteTeam.Cancelled -> { model with ConfirmDeleteTeam = None }, subSteps
        | ConfirmDeleteTeam.Done(Ok()) ->
            {
                model with
                    ConfirmDeleteTeam = None
                    Teams = Remote.Loading
            },
            subSteps @ loadTeams ()
        | ConfirmDeleteTeam.Done(Error e) ->
            {
                model with
                    ConfirmDeleteTeam = None
                    TeamActionError = model.TeamActionError |> Map.add sub.TeamId (Notice.Problem e)
            },
            subSteps

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, []

    | RefreshAdmins -> { model with Admins = Remote.Loading }, loadAdmins ()

    | AdminsLoaded(Ok list) ->
        let model = {
            model with
                Admins = Remote.Loaded(list |> List.sort)
        }

        model, resolve (referencedUserIds model) model

    | AdminsLoaded(Error msg) ->
        {
            model with
                Admins = Remote.Failed msg
        },
        []

    | UpdateAssignInput input ->
        {
            model with
                AssignInput = input
                AssignError = None
        },
        []

    | RequestAssignConfirm ->
        let trimmed = model.AssignInput.Trim()

        if String.length trimmed = 0 then
            {
                model with
                    AssignError = Some(Notice.Problem model.Messages.EnterUserId)
            },
            []
        else
            {
                model with
                    AssignError = None
                    AssignConfirm = Some(AssignConfirm.init trimmed)
            },
            // Resolve the target so the confirmation can echo their email.
            resolve [ trimmed ] model

    | AssignConfirmMsg subMsg -> assignConfirmStep subMsg model

    | RequestRevoke targetUserId ->
        {
            model with
                RevokeError = model.RevokeError |> Map.remove targetUserId
        },
        revoke targetUserId

    | RevokeResolved(_, Ok()) -> { model with Admins = Remote.Loading }, loadAdmins ()

    | RevokeResolved(targetUserId, Error msg) ->
        {
            model with
                RevokeError = model.RevokeError |> Map.add targetUserId (Notice.Problem msg)
        },
        []

    | SetNewTeamName name ->
        {
            model with
                NewTeamName = name
                CreateTeamError = None
        },
        []

    | SetNewTeamOwnerUserId userId ->
        {
            model with
                NewTeamOwnerUserId = userId
                CreateTeamError = None
        },
        // Resolve the committed id so the confirmation line can echo the
        // owner's email.
        resolve [ userId.Trim() ] model

    | UseSelfAsTeamOwner ->
        {
            model with
                NewTeamOwnerUserId = UserSession.getUserId ()
                CreateTeamError = None
        },
        []

    | SubmitCreateTeam ->
        let name = model.NewTeamName.Trim()
        let ownerId = model.NewTeamOwnerUserId.Trim()

        if String.length name = 0 then
            {
                model with
                    CreateTeamError = Some(Notice.Problem model.Messages.TeamNameRequired)
            },
            []
        elif String.length ownerId = 0 then
            {
                model with
                    CreateTeamError = Some(Notice.Problem model.Messages.OwnerRequired)
            },
            []
        else
            {
                model with
                    CreateTeamError = None
                    CreateTeamInFlight = true
            },
            createTeam {
                Name = name
                InitialOwnerUserId = ownerId
            }

    | CreateTeamResolved(Ok _) ->
        {
            model with
                NewTeamName = ""
                NewTeamOwnerUserId = ""
                CreateTeamError = None
                CreateTeamInFlight = false
                Teams = Remote.Loading
        },
        // Refresh the admin table so the new team appears immediately.
        loadTeams ()

    | CreateTeamResolved(Error msg) ->
        {
            model with
                CreateTeamError = Some(Notice.Problem msg)
                CreateTeamInFlight = false
        },
        []

    | RefreshTeams -> { model with Teams = Remote.Loading }, loadTeams ()

    | TeamsLoaded(Ok teams) ->
        let model = {
            model with
                Teams = Remote.Loaded(teams |> List.sortBy (fun t -> t.Name.ToLowerInvariant()))
        }

        model, resolve (referencedUserIds model) model

    | TeamsLoaded(Error msg) -> { model with Teams = Remote.Failed msg }, []

    | SetTeamArchived(teamId, archived) ->
        {
            model with
                TeamActionPending = model.TeamActionPending |> Set.add teamId
                TeamActionError = model.TeamActionError |> Map.remove teamId
        },
        setArchived teamId archived

    | TeamArchiveResolved(teamId, _, Ok()) ->
        {
            model with
                TeamActionPending = model.TeamActionPending |> Set.remove teamId
                Teams = Remote.Loading
        },
        loadTeams ()

    | TeamArchiveResolved(teamId, _, Error msg) ->
        {
            model with
                TeamActionPending = model.TeamActionPending |> Set.remove teamId
                TeamActionError = model.TeamActionError |> Map.add teamId (Notice.Problem msg)
        },
        []

    | RequestDeleteTeam teamId ->
        let teamName =
            model.Teams
            |> Remote.toOption
            |> Option.bind (List.tryFind (fun t -> t.TeamId = teamId))
            |> Option.map _.Name
            |> Option.defaultValue teamId

        {
            model with
                ConfirmDeleteTeam = Some(ConfirmDeleteTeam.init teamId teamName)
                TeamActionError = model.TeamActionError |> Map.remove teamId
        },
        []

    | DeleteTeamMsg subMsg -> deleteTeamStep subMsg model

    | PlatformKnowledgeBaseLoaded(Ok mode) ->
        {
            model with
                PlatformKnowledgeBase = Remote.Loaded mode
                SettingsError = None
        },
        []

    | PlatformKnowledgeBaseLoaded(Error msg) ->
        {
            model with
                PlatformKnowledgeBase = Remote.Failed msg
        },
        []

    | TogglePlatformKnowledgeBase requested -> { model with SettingsError = None }, setPlatformKnowledgeBase requested

    | TogglePlatformKnowledgeBaseResolved(requested, Ok()) ->
        {
            model with
                PlatformKnowledgeBase = Remote.Loaded requested
                SettingsError = None
        },
        []

    | TogglePlatformKnowledgeBaseResolved(_, Error msg) ->
        {
            model with
                SettingsError = Some(Notice.Problem msg)
        },
        []

    | DirectoryMsg subMsg ->
        let directory, steps = UserDirectory.update subMsg model.Directory
        { model with Directory = directory }, lift steps

// ─── View helpers ────────────────────────────────────────────────────

/// A user id rendered through the directory: the email when resolved (the
/// raw id on hover), the id in monospace when not.
// ─── View ────────────────────────────────────────────────────────────

/// The signed-in operator, for the "Self" owner affordance: their id and the
/// label it shows (their email, else their display name, else the id).
type internal Caller = { UserId: string; Display: string }

let internal caller () : Caller =
    let id = UserSession.getUserId ()

    {
        UserId = id
        Display =
            UserSession.getEmail ()
            |> Option.orElse (UserSession.getDisplayName ())
            |> Option.defaultValue id
    }

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Caller: Caller
    Model: Model
    Dispatch: Msg -> unit
    Messages: PlatformAdminMessages
    Widgets: PublicUtilityWidgetsMessages
    Config: PublicUtilityConfig
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./PlatformAdminTree.fs")

/// The page, closed over the deployment's `PublicUtilityConfig` so the
/// public-utility widgets (appended below the tabs under
/// `PublicUtilityPlatformAdminProfile`) can read ad / premium substrate state.
let private view (config: PublicUtilityConfig) (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "PlatformAdminTree.Surface" loadSurface {|
            Caller = caller ()
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.PlatformAdmin
            Widgets = ctx.Messages.PublicUtilityWidgets
            Config = config
            Locale = ctx.Locale
        |}
    )


// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.PlatformAdmin"

/// Create the Platform Management module as an `ErasedModule`.
/// Fills the `PlatformAdmin` shell slot (default `SlotFill.Default`,
/// admitted on every surface; the slot name is retained for backwards
/// compatibility; the display name + sidebar group both surface as
/// "Platform Management"). The shell's sidebar filter
/// (4f.2) hides the "Platform Management" group from non-admin
/// callers; the module itself does no client-side gating beyond the
/// standard API-error surface (server-side `canModifyPlatformConfig`
/// is the actual enforcement).
///
/// Module Id (`_sdk.PlatformAdmin`) is preserved so the server-side
/// `ServerConfig.ModuleNames` keys (RBAC permission entries) keyed
/// to it don't have to migrate. The display name and group label
/// are presentation only.
let builtIn (publicUtility: PublicUtilityConfig) : BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Platform Management"
        Icon = ToolUp.Platform.Icons.users
    }
    Group = Some "Platform Management"
    NavRole = Some ToolUp.Platform.NavRole.PlatformAdminOnly
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = init
    Update = update
    View = BuiltInView.Page(view publicUtility)
    Extend = id
}