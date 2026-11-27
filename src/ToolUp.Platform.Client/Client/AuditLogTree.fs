// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The audit-trail viewer as a Fuaran-UI tree, built and drawn in a file of its
/// own, which `AuditLogUI`'s view mounts through `TreeSurface.deferred` (the
/// shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// The event rows are kept per event, every row id from the event's id and
/// every row message carrying it; an open row is drawn by a second kept table,
/// so opening one row builds that row alone. The time window and the event
/// type are filter chips; the actor is an island (`TreeIslands`), holding what
/// is typed until the field is left.
module internal AuditLogTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open AuditLogUI
open KeepScoped

// ─── The filter bar ──────────────────────────────────────────────────

/// The time window and the event type, as filter chips: each change sends its
/// message, and no filter store is written.
let private filters (msgs: AuditLogMessages) (model: Model) : Node<Msg> =
    let date (name: string) (label: string) (value: string) (msg: string -> Msg) : FilterSpec<Msg> = {
        Kind =
            FormFieldKind.dateTimeControl (
                Binding.Static(Some value),
                DateTimeVariant.Date,
                onChange = (msg >> Action.dispatch)
            )
        Label = Nodes.literal label
        Name = name
    }

    let option (value: string) (label: string) : SelectOption = {
        SelectOption.Value = value
        SelectOption.Label = Nodes.literal label
    }

    let options =
        option "" msgs.AllEventTypes :: List.map (fun t -> option t t) model.EventTypes

    Fuaran.filters "audit-filters" [
        date "audit-from" msgs.FromLabel model.Draft.From SetFrom
        date "audit-to" msgs.ToLabel model.Draft.To SetTo
        {
            Kind =
                FormFieldKind.choiceControl (
                    Binding.Static(Some options),
                    Binding.Static(Some model.Draft.EventType),
                    onChange = (fun chosen -> Action.dispatch (SetEventType(defaultArg chosen "")))
                )
            Label = Nodes.literal msgs.EventTypeLabel
            Name = "audit-event-type"
        }
    ]

let private filterBar (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.row "audit-filter-bar" [
        filters msgs model
        TreeIslands.textField
            "audit-actor"
            msgs.ActorLabel
            msgs.ActorPlaceholder
            model.Draft.Actor
            (SetActor >> dispatch)
        Nodes.Layout.row "audit-filter-actions" [
            Nodes.Actions.primary "audit-apply" msgs.ApplyFilters ApplyFilters
            Nodes.Actions.secondary "audit-clear" msgs.ClearFilters ClearFilters
            Nodes.Actions.secondary "audit-refresh" msgs.Refresh Refresh
            Nodes.Actions.button
                "audit-export"
                ButtonVariant.Primary
                (if model.Exporting then msgs.Exporting else msgs.ExportCsv)
                (model.Exporting || List.isEmpty model.Events)
                ExportCsv
        ]
    ]

// ─── The event rows ──────────────────────────────────────────────────

let private columns (msgs: AuditLogMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "time" msgs.ColumnTime 2
    Nodes.Table.column "type" msgs.ColumnEventType 2
    Nodes.Table.column "actor" msgs.ColumnActor 2
    Nodes.Table.column "summary" msgs.ColumnSummary 5
]

/// One event, its detail open or closed. The detail toggle sits in the
/// summary cell: the catalog names four columns, and the toggle is the
/// summary's.
let private eventRow (expanded: bool) (msgs: AuditLogMessages) (event: AuditEventView) : Node<Msg> =
    let id = Nodes.Ids.keyed "audit-event" (string event.Id)

    Nodes.Table.row id (columns msgs) [
        Nodes.Table.cell (Nodes.Ids.child id "time") [
            Nodes.text (Nodes.Ids.child id "time-text") (formatTimestamp event.OccurredAt)
        ]
        Nodes.Table.cell (Nodes.Ids.child id "type") [ Nodes.text (Nodes.Ids.child id "type-text") event.EventType ]
        Nodes.Table.cell (Nodes.Ids.child id "actor") [
            Nodes.text (Nodes.Ids.child id "actor-text") (event.Actor |> Option.defaultValue msgs.UnattributedActor)
        ]
        Nodes.Table.cell (Nodes.Ids.child id "summary") [
            Fuaran.box (Nodes.Ids.child id "summary-line") {
                Defaults.box with
                    Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 12)
                    Children = [
                        Nodes.text (Nodes.Ids.child id "summary-text") event.Summary
                        Nodes.Actions.button
                            (Nodes.Ids.child id "toggle")
                            ButtonVariant.Tertiary
                            (if expanded then msgs.HideDetail else msgs.ShowDetail)
                            false
                            (ToggleDetail event.Id)
                    ]
            }
            if expanded then
                Nodes.Layout.column (Nodes.Ids.child id "detail") [
                    Nodes.text (Nodes.Ids.child id "payload-heading") msgs.PayloadHeading
                    Fuaran.codeBlock (Nodes.Ids.child id "payload") "json" (prettyPayload event.Payload)
                ]
        ]
    ]

let private closedRows =
    Rows((fun (a: AuditLogMessages) b -> sameObject a b), eventRow false)

let private openRows =
    Rows((fun (a: AuditLogMessages) b -> sameObject a b), eventRow true)

/// The two empty states are different states: nothing recorded sends the
/// operator to the server, nothing matching to the filters here.
let private empty (msgs: AuditLogMessages) (model: Model) : Node<Msg> =
    let filtered = model.Applied <> emptyFilters || not (List.isEmpty model.EventTypes)
    Nodes.text "audit-empty" (if filtered then msgs.NoMatches else msgs.NoEvents)

let private events (msgs: AuditLogMessages) (model: Model) : Node<Msg> =
    if List.isEmpty model.Events then
        empty msgs model
    else
        let rows =
            model.Events
            |> List.collect (fun event ->
                if Set.contains event.Id model.Expanded then
                    openRows.List msgs [ event ]
                else
                    closedRows.List msgs [ event ])

        Nodes.Layout.column "audit-events" [
            Nodes.Table.table "audit-table" msgs.Heading (columns msgs) rows
            Nodes.Layout.row "audit-paging" [
                Nodes.text "audit-showing" (msgs.ShowingCount (List.length model.Events) model.MatchedCount)
                match model.NextCursor with
                | None -> ()
                | Some _ ->
                    Nodes.Actions.button
                        "audit-load-more"
                        ButtonVariant.Secondary
                        (if model.LoadingMore then msgs.Loading else msgs.LoadMore)
                        model.LoadingMore
                        LoadMore
            ]
        ]

// ─── The page ────────────────────────────────────────────────────────

/// The page: the heading, the filter bar, then the events or the state they
/// are in. `dispatch` reaches the actor island only; every node sends its
/// message through the tree.
let tree (msgs: AuditLogMessages) (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
    Nodes.Layout.column "audit-log" [
        Nodes.heading "audit-heading" 2 msgs.Heading
        Nodes.text "audit-subheading" msgs.Subheading
        filterBar msgs model dispatch
        match model.Status with
        | Remote.Failed error ->
            Fuaran.callout "audit-failed" {
                Defaults.callout with
                    Body = Nodes.literal error
                    Tone = ToneVariant.Critical
            }
        | Remote.NotAsked
        | Remote.Loading -> Nodes.text "audit-loading" msgs.Loading
        | Remote.Reloading()
        | Remote.Loaded() -> events msgs model
    ]

/// The deferred component `AuditLogUI`'s view mounts.
let Surface (props: AuditLogUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model props.Dispatch)
    )