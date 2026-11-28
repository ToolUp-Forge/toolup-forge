// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module HealthMonitorUI

open Fuaran.MVU
open System
open Fable.Core
open Fable.Core.JsInterop
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
    /// The most recent job-scheduler missed-tick telemetry.
    /// Loaded alongside `Live` so the operator sees both on the Live
    /// tab without a second click. `HasScheduler = false` (carried
    /// inside the view) suppresses the inline card.
    SchedulerTelemetry: Remote<JobSchedulerTelemetryView, string>
    /// The degraded-capability set from `GetDegradedCapabilities`.
    /// Loaded alongside `Live`; empty on a healthy deployment, in which
    /// case the card is suppressed entirely (GP 13).
    Degraded: Remote<DegradedCapability list, string>
    /// The rolling AI action-denial rollup from
    /// `GetAIDenialRollup`. Loaded alongside `Live`. `Loaded None` means
    /// no AI companion is composed (the `IAIDenialRollupProbe` seam is
    /// absent from DI) and the card is suppressed — distinct from
    /// `Loaded (Some rollup)` with a zero count, which is a real
    /// "nothing is being refused" reading and worth showing.
    AIDenials: Remote<AIDenialRollup option, string>
    /// Names of probes whose status changed between the prior and
    /// most recent live snapshot: their status fades in, so an admin
    /// spotting a freshly-flipped probe doesn't have to re-read the whole
    /// table. Cleared on the next refresh.
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

let internal loadLive () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IHealthMonitorApi.GetCurrentHealth"
            healthMonitorApi.GetCurrentHealth
            ()
            (RemoteEffect.flatten >> LiveLoaded)
    )
]

let internal loadPreflight () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IHealthMonitorApi.GetPreflightSnapshot"
            healthMonitorApi.GetPreflightSnapshot
            ()
            (RemoteEffect.flatten >> PreflightLoaded)
    )
]

let internal loadSchedulerTelemetry () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IHealthMonitorApi.GetJobSchedulerTelemetry"
            healthMonitorApi.GetJobSchedulerTelemetry
            ()
            (RemoteEffect.flatten >> SchedulerTelemetryLoaded)
    )
]

let internal loadDegraded () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IHealthMonitorApi.GetDegradedCapabilities"
            healthMonitorApi.GetDegradedCapabilities
            ()
            (RemoteEffect.flatten >> DegradedLoaded)
    )
]

let internal loadAIDenials () : Step<Msg, RemoteEffect<Msg>> list = [
    Do(
        RemoteEffect.call
            "IHealthMonitorApi.GetAIDenialRollup"
            healthMonitorApi.GetAIDenialRollup
            ()
            (RemoteEffect.flatten >> AIDenialsLoaded)
    )
]

let init () : Model * Step<Msg, RemoteEffect<Msg>> list =
    let model = {
        ActiveTab = LiveHealthTab
        Live = Remote.Loading
        Preflight = Remote.Loading
        SchedulerTelemetry = Remote.Loading
        Degraded = Remote.Loading
        AIDenials = Remote.Loading
        RecentlyFlipped = Set.empty
    }

    (model,
     loadLive ()
     @ loadPreflight ()
     @ loadSchedulerTelemetry ()
     @ loadDegraded ()
     @ loadAIDenials ())

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

/// The transitions, with their remote calls as values.
let update (msg: Msg) (model: Model) : Model * Step<Msg, RemoteEffect<Msg>> list =
    match msg with
    | SwitchTab tab -> { model with ActiveTab = tab }, []

    | RefreshLive ->
        // Refresh both panels on the Live tab in one click — scheduler
        // telemetry is a pull off an in-memory counter (cheap) so
        // re-fetching alongside the probe sweep is free. The live reading
        // is kept while the sweep is in flight: the answer is compared
        // with it to mark the probes whose status changed.
        {
            model with
                Live = Remote.reload model.Live
                SchedulerTelemetry = Remote.Loading
                Degraded = Remote.Loading
                AIDenials = Remote.Loading
        },
        loadLive () @ loadSchedulerTelemetry () @ loadDegraded () @ loadAIDenials ()

    | LiveLoaded(Ok snapshot) ->
        let flipped = flippedNames (Remote.toOption model.Live) snapshot

        {
            model with
                Live = Remote.Loaded snapshot
                RecentlyFlipped = flipped
        },
        []

    | LiveLoaded(Error msg) -> { model with Live = Remote.Failed msg }, []

    | RefreshPreflight ->
        {
            model with
                Preflight = Remote.Loading
        },
        loadPreflight ()

    | PreflightLoaded result ->
        {
            model with
                Preflight = Remote.ofResult result
        },
        []

    | SchedulerTelemetryLoaded result ->
        {
            model with
                SchedulerTelemetry = Remote.ofResult result
        },
        []

    | DegradedLoaded result ->
        {
            model with
                Degraded = Remote.ofResult result
        },
        []

    | AIDenialsLoaded result ->
        {
            model with
                AIDenials = Remote.ofResult result
        },
        []

// ─── View ────────────────────────────────────────────────────────────

/// What the deferred tree component takes.
type internal SurfaceProps = {|
    Model: Model
    Dispatch: Msg -> unit
    Messages: HealthMonitorMessages
    Locale: string
|}

[<Emit("$0.then(m => m.Surface)")>]
let private surfaceOf (loaded: JS.Promise<obj>) : JS.Promise<obj> = jsNative

/// The tree's file, fetched on demand with the node vocabulary and the
/// renderer, so the shell's first load pays nothing for them.
let private loadSurface () : JS.Promise<obj> =
    surfaceOf (importDynamic "./HealthMonitorTree.fs")

let private view (model: Model) (ctx: RenderContext<Msg>) : PageContent =
    PageContent.FullWidth(
        TreeSurface.deferred "HealthMonitorTree.Surface" loadSurface {|
            Model = model
            Dispatch = ctx.Dispatch
            Messages = ctx.Messages.HealthMonitor
            Locale = ctx.Locale
        |}
    )

// ─── Module creation ─────────────────────────────────────────────────

/// This built-in's module id. Its shell slot reads it
/// (`ShellSlot.FilledModuleId`) to know which module fills the slot
/// without building one; `builtIn` declares the module under it.
let moduleId = "_sdk.HealthMonitor"

/// The built-in health monitor admin (`ShellSlots.builtIn` builds its module). The
/// module fills the `HealthMonitor` shell slot (Default / Configured),
/// which the shell admits on every surface; its `PlatformAdminOnly` nav
/// role hides it from non-admins.
///
/// **The gate.** The server-side
/// handler gates on `AccessContext.canModifyPlatformConfig`
/// (Platform Admin role) rather than per-team Owner/Admin — probes
/// are deployment-wide data, so the gate is too. The sidebar group is
/// "Platform Management" (not the previous "Admin") so the shell's
/// role-gated sidebar filter hides the entry from
/// non-admin callers. Existing Team Admins relying on HealthMonitor lose access
/// until they're also assigned `PlatformRole.PlatformAdmin`.
///
/// **Grouped under "Observability"**, beside the Datadog readback and self-hosted
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

/// The administration-landing tile of the health-monitor
/// slot, declared here rather than in the landing page so the page never
/// names a module (GP 9).
///
/// The tile belongs to the SLOT, not to this module: the
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