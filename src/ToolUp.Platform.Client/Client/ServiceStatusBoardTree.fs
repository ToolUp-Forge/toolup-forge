// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The service status board as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `ServiceStatusBoardUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// The six sections are named panels in a fixed order. A section's refresh
/// replaces that section's summary and leaves the other five the objects they
/// were, so each panel is kept whole on its summary and its own flags
/// (collapsed, refreshing, changed): one section's refresh builds that panel
/// alone. A section whose severity changed fades its badge in.
module internal ServiceStatusBoardTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open ServiceStatusBoardUI
open KeepScoped

/// An instant as the universal sortable form, from its components.
let private universal (at: System.DateTime) =
    sprintf "%04d-%02d-%02d %02d:%02d:%02dZ" at.Year at.Month at.Day at.Hour at.Minute at.Second

let private badge (id: string) (variant: BadgeVariant) (label: string) : Node<'Msg> =
    Fuaran.badge id {
        Label = Nodes.literal label
        Variant = variant
    }

let private severityBadge (id: string) (msgs: ServiceStatusBoardMessages) (summary: SectionSummary) =
    if summary.Disabled then
        badge id BadgeVariant.Neutral msgs.SeverityDisabled
    else
        match summary.Severity with
        | StatusSeverity.Ok -> badge id BadgeVariant.Success msgs.SeverityOk
        | StatusSeverity.Warn -> badge id BadgeVariant.Warning msgs.SeverityWarn
        | StatusSeverity.Error -> badge id BadgeVariant.Critical msgs.SeverityError

let private overallBadge (msgs: ServiceStatusBoardMessages) (overall: OverallStatus) =
    match overall with
    | AllOk -> badge "status-overall" BadgeVariant.Success msgs.AllSystemsOk
    | DegradedBy sections -> badge "status-overall" BadgeVariant.Warning (msgs.DegradedBy(String.concat ", " sections))
    | UnhealthyBy sections ->
        badge "status-overall" BadgeVariant.Critical (msgs.UnhealthyBy(String.concat ", " sections))

/// Two children on one line: the first takes the width, the second sits at
/// the right edge.
let private split (id: string) (left: Node<'Msg>) (right: Node<'Msg>) : Node<'Msg> =
    Fuaran.box id {
        Defaults.box with
            Layout = BoxLayout.Grid(2, Some "minmax(0, 1fr) auto", Some 16)
            Children = [ left; right ]
    }

// ─── A section's panel ───────────────────────────────────────────────

type private PanelContext = {
    Msgs: ServiceStatusBoardMessages
    Section: string
    Summary: SectionSummary
    Collapsed: bool
    Refreshing: bool
    Flipped: bool
}

let private samePanel (a: PanelContext) (b: PanelContext) =
    sameObject a.Msgs b.Msgs
    && sameObject a.Summary b.Summary
    && a.Collapsed = b.Collapsed
    && a.Refreshing = b.Refreshing
    && a.Flipped = b.Flipped

let private panel (context: PanelContext) : Node<Msg> =
    let msgs = context.Msgs
    let summary = context.Summary
    let id = Nodes.Ids.keyed "status-section" context.Section

    let severity =
        if context.Flipped then
            severityBadge (Nodes.Ids.child id "severity-changed") msgs summary
            |> Node.withMotion Motion.FadeInOnMount
        else
            severityBadge (Nodes.Ids.child id "severity") msgs summary

    Fuaran.box id {
        Defaults.box with
            Role = BoxRole.Card
            Children = [
                split
                    (Nodes.Ids.child id "head")
                    (Nodes.Actions.button
                        (Nodes.Ids.child id "toggle")
                        ButtonVariant.Tertiary
                        context.Section
                        false
                        (ToggleCollapsed context.Section)
                     |> Node.withExtraAttribute "aria-expanded" (if context.Collapsed then "false" else "true"))
                    (Nodes.Actions.button
                        (Nodes.Ids.child id "refresh")
                        ButtonVariant.Secondary
                        (if context.Refreshing then msgs.Refreshing else msgs.Refresh)
                        context.Refreshing
                        (RefreshSection context.Section))
                // The severity stays in view while the section is collapsed;
                // the headline and the details are what collapsing hides.
                Nodes.Layout.row (Nodes.Ids.child id "status") [
                    severity
                    if not context.Collapsed then
                        Nodes.text
                            (Nodes.Ids.child id "headline")
                            (if summary.Disabled then
                                 summary.DisabledReason
                             else
                                 summary.Headline)
                ]
                if not context.Collapsed && not summary.Details.IsEmpty then
                    Nodes.Layout.column (Nodes.Ids.child id "details") [
                        // A summary arrives whole and its details are strings: a
                        // detail's place in it names it.
                        for i, detail in List.indexed summary.Details do
                            Nodes.text (Nodes.Ids.keyed (Nodes.Ids.child id "detail") (string i)) detail
                    ]
            ]
    }

/// One kept panel per section, in the board's order.
let private panels: (string * One<PanelContext, Msg>) list = [
    for section in
        [
            ServiceStatusSnapshot.HealthSection
            ServiceStatusSnapshot.PreflightSection
            ServiceStatusSnapshot.DriftSection
            ServiceStatusSnapshot.RateLimitSection
            ServiceStatusSnapshot.JobQueueSection
            ServiceStatusSnapshot.SmokeTestSection
        ] -> section, One(samePanel, panel)
]

// ─── The page ────────────────────────────────────────────────────────

/// The page: the heading and its refresh, then the overall status and the six
/// panels, or the state the board is in. Every node sends its message through
/// the tree.
let tree (msgs: ServiceStatusBoardMessages) (model: Model) : Node<Msg> =
    let loading = Remote.isLoading model.Snapshot

    Nodes.Layout.column "status-board" [
        split
            "status-header"
            (Nodes.Layout.column "status-titles" [
                Nodes.heading "status-heading" 2 msgs.Heading
                Nodes.text "status-subheading" msgs.Subheading
            ])
            (Nodes.Actions.button
                "status-refresh-all"
                ButtonVariant.Secondary
                (if loading then msgs.Refreshing else msgs.RefreshAll)
                loading
                RefreshAll)
        match model.Snapshot with
        | Remote.NotAsked
        | Remote.Loading
        | Remote.Reloading _ -> Nodes.text "status-loading" msgs.Loading
        | Remote.Failed error ->
            Fuaran.callout "status-failed" {
                Defaults.callout with
                    Body = Nodes.literal error
                    Tone = ToneVariant.Critical
            }
        | Remote.Loaded snap ->
            Fuaran.box "status-overall-line" {
                Defaults.box with
                    Role = BoxRole.Card
                    Children = [
                        split
                            "status-overall-split"
                            (overallBadge msgs snap.Overall)
                            (Nodes.text "status-generated" (msgs.GeneratedAt(universal snap.GeneratedAt)))
                    ]
            }

            Nodes.Layout.column "status-sections" [
                for section, kept in panels do
                    kept.Node {
                        Msgs = msgs
                        Section = section
                        Summary = sectionOf msgs snap section
                        Collapsed = model.Collapsed.Contains section
                        Refreshing = model.Refreshing.Contains section
                        Flipped = model.RecentlyFlipped.Contains section
                    }
            ]
    ]

/// The deferred component `ServiceStatusBoardUI`'s view mounts.
let Surface (props: ServiceStatusBoardUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Model))