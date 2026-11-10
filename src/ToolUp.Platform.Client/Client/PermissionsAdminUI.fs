// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module PermissionsAdminUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── PermissionsAdminUI ──────────────────────────────────────────────
//
// Tidy-Up sweep #3 — closes the Phase 4 + Phase 5 long-standing
// "PermissionsAdminUI built-in module" residual `[ ]`. Brings RBAC
// admin to surface parity with the other built-in admin modules
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

let private allPermLevels: ModulePermission list = [
    ModulePermission.Read
    ModulePermission.Write
    ModulePermission.Admin
    ModulePermission.SchemaOnly
]

let private permLabel (msgs: PermissionsAdminMessages) (p: ModulePermission) =
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

let private hasOverride (perms: TeamPermissions) (userId: string) =
    match Map.tryFind userId perms.Members with
    | Some m when not (Map.isEmpty m) -> true
    | _ -> false

let private memberOverrideCount (perms: TeamPermissions) (moduleName: string) =
    perms.Members
    |> Map.toSeq
    |> Seq.filter (fun (_, modMap) -> Map.containsKey moduleName modMap)
    |> Seq.length

/// The exposure state of a module — absence in the map ⇒ `Available`.
let private exposureOf (perms: TeamPermissions) (moduleName: string) =
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
let private effectiveMemberPerms
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

let private loadSnapshotCmd (msgs: PermissionsAdminMessages) =
    Cmd.OfRemoting.call loadSnapshotAsync msgs SnapshotLoaded (fun ex -> SnapshotLoaded(Error ex.Message))

let private saveDefaultsCmd (teamId: string) (defaults: Map<string, ModulePermission list>) =
    Cmd.OfRemoting.call permissionApi.SetTeamDefaults (teamId, defaults) SaveDefaultsResult (fun ex ->
        SaveDefaultsResult(Error ex.Message))

let private setExposureCmd (teamId: string) (moduleName: string) (previous: ModuleExposure) (next: ModuleExposure) =
    Cmd.OfRemoting.call
        permissionApi.SetModuleExposure
        (teamId, moduleName, next)
        (fun result -> ExposureResult(moduleName, previous, next, result))
        (fun ex -> ExposureResult(moduleName, previous, next, Error ex.Message))

let private saveMemberCmd (teamId: string) (userId: string) (moduleName: string) (perms: ModulePermission list) =
    Cmd.OfRemoting.call
        permissionApi.SetMemberPermissions
        (teamId, userId, moduleName, perms)
        (fun result -> SaveMemberResult(userId, moduleName, result))
        (fun ex -> SaveMemberResult(userId, moduleName, Error ex.Message))

let init (ctx: ClientModuleContext) : Model * Cmd<Msg> =
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

    model, loadSnapshotCmd model.Messages

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

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | SwitchTab tab ->
        {
            model with
                ActiveTab = tab
                Banner = None
        },
        Cmd.none

    | RefreshSnapshot ->
        {
            model with
                Snapshot = Remote.Loading
                EditingDefaults = None
                EditingMembers = Map.empty
                Banner = None
        },
        loadSnapshotCmd model.Messages

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
        Cmd.none

    | SnapshotLoaded(Error msg) ->
        {
            model with
                Snapshot = Remote.Failed msg
                EditingDefaults = None
                EditingMembers = Map.empty
        },
        Cmd.none

    | ToggleDefault(moduleName, perm) ->
        match model.EditingDefaults with
        | None -> model, Cmd.none
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
            Cmd.none

    | ResetDefaults ->
        match Remote.toOption model.Snapshot with
        | None -> model, Cmd.none
        | Some snap ->
            {
                model with
                    EditingDefaults = Some snap.Permissions.Defaults
                    Banner = None
            },
            Cmd.none

    | SaveDefaults ->
        match Remote.toOption model.Snapshot, model.EditingDefaults with
        | Some snap, Some edits when not model.SaveInFlight ->
            { model with SaveInFlight = true }, saveDefaultsCmd snap.TeamId edits
        | _ -> model, Cmd.none

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
        Cmd.none

    | SaveDefaultsResult(Error msg) ->
        {
            model with
                SaveInFlight = false
                Banner = Some(Notice.Problem(model.Messages.SaveFailed msg))
        },
        Cmd.none

    | SelectMember userId ->
        {
            model with
                SelectedMember = Some userId
                Banner = None
        },
        Cmd.none

    | ToggleMemberPermission(userId, moduleName, perm) ->
        // Overlay-only: toggle the level on the cell's effective value.
        match Remote.toOption model.Snapshot with
        | None -> model, Cmd.none
        | Some snap ->
            let current = effectiveMemberPerms snap model.EditingMembers userId moduleName

            {
                model with
                    EditingMembers = model.EditingMembers |> Map.add (userId, moduleName) (toggleInList perm current)
                    Banner = None
            },
            Cmd.none

    | ToggleMemberColumn(userId, level, add) ->
        // Select-all / clear-all for one column on the given user's row
        // set: every managed module whose effective value changes gets an
        // overlay entry; the rest are left alone.
        match Remote.toOption model.Snapshot with
        | None -> model, Cmd.none
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
            Cmd.none

    | ResetMembers userId ->
        // Drop every pending overlay entry for the given user.
        {
            model with
                EditingMembers = model.EditingMembers |> Map.filter (fun (uid, _) _ -> uid <> userId)
                Banner = None
        },
        Cmd.none

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
                { model with Banner = None }, Cmd.none
            else
                { model with SaveInFlight = true },
                pending
                |> List.map (fun (moduleName, next) -> saveMemberCmd snap.TeamId userId moduleName next)
                |> Cmd.batch
        | _ -> model, Cmd.none

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
            Cmd.none
        else
            {
                model with
                    EditingMembers = editing
                    SaveInFlight = false
                    Banner = Some(Notice.Info(model.Messages.SavedPermissionsFor userId))
            },
            loadSnapshotCmd model.Messages

    | SaveMemberResult(userId, moduleName, Error msg) ->
        {
            model with
                SaveInFlight = false
                Banner = Some(Notice.Problem(model.Messages.OverrideSaveFailed userId moduleName msg))
        },
        Cmd.none

    | SetExposure(moduleName, previous, next) ->
        match Remote.toOption model.Snapshot with
        | None -> model, Cmd.none
        | Some snap ->
            // Optimistic local update so the selector responds instantly;
            // reverted to `previous` on a failed round-trip.
            {
                model with
                    Snapshot = Remote.Loaded(withExposure moduleName next snap)
                    Banner = None
            },
            setExposureCmd snap.TeamId moduleName previous next

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
        Cmd.none

    | ExposureResult(moduleName, previous, _attempted, Error msg) ->
        // Revert the optimistic change back to the prior state.
        {
            mapSnapshot (withExposure moduleName previous) model with
                Banner = Some(Notice.Problem(model.Messages.ExposureChangeFailed moduleName msg))
        },
        Cmd.none

    | DismissBanner -> { model with Banner = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

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
            Forms.Button.primaryWith (Forms.Button.stateOf model.SaveInFlight msgs.Saving (not dirty)) saveLabel onSave
        ]
    ]

let private teamDefaultsView (msgs: PermissionsAdminMessages) (model: Model) (snap: TeamView) (dispatch: Msg -> unit) =
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
                    editActions msgs model dirty msgs.SaveDefaultsLabel (fun () -> dispatch ResetDefaults) (fun () ->
                        dispatch SaveDefaults)
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

/// A member's display label: for the signed-in user, the JWT-derived
/// display name over the raw UserId; for other members the UserId (a
/// per-member profile lookup is tracked separately).
let private memberLabel (userId: string) =
    if UserSession.getUserId () = userId then
        UserSession.getDisplayName () |> Option.defaultValue userId
    else
        userId

let private memberListItem
    (msgs: PermissionsAdminMessages)
    (model: Model)
    (snap: TeamView)
    (m: TeamMembership)
    (dispatch: Msg -> unit)
    =
    let selected = model.SelectedMember = Some m.UserId
    let hasOver = hasOverride snap.Permissions m.UserId

    let emailSubtitle =
        if UserSession.getUserId () = m.UserId then
            UserSession.getEmail ()
        else
            None

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
                    Html.span [ prop.className "font-medium text-sm"; prop.text (memberLabel m.UserId) ]
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
                                prop.text (msgs.OverridesHeading(memberLabel userId))
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

let private membersView (msgs: PermissionsAdminMessages) (model: Model) (snap: TeamView) (dispatch: Msg -> unit) =
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
                            memberListItem msgs model snap m dispatch
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
                        | Some uid -> memberOverrideEditor msgs model snap uid dispatch
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
            exposureOption ModuleExposure.Available msgs.ExposureAvailable "border-green-200 bg-green-50 text-green-700"
            exposureOption
                ModuleExposure.Hidden
                msgs.ExposureHidden
                ("border-gray-300 bg-gray-100 " + Tokens.Text.secondary)
            exposureOption ModuleExposure.Unavailable msgs.ExposureUnavailable "border-red-200 bg-red-50 text-red-700"
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

/// The module body as a React COMPONENT rather than a plain
/// render function, so it has a hook site from which to read the resolved
/// catalog. A module's `view` is invoked inline by the shell's own render,
/// where a hook would join the shell's hook order and break the moment the
/// active module changed; a component of its own has a stable identity and
/// its own. Same distinction `HealthMonitorUI.HealthMonitorBody` documents,
/// applied here because every tab under it renders catalog strings.
[<ReactComponent>]
let private PermissionsAdminBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).PermissionsAdmin

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
            | MembersTab -> membersView msgs model snap dispatch
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

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = PermissionsAdminBody model dispatch

// ─── Module registration ─────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `create` registers the module under it.
let moduleId = "_sdk.PermissionsAdmin"

/// Create the built-in permissions admin as an `ErasedModule`. The
/// module fills the `PermissionsAdmin` shell slot, admitted on any
/// authenticated surface — Anonymous mode has
/// no role concept so the gate is suppressed by construction.
///
/// Sits in the "Admin" sidebar group alongside `TeamConfigUI`,
/// `WebhookAdminUI`, `DataIngestionUI`. Owner/Admin gating on the
/// write paths is enforced server-side via `PermissionApi`'s docstring
/// contract; the client surfaces the gating message as a banner.
let create (config: ModuleLabel option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Permissions"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.lock

    // `init` is `ClientModuleContext -> Model * Cmd<Msg>` (it reads
    // `OnAccessibleModulesChanged`); seed `create` with the
    // empty context, then override with the real context-aware init via
    // `withContextInit`, the same wiring `TeamManagerUI` uses.
    ToolUp.Platform.ClientModule.create {
        Init = fun () -> init ToolUp.Platform.ClientModuleContext.empty
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    |> ToolUp.Platform.ClientModule.withContextInit init
    |> ToolUp.Platform.ClientModule.withView (fun model ctx -> PageContent.FullWidth(view model ctx.Dispatch))
    |> ToolUp.Platform.ClientModule.withGroup "Team Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register