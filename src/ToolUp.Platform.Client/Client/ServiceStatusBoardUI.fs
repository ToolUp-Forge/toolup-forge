// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ServiceStatusBoardUI

open Fuaran.MVU
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform

// ─── ServiceStatusBoard admin module ─────────────────────────────────
//
// Single-page admin module that aggregates every operator-facing
// observability surface (Health, Preflight, Drift, RateLimit, JobQueue,
// SmokeTest) into one composite snapshot. Mirrors `HealthMonitorUI`'s
// patterns (proxy built per call, status pill renderer, refresh button)
// but renders six collapsible per-section panels under one top-line
// `OverallStatus` pill.

/// Set of section names whose status flipped between the prior and
/// most-recent snapshot — surfaced via Tailwind `animate-pulse` for
/// ~1.5s so operators spot freshly-changed sections without re-reading
/// every panel. Cleared on the next refresh.
type Model = {
    Snapshot: Remote<ServiceStatusSnapshot, string>
    /// Sections the user has collapsed. Default empty (all expanded);
    /// the user clicks the header to toggle.
    Collapsed: Set<string>
    /// Per-section in-flight refresh tracker — used to disable the
    /// per-section refresh button while its targeted call is pending.
    Refreshing: Set<string>
    RecentlyFlipped: Set<string>
    /// This module's section of `ClientModuleContext.Messages`,
    /// kept from `init` so `update` authors reader-facing text in the
    /// session's language (a locale switch re-initialises the module).
    Messages: ServiceStatusBoardMessages
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

let internal loadSnapshot () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IServiceStatusBoardApi.GetSnapshot"
            boardApi.GetSnapshot
            ()
            (RemoteEffect.flatten >> SnapshotLoaded)
    )
]

let internal loadSection (msgs: ServiceStatusBoardMessages) (section: string) : Step<Msg, RemoteEffect<Msg>> list =
    let refresh (methodName: string) (proxyCall: unit -> Async<Result<SectionSummary, string>>) = [
        Do(RemoteEffect.call methodName proxyCall () (fun reply -> SectionLoaded(section, RemoteEffect.flatten reply)))
    ]

    match section with
    | s when s = ServiceStatusSnapshot.HealthSection ->
        refresh "IServiceStatusBoardApi.RefreshHealth" boardApi.RefreshHealth
    | s when s = ServiceStatusSnapshot.PreflightSection ->
        refresh "IServiceStatusBoardApi.RefreshPreflight" boardApi.RefreshPreflight
    | s when s = ServiceStatusSnapshot.DriftSection ->
        refresh "IServiceStatusBoardApi.RefreshDrift" boardApi.RefreshDrift
    | s when s = ServiceStatusSnapshot.RateLimitSection ->
        refresh "IServiceStatusBoardApi.RefreshRateLimit" boardApi.RefreshRateLimit
    | s when s = ServiceStatusSnapshot.JobQueueSection ->
        refresh "IServiceStatusBoardApi.RefreshJobQueue" boardApi.RefreshJobQueue
    | s when s = ServiceStatusSnapshot.SmokeTestSection ->
        refresh "IServiceStatusBoardApi.RefreshSmokeTest" boardApi.RefreshSmokeTest
    | other -> [ Then(SectionLoaded(section, Error(msgs.UnknownSectionMessage other))) ]

// ─── Init ────────────────────────────────────────────────────────────

let init (ctx: ClientModuleContext) : Model * Step<Msg, RemoteEffect<Msg>> list =
    let model = {
        Snapshot = Remote.Loading
        Collapsed = Set.empty
        Refreshing = Set.empty
        RecentlyFlipped = Set.empty
        Messages = ctx.Messages.ServiceStatusBoard
    }

    (model, loadSnapshot ())

// ─── Helpers ─────────────────────────────────────────────────────────

let private sectionOf (msgs: ServiceStatusBoardMessages) (snap: ServiceStatusSnapshot) (name: string) : SectionSummary =
    match name with
    | s when s = ServiceStatusSnapshot.HealthSection -> snap.Health
    | s when s = ServiceStatusSnapshot.PreflightSection -> snap.Preflight
    | s when s = ServiceStatusSnapshot.DriftSection -> snap.Drift
    | s when s = ServiceStatusSnapshot.RateLimitSection -> snap.RateLimit
    | s when s = ServiceStatusSnapshot.JobQueueSection -> snap.JobQueue
    | s when s = ServiceStatusSnapshot.SmokeTestSection -> snap.SmokeTest
    | _ -> SectionSummary.failure (msgs.UnknownSectionHeadline name) msgs.SectionMappingIncomplete

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
let private flippedSections
    (msgs: ServiceStatusBoardMessages)
    (prev: ServiceStatusSnapshot option)
    (next: ServiceStatusSnapshot)
    : Set<string> =
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
            let prevSec = sectionOf msgs p name
            let nextSec = sectionOf msgs next name

            if prevSec.Disabled <> nextSec.Disabled || prevSec.Severity <> nextSec.Severity then
                Some name
            else
                None)
        |> Set.ofList

// ─── Update ──────────────────────────────────────────────────────────

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | RefreshAll -> { model with Snapshot = Remote.Loading }, loadSnapshot ()

    | SnapshotLoaded(Ok snap) ->
        let flipped = flippedSections model.Messages (Remote.toOption model.Snapshot) snap

        {
            model with
                Snapshot = Remote.Loaded snap
                Refreshing = Set.empty
                RecentlyFlipped = flipped
        },
        []

    | SnapshotLoaded(Error e) ->
        {
            model with
                Snapshot = Remote.Failed e
                Refreshing = Set.empty
        },
        []

    | ToggleCollapsed section ->
        let next =
            if Set.contains section model.Collapsed then
                Set.remove section model.Collapsed
            else
                Set.add section model.Collapsed

        { model with Collapsed = next }, []

    | RefreshSection section ->
        {
            model with
                Refreshing = Set.add section model.Refreshing
        },
        loadSection model.Messages section

    | SectionLoaded(section, Ok summary) ->
        let nextRefreshing = Set.remove section model.Refreshing

        match Remote.toOption model.Snapshot with
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

            let priorSec = sectionOf model.Messages snap section

            let recentlyFlipped =
                if priorSec.Severity <> summary.Severity || priorSec.Disabled <> summary.Disabled then
                    Set.add section (Set.remove section model.RecentlyFlipped)
                else
                    model.RecentlyFlipped

            {
                model with
                    Snapshot = Remote.Loaded withOverall
                    Refreshing = nextRefreshing
                    RecentlyFlipped = recentlyFlipped
            },
            []
        | None ->
            // Per-section refresh hit before the composite snapshot
            // landed. Drop the result and let the composite load
            // populate every section.
            {
                model with
                    Refreshing = nextRefreshing
            },
            []

    | SectionLoaded(section, Error e) ->
        let failureSummary =
            SectionSummary.failure (model.Messages.SectionRefreshFailed section) e

        let nextRefreshing = Set.remove section model.Refreshing

        match Remote.toOption model.Snapshot with
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
                    Snapshot = Remote.Loaded { updated with Overall = overall }
                    Refreshing = nextRefreshing
                    RecentlyFlipped = Set.add section model.RecentlyFlipped
            },
            []
        | None ->
            {
                model with
                    Refreshing = nextRefreshing
            },
            []

// ─── Renderers ───────────────────────────────────────────────────────

let private severityPill (msgs: ServiceStatusBoardMessages) (summary: SectionSummary) =
    let label, cls =
        if summary.Disabled then
            msgs.SeverityDisabled, ("bg-gray-100 border-gray-200 " + Tokens.Text.secondary)
        else
            match summary.Severity with
            | StatusSeverity.Ok -> msgs.SeverityOk, "bg-green-100 text-green-700 border-green-200"
            | StatusSeverity.Warn -> msgs.SeverityWarn, "bg-yellow-100 text-yellow-700 border-yellow-200"
            | StatusSeverity.Error -> msgs.SeverityError, "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block text-xs px-2 py-0.5 rounded border font-medium {cls}"
        prop.text label
    ]

let private overallPill (msgs: ServiceStatusBoardMessages) (overall: OverallStatus) =
    let label, cls =
        match overall with
        | AllOk -> msgs.AllSystemsOk, "bg-green-100 text-green-700 border-green-200"
        | DegradedBy sections ->
            let joined = sections |> String.concat ", "
            msgs.DegradedBy joined, "bg-yellow-100 text-yellow-700 border-yellow-200"
        | UnhealthyBy sections ->
            let joined = sections |> String.concat ", "
            msgs.UnhealthyBy joined, "bg-red-100 text-red-700 border-red-200"

    Html.span [
        prop.className $"inline-block text-sm px-3 py-1 rounded border font-semibold {cls}"
        prop.text label
    ]

let private sectionPanel
    (msgs: ServiceStatusBoardMessages)
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
                                prop.className [ "text-xs font-mono w-4"; Tokens.Text.muted ]
                                prop.text (if isCollapsed then "▸" else "▾")
                            ]
                            Html.span [
                                prop.className [ "text-sm font-semibold"; Tokens.Text.primary ]
                                prop.text section
                            ]
                            severityPill msgs summary
                        ]
                    ]
                    Forms.Button.secondaryWith
                        (Forms.Button.stateOf isRefreshing msgs.Refreshing false)
                        msgs.Refresh
                        (fun () -> dispatch (RefreshSection section))
                ]
            ]
            if not isCollapsed then
                Html.div [
                    prop.className "px-4 py-3"
                    prop.children [
                        Html.p [
                            prop.className [ "text-sm"; Tokens.Text.secondary ]
                            prop.text (
                                if summary.Disabled then
                                    summary.DisabledReason
                                else
                                    summary.Headline
                            )
                        ]
                        if not summary.Details.IsEmpty then
                            Html.ul [
                                prop.className [ "mt-2 text-xs list-disc pl-5 space-y-1"; Tokens.Text.secondary ]
                                prop.children (summary.Details |> List.map (fun d -> Html.li [ prop.text d ]))
                            ]
                    ]
                ]
        ]
    ]

// ─── View ────────────────────────────────────────────────────────────

let private boardView (msgs: ServiceStatusBoardMessages) (model: Model) (dispatch: Msg -> unit) =
    let isLoading = Remote.isLoading model.Snapshot

    Html.div [
        prop.className "flex-1 p-6 overflow-y-auto"
        prop.children [
            Html.div [
                prop.className "flex items-center justify-between mb-4"
                prop.children [
                    Html.div [
                        prop.children [
                            Html.h2 [ prop.className "text-lg font-semibold"; prop.text msgs.Heading ]
                            Html.p [ prop.className [ "text-xs"; Tokens.Text.muted ]; prop.text msgs.Subheading ]
                        ]
                    ]
                    Forms.Button.secondaryWith
                        (Forms.Button.stateOf isLoading msgs.Refreshing false)
                        msgs.RefreshAll
                        (fun () -> dispatch RefreshAll)
                ]
            ]
            match model.Snapshot with
            | Remote.NotAsked
            | Remote.Loading
            | Remote.Reloading _ -> Html.p [ prop.className [ "text-sm"; Tokens.Text.muted ]; prop.text msgs.Loading ]
            | Remote.Failed msg -> StateViews.noticeStatic (Notice.Problem msg)
            | Remote.Loaded snap ->
                Html.div [
                    prop.children [
                        Html.div [
                            prop.className
                                "flex items-center justify-between p-3 mb-4 bg-white border border-border rounded-lg"
                            prop.children [
                                overallPill msgs snap.Overall
                                Html.span [
                                    prop.className [ "text-xs"; Tokens.Text.muted ]
                                    prop.text (msgs.GeneratedAt(snap.GeneratedAt.ToString "u"))
                                ]
                            ]
                        ]
                        Html.div [
                            prop.className "flex flex-col gap-3"
                            prop.children [
                                sectionPanel msgs model dispatch ServiceStatusSnapshot.HealthSection snap.Health
                                sectionPanel msgs model dispatch ServiceStatusSnapshot.PreflightSection snap.Preflight
                                sectionPanel msgs model dispatch ServiceStatusSnapshot.DriftSection snap.Drift
                                sectionPanel msgs model dispatch ServiceStatusSnapshot.RateLimitSection snap.RateLimit
                                sectionPanel msgs model dispatch ServiceStatusSnapshot.JobQueueSection snap.JobQueue
                                sectionPanel msgs model dispatch ServiceStatusSnapshot.SmokeTestSection snap.SmokeTest
                            ]
                        ]
                    ]
                ]
        ]
    ]

/// The module's page, over the catalog section the view hands it.
let private body (msgs: ServiceStatusBoardMessages) (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "flex flex-col h-full"
        prop.children [ boardView msgs model dispatch ]
    ]

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(body ctx.Messages.ServiceStatusBoard model ctx.Dispatch)

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.ServiceStatusBoard"

/// Create the built-in service-status-board admin module. The shell's
/// module fills the `ServiceStatusBoard` shell slot (Default /
/// Configured), which the shell admits on every surface. Grouped under
/// "Platform Management" so the role-gated sidebar filter
/// hides the entry from non-admin callers.
let builtIn: BuiltIn<Model, Msg> = {
    Id = moduleId
    Defaults = {
        Name = "Service Status"
        Icon = ToolUp.Platform.Icons.health
    }
    Group = Some "Platform Management"
    NavRole = Some ToolUp.Platform.NavRole.PlatformAdminOnly
    Area = Product
    Visibility = ToolUp.Platform.Visibility.visibleToAuthenticated
    Init = init
    Update = update
    View = BuiltInView.Page view
    Extend = id
}

/// The administration-landing tile of this built-in's
/// slot (see `HealthMonitorUI.adminTile` for the full rationale): built
/// for whichever module fills the slot (`owner`), so a replacement keeps
/// the tile, with its own id, name and icon.
///
/// Supply `"_sdk.admin.service-status"` from an `IHomeWidgetDataProvider`
/// to lead the tile with a live headline.
let adminTile (owner: ModuleDefinition) : AdminTile = {
    OwnerModuleId = owner.Id
    Widget = {
        Id = "_sdk.tile.service-status"
        Title = owner.Name
        Icon = owner.Icon
        Weight = 40
        Body =
            AdminTileBody.summary
                "_sdk.admin.service-status"
                "One snapshot across health, preflight, config drift, rate limits, the job queue and smoke tests."
    }
}