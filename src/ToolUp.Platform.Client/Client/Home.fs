// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Home

open ToolUp.Elmish
open Feliz
open ToolUp.Platform
open ProcessedDataTypes

// ─── Phase 171 — Home / Overview landing module (client) ─────────
//
// Optional SDK-built-in landing surface. When a deployment opts in
// via `ClientConfig.HomeModule = EnabledHomeModule`, the shell injects
// this module at the head of the sidebar and lands on it by default
// (unless `ActiveModule` names a specific module). It calls
// `IHomeOverviewApi.GetOverview` and renders, scope-correct + RBAC-
// filtered server-side: one card per data-producing tool (with its
// per-type record counts + a thoughtful empty state), an "Active AI"
// card, and a deployment-context strip. Each tool card navigates to
// its module via `NavigationRequest.request` (Phase 6g.C) — no
// dependency on the shell's internal `Msg` type. Mirrors
// `UsageDashboard`'s shape: `_sdk.home` reserved Id.

type LoadState<'T> =
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = { Overview: LoadState<HomeOverview> }

type Msg =
    | Refresh
    | OverviewLoaded of Result<HomeOverview, string>

// ─── API proxy ───────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// UserSession.fs + SDK.Client.fs installRequestGuard.
let private homeApi: IHomeOverviewApi =
    Api.makeProxy<IHomeOverviewApi> (
        routeBuilder = HomeOverviewApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

// ─── Init / update ───────────────────────────────────────────────

let private loadOverviewCmd =
    Cmd.OfRemoting.call homeApi.GetOverview () (fun ov -> OverviewLoaded(Ok ov)) (fun e ->
        OverviewLoaded(Error e.Message))

let init () = { Overview = Loading }, loadOverviewCmd

let update msg model =
    match msg with
    | Refresh -> { model with Overview = Loading }, loadOverviewCmd
    | OverviewLoaded(Ok ov) -> { model with Overview = Loaded ov }, Cmd.none
    | OverviewLoaded(Error err) -> { model with Overview = LoadError err }, Cmd.none

// ─── View ────────────────────────────────────────────────────────

let private formatCount (n: int) : string = n.ToString "N0"

/// One tool card. Clickable when the producing module is reachable as
/// a sidebar id — navigation goes through the shell's NavigationRequest
/// hook, the same path a sidebar click takes.
let private renderToolCard (tool: ToolSummary) =
    Html.button [
        prop.className
            "text-left p-4 bg-white border border-gray-200 rounded-lg hover:border-blue-400 hover:shadow-sm transition w-full"
        prop.onClick (fun _ -> NavigationRequest.request tool.ModuleId)
        prop.children [
            Html.div [
                prop.className "flex items-baseline justify-between mb-2"
                prop.children [
                    Html.span [ prop.className "text-sm font-semibold text-gray-800"; prop.text tool.Name ]
                    Html.span [
                        prop.className "text-lg font-bold text-blue-600 font-mono"
                        prop.text (formatCount tool.TotalRecords)
                    ]
                ]
            ]
            if List.isEmpty tool.DataCounts || tool.TotalRecords = 0 then
                Html.p [
                    prop.className "text-xs text-gray-400"
                    prop.text "No data yet — open this tool to add some."
                ]
            else
                Html.div [
                    prop.className "space-y-1"
                    prop.children (
                        tool.DataCounts
                        |> List.map (fun dc ->
                            Html.div [
                                prop.className "flex items-center justify-between text-xs text-gray-600"
                                prop.children [
                                    Html.span [ prop.text dc.DisplayName ]
                                    Html.span [
                                        prop.className "font-mono text-gray-500"
                                        prop.text (formatCount dc.Count)
                                    ]
                                ]
                            ])
                    )
                ]
        ]
    ]

let private renderActiveAi (ai: ActiveAiSummary option) =
    Html.div [
        prop.className "p-4 bg-white border border-gray-200 rounded-lg"
        prop.children [
            Html.div [
                prop.className "text-xs uppercase tracking-wider text-gray-500 mb-1"
                prop.text "Active AI"
            ]
            match ai with
            | Some a ->
                Html.div [
                    prop.children [
                        Html.div [
                            prop.className "text-sm font-semibold text-gray-800"
                            prop.text a.ProviderLabel
                        ]
                        match a.Model with
                        | Some m -> Html.div [ prop.className "text-xs text-gray-500 font-mono"; prop.text m ]
                        | None -> Html.none
                    ]
                ]
            | None ->
                Html.div [
                    prop.className "text-sm text-gray-400"
                    prop.text "No AI provider configured."
                ]
        ]
    ]

let private renderContext (ctx: DeploymentContext) =
    Html.div [
        prop.className "flex flex-wrap items-center gap-x-6 gap-y-1 text-xs text-gray-500"
        prop.children [
            Html.span [ prop.text (sprintf "Mode: %s" ctx.Mode) ]
            match ctx.Health with
            | Some h -> Html.span [ prop.text (sprintf "Health: %s" h) ]
            | None -> Html.none
        ]
    ]

/// Tool cards, with each tool's per-type catalog counts augmented by the
/// client-side processed-data context (populated by the boot prefetch). A
/// ReactComponent so `React.useContext` re-renders the grid the instant the
/// background data fetch lands — so the home reflects loaded data without a
/// Data Manager visit, even though the server overview's catalog count is 0.
[<ReactComponent>]
let private ToolsGrid (tools: ToolSummary list) =
    let processed = React.useContext ProcessedDataContext.Context

    let fileCountFor typeId =
        processed
        |> List.filter (fun e -> e.DataType = typeId && e.Error.IsNone)
        |> List.length

    let augmented =
        tools
        |> List.map (fun tool ->
            let counts =
                tool.DataCounts
                |> List.map (fun dc -> {
                    dc with
                        Count = max dc.Count (fileCountFor dc.TypeId)
                })

            {
                tool with
                    DataCounts = counts
                    TotalRecords = counts |> List.sumBy _.Count
            })

    Html.div [
        prop.className "grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4"
        prop.children (augmented |> List.map renderToolCard)
    ]

let private renderOverview (ov: HomeOverview) =
    Html.div [
        prop.className "space-y-6"
        prop.children [
            // Active AI + deployment context strip.
            Html.div [
                prop.className "grid grid-cols-1 md:grid-cols-3 gap-4 items-stretch"
                prop.children [
                    Html.div [ prop.className "md:col-span-1"; prop.children [ renderActiveAi ov.ActiveAi ] ]
                    Html.div [
                        prop.className "md:col-span-2 flex items-end"
                        prop.children [ renderContext ov.Deployment ]
                    ]
                ]
            ]

            // Tools grid.
            Html.div [
                prop.children [
                    Html.h3 [
                        prop.className "text-sm font-semibold text-gray-700 mb-3"
                        prop.text "Your tools"
                    ]
                    if List.isEmpty ov.Tools then
                        Html.div [
                            prop.className
                                "p-8 text-center text-sm text-gray-500 border border-dashed border-gray-200 rounded"
                            prop.text "No data-producing tools are registered in this deployment yet."
                        ]
                    else
                        ToolsGrid ov.Tools
                ]
            ]
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "p-4"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold text-gray-800"; prop.text "Home" ]
                            Html.p [
                                prop.className "text-sm text-gray-600"
                                prop.text "An overview of your tools, the data in each, and the active AI."
                            ]
                        ]
                    ]
                    Html.button [
                        prop.className "px-3 py-1 text-sm bg-gray-100 hover:bg-gray-200 border border-gray-300 rounded"
                        prop.onClick (fun _ -> dispatch Refresh)
                        prop.text "Refresh"
                    ]
                ]
            ]

            match model.Overview with
            | Loading -> Html.div [ prop.className "text-sm text-gray-500"; prop.text "Loading…" ]
            | Loaded ov -> renderOverview ov
            | LoadError err ->
                Html.div [
                    prop.className "p-3 bg-red-50 border border-red-200 rounded text-sm text-red-700"
                    prop.text err
                ]
        ]
    ]

// ─── Module creation ─────────────────────────────────────────────

/// Create the built-in Home / Overview landing module as an
/// `ErasedModule`. The shell's `prepareModules` injects this at the
/// head of the sidebar when `ClientConfig.HomeModule` is
/// `EnabledHomeModule` / `ConfiguredHomeModule`, and lands on it by
/// default (unless `ActiveModule` names another module). Ungrouped so
/// it renders as the leading sidebar entry.
let create (config: HomeModuleConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Home"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.home

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.home"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.register