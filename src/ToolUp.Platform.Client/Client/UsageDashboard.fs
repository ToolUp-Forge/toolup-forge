// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module UsageDashboard

open System
open ToolUp.Elmish
open Feliz
open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.Platform.Usage

// ─── Phase 9d — Usage admin module (client) ──────────────────────
//
// Read-only Owner / Admin dashboard. Calls `IUsageQueryApi`
// (`Aggregate` / `Query` / `ExportCsv`) and renders a date-range
// picker, a grouping selector, an aggregated table, and a CSV export
// button. Mirrors `HealthMonitorUI`'s shape — `_sdk.UsageDashboard`
// reserved Id, `Admin` sidebar group.

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    Grouping: UsageGrouping
    From: DateTime
    To: DateTime
    Aggregate: LoadState<UsageAggregateRow list>
    Exporting: bool
}

type Msg =
    | SetGrouping of UsageGrouping
    | SetFrom of DateTime
    | SetTo of DateTime
    | Refresh
    | AggregateLoaded of Result<UsageAggregateRow list, string>
    | ExportCsv
    | ExportComplete of Result<byte[], string>

// ─── API proxy ───────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private usageApi: IUsageQueryApi =
    Api.makeProxy<IUsageQueryApi> (
        routeBuilder = UsageQueryApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Init / update ───────────────────────────────────────────────

let private loadAggregateCmd (grouping: UsageGrouping) =
    Cmd.OfRemoting.call usageApi.Aggregate grouping (fun rows -> AggregateLoaded(Ok rows)) (fun e ->
        AggregateLoaded(Error e.Message))

let init () =
    let now = DateTime.UtcNow
    let monthStart = DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)

    let model = {
        Grouping = ByResourceKind
        From = monthStart
        To = now
        Aggregate = Loading
        Exporting = false
    }

    model, loadAggregateCmd model.Grouping

let private downloadCsv (bytes: byte[]) =
    // Build a Blob, attach an <a download>, click it, revoke the URL.
    let blobObj: obj =
        emitJsExpr (bytes) "new Blob([new Uint8Array($0)], { type: 'text/csv;charset=utf-8' })"

    let url: string = emitJsExpr blobObj "URL.createObjectURL($0)"
    let anchor = document.createElement "a" :?> HTMLAnchorElement
    anchor.href <- url

    anchor?download <- sprintf "usage-%s.csv" (DateTime.UtcNow.ToString "yyyy-MM-dd")

    document.body.appendChild anchor |> ignore
    anchor.click ()
    document.body.removeChild anchor |> ignore
    emitJsExpr<unit> url "URL.revokeObjectURL($0)"

let update msg model =
    match msg with
    | SetGrouping g ->
        let m = {
            model with
                Grouping = g
                Aggregate = Loading
        }

        m, loadAggregateCmd g

    | SetFrom from -> { model with From = from }, Cmd.none

    | SetTo toDt -> { model with To = toDt }, Cmd.none

    | Refresh -> { model with Aggregate = Loading }, loadAggregateCmd model.Grouping

    | AggregateLoaded(Ok rows) -> { model with Aggregate = Loaded rows }, Cmd.none

    | AggregateLoaded(Error err) -> { model with Aggregate = LoadError err }, Cmd.none

    | ExportCsv ->
        let range = Some { From = model.From; To = model.To }

        let cmd =
            Cmd.OfRemoting.call usageApi.ExportCsv range (fun bytes -> ExportComplete(Ok bytes)) (fun e ->
                ExportComplete(Error e.Message))

        { model with Exporting = true }, cmd

    | ExportComplete(Ok bytes) ->
        downloadCsv bytes
        { model with Exporting = false }, Cmd.none

    | ExportComplete(Error _) -> { model with Exporting = false }, Cmd.none

// ─── View ────────────────────────────────────────────────────────

let private groupingLabel =
    function
    | ByDay -> "By day"
    | ByMonth -> "By month"
    | ByResourceKind -> "By resource kind"
    | ByUser -> "By user"

let private allGroupings = [ ByDay; ByMonth; ByResourceKind; ByUser ]

let private formatQuantity (q: decimal) : string = if q = 0M then "0" else q.ToString "N2"

let private renderRow (row: UsageAggregateRow) =
    Html.tr [
        Html.td [
            prop.className "px-3 py-2 text-sm text-gray-700 font-mono"
            prop.text row.Bucket
        ]
        Html.td [
            prop.className "px-3 py-2 text-sm text-gray-700 text-right font-mono"
            prop.text (formatQuantity row.Quantity)
        ]
    ]

let private renderTable (rows: UsageAggregateRow list) =
    if List.isEmpty rows then
        Html.div [
            prop.className "p-8 text-center text-sm text-gray-500"
            prop.text
                "No usage records for this scope. Records appear after the first metered AI call, file upload, or ingestion run."
        ]
    else
        Html.table [
            prop.className "min-w-full divide-y divide-gray-200 border border-gray-200 rounded"
            prop.children [
                Html.thead [
                    prop.className "bg-gray-50"
                    prop.children [
                        Html.tr [
                            Html.th [
                                prop.className
                                    "px-3 py-2 text-left text-xs font-semibold text-gray-700 uppercase tracking-wider"
                                prop.text "Bucket"
                            ]
                            Html.th [
                                prop.className
                                    "px-3 py-2 text-right text-xs font-semibold text-gray-700 uppercase tracking-wider"
                                prop.text "Quantity"
                            ]
                        ]
                    ]
                ]
                Html.tbody [
                    prop.className "bg-white divide-y divide-gray-200"
                    prop.children (rows |> List.map renderRow)
                ]
            ]
        ]

let private renderControls (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex flex-wrap items-end gap-4 mb-4"
        prop.children [
            Html.div [
                prop.className "flex flex-col"
                prop.children [
                    Html.label [ prop.className "text-xs text-gray-600 mb-1"; prop.text "Group by" ]
                    Html.select [
                        prop.className "border border-gray-300 rounded px-2 py-1 text-sm"
                        prop.value (groupingLabel model.Grouping)
                        prop.onChange (fun (v: string) ->
                            allGroupings
                            |> List.tryFind (fun g -> groupingLabel g = v)
                            |> Option.iter (SetGrouping >> dispatch))
                        prop.children (
                            allGroupings
                            |> List.map (fun g ->
                                Html.option [ prop.value (groupingLabel g); prop.text (groupingLabel g) ])
                        )
                    ]
                ]
            ]
            Html.button [
                prop.className "px-3 py-1 text-sm bg-gray-100 hover:bg-gray-200 border border-gray-300 rounded"
                prop.onClick (fun _ -> dispatch Refresh)
                prop.text "Refresh"
            ]
            Html.button [
                prop.className "px-3 py-1 text-sm bg-blue-600 hover:bg-blue-700 text-white rounded disabled:opacity-50"
                prop.disabled model.Exporting
                prop.onClick (fun _ -> dispatch ExportCsv)
                prop.text (if model.Exporting then "Exporting…" else "Export CSV")
            ]
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "p-4"
        prop.children [
            Html.h2 [ prop.className "text-lg font-semibold text-gray-800 mb-2"; prop.text "Usage" ]
            Html.p [
                prop.className "text-sm text-gray-600 mb-4"
                prop.text
                    "Per-team consumption — AI tokens, storage bytes, ingestion rows, request counts. Owner / Admin only."
            ]
            renderControls model dispatch

            match model.Aggregate with
            | NotLoaded -> Html.div [ prop.className "text-sm text-gray-500"; prop.text "Click Refresh." ]
            | Loading -> Html.div [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
            | Loaded rows -> renderTable rows
            | LoadError err ->
                Html.div [
                    prop.className "p-3 bg-red-50 border border-red-200 rounded text-sm text-red-700"
                    prop.text err
                ]
        ]
    ]

// ─── Module creation ─────────────────────────────────────────────

/// Create the built-in usage dashboard admin as an `ErasedModule`.
/// The shell's `prepareModules` injects this in any non-Anonymous
/// mode unless `UsageDashboard = NoUsageDashboard`. The server-side
/// handler enforces the Owner/Admin gate independently.
let create (config: UsageDashboardConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Usage"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.usage

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.UsageDashboard"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register