// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PermissionsAdminUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── PermissionsAdminUI ──────────────────────────────────────────────
//
// The built-in RBAC admin, at surface parity with the other built-in admin modules
// (`TeamConfigUI`, `WebhookAdminUI`, `HealthMonitorUI`,
// `DataIngestionUI`, `ServiceStatusBoardUI`).
//
// **What this surfaces.** The full `TeamPermissions` document for the
// caller's active team — the team-defaults map (per-module
// `ModulePermission list`) plus per-member overrides (userId →
// moduleName → list). Three tab views:
//
//   - **Team Defaults** — bulk-editable matrix of every managed
//     module × Read/Write/Admin. One Save button per page round-trips
//     through `PermissionApi.SetTeamDefaults` (atomic replace).
//   - **Members** — left-pane list of every team member; click a
//     member to see their override matrix on the right. Per-cell save
//     via `PermissionApi.SetMemberPermissions` (one module at a
//     time — the backing API takes `(teamId, userId, moduleName,
//     perms)`, so this is a natural fit).
//   - **Modules** — per-module **exposure** control: a
//     tri-state selector (Available / Hidden / Unavailable) orthogonal
//     to permission level. `Hidden` removes a module from every member's
//     sidebar + Home (data still mappable); `Unavailable` removes it AND
//     blocks mapping any data into it. Shown alongside the module's
//     team-default permissions and the count of members carrying an
//     explicit override. Exposure round-trips per selection through
//     `PermissionApi.SetModuleExposure`.
//
// **Owner/Admin gating.** Server-side `PermissionApi` already returns
// `Error` for non-Owner/Admin callers (per the docstring on
// `PermissionApi` in `TeamTypes.fs`). The client shows the read
// surface to any team member and surfaces the gating message as a
// banner on the failed write path; the sidebar role filter further
// hides the entry from members in Team / MultiTeam modes.
//
// Fills the `PermissionsAdmin` shell slot (default `SlotFill.Default`),
// admitted in every non-Anonymous mode.
// Anonymous mode never has role concepts so the gate is suppressed by
// construction.

// ─── Model ───────────────────────────────────────────────────────────

type Tab =
    | TeamDefaultsTab
    | MembersTab
    | ModulesTab

/// Bundle of "everything needed to render once we know the active team":
/// the managed-module list (`Managed` from `AccessibilityApi`) + the
/// member list + the persisted `TeamPermissions` document. Wrapping
/// these together makes the "loading vs ready" decision a single
/// `Remote` check rather than three parallel ones.
type TeamView = {
    TeamId: string
    Members: TeamMembership list
    Managed: string list
    Permissions: TeamPermissions
}

type Model = {
    /// Currently rendered tab.
    ActiveTab: Tab
    /// Top-level snapshot for the caller's active team. `None` ⇒ no
    /// active team yet (typically Anonymous mode dev shells before
    /// MultiTeam selection); error states surface as `Remote.Failed`.
    Snapshot: Remote<TeamView, string>
    /// Local-edit overlay for the team-defaults map. `None` while the
    /// snapshot is loading; `Some` mirrors the persisted Defaults and
    /// accumulates uncommitted cell toggles until the operator saves
    /// or reloads.
    EditingDefaults: Map<string, ModulePermission list> option
    /// On the Members tab: which member is selected for the override
    /// editor. Defaults to the first member after load.
    SelectedMember: string option
    /// Local-edit overlay for per-member overrides, keyed by
    /// `(userId, moduleName)`. Mirrors the `EditingDefaults` /
    /// `SaveDefaults` pattern: checkbox toggles update the overlay
    /// only, and the operator commits the whole diff via an explicit
    /// "Update" button rather than the SDK firing one round-trip per
    /// click. Empty Map = nothing pending; absence of a key for a
    /// `(userId, moduleName)` cell means the rendering layer falls
    /// back to the persisted override / team default. Reset on
    /// snapshot reload, member-select switch, and successful save.
    EditingMembers: Map<string * string, ModulePermission list>
    /// Save round-trips are not pipelined — one writer at a time.
    SaveInFlight: bool
    /// Banner under the tab bar: success / error after a save round-
    /// trip. Cleared on the next user interaction or tab switch.
    Banner: Notice option
    /// `ClientModuleContext.OnAccessibleModulesChanged`,
    /// stashed at init. Invoked after a successful `SetModuleExposure`
    /// so the shell re-fetches its accessible-modules list and the
    /// sidebar + admin "show hidden modules" toggle update without a
    /// manual reload. `None` on non-team surfaces / when unwired.
    OnAccessibleModulesChanged: (unit -> unit) option
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: PermissionsAdminMessages
}

type Msg =
    | SwitchTab of Tab
    | RefreshSnapshot
    | SnapshotLoaded of Result<TeamView, string>
    /// User toggled `(moduleName, ModulePermission)` on the team-defaults
    /// matrix. Updates `EditingDefaults` only — no server round-trip
    /// until `SaveDefaults`.
    | ToggleDefault of moduleName: string * ModulePermission
    /// Discard uncommitted edits on the defaults tab.
    | ResetDefaults
    /// Round-trip every cell in `EditingDefaults` through
    /// `PermissionApi.SetTeamDefaults`.
    | SaveDefaults
    | SaveDefaultsResult of Result<unit, string>
    /// User picked a member from the left-pane list.
    | SelectMember of userId: string
    /// User toggled `(userId, moduleName, ModulePermission)` on the
    /// per-member override matrix. Updates the `EditingMembers`
    /// overlay only; no server round-trip until `SaveMembers` — the
    /// operator commits the whole diff via the "Update" button.
    | ToggleMemberPermission of userId: string * moduleName: string * ModulePermission
    /// Select-all / clear-all for a single column on the
    /// per-member override matrix. Applies the level to every managed
    /// module in `EditingMembers` for the given user — either grants
    /// the level on every module (`add = true`) or strips it from
    /// every module (`add = false`). Idempotent: re-applying does not
    /// duplicate the level on rows that already carry it.
    | ToggleMemberColumn of userId: string * level: ModulePermission * add: bool
    /// Discard uncommitted edits in `EditingMembers` for the
    /// given user.
    | ResetMembers of userId: string
    /// Round-trip every cell in `EditingMembers` for the given user
    /// through `PermissionApi.SetMemberPermissions`, one call per
    /// touched module, in dispatched order.
    | SaveMembers of userId: string
    | SaveMemberResult of userId: string * moduleName: string * Result<unit, string>
    /// Set a module's **exposure** state for the team (the exposure
    /// selector). `Available` shows it; `Hidden` removes it from the
    /// sidebar + Home but keeps its data types mappable; `Unavailable`
    /// removes it AND blocks data mapping. Orthogonal to the permission
    /// matrix — one round-trip through `PermissionApi.SetModuleExposure`.
    /// The local `Exposure` map is updated optimistically and reverted to
    /// `previous` if the round-trip fails.
    | SetExposure of moduleName: string * previous: ModuleExposure * next: ModuleExposure
    | ExposureResult of moduleName: string * previous: ModuleExposure * attempted: ModuleExposure * Result<unit, string>
    /// Banner dismissal.
    | DismissBanner

// ─── API proxies ─────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private teamApi: TeamApi =
    Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

let private permissionApi: PermissionApi =
    Api.makeProxy<PermissionApi> (customOptions = UserSession.withRequestHeaders)

let private accessibilityApi: AccessibilityApi =
    Api.makeProxy<AccessibilityApi> (customOptions = UserSession.withRequestHeaders)

// ─── Helpers ─────────────────────────────────────────────────────────

let internal allPermLevels: ModulePermission list = [
    ModulePermission.Read
    ModulePermission.Write
    ModulePermission.Admin
    ModulePermission.SchemaOnly
]

let internal permLabel (msgs: PermissionsAdminMessages) (p: ModulePermission) =
    match p with
    | ModulePermission.Read -> msgs.PermRead
    | ModulePermission.Write -> msgs.PermWrite
    | ModulePermission.Admin -> msgs.PermAdmin
    | ModulePermission.SchemaOnly -> msgs.PermSchemaOnly

let private toggleInList (p: ModulePermission) (current: ModulePermission list) =
    if List.contains p current then
        current |> List.filter (fun x -> x <> p)
    else
        current @ [ p ]

let internal hasOverride (perms: TeamPermissions) (userId: string) =
    match Map.tryFind userId perms.Members with
    | Some m when not (Map.isEmpty m) -> true
    | _ -> false

let internal memberOverrideCount (perms: TeamPermissions) (moduleName: string) =
    perms.Members
    |> Map.toSeq
    |> Seq.filter (fun (_, modMap) -> Map.containsKey moduleName modMap)
    |> Seq.length

/// The exposure state of a module — absence in the map ⇒ `Available`.
let internal exposureOf (perms: TeamPermissions) (moduleName: string) =
    perms.Exposure
    |> Map.tryFind moduleName
    |> Option.defaultValue ModuleExposure.Available

/// Apply an exposure state to the map: `Available` clears the entry (the
/// default), the other states record it. Mirrors the store's write rule.
let private applyExposure (moduleName: string) (state: ModuleExposure) (exposure: Map<string, ModuleExposure>) =
    match state with
    | ModuleExposure.Available -> exposure |> Map.remove moduleName
    | s -> exposure |> Map.add moduleName s

let private memberPerms (perms: TeamPermissions) (userId: string) (moduleName: string) =
    Map.tryFind userId perms.Members |> Option.bind (Map.tryFind moduleName)

/// A member's effective permissions on a module: the pending local edit,
/// else the persisted override, else the team default.
let internal effectiveMemberPerms
    (snap: TeamView)
    (editing: Map<string * string, ModulePermission list>)
    (userId: string)
    (moduleName: string)
    =
    match Map.tryFind (userId, moduleName) editing with
    | Some v -> v
    | None ->
        match memberPerms snap.Permissions userId moduleName with
        | Some ps -> ps
        | None -> Map.tryFind moduleName snap.Permissions.Defaults |> Option.defaultValue []

// ─── Init / load chain ───────────────────────────────────────────────

/// Fetch the active team's full view in one async block so the UI
/// renders a single coherent loaded state rather than a flicker of
/// partial loads. Errors at any leg surface as a `Remote.Failed` pane
/// the operator can retry.
let private loadSnapshotAsync (msgs: PermissionsAdminMessages) = async {
    let! activeTeamOpt = teamApi.GetActiveTeam()

    match activeTeamOpt with
    | None -> return Error msgs.NoActiveTeam
    | Some teamId ->
        let! members = teamApi.GetTeamMembers teamId
        let! permsResult = permissionApi.GetTeamPermissions teamId
        let! accessible = accessibilityApi.GetAccessibleModules()

        match permsResult with
        | Error msg -> return Error msg
        | Ok perms ->
            return
                Ok {
                    TeamId = teamId
                    Members = members
                    Managed = accessible.Managed |> List.sort
                    Permissions = perms
                }
}

/// The snapshot read: several calls in one block, so it is named for this
/// module rather than for one API method.
let internal loadSnapshot (msgs: PermissionsAdminMessages) : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call "PermissionsAdminUI.loadSnapshot" (fun () -> loadSnapshotAsync msgs) () (fun reply ->
            SnapshotLoaded(RemoteEffect.flatten reply))
    )
]

let internal saveDefaults
    (teamId: string)
    (defaults: Map<string, ModulePermission list>)
    : Step<Msg, RemoteEffect<Msg>> list =
    [
        Do(
            RemoteEffect.call
                "PermissionApi.SetTeamDefaults"
                permissionApi.SetTeamDefaults
                (teamId, defaults)
                (RemoteEffect.flatten >> SaveDefaultsResult)
        )
    ]

let internal setExposure
    (teamId: string)
    (moduleName: string)
    (previous: ModuleExposure)
    (next: ModuleExposure)
    : Step<Msg, RemoteEffect<Msg>> list =
    [
        Do(
            RemoteEffect.call
                "PermissionApi.SetModuleExposure"
                permissionApi.SetModuleExposure
                (teamId, moduleName, next)
                (fun reply -> ExposureResult(moduleName, previous, next, RemoteEffect.flatten reply))
        )
    ]

let internal saveMember
    (teamId: string)
    (userId: string)
    (moduleName: string)
    (perms: ModulePermission list)
    : Step<Msg, RemoteEffect<Msg>> list =
    [
        Do(
            RemoteEffect.call
                "PermissionApi.SetMemberPermissions"
                permissionApi.SetMemberPermissions
                (teamId, userId, moduleName, perms)
                (fun reply -> SaveMemberResult(userId, moduleName, RemoteEffect.flatten reply))
        )
    ]

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    let model = {
        ActiveTab = TeamDefaultsTab
        Snapshot = Remote.Loading
        EditingDefaults = None
        SelectedMember = None
        EditingMembers = Map.empty
        SaveInFlight = false
        Banner = None
        OnAccessibleModulesChanged = ctx.OnAccessibleModulesChanged
        Messages = ctx.Messages.PermissionsAdmin
    }

    (model, loadSnapshot model.Messages)

// ─── Update ──────────────────────────────────────────────────────────

/// Apply `f` to the loaded snapshot; a model with no snapshot is unchanged.
let private mapSnapshot (f: TeamView -> TeamView) (model: Model) =
    match Remote.toOption model.Snapshot with
    | Some snap -> {
        model with
            Snapshot = Remote.Loaded(f snap)
      }
    | None -> model

let private withExposure (moduleName: string) (state: ModuleExposure) (snap: TeamView) = {
    snap with
        Permissions = {
            snap.Permissions with
                Exposure = applyExposure moduleName state snap.Permissions.Exposure
        }
}

/// The banner after an exposure change lands: one whole sentence per case
/// rather than a composed "{moduleName} is now {verb}." template, so a
/// translation is never assembled from independently-ordered fragments.
let private exposureBanner (msgs: PermissionsAdminMessages) (moduleName: string) (attempted: ModuleExposure) =
    match attempted with
    | ModuleExposure.Available -> msgs.ExposureNowAvailable moduleName
    | ModuleExposure.Hidden -> msgs.ExposureNowHidden moduleName
    | ModuleExposure.Unavailable -> msgs.ExposureNowUnavailable moduleName

/// Grant (`add = true`) or strip `level` on one cell's permissions; a cell
/// already in that state is returned unchanged.
let private setLevel (level: ModulePermission) (add: bool) (current: ModulePermission list) =
    if add && not (List.contains level current) then
        current @ [ level ]
    elif not add && List.contains level current then
        current |> List.filter (fun p -> p <> level)
    else
        current

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SwitchTab tab ->
        {
            model with
                ActiveTab = tab
                Banner = None
        },
        []

    | RefreshSnapshot ->
        {
            model with
                Snapshot = Remote.Loading
                EditingDefaults = None
                EditingMembers = Map.empty
                Banner = None
        },
        loadSnapshot model.Messages

    | SnapshotLoaded(Ok snap) ->
        // First member auto-selected so the Members tab renders an
        // editor pane on first land. If the team has no members the
        // editor pane shows a "no members" empty state.
        let firstMember = snap.Members |> List.tryHead |> Option.map _.UserId

        {
            model with
                Snapshot = Remote.Loaded snap
                EditingDefaults = Some snap.Permissions.Defaults
                EditingMembers = Map.empty
                SelectedMember = firstMember
                Banner = None
        },
        []

    | SnapshotLoaded(Error msg) ->
        {
            model with
                Snapshot = Remote.Failed msg
                EditingDefaults = None
                EditingMembers = Map.empty
        },
        []

    | ToggleDefault(moduleName, perm) ->
        match model.EditingDefaults with
        | None -> model, []
        | Some edits ->
            let current = Map.tryFind moduleName edits |> Option.defaultValue []

            // Store the empty list explicitly rather than removing the
            // key. Removing it could empty the whole map, which
            // `AccessContext.canAccessModule` reads as "unrestricted" —
            // silently flipping the team back to every-member-sees-
            // everything. An explicit `[]` entry means "no default
            // permission for this module", distinct from "no document".
            {
                model with
                    EditingDefaults = Some(edits |> Map.add moduleName (toggleInList perm current))
                    Banner = None
            },
            []

    | ResetDefaults ->
        match Remote.toOption model.Snapshot with
        | None -> model, []
        | Some snap ->
            {
                model with
                    EditingDefaults = Some snap.Permissions.Defaults
                    Banner = None
            },
            []

    | SaveDefaults ->
        match Remote.toOption model.Snapshot, model.EditingDefaults with
        | Some snap, Some edits when not model.SaveInFlight ->
            { model with SaveInFlight = true }, saveDefaults snap.TeamId edits
        | _ -> model, []

    | SaveDefaultsResult(Ok()) ->
        // Persist the just-saved edits into the snapshot's
        // `TeamPermissions` so subsequent reads (Modules tab, member
        // editor) reflect the truth without a full reload.
        let saved =
            match model.EditingDefaults with
            | Some edits ->
                model
                |> mapSnapshot (fun snap -> {
                    snap with
                        Permissions = {
                            snap.Permissions with
                                Defaults = edits
                        }
                })
            | None -> model

        {
            saved with
                SaveInFlight = false
                Banner = Some(Notice.Info(model.Messages.TeamDefaultsSaved))
        },
        []

    | SaveDefaultsResult(Error msg) ->
        {
            model with
                SaveInFlight = false
                Banner = Some(Notice.Problem(model.Messages.SaveFailed msg))
        },
        []

    | SelectMember userId ->
        {
            model with
                SelectedMember = Some userId
                Banner = None
        },
        []

    | ToggleMemberPermission(userId, moduleName, perm) ->
        // Overlay-only: toggle the level on the cell's effective value.
        match Remote.toOption model.Snapshot with
        | None -> model, []
        | Some snap ->
            let current = effectiveMemberPerms snap model.EditingMembers userId moduleName

            {
                model with
                    EditingMembers = model.EditingMembers |> Map.add (userId, moduleName) (toggleInList perm current)
                    Banner = None
            },
            []

    | ToggleMemberColumn(userId, level, add) ->
        // Select-all / clear-all for one column on the given user's row
        // set: every managed module whose effective value changes gets an
        // overlay entry; the rest are left alone.
        match Remote.toOption model.Snapshot with
        | None -> model, []
        | Some snap ->
            let editing =
                snap.Managed
                |> List.fold
                    (fun acc moduleName ->
                        let current = effectiveMemberPerms snap acc userId moduleName
                        let next = setLevel level add current

                        if next = current then
                            acc
                        else
                            acc |> Map.add (userId, moduleName) next)
                    model.EditingMembers

            {
                model with
                    EditingMembers = editing
                    Banner = None
            },
            []

    | ResetMembers userId ->
        // Drop every pending overlay entry for the given user.
        {
            model with
                EditingMembers = model.EditingMembers |> Map.filter (fun (uid, _) _ -> uid <> userId)
                Banner = None
        },
        []

    | SaveMembers userId ->
        match Remote.toOption model.Snapshot with
        | Some snap when not model.SaveInFlight ->
            // One save per touched (userId, moduleName) cell:
            // `PermissionApi.SetMemberPermissions` is per-member-per-module.
            let pending =
                model.EditingMembers
                |> Map.toList
                |> List.choose (fun ((uid, moduleName), next) -> if uid = userId then Some(moduleName, next) else None)

            if List.isEmpty pending then
                { model with Banner = None }, []
            else
                { model with SaveInFlight = true },
                pending
                |> List.collect (fun (moduleName, next) -> saveMember snap.TeamId userId moduleName next)
        | _ -> model, []

    | SaveMemberResult(userId, moduleName, Ok()) ->
        // Drop the pending overlay entry on success. When the last pending
        // entry for the user clears, end the SaveInFlight and reload the
        // snapshot once to refresh the persisted view.
        let editing = model.EditingMembers |> Map.remove (userId, moduleName)

        if editing |> Map.exists (fun (uid, _) _ -> uid = userId) then
            {
                model with
                    EditingMembers = editing
                    Banner = Some(Notice.Info(model.Messages.SavedModuleForUser moduleName userId))
            },
            []
        else
            {
                model with
                    EditingMembers = editing
                    SaveInFlight = false
                    Banner = Some(Notice.Info(model.Messages.SavedPermissionsFor userId))
            },
            loadSnapshot model.Messages

    | SaveMemberResult(userId, moduleName, Error msg) ->
        {
            model with
                SaveInFlight = false
                Banner = Some(Notice.Problem(model.Messages.OverrideSaveFailed userId moduleName msg))
        },
        []

    | SetExposure(moduleName, previous, next) ->
        match Remote.toOption model.Snapshot with
        | None -> model, []
        | Some snap ->
            // Optimistic local update so the selector responds instantly;
            // reverted to `previous` on a failed round-trip.
            {
                model with
                    Snapshot = Remote.Loaded(withExposure moduleName next snap)
                    Banner = None
            },
            setExposure snap.TeamId moduleName previous next

    | ExposureResult(moduleName, _previous, attempted, Ok()) ->
        // Tell the shell to re-fetch its accessible-modules list so the
        // sidebar (and the admin "show hidden modules" toggle) reflect
        // the new exposure live, without a manual reload. Mirrors
        // `TeamManagerUI`'s direct `OnTeamSwitched` invocation.
        model.OnAccessibleModulesChanged |> Option.iter (fun f -> f ())

        {
            model with
                Banner = Some(Notice.Info(exposureBanner model.Messages moduleName attempted))
        },
        []

    | ExposureResult(moduleName, previous, _attempted, Error msg) ->
        // Revert the optimistic change back to the prior state.
        {
            mapSnapshot (withExposure moduleName previous) model with
                Banner = Some(Notice.Problem(model.Messages.ExposureChangeFailed moduleName msg))
        },
        []

    | DismissBanner -> { model with Banner = None }, []

// ─── View ────────────────────────────────────────────────────────────

/// The signed-in caller, as the member list names them: their display name
/// over the raw user id, and their email beneath it.
type internal Caller = {
    UserId: string
    DisplayName: string option
    Email: string option
}

let internal caller () : Caller = {
    UserId = UserSession.getUserId ()
    DisplayName = UserSession.getDisplayName ()
    Email = UserSession.getEmail ()
}

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: PermissionsAdminMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./PermissionsAdminTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "PermissionsAdminTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.PermissionsAdmin
            Locale = ctx.Locale
        |}
    )

// ─── Module registration ─────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.PermissionsAdmin"

/// The built-in permissions admin (`ShellSlots.builtIn` builds its module). The
/// module fills the `PermissionsAdmin` shell slot, admitted on any
/// authenticated surface — Anonymous mode has
/// no role concept so the gate is suppressed by construction.
///
/// Sits in the "Admin" sidebar group alongside `TeamConfigUI`,
/// `WebhookAdminUI`, `DataIngestionUI`. Owner/Admin gating on the
/// write paths is enforced server-side via `PermissionApi`'s docstring
/// contract; the client surfaces the gating message as a banner.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Permissions"
        Icon = ToolUp.Platform.Icons.lock
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