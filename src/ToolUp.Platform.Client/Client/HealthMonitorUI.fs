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

type Model = {
    /// Currently rendered section.
    ActiveTab: Tab
    /// Live snapshot from `GetCurrentHealth`. Refreshed on click.
    Live: Remote<HealthSnapshot, string>
    /// Preflight snapshot from `GetPreflightSnapshot`.
    Preflight: Remote<PreflightSnapshotView, string>
    /// Phase 9b.A — most recent job-scheduler missed-tick telemetry.
    /// Loaded alongside `Live` so the operator sees both on the Live
    /// tab without a second click. `HasScheduler = false` (carried
    /// inside the view) suppresses the inline card.
    SchedulerTelemetry: Remote<JobSchedulerTelemetryView, string>
    /// Phase 118 — degraded-capability set from `GetDegradedCapabilities`.
    /// Loaded alongside `Live`; empty on a healthy deployment, in which
    /// case the card is suppressed entirely (GP 13).
    Degraded: Remote<DegradedCapability list, string>
    /// Phase 47 — rolling AI action-denial rollup from
    /// `GetAIDenialRollup`. Loaded alongside `Live`. `Loaded None` means
    /// no AI companion is composed (the `IAIDenialRollupProbe` seam is
    /// absent from DI) and the card is suppressed — distinct from
    /// `Loaded (Some rollup)` with a zero count, which is a real
    /// "nothing is being refused" reading and worth showing.
    AIDenials: Remote<AIDenialRollup option, string>
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
    | AIDenialsLoaded of Result<AIDenialRollup option, string>

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

let private loadAIDenialsCmd () =
    Cmd.OfRemoting.call healthMonitorApi.GetAIDenialRollup () AIDenialsLoaded (fun e ->
        AIDenialsLoaded(Error e.Message))

let init () =
    let model = {
        ActiveTab = LiveHealthTab
        Live = Remote.Loading
        Preflight = Remote.Loading
        SchedulerTelemetry = Remote.Loading
        Degraded = Remote.Loading
        AIDenials = Remote.Loading
        RecentlyFlipped = Set.empty
    }

    model,
    Cmd.batch [
        loadLiveCmd ()
        loadPreflightCmd ()
        loadSchedulerTelemetryCmd ()
        loadDegradedCmd ()
        loadAIDenialsCmd ()
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
                Live = Remote.Loading
                SchedulerTelemetry = Remote.Loading
                Degraded = Remote.Loading
                AIDenials = Remote.Loading
        },
        Cmd.batch [
            loadLiveCmd ()
            loadSchedulerTelemetryCmd ()
            loadDegradedCmd ()
            loadAIDenialsCmd ()
        ]

    | LiveLoaded(Ok snapshot) ->
        let flipped = flippedNames (Remote.toOption model.Live) snapshot

        {
            model with
                Live = Remote.Loaded snapshot
                RecentlyFlipped = flipped
        },
        Cmd.none

    | LiveLoaded(Error msg) -> { model with Live = Remote.Failed msg }, Cmd.none

    | RefreshPreflight ->
        {
            model with
                Preflight = Remote.Loading
        },
        loadPreflightCmd ()

    | PreflightLoaded result ->
        {
            model with
                Preflight = Remote.ofResult result
        },
        Cmd.none

    | SchedulerTelemetryLoaded result ->
        {
            model with
                SchedulerTelemetry = Remote.ofResult result
        },
        Cmd.none

    | DegradedLoaded result ->
        {
            model with
                Degraded = Remote.ofResult result
        },
        Cmd.none

    | AIDenialsLoaded result ->
        {
            model with
                AIDenials = Remote.ofResult result
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
        | _ -> ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text status
    ]

let private refreshButton (msgs: HealthMonitorMessages) (label: string) (onClick: unit -> unit) (loading: bool) =
    Forms.Button.secondaryWith (Forms.Button.stateOf loading msgs.Refreshing false) label onClick

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
                Tokens.Text.secondary

        let driftCell =
            match view.LastDriftMs with
            | Some ms -> Html.text $"{ms} ms"
            | None -> Html.span [ prop.className Tokens.Text.muted; prop.text "—" ]

        let missedAtCell =
            match view.LastTickMissedAt with
            | Some ts -> Html.text (ts.ToString "u")
            | None -> Html.span [ prop.className Tokens.Text.muted; prop.text "—" ]

        Html.div [
            prop.className $"border rounded-lg p-3 mb-4 text-sm {borderCls}"
            prop.children [
                Html.div [
                    prop.className "flex items-baseline justify-between mb-1"
                    prop.children [
                        Html.h3 [ prop.className "text-sm font-semibold"; prop.text msgs.SchedulerDriftHeading ]
                        let generatedAtLabel = view.GeneratedAt.ToString "u"

                        Html.span [
                            prop.className ("text-xs " + Tokens.Text.muted)
                            prop.text (msgs.AsOf generatedAtLabel)
                        ]
                    ]
                ]
                Html.p [
                    prop.className ("text-xs mb-2 " + Tokens.Text.secondary)
                    prop.text msgs.SchedulerLagHelp
                ]
                Html.div [
                    prop.className "grid grid-cols-3 gap-x-4 gap-y-1"
                    prop.children [
                        Html.div [
                            prop.className ("text-xs " + Tokens.Text.muted)
                            prop.text msgs.SchedulerMissed60m
                        ]
                        Html.div [
                            prop.className ("text-xs " + Tokens.Text.muted)
                            prop.text msgs.SchedulerLastDrift
                        ]
                        Html.div [
                            prop.className ("text-xs " + Tokens.Text.muted)
                            prop.text msgs.SchedulerLastMissAt
                        ]
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
                                                prop.className ("text-xs " + Tokens.Text.muted)
                                                prop.text (msgs.DegradedSince(string d.DegradedSince))
                                            ]
                                        ]
                                    ]
                                    Html.dl [
                                        prop.className "grid grid-cols-[max-content_1fr] gap-x-3 gap-y-1 text-xs"
                                        prop.children [
                                            Html.dt [
                                                prop.className ("font-medium " + Tokens.Text.muted)
                                                prop.text msgs.DegradedReason
                                            ]
                                            Html.dd [ prop.className Tokens.Text.secondary; prop.text d.Reason ]
                                            Html.dt [
                                                prop.className ("font-medium " + Tokens.Text.muted)
                                                prop.text msgs.DegradedImpact
                                            ]
                                            Html.dd [ prop.className Tokens.Text.secondary; prop.text d.Impact ]
                                            Html.dt [
                                                prop.className ("font-medium " + Tokens.Text.muted)
                                                prop.text msgs.Remediation
                                            ]
                                            Html.dd [ prop.className Tokens.Text.secondary; prop.text d.Remediation ]
                                        ]
                                    ]
                                ]
                            ])
                    )
                ]
            ]
        ]

// ─── AI action-denial card (Phase 47) ────────────────────────────────

/// Inline card on the Live tab showing the rolling AI action-denial
/// rollup — the production-safe twin of the `EnableDevEndpoints`-gated
/// `/dev/ai-allowlist` endpoint. Same `AIDenialRollup` record, same
/// numbers; an operator watching a prompt-injection campaign in
/// production does not have to turn the debug surface on to see it.
///
/// **Suppressed entirely when the deployment has never recorded a
/// denial** (GP 13) — an install with no AI, or one whose allowlist has
/// never refused anything, sees no card at all. Once it HAS recorded
/// one, the card stays and honestly reports a clean current window,
/// because "it has subsided" is exactly what an operator who just
/// received the alert needs to read.
///
/// Renders only what the rollup carries: reasons are already
/// control-stripped and truncated by the producing tier, and raw model
/// arguments are never in the payload at all.
let private aiDenialCard (msgs: HealthMonitorMessages) (rollup: AIDenialRollup) =
    if rollup.TotalDenialsAllTime = 0 then
        Html.none
    else
        let active = rollup.TotalDenialsInWindow > 0

        let shellCls =
            if active then
                "border border-amber-300 bg-amber-50 rounded-lg p-3 mb-4"
            else
                "border border-border bg-surface rounded-lg p-3 mb-4"

        let headingCls =
            if active then
                "text-sm font-semibold text-amber-800"
            else
                ("text-sm font-semibold " + Tokens.Text.secondary)

        let groupList (heading: string) (rows: AIDenialGroupCount list) =
            if List.isEmpty rows then
                Html.none
            else
                Html.div [
                    prop.children [
                        Html.div [
                            prop.className ("text-xs font-medium mb-1 " + Tokens.Text.muted)
                            prop.text heading
                        ]
                        Html.div [
                            prop.className "flex flex-col gap-0.5"
                            prop.children (
                                rows
                                |> List.truncate 5
                                |> List.map (fun row ->
                                    Html.div [
                                        prop.className "flex items-baseline justify-between gap-3 text-xs"
                                        prop.children [
                                            Html.span [
                                                prop.className ("font-mono " + Tokens.Text.secondary)
                                                prop.text row.Key
                                            ]
                                            Html.span [
                                                prop.className ("font-mono " + Tokens.Text.muted)
                                                prop.text (string row.Count)
                                            ]
                                        ]
                                    ])
                            )
                        ]
                    ]
                ]

        let recentRow (d: RecentAIDenial) =
            Html.div [
                prop.className "text-xs bg-white border border-border rounded p-2"
                prop.children [
                    Html.div [
                        prop.className "flex items-baseline justify-between gap-3"
                        prop.children [
                            Html.span [
                                prop.className ("font-mono " + Tokens.Text.secondary)
                                prop.text $"{d.ToolName} · {d.ActiveModule}"
                            ]
                            Html.span [ prop.className Tokens.Text.muted; prop.text (string d.OccurredAt) ]
                        ]
                    ]
                    Html.div [ prop.className ("mt-0.5 " + Tokens.Text.secondary); prop.text d.Reason ]
                ]
            ]

        Html.div [
            prop.className shellCls
            prop.children [
                Html.div [
                    prop.className "flex items-baseline justify-between mb-2"
                    prop.children [
                        Html.h3 [
                            prop.className headingCls
                            prop.text (msgs.AIDenials rollup.TotalDenialsInWindow)
                        ]
                        Html.span [
                            prop.className ("text-xs font-mono " + Tokens.Text.muted)
                            prop.text (msgs.AIDenialsRate rollup.WindowMinutes (sprintf "%.2f" rollup.DenialsPerMinute))
                        ]
                    ]
                ]
                Html.p [
                    prop.className ("text-xs mb-3 " + Tokens.Text.secondary)
                    prop.text msgs.AIDenialsHelp
                ]

                if not active then
                    Html.p [
                        prop.className ("text-xs " + Tokens.Text.muted)
                        prop.text msgs.AIDenialsQuiet
                    ]
                else
                    Html.div [
                        prop.className "flex flex-col gap-3"
                        prop.children [
                            Html.div [
                                prop.className "grid grid-cols-1 sm:grid-cols-2 gap-3"
                                prop.children [
                                    groupList msgs.AIDenialsByTool rollup.ByToolName
                                    groupList msgs.AIDenialsByModule rollup.ByActiveModule
                                ]
                            ]
                            if not (List.isEmpty rollup.RecentDenials) then
                                Html.div [
                                    prop.children [
                                        Html.div [
                                            prop.className ("text-xs font-medium mb-1 " + Tokens.Text.muted)
                                            prop.text msgs.AIDenialsRecent
                                        ]
                                        Html.div [
                                            prop.className "flex flex-col gap-1"
                                            prop.children (
                                                rollup.RecentDenials |> List.truncate 5 |> List.map recentRow
                                            )
                                        ]
                                    ]
                                ]
                        ]
                    ]
            ]
        ]

let private liveHealthHeader (msgs: HealthMonitorMessages) (snapshot: HealthSnapshot) =
    Html.div [
        prop.className ("text-xs " + Tokens.Text.muted)
        prop.text (msgs.GeneratedAt (string snapshot.GeneratedAt) snapshot.Probes.Length)
    ]

let private column header (cell: 'row -> ReactElement) : Data.Column<'row> = {
    Header = header
    Align = Data.Left
    Cell = cell
}

let private quiet (classes: string) (value: string) =
    Html.span [ prop.className [ "text-xs"; classes ]; prop.text value ]

/// A probe whose status flipped since the previous snapshot pulses its
/// status pill, so the change is the first thing the eye lands on.
let private probeColumns
    (msgs: HealthMonitorMessages)
    (recentlyFlipped: Set<string>)
    : Data.Column<HealthProbeView> list =
    [
        column msgs.ColumnStatus (fun row ->
            Html.span [
                if recentlyFlipped |> Set.contains row.Name then
                    prop.className "inline-block animate-pulse"
                prop.children [ statusPill row.Status ]
            ])
        column msgs.ColumnProbe (fun row -> Html.span [ prop.className "font-medium"; prop.text row.Name ])
        column msgs.ColumnKind (fun row -> quiet Tokens.Text.muted row.Kind)
        column msgs.ColumnTimeout (fun row -> quiet ("font-mono " + Tokens.Text.muted) $"{row.TimeoutMs} ms")
        column msgs.ColumnElapsed (fun row -> quiet ("font-mono " + Tokens.Text.muted) $"{row.ElapsedMs} ms")
        column msgs.ColumnMessage (fun row -> quiet Tokens.Text.secondary row.Message)
    ]

let private probesTable (msgs: HealthMonitorMessages) (recentlyFlipped: Set<string>) (probes: HealthProbeView list) =
    if List.isEmpty probes then
        Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoProbes ]
    else
        Data.table (probeColumns msgs recentlyFlipped) probes

let private liveHealthTabView (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
    let isLoading = Remote.isLoading model.Live

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.LiveHealthHeading ]
                            Html.p [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text msgs.ProbesFootnote
                            ]
                        ]
                    ]
                    refreshButton msgs msgs.Refresh (fun () -> dispatch RefreshLive) isLoading
                ]
            ]
            // Phase 118 — degraded capabilities first (most urgent: a
            // capability is down). Suppressed when the set is empty or
            // the load is in flight.
            match model.Degraded with
            | Remote.Loaded entries -> degradedCapabilitiesCard msgs entries
            | _ -> Html.none

            // Phase 47 — AI action denials. `Loaded None` means no AI
            // companion is composed, so there is nothing to say; a
            // `Some` rollup suppresses itself when the deployment has
            // never recorded a denial.
            match model.AIDenials with
            | Remote.Loaded(Some rollup) -> aiDenialCard msgs rollup
            | _ -> Html.none

            // Phase 9b.A — surface scheduler drift above the probe
            // table. Suppressed when no scheduler is registered, and
            // when the load is in flight (no point flashing "Loading..."
            // for a side-counter).
            match model.SchedulerTelemetry with
            | Remote.Loaded view -> schedulerTelemetryCard msgs view
            | _ -> Html.none

            match model.Live with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ -> Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
            | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
            | Remote.Loaded snapshot ->
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

let private outcomeColumns (msgs: HealthMonitorMessages) : Data.Column<PreflightOutcomeView> list = [
    column msgs.ColumnStatus (fun row -> statusPill row.Status)
    column msgs.ColumnValidator (fun row -> Html.span [ prop.className "font-medium"; prop.text row.Name ])
    column msgs.ColumnElapsed (fun row -> quiet ("font-mono " + Tokens.Text.muted) $"{row.ElapsedMs} ms")
    column msgs.ColumnMessage (fun row -> quiet Tokens.Text.secondary row.Message)
]

let private outcomesTable (msgs: HealthMonitorMessages) (outcomes: PreflightOutcomeView list) =
    if List.isEmpty outcomes then
        Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.NoValidators ]
    else
        Data.table (outcomeColumns msgs) outcomes

let private preflightTabView (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
    let isLoading = Remote.isLoading model.Preflight

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.PreflightHeading ]
                            Html.p [
                                prop.className ("text-xs " + Tokens.Text.muted)
                                prop.text msgs.PreflightFootnote
                            ]
                        ]
                    ]
                    refreshButton msgs msgs.Refetch (fun () -> dispatch RefreshPreflight) isLoading
                ]
            ]
            match model.Preflight with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ -> Html.p [ prop.className ("text-sm " + Tokens.Text.muted); prop.text msgs.Loading ]
            | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
            | Remote.Loaded snapshot ->
                if not snapshot.HasSnapshot then
                    Html.p [
                        prop.className ("text-sm " + Tokens.Text.muted)
                        prop.text msgs.PreflightUnavailable
                    ]
                else
                    outcomesTable msgs snapshot.Outcomes
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

let private tabBar (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) =
    Html.div [
        prop.className [ "px-4"; Tokens.Bg.card ]
        prop.children [
            Tabs.bar
                [ LiveHealthTab, msgs.LiveHealthTab; PreflightTab, msgs.PreflightTab ]
                model.ActiveTab
                (SwitchTab >> dispatch)
        ]
    ]

/// The module's page, over the catalog section the view hands it.
let private body (msgs: HealthMonitorMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    let content =
        match model.ActiveTab with
        | LiveHealthTab -> liveHealthTabView msgs model dispatch
        | PreflightTab -> preflightTabView msgs model dispatch

    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ tabBar msgs model dispatch; content ]
    ]

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(body ctx.Messages.HealthMonitor model ctx.Dispatch)

// ─── Module creation ─────────────────────────────────────────────────

/// Phase 879 — this built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.HealthMonitor"

/// The built-in health monitor admin (`ShellSlots.builtIn` builds its module). The
/// module fills the `HealthMonitor` shell slot (Default / Configured),
/// which the shell admits on every surface; its `PlatformAdminOnly` nav
/// role hides it from non-admins.
///
/// **Phase 4b re-gate (commit 4f.1, 2026-05-10).** The server-side
/// handler now gates on `AccessContext.canModifyPlatformConfig`
/// (Platform Admin role) rather than per-team Owner/Admin — probes
/// are deployment-wide data, so the gate is too. The sidebar group is
/// "Platform Management" (not the previous "Admin") so the shell's
/// role-gated sidebar filter (commit 4f.2) hides the entry from
/// non-admin callers. Existing Team Admins relying on HealthMonitor lose access
/// until they're also assigned `PlatformRole.PlatformAdmin`.
///
/// **Phase 9x — regrouped under "Observability"** (operator decision
/// 2026-09-23), beside the Datadog readback and self-hosted
/// observability modules: three observability modules, one group. The
/// group joined the platform-admin allow-list in the same change, so
/// the entry keeps its administration area and a team-less platform
/// admin still reaches it; the gate itself is the typed `NavRole`.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Health Monitor"
        Icon = ToolUp.Platform.Icons.health
    }
    Group = Some ToolUp.Platform.SidebarVisibility.ObservabilitySidebarGroup
    NavRole = Some ToolUp.Platform.NavRole.PlatformAdminOnly
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = fun _ -> init ()
    Update = update
    View = BuiltInView.Page view
    Extend = id
}

/// Phase 573.B — the administration-landing tile of the health-monitor
/// slot, declared here rather than in the landing page so the page never
/// names a module (GP 9).
///
/// Phase 879 — the tile belongs to the SLOT, not to this module: the
/// shell builds it for whichever module fills the slot (`owner`), so a
/// deployment that puts its own module in the slot keeps the tile, and
/// the tile's click-through, name and icon are the replacement's. An
/// empty slot contributes nothing; a replacement that wires its own tile
/// through `ClientConfig.Composition.Handlers.AdminTileContributors` keeps that one
/// instead (`ShellSlots.adminTiles`).
///
/// `Title` / `Icon` are the owning module's own, so a relabelled
/// deployment's tile matches its rail entry. The headline key is read
/// from the shared `HomeWidgetContext.Data` bag — supply
/// `"_sdk.admin.health"` from an `IHomeWidgetDataProvider` to put a live
/// status on the tile.
let adminTile (owner: ModuleDefinition) : AdminTile = {
    OwnerModuleId = owner.Id
    Widget = {
        Id = "_sdk.tile.health"
        Title = owner.Name
        Icon = owner.Icon
        Weight = 30
        Body =
            AdminTileBody.summary
                "_sdk.admin.health"
                "Liveness, readiness and dependency probes for this deployment, plus the startup preflight report."
    }
}