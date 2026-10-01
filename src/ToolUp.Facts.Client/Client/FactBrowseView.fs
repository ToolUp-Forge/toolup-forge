// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Facts.Client.FactBrowseView

// ─── Fact browse — pages and module (Phase 895) ──────────────────────
//
// Three declared pages, summary first:
//
//   /tables — one row per declared table: owning module, metrics, level,
//             row count, last refresh, freshness.
//   /table  — the selected table's declaration, population summary and
//             run history (paged), with each committed run's change
//             summary and, on request, its largest movers.
//   /rows   — the drill-down: rank by a metric column, top or bottom,
//             under a subject prefix, one server page at a time; and the
//             opened fact with its provenance chain.
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
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Facts.Client.FactBrowseModel

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

let private freshnessBadge (freshness: string) (overdueSeconds: int64 option) =
    let colour =
        match freshness with
        | "fresh" -> "bg-green-100 text-green-700"
        | "stale" -> "bg-amber-100 text-amber-700"
        | "unavailable" -> "bg-red-100 text-red-700"
        | _ -> "bg-gray-100 text-gray-600"

    let text =
        match freshness, overdueSeconds with
        | "stale", Some s -> sprintf "stale · %dh overdue" (max 1L (s / 3600L))
        | "never-refreshed", _ -> "never refreshed"
        | other, _ -> other

    Html.span [
        prop.className (sprintf "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium %s" colour)
        prop.text text
    ]

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

// ─── Tables page ─────────────────────────────────────────────────────

/// The Tables page's markup: one row per declared table and its standing.
/// Pure — `TablesPage` adds the per-run notification subscription.
let tablesView (model: Model) (dispatch: Msg -> unit) =
    let rows (tables: FactTableListing list) =
        tables
        |> List.map (fun t ->
            Html.tr [
                prop.key t.TableId
                prop.children [
                    Html.td [
                        prop.className "px-3 py-2"
                        prop.children [
                            linkButton t.TableId (fun () ->
                                dispatch (OpenTable t.TableId)
                                goTo FactBrowseLinks.TableRoute)
                        ]
                    ]
                    cell t.Module
                    cell (String.concat ", " t.Metrics)
                    cell (sprintf "%s · %s" t.Hierarchy t.Level)
                    cell (t.RowCount |> Option.map string |> Option.defaultValue "—")
                    cell (optionalTime t.LastRefreshedAt)
                    Html.td [
                        prop.className "px-3 py-2"
                        prop.title (t.LatestRunReason |> Option.defaultValue "")
                        prop.children [ freshnessBadge t.Freshness t.OverdueBySeconds ]
                    ]
                ]
            ])

    Html.div [
        prop.className "p-6 space-y-3"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    heading "Fact tables"
                    button "Refresh" true (fun () -> dispatch RefreshTables)
                ]
            ]
            match model.MetricFilter with
            | Some metric ->
                Html.div [
                    prop.className "flex items-center gap-2 text-sm text-gray-700"
                    prop.children [
                        Html.span [ prop.text (sprintf "Showing the tables that carry metric '%s'." metric) ]
                        linkButton "Show all tables" (fun () -> dispatch ClearMetricFilter)
                    ]
                ]
            | None -> Html.none
            match model.Tables with
            | NotAsked
            | Loading -> loadingNote ()
            | LoadFailed message -> failed message
            | Loaded [] ->
                emptyState
                    "No fact tables are declared in this deployment."
                    "A module declares the tables it publishes; they appear here with their runs once it does."
            | Loaded _ ->
                match visibleTables model with
                | [] ->
                    emptyState
                        "No declared table carries this metric."
                        "The metric's facts may be asserted individually rather than published as a table."
                | visible ->
                    table [ "Table"; "Module"; "Metrics"; "Level"; "Rows"; "Last refresh"; "Freshness" ] (rows visible)
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
            // Phase 942 — who in the team sees restricted output, for every
            // member who browses it (nothing until the deployment composes
            // team output visibility).
            Html.div [
                prop.className "px-6 pt-4"
                prop.children [ TeamConfigUI.TeamOutputVisibilityNotice() ]
            ]
            tablesView model dispatch
        ]
    ]

// ─── Population summary ──────────────────────────────────────────────

let private populationSummary (population: FactPopulationView) =
    let stat (label: string) (value: string) =
        Html.div [
            prop.className "rounded border border-gray-200 bg-white px-3 py-2"
            prop.children [
                Html.div [ prop.className "text-xs text-gray-500"; prop.text label ]
                Html.div [ prop.className "text-sm font-medium text-gray-900"; prop.text value ]
            ]
        ]

    let magnitude (value: string option) =
        match value with
        | Some v -> v
        | None when population.ValueStatisticsWithheld -> "withheld"
        | None -> "—"

    Html.div [
        prop.children [
            Html.div [
                prop.className "grid grid-cols-2 md:grid-cols-4 gap-2"
                prop.children [
                    stat "Subjects" (string population.SubjectCount)
                    stat "Facts" (string population.FactCount)
                    stat "Fresh / stale" (sprintf "%d / %d" population.FreshCount population.StaleCount)
                    stat
                        "Period"
                        (sprintf "%s → %s" (optionalTime population.PeriodFrom) (optionalTime population.PeriodTo))
                    stat "Minimum" (magnitude population.Minimum)
                    stat "Maximum" (magnitude population.Maximum)
                    stat "Mean" (magnitude population.Mean)
                    stat
                        "Methods"
                        (population.Methods
                         |> List.map (fun m -> sprintf "%s (%d)" m.Method m.FactCount)
                         |> String.concat ", ")
                ]
            ]
            if population.ValueStatisticsWithheld then
                Html.p [
                    prop.className "text-xs text-gray-500 mt-1"
                    prop.text
                        "The minimum, maximum and mean are withheld because the ranking holds rows you may not see; the counts describe the whole table."
                ]
        ]
    ]

// ─── Table detail page ───────────────────────────────────────────────

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

/// The Table detail page: declaration, population summary, run history.
let TablePage (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "p-6 space-y-3"
        prop.children [
            match model.SelectedTable, model.Detail with
            | None, _ ->
                heading "Table"

                emptyState
                    "No table is chosen."
                    "Choose a table on the Tables page to see its refreshes and what each one changed."
            | Some _, (NotAsked | Loading) -> loadingNote ()
            | Some _, LoadFailed message -> failed message
            | Some _, Loaded detail ->
                let listing = detail.Listing

                Html.div [
                    prop.className "flex items-center justify-between"
                    prop.children [
                        heading listing.TableId
                        linkButton "Browse rows" (fun () -> goTo FactBrowseLinks.RowsRoute)
                    ]
                ]

                Html.p [
                    prop.className "text-sm text-gray-600"
                    prop.text (
                        sprintf
                            "Declared by %s · %s at level %s · %s grain · %s · refreshed every %dh · %s"
                            listing.Module
                            listing.Hierarchy
                            listing.Level
                            listing.PeriodGrain
                            listing.HistoryMode
                            (max 1L (detail.RefreshCadenceSeconds / 3600L))
                            detail.Requirement
                    )
                ]

                Html.div [
                    prop.className "flex items-center gap-2"
                    prop.children [
                        freshnessBadge listing.Freshness listing.OverdueBySeconds
                        Html.span [
                            prop.className "text-sm text-gray-600"
                            prop.text (
                                match listing.RowCount with
                                | Some n -> sprintf "%d rows · last refresh %s" n (optionalTime listing.LastRefreshedAt)
                                | None -> "No committed run yet"
                            )
                        ]
                    ]
                ]

                sectionHeading "Columns"

                table
                    [ "Metric"; "Shape"; "Disclosure" ]
                    (detail.Columns
                     |> List.map (fun c ->
                         Html.tr [
                             prop.key c.Metric
                             prop.children [ cell c.Metric; cell c.Shape; cell c.Disclosure ]
                         ]))

                sectionHeading "Population"

                match model.Rows with
                | Loaded page when page.TableId = listing.TableId ->
                    Html.div [
                        prop.children [
                            Html.p [
                                prop.className "text-xs text-gray-500 mb-1"
                                prop.text (sprintf "Metric %s" page.Metric)
                            ]
                            populationSummary page.Population
                        ]
                    ]
                | LoadFailed message -> failed message
                | Loaded _
                | Loading -> loadingNote ()
                | NotAsked ->
                    Html.p [
                        prop.className "text-sm text-gray-500 italic"
                        prop.text "This table declares no metric column to summarise."
                    ]

                sectionHeading "Runs"

                match model.Runs with
                | NotAsked
                | Loading -> loadingNote ()
                | LoadFailed message -> failed message
                | Loaded page -> runsTable model page dispatch
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

/// The drill-down page: one server page of ranked rows and the opened fact.
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

                match model.Rows with
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

    config, (fun (model: Model) (dispatch: Msg -> unit) -> PageContent.FullWidth(body model dispatch))

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
        page FactBrowseLinks.TableRoute "Table" ToolUp.Platform.Icons.dataLoading TablePage
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