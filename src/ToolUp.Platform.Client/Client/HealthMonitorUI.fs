// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HealthMonitorUI

open System
open ToolUp.Elmish
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── Model ───────────────────────────────────────────────────────────

/// Two top-level sections of the health-monitor admin UI. Each tab
/// loads independently — switching tabs does not refetch the other.
type Tab =
    | LiveHealthTab
    | PreflightTab

type LoadState<'T> =
    | NotLoaded
    | Loading
    | Loaded of 'T
    | LoadError of string

type Model = {
    /// Currently rendered section.
    ActiveTab: Tab
    /// Live snapshot from `GetCurrentHealth`. Refreshed on click.
    Live: LoadState<HealthSnapshot>
    /// Preflight snapshot from `GetPreflightSnapshot`.
    Preflight: LoadState<PreflightSnapshotView>
    /// Phase 9b.A — most recent job-scheduler missed-tick telemetry.
    /// Loaded alongside `Live` so the operator sees both on the Live
    /// tab without a second click. `HasScheduler = false` (carried
    /// inside the view) suppresses the inline card.
    SchedulerTelemetry: LoadState<JobSchedulerTelemetryView>
    /// Names of probes whose status changed between the prior and
    /// most recent live snapshot — surfaced via Tailwind
    /// `animate-pulse` for ~1.5s so an admin spotting a freshly-flipped
    /// probe doesn't have to re-read the whole table. Cleared on the
    /// next refresh.
    RecentlyFlipped: Set<string>
}

type Msg =
    | SwitchTab of Tab
    | RefreshLive
    | LiveLoaded of Result<HealthSnapshot, string>
    | RefreshPreflight
    | PreflightLoaded of Result<PreflightSnapshotView, string>
    | SchedulerTelemetryLoaded of Result<JobSchedulerTelemetryView, string>

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + SDK.Client.fs installRequestGuard.
let private healthMonitorApi: IHealthMonitorApi =
    Api.makeProxy<IHealthMonitorApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ────────────────────────────────────────────────────────────

let private loadLiveCmd () =
    Cmd.OfRemoting.call healthMonitorApi.GetCurrentHealth () LiveLoaded (fun e -> LiveLoaded(Error e.Message))

let private loadPreflightCmd () =
    Cmd.OfRemoting.call healthMonitorApi.GetPreflightSnapshot () PreflightLoaded (fun e ->
        PreflightLoaded(Error e.Message))

let private loadSchedulerTelemetryCmd () =
    Cmd.OfRemoting.call healthMonitorApi.GetJobSchedulerTelemetry () SchedulerTelemetryLoaded (fun e ->
        SchedulerTelemetryLoaded(Error e.Message))

let init () =
    let model = {
        ActiveTab = LiveHealthTab
        Live = Loading
        Preflight = Loading
        SchedulerTelemetry = Loading
        RecentlyFlipped = Set.empty
    }

    model, Cmd.batch [ loadLiveCmd (); loadPreflightCmd (); loadSchedulerTelemetryCmd () ]

// ─── Helpers ─────────────────────────────────────────────────────────

/// Compute the set of probe names whose status differs between the
/// prior and the new snapshot. New probes (present in `next` but not
/// `prev`) are considered "flipped" so an admin sees the row appear
/// highlighted on the first refresh that introduces it.
let private flippedNames (prev: HealthSnapshot option) (next: HealthSnapshot) : Set<string> =
    match prev with
    | None -> Set.empty
    | Some p ->
        let prevByName = p.Probes |> List.map (fun r -> r.Name, r.Status) |> Map.ofList

        next.Probes
        |> List.choose (fun row ->
            match Map.tryFind row.Name prevByName with
            | Some prevStatus when prevStatus = row.Status -> None
            | _ -> Some row.Name)
        |> Set.ofList

let private currentLiveOpt (model: Model) =
    match model.Live with
    | Loaded snap -> Some snap
    | _ -> None

// ─── Update ──────────────────────────────────────────────────────────

let update (msg: Msg) (model: Model) =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, Cmd.none

    | RefreshLive ->
        // Refresh both panels on the Live tab in one click — scheduler
        // telemetry is a pull off an in-memory counter (cheap) so
        // re-fetching alongside the probe sweep is free.
        {
            model with
                Live = Loading
                SchedulerTelemetry = Loading
        },
        Cmd.batch [ loadLiveCmd (); loadSchedulerTelemetryCmd () ]

    | LiveLoaded(Ok snapshot) ->
        let flipped = flippedNames (currentLiveOpt model) snapshot

        {
            model with
                Live = Loaded snapshot
                RecentlyFlipped = flipped
        },
        Cmd.none

    | LiveLoaded(Error msg) -> { model with Live = LoadError msg }, Cmd.none

    | RefreshPreflight -> { model with Preflight = Loading }, loadPreflightCmd ()

    | PreflightLoaded(Ok snapshot) ->
        {
            model with
                Preflight = Loaded snapshot
        },
        Cmd.none

    | PreflightLoaded(Error msg) -> { model with Preflight = LoadError msg }, Cmd.none

    | SchedulerTelemetryLoaded(Ok view) ->
        {
            model with
                SchedulerTelemetry = Loaded view
        },
        Cmd.none

    | SchedulerTelemetryLoaded(Error msg) ->
        {
            model with
                SchedulerTelemetry = LoadError msg
        },
        Cmd.none

// ─── Status-pill renderers ───────────────────────────────────────────

/// Three-colour status pill mirroring the convention used elsewhere in
/// the admin UI (`WebhookAdminUI.outcomeLabel`'s green / yellow / red
/// idiom). Centralised so the live-health and preflight tables stay
/// visually consistent — the same probe status (`Healthy`) and the same
/// validator outcome (`Ok`) read identically green.
let private statusPill (status: string) =
    let cls =
        match status with
        | "Healthy"
        | "Ok" -> "bg-green-100 text-green-700 border-green-200"
        | "Degraded"
        | "Warning" -> "bg-yellow-100 text-yellow-700 border-yellow-200"
        | "Unhealthy"
        | "Error" -> "bg-red-100 text-red-700 border-red-200"
        | _ -> "bg-gray-100 text-gray-700 border-gray-200"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text status
    ]

let private refreshButton (label: string) (onClick: unit -> unit) (loading: bool) =
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

// ─── Live health tab ─────────────────────────────────────────────────

// ─── Job-scheduler telemetry card (Phase 9b.A) ───────────────────────

/// Inline card on the Live tab. Suppressed entirely when no scheduler
/// is registered (`HasScheduler = false`) so deployments without
/// background jobs don't see a misleading zero counter. When a miss
/// has been observed, the card pulls a warning border so operators
/// scanning the page register the signal before reading the numbers.
let private schedulerTelemetryCard (view: JobSchedulerTelemetryView) =
    if not view.HasScheduler then
        Html.none
    else
        let hasMiss = view.TickMissedCount60Min > 0 || view.LastTickMissedAt.IsSome

        let borderCls =
            if hasMiss then
                "border-yellow-300 bg-yellow-50"
            else
                "border-border bg-white"

        let countCls =
            if hasMiss then
                "text-yellow-700 font-semibold"
            else
                "text-gray-700"

        let driftCell =
            match view.LastDriftMs with
            | Some ms -> Html.text $"{ms} ms"
            | None -> Html.span [ prop.className "text-gray-400"; prop.text "—" ]

        let missedAtCell =
            match view.LastTickMissedAt with
            | Some ts -> Html.text (ts.ToString "u")
            | None -> Html.span [ prop.className "text-gray-400"; prop.text "—" ]

        Html.div [
            prop.className $"border rounded-lg p-3 mb-4 text-sm {borderCls}"
            prop.children [
                Html.div [
                    prop.className "flex items-baseline justify-between mb-1"
                    prop.children [
                        Html.h3 [ prop.className "text-sm font-semibold"; prop.text "Job scheduler tick drift" ]
                        let generatedAtLabel = view.GeneratedAt.ToString "u"

                        Html.span [
                            prop.className "text-xs text-gray-500"
                            prop.text $"as of {generatedAtLabel}"
                        ]
                    ]
                ]
                Html.p [
                    prop.className "text-xs text-gray-600 mb-2"
                    prop.text
                        "Counts minute boundaries where the scheduler woke late (debugger pause, GC stall, container throttling). Healthy deployments stay at zero; recovers automatically once the process resumes."
                ]
                Html.div [
                    prop.className "grid grid-cols-3 gap-x-4 gap-y-1"
                    prop.children [
                        Html.div [ prop.className "text-xs text-gray-500"; prop.text "Missed (60-min)" ]
                        Html.div [ prop.className "text-xs text-gray-500"; prop.text "Last drift" ]
                        Html.div [ prop.className "text-xs text-gray-500"; prop.text "Last miss at" ]
                        Html.div [
                            prop.className $"text-sm font-mono {countCls}"
                            prop.text (string view.TickMissedCount60Min)
                        ]
                        Html.div [ prop.className "text-sm font-mono"; prop.children [ driftCell ] ]
                        Html.div [ prop.className "text-sm font-mono"; prop.children [ missedAtCell ] ]
                    ]
                ]
            ]
        ]

let private liveHealthHeader (snapshot: HealthSnapshot) =
    Html.div [
        prop.className "text-xs text-gray-500"
        prop.text $"Generated at {snapshot.GeneratedAt} ({snapshot.Probes.Length} probes)"
    ]

let private probeRow (recentlyFlipped: Set<string>) (row: HealthProbeView) =
    let isFlipped = recentlyFlipped |> Set.contains row.Name

    Html.tr [
        prop.className [
            "border-t border-border"
            if isFlipped then
                "animate-pulse bg-yellow-50"
        ]
        prop.children [
            Html.td [ prop.className "px-3 py-2"; prop.children [ statusPill row.Status ] ]
            Html.td [ prop.className "px-3 py-2 font-medium"; prop.text row.Name ]
            Html.td [ prop.className "px-3 py-2 text-xs text-gray-500"; prop.text row.Kind ]
            Html.td [
                prop.className "px-3 py-2 text-xs text-gray-500 font-mono"
                prop.text $"{row.TimeoutMs} ms"
            ]
            Html.td [
                prop.className "px-3 py-2 text-xs text-gray-500 font-mono"
                prop.text $"{row.ElapsedMs} ms"
            ]
            Html.td [ prop.className "px-3 py-2 text-xs text-gray-700"; prop.text row.Message ]
        ]
    ]

let private probesTable (recentlyFlipped: Set<string>) (probes: HealthProbeView list) =
    if List.isEmpty probes then
        Html.p [
            prop.className "text-sm text-gray-500"
            prop.text
                "No health probes registered. Companions self-register via services.AddSingleton<IHealthCheck>(instance) — see TECHNICAL_GUIDE.md."
        ]
    else
        Html.div [
            prop.className "border border-border rounded-lg overflow-hidden"
            prop.children [
                Html.table [
                    prop.className "w-full text-sm"
                    prop.children [
                        Html.thead [
                            prop.className "bg-gray-50"
                            prop.children [
                                Html.tr [
                                    prop.children [
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Status"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Probe"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Kind"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Timeout"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Elapsed"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Message"
                                        ]
                                    ]
                                ]
                            ]
                        ]
                        Html.tbody [ prop.children (probes |> List.map (probeRow recentlyFlipped)) ]
                    ]
                ]
            ]
        ]

let private liveHealthTabView (model: Model) (dispatch: Msg -> unit) =
    let isLoading =
        match model.Live with
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
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text "Live health" ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text
                                    "Each refresh re-runs every registered IHealthCheck in parallel. Probes are deployment-wide — no per-team filter applies."
                            ]
                        ]
                    ]
                    refreshButton "Refresh" (fun () -> dispatch RefreshLive) isLoading
                ]
            ]
            // Phase 9b.A — surface scheduler drift above the probe
            // table. Suppressed when no scheduler is registered, and
            // when the load is in flight (no point flashing "Loading..."
            // for a side-counter).
            match model.SchedulerTelemetry with
            | Loaded view -> schedulerTelemetryCard view
            | _ -> Html.none

            match model.Live with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading..." ]
            | LoadError msg -> errorBanner msg
            | Loaded snapshot ->
                Html.div [
                    prop.children [
                        liveHealthHeader snapshot
                        Html.div [
                            prop.className "mt-3"
                            prop.children [ probesTable model.RecentlyFlipped snapshot.Probes ]
                        ]
                    ]
                ]
        ]
    ]

// ─── Preflight tab ───────────────────────────────────────────────────

let private preflightRow (row: PreflightOutcomeView) =
    Html.tr [
        prop.className "border-t border-border"
        prop.children [
            Html.td [ prop.className "px-3 py-2"; prop.children [ statusPill row.Status ] ]
            Html.td [ prop.className "px-3 py-2 font-medium"; prop.text row.Name ]
            Html.td [
                prop.className "px-3 py-2 text-xs text-gray-500 font-mono"
                prop.text $"{row.ElapsedMs} ms"
            ]
            Html.td [ prop.className "px-3 py-2 text-xs text-gray-700"; prop.text row.Message ]
        ]
    ]

let private outcomesTable (outcomes: PreflightOutcomeView list) =
    if List.isEmpty outcomes then
        Html.p [
            prop.className "text-sm text-gray-500"
            prop.text
                "No validators recorded. Either no IConfigValidator was registered at the most recent boot, or ServerConfig.SkipPreflight = true was set for an emergency boot."
        ]
    else
        Html.div [
            prop.className "border border-border rounded-lg overflow-hidden"
            prop.children [
                Html.table [
                    prop.className "w-full text-sm"
                    prop.children [
                        Html.thead [
                            prop.className "bg-gray-50"
                            prop.children [
                                Html.tr [
                                    prop.children [
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Status"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Validator"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Elapsed"
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text "Message"
                                        ]
                                    ]
                                ]
                            ]
                        ]
                        Html.tbody [ prop.children (outcomes |> List.map preflightRow) ]
                    ]
                ]
            ]
        ]

let private preflightTabView (model: Model) (dispatch: Msg -> unit) =
    let isLoading =
        match model.Preflight with
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
                            Html.h2 [
                                prop.className "text-lg font-semibold"
                                prop.text "Preflight (most recent boot)"
                            ]
                            Html.p [
                                prop.className "text-xs text-gray-500"
                                prop.text
                                    "Snapshot from the most recent startup. Re-fetch to confirm a redeploy passed without a hard reload — validators do not re-run against this view."
                            ]
                        ]
                    ]
                    refreshButton "Re-fetch" (fun () -> dispatch RefreshPreflight) isLoading
                ]
            ]
            match model.Preflight with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text "Loading..." ]
            | LoadError msg -> errorBanner msg
            | Loaded snapshot ->
                if not snapshot.HasSnapshot then
                    Html.p [
                        prop.className "text-sm text-gray-500"
                        prop.text
                            "Preflight snapshot is not available — this deployment was composed before Phase 9m landed, or no IPreflightSnapshot service is registered."
                    ]
                else
                    outcomesTable snapshot.Outcomes
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

let private tabBar (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex gap-1 border-b border-border bg-white px-4"
        prop.children [
            tabButton "Live health" (model.ActiveTab = LiveHealthTab) (fun () -> dispatch (SwitchTab LiveHealthTab))
            tabButton "Preflight" (model.ActiveTab = PreflightTab) (fun () -> dispatch (SwitchTab PreflightTab))
        ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let content =
        match model.ActiveTab with
        | LiveHealthTab -> liveHealthTabView model dispatch
        | PreflightTab -> preflightTabView model dispatch

    let body =
        Html.div [
            prop.className "flex flex-col h-full"
            prop.children [ tabBar model dispatch; content ]
        ]

    body

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in health monitor admin as an `ErasedModule`. The
/// shell's `prepareModules` injects this in any non-Anonymous mode
/// unless `HealthMonitor = NoHealthMonitor`.
///
/// **Phase 4b re-gate (commit 4f.1, 2026-05-10).** The server-side
/// handler now gates on `AccessContext.canModifyPlatformConfig`
/// (Platform Admin role) rather than per-team Owner/Admin — probes
/// are deployment-wide data, so the gate is too. The sidebar group is
/// "Platform Admin" (not the previous "Admin") so the shell's role-
/// gated sidebar filter (commit 4f.2) hides the entry from non-admin
/// callers. Existing Team Admins relying on HealthMonitor lose access
/// until they're also assigned `PlatformRole.PlatformAdmin`.
let create (config: HealthMonitorConfig option) : ErasedModule =
    let name = config |> Option.map _.Name |> Option.defaultValue "Health Monitor"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.health

    // SDK-built-in — reserved under the `_sdk.` Id namespace so it can
    // never collide with an app's RBAC-managed `ServerConfig.ModuleNames`.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_sdk.HealthMonitor"
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withGroup "Platform Admin"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register