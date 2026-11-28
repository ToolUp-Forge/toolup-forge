// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ServiceStatusBoardUI

open Fuaran.MVU
open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Platform

// ─── ServiceStatusBoard admin module ─────────────────────────────────
//
// Single-page admin module that aggregates every operator-facing
// observability surface (Health, Preflight, Drift, RateLimit, JobQueue,
// SmokeTest) into one composite snapshot. Mirrors `HealthMonitorUI`'s
// patterns (proxy built per call, a status per section, refresh buttons)
// and draws six collapsible per-section panels under one top-line
// `OverallStatus` pill.

type Model = {
    Snapshot: Remote<ServiceStatusSnapshot, string>
    /// Sections the user has collapsed. Default empty (all expanded);
    /// the user clicks the header to toggle.
    Collapsed: Set<string>
    /// Per-section in-flight refresh tracker — used to disable the
    /// per-section refresh button while its targeted call is pending.
    Refreshing: Set<string>
    /// Sections whose status flipped between the prior and the most recent
    /// snapshot: their status fades in, so an operator spots a freshly
    /// changed section without re-reading every panel. Cleared on the next
    /// refresh.
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

let internal sectionOf
    (msgs: ServiceStatusBoardMessages)
    (snap: ServiceStatusSnapshot)
    (name: string)
    : SectionSummary =
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

/// The six sections, in the board's order.
let internal sections = [
    ServiceStatusSnapshot.HealthSection
    ServiceStatusSnapshot.PreflightSection
    ServiceStatusSnapshot.DriftSection
    ServiceStatusSnapshot.RateLimitSection
    ServiceStatusSnapshot.JobQueueSection
    ServiceStatusSnapshot.SmokeTestSection
]

/// Compute the set of section names whose severity / disabled state
/// flipped between the prior and new snapshot: their status fades in.
let private flippedSections
    (msgs: ServiceStatusBoardMessages)
    (prev: ServiceStatusSnapshot option)
    (next: ServiceStatusSnapshot)
    : Set<string> =
    match prev with
    | None -> Set.empty
    | Some p ->
        sections
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
    // The board is kept while the refresh is in flight: the answer is
    // compared with it to mark the sections whose status changed.
    | RefreshAll ->
        {
            model with
                Snapshot = Remote.reload model.Snapshot
        },
        loadSnapshot ()

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

            // A section answering while Refresh all is in flight keeps the
            // board in flight.
            let snapshot =
                match model.Snapshot with
                | Remote.Reloading _ -> Remote.Reloading withOverall
                | _ -> Remote.Loaded withOverall

            {
                model with
                    Snapshot = snapshot
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

// ─── View ────────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: ServiceStatusBoardMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./ServiceStatusBoardTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "ServiceStatusBoardTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.ServiceStatusBoard
            Locale = ctx.Locale
        |}
    )

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