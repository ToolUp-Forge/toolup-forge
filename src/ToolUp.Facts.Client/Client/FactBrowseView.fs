// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Facts.Client.FactBrowseView

// ─── Fact browse — pages and module ──────────────────────────────────
//
// Three declared pages, summary first:
//
//   /tables — one row per declared table: owning module, metrics, level,
//             row count, last refresh, freshness, latest run; and the
//             table select. A Fuaran-UI tree.
//   /table  — the selected table's declaration, population summary and
//             run history (paged), with each committed run's change
//             summary and, on request, its largest movers; and the table
//             select. A Fuaran-UI tree, its run history a React island.
//   /rows   — the drill-down: rank by a metric column, top or bottom,
//             under a subject prefix, one page at a time — answered from the
//             table's held view slice, or page by page from the
//             server when the slice was refused; and the
//             opened fact with its provenance chain.
//
// The two trees are lowered through the shell's tree seam
// (`TreeLowering.lower`), which loads the renderer on demand, so the
// module's registration, its id and its topic subscriptions are those of
// any other module.
//
// Every page has a considered empty state — no tables declared, no table
// chosen, a table with no runs yet, a ranking with nothing to show — and
// the drill-down's empty state says where bulk extraction lives, since
// it is deliberately not offered here.
//
// Two other companions link in through `FactBrowseLinks` (Platform.Core):
// the module subscribes to the open-table / open-metric / open-fact
// topics on the cross-module event bus, so neither the knowledge base
// client nor the conversation panel imports this package.

open System
open Feliz
open Fable.SimpleJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Facts.Client.FactBrowseModel

module Nodes = Toolup.UIToolkit.Nodes

// ─── Shared view helpers ─────────────────────────────────────────────

let private headerCell (label: string) =
    Html.th [
        prop.className "px-3 py-2 text-left text-xs font-semibold text-gray-700 uppercase tracking-wider"
        prop.text label
    ]

let private cell (value: string) =
    Html.td [ prop.className "px-3 py-2 text-sm text-gray-700"; prop.text value ]

let private monoCell (value: string) =
    Html.td [
        prop.className "px-3 py-2 text-sm text-gray-700 font-mono break-all"
        prop.text value
    ]

let private table (headers: string list) (rows: ReactElement list) =
    Html.div [
        prop.className "overflow-x-auto"
        prop.children [
            Html.table [
                prop.className "min-w-full divide-y divide-gray-200 border border-gray-200 rounded"
                prop.children [
                    Html.thead [
                        prop.className "bg-gray-50"
                        prop.children [ Html.tr (headers |> List.map headerCell) ]
                    ]
                    Html.tbody [ prop.className "bg-white divide-y divide-gray-200"; prop.children rows ]
                ]
            ]
        ]
    ]

let private emptyState (title: string) (detail: string) =
    Html.div [
        prop.className "rounded border border-dashed border-gray-300 bg-gray-50 p-6 text-center"
        prop.children [
            Html.p [ prop.className "text-sm font-medium text-gray-700"; prop.text title ]
            Html.p [ prop.className "mt-1 text-sm text-gray-500"; prop.text detail ]
        ]
    ]

let private heading (text: string) =
    Html.h2 [ prop.className "text-lg font-semibold text-gray-900 mb-3"; prop.text text ]

let private sectionHeading (text: string) =
    Html.h3 [
        prop.className "text-sm font-semibold text-gray-800 mt-5 mb-2"
        prop.text text
    ]

let private failed (message: string) =
    Html.p [
        prop.className "text-sm text-red-700 bg-red-50 border border-red-100 rounded p-3"
        prop.text message
    ]

let private loadingNote () =
    Html.p [ prop.className "text-sm text-gray-500 italic"; prop.text "Loading…" ]

/// A re-read draws what it replaces until its answer arrives. Swapping a page of
/// rows for one "Loading…" line collapses the page, and the browser resets the
/// reader's scroll to fit. `keeps` says whether the last answer still belongs on
/// screen (it is the same table); the flag says the answer drawn is on its way out.
let private useHeld (keeps: 'T -> bool) (load: Load<'T>) : Load<'T> * bool =
    let held = React.useRef (None: 'T option)

    match load with
    | Loaded value -> held.current <- Some value
    | Loading -> ()
    | NotAsked
    | LoadFailed _ -> held.current <- None

    match load, held.current with
    | Loading, Some value when keeps value -> Loaded value, true
    | _ -> load, false

/// The class of a container whose answer `useHeld` is holding.
let private heldClass (busy: bool) = if busy then "opacity-60" else ""

let private button (label: string) (enabled: bool) (onClick: unit -> unit) =
    Html.button [
        prop.className
            "px-3 py-1 rounded border border-gray-300 text-sm text-gray-700 hover:bg-gray-100 disabled:opacity-50"
        prop.disabled (not enabled)
        prop.onClick (fun _ -> onClick ())
        prop.text label
    ]

let private linkButton (label: string) (onClick: unit -> unit) =
    Html.button [
        prop.className "text-left text-sm font-medium text-indigo-700 hover:underline"
        prop.onClick (fun _ -> onClick ())
        prop.text label
    ]

let private time (t: DateTime) = t.ToString("yyyy-MM-dd HH:mm")

let private optionalTime (t: DateTime option) =
    t |> Option.map time |> Option.defaultValue "—"

/// A table's standing in words: how fresh it is, and how overdue when stale.
let private freshnessText (freshness: string) (overdueSeconds: int64 option) =
    match freshness, overdueSeconds with
    | "stale", Some s -> sprintf "stale · %dh overdue" (max 1L (s / 3600L))
    | "never-refreshed", _ -> "never refreshed"
    | other, _ -> other

let private withheldNote (count: int) (withheld: FactWithheldCount list) (noun: string) =
    if count = 0 then
        Html.none
    else
        let byPolicy =
            withheld
            |> List.map (fun w -> sprintf "%s: %d" w.PolicyRef w.Count)
            |> String.concat ", "

        Html.p [
            prop.className "text-xs text-amber-800 bg-amber-50 border border-amber-100 rounded p-2 mt-2"
            prop.text (
                sprintf
                    "%d %s withheld from you under disclosure policy (%s). Withheld entries are left out, never shown with a value."
                    count
                    noun
                    byPolicy
            )
        ]

/// Ask the shell to show one of this module's pages.
let private goTo (route: string) =
    NavigationRequest.request (FactBrowseLinks.sidebarId route)

// ─── Run history ─────────────────────────────────────────────────────

let private moversPanel (movers: Load<FactRunMovers>) =
    match movers with
    | NotAsked
    | Loading -> loadingNote ()
    | LoadFailed message -> failed message
    | Loaded m ->
        Html.div [
            prop.className "bg-gray-50 p-3"
            prop.children [
                if List.isEmpty m.Movers && m.WithheldCount = 0 then
                    Html.p [
                        prop.className "text-sm text-gray-500 italic"
                        prop.text "No scalar cell moved in this run."
                    ]
                else
                    table
                        [ "Metric"; "Subject"; "Period"; "Previous"; "Current"; "Change" ]
                        (m.Movers
                         |> List.mapi (fun i mv ->
                             Html.tr [
                                 prop.key i
                                 prop.children [
                                     cell mv.Metric
                                     monoCell mv.Subject
                                     cell mv.Period
                                     cell mv.Previous
                                     cell mv.Current
                                     cell mv.Delta
                                 ]
                             ]))
                withheldNote m.WithheldCount m.Withheld "movers"
            ]
        ]

let private runsTable (model: Model) (page: FactRunPage) (dispatch: Msg -> unit) =
    if page.TotalRuns = 0 then
        emptyState
            "This table has not been refreshed yet."
            "Each refresh appears here as one row, with its validation outcome and what it changed."
    else
        let openRun = model.Movers |> Option.map fst

        let rows =
            page.Runs
            |> List.collect (fun run ->
                let change =
                    match run.Change with
                    | Some c ->
                        sprintf
                            "+%d new · %d changed · %d unchanged · −%d removed"
                            c.New
                            c.Changed
                            c.Unchanged
                            c.Removed
                    | None -> "—"

                let moversCell =
                    match run.Change with
                    | Some c when c.MoverCount > 0 ->
                        Html.td [
                            prop.className "px-3 py-2"
                            prop.children [
                                linkButton
                                    (if openRun = Some run.RunId then
                                         "Hide movers"
                                     else
                                         sprintf "Largest movers (%d)" c.MoverCount)
                                    (fun () -> dispatch (ToggleMovers run.RunId))
                            ]
                        ]
                    | _ -> cell "—"

                [
                    Html.tr [
                        prop.key run.RunId
                        prop.children [
                            cell (time run.OpenedAt)
                            Html.td [
                                prop.className "px-3 py-2 text-sm text-gray-700"
                                prop.title (run.Reason |> Option.defaultValue "")
                                prop.text run.Outcome
                            ]
                            cell (run.RowsWritten |> Option.map string |> Option.defaultValue "—")
                            cell change
                            moversCell
                        ]
                    ]
                    match model.Movers with
                    | Some(runId, movers) when runId = run.RunId ->
                        Html.tr [
                            prop.key (run.RunId + "-movers")
                            prop.children [ Html.td [ prop.colSpan 5; prop.children [ moversPanel movers ] ] ]
                        ]
                    | _ -> Html.none
                ])

        let lastPage = (page.TotalRuns - 1) / max 1 page.PageSize

        Html.div [
            prop.children [
                table [ "Opened"; "Outcome"; "Rows written"; "Change"; "Movers" ] rows
                Html.div [
                    prop.className "flex items-center gap-2 mt-2 text-sm text-gray-600"
                    prop.children [
                        button "Newer" (page.Page > 0) (fun () -> dispatch (RunsPageRequested(page.Page - 1)))
                        Html.span [
                            prop.text (sprintf "Page %d of %d · %d runs" (page.Page + 1) (lastPage + 1) page.TotalRuns)
                        ]
                        button "Older" (page.Page < lastPage) (fun () -> dispatch (RunsPageRequested(page.Page + 1)))
                    ]
                ]
            ]
        ]

/// The run history, holding the page it shows while another page is read.
[<ReactComponent>]
let private RunsSection (tableId: string) (model: Model) (dispatch: Msg -> unit) =
    let runs, busy =
        useHeld (fun (page: FactRunPage) -> page.TableId = tableId) model.Runs

    match runs with
    | NotAsked
    | Loading -> loadingNote ()
    | LoadFailed message -> failed message
    | Loaded page ->
        Html.div [
            prop.ariaBusy busy
            prop.className (heldClass busy)
            prop.children [ runsTable model page dispatch ]
        ]

// ─── The listing and the detail as trees ─────────────────────────────

/// The Tables page and the Table page as Fuaran-UI trees. Each page is a
/// group named for itself, and the table selection is the select named
/// `table`: its options are the listed tables, the selected one is chosen,
/// and a change sends `OpenTable`. What an instruction may set on the browse
/// is therefore read off the tree (`Fuaran.UI.Operability.derive`), not off
/// a declaration kept beside it. The run history keeps its held page, so it
/// stays React inside the detail tree.
module internal Trees =
    open Fuaran.UI
    open Fuaran.UI.Types

    let private literal = Nodes.literal

    /// A page's root: a group named for the page.
    let private page (id: string) (name: string) (children: Node<Msg> list) : Node<Msg> =
        Nodes.Layout.column id children
        |> Node.withAccessibility (
            Some {
                Defaults.Accessibility.empty with
                    Label = Some(Binding.Static(Some name))
            }
        )

    let private note (id: string) (title: string) (detail: string) : Node<Msg> =
        Fuaran.callout id {
            Defaults.callout with
                Tone = ToneVariant.Default
                Heading = Some(literal title)
                Body = literal detail
        }

    let private failure (id: string) (message: string) : Node<Msg> =
        Fuaran.callout id {
            Defaults.callout with
                Tone = ToneVariant.Critical
                Body = literal message
        }

    let private loading (id: string) : Node<Msg> = Nodes.text id "Loading…"

    /// The phrasings the table select answers beyond the `set` and `change`
    /// forms its label already yields.
    let tablePatterns = [ "open {value}"; "open the {value} table"; "show the {value} table" ]

    /// The table selection: one option per listed table, the selected one
    /// chosen when it is listed, and `OpenTable` on a change.
    let tableSelect (tables: FactTableListing list) (selected: string option) : Node<Msg> =
        let options: SelectOption list =
            tables
            |> List.map (fun t -> {
                Label = literal t.TableId
                Value = t.TableId
            })

        let chosen =
            selected
            |> Option.filter (fun id -> tables |> List.exists (fun t -> t.TableId = id))

        Fuaran.select "table" {
            Defaults.select with
                Label = literal "Table"
                Source = Binding.Static(Some options)
                Value = Some(Binding.Static chosen)
                Placeholder = Some(literal "Choose a table")
                OnChange =
                    Some(fun value ->
                        match value with
                        | Some tableId -> Action.dispatch (OpenTable tableId)
                        | None -> Action.Chain [])
                Affordance =
                    Some {
                        Defaults.affordance with
                            Patterns = tablePatterns
                            Description = Some "The declared fact table the browse shows."
                    }
        }

    let private freshnessTone (freshness: string) : ToneVariant =
        match freshness with
        | "fresh" -> ToneVariant.Success
        | "stale" -> ToneVariant.Warning
        | "unavailable" -> ToneVariant.Critical
        | _ -> ToneVariant.Default

    /// The latest run's outcome and the reason it gave, which the Feliz
    /// listing carried only as a hover title.
    let private latestRun (t: FactTableListing) =
        match t.LatestRunOutcome, t.LatestRunReason with
        | Some outcome, Some reason -> sprintf "%s: %s" outcome reason
        | Some outcome, None -> outcome
        | None, Some reason -> reason
        | None, None -> "—"

    /// One row per listed table and its standing; the freshness cell is a pill
    /// in the standing's tone, with the standing's own words.
    let private listing (tables: FactTableListing list) : Node<Msg> =
        let freshness (t: FactTableListing) =
            freshnessText t.Freshness t.OverdueBySeconds

        let tones =
            tables
            |> List.map (fun t -> freshness t, freshnessTone t.Freshness)
            |> Map.ofList

        let headers = [
            "Table"
            "Module"
            "Metrics"
            "Level"
            "Rows"
            "Last refresh"
            "Freshness"
            "Latest run"
        ]

        let columns =
            headers
            |> List.map (fun header ->
                if header = "Freshness" then
                    TableColumn.tonedPill header tones ToneVariant.Default
                else
                    TableColumn.text header)

        Fuaran.tableWithColumns "fact-table-listing" columns {
            Defaults.table with
                Headers = headers |> List.map literal
                Rows =
                    tables
                    |> List.map (fun t ->
                        [
                            t.TableId
                            t.Module
                            String.concat ", " t.Metrics
                            sprintf "%s · %s" t.Hierarchy t.Level
                            t.RowCount |> Option.map string |> Option.defaultValue "—"
                            optionalTime t.LastRefreshedAt
                            freshness t
                            latestRun t
                        ]
                        |> List.map literal)
        }

    /// The Tables page: the declared tables, the table select over them, and
    /// a considered empty state for no table and for a metric no table carries.
    let tables (model: Model) : Node<Msg> =
        page "fact-tables" "Fact tables" [
            Nodes.Layout.row "fact-tables-header" [
                Nodes.heading "fact-tables-heading" 2 "Fact tables"
                Nodes.Actions.button "refresh-tables" ButtonVariant.Secondary "Refresh" false RefreshTables
            ]
            match model.MetricFilter with
            | Some metric ->
                Nodes.Layout.row "metric-filter" [
                    Nodes.text "metric-filter-note" (sprintf "Showing the tables that carry metric '%s'." metric)
                    Nodes.Actions.button
                        "show-all-tables"
                        ButtonVariant.Tertiary
                        "Show all tables"
                        false
                        ClearMetricFilter
                ]
            | None -> ()
            match model.Tables with
            | NotAsked
            | Loading -> loading "tables-loading"
            | LoadFailed message -> failure "tables-failed" message
            | Loaded [] ->
                note
                    "no-tables"
                    "No fact tables are declared in this deployment."
                    "A module declares the tables it publishes; they appear here with their runs once it does."
            | Loaded _ ->
                match visibleTables model with
                | [] ->
                    note
                        "no-carrying-table"
                        "No declared table carries this metric."
                        "The metric's facts may be asserted individually rather than published as a table."
                | visible ->
                    tableSelect visible model.SelectedTable
                    listing visible
        ]

    let private standing (listing: FactTableListing) : Node<Msg> =
        let variant =
            match listing.Freshness with
            | "fresh" -> BadgeVariant.Success
            | "stale" -> BadgeVariant.Warning
            | "unavailable" -> BadgeVariant.Critical
            | _ -> BadgeVariant.Neutral

        Nodes.Layout.row "table-standing" [
            Fuaran.badge "table-freshness" {
                Label = literal (freshnessText listing.Freshness listing.OverdueBySeconds)
                Variant = variant
            }
            Nodes.text
                "table-rows"
                (match listing.RowCount with
                 | Some n -> sprintf "%d rows · last refresh %s" n (optionalTime listing.LastRefreshedAt)
                 | None -> "No committed run yet")
        ]

    /// The declared columns, each with its description.
    let private columns (detail: FactTableDetail) : Node<Msg> =
        Fuaran.table "table-columns" {
            Defaults.table with
                Headers = [ "Metric"; "Description"; "Shape"; "Disclosure" ] |> List.map literal
                Rows =
                    detail.Columns
                    |> List.map (fun c ->
                        [ c.Metric; c.Description |> Option.defaultValue ""; c.Shape; c.Disclosure ]
                        |> List.map literal)
        }

    let private population (rows: FactRowPage) : Node<Msg> =
        let p = rows.Population

        let magnitude (value: string option) =
            match value with
            | Some v -> v
            | None when p.ValueStatisticsWithheld -> "withheld"
            | None -> "—"

        Nodes.Layout.column "population" [
            Nodes.text "population-metric" (sprintf "Metric %s" rows.Metric)
            Nodes.Layout.row "population-figures" [
                Fuaran.fact "population-subjects" "Subjects" (string p.SubjectCount)
                Fuaran.fact "population-facts" "Facts" (string p.FactCount)
                Fuaran.fact "population-freshness" "Fresh / stale" (sprintf "%d / %d" p.FreshCount p.StaleCount)
                Fuaran.fact
                    "population-period"
                    "Period"
                    (sprintf "%s → %s" (optionalTime p.PeriodFrom) (optionalTime p.PeriodTo))
                Fuaran.fact "population-minimum" "Minimum" (magnitude p.Minimum)
                Fuaran.fact "population-maximum" "Maximum" (magnitude p.Maximum)
                Fuaran.fact "population-mean" "Mean" (magnitude p.Mean)
                Fuaran.fact
                    "population-methods"
                    "Methods"
                    (p.Methods
                     |> List.map (fun m -> sprintf "%s (%d)" m.Method m.FactCount)
                     |> String.concat ", ")
            ]
            if p.ValueStatisticsWithheld then
                Nodes.text
                    "population-withheld"
                    "The minimum, maximum and mean are withheld because the ranking holds rows you may not see; the counts describe the whole table."
        ]

    /// The Table page: the selected table's declaration, standing, columns,
    /// population summary and run history, with the table select above them.
    let table (model: Model) (dispatch: Msg -> unit) : Node<Msg> =
        let listed =
            match model.Tables with
            | Loaded tables -> tables
            | _ -> []

        page "fact-table" "Table" [
            match model.SelectedTable, model.Detail with
            | None, _ ->
                Nodes.heading "fact-table-heading" 2 "Table"

                if not (List.isEmpty listed) then
                    tableSelect listed None

                note
                    "no-table"
                    "No table is chosen."
                    "Choose a table here or on the Tables page to see its refreshes and what each one changed."
            | Some _, (NotAsked | Loading) -> loading "table-loading"
            | Some _, LoadFailed message -> failure "table-failed" message
            | Some selected, Loaded detail ->
                let listing = detail.Listing

                Nodes.Layout.row "fact-table-header" [
                    Nodes.heading "fact-table-heading" 2 listing.TableId
                    TreeLowering.island "browse-rows" (fun () ->
                        linkButton "Browse rows" (fun () -> goTo FactBrowseLinks.RowsRoute))
                ]

                if not (List.isEmpty listed) then
                    tableSelect listed (Some selected)

                match detail.Description with
                | Some description -> Nodes.text "table-description" description
                | None -> ()

                Nodes.text
                    "table-declaration"
                    (sprintf
                        "Declared by %s · %s at level %s · %s grain · %s · refreshed every %dh · %s"
                        listing.Module
                        listing.Hierarchy
                        listing.Level
                        listing.PeriodGrain
                        listing.HistoryMode
                        (max 1L (detail.RefreshCadenceSeconds / 3600L))
                        detail.Requirement)

                standing listing
                Nodes.heading "columns-heading" 3 "Columns"
                columns detail
                Nodes.heading "population-heading" 3 "Population"

                match model.Rows with
                | Loaded rows when rows.TableId = listing.TableId -> population rows
                | LoadFailed message -> failure "population-failed" message
                | Loaded _
                | Loading -> loading "population-loading"
                | NotAsked -> Nodes.text "population-none" "This table declares no metric column to summarise."

                Nodes.heading "runs-heading" 3 "Runs"
                TreeLowering.island "fact-table-runs" (fun () -> RunsSection listing.TableId model dispatch)
        ]

// ─── Tables page ─────────────────────────────────────────────────────

/// The listing's dispatch: opening a table from the listing also shows its
/// page, as the listing's links did before the page was a tree.
let private showingTable (dispatch: Msg -> unit) (msg: Msg) =
    dispatch msg

    match msg with
    | OpenTable _ -> goTo FactBrowseLinks.TableRoute
    | _ -> ()

/// The Tables page's tree, lowered: one row per declared table and its
/// standing, and the table select. `TablesPage` adds the per-run
/// notification subscription.
let tablesView (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-6"
        prop.children [
            TreeLowering.lower
                (TreeLowering.TreeHost.ofSources HostBindingSources.empty)
                (showingTable dispatch)
                (Trees.tables model)
        ]
    ]

/// The Tables page: `tablesView`, refreshed by the per-run notification.
[<ReactComponent>]
let TablesPage (model: Model) (dispatch: Msg -> unit) =
    // The per-run notification: refresh standing when a run commits.
    // Nothing subscribes per fact — there is no per-fact signal.
    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.CustomNotification(key, payloadJson) when
                    key = FactBrowseLinks.RunCommittedNotificationKey
                    ->
                    try
                        dispatch (RunCommitted(Json.parseAs<FactTableRunNotice> payloadJson))
                    with _ ->
                        ()
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    Html.div [
        prop.children [
            // Who in the team sees restricted output, for every member who
            // browses it (nothing until the deployment composes team output
            // visibility).
            Html.div [
                prop.className "px-6 pt-4"
                prop.children [ TeamConfigUI.TeamOutputVisibilityNotice() ]
            ]
            tablesView model dispatch
        ]
    ]

// ─── Table detail page ───────────────────────────────────────────────

/// The Table detail page's tree, lowered: declaration, population summary,
/// run history.
let TablePage (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-6"
        prop.children [
            TreeLowering.lower
                (TreeLowering.TreeHost.ofSources HostBindingSources.empty)
                dispatch
                (Trees.table model dispatch)
        ]
    ]

// ─── Drill-down page ─────────────────────────────────────────────────

let private factPanel (fact: Load<FactDetailView>) (dispatch: Msg -> unit) =
    match fact with
    | NotAsked -> Html.none
    | Loading -> loadingNote ()
    | LoadFailed message ->
        Html.div [
            prop.className "space-y-2"
            prop.children [ failed message; linkButton "Close" (fun () -> dispatch CloseFact) ]
        ]
    | Loaded f ->
        let line (label: string) (value: string) =
            Html.div [
                prop.className "flex gap-2 text-sm"
                prop.children [
                    Html.span [ prop.className "w-32 shrink-0 text-gray-500"; prop.text label ]
                    Html.span [ prop.className "text-gray-800 break-all"; prop.text value ]
                ]
            ]

        Html.div [
            prop.className "rounded border border-indigo-200 bg-indigo-50/40 p-4 space-y-1"
            prop.children [
                Html.div [
                    prop.className "flex items-center justify-between"
                    prop.children [
                        Html.h3 [
                            prop.className "text-sm font-semibold text-gray-900"
                            prop.text "Fact and provenance"
                        ]
                        linkButton "Close" (fun () -> dispatch CloseFact)
                    ]
                ]
                line "Subject" f.Row.Subject
                line "Metric" f.Metric
                line "Value" f.Row.Rendering
                line
                    "Period"
                    (f.Row.PeriodLabel
                     |> Option.defaultValue (sprintf "%s → %s" (time f.Row.PeriodFrom) (time f.Row.PeriodTo)))
                line "Asserted" (time f.Row.AsOf)
                line "Freshness" f.Row.Freshness
                line "Method" f.Method
                line "Disclosure" f.Disclosure
                line "Table" (f.TableId |> Option.defaultValue "—")
                line "Run" (f.RunId |> Option.defaultValue "—")
                line "Computed from" (f.ResultRef |> Option.defaultValue "—")
                line "Trigger" (f.TriggerRef |> Option.defaultValue "—")
                line "Input hashes" (String.concat ", " f.InputHashes)
                line "Fact id" f.Row.FactId
                sectionHeading "Supersession chain"
                if List.isEmpty f.Chain && f.ChainWithheldCount = 0 then
                    Html.p [
                        prop.className "text-sm text-gray-500 italic"
                        prop.text "This is the first value asserted for this subject, metric and period."
                    ]
                else
                    table
                        [ "Asserted"; "Value"; "Method"; "Fact id" ]
                        (f.Chain
                         |> List.map (fun step ->
                             Html.tr [
                                 prop.key step.FactId
                                 prop.children [
                                     cell (time step.AsOf)
                                     cell step.Rendering
                                     cell step.Method
                                     monoCell step.FactId
                                 ]
                             ]))
                withheldNote f.ChainWithheldCount [] "earlier values are"
            ]
        ]

/// The drill-down page: one page of ranked rows (from the held slice, or the
/// server's page read) and the opened fact.
[<ReactComponent>]
let RowsPage (model: Model) (dispatch: Msg -> unit) =
    let queryPrefix =
        model.RowQuery
        |> Option.map (fun q -> String.concat ">" q.SubjectPrefix)
        |> Option.defaultValue ""

    // Display state only: the prefix is sent on submit, never per
    // keystroke. It follows the query when something else moves it (a
    // fact opened from a citation narrows the drill-down to its subject).
    let prefixText, setPrefixText = React.useState queryPrefix
    React.useEffect ((fun () -> setPrefixText queryPrefix), [| box queryPrefix |])

    let rows, rowsBusy =
        useHeld (fun (page: FactRowPage) -> model.SelectedTable = Some page.TableId) model.Rows

    Html.div [
        prop.className "p-6 space-y-3"
        prop.children [
            heading "Rows"
            match model.SelectedTable, model.RowQuery with
            | None, _ ->
                emptyState
                    "No table is chosen."
                    "Choose a table on the Tables page, then look any subject up here, one page at a time."
            | Some _, None ->
                match model.Detail with
                | Loaded detail when List.isEmpty detail.Columns ->
                    emptyState "This table declares no metric column." "There is nothing to rank."
                | LoadFailed message -> failed message
                | _ -> loadingNote ()
            | Some _, Some query ->
                let metrics =
                    match model.Detail with
                    | Loaded detail -> detail.Listing.Metrics
                    | _ -> [ query.Metric ]

                let request (q: FactRowQuery) = dispatch (RowsRequested q)

                Html.div [
                    prop.className "flex flex-wrap items-center gap-2"
                    prop.children [
                        Html.select [
                            prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                            prop.value query.Metric
                            prop.onChange (fun (metric: string) -> request { query with Metric = metric; Page = 0 })
                            prop.children (metrics |> List.map (fun m -> Html.option [ prop.value m; prop.text m ]))
                        ]
                        button "Top" (query.Direction <> TopOfRanking) (fun () ->
                            request {
                                query with
                                    Direction = TopOfRanking
                                    Page = 0
                            })
                        button "Bottom" (query.Direction <> BottomOfRanking) (fun () ->
                            request {
                                query with
                                    Direction = BottomOfRanking
                                    Page = 0
                            })
                        Html.form [
                            prop.className "flex items-center gap-2"
                            prop.onSubmit (fun e ->
                                e.preventDefault ()

                                request {
                                    query with
                                        SubjectPrefix = parseSubjectPrefix prefixText
                                        Page = 0
                                })
                            prop.children [
                                Html.input [
                                    prop.className "border border-gray-300 rounded px-2 py-1 text-sm w-64"
                                    prop.placeholder "Subject path, e.g. region>store"
                                    prop.value prefixText
                                    prop.onChange (fun (text: string) -> setPrefixText text)
                                ]
                                Html.button [
                                    prop.type' "submit"
                                    prop.className
                                        "px-3 py-1 rounded border border-gray-300 text-sm text-gray-700 hover:bg-gray-100"
                                    prop.text "Search"
                                ]
                            ]
                        ]
                    ]
                ]

                match rows with
                | NotAsked
                | Loading -> loadingNote ()
                | LoadFailed message -> failed message
                | Loaded page ->
                    let ranked =
                        page.Rows
                        |> List.map (fun r ->
                            Html.tr [
                                prop.key r.FactId
                                prop.children [
                                    cell (string r.Rank)
                                    Html.td [
                                        prop.className "px-3 py-2"
                                        prop.children [ linkButton r.Subject (fun () -> dispatch (OpenFact r.FactId)) ]
                                    ]
                                    cell r.Rendering
                                    cell (r.PeriodLabel |> Option.defaultValue (time r.PeriodFrom))
                                    cell r.Freshness
                                ]
                            ])

                    Html.div [
                        prop.ariaBusy rowsBusy
                        prop.className (heldClass rowsBusy)
                        prop.children [
                            if List.isEmpty page.Rows && page.WithheldCount = 0 then
                                emptyState
                                    (if List.isEmpty page.SubjectPrefix then
                                         "No ranked rows on this page."
                                     else
                                         "No subject under this path has a ranked value.")
                                    FactBrowseApi.BulkExtractionNote
                            else
                                table [ "Rank"; "Subject"; page.Metric; "Period"; "Freshness" ] ranked
                            withheldNote page.WithheldCount page.Withheld "rows on this page are"
                            Html.div [
                                prop.className "flex items-center gap-2 mt-2 text-sm text-gray-600"
                                prop.children [
                                    button "Previous" (page.Page > 0) (fun () ->
                                        request { query with Page = page.Page - 1 })
                                    Html.span [
                                        prop.text (
                                            sprintf
                                                "Page %d · %d per page (at most %d)"
                                                (page.Page + 1)
                                                page.PageSize
                                                page.MaxPageSize
                                        )
                                    ]
                                    button "Next" page.HasMore (fun () -> request { query with Page = page.Page + 1 })
                                ]
                            ]
                            if page.ReachedRankCeiling then
                                Html.p [
                                    prop.className "text-xs text-gray-500"
                                    prop.text (
                                        sprintf
                                            "Browsing reaches the first %d ranks. %s"
                                            page.RankCeiling
                                            FactBrowseApi.BulkExtractionNote
                                    )
                                ]
                            else
                                Html.p [
                                    prop.className "text-xs text-gray-500"
                                    prop.text FactBrowseApi.BulkExtractionNote
                                ]
                        ]
                    ]
            factPanel model.Fact dispatch
        ]
    ]

// ─── Module ──────────────────────────────────────────────────────────

let private page (route: string) (title: string) (icon: ReactElement) (body: Model -> (Msg -> unit) -> ReactElement) =
    let config: PageConfig = {
        Route = route
        Title = title
        Icon = icon
    }

    config, (fun (model: Model) (ctx: RenderContext<Msg>) -> PageContent.FullWidth(body model ctx.Dispatch))

/// Create the fact browse module (`FactBrowseLinks.ModuleId`). Its three
/// pages sit under the module's name in the sidebar; it subscribes to the
/// cross-module open-table / open-metric / open-fact topics, so other
/// companions can link in without importing this package.
let create (label: ModuleLabel option) : ErasedModule =
    let name = label |> Option.map _.Name |> Option.defaultValue "Fact tables"

    let icon =
        label |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.usage

    ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ClientModule.withId FactBrowseLinks.ModuleId
    |> ClientModule.withPages [
        page FactBrowseLinks.TablesRoute "Tables" ToolUp.Platform.Icons.usage TablesPage
        page FactBrowseLinks.TableRoute "Table" ToolUp.Platform.Icons.interconnected TablePage
        page FactBrowseLinks.RowsRoute "Rows" ToolUp.Platform.Icons.target RowsPage
    ]
    |> ClientModule.withEventSubscription FactBrowseLinks.OpenTableTopic OpenTable
    |> ClientModule.withEventSubscription FactBrowseLinks.OpenMetricTopic FilterByMetric
    |> ClientModule.withEventSubscription FactBrowseLinks.OpenFactTopic OpenFact
    |> ClientModule.withGroup "Knowledge"
    |> ClientModule.withVisibility Visibility.visibleToAuthenticated
    |> ClientModule.register

/// Append the fact browse module to a deployment's module list. A
/// deployment that composes the fact tier's browse surface on the server
/// (`FactBrowseHandler.withFactBrowse`) adds this on the client; one that
/// does not is byte-for-byte unchanged, and the knowledge base badge and
/// the conversation panel's fact link stay hidden because this module is
/// not in the shell's module list.
let appendFactBrowseModule (label: ModuleLabel option) (modules: ErasedModule list) : ErasedModule list =
    modules @ [ create label ]