// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Elmish
open ToolUp.Elmish.React
open Feliz
open ProcessedDataTypes

// ─── Shell MVU ────────────────────────────────────────────────────

// HMR is always open; the ToolUp.Elmish.HMR overrides of `Program.run` /
// `Program.withReactSynchronous` etc. detect the absence of Vite's
// `import.meta.hot` at runtime and become identity functions in
// production builds. The previous `#if DEBUG`-gated open was removed
// when ToolUp.Platform stopped carrying compile-time gates (App-side
// `#if DEBUG` is unaffected).
open ToolUp.Elmish.HMR

module Client =

    let private log = Logger.forCategory "client.bootstrap"

    /// 0.5.16 — shell lifecycle phase. Gates whether the active module's
    /// `Init` has run and whether its view is mounted. Eliminates the
    /// cold-load "empty seed → real prefetched values" re-init that, on a
    /// slow first paint (Issue #4 territory), destroyed AG Grid mid-flight
    /// and produced the "table doesn't render until mouseover" symptom
    /// reported as Issue #3.
    ///
    /// `Prefetching` — only entered when init detects an auth-required
    /// deployment with a token present (so the boot-time loaders WILL
    /// fire and populate real `ModuleConfigs` / `ResolvedFlags`). The
    /// shell renders chrome (sidebar + header) plus a content-area
    /// skeleton; the active module is NOT mounted and its `Init` has
    /// NOT been called. The last of the `GatesFirstInit` boot sources to
    /// answer promotes to `Ready` exactly once, initialising the active
    /// module against the now-populated context.
    ///
    /// `Ready` — active module is mounted; `Init` has been called against
    /// real (or anonymous-empty) context. The steady-state phase.
    ///
    /// `Reprefetching` — entered from `Ready` when `AuthTokenAcquired`
    /// fires mid-session (the anonymous-side branch of a mixed-mode
    /// deployment whose user just signed in, or a returning user whose
    /// token surfaced after first render). Current view stays rendered
    /// (stale) so the user keeps their place while loaders refresh.
    /// Promoted back to `Ready` through the skip-when-unchanged guard — a
    /// true value change re-inits; a no-op refresh preserves the React
    /// subtree.
    type InitPhase =
        | Prefetching
        | Ready
        | Reprefetching

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
        /// Every initialised module's host — its state and its reset
        /// generation — keyed by module id. A module with no entry has not
        /// been initialised (or a reset cleared it); `Client.moduleState`
        /// reads one module's state.
        Hosts: Map<string, ModuleHost>
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
        /// Whether the boot-time `GetActiveTeam` fetch has resolved at
        /// least once. Distinguishes "not yet loaded" (the initial
        /// `ActiveTeamId = None`) from "loaded, and the user genuinely
        /// has no active team" — only the latter may trigger the
        /// sole-team auto-select (see `maybeAutoSelectSoleTeam`).
        /// Without the gate, a `MyTeamsLoaded` arriving before
        /// `ActiveTeamLoaded` would auto-select over a server-side
        /// pointer that simply hadn't loaded yet.
        ActiveTeamLoadCompleted: bool
        /// Phase 4b — caller's resolved `PlatformRole`. `None` until
        /// the boot-time `IsPlatformAdmin` fetch resolves, OR after a
        /// fetch that returned false. `Some PlatformAdmin` unlocks the
        /// platform-scoped admin sidebar groups. Survives team switches —
        /// the role is user-bound, not team-bound. Anonymous-mode
        /// deployments leave this `None` (the fetch returns false).
        PlatformRole: PlatformRole option
        /// Phase 568 — the caller's `TeamRole` on the ACTIVE team, the
        /// input `NavRole.TeamOwnerAdmin` is evaluated against. Loaded
        /// once per active team (on `ActiveTeamLoaded` at boot and again
        /// on `TeamSwitched`) by reading the team's membership list and
        /// picking out the caller's own row — the same derivation
        /// `TeamManagerUI` has always done module-internally, lifted to
        /// the shell so the sidebar fold can see it.
        ///
        /// `None` = not known: no active team, a deployment with no team
        /// scope (the load never fires), the membership read failed, or
        /// it is still in flight. `NavRole.TeamOwnerAdmin` fails OPEN
        /// against `None` — see that case's doc-comment. Team-bound, so
        /// unlike `PlatformRole` it is cleared and re-read on every
        /// switch.
        ActiveTeamRole: TeamRole option
        /// Phase 567 — which navigation area the sidebar is currently
        /// showing under `ShellChrome.AdminArea = ChromeFill.On()`. Ignored
        /// under the default inline groups. Flipped by the area switcher
        /// (reserved sidebar ids) and by deep-linking to an admin-area route.
        CurrentArea: ModuleArea
        /// Phase 245 — platform-admin "show all modules" escape. With
        /// per-team module exposure, a platform admin's sidebar is
        /// exposure-filtered like a member's (admins respect a team's
        /// hidden set). `true` bypasses that filter so the admin sees
        /// every managed module — including ones the active team hides —
        /// for triage/configuration; `false` (default) is the member
        /// view. Admin-only and team-scoped: inert for non-admins and
        /// when nothing is hidden. Not persisted; resets each shell start.
        ShowAllModules: bool
        /// The keys of the `GatesFirstInit` boot sources (the persisted
        /// configs and the resolved flags) that have not answered since the
        /// last boot or reset. A failure counts as an answer. When the set
        /// empties, the active module is initialised (first load) or
        /// re-initialised if what it was built from changed.
        AwaitingFirstInit: Set<string>
        /// 0.5.16 — shell lifecycle phase. See `InitPhase` doc-comment
        /// for the three-state contract. Drives the
        /// "defer-active-module-Init-until-prefetches-complete"
        /// discipline that eliminates the cold-load AG-Grid-destroyed-
        /// mid-flight race (Issue #3).
        InitPhase: InitPhase
        /// Phase 121 — failed boot loads accumulated for the standard
        /// dismissible banner (deduped by source; empty on a clean boot,
        /// GP 13). A failed load keeps its benign default (empty teams,
        /// no active team, …) AND records an entry here so the failure
        /// is distinguishable from genuinely-empty data (GP 9). Wiped by a
        /// team or locale reset like every other per-boot-cycle field.
        Degradations: BootDegradation.BootDegradation list
        /// A boot load was refused because this principal is a publication
        /// reader, whom every `/api` route refuses. The shell then renders the
        /// whole-app refusal in place of itself. Wiped with `Degradations`.
        AppAccessRefused: bool
        /// File snapshot prefetched at boot — its `Processed` entries. Merged
        /// into `ProcessedData` (so data modules see uploaded data on load
        /// without visiting Data Manager) until the DataManager module is
        /// mounted, at which point its live state becomes authoritative.
        PrefetchedProcessedData: ProcessedFileEntry list
        /// Phase 571 — the Ctrl+K command palette's overlay state (open,
        /// query, highlight counter). Carried on the shell model rather
        /// than inside the overlay component so the palette is
        /// Elmish-native: every transition is a message `update` can see,
        /// and selection reduces to the same `ModuleSelected` a sidebar
        /// click sends. `CommandPaletteNav.closed` for every deployment
        /// that leaves `ClientConfig.Shell.CommandPalette = FeatureMode.Disabled`
        /// (the default) — the field is inert, the component unmounted.
        CommandPalette: CommandPaletteNav.PaletteState
        /// Phase 444 — a locale the user (or a consumer module, via
        /// `LocaleRequest.request`) chose in-session, overriding whatever
        /// `ClientConfig.Locale` would otherwise resolve to. `None` for
        /// every deployment that never issues a switch — which is all of
        /// them until one opts in — so the resolve collapses to the
        /// declared mode and the field is inert (GP 13).
        ///
        /// Deliberately NOT cleared by `TeamSwitched`, unlike every other
        /// per-team field beside it: an explicit language choice is a
        /// property of the person reading, not of the team they are
        /// looking at, and silently reverting it on a team switch would
        /// be the more surprising behaviour. A `TeamDefault` deployment's
        /// team locale still wins for anyone who has *not* chosen.
        LocaleOverride: string option
    }

    type Msg =
        /// A message for the named module, from its own view or from a
        /// command its `Init` or `Update` returned. It goes to that module
        /// whichever module is active. With no state for that module (a
        /// team switch or a reset cleared it) there is nothing to land on,
        /// and it is dropped.
        | ModuleMsg of moduleId: string * message: obj
        | ModuleSelected of string
        | AccessibleModulesLoaded of AccessibleModulesResponse option
        /// Phase 245 — re-fetch `AccessibleModules` after a module's
        /// exposure changed (fired by a module via
        /// `ClientModuleContext.OnAccessibleModulesChanged`) so the
        /// sidebar + admin toggle update without a manual reload.
        | RefreshAccessibleModules
        /// Phase 245 — platform admin toggled the "show all modules"
        /// sidebar escape (reveal/hide the active team's hidden modules).
        | ToggleShowAllModules
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
        /// Boot-time data prefetch result. Carries the persisted file
        /// snapshot; its `Processed` entries are stored in
        /// `PrefetchedProcessedData` and merged into `ProcessedData` so data
        /// modules see uploaded data on load without visiting Data Manager.
        | DataSnapshotPrefetched of FileListSnapshot
        /// Phase 1046 — the server's `PlatformInfo` arrived. Its
        /// `FileManagementMounted` is the one answer to "does this
        /// deployment mount `FileManagementApi`": the shell arms the
        /// session-epoch reads on it and, when the data-manager slot is
        /// filled, loads the file snapshot. A deployment whose modules
        /// declare no data type makes no file-API call at all.
        | PlatformInfoLoaded of PlatformInfo
        /// Phase 6p — the server-side session file store this tab was
        /// talking to is gone (TTL eviction, process restart, scope
        /// change) and a new one has taken its place. Raised by
        /// `SessionEpoch` from whichever path noticed first: the server's
        /// `Platform.SessionStoreReset` notification, the on-focus epoch
        /// poll, the SSE-reconnect check, or a pre-flight guard. Clears
        /// the prefetched processed data and toasts the cause — the file
        /// list holders clear themselves through their own
        /// `SessionEpoch.subscribe`.
        | SessionStoreReset of SessionStoreResetNotification
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
        /// User expanded or collapsed a multi-page module's page subtree.
        /// The key is the bare module Id. Toggles
        /// `UserSidebarPreferences.ExpandedModules` and re-saves.
        | SidebarModuleExpandToggled of moduleId: string
        /// User hid a sidebar entry, or restored one from the "Hidden
        /// items" section (Phase 572). `entryId` is the sidebar id — a
        /// bare module id, or a composite `{moduleId}{pageRoute}` page id
        /// for a single page of a multi-page module. Toggles
        /// `UserSidebarPreferences.HiddenEntryIds` and re-saves.
        ///
        /// **Preference only.** The entry leaves this browser's rail and
        /// nothing else changes: no permission, no team exposure, no
        /// route. The page stays reachable by URL and stays listed in the
        /// command palette, and no other user is affected. The shell
        /// deliberately does NOT navigate away when the user hides the
        /// entry they are currently on — the page they are reading is not
        /// what they asked to change.
        | SidebarEntryHideToggled of entryId: string
        /// Server-driven `Notification.ModuleAction` targeting a module
        /// registered in this deployment. The shell looks up the module
        /// by Id, gates on `AccessibleModules`, calls the module's
        /// `ActionDecoder`, and steps the target module with the decoded
        /// `Msg`, initialising it first if it has no host. Silently drops on
        /// any gate failure — actions are best-effort UX, not an
        /// authorisation boundary (the server-side tool guard is the
        /// authorisation boundary). Phase 6c.
        | ModuleActionReceived of moduleId: string * actionKey: string * payloadJson: string
        /// Cross-module client event published by some module's `update`
        /// via `ModuleEvents.publish`. The shell fans the publication out
        /// to every registered module whose `EventSubscriptions` map
        /// carries a mapper for `topic`, decodes the `payload` into each
        /// subscriber's `Msg`, and steps that module with it (initialising
        /// a never-navigated module first).
        /// Identity-free: a module receives its own publication only when
        /// it also subscribes to the same topic. Dispatched by the
        /// boot-time `ModuleEvents.subscribe` lifetime effect. Topics with
        /// no subscribers are a no-op.
        | ModuleEventPublished of topic: string * payload: string
        /// User switched to a different team in `MultiTeam` mode (or any
        /// other path that calls `PlatformApi.SetActiveTeam`). Carries
        /// the new active team id, or `None` if the user was revoked
        /// from their active team and is now in the no-active-team
        /// state. The arm runs the shell's one `reset`: it clears the
        /// per-scope state, re-issues the boot sources a team switch
        /// clears, and re-inits the active module once they answer.
        | TeamSwitched of string option
        /// Phase 444 — the shell's active locale changed. Dispatched by
        /// the boot-time `LocaleRequest.subscribe` lifetime effect when a
        /// module (a settings page, a consumer's own language picker)
        /// calls `LocaleRequest.request`.
        ///
        /// Runs the shell's one `reset`, as a team switch does: a module
        /// that formatted a string at `Init` — a column header, a status
        /// label it holds in its own model — is not re-rendered into the
        /// new language by a React re-render, because the string is in its
        /// state rather than its view. Clearing the hosts and re-initialising
        /// is the only reset that reaches those.
        | LocaleSwitched of locale: string
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
        /// Server-driven `MembershipChanged.Added` for the current user —
        /// they were just added to a team (e.g. a Platform Admin created
        /// or provisioned one). Refreshes `MyTeams` so the new team
        /// appears in the header switcher in-session, without changing
        /// the active team (use the switcher to move). Previously this
        /// kind was dropped, so a freshly-created team only surfaced
        /// after a page reload.
        | MembershipAdded of teamId: string
        /// Initial / refresh load of the team list for the header
        /// switcher and any caller that wants membership-aware UI
        /// outside `TeamManagerUI`. Fired on init in team-scoped modes
        /// and after `TeamSwitched` to capture newly-created teams.
        | MyTeamsLoaded of TeamInfo list
        /// Phase 4b — boot-time `IsPlatformAdmin` fetch resolved.
        /// `true` lifts `PlatformRole = Some PlatformAdmin` and reveals
        /// the platform-scoped admin sidebar groups ("Platform Admin" /
        /// "Platform Management"); `false` (the default for non-admins,
        /// Anonymous mode, and any error path) leaves it `None` so the
        /// groups stay hidden.
        | PlatformRoleLoaded of bool
        /// Phase 568 — the caller's own `TeamRole` on the active team
        /// resolved (or was cleared, on a switch to no team). Drives
        /// `NavRole.TeamOwnerAdmin` in the sidebar visibility fold.
        /// `None` leaves the gate fail-open, exactly as before the role
        /// was carried at all.
        | ActiveTeamRoleLoaded of TeamRole option
        /// 0.5.5 — `UserSession.localStorage.toolup-auth-token`
        /// transitioned `None → Some`. Re-runs the boot-time API
        /// fetches (perms / configs / flags / role / team list /
        /// active team) that were deliberately skipped at `init` in
        /// deployments that require auth but had no token present at
        /// program start.
        ///
        /// Dispatched by the `programLifetime` effect wrapping
        /// `UserSession.onAuthTokenChange`. Idempotent — re-firing the
        /// loaders against an already-populated model just refreshes
        /// the state with whatever the server now reports under the
        /// authenticated identity.
        | AuthTokenAcquired
        /// Initial load of the user's active team id. Fired on init in
        /// team-scoped modes so the header switcher can highlight the
        /// current selection without an extra round-trip.
        | ActiveTeamLoaded of string option
        /// Phase 12c — fired by the "Reload module" button inside the
        /// per-module error boundary (and available to any caller that
        /// wants to reset a single module). Re-initialises the named
        /// module and advances its host's generation, which remounts the
        /// boundary. Other modules and per-team state untouched. If `Init`
        /// throws, the module is left with no host, `OnError` reports it,
        /// and the next `ModuleSelected` for this module retries.
        | ResetModule of moduleId: string
        /// Phase 121 — a boot loader threw (routed via `Cmd.OfAsync.either`
        /// in `bootLoadCmd`, or dispatched directly by a failing effect).
        /// Accumulates a `BootDegradation` entry on the Model and warns
        /// through the category logger; the Model keeps the loader's
        /// benign default, so rendering is unchanged except for the
        /// banner.
        | BootLoadFailed of source: string * error: string
        /// The user clicked Retry on the degradation banner. Optimistically
        /// clears the source's entry and re-runs the source's `Load` from
        /// `BootSources.all`; a re-failure re-adds via `BootLoadFailed`.
        | RetryBootLoad of source: string
        /// Phase 121 — a previously-degraded source recovered through its
        /// own channel (the auth bridge's refresh interval succeeding
        /// again). Clears the entry without re-firing anything.
        | BootLoadRecovered of source: string
        /// Phase 121 — user dismissed the degradation banner. Clears every
        /// entry; later failures re-surface it.
        | DismissBootDegradations
        /// A boot load came back as the publication-reader refusal.
        | PublicationReaderRefused
        /// Phase 571 — Ctrl+K / Cmd+K was pressed outside a text field
        /// (or the palette was opened by some other affordance). Opens
        /// the overlay with an empty query and the first entry
        /// highlighted — a shortcut whose result depends on when it was
        /// last used is a shortcut nobody trusts.
        | CommandPaletteOpened
        /// Phase 571 — Escape, a backdrop click, or a completed
        /// selection. Returns the palette to `closed`.
        | CommandPaletteDismissed
        /// Phase 571 — the query box changed. Resets the highlight: the
        /// ranking has moved, so the old cursor position points at an
        /// unrelated row.
        | CommandPaletteQueryChanged of string
        /// Phase 571 — arrow-key movement, as a signed delta on an
        /// unbounded counter. The renderer wraps it into range because
        /// it is the only side that knows how many entries the query
        /// matched (`CommandPaletteNav.highlightIndex`).
        | CommandPaletteHighlightMoved of int
        /// Phase 571 — an entry was activated (Enter or click). Carries
        /// the composite sidebar id, so the arm closes the overlay and
        /// re-dispatches the ordinary `ModuleSelected` — the palette
        /// navigates through the existing page path (Phase 12b
        /// semantics, route guard included), never around it.
        | CommandPaletteSelected of sidebarId: string

    // Header freshness is the CsrfClient request-guard's job — see `UserSession.withRequestHeaders` + `installRequestSeam` below.
    // Proxies are constructed at module load; identity + CSRF headers are read live per request by the send-time guard.

    /// Server-side ToolUp.Remoting proxy for the team API. Used to
    /// fetch the user's team memberships, drive header-switcher active
    /// team selection, and persist team switches initiated from the
    /// shell. Mirrors `TeamManagerUI`'s proxy — split from the original
    /// `PlatformApi` umbrella to keep team-CRUD calls behind a focused
    /// route prefix.
    let private teamApi: TeamApi =
        Api.makeProxy<TeamApi> (customOptions = UserSession.withRequestHeaders)

    /// Server-side ToolUp.Remoting proxy for the accessibility helper
    /// (`GetAccessibleModules`). Used to fetch the per-user accessible-
    /// modules list on shell startup so the sidebar can hide entries
    /// the user can't use. Not a security boundary — the server's
    /// `ServerModule.withGuardedApi` gate is the actual enforcement; this proxy
    /// is purely UX.
    let private accessibilityApi: AccessibilityApi =
        Api.makeProxy<AccessibilityApi> (customOptions = UserSession.withRequestHeaders)

    /// Phase 4b — Platform Admin API proxy. Used at shell startup to
    /// resolve the caller's `PlatformRole`; the result drives the
    /// platform-scoped admin sidebar groups' visibility (admin-only).
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

    /// File-management proxy for the boot-time data prefetch. `ListFiles`
    /// returns the persisted file snapshot whose `Processed` field is the
    /// same `ProcessedFileEntry list` the DataManager module publishes once
    /// mounted — fetching it at boot lets every data module see its uploaded
    /// data on load, without the user first visiting the Data Manager page.
    let private fileApi: FileManagementApi =
        Api.makeProxy<FileManagementApi> (customOptions = UserSession.withRequestHeaders)

    let private loadFileSnapshot = async { return! fileApi.ListFiles() }

    /// Phase 1046 — the always-on, ungated platform-info proxy. Its
    /// `FileManagementMounted` is the server's own mount decision, read at
    /// boot BEFORE any `FileManagementApi` call so the shell never calls an
    /// API the server did not mount.
    let private platformInfoApi: PlatformInfoApi =
        Api.makeProxy<PlatformInfoApi> (customOptions = UserSession.withRequestHeaders)

    let private loadPlatformInfo = async { return! platformInfoApi.GetPlatformInfo() }

    /// Async wrapper around `platformApi.GetAccessibleModules` that
    /// swallows any error — the client is expected to keep working if
    /// the RBAC endpoint is unavailable (e.g. an older server that
    /// doesn't expose it). A failed load leaves the sidebar
    /// permissive.
    let private loadAccessibleModules = async {
        // Phase 121 — no internal catch: a thrown fetch routes through
        // `bootLoadCmd`'s `Cmd.OfAsync.either` to `BootLoadFailed`, and
        // `AccessibleModules` stays `None` (the permissive fallback) —
        // same render, but the failure is now visible.
        let! response = accessibilityApi.GetAccessibleModules()
        return Some response
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

    /// Fetch every registered module's persisted config map (including
    /// the reserved `_platform` entry) in one batch round-trip via
    /// `GetAllModuleConfigs`, replacing the boot-time `ListModules` +
    /// per-module `GetModuleConfig` fan-out (the config N+1). The server
    /// reads the same set in a single server-side fan-out and returns
    /// `Map.empty` for a no-scope caller (`Anonymous` mode) — the same
    /// empty-config result the per-module path produced. A total failure
    /// throws (routed to `BootLoadFailed`, whose handler applies the
    /// empty map AND flips the prefetch gate so `init` still finishes
    /// without blocking). The admin Config UI keeps using `ListModules`
    /// / `GetModuleConfig` for schema-bearing per-module views.
    let private loadAllConfigs = async { return! configApi.GetAllModuleConfigs() }

    /// Fetch the resolved feature-flag map for the caller. The prefetch
    /// is best-effort: on failure (routed to `BootLoadFailed`, Phase
    /// 121) modules that declare flags see `Map.empty` (every `flag`
    /// returns `false`) until a retry succeeds. An `Anonymous`
    /// deployment that declares no platform flags also sees an empty
    /// map, which is the correct answer — and is now distinguishable
    /// from a failed fetch.
    let private loadResolvedFlags = async { return! featureFlagApi.GetResolvedFlags() }

    /// Fetch the user's team memberships for the header switcher and
    /// any other caller that wants membership-aware UI. Fires on init
    /// in team-scoped modes and after `TeamSwitched` to pick up newly-
    /// created teams. Phase 121 — failure no longer silently degrades
    /// to `[]` (indistinguishable from a genuinely teamless user, the
    /// "my teams disappeared" ticket): it routes to `BootLoadFailed`,
    /// the model keeps the empty default, and the banner offers retry.
    let private loadMyTeams = async { return! teamApi.GetMyTeams() }

    /// Fetch the user's currently-selected active team id. Phase 121 —
    /// failure routes to `BootLoadFailed`; `ActiveTeamLoadCompleted`
    /// stays `false`, so the sole-team auto-select never fires off the
    /// back of a failed fetch.
    let private loadActiveTeam = async { return! teamApi.GetActiveTeam() }

    /// Sole-team auto-select: persist `teamId` as the caller's active
    /// team, then run the standard `TeamSwitched` reset so every
    /// boot loader re-fetches against the now-team-scoped subject.
    /// Failure (older server still gating `SetActiveTeam` on
    /// `teamScoped`, network glitch) leaves the user in the
    /// no-active-team state with the header switcher / Team Manager as
    /// the manual affordances — but Phase 121 surfaces the failure as
    /// a degradation (with retry) instead of swallowing it.
    let private autoSelectSoleTeamCmd (teamId: string) : Cmd<Msg> =
        Cmd.OfRemoting.call
            (fun id -> withCsrf (teamApi.SetActiveTeam id))
            teamId
            (function
            | Ok() -> TeamSwitched(Some teamId)
            | Error reason -> BootLoadFailed(BootSources.Key.TeamAutoSelect, reason))
            (fun ex -> BootLoadFailed(BootSources.Key.TeamAutoSelect, ex.Message))

    /// Fire the sole-team auto-select when (and only when) all three
    /// hold: the boot-time active-team fetch has resolved, it resolved
    /// to "no active team", and the user belongs to exactly one team.
    /// Covers the onboarding gap where a member is added to a team
    /// (admin add / invite / pending invite) without their active-team
    /// pointer being set — without this they sit in personal
    /// `user-{id}` scope with no data, no modules, and (single-team
    /// case) no switcher to escape through. Multi-team users with no
    /// active selection are NOT auto-selected — choosing among N teams
    /// is theirs to make via the header switcher, which renders in the
    /// no-active-team state for exactly that purpose.
    let private maybeAutoSelectSoleTeam (model: Model) : Cmd<Msg> =
        match model.ActiveTeamLoadCompleted, model.ActiveTeamId, model.MyTeams with
        | true, None, [ soleTeam ] -> autoSelectSoleTeamCmd soleTeam.TeamId
        | _ -> Cmd.none

    /// Phase 4b — fetch whether the caller holds
    /// `PlatformRole.PlatformAdmin`. Failure (CSRF/auth seam, network
    /// glitch, server doesn't expose the API yet) leaves the role
    /// `None`, which keeps the platform-scoped admin sidebar groups hidden —
    /// the safe default. Phase 121 — the failure routes to
    /// `BootLoadFailed` (this loader's warn was the precedent the
    /// degradation surface generalises): a failed role check is now
    /// banner-visible and retryable, not just a dev-tools warn that is
    /// otherwise identical to a definitive "not an admin". Fires once
    /// at shell init; the role doesn't change when switching teams
    /// (it's user-bound, not team-bound), so no refresh on
    /// `TeamSwitched`.
    let private loadPlatformRole = async { return! platformAdminApi.IsPlatformAdmin() }

    /// Phase 568 — resolve the caller's own `TeamRole` on `teamId` so
    /// the sidebar fold can evaluate `NavRole.TeamOwnerAdmin`.
    ///
    /// There is no "what is my role" endpoint and deliberately no new
    /// one: `GetTeamMembers` already returns the team's membership rows
    /// to any member (the handler's leak guard returns `[]` to a
    /// non-member, the same shape as "no such team"), so the caller's
    /// row is picked out locally against `UserSession.getUserId ()` —
    /// the identical derivation `TeamManagerUI` has always performed
    /// module-internally, now done once at the shell so every module's
    /// nav gate reads one answer. One round-trip per active team: at
    /// boot when `ActiveTeamLoaded` resolves, and again on
    /// `TeamSwitched`.
    ///
    /// A caller with no matching row (non-member, or an id the server
    /// records differently) yields `None`, which leaves the gate
    /// fail-open — the pre-568 shape. GP 12: the server's Owner/Admin
    /// guards are the enforcement; this only decides what renders.
    let private loadActiveTeamRole (teamId: string) = async {
        let! members = teamApi.GetTeamMembers teamId
        let selfId = UserSession.getUserId ()
        return members |> List.tryFind (fun m -> m.UserId = selfId) |> Option.map _.Role
    }

    // ─── Phase 121 — boot-degradation plumbing ───────────────────────

    /// Whether a failed load is the server refusing a publication reader: not a
    /// degraded load to retry, but an account this app does not serve.
    let internal isPublicationReaderRefusal (ex: exn) : bool =
        match ex with
        | :? ToolUp.Remoting.Client.ProxyRequestException as refused ->
            refused.StatusCode = 403
            && refused.ResponseText.Contains "publication_reader_not_admitted"
        | _ -> false

    /// Wrap a boot loader so a thrown load surfaces as `BootLoadFailed`
    /// (typed accumulator + banner) instead of being swallowed into a
    /// benign-looking default. GP 9 — a failed load must be
    /// distinguishable from empty data.
    let private bootLoadCmd (source: string) (loader: Async<'T>) (onLoaded: 'T -> Msg) : Cmd<Msg> =
        Cmd.OfAsync.either (fun () -> loader) () onLoaded (fun ex ->
            if isPublicationReaderRefusal ex then
                PublicationReaderRefused
            else
                BootLoadFailed(source, ex.Message))

    /// Phase 568 — re-read the caller's role on the (new) active team,
    /// or clear it when there is no active team to have a role on. The
    /// single place the role is refreshed, called from both arms that
    /// can move the active team: `ActiveTeamLoaded` (boot) and
    /// `TeamSwitched` (switch / revoke). Clearing goes through the same
    /// message rather than an inline record update so the arm keeps one
    /// definition of what the field means.
    let private activeTeamRoleCmd (teamId: string option) : Cmd<Msg> =
        match teamId with
        | Some id -> bootLoadCmd BootSources.Key.TeamRole (withCsrf (loadActiveTeamRole id)) ActiveTeamRoleLoaded
        | None -> Cmd.ofMsg (ActiveTeamRoleLoaded None)

    /// The loaders the boot-source table's rows pick from.
    let private bootLoads: BootSources.BootLoads<Model, Msg> = {
        Permissions = bootLoadCmd BootSources.Key.Permissions (withCsrf loadAccessibleModules) AccessibleModulesLoaded
        Configs = bootLoadCmd BootSources.Key.Configs (withCsrf loadAllConfigs) ConfigsLoaded
        Flags = bootLoadCmd BootSources.Key.Flags (withCsrf loadResolvedFlags) FlagsLoaded
        Teams = bootLoadCmd BootSources.Key.Teams (withCsrf loadMyTeams) MyTeamsLoaded
        ActiveTeam = bootLoadCmd BootSources.Key.ActiveTeam (withCsrf loadActiveTeam) ActiveTeamLoaded
        PlatformRole = bootLoadCmd BootSources.Key.PlatformRole loadPlatformRole PlatformRoleLoaded
        PlatformInfo = bootLoadCmd BootSources.Key.PlatformInfo loadPlatformInfo PlatformInfoLoaded
        DataSnapshot = bootLoadCmd BootSources.Key.DataSnapshot loadFileSnapshot DataSnapshotPrefetched
        TeamRole = fun model -> activeTeamRoleCmd model.ActiveTeamId
        TeamAutoSelect = maybeAutoSelectSoleTeam
    }

    /// The shell's boot sources: `BootSources.all` at the shell's types.
    let internal bootSources: BootSources.BootSource<Model, Msg> list =
        BootSources.all ()

    /// Issue the loads of `sources` against `model`.
    let private issueLoads (model: Model) (sources: BootSources.BootSource<Model, Msg> list) : Cmd<Msg> =
        sources |> List.map (fun source -> source.Load bootLoads model) |> Cmd.batch

    /// The source keys the active module's first `Init` waits for.
    let firstInitGates: Set<string> = BootSources.firstInitGates bootSources

    /// Phase 444 — resolve the active locale and the message catalog the
    /// shell renders from. One definition, called from `view` (chrome +
    /// the provider mount), from `update` (the handful of arms that
    /// author user-visible text outside a render, e.g. the cross-module
    /// results toast) and from `buildContext` (the catalog a
    /// module's `init` and `update` read as `ClientModuleContext.Messages`),
    /// so the three cannot answer differently.
    ///
    /// Precedence: an in-session choice (`Model.LocaleOverride`, set by
    /// `LocaleSwitched`) first, then the declared `ClientConfig.Locale`
    /// resolved against the team's `_platform.locale` and the browser
    /// preference. Under the default `FixedLocale "en"` with no override
    /// this is a `match` returning a constant plus a `None` branch, and no
    /// map read or browser call.
    ///
    /// The SAME catalog object comes back while the locale and the
    /// override are unchanged (a one-entry memo, the locale compared by
    /// value and the override by reference). `MessageCatalog.resolve`
    /// stamps the locale onto a copy of the built-in catalog, so without
    /// the memo every render handed `MessageCatalogProvider` a new value
    /// and re-rendered every `useMessages` reader — including readers
    /// inside a module subtree the sliced shell's memo boundary would
    /// otherwise have held.
    let private catalogFor =
        Memo.oneEntryBy
            (fun (locale1: string, override1) (locale2, override2) ->
                locale1 = locale2 && obj.ReferenceEquals(override1, override2))
            (fun (locale, catalogOverride) -> MessageCatalog.resolve locale catalogOverride)

    let internal resolveCatalog
        (config: ClientConfig)
        (localeOverride: string option)
        (platformConfig: Map<string, string>)
        : MessageCatalog =
        let locale =
            match localeOverride with
            | Some chosen -> chosen
            | None ->
                match config.Locale.Resolution with
                | FixedLocale _ ->
                    // The fixed arm consults neither ambient source, so
                    // resolve it without paying for either.
                    MessageCatalog.resolveLocale config.Locale.Resolution None None
                | BrowserLocale _ ->
                    MessageCatalog.resolveLocale config.Locale.Resolution None (MessageCatalogProvider.browserLocale ())
                | TeamDefault _ ->
                    MessageCatalog.resolveLocale
                        config.Locale.Resolution
                        (MessageCatalog.teamLocaleOf platformConfig)
                        (MessageCatalogProvider.browserLocale ())

        catalogFor (locale, config.Locale.MessageCatalogOverride)

    let private degradationEntry (source: string) (error: string) : BootDegradation.BootDegradation = {
        Source = source
        Label = BootDegradation.localisedLabel MessageCatalog.english.BootDegradation.Sources source source
        Error = error
        Retryable = BootSources.retryable source bootSources
        OccurredAt = System.DateTime.UtcNow
    }

    /// Persist the sidebar preferences as an effect. A throwing store (quota,
    /// private window, blocked site data) surfaces as the sidebar-preference
    /// banner row rather than an exception out of `update`.
    let private persistSidebarPrefs (prefs: SidebarPreferences.UserSidebarPreferences) : Cmd<Msg> =
        SidebarPreferences.persist (fun ex -> BootLoadFailed(BootSources.Key.SidebarPrefs, ex.Message)) prefs

    /// The captured shell dispatcher as the shell reads it (see
    /// `shellDispatcher`).
    type private ShellDispatch = {
        SendToShell: Msg -> unit
        ShellIsActive: unit -> bool
    }

    /// Module-level capture of the shell's Elmish `dispatch` function,
    /// set once at program start (see the capture paragraph below). Lets
    /// `ClientModuleContext.OnTeamSwitched`
    /// dispatch a shell-level `TeamSwitched` from within a module's
    /// update handler — the module's own `dispatch` is typed to its own
    /// `Msg`, so it can't otherwise reach the shell.
    ///
    /// Sanctioned mutable: initialisation-time set, read after. Mirrors
    /// `UserSession.session` and `NotificationClient.state`
    /// — documented in the platform README's "No new side effects"
    /// exceptions list.
    ///
    /// Read off the program's `IDispatcher` (ToolUp.Elmish primitive —
    /// replaces the legacy `(Msg -> unit) option` shape), captured at
    /// program-start via `Program.withDispatcherHandle` by
    /// `withShellLifetimeEffects`, for the shell and every composer over it
    /// (Phase 931). A composer's loop takes its own message type, so what is
    /// held is that dispatcher's `Dispatch` lifted through the composer's
    /// shell-message constructor, beside its `IsActive` — which flips to
    /// `false` when `withTermination` triggers, so background callbacks
    /// check before dispatching and no-op cleanly on hot-reload / teardown
    /// rather than spraying messages at a dead loop.
    let mutable private shellDispatcher: ShellDispatch option = None

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
                model.Hosts
                |> Map.tryFind model.ActiveModuleId
                |> Option.bind (fun host -> extract host.State model.ActivePageRoute)))

    /// The named module's state, when it has been initialised.
    let moduleState (moduleId: string) (model: Model) : obj option =
        model.Hosts |> Map.tryFind moduleId |> Option.map _.State

    /// A command the named module's `Init` or `Update` returned, its messages
    /// addressed back to that module.
    let private moduleCmd (moduleId: string) (cmd: Cmd<obj>) : Cmd<Msg> =
        Cmd.map (fun message -> ModuleMsg(moduleId, message)) cmd

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
        | Some _ -> moduleImpl.Definition.Pages |> List.tryHead |> Option.map _.Route
        | None -> None

    /// Phase 1240 — the page a first mount or a re-init lands on. Those paths
    /// reset to the first page, which under browser history would discard the
    /// page the URL just restored, so the current page stands while the module
    /// has not changed. Everything else resets as it always has.
    let private settledPageRoute (config: ClientConfig) (model: Model) (moduleImpl: ErasedModule) =
        match config.Shell.BrowserHistory, model.ActivePageRoute with
        | HashHistory, Some route when model.ActiveModuleId = moduleImpl.Definition.Id -> Some route
        | _ -> defaultPageRoute moduleImpl

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
                    if d.ShellIsActive() then
                        d.SendToShell(TeamSwitched(Some teamId))))
        else
            None

    /// Phase 245 — the `OnAccessibleModulesChanged` callback handed to
    /// modules. Only meaningful where exposure applies (a `Team`-shaped
    /// surface), so it mirrors `buildOnTeamSwitched`'s gate; `None`
    /// otherwise. A successful `PermissionApi.SetModuleExposure`
    /// invokes it to re-fetch the shell's accessible-modules list.
    let private buildOnAccessibleModulesChanged (config: ClientConfig) : (unit -> unit) option =
        if ClientConfig.hasTeamScope config then
            Some(fun () ->
                shellDispatcher
                |> Option.iter (fun d ->
                    if d.ShellIsActive() then
                        d.SendToShell RefreshAccessibleModules))
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
            UserId = UserId(UserSession.getUserId ())
            TeamId = model.ActiveTeamId |> Option.map TeamId
            QueryBus = queryBus
            OnTeamSwitched = buildOnTeamSwitched config
            OnAccessibleModulesChanged = buildOnAccessibleModulesChanged config
            // The catalog the provider is handed for the same
            // model. A `LocaleSwitched` reset sets `LocaleOverride` and
            // clears every module state, so each module is re-initialised
            // through here with the new language's catalog.
            Messages = resolveCatalog config model.LocaleOverride model.PlatformConfig
        }

    // ─── Phase 569 — the route guard's shared inputs ──────────────────
    //
    // The sidebar's four filters and the router's admission question are
    // ONE decision (`SidebarVisibility.decide`); these two helpers are
    // the shell's single projection into it, so the two call sites —
    // `view`'s `sidebarSections` pipeline and the guard below — cannot
    // be handed different inputs. That is the whole point of 569.A: a
    // module hidden from the rail is unreachable by URL by construction,
    // not by two filters someone keeps in agreement by hand.

    /// `ErasedModule` → the four facts the navigation decision reads.
    /// The projection `SidebarVisibility` is generic over (it is declared
    /// ahead of `ErasedModule`, so it cannot name the type).
    let private moduleFacts (m: ErasedModule) : SidebarVisibility.SidebarModuleFacts = {
        Id = m.Definition.Id
        Group = m.Group
        NavRole = m.NavRole
        Visibility = m.Visibility
    }

    /// Phase 571 — `ErasedModule` → the facts the command palette reads.
    /// Carries the navigation facts above VERBATIM, so the palette's
    /// candidate derivation runs the same fold on the same values the
    /// rail and the route guard do; the rest is display data the pure
    /// module cannot name (`ReactElement` icons are re-attached at
    /// render, where they can actually be built).
    let private paletteFacts (m: ErasedModule) : CommandPaletteNav.PaletteModuleFacts = {
        Nav = moduleFacts m
        Name = m.Definition.Name
        Pages =
            m.Definition.Pages
            |> List.map (fun page -> {
                CommandPaletteNav.PalettePageFacts.Route = page.Route
                Title = page.Title
            })
        HasPageViews = m.PageViews.IsSome
    }

    /// The shell model + config projected into the decision's inputs. The
    /// three `ClientConfig` reads live here because `ClientConfig` is
    /// Fable-only at runtime; passing their resolved values keeps the
    /// decision itself pure data (and .NET-testable).
    let private navigationInputs (config: ClientConfig) (model: Model) : SidebarVisibility.SidebarVisibilityInputs = {
        Accessibility = model.AccessibleModules
        PlatformRole = model.PlatformRole
        ActiveTeamRole = model.ActiveTeamRole
        ShowAllModules = model.ShowAllModules
        SubjectKind = ClientConfig.resolveSubjectKind model.ActiveTeamId config
        HasTeamScope = ClientConfig.hasTeamScope config
        ActiveTeamId = model.ActiveTeamId
        NoActiveTeamLandingId = ClientConfig.effectiveNoActiveTeamLandingId config
        // Phase 637 — the server-resolved visibility profile rides the
        // accessible-modules response, so it needs no model field of its
        // own and no second prefetch: it arrives, refreshes and clears on
        // exactly the events `AccessibleModules` already does.
        VisibilityProfile = model.AccessibleModules |> Option.bind _.VisibilityProfile
    }

    /// Phase 569.B — may this caller reach this module, with the reason
    /// when not. Consulted before any module `Init` and again at render.
    let private navigationDecision
        (config: ClientConfig)
        (model: Model)
        (m: ErasedModule)
        : SidebarVisibility.NavigationDecision =
        SidebarVisibility.decide moduleFacts (navigationInputs config model) m

    /// The predicate form, for the init paths that only need yes/no.
    let private canNavigateToModule (config: ClientConfig) (model: Model) (m: ErasedModule) : bool =
        SidebarVisibility.canNavigateTo moduleFacts (navigationInputs config model) m

    /// The deployment's default landing surface id — the module the
    /// shell lands on when nothing else is selected:
    /// `ClientConfig.Shell.ActiveModule` when set, else the first registered
    /// module (Home when the Home module is enabled, since it's injected
    /// at the head). Empty string only for the degenerate no-modules case.
    let private bootDefaultModuleId (config: ClientConfig) (modules: ErasedModule list) : string =
        match config.Shell.ActiveModule with
        | Some id -> id
        | None ->
            modules
            |> List.tryHead
            |> Option.map (fun m -> m.Definition.Id)
            |> Option.defaultValue ""

    /// Phase 569 — where the denial view's "Go to home" action lands.
    /// The deployment default, EXCEPT while the no-active-team collapse
    /// owns the surface: sending a team-less caller to a Home they also
    /// cannot reach turns one denial into two, so the landing module is
    /// the route home for that window.
    let private routeHomeModuleId (config: ClientConfig) (modules: ErasedModule list) (model: Model) : string =
        match ClientConfig.effectiveNoActiveTeamLandingId config with
        | Some landingId when ClientConfig.hasTeamScope config && model.ActiveTeamId.IsNone -> landingId
        | _ -> bootDefaultModuleId config modules

    /// Resolve the active *content* surface accounting for the
    /// no-active-team gate (`ClientConfig.Chrome.NoActiveTeamLanding` (`NoActiveTeamLandingFill.Module`)).
    /// The sidebar filter hides the work modules while a gated, team-
    /// scoped deployment has no active team, but the active module (the
    /// content area) is tracked separately — without this a no-team user
    /// lands on the sidebar-hidden Home / first module instead of the
    /// landing. Applied at boot promotion, team-state changes, and
    /// prefetch refreshes (NOT on explicit sidebar navigation, which the
    /// `SelectModule` arm honours directly):
    ///   * Active team present: if the user is still parked on the landing
    ///     (e.g. a team was just assigned), fall through to the default
    ///     surface; otherwise keep the selection.
    ///   * No active team: keep the selection only if it's still reachable
    ///     under the gate — the landing itself, or (for a platform admin)
    ///     a module in an admin / management group. Anything else (the
    ///     default Home, a hidden work module) resolves to the landing.
    /// Returns `model.ActiveModuleId` unchanged for every non-gated
    /// deployment.
    let private resolveActiveModuleId (config: ClientConfig) (modules: ErasedModule list) (model: Model) : string =
        match ClientConfig.effectiveNoActiveTeamLandingId config with
        | Some landingId when ClientConfig.hasTeamScope config ->
            if model.ActiveTeamId.IsSome then
                // Gate inactive — only rescue a user stranded on the landing
                // now that they have a team.
                if model.ActiveModuleId = landingId then
                    bootDefaultModuleId config modules
                else
                    model.ActiveModuleId
            elif config.Shell.BrowserHistory = HashHistory && not model.ActiveTeamLoadCompleted then
                // Phase 1240 — "no team" is not known yet, only "no team
                // loaded". Forcing the landing here would lose a view the URL
                // restored, since a team arriving moves a caller on the landing
                // to the default surface. The route guard holds the selection
                // back meanwhile; `ActiveTeamLoaded` re-runs this with the answer.
                model.ActiveModuleId
            else
                // Gate active — the selection survives only if the gate's own
                // stage still admits it (the landing, or an admin/management
                // module for an admin); otherwise force the landing.
                let selectionStillReachable =
                    match tryFind modules model.ActiveModuleId with
                    | Some m -> SidebarVisibility.noActiveTeamAdmits (navigationInputs config model) (moduleFacts m)
                    | None -> model.ActiveModuleId = landingId

                if selectionStillReachable then
                    model.ActiveModuleId
                else
                    landingId
        | _ -> model.ActiveModuleId

    // ─── The module host ──────────────────────────────────────────────
    //
    // One function answers "may this module init now, and where does its
    // command go": `initModule`. Every path that initialises a module — boot,
    // the first-init gate, a re-init, a guard input arriving, navigation, an
    // inbound action or event, a reset — calls it, so none can differ in its
    // guard, its deferral or its routing.

    /// Report a module whose `Init` threw, as a command.
    let private reportInitFailure (config: ClientConfig) (moduleId: string) (ex: exn) : Cmd<Msg> =
        Cmd.ofEffect (fun _ ->
            match config.Composition.OnError with
            | Some report ->
                report {
                    ModuleId = moduleId
                    Error = ex
                    ComponentStack = ex.StackTrace |> Option.ofObj |> Option.defaultValue ""
                }
            | None -> log.Error(sprintf "[ModuleBoundary] %s Init crashed: %s" moduleId ex.Message, Some ex))

    /// Initialise `moduleId`: the one call site of a module's `Init`.
    ///
    /// Deferred while the shell is `Prefetching` (the first init waits for
    /// the `GatesFirstInit` boot sources). Refused when the navigation
    /// decision refuses the module, so none of its boot calls go out to come
    /// back as 403s; a guard input still loading reads as refused, and the
    /// arm that receives it initialises the module then. Its command is
    /// addressed to it. An `Init` that throws leaves the module with no host
    /// and is reported through `ClientConfig.Composition.OnError`.
    let private initModule
        (config: ClientConfig)
        (queryBus: IModuleQueryBus)
        (modules: ErasedModule list)
        (moduleId: string)
        (model: Model)
        : Model * Cmd<Msg> =
        match tryFind modules moduleId with
        | Some moduleImpl when model.InitPhase <> Prefetching && canNavigateToModule config model moduleImpl ->
            try
                let context = buildContext config queryBus model moduleId
                let state, cmd = moduleImpl.Init context

                {
                    model with
                        Hosts = model.Hosts |> Map.add moduleId { State = state; Generation = 0 }
                },
                moduleCmd moduleId cmd
            with ex ->
                {
                    model with
                        Hosts = model.Hosts |> Map.remove moduleId
                },
                reportInitFailure config moduleId ex
        | _ -> model, Cmd.none

    /// Initialise `moduleId` unless it already has a host.
    let private ensureHost config queryBus modules (moduleId: string) (model: Model) : Model * Cmd<Msg> =
        if model.Hosts |> Map.containsKey moduleId then
            model, Cmd.none
        else
            initModule config queryBus modules moduleId model

    /// Run one message through a hosted module's `Update`; its command comes
    /// back to it. A module with no host (a reset or a team switch cleared
    /// it) has nothing to land on, and the message is dropped.
    let private applyToHost (modules: ErasedModule list) (moduleId: string) (message: obj) (model: Model) =
        match tryFind modules moduleId, model.Hosts |> Map.tryFind moduleId with
        | Some moduleImpl, Some host ->
            let state, cmd = moduleImpl.Update message host.State

            {
                model with
                    Hosts = model.Hosts |> Map.add moduleId { host with State = state }
            },
            moduleCmd moduleId cmd
        | _ -> model, Cmd.none

    /// Deliver a message the module's own view did not send — a decoded
    /// action or a subscribed event — initialising the module first if it
    /// has no host, active or not. `None` when the navigation decision
    /// refuses the module: an inbound message is admitted by that decision
    /// and by nothing else.
    let private stepModule
        (config: ClientConfig)
        (queryBus: IModuleQueryBus)
        (modules: ErasedModule list)
        (moduleImpl: ErasedModule)
        (message: obj)
        (model: Model)
        : (Model * Cmd<Msg>) option =
        if canNavigateToModule config model moduleImpl then
            let moduleId = moduleImpl.Definition.Id
            let hosted, initCmd = ensureHost config queryBus modules moduleId model
            let stepped, updateCmd = applyToHost modules moduleId message hosted
            Some(stepped, Cmd.batch [ initCmd; updateCmd ])
        else
            None

    /// Move the active surface through the no-active-team gate and settle its
    /// page.
    let private settleActive (config: ClientConfig) (modules: ErasedModule list) (model: Model) : Model =
        let activeId = resolveActiveModuleId config modules model

        match tryFind modules activeId with
        | Some moduleImpl -> {
            model with
                ActiveModuleId = activeId
                ActivePageRoute = settledPageRoute config model moduleImpl
          }
        | None -> { model with ActiveModuleId = activeId }

    /// The first-init gates have all answered. On a first load, or after a
    /// reset, the active module is initialised now. After a refresh it is
    /// re-initialised only when what it was built from changed (the configs,
    /// the flags, or the surface the no-team gate settles on), compared with
    /// `prior`, the model before the answer: a refresh that brings the same
    /// values keeps the module and its React subtree.
    let private onFirstInitReady config queryBus modules (prior: Model) (model: Model) : Model * Cmd<Msg> =
        let settled = settleActive config modules { model with InitPhase = Ready }

        let unchanged =
            model.InitPhase <> Prefetching
            && settled.ActiveModuleId = model.ActiveModuleId
            && model.Hosts |> Map.containsKey model.ActiveModuleId
            && prior.ModuleConfigs = model.ModuleConfigs
            && prior.PlatformConfig = model.PlatformConfig
            && prior.ResolvedFlags = model.ResolvedFlags

        if unchanged then
            { model with InitPhase = Ready }, Cmd.none
        else
            initModule config queryBus modules settled.ActiveModuleId { settled with Hosts = Map.empty }

    /// A first-init boot source answered, or failed, which also counts. When
    /// it was the last such source outstanding, the active module is
    /// initialised (or re-initialised, see `onFirstInitReady`).
    let private onSourceLoaded config queryBus modules (key: string) (prior: Model) (model: Model) =
        let awaiting = model.AwaitingFirstInit |> Set.remove key

        let updated = {
            model with
                AwaitingFirstInit = awaiting
        }

        if awaiting.IsEmpty then
            onFirstInitReady config queryBus modules prior updated
        else
            updated, Cmd.none

    /// Aggregate processed data from every module that exposes it.
    /// Pure — callers store the result in `Model.ProcessedData` and the
    /// shell's `view` then publishes it through `ProcessedDataContext`.
    let private computeProcessedData (modules: ErasedModule list) (hosts: Map<string, ModuleHost>) =
        modules
        |> List.collect (fun m ->
            match m.ProvidesProcessedData with
            | Some getter ->
                match hosts |> Map.tryFind m.Definition.Id with
                | Some host -> getter host.State
                | None -> []
            | None -> [])

    /// The id of the module filling the data-manager slot, if any — the
    /// SDK's or a deployment's own. Used to decide when the boot-prefetched
    /// snapshot is still the source (the data manager not yet mounted) vs
    /// its live module state. Read from the slot without building the
    /// module: this runs on every message.
    let private dataManagerModuleId (config: ClientConfig) : string option =
        ShellSlots.composedModuleId ShellSlots.dataManager config

    /// Phase 1046 — whether the shell prefetches the file snapshot (and
    /// seeds the session epoch) at boot: the data-manager slot is filled
    /// AND the server advertises `FileManagementApi` as mounted. The slot
    /// alone is not enough — a deployment can fill it with a module that
    /// does not use forge's file API while declaring no data type, and the
    /// server then mounts no file API to call.
    let internal loadsDataSnapshotOnBoot (dataManagerModuleId: string option) (info: PlatformInfo) : bool =
        dataManagerModuleId.IsSome && info.FileManagementMounted

    /// Module-derived processed data, plus the boot-prefetched snapshot while
    /// the DataManager module hasn't been mounted yet — so data modules see
    /// uploaded data on load without first visiting the Data Manager page.
    /// Once the DataManager has a host, its live state (which the
    /// `computeProcessedData` aggregation already includes) is authoritative
    /// and the prefetched fallback is dropped to avoid stale entries.
    let private resolveProcessedData
        (config: ClientConfig)
        (modules: ErasedModule list)
        (hosts: Map<string, ModuleHost>)
        (prefetched: ProcessedFileEntry list)
        : ProcessedFileEntry list =
        let derived = computeProcessedData modules hosts

        match dataManagerModuleId config with
        | Some dmId when hosts |> Map.containsKey dmId -> derived
        | _ -> derived @ prefetched

    /// Whether the deployment wants a sign-in and this reader has not done one.
    ///
    /// A dev composition root that sets `DevDefaultUserId` ships a stable
    /// header identity (X-User-Id) that the boot loaders attach via
    /// `UserSession.withRequestHeaders`, so they succeed against a
    /// header-auth-accepting server even with no JWT. That identity counts like
    /// a token: without it a no-JWT dev build skips the team / role / config
    /// loaders entirely, so the shell never learns the caller's active team —
    /// and the no-team gate (`NoActiveTeamLandingFill.Module`) would then
    /// mis-classify every dev user as teamless. Production (real OIDC) always
    /// has a token, so this only changes the no-JWT dev path.
    let private isSignedOut (config: ClientConfig) : bool =
        ClientConfig.requiresAnyAuth config
        && UserSession.getAuthToken () |> Option.isNone
        && config.Auth.DevDefaultUserId |> Option.isNone

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

        let signedOut = isSignedOut _config
        let signedIn = ClientConfig.requiresAnyAuth _config && not signedOut

        // Phase 1240 — the view the URL names, if the deployment gave views
        // URLs. It only chooses the initial selection: the deny-before-`Init`
        // decisions below read this module exactly as they read a default one.
        // Phase 1241 — signed in with no view in the URL, the view kept across
        // sign-in stands in for it, under those same decisions.
        let restoredView =
            match _config.Shell.BrowserHistory with
            | NoBrowserHistory -> None
            | HashHistory ->
                let entries = RegisteredModules.snapshot ()

                BrowserHistory.current entries
                |> Option.orElseWith (fun () -> if signedIn then BrowserHistory.stored entries else None)
                |> Option.map parseSidebarId

        let moduleImpl =
            // The no-team gate lands a freshly-signed-in user (no active
            // team yet) on the landing module rather than the default
            // Home / first module. `ActiveTeamId` is None at boot; the
            // active-team fetch reconciles the surface via `ActiveTeamLoaded`
            // once it resolves (team → default; still no team → landing).
            let initialId =
                match restoredView, ClientConfig.ownNoActiveTeamLandingModuleId _config with
                | Some(id, _), _ -> id
                | None, Some landingId when ClientConfig.hasTeamScope _config -> landingId
                | None, _ ->
                    match _config.Shell.ActiveModule with
                    | Some id -> id
                    | None -> modules[0].Definition.Id

            tryFind modules initialId |> Option.defaultValue modules[0]

        // Signed in, the first `Init` waits for the first-init boot sources,
        // so the module is built from the persisted configs and flags rather
        // than built from an empty seed and rebuilt. Signed out, or with no
        // sign-in wanted, the loaders either do not run yet or bring what the
        // empty seed already holds, so the module mounts now — through the
        // same guard as every other init: a default surface whose declared
        // `Visibility` the boot subject fails is not initialised.
        let unhosted = {
            ActiveModuleId = moduleImpl.Definition.Id
            ActivePageRoute =
                match restoredView with
                | Some(_, Some route) -> Some route
                | _ -> defaultPageRoute moduleImpl
            Hosts = Map.empty
            AccessibleModules = None
            ShowAllModules = false
            ModuleConfigs = Map.empty
            PlatformConfig = Map.empty
            ResolvedFlags = Map.empty
            SidebarPrefs = SidebarPreferences.load ()
            ProcessedData = []
            MyTeams = []
            ActiveTeamId = None
            ActiveTeamLoadCompleted = false
            PlatformRole = None
            ActiveTeamRole = None
            CurrentArea =
                match restoredView with
                | Some _ -> ClientConfig.effectiveArea moduleImpl.Area moduleImpl.Group
                | None -> ModuleArea.Product
            AwaitingFirstInit = firstInitGates
            InitPhase = if signedIn then Prefetching else Ready
            Degradations = []
            AppAccessRefused = false
            PrefetchedProcessedData = []
            CommandPalette = CommandPaletteNav.closed
            LocaleOverride = None
        }

        let hosted, moduleInitCmd =
            initModule _config queryBus modules moduleImpl.Definition.Id unhosted

        let model = {
            hosted with
                ProcessedData = resolveProcessedData _config modules hosted.Hosts []
        }

        let bootLoadCommands =
            issueLoads model (BootSources.issuedAtBoot (ClientConfig.hasTeamScope _config) bootSources)

        // Shell dispatcher capture lives at the `Program` site via
        // `Program.withDispatcherHandle` (ToolUp.Elmish primitive), inside
        // `withShellLifetimeEffects` below so every composer gets it
        // (Phase 931). The previous `Cmd.ofEffect`-capture pattern
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

        let initCmds =
            if signedOut then
                // Welcome-Page case: the user hasn't signed in yet and
                // every auth-required fetch would 401. Drop them
                // entirely; the `auth-token-acquired` lifetime effect
                // dispatches `AuthTokenAcquired` once `setAuthToken`
                // runs and we re-fire `bootLoadCommands` then.
                [ moduleInitCmd ]
            else
                [ moduleInitCmd; bootLoadCommands ]

        // Phase 1240 — the first entry names its view too, or Back to it would
        // land on a URL with nothing to restore.
        //
        // Phase 1241 — a signed-in boot has used the kept view or been overruled
        // by the URL, so it goes either way. A signed-out boot keeps the view its
        // URL named, and only that: an issuer's callback boots the shell signed
        // out with no fragment, on its way to reloading `/`, and the default view
        // it shows meanwhile must not overwrite the view being carried.
        let firstEntryCmd =
            match _config.Shell.BrowserHistory with
            | NoBrowserHistory -> []
            | HashHistory ->
                let view = BrowserHistory.viewId model.ActiveModuleId model.ActivePageRoute

                [
                    Cmd.ofEffect (fun _ ->
                        BrowserHistory.replace view

                        if signedIn then
                            BrowserHistory.forget ()
                        elif signedOut && restoredView.IsSome then
                            BrowserHistory.store view)
                ]

        model, Cmd.batch (initCmds @ firstEntryCmd)

    /// The shell's one reset. A team switch and a locale switch wipe the
    /// per-scope state (the hosts, the loaded configs, flags and access
    /// response, the degradations), return to `Prefetching` and re-issue the
    /// boot sources that reset clears, so the active module is re-initialised
    /// against the new scope once the first-init sources answer. A token
    /// arriving after a signed-out boot re-issues its sources without the
    /// wipe: the view stays on screen, stale, and is re-initialised only if
    /// what it was built from changed.
    ///
    /// A locale switch to the locale already chosen, and a token arriving
    /// while the boot loads are still in flight, change nothing.
    let private reset (config: ClientConfig) (kind: BootSources.ResetKind) (model: Model) : Model * Cmd<Msg> =
        let rescoped =
            match kind with
            | BootSources.ResetKind.TeamSwitch teamId ->
                // The caller's role belongs to the team, so it is stale the
                // moment the team moves. `None` fails the role gate open
                // until it is re-read, as at boot.
                Some {
                    model with
                        ActiveTeamId = teamId
                        ActiveTeamRole = None
                }
            | BootSources.ResetKind.LocaleSwitch locale when locale <> model.LocaleOverride ->
                Some { model with LocaleOverride = locale }
            | BootSources.ResetKind.TokenAcquired when model.InitPhase <> Prefetching -> Some model
            | BootSources.ResetKind.LocaleSwitch _
            | BootSources.ResetKind.TokenAcquired -> None

        match rescoped with
        | None -> model, Cmd.none
        | Some rescoped ->
            let next =
                match kind with
                | BootSources.ResetKind.TokenAcquired -> {
                    rescoped with
                        InitPhase = Reprefetching
                        AwaitingFirstInit = firstInitGates
                  }
                | BootSources.ResetKind.TeamSwitch _
                | BootSources.ResetKind.LocaleSwitch _ -> {
                    rescoped with
                        ModuleConfigs = Map.empty
                        PlatformConfig = Map.empty
                        ResolvedFlags = Map.empty
                        AccessibleModules = None
                        Hosts = Map.empty
                        AwaitingFirstInit = firstInitGates
                        InitPhase = Prefetching
                        Degradations = []
                        AppAccessRefused = false
                  }

            let reissued =
                bootSources
                |> BootSources.reissuedBy (BootSources.ResetKind.trigger kind) (ClientConfig.hasTeamScope config)
                |> issueLoads next

            match kind with
            | BootSources.ResetKind.TeamSwitch _ ->
                next,
                Cmd.batch [
                    // The server scopes a read to the active team and a
                    // cached read's key does not carry it, so the move is an
                    // identity change: drop every cached read BEFORE the
                    // re-issued loads go out.
                    Cmd.ofEffect (fun _ -> UserSession.identityChanged ())
                    reissued
                    // The session file store on the other side is now a
                    // different one: re-seed the epoch against it, or the next
                    // focus check reports a reset that never happened.
                    Cmd.ofEffect (fun _ ->
                        SessionEpoch.reset ()
                        Async.StartImmediate(SessionEpoch.prime ()))
                ]
            | BootSources.ResetKind.LocaleSwitch _
            | BootSources.ResetKind.TokenAcquired -> next, reissued

    let private updateShell (_config: ClientConfig) (queryBus: IModuleQueryBus) (modules: ErasedModule list) msg model =
        let newModel, cmd =
            match msg with
            | ModuleMsg(moduleId, message) -> applyToHost modules moduleId message model

            | SessionStoreReset notice ->
                // Drain the shell's half of the file-derived state. The
                // Data Manager surfaces clear their own lists through
                // their `SessionEpoch` subscriptions — one event, each
                // holder responsible for its own state, rather than the
                // shell reaching into modules it does not own.
                //
                // The toast goes through `NotificationClient.publishLocal`
                // so it lands in whatever `ToastCentre` the deployment
                // composed, exactly like the team-switch failures above:
                // no second notification surface for one more event.
                // The toast names the cause rather than only the effect:
                // "it was idle too long" and "the server restarted" call
                // for different user responses, and the payload already
                // carries which one it was.
                let cause =
                    if notice.Reason = SessionStoreResetReasonEvicted then
                        "after a period of inactivity"
                    else
                        "by a server restart"

                // Phase 1069 — published from a `Cmd`, not inline: the
                // envelope's id and timestamp are minted when the effect
                // runs, so `update` itself performs no I/O.
                let text = $"Your server session was reset {cause}; please re-upload your files."

                {
                    model with
                        PrefetchedProcessedData = []
                },
                Cmd.ofEffect (fun _ ->
                    NotificationClient.publishLocal (
                        NotificationEnvelope.create "" (Notification.SystemMessage(SystemMessageLevel.Warning, text))
                    ))

            | DataSnapshotPrefetched snapshot ->
                // Boot-time data prefetch: store the snapshot's processed
                // entries. The post-update recompute below merges them into
                // `ProcessedData` until the DataManager module is mounted.
                {
                    model with
                        PrefetchedProcessedData = snapshot.Processed
                },
                Cmd.none

            | PlatformInfoLoaded info ->
                // Phase 1046 — arm (or disarm) the session-epoch reads on
                // the server's own mount decision, then issue the file-API
                // boot loads only where the API exists.
                SessionEpoch.setFileApiMounted info.FileManagementMounted

                let fileLoads =
                    if loadsDataSnapshotOnBoot (dataManagerModuleId _config) info then
                        [
                            // When the data-manager slot is filled — by the
                            // SDK's module or a deployment's own — prefetch its
                            // file snapshot at boot so data modules see uploaded
                            // data immediately, without the user first opening
                            // the data manager (best-effort — a failed fetch
                            // routes to `BootLoadFailed` and
                            // `PrefetchedProcessedData` stays empty).
                            bootLoads.DataSnapshot
                            // Phase 6p — seed the session epoch alongside the
                            // snapshot it describes. A first read can never
                            // report a reset (there is no prior epoch to differ
                            // from), so this only establishes the baseline the
                            // later checks compare against. Failure is swallowed
                            // inside `SessionEpoch.check`.
                            Cmd.ofEffect (fun _ -> Async.StartImmediate(SessionEpoch.prime ()))
                        ]
                    else
                        []

                model, Cmd.batch fileLoads

            // Phase 567 — the two-surface area switcher fires through the
            // same onSelect as modules; its reserved ids flip the rendered
            // navigation area rather than navigating. Inert under the default
            // Chrome.AdminArea = ChromeFill.Off (no switcher is ever rendered).
            | ModuleSelected sidebarId when sidebarId = Toolup.Sidebar.AdminAreaId ->
                // Phase 573.A — the flip LANDS somewhere. Before this it
                // only re-pointed the rail, leaving the content pane on
                // whichever module was already open (a product module, as
                // often as not). The landing module is registered only
                // under `ShellChrome.AdminArea = ChromeFill.On()`, so this is a
                // guarded follow-up rather than an assumption: with no
                // landing registered the flip behaves exactly as it did
                // pre-573.
                //
                // Re-entering the generic `ModuleSelected` path (rather
                // than open-coding the selection here) is deliberate —
                // the route guard, the prefetch deferral and the
                // area derivation all live there, and a second
                // implementation of "select a module" is precisely the
                // kind of drift Phases 568–571 kept closing.
                let flipped = {
                    model with
                        CurrentArea = ModuleArea.Administration
                }

                let landingCmd =
                    if modules |> List.exists (fun m -> m.Definition.Id = AdminHome.moduleId) then
                        Cmd.ofMsg (ModuleSelected AdminHome.moduleId)
                    else
                        Cmd.none

                flipped, landingCmd

            | ModuleSelected sidebarId when sidebarId = Toolup.Sidebar.ProductAreaId ->
                {
                    model with
                        CurrentArea = ModuleArea.Product
                },
                Cmd.none

            | ModuleSelected sidebarId ->
                // Sidebar Ids for multi-page modules are composite
                // (`"{moduleId}{pageRoute}"`); single-page modules stay
                // bare. Navigating between pages of the same module
                // updates `ActivePageRoute` only — `Init` does not re-run,
                // so per-page state is preserved.
                let moduleId, pageRouteOpt = parseSidebarId sidebarId

                match tryFind modules moduleId with
                | Some moduleImpl ->
                    // Phase 567.D — deep-link coherence: navigating to a
                    // module flips the rendered area to that module's
                    // effective area (a route owned by an admin-area module
                    // auto-shows the admin rail, and vice versa). Inert under
                    // InlineGroups, where CurrentArea is ignored.
                    let targetArea = ClientConfig.effectiveArea moduleImpl.Area moduleImpl.Group

                    let pageRoute =
                        match pageRouteOpt with
                        | Some r -> Some r
                        | None -> defaultPageRoute moduleImpl

                    // During `Prefetching` no module has a host: a click
                    // only moves which module the first init will build.
                    if model.InitPhase = Prefetching then
                        {
                            model with
                                ActiveModuleId = moduleId
                                ActivePageRoute = pageRoute
                                CurrentArea = targetArea
                        },
                        Cmd.none
                    elif model.ActiveModuleId = moduleId then
                        {
                            model with
                                ActivePageRoute = pageRoute
                                CurrentArea = targetArea
                        },
                        Cmd.none
                    else
                        // The selection moves even when the route guard
                        // refuses the module (a deep link reaches here; the
                        // rail no longer offers a denied module), so the
                        // shell reports the route the caller asked for and
                        // `view` names the module in the denial. What the
                        // guard holds back is the `Init`, in `initModule`.
                        {
                            model with
                                ActiveModuleId = moduleId
                                ActivePageRoute = pageRoute
                                CurrentArea = targetArea
                        }
                        |> ensureHost _config queryBus modules moduleId
                | None -> model, Cmd.none

            | AccessibleModulesLoaded accessible ->
                // A route-guard input landed; mount the active module if it
                // made it reachable.
                {
                    model with
                        AccessibleModules = accessible
                        Degradations = BootDegradation.remove BootSources.Key.Permissions model.Degradations
                }
                |> fun m -> ensureHost _config queryBus modules m.ActiveModuleId m

            | RefreshAccessibleModules -> model, bootLoads.Permissions

            | ToggleShowAllModules ->
                {
                    model with
                        ShowAllModules = not model.ShowAllModules
                },
                Cmd.none

            | ConfigsLoaded configs ->
                let updated = {
                    model with
                        ModuleConfigs = configs |> Map.remove ConfigKeys.PlatformModuleKey
                        PlatformConfig =
                            configs
                            |> Map.tryFind ConfigKeys.PlatformModuleKey
                            |> Option.defaultValue Map.empty
                        Degradations = BootDegradation.remove BootSources.Key.Configs model.Degradations
                }

                onSourceLoaded _config queryBus modules BootSources.Key.Configs model updated

            | FlagsLoaded flags ->
                let updated = {
                    model with
                        ResolvedFlags = flags
                        Degradations = BootDegradation.remove BootSources.Key.Flags model.Degradations
                }

                onSourceLoaded _config queryBus modules BootSources.Key.Flags model updated

            | TeamSwitched newTeamIdOpt -> reset _config (BootSources.ResetKind.TeamSwitch newTeamIdOpt) model

            | LocaleSwitched locale ->
                // A blank tag clears the choice, falling back to whatever
                // `ClientConfig.Locale` resolves to.
                let chosen =
                    if System.String.IsNullOrWhiteSpace locale then
                        None
                    else
                        Some(locale.Trim())

                reset _config (BootSources.ResetKind.LocaleSwitch chosen) model

            | MembershipRevoked teamId ->
                // Server-driven `MembershipChanged.Removed` for this
                // user. Reset only if the removed team is the active
                // one — otherwise the membership change affects a team
                // we're not currently in, and `MyTeams` refresh is
                // enough.
                if model.ActiveTeamId = Some teamId then
                    model, Cmd.ofMsg (TeamSwitched None)
                else
                    model, bootLoads.Teams

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

            | MembershipAdded _ ->
                // Added to a team (without an active-team switch) — refresh
                // the team list so the new team shows up in the header
                // switcher this session. Active team is left untouched.
                model, bootLoads.Teams

            | MyTeamsLoaded teams ->
                // Sole-team auto-select may fire here (teams arriving
                // after the active-team fetch) or in `ActiveTeamLoaded`
                // (the reverse order) — the guard inside
                // `maybeAutoSelectSoleTeam` makes the double-check safe.
                let updated = {
                    model with
                        MyTeams = teams
                        Degradations = BootDegradation.remove BootSources.Key.Teams model.Degradations
                }

                updated, maybeAutoSelectSoleTeam updated

            | PlatformRoleLoaded isAdmin ->
                // A route-guard input: an admin who opened an admin URL was
                // refused while this read was in flight, and is initialised
                // now.
                {
                    model with
                        PlatformRole = if isAdmin then Some PlatformRole.PlatformAdmin else None
                        Degradations = BootDegradation.remove BootSources.Key.PlatformRole model.Degradations
                }
                |> fun m -> ensureHost _config queryBus modules m.ActiveModuleId m

            | ActiveTeamRoleLoaded role ->
                // The caller's own role on the active team: a route-guard
                // input for `NavRole.TeamOwnerAdmin`.
                {
                    model with
                        ActiveTeamRole = role
                        Degradations = BootDegradation.remove BootSources.Key.TeamRole model.Degradations
                }
                |> fun m -> ensureHost _config queryBus modules m.ActiveModuleId m

            | ActiveTeamLoaded teamId ->
                let updated = {
                    model with
                        ActiveTeamId = teamId
                        ActiveTeamLoadCompleted = true
                        Degradations = BootDegradation.remove BootSources.Key.ActiveTeam model.Degradations
                }

                // The no-team gate keys on `ActiveTeamId`: once the module
                // has mounted, settle the surface again (landing or default),
                // re-initialising only if the gate moved it. Before mount the
                // first-init gate settles it.
                let reconciled, reconcileCmd =
                    match updated.InitPhase with
                    | Ready -> onFirstInitReady _config queryBus modules updated updated
                    | Prefetching
                    | Reprefetching -> updated, Cmd.none

                // The moment the active team becomes known at boot, so the
                // caller's role on it is read here; a team switch re-reads it
                // through `reset`.
                reconciled, Cmd.batch [ reconcileCmd; activeTeamRoleCmd teamId; maybeAutoSelectSoleTeam reconciled ]

            | AuthTokenAcquired -> reset _config BootSources.ResetKind.TokenAcquired model

            | BootLoadFailed(source, error) ->
                log.Warn(
                    sprintf
                        "[BootDegradation] %s load FAILED (%s) — rendering with the benign default; the degradation banner offers retry. A failed load is NOT the same as empty data."
                        source
                        error
                )

                let updated = {
                    model with
                        Degradations = BootDegradation.add (degradationEntry source error) model.Degradations
                }

                // A failure is an answer: a source the first init waits for
                // must still release it, or the shell would hold the
                // `Prefetching` skeleton for good.
                match BootSources.tryFind source bootSources with
                | Some row when row.GatesFirstInit -> onSourceLoaded _config queryBus modules source updated updated
                | _ -> updated, Cmd.none

            | RetryBootLoad source ->
                // Optimistic clear — a re-failure re-adds via
                // `BootLoadFailed`, so the banner row is off only while the
                // retry is in flight.
                let updated = {
                    model with
                        Degradations = BootDegradation.remove source model.Degradations
                }

                match BootSources.tryFind source bootSources with
                | Some row when row.Retryable -> updated, row.Load bootLoads updated
                | _ -> updated, Cmd.none

            | BootLoadRecovered source ->
                {
                    model with
                        Degradations = BootDegradation.remove source model.Degradations
                },
                Cmd.none

            | DismissBootDegradations -> { model with Degradations = [] }, Cmd.none

            | PublicationReaderRefused -> { model with AppAccessRefused = true }, Cmd.none

            // ─── Phase 571 — command palette ──────────────────────────
            //
            // Four state transitions and a delegation. Nothing here
            // decides what the palette CONTAINS: the candidate set is
            // derived in `view` from `CommandPaletteNav.candidates`, so
            // there is no cached list to keep in step with an RBAC
            // response or a team switch landing mid-session.
            | CommandPaletteOpened ->
                {
                    model with
                        CommandPalette = CommandPaletteNav.opened
                },
                Cmd.none

            | CommandPaletteDismissed ->
                {
                    model with
                        CommandPalette = CommandPaletteNav.closed
                },
                Cmd.none

            | CommandPaletteQueryChanged query ->
                {
                    model with
                        CommandPalette = CommandPaletteNav.withQuery query model.CommandPalette
                },
                Cmd.none

            | CommandPaletteHighlightMoved delta ->
                {
                    model with
                        CommandPalette = CommandPaletteNav.moveHighlight delta model.CommandPalette
                },
                Cmd.none

            | CommandPaletteSelected sidebarId ->
                // Close, then navigate through the ORDINARY path — the
                // same message a sidebar click sends, so same-module page
                // switches still skip `Init`, the Phase 567 area still
                // flips, and the Phase 569 route guard still applies. A
                // palette that reimplemented selection would be a second
                // navigation path to keep in agreement with the first.
                {
                    model with
                        CommandPalette = CommandPaletteNav.closed
                },
                Cmd.ofMsg (ModuleSelected sidebarId)

            | ResetModule moduleId ->
                // Re-initialise the module and advance its host's generation,
                // which is part of the boundary's React key, so the boundary
                // remounts and clears its error state. Other modules and the
                // per-team state are untouched.
                let generation =
                    model.Hosts
                    |> Map.tryFind moduleId
                    |> Option.map (fun host -> host.Generation + 1)
                    |> Option.defaultValue 0

                let reinitialised, cmd =
                    initModule _config queryBus modules moduleId {
                        model with
                            Hosts = model.Hosts |> Map.remove moduleId
                    }

                match reinitialised.Hosts |> Map.tryFind moduleId with
                | Some host ->
                    {
                        reinitialised with
                            Hosts = reinitialised.Hosts |> Map.add moduleId { host with Generation = generation }
                    },
                    cmd
                | None -> reinitialised, cmd

            | SidebarGroupToggled groupKey ->
                let prefs = SidebarPreferences.toggleExpanded groupKey model.SidebarPrefs
                { model with SidebarPrefs = prefs }, persistSidebarPrefs prefs

            | SidebarModulePinToggled moduleId ->
                let prefs = SidebarPreferences.togglePinned moduleId model.SidebarPrefs
                { model with SidebarPrefs = prefs }, persistSidebarPrefs prefs

            | SidebarModuleReordered(groupKey, orderedIds) ->
                let prefs =
                    if groupKey = Toolup.Sidebar.PinnedKey then
                        SidebarPreferences.setPinnedOrder orderedIds model.SidebarPrefs
                    else
                        SidebarPreferences.setOrder groupKey orderedIds model.SidebarPrefs

                { model with SidebarPrefs = prefs }, persistSidebarPrefs prefs

            | SidebarModuleExpandToggled moduleId ->
                let prefs = SidebarPreferences.toggleModuleExpanded moduleId model.SidebarPrefs
                { model with SidebarPrefs = prefs }, persistSidebarPrefs prefs

            | SidebarEntryHideToggled entryId ->
                // `toggleHidden` also unpins on the hide leg (the Phase
                // 572.C rule) — the shell does not re-implement it here,
                // so pin and hide cannot disagree about the same entry.
                let prefs = SidebarPreferences.toggleHidden entryId model.SidebarPrefs
                { model with SidebarPrefs = prefs }, persistSidebarPrefs prefs

            | ModuleActionReceived(moduleId, actionKey, payloadJson) ->
                // An unregistered target, one with no decoder for this
                // action, or one the navigation decision refuses, drops it:
                // the server-side tool ran and produced its text result, and
                // there is no UI to update here.
                let delivered =
                    tryFind modules moduleId
                    |> Option.bind (fun moduleImpl ->
                        moduleImpl.ActionDecoder
                        |> Option.bind (fun decoder -> decoder (actionKey, payloadJson))
                        |> Option.bind (fun decoded -> stepModule _config queryBus modules moduleImpl decoded model)
                        |> Option.map (fun stepped -> moduleImpl, stepped))

                match delivered with
                | None -> model, Cmd.none
                | Some(moduleImpl, (updatedModel, routedCmd)) ->
                    // A local toast when the target is not the module on
                    // screen. A `Cmd`, because the envelope (user id, id,
                    // timestamp) is built when the effect runs.
                    let toastCmd =
                        if moduleId <> model.ActiveModuleId then
                            let text =
                                (resolveCatalog _config model.LocaleOverride model.PlatformConfig)
                                    .Shell.ResultsAvailableIn
                                    moduleImpl.Definition.Name

                            Cmd.ofEffect (fun _ ->
                                NotificationClient.publishLocal (
                                    NotificationEnvelope.create
                                        (UserSession.getUserId ())
                                        (Notification.SystemMessage(SystemMessageLevel.Info, text))
                                ))
                        else
                            Cmd.none

                    updatedModel, Cmd.batch [ routedCmd; toastCmd ]

            | ModuleEventPublished(topic, payload) ->
                // Fan a cross-module event out to every module subscribed to
                // the topic, by topic alone (a publisher receives its own
                // event only when it also subscribes). Each subscriber the
                // navigation decision admits is stepped with its mapped
                // message, initialised first if it has never been opened.
                // Not a trust boundary: the publisher runs in this user's
                // client, and the server enforces access.
                let folder (accModel, accCmds) (moduleImpl: ErasedModule) =
                    moduleImpl.EventSubscriptions
                    |> Map.tryFind topic
                    |> Option.bind (fun mapMsg ->
                        stepModule _config queryBus modules moduleImpl (mapMsg payload) accModel)
                    |> Option.map (fun (next, cmd) -> next, cmd :: accCmds)
                    |> Option.defaultValue (accModel, accCmds)

                let finalModel, cmds = modules |> List.fold folder (model, [])
                finalModel, Cmd.batch cmds

        // Phase 852 — return the same list when nothing changed. The
        // aggregation re-runs on every message and always allocates, so
        // storing its result unconditionally handed every render a NEW
        // `ProcessedData` — which re-rendered every `ProcessedDataContext`
        // consumer, and would re-render the shell chrome on every module
        // message under the sliced store (the chrome reads this field).
        // Entries are small records (names, a timestamp, a JSON summary),
        // so the structural compare is cheap next to the render it saves.
        let finalModel =
            let processed =
                resolveProcessedData _config modules newModel.Hosts newModel.PrefetchedProcessedData

            if processed = newModel.ProcessedData then
                newModel
            else
                {
                    newModel with
                        ProcessedData = processed
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
        // initialised yet (no host) are skipped — the
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
                    | Some _ when finalModel.Hosts |> Map.containsKey m.Definition.Id ->
                        Some(Cmd.ofMsg (ResetModule m.Definition.Id))
                    | _ -> None)
                |> Cmd.batch

        finalModel, Cmd.batch [ cmd; processedDataResetCmds ]

    /// The shell's `update`. Phase 1240 — under browser history, a message that
    /// moved the view also writes the URL, as a command (`update` performs no
    /// side effect, Phase 1069), and at this one site so every composer over
    /// `update` writes it too. A navigation adds a history entry. Anything else
    /// that moved the view, the no-active-team gate above all, replaces the
    /// entry, so Back does not land on a view that would redirect again. A Back
    /// or Forward arrives here as `ModuleSelected` too, and adds nothing: the
    /// URL already names the view when the write runs.
    ///
    /// Phase 1241 — while signed out, the view kept across sign-in follows the
    /// reader. Whether they are signed out is read when the command runs, so
    /// `update` stays free of reads too. A token arriving with the reader still
    /// on the kept view is a sign-in that did not leave the page, so it goes.
    let update (config: ClientConfig) (queryBus: IModuleQueryBus) (modules: ErasedModule list) msg model =
        let next, cmd = updateShell config queryBus modules msg model

        match config.Shell.BrowserHistory with
        | NoBrowserHistory -> next, cmd
        | HashHistory ->
            let view = BrowserHistory.viewId next.ActiveModuleId next.ActivePageRoute

            if view = BrowserHistory.viewId model.ActiveModuleId model.ActivePageRoute then
                match msg with
                | AuthTokenAcquired ->
                    next, Cmd.batch [ cmd; Cmd.ofEffect (fun _ -> BrowserHistory.forgetIfNames view) ]
                | _ -> next, cmd
            else
                let write =
                    match msg with
                    | ModuleSelected _ -> BrowserHistory.push
                    | _ -> BrowserHistory.replace

                next,
                Cmd.batch [
                    cmd
                    Cmd.ofEffect (fun _ ->
                        write view

                        if isSignedOut config then
                            BrowserHistory.store view)
                ]

    /// The module-state observer publish, kept out of `update` so `update`
    /// performs no side effect. Called AFTER an update with the hosts the
    /// model held before it: a module the message could have moved — the named module
    /// on `ModuleMsg`, the target of `ModuleActionReceived`, each subscriber
    /// of a `ModuleEventPublished` topic — is published when its state
    /// object changed. A message whose gates dropped it (an unregistered or
    /// inaccessible target) leaves the state untouched and publishes
    /// nothing, exactly as the inline calls did.
    let observeModuleStates
        (modules: ErasedModule list)
        (msg: Msg)
        (priorHosts: Map<string, ModuleHost>)
        (next: Model)
        : unit =
        let candidates =
            match msg with
            | ModuleMsg(moduleId, _) -> [ moduleId ]
            | ModuleActionReceived(moduleId, _, _) -> [ moduleId ]
            | ModuleEventPublished(topic, _) ->
                modules
                |> List.filter (fun m -> m.EventSubscriptions |> Map.containsKey topic)
                |> List.map _.Definition.Id
            | _ -> []

        for moduleId in candidates do
            match tryFind modules moduleId, next.Hosts |> Map.tryFind moduleId with
            | Some moduleImpl, Some host ->
                let changed =
                    match priorHosts |> Map.tryFind moduleId with
                    | Some prior -> not (obj.ReferenceEquals(prior.State, host.State))
                    | None -> true

                if changed then
                    ModuleStateObserver.publishState moduleImpl.InspectState moduleId host.State
            | _ -> ()

    /// Install `observeModuleStates` as the program's one post-update hook,
    /// through the fork's `withTrace` seam. `unwrap` recovers the shell message from the program's
    /// message type and `shellOf` the shell model, so a composer that
    /// embeds the shell (ToolUp.AI's `AIClientConfig`) installs the same
    /// hook as `Client.program`. The hook keeps the last hosts it saw, which
    /// is what a shell message's prior state was: only shell messages move
    /// them.
    let withModuleStateObserver
        (modules: ErasedModule list)
        (unwrap: 'msg -> Msg option)
        (shellOf: 'model -> Model)
        (prog: Program<'arg, 'model, 'msg, 'view>)
        : Program<'arg, 'model, 'msg, 'view> =
        let lastHosts: Map<string, ModuleHost> ref = ref Map.empty

        prog
        |> Program.withTrace (fun msg model _ ->
            match unwrap msg with
            | Some shellMsg ->
                let shell = shellOf model
                observeModuleStates modules shellMsg lastHosts.Value shell
                lastHosts.Value <- shell.Hosts
            | None -> ())

    /// Controlled team switcher for the page header. Replaces the previous
    /// native `<details>` element, which had neither close-on-outside-click
    /// nor close-on-selection — once opened (e.g. to pick a freshly-created
    /// team) nothing dismissed it. Open state lives in `React.useState`; a
    /// `mousedown` listener on `document` closes the menu on an outside
    /// click (mirroring `UI.Toolkit.Forms.dropdown`), and selecting a team
    /// closes it explicitly. The server-persist-then-dispatch path
    /// (`teamApi.SetActiveTeam` → `TeamSwitched`) is unchanged from the old
    /// inline render.
    [<ReactComponent>]
    let TeamSwitcher (myTeams: TeamInfo list) (activeTeamId: string option) (dispatch: Msg -> unit) =
        let isOpen, setIsOpen = React.useState false
        let rootRef = React.useRef<Browser.Types.HTMLDivElement option> (None)
        // Phase 444 — shell chrome reads its strings from the resolved
        // catalog. A hook is legal here, and not inside a module's `view`,
        // because this is a `[<ReactComponent>]` — the same distinction
        // `FileManagerUI.LoadingSlot` documents.
        let msgs = (MessageCatalogProvider.useMessages ()).Shell

        // Close on outside click — same idiom as UI.Toolkit.Forms.dropdown.
        React.useEffect (
            (fun () ->
                let handler (e: Browser.Types.Event) =
                    match rootRef.current with
                    | Some element when not (element.contains (e.target :?> Browser.Types.Node)) -> setIsOpen false
                    | _ -> ()

                // `removeEventListener` matches by REFERENCE — bind the JS
                // function once so add and remove receive the identical
                // object (Fable can emit a fresh wrapper per call site
                // otherwise, leaking one listener per mount).
                let listener: Browser.Types.Event -> unit = handler

                Browser.Dom.document.addEventListener ("mousedown", listener)
                FsReact.createDisposable (fun () -> Browser.Dom.document.removeEventListener ("mousedown", listener))),
            [||]
        )

        let activeName =
            activeTeamId
            |> Option.bind (fun id -> myTeams |> List.tryFind (fun t -> t.TeamId = id))
            |> Option.map _.Name
            |> Option.defaultValue msgs.SelectTeam

        // Surface a failed switch via the existing ToastCentre instead of
        // swallowing it — a silent failure leaves the user believing they
        // switched team when they didn't.
        let warnSwitchFailed () =
            NotificationClient.publishLocal (
                NotificationEnvelope.create
                    (UserSession.getUserId ())
                    (Notification.SystemMessage(SystemMessageLevel.Warning, msgs.SwitchTeamFailed))
            )

        Html.div [
            prop.ref rootRef
            prop.className "relative inline-block"
            prop.children [
                Html.button [
                    prop.className
                        "list-none cursor-pointer flex items-center gap-2 px-3 py-1.5 rounded border border-gray-200 hover:bg-gray-50 text-sm"
                    prop.onClick (fun _ -> setIsOpen (not isOpen))
                    prop.children [
                        Html.span [ prop.className "text-gray-500"; prop.text msgs.TeamLabel ]
                        Html.span [ prop.className "font-medium text-gray-900"; prop.text activeName ]
                        Html.span [ prop.className "text-gray-400 text-xs"; prop.text "▾" ]
                    ]
                ]
                if isOpen then
                    Html.div [
                        prop.className
                            "absolute right-0 mt-1 min-w-[200px] bg-white border border-gray-200 rounded shadow-lg z-20"
                        prop.children [
                            for team in myTeams do
                                let isActive = activeTeamId = Some team.TeamId

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
                                            // Close immediately, then persist on the server
                                            // (CSRF-ensured, matching TeamManagerUI) and
                                            // dispatch shell-level `TeamSwitched`. Surface a
                                            // toast on failure rather than swallowing it.
                                            setIsOpen false

                                            async {
                                                try
                                                    match! withCsrf (teamApi.SetActiveTeam team.TeamId) with
                                                    | Ok() -> dispatch (TeamSwitched(Some team.TeamId))
                                                    | Error _ -> warnSwitchFailed ()
                                                with _ ->
                                                    warnSwitchFailed ()
                                            }
                                            |> Async.StartImmediate)
                                ]
                        ]
                    ]
            ]
        ]

    /// Built-in no-active-team onboarding surface. Rendered in the content
    /// area when a team-scoped deployment has an authenticated user whose
    /// active-team fetch resolved to none AND no `NoActiveTeamLandingFill.Module`
    /// is configured — replacing the wall of `401 missing-tenant` that every
    /// `TenantScoped` module would otherwise produce. Consumers wanting a
    /// bespoke landing keep setting `NoActiveTeamLandingFill.Module`; this is the
    /// sane default when they don't. Adapts to membership: create a team when
    /// the user has none, or pick one when they have teams but none active.
    [<ReactComponent>]
    let NoActiveTeamSurface (myTeams: TeamInfo list) (dispatch: Msg -> unit) =
        let newName, setNewName = React.useState ""
        let busy, setBusy = React.useState false
        // Phase 444 — see `TeamSwitcher` above for why the hook is legal here.
        let msgs = (MessageCatalogProvider.useMessages ()).Shell

        let toast (level: SystemMessageLevel) (text: string) =
            NotificationClient.publishLocal (
                NotificationEnvelope.create (UserSession.getUserId ()) (Notification.SystemMessage(level, text))
            )

        let switchTo (teamId: string) =
            async {
                try
                    match! withCsrf (teamApi.SetActiveTeam teamId) with
                    | Ok() -> dispatch (TeamSwitched(Some teamId))
                    | Error _ -> toast SystemMessageLevel.Warning msgs.SwitchTeamFailed
                with _ ->
                    toast SystemMessageLevel.Warning msgs.SwitchTeamFailed
            }
            |> Async.StartImmediate

        let createTeam () =
            let name = newName.Trim()

            if name <> "" && not busy then
                setBusy true

                async {
                    try
                        match! withCsrf (teamApi.CreateTeam name) with
                        | Ok team ->
                            // Switch straight into the new team — reloads MyTeams,
                            // sets active, and mounts the work modules. Reliable
                            // even if the membership-notification stream is down.
                            dispatch (TeamSwitched(Some team.TeamId))
                        | Error msg ->
                            setBusy false
                            // Also surfaces a policy denial ("requires Platform Admin").
                            toast SystemMessageLevel.Warning msg
                    with _ ->
                        setBusy false
                        toast SystemMessageLevel.Warning msgs.CreateTeamFailed
                }
                |> Async.StartImmediate

        Html.div [
            prop.className "flex items-center justify-center w-full h-full p-8"
            prop.children [
                Html.div [
                    prop.className "max-w-md w-full bg-white border border-gray-200 rounded-lg shadow-sm p-6 space-y-4"
                    prop.children [
                        Html.h2 [
                            prop.className "text-lg font-semibold text-gray-900"
                            prop.text msgs.NoTeamHeading
                        ]

                        if List.isEmpty myTeams then
                            Html.p [ prop.className "text-sm text-gray-600"; prop.text msgs.NoTeamBody ]

                            Html.div [
                                prop.className "space-y-2"
                                prop.children [
                                    Html.input [
                                        prop.className "w-full px-3 py-2 border border-gray-200 rounded text-sm"
                                        prop.placeholder msgs.TeamNamePlaceholder
                                        prop.value newName
                                        prop.onChange (fun (v: string) -> setNewName v)
                                        prop.onKeyDown (fun e ->
                                            if e.key = "Enter" then
                                                createTeam ())
                                    ]
                                    Html.button [
                                        prop.className
                                            "w-full px-3 py-2 rounded bg-blue-600 text-white text-sm font-medium hover:bg-blue-700 disabled:opacity-50"
                                        prop.disabled (busy || newName.Trim() = "")
                                        prop.text (if busy then msgs.CreatingTeam else msgs.CreateTeam)
                                        prop.onClick (fun _ -> createTeam ())
                                    ]
                                ]
                            ]
                        else
                            Html.p [ prop.className "text-sm text-gray-600"; prop.text msgs.PickTeam ]

                            Html.div [
                                prop.className "space-y-1"
                                prop.children [
                                    for team in myTeams do
                                        Html.button [
                                            prop.className
                                                "w-full text-left px-3 py-2 text-sm rounded border border-gray-200 hover:bg-gray-50"
                                            prop.text team.Name
                                            prop.onClick (fun _ -> switchTo team.TeamId)
                                        ]
                                ]
                            ]
                    ]
                ]
            ]
        ]

    // ─── The shell's dispatch wrappers, built once per loop ─────────────
    //
    // A module view receives `dispatch` wrapped in `ModuleMsg` with its own
    // id, and the sidebar six callbacks over it. Built inline in `view`,
    // each wrapper is a FRESH closure on every render, so the props of the
    // module's memo boundary (`React.memo`, a `[<ReactComponent>]` keyed on
    // props) and of the sidebar's (`Layout.AppShell`) change identity on
    // every shell render and neither boundary holds. The loop's own
    // dispatcher is one function for the loop's life, so the program builds
    // the wrappers from it once (`Program.withStableDispatch dispatchers`)
    // and every render hands out the same ones. The wrappers are compared by
    // REFERENCE because that is the property the boundaries key on.

    /// The sidebar's six callbacks for one Elmish loop.
    type internal SidebarDispatchers = {
        Select: string -> unit
        ToggleGroup: string -> unit
        TogglePin: string -> unit
        ToggleModule: string -> unit
        ToggleHidden: string -> unit
        Reorder: string -> string list -> unit
    }

    /// The shell's dispatch and the wrappers its views hand out, for one
    /// loop. Build it with `Client.dispatchers` through
    /// `Program.withStableDispatch`, so it is built once per loop: a set
    /// built per render hands every boundary a new callback and holds none.
    [<Sealed>]
    type Dispatchers internal (dispatch: Msg -> unit) =
        let byModule = System.Collections.Generic.Dictionary<string, obj -> unit>()

        let sidebar = {
            Select = ModuleSelected >> dispatch
            ToggleGroup = SidebarGroupToggled >> dispatch
            TogglePin = SidebarModulePinToggled >> dispatch
            ToggleModule = SidebarModuleExpandToggled >> dispatch
            ToggleHidden = SidebarEntryHideToggled >> dispatch
            Reorder = fun groupKey orderedIds -> dispatch (SidebarModuleReordered(groupKey, orderedIds))
        }

        /// The loop's dispatcher.
        member _.Dispatch: Msg -> unit = dispatch

        /// The dispatcher one module's view receives: the same function on
        /// every call for this set, bound to that module.
        member _.ForModule(moduleId: string) : obj -> unit =
            match byModule.TryGetValue moduleId with
            | true, forModule -> forModule
            | _ ->
                let forModule: obj -> unit = fun message -> dispatch (ModuleMsg(moduleId, message))
                byModule[moduleId] <- forModule
                forModule

        /// The sidebar's callbacks.
        member internal _.Sidebar: SidebarDispatchers = sidebar

    /// The shell's wrapper set over a loop's dispatcher — the `build` a
    /// program hands `Program.withStableDispatch`.
    let dispatchers (dispatch: Dispatch<Msg>) : Dispatchers = Dispatchers dispatch

    // ─── The chrome's context values, memoised per input ────────────────
    //
    // The shell publishes shared state to every view through React
    // contexts (feature flags, branding, administration tiles, the message
    // catalog), and a context re-renders every reader whenever its value
    // changes identity — through any memo boundary. Each value is therefore
    // the SAME object while its inputs are the same objects (a one-entry
    // `Memo` per value, as `resolveCatalog` keeps for the catalog); a change
    // to any input builds a new one.

    /// The flag keys the client knows about — every module's declared
    /// `FeatureFlags` and every key the server resolved — the same `Set`
    /// while the module list and the resolved map are unchanged.
    let private declaredFlagKeys: ErasedModule list -> Map<string, FlagValue> -> Set<string> =
        Memo.byRef2 (fun modules resolved ->
            let moduleKeys =
                modules |> List.collect _.FeatureFlags |> List.map _.Key |> Set.ofList

            let serverKeys = resolved |> Map.toSeq |> Seq.map fst |> Set.ofSeq
            Set.union moduleKeys serverKeys)

    /// The active team's branding resolved against the composition root's
    /// defaults — the same record while the config and the team's
    /// `_platform` config are unchanged.
    let private resolveBranding: ClientConfig -> Map<string, string> -> Branding =
        Memo.byRef2 (fun config platformConfig ->
            Branding.resolve
                {
                    AppName = config.AppName
                    PrimaryColor = Branding.DefaultPrimaryColor
                    LogoUrl = config.AppLogo
                    FaviconUrl = config.AppLogo
                    PaletteOverrides = []
                }
                platformConfig)

    /// The tiles of `registered` whose owning module is in `visibleIds` —
    /// the same list while the registered tiles (by reference) and the
    /// visible ids (by value: the visible-module list is re-derived on every
    /// chrome render) are unchanged.
    let private adminTilesFor =
        Memo.oneEntryBy
            (fun (registered1, ids1: string list) (registered2, ids2) ->
                obj.ReferenceEquals(registered1, registered2) && ids1 = ids2)
            (fun (registered, visibleIds) -> AdminTiles.forOwners AdminTileRegistry.facts visibleIds registered)

    /// The administration tiles whose owning module is in `visibleIds`.
    let private visibleAdminTiles (visibleIds: string list) : AdminTile list =
        adminTilesFor (AdminTileRegistry.tiles (), visibleIds)

    /// The module boundary's React key: the active team and the host's
    /// generation, so a team switch or a `ResetModule` remounts the boundary
    /// and clears its error state.
    let private resetKeyOf (scopeKey: string) (host: ModuleHost) : string = $"{scopeKey}-{host.Generation}"

    /// The active module's error boundary around its view — one
    /// construction site for both render paths (the whole-tree `view` and
    /// the Phase 852 store-subscribed host below).
    let private moduleBoundary
        (config: ClientConfig)
        (shellMsgs: ShellMessages)
        (boundaryMsgs: ModuleBoundaryMessages)
        (moduleImpl: ErasedModule)
        (moduleId: string)
        (pageRoute: string option)
        (resetKey: string)
        (dispatchers: Dispatchers)
        (currentState: obj)
        : ReactElement =
        let dispatch = dispatchers.Dispatch
        let dispatchMsg = dispatchers.ForModule moduleId

        let renderInner () : PageContent =
            match moduleImpl.PageViews, pageRoute with
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
                    SplitPanel(Html.div (shellMsgs.NoViewForRoute(defaultArg pageRoute "")), Html.div "")

        Components.ModuleBoundary.wrapWith
            boundaryMsgs
            moduleId
            resetKey
            config.Composition.OnError
            (fun () -> dispatch (ResetModule moduleId))
            config.Shell.InputsPaneWidth
            renderInner

    // ─── Phase 852 — the sliced shell (store-bound `Client.run` only) ────
    //
    // Under the store binding (`Program.withReactStore`), the shell's chrome
    // and the active module are two separate React boundaries that each
    // read their own slice of the model:
    //
    //   * the CHROME (`shellChrome`) is a memo boundary over the whole model
    //     with `Hosts` masked out — it re-renders when any OTHER
    //     field changes identity, and never on a message that changed only
    //     a module's state. Masking one named field (rather than listing the
    //     fields the chrome reads) is deliberate: a field added to `Model`
    //     later is read by the chrome's comparison automatically, so the
    //     failure mode of forgetting is an extra render, never a stale one.
    //   * the ACTIVE MODULE (`activeModuleHost`) is a memo boundary over its
    //     identity props that reads `Hosts[moduleId]` through
    //     `ModelStore.useSelector` by reference — so it re-renders when its
    //     own state object changes, and on nothing else. The selector runs
    //     on every publish; the view runs only when the state moved.
    //
    // Reference comparison is the whole mechanism, and it is sound because
    // the model is immutable: `Map.add` for one key leaves every other value
    // the same object, and an update that returns its input state unchanged
    // returns the same object — the "return the same state when nothing
    // changed" rule `docs/platform/modules.md` states for module authors.

    [<Fable.Core.Import("memo", "react")>]
    let private reactMemo (render: obj, areEqual: obj) : obj = Fable.Core.Util.jsNative

    // ─── Phase 910 — the sliced shell over any store that holds the shell ──
    //
    // `Client.run` publishes the shell's own `Model` to its store. A
    // composer that wraps the shell in a model of its own (the AI
    // assistant's `OuterModel`) publishes THAT, so the sliced shell reads
    // its slices through a projection: the store, and where the shell's
    // model sits in what the store publishes. The boundaries compare the
    // STORE by reference (`Source`), never the reader, so a reader rebuilt
    // on a later render is the same reader to them.

    /// Where a store-bound shell reads the shell model from: a model store,
    /// and how to find the shell's `Model` in what that store publishes.
    /// Build one with `Client.shellStore` and render with
    /// `Client.viewSlicedOver`.
    [<Sealed>]
    type ShellStore internal (source: obj, useShellSlice: (Model -> obj) -> obj) =
        /// The store read — the identity the shell's boundaries compare.
        member internal _.Source = source

        /// A React hook: the slice `select` picks from the shell model the
        /// store last published, re-rendering the caller only when it
        /// changes by reference.
        member internal _.UseSlice(select: Model -> obj) : obj = useShellSlice select

    /// Phase 910 — a `ShellStore` over `store`, finding the shell's model in
    /// each published model with `shellOf`. `shellOf` must be a pure
    /// projection that returns the SAME `Model` object while the shell's
    /// model is unchanged (a record field read, e.g. `_.Shell`) — the
    /// shell's slices are compared by reference. For a program whose model
    /// IS the shell's, pass `id`.
    let shellStore (store: ModelStore<'model, 'msg>) (shellOf: 'model -> Model) : ShellStore =
        ShellStore(
            box store,
            fun select -> ModelStore.useSelector store (fun model -> select (shellOf model)) ModelStore.refEquals
        )

    /// The props of the active-module host. Every member is stable across
    /// renders unless the chrome's own slice changed (the config and module
    /// are fixed for the program run; `dispatch` is the loop's own since
    /// 851; the store is created once per run), so the host's default
    /// shallow memo holds whenever the chrome re-renders for a reason of
    /// its own.
    type internal ModuleHostProps = {
        Store: ShellStore
        Config: ClientConfig
        // The two catalog sections the host reads, not the catalog: the
        // resolved catalog is a fresh record per chrome render
        // (`MessageCatalog.forLocale` stamps the locale), while its
        // sections are shared with the built-in catalog, so passing the
        // sections keeps the host's memo holding across chrome renders.
        ShellMessages: ShellMessages
        BoundaryMessages: ModuleBoundaryMessages
        Module: ErasedModule
        ModuleId: string
        PageRoute: string option
        /// The scope half of the boundary's React key; the host's generation
        /// is the other half, read from the store with the state.
        ScopeKey: string
        /// The loop's wrapper set — the module's dispatcher comes from it.
        Dispatchers: Dispatchers
    }

    let private renderModuleHost (props: obj) : ReactElement =
        let p = unbox<ModuleHostProps> props

        let host: obj =
            p.Store.UseSlice(fun (m: Model) ->
                match m.Hosts |> Map.tryFind p.ModuleId with
                | Some host -> box host
                | None -> null)

        // Permitted but with no host yet: the route guard refused this module
        // while one of its inputs was still loading, and the arm receiving it
        // has not yet run the deferred `Init`. Decided HERE, not by the
        // chrome, because the host's arrival is a `Hosts` change — which is
        // exactly what the chrome's comparison does not see.
        if isNull host then
            Toolup.UIToolkit.Layout.loadingIndicator p.Config.Shell.LoadingIndicator
        else
            let host = unbox<ModuleHost> host

            moduleBoundary
                p.Config
                p.ShellMessages
                p.BoundaryMessages
                p.Module
                p.ModuleId
                p.PageRoute
                (resetKeyOf p.ScopeKey host)
                p.Dispatchers
                host.State

    /// Equal when every prop is the same object, the store compared by the
    /// store it reads (see `ShellStore`).
    let private moduleHostEqual (prev: obj) (next: obj) : bool =
        let p = unbox<ModuleHostProps> prev
        let n = unbox<ModuleHostProps> next

        obj.ReferenceEquals(p.Store.Source, n.Store.Source)
        && obj.ReferenceEquals(p.Config, n.Config)
        && obj.ReferenceEquals(p.ShellMessages, n.ShellMessages)
        && obj.ReferenceEquals(p.BoundaryMessages, n.BoundaryMessages)
        && obj.ReferenceEquals(p.Module, n.Module)
        && obj.ReferenceEquals(p.ModuleId, n.ModuleId)
        && obj.ReferenceEquals(p.PageRoute, n.PageRoute)
        && obj.ReferenceEquals(p.ScopeKey, n.ScopeKey)
        && obj.ReferenceEquals(p.Dispatchers, n.Dispatchers)

    /// The active module's store-subscribed memo boundary. The comparer goes
    /// to React as a two-argument JS function (see `shellChrome`).
    let private activeModuleHost: obj =
        reactMemo (box renderModuleHost, box (System.Func<obj, obj, bool>(moduleHostEqual)))

    /// How `view` produces the active module's content.
    [<RequireQualifiedAccess>]
    type private ModuleSlot =
        /// Read the state from the model `view` was handed — the whole-tree
        /// path every `Client.view` / `Client.viewWithSignIn` caller gets.
        | Inline
        /// Emit the store-subscribed host.
        | Subscribed of ShellStore

    let private viewShell
        (slot: ModuleSlot)
        (config: ClientConfig)
        (modules: ErasedModule list)
        (chrome: ExtraChrome)
        (model: Model)
        (dispatchers: Dispatchers)
        =
        let dispatch = dispatchers.Dispatch

        // The catalog every string in this render comes from.
        // Resolved once at the top of `view` because chrome is built at
        // several depths below (the page-route placeholder, the area
        // switcher entries, the header actions) and they must all read the
        // same value.
        let resolvedCatalog =
            resolveCatalog config model.LocaleOverride model.PlatformConfig

        let shellMsgs = resolvedCatalog.Shell

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
        // 0.5.16 — when the shell is still gating on prefetch arrivals,
        // render a skeleton in the content area while keeping sidebar +
        // header chrome live. The active module is unmounted (its `Init`
        // has not run yet); we don't reach `tryFind` below because
        // no module has a host under the `Prefetching` invariant and the
        // existing `Map.find` would crash. `Reprefetching` falls through
        // to the steady-state render — the prior view stays mounted with
        // stale data while new prefetches refresh.
        let content: PageContent =
            match model.InitPhase with
            | Prefetching -> Custom(Toolup.UIToolkit.Layout.loadingIndicator config.Shell.LoadingIndicator)
            | Ready
            | Reprefetching when
                ClientConfig.hasTeamScope config
                && model.ActiveTeamLoadCompleted
                && model.ActiveTeamId.IsNone
                && (ClientConfig.ownNoActiveTeamLandingModuleId config).IsNone
                ->
                // No active team in a team-scoped deployment, the active-team
                // fetch has resolved (no flash before teams load), and the
                // consumer configured no landing module. Render the built-in
                // onboarding surface instead of mounting a TenantScoped module
                // that would 401 `missing-tenant`.
                Custom(NoActiveTeamSurface model.MyTeams dispatch)
            | Ready
            | Reprefetching ->
                // Phase 569.B — the route guard at page dispatch. The
                // active module reaches here from a sidebar click, a
                // `NavigationRequest` deep link, or a pasted URL; only
                // the first was ever filtered, which is exactly the
                // incoherence this closes. `navigationDecision` is the
                // same call the sidebar's `visibleModules` fold makes
                // (`SidebarVisibility.canNavigateTo`, one definition
                // site), so a module absent from the rail cannot render
                // here. The update side has already refused to run its
                // `Init`, so nothing has been fetched on its behalf.
                match
                    tryFind modules model.ActiveModuleId
                    |> Option.map (fun m -> m, navigationDecision config model m)
                with
                | Some(moduleImpl, SidebarVisibility.NavigationDecision.Denied reason) ->
                    Custom(
                        Components.NotAuthorised.render config.Shell.NotAuthorisedView {
                            ModuleId = moduleImpl.Definition.Id
                            ModuleName = moduleImpl.Definition.Name
                            Denial = reason
                            GoHome = fun () -> dispatch (ModuleSelected(routeHomeModuleId config modules model))
                        }
                    )
                | Some(moduleImpl, SidebarVisibility.NavigationDecision.Permitted) ->
                    // Permitted but with no host yet: the guard refused
                    // this module while one of its inputs was still
                    // loading, and the arm receiving it has not yet run the
                    // deferred `Init`. A one-render window —
                    // both happen in the same update — but `Map.find`
                    // here would crash the shell, so hold the loading
                    // indicator rather than assume the entry exists.
                    let scopeKey = model.ActiveTeamId |> Option.defaultValue "_"

                    match slot with
                    | ModuleSlot.Subscribed store ->
                        // Phase 852 — the host reads the module's state
                        // from the store itself (and holds the loading
                        // indicator while the entry is absent).
                        let props: ModuleHostProps = {
                            Store = store
                            Config = config
                            ShellMessages = resolvedCatalog.Shell
                            BoundaryMessages = resolvedCatalog.ModuleBoundary
                            Module = moduleImpl
                            ModuleId = model.ActiveModuleId
                            PageRoute = model.ActivePageRoute
                            ScopeKey = scopeKey
                            Dispatchers = dispatchers
                        }

                        Custom(ReactLegacy.createElement (unbox<ReactElement> activeModuleHost, box props))
                    | ModuleSlot.Inline ->
                        match model.Hosts |> Map.tryFind model.ActiveModuleId with
                        | None -> Custom(Toolup.UIToolkit.Layout.loadingIndicator config.Shell.LoadingIndicator)
                        | Some host ->
                            Custom(
                                moduleBoundary
                                    config
                                    resolvedCatalog.Shell
                                    resolvedCatalog.ModuleBoundary
                                    moduleImpl
                                    model.ActiveModuleId
                                    model.ActivePageRoute
                                    (resetKeyOf scopeKey host)
                                    dispatchers
                                    host.State
                            )
                | None -> SplitPanel(Html.div shellMsgs.ModuleNotFound, Html.div "")

        // Composite sidebar Id for the active-border highlight so that
        // the shell matches the sidebar entry emitted for the active
        // page (multi-page modules emit one entry per PageConfig).
        let selectedSidebarId =
            match model.ActivePageRoute with
            | Some route -> $"{model.ActiveModuleId}{route}"
            | None -> model.ActiveModuleId

        // Phase 570.A — the four-filter visibility fold (RBAC
        // accessibility → the Phase 4b platform-admin role gate →
        // the Phase 66 B.3 per-module `Visibility` predicate → the
        // no-active-team landing collapse) was lifted out of the
        // `sidebarSections` binding below into
        // `SidebarVisibility.visible`, which is its
        // single definition site. **The filter order and the reason
        // for it are documented there, on `SidebarVisibility.visible`
        // — read that doc-comment, not the fragments this comment
        // replaced.** The prose that used to sit beside each inline
        // `List.filter` here had drifted from the code it described
        // (the role gate matched a group label no built-in declared),
        // which is why the decision now lives somewhere a contract
        // pack can pin it: `ToolUp.Platform.Tests`'
        // `InProcess/SidebarVisibilityContractTests.fs` asserts the
        // visible-id set across role × mode × exposure.
        //
        // The three `ClientConfig` reads stay here — `SubjectKind`
        // resolution (B.8's canonical projection over
        // `config.Surfaces`, shared with `UserSession` storage
        // selection and the `AuthUIProvider` sign-in mount), the
        // team-scope predicate, and the unified landing-module id —
        // because `ClientConfig` itself is Fable-only at runtime.
        // Passing their resolved values keeps the fold pure data.
        //
        // Phase 569.A — the projection + inputs are no longer built
        // inline here: `moduleFacts` / `navigationInputs` are the
        // shell's one construction site, shared verbatim with the
        // route guard in the content dispatch above. `visible` is
        // itself `List.filter SidebarVisibility.canNavigateTo`, so
        // "hidden from the rail" and "refused by the router" are the
        // same call on the same inputs — they cannot drift.
        //
        // Phase 571 — hoisted out of `sidebarSections` so the command
        // palette below reads the SAME already-filtered list rather
        // than re-deriving one. The palette re-runs the fold through
        // `CommandPaletteNav.candidates` (one call, tens of modules)
        // precisely so its parity with the rail is a call to the same
        // function rather than a promise; this binding is what the
        // per-destination icons are matched against.
        let visibleModules =
            modules |> SidebarVisibility.visible moduleFacts (navigationInputs config model)

        let sidebarSections =
            let hasDataForType dt =
                model.ProcessedData |> List.exists (fun e -> e.DataType = dt && e.Error.IsNone)

            // One sidebar *view* per module. A single-page (or legacy)
            // module carries no `Pages` and renders as a leaf whose
            // sidebar Id is the bare module Id. A multi-page module
            // (`PageViews = Some ...` with >1 page) carries one
            // `SidebarPageView` per `PageConfig`, each with the composite
            // Id `"{moduleId}{pageRoute}"`, and renders as one collapsible
            // parent entry that expands to those pages. Route strings
            // conventionally start with `/`, so direct concatenation keeps
            // the form readable (e.g. `"SalesAnalysis/sku-analysis"`). The
            // composite page Id round-trips through `parseSidebarId` back
            // to `(moduleId, pageRoute)` on click, and is compared against
            // `selectedSidebarId` below for the active-border highlight —
            // routing and deep-linking are unchanged by the nesting; only
            // the presentation groups the pages under their module.
            // Phase 567 — under Chrome.AdminArea = ChromeFill.On(), render one
            // navigation area at a time plus a role-gated switcher entry; the
            // default inline groups leaves visibleModules and the switcher list
            // untouched (byte-identical to pre-567).
            //
            // 567.E compose-order: the area split runs LAST, over
            // `visibleModules` — i.e. AFTER all four stages of
            // `SidebarVisibility.visible` (RBAC → platform-admin role gate →
            // per-subject `Visibility` → no-active-team landing collapse; the
            // canonical order + rationale is the doc-comment on that function).
            // So the admin partition only ever contains modules the caller may
            // already see: a non-admin has had the platform-scoped groups
            // stripped upstream (empty admin partition ⇒ no switcher), and
            // Phase 245 per-team exposure / `ShowAllModules` have already
            // applied. No double-hiding: the split re-buckets an
            // already-authorised set, it does not re-filter.
            let areaFilteredModules, switcherViews =
                match config.Chrome.AdminArea with
                | ChromeFill.Off -> visibleModules, []
                | ChromeFill.On() ->
                    let adminModules, productModules =
                        visibleModules
                        |> List.partition (fun m ->
                            ClientConfig.effectiveArea m.Area m.Group = ModuleArea.Administration)

                    match model.CurrentArea with
                    | ModuleArea.Administration ->
                        adminModules,
                        [
                            ({
                                Id = Toolup.Sidebar.ProductAreaId
                                Name = shellMsgs.BackToApp
                                // Phase 610 Tidy-Up — this was `Html.none`,
                                // and in the NARROW icon-only rail the icon
                                // is the entire row: the switcher rendered
                                // as an empty 32×32 box. Named and focusable
                                // since Phase 609, so keyboard, voice and
                                // screen-reader users could always reach it
                                // — it was invisible only to the sighted
                                // pointer user, which is the majority case
                                // for the one row that leaves the
                                // administration area.
                                Icon = ToolUp.Platform.Icons.arrowLeft
                                HasData = true
                                Group = None
                                Pages = []
                                // Phase 611 — declared, not inferred. Before
                                // this the row carried `Group = None` and the
                                // sidebar's bucketing put it in the collapsed
                                // `_other` catch-all at the foot of the rail:
                                // the only route back out of the
                                // administration area, hidden behind a
                                // section the user had to think to open.
                                Placement = Some Toolup.Sidebar.TrailingSlot
                            }
                            : Toolup.Sidebar.SidebarModuleView)
                        ]
                    | ModuleArea.Product ->
                        // The switcher appears only when there is an admin area
                        // to switch to. Stage 2 of `SidebarVisibility.visible`
                        // already stripped every module whose declared
                        // `NavRole` this caller does not hold (Phase 568; the
                        // deprecated group-name fallback covers modules that
                        // declare none), so a plain user with no admin-area
                        // modules sees no switcher (GP 12 — server enforcement
                        // is authoritative).
                        let switcher =
                            if List.isEmpty adminModules then
                                []
                            else
                                [
                                    ({
                                        Id = Toolup.Sidebar.AdminAreaId
                                        Name = shellMsgs.AdministrationArea
                                        // Phase 610 Tidy-Up — was
                                        // `Html.none`; see the sibling
                                        // switcher above for why an empty
                                        // icon is an empty ROW at the narrow
                                        // width. The glyph is the one
                                        // `AdminHome.create` already gives
                                        // the Administration LANDING, so the
                                        // gear means administration on both
                                        // sides of the switch rather than the
                                        // metaphor changing as the user
                                        // crosses it.
                                        Icon = ToolUp.Platform.Icons.settings
                                        HasData = true
                                        Group = None
                                        Pages = []
                                        // Phase 611 — the row the defect was
                                        // observed on: the only route INTO the
                                        // administration area, which bucketed
                                        // into `_other` and so sat last and
                                        // collapsed on a fresh profile. Placed
                                        // in the trailing always-visible
                                        // section, reachable in both rail
                                        // widths with no persisted state.
                                        Placement = Some Toolup.Sidebar.TrailingSlot
                                    }
                                    : Toolup.Sidebar.SidebarModuleView)
                                ]

                        productModules, switcher

            let views: Toolup.Sidebar.SidebarModuleView list =
                areaFilteredModules
                |> List.map (fun moduleImpl ->
                    let hasData =
                        moduleImpl.NeedsData
                        |> Option.map (fun check -> check hasDataForType)
                        |> Option.defaultValue true

                    let pages: Toolup.Sidebar.SidebarPageView list =
                        match moduleImpl.PageViews, moduleImpl.Definition.Pages with
                        | None, _
                        | Some _, ([] | [ _ ]) -> []
                        | Some _, modulePages ->
                            modulePages
                            |> List.map (fun page -> {
                                Id = $"{moduleImpl.Definition.Id}{page.Route}"
                                Name = page.Title
                                Icon = page.Icon
                            })

                    // Leaf modules keep the historical icon derivation
                    // (Pages[0].Icon when set, else Definition.Icon); a
                    // multi-page parent uses the module-level Definition.Icon
                    // to represent the whole module rather than any one page.
                    let icon =
                        if List.isEmpty pages then
                            singlePageIcon moduleImpl.Definition
                        else
                            moduleImpl.Definition.Icon

                    {
                        Id = moduleImpl.Definition.Id
                        Name = moduleImpl.Definition.Name
                        Icon = icon
                        HasData = hasData
                        Group = moduleImpl.Group
                        Pages = pages
                        // Phase 611 — carried straight through from the
                        // module's own declaration (`ClientModule.withPlacement`),
                        // so this projection makes no placement decision and
                        // names no id. `None` for every module that declares
                        // nothing, which the sidebar reads as ordinary group
                        // bucketing (GP 11).
                        Placement = moduleImpl.Placement
                    })

            // Phase 767 — the sidebar's section titles come from the
            // resolved catalog, like every other shell string.
            Toolup.Sidebar.buildSections resolvedCatalog.Sidebar (views @ switcherViews) model.SidebarPrefs

        // Header team switcher — only rendered when the deployment
        // declares a `Team` surface with `Switching = HeaderSwitcher`
        // (the retiring `MultiTeam` UX intent). Single-team deployments
        // (`Switching = NoSwitcher`) don't surface a switcher. Hidden
        // until `MyTeams` has loaded so we don't render an empty
        // dropdown during the initial round-trip. Rendered when the
        // user has two-or-more memberships to switch between, OR has
        // any membership but no active team — switching from *no team*
        // to a team is the onboarding transition, and hiding the
        // switcher there left single-team members stranded in personal
        // scope with no affordance (the sole-team auto-select usually
        // beats this render, but the switcher is the visible fallback
        // if that call fails).
        let teamSwitcher =
            let switcherUseful =
                model.MyTeams.Length >= 2
                || (not model.MyTeams.IsEmpty && model.ActiveTeamId.IsNone)

            if ClientConfig.hasMultiTeamSwitcher config && switcherUseful then
                Some(TeamSwitcher model.MyTeams model.ActiveTeamId dispatch)
            else
                None

        // 0.5.7 — sign-out affordance. Rendered in the page header
        // when `ClientConfig.Composition.Handlers.SignOutHandler` is `Some _`
        // (typically wired by the OIDC / Clerk companion at compose
        // time). Mirrors the existing `OidcAuthUI.UserMenu`
        // contract; the SDK no longer requires the consumer to
        // hand-roll a header-mounted sign-out button. Consumers
        // wanting bespoke UX (confirmation modal, profile dropdown,
        // etc.) leave `SignOutHandler = None` and continue to mount
        // their own affordance via `ExtraChrome.HeaderAction` /
        // `ExtraChrome` — wiring both is harmless but renders two.
        let signOutAction =
            match config.Composition.Handlers.SignOutHandler with
            | None -> None
            | Some signOut ->
                Some(
                    Html.button [
                        prop.className [
                            "text-sm px-3 py-1.5 rounded transition-colors"
                            "border border-border text-text"
                            "hover:bg-gray-100"
                        ]
                        prop.text shellMsgs.SignOut
                        prop.onClick (fun _ -> signOut ())
                    ]
                )

        // Phase 245 — admin "show all modules" escape. With per-team
        // module exposure, a platform admin's sidebar is exposure-
        // filtered like a member's; this toggle reveals the modules the
        // active team hides (and switches back to the member view). Only
        // rendered for a platform admin whose loaded accessible set is a
        // strict subset of Managed — i.e. the active team actually hides
        // something — so it stays invisible for non-admins and when there
        // is nothing to reveal.
        let adminShowAllToggle =
            let isAdmin = model.PlatformRole = Some PlatformRole.PlatformAdmin

            let hasHidden =
                match model.AccessibleModules with
                | Some r -> List.length r.Accessible < List.length r.Managed
                | None -> false

            if isAdmin && hasHidden then
                Some(
                    Html.button [
                        prop.className [
                            "text-sm px-3 py-1.5 rounded transition-colors border"
                            if model.ShowAllModules then
                                "border-purple-300 bg-purple-50 text-purple-700 hover:bg-purple-100"
                            else
                                "border-border text-text hover:bg-gray-100"
                        ]
                        prop.title (
                            if model.ShowAllModules then
                                shellMsgs.ShowingAllModulesHint
                            else
                                shellMsgs.MemberViewHint
                        )
                        prop.text (
                            if model.ShowAllModules then
                                shellMsgs.ViewingAllModules
                            else
                                shellMsgs.ShowHiddenModules
                        )
                        prop.onClick (fun _ -> dispatch ToggleShowAllModules)
                    ]
                )
            else
                None

        // Compose the switcher with any caller-supplied HeaderAction
        // and the SDK's default sign-out button. All render side-by-side
        // in the page header.
        let combinedHeaderAction =
            let pieces =
                [ teamSwitcher; adminShowAllToggle; chrome.HeaderAction; signOutAction ]
                |> List.choose id

            match pieces with
            | [] -> None
            | [ single ] -> Some single
            | many -> Some(Html.div [ prop.className "flex items-center gap-3"; prop.children many ])

        // Phase 5e — resolve the active team's branding from the
        // prefetched `_platform` config against the composition root's
        // `ClientConfig` defaults. Blank / absent overrides fall back to
        // the defaults, so a single-tenant deployment (or a team that
        // customised nothing) renders byte-for-byte as before. Live on
        // team switch: `TeamSwitched` clears `PlatformConfig` and the
        // `ConfigsLoaded` reload repopulates it, re-running this resolve.
        // Phase 444 — resolve the active locale and the message catalog
        // the shell renders from. Three inputs in precedence order: an
        // in-session choice (`Model.LocaleOverride`, set by
        // `LocaleSwitched`), then the declared `ClientConfig.Locale`
        // resolved against the team's `_platform.locale` and the browser
        // preference. Live on team switch for the same reason branding is
        // — `TeamSwitched` clears `PlatformConfig` and `ConfigsLoaded`
        // repopulates it, re-running this resolve.
        //
        // Under the default `FixedLocale "en"` with no override, this is
        // a `match` returning a constant plus a `None` branch — no map read
        // and no browser call (GP 11 / GP 13) — and the same catalog object
        // while nothing it depends on moved (Phase 852, `resolveCatalog`).
        //
        // The same record while the config and the team's `_platform`
        // config are unchanged (`resolveBranding`), so a chrome
        // render does not re-render every branding reader.
        let resolvedBranding = resolveBranding config model.PlatformConfig

        // The sidebar renders behind its own memo boundary, on every render
        // path (`view`, `viewWithSignIn` and the sliced `Client.run` shell
        // alike): a chrome render that leaves these values alone renders no
        // sidebar. `sidebarSections` is rebuilt on every chrome render and
        // compared by value at the boundary; the callbacks are the loop's
        // own (`Dispatchers`), so they compare equal by reference.
        let sidebarDispatch = dispatchers.Sidebar

        let sidebar: Toolup.UIToolkit.Layout.SidebarProps = {
            AppName = resolvedBranding.AppName
            AppLogo = resolvedBranding.LogoUrl
            Sections = sidebarSections
            SelectedModule = selectedSidebarId
            OnModuleSelected = sidebarDispatch.Select
            OnGroupToggled = sidebarDispatch.ToggleGroup
            OnPinToggled = sidebarDispatch.TogglePin
            OnModuleToggled = sidebarDispatch.ToggleModule
            OnHideToggled = sidebarDispatch.ToggleHidden
            OnReorder = sidebarDispatch.Reorder
        }

        let shell =
            Toolup.UIToolkit.Layout.AppShell {
                Sidebar = sidebar
                Content = content
                SidePanel = chrome.SidePanel
                HeaderAction = combinedHeaderAction
                IdentityAction = Toolup.UIToolkit.Layout.headerIdentityAction ()
                InputsWidth = config.Shell.InputsPaneWidth
            }

        // Render the toast renderer alongside the shell. Fixed-positioned
        // containers sit on top of everything else via z-index; the
        // built-in `ToastCentre` subscribes to `/api/notifications` on
        // mount and pops `SystemMessage` envelopes. Apps that want a
        // bespoke renderer pass `CustomToastCentre` with their own
        // subscription; apps that don't want toasts at all pass
        // `NoToastCentre`.
        let toastCentre =
            match config.Shell.ToastCentre with
            | NoToastCentre -> Html.none
            | DefaultToastCentre -> Components.ToastCentre.ToastCentre()
            | CustomToastCentre element -> element

        // Phase 571 — the Ctrl+K command palette. `Html.none` under the
        // default `FeatureMode.Disabled`, so no element enters the tree and
        // the component's document keydown listener is never registered
        // (GP 13): a deployment that does not opt in is byte-identical
        // to pre-571.
        //
        // The entry list is `CommandPaletteNav.candidates` — literally
        // `SidebarVisibility.visible` plus a page expansion, run on the
        // same `navigationInputs` the rail used two bindings up. That is
        // the phase's safety property and it is structural, not
        // procedural: there is no palette-side filter that could fall
        // out of step with the sidebar's, because there is no
        // palette-side filter.
        let commandPalette =
            match config.Shell.CommandPalette with
            | FeatureMode.Disabled -> Html.none
            | FeatureMode.Enabled ->
                // Icons cannot live on the pure candidate (a
                // `ReactElement` is unbuildable outside Fable, which is
                // what keeps the derivation .NET-testable), so they are
                // matched back on here by sidebar id. Same derivation
                // the rail uses: a page row shows its page icon, a leaf
                // shows `singlePageIcon`.
                let icons =
                    visibleModules
                    |> List.collect (fun m ->
                        match m.PageViews, m.Definition.Pages with
                        | Some _, ((_ :: _ :: _) as pages) ->
                            pages |> List.map (fun page -> $"{m.Definition.Id}{page.Route}", page.Icon)
                        | _ -> [ m.Definition.Id, singlePageIcon m.Definition ])
                    |> Map.ofList

                // Under the Administration area a module with no group still
                // belongs to an area, and the area is often the only
                // thing distinguishing two similarly-named pages — so
                // fall back to the area name rather than showing
                // nothing. Inert under inline groups, where areas are
                // not a user-visible concept.
                let contextLabel (candidate: CommandPaletteNav.PaletteCandidate) =
                    match candidate.Group, config.Chrome.AdminArea with
                    | Some group, _ -> Some group
                    | None, ChromeFill.Off -> None
                    | None, ChromeFill.On() ->
                        modules
                        |> List.tryFind (fun m -> m.Definition.Id = candidate.ModuleId)
                        |> Option.map (fun m ->
                            match ClientConfig.effectiveArea m.Area m.Group with
                            | ModuleArea.Administration -> shellMsgs.AdministrationArea
                            | ModuleArea.Product -> shellMsgs.ProductArea)

                let toEntry
                    (candidate: CommandPaletteNav.PaletteCandidate)
                    : Components.CommandPalette.CommandPaletteEntry =
                    {
                        SidebarId = candidate.SidebarId
                        ModuleName = candidate.ModuleName
                        PageTitle = candidate.PageTitle
                        Group = contextLabel candidate
                        Icon = icons |> Map.tryFind candidate.SidebarId |> Option.defaultValue Html.none
                    }

                let entries =
                    CommandPaletteNav.candidates paletteFacts (navigationInputs config model) modules
                    |> List.map toEntry

                Components.CommandPalette.CommandPalette {
                    IsOpen = model.CommandPalette.IsOpen
                    Query = model.CommandPalette.Query
                    Highlight = model.CommandPalette.Highlight
                    Entries = entries
                    OnOpen = fun () -> dispatch CommandPaletteOpened
                    OnDismiss = fun () -> dispatch CommandPaletteDismissed
                    OnQueryChanged = CommandPaletteQueryChanged >> dispatch
                    OnHighlightMoved = CommandPaletteHighlightMoved >> dispatch
                    OnSelect = CommandPaletteSelected >> dispatch
                }

        // Phase 6g.D: render every companion-supplied global overlay.
        // Each thunk is invoked here on every shell render so its
        // hooks register correctly. Typical consumers are floating
        // banners / status indicators owned by a companion.
        let globalOverlays = config.Shell.GlobalOverlays |> List.map (fun thunk -> thunk ())

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
        //
        // Phase 910 — the same `Set` while neither input moved
        // (`declaredFlagKeys`), so a chrome render does not re-render
        // every flag reader.
        let declaredKeys = declaredFlagKeys modules model.ResolvedFlags

        // Phase 121 — standard dismissible degradation banner. Renders
        // `Html.none` when no boot load has failed (GP 13).
        let degradationBanner =
            BootDegradation.banner
                resolvedCatalog.BootDegradation
                model.Degradations
                (RetryBootLoad >> dispatch)
                (fun () -> dispatch DismissBootDegradations)

        let flagged =
            FeatureFlags.provider
                declaredKeys
                model.ResolvedFlags
                (React.Fragment([ shell; toastCentre; degradationBanner; commandPalette ] @ globalOverlays))

        // Wrap in `ProcessedDataContext.Context` provider — publishes
        // the platform-aggregated `ProcessedFileEntry` list to module
        // views via `ProcessedData.forType`.
        let withProcessedData =
            ProcessedDataContext.Context.Provider(model.ProcessedData, flagged)

        // Phase 573.C — publish the administration tiles this caller may
        // see. The filter is `AdminTiles.forOwners` over the ids of
        // `visibleModules`, i.e. over the output of the SAME
        // `SidebarVisibility.visible` fold that produced the rail (and
        // that the route guard and the command palette run) — reusing
        // the already-computed list rather than re-folding, so the two
        // cannot disagree even transiently. A tile whose owning module
        // this caller may not reach is therefore not merely hidden: it
        // is absent for exactly the reason its module is.
        //
        // The landing module reads this through `AdminTileContext`
        // rather than deriving it, because the decision needs the
        // shell's navigation inputs and a module view has none — the
        // same "shell distributes shared state" reason
        // `ProcessedDataContext` exists. Empty (and free) for every
        // deployment that contributes no tiles.
        //
        // Phase 910 — the same list while the registered tiles and the
        // visible ids are unchanged (`visibleAdminTiles`).
        let withAdminTiles =
            let tiles =
                visibleAdminTiles (visibleModules |> List.map (fun m -> m.Definition.Id))

            AdminTileContext.Context.Provider(tiles, withProcessedData)

        // Wrap in the loading-indicator provider — surfaces the configured
        // `ClientConfig.Shell.LoadingIndicator` to module views (e.g. the
        // built-in Data Manager renders it while its file list loads) via
        // the `LoadingIndicatorContext.useIndicator` hook.
        let withLoadingIndicator =
            LoadingIndicatorContext.provider config.Shell.LoadingIndicator withAdminTiles

        // Phase 1168 — the grid modules and license key reach every grid from
        // its own chunk, so the shell names them and holds no AG Grid. The
        // chunk puts them in AgGridProvider around the grid. Set here, above
        // every grid in the tree, because this is the one path both the
        // mounted shell and the prerender pass take; the write is idempotent.
        Feliz.AgGrid.configure config.Shell.GridModules

        // Phase 622 — wrap in the presence + soft-lock provider so module
        // views can read "who else is here" / "is this entity locked" via
        // the Phase 442 hooks with no deployment wiring. The active module
        // id is passed through as the peer's location descriptor, so the
        // roster shows *where* each collaborator is.
        //
        // Gated on `ClientConfig.Shell.Presence`, default `FeatureMode.Disabled`: an
        // unconfigured deployment evaluates one `match` and renders
        // `withLoadingIndicator` unchanged — no provider, no heartbeat timer, no SSE
        // subscription (GP 11 + GP 13).
        let withPresence =
            match config.Shell.Presence with
            | FeatureMode.Disabled -> withLoadingIndicator
            | FeatureMode.Enabled -> PresenceMount.mount model.ActiveModuleId withLoadingIndicator

        // Phase 5e — wrap the whole tree in the branding provider so any
        // view (shell chrome and module views alike) can read the active
        // team's `Branding` via `BrandingProvider.useBranding`. The
        // `BrandedHeader` sibling, mounted inside the provider, applies
        // the favicon + `--brand-primary` document side effects.
        let withBranding =
            BrandingProvider.provider
                resolvedBranding
                (React.Fragment [ Components.BrandedHeader.BrandedHeader(); withPresence ])

        // Phase 444 — outermost wrap: the resolved message catalog, so
        // every shell-chrome and built-in-module view reads its strings
        // through `MessageCatalogProvider.useMessages ()`. Outside the
        // branding provider rather than inside it because the catalog is
        // the more fundamental of the two (a branded header still renders
        // its own labels from the catalog) and because the resolve is
        // strictly cheaper — a `match` and, only in the non-default
        // modes, one map read.
        MessageCatalogProvider.provider resolvedCatalog withBranding

    /// The shell, or in its place the whole-app refusal once the server has
    /// refused this principal as a publication reader. The module views, the
    /// navigation and the degradation banner would only show the same refusal
    /// over and over.
    let private viewWith
        (slot: ModuleSlot)
        (config: ClientConfig)
        (modules: ErasedModule list)
        (chrome: ExtraChrome)
        (model: Model)
        (dispatchers: Dispatchers)
        =
        if model.AppAccessRefused then
            let catalog = resolveCatalog config model.LocaleOverride model.PlatformConfig

            let signOut =
                config.Composition.Handlers.SignOutHandler
                |> Option.map (fun signOut -> catalog.Shell.SignOut, signOut)

            Components.NotAuthorised.AppAccessRefusedView signOut
            |> MessageCatalogProvider.provider catalog
        else
            viewShell slot config modules chrome model dispatchers

    /// The shell's view: chrome, sidebar and the active module, built from
    /// the model it is handed. `dispatchers` is the loop's wrapper set
    /// (`Client.dispatchers` through `Program.withStableDispatch`).
    let view
        (config: ClientConfig)
        (modules: ErasedModule list)
        (chrome: ExtraChrome)
        model
        (dispatchers: Dispatchers)
        =
        viewWith ModuleSlot.Inline config modules chrome model dispatchers

    let private viewWithSignInWith
        (slot: ModuleSlot)
        (config: ClientConfig)
        modules
        chrome
        (model: Model)
        (dispatchers: Dispatchers)
        =
        let resolvedSubjectKind = ClientConfig.resolveSubjectKind model.ActiveTeamId config

        viewWith slot config modules chrome model dispatchers
        |> AuthUIProvider.gate config.Auth.AuthUI resolvedSubjectKind
        // Phase 751 — the catalog provider is mounted OUTSIDE the auth
        // gate as well as inside `view`, because the gate WRAPS the
        // shell: a signed-out visitor sees the companion's sign-in
        // screen and nothing of `view` at all. Mounted only here, the
        // provider would be the one surface a deployment's
        // `MessageCatalogOverride` could not reach — its sign-in screen
        // would stay English while every page behind it translated.
        //
        // The inner mount stays: `view` is public and outer composers
        // call it directly (see the note on `viewWithSignIn`), so removing
        // it would strand them. Two nested providers carrying the same
        // resolved value cost one context read.
        |> MessageCatalogProvider.provider (resolveCatalog config model.LocaleOverride model.PlatformConfig)

    /// Wrap the rendered shell with the auth UI handler registered
    /// for the configured `AuthUIMode`. `AnonymousKind` / `ClaimBearerKind`
    /// / `NoAuthUI` pass through unchanged; other subject kinds
    /// delegate to a companion (OidcClient, ClerkUI) that has called
    /// `AuthUIProvider.setHandlers` at load time.
    ///
    /// Public from Phase 3b.B so outer composers (`AIClientConfig.withSidePanel`
    /// + custom composition roots that build their own Elmish program over
    /// `Client.view`) can wrap the shell with the same auth gate `program`
    /// applies. Calling `Client.view` directly from an outer view bypasses
    /// the gate entirely — the OIDC sign-in screen never renders and the
    /// shell drops into a 401 storm for any unauthenticated visit.
    let viewWithSignIn (config: ClientConfig) modules chrome (model: Model) (dispatchers: Dispatchers) =
        viewWithSignInWith ModuleSlot.Inline config modules chrome model dispatchers

    /// The props of the chrome memo boundary: the model plus what the
    /// program run fixes (config, module list, chrome, the loop's dispatch,
    /// the store).
    type internal ShellChromeProps = {
        Store: ShellStore
        Config: ClientConfig
        Modules: ErasedModule list
        Chrome: ExtraChrome
        Model: Model
        Dispatchers: Dispatchers
    }

    /// The model field the chrome does not read — the active module reads
    /// it, through the store, below the chrome's boundary.
    let private hostsField = nameof Unchecked.defaultof<Model>.Hosts

    /// Equal when every model field except `Hosts` is the same
    /// object. Walks the record's own fields, so a field added to `Model`
    /// participates without an edit here.
    [<Fable.Core.Emit("(function (a, b, skip) { if (a === b) return true; var ka = Object.keys(a); if (ka.length !== Object.keys(b).length) return false; for (var i = 0; i < ka.length; i++) { var k = ka[i]; if (k !== skip && a[k] !== b[k]) return false; } return true; })($0, $1, $2)")>]
    let private sameFieldsExcept (a: Model) (b: Model) (skip: string) : bool = Fable.Core.Util.jsNative

    /// Internal so the Fable pack can pin the comparison the chrome's memo
    /// boundary runs.
    let internal chromeSliceEqual (a: Model) (b: Model) : bool = sameFieldsExcept a b hostsField

    let private shellChromeEqual (prev: obj) (next: obj) : bool =
        let p = unbox<ShellChromeProps> prev
        let n = unbox<ShellChromeProps> next

        obj.ReferenceEquals(p.Store.Source, n.Store.Source)
        && obj.ReferenceEquals(p.Config, n.Config)
        && obj.ReferenceEquals(p.Modules, n.Modules)
        && obj.ReferenceEquals(p.Chrome, n.Chrome)
        && obj.ReferenceEquals(p.Dispatchers, n.Dispatchers)
        && chromeSliceEqual p.Model n.Model

    let private renderShellChrome (props: obj) : ReactElement =
        let p = unbox<ShellChromeProps> props
        // A chrome that throws keeps the last good shell on screen and
        // reports the error as uncaught — what the whole-tree binding,
        // which builds the view outside React, always did.
        let lastGood = useRef null

        buildHoldingLastGood lastGood (fun () ->
            viewWithSignInWith (ModuleSlot.Subscribed p.Store) p.Config p.Modules p.Chrome p.Model p.Dispatchers)

    let private shellChrome: obj =
        // The comparer goes to React as a two-argument JS function: a boxed
        // curried F# function may reach it curried, and React would read the
        // returned closure as "equal".
        reactMemo (box renderShellChrome, box (System.Func<obj, obj, bool>(shellChromeEqual)))

    /// Phase 852 / 910 — the store-bound shell view: the same tree
    /// `viewWithSignIn` builds, behind two boundaries — the chrome, which
    /// re-renders only when a shell-model field other than `Hosts`
    /// (or the `chrome` record, or the `Dispatchers`) changes, and the active
    /// module, which reads its own state from the store `shell` names.
    /// Mount it on that same store (`Program.withReactStore`): the module
    /// host reads the store, so the store and the binding must be one.
    /// `Client.run` renders it over the shell's own store; a composer that
    /// wraps the shell in a model of its own renders it over its store
    /// (`Client.shellStore store _.Shell`), passing a `chrome` record and a
    /// `Dispatchers` set that keep their identity from render to render
    /// (`Program.withStableDispatch`) — a fresh one per render re-renders
    /// the chrome every time, which is correct output at the whole-tree cost.
    let viewSlicedOver
        (shell: ShellStore)
        (config: ClientConfig)
        (modules: ErasedModule list)
        (chrome: ExtraChrome)
        (model: Model)
        (dispatchers: Dispatchers)
        : ReactElement =
        let props: ShellChromeProps = {
            Store = shell
            Config = config
            Modules = modules
            Chrome = chrome
            Model = model
            Dispatchers = dispatchers
        }

        ReactLegacy.createElement (unbox<ReactElement> shellChrome, box props)

    /// Phase 852 — the store-bound shell view over the shell's own store
    /// (`Client.run`).
    let internal viewSliced
        (store: ModelStore<Model, Msg>)
        (config: ClientConfig)
        (modules: ErasedModule list)
        (chrome: ExtraChrome)
        (model: Model)
        (dispatchers: Dispatchers)
        : ReactElement =
        viewSlicedOver (shellStore store id) config modules chrome model dispatchers

    // ─── Phase 580 — client-shell module identity gate ────────────────
    //
    // `Model.Hosts` is a `Map<string, ModuleHost>` keyed by
    // `Definition.Id`, so two registered modules sharing an id collapse
    // into one entry: the second `Init` overwrites the first and both
    // modules' `update` calls thereafter run against whichever `Model`
    // won. The server has refused that shape since Phase 279
    // (`ComponentId.ensureUnique` in `ServerApp.run`); the client shell
    // had no equivalent. The gate below runs over the *composed* list
    // (`prepareModules`' output — consumer modules plus SDK built-ins,
    // exactly the list that keys `Hosts`) before the shell's
    // `Program` is constructed, so a collision is a compose-time failure
    // naming both display names rather than a silent state merge.
    //
    // The derivation + collision logic is tier-shared in
    // `ToolUp.Platform.Core` (`Shared/ModuleIdentity.fs`) so the client
    // and the server cannot drift on what a module's identity is.

    /// The composed module-identity table: one row per module carrying
    /// its declared id, display name, resolved `ComponentId`, and whether
    /// the id was derived from `Name` (the `ClientModule.create` default,
    /// spaces stripped) or explicitly declared via `ClientModule.withId`.
    ///
    /// Exposed so a consumer can audit the auto-derivation that would
    /// otherwise be invisible — pass either the app's own module list or
    /// `prepareModules config modules` for the fully-composed set.
    let moduleIdentityTable (modules: ErasedModule list) : ModuleIdentityRow list =
        modules
        |> List.map (fun m -> m.Definition.Name, m.Definition.Id)
        |> ModuleIdentity.table

    /// `moduleIdentityTable` rendered as one line per module — the
    /// human-readable derived-vs-explicit audit surface. Log it (or
    /// assert on it) from a composition root when a module's identity is
    /// in question; the same rendering is appended to the compose-time
    /// duplicate-id failure.
    let moduleIdentityReport (modules: ErasedModule list) : string =
        modules |> moduleIdentityTable |> ModuleIdentity.render

    /// Inject the SDK built-in modules around the app's own list. The
    /// shell composes from its slot table (`ShellSlots`), never from a
    /// built-in's name: the Home slot heads the list (so, with no
    /// `ActiveModule`, it is where the shell lands), the Leading rows
    /// — the data manager, the "where you add data" entry — precede the
    /// app's modules, and the Trailing rows — every administration
    /// built-in, Team Management before Platform Management — follow
    /// them, so the admin groups render near the bottom of the sidebar.
    /// Whatever fills a slot (`ClientConfig.Slots`) takes the slot's
    /// position and gate, the SDK's module and a deployment's own alike.
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
        // Phase 1074 — a companion's slots (`Slots.Extensions`) compose
        // after the app's own modules, filtered with them.
        let modules =
            modules @ ShellSlotFills.extensionModules config.Slots
            |> ModuleFilter.apply config.Composition.ModuleFilter _.Definition.Name

        // DebugOnly modules are filtered out of the sidebar unless the
        // deployment opts in via `ClientConfig.Composition.ShowDebugOnlyModules`. The
        // JS still ships — goal is hiding under-development modules, not
        // reducing bundle size.
        let modules =
            if config.Composition.ShowDebugOnlyModules then
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

        // Phase 879 — every built-in comes from the slot table. What each
        // slot holds, what admits it and the order the rows run in are
        // declared once there (`ShellSlots.fs`); this function names none
        // of them.
        let ctx: ShellSlotContext = {
            Config = config
            DataTypes = modules |> List.collect _.DataTypes
        }

        let at position = ShellSlots.modulesAt position ctx

        let composed =
            at ShellSlotPosition.Home
            @ at ShellSlotPosition.Leading
            @ workApp
            @ at ShellSlotPosition.Trailing
            @ debugApp

        // Phase 580 — refuse a composition where two modules resolve to
        // the same id before the shell keys any state by it. Runs on the
        // composed list (so a consumer module colliding with an SDK
        // built-in is caught too) and on every composition path, since
        // both `Client.program` and the companion composers that build
        // their own `Program` (ToolUp.AI's `withSidePanel`) funnel
        // through here. A composition with unique ids is unaffected —
        // same list, same order (GP 11).
        ModuleIdentity.ensureUnique "client module composition" (moduleIdentityTable composed)

        composed

    /// Phase 879 — every administration tile the shell registers: the
    /// slots' tiles, each built for whichever module fills its slot
    /// (`ShellSlots.adminTiles`), then the deployment's own
    /// (`ClientConfig.Composition.Handlers.AdminTileContributors`). A slot tile steps
    /// aside for a module that supplies its own tile, so a replacement
    /// that wires one keeps it and a replacement that does not still has
    /// the slot's. Each consumer contributor is called exactly once.
    ///
    /// Note this gate is belt AND braces: a tile is rendered only when
    /// the caller may navigate to its owning module
    /// (`AdminTiles.visible`), so a tile whose module the shell did not
    /// compose has no place on the landing page even though it counts
    /// as contributed.
    let private shellAdminTileContributor (config: ClientConfig) : IAdminTileContributor =
        { new IAdminTileContributor with
            member _.Tiles() =
                let supplied =
                    config.Composition.Handlers.AdminTileContributors
                    |> List.collect (fun contributor -> contributor.Tiles())

                ShellSlots.adminTiles config supplied @ supplied
        }

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
            match config.Auth.AuthUI with
            // Phase 494 — the vendor-neutral case validates on its
            // caller-supplied tag; the hint names the companion-export
            // convention rather than a specific vendor.
            | ProviderAuthUI(tag, _) -> Some(tag, sprintf "the companion-exported handler value for tag \"%s\"" tag)
            | OidcAuthUI _ -> Some("oidc", "ToolUp.AuthProviders.OidcRegister.handler")
            | PasskeyAuthUI _ -> Some("passkey", "ToolUp.AuthProviders.PasskeyRegister.handler")
            | NoAuthUI
            | CustomAuthUI _ -> None

        match authUITag with
        | Some(tag, companion) ->
            let hasHandler =
                config.Composition.Handlers.AuthUIHandlers
                |> List.exists (fun (k, _) -> k = tag)

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

        let authUIDupes = dupes config.Composition.Handlers.AuthUIHandlers

        if not (List.isEmpty authUIDupes) then
            failwithf
                "ClientConfig.Handlers.AuthUIHandlers contains duplicate tag(s): %A. Each tag must be unique."
                authUIDupes

        let credDupes = dupes config.Composition.Handlers.DataSourceCredentialHandlers

        if not (List.isEmpty credDupes) then
            failwithf
                "ClientConfig.Handlers.DataSourceCredentialHandlers contains duplicate Kind(s): %A. Each Kind must be unique."
                credDupes

        // Phase 217 — module-contributed Home-widget ids must be unique
        // across every contributor (the id is the React key + the
        // namespace prefix for `HomeWidgetContext.Data`).
        let widgetIdDupes =
            config.Composition.Handlers.HomeWidgetContributors
            |> List.collect (fun c -> c.Widgets() |> List.map (fun w -> (w.Id, ())))
            |> dupes

        if not (List.isEmpty widgetIdDupes) then
            failwithf
                "ClientConfig.Handlers.HomeWidgetContributors contributes duplicate widget id(s): %A. Each HomeWidget.Id must be unique across all contributors."
                widgetIdDupes

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
                config.Composition.RequestSeam.HeaderProviders
                |> List.collect (fun p ->
                    try
                        p () |> Array.toList
                    with _ -> [])
                |> List.toArray

            Array.append sdkPairs consumerPairs

    let private composeApiOriginGetter (config: ClientConfig) : unit -> string =
        fun () ->
            match config.Composition.RequestSeam.ApiOrigin() with
            | Some s -> s
            | None -> ""

    /// Browser-console attribution banner — styled `console.log` mirror
    /// of the server-side `ConsoleLogger.fromEnv` first-line emission and
    /// the SSR-side `Attribution.poweredByBadge`. A `%c`-formatted pill
    /// + URL adapts to `prefers-color-scheme` (white-on-dark pill for
    /// dark consoles, dark-on-light pill for light consoles). Emitted
    /// once, before the `[client.bootstrap] [ToolUp] composed | …`
    /// summary, so the SDK identifier is the first thing the browser
    /// console shows on app load. Defensive: no-op when `window.console`
    /// is unavailable (SSR / prerender contexts).
    ///
    /// Note: deliberately text-only — `background-image: url(...)` inside
    /// a `%c` console style works inconsistently across browsers (Chrome
    /// / Firefox / Safari each handle relative + bundler-rewritten asset
    /// URLs differently in the console-log context). The rich visual
    /// brand surface ships through the sidebar footer and the SSR
    /// `Attribution.poweredByBadge`; the dev-console banner is operator
    /// chrome that just needs to be identifiable.
    let private emitBrandedBootBanner () : unit =
        Fable.Core.JsInterop.emitJsStatement
            ()
            """(function () {
                if (typeof window === 'undefined' || !window.console || !window.console.log) return;
                var dark = !!(window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches);
                var brand = dark
                    ? 'color:#fff;background:#1a1a1a;padding:3px 10px;border-radius:4px;font-weight:bold;font-size:12px;'
                    : 'color:#1a1a1a;background:#f5f5f5;padding:3px 10px;border-radius:4px;font-weight:bold;font-size:12px;border:1px solid #e0e0e0;';
                var url = dark
                    ? 'color:#9ecbff;margin-left:8px;font-size:11px;'
                    : 'color:#0366d6;margin-left:8px;font-size:11px;';
                window.console.log('%cPowered by ToolUp-Forge%c  https://toolup-forge.io', brand, url);
            })()"""

    /// One-line structured boot summary — operators can verify
    /// composition shape without opening dev tools.
    let private bootSummary (config: ClientConfig) (modules: ErasedModule list) : string =
        let authUI =
            match config.Auth.AuthUI with
            | NoAuthUI -> "none"
            | OidcAuthUI _ -> "oidc"
            | PasskeyAuthUI _ -> "passkey"
            | CustomAuthUI _ -> "custom"
            // Phase 494 — the neutral case reports its own tag, so a
            // Clerk deployment on the neutral form still logs "clerk".
            | ProviderAuthUI(tag, _) -> tag

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

        let credKinds =
            config.Composition.Handlers.DataSourceCredentialHandlers |> List.map fst

        let credKindsStr =
            if List.isEmpty credKinds then
                "none"
            else
                String.concat "," credKinds

        let providers = config.Composition.RequestSeam.HeaderProviders |> List.length
        let bridge = if config.Auth.AuthBridge.IsSome then "yes" else "no"

        let kb =
            if config.Composition.Handlers.NarrativeCommitHandler.IsSome then
                "yes"
            else
                "no"

        // Phase 580 — the derived-vs-explicit id split, so the invisible
        // `Name`-derivation is at least *countable* from the boot line.
        // The full per-module table stays off the console by default
        // (a table per boot is noise for a deployment that never asks);
        // consumers audit it on demand via `Client.moduleIdentityReport`,
        // and the compose-time duplicate-id failure prints it in full.
        let derivedIds, explicitIds =
            moduleIdentityTable modules
            |> List.partition (fun r -> r.Origin = DerivedFromName)

        sprintf
            "[ToolUp] composed | surfaces=%s | authUI=%s | modules=%d | moduleIds=derived:%d,explicit:%d | credentialUIs=%s | extraHeaderProviders=%d | authBridge=%s | knowledgeBase=%s"
            surfacesLabel
            authUI
            (List.length modules)
            (List.length derivedIds)
            (List.length explicitIds)
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
    /// guard-installed sentinel `CsrfClient` keeps internally.
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
        UserSession.configureDevDefault config.Auth.DevDefaultUserId
        // Phase 133 — auth-token storage strategy. `ServerSetHttpOnlyCookie`
        // moves the JWT out of JS-readable storage (localStorage + JS
        // cookie) into a server-set HttpOnly cookie via
        // `POST /api/auth/session`; `ClientCookieAndLocalStorage` (default)
        // preserves the legacy behaviour.
        UserSession.configureAuthTokenStorage config.Auth.HttpOnlySession

        // Phase 13a — install the per-request guard with explicit
        // seam thunks (replaces the legacy module-load do block in
        // CsrfClient that read from setIdentityProvider / setApiOrigin
        // mutables).
        let identityGetter = composeIdentityGetter config
        let apiOriginGetter = composeApiOriginGetter config

        // 0.4.1 — per-request correlation id. Default: a fresh
        // `Guid.NewGuid().ToString("N")` per call. Consumers with an
        // existing logical trace id (an OIDC session correlation, a
        // per-tab span id) override via
        // `ClientConfig.Composition.RequestSeam.CorrelationIdProvider`. The header
        // is read by the Giraffe dispatcher's `CallContext.beginRequest`
        // per Phase 69b.D and stamped back on the response.
        let correlationGetter: unit -> string =
            match config.Composition.RequestSeam.CorrelationIdProvider with
            | Some provider -> provider
            | None -> fun () -> System.Guid.NewGuid().ToString("N")

        // `csrfEnabled` short-circuits the guard's state-changing CSRF
        // branch in anonymous-only deployments — same gate used to skip
        // the prefetch below. Without this the `notoken` console warn
        // fires on every state-changing /api call in anonymous mode
        // (server-side CsrfMiddleware exempts anonymous-admitting routes,
        // so the warn is signal-free noise).
        let csrfEnabled = ClientConfig.requiresAnyAuth config
        CsrfClient.installRequestGuard identityGetter apiOriginGetter correlationGetter csrfEnabled

        // 0.4.1 — install the SDK's standard `Cmd.OfRemoting`
        // interceptor chain (categorised-error bridge + telemetry).
        // Idempotent: re-registering the same interceptor instance
        // no-ops via the underlying registry's reference check.
        //
        // 0.4.3 — pass the same correlationGetter so the telemetry
        // interceptor stamps the wire id rather than minting its
        // own; client log id == outbound wire header == server-
        // stamped response header (modulo proxy mangling).
        RemotingInterceptors.install correlationGetter

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
        AuthUIProvider.setHandlers config.Composition.Handlers.AuthUIHandlers
        DataSourceCredentialUIRegistry.setHandlers config.Composition.Handlers.DataSourceCredentialHandlers
        // Phase 217 — collect module-contributed Home widgets once.
        HomeWidgetRegistry.setContributors config.Composition.Handlers.HomeWidgetContributors
        // Phase 573 — the same, for administration-landing tiles: the
        // shell slots' tiles first (so they lead the grid at equal
        // weight), then whatever the consumer wired (Phase 879 — one
        // contributor, so a slot tile can step aside for a module's own).
        AdminTileRegistry.setContributors [ shellAdminTileContributor config ]
        Toolup.NarrativeCommit.setHandler config.Composition.Handlers.NarrativeCommitHandler

        // Phase 13a — validate explicit composition against declared
        // modes. Fails loud naming the missing companion / import.
        validateHandlers config
        validateRequestSeam config.Composition.RequestSeam

        // Phase 55 — refuse deployments where every consumer module is
        // ungrouped. The sidebar's reserved `_other` bucket collapses
        // title-less when no other declared group is present, so the
        // operator sees an empty sidebar even though every module is
        // registered and accessible. See `ModuleGroupingValidator.fs`.
        // Phase 1074 — a companion's slot modules count with the app's own,
        // as they did when the companion appended them to the list.
        let consumerModules = modules @ ShellSlotFills.extensionModules config.Slots

        ModuleGroupingValidator.validate consumerModules

        // Phase 583 — client half of `client-server-module-parity`. When
        // the consumer declares `ClientConfig.Composition.ExpectedModules` (the same
        // list it declares on `ServerConfig.ExpectedModules`), refuse a
        // boot whose consumer module set does not match it exactly — the
        // server root refuses the mirror-image mismatch at preflight, so
        // one declaration makes both roots agree. Dormant (a single
        // `match`) when undeclared. See `ModuleParityValidator.fs`.
        ModuleParityValidator.validate config.Composition.ExpectedModules consumerModules

        // Phase 13a — install the request seam if not already installed
        // (idempotent). Covers outer composers that bypass `Client.run`
        // and call `boot` directly.
        installRequestSeam config

        // Phase 6k Workstream A. Install the optional auth bridge so
        // it can refresh JWTs into localStorage + the SSE auth cookie
        // before the first request goes out. No-op when None — the
        // existing X-User-Id / ?userId= path keeps working.
        //
        // A7 — wrap the call in try/with so a throwing bridge
        // constructor emits a structured diagnostic (via
        // `AuthDiagnostics`) AND still surfaces as a fatal SDK boot
        // error. Distinguishing a bridge defect from a generic init
        // defect was previously manual log triage; now the operator
        // grep `bridge-install-failed` in the auth tracer output.
        match config.Auth.AuthBridge with
        | Some bridge ->
            try
                UserSession.installBridge bridge
                AuthDiagnostics.emitOk None "bridge-install-ok" None
            with ex ->
                AuthDiagnostics.emitException None "bridge-install-failed" "BridgeInstallFailed" ex

                // Re-raise — a failed bridge install is a fatal SDK
                // boot error. The diagnostic emission above gives
                // operators the structured signal; the re-raise gives
                // the consumer's unhandled-error path the chance to
                // present a meaningful UI ("authentication is
                // unavailable; refresh to retry").
                reraise ()
        | None -> ()

        // Browser-console attribution banner — see `emitBrandedBootBanner`.
        // Emitted before the structured boot summary so the SDK identifier
        // is the first thing the browser console shows on app load.
        emitBrandedBootBanner ()

        // Phase 13a — boot-line summary log so operators can verify
        // composition without dev tools.
        log.Info(bootSummary config modules)

    /// The shell's boot-time background subscriptions — the
    /// NavigationRequest bus (deep links, Home tool cards, admin-landing
    /// tiles, AI navigate tools), the cross-module client event bus, the
    /// SSE notification stream, the auth-token-acquired watcher, and the
    /// auth-bridge health observer — as lifetime-aware `EffectHandle`s.
    ///
    /// `Client.program` attaches every one of these (via
    /// `withShellLifetimeEffects` below). **So must any outer composer
    /// that rebuilds the Program from the shell's `init`/`update`/`view`
    /// pieces** (e.g. ToolUp.AI's `withSidePanel`), mapping each handle
    /// into its outer Msg with `EffectHandle.map` — Elmish does not expose
    /// a built Program's internals post-construction, so a composer that
    /// stitches the shell's pieces into an outer Program starts from an
    /// EMPTY effect list. Dropping them silently severs every seam listed
    /// above: the failure mode is not an error but a bus with no
    /// subscriber (`NavigationRequest.request` is a documented no-op with
    /// zero subscribers — an admin-landing tile click simply does
    /// nothing).
    let programLifetimeEffects (config: ClientConfig) : EffectHandle<Msg> list =
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

        // Phase 444 — shell-locale switch subscription. Translates every
        // `LocaleRequest.request` into a `LocaleSwitched` dispatch, which
        // runs the `TeamSwitched`-shaped reset. Same lifetime-aware shape
        // and the same teardown contract as the navigation subscription
        // above; the seam is a mutable global for the same reason
        // `NavigationRequest` is, so a language picker in a consumer's own
        // chrome needs no dependency on the shell's `Msg` type.
        let localeRequestEffect =
            EffectHandle.programLifetime "locale-request" (fun dispatch ->
                let unsubscribe =
                    LocaleRequest.subscribe (fun locale -> dispatch (LocaleSwitched locale))

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        // Cross-module client event bus subscription. Translates every
        // `ModuleEvents.publish` into a `ModuleEventPublished` dispatch the
        // shell update fans out to subscribing modules. Same
        // lifetime-aware shape as the navigation subscription above —
        // disposed on `IDispatcher.Terminate()` / HMR.
        let moduleEventsEffect =
            EffectHandle.programLifetime "module-events" (fun dispatch ->
                let unsubscribe =
                    ModuleEvents.subscribe (fun topic payload -> dispatch (ModuleEventPublished(topic, payload)))

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        // Phase 6p — session-store reconciliation. Three jobs, one
        // lifetime: relay `SessionEpoch`'s fan-out into the shell's
        // `Msg` (so the shell's own state clears wherever the reset was
        // detected), re-check the epoch when the tab comes back to the
        // foreground, and re-check it whenever the SSE stream reopens.
        //
        // The last two are the paths that catch what the notification
        // cannot: a process restart takes the channel down with it, and
        // an eviction that happened while this tab was backgrounded may
        // have been published to a connection that was already gone.
        // Both are events that fire rarely and cost one read each; a
        // deployment that never evicts sees the read return a matching
        // epoch and dispatches nothing (GP 11).
        let sessionEpochEffect =
            EffectHandle.programLifetime "session-epoch" (fun dispatch ->
                let unsubscribeReset =
                    SessionEpoch.subscribe (fun notice -> dispatch (SessionStoreReset notice))

                let recheck () =
                    Async.StartImmediate(
                        async {
                            let! _ = SessionEpoch.check ()
                            return ()
                        }
                    )

                // `visibilitychange` rather than `focus`: it fires for the
                // tab-switch case the operator report describes (the user
                // left the tab open and came back), and unlike `focus` it
                // does not fire for every click back into the window from
                // a devtools pane or another app, which would turn a
                // once-per-return read into a per-interaction one.
                let onVisibility =
                    fun (_: Browser.Types.Event) ->
                        if SessionEpoch.documentVisible () then
                            recheck ()

                Browser.Dom.document.addEventListener ("visibilitychange", unbox onVisibility)

                let unsubscribeOpened = NotificationClient.onConnectionOpened recheck

                { new System.IDisposable with
                    member _.Dispose() =
                        unsubscribeReset ()
                        unsubscribeOpened ()
                        Browser.Dom.document.removeEventListener ("visibilitychange", unbox onVisibility)
                })

        let notificationsEffect =
            EffectHandle.programLifetime "notifications-stream" (fun dispatch ->
                let onEnvelope (envelope: NotificationEnvelope) =
                    match envelope.Notification with
                    | Notification.ModuleAction(moduleId, actionKey, payloadJson) ->
                        dispatch (ModuleActionReceived(moduleId, actionKey, payloadJson))
                    | Notification.CustomNotification(key, payloadJson) when key = SessionStoreResetKey ->
                        // Phase 6p — the authoritative arm. The server
                        // publishes this on eviction-then-recreate, so
                        // the epoch it carries needs no round trip to
                        // confirm. `applyServerNotice` adopts it and
                        // fans out once; a focus check moments later
                        // sees a match and stays silent.
                        SessionEpoch.applyServerNoticeJson payloadJson
                    | Notification.MembershipChanged payload ->
                        // Server-side bridge filters by AffectedUserId
                        // before forwarding to this connection — every
                        // event delivered here targets this user. Route
                        // by `ChangeKind` to internal Msgs that compare
                        // against `model.ActiveTeamId` (this closure
                        // can't read the model directly).
                        match payload.ChangeKind with
                        | MembershipChangeKind.Removed -> dispatch (MembershipRevoked payload.TeamId.Value)
                        | MembershipChangeKind.ActiveTeamSet -> dispatch (MembershipActiveTeamSet payload.TeamId.Value)
                        | MembershipChangeKind.Added -> dispatch (MembershipAdded payload.TeamId.Value)
                        | MembershipChangeKind.RoleChanged -> ()
                    | _ -> ()

                // 0.5.5 — gate the SSE subscribe on auth state. In an
                // auth-required deployment, opening the EventSource
                // before sign-in produces a guaranteed 401 (the server
                // can't read an Authorization header — EventSource
                // doesn't send custom headers — and the cookie isn't
                // populated yet either). The pre-0.5.5 path eagerly
                // subscribed at boot, the connection 401'd, and SSE's
                // no-retry policy kept it closed for the rest of the
                // session — so even a successful sign-in didn't restore
                // notifications. Defer the subscribe until a token is
                // present; re-subscribe on `None → Some` transitions so
                // sign-in mid-session restores the channel without
                // a page reload.
                let needsAuth = ClientConfig.requiresAnyAuth config
                let mutable currentUnsubscribe: (unit -> unit) option = None

                let subscribeNow () =
                    if currentUnsubscribe.IsNone then
                        currentUnsubscribe <- Some(NotificationClient.subscribe onEnvelope)

                let unsubscribeNow () =
                    match currentUnsubscribe with
                    | Some u ->
                        u ()
                        currentUnsubscribe <- None
                    | None -> ()

                let hasTokenNow () =
                    UserSession.getAuthToken () |> Option.isSome

                if not needsAuth || hasTokenNow () then
                    subscribeNow ()

                let stopWatcher =
                    if needsAuth then
                        UserSession.onAuthTokenChange (fun token ->
                            match token with
                            | Some _ ->
                                subscribeNow ()
                                // Phase 117 — a transition with a token present
                                // is a sign-in or a token-identity change: cycle
                                // the EventSource so the stream re-registers
                                // under the fresh identity (no-op for same-user
                                // token refreshes; also clears the give-up
                                // latch so a pre-sign-in 401 doesn't keep
                                // notifications dead after sign-in).
                                NotificationClient.reconnect ()
                            | None ->
                                unsubscribeNow ()
                                // Phase 117 — sign-out: close the stream so the
                                // previous user's subscription doesn't outlive
                                // the session (pre-117 only the shell's handler
                                // was removed; the EventSource stayed registered
                                // under the old identity and kept receiving
                                // their broadcasts).
                                NotificationClient.reset ())
                    else
                        ignore

                { new System.IDisposable with
                    member _.Dispose() =
                        stopWatcher ()
                        unsubscribeNow ()
                })

        // 0.5.5 — auth-token-acquired observer. Polls
        // `localStorage.toolup-auth-token` via
        // `UserSession.onAuthTokenChange` and dispatches
        // `AuthTokenAcquired` when it transitions `None → Some` (the
        // OIDC PKCE callback's `setAuthToken` runs, or the bridge's
        // periodic refresh delivers a first token). Pairs with the
        // auth-gate in `init`: deployments that require auth + have
        // no token at program start skip the boot-time API fetches,
        // and this effect re-fires them when sign-in completes —
        // restoring the previously-noisy 401 storm on the Welcome
        // Page to a single token-arrival event followed by a clean
        // batch of authenticated loads.
        //
        // `None → Some` is the only transition that fires the dispatch.
        // `Some → Some` (rotation / refresh) and `Some → None`
        // (sign-out) are no-ops — the shell's `TeamSwitched` /
        // `MembershipRevoked` paths and the `Cmd.OfRemoting`
        // categorised-error interceptor handle those concerns.
        let authTokenAcquiredEffect =
            EffectHandle.programLifetime "auth-token-acquired" (fun dispatch ->
                let mutable lastWasNone = UserSession.getAuthToken () |> Option.isNone

                let unsubscribe =
                    UserSession.onAuthTokenChange (fun token ->
                        match lastWasNone, token with
                        | true, Some _ ->
                            lastWasNone <- false
                            dispatch AuthTokenAcquired
                        | _, None -> lastWasNone <- true
                        | false, Some _ -> ())

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        // Phase 121 — auth-bridge health observer. `UserSession` counts
        // consecutive `GetJwt` failures and notifies once the streak
        // crosses its threshold (and again on recovery); the shell maps
        // the transition onto the boot-degradation banner so a silently-
        // expiring session is visible before the user is signed out.
        let bridgeHealthEffect =
            EffectHandle.programLifetime "auth-bridge-health" (fun dispatch ->
                let unsubscribe =
                    UserSession.onBridgeHealthChange (fun health ->
                        match health with
                        | Some message -> dispatch (BootLoadFailed(BootSources.Key.AuthBridge, message))
                        | None -> dispatch (BootLoadRecovered BootSources.Key.AuthBridge))

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        // Another tab's sign-in / sign-out changes the identity this tab's
        // requests carry; the watch routes it through the one identity-change
        // seam. A program-lifetime effect, so a hot reload disposes the
        // listener with the loop that installed it.
        let identityAcrossTabsEffect =
            EffectHandle.programLifetime "identity-across-tabs" (fun _ ->
                let unsubscribe = UserSession.watchIdentityAcrossTabs ()

                { new System.IDisposable with
                    member _.Dispose() = unsubscribe ()
                })

        // Phase 1240 — Back and Forward reach the shell as the ordinary
        // `ModuleSelected`, so the route guard, the area flip and the lazy
        // `Init` all apply. Attached only under browser history: by default the
        // shell does not listen, and this list is what it was.
        let browserHistoryEffects =
            match config.Shell.BrowserHistory with
            | NoBrowserHistory -> []
            | HashHistory -> [
                EffectHandle.programLifetime "browser-history" (fun dispatch ->
                    let unsubscribe =
                        BrowserHistory.subscribe RegisteredModules.snapshot (fun sidebarId ->
                            dispatch (ModuleSelected sidebarId))

                    { new System.IDisposable with
                        member _.Dispose() = unsubscribe ()
                    })
              ]

        [
            navigationEffect
            localeRequestEffect
            moduleEventsEffect
            sessionEpochEffect
            notificationsEffect
            authTokenAcquiredEffect
            bridgeHealthEffect
            identityAcrossTabsEffect
        ]
        @ browserHistoryEffects

    /// Attach every shell program-lifetime effect to `prog`, lifting each
    /// into the program's message type via `wrap`. `Client.program` calls
    /// this with `id`; an outer composer wrapping the shell calls it with
    /// its shell-message constructor (e.g. `ShellMsg`). One definition
    /// site, so the shell and every composer attach the same set and a
    /// new shell effect cannot be silently missing from a composed app.
    ///
    /// Phase 931 — it also captures the program's dispatcher as the
    /// shell's (lifted through `wrap`), which is what the
    /// `ClientModuleContext.OnTeamSwitched` and `OnAccessibleModulesChanged`
    /// callbacks dispatch through. The capture used to live at
    /// `Client.run`'s mount alone, so a composer that rebuilt the Program
    /// from the shell's pieces (ToolUp.AI's `AIClientConfig.run`) handed
    /// its modules two callbacks that did nothing.
    let withShellLifetimeEffects
        (config: ClientConfig)
        (wrap: Msg -> 'msg)
        (prog: Program<'arg, 'model, 'msg, 'view>)
        : Program<'arg, 'model, 'msg, 'view> =
        let captureShellDispatcher (dispatcher: IDispatcher<'msg>) =
            shellDispatcher <-
                Some {
                    SendToShell = fun msg -> dispatcher.Dispatch(wrap msg)
                    ShellIsActive = fun () -> dispatcher.IsActive
                }

        (prog, programLifetimeEffects config)
        ||> List.fold (fun p effect -> p |> Program.withEffect (EffectHandle.map wrap effect))
        |> Program.withDispatcherHandle captureShellDispatcher

    /// The program builder every shell-hosting program shares — the shell's
    /// own and any composer that embeds the shell in a model of its own.
    /// Runtime errors go to `ClientConfig.Composition.OnElmishError` (else the
    /// `client.elmish` logger), each update is traced through the
    /// `client.elmish.trace` logger when `EnableElmishConsoleTrace` is set,
    /// and the view receives the loop's wrapper set, built once per loop
    /// from its dispatcher by `build` (`Program.withStableDispatch`).
    let programOver
        (config: ClientConfig)
        (init: unit -> 'model * Cmd<'msg>)
        (update: 'msg -> 'model -> 'model * Cmd<'msg>)
        (build: Dispatch<'msg> -> 'stable)
        (view: 'model -> 'stable -> ReactElement)
        : Program<unit, 'model, 'msg, ReactElement> =
        let elmishLog = Logger.forCategory "client.elmish"

        let elmishReporter (ctx: ErrorContext) =
            match config.Composition.OnElmishError with
            | Some sink ->
                try
                    sink ctx
                with ex ->
                    // A throwing sink must not crash the reporter.
                    elmishLog.Error("OnElmishError sink raised", Some ex)
            | None -> elmishLog.Error(ctx.Message, Some ctx.Exception)

        let traceLog = Logger.forCategory "client.elmish.trace"

        // Formatted defensively: a message may carry a large payload (an
        // uploaded file's contents) and `sprintf "%A"` over multi-megabyte
        // data overflows the JS call stack. Tracing must never crash message
        // handling.
        let traceUpdate (msg: 'msg) (model: 'model) =
            let next = update msg model
            traceLog.Debug("msg=" + Program.safeMsgRepr msg)
            next

        // The view is set by `withStableDispatch` below; `mkProgram` only
        // needs a placeholder for it.
        Program.mkProgram
            init
            (if config.Composition.EnableElmishConsoleTrace then
                 traceUpdate
             else
                 update)
            (fun _ _ -> Html.none)
        |> Program.withStableDispatch build view
        |> Program.withErrorReporter elmishReporter

    /// The shell Program over a given shell view (whole-tree or sliced).
    let private programWith
        (shellView: ClientConfig -> ErasedModule list -> ExtraChrome -> Model -> Dispatchers -> ReactElement)
        (config: ClientConfig)
        (modules: ErasedModule list)
        : Program<unit, Model, Msg, ReactElement> =
        boot config modules

        let allModules = prepareModules config modules
        let queryBus = buildQueryBus allModules

        // AuthUI gating is driven by ClientConfig.Auth.AuthUI — default
        // NoAuthUI is pass-through, so DEBUG and RELEASE behave the
        // same unless the app opts into a sign-in mode.
        programOver
            config
            (init config queryBus allModules)
            (update config queryBus allModules)
            dispatchers
            (fun model stable -> shellView config allModules emptyChrome model stable)
        |> withModuleStateObserver allModules Some id
        |> withShellLifetimeEffects config id

    /// Build the shell Elmish Program without running it. Outer composition
    /// (e.g. AIClientConfig.withAIAssistant) consumes this to layer
    /// additional state, messages, subscriptions, and view chrome on top.
    /// Its view is the whole-tree `viewWithSignIn`, which any React binding
    /// renders; `run` mounts the store-bound sliced shell instead.
    let program (config: ClientConfig) (modules: ErasedModule list) : Program<unit, Model, Msg, ReactElement> =
        programWith viewWithSignIn config modules

    /// Returns `true` if a registered `PublicEntryDispatchers` short-circuits
    /// the full shell bootstrap (the dispatcher has rendered its own program).
    /// Returns `false` otherwise — the caller should bootstrap the full shell.
    ///
    /// 0.4.1 — PublicEntryDispatcher implementors should compose
    /// `Program.withDispatcherHandle (fun _ -> ())` against their
    /// minimal program (or accept the React adapter's auto-installed
    /// dispatch sink that fires on `beforeunload`) so any background
    /// callback they grow gets the same `IDispatcher.IsActive` /
    /// `Terminate()` lifecycle guard the full shell enjoys. The
    /// `IDispatcher` dispatched here is per-dispatcher-program — the
    /// shell's `shellDispatcher` does not bleed across.
    let tryDispatchPublicEntry (config: ClientConfig) : bool =
        config.Composition.PublicEntryDispatchers
        |> List.exists (fun dispatch -> dispatch config)

    /// Phase 852 / 910 — mount the store-bound shell at `"elmish-app"`: the
    /// chrome and the active module are separate boundaries over one model
    /// store, so a message that changes only the active module's state
    /// re-renders that module and no chrome. One store per run; the view's
    /// module host and the binding read the same one. `hydrate` adopts a
    /// server-rendered tree (`hydrateRoot`) instead of rendering from
    /// scratch — `Bootstrap.Hydration.run`'s prerendered branch.
    let internal runStoreBound (hydrate: bool) (config: ClientConfig) (modules: ErasedModule list) =
        let store = ModelStore.create<Model, Msg> ()

        let bind =
            if hydrate then
                Program.withReactStoreHydrate store "elmish-app"
            else
                Program.withReactStore store "elmish-app"

        // The shell dispatcher is captured by `withShellLifetimeEffects`,
        // which `programWith` attaches (Phase 931).
        programWith (viewSliced store) config modules |> bind |> Program.run

    /// Run the client application with the given modules. Convenience entry
    /// point — builds the shell Program and starts React. Applications that
    /// layer a companion wrapper (e.g. ToolUp.AI's withAIAssistant) should
    /// call `program` directly, wrap, then run.
    ///
    /// Consults `ClientConfig.Composition.PublicEntryDispatchers` first; any dispatcher
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
            runStoreBound false config modules