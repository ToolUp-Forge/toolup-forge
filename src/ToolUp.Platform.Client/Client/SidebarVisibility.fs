// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SidebarVisibility

// ─── Phase 570 — the sidebar visibility fold, lifted out of the shell ─
//
// The shell's sidebar used to be produced by four filters written inline
// in `SDK.Client.fs`'s `sidebarSections` let-binding. They drifted from
// their own docstrings undetected — the Phase 4b role gate matched a
// group label no SDK built-in declared, so every authenticated caller saw
// the admin rail (fixed at commit 4f.2). Prose beside a `List.filter` is
// not a contract; this module is the contract, and
// `ToolUp.Platform.Tests/InProcess/SidebarVisibilityContractTests.fs` is
// the matrix that pins it.
//
// **Why this file and not `SDK.Client.fs`.** The fold has to be callable
// from the .NET test harness, and nothing inside `module Client` is:
// its top-level values build ToolUp.Remoting proxies, and `ClientConfig`'s
// own module initialiser reaches `AgGridModuleConfig.community`, whose
// Fable `import` binding throws "You've hit dummy code used for Fable
// bindings" outside a Fable compilation. So this module sits AHEAD of
// `SDK.ClientTypes.fs` (same position + same reason as
// `NoActiveTeamLanding.fs`), depends only on Core types, and takes the
// narrow derived values rather than a whole `ClientConfig` — the same
// narrowing `ModuleParityValidator` documents beside it.
//
// **Why a projection instead of `ErasedModule`.** `ErasedModule` is
// declared in `SDK.ClientTypes.fs`, which compiles after this file. The
// fold reads exactly three per-module facts, so it is generic over the
// module representation and takes a `'m -> SidebarModuleFacts`
// projection. The shell passes the `ErasedModule` projection at its one
// call site; the contract pack passes `id` over hand-built facts.

// ─── Admin sidebar groups (Phase 4b / commit 4f.2) ────────────────────
//
// Canonical home for the two group sets since Phase 570 — `ClientConfig`
// re-exports both predicates unchanged so every existing call site (and
// the Fable-tier `SidebarAdminGroupGateTests` pack) is untouched.

/// Platform-scoped admin sidebar groups — visible ONLY to callers
/// holding `PlatformRole.PlatformAdmin`. Mirrors the SDK built-ins'
/// `withGroup` labels (PlatformAdminUI / HealthMonitorUI /
/// ServiceStatusBoardUI / DataSubjectRequestAdminUI /
/// TenantLifecycleAdminUI / PlatformUsersUI → "Platform Management")
/// plus the consumer-convention "Platform Admin" group that the
/// Phase 4b role-gate has recognised since commit 4f.2.
let private platformAdminSidebarGroups: Set<string> =
    Set.ofList [ "Platform Admin"; "Platform Management" ]

/// Team-scoped admin sidebar group(s) — the management surfaces a
/// team Owner/Admin legitimately uses (TeamManagerUI /
/// PermissionsAdminUI / DataIngestionUI / TeamConfigUI /
/// WebhookAdminUI / UsageDashboard → "Team Management"). NOT gated
/// on `PlatformRole` — hiding these from non-platform-admins would
/// strip team Owners of their own management tools.
let private teamAdminSidebarGroups: Set<string> = Set.ofList [ "Team Management" ]

/// Union of the platform-scoped and team-scoped admin groups — the
/// set kept visible for a `PlatformRole.PlatformAdmin` caller under
/// the no-active-team landing gate, so a team-less admin still reaches
/// the team-assignment tools (Team Manager lives in "Team Management").
/// A module with no group is never in the admin set.
let private adminSidebarGroups: Set<string> =
    Set.union platformAdminSidebarGroups teamAdminSidebarGroups

/// True iff the (optional) sidebar group is platform-scoped — i.e.
/// its entries should render only for `PlatformRole.PlatformAdmin`
/// callers. This is the predicate behind stage 2 of the fold below
/// (Phase 4b, commit 4f.2). Team-scoped groups ("Team Management")
/// return `false`: their visibility is a team-role concern, not a
/// platform-role one.
let isPlatformAdminSidebarGroup (group: string option) : bool =
    match group with
    | Some g -> platformAdminSidebarGroups.Contains g
    | None -> false

/// True iff the (optional) sidebar group is an admin / management
/// group (platform- OR team-scoped) kept visible to a team-less
/// platform admin under stage 4's no-team collapse.
let isAdminSidebarGroup (group: string option) : bool =
    match group with
    | Some g -> adminSidebarGroups.Contains g
    | None -> false

// ─── Inputs ───────────────────────────────────────────────────────────

/// The three per-module facts the visibility fold reads. Everything else
/// on a registered module (views, init/update, data types, …) is
/// irrelevant to whether it appears in the sidebar, so the fold is stated
/// over this projection rather than over `ErasedModule` — which also
/// keeps it declared ahead of `SDK.ClientTypes.fs` and .NET-testable.
type SidebarModuleFacts = {
    /// `ModuleDefinition.Id` — the stable permission key the RBAC
    /// response and the no-team landing target are both expressed in
    /// (never the display `Name`).
    Id: string
    /// `ErasedModule.Group` — the `withGroup` label the Phase 4b role
    /// gate and the no-team admin escape both key off.
    Group: string option
    /// `ErasedModule.Visibility` — the Phase 66 Stream B.3 per-module
    /// predicate over the resolved `SubjectKind`. Defaults to
    /// `Visibility.visibleToAll` for modules that declare nothing.
    Visibility: SubjectKind -> bool
}

/// The shell-model + config slice the visibility fold reads. Carries the
/// *derived* config projections rather than a `ClientConfig` (which
/// cannot be constructed under .NET at all — see the file header), so the
/// three `ClientConfig` reads happen once at the shell's call site and
/// this decision stays a pure function of plain data.
type SidebarVisibilityInputs = {
    /// `model.AccessibleModules` — the server's RBAC response, `None`
    /// until the boot-time fetch resolves.
    Accessibility: AccessibleModulesResponse option
    /// `model.PlatformRole` — `Some PlatformRole.PlatformAdmin` unlocks
    /// the admin group gate, the `ShowAllModules` escape, and the
    /// no-team admin carve-out.
    PlatformRole: PlatformRole option
    /// `model.ShowAllModules` — the Phase 245 admin "show all modules"
    /// toggle.
    ShowAllModules: bool
    /// `ClientConfig.resolveSubjectKind model.ActiveTeamId config` — the
    /// Phase 66 Stream B.8 projection of the declared surfaces plus the
    /// active team scope.
    SubjectKind: SubjectKind
    /// `ClientConfig.hasTeamScope config` — true iff any declared
    /// surface carries a team scope.
    HasTeamScope: bool
    /// `model.ActiveTeamId`.
    ActiveTeamId: string option
    /// `ClientConfig.effectiveNoActiveTeamLandingId config` — the module
    /// id the no-team collapse targets, `None` when the deployment
    /// opted into neither landing path (the gate is then inert).
    NoActiveTeamLandingId: string option
}

/// The inert shape: no RBAC response loaded, no platform role, no team
/// scope, no landing gate. Stages 1, 2 and 4 are unconditional no-ops
/// against it and stage 3 admits every module that declares no
/// `Visibility`, so `visible defaults` returns a pre-B.3-shaped module
/// list unchanged — the baseline callers customise with record-update
/// syntax, and the fixture the contract pack builds its matrix from.
let defaults: SidebarVisibilityInputs = {
    Accessibility = None
    PlatformRole = None
    ShowAllModules = false
    SubjectKind = AnonymousKind
    HasTeamScope = false
    ActiveTeamId = None
    NoActiveTeamLandingId = None
}

// ─── The fold ─────────────────────────────────────────────────────────

let private isPlatformAdmin (inputs: SidebarVisibilityInputs) : bool =
    inputs.PlatformRole = Some PlatformRole.PlatformAdmin

/// Stage 1 — RBAC accessibility.
///
/// Filters by the server's accessible-modules list once it has loaded.
/// Not a security boundary — the server's per-module permission guard is
/// the enforcement (GP 12). Pre-load (`None`): show everything so the
/// sidebar doesn't flicker empty while the fetch is in flight.
///
/// The filter operates on module Id (the stable permission key), not Name
/// (display). Modules whose Id is in `Managed` but not in `Accessible`
/// are hidden. Modules whose Id is NOT in `Managed` — SDK built-ins and
/// debug-only modules — bypass the filter and stay visible regardless of
/// RBAC config. Phase 245's admin `ShowAllModules` escape short-circuits
/// the whole stage for a platform admin, revealing every managed module
/// including the ones the active team hides.
let private rbacFiltered (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) =
    match inputs.Accessibility with
    | Some _ when isPlatformAdmin inputs && inputs.ShowAllModules -> modules
    | Some response ->
        let managed = Set.ofList response.Managed
        let accessible = Set.ofList response.Accessible

        modules
        |> List.filter (fun m ->
            let id = (facts m).Id
            not (managed.Contains id) || accessible.Contains id)
    | None -> modules

/// Stage 2 — the platform-admin role gate (Phase 4b).
///
/// Hides the platform-scoped admin sidebar groups from callers without
/// `PlatformRole.PlatformAdmin`. Group label is the contract: any module
/// declaring `withGroup` with one of those labels is gated by the role.
/// Distinct from stage 1's per-module `Managed` / `Accessible` filter —
/// that targets app-domain modules; this targets SDK built-in admin
/// modules whose Ids start with `_sdk.` and so bypass RBAC by design.
///
/// "Team Management" is deliberately NOT gated here: its write surfaces
/// are for team Owners/Admins, a TEAM role the shell model does not carry
/// (`TeamInfo` has no role field; TeamManagerUI derives the caller's role
/// module-internally via `GetTeamMembers`). Blanket-hiding the group on
/// `PlatformRole` would strip team Owners of Team Manager / Permissions
/// admin, so it stays visible to every authenticated caller and the
/// server-side Owner/Admin guards remain the enforcement (GP 12).
let private adminGroupFiltered (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) =
    let isAdmin = isPlatformAdmin inputs

    modules
    |> List.filter (fun m -> isAdmin || not (isPlatformAdminSidebarGroup (facts m).Group))

/// Stage 3 — the per-module `Visibility` gate (Phase 66 Stream B.3).
///
/// Applies each module's own predicate to the resolved `SubjectKind`.
/// Structurally replaces the deployment-wide sidebar blanket-hide Phase
/// 55 introduced as a partial fix: modules own their per-subject
/// visibility, so an Anonymous-mode deployment shows the
/// (visible-to-anonymous) modules the module author intended rather than
/// the empty-sidebar failure mode that motivated the `Mode = Individual`
/// workaround. Default `Visibility.visibleToAll` is byte-identical to
/// pre-B.3 (GP 11). GP 12 — UI shape only; server-side
/// `SurfaceEnforcementMiddleware` is the authoritative gate.
let private kindVisible (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) =
    modules |> List.filter (fun m -> (facts m).Visibility inputs.SubjectKind)

/// Stage 4 — the no-active-team landing collapse.
///
/// Opt-in via `ClientConfig.NoActiveTeamLandingModuleId` (a custom
/// module) or `ClientConfig.NoActiveTeamLanding` (the SDK built-in
/// landing); `effectiveNoActiveTeamLandingId` unifies both into
/// `NoActiveTeamLandingId`. When the deployment declares a `Team` surface
/// and the caller has no active team — the post-sign-in / pre-team-pick
/// window — the sidebar collapses to just the named landing module, so a
/// freshly-signed-in user sees only the "you have no team yet" surface.
/// Platform admins additionally keep their admin / management groups
/// (`isAdminSidebarGroup`, the union — including team-scoped "Team
/// Management") so they can reach the team-assignment tools and unblock
/// themselves.
///
/// Inert once an active team upgrades the subject, on non-team surfaces,
/// and for every deployment that leaves both fields `None` (GP 13). The
/// header team switcher is rendered separately, so a multi-team member
/// with an unpicked team still gets the affordance to select one.
/// GP 12 — UI shape only; the server's `[<TenantScoped>]` gate is
/// authoritative.
let private noActiveTeamCollapsed
    (facts: 'm -> SidebarModuleFacts)
    (inputs: SidebarVisibilityInputs)
    (modules: 'm list)
    =
    match inputs.NoActiveTeamLandingId with
    | Some landingId when inputs.HasTeamScope && inputs.ActiveTeamId.IsNone ->
        let isAdmin = isPlatformAdmin inputs

        modules
        |> List.filter (fun m ->
            let f = facts m
            f.Id = landingId || (isAdmin && isAdminSidebarGroup f.Group))
    | _ -> modules

/// **The canonical sidebar visibility decision** (Phase 570.A) — the one
/// definition site for which registered modules a caller may see. Pure:
/// no model mutation, no React, no remoting; a function of the module
/// list, the caller's subject/role, the RBAC response, the active-team
/// state, and the three derived `ClientConfig` projections.
///
/// **Filter order is load-bearing, and this is why (570.C).** The stages
/// compose narrowest-authority-first, each one only ever removing:
///
/// 1. **RBAC accessibility** — the server's own answer to "may this
///    caller reach this module at all". It runs first so that every later
///    stage reasons about an already-authorised set, and so that Phase
///    245 per-team exposure (folded into the same response server-side)
///    has applied before anything else looks at the list.
/// 2. **The platform-admin role gate** — the SDK's admin built-ins carry
///    `_sdk.` ids that are absent from `Managed` and therefore bypass
///    stage 1 by design; this stage is what gates them. It must run after
///    stage 1 so the two gates COMPOSE (pass RBAC **and**, if the module
///    sits in a platform-scoped group, hold the role) rather than the
///    role gate re-admitting something RBAC removed.
/// 3. **The per-module `Visibility` predicate** — module-author intent
///    over the resolved `SubjectKind`. Deployment-level authority
///    (stages 1–2) outranks module-level intent, so it runs after both:
///    a module may hide itself from anonymous callers, never reveal
///    itself to a caller the deployment already excluded.
/// 4. **The no-active-team landing collapse** — a whole-sidebar override
///    for the pre-team-pick window, so it runs LAST over whatever
///    survived: it collapses to the landing module (plus the admin
///    escape) rather than expanding, and running it earlier would let a
///    later stage re-widen the collapsed set.
///
/// Downstream of this function, Phase 567's `SeparateArea` split
/// re-buckets the result into product / administration areas. That is a
/// partition, not a fifth filter — it never re-admits a module this
/// function removed, which is why the admin partition can only ever
/// contain modules the caller may already see.
///
/// Every stage is a no-op against `defaults`, so a deployment that
/// declares nothing gets its input list back unchanged (GP 11).
let visible (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) : 'm list =
    modules
    |> rbacFiltered facts inputs
    |> adminGroupFiltered facts inputs
    |> kindVisible facts inputs
    |> noActiveTeamCollapsed facts inputs

/// `visible` projected to the visible module ids, in sidebar order. The
/// shape the contract pack asserts on, and the cheapest thing to log when
/// diagnosing "why can't I see my module".
let visibleIds (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) : string list =
    visible facts inputs modules |> List.map (fun m -> (facts m).Id)