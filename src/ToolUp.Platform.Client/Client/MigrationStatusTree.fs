// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The data-migration admin as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `MigrationStatusUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// A data type's row shows the type and its own entries in the model (its
/// recorded status, its trigger in flight, whether its failures are open), so
/// a row is kept on those (`KeepScoped.RowInputs`): a trigger's answer builds
/// that type's row alone. Row ids come from the data type's id and every row
/// message carries it. A status's failures arrive whole with it and are drawn
/// with it.
module internal MigrationStatusTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open MigrationStatusUI
open KeepScoped

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

let private stateBadge (id: string) (msgs: MigrationStatusMessages) (state: MigrationRunState) =
    match state with
    | MigrationIdle -> badge id BadgeVariant.Neutral msgs.NotYetRun
    | MigrationInProgress -> badge id BadgeVariant.Brand msgs.InProgressLabel
    | MigrationComplete -> badge id BadgeVariant.Success msgs.UpToDate
    | MigrationCompleteWithFailures -> badge id BadgeVariant.Warning msgs.CompletedWithFailures
    | MigrationChainBlocked _ -> badge id BadgeVariant.Critical msgs.Blocked

/// The progress sentence: which version pair, and how far through the scope.
let private progressText
    (msgs: MigrationStatusMessages)
    (info: MigrationDataTypeInfo)
    (status: MigrationStatus option)
    =
    match status with
    | None -> msgs.NoPassRecorded
    | Some s ->
        let done' = s.MigratedObjects + s.AlreadyCurrentObjects

        match s.State with
        | MigrationChainBlocked reason -> reason
        | MigrationIdle -> msgs.NoPassRecorded
        | MigrationInProgress -> msgs.InProgressText info.DisplayName s.TargetVersion done' s.TotalObjects
        | MigrationComplete -> msgs.CompleteText s.TotalObjects s.TargetVersion
        | MigrationCompleteWithFailures ->
            msgs.CompleteWithFailuresText done' s.TotalObjects s.TargetVersion s.FailedObjects

// ─── A data type's row ───────────────────────────────────────────────

/// What one data type's row reads: the type and its own entries in the model.
type private TypeRow = {
    Msgs: MigrationStatusMessages
    Info: MigrationDataTypeInfo
    Status: MigrationStatus option
    Triggering: bool
    Expanded: bool
}

let private inputs =
    RowInputs(
        (fun (row: TypeRow) -> row.Info.DataTypeId),
        fun a b ->
            sameObject a.Msgs b.Msgs
            && sameObject a.Info b.Info
            && (match a.Status, b.Status with
                | Some x, Some y -> sameObject x y
                | None, None -> true
                | _ -> false)
            && a.Triggering = b.Triggering
            && a.Expanded = b.Expanded
    )

let private columns (msgs: MigrationStatusMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "data-type" msgs.ColumnDataType 3
    Nodes.Table.column "declared" msgs.ColumnDeclared 2
    Nodes.Table.column "progress" msgs.ColumnProgress 5
    Nodes.Table.column "state" msgs.ColumnState 2
    Nodes.Table.column "actions" msgs.ColumnActions 3
]

let private failures (id: string) (msgs: MigrationStatusMessages) (status: MigrationStatus) : Node<Msg> =
    Nodes.Layout.column id [
        if status.Failures.IsEmpty then
            Nodes.text (Nodes.Ids.child id "none") msgs.NoFailuresRecorded
        else
            Nodes.text (Nodes.Ids.child id "summary") (msgs.FailuresSummary(List.length status.Failures))

            for failure in status.Failures do
                Nodes.text
                    (Nodes.Ids.keyed (Nodes.Ids.child id "failure") (sprintf "%s-%d" failure.ObjectId failure.AtVersion))
                    (msgs.FailureLine failure.ObjectId failure.AtVersion failure.Error)
    ]

let private typeRows =
    Keep.create (fun (row: TypeRow) ->
        let msgs = row.Msgs
        let info = row.Info
        let id = Nodes.Ids.keyed "migration-type" info.DataTypeId

        let cell part children =
            Nodes.Table.cell (Nodes.Ids.child id part) children

        let failureCount = row.Status |> Option.map _.FailedObjects |> Option.defaultValue 0

        Nodes.Table.row id (columns msgs) [
            cell "data-type" [
                Nodes.text (Nodes.Ids.child id "name") info.DisplayName
                Nodes.text (Nodes.Ids.child id "id") info.DataTypeId
            ]
            cell "declared" [
                Nodes.text (Nodes.Ids.child id "declared-text") (msgs.DeclaredVersion info.CurrentVersion)
            ]
            cell "progress" [
                Nodes.text (Nodes.Ids.child id "progress-text") (progressText msgs info row.Status)
                match row.Status with
                | Some status when row.Expanded -> failures (Nodes.Ids.child id "failures") msgs status
                | _ -> ()
            ]
            cell "state" [
                stateBadge
                    (Nodes.Ids.child id "state-badge")
                    msgs
                    (row.Status |> Option.map _.State |> Option.defaultValue MigrationIdle)
            ]
            cell "actions" [
                match info.ChainProblem with
                | Some problem ->
                    Nodes.text (Nodes.Ids.child id "chain-incomplete") msgs.ChainIncomplete
                    |> Node.withTooltip (Nodes.literal problem)
                | None ->
                    Nodes.Actions.button
                        (Nodes.Ids.child id "migrate")
                        ButtonVariant.Secondary
                        (if row.Triggering then msgs.Migrating else msgs.MigrateNow)
                        row.Triggering
                        (TriggerClicked info.DataTypeId)
                if failureCount > 0 then
                    Nodes.Actions.button
                        (Nodes.Ids.child id "failures-toggle")
                        ButtonVariant.Tertiary
                        (if row.Expanded then
                             msgs.HideFailures
                         else
                             msgs.FailuresButton failureCount)
                        false
                        (ToggleFailures info.DataTypeId)
                    |> Node.withExtraAttribute "aria-expanded" (if row.Expanded then "true" else "false")
            ]
        ])

// ─── The page ────────────────────────────────────────────────────────

/// The page: the heading and its refresh, the notice, then the data types or
/// the state they are in. Every node sends its message through the tree.
let tree (msgs: MigrationStatusMessages) (model: Model) : Node<Msg> =
    let loading = Remote.isLoading model.DataTypes

    let failed (text: string) =
        Nodes.Actions.banner "migration-banner" ToneVariant.Critical text msgs.Dismiss DismissError

    Nodes.Layout.column "migrations" [
        Fuaran.box "migration-header" {
            Defaults.box with
                Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
                Children = [
                    Nodes.Layout.column "migration-titles" [
                        Nodes.heading "migration-heading" 2 msgs.Heading
                        Nodes.text "migration-subheading" msgs.Subheading
                    ]
                    Nodes.Actions.button
                        "migration-refresh"
                        ButtonVariant.Secondary
                        (if loading then msgs.Refreshing else msgs.Refresh)
                        loading
                        Refresh
                ]
        }
        match model.LastError with
        | Some(Notice.Problem text) -> failed text
        | Some(Notice.Info text) ->
            Nodes.Actions.banner "migration-banner" ToneVariant.Info text msgs.Dismiss DismissError
        | None -> ()
        match model.DataTypes with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "migration-loading" msgs.LoadingDataTypes
        | Remote.Failed message -> failed message
        | Remote.Loaded [] -> Nodes.text "migration-none" msgs.NoDataTypes
        | Remote.Loaded dataTypes ->
            let rows =
                dataTypes
                |> List.map (fun info -> {
                    Msgs = msgs
                    Info = info
                    Status = model.Statuses.TryFind info.DataTypeId
                    Triggering = model.Triggering.Contains info.DataTypeId
                    Expanded = model.ExpandedFailures = Some info.DataTypeId
                })
                |> inputs.Of

            Nodes.Table.table "migration-table" msgs.Heading (columns msgs) (Keep.list typeRows rows)
    ]

/// The deferred component `MigrationStatusUI`'s view mounts.
let Surface (props: MigrationStatusUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model))