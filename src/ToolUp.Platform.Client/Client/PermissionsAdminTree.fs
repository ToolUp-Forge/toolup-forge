// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The permissions admin as a Fuaran-UI tree, built and drawn in a file of its
/// own, which `PermissionsAdminUI`'s view mounts through `TreeSurface.deferred`
/// (the shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// The member list is kept per member, each id from the member's user id and
/// each message carrying it; the two matrices and the modules table are lists
/// of module names, which cannot key a `Keep`, so each is kept whole on what it
/// shows. The page owns no hook; the matrices' checkboxes are islands, since the
/// vocabulary's only free checkbox is a filter chip, which reads its name from
/// the browser's filter store.
module internal PermissionsAdminTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open PermissionsAdminUI
open KeepScoped

/// A notice; a dismissable one routes its dismissal through `update`.
let private problem (id: string) (text: string) : Node<'Msg> =
    Fuaran.callout id {
        Defaults.callout with
            Body = Nodes.literal text
            Tone = ToneVariant.Critical
    }

let private banner (msgs: PermissionsAdminMessages) (notice: Notice option) : Node<Msg> list =
    match notice with
    | Some(Notice.Problem text) -> [
        Nodes.Actions.banner "permissions-banner" ToneVariant.Critical text msgs.Dismiss DismissBanner
      ]
    | Some(Notice.Info text) -> [
        Nodes.Actions.banner "permissions-banner" ToneVariant.Info text msgs.Dismiss DismissBanner
      ]
    | None -> []

/// A labelled checkbox calling `onToggle` when it is toggled, inert while
/// `disabled`. An island: the vocabulary has no checkbox outside a form but a
/// filter chip, and a chip reads its name from the browser's filter store.
let private checkbox (id: string) (label: string) (value: bool) (disabled: bool) (onToggle: unit -> unit) =
    TreeLowering.island id (fun () ->
        Feliz.Html.label [
            Feliz.prop.className "inline-flex items-center gap-1 text-xs select-none"
            Feliz.prop.children [
                Feliz.Html.input [
                    Feliz.prop.type' "checkbox"
                    Feliz.prop.isChecked value
                    Feliz.prop.disabled disabled
                    Feliz.prop.onChange (fun (_: bool) -> onToggle ())
                ]
                Feliz.Html.span [ Feliz.prop.text label ]
            ]
        ])

/// A button sending every message of `msgs` in order.
let private chainButton (id: string) (label: string) (msgs: Msg list) : Node<Msg> =
    Fuaran.button id {
        Defaults.button with
            Label = Nodes.literal label
            Variant = ButtonVariant.Tertiary
            OnClick = Action.Chain(List.map Action.dispatch msgs)
    }

/// The Reset and Save pair of an edit overlay: both inert until there is
/// something to commit, Save showing its busy label while a save is in flight.
let private editActions
    (id: string)
    (msgs: PermissionsAdminMessages)
    (inFlight: bool)
    (dirty: bool)
    (saveLabel: string)
    (onReset: Msg)
    (onSave: Msg)
    =
    Nodes.Layout.row id [
        Nodes.Actions.button
            (Nodes.Ids.child id "reset")
            ButtonVariant.Secondary
            msgs.Reset
            (not dirty || inFlight)
            onReset
        Nodes.Actions.button
            (Nodes.Ids.child id "save")
            ButtonVariant.Primary
            (if inFlight then msgs.Saving else saveLabel)
            (inFlight || not dirty)
            onSave
    ]

/// A tab's heading, its help and, beside them, its actions.
let private tabHeader (id: string) (heading: string) (help: Node<Msg> list) (actions: Node<Msg> list) =
    Nodes.Layout.row id [
        Nodes.Layout.column (Nodes.Ids.child id "about") [
            Nodes.heading (Nodes.Ids.child id "title") 2 heading
            yield! help
        ]
        yield! actions
    ]

// ─── The permission matrix ───────────────────────────────────────────

/// The matrix's columns: the module, then one per level.
let private matrixColumns (msgs: PermissionsAdminMessages) : Nodes.Table.Column list =
    Nodes.Table.column "module" msgs.ColumnModule 3
    :: [
        for level in allPermLevels -> Nodes.Table.column (string level) (permLabel msgs level) 2
    ]

/// The module × level matrix: a row of column toggles, then a row per module
/// with a checkbox per level. `perms` reads a module's levels as shown,
/// `cell` is a cell's message and `column` a column toggle's messages.
let private matrix
    (id: string)
    (msgs: PermissionsAdminMessages)
    (managed: string list)
    (inFlight: bool)
    (dispatch: Msg -> unit)
    (perms: string -> ModulePermission list)
    (cell: string -> ModulePermission -> Msg)
    (column: ModulePermission -> bool -> Msg list)
    : Node<Msg> =
    if List.isEmpty managed then
        Nodes.text (Nodes.Ids.child id "none") msgs.NoManagedModules
    else
        let columns = matrixColumns msgs
        let togglesId = Nodes.Ids.child id "toggles"

        let toggles =
            Nodes.Table.row togglesId columns [
                Nodes.Table.cell (Nodes.Ids.child togglesId "module") []
                for level in allPermLevels do
                    let on = managed |> List.forall (fun m -> perms m |> List.contains level)
                    let cellId = Nodes.Ids.keyed togglesId (string level)

                    Nodes.Table.cell cellId [
                        chainButton
                            (Nodes.Ids.child cellId "toggle")
                            (if on then msgs.ClearAll else msgs.SelectAll)
                            (column level (not on))
                    ]
            ]

        let moduleRow (moduleName: string) =
            let rowId = Nodes.Ids.keyed id moduleName
            let granted = perms moduleName

            Nodes.Table.row rowId columns [
                Nodes.Table.cell (Nodes.Ids.child rowId "module") [
                    Nodes.text (Nodes.Ids.child rowId "name") moduleName
                ]
                for level in allPermLevels do
                    let cellId = Nodes.Ids.keyed rowId (string level)

                    Nodes.Table.cell cellId [
                        checkbox
                            (Nodes.Ids.child cellId "granted")
                            (permLabel msgs level)
                            (List.contains level granted)
                            inFlight
                            (fun () -> dispatch (cell moduleName level))
                    ]
            ]

        Nodes.Table.table id msgs.ColumnModule columns (toggles :: List.map moduleRow managed)

// ─── Team defaults ───────────────────────────────────────────────────

type private DefaultsContext = {
    Msgs: PermissionsAdminMessages
    Managed: string list
    Edits: Map<string, ModulePermission list>
    InFlight: bool
    Dispatch: Msg -> unit
}

/// The team-defaults matrix, kept on the overlay it shows.
let private defaultsMatrix =
    One(
        (fun (a: DefaultsContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Managed b.Managed
            && sameObject a.Edits b.Edits
            && a.InFlight = b.InFlight
            && sameObject a.Dispatch b.Dispatch),
        fun context ->
            let permsOf m =
                Map.tryFind m context.Edits |> Option.defaultValue []

            matrix
                "defaults-matrix"
                context.Msgs
                context.Managed
                context.InFlight
                context.Dispatch
                permsOf
                (fun m level -> ToggleDefault(m, level))
                (fun level add ->
                    // One toggle per module whose cell is not yet as asked.
                    [
                        for m in context.Managed do
                            if add <> List.contains level (permsOf m) then
                                ToggleDefault(m, level)
                    ])
    )

let private defaultsTab (msgs: PermissionsAdminMessages) (model: Model) (snap: TeamView) (dispatch: Msg -> unit) =
    let edits = model.EditingDefaults |> Option.defaultValue snap.Permissions.Defaults

    Nodes.Layout.column "defaults" [
        tabHeader "defaults-header" msgs.TeamDefaultsHeading [ Nodes.text "defaults-help" msgs.TeamDefaultsSubheading ] [
            editActions
                "defaults-actions"
                msgs
                model.SaveInFlight
                (edits <> snap.Permissions.Defaults)
                msgs.SaveDefaultsLabel
                ResetDefaults
                SaveDefaults
        ]
        defaultsMatrix.Node {
            Msgs = msgs
            Managed = snap.Managed
            Edits = edits
            InFlight = model.SaveInFlight
            Dispatch = dispatch
        }
    ]

// ─── Members ─────────────────────────────────────────────────────────

/// A member's display label: the signed-in caller's display name over the raw
/// user id; for any other member, the user id.
let private memberLabel (self: Caller) (userId: string) =
    if self.UserId = userId then
        self.DisplayName |> Option.defaultValue userId
    else
        userId

type private MembersContext = {
    Msgs: PermissionsAdminMessages
    Self: Caller
    Selected: string option
    Permissions: TeamPermissions
}

/// The member list: each member a button that opens their overrides, with
/// their override badge, the caller's own email and the member's role.
let private memberRows =
    Rows(
        (fun (a: MembersContext) b ->
            sameObject a.Msgs b.Msgs
            && a.Self = b.Self
            && a.Selected = b.Selected
            && sameObject a.Permissions b.Permissions),
        fun context (m: TeamMembership) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "member" m.UserId

            Nodes.Layout.column id [
                Nodes.Actions.button
                    (Nodes.Ids.child id "select")
                    (if context.Selected = Some m.UserId then
                         ButtonVariant.Primary
                     else
                         ButtonVariant.Tertiary)
                    (memberLabel context.Self m.UserId)
                    false
                    (SelectMember m.UserId)
                if hasOverride context.Permissions m.UserId then
                    Fuaran.badge (Nodes.Ids.child id "override") {
                        Label = Nodes.literal msgs.OverrideBadge
                        Variant = BadgeVariant.Brand
                    }
                if context.Self.UserId = m.UserId then
                    match context.Self.Email with
                    | Some email -> Nodes.text (Nodes.Ids.child id "email") email
                    | None -> ()
                Nodes.text (Nodes.Ids.child id "role") (string m.Role)
            ]
    )

type private OverridesContext = {
    Msgs: PermissionsAdminMessages
    Managed: string list
    Snapshot: TeamView
    Editing: Map<string * string, ModulePermission list>
    InFlight: bool
    UserId: string
    Dispatch: Msg -> unit
}

/// One member's override matrix, kept on the overlay it shows.
let private overridesMatrix =
    One(
        (fun (a: OverridesContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Snapshot b.Snapshot
            && sameObject a.Editing b.Editing
            && a.InFlight = b.InFlight
            && a.UserId = b.UserId
            && sameObject a.Dispatch b.Dispatch),
        fun context ->
            let userId = context.UserId

            matrix
                "overrides-matrix"
                context.Msgs
                context.Snapshot.Managed
                context.InFlight
                context.Dispatch
                (effectiveMemberPerms context.Snapshot context.Editing userId)
                (fun m level -> ToggleMemberPermission(userId, m, level))
                (fun level add -> [ ToggleMemberColumn(userId, level, add) ])
    )

let private overridesEditor
    (msgs: PermissionsAdminMessages)
    (self: Caller)
    (model: Model)
    (snap: TeamView)
    (userId: string)
    (dispatch: Msg -> unit)
    : Node<Msg> =
    let overrideKeys =
        match Map.tryFind userId snap.Permissions.Members with
        | Some modMap -> Map.toList modMap |> List.map fst |> List.sort
        | None -> []

    Nodes.Layout.column "overrides" [
        tabHeader "overrides-header" (msgs.OverridesHeading(memberLabel self userId)) [
            Nodes.text "overrides-help" msgs.OverridesHelp
            if not (List.isEmpty overrideKeys) then
                Nodes.text "overrides-active" (msgs.ActiveOverridesOn(String.concat ", " overrideKeys))
        ] [
            // Edits stay local until Update is pressed.
            editActions
                "overrides-actions"
                msgs
                model.SaveInFlight
                (model.EditingMembers |> Map.exists (fun (uid, _) _ -> uid = userId))
                msgs.Update
                (ResetMembers userId)
                (SaveMembers userId)
        ]
        overridesMatrix.Node {
            Msgs = msgs
            Managed = snap.Managed
            Snapshot = snap
            Editing = model.EditingMembers
            InFlight = model.SaveInFlight
            UserId = userId
            Dispatch = dispatch
        }
    ]

let private membersTab
    (msgs: PermissionsAdminMessages)
    (self: Caller)
    (model: Model)
    (snap: TeamView)
    (dispatch: Msg -> unit)
    =
    if List.isEmpty snap.Members then
        Nodes.text "members-none" msgs.NoMembersYet
    else
        Fuaran.box "members" {
            Defaults.box with
                Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) minmax(0, 3fr)", Some 24)
                Children = [
                    Nodes.Layout.column "members-list" [
                        Nodes.text "members-list-label" msgs.MembersListLabel
                        yield!
                            memberRows.List
                                {
                                    Msgs = msgs
                                    Self = self
                                    Selected = model.SelectedMember
                                    Permissions = snap.Permissions
                                }
                                snap.Members
                    ]
                    match model.SelectedMember with
                    | None -> Nodes.text "members-select" msgs.SelectMemberPrompt
                    | Some userId -> overridesEditor msgs self model snap userId dispatch
                ]
        }

// ─── Modules ─────────────────────────────────────────────────────────

type private ModulesContext = {
    Msgs: PermissionsAdminMessages
    Managed: string list
    Permissions: TeamPermissions
}

/// The modules table's columns.
let private moduleColumns (msgs: PermissionsAdminMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "module" msgs.ColumnModule 2
    Nodes.Table.column "exposure" msgs.ColumnExposure 4
    Nodes.Table.column "default" msgs.ColumnTeamDefault 2
    Nodes.Table.column "overrides" msgs.ColumnOverrides 1
]

/// Each module with its exposure (a choice of three, the current one inert),
/// its team-default levels and how many members override it.
let private modulesTable =
    One(
        (fun (a: ModulesContext) b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Managed b.Managed
            && sameObject a.Permissions b.Permissions),
        fun context ->
            let msgs = context.Msgs
            let columns = moduleColumns msgs

            let row (moduleName: string) =
                let id = Nodes.Ids.keyed "module" moduleName
                let current = exposureOf context.Permissions moduleName

                let option (part: string) (state: ModuleExposure) (label: string) =
                    Nodes.Actions.button
                        (Nodes.Ids.child id part)
                        (if current = state then
                             ButtonVariant.Primary
                         else
                             ButtonVariant.Secondary)
                        label
                        (current = state)
                        (SetExposure(moduleName, current, state))

                let defaults =
                    Map.tryFind moduleName context.Permissions.Defaults |> Option.defaultValue []

                Nodes.Table.row id columns [
                    Nodes.Table.cell (Nodes.Ids.child id "name-cell") [
                        Nodes.text (Nodes.Ids.child id "name") moduleName
                    ]
                    Nodes.Table.cell (Nodes.Ids.child id "exposure-cell") [
                        Nodes.Layout.row (Nodes.Ids.child id "exposure") [
                            option "available" ModuleExposure.Available msgs.ExposureAvailable
                            option "hidden" ModuleExposure.Hidden msgs.ExposureHidden
                            option "unavailable" ModuleExposure.Unavailable msgs.ExposureUnavailable
                        ]
                    ]
                    Nodes.Table.cell (Nodes.Ids.child id "default-cell") [
                        Nodes.text
                            (Nodes.Ids.child id "default")
                            (if List.isEmpty defaults then
                                 msgs.NoDefaultPermission
                             else
                                 // The middot joins labels already localized.
                                 defaults |> List.map (permLabel msgs) |> String.concat " · ")
                    ]
                    Nodes.Table.cell (Nodes.Ids.child id "overrides-cell") [
                        Nodes.text
                            (Nodes.Ids.child id "overrides")
                            (string (memberOverrideCount context.Permissions moduleName))
                    ]
                ]

            Nodes.Table.table "modules-table" msgs.ModulesHeading columns (List.map row context.Managed)
    )

let private modulesTab (msgs: PermissionsAdminMessages) (snap: TeamView) : Node<Msg> =
    Nodes.Layout.column "modules" [
        tabHeader "modules-header" msgs.ModulesHeading [ Nodes.text "modules-help" msgs.ModulesSubheading ] []
        if List.isEmpty snap.Managed then
            Nodes.text "modules-none" msgs.NoManagedModules
        else
            modulesTable.Node {
                Msgs = msgs
                Managed = snap.Managed
                Permissions = snap.Permissions
            }
    ]

// ─── The page ────────────────────────────────────────────────────────

let private tabs = [ TeamDefaultsTab; MembersTab; ModulesTab ]

/// The page: the three tabs, the banner and the active tab's panel. `caller` is
/// read only while the Members tab, which names the signed-in caller by display
/// name, is drawn.
/// `dispatch` reaches the matrices' checkboxes only; every other node sends its
/// message through the tree.
let tree (msgs: PermissionsAdminMessages) (caller: unit -> Caller) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    let content (tab: Tab) =
        match model.Snapshot with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "permissions-loading" msgs.Loading
        | Remote.Failed error ->
            Nodes.Layout.column "permissions-failed" [
                problem "permissions-failed-error" error
                Nodes.Actions.secondary "permissions-retry" msgs.Retry RefreshSnapshot
            ]
        | Remote.Loaded snap ->
            match tab with
            | TeamDefaultsTab -> defaultsTab msgs model snap dispatch
            | MembersTab -> membersTab msgs (caller ()) model snap dispatch
            | ModulesTab -> modulesTab msgs snap

    let panel (tab: Tab) =
        if tab <> model.ActiveTab then
            Nodes.Layout.column (sprintf "tab-%A" tab) []
        else
            Nodes.Layout.column (sprintf "tab-%A" tab) [ yield! banner msgs model.Banner; content tab ]

    Fuaran.tabs "permissions-tabs" {
        Defaults.tabs with
            ActiveIndex = Binding.Static(Some(List.findIndex ((=) model.ActiveTab) tabs))
            OnSelect = Some(fun i -> Action.dispatch (SwitchTab tabs[i]))
            TabHeaders =
                Some [
                    for label in [ msgs.TabTeamDefaults; msgs.TabMembers; msgs.TabModules ] ->
                        {
                            Label = Nodes.literal label
                            Icon = None
                            Disabled = None
                        }
                ]
            Children = List.map panel tabs
    }

/// The deferred component `PermissionsAdminUI`'s view mounts.
let Surface (props: PermissionsAdminUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps
            host
            props.Dispatch
            (tree props.Messages PermissionsAdminUI.caller props.Model props.Dispatch)
    )