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
open Toolup.UIToolkit

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
// `Remote.Failed` is reserved for the cases where the platform itself did
// not answer: 403, 503, a transport failure, or a body that will not
// parse.

// ─── Model ───────────────────────────────────────────────────────────

/// The three sections. Each loads independently — switching tabs does
/// not refetch the others.
type Tab =
    | MonitorsTab
    | RecentErrorsTab
    | KeyMetricsTab

type Model = {
    /// The deployment's readback configuration — the window and page
    /// size the panel asks for, carried so they cannot drift from the
    /// ones the endpoints apply.
    Config: DatadogReadbackConfig
    ActiveTab: Tab
    Monitors: Remote<DatadogMonitorsResponse, string>
    Logs: Remote<DatadogLogsResponse, string>
    /// One entry per charted metric, in declaration order. A list rather
    /// than a map so the chart grid renders in a stable order.
    Metrics: (string * Remote<DatadogMetricResponse, string>) list
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
        Monitors = Remote.Loading
        Logs = Remote.Loading
        Metrics =
            DatadogReadback.defaultChartedMetrics
            |> List.map (fun metric -> metric, Remote.Loading)
    }

    model, Cmd.batch [ loadMonitorsCmd (); loadLogsCmd config; loadMetricsCmd config ]

let update (msg: Msg) (model: Model) =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | RefreshMonitors -> { model with Monitors = Remote.Loading }, loadMonitorsCmd ()

    | MonitorsLoaded result ->
        {
            model with
                Monitors = Remote.ofResult result
        },
        Cmd.none

    | RefreshLogs -> { model with Logs = Remote.Loading }, loadLogsCmd model.Config

    | LogsLoaded result ->
        {
            model with
                Logs = Remote.ofResult result
        },
        Cmd.none

    | RefreshMetrics ->
        {
            model with
                Metrics = model.Metrics |> List.map (fun (metric, _) -> metric, Remote.Loading)
        },
        loadMetricsCmd model.Config

    | MetricLoaded(metric, result) ->
        let next = Remote.ofResult result

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
        | DatadogMonitorStatus.NoData -> ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary), msgs.StatusNoData
        | DatadogMonitorStatus.Unknown -> ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary), msgs.StatusUnknown

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

let private emptyState (message: string) =
    Html.p [
        prop.className [ "px-3 py-6 text-center text-sm"; Tokens.Text.muted ]
        prop.text message
    ]

let private footnote (text: string) =
    Html.p [ prop.className ("mt-3 text-xs " + Tokens.Text.muted); prop.text text ]

let private refreshButton (msgs: DatadogReadbackMessages) (loading: bool) (onClick: unit -> unit) =
    Forms.Button.secondaryWith (Forms.Button.stateOf loading msgs.Refreshing false) msgs.Refresh onClick

/// A plain-text column.
let private column (header: string) (value: 'row -> string) : Data.Column<'row> = {
    Header = header
    Align = Data.Left
    Cell = fun row -> Html.text (value row)
}

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
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
        | Remote.Failed message -> StateViews.noticeStatic (Notice.Problem message)
        | Remote.Loaded response ->
            Html.div [
                prop.children [
                    warningBanner msgs response.Warning
                    if List.isEmpty response.Monitors then
                        emptyState msgs.NoMonitors
                    else
                        Data.table
                            [
                                {
                                    Header = msgs.ColumnStatus
                                    Align = Data.Left
                                    Cell = fun (monitor: DatadogMonitorState) -> statusPill msgs monitor.Status
                                }
                                column msgs.ColumnMonitor _.Name
                                column msgs.ColumnLastTransition (fun monitor -> instant msgs monitor.LastTransitionUtc)
                                column msgs.ColumnTags (fun monitor -> tagList monitor.Tags)
                            ]
                            response.Monitors
                    footnote msgs.MonitorsFootnote
                ]
            ]

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            Html.div [
                prop.className "flex justify-end"
                prop.children [
                    refreshButton msgs (Remote.isLoading model.Monitors) (fun () -> dispatch RefreshMonitors)
                ]
            ]
            body
        ]
    ]

// ─── Recent errors tab ───────────────────────────────────────────────

let private recentErrorsTabView (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) =
    let body =
        match model.Logs with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
        | Remote.Failed message -> StateViews.noticeStatic (Notice.Problem message)
        | Remote.Loaded response ->
            Html.div [
                prop.children [
                    warningBanner msgs response.Warning
                    if List.isEmpty response.Events then
                        emptyState msgs.NoErrors
                    else
                        Data.table
                            [
                                column msgs.ColumnTime (fun (event: DatadogLogEvent) -> instant msgs event.Timestamp)
                                column msgs.ColumnLevel _.Level
                                column msgs.ColumnService _.Service
                                column msgs.ColumnHost _.Host
                                column msgs.ColumnMessage _.Message
                            ]
                            response.Events
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
                        prop.className ("text-sm " + Tokens.Text.secondary)
                        prop.text (msgs.WindowLabel model.Config.DefaultWindowMinutes)
                    ]
                    refreshButton msgs (Remote.isLoading model.Logs) (fun () -> dispatch RefreshLogs)
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

let private metricChart
    (msgs: DatadogReadbackMessages)
    (metric: string)
    (state: Remote<DatadogMetricResponse, string>)
    =
    let body =
        match state with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
        | Remote.Failed message -> StateViews.noticeStatic (Notice.Problem message)
        | Remote.Loaded response ->
            if List.isEmpty response.Series.Points then
                Html.div [
                    prop.children [
                        warningBanner msgs response.Warning
                        Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.NoPoints ]
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
            Html.h3 [
                prop.className ("text-sm font-semibold mb-2 " + Tokens.Text.secondary)
                prop.text metric
            ]
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
    let anyLoading =
        model.Metrics |> List.exists (fun (_, state) -> Remote.isLoading state)

    Html.div [
        prop.className "p-4 flex flex-col gap-3"
        prop.children [
            MetricsAutoRefresh dispatch
            Html.div [
                prop.className "flex items-center justify-between"
                prop.children [
                    Html.span [
                        prop.className ("text-sm " + Tokens.Text.secondary)
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

let private tabBar (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className [ "px-4"; Tokens.Bg.card ]
        prop.children [
            Tabs.bar
                [
                    MonitorsTab, msgs.MonitorsTab
                    RecentErrorsTab, msgs.RecentErrorsTab
                    KeyMetricsTab, msgs.KeyMetricsTab
                ]
                model.ActiveTab
                (SwitchTab >> dispatch)
        ]
    ]

/// The module's page, over the catalog section the view hands it.
let private body (msgs: DatadogReadbackMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let content =
        match model.ActiveTab with
        | MonitorsTab -> monitorsTabView msgs model dispatch
        | RecentErrorsTab -> recentErrorsTabView msgs model dispatch
        | KeyMetricsTab -> keyMetricsTabView msgs model dispatch

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ tabBar msgs model dispatch; content ]
    ]

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(body ctx.Messages.DatadogReadback model ctx.Dispatch)

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
/// `ClientConfig.Chrome.DatadogReadback = EnabledDatadogReadback cfg`.
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
/// This built-in's module id.
let moduleId = "_sdk.DatadogReadback"

let builtIn (config: DatadogReadbackConfig) : BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Datadog"
        Icon = ToolUp.Platform.Icons.health
    }
    Group = Some ObservabilityGroup
    NavRole = Some ToolUp.Platform.NavRole.PlatformAdminOnly
    Area = ToolUp.Platform.ModuleArea.Administration
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init config ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}