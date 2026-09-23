// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module DatadogReadbackUI

open System
open Fable.Core
open Fable.SimpleHttp
open Fable.SimpleJson
open Feliz
open Feliz.AgCharts
open ToolUp.Elmish
open ToolUp.Platform

// ─── Phase 9w — the Datadog readback admin module ────────────────────
//
// Three tabs over the SDK's `/api/observability/datadog/*` endpoints:
// monitor state, recent error logs, and a small grid of charts over the
// four standard SDK metrics. Registered under the `"Observability"`
// sidebar group, which this module COINS — nothing declared it before.
//
// **Plain `Http.request`, not a Remoting proxy.** The server side is
// three read-only REST endpoints rather than a contract record (they are
// also reachable by an operator with `curl`), so the reads go through
// `Fable.SimpleHttp` the way the Phase 61 admin widgets do. Identity and
// CSRF headers are spliced in at send time by `CsrfClient`'s request
// guard, so nothing is threaded through here.
//
// **The degraded state is a first-class render, not an error.** Each
// endpoint answers 200 with an empty payload and a `Warning` when
// Datadog could not be reached — so a rotated key or an outage shows one
// warning line above real, empty tables rather than blanking the module.
// `LoadError` is reserved for the cases where the platform itself did
// not answer: 403, 503, a transport failure, or a body that will not
// parse.

// ─── Model ───────────────────────────────────────────────────────────

/// The three sections. Each loads independently — switching tabs does
/// not refetch the others.
type Tab =
    | MonitorsTab
    | RecentErrorsTab
    | KeyMetricsTab

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    /// The deployment's readback configuration — the window and page
    /// size the panel asks for, carried so they cannot drift from the
    /// ones the endpoints apply.
    Config: DatadogReadbackConfig
    ActiveTab: Tab
    Monitors: LoadState<DatadogMonitorsResponse>
    Logs: LoadState<DatadogLogsResponse>
    /// One entry per charted metric, in declaration order. A list rather
    /// than a map so the chart grid renders in a stable order.
    Metrics: (string * LoadState<DatadogMetricResponse>) list
}

type Msg =
    | SwitchTab of Tab
    | RefreshMonitors
    | MonitorsLoaded of Result<DatadogMonitorsResponse, string>
    | RefreshLogs
    | LogsLoaded of Result<DatadogLogsResponse, string>
    | RefreshMetrics
    | MetricLoaded of string * Result<DatadogMetricResponse, string>

// ─── Fetch ───────────────────────────────────────────────────────────

[<Literal>]
let private RoutePrefix = "/api/observability/datadog"

/// One typed GET. The status mapping is the whole contract with the
/// server half: 200 is data (possibly carrying its own `Warning`), 403
/// and 503 are platform-side answers an operator can act on, and
/// anything else — including a body that will not parse — is reported
/// with its code rather than swallowed.
let inline private fetchJson<'T> (url: string) : Async<Result<'T, string>> = async {
    try
        let! response = Http.request url |> Http.method GET |> Http.send

        match response.statusCode with
        | 200 ->
            try
                return Ok(Json.parseAs<'T> response.responseText)
            with ex ->
                return Error(sprintf "The response could not be read: %s" ex.Message)
        | 403 -> return Error "This view is limited to platform administrators."
        | 503 -> return Error "The Datadog readback companion is not composed in this deployment."
        | code -> return Error(sprintf "The request failed (HTTP %d)." code)
    with ex ->
        return Error(sprintf "The request could not be sent: %s" ex.Message)
}

// `fetchJson` is `inline` and the three call sites below are its only
// ones, each resolving `'T` concretely. Both halves are load-bearing:
// `Json.parseAs<'T>` needs the type at COMPILE time, and Fable erases
// generics at runtime, so a non-inline generic fetcher is not a style
// preference but a compile error — `Cannot get type info of generic
// parameter T`. Passing the inline function itself to `Cmd.OfAsync`
// would erase it again, which is why each command names a concrete
// wrapper rather than `fetchJson<Something>`.

let private fetchMonitors (url: string) : Async<Result<DatadogMonitorsResponse, string>> = fetchJson url

let private fetchLogs (url: string) : Async<Result<DatadogLogsResponse, string>> = fetchJson url

let private fetchMetric (url: string) : Async<Result<DatadogMetricResponse, string>> = fetchJson url

let private loadMonitorsCmd () =
    Cmd.OfAsync.perform fetchMonitors (RoutePrefix + "/monitors") MonitorsLoaded

let private loadLogsCmd (config: DatadogReadbackConfig) =
    let url =
        sprintf "%s/logs?minutes=%d&limit=%d" RoutePrefix config.DefaultWindowMinutes config.LogPageLimit

    Cmd.OfAsync.perform fetchLogs url LogsLoaded

/// `encodeURIComponent` rather than `Uri.EscapeDataString`: the BCL
/// call has no Fable implementation, and the browser's own is what this
/// tier should be using anyway.
[<Emit("encodeURIComponent($0)")>]
let private encodeUriComponent (value: string) : string = jsNative

let private loadMetricCmd (config: DatadogReadbackConfig) (metric: string) =
    let url =
        sprintf "%s/metric?metric=%s&minutes=%d" RoutePrefix (encodeUriComponent metric) config.DefaultWindowMinutes

    Cmd.OfAsync.perform fetchMetric url (fun result -> MetricLoaded(metric, result))

let private loadMetricsCmd (config: DatadogReadbackConfig) =
    DatadogReadback.defaultChartedMetrics
    |> List.map (loadMetricCmd config)
    |> Cmd.batch

// ─── Init / update ───────────────────────────────────────────────────

let init (config: DatadogReadbackConfig) () =
    let model = {
        Config = config
        ActiveTab = MonitorsTab
        Monitors = Loading
        Logs = Loading
        Metrics =
            DatadogReadback.defaultChartedMetrics
            |> List.map (fun metric -> metric, Loading)
    }

    model, Cmd.batch [ loadMonitorsCmd (); loadLogsCmd config; loadMetricsCmd config ]

let update (msg: Msg) (model: Model) =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | RefreshMonitors -> { model with Monitors = Loading }, loadMonitorsCmd ()

    | MonitorsLoaded(Ok response) ->
        {
            model with
                Monitors = Loaded response
        },
        Cmd.none

    | MonitorsLoaded(Error message) ->
        {
            model with
                Monitors = LoadError message
        },
        Cmd.none

    | RefreshLogs -> { model with Logs = Loading }, loadLogsCmd model.Config

    | LogsLoaded(Ok response) -> { model with Logs = Loaded response }, Cmd.none

    | LogsLoaded(Error message) -> { model with Logs = LoadError message }, Cmd.none

    | RefreshMetrics ->
        {
            model with
                Metrics = model.Metrics |> List.map (fun (metric, _) -> metric, Loading)
        },
        loadMetricsCmd model.Config

    | MetricLoaded(metric, result) ->
        let next =
            match result with
            | Ok response -> Loaded response
            | Error message -> LoadError message

        {
            model with
                Metrics =
                    model.Metrics
                    |> List.map (fun (name, state) -> if name = metric then name, next else name, state)
        },
        Cmd.none

// ─── Shared renderers ────────────────────────────────────────────────

let private statusPill (msgs: DatadogReadbackMessages) (status: DatadogMonitorStatus) =
    let cls, label =
        match status with
        | DatadogMonitorStatus.OK -> "bg-green-100 text-green-700 border-green-200", msgs.StatusOk
        | DatadogMonitorStatus.Warn -> "bg-yellow-100 text-yellow-700 border-yellow-200", msgs.StatusWarn
        | DatadogMonitorStatus.Alert -> "bg-red-100 text-red-700 border-red-200", msgs.StatusAlert
        | DatadogMonitorStatus.NoData -> "bg-gray-100 text-gray-700 border-gray-200", msgs.StatusNoData
        | DatadogMonitorStatus.Unknown -> "bg-gray-100 text-gray-700 border-gray-200", msgs.StatusUnknown

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text label
    ]

/// The warning a soft-failed read carries. Rendered ABOVE the (empty)
/// table rather than in place of it, so the panel's shape does not
/// change when Datadog is degraded.
let private warningBanner (msgs: DatadogReadbackMessages) (warning: string option) =
    match warning with
    | None -> Html.none
    | Some reason ->
        Html.div [
            prop.className "p-3 mb-3 bg-yellow-50 border border-yellow-200 rounded text-yellow-800 text-sm"
            prop.text (msgs.DegradedWarning reason)
        ]

let private errorBanner (message: string) =
    Html.div [
        prop.className "p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm"
        prop.text message
    ]

let private emptyRow (colSpan: int) (message: string) =
    Html.tr [
        Html.td [
            prop.colSpan colSpan
            prop.className "px-3 py-6 text-center text-sm text-gray-500"
            prop.text message
        ]
    ]

let private footnote (text: string) =
    Html.p [ prop.className "mt-3 text-xs text-gray-500"; prop.text text ]

let private refreshButton (msgs: DatadogReadbackMessages) (loading: bool) (onClick: unit -> unit) =
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

let private headerCell (label: string) =
    Html.th [
        prop.className "px-3 py-2 text-left text-xs font-semibold text-gray-600 uppercase tracking-wide"
        prop.text label
    ]

let private cell (value: string) =
    Html.td [ prop.className "px-3 py-2 text-sm text-gray-800 align-top"; prop.text value ]

/// `DateTime.MinValue` is what the parser yields for an instant Datadog
/// did not report — rendered as "never" rather than as year 1.
let private instant (msgs: DatadogReadbackMessages) (value: DateTime) =
    if value = DateTime.MinValue then
        msgs.NeverTransitioned
    else
        // Formatted by hand rather than through a .NET format string:
        // `DateTime.ToString(format)` is only partly implemented under
        // Fable, and a date that renders differently in the browser
        // from the way it does under test is the kind of drift no test
        // here would catch.
        sprintf "%04d-%02d-%02d %02d:%02d:%02d" value.Year value.Month value.Day value.Hour value.Minute value.Second

let private clockTime (value: DateTime) =
    sprintf "%02d:%02d" value.Hour value.Minute

let private tagList (tags: string list) = String.concat ", " tags

// ─── Monitors tab ────────────────────────────────────────────────────

let private monitorsTabView (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) =
    let body =
        match model.Monitors with
        | NotLoaded
        | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
        | LoadError message -> errorBanner message
        | Loaded response ->
            Html.div [
                prop.children [
                    warningBanner msgs response.Warning
                    Html.table [
                        prop.className "min-w-full border border-border rounded bg-white"
                        prop.children [
                            Html.thead [
                                Html.tr [
                                    prop.className "bg-gray-50 border-b border-border"
                                    prop.children [
                                        headerCell msgs.ColumnStatus
                                        headerCell msgs.ColumnMonitor
                                        headerCell msgs.ColumnLastTransition
                                        headerCell msgs.ColumnTags
                                    ]
                                ]
                            ]
                            Html.tbody [
                                if List.isEmpty response.Monitors then
                                    emptyRow 4 msgs.NoMonitors
                                else
                                    for monitor in response.Monitors do
                                        Html.tr [
                                            prop.key (string monitor.Id)
                                            prop.className "border-b border-border last:border-0"
                                            prop.children [
                                                Html.td [
                                                    prop.className "px-3 py-2 align-top"
                                                    prop.children [ statusPill msgs monitor.Status ]
                                                ]
                                                cell monitor.Name
                                                cell (instant msgs monitor.LastTransitionUtc)
                                                cell (tagList monitor.Tags)
                                            ]
                                        ]
                            ]
                        ]
                    ]
                    footnote msgs.MonitorsFootnote
                ]
            ]

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            Html.div [
                prop.className "flex justify-end"
                prop.children [
                    refreshButton msgs (model.Monitors = Loading) (fun () -> dispatch RefreshMonitors)
                ]
            ]
            body
        ]
    ]

// ─── Recent errors tab ───────────────────────────────────────────────

let private recentErrorsTabView (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) =
    let body =
        match model.Logs with
        | NotLoaded
        | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
        | LoadError message -> errorBanner message
        | Loaded response ->
            Html.div [
                prop.children [
                    warningBanner msgs response.Warning
                    Html.table [
                        prop.className "min-w-full border border-border rounded bg-white"
                        prop.children [
                            Html.thead [
                                Html.tr [
                                    prop.className "bg-gray-50 border-b border-border"
                                    prop.children [
                                        headerCell msgs.ColumnTime
                                        headerCell msgs.ColumnLevel
                                        headerCell msgs.ColumnService
                                        headerCell msgs.ColumnHost
                                        headerCell msgs.ColumnMessage
                                    ]
                                ]
                            ]
                            Html.tbody [
                                if List.isEmpty response.Events then
                                    emptyRow 5 msgs.NoErrors
                                else
                                    for index, event in List.indexed response.Events do
                                        Html.tr [
                                            prop.key (string index)
                                            prop.className "border-b border-border last:border-0"
                                            prop.children [
                                                cell (instant msgs event.Timestamp)
                                                cell event.Level
                                                cell event.Service
                                                cell event.Host
                                                cell event.Message
                                            ]
                                        ]
                            ]
                        ]
                    ]
                    footnote msgs.ErrorsFootnote
                ]
            ]

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.span [
                        prop.className "text-sm text-gray-600"
                        prop.text (msgs.WindowLabel model.Config.DefaultWindowMinutes)
                    ]
                    refreshButton msgs (model.Logs = Loading) (fun () -> dispatch RefreshLogs)
                ]
            ]
            body
        ]
    ]

// ─── Key metrics tab ─────────────────────────────────────────────────

/// One chart row. `At` is pre-rendered rather than a `DateTime` because
/// the category axis renders the value it is given, and a stable string
/// keeps the axis labels identical across a re-fetch.
type private ChartRow = { At: string; Value: float }

let private metricChart (msgs: DatadogReadbackMessages) (metric: string) (state: LoadState<DatadogMetricResponse>) =
    let body =
        match state with
        | NotLoaded
        | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
        | LoadError message -> errorBanner message
        | Loaded response ->
            if List.isEmpty response.Series.Points then
                Html.div [
                    prop.children [
                        warningBanner msgs response.Warning
                        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoPoints ]
                    ]
                ]
            else
                let rows =
                    response.Series.Points
                    |> List.map (fun (at, value) -> { At = clockTime at; Value = value })

                Html.div [
                    prop.children [
                        warningBanner msgs response.Warning
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
                    ]
                ]

    Html.div [
        prop.key metric
        prop.className "border border-border rounded bg-white p-3"
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
let private AutoRefreshMs = 30000

/// Owns the 30-second chart re-read. A component of its own, mounted
/// only while the metrics tab is rendered, so the timer's lifetime IS
/// the tab's: leaving the tab unmounts it and clears the interval, and
/// a module the operator is not looking at polls nothing. Renders
/// nothing.
[<ReactComponent>]
let private MetricsAutoRefresh (dispatch: Msg -> unit) =
    React.useEffectOnce (fun () ->
        let handle = setInterval (fun () -> dispatch RefreshMetrics) AutoRefreshMs

        { new IDisposable with
            member _.Dispose() = clearInterval handle
        })

    Html.none

let private keyMetricsTabView (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) =
    let anyLoading = model.Metrics |> List.exists (fun (_, state) -> state = Loading)

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            MetricsAutoRefresh dispatch
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.span [
                        prop.className "text-sm text-gray-600"
                        prop.text (msgs.WindowLabel model.Config.DefaultWindowMinutes)
                    ]
                    refreshButton msgs anyLoading (fun () -> dispatch RefreshMetrics)
                ]
            ]
            Html.div [
                prop.className "grid grid-cols-1 md:grid-cols-2 gap-3"
                prop.children [ for metric, state in model.Metrics -> metricChart msgs metric state ]
            ]
            footnote msgs.MetricsFootnote
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

let private tabBar (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex gap-1 border-b border-border bg-white px-4"
        prop.children [
            tabButton msgs.MonitorsTab (model.ActiveTab = MonitorsTab) (fun () -> dispatch (SwitchTab MonitorsTab))
            tabButton msgs.RecentErrorsTab (model.ActiveTab = RecentErrorsTab) (fun () ->
                dispatch (SwitchTab RecentErrorsTab))
            tabButton msgs.KeyMetricsTab (model.ActiveTab = KeyMetricsTab) (fun () ->
                dispatch (SwitchTab KeyMetricsTab))
        ]
    ]

/// The module body as a React COMPONENT rather than a plain render
/// function, so it has a hook site from which to read the resolved
/// catalog — the same reason `HealthMonitorUI.HealthMonitorBody`
/// documents. A module's `view` is invoked inline by the shell's own
/// render, where a hook would join the shell's hook order and break the
/// moment the active module changed.
[<ReactComponent>]
let private DatadogReadbackBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).DatadogReadback

    let content =
        match model.ActiveTab with
        | MonitorsTab -> monitorsTabView msgs model dispatch
        | RecentErrorsTab -> recentErrorsTabView msgs model dispatch
        | KeyMetricsTab -> keyMetricsTabView msgs model dispatch

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ tabBar msgs model dispatch; content ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = DatadogReadbackBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// The sidebar group this module coined at Phase 9w. Since Phase 9x
/// the string itself lives in `SidebarVisibility.ObservabilitySidebarGroup`
/// — the platform-admin group allow-list names it, and that file compiles
/// ahead of every module — and this alias keeps the published name. A
/// sibling observability module reuses it rather than minting a
/// near-miss.
[<Literal>]
let ObservabilityGroup = ToolUp.Platform.SidebarVisibility.ObservabilitySidebarGroup

/// Create the Datadog readback admin as an `ErasedModule`. The shell's
/// `prepareModules` injects it when
/// `ClientConfig.DatadogReadback = EnabledDatadogReadback cfg`.
///
/// **Gating.** `NavRole.PlatformAdminOnly` is the gate (Phase 568's
/// typed field, not the deprecated group-name fallback) — monitor state
/// and error logs are deployment-wide data, and the endpoints enforce
/// the same predicate server-side regardless of what the client renders.
/// `withArea Administration` is declared EXPLICITLY rather than derived:
/// the Phase 567 area derivation reads the GROUP name against a closed
/// set, and when this module coined its group that set did not name it.
/// Phase 9x added the group to the set, so the declaration is now
/// belt-and-braces rather than load-bearing; it stays, because the area
/// is this module's decision and should not hinge on a set elsewhere.
let create (config: DatadogReadbackConfig) : ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init config
        Update = update
        Name = "Datadog"
        Icon = ToolUp.Platform.Icons.health
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.DatadogReadback"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup ObservabilityGroup
    |> ToolUp.Platform.ClientModule.withArea ToolUp.Platform.ModuleArea.Administration
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register