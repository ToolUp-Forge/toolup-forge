// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The usage dashboard as a Fuaran-UI tree, built and drawn in a file of its
/// own, which `UsageDashboard`'s view mounts through `TreeSurface.deferred`
/// (the shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// The aggregate's rows arrive whole with each read and are kept per record
/// under the catalog section (`KeepScoped.Rows`), their ids from the bucket.
/// A read always clears first (`SetGrouping` and `Refresh` set the aggregate
/// loading), so there is nothing to keep across one; an export in flight
/// leaves the rows the objects they were.
module internal UsageDashboardTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.Platform.Usage
open UsageDashboard
open KeepScoped

/// Each grouping, the value its option carries and its label.
let private groupings (msgs: UsageGroupingMessages) = [
    ByDay, "day", msgs.ByDay
    ByMonth, "month", msgs.ByMonth
    ByResourceKind, "resource-kind", msgs.ByResourceKind
    ByUser, "user", msgs.ByUser
]

let private formatQuantity (q: decimal) : string = if q = 0M then "0" else q.ToString "N2"

let private columns (msgs: UsageDashboardMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "bucket" msgs.ColumnBucket 3
    Nodes.Table.column "quantity" msgs.ColumnQuantity 1
]

let private aggregateRows =
    Rows(
        (fun (a: UsageDashboardMessages) b -> sameObject a b),
        fun msgs (row: UsageAggregateRow) ->
            let id = Nodes.Ids.keyed "usage-row" row.Bucket

            Nodes.Table.row id (columns msgs) [
                Nodes.Table.cell (Nodes.Ids.child id "bucket") [
                    Nodes.text (Nodes.Ids.child id "bucket-text") row.Bucket
                ]
                Nodes.Table.cell (Nodes.Ids.child id "quantity") [
                    Nodes.text (Nodes.Ids.child id "quantity-text") (formatQuantity row.Quantity)
                ]
            ]
    )

/// The controls: the grouping, the reload and the export, inert while its CSV
/// is being built.
let private controls (msgs: UsageDashboardMessages) (model: Model) : Node<Msg> =
    let options = groupings msgs.Grouping

    let chosen =
        options
        |> List.tryPick (fun (g, value, _) -> if g = model.Grouping then Some value else None)
        |> Option.defaultValue ""

    let groupingOf (value: string) =
        options
        |> List.tryPick (fun (g, v, _) -> if v = value then Some g else None)
        |> Option.defaultValue model.Grouping

    Nodes.Layout.row "usage-controls" [
        Nodes.Forms.dropdown
            "usage-grouping"
            msgs.GroupByLabel
            [ for _, value, label in options -> value, label ]
            chosen
            (groupingOf >> SetGrouping)
        Nodes.Actions.secondary "usage-refresh" msgs.Refresh Refresh
        Nodes.Actions.button
            "usage-export"
            ButtonVariant.Primary
            (if model.Exporting then msgs.Exporting else msgs.ExportCsv)
            model.Exporting
            ExportCsv
    ]

/// The page: the heading, the controls, then the aggregate or the state it is
/// in. Every node sends its message through the tree.
let tree (msgs: UsageDashboardMessages) (model: Model) : Node<Msg> =
    Nodes.Layout.column "usage" [
        Nodes.Layout.column "usage-titles" [
            Nodes.heading "usage-heading" 2 msgs.Heading
            Nodes.text "usage-subheading" msgs.Subheading
        ]
        controls msgs model
        match model.Aggregate with
        | Remote.NotAsked -> Nodes.text "usage-not-asked" msgs.ClickRefresh
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "usage-loading" msgs.Loading
        | Remote.Loaded [] -> Nodes.text "usage-none" msgs.NoRecords
        | Remote.Loaded rows ->
            Nodes.Table.table "usage-table" msgs.Heading (columns msgs) (aggregateRows.List msgs rows)
        | Remote.Failed message ->
            Fuaran.callout "usage-failed" {
                Defaults.callout with
                    Body = Nodes.literal message
                    Tone = ToneVariant.Critical
            }
    ]

/// The deferred component `UsageDashboard`'s view mounts.
let Surface (props: UsageDashboard.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model))