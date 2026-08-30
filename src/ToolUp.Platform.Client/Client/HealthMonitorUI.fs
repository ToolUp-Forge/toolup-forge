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
    /// Phase 118 — degraded-capability set from `GetDegradedCapabilities`.
    /// Loaded alongside `Live`; empty on a healthy deployment, in which
    /// case the card is suppressed entirely (GP 13).
    Degraded: LoadState<DegradedCapability list>
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
    | DegradedLoaded of Result<DegradedCapability list, string>

// ─── API proxy ───────────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `CsrfClient.installRequestGuard`.
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

let private loadDegradedCmd () =
    Cmd.OfRemoting.call healthMonitorApi.GetDegradedCapabilities () DegradedLoaded (fun e ->
        DegradedLoaded(Error e.Message))

let init () =
    let model = {
        ActiveTab = LiveHealthTab
        Live = Loading
        Preflight = Loading
        SchedulerTelemetry = Loading
        Degraded = Loading
        RecentlyFlipped = Set.empty
    }

    model,
    Cmd.batch [
        loadLiveCmd ()
        loadPreflightCmd ()
        loadSchedulerTelemetryCmd ()
        loadDegradedCmd ()
    ]

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
                Degraded = Loading
        },
        Cmd.batch [ loadLiveCmd (); loadSchedulerTelemetryCmd (); loadDegradedCmd () ]

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

    | DegradedLoaded(Ok entries) -> { model with Degraded = Loaded entries }, Cmd.none

    | DegradedLoaded(Error msg) -> { model with Degraded = LoadError msg }, Cmd.none

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

let private refreshButton (msgs: HealthMonitorMessages) (label: string) (onClick: unit -> unit) (loading: bool) =
    Html.button [
        prop.className [
            "px-3 py-1.5 text-sm font-medium rounded border transition-colors"
            if loading then
                "bg-gray-100 text-gray-400 border-gray-200 cursor-not-allowed"
            else
                "bg-white text-gray-700 border-border hover:bg-gray-50"
        ]
        prop.disabled loading
        prop.text (if loading then msgs.Refreshing else label)
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
let private schedulerTelemetryCard (msgs: HealthMonitorMessages) (view: JobSchedulerTelemetryView) =
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
                        Html.h3 [ prop.className "text-sm font-semibold"; prop.text msgs.SchedulerDriftHeading ]
                        let generatedAtLabel = view.GeneratedAt.ToString "u"

                        Html.span [
                            prop.className "text-xs text-gray-500"
                            prop.text (msgs.AsOf generatedAtLabel)
                        ]
                    ]
                ]
                Html.p [ prop.className "text-xs text-gray-600 mb-2"; prop.text msgs.SchedulerLagHelp ]
                Html.div [
                    prop.className "grid grid-cols-3 gap-x-4 gap-y-1"
                    prop.children [
                        Html.div [ prop.className "text-xs text-gray-500"; prop.text msgs.SchedulerMissed60m ]
                        Html.div [ prop.className "text-xs text-gray-500"; prop.text msgs.SchedulerLastDrift ]
                        Html.div [ prop.className "text-xs text-gray-500"; prop.text msgs.SchedulerLastMissAt ]
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

// ─── Degraded-capability card (Phase 118) ────────────────────────────

/// Inline card on the Live tab listing capabilities that wired
/// best-effort and FAILED — the deployment is up but a capability is
/// silently down (e.g. cross-silo crypto-shred cache eviction). Rendered
/// ONLY when the set is non-empty (GP 13): a healthy deployment sees
/// nothing, matching the byte-for-byte-unchanged `/health` payload. The
/// red border signals this is a security/correctness degradation, not a
/// transient probe blip.
let private degradedCapabilitiesCard (msgs: HealthMonitorMessages) (entries: DegradedCapability list) =
    if List.isEmpty entries then
        Html.none
    else
        Html.div [
            prop.className "border border-red-300 bg-red-50 rounded-lg p-3 mb-4"
            prop.children [
                Html.div [
                    prop.className "flex items-baseline justify-between mb-2"
                    prop.children [
                        Html.h3 [
                            prop.className "text-sm font-semibold text-red-800"
                            prop.text (msgs.DegradedCapabilities entries.Length)
                        ]
                    ]
                ]
                Html.p [
                    prop.className "text-xs text-red-700 mb-3"
                    prop.text msgs.DegradedCapabilitiesHelp
                ]
                Html.div [
                    prop.className "flex flex-col gap-3"
                    prop.children (
                        entries
                        |> List.map (fun d ->
                            Html.div [
                                prop.className "border border-red-200 bg-white rounded p-3"
                                prop.children [
                                    Html.div [
                                        prop.className "flex items-baseline justify-between mb-1"
                                        prop.children [
                                            Html.span [
                                                prop.className "text-sm font-mono font-semibold text-red-800"
                                                prop.text d.Capability
                                            ]
                                            Html.span [
                                                prop.className "text-xs text-gray-500"
                                                prop.text (msgs.DegradedSince(string d.DegradedSince))
                                            ]
                                        ]
                                    ]
                                    Html.dl [
                                        prop.className "grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs"
                                        prop.children [
                                            Html.dt [
                                                prop.className "text-gray-500 font-medium"
                                                prop.text msgs.DegradedReason
                                            ]
                                            Html.dd [ prop.className "text-gray-700"; prop.text d.Reason ]
                                            Html.dt [
                                                prop.className "text-gray-500 font-medium"
                                                prop.text msgs.DegradedImpact
                                            ]
                                            Html.dd [ prop.className "text-gray-700"; prop.text d.Impact ]
                                            Html.dt [
                                                prop.className "text-gray-500 font-medium"
                                                prop.text msgs.Remediation
                                            ]
                                            Html.dd [ prop.className "text-gray-700"; prop.text d.Remediation ]
                                        ]
                                    ]
                                ]
                            ])
                    )
                ]
            ]
        ]

let private liveHealthHeader (msgs: HealthMonitorMessages) (snapshot: HealthSnapshot) =
    Html.div [
        prop.className "text-xs text-gray-500"
        prop.text (msgs.GeneratedAt (string snapshot.GeneratedAt) snapshot.Probes.Length)
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

let private probesTable (msgs: HealthMonitorMessages) (recentlyFlipped: Set<string>) (probes: HealthProbeView list) =
    if List.isEmpty probes then
        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoProbes ]
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
                                            prop.text msgs.ColumnStatus
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnProbe
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnKind
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnTimeout
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnElapsed
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnMessage
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

let private liveHealthTabView (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
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
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.LiveHealthHeading ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.ProbesFootnote ]
                        ]
                    ]
                    refreshButton msgs msgs.Refresh (fun () -> dispatch RefreshLive) isLoading
                ]
            ]
            // Phase 118 — degraded capabilities first (most urgent: a
            // capability is down). Suppressed when the set is empty or
            // the load is in flight.
            match model.Degraded with
            | Loaded entries -> degradedCapabilitiesCard msgs entries
            | _ -> Html.none

            // Phase 9b.A — surface scheduler drift above the probe
            // table. Suppressed when no scheduler is registered, and
            // when the load is in flight (no point flashing "Loading..."
            // for a side-counter).
            match model.SchedulerTelemetry with
            | Loaded view -> schedulerTelemetryCard msgs view
            | _ -> Html.none

            match model.Live with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
            | LoadError msg -> errorBanner msg
            | Loaded snapshot ->
                Html.div [
                    prop.children [
                        liveHealthHeader msgs snapshot
                        Html.div [
                            prop.className "mt-3"
                            prop.children [ probesTable msgs model.RecentlyFlipped snapshot.Probes ]
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

let private outcomesTable (msgs: HealthMonitorMessages) (outcomes: PreflightOutcomeView list) =
    if List.isEmpty outcomes then
        Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.NoValidators ]
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
                                            prop.text msgs.ColumnStatus
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnValidator
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnElapsed
                                        ]
                                        Html.th [
                                            prop.className "text-left px-3 py-2 font-medium text-gray-600"
                                            prop.text msgs.ColumnMessage
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

let private preflightTabView (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
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
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.PreflightHeading ]
                            Html.p [ prop.className "text-xs text-gray-500"; prop.text msgs.PreflightFootnote ]
                        ]
                    ]
                    refreshButton msgs msgs.Refetch (fun () -> dispatch RefreshPreflight) isLoading
                ]
            ]
            match model.Preflight with
            | NotLoaded
            | Loading -> Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.Loading ]
            | LoadError msg -> errorBanner msg
            | Loaded snapshot ->
                if not snapshot.HasSnapshot then
                    Html.p [ prop.className "text-sm text-gray-500"; prop.text msgs.PreflightUnavailable ]
                else
                    outcomesTable msgs snapshot.Outcomes
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

let private tabBar (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className "flex gap-1 border-b border-border bg-white px-4"
        prop.children [
            tabButton msgs.LiveHealthTab (model.ActiveTab = LiveHealthTab) (fun () ->
                dispatch (SwitchTab LiveHealthTab))
            tabButton msgs.PreflightTab (model.ActiveTab = PreflightTab) (fun () -> dispatch (SwitchTab PreflightTab))
        ]
    ]

/// Phase 444 — the module body as a React COMPONENT rather than a plain
/// render function, so it has a hook site from which to read the resolved
/// catalog. A module's `view` is invoked inline by the shell's own render,
/// where a hook would join the shell's hook order and break the moment the
/// active module changed; a component of its own has a stable identity and
/// its own. Same distinction `FileManagerUI.LoadingSlot` documents, applied
/// to the whole body because every tab under it renders catalog strings.
[<ReactComponent>]
let private HealthMonitorBody (model: Model) (dispatch: Msg -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).HealthMonitor

    let content =
        match model.ActiveTab with
        | LiveHealthTab -> liveHealthTabView msgs model dispatch
        | PreflightTab -> preflightTabView msgs model dispatch

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ tabBar msgs model dispatch; content ]
    ]

let private view (model: Model) (dispatch: Msg -> unit) : ReactElement = HealthMonitorBody model dispatch

// ─── Module creation ─────────────────────────────────────────────────

/// Create the built-in health monitor admin as an `ErasedModule`. The
/// shell's `prepareModules` injects this in any non-Anonymous mode
/// unless `HealthMonitor = NoHealthMonitor`.
///
/// **Phase 4b re-gate (commit 4f.1, 2026-05-10).** The server-side
/// handler now gates on `AccessContext.canModifyPlatformConfig`
/// (Platform Admin role) rather than per-team Owner/Admin — probes
/// are deployment-wide data, so the gate is too. The sidebar group is
/// "Platform Management" (not the previous "Admin") so the shell's
/// role-gated sidebar filter (commit 4f.2) hides the entry from
/// non-admin callers. Existing Team Admins relying on HealthMonitor lose access
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
    |> ToolUp.Platform.ClientModule.withGroup "Platform Management"
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.PlatformAdminOnly
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register

/// Phase 573.B — the administration-landing tile this built-in
/// contributes, declared here rather than in the landing page so the
/// page never names a module (GP 9). The shell adds it only when the
/// deployment runs the SDK's own health monitor
/// (`DefaultHealthMonitor` / `ConfiguredHealthMonitor`):
/// `NoHealthMonitor` contributes nothing, and an
/// `ExternalHealthMonitor` replacement owns its own tile — it is a
/// different module with a different id, and inheriting this one's
/// click-through would navigate to a module that is not registered.
///
/// `Title` / `Icon` follow the same config-or-default derivation
/// `create` uses, so a branded deployment's tile matches its rail
/// entry. The headline key is read from the shared
/// `HomeWidgetContext.Data` bag — supply `"_sdk.admin.health"` from an
/// `IHomeWidgetDataProvider` to put a live status on the tile.
let adminTile (config: HealthMonitorConfig option) : AdminTile =
    let name = config |> Option.map _.Name |> Option.defaultValue "Health Monitor"

    let icon =
        config |> Option.map _.Icon |> Option.defaultValue ToolUp.Platform.Icons.health

    {
        OwnerModuleId = "_sdk.HealthMonitor"
        Widget = {
            Id = "_sdk.tile.health"
            Title = name
            Icon = icon
            Weight = 30
            Body =
                AdminTileBody.summary
                    "_sdk.admin.health"
                    "Liveness, readiness and dependency probes for this deployment, plus the startup preflight report."
        }
    }