// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ServiceStatusBoardUI

open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Phase 9p.A — ServiceStatusBoard admin module ────────────────────
//
// Single-page admin module that aggregates every operator-facing
// observability surface (Health, Preflight, Drift, RateLimit, JobQueue,
// SmokeTest) into one composite snapshot. Mirrors `HealthMonitorUI`'s
// patterns (proxy built per call, status pill renderer, refresh button)
// but renders six collapsible per-section panels under one top-line
// `OverallStatus` pill.

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

/// Set of section names whose status flipped between the prior and
/// most-recent snapshot — surfaced via Tailwind `animate-pulse` for
/// ~1.5s so operators spot freshly-changed sections without re-reading
/// every panel. Cleared on the next refresh.
type Model = {
    Snapshot: LoadState<ServiceStatusSnapshot>
    /// Sections the user has collapsed. Default empty (all expanded);
    /// the user clicks the header to toggle.
    Collapsed: Set<string>
    /// Per-section in-flight refresh tracker — used to disable the
    /// per-section refresh button while its targeted call is pending.
    Refreshing: Set<string>
    RecentlyFlipped: Set<string>
}

type Msg =
    | RefreshAll
    | SnapshotLoaded of Result<ServiceStatusSnapshot, string>
    | ToggleCollapsed of section: string
    | RefreshSection of section: string
    | SectionLoaded of section: string * Result<SectionSummary, string>

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
let private boardApi: IServiceStatusBoardApi =
    Api.makeProxy<IServiceStatusBoardApi> (customOptions = UserSession.withRequestHeaders)

let private loadSnapshotCmd () =
    Cmd.OfRemoting.call boardApi.GetSnapshot () SnapshotLoaded (fun e -> SnapshotLoaded(Error e.Message))

let private loadSectionCmd (section: string) =
    let api = boardApi

    let work: Async<Result<SectionSummary, string>> =
        match section with
        | s when s = ServiceStatusSnapshot.HealthSection -> api.RefreshHealth()
        | s when s = ServiceStatusSnapshot.PreflightSection -> api.RefreshPreflight()
        | s when s = ServiceStatusSnapshot.DriftSection -> api.RefreshDrift()
        | s when s = ServiceStatusSnapshot.RateLimitSection -> api.RefreshRateLimit()
        | s when s = ServiceStatusSnapshot.JobQueueSection -> api.RefreshJobQueue()
        | s when s = ServiceStatusSnapshot.SmokeTestSection -> api.RefreshSmokeTest()
        | other -> async { return Error(sprintf "unknown section: %s" other) }

    Cmd.OfRemoting.call (fun () -> work) () (fun result -> SectionLoaded(section, result)) (fun e ->
        SectionLoaded(section, Error e.Message))

// ─── Init ────────────────────────────────────────────────────────────

let init () =
    let model = {
        Snapshot = Loading
        Collapsed = Set.empty
        Refreshing = Set.empty
        RecentlyFlipped = Set.empty
    }

    model, loadSnapshotCmd ()

// ─── Helpers ─────────────────────────────────────────────────────────

let private currentSnapshot (model: Model) =
    match model.Snapshot with
    | Loaded snap -> Some snap
    | _ -> None

let private sectionOf (snap: ServiceStatusSnapshot) (name: string) : SectionSummary =
    match name with
    | s when s = ServiceStatusSnapshot.HealthSection -> snap.Health
    | s when s = ServiceStatusSnapshot.PreflightSection -> snap.Preflight
    | s when s = ServiceStatusSnapshot.DriftSection -> snap.Drift
    | s when s = ServiceStatusSnapshot.RateLimitSection -> snap.RateLimit
    | s when s = ServiceStatusSnapshot.JobQueueSection -> snap.JobQueue
    | s when s = ServiceStatusSnapshot.SmokeTestSection -> snap.SmokeTest
    | _ -> SectionSummary.failure (sprintf "Unknown section: %s" name) "Client-side section mapping is incomplete."

let private updateSection
    (snap: ServiceStatusSnapshot)
    (name: string)
    (summary: SectionSummary)
    : ServiceStatusSnapshot =
    match name with
    | s when s = ServiceStatusSnapshot.HealthSection -> { snap with Health = summary }
    | s when s = ServiceStatusSnapshot.PreflightSection -> { snap with Preflight = summary }
    | s when s = ServiceStatusSnapshot.DriftSection -> { snap with Drift = summary }
    | s when s = ServiceStatusSnapshot.RateLimitSection -> { snap with RateLimit = summary }
    | s when s = ServiceStatusSnapshot.JobQueueSection -> { snap with JobQueue = summary }
    | s when s = ServiceStatusSnapshot.SmokeTestSection -> { snap with SmokeTest = summary }
    | _ -> snap

/// Compute the set of section names whose severity / disabled state
/// flipped between the prior and new snapshot. Used to drive the
/// pulse-highlight on freshly-changed panels.
let private flippedSections (prev: ServiceStatusSnapshot option) (next: ServiceStatusSnapshot) : Set<string> =
    match prev with
    | None -> Set.empty
    | Some p ->
        let allNames = [
            ServiceStatusSnapshot.HealthSection
            ServiceStatusSnapshot.PreflightSection
            ServiceStatusSnapshot.DriftSection
            ServiceStatusSnapshot.RateLimitSection
            ServiceStatusSnapshot.JobQueueSection
            ServiceStatusSnapshot.SmokeTestSection
        ]

        allNames
        |> List.choose (fun name ->
            let prevSec = sectionOf p name
            let nextSec = sectionOf next name

            if prevSec.Disabled <> nextSec.Disabled || prevSec.Severity <> nextSec.Severity then
                Some name
            else
                None)
        |> Set.ofList

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | RefreshAll -> { model with Snapshot = Loading }, loadSnapshotCmd ()

    | SnapshotLoaded(Ok snap) ->
        let flipped = flippedSections (currentSnapshot model) snap

        {
            model with
                Snapshot = Loaded snap
                Refreshing = Set.empty
                RecentlyFlipped = flipped
        },
        Cmd.none

    | SnapshotLoaded(Error e) ->
        {
            model with
                Snapshot = LoadError e
                Refreshing = Set.empty
        },
        Cmd.none

    | ToggleCollapsed section ->
        let next =
            if Set.contains section model.Collapsed then
                Set.remove section model.Collapsed
            else
                Set.add section model.Collapsed

        { model with Collapsed = next }, Cmd.none

    | RefreshSection section ->
        {
            model with
                Refreshing = Set.add section model.Refreshing
        },
        loadSectionCmd section

    | SectionLoaded(section, Ok summary) ->
        let nextRefreshing = Set.remove section model.Refreshing

        match currentSnapshot model with
        | Some snap ->
            let updated = updateSection snap section summary

            let overall =
                ServiceStatusSnapshot.computeOverall
                    updated.Health
                    updated.Preflight
                    updated.Drift
                    updated.RateLimit
                    updated.JobQueue
                    updated.SmokeTest

            let withOverall = { updated with Overall = overall }

            let priorSec = sectionOf snap section

            let recentlyFlipped =
                if priorSec.Severity <> summary.Severity || priorSec.Disabled <> summary.Disabled then
                    Set.add section (Set.remove section model.RecentlyFlipped)
                else
                    model.RecentlyFlipped

            {
                model with
                    Snapshot = Loaded withOverall
                    Refreshing = nextRefreshing
                    RecentlyFlipped = recentlyFlipped
            },
            Cmd.none
        | None ->
            // Per-section refresh hit before the composite snapshot
            // landed. Drop the result and let the composite load
            // populate every section.
            {
                model with
                    Refreshing = nextRefreshing
            },
            Cmd.none

    | SectionLoaded(section, Error e) ->
        let failureSummary = SectionSummary.failure (sprintf "%s refresh failed." section) e

        let nextRefreshing = Set.remove section model.Refreshing

        match currentSnapshot model with
        | Some snap ->
            let updated = updateSection snap section failureSummary

            let overall =
                ServiceStatusSnapshot.computeOverall
                    updated.Health
                    updated.Preflight
                    updated.Drift
                    updated.RateLimit
                    updated.JobQueue
                    updated.SmokeTest

            {
                model with
                    Snapshot = Loaded { updated with Overall = overall }
                    Refreshing = nextRefreshing
                    RecentlyFlipped = Set.add section model.RecentlyFlipped
            },
            Cmd.none
        | None ->
            {
                model with
                    Refreshing = nextRefreshing
            },
            Cmd.none

// ─── Renderers ───────────────────────────────────────────────────────

let private severityPill (summary: SectionSummary) =
    let label, cls =
        if summary.Disabled then
            "Disabled", "bg-gray-100 text-gray-600 border-gray-200"
        else
            match summary.Severity with
            | StatusSeverity.Ok -> "Ok", "bg-green-100 text-green-700 border-green-200"
            | StatusSeverity.Warn -> "Warn", "bg-yellow-100 text-yellow-700 border-yellow-200"
            | StatusSeverity.Error -> "Error", "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text label
    ]

let private overallPill (overall: OverallStatus) =
    let label, cls =
        match overall with
        | AllOk -> "All systems Ok", "bg-green-100 text-green-700 border-green-200"
        | DegradedBy sections ->
            let joined = sections |> String.concat ", "
            sprintf "Degraded — %s" joined, "bg-yellow-100 text-yellow-700 border-yellow-200"
        | UnhealthyBy sections ->
            let joined = sections |> String.concat ", "
            sprintf "Unhealthy — %s" joined, "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block text-sm px-3 py-1 rounded border font-semibold {cls}"
        prop.text label
    ]

let private refreshButton (label: string) (loading: bool) (onClick: unit -> unit) =
    Html.button [
        prop.className [
            "px-3 py-1.5 text-sm font-medium rounded border transition-colors"
            if loading then
                "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
            else
                "bg-white text-gray-700 border-border hover:bg-gray-50"
        ]
        prop.disabled loading
        prop.text (if loading then "Refreshing..." else label)
        prop.onClick (fun _ -> onClick ())
    ]

let private errorBanner (msg: string) =
    Html.div [
        prop.className "p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm"
        prop.text msg
    ]

let private sectionPanel
    (model: Model)
    (dispatch: Msg -> unit)
    (section: string)
    (summary: SectionSummary)
    : ReactElement =
    let isCollapsed = Set.contains section model.Collapsed
    let isRefreshing = Set.contains section model.Refreshing
    let isFlipped = Set.contains section model.RecentlyFlipped

    Html.div [
        prop.className [
            "border border-border rounded-lg overflow-hidden bg-white"
            if isFlipped then
                "animate-pulse"
        ]
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between gap-3 px-4 py-3 bg-gray-50 border-b border-border"
                prop.children [
                    Html.button [
                        prop.className "flex items-center gap-2 text-left flex-1"
                        prop.onClick (fun _ -> dispatch (ToggleCollapsed section))
                        prop.children [
                            Html.span [
                                prop.className "text-xs text-gray-400 font-mono w-4"
                                prop.text (if isCollapsed then "▸" else "▾")
                            ]
                            Html.span [ prop.className "text-sm font-semibold text-gray-800"; prop.text section ]
                            severityPill summary
                        ]
                    ]
                    refreshButton "Refresh" isRefreshing (fun () -> dispatch (RefreshSection section))
                ]
            ]
            if not isCollapsed then
                Html.div [
                    prop.className "px-4 py-3"
                    prop.children [
                        Html.p [
                            prop.className "text-sm text-gray-700"
                            prop.text (
                                if summary.Disabled then
                                    summary.DisabledReason
                                else
                                    summary.Headline
                            )
                        ]
                        if not summary.Details.IsEmpty then
                            Html.ul [
                                prop.className "mt-2 text-xs text-gray-600 list-disc pl-5 space-y-1"
                                prop.children (summary.Details |> List.map (fun d -> Html.li [ prop.text d ]))
                            ]
                    ]
                ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

let private boardView (model: Model) (dispatch: Msg -> unit) =
    let isLoading =
        match model.Snapshot with
        | Loading -> true
        | _ -> false

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text "Service status" ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text
                                    "Composite snapshot of every operator-facing observability surface. Refresh re-runs every section in parallel; per-section refresh re-runs that section alone."
                            ]
                        ]
                    ]
                    refreshButton "Refresh all" isLoading (fun () -> dispatch RefreshAll)
                ]
            ]
            match model.Snapshot with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading..." ]
            | LoadError msg -> errorBanner msg
            | Loaded snap ->
                Html.div [
                    prop.children [
                        Html.div [
                            prop.className
                                "flex items-center justify-between p-3 mb-4 bg-white border border-border rounded-lg"
                            prop.children [
                                overallPill snap.Overall
                                Html.span [
                                    prop.className "text-xs text-gray-500"
                                    prop.text (sprintf "Generated at %s" (snap.GeneratedAt.ToString "u"))
                                ]
                            ]
                        ]
                        Html.div [
                            prop.className "flex flex-col gap-3"
                            prop.children [
                                sectionPanel model dispatch ServiceStatusSnapshot.HealthSection snap.Health
                                sectionPanel model dispatch ServiceStatusSnapshot.PreflightSection snap.Preflight
                                sectionPanel model dispatch ServiceStatusSnapshot.DriftSection snap.Drift
                                sectionPanel model dispatch ServiceStatusSnapshot.RateLimitSection snap.RateLimit
                                sectionPanel model dispatch ServiceStatusSnapshot.JobQueueSection snap.JobQueue
                                sectionPanel model dispatch ServiceStatusSnapshot.SmokeTestSection snap.SmokeTest
                            ]
                        ]
                    ]
                ]
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let body =
        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [ boardView model dispatch ]
        ]

    body

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in service-status-board admin module. The shell's
/// `prepareModules` injects this in any non-Anonymous mode unless
/// `ServiceStatusBoard = NoServiceStatusBoard`. Grouped under
/// "Platform Management" so the role-gated sidebar filter (commit 4f.2)
/// hides the entry from non-admin callers.
let create (config: ServiceStatusBoardConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Service Status"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.health

    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.ServiceStatusBoard"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// Phase 573.B — the administration-landing tile this built-in
/// contributes (see `HealthMonitorUI.adminTile` for the full rationale:
/// declared here so the landing page never names a module, added by the
/// shell only for the SDK-owned modes, and `Title` / `Icon` following
/// the same derivation `create` uses). Supply `"_sdk.admin.service-status"`
/// from an `IHomeWidgetDataProvider` to lead the tile with a live
/// headline.
let adminTile (config: ServiceStatusBoardConfig option) : AdminTile =
    let name = config |> Option.map _.Name |> Option.defaultValue "Service Status"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.health

    {
        OwnerModuleId = "_sdk.ServiceStatusBoard"
        Widget = {
            Id = "_sdk.tile.service-status"
            Title = name
            Icon = icon
            Weight = 40
            Body =
                AdminTileBody.summary
                    "_sdk.admin.service-status"
                    "One snapshot across health, preflight, config drift, rate limits, the job queue and smoke tests."
        }
    }