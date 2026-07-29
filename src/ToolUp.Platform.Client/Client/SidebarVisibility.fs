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
//
// **DEPRECATED as a GATE since Phase 568** (still current as the Phase
// 567 area derivation and as stage 4's no-team admin escape). A module
// declares its gate with `ClientModule.withNavRole` now; the group-name
// match below survives only as the fallback for consumer modules built
// against `withGroup "Platform Admin"` before the typed field existed.
// Removal is deferred to the next major (GP 11) — see `effectiveNavRole`.

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

/// The four per-module facts the visibility fold reads. Everything else
/// on a registered module (views, init/update, data types, …) is
/// irrelevant to whether it appears in the sidebar, so the fold is stated
/// over this projection rather than over `ErasedModule` — which also
/// keeps it declared ahead of `SDK.ClientTypes.fs` and .NET-testable.
type SidebarModuleFacts = {
    /// `ModuleDefinition.Id` — the stable permission key the RBAC
    /// response and the no-team landing target are both expressed in
    /// (never the display `Name`).
    Id: string
    /// `ErasedModule.Group` — the `withGroup` label. Since Phase 568 it
    /// is read by the no-team admin escape (stage 4) and by the
    /// DEPRECATED group-name fallback in `effectiveNavRole`; the gate
    /// itself is `NavRole` below.
    Group: string option
    /// `ErasedModule.NavRole` — the Phase 568 typed navigation-role
    /// gate. `None` (the default, and every module that predates 568)
    /// falls back to the group-name match, which for a non-admin group
    /// is "ungated" — byte-identical to pre-568 (GP 11).
    NavRole: NavRole option
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
    /// `NavRole.PlatformAdminOnly` (and, composing, `TeamOwnerAdmin`),
    /// the `ShowAllModules` escape, and the no-team admin carve-out.
    PlatformRole: PlatformRole option
    /// Phase 568 — `model.ActiveTeamRole`: the caller's `TeamRole` on
    /// the ACTIVE team, loaded once at boot and re-read on
    /// `TeamSwitched`. `None` means "not known" — no active team, a
    /// deployment with no team scope, or the load still in flight — and
    /// `NavRole.TeamOwnerAdmin` deliberately fails OPEN against it (see
    /// that case's doc-comment).
    ActiveTeamRole: TeamRole option
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
    ActiveTeamRole = None
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

/// The gate a module is actually subject to (Phase 568).
///
/// A declared `NavRole` wins outright — that is the whole point of the
/// phase: the gate is data on the module, so renaming its sidebar group
/// changes nothing.
///
/// **The `None` arm is the DEPRECATED group-name fallback** kept for
/// consumer modules written against the pre-568 convention
/// (`withGroup "Platform Admin"`, the label the Phase 4b gate has
/// recognised since commit 4f.2). It maps a platform-scoped group — and
/// ONLY a platform-scoped group — onto `PlatformAdminOnly`, so an
/// undeclared module's gate is byte-identical to pre-568 (GP 11):
/// "Team Management" was ungated then and stays ungated under the
/// fallback, because inferring `TeamOwnerAdmin` from a group label would
/// hide entries from Members that a consumer never asked to gate.
///
/// Removal is deferred to the next major. Migration is one line per
/// module: `|> ClientModule.withNavRole NavRole.PlatformAdminOnly`.
let private effectiveNavRole (f: SidebarModuleFacts) : NavRole option =
    match f.NavRole with
    | Some declared -> Some declared
    | None ->
        if isPlatformAdminSidebarGroup f.Group then
            Some NavRole.PlatformAdminOnly
        else
            None

/// Does this caller clear the given gate? See the `NavRole` cases for
/// the reasoning behind each answer — in particular why `TeamOwnerAdmin`
/// admits an UNKNOWN active-team role.
let private admits (inputs: SidebarVisibilityInputs) (role: NavRole) : bool =
    match role with
    | NavRole.PlatformAdminOnly -> isPlatformAdmin inputs
    | NavRole.TeamOwnerAdmin ->
        isPlatformAdmin inputs
        || (match inputs.ActiveTeamRole with
            | Some TeamRole.Owner
            | Some TeamRole.Admin -> true
            | Some TeamRole.Member -> false
            | None -> true)

/// Stage 2 — the navigation-role gate (Phase 4b, typed by Phase 568).
///
/// Hides a module whose declared `NavRole` the caller does not hold.
/// Distinct from stage 1's per-module `Managed` / `Accessible` filter —
/// that targets app-domain modules; this targets the SDK's admin
/// built-ins, whose `_sdk.` Ids are absent from `Managed` and so bypass
/// RBAC by design.
///
/// Until Phase 568 the gate WAS the group label: "any module in the
/// platform-scoped group set requires `PlatformRole.PlatformAdmin`". That
/// coupling of a presentational name to an access decision drifted once
/// already (the filter matched `"Platform Admin"`; every built-in
/// declared `"Platform Management"`), so the gate is now a typed
/// declaration and the group match survives only as
/// `effectiveNavRole`'s documented fallback.
///
/// What this stage newly CAN express: "Team Management" was previously
/// ungated here for a structural reason — the shell model carried no TEAM
/// role, so the fold could not tell an Owner from a Member, and
/// blanket-hiding on `PlatformRole` would have stripped team Owners of
/// their own tools. Phase 568.B carries `ActiveTeamRole` on the model, so
/// `NavRole.TeamOwnerAdmin` states that gate properly. GP 12 is unchanged
/// throughout: the server-side Owner/Admin guards are the enforcement.
let private navRoleFiltered (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) =
    modules
    |> List.filter (fun m ->
        match effectiveNavRole (facts m) with
        | Some role -> admits inputs role
        | None -> true)

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
/// 2. **The navigation-role gate** — the SDK's admin built-ins carry
///    `_sdk.` ids that are absent from `Managed` and therefore bypass
///    stage 1 by design; this stage is what gates them. It must run after
///    stage 1 so the two gates COMPOSE (pass RBAC **and**, if the module
///    declares a `NavRole`, hold that role) rather than the role gate
///    re-admitting something RBAC removed.
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
    |> navRoleFiltered facts inputs
    |> kindVisible facts inputs
    |> noActiveTeamCollapsed facts inputs

/// `visible` projected to the visible module ids, in sidebar order. The
/// shape the contract pack asserts on, and the cheapest thing to log when
/// diagnosing "why can't I see my module".
let visibleIds (facts: 'm -> SidebarModuleFacts) (inputs: SidebarVisibilityInputs) (modules: 'm list) : string list =
    visible facts inputs modules |> List.map (fun m -> (facts m).Id)