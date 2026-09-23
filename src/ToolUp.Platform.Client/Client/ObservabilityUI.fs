// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ObservabilityUI

open System
open Fable.Core
open Fable.SimpleHttp
open Fable.SimpleJson
open Feliz
open Feliz.AgCharts
open Feliz.AgGrid
open ToolUp.Elmish
open ToolUp.Platform

// ─── Phase 9x — the self-hosted observability admin module ───────────
//
// Logs / Metrics / Alerts over the SDK's own `/api/observability/*`
// endpoints: the Phase 828 log store, the Phase 829 metrics history and
// the Phase 178 alert engine's per-rule state. Registered under the
// "Observability" sidebar group beside the health monitor and the
// Datadog readback.
//
// **Sources first.** The client cannot see which of the three sources
// the server composed, so the module reads `/api/observability/sources`
// on open and renders only the tabs whose source is on. A server with
// none of them on mounts no route at all, which reads here as "not
// available" rather than as three empty tabs.
//
// **Plain `Http.request`, not a Remoting proxy** — the endpoints are
// read-only REST (reachable with `curl` too), the shape the Datadog
// readback module uses beside this one; identity and CSRF headers are
// spliced in by `CsrfClient`'s request guard.
//
// **Failures are typed, and rendered through the catalog.** A fetch
// resolves to `FetchError` rather than to English prose, because the
// commands run outside any render and so cannot reach the resolved
// message catalog; the view renders the error at the one place that can.
//
// **No trace cross-references tab.** The shard sketched one "shown only
// when `IActivitySink` is registered". A stored `LogRecord` carries no
// trace id (the log store keeps scope and correlation ids only) and
// `IActivitySink` is export-only, so there is nothing for such a tab to
// read; the Logs tab's "every line of this request" action — a search on
// the correlation id — is the cross-reference the stored data supports.

// ─── Model ───────────────────────────────────────────────────────────

/// The module's three sections. Only those whose source the server
/// enabled are rendered.
type Tab =
    /// The log search (Phase 828 log store).
    | LogsTab
    /// The metric charts (Phase 829 metrics history).
    | MetricsTab
    /// The alert rules and their state (Phase 178 engine).
    | AlertsTab

/// Why a read produced no data. `NotComposed` covers both a 404 (the
/// route is unmounted — the source is off) and a 503 (the source is on
/// but its store is not composed).
type FetchError =
    /// 403 — the caller is not a platform administrator.
    | Forbidden
    /// 404 or 503 — the source is off, or its store is not composed.
    | NotComposed
    /// Any other non-200 status.
    | HttpStatus of int
    /// A 200 whose body did not parse; carries the parser's reason.
    | Unreadable of string
    /// The request never got an answer; carries the transport's reason.
    | Unreachable of string

/// One read's progress.
type LoadState<'T> =
    /// Not asked for — the source is off, or the module has not got there.
    | NotLoaded
    /// In flight.
    | Loading
    /// Answered.
    | Loaded of 'T
    /// Failed; rendered through the catalog.
    | LoadError of FetchError

/// The log search as the operator last submitted it.
type LogFilter = {
    /// Whole-word message search; blank matches everything.
    Text: string
    /// The levels to include; empty means every level.
    Levels: LogLevel list
    /// The look-back window, in minutes.
    WindowMinutes: int
    /// Exact scope-id filter; blank constrains nothing.
    ScopeId: string
    /// Exact correlation-id filter; blank constrains nothing.
    CorrelationId: string
}

/// The module's state.
type Model = {
    /// Which sources the server enabled — read first, decides the tabs.
    Sources: LoadState<ObservabilitySources>
    /// The section on screen.
    ActiveTab: Tab
    /// The log search as last submitted.
    Filter: LogFilter
    /// Bumped whenever the filter changes from OUTSIDE the filter bar
    /// (the "every line of this request" action), so the bar — which
    /// keeps its text inputs in local state — remounts with the new
    /// values rather than showing stale ones.
    FilterRevision: int
    /// The latest log search result.
    Logs: LoadState<ObservabilityLogsResponse>
    /// The log line whose full record is shown under the grid.
    Selected: LogRecord option
    /// The metric charts' look-back window, in minutes.
    MetricsWindow: int
    /// One entry per charted metric, in render order.
    Metrics: (string * LoadState<ObservabilityMetricResponse>) list
    /// The metric an alert row linked to; its chart renders first.
    FocusedMetric: string option
    /// The latest alert-rule read.
    Alerts: LoadState<ObservabilityAlertsResponse>
}

/// The module's messages.
type Msg =
    /// The sources read answered.
    | SourcesLoaded of Result<ObservabilitySources, FetchError>
    /// Show another section.
    | SwitchTab of Tab
    /// Run the log search with this filter.
    | SubmitFilter of LogFilter
    /// Add or remove one level from the search, and re-run it.
    | ToggleLevel of LogLevel
    /// Change the log search's window, and re-run it.
    | SetLogWindow of int
    /// The log search answered.
    | LogsLoaded of Result<ObservabilityLogsResponse, FetchError>
    /// Show one line's full record.
    | SelectRecord of LogRecord
    /// Search every line carrying this correlation id.
    | ShowCorrelated of string
    /// Change the charts' window, and re-read them.
    | SetMetricsWindow of int
    /// Re-read every chart.
    | RefreshMetrics
    /// One chart's read answered.
    | MetricLoaded of string * Result<ObservabilityMetricResponse, FetchError>
    /// Open the metrics section on this metric's chart (an alert row's link).
    | ShowMetric of string
    /// Re-read the alert rules.
    | RefreshAlerts
    /// The alert-rule read answered.
    | AlertsLoaded of Result<ObservabilityAlertsResponse, FetchError>

// ─── Fetch ───────────────────────────────────────────────────────────

/// One typed GET. 200 is data; 403 is the gate; 404 / 503 mean the
/// source is not available; anything else, and a body that will not
/// parse, is reported with what went wrong rather than swallowed.
let inline private fetchJson<'T> (url: string) : Async<Result<'T, FetchError>> = async {
    try
        let! response = Http.request url |> Http.method GET |> Http.send

        match response.statusCode with
        | 200 ->
            try
                return Ok(Json.parseAs<'T> response.responseText)
            with ex ->
                return Error(Unreadable ex.Message)
        | 403 -> return Error Forbidden
        | 404
        | 503 -> return Error NotComposed
        | code -> return Error(HttpStatus code)
    with ex ->
        return Error(Unreachable ex.Message)
}

// `fetchJson` is `inline` and each wrapper below resolves `'T`
// concretely — the Datadog readback module records why both halves are
// load-bearing: `Json.parseAs<'T>` needs the type at compile time, and
// handing the generic function itself to `Cmd.OfAsync` would erase it.

let private fetchSources (url: string) : Async<Result<ObservabilitySources, FetchError>> = fetchJson url

let private fetchLogs (url: string) : Async<Result<ObservabilityLogsResponse, FetchError>> = fetchJson url

let private fetchMetric (url: string) : Async<Result<ObservabilityMetricResponse, FetchError>> = fetchJson url

let private fetchAlerts (url: string) : Async<Result<ObservabilityAlertsResponse, FetchError>> = fetchJson url

/// The browser's own escape — `Uri.EscapeDataString` has no Fable
/// implementation.
[<Emit("encodeURIComponent($0)")>]
let private encodeUriComponent (value: string) : string = jsNative

let private levelToken (level: LogLevel) : string =
    match level with
    | LogLevel.Trace -> "trace"
    | LogLevel.Debug -> "debug"
    | LogLevel.Info -> "info"
    | LogLevel.Warn -> "warn"
    | LogLevel.Error -> "error"

let private loadSourcesCmd () =
    Cmd.OfAsync.perform fetchSources (ObservabilityReadback.RoutePrefix + "/sources") SourcesLoaded

let private loadLogsCmd (filter: LogFilter) =
    let optional (name: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            ""
        else
            sprintf "&%s=%s" name (encodeUriComponent (value.Trim()))

    let levels =
        if List.isEmpty filter.Levels then
            ""
        else
            "&levels=" + (filter.Levels |> List.map levelToken |> String.concat ",")

    let url =
        sprintf
            "%s/logs?minutes=%d%s%s%s%s"
            ObservabilityReadback.RoutePrefix
            filter.WindowMinutes
            levels
            (optional "q" filter.Text)
            (optional "scope" filter.ScopeId)
            (optional "correlationId" filter.CorrelationId)

    Cmd.OfAsync.perform fetchLogs url LogsLoaded

let private loadMetricCmd (windowMinutes: int) (metric: string) =
    let url =
        sprintf
            "%s/metrics?metric=%s&minutes=%d"
            ObservabilityReadback.RoutePrefix
            (encodeUriComponent metric)
            windowMinutes

    Cmd.OfAsync.perform fetchMetric url (fun result -> MetricLoaded(metric, result))

let private loadMetricsCmd (model: Model) =
    model.Metrics
    |> List.map (fst >> loadMetricCmd model.MetricsWindow)
    |> Cmd.batch

let private loadAlertsCmd () =
    Cmd.OfAsync.perform fetchAlerts (ObservabilityReadback.RoutePrefix + "/alerts") AlertsLoaded

// ─── Init / update ───────────────────────────────────────────────────

let private defaultFilter: LogFilter = {
    Text = ""
    Levels = []
    WindowMinutes = ObservabilityReadback.DefaultWindowMinutes
    ScopeId = ""
    CorrelationId = ""
}

/// The initial state: every read pending on the sources answer.
let init () =
    let model = {
        Sources = Loading
        ActiveTab = LogsTab
        Filter = defaultFilter
        FilterRevision = 0
        Logs = NotLoaded
        Selected = None
        MetricsWindow = ObservabilityReadback.DefaultWindowMinutes
        Metrics =
            ObservabilityReadback.defaultChartedMetrics
            |> List.map (fun metric -> metric, NotLoaded)
        FocusedMetric = None
        Alerts = NotLoaded
    }

    model, loadSourcesCmd ()

/// The first tab whose source is on, in tab order.
let private firstTab (sources: ObservabilitySources) : Tab =
    if sources.Logs then LogsTab
    elif sources.Metrics then MetricsTab
    else AlertsTab

let private searchLogs (model: Model) (filter: LogFilter) =
    {
        model with
            Filter = filter
            Logs = Loading
            Selected = None
    },
    loadLogsCmd filter

/// The module's update — pure; every read is a `Cmd`.
let update (msg: Msg) (model: Model) =
    match msg with
    | SourcesLoaded(Error error) -> { model with Sources = LoadError error }, Cmd.none

    | SourcesLoaded(Ok sources) ->
        let withMetrics =
            if sources.Metrics then
                {
                    model with
                        Metrics = model.Metrics |> List.map (fun (metric, _) -> metric, Loading)
                }
            else
                model

        let next = {
            withMetrics with
                Sources = Loaded sources
                ActiveTab = firstTab sources
                Logs = (if sources.Logs then Loading else NotLoaded)
                Alerts = (if sources.Alerts then Loading else NotLoaded)
        }

        next,
        Cmd.batch [
            if sources.Logs then
                loadLogsCmd next.Filter
            if sources.Metrics then
                loadMetricsCmd next
            if sources.Alerts then
                loadAlertsCmd ()
        ]

    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | SubmitFilter filter -> searchLogs model filter

    | ToggleLevel level ->
        let levels =
            if List.contains level model.Filter.Levels then
                model.Filter.Levels |> List.filter ((<>) level)
            else
                model.Filter.Levels @ [ level ]

        searchLogs model { model.Filter with Levels = levels }

    | SetLogWindow minutes ->
        searchLogs model {
            model.Filter with
                WindowMinutes = minutes
        }

    | LogsLoaded(Ok response) -> { model with Logs = Loaded response }, Cmd.none

    | LogsLoaded(Error error) -> { model with Logs = LoadError error }, Cmd.none

    | SelectRecord record -> { model with Selected = Some record }, Cmd.none

    | ShowCorrelated correlationId ->
        let next, cmd =
            searchLogs model {
                model.Filter with
                    CorrelationId = correlationId
                    Text = ""
                    ScopeId = ""
            }

        {
            next with
                FilterRevision = model.FilterRevision + 1
        },
        cmd

    | SetMetricsWindow minutes ->
        let next = {
            model with
                MetricsWindow = minutes
                Metrics = model.Metrics |> List.map (fun (metric, _) -> metric, Loading)
        }

        next, loadMetricsCmd next

    | RefreshMetrics ->
        let next = {
            model with
                Metrics = model.Metrics |> List.map (fun (metric, _) -> metric, Loading)
        }

        next, loadMetricsCmd next

    | MetricLoaded(metric, result) ->
        let loaded =
            match result with
            | Ok response -> Loaded response
            | Error error -> LoadError error

        {
            model with
                Metrics =
                    model.Metrics
                    |> List.map (fun (name, state) -> if name = metric then name, loaded else name, state)
        },
        Cmd.none

    | ShowMetric metric ->
        let known = model.Metrics |> List.exists (fun (name, _) -> name = metric)

        let next = {
            model with
                ActiveTab = MetricsTab
                FocusedMetric = Some metric
                Metrics =
                    if known then
                        model.Metrics
                    else
                        model.Metrics @ [ metric, Loading ]
        }

        next,
        (if known then
             Cmd.none
         else
             loadMetricCmd model.MetricsWindow metric)

    | RefreshAlerts -> { model with Alerts = Loading }, loadAlertsCmd ()

    | AlertsLoaded(Ok response) -> { model with Alerts = Loaded response }, Cmd.none

    | AlertsLoaded(Error error) -> { model with Alerts = LoadError error }, Cmd.none

// ─── Shared renderers ────────────────────────────────────────────────

let private describeError (msgs: ObservabilityMessages) (error: FetchError) : string =
    match error with
    | Forbidden -> msgs.ErrorForbidden
    | NotComposed -> msgs.ErrorNotComposed
    | HttpStatus code -> msgs.ErrorStatus code
    | Unreadable reason -> msgs.ErrorUnreadable reason
    | Unreachable reason -> msgs.ErrorUnreachable reason

let private errorBanner (message: string) =
    Html.div [
        prop.className "p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm"
        prop.text message
    ]

let private warningBanner (message: string) =
    Html.div [
        prop.className "p-3 mb-3 bg-yellow-50 border border-yellow-200 rounded text-yellow-800 text-sm"
        prop.text message
    ]

let private loadingLine (msgs: ObservabilityMessages) =
    Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]

let private footnote (text: string) =
    Html.p [ prop.className "mt-3 text-xs text-gray-500"; prop.text text ]

let private refreshButton (msgs: ObservabilityMessages) (loading: bool) (onClick: unit -> unit) =
    Html.button [
        prop.className [
            "px-3 py-1.5 text-sm font-medium rounded border transition-colors"
            if loading then
                "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
            else
                "bg-white text-gray-700 border-border hover:bg-gray-50"
        ]
        prop.disabled loading
        prop.text (if loading then msgs.Refreshing else msgs.Refresh)
        prop.onClick (fun _ -> onClick ())
    ]

/// Formatted by hand: `DateTime.ToString(format)` is only partly
/// implemented under Fable (the Datadog readback module records the same
/// choice).
let private instant (value: DateTime) =
    sprintf "%04d-%02d-%02d %02d:%02d:%02d" value.Year value.Month value.Day value.Hour value.Minute value.Second

let private optionalInstant (msgs: ObservabilityMessages) (value: DateTime option) =
    match value with
    | Some at -> instant at
    | None -> msgs.Never

let private clockTime (value: DateTime) =
    sprintf "%02d:%02d" value.Hour value.Minute

let private levelLabel (msgs: ObservabilityMessages) (level: LogLevel) =
    match level with
    | LogLevel.Trace -> msgs.LevelTrace
    | LogLevel.Debug -> msgs.LevelDebug
    | LogLevel.Info -> msgs.LevelInfo
    | LogLevel.Warn -> msgs.LevelWarn
    | LogLevel.Error -> msgs.LevelError

/// The look-back windows the pickers offer, in minutes.
let private windowChoices = [ 15; 60; 240; 1440 ]

let private windowPicker (msgs: ObservabilityMessages) (current: int) (onChange: int -> unit) =
    Html.label [
        prop.className "flex items-center gap-2 text-sm text-gray-600"
        prop.children [
            Html.span [ prop.text msgs.WindowPickerLabel ]
            Html.select [
                prop.className "border border-border rounded px-2 py-1 text-sm bg-white"
                prop.value (string current)
                prop.onChange (fun (value: string) ->
                    match Int32.TryParse value with
                    | true, minutes -> onChange minutes
                    | _ -> ())
                prop.children [
                    for minutes in windowChoices ->
                        Html.option [ prop.value (string minutes); prop.text (msgs.WindowLabel minutes) ]
                ]
            ]
        ]
    ]

let private headerCell (label: string) =
    Html.th [
        prop.className "px-3 py-2 text-left text-xs font-semibold text-gray-600 uppercase tracking-wide"
        prop.text label
    ]

let private cell (value: string) =
    Html.td [ prop.className "px-3 py-2 text-sm text-gray-800 align-top"; prop.text value ]

// ─── Logs tab ────────────────────────────────────────────────────────

/// The search bar. Its text inputs live in local state and only a submit
/// (Enter or the button) reaches the model — no per-keystroke messages.
/// Keyed by `FilterRevision` at the call site, so an outside change to
/// the filter remounts it with the new values.
[<ReactComponent>]
let private LogFilterBar (msgs: ObservabilityMessages) (filter: LogFilter) (dispatch: Msg -> unit) =
    let text, setText = React.useState filter.Text
    let scope, setScope = React.useState filter.ScopeId
    let correlation, setCorrelation = React.useState filter.CorrelationId

    let submit () =
        dispatch (
            SubmitFilter {
                filter with
                    Text = text
                    ScopeId = scope
                    CorrelationId = correlation
            }
        )

    let input (placeholder: string) (value: string) (onChange: string -> unit) (width: string) =
        Html.input [
            prop.className $"border border-border rounded px-2 py-1 text-sm {width}"
            prop.placeholder placeholder
            prop.value value
            prop.onChange onChange
            prop.onKeyDown (fun e ->
                if e.key = "Enter" then
                    submit ())
        ]

    Html.div [
        prop.className "flex flex-wrap items-center gap-2"
        prop.children [
            input msgs.SearchPlaceholder text setText "flex-1 min-w-48"
            input msgs.ScopePlaceholder scope setScope "w-40"
            input msgs.CorrelationPlaceholder correlation setCorrelation "w-48"
            Html.button [
                prop.className
                    "px-3 py-1.5 text-sm font-medium rounded border bg-white text-gray-700 border-border hover:bg-gray-50"
                prop.text msgs.Search
                prop.onClick (fun _ -> submit ())
            ]
        ]
    ]

let private levelToggles (msgs: ObservabilityMessages) (filter: LogFilter) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex items-center gap-1 text-sm"
        prop.children [
            Html.span [ prop.className "text-gray-600 mr-1"; prop.text msgs.LevelsLabel ]
            for level in [ LogLevel.Trace; LogLevel.Debug; LogLevel.Info; LogLevel.Warn; LogLevel.Error ] do
                let active = List.contains level filter.Levels

                Html.button [
                    prop.key (levelToken level)
                    prop.className [
                        "px-2 py-0.5 text-xs rounded border"
                        if active then
                            "bg-brand text-white border-brand"
                        else
                            "bg-white text-gray-600 border-border hover:bg-gray-50"
                    ]
                    prop.text (levelLabel msgs level)
                    prop.onClick (fun _ -> dispatch (ToggleLevel level))
                ]
        ]
    ]

/// The selected line in full — every field of the stored record as a
/// labelled tree, the rendered exception preserved as preformatted text.
let private recordDetail (msgs: ObservabilityMessages) (record: LogRecord) (dispatch: Msg -> unit) =
    let row (label: string) (value: ReactElement) =
        Html.div [
            prop.className "grid grid-cols-[10rem_1fr] gap-2 py-1 border-b border-border last:border-0"
            prop.children [
                Html.span [ prop.className "text-xs font-semibold text-gray-600"; prop.text label ]
                value
            ]
        ]

    let plain (value: string) =
        Html.span [ prop.className "text-sm text-gray-800 break-all"; prop.text value ]

    let optional (value: string option) = plain (value |> Option.defaultValue "")

    Html.div [
        prop.className "border border-border rounded bg-white p-3"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-semibold text-gray-700 mb-2"
                prop.text msgs.DetailHeading
            ]
            row msgs.ColumnTime (plain (instant record.TimestampUtc))
            row msgs.ColumnLevel (plain (levelLabel msgs record.Level))
            row msgs.ColumnLogger (plain record.Logger)
            row msgs.ColumnMessage (plain record.Message)
            row msgs.ColumnScope (optional record.ScopeId)
            row
                msgs.ColumnCorrelation
                (match record.CorrelationId with
                 | None -> plain ""
                 | Some correlationId ->
                     Html.div [
                         prop.className "flex items-center gap-2"
                         prop.children [
                             plain correlationId
                             Html.button [
                                 prop.className "text-xs text-brand underline"
                                 prop.text msgs.ShowCorrelated
                                 prop.onClick (fun _ -> dispatch (ShowCorrelated correlationId))
                             ]
                         ]
                     ])
            match record.Error with
            | None -> Html.none
            | Some error ->
                row
                    msgs.ColumnError
                    (Html.pre [
                        prop.className "text-xs text-red-700 whitespace-pre-wrap break-all"
                        prop.text error
                    ])
        ]
    ]

let private logGrid (msgs: ObservabilityMessages) (records: LogRecord list) (dispatch: Msg -> unit) =
    // A fixed-height viewport with the default (normal) layout is what
    // lets AG Grid virtualise the rows; `AutoHeight` would render every
    // row of a 500-line page.
    Html.div [
        prop.style [ style.height 420 ]
        prop.children [
            AgGrid.grid [
                AgGrid.theme Theme.themeBalham
                AgGrid.onRowClicked (fun (_: obj) (record: LogRecord) -> dispatch (SelectRecord record))
                AgGrid.columnDefs [
                    ColumnDef.create [
                        ColumnDef.headerName msgs.ColumnTime
                        ColumnDef.valueGetter (fun (r: LogRecord) -> instant r.TimestampUtc)
                    ]
                    ColumnDef.create [
                        ColumnDef.headerName msgs.ColumnLevel
                        ColumnDef.valueGetter (fun (r: LogRecord) -> levelLabel msgs r.Level)
                    ]
                    ColumnDef.create [
                        ColumnDef.headerName msgs.ColumnLogger
                        ColumnDef.valueGetter (fun (r: LogRecord) -> r.Logger)
                    ]
                    ColumnDef.create [
                        ColumnDef.headerName msgs.ColumnMessage
                        ColumnDef.valueGetter (fun (r: LogRecord) -> r.Message)
                    ]
                    ColumnDef.create [
                        ColumnDef.headerName msgs.ColumnCorrelation
                        ColumnDef.valueGetter (fun (r: LogRecord) -> r.CorrelationId |> Option.defaultValue "")
                    ]
                ]
                AgGrid.rowData (List.toArray records)
            ]
        ]
    ]

let private logsTabView (msgs: ObservabilityMessages) (model: Model) (dispatch: Msg -> unit) =
    let body =
        match model.Logs with
        | NotLoaded
        | Loading -> loadingLine msgs
        | LoadError error -> errorBanner (describeError msgs error)
        | Loaded response when List.isEmpty response.Records ->
            Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoLogs ]
        | Loaded response ->
            Html.div [
                prop.className "flex flex-col gap-3"
                prop.children [
                    logGrid msgs response.Records dispatch
                    match model.Selected with
                    | Some record -> recordDetail msgs record dispatch
                    | None -> Html.none
                ]
            ]

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            Html.div [
                prop.key (string model.FilterRevision)
                prop.children [ LogFilterBar msgs model.Filter dispatch ]
            ]
            Html.div [
                prop.className "flex flex-wrap items-center justify-between gap-2"
                prop.children [
                    levelToggles msgs model.Filter dispatch
                    Html.div [
                        prop.className "flex items-center gap-2"
                        prop.children [
                            windowPicker msgs model.Filter.WindowMinutes (SetLogWindow >> dispatch)
                            refreshButton msgs (model.Logs = Loading) (fun () -> dispatch (SubmitFilter model.Filter))
                        ]
                    ]
                ]
            ]
            body
            footnote msgs.LogsFootnote
        ]
    ]

// ─── Metrics tab ─────────────────────────────────────────────────────

/// One chart row. `At` is pre-rendered so the category axis labels stay
/// identical across a re-fetch.
type private ChartRow = { At: string; Value: float }

let private metricChart
    (msgs: ObservabilityMessages)
    (focused: bool)
    (metric: string)
    (state: LoadState<ObservabilityMetricResponse>)
    =
    let body =
        match state with
        | NotLoaded
        | Loading -> loadingLine msgs
        | LoadError error -> errorBanner (describeError msgs error)
        | Loaded response ->
            let partial =
                match response.Warning with
                | Some reason -> warningBanner (msgs.PartialRead reason)
                | None -> Html.none

            let line = ObservabilityReadback.combineSeries metric response.Series

            if List.isEmpty line then
                Html.div [
                    prop.children [
                        partial
                        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoPoints ]
                    ]
                ]
            else
                let rows =
                    line |> List.map (fun (at, value) -> { At = clockTime at; Value = value })

                Html.div [
                    prop.children [
                        partial
                        AgChart.chart [
                            AgChart.options [
                                AgChart.data rows
                                AgChart.tooltip true
                                AgChart.padding 8
                                AgChart.series [
                                    Series.create [ Series.seriesKind Line; Series.xKey "At"; Series.yKey "Value" ]
                                ]
                                AgChart.axes [
                                    Axis.create [ Axis.axisKind AxisKind.Category ]
                                    Axis.create [ Axis.axisKind AxisKind.Number; Axis.gridLine true ]
                                ]
                            ]
                        ]
                        if List.length response.Series > 1 then
                            Html.p [
                                prop.className "mt-1 text-xs text-gray-500"
                                prop.text (msgs.SeriesCount(List.length response.Series))
                            ]
                    ]
                ]

    Html.div [
        prop.key metric
        prop.className [
            "border rounded bg-white p-3"
            if focused then
                "border-brand ring-1 ring-brand"
            else
                "border-border"
        ]
        prop.children [
            Html.h3 [ prop.className "text-sm font-semibold text-gray-700 mb-2"; prop.text metric ]
            body
        ]
    ]

[<Emit("setInterval($0, $1)")>]
let private setInterval (callback: unit -> unit) (milliseconds: int) : int = jsNative

[<Emit("clearInterval($0)")>]
let private clearInterval (handle: int) : unit = jsNative

[<Literal>]
let private AutoRefreshMs = 60000

/// Owns the chart re-read. Mounted only while the metrics tab is, so the
/// timer's lifetime is the tab's — the Datadog readback module's shape.
/// Once a minute: the default flush cadence, so a faster re-read would
/// mostly fetch the same points again.
[<ReactComponent>]
let private MetricsAutoRefresh (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        let handle = setInterval (fun () -> dispatch RefreshMetrics) AutoRefreshMs

        { new IDisposable with
            member _.Dispose() = clearInterval handle
        })

    Html.none

let private metricsTabView (msgs: ObservabilityMessages) (model: Model) (dispatch: Msg -> unit) =
    let anyLoading = model.Metrics |> List.exists (fun (_, state) -> state = Loading)

    let ordered =
        match model.FocusedMetric with
        | None -> model.Metrics
        | Some focus ->
            (model.Metrics |> List.filter (fun (name, _) -> name = focus))
            @ (model.Metrics |> List.filter (fun (name, _) -> name <> focus))

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            MetricsAutoRefresh dispatch
            Html.div [
                prop.className "flex items-center justify-end gap-2"
                prop.children [
                    windowPicker msgs model.MetricsWindow (SetMetricsWindow >> dispatch)
                    refreshButton msgs anyLoading (fun () -> dispatch RefreshMetrics)
                ]
            ]
            Html.div [
                prop.className "grid grid-cols-1 md:grid-cols-2 gap-3"
                prop.children [
                    for metric, state in ordered -> metricChart msgs (model.FocusedMetric = Some metric) metric state
                ]
            ]
            footnote msgs.MetricsFootnote
        ]
    ]

// ─── Alerts tab ──────────────────────────────────────────────────────

let private statePill (msgs: ObservabilityMessages) (state: AlertRuleState) =
    let cls, label =
        match state with
        | AlertRuleState.Firing -> "bg-red-100 text-red-700 border-red-200", msgs.StateFiring
        | AlertRuleState.Pending -> "bg-yellow-100 text-yellow-700 border-yellow-200", msgs.StatePending
        | AlertRuleState.Clear -> "bg-green-100 text-green-700 border-green-200", msgs.StateClear
        | AlertRuleState.NoData -> "bg-gray-100 text-gray-700 border-gray-200", msgs.StateNoData
        | AlertRuleState.NotEvaluated -> "bg-gray-100 text-gray-500 border-gray-200", msgs.StateNotEvaluated

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text label
    ]

let private alertsTabView (msgs: ObservabilityMessages) (model: Model) (dispatch: Msg -> unit) =
    let body =
        match model.Alerts with
        | NotLoaded
        | Loading -> loadingLine msgs
        | LoadError error -> errorBanner (describeError msgs error)
        | Loaded response ->
            Html.table [
                prop.className "min-w-full border border-border rounded bg-white"
                prop.children [
                    Html.thead [
                        Html.tr [
                            prop.className "bg-gray-50 border-b border-border"
                            prop.children [
                                headerCell msgs.ColumnState
                                headerCell msgs.ColumnRule
                                headerCell msgs.ColumnSignal
                                headerCell msgs.ColumnCondition
                                headerCell msgs.ColumnBreachingSince
                                headerCell msgs.ColumnLastFired
                                headerCell msgs.ColumnLastCleared
                                headerCell msgs.ColumnLastEvaluated
                            ]
                        ]
                    ]
                    Html.tbody [
                        if List.isEmpty response.Rules then
                            Html.tr [
                                Html.td [
                                    prop.colSpan 8
                                    prop.className "px-3 py-6 text-center text-sm text-gray-500"
                                    prop.text msgs.NoRules
                                ]
                            ]
                        else
                            for rule in response.Rules do
                                Html.tr [
                                    prop.key rule.Name
                                    prop.className "border-b border-border last:border-0"
                                    prop.children [
                                        Html.td [
                                            prop.className "px-3 py-2 align-top"
                                            prop.children [ statePill msgs rule.Observation.State ]
                                        ]
                                        cell rule.Name
                                        Html.td [
                                            prop.className "px-3 py-2 text-sm text-gray-800 align-top"
                                            prop.children [
                                                Html.div [ prop.text rule.Signal ]
                                                match rule.MetricName with
                                                | Some metric ->
                                                    Html.button [
                                                        prop.className "text-xs text-brand underline"
                                                        prop.text msgs.ViewChart
                                                        prop.onClick (fun _ -> dispatch (ShowMetric metric))
                                                    ]
                                                | None -> Html.none
                                            ]
                                        ]
                                        cell (rule.Condition + " " + msgs.Minutes(int (Math.Round rule.ForMinutes)))
                                        cell (optionalInstant msgs rule.Observation.BreachingSinceUtc)
                                        cell (optionalInstant msgs rule.Observation.LastFiredUtc)
                                        cell (optionalInstant msgs rule.Observation.LastClearedUtc)
                                        cell (optionalInstant msgs rule.Observation.LastEvaluatedUtc)
                                    ]
                                ]
                    ]
                ]
            ]

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            Html.div [
                prop.className "flex justify-end"
                prop.children [
                    refreshButton msgs (model.Alerts = Loading) (fun () -> dispatch RefreshAlerts)
                ]
            ]
            body
            footnote msgs.AlertsFootnote
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

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

let private tabBar
    (msgs: ObservabilityMessages)
    (sources: ObservabilitySources)
    (model: Model)
    (dispatch: Msg -> unit)
    =
    Html.div [
        prop.className "flex gap-1 border-b border-border bg-white px-4"
        prop.children [
            if sources.Logs then
                tabButton msgs.LogsTab (model.ActiveTab = LogsTab) (fun () -> dispatch (SwitchTab LogsTab))
            if sources.Metrics then
                tabButton msgs.MetricsTab (model.ActiveTab = MetricsTab) (fun () -> dispatch (SwitchTab MetricsTab))
            if sources.Alerts then
                tabButton msgs.AlertsTab (model.ActiveTab = AlertsTab) (fun () -> dispatch (SwitchTab AlertsTab))
        ]
    ]

/// The module body as a React COMPONENT, so it has a hook site from which
/// to read the resolved catalog — the reason `HealthMonitorUI` and the
/// Datadog readback module give for the same shape.
[<ReactComponent>]
let private ObservabilityBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).Observability

    let content =
        match model.Sources with
        | NotLoaded
        | Loading -> Html.div [ prop.className "p-4"; prop.children [ loadingLine msgs ] ]
        | LoadError error ->
            Html.div [
                prop.className "p-4"
                prop.children [ errorBanner (describeError msgs error) ]
            ]
        | Loaded sources when not (sources.Logs || sources.Metrics || sources.Alerts) ->
            Html.div [
                prop.className "p-4"
                prop.children [ Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoSources ] ]
            ]
        | Loaded sources ->
            Html.div [
                prop.className "flex flex-col h-full"
                prop.children [
                    tabBar msgs sources model dispatch
                    match model.ActiveTab with
                    | LogsTab -> logsTabView msgs model dispatch
                    | MetricsTab -> metricsTabView msgs model dispatch
                    | AlertsTab -> alertsTabView msgs model dispatch
                ]
            ]

    Html.div [ prop.className "flex flex-col h-full"; prop.children [ content ] ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = ObservabilityBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Create the self-hosted observability admin as an `ErasedModule`. The
/// shell's `prepareModules` injects it when
/// `ClientConfig.Observability = DefaultObservabilityModule`.
///
/// **Gating.** `NavRole.PlatformAdminOnly` — logs, metric history and
/// alert state are deployment-wide data, and the endpoints enforce the
/// same predicate server-side regardless of what the client renders.
/// Grouped with the health monitor and the Datadog readback under
/// `DatadogReadbackUI.ObservabilityGroup`, which is in the platform-admin
/// allow-list, so the module lands in the administration area and stays
/// visible to a team-less platform admin.
let create () : ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "Telemetry"
        Icon = ToolUp.Platform.Icons.usage
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.Observability"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup DatadogReadbackUI.ObservabilityGroup
    |> ToolUp.Platform.ClientModule.withArea ToolUp.Platform.ModuleArea.Administration
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register