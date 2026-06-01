// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Elmish
open Elmish.React
open Feliz
open ProcessedDataTypes

// ─── Shell MVU ────────────────────────────────────────────────────

// HMR is always open; the Elmish.HMR overrides of `Program.run` /
// `Program.withReactSynchronous` etc. detect the absence of Vite's
// `import.meta.hot` at runtime and become identity functions in
// production builds. The previous `#if DEBUG`-gated open was removed
// when ToolUp.Platform stopped carrying compile-time gates (App-side
// `#if DEBUG` is unaffected).
open Elmish.HMR

module Client =

    let private log = Logger.forCategory "client.bootstrap"

    // 0.4.1 — boot-time prefetch gates use `Prefetch<unit>` from
    // ToolUp.Elmish (the primitive that codifies the dual-drain
    // pattern). The actual loaded values still live in `ModuleConfigs`
    // / `PlatformConfig` / `ResolvedFlags`; `ConfigsPrefetch` /
    // `FlagsPrefetch` are pure gates flipped from `Pending` to `Loaded
    // ()` as each source resolves. The "re-init exactly once when the
    // last source drains" semantic is enforced by `Prefetch.onAllReady`
    // (consumed in `ConfigsLoaded` / `FlagsLoaded`).
    //
    // The previous `PrefetchKind` enum + `PrefetchPending: Set<...>`
    // bookkeeping is retired — the typed primitive replaces it
    // verbatim, and a future prefetch source (e.g. boot-time tenant
    // resolution) gets its own `Prefetch<unit>` rather than another
    // enum case to add + remove from the set.

    // Public so that outer-program composition in companion packages
    // (e.g. ToolUp.AI.Client's AIClientConfig.fs) can wrap the shell MVU
    // without reaching into private internals.
    type Model = {
        ActiveModuleId: string
        /// Currently-active page route for the active module. `Some r`
        /// when the active module is multi-page (`ClientModule.withPages`);
        /// `None` for legacy single-page modules. Drives `PageViews`
        /// dispatch and the composite sidebar Id used for the active-
        /// border highlight. Switching pages of the same module updates
        /// this field only — `Init` is not re-run, so per-page state is
        /// preserved across navigation.
        ActivePageRoute: string option
        ModuleStates: Map<string, obj>
        /// Server response describing which modules it RBAC-manages and
        /// which of those the caller can access. `None` = not yet loaded
        /// OR the fetch failed — the sidebar falls back to showing every
        /// registered module (permissive, opt-in RBAC). `Some response`
        /// = apply the filter (server-managed modules need membership in
        /// `Accessible`; SDK-built-ins outside `Managed` pass through
        /// unconditionally).
        AccessibleModules: AccessibleModulesResponse option
        /// Persisted config values per module Id. Populated asynchronously
        /// on shell start — empty until `ConfigsLoaded` fires. Modules
        /// read their slice via `ClientModuleContext.Config`.
        ModuleConfigs: Map<string, Map<string, string>>
        /// Persisted `_platform` config values. Same lifecycle as
        /// `ModuleConfigs`; handed to every module via
        /// `ClientModuleContext.PlatformConfig`.
        PlatformConfig: Map<string, string>
        /// Resolved feature-flag snapshot from the initial server
        /// prefetch. Empty until `FlagsLoaded` fires (and for
        /// deployments that declare no flags). Handed to modules via
        /// `ClientModuleContext.Flags`; views read it via the
        /// `FeatureFlags` context mounted around the shell.
        ResolvedFlags: Map<string, FlagValue>
        /// Per-user sidebar overlay: pinned modules, per-group
        /// ordering, collapsed-group state. Loaded from localStorage
        /// on shell start and persisted on every mutation. Module
        /// taxonomy (Group) comes from the module declarations; this
        /// is the user's personal view on top.
        SidebarPrefs: SidebarPreferences.UserSidebarPreferences
        /// Platform-aggregated `ProcessedFileEntry` list collected from
        /// every module's `ProvidesProcessedData` hook after each update
        /// cycle. Published to module views via the
        /// `ProcessedDataContext` React provider mounted in `view`.
        ProcessedData: ProcessedFileEntry list
        /// Teams the signed-in user belongs to. Loaded on init in
        /// team-scoped modes (`Team` / `MultiTeam`); empty otherwise.
        /// Refreshed on `TeamSwitched` so newly-created teams appear in
        /// the header switcher without a page reload. Drives the
        /// `MultiTeam` header switcher; not used in single-team `Team`
        /// mode (no switcher rendered).
        MyTeams: TeamInfo list
        /// User's currently-selected active team id. Tracked at the
        /// shell level so the `MultiTeam` header switcher can highlight
        /// the active entry without depending on `TeamManagerUI`'s
        /// internal state. `None` = not loaded yet, no active team
        /// selected, or non-team-scoped mode.
        ActiveTeamId: string option
        /// Phase 4b — caller's resolved `PlatformRole`. `None` until
        /// the boot-time `IsPlatformAdmin` fetch resolves, OR after a
        /// fetch that returned false. `Some PlatformAdmin` unlocks the
        /// "Platform Admin" sidebar group. Survives team switches —
        /// the role is user-bound, not team-bound. Anonymous-mode
        /// deployments leave this `None` (the fetch returns false).
        PlatformRole: PlatformRole option
        /// Boot-time prefetch gate for the persisted config fetch
        /// (`/api/_platform/config/all`). Flips `Pending -> Loaded ()`
        /// inside `ConfigsLoaded`. Combined with `FlagsPrefetch` via
        /// `Prefetch.onAllReady` to fire the single active-module
        /// re-init when the last source drains. See the module-level
        /// comment above for rationale.
        ConfigsPrefetch: Prefetch<unit>
        /// Boot-time prefetch gate for the feature-flag resolution.
        /// Same semantics as `ConfigsPrefetch`.
        FlagsPrefetch: Prefetch<unit>
        /// Phase 12c — per-module Reload counter. Incremented by
        /// `ResetModule`. Composed into the React `key` of
        /// `Components.ModuleBoundary` so the boundary instance unmounts
        /// and remounts (clearing its internal `Error` state) when the
        /// user clicks Reload. Wiped on `TeamSwitched` for the same
        /// reason every other per-team field is wiped — counters from
        /// the prior team don't apply to the new one.
        ResetCounters: Map<string, int>
    }

    type Msg =
        | ModuleMsg of obj
        | ModuleSelected of string
        | AccessibleModulesLoaded of AccessibleModulesResponse option
        /// Fires once per shell start when the initial config fetch
        /// completes. Carries every module's values (keyed by module Id)
        /// plus the reserved `_platform` entry. Modules are re-initialised
        /// with the loaded values so their first meaningful render uses
        /// the persisted config rather than the pre-fetch empty seed.
        | ConfigsLoaded of Map<string, Map<string, string>>
        /// Fires once per shell start when the initial feature-flag
        /// prefetch completes. Carries the resolved flag map for the
        /// caller's access context. Re-inits the active module so its
        /// `ClientModuleContext.Flags` reflects the loaded values.
        | FlagsLoaded of Map<string, FlagValue>
        /// User collapsed or expanded a sidebar section. The key is
        /// the reserved `"_pinned"` / `"_other"` sentinel or a declared
        /// group name. Persisted to localStorage — no server round-trip
        /// until Phase 6e's user-scoped `IConfigStore` sync lands.
        | SidebarGroupToggled of groupKey: string
        /// User pinned or unpinned a module. Adds to / removes from
        /// `UserSidebarPreferences.PinnedModuleIds` and re-saves.
        | SidebarModulePinToggled of moduleId: string
        /// User drag-reordered modules within a section. `groupKey` is
        /// the reserved `"_pinned"` / `"_other"` sentinel or a declared
        /// group name; `orderedIds` is the new full ordering for that
        /// section. Persisted to localStorage on every drop.
        | SidebarModuleReordered of groupKey: string * orderedIds: string list
        /// Server-driven `Notification.ModuleAction` targeting a module
        /// registered in this deployment. The shell looks up the module
        /// by Id, gates on `AccessibleModules`, calls the module's
        /// `ActionDecoder`, and dispatches the decoded `Msg` against the
        /// target module's state in `ModuleStates`. Silently drops on
        /// any gate failure — actions are best-effort UX, not an
        /// authorisation boundary (the server-side tool guard is the
        /// authorisation boundary). Phase 6c.
        | ModuleActionReceived of moduleId: string * actionKey: string * payloadJson: string
        /// User switched to a different team in `MultiTeam` mode (or any
        /// other path that calls `PlatformApi.SetActiveTeam`). Carries
        /// the new active team id, or `None` if the user was revoked
        /// from their active team and is now in the no-active-team
        /// state. The handler clears every per-team piece of shell
        /// state (`ModuleStates`, `ModuleConfigs`, `PlatformConfig`,
        /// `ResolvedFlags`, `AccessibleModules`), re-issues the
        /// bootstrap loaders, and re-inits the active module — the
        /// same chain `ConfigsLoaded` and `FlagsLoaded` run, just
        /// rebound to the new team's data (or no team).
        | TeamSwitched of string option
        /// Server-driven equivalent of `TeamSwitched None` — fired by
        /// the notification subscriber when `MembershipChanged.Removed`
        /// affects the current user's active team. Routes through the
        /// shell so we can compare against the current `ActiveTeamId`
        /// and avoid resetting state when the removed team isn't the
        /// active one.
        | MembershipRevoked of teamId: string
        /// Server-driven equivalent of `TeamSwitched (Some teamId)` —
        /// fired by the notification subscriber when
        /// `MembershipChanged.ActiveTeamSet` affects the current user
        /// for a team different from the current `ActiveTeamId`. Lets
        /// admin- or script-initiated active-team changes propagate
        /// without a page reload.
        | MembershipActiveTeamSet of teamId: string
        /// Initial / refresh load of the team list for the header
        /// switcher and any caller that wants membership-aware UI
        /// outside `TeamManagerUI`. Fired on init in team-scoped modes
        /// and after `TeamSwitched` to capture newly-created teams.
        | MyTeamsLoaded of TeamInfo list
        /// Phase 4b — boot-time `IsPlatformAdmin` fetch resolved.
        /// `true` lifts `PlatformRole = Some PlatformAdmin` and reveals
        /// the "Platform Admin" sidebar group; `false` (the default
        /// for non-admins, Anonymous mode, and any error path) leaves
        /// it `None` so the group stays hidden.
        | PlatformRoleLoaded of bool
        /// Initial load of the user's active team id. Fired on init in
        /// team-scoped modes so the header switcher can highlight the
        /// current selection without an extra round-trip.
        | ActiveTeamLoaded of string option
        /// Phase 12c — fired by the "Reload module" button inside the
        /// per-module error boundary (and available to any caller that
        /// wants to reset a single module). Clears the named module's
        /// `ModuleStates` entry, increments its `ResetCounters` (which
        /// remounts the boundary), and re-runs `Init` against the
        /// current `ClientModuleContext`. Other modules and per-team
        /// state untouched. If `Init` itself throws during reset, the
        /// `ModuleStates` entry stays empty, `OnError` fires, and the
        /// next `ModuleSelected` for this module retries from scratch.
        | ResetModule of moduleId: string

    // Header freshness is the CsrfClient request-guard's job — see UserSession.fs:342 + installRequestGuard below.
    // Proxies are constructed at module load; identity + CSRF headers are read live per request by the send-time guard.

    /// Server-side Fable.Remoting proxy for the team API. Used to
    /// fetch the user's team memberships, drive header-switcher active
    /// team selection, and persist team switches initiated from the
    /// shell. Mirrors `TeamManagerUI`'s proxy — split from the original
    /// `PlatformApi` umbrella to keep team-CRUD calls behind a focused
    /// route prefix.
    let private teamApi: TeamApi =
        Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

    /// Server-side Fable.Remoting proxy for the accessibility helper
    /// (`GetAccessibleModules`). Used to fetch the per-user accessible-
    /// modules list on shell startup so the sidebar can hide entries
    /// the user can't use. Not a security boundary — the server's
    /// `makePermissionGuardedApi` is the actual enforcement; this proxy
    /// is purely UX.
    let private accessibilityApi: AccessibilityApi =
        Api.makeProxy<AccessibilityApi> (customOptions = UserSession.withRequestHeaders)

    /// Phase 4b — Platform Admin API proxy. Used at shell startup to
    /// resolve the caller's `PlatformRole`; the result drives the
    /// "Platform Admin" sidebar group's visibility (admin-only).
    /// Visible to every authenticated caller (returns false for
    /// non-admins) — no extra gating beyond the standard `/api/*`
    /// auth middleware.
    let private platformAdminApi: PlatformAdminApi =
        Api.makeProxy<PlatformAdminApi> (customOptions = UserSession.withRequestHeaders)

    /// Config API proxy. Used to fetch every registered module's
    /// persisted values on shell startup so modules can receive them
    /// via `ClientModuleContext` rather than going through an ad-hoc
    /// per-module request. The admin UI shares this proxy via its own
    /// module state.
    let private configApi: IConfigApi =
        Api.makeProxy<IConfigApi> (customOptions = UserSession.withRequestHeaders)

    /// Feature-flag API proxy. Used to fetch the resolved flag map
    /// for the caller's access context on shell startup so modules
    /// can receive it via `ClientModuleContext.Flags` and views via
    /// the `FeatureFlags` React context.
    let private featureFlagApi: IFeatureFlagApi =
        Api.makeProxy<IFeatureFlagApi> (customOptions = UserSession.withRequestHeaders)

    /// Async wrapper around `platformApi.GetAccessibleModules` that
    /// swallows any error — the client is expected to keep working if
    /// the RBAC endpoint is unavailable (e.g. an older server that
    /// doesn't expose it). A failed load leaves the sidebar
    /// permissive.
    let private loadAccessibleModules = async {
        try
            let! response = accessibilityApi.GetAccessibleModules()
            return Some response
        with _ ->
            return None
    }

    /// Gate a boot-time loader on the shared CSRF prefetch. Under
    /// `DefaultSecurityHardening` the server 403s any `/api` POST that
    /// lacks the session `X-CSRF-Token`; awaiting the prefetch here
    /// means the first mutating Remoting call carries the header. No
    /// added cost under `NoSecurityHardening` — the same fetch runs
    /// eagerly at startup regardless, and `ensure` short-circuits once
    /// the token is cached.
    let private withCsrf (loader: Async<'T>) : Async<'T> = async {
        do! CsrfClient.ensure ()
        return! loader
    }

    /// Fetch every registered module's config (including the reserved
    /// `_platform` entry). Failures per module degrade to an empty
    /// entry so the shell can still render — `Anonymous` mode, for
    /// example, rejects every read because no scope resolves. A total
    /// failure of `ListModules` returns the empty map so `init` can
    /// finish without blocking on the network.
    let private loadAllConfigs = async {
        try
            let! entries = configApi.ListModules()

            let! pairs =
                entries
                |> List.map (fun entry -> async {
                    try
                        let! result = configApi.GetModuleConfig entry.ModuleKey

                        return
                            match result with
                            | Ok view -> Some(view.ModuleKey, view.Values)
                            | Error _ -> None
                    with _ ->
                        return None
                })
                |> Async.Parallel

            return pairs |> Array.choose id |> Map.ofArray
        with _ ->
            return Map.empty
    }

    /// Fetch the resolved feature-flag map for the caller. Failure
    /// degrades to an empty map — the prefetch is best-effort and
    /// modules that declare flags continue to see `Map.empty` (i.e.
    /// every `flag` returns `false`) until a future load succeeds.
    /// An `Anonymous` deployment that declares no platform flags
    /// will also see an empty map, which is the correct answer.
    let private loadResolvedFlags = async {
        try
            return! featureFlagApi.GetResolvedFlags()
        with _ ->
            return Map.empty
    }

    /// Fetch the user's team memberships for the header switcher and
    /// any other caller that wants membership-aware UI. Failure
    /// degrades to an empty list. Fires on init in team-scoped modes
    /// and after `TeamSwitched` to pick up newly-created teams.
    let private loadMyTeams = async {
        try
            return! teamApi.GetMyTeams()
        with _ ->
            return []
    }

    /// Fetch the user's currently-selected active team id. Failure
    /// degrades to `None`.
    let private loadActiveTeam = async {
        try
            return! teamApi.GetActiveTeam()
        with _ ->
            return None
    }

    /// Phase 4b — fetch whether the caller holds
    /// `PlatformRole.PlatformAdmin`. Failure (Anonymous mode, network
    /// glitch, server doesn't expose the API yet) degrades to `false`,
    /// which leaves the "Platform Admin" sidebar group hidden — the
    /// safe default. Fires once at shell init; the role doesn't change
    /// when switching teams (it's user-bound, not team-bound), so no
    /// refresh on `TeamSwitched`.
    let private loadPlatformRole = async {
        try
            return! platformAdminApi.IsPlatformAdmin()
        with ex ->
            // A thrown call (CSRF/auth seam, network glitch, server not
            // exposing the API yet) is otherwise silently identical to a
            // clean `false`. The warn distinguishes a *failed role check*
            // from a definitive "not an admin" in browser dev tools.
            log.Warn(
                sprintf
                    "[PlatformAdmin] IsPlatformAdmin() call FAILED (%s) — treating caller as non-admin, so the \"Platform Admin\" sidebar group (Health Monitor, etc.) stays hidden. This is a transport/role-check failure, not a definitive \"you are not an admin\"."
                    ex.Message
            )

            return false
    }

    /// Module-level capture of the shell's Elmish `dispatch` function.
    /// Set once during `init` via `Cmd.ofEffect` (same pattern as the
    /// notification subscriber). Lets `ClientModuleContext.OnTeamSwitched`
    /// dispatch a shell-level `TeamSwitched` from within a module's
    /// update handler — the module's own `dispatch` is typed to its own
    /// `Msg`, so it can't otherwise reach the shell.
    ///
    /// Sanctioned mutable: initialisation-time set, read after. Mirrors
    /// `UserSession.currentSubjectKind` and `NotificationClient.handlers`
    /// — documented in the platform README's "No new side effects"
    /// exceptions list.
    ///
    /// Typed via `IDispatcher<Msg>` (ToolUp.Elmish primitive — replaces
    /// the legacy `(Msg -> unit) option` shape). Captured at program-start
    /// via `Program.withDispatcherHandle`; `IsActive` flips to `false`
    /// when `withTermination` triggers, so background callbacks check
    /// before dispatching and no-op cleanly on hot-reload / teardown
    /// rather than spraying messages at a dead loop.
    let mutable private shellDispatcher: IDispatcher<Msg> option = None

    /// Optional caller-supplied UI to inject into the shell. Companion
    /// packages (notably ToolUp.AI) fill these slots from their own
    /// state; the shell renders what it is given.
    type ExtraChrome = {
        HeaderAction: ReactElement option
        SidePanel: ReactElement option
    }

    let emptyChrome: ExtraChrome = {
        HeaderAction = None
        SidePanel = None
    }

    /// Public accessor: outer wrappers read the active module id to
    /// pass along with their own requests (e.g. AI's ActiveModule field).
    let activeModuleId (model: Model) = model.ActiveModuleId

    /// Public accessor: outer wrappers read the active page route. `None`
    /// for legacy single-page modules; `Some "/..."` for multi-page
    /// modules (leading slash preserved).
    let activePageRoute (model: Model) : string option = model.ActivePageRoute

    /// Resolve the `NarrativeDocument` (if any) that the active module
    /// currently exposes for the active page. Returns `None` when the
    /// module has not registered a `ProvidesNarrative` extractor, when
    /// the module has no state yet, or when the extractor returns `None`.
    let currentNarrative (modules: ErasedModule list) (model: Model) : Narrative.NarrativeDocument option =
        modules
        |> List.tryFind (fun m -> m.Definition.Id = model.ActiveModuleId)
        |> Option.bind (fun m ->
            m.ProvidesNarrative
            |> Option.bind (fun extract ->
                model.ModuleStates
                |> Map.tryFind model.ActiveModuleId
                |> Option.bind (fun state -> extract state model.ActivePageRoute)))

    let private tryFind (modules: ErasedModule list) id =
        modules |> List.tryFind (fun m -> m.Definition.Id = id)

    /// Split a sidebar-click Id into `(moduleId, pageRouteOpt)`. Sidebar
    /// entries for legacy single-page modules are the bare module Id
    /// (`"SkuAnalysis"`); multi-page modules emit one entry per page
    /// composed as `"{moduleId}{pageRoute}"` — since `PageConfig.Route`
    /// conventionally starts with `/`, the form is e.g.
    /// `"SalesAnalysis/sku-analysis"`. The route is returned with its
    /// leading `/` preserved so it matches `ClientModule.PageViews` keys
    /// directly.
    let private parseSidebarId (id: string) =
        match id.IndexOf '/' with
        | -1 -> id, None
        | i -> id.Substring(0, i), Some(id.Substring i)

    /// Active-page seed for a newly-selected module. Multi-page modules
    /// default to their first declared page; single-page modules have
    /// `ActivePageRoute = None`.
    let private defaultPageRoute (moduleImpl: ErasedModule) =
        match moduleImpl.PageViews with
        | Some _ -> moduleImpl.Definition.Pages |> List.tryHead |> Option.map (fun p -> p.Route)
        | None -> None

    /// Resolve the sidebar-icon path for a module. Single-page modules
    /// (auto-derived: `Pages = []`) use `Definition.Icon` directly;
    /// modules with explicit `Pages` use the first page's icon. The
    /// auto-derive applies when there are no Pages declared at all,
    /// matching the multi-page sidebar code's per-page icon use.
    let private singlePageIcon (def: ModuleDefinition) : ReactElement =
        match def.Pages with
        | [] -> def.Icon
        | page :: _ -> page.Icon

    /// Assemble the `ClientModuleContext` handed to a module's `Init`.
    /// The client-side context carries the persisted config slice for
    /// the named module and the platform-level slice, plus resolved
    /// identity. `TeamId` is intentionally `None` here — per-request
    /// scope resolution is the server's responsibility; modules that
    /// need the active team should call `PlatformApi.GetActiveTeam`.
    /// Build the `OnTeamSwitched` callback handed to modules via
    /// `ClientModuleContext`. Set when the deployment declares any
    /// `Team`-shaped surface (single-team or multi-team UX) so
    /// `TeamManagerUI` (and any custom team-management module) can
    /// notify the shell when the active team changes. `None` when no
    /// team surface is declared.
    let private buildOnTeamSwitched (config: ClientConfig) : (string -> unit) option =
        if ClientConfig.hasTeamScope config then
            Some(fun teamId ->
                shellDispatcher
                |> Option.iter (fun d ->
                    if d.IsActive then
                        d.Dispatch(TeamSwitched(Some teamId))))
        else
            None

    let private buildContext
        (config: ClientConfig)
        (queryBus: IModuleQueryBus)
        (model: Model)
        (moduleId: string)
        : ClientModuleContext =
        {
            Config = model.ModuleConfigs |> Map.tryFind moduleId |> Option.defaultValue Map.empty
            PlatformConfig = model.PlatformConfig
            Flags = model.ResolvedFlags
            UserId = UserSession.getUserId ()
            TeamId = model.ActiveTeamId
            QueryBus = queryBus
            OnTeamSwitched = buildOnTeamSwitched config
        }

    /// Wipe `ModuleStates` and re-run the active module's `Init`
    /// against the now-populated context. Shared by the cold-load
    /// prefetch gate (`ConfigsLoaded` / `FlagsLoaded` after both have
    /// arrived) — both handlers used to do this unconditionally on
    /// every arrival, causing 3× active-module init per cold load.
    let private reinitActiveAfterPrefetch
        (config: ClientConfig)
        (queryBus: IModuleQueryBus)
        (modules: ErasedModule list)
        (model: Model)
        : Model * Cmd<Msg> =
        let reset = { model with ModuleStates = Map.empty }

        match tryFind modules reset.ActiveModuleId with
        | Some moduleImpl ->
            let ctx = buildContext config queryBus reset reset.ActiveModuleId
            let state, cmd = moduleImpl.Init ctx

            {
                reset with
                    ModuleStates = Map.ofList [ moduleImpl.Definition.Id, state ]
            },
            Cmd.map ModuleMsg cmd
        | None -> reset, Cmd.none

    /// Aggregate processed data from every module that exposes it.
    /// Pure — callers store the result in `Model.ProcessedData` and the
    /// shell's `view` then publishes it through `ProcessedDataContext`.
    let private computeProcessedData (modules: ErasedModule list) (states: Map<string, obj>) =
        modules
        |> List.collect (fun m ->
            match m.ProvidesProcessedData with
            | Some getter ->
                match states |> Map.tryFind m.Definition.Id with
                | Some state -> getter state
                | None -> []
            | None -> [])

    let init (_config: ClientConfig) (queryBus: IModuleQueryBus) (modules: ErasedModule list) () =
        // Phase 6g.C: publish the registered module list so companion
        // packages (e.g. a client-resident navigate executor) can
        // validate `(moduleId, pageRoute)` arguments before firing
        // a navigation request. Single-page modules contribute an
        // empty `PageRoutes`; multi-page modules contribute their
        // page routes from `Definition.Pages`.
        modules
        |> List.map (fun m ->
            ({
                ModuleId = m.Definition.Id
                ModuleName = m.Definition.Name
                PageRoutes = m.Definition.Pages |> List.map _.Route
            }
            : RegisteredModules.ModuleEntry))
        |> RegisteredModules.publish

        let moduleImpl =
            match _config.ActiveModule with
            | Some id -> tryFind modules id |> Option.defaultValue modules[0]
            | None -> modules[0]

        // Pre-fetch seed. The async `ConfigsLoaded` / `FlagsLoaded`
        // messages below re-init the active module with the loaded
        // values; modules that declare no config schema and no flag
        // reads are unaffected by the re-init.
        let seedCtx = {
            Config = Map.empty
            PlatformConfig = Map.empty
            Flags = Map.empty
            UserId = UserSession.getUserId ()
            TeamId = None
            QueryBus = queryBus
            OnTeamSwitched = buildOnTeamSwitched _config
        }

        let state, cmd = moduleImpl.Init seedCtx
        let states = Map.ofList [ moduleImpl.Definition.Id, state ]
        let processed = computeProcessedData modules states

        let model = {
            ActiveModuleId = moduleImpl.Definition.Id
            ActivePageRoute = defaultPageRoute moduleImpl
            ModuleStates = states
            AccessibleModules = None
            ModuleConfigs = Map.empty
            PlatformConfig = Map.empty
            ResolvedFlags = Map.empty
            SidebarPrefs = SidebarPreferences.load ()
            ProcessedData = processed
            MyTeams = []
            ActiveTeamId = None
            PlatformRole = None
            ConfigsPrefetch = Prefetch.none
            FlagsPrefetch = Prefetch.none
            ResetCounters = Map.empty
        }

        let loadPerms =
            Cmd.OfAsync.perform (fun () -> withCsrf loadAccessibleModules) () AccessibleModulesLoaded

        let loadConfigs =
            Cmd.OfAsync.perform (fun () -> withCsrf loadAllConfigs) () ConfigsLoaded

        let loadFlags =
            Cmd.OfAsync.perform (fun () -> withCsrf loadResolvedFlags) () FlagsLoaded

        // Team-scoped deployments load the user's team list and
        // active-team id so the header (when `HeaderSwitcher`-UX) can
        // render a switcher and the shell can populate
        // `ClientModuleContext.TeamId` for module init. Other
        // deployment shapes skip the round-trips.
        let teamScopedLoaders =
            if ClientConfig.hasTeamScope _config then
                [
                    Cmd.OfAsync.perform (fun () -> withCsrf loadMyTeams) () MyTeamsLoaded
                    Cmd.OfAsync.perform (fun () -> withCsrf loadActiveTeam) () ActiveTeamLoaded
                ]
            else
                []

        // Shell dispatcher capture lives at the `Program.run` site via
        // `Program.withDispatcherHandle` (ToolUp.Elmish primitive) — see
        // the run call below. The previous `Cmd.ofEffect`-capture pattern
        // (running once at init, writing the raw `Dispatch<Msg>` into a
        // mutable) is no longer needed: `IDispatcher<Msg>` is captured
        // before `init`'s commands fire, so background callbacks reading
        // it from the very first dispatch will see a live handle.

        // 0.4.1 — the boot-time background subscriptions (notification
        // stream + NavigationRequest bus) used to land here as
        // `Cmd.ofEffect` commands. They now register via
        // `Program.withEffect (EffectHandle.programLifetime ...)` at the
        // `program` site below, so the runtime knows their lifetime and
        // disposes them cleanly on `IDispatcher.Terminate()` — fixing
        // the SSE / notification leak across HMR hot-reloads and
        // page-navigation that the 0.4.0 README headlines as resolved.
        // See `programLifetimeEffects` below.

        model,
        Cmd.batch (
            [
                Cmd.map ModuleMsg cmd
                loadPerms
                loadConfigs
                loadFlags
                Cmd.OfAsync.perform (fun () -> loadPlatformRole) () PlatformRoleLoaded
            ]
            @ teamScopedLoaders
        )

    let update (_config: ClientConfig) (queryBus: IModuleQueryBus) (modules: ErasedModule list) msg model =
        let newModel, cmd =
            match msg with
            | ModuleMsg moduleMsg ->
                match tryFind modules model.ActiveModuleId with
                | Some moduleImpl ->
                    let currentState = model.ModuleStates |> Map.find model.ActiveModuleId
                    let newState, cmd = moduleImpl.Update moduleMsg currentState

                    // Phase 6g.A: publish the new state to any
                    // registered observers (a companion can mirror it
                    // into its own snapshot registry). Side-effect
                    // inside `update` is the same trade-off as the
                    // `NotificationClient.publishLocal` toast in the
                    // ModuleActionReceived branch — companions
                    // observe state changes without the shell taking
                    // a compile-time dependency on them.
                    ModuleStateObserver.publish model.ActiveModuleId newState

                    {
                        model with
                            ModuleStates = model.ModuleStates |> Map.add model.ActiveModuleId newState
                    },
                    Cmd.map ModuleMsg cmd
                | None -> model, Cmd.none

            | ModuleSelected sidebarId ->
                // Sidebar Ids for multi-page modules are composite
                // (`"{moduleId}{pageRoute}"`); single-page modules stay
                // bare. Navigating between pages of the same module
                // updates `ActivePageRoute` only — `Init` does not re-run,
                // so per-page state is preserved.
                let moduleId, pageRouteOpt = parseSidebarId sidebarId

                match tryFind modules moduleId with
                | Some moduleImpl ->
                    let pageRoute =
                        match pageRouteOpt with
                        | Some r -> Some r
                        | None -> defaultPageRoute moduleImpl

                    if model.ActiveModuleId = moduleId then
                        {
                            model with
                                ActivePageRoute = pageRoute
                        },
                        Cmd.none
                    else
                        let state, cmd =
                            match model.ModuleStates |> Map.tryFind moduleId with
                            | Some existingState -> existingState, Cmd.none
                            | None ->
                                let ctx = buildContext _config queryBus model moduleId
                                let s, c = moduleImpl.Init ctx
                                s, Cmd.map ModuleMsg c

                        {
                            model with
                                ActiveModuleId = moduleId
                                ActivePageRoute = pageRoute
                                ModuleStates = model.ModuleStates |> Map.add moduleId state
                        },
                        cmd
                | None -> model, Cmd.none

            | AccessibleModulesLoaded accessible ->
                {
                    model with
                        AccessibleModules = accessible
                },
                Cmd.none

            | ConfigsLoaded configs ->
                // Cold-load gate via `Prefetch<unit>` + `Prefetch.onAllReady`
                // (ToolUp.Elmish 0.4.0 primitive). The persisted value is
                // recorded on `ModuleConfigs` / `PlatformConfig`; the gate is
                // a `Prefetch<unit>` that flips to `Loaded ()`. When *both*
                // prefetches are complete the last handler wipes
                // `ModuleStates` and re-runs `Init` against a context
                // populated with both data sources — so the active module's
                // `Init` runs exactly twice on cold load (empty seed +
                // re-init), never three times.
                let platformCfg =
                    configs
                    |> Map.tryFind ConfigKeys.PlatformModuleKey
                    |> Option.defaultValue Map.empty

                let moduleCfgs = configs |> Map.remove ConfigKeys.PlatformModuleKey

                let updated = {
                    model with
                        ModuleConfigs = moduleCfgs
                        PlatformConfig = platformCfg
                        ConfigsPrefetch = Prefetch.loaded ()
                }

                if Prefetch.isComplete updated.FlagsPrefetch then
                    reinitActiveAfterPrefetch _config queryBus modules updated
                else
                    updated, Cmd.none

            | FlagsLoaded flags ->
                // Same gate as `ConfigsLoaded` — see that handler.
                let updated = {
                    model with
                        ResolvedFlags = flags
                        FlagsPrefetch = Prefetch.loaded ()
                }

                if Prefetch.isComplete updated.ConfigsPrefetch then
                    reinitActiveAfterPrefetch _config queryBus modules updated
                else
                    updated, Cmd.none

            | TeamSwitched newTeamIdOpt ->
                // Active team changed. Every per-team piece of shell
                // state must be cleared and re-fetched against the new
                // scope, then the active module re-initialised so its
                // `ClientModuleContext` reflects the swap. Same shape
                // as the `ConfigsLoaded` / `FlagsLoaded` reset paths
                // above, just rebound to the new team's data and
                // chained through the loader cmds rather than running
                // inline.
                //
                // `newTeamIdOpt = None` is the membership-revoked path
                // — loaders run against an empty scope and the user
                // lands in the no-active-team state.
                //
                // `MyTeams` is also refreshed because `TeamCreated`
                // (which arrives via this message — `TeamManagerUI`
                // invokes `OnTeamSwitched` for both switch and create)
                // can introduce a team that wasn't in the boot-time
                // list.
                let reset = {
                    model with
                        ActiveTeamId = newTeamIdOpt
                        ModuleConfigs = Map.empty
                        PlatformConfig = Map.empty
                        ResolvedFlags = Map.empty
                        AccessibleModules = None
                        ModuleStates = Map.empty
                        ResetCounters = Map.empty
                }

                reset,
                Cmd.batch [
                    Cmd.OfAsync.perform (fun () -> withCsrf loadAccessibleModules) () AccessibleModulesLoaded
                    Cmd.OfAsync.perform (fun () -> withCsrf loadAllConfigs) () ConfigsLoaded
                    Cmd.OfAsync.perform (fun () -> withCsrf loadResolvedFlags) () FlagsLoaded
                    Cmd.OfAsync.perform (fun () -> withCsrf loadMyTeams) () MyTeamsLoaded
                ]

            | MembershipRevoked teamId ->
                // Server-driven `MembershipChanged.Removed` for this
                // user. Reset only if the removed team is the active
                // one — otherwise the membership change affects a team
                // we're not currently in, and `MyTeams` refresh is
                // enough.
                if model.ActiveTeamId = Some teamId then
                    model, Cmd.ofMsg (TeamSwitched None)
                else
                    model, Cmd.OfAsync.perform (fun () -> withCsrf loadMyTeams) () MyTeamsLoaded

            | MembershipActiveTeamSet teamId ->
                // Server-driven `MembershipChanged.ActiveTeamSet` for
                // this user. Reset only if the new active team
                // differs from what we have — same payload arrives
                // when the switch was originated by this client
                // (already handled via `TeamSwitched`), and we don't
                // want to double-reset.
                if model.ActiveTeamId <> Some teamId then
                    model, Cmd.ofMsg (TeamSwitched(Some teamId))
                else
                    model, Cmd.none

            | MyTeamsLoaded teams -> { model with MyTeams = teams }, Cmd.none

            | PlatformRoleLoaded isAdmin ->
                let role = if isAdmin then Some PlatformRole.PlatformAdmin else None
                { model with PlatformRole = role }, Cmd.none

            | ActiveTeamLoaded teamId -> { model with ActiveTeamId = teamId }, Cmd.none

            | ResetModule moduleId ->
                // Phase 12c — clear the named module's state, bump its
                // reset counter (which forces the boundary to remount via
                // a key change), and re-run `Init` against the current
                // context. Other modules and per-team state untouched —
                // mirrors the lazy-init path in `ModuleSelected` plus a
                // counter bump.
                //
                // R2 mitigation: `Init` itself can throw. Wrap in F#
                // try/with so the shell's update tick survives. On
                // failure, leave `ModuleStates` without the key and
                // surface via `OnError`. The next `ModuleSelected` for
                // this module retries from scratch (and the boundary
                // catches any re-thrown render-time exception).
                match tryFind modules moduleId with
                | None -> model, Cmd.none
                | Some moduleImpl ->
                    let nextGen =
                        (model.ResetCounters |> Map.tryFind moduleId |> Option.defaultValue 0) + 1

                    let cleared = {
                        model with
                            ModuleStates = model.ModuleStates |> Map.remove moduleId
                            ResetCounters = model.ResetCounters |> Map.add moduleId nextGen
                    }

                    let ctx = buildContext _config queryBus cleared moduleId

                    try
                        let state, cmd = moduleImpl.Init ctx

                        {
                            cleared with
                                ModuleStates = cleared.ModuleStates |> Map.add moduleId state
                        },
                        Cmd.map ModuleMsg cmd
                    with ex ->
                        let stack = ex.StackTrace |> Option.ofObj |> Option.defaultValue ""

                        match _config.OnError with
                        | Some f ->
                            f {
                                ModuleId = moduleId
                                Error = ex
                                ComponentStack = stack
                            }
                        | None ->
                            log.Error(
                                sprintf "[ModuleBoundary] %s Init crashed during reset: %s" moduleId ex.Message,
                                Some ex
                            )

                        cleared, Cmd.none

            | SidebarGroupToggled groupKey ->
                let prefs = SidebarPreferences.toggleExpanded groupKey model.SidebarPrefs
                SidebarPreferences.save prefs
                { model with SidebarPrefs = prefs }, Cmd.none

            | SidebarModulePinToggled moduleId ->
                let prefs = SidebarPreferences.togglePinned moduleId model.SidebarPrefs
                SidebarPreferences.save prefs
                { model with SidebarPrefs = prefs }, Cmd.none

            | SidebarModuleReordered(groupKey, orderedIds) ->
                let prefs =
                    if groupKey = Toolup.Sidebar.PinnedKey then
                        SidebarPreferences.setPinnedOrder orderedIds model.SidebarPrefs
                    else
                        SidebarPreferences.setOrder groupKey orderedIds model.SidebarPrefs

                SidebarPreferences.save prefs
                { model with SidebarPrefs = prefs }, Cmd.none

            | ModuleActionReceived(moduleId, actionKey, payloadJson) ->
                // Gate 1: module registered in this deployment? If not,
                // silently drop — the server-side tool ran and produced
                // its text result; there is no UI to update here.
                match tryFind modules moduleId with
                | None -> model, Cmd.none
                | Some moduleImpl ->
                    // Gate 2: caller accessible? RBAC-managed modules
                    // that the caller can't see filter out here. Un-managed
                    // modules (SDK built-ins, modules with no permission
                    // entry) pass through — same semantics as the sidebar
                    // filter. `None` (pre-load) is permissive.
                    let accessible =
                        match model.AccessibleModules with
                        | Some resp ->
                            let managed = Set.ofList resp.Managed
                            let okSet = Set.ofList resp.Accessible
                            not (managed.Contains moduleId) || okSet.Contains moduleId
                        | None -> true

                    // Gate 3: module has a decoder for this action?
                    let decoded =
                        if accessible then
                            moduleImpl.ActionDecoder
                            |> Option.bind (fun decoder -> decoder (actionKey, payloadJson))
                        else
                            None

                    match decoded with
                    | None -> model, Cmd.none
                    | Some decodedMsg ->
                        // Init the target module if its state hasn't been
                        // materialised yet (user has never navigated to
                        // it this session). The init cmd stays scoped to
                        // the target module Id via a wrapper that routes
                        // its internal messages through `ModuleActionReceived`
                        // paths… actually, we route init Cmds through
                        // `ModuleMsg` which targets `ActiveModuleId`, so
                        // we only honour init-time Cmds when the target
                        // module is already active. For inactive targets
                        // we discard the init Cmd (it's an acceptable
                        // trade for Phase 6c — most `Init` implementations
                        // return `Cmd.none`; any that don't run on next
                        // navigation).
                        let existingState = model.ModuleStates |> Map.tryFind moduleId

                        let state, preCmd =
                            match existingState with
                            | Some s -> s, Cmd.none
                            | None ->
                                let ctx = buildContext _config queryBus model moduleId
                                let s, c = moduleImpl.Init ctx

                                if moduleId = model.ActiveModuleId then
                                    s, Cmd.map ModuleMsg c
                                else
                                    s, Cmd.none

                        // Apply the decoded Msg against the target state.
                        let newState, updateCmd = moduleImpl.Update decodedMsg state

                        // Phase 6g.A: same observer publish as the
                        // ModuleMsg branch — server-emitted action
                        // arrivals also change module state.
                        ModuleStateObserver.publish moduleId newState

                        let updatedModel = {
                            model with
                                ModuleStates = model.ModuleStates |> Map.add moduleId newState
                        }

                        // Route the module's post-update Cmd through
                        // `ModuleMsg` only when the target is active —
                        // `ModuleMsg` targets `ActiveModuleId`. For
                        // inactive targets we discard the Cmd; a later
                        // follow-up can add a targeted-module Cmd path
                        // if real usage needs it.
                        let routedCmd =
                            if moduleId = model.ActiveModuleId then
                                Cmd.batch [ preCmd; Cmd.map ModuleMsg updateCmd ]
                            else
                                preCmd

                        // Background UX: emit a local `SystemMessage` toast
                        // when the target is inactive. Routes through
                        // `NotificationClient.publishLocal` so the existing
                        // `ToastCentre` picks it up without any additional
                        // plumbing. Side-effect in update is a trade-off
                        // for the simpler wiring; the alternative (new
                        // Elmish toast list + React consumer) is
                        // strictly more code for the same user-visible
                        // result. Kept conservative: no toast when the
                        // target is active (the user is already looking
                        // at the result).
                        if moduleId <> model.ActiveModuleId then
                            let text = $"Results available in {moduleImpl.Definition.Name}"

                            let envelope =
                                NotificationEnvelope.create
                                    (UserSession.getUserId ())
                                    (Notification.SystemMessage(SystemMessageLevel.Info, text))

                            NotificationClient.publishLocal envelope

                        updatedModel, routedCmd

        let finalModel = {
            newModel with
                ProcessedData = computeProcessedData modules newModel.ModuleStates
        }

        // Detect ProcessedData entries that disappeared (file deleted,
        // data store reset, ingestion result invalidated) and reset
        // every initialised module that declared `NeedsData`. The
        // module's view picks up the shrunk `ProcessedDataContext`
        // automatically on next render — but its Elmish `Model` may
        // still hold a stale reference (selected file name, cached
        // parse, picker dropdown index) that won't self-heal. Reset
        // wipes that state and re-runs `Init` against the current
        // data snapshot, mirroring the targeted-reset path Phase 12c
        // already uses for module crashes.
        //
        // Conservative scope: every module with `NeedsData = Some _`
        // (so SDK-built-ins like `FileManagerUI` / `TeamManagerUI` /
        // `HealthMonitorUI` / `TeamConfigUI` aren't touched — they
        // don't declare `NeedsData`). Modules that haven't been
        // initialised yet (no `ModuleStates` entry) are skipped — the
        // lazy init at `ModuleSelected` already starts from a clean
        // slate.
        let processedDataResetCmds =
            let toKeySet (entries: ProcessedFileEntry list) =
                entries |> List.map (fun e -> e.FileName, e.DataType) |> Set.ofList

            let oldKeys = toKeySet model.ProcessedData
            let newKeys = toKeySet finalModel.ProcessedData
            let removed = Set.difference oldKeys newKeys

            if Set.isEmpty removed then
                Cmd.none
            else
                modules
                |> List.choose (fun m ->
                    match m.NeedsData with
                    | Some _ when finalModel.ModuleStates |> Map.containsKey m.Definition.Id ->
                        Some(Cmd.ofMsg (ResetModule m.Definition.Id))
                    | _ -> None)
                |> Cmd.batch

        finalModel, Cmd.batch [ cmd; processedDataResetCmds ]

    let view (config: ClientConfig) (modules: ErasedModule list) (chrome: ExtraChrome) model dispatch =
        // Select the page content for the active module. Multi-page
        // modules (`PageViews = Some map`) dispatch on the active
        // `PageConfig.Route`; their views return `PageContent` directly.
        // Single-page modules fall through the legacy tuple path — the
        // shell wraps their `(left, right)` return in `SplitPanel` so
        // every existing module renders byte-identically.
        // Phase 12c — wrap the active module's view in a per-module React
        // error boundary. The view-function call (`pageView state dispatch`
        // / `v state dispatch`) lives inside the `renderInner` thunk and is
        // invoked by the boundary's child host component. Sync F# exceptions
        // during the call AND React render-time exceptions in the produced
        // tree both bubble into the boundary's `componentDidCatch`. The
        // boundary returns a single `ReactElement`; we wrap it in
        // `PageContent.Custom` so `Layout.AppShell`'s downstream renderer
        // (`Layout.renderPageContent`) emits it verbatim. The module's
        // chosen `PageContent` layout is preserved because `renderInner`
        // returns the original `PageContent` value, and the boundary's host
        // applies the per-case gutters by calling `renderPageContent` itself.
        //
        // Reset-key composition: team id + per-module reset counter. Either
        // changing forces React to remount the boundary (clearing its
        // `Error = Some` state). Without the team-id slot, switching teams
        // while the boundary was in error state would leave the error UI
        // stuck for the new team's data.
        let content: PageContent =
            match tryFind modules model.ActiveModuleId with
            | Some moduleImpl ->
                let currentState = model.ModuleStates |> Map.find model.ActiveModuleId
                let dispatchMsg = ModuleMsg >> dispatch

                let renderInner () : PageContent =
                    match moduleImpl.PageViews, model.ActivePageRoute with
                    | Some map, Some route when map.ContainsKey route ->
                        let pageView = map[route]
                        pageView currentState dispatchMsg
                    | _ ->
                        match moduleImpl.View with
                        | Some v ->
                            let left, right = v currentState dispatchMsg
                            SplitPanel(left, right)
                        | None ->
                            // `register` rejects modules with neither View nor PageViews,
                            // so this branch only fires when a multi-page module's
                            // ActivePageRoute doesn't match any registered PageViews entry.
                            SplitPanel(
                                Html.div $"No view registered for page route {model.ActivePageRoute}",
                                Html.div ""
                            )

                let teamPart = model.ActiveTeamId |> Option.defaultValue "_"

                let counter =
                    model.ResetCounters |> Map.tryFind model.ActiveModuleId |> Option.defaultValue 0

                let resetKey = sprintf "%s-%d" teamPart counter

                let boundaryEl =
                    Components.ModuleBoundary.wrap
                        model.ActiveModuleId
                        resetKey
                        config.OnError
                        (fun () -> dispatch (ResetModule model.ActiveModuleId))
                        config.InputsPaneWidth
                        renderInner

                Custom boundaryEl
            | None -> SplitPanel(Html.div "Error: Module not found", Html.div "")

        // Composite sidebar Id for the active-border highlight so that
        // the shell matches the sidebar entry emitted for the active
        // page (multi-page modules emit one entry per PageConfig).
        let selectedSidebarId =
            match model.ActivePageRoute with
            | Some route -> $"{model.ActiveModuleId}{route}"
            | None -> model.ActiveModuleId

        let sidebarSections =
            let hasDataForType dt =
                model.ProcessedData |> List.exists (fun e -> e.DataType = dt && e.Error.IsNone)

            // Filter sidebar by the server's accessible-modules list
            // when it's been loaded. Not a security boundary — the
            // server's per-module permission guard is the enforcement.
            // Pre-load (None): show everything so the sidebar doesn't
            // flicker empty while the fetch is in flight.
            //
            // The filter operates on module Id (stable permission key),
            // not Name (display). Modules whose Id is in `Managed` but
            // not in `Accessible` are hidden. Modules whose Id is NOT
            // in `Managed` — SDK-built-ins (FileManager, TeamManager)
            // and debug-only modules — bypass the filter and stay
            // visible regardless of RBAC config.
            let rbacFiltered =
                match model.AccessibleModules with
                | Some response ->
                    let managed = Set.ofList response.Managed
                    let accessible = Set.ofList response.Accessible

                    modules
                    |> List.filter (fun m ->
                        not (managed.Contains m.Definition.Id) || accessible.Contains m.Definition.Id)
                | None -> modules

            // Phase 4b — hide the "Platform Admin" sidebar group from
            // callers without `PlatformRole.PlatformAdmin`. Group label
            // is the contract: any module declaring `withGroup
            // "Platform Admin"` is gated by the role. Distinct from
            // RBAC's per-module `Managed` / `Accessible` filter — that
            // filter targets app-domain modules; the Platform Admin
            // gate targets SDK-built-in admin modules whose Ids start
            // with `_sdk.` and so bypass RBAC by design. Both gates
            // compose: the user must pass RBAC AND (if the module is
            // in the Platform Admin group) hold the role.
            let adminGroupFiltered =
                let isAdmin = model.PlatformRole = Some PlatformRole.PlatformAdmin

                rbacFiltered
                |> List.filter (fun m ->
                    match m.Group with
                    | Some "Platform Admin" -> isAdmin
                    | _ -> true)

            // Phase 66 Stream B.3 — per-module `Visibility` gate over the
            // resolved `SubjectKind`. Structurally replaces the
            // deployment-wide sidebar blanket-hide Phase 55 introduced as
            // a partial fix in `PlatformApiHandler.fs:497`: modules now
            // own their per-subject visibility, so an Anonymous-mode
            // deployment shows the (visible-to-anonymous) modules the
            // module-author intended rather than the empty-sidebar
            // failure mode that motivated the `Mode = Individual`
            // workaround.
            //
            // Phase 66 Stream B.8 — derivation now reads from
            // `config.Surfaces` via `ClientConfig.resolveSubjectKind`
            // (the canonical projection that also drives storage
            // selection in `UserSession` and the sign-in UI mount in
            // `AuthUIProvider`). Single-surface deployments collapse
            // to the same `SubjectKind` the pre-B.8 `config.Mode`
            // derivation produced: `Surfaces.anonymous` → `AnonymousKind`;
            // `Surfaces.individual` / `Surfaces.trial` → `UserKind`;
            // `Surfaces.team` / `Surfaces.multiTeam` with `ActiveTeamId
            // = Some _` → `TeamMemberKind` (else `UserKind`); single-
            // shape `claimBearer` → `ClaimBearerKind`. Mixed-mode
            // deployments fall back to the most-authenticated shape
            // present (per-request `Subject` resolution catches up
            // server-side, the client mirrors on the next render).
            //
            // GP 12 — this is UI shape only; server-side
            // `SurfaceEnforcementMiddleware` is the authoritative gate.
            //
            // Default `Visibility.visibleToAll` is byte-identical to
            // pre-B.3: modules declaring nothing pass every kind.
            let resolvedSubjectKind: SubjectKind =
                ClientConfig.resolveSubjectKind model.ActiveTeamId config

            let visibleModules =
                adminGroupFiltered |> List.filter (fun m -> m.Visibility resolvedSubjectKind)

            // One sidebar entry per module for legacy single-page modules
            // (sidebar Id = module Id); one entry per `PageConfig` for
            // multi-page modules (`PageViews = Some ...`), with composite
            // Id `"{moduleId}{pageRoute}"`. Route strings conventionally
            // start with `/`, so direct concatenation keeps the form
            // readable (e.g. `"SalesAnalysis/sku-analysis"`). The
            // composite Id round-trips through `parseSidebarId` back to
            // `(moduleId, pageRoute)` on click, and is compared against
            // `selectedSidebarId` below for the active-border highlight.
            let views: Toolup.Sidebar.SidebarModuleView list =
                visibleModules
                |> List.collect (fun moduleImpl ->
                    let hasData =
                        moduleImpl.NeedsData
                        |> Option.map (fun check -> check hasDataForType)
                        |> Option.defaultValue true

                    match moduleImpl.PageViews, moduleImpl.Definition.Pages with
                    | None, _
                    | Some _, ([] | [ _ ]) ->
                        // Single-page modules: use Pages[0].Icon when set,
                        // else auto-derive from Definition.Icon.
                        let icon = singlePageIcon moduleImpl.Definition

                        [
                            {
                                Id = moduleImpl.Definition.Id
                                Name = moduleImpl.Definition.Name
                                Icon = icon
                                HasData = hasData
                                Group = moduleImpl.Group
                            }
                        ]
                    | Some _, pages ->
                        pages
                        |> List.map (fun page -> {
                            Id = $"{moduleImpl.Definition.Id}{page.Route}"
                            Name = page.Title
                            Icon = page.Icon
                            HasData = hasData
                            Group = moduleImpl.Group
                        }))

            Toolup.Sidebar.buildSections views model.SidebarPrefs

        // Header team switcher — only rendered when the deployment
        // declares a `Team` surface with `Switching = HeaderSwitcher`
        // (the retiring `MultiTeam` UX intent). Single-team deployments
        // (`Switching = NoSwitcher`) don't surface a switcher. Hidden
        // until `MyTeams` has loaded so we don't render an empty
        // dropdown during the initial round-trip. Two-or-more
        // memberships is the threshold; if the user is in only one
        // team there's nothing useful to switch to.
        let teamSwitcher =
            if ClientConfig.hasMultiTeamSwitcher config && model.MyTeams.Length >= 2 then
                let activeName =
                    model.ActiveTeamId
                    |> Option.bind (fun id -> model.MyTeams |> List.tryFind (fun t -> t.TeamId = id))
                    |> Option.map _.Name
                    |> Option.defaultValue "Select team"

                Some(
                    Html.div [
                        prop.className "relative inline-block"
                        prop.children [
                            Html.details [
                                prop.className "group"
                                prop.children [
                                    Html.summary [
                                        prop.className
                                            "list-none cursor-pointer flex items-center gap-2 px-3 py-1.5 rounded border border-gray-200 hover:bg-gray-50 text-sm"
                                        prop.children [
                                            Html.span [ prop.className "text-gray-500"; prop.text "Team:" ]
                                            Html.span [
                                                prop.className "font-medium text-gray-900"
                                                prop.text activeName
                                            ]
                                            Html.span [ prop.className "text-gray-400 text-xs"; prop.text "▾" ]
                                        ]
                                    ]
                                    Html.div [
                                        prop.className
                                            "absolute right-0 mt-1 min-w-[200px] bg-white border border-gray-200 rounded shadow-lg z-20"
                                        prop.children [
                                            for team in model.MyTeams do
                                                let isActive = model.ActiveTeamId = Some team.TeamId

                                                Html.button [
                                                    prop.className [
                                                        "w-full text-left px-3 py-2 text-sm hover:bg-gray-50"
                                                        if isActive then
                                                            "bg-blue-50 font-medium text-blue-700"
                                                    ]
                                                    prop.disabled isActive
                                                    prop.text team.Name
                                                    prop.onClick (fun _ ->
                                                        if not isActive then
                                                            // Persist on the server, then dispatch
                                                            // shell-level `TeamSwitched`. Same path
                                                            // `TeamManagerUI` uses, just routed from
                                                            // the header instead of the page.
                                                            async {
                                                                try
                                                                    let! _ = teamApi.SetActiveTeam team.TeamId
                                                                    dispatch (TeamSwitched(Some team.TeamId))
                                                                with _ ->
                                                                    ()
                                                            }
                                                            |> Async.StartImmediate)
                                                ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                )
            else
                None

        // Compose the switcher with any caller-supplied HeaderAction.
        // Both render side-by-side in the page header.
        let combinedHeaderAction =
            match teamSwitcher, chrome.HeaderAction with
            | None, x -> x
            | Some sw, None -> Some sw
            | Some sw, Some ha -> Some(Html.div [ prop.className "flex items-center gap-3"; prop.children [ sw; ha ] ])

        let shell =
            Toolup.UIToolkit.Layout.AppShell
                config.AppName
                config.AppLogo
                sidebarSections
                selectedSidebarId
                (ModuleSelected >> dispatch)
                (SidebarGroupToggled >> dispatch)
                (SidebarModulePinToggled >> dispatch)
                (fun groupKey orderedIds -> dispatch (SidebarModuleReordered(groupKey, orderedIds)))
                content
                chrome.SidePanel
                combinedHeaderAction
                config.InputsPaneWidth

        // Render the toast renderer alongside the shell. Fixed-positioned
        // containers sit on top of everything else via z-index; the
        // built-in `ToastCentre` subscribes to `/api/notifications` on
        // mount and pops `SystemMessage` envelopes. Apps that want a
        // bespoke renderer pass `CustomToastCentre` with their own
        // subscription; apps that don't want toasts at all pass
        // `NoToastCentre`.
        let toastCentre =
            match config.ToastCentre with
            | NoToastCentre -> Html.none
            | DefaultToastCentre -> Components.ToastCentre.ToastCentre()
            | CustomToastCentre element -> element

        // Phase 6g.D: render every companion-supplied global overlay.
        // Each thunk is invoked here on every shell render so its
        // hooks register correctly. Typical consumers are floating
        // banners / status indicators owned by a companion.
        let globalOverlays = config.GlobalOverlays |> List.map (fun thunk -> thunk ())

        // Wrap in FeatureFlags provider so deeply-nested views can read
        // the resolved flag map via the `flag` / `variant` hooks without
        // threading it through props. Nested inside AgGridProvider so
        // grid cell renderers can also consult flags.
        //
        // The declared-keys set unions every module's `FeatureFlags`
        // declaration with whatever keys the server surfaced in the
        // prefetch; reads against a key outside the union log a
        // `console.warn` (typo protection). Pre-fetch renders see only
        // the module-declared slice — server-only flags aren't caught
        // until `FlagsLoaded` fires, but the warnings are one-shot so
        // a brief mid-boot false alarm is the price of keeping the
        // helper pure and per-render cheap.
        let declaredKeys =
            let moduleKeys =
                modules |> List.collect _.FeatureFlags |> List.map _.Key |> Set.ofList

            let serverKeys = model.ResolvedFlags |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            Set.union moduleKeys serverKeys

        let flagged =
            FeatureFlags.provider
                declaredKeys
                model.ResolvedFlags
                (React.Fragment([ shell; toastCentre ] @ globalOverlays))

        // Wrap in `ProcessedDataContext.Context` provider — publishes
        // the platform-aggregated `ProcessedFileEntry` list to module
        // views via `ProcessedData.forType`.
        let withProcessedData =
            ProcessedDataContext.Context.Provider(model.ProcessedData, flagged)

        // Wrap in AgGridProvider — supplies AG Grid modules and optional license key
        // to all grid instances in the React tree via context.
        ToolUp.Platform.AgGrid.provider config.GridModules [ withProcessedData ]

    /// Wrap the rendered shell with the auth UI handler registered
    /// for the configured `AuthUIMode`. `AnonymousKind` / `ClaimBearerKind`
    /// / `NoAuthUI` pass through unchanged; other subject kinds
    /// delegate to a companion (OidcClient, ClerkUI) that has called
    /// `AuthUIProvider.register` at load time.
    ///
    /// Public from Phase 3b.B so outer composers (`AIClientConfig.withSidePanel`
    /// + custom composition roots that build their own Elmish program over
    /// `Client.view`) can wrap the shell with the same auth gate `program`
    /// applies. Calling `Client.view` directly from an outer view bypasses
    /// the gate entirely — the OIDC sign-in screen never renders and the
    /// shell drops into a 401 storm for any unauthenticated visit.
    let viewWithSignIn (config: ClientConfig) modules chrome (model: Model) dispatch =
        let resolvedSubjectKind = ClientConfig.resolveSubjectKind model.ActiveTeamId config

        view config modules chrome model dispatch
        |> AuthUIProvider.gate config.AuthUI resolvedSubjectKind

    /// Inject the SDK built-in modules around the app's own list. The
    /// data manager is prepended so it sits near the top of the sidebar
    /// — the natural "where you add data" entry point. Every
    /// Admin-grouped built-in (team manager, team config, webhook
    /// admin, health monitor, usage dashboard) is appended so the
    /// Admin section's first-occurrence position lands AFTER the app's
    /// work groups, putting Admin near the bottom of the sidebar.
    /// `DebugOnly` modules are then partitioned to the very end of the
    /// list so their group renders below Admin — a fresh sidebar
    /// surfaces production work first, with experimental / scratch
    /// modules tucked underneath.
    ///
    /// Exposed so companion packages that build their own Program (rather
    /// than calling `run`) can apply the same module transformation.
    /// `config.ModuleFilter` is applied to the app-provided modules
    /// before built-ins are added — built-ins are never filtered out,
    /// so single-module dev runs still get the data manager in the
    /// sidebar.
    let prepareModules (config: ClientConfig) (modules: ErasedModule list) =
        let modules = modules |> ModuleFilter.apply config.ModuleFilter _.Definition.Name

        // DebugOnly modules are filtered out of the sidebar unless the
        // deployment opts in via `ClientConfig.ShowDebugOnlyModules`. The
        // JS still ships — goal is hiding under-development modules, not
        // reducing bundle size.
        let modules =
            if config.ShowDebugOnlyModules then
                modules
            else
                modules |> List.filter (fun m -> m.Availability = Always)

        // Partition app modules so DebugOnly entries land at the end
        // of the final list (below Admin); their first-occurrence
        // group position therefore renders last among declared groups.
        // The split runs against the post-filter `modules` so Release
        // builds (where DebugOnly has already been stripped) get an
        // empty `debug` partition and the same final ordering as if
        // the partition were absent.
        let workApp, debugApp = modules |> List.partition (fun m -> m.Availability = Always)

        let allDataTypeDisplays = modules |> List.collect _.DataTypes

        // Leading SDK module — DataManager (Knowledge group). Prepended
        // so the "add data" entry sits near the top of the sidebar.
        let leading =
            match config.DataManager with
            | NoDataManager -> []
            | DefaultDataManager -> [ FileManagerUI.create allDataTypeDisplays None ]
            | ConfiguredDataManager dmConfig -> [ FileManagerUI.create allDataTypeDisplays (Some dmConfig) ]
            | ExternalDataManager custom -> [ custom ]

        // Trailing SDK modules — Admin-grouped built-ins. Appended after
        // the app's modules so the Admin section's first-occurrence
        // position lands at the bottom of the sidebar.

        // Team manager is only meaningful when the deployment declares
        // a single-team `Team` surface (`Switching = NoSwitcher`) —
        // non-team and multi-team deployments either have no teams to
        // manage or use the header switcher UX. Opt-out is
        // `TeamManager = NoTeamManager` in ClientConfig; explicit
        // swap is `ExternalTeamManager m`.
        let isSingleTeamSurface =
            config.Surfaces
            |> List.exists (function
                | SurfaceProfile.Team { Switching = NoSwitcher } -> true
                | _ -> false)

        let teamManager =
            match isSingleTeamSurface, config.TeamManager with
            | true, DefaultTeamManager -> [ TeamManagerUI.create None ]
            | true, ConfiguredTeamManager tmConfig -> [ TeamManagerUI.create (Some tmConfig) ]
            | true, ExternalTeamManager custom -> [ custom ]
            | true, NoTeamManager
            | _, _ -> []

        // Configuration admin is meaningful when the deployment
        // declares any authenticated surface — Anonymous-only
        // deployments have no persistent scope so every read / write
        // would fail. Opt-out per config; explicit swap via
        // `ExternalTeamConfig`.
        let teamConfig =
            match ClientConfig.requiresAnyAuth config, config.TeamConfig with
            | false, _
            | _, NoTeamConfig -> []
            | _, DefaultTeamConfig -> [ TeamConfigUI.create None ]
            | _, ConfiguredTeamConfig cfg -> [ TeamConfigUI.create (Some cfg) ]
            | _, ExternalTeamConfig custom -> [ custom ]

        // Webhook admin: same scope rule as TeamConfig — Anonymous-only
        // deployments have no persistent scope to attach subscriptions
        // to, so the module is omitted there regardless of
        // `WebhookAdmin` setting.
        let webhookAdmin =
            match ClientConfig.requiresAnyAuth config, config.WebhookAdmin with
            | false, _
            | _, NoWebhookAdmin -> []
            | _, DefaultWebhookAdmin -> [ WebhookAdminUI.create None ]
            | _, ConfiguredWebhookAdmin cfg -> [ WebhookAdminUI.create (Some cfg) ]
            | _, ExternalWebhookAdmin custom -> [ custom ]

        // Phase 4b — Platform Admin module. Mode-agnostic: registered
        // unconditionally and gated by the shell's sidebar filter
        // (commit 4f.2) on `PlatformRole.PlatformAdmin`. Anonymous-mode
        // suppression dropped post-smoke-test (2026-05-10): a
        // bootstrapped admin in Anonymous mode legitimately holds the
        // role (via TOOLUP_INITIAL_PLATFORM_ADMIN or AutoBootstrapDevAdmin)
        // and should reach the admin surface. Non-admins never see the
        // entry because the role filter hides the entire "Platform
        // Admin" group when `PlatformRole = None`.
        let platformAdmin =
            match config.PlatformAdmin with
            | NoPlatformAdmin -> []
            | DefaultPlatformAdmin -> [ PlatformAdminUI.create None config ]
            | ConfiguredPlatformAdmin cfg -> [ PlatformAdminUI.create (Some cfg) config ]
            | ExternalPlatformAdmin custom -> [ custom ]

        // Permissions admin (Tidy-Up #3 closure of Phase 4 + Phase 5).
        // Anonymous mode skipped by construction — the server-side
        // `PermissionApi.GetTeamPermissions` returns `Error` for
        // unscoped callers, so the module would render an empty
        // error pane. Suppress at the sidebar level instead. Sits
        // in the standard "Admin" group alongside TeamConfig /
        // WebhookAdmin / DataIngestion; Owner/Admin gating on the
        // write paths is enforced server-side via PermissionApi.
        let permissionsAdmin =
            match ClientConfig.requiresAnyAuth config, config.PermissionsAdmin with
            | false, _
            | _, NoPermissionsAdmin -> []
            | _, DefaultPermissionsAdmin -> [ PermissionsAdminUI.create None ]
            | _, ConfiguredPermissionsAdmin cfg -> [ PermissionsAdminUI.create (Some cfg) ]
            | _, ExternalPermissionsAdmin custom -> [ custom ]

        // Health monitor admin (Phase 9p): re-grouped to "Platform Admin"
        // in commit 4f.1 + 4f.3 alongside the role re-gate to
        // `canModifyPlatformConfig`. Mode-agnostic post-Phase-4b — same
        // reasoning as the Platform Admin module above. Bootstrapped
        // admin in any mode (including Anonymous dev) reaches the
        // panels; non-admins are hidden by the sidebar role filter.
        let healthMonitor =
            match config.HealthMonitor with
            | NoHealthMonitor -> []
            | DefaultHealthMonitor -> [ HealthMonitorUI.create None ]
            | ConfiguredHealthMonitor cfg -> [ HealthMonitorUI.create (Some cfg) ]
            | ExternalHealthMonitor custom -> [ custom ]

        // Phase 9p.A — service-status-board admin. Same Platform-Admin
        // gating as HealthMonitor: composes deployment-wide observability
        // surfaces (Health, Preflight, Drift, RateLimit, JobQueue,
        // SmokeTest) into one snapshot. Mode-agnostic by the same
        // reasoning — a bootstrapped admin in any mode (including
        // Anonymous dev) reaches the board; non-admins are hidden by
        // the sidebar role filter.
        let serviceStatusBoard =
            match config.ServiceStatusBoard with
            | NoServiceStatusBoard -> []
            | DefaultServiceStatusBoard -> [ ServiceStatusBoardUI.create None ]
            | ConfiguredServiceStatusBoard cfg -> [ ServiceStatusBoardUI.create (Some cfg) ]
            | ExternalServiceStatusBoard custom -> [ custom ]

        // Phase 9d — usage dashboard. Same Anonymous suppression as
        // HealthMonitor: Anonymous deployments have no role concept and
        // exposing tenant cost telemetry to every visitor is a
        // reconnaissance gift. Server-side handler short-circuits
        // Anonymous independently. Owner/Admin gate is enforced
        // server-side; Member-role users see the sidebar entry but the
        // table renders an "only owners and admins" error message.
        let usageDashboard =
            match ClientConfig.requiresAnyAuth config, config.UsageDashboard with
            | false, _
            | _, NoUsageDashboard -> []
            | _, DefaultUsageDashboard -> [ UsageDashboard.create None ]
            | _, ConfiguredUsageDashboard cfg -> [ UsageDashboard.create (Some cfg) ]
            | _, ExternalUsageDashboard custom -> [ custom ]

        // Phase 10b — data-ingestion admin. Same Anonymous suppression
        // as TeamConfig / WebhookAdmin / HealthMonitor / UsageDashboard:
        // Anonymous deployments have no role concept and exposing data-
        // source credentials to every visitor is a reconnaissance gift.
        // Pair with `ServerConfig.DataIngestion = EnabledDataIngestion`
        // — the admin renders an empty list when ingestion is disabled
        // server-side, but it's harmless to leave the sidebar entry in
        // place for future enablement.
        let dataIngestionAdmin =
            match ClientConfig.requiresAnyAuth config, config.DataIngestionAdmin with
            | false, _
            | _, NoDataIngestionAdmin -> []
            | _, DefaultDataIngestionAdmin -> [ DataIngestionUI.create None ]
            | _, ConfiguredDataIngestionAdmin cfg -> [ DataIngestionUI.create (Some cfg) ]
            | _, ExternalDataIngestionAdmin custom -> [ custom ]

        // Phase 9h — data-subject-request admin. Same Anonymous
        // suppression: Anonymous deployments have no persistent scope
        // for a request to attach to. Default `NoDataSubjectRequestAdmin`
        // — opt in by setting `ClientConfig.DataSubjectRequestAdmin`
        // AND `ServerConfig.DataSubjectRequests = Enabled policy` on
        // the server (the API endpoint short-circuits otherwise).
        // Owner / Admin gating is enforced server-side by the handler;
        // the sidebar entry renders for every authenticated caller in
        // non-Anonymous modes.
        let dataSubjectRequestAdmin =
            match ClientConfig.requiresAnyAuth config, config.DataSubjectRequestAdmin with
            | false, _
            | _, NoDataSubjectRequestAdmin -> []
            | _, DefaultDataSubjectRequestAdmin -> [ DataSubjectRequestAdminUI.create None ]
            | _, ConfiguredDataSubjectRequestAdmin cfg -> [ DataSubjectRequestAdminUI.create (Some cfg) ]
            | _, ExternalDataSubjectRequestAdmin custom -> [ custom ]

        let trailing =
            teamManager
            @ teamConfig
            @ webhookAdmin
            @ permissionsAdmin
            @ platformAdmin
            @ healthMonitor
            @ serviceStatusBoard
            @ usageDashboard
            @ dataIngestionAdmin
            @ dataSubjectRequestAdmin

        leading @ workApp @ trailing @ debugApp

    /// Aggregate every module's `ClientQueryHandlers` into the per-module
    /// registry of the shared `ClientModuleQueryBus`. Modules are keyed by
    /// `Definition.Id` (the same identifier server-side handlers register
    /// under), so a deployment that ships both server and client handlers
    /// for the same `(moduleName, queryKey)` will prefer the local one —
    /// the client bus only falls back to HTTP when no local handler
    /// matches. SDK-built-in modules (file manager, team manager,
    /// team-config admin) declare no handlers today; this aggregation
    /// runs after `prepareModules` so if they ever do, they compose
    /// uniformly with app modules.
    ///
    /// Public so companion packages (ToolUp.AI's `withSidePanel`) can
    /// construct the same bus their outer program passes to
    /// `Client.init` / `Client.update`.
    let buildQueryBus (allModules: ErasedModule list) : IModuleQueryBus =
        let entries =
            allModules
            |> List.collect (fun m -> m.ClientQueryHandlers |> List.map (fun h -> m.Definition.Id, h))

        let registry = ModuleQueryClient.buildRegistry entries
        ModuleQueryClient.ClientModuleQueryBus(registry) :> IModuleQueryBus

    // ─── Phase 13a — explicit-composition validators + seam wiring ─

    /// Fail-loud check that every declared `AuthUI` / `DataSource.Kind`
    /// has a registered handler, and that no two handlers compete for
    /// the same tag. Called once during `program` boot.
    let private validateHandlers (config: ClientConfig) : unit =
        let authUITag =
            match config.AuthUI with
            | OidcAuthUI _ -> Some("oidc", "ToolUp.AuthProviders.OidcRegister.handler")
            | ClerkAuthUI _ -> Some("clerk", "ToolUp.AuthProviders.ClerkRegister.handler")
            | NoAuthUI
            | CustomAuthUI _ -> None

        match authUITag with
        | Some(tag, companion) ->
            let hasHandler =
                config.Handlers.AuthUIHandlers |> List.exists (fun (k, _) -> k = tag)

            if not hasHandler then
                failwithf
                    "ClientConfig.AuthUI declares mode \"%s\" but ClientConfig.Handlers.AuthUIHandlers contains no entry with that tag. Add %s to the handler list, or set ClientConfig.AuthUI = NoAuthUI."
                    tag
                    companion
        | None -> ()

        let dupes (entries: (string * 'a) list) =
            entries
            |> List.groupBy fst
            |> List.filter (fun (_, xs) -> List.length xs > 1)
            |> List.map fst

        let authUIDupes = dupes config.Handlers.AuthUIHandlers

        if not (List.isEmpty authUIDupes) then
            failwithf
                "ClientConfig.Handlers.AuthUIHandlers contains duplicate tag(s): %A. Each tag must be unique."
                authUIDupes

        let credDupes = dupes config.Handlers.DataSourceCredentialHandlers

        if not (List.isEmpty credDupes) then
            failwithf
                "ClientConfig.Handlers.DataSourceCredentialHandlers contains duplicate Kind(s): %A. Each Kind must be unique."
                credDupes

    /// Fail-loud check on header-provider collisions; one-time
    /// `console.warn` on names outside the reserved `X-ToolUp-*`
    /// prefix.
    let private validateRequestSeam (seam: ClientRequestSeam) : unit =
        let names =
            seam.HeaderProviders
            |> List.collect (fun p ->
                (try
                    p () |> Array.toList
                 with _ -> [])
                |> List.map fst)

        let dupes =
            names
            |> List.groupBy id
            |> List.filter (fun (_, xs) -> List.length xs > 1)
            |> List.map fst

        if not (List.isEmpty dupes) then
            failwithf
                "ClientConfig.RequestSeam.HeaderProviders emit duplicate header name(s): %A. Each provider must contribute distinct header names."
                dupes

        let isReserved (n: string) =
            n.StartsWith "X-ToolUp-"
            || n = "X-User-Id"
            || n = "Authorization"
            || n = CsrfClient.HeaderName

        let nonReserved = names |> List.distinct |> List.filter (isReserved >> not)

        if not (List.isEmpty nonReserved) then
            log.Warn(
                sprintf
                    "ClientConfig.RequestSeam.HeaderProviders emit header name(s) outside the reserved 'X-ToolUp-*' prefix: %A. Safe but may collide with future SDK additions; consider namespacing."
                    nonReserved
            )

    /// Compose the per-request identity-pairs getter: the SDK's own
    /// `UserSession.identityHeaderPairs` first, then any consumer-
    /// supplied providers from `config.RequestSeam.HeaderProviders`.
    /// Called per request at send time inside the guard.
    let private composeIdentityGetter (config: ClientConfig) : unit -> (string * string)[] =
        fun () ->
            let sdkPairs = UserSession.identityHeaderPairs ()

            let consumerPairs =
                config.RequestSeam.HeaderProviders
                |> List.collect (fun p ->
                    try
                        p () |> Array.toList
                    with _ -> [])
                |> List.toArray

            Array.append sdkPairs consumerPairs

    let private composeApiOriginGetter (config: ClientConfig) : unit -> string =
        fun () ->
            match config.RequestSeam.ApiOrigin() with
            | Some s -> s
            | None -> ""

    /// One-line structured boot summary — operators can verify
    /// composition shape without opening dev tools.
    let private bootSummary (config: ClientConfig) (modules: ErasedModule list) : string =
        let authUI =
            match config.AuthUI with
            | NoAuthUI -> "none"
            | OidcAuthUI _ -> "oidc"
            | ClerkAuthUI _ -> "clerk"
            | CustomAuthUI _ -> "custom"

        // Phase 66 Stream B.8 — log the canonical surfaces label.
        // Single-surface deployments render their pre-66 mode name
        // (`"Individual"` / `"Anonymous"` / etc.) so existing
        // grep-on-startup tooling stays stable; mixed-mode deployments
        // render a `+`-joined list (e.g. `"Anonymous + Individual"`).
        let surfacesLabel =
            let labelOne =
                function
                | SurfaceProfile.Anonymous _ -> "Anonymous"
                | SurfaceProfile.AuthenticatedUser { Persistence = Persistent } -> "Individual"
                | SurfaceProfile.AuthenticatedUser { Persistence = Ephemeral } -> "AuthenticatedEphemeral"
                | SurfaceProfile.Team { Switching = NoSwitcher } -> "Team"
                | SurfaceProfile.Team { Switching = HeaderSwitcher } -> "MultiTeam"
                | SurfaceProfile.ClaimBearer _ -> "ClaimBearer"

            config.Surfaces |> List.map labelOne |> String.concat " + "

        let credKinds = config.Handlers.DataSourceCredentialHandlers |> List.map fst

        let credKindsStr =
            if List.isEmpty credKinds then
                "none"
            else
                String.concat "," credKinds

        let providers = config.RequestSeam.HeaderProviders |> List.length
        let bridge = if config.AuthBridge.IsSome then "yes" else "no"

        let kb =
            if config.Handlers.NarrativeCommitHandler.IsSome then
                "yes"
            else
                "no"

        sprintf
            "[ToolUp] composed | surfaces=%s | authUI=%s | modules=%d | credentialUIs=%s | extraHeaderProviders=%d | authBridge=%s | knowledgeBase=%s"
            surfacesLabel
            authUI
            (List.length modules)
            credKindsStr
            providers
            bridge
            kb

    /// Phase 13a — minimal pre-dispatcher request-seam setup. Performs
    /// the side effects that must happen BEFORE any `/api/*` request
    /// can fly, including from a `PublicEntryDispatchers` short-
    /// circuit path:
    ///   * `UserSession.configure` mode + dev-default user-id.
    ///   * `CsrfClient.installRequestGuard` with composed seam thunks.
    ///   * `CsrfClient.prefetch` the per-session token (skipped in
    ///     `Anonymous` mode — no sessions → no per-session token).
    ///
    /// Called by both `Client.run` and `AIClientConfig.run` BEFORE
    /// consulting `tryDispatchPublicEntry`, so dispatchers that fire
    /// authenticated requests get the same header attachment any
    /// other request gets. (The 0.1.x `do installRequestGuard ()`
    /// module-load block in `CsrfClient.fs` had the same timing
    /// effect implicitly; Phase 13a relocates it to the explicit
    /// run-entry path here.)
    ///
    /// Also called by `boot` below — idempotent via
    /// `UserSession.configure` being a plain mutable set + the
    /// `CsrfClient.guardInstalled` sentinel.
    let installRequestSeam (config: ClientConfig) : unit =
        // Phase 66 Stream B.8 — derive the boot-time `SubjectKind`
        // from declared surfaces. `installRequestSeam` runs before the
        // shell exists (no `ActiveTeamId` yet), so the seed kind is
        // either `AnonymousKind` (anonymous-only deployment),
        // `ClaimBearerKind` (single-shape claim-bearer), or `UserKind`
        // (any authenticated surface present, pre-team-resolution).
        // The shell's later render via `resolveSubjectKind` upgrades
        // to `TeamMemberKind` once `model.ActiveTeamId` resolves.
        UserSession.configure (ClientConfig.resolveSubjectKind None config)
        // Phase 4b dev convenience — when DevDefaultUserId is set the
        // first-visit seed becomes a stable value instead of a GUID, so
        // the local-run X-User-Id matches ServerConfig.AutoBootstrapDevAdmin
        // end-to-end. Existing localStorage values are preserved.
        UserSession.configureDevDefault config.DevDefaultUserId

        // Phase 13a — install the per-request guard with explicit
        // seam thunks (replaces the legacy module-load do block in
        // CsrfClient that read from setIdentityProvider / setApiOrigin
        // mutables).
        let identityGetter = composeIdentityGetter config
        let apiOriginGetter = composeApiOriginGetter config
        CsrfClient.installRequestGuard identityGetter apiOriginGetter

        // Phase 9j — pre-fetch the per-session CSRF token so the
        // request-guard can attach it to the first mutating call.
        // Skipped in anonymous-only deployments (no sessions → no
        // per-session CSRF token → the server doesn't mount
        // `/api/csrf-token`, so the prefetch would surface as a
        // benign-but-noisy 404 plus a request-guard warn-once on
        // every boot). In every other shape the prefetch is a no-op
        // against a `NoSecurityHardening` server (the route 404s,
        // the cache stays empty). Phase 66 Stream B.8 — gate flipped
        // from `config.Mode = Anonymous` to `not requiresAnyAuth`;
        // mixed-mode deployments with any authenticated surface
        // present prefetch (the authenticated path needs the token).
        if ClientConfig.requiresAnyAuth config then
            CsrfClient.prefetch ()

    /// Phase 13a boot-time composition setup for the full shell.
    /// Performs everything the shell needs that *isn't* on the
    /// pre-dispatcher path (those bits are in `installRequestSeam`):
    ///   * Populate per-tab handler caches from `config.Handlers`
    ///     (`AuthUIProvider`, `DataSourceCredentialUIRegistry`,
    ///     `Toolup.NarrativeCommit`).
    ///   * Run validators (declared mode ⇔ handler parity, duplicate
    ///     tag detection, header-name collision detection).
    ///   * Re-call `installRequestSeam` (idempotent) — covers outer
    ///     composers that bypass `Client.run` and call `boot`
    ///     directly without having installed the seam yet.
    ///   * Install the optional `AuthBridge`.
    ///   * Emit the boot-line composition summary.
    ///
    /// Called from `Client.program` AND from every outer entry point
    /// that bypasses `Client.program` (`AIClientConfig.withSidePanel`,
    /// future RAG / Forms / KB top-level composers). Idempotent — the
    /// underlying setters / installers all guard against double-init.
    let boot (config: ClientConfig) (modules: ErasedModule list) : unit =
        // Phase 13a — populate per-tab handler caches from explicit
        // config (replaces companion module-load register() side
        // effects).
        AuthUIProvider.setHandlers config.Handlers.AuthUIHandlers
        DataSourceCredentialUIRegistry.setHandlers config.Handlers.DataSourceCredentialHandlers
        Toolup.NarrativeCommit.setHandler config.Handlers.NarrativeCommitHandler

        // Phase 13a — validate explicit composition against declared
        // modes. Fails loud naming the missing companion / import.
        validateHandlers config
        validateRequestSeam config.RequestSeam

        // Phase 55 — refuse deployments where every consumer module is
        // ungrouped. The sidebar's reserved `_other` bucket collapses
        // title-less when no other declared group is present, so the
        // operator sees an empty sidebar even though every module is
        // registered and accessible. See `ModuleGroupingValidator.fs`.
        ModuleGroupingValidator.validate modules

        // Phase 13a — install the request seam if not already installed
        // (idempotent). Covers outer composers that bypass `Client.run`
        // and call `boot` directly.
        installRequestSeam config

        // Phase 6k Workstream A. Install the optional auth bridge so
        // it can refresh JWTs into localStorage + the SSE auth cookie
        // before the first request goes out. No-op when None — the
        // existing X-User-Id / ?userId= path keeps working.
        match config.AuthBridge with
        | Some bridge -> UserSession.installBridge bridge
        | None -> ()

        // Phase 13a — boot-line summary log so operators can verify
        // composition without dev tools.
        log.Info(bootSummary config modules)

    /// Build the shell Elmish Program without running it. Outer composition
    /// (e.g. AIClientConfig.withAIAssistant) consumes this to layer
    /// additional state, messages, subscriptions, and view chrome on top.
    let program (config: ClientConfig) (modules: ErasedModule list) : Program<unit, Model, Msg, ReactElement> =
        boot config modules

        let allModules = prepareModules config modules
        let queryBus = buildQueryBus allModules

        // AuthUI gating is driven by ClientConfig.AuthUI — default
        // NoAuthUI is pass-through, so DEBUG and RELEASE behave the
        // same unless the app opts into a sign-in mode. Apps that
        // want the old "no sign-in in DEBUG, sign-in in RELEASE"
        // behaviour set AuthUI conditionally in their own Client.fs.
        // Structured Elmish error reporter (ToolUp.Elmish 0.4.0 primitive).
        // Routes runtime exceptions through `ClientConfig.OnElmishError`
        // when set; otherwise falls back to the categorised default
        // logger so devtools still surface them. Replaces the previous
        // unstructured `(string * exn) -> unit` `onError` shape.
        let elmishLog = Logger.forCategory "client.elmish"

        let elmishReporter (ctx: ErrorContext) =
            match config.OnElmishError with
            | Some sink ->
                try
                    sink ctx
                with ex ->
                    // Defensive: a throwing sink mustn't crash the reporter.
                    elmishLog.Error("OnElmishError sink raised", Some ex)
            | None -> elmishLog.Error(ctx.Message, Some ctx.Exception)

        // 0.4.1 — `withConsoleTrace` is [<Obsolete>]; replaced by an
        // update interceptor that logs each transition through the
        // category logger. Same shape (initial state / message /
        // updated state) but now grep-able by category.
        let traceLog = Logger.forCategory "client.elmish.trace"

        let traceUpdate (msg: Msg) (model: Model) =
            let nextModel, nextCmd = update config queryBus allModules msg model
            traceLog.Debug(sprintf "msg=%A activeModule=%s" msg nextModel.ActiveModuleId)
            nextModel, nextCmd

        let prog =
            Program.mkProgram
                (init config queryBus allModules)
                (if config.EnableElmishConsoleTrace then
                     traceUpdate
                 else
                     update config queryBus allModules)
                (fun model dispatch -> viewWithSignIn config allModules emptyChrome model dispatch)

        // 0.4.1 — boot-time NavigationRequest + NotificationClient
        // subscriptions registered as lifetime-aware `EffectHandle`s.
        // The runtime disposes them on `IDispatcher.Terminate()` (which
        // the React adapter fires on `beforeunload` and HMR fires on
        // hot-reload), so the SSE / notification leak across page
        // navigation and HMR is gone. Wrapping the upstream
        // `unit -> unit` unsubscribe into `IDisposable.Dispose` is
        // mechanical — both `NavigationRequest.subscribe` and
        // `NotificationClient.subscribe` already return their own
        // teardown thunk.
        let navigationEffect =
            EffectHandle.programLifetime "navigation-request" (fun dispatch ->
                let unsubscribe =
                    NavigationRequest.subscribe (fun sidebarId -> dispatch (ModuleSelected sidebarId))

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        let notificationsEffect =
            EffectHandle.programLifetime "notifications-stream" (fun dispatch ->
                let unsubscribe =
                    NotificationClient.subscribe (fun envelope ->
                        match envelope.Notification with
                        | Notification.ModuleAction(moduleId, actionKey, payloadJson) ->
                            dispatch (ModuleActionReceived(moduleId, actionKey, payloadJson))
                        | Notification.MembershipChanged payload ->
                            // Server-side bridge filters by AffectedUserId
                            // before forwarding to this connection — every
                            // event delivered here targets this user. Route
                            // by `ChangeKind` to internal Msgs that compare
                            // against `model.ActiveTeamId` (this closure
                            // can't read the model directly).
                            match payload.ChangeKind with
                            | MembershipChangeKind.Removed -> dispatch (MembershipRevoked payload.TeamId)
                            | MembershipChangeKind.ActiveTeamSet -> dispatch (MembershipActiveTeamSet payload.TeamId)
                            | MembershipChangeKind.Added
                            | MembershipChangeKind.RoleChanged -> ()
                        | _ -> ())

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        prog
        |> Program.withErrorReporter elmishReporter
        |> Program.withEffect navigationEffect
        |> Program.withEffect notificationsEffect

    /// Returns `true` if a registered `PublicEntryDispatchers` short-circuits
    /// the full shell bootstrap (the dispatcher has rendered its own program).
    /// Returns `false` otherwise — the caller should bootstrap the full shell.
    let tryDispatchPublicEntry (config: ClientConfig) : bool =
        config.PublicEntryDispatchers |> List.exists (fun dispatch -> dispatch config)

    /// Run the client application with the given modules. Convenience entry
    /// point — builds the shell Program and starts React. Applications that
    /// layer a companion wrapper (e.g. ToolUp.AI's withAIAssistant) should
    /// call `program` directly, wrap, then run.
    ///
    /// Consults `ClientConfig.PublicEntryDispatchers` first; any dispatcher
    /// that returns `true` (e.g. matching a `/r/{token}` survey URL) has
    /// rendered its own minimal Elmish program and the full shell is skipped.
    let run (config: ClientConfig) (modules: ErasedModule list) =
        // Phase 13a — install the request seam BEFORE consulting
        // PublicEntryDispatchers so dispatchers that fire authenticated
        // requests get the same header attachment any other request
        // gets. The 0.1.x `do installRequestGuard ()` module-load block
        // in CsrfClient.fs had this timing effect implicitly; Phase 13a
        // relocates it here. For dispatchers that only hit anonymous
        // token-gated endpoints (PublicEmbed) this is benign overhead
        // (the prefetched CSRF token is unused) but it future-proofs
        // dispatchers that need authenticated headers.
        installRequestSeam config

        if tryDispatchPublicEntry config then
            ()
        else
            program config modules
            |> Program.withDispatcherHandle (fun dispatcher -> shellDispatcher <- Some dispatcher)
            |> Program.withReactSynchronous "elmish-app"
            |> Program.run