// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The health monitor as a Fuaran-UI tree, built and drawn in a file of its
/// own, which `HealthMonitorUI`'s view mounts through `TreeSurface.deferred`
/// (the shell reads that module at boot; see `TeamManagerTree` for the trade).
/// Nothing imports this file statically.
///
/// The probes and the validator outcomes are tables kept per row, every row id
/// from the probe's or validator's name. The degraded capabilities, the
/// AI-denial rollup and the scheduler telemetry arrive whole with their read
/// and never change in place, so each card is kept whole on the record it
/// shows. A probe whose status changed since the previous snapshot fades its
/// status in, so the change is the first thing the eye lands on.
module internal HealthMonitorTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open HealthMonitorUI
open KeepScoped

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

/// The three-colour status of a probe or a validator: the same status reads
/// the same on both tabs.
let private statusBadge (id: string) (status: string) : Node<'Msg> =
    let variant =
        match status with
        | "Healthy"
        | "Ok" -> BadgeVariant.Success
        | "Degraded"
        | "Warning" -> BadgeVariant.Warning
        | "Unhealthy"
        | "Error" -> BadgeVariant.Critical
        | _ -> BadgeVariant.Neutral

    badge id variant status

/// Two children on one line: the first takes the width, the second sits at
/// the right edge.
let private split (id: string) (left: Node<'Msg>) (right: Node<'Msg>) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
            Children = [ left; right ]
    }

/// A titled card inside a tab: its title at heading level 3, under the tab's
/// own heading.
let private card (id: string) (title: string) (aside: Node<'Msg> option) (children: Node<'Msg> list) : Node<'Msg> =
    let heading = Nodes.heading (Nodes.Ids.child id "title") 3 title

    Fuaran.box id {
        Defaults.box with
            Role = BoxRole.Card
            Children =
                (match aside with
                 | Some aside -> split (Nodes.Ids.child id "head") heading aside
                 | None -> heading)
                :: children
    }

/// Labels over their values, one column each.
let private readings (id: string) (pairs: (string * Node<'Msg>) list) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Grid(List.length pairs, None, Some 16)
            Children = [
                for i, (label, _) in List.indexed pairs do
                    Nodes.text (Nodes.Ids.child id (sprintf "label-%d" i)) label
                for _, value in pairs do
                    value
            ]
    }

/// Labels beside their values, one pair a line.
let private definitions (id: string) (pairs: (string * string) list) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Grid(2, Some "max-content minmax(0, 1fr)", Some 12)
            Children = [
                for i, (label, value) in List.indexed pairs do
                    Nodes.text (Nodes.Ids.child id (sprintf "term-%d" i)) label
                    Nodes.text (Nodes.Ids.child id (sprintf "value-%d" i)) value
            ]
    }

/// A tab's heading and footnote on the left, its refresh on the right, which
/// shows `msgs.Refreshing` and is inert while its read is in flight.
let private tabHeader
    (id: string)
    (msgs: HealthMonitorMessages)
    (heading: string)
    (footnote: string)
    (label: string)
    (loading: bool)
    (msg: Msg)
    =
    split
        id
        (Nodes.Layout.column (Nodes.Ids.child id "titles") [
            Nodes.heading (Nodes.Ids.child id "heading") 2 heading
            Nodes.text (Nodes.Ids.child id "footnote") footnote
        ])
        (Nodes.Actions.button
            (Nodes.Ids.child id "refresh")
            ButtonVariant.Secondary
            (if loading then msgs.Refreshing else label)
            loading
            msg)

let private failed (id: string) (error: string) : Node<'Msg> =
    Fuaran.callout id {
        Defaults.callout with
            Body = Nodes.literal error
            Tone = ToneVariant.Critical
    }

// ─── The Live tab's cards ────────────────────────────────────────────

type private CardContext<'T> = {
    Msgs: HealthMonitorMessages
    Shown: 'T
}

let private sameCard (a: CardContext<'T>) (b: CardContext<'T>) =
    sameObject a.Msgs b.Msgs && sameObject a.Shown b.Shown

/// The capabilities that wired best-effort and failed: the deployment is up,
/// a capability is silently down. Drawn only when there is one.
let private degradedCard =
    One(
        sameCard,
        fun (context: CardContext<DegradedCapability list>) ->
            let msgs = context.Msgs

            card "health-degraded" (msgs.DegradedCapabilities context.Shown.Length) None [
                Fuaran.callout "health-degraded-help" {
                    Defaults.callout with
                        Body = Nodes.literal msgs.DegradedCapabilitiesHelp
                        Tone = ToneVariant.Critical
                }
                Nodes.Layout.column "health-degraded-list" [
                    for d in context.Shown do
                        let id = Nodes.Ids.keyed "health-degraded" d.Capability

                        Fuaran.box id {
                            Defaults.box with
                                Role = BoxRole.Card
                                Children = [
                                    split
                                        (Nodes.Ids.child id "head")
                                        (Nodes.text (Nodes.Ids.child id "name") d.Capability)
                                        (Nodes.text
                                            (Nodes.Ids.child id "since")
                                            (msgs.DegradedSince(universal d.DegradedSince.UtcDateTime)))
                                    definitions (Nodes.Ids.child id "detail") [
                                        msgs.DegradedReason, d.Reason
                                        msgs.DegradedImpact, d.Impact
                                        msgs.Remediation, d.Remediation
                                    ]
                                ]
                        }
                ]
            ]
    )

/// The rolling AI action-denial rollup. Drawn once the deployment has recorded
/// a denial, and kept after, so a window that has gone quiet says so.
let private denialCard =
    One(
        sameCard,
        fun (context: CardContext<AIDenialRollup>) ->
            let msgs = context.Msgs
            let rollup = context.Shown
            let active = rollup.TotalDenialsInWindow > 0

            let groups (id: string) (heading: string) (rows: AIDenialGroupCount list) =
                Nodes.Layout.column id [
                    Nodes.text (Nodes.Ids.child id "heading") heading
                    for row in List.truncate 5 rows do
                        let rowId = Nodes.Ids.keyed id row.Key

                        split
                            rowId
                            (Nodes.text (Nodes.Ids.child rowId "key") row.Key)
                            (Nodes.text (Nodes.Ids.child rowId "count") (string row.Count))
                ]

            card
                "health-denials"
                (msgs.AIDenials rollup.TotalDenialsInWindow)
                (Some(
                    Nodes.text
                        "health-denials-rate"
                        (msgs.AIDenialsRate rollup.WindowMinutes (sprintf "%.2f" rollup.DenialsPerMinute))
                ))
                [
                    if active then
                        Fuaran.callout "health-denials-help" {
                            Defaults.callout with
                                Body = Nodes.literal msgs.AIDenialsHelp
                                Tone = ToneVariant.Warning
                        }
                    else
                        Nodes.text "health-denials-help" msgs.AIDenialsHelp

                    if not active then
                        Nodes.text "health-denials-quiet" msgs.AIDenialsQuiet
                    else
                        Nodes.Layout.grid "health-denials-groups" 2 [
                            if not rollup.ByToolName.IsEmpty then
                                groups "health-denials-tool" msgs.AIDenialsByTool rollup.ByToolName
                            if not rollup.ByActiveModule.IsEmpty then
                                groups "health-denials-module" msgs.AIDenialsByModule rollup.ByActiveModule
                        ]

                        if not rollup.RecentDenials.IsEmpty then
                            Nodes.Layout.column "health-denials-recent" [
                                Nodes.text "health-denials-recent-heading" msgs.AIDenialsRecent
                                // The rollup arrives whole and its recent denials carry
                                // no id of their own: a denial's place in it names it.
                                for i, d in List.indexed (List.truncate 5 rollup.RecentDenials) do
                                    let id = Nodes.Ids.keyed "health-denials-recent" (string i)

                                    Fuaran.box id {
                                        Defaults.box with
                                            Role = BoxRole.Card
                                            Children = [
                                                split
                                                    (Nodes.Ids.child id "head")
                                                    (Nodes.text
                                                        (Nodes.Ids.child id "where")
                                                        $"{d.ToolName} · {d.ActiveModule}")
                                                    (Nodes.text (Nodes.Ids.child id "at") (universal d.OccurredAt))
                                                Nodes.text (Nodes.Ids.child id "reason") d.Reason
                                            ]
                                    }
                            ]
                ]
    )

/// The job scheduler's missed ticks, when a scheduler is registered. A miss
/// turns the count into a warning.
let private schedulerCard =
    One(
        sameCard,
        fun (context: CardContext<JobSchedulerTelemetryView>) ->
            let msgs = context.Msgs
            let view = context.Shown
            let hasMiss = view.TickMissedCount60Min > 0 || view.LastTickMissedAt.IsSome
            let none = "—"

            card
                "health-scheduler"
                msgs.SchedulerDriftHeading
                (Some(Nodes.text "health-scheduler-as-of" (msgs.AsOf(universal view.GeneratedAt))))
                [
                    Nodes.text "health-scheduler-help" msgs.SchedulerLagHelp
                    readings "health-scheduler-readings" [
                        msgs.SchedulerMissed60m,
                        (if hasMiss then
                             badge "health-scheduler-missed" BadgeVariant.Warning (string view.TickMissedCount60Min)
                         else
                             Nodes.text "health-scheduler-missed" (string view.TickMissedCount60Min))
                        msgs.SchedulerLastDrift,
                        Nodes.text
                            "health-scheduler-drift"
                            (view.LastDriftMs |> Option.map (sprintf "%d ms") |> Option.defaultValue none)
                        msgs.SchedulerLastMissAt,
                        Nodes.text
                            "health-scheduler-miss-at"
                            (view.LastTickMissedAt |> Option.map universal |> Option.defaultValue none)
                    ]
                ]
    )

// ─── The probes ──────────────────────────────────────────────────────

let private probeColumns (msgs: HealthMonitorMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "status" msgs.ColumnStatus 2
    Nodes.Table.column "probe" msgs.ColumnProbe 3
    Nodes.Table.column "kind" msgs.ColumnKind 2
    Nodes.Table.column "timeout" msgs.ColumnTimeout 2
    Nodes.Table.column "elapsed" msgs.ColumnElapsed 2
    Nodes.Table.column "message" msgs.ColumnMessage 5
]

type private ProbeContext = {
    Msgs: HealthMonitorMessages
    Flipped: Set<string>
}

/// One probe. A status that changed since the previous snapshot is a fresh
/// node, so it fades in.
let private probeRows =
    Rows(
        (fun (a: ProbeContext) b -> sameObject a.Msgs b.Msgs && sameObject a.Flipped b.Flipped),
        fun context (probe: HealthProbeView) ->
            let id = Nodes.Ids.keyed "health-probe" probe.Name

            let cell part children =
                Nodes.Table.cell (Nodes.Ids.child id part) children

            let text part value =
                Nodes.text (Nodes.Ids.child id (part + "-text")) value

            let status =
                if context.Flipped.Contains probe.Name then
                    statusBadge (Nodes.Ids.child id "status-changed") probe.Status
                    |> Node.withMotion Motion.FadeInOnMount
                else
                    statusBadge (Nodes.Ids.child id "status-badge") probe.Status

            Nodes.Table.row id (probeColumns context.Msgs) [
                cell "status" [ status ]
                cell "probe" [ text "probe" probe.Name ]
                cell "kind" [ text "kind" probe.Kind ]
                cell "timeout" [ text "timeout" $"{probe.TimeoutMs} ms" ]
                cell "elapsed" [ text "elapsed" $"{probe.ElapsedMs} ms" ]
                cell "message" [ text "message" probe.Message ]
            ]
    )

let private liveTab (msgs: HealthMonitorMessages) (model: Model) : Node<Msg> =
    Nodes.Layout.column "health-live" [
        tabHeader
            "health-live-header"
            msgs
            msgs.LiveHealthHeading
            msgs.ProbesFootnote
            msgs.Refresh
            (Remote.isLoading model.Live)
            RefreshLive
        // The most urgent first: a capability that is down.
        match model.Degraded with
        | Remote.Loaded entries when not entries.IsEmpty -> degradedCard.Node { Msgs = msgs; Shown = entries }
        | _ -> ()
        match model.AIDenials with
        | Remote.Loaded(Some rollup) when rollup.TotalDenialsAllTime > 0 ->
            denialCard.Node { Msgs = msgs; Shown = rollup }
        | _ -> ()
        match model.SchedulerTelemetry with
        | Remote.Loaded view when view.HasScheduler -> schedulerCard.Node { Msgs = msgs; Shown = view }
        | _ -> ()
        match model.Live with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "health-live-loading" msgs.Loading
        | Remote.Failed error -> failed "health-live-failed" error
        | Remote.Loaded snapshot ->
            Nodes.text
                "health-live-generated"
                (msgs.GeneratedAt (universal snapshot.GeneratedAt) snapshot.Probes.Length)

            if snapshot.Probes.IsEmpty then
                Nodes.text "health-probes-none" msgs.NoProbes
            else
                Nodes.Table.table
                    "health-probes"
                    msgs.LiveHealthHeading
                    (probeColumns msgs)
                    (probeRows.List
                        {
                            Msgs = msgs
                            Flipped = model.RecentlyFlipped
                        }
                        snapshot.Probes)
    ]

// ─── The preflight outcomes ──────────────────────────────────────────

let private outcomeColumns (msgs: HealthMonitorMessages) : Nodes.Table.Column list = [
    Nodes.Table.column "status" msgs.ColumnStatus 2
    Nodes.Table.column "validator" msgs.ColumnValidator 4
    Nodes.Table.column "elapsed" msgs.ColumnElapsed 2
    Nodes.Table.column "message" msgs.ColumnMessage 6
]

let private outcomeRows =
    Rows(
        (fun (a: HealthMonitorMessages) b -> sameObject a b),
        fun msgs (outcome: PreflightOutcomeView) ->
            let id = Nodes.Ids.keyed "health-outcome" outcome.Name

            let cell part children =
                Nodes.Table.cell (Nodes.Ids.child id part) children

            let text part value =
                Nodes.text (Nodes.Ids.child id (part + "-text")) value

            Nodes.Table.row id (outcomeColumns msgs) [
                cell "status" [ statusBadge (Nodes.Ids.child id "status-badge") outcome.Status ]
                cell "validator" [ text "validator" outcome.Name ]
                cell "elapsed" [ text "elapsed" $"{outcome.ElapsedMs} ms" ]
                cell "message" [ text "message" outcome.Message ]
            ]
    )

let private preflightTab (msgs: HealthMonitorMessages) (model: Model) : Node<Msg> =
    Nodes.Layout.column "health-preflight" [
        tabHeader
            "health-preflight-header"
            msgs
            msgs.PreflightHeading
            msgs.PreflightFootnote
            msgs.Refetch
            (Remote.isLoading model.Preflight)
            RefreshPreflight
        match model.Preflight with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "health-preflight-loading" msgs.Loading
        | Remote.Failed error -> failed "health-preflight-failed" error
        | Remote.Loaded snapshot when not snapshot.HasSnapshot ->
            Nodes.text "health-preflight-unavailable" msgs.PreflightUnavailable
        | Remote.Loaded snapshot when snapshot.Outcomes.IsEmpty -> Nodes.text "health-outcomes-none" msgs.NoValidators
        | Remote.Loaded snapshot ->
            Nodes.Table.table
                "health-outcomes"
                msgs.PreflightHeading
                (outcomeColumns msgs)
                (outcomeRows.List msgs snapshot.Outcomes)
    ]

// ─── The page ────────────────────────────────────────────────────────

let private tabs = [ LiveHealthTab; PreflightTab ]

/// The page: the two tabs with the active one's panel. Every node sends its
/// message through the tree.
let tree (msgs: HealthMonitorMessages) (model: Model) : Node<Msg> =
    let panel (tab: Tab) =
        Nodes.Layout.column (sprintf "health-tab-%A" tab) [
            if tab = model.ActiveTab then
                match tab with
                | LiveHealthTab -> liveTab msgs model
                | PreflightTab -> preflightTab msgs model
        ]

    Fuaran.tabs "health-tabs" {
        Defaults.tabs with
            ActiveIndex = Binding.Static(Some(List.findIndex ((=) model.ActiveTab) tabs))
            OnSelect = Some(fun i -> Action.dispatch (SwitchTab tabs[i]))
            TabHeaders =
                Some [
                    for label in [ msgs.LiveHealthTab; msgs.PreflightTab ] ->
                        {
                            Label = Nodes.literal label
                            Icon = None
                            Disabled = None
                        }
                ]
            Children = List.map panel tabs
    }

/// The deferred component `HealthMonitorUI`'s view mounts.
let Surface (props: HealthMonitorUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model))