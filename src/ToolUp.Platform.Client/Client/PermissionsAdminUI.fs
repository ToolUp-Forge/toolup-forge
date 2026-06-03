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
//   - **Modules** — read-only summary: every managed module + its
//     team-default permissions + the count of members carrying
//     an explicit override.
//
// **Owner/Admin gating.** Server-side `PermissionApi` already returns
// `Error` for non-Owner/Admin callers (per the docstring on
// `PermissionApi` in `TeamTypes.fs`). The client shows the read
// surface to any team member and surfaces the gating message as a
// banner on the failed write path; the sidebar role filter further
// hides the entry from members in Team / MultiTeam modes.
//
// Auto-injected by `SDK.Client.prepareModules` in every non-Anonymous
// mode unless `ClientConfig.PermissionsAdmin = NoPermissionsAdmin`.
// Anonymous mode never has role concepts so the gate is suppressed by
// construction.

// ─── Model ───────────────────────────────────────────────────────────

type Tab =
    | TeamDefaultsTab
    | MembersTab
    | ModulesTab

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

/// Bundle of "everything needed to render once we know the active team":
/// the managed-module list (`Managed` from `AccessibilityApi`) + the
/// member list + the persisted `TeamPermissions` document. Wrapping
/// these together makes the "loading vs ready" decision a single
/// `LoadState` check rather than three parallel ones.
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
    /// MultiTeam selection); error states surface as `LoadError`.
    Snapshot: LoadState<TeamView>
    /// Local-edit overlay for the team-defaults map. `None` while the
    /// snapshot is loading; `Some` mirrors the persisted Defaults and
    /// accumulates uncommitted cell toggles until the operator saves
    /// or reloads.
    EditingDefaults: Map<string, ModulePermission list> option
    /// On the Members tab: which member is selected for the override
    /// editor. Defaults to the first member after load.
    SelectedMember: string option
    /// 0.5.7 — Local-edit overlay for per-member overrides, keyed by
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
    Banner: (bool * string) option
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
    /// per-member override matrix. 0.5.7 — Updates the `EditingMembers`
    /// overlay only; no server round-trip until `SaveMembers`. The
    /// snapshot reload that previously rebuilt the page on every
    /// click is gone — the checkbox responds locally and the operator
    /// commits the whole diff via the "Update" button.
    | ToggleMemberPermission of userId: string * moduleName: string * ModulePermission
    /// 0.5.7 — Select-all / clear-all for a single column on the
    /// per-member override matrix. Applies the level to every managed
    /// module in `EditingMembers` for the given user — either grants
    /// the level on every module (`add = true`) or strips it from
    /// every module (`add = false`). Idempotent: re-applying does not
    /// duplicate the level on rows that already carry it.
    | ToggleMemberColumn of userId: string * level: ModulePermission * add: bool
    /// 0.5.7 — Discard uncommitted edits in `EditingMembers` for the
    /// given user.
    | ResetMembers of userId: string
    /// 0.5.7 — Round-trip every cell in `EditingMembers` for the
    /// given user through `PermissionApi.SetMemberPermissions`, one
    /// call per touched module, in dispatched order. The previous
    /// per-cell round-trip dies with the local-overlay model.
    | SaveMembers of userId: string
    | SaveMemberResult of userId: string * moduleName: string * Result<unit, string>
    /// Banner dismissal.
    | DismissBanner

// ─── API proxies ─────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
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

let private permLabel (p: ModulePermission) =
    match p with
    | ModulePermission.Read -> "Read"
    | ModulePermission.Write -> "Write"
    | ModulePermission.Admin -> "Admin"
    | ModulePermission.SchemaOnly -> "Schema-only"

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

let private memberPerms (perms: TeamPermissions) (userId: string) (moduleName: string) =
    match Map.tryFind userId perms.Members with
    | Some modMap ->
        match Map.tryFind moduleName modMap with
        | Some ps -> Some ps
        | None -> None
    | None -> None

// ─── Init / load chain ───────────────────────────────────────────────

/// Fetch the active team's full view in one async block so the UI
/// renders a single coherent loaded state rather than a flicker of
/// partial loads. Errors at any leg surface as a `LoadError` banner
/// the operator can retry.
let private loadSnapshotAsync () = async {
    let! activeTeamOpt = teamApi.GetActiveTeam()

    match activeTeamOpt with
    | None -> return Error "No active team selected. Switch into a team before managing permissions."
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

let private loadSnapshotCmd () =
    Cmd.OfRemoting.call loadSnapshotAsync () SnapshotLoaded (fun ex -> SnapshotLoaded(Error ex.Message))

let private saveDefaultsCmd (teamId: string) (defaults: Map<string, ModulePermission list>) =
    Cmd.OfRemoting.call permissionApi.SetTeamDefaults (teamId, defaults) SaveDefaultsResult (fun ex ->
        SaveDefaultsResult(Error ex.Message))

let private saveMemberCmd (teamId: string) (userId: string) (moduleName: string) (perms: ModulePermission list) =
    Cmd.OfRemoting.call
        permissionApi.SetMemberPermissions
        (teamId, userId, moduleName, perms)
        (fun result -> SaveMemberResult(userId, moduleName, result))
        (fun ex -> SaveMemberResult(userId, moduleName, Error ex.Message))

let init () : Model * Cmd<Msg> =
    let model = {
        ActiveTab = TeamDefaultsTab
        Snapshot = Loading
        EditingDefaults = None
        SelectedMember = None
        EditingMembers = Map.empty
        SaveInFlight = false
        Banner = None
    }

    model, loadSnapshotCmd ()

// ─── Update ──────────────────────────────────────────────────────────

let private snapshotOpt (model: Model) =
    match model.Snapshot with
    | Loaded snap -> Some snap
    | _ -> None

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
                Snapshot = Loading
                EditingDefaults = None
                EditingMembers = Map.empty
                Banner = None
        },
        loadSnapshotCmd ()

    | SnapshotLoaded(Ok snap) ->
        // First member auto-selected so the Members tab renders an
        // editor pane on first land. If the team has no members the
        // editor pane shows a "no members" empty state.
        let firstMember = snap.Members |> List.tryHead |> Option.map _.UserId

        {
            model with
                Snapshot = Loaded snap
                EditingDefaults = Some snap.Permissions.Defaults
                EditingMembers = Map.empty
                SelectedMember = firstMember
                Banner = None
        },
        Cmd.none

    | SnapshotLoaded(Error msg) ->
        {
            model with
                Snapshot = LoadError msg
                EditingDefaults = None
                EditingMembers = Map.empty
        },
        Cmd.none

    | ToggleDefault(moduleName, perm) ->
        match model.EditingDefaults with
        | None -> model, Cmd.none
        | Some edits ->
            let current = Map.tryFind moduleName edits |> Option.defaultValue []
            let next = toggleInList perm current

            let edits' =
                if List.isEmpty next then
                    edits |> Map.remove moduleName
                else
                    edits |> Map.add moduleName next

            {
                model with
                    EditingDefaults = Some edits'
                    Banner = None
            },
            Cmd.none

    | ResetDefaults ->
        match snapshotOpt model with
        | None -> model, Cmd.none
        | Some snap ->
            {
                model with
                    EditingDefaults = Some snap.Permissions.Defaults
                    Banner = None
            },
            Cmd.none

    | SaveDefaults ->
        match snapshotOpt model, model.EditingDefaults with
        | Some snap, Some edits when not model.SaveInFlight ->
            { model with SaveInFlight = true }, saveDefaultsCmd snap.TeamId edits
        | _ -> model, Cmd.none

    | SaveDefaultsResult(Ok()) ->
        // Persist the just-saved edits into the snapshot's
        // `TeamPermissions` so subsequent reads (Modules tab, member
        // editor) reflect the truth without a full reload.
        let model' =
            match snapshotOpt model, model.EditingDefaults with
            | Some snap, Some edits ->
                let updatedSnap = {
                    snap with
                        Permissions = {
                            snap.Permissions with
                                Defaults = edits
                        }
                }

                {
                    model with
                        Snapshot = Loaded updatedSnap
                }
            | _ -> model

        {
            model' with
                SaveInFlight = false
                Banner = Some(false, "Team defaults saved.")
        },
        Cmd.none

    | SaveDefaultsResult(Error msg) ->
        {
            model with
                SaveInFlight = false
                Banner = Some(true, $"Save failed: {msg}")
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
        // 0.5.7 — overlay-only mutation. Read the cell's current
        // effective value (overlay if present, else persisted override
        // or team default), toggle the level, persist to overlay.
        match snapshotOpt model with
        | None -> model, Cmd.none
        | Some snap ->
            let key = (userId, moduleName)

            let current =
                match Map.tryFind key model.EditingMembers with
                | Some v -> v
                | None ->
                    match memberPerms snap.Permissions userId moduleName with
                    | Some ps -> ps
                    | None -> Map.tryFind moduleName snap.Permissions.Defaults |> Option.defaultValue []

            let next = toggleInList perm current

            {
                model with
                    EditingMembers = model.EditingMembers |> Map.add key next
                    Banner = None
            },
            Cmd.none

    | ToggleMemberColumn(userId, level, add) ->
        // 0.5.7 — Select-all / clear-all for one column on the given
        // user's row set. Walks every managed module and applies the
        // overlay update; cells that already carry the level when
        // `add=true` are left alone, cells that don't carry it when
        // `add=false` are left alone.
        match snapshotOpt model with
        | None -> model, Cmd.none
        | Some snap ->
            let editing' =
                snap.Managed
                |> List.fold
                    (fun acc moduleName ->
                        let key = (userId, moduleName)

                        let current =
                            match Map.tryFind key acc with
                            | Some v -> v
                            | None ->
                                match memberPerms snap.Permissions userId moduleName with
                                | Some ps -> ps
                                | None -> Map.tryFind moduleName snap.Permissions.Defaults |> Option.defaultValue []

                        let next =
                            if add && not (List.contains level current) then
                                current @ [ level ]
                            elif not add && List.contains level current then
                                current |> List.filter (fun p -> p <> level)
                            else
                                current

                        if next = current then acc else acc |> Map.add key next)
                    model.EditingMembers

            {
                model with
                    EditingMembers = editing'
                    Banner = None
            },
            Cmd.none

    | ResetMembers userId ->
        // Drop every pending overlay entry for the given user.
        let editing' = model.EditingMembers |> Map.filter (fun (uid, _) _ -> uid <> userId)

        {
            model with
                EditingMembers = editing'
                Banner = None
        },
        Cmd.none

    | SaveMembers userId ->
        match snapshotOpt model with
        | None -> model, Cmd.none
        | Some snap when not model.SaveInFlight ->
            // Dispatch one save per touched (userId, moduleName) cell.
            // The SDK's `PermissionApi.SetMemberPermissions` is per-
            // member-per-module today; bulk-set is tracked separately.
            let pending =
                model.EditingMembers
                |> Map.toList
                |> List.choose (fun ((uid, moduleName), next) -> if uid = userId then Some(moduleName, next) else None)

            if List.isEmpty pending then
                { model with Banner = None }, Cmd.none
            else
                let cmds =
                    pending
                    |> List.map (fun (moduleName, next) -> saveMemberCmd snap.TeamId userId moduleName next)

                { model with SaveInFlight = true }, Cmd.batch cmds
        | _ -> model, Cmd.none

    | SaveMemberResult(userId, moduleName, Ok()) ->
        // 0.5.7 — drop the pending overlay entry on success. When the
        // last pending entry for the user clears, end the SaveInFlight
        // and reload the snapshot once to refresh the persisted view.
        let key = (userId, moduleName)

        let editing' = model.EditingMembers |> Map.remove key

        let remainingForUser =
            editing' |> Map.filter (fun (uid, _) _ -> uid = userId) |> Map.isEmpty |> not

        if remainingForUser then
            {
                model with
                    EditingMembers = editing'
                    Banner = Some(false, $"Saved {moduleName} for {userId}.")
            },
            Cmd.none
        else
            {
                model with
                    EditingMembers = editing'
                    SaveInFlight = false
                    Banner = Some(false, $"Saved permissions for {userId}.")
            },
            loadSnapshotCmd ()

    | SaveMemberResult(userId, moduleName, Error msg) ->
        {
            model with
                SaveInFlight = false
                Banner = Some(true, $"Override save failed for {userId} / {moduleName}: {msg}")
        },
        Cmd.none

    | DismissBanner -> { model with Banner = None }, Cmd.none

// ─── View helpers ────────────────────────────────────────────────────

let private banner (model: Model) (dispatch: Msg -> unit) =
    match model.Banner with
    | None -> Html.none
    | Some(isError, msg) ->
        let cls =
            if isError then
                "bg-red-50 border-red-200 text-red-700"
            else
                "bg-green-50 border-green-200 text-green-700"

        Html.div [
            prop.className $"mx-6 mt-4 p-3 border rounded text-sm flex items-center justify-between {cls}"
            prop.children [
                Html.span [ prop.text msg ]
                Html.button [
                    prop.className "text-xs text-gray-500 hover:text-gray-700 px-2"
                    prop.text "Dismiss"
                    prop.onClick (fun _ -> dispatch DismissBanner)
                ]
            ]
        ]

let private tabButton (label: string) (active: bool) (onClick: unit -> unit) =
    Html.button [
        prop.className [
            "px-4 py-2 text-sm font-medium border-b-2 transition-colors"
            if active then
                "border-brand text-brand"
            else
                "border-transparent text-gray-500 hover:text-gray-700"
        ]
        prop.text label
        prop.onClick (fun _ -> onClick ())
    ]

let private tabBar (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex gap-1 border-b border-border bg-white px-4"
        prop.children [
            tabButton "Team Defaults" (model.ActiveTab = TeamDefaultsTab) (fun () ->
                dispatch (SwitchTab TeamDefaultsTab))

            tabButton "Members" (model.ActiveTab = MembersTab) (fun () -> dispatch (SwitchTab MembersTab))
            tabButton "Modules" (model.ActiveTab = ModulesTab) (fun () -> dispatch (SwitchTab ModulesTab))
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

let private permRow
    (moduleName: string)
    (perms: ModulePermission list)
    (disabled: bool)
    (onToggle: ModulePermission -> unit)
    =
    Html.tr [
        prop.className "border-t border-border"
        prop.children [
            Html.td [ prop.className "px-3 py-2 font-medium text-sm"; prop.text moduleName ]
            for level in allPermLevels do
                Html.td [
                    prop.className "px-3 py-2"
                    prop.children [
                        permCheckbox (permLabel level) (List.contains level perms) disabled (fun () -> onToggle level)
                    ]
                ]
        ]
    ]

/// 0.5.7 — Column action shape. When `permTable` is called with
/// `Some onColumnAction`, each non-header column gets a tiny "all /
/// none" toggle in its header. `allOn` is computed by the caller (it
/// needs the underlying overlay/snapshot state to decide); the
/// callback receives `(level, add)` and either grants or clears the
/// level across every row.
type private PermColumnAction = {
    allOn: ModulePermission -> bool
    onToggle: ModulePermission * bool -> unit
}

let private permTable (header: string) (rows: ReactElement list) (columnAction: PermColumnAction option) =
    if List.isEmpty rows then
        Html.p [
            prop.className "text-sm text-gray-500 italic"
            prop.text "No managed modules. Modules are registered server-side via `ServerConfig.ModuleNames`."
        ]
    else
        Html.div [
            prop.className "border border-border rounded-lg overflow-hidden"
            prop.children [
                Html.table [
                    prop.className "w-full text-sm"
                    prop.children [
                        Html.thead [
                            prop.className "bg-gray-50"
                            prop.children [
                                Html.tr [
                                    prop.children [
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text header
                                        ]
                                        for level in allPermLevels do
                                            Html.th [
                                                prop.className "text-left px-3 py-2 font-medium text-gray-600 w-24"
                                                prop.children [
                                                    Html.div [
                                                        prop.className "flex flex-col gap-0.5"
                                                        prop.children [
                                                            Html.span [ prop.text (permLabel level) ]
                                                            match columnAction with
                                                            | Some action ->
                                                                let on = action.allOn level

                                                                Html.button [
                                                                    prop.className
                                                                        "text-[10px] text-brand hover:underline self-start"
                                                                    prop.text (if on then "Clear all" else "Select all")
                                                                    prop.onClick (fun _ ->
                                                                        action.onToggle (level, not on))
                                                                ]
                                                            | None -> ()
                                                        ]
                                                    ]
                                                ]
                                            ]
                                    ]
                                ]
                            ]
                        ]
                        Html.tbody [ prop.children rows ]
                    ]
                ]
            ]
        ]

// ─── Team defaults tab ───────────────────────────────────────────────

let private teamDefaultsView (model: Model) (snap: TeamView) (dispatch: Msg -> unit) =
    let edits = model.EditingDefaults |> Option.defaultValue snap.Permissions.Defaults

    let dirty = edits <> snap.Permissions.Defaults

    let rows =
        snap.Managed
        |> List.map (fun m ->
            let perms = Map.tryFind m edits |> Option.defaultValue []
            permRow m perms model.SaveInFlight (fun level -> dispatch (ToggleDefault(m, level))))

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text "Team defaults" ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text
                                    "Per-module permissions applied to every team member who has no explicit override. Empty rows mean the module is unreachable for that member."
                            ]
                        ]
                    ]
                    Html.div [
                        prop.className "flex gap-2"
                        prop.children [
                            Html.button [
                                prop.className [
                                    "px-3 py-1.5 text-sm border rounded transition-colors"
                                    if dirty && not model.SaveInFlight then
                                        "bg-white text-gray-700 border-border hover:bg-gray-50"
                                    else
                                        "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
                                ]
                                prop.disabled (not dirty || model.SaveInFlight)
                                prop.text "Reset"
                                prop.onClick (fun _ -> dispatch ResetDefaults)
                            ]
                            Html.button [
                                prop.className [
                                    "px-3 py-1.5 text-sm font-medium border rounded transition-colors"
                                    if dirty && not model.SaveInFlight then
                                        "bg-brand text-white border-brand hover:opacity-90"
                                    else
                                        "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
                                ]
                                prop.disabled (not dirty || model.SaveInFlight)
                                prop.text (if model.SaveInFlight then "Saving..." else "Save defaults")
                                prop.onClick (fun _ -> dispatch SaveDefaults)
                            ]
                        ]
                    ]
                ]
            ]
            // 0.5.7 — column "Select all" / "Clear all" on team
            // defaults. Reads from the local `edits` overlay so the
            // toggle reflects the operator's in-progress changes
            // rather than the persisted snapshot.
            let columnAction = {
                allOn =
                    fun level ->
                        snap.Managed
                        |> List.forall (fun m ->
                            edits |> Map.tryFind m |> Option.defaultValue [] |> List.contains level)
                onToggle =
                    fun (level, add) ->
                        for m in snap.Managed do
                            let current = edits |> Map.tryFind m |> Option.defaultValue []

                            let already = List.contains level current

                            if add <> already then
                                dispatch (ToggleDefault(m, level))
            }

            permTable "Module" rows (Some columnAction)
        ]
    ]

// ─── Members tab ─────────────────────────────────────────────────────

let private memberListItem (model: Model) (snap: TeamView) (m: TeamMembership) (dispatch: Msg -> unit) =
    let selected = model.SelectedMember = Some m.UserId
    let hasOver = hasOverride snap.Permissions m.UserId

    // 0.5.7 — for the currently-signed-in user, prefer the JWT-derived
    // display name / email over the raw UserId. For other members we
    // still fall back to UserId — a per-member profile-lookup
    // substrate (`IUserProfileStore` mirroring Entra Graph) is
    // tracked separately.
    let isSelf = UserSession.getUserId () = m.UserId

    let displayLabel =
        if isSelf then
            UserSession.getDisplayName () |> Option.defaultValue m.UserId
        else
            m.UserId

    let emailSubtitle = if isSelf then UserSession.getEmail () else None

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
                    Html.span [ prop.className "font-medium text-sm"; prop.text displayLabel ]
                    if hasOver then
                        Html.span [
                            prop.className
                                "inline-block text-[10px] px-1.5 py-0.5 rounded border bg-purple-100 border-purple-200 text-purple-700"
                            prop.text "override"
                        ]
                ]
            ]
            match emailSubtitle with
            | Some email -> Html.div [ prop.className "text-xs text-gray-500 truncate"; prop.text email ]
            | None -> ()
            Html.div [ prop.className "text-xs text-gray-500"; prop.text (string m.Role) ]
        ]
    ]

let private memberOverrideEditor (model: Model) (snap: TeamView) (userId: string) (dispatch: Msg -> unit) =
    // 0.5.7 — Read effective cell value from the overlay first
    // (operator's in-progress local edits), then the persisted
    // override, then the team default.
    let effectivePerms (moduleName: string) =
        match Map.tryFind (userId, moduleName) model.EditingMembers with
        | Some v -> v
        | None ->
            match memberPerms snap.Permissions userId moduleName with
            | Some ps -> ps
            | None -> Map.tryFind moduleName snap.Permissions.Defaults |> Option.defaultValue []

    let rows =
        snap.Managed
        |> List.map (fun m ->
            let perms = effectivePerms m
            permRow m perms model.SaveInFlight (fun level -> dispatch (ToggleMemberPermission(userId, m, level))))

    let overrideKeys =
        match Map.tryFind userId snap.Permissions.Members with
        | Some modMap -> Map.toList modMap |> List.map fst |> List.sort
        | None -> []

    let dirty = model.EditingMembers |> Map.exists (fun (uid, _) _ -> uid = userId)

    // 0.5.7 — Display name for the heading: prefer JWT name/email for
    // self, fall back to the raw UserId for others.
    let isSelf = UserSession.getUserId () = userId

    let displayLabel =
        if isSelf then
            UserSession.getDisplayName () |> Option.defaultValue userId
        else
            userId

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
                                prop.text $"Overrides — {displayLabel}"
                            ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text
                                    "Toggle any cell to stage an explicit per-member override. Use the column \"Select all\" links to grant or clear a level across every module. Effective permissions resolve to the override if present, otherwise the team default. Setting every level off for a module clears the override (member falls back to defaults)."
                            ]
                            if not (List.isEmpty overrideKeys) then
                                Html.p [
                                    prop.className "text-xs text-gray-500 mt-1"
                                    prop.text (sprintf "Active overrides on: %s" (String.concat ", " overrideKeys))
                                ]
                        ]
                    ]
                    // 0.5.7 — Reset + Update actions. Edits stay
                    // local until "Update" is pressed; no round-trip
                    // per checkbox click.
                    Html.div [
                        prop.className "flex items-center gap-2 shrink-0"
                        prop.children [
                            Html.button [
                                prop.className [
                                    "px-3 py-1.5 text-sm border rounded transition-colors"
                                    if dirty && not model.SaveInFlight then
                                        "border-gray-300 text-gray-700 hover:bg-gray-100"
                                    else
                                        "border-gray-200 text-gray-400 cursor-not-allowed"
                                ]
                                prop.disabled (not dirty || model.SaveInFlight)
                                prop.text "Reset"
                                prop.onClick (fun _ -> dispatch (ResetMembers userId))
                            ]
                            Html.button [
                                prop.className [
                                    "px-3 py-1.5 text-sm font-medium border rounded transition-colors"
                                    if dirty && not model.SaveInFlight then
                                        "bg-brand text-white border-brand hover:opacity-90"
                                    else
                                        "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
                                ]
                                prop.disabled (not dirty || model.SaveInFlight)
                                prop.text (if model.SaveInFlight then "Saving..." else "Update")
                                prop.onClick (fun _ -> dispatch (SaveMembers userId))
                            ]
                        ]
                    ]
                ]
            ]
            permTable "Module" rows (Some columnAction)
        ]
    ]

let private membersView (model: Model) (snap: TeamView) (dispatch: Msg -> unit) =
    if List.isEmpty snap.Members then
        Html.div [
            prop.className "flex-1 p-6 overflow-y-auto"
            prop.children [
                Html.p [
                    prop.className "text-sm text-gray-500 italic"
                    prop.text
                        "This team has no members yet. Add members from Team Manager before configuring per-member overrides."
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
                            prop.className "px-3 py-2 text-xs font-medium text-gray-500 uppercase"
                            prop.text "Members"
                        ]
                        for m in snap.Members do
                            memberListItem model snap m dispatch
                    ]
                ]
                Html.div [
                    prop.className "flex-1 overflow-y-auto"
                    prop.children [
                        match model.SelectedMember with
                        | None ->
                            Html.div [
                                prop.className "p-6 text-sm text-gray-500 italic"
                                prop.text "Select a member to view and edit their overrides."
                            ]
                        | Some uid -> memberOverrideEditor model snap uid dispatch
                    ]
                ]
            ]
        ]

// ─── Modules tab (read-only summary) ─────────────────────────────────

let private moduleSummaryRow (snap: TeamView) (moduleName: string) =
    let defaults =
        Map.tryFind moduleName snap.Permissions.Defaults |> Option.defaultValue []

    let overrideCount = memberOverrideCount snap.Permissions moduleName

    let renderPerms (ps: ModulePermission list) =
        if List.isEmpty ps then
            Html.span [ prop.className "text-xs text-gray-400"; prop.text "(no access)" ]
        else
            Html.span [
                prop.className "text-xs"
                prop.text (ps |> List.map permLabel |> String.concat " · ")
            ]

    Html.tr [
        prop.className "border-t border-border"
        prop.children [
            Html.td [ prop.className "px-3 py-2 font-medium text-sm"; prop.text moduleName ]
            Html.td [ prop.className "px-3 py-2"; prop.children [ renderPerms defaults ] ]
            Html.td [
                prop.className "px-3 py-2 text-xs text-gray-700 text-right font-mono"
                prop.text (string overrideCount)
            ]
        ]
    ]

let private modulesView (snap: TeamView) =
    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "mb-4"
                prop.children [
                    Html.h2 [ prop.className "text-lg font-semibold"; prop.text "Modules" ]
                    Html.p [
                        prop.className "text-xs text-gray-500"
                        prop.text
                            "Read-only summary: every managed module + its team-default permissions + the count of members carrying an explicit override on that module."
                    ]
                ]
            ]
            if List.isEmpty snap.Managed then
                Html.p [
                    prop.className "text-sm text-gray-500 italic"
                    prop.text "No managed modules. Modules are registered server-side via `ServerConfig.ModuleNames`."
                ]
            else
                Html.div [
                    prop.className "border border-border rounded-lg overflow-hidden"
                    prop.children [
                        Html.table [
                            prop.className "w-full text-sm"
                            prop.children [
                                Html.thead [
                                    prop.className "bg-gray-50"
                                    prop.children [
                                        Html.tr [
                                            prop.children [
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text "Module"
                                                ]
                                                Html.th [
                                                    prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                                    prop.text "Team default"
                                                ]
                                                Html.th [
                                                    prop.className "text-right px-3 py-2 font-medium text-gray-600 w-32"
                                                    prop.text "Overrides"
                                                ]
                                            ]
                                        ]
                                    ]
                                ]
                                Html.tbody [ prop.children (snap.Managed |> List.map (moduleSummaryRow snap)) ]
                            ]
                        ]
                    ]
                ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

let private errorPane (msg: string) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "p-4 bg-red-50 border border-red-200 rounded text-red-700 text-sm"
                prop.text msg
            ]
            Html.button [
                prop.className
                    "mt-3 px-3 py-1.5 text-sm font-medium bg-white text-gray-700 border border-border rounded hover:bg-gray-50"
                prop.text "Retry"
                prop.onClick (fun _ -> dispatch RefreshSnapshot)
            ]
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let content =
        match model.Snapshot with
        | NotLoaded
        | Loading ->
            Html.div [
                prop.className "flex-1 p-6 overflow-y-auto"
                prop.children [ Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading..." ] ]
            ]
        | LoadError msg -> errorPane msg dispatch
        | Loaded snap ->
            match model.ActiveTab with
            | TeamDefaultsTab -> teamDefaultsView model snap dispatch
            | MembersTab -> membersView model snap dispatch
            | ModulesTab -> modulesView snap

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ tabBar model dispatch; banner model dispatch; content ]
    ]

// ─── Module registration ─────────────────────────────────────────────

/// Create the built-in permissions admin as an `ErasedModule`. The
/// shell's `prepareModules` injects this in any non-Anonymous mode
/// unless `PermissionsAdmin = NoPermissionsAdmin` — Anonymous mode has
/// no role concept so the gate is suppressed by construction.
///
/// Sits in the "Admin" sidebar group alongside `TeamConfigUI`,
/// `WebhookAdminUI`, `DataIngestionUI`. Owner/Admin gating on the
/// write paths is enforced server-side via `PermissionApi`'s docstring
/// contract; the client surfaces the gating message as a banner.
let create (config: PermissionsAdminConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Permissions"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.lock

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.PermissionsAdmin"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register