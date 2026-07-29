// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SidebarVisibilityContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 570 — sidebar visibility matrix contract pack ─────────────
//
// The sidebar is produced by four composed filters — RBAC accessibility,
// the Phase 4b platform-admin role gate, the Phase 66 B.3 per-module
// `Visibility` predicate, and the no-active-team landing collapse. They
// used to live inline in `SDK.Client.fs`'s `sidebarSections` binding with
// their contracts stated only in prose beside each `List.filter`, and the
// prose drifted from the code undetected: the role gate matched a group
// label no SDK built-in declared, so every authenticated caller saw the
// admin rail until commit 4f.2. Phase 570.A lifted the fold into the pure
// `SidebarVisibility.visible`; this is the table that pins it.
//
// The matrix spans SUBJECT (anonymous / authenticated Member / team
// Owner-Admin / platform admin) × MODE (Anonymous / Individual / Team
// surfaces, the last split by whether an active team is picked) ×
// EXPOSURE (Phase 245 exposed / hidden, plus the pre-load `None`), and
// asserts the exact visible-module-id LIST — order included, since the
// fold only ever filters. A failure prints the missing and unexpected
// ids, not a boolean.
//
// **How the mode axis is expressed.** The fold takes the resolved
// `SubjectKind` + `hasTeamScope` + landing id rather than a
// `ClientConfig`, because `ClientConfig` cannot be constructed under .NET
// at all — its module initialiser reaches `AgGridModuleConfig.community`,
// whose Fable `import` binding throws "You've hit dummy code used for
// Fable bindings" (the same constraint `ModuleParityValidator` documents).
// So each mode below states the `(SubjectKind, HasTeamScope)` pair that
// `ClientConfig.resolveSubjectKind` / `ClientConfig.hasTeamScope` produce
// for that surface list; the surfaces → `SubjectKind` derivation itself is
// Phase 66 B.8's and is pinned Fable-side.
//
// **The Owner/Admin row is deliberately identical to the Member row.**
// The shell model carries no TEAM role (`TeamInfo` has no role field), so
// the fold cannot distinguish a team Owner from a team Member and the
// "Team Management" group is deliberately ungated — hiding it on
// `PlatformRole` would strip team Owners of their own management tools.
// Server-side Owner/Admin guards are the enforcement (GP 12). Pinning the
// two rows as equal makes that a stated contract rather than an omission.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private facts
    (id: string)
    (group: string option)
    (visibility: SubjectKind -> bool)
    : SidebarVisibility.SidebarModuleFacts =
    {
        Id = id
        Group = group
        Visibility = visibility
    }

// The registered set, in the order `prepareModules` composes it: the
// consumer's own modules first, then the SDK built-ins. Ids, groups and
// `Visibility` declarations mirror the shipped built-ins
// (`FileManagerUI` / `TeamManagerUI` / `UsageDashboard` / `PlatformAdminUI`
// / `HealthMonitorUI`) — the built-ins themselves cannot be constructed
// here (their `create` bodies touch `Icons`, Fable `importDefault` dummy
// code that throws under .NET; that is why
// `BuiltInModuleSurfaceTests.visibilityTests` is a Fable-tier pack), so
// their three visibility-relevant facts are restated as fixtures.
let private sales = facts "Sales" None Visibility.visibleToAll
let private marketing = facts "Marketing" None Visibility.visibleToAll

let private dataManager =
    facts "_sdk.DataManager" (Some "Data Management") Visibility.visibleToAuthenticated

let private teamManager =
    facts "_sdk.TeamManager" (Some "Team Management") Visibility.visibleToAuthenticated

let private usageDashboard =
    facts "_sdk.UsageDashboard" (Some "Team Management") Visibility.visibleToAuthenticated

let private platformAdmin =
    facts "_sdk.PlatformAdmin" (Some "Platform Management") Visibility.visibleToAuthenticated

let private healthMonitor =
    facts "_sdk.HealthMonitor" (Some "Platform Management") Visibility.visibleToAuthenticated

/// The SDK built-in no-active-team landing (`NoActiveTeamLandingUI`):
/// grouped by its configured label, visible to `UserKind` only — the
/// pre-team-pick window it exists for.
let private awaitingTeam =
    facts NoActiveTeamLanding.moduleId (Some "Welcome") (Visibility.visibleTo [ UserKind ])

let private registered = [
    sales
    marketing
    dataManager
    teamManager
    usageDashboard
    platformAdmin
    healthMonitor
]

/// The same deployment with the landing module injected — what
/// `prepareModules` composes once `NoActiveTeamLanding` is configured.
let private registeredWithLanding = registered @ [ awaitingTeam ]

// RBAC manages only the consumer's modules; `_sdk.*` ids are reserved
// precisely so they never appear in `ServerConfig.ModuleNames` and so
// bypass stage 1 by design.
let private managed = [ "Sales"; "Marketing" ]

/// Phase 245 exposure — every managed module exposed to this team.
let private exposed: AccessibleModulesResponse = {
    Managed = managed
    Accessible = managed
}

/// Phase 245 exposure — "Marketing" hidden from this team.
let private hidden: AccessibleModulesResponse = {
    Managed = managed
    Accessible = [ "Sales" ]
}

// ─── Axes ─────────────────────────────────────────────────────────────

/// Mode axis — the `(SubjectKind, HasTeamScope, ActiveTeamId)` triple each
/// deployment shape resolves to (Phase 66 Stream B.8).
let private anonymousMode (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = AnonymousKind
        HasTeamScope = false
        ActiveTeamId = None
}

let private individualMode (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = UserKind
        HasTeamScope = false
        ActiveTeamId = None
}

/// `Surfaces.team` / `Surfaces.multiTeam` with an active team picked.
let private teamMode (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = TeamMemberKind
        HasTeamScope = true
        ActiveTeamId = Some "team-a"
}

/// The post-sign-in / pre-team-pick window: a team surface whose caller
/// has no active team still resolves as `UserKind`.
let private teamModeNoActiveTeam (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = UserKind
        HasTeamScope = true
        ActiveTeamId = None
}

/// Subject axis — role + exposure. `member'` and `teamOwnerAdmin` are the
/// same value by construction: see the header note.
let private anonymousSubject (response: AccessibleModulesResponse option) = {
    SidebarVisibility.defaults with
        Accessibility = response
        PlatformRole = None
}

let private member' (response: AccessibleModulesResponse option) = anonymousSubject response

let private teamOwnerAdmin (response: AccessibleModulesResponse option) = anonymousSubject response

let private platformAdminSubject (response: AccessibleModulesResponse option) = {
    SidebarVisibility.defaults with
        Accessibility = response
        PlatformRole = Some PlatformRole.PlatformAdmin
}

// ─── Matrix runner ────────────────────────────────────────────────────

type private Case = {
    /// "<subject> × <mode> × <exposure>" — the coordinate that failed.
    Coordinate: string
    Modules: SidebarVisibility.SidebarModuleFacts list
    Inputs: SidebarVisibility.SidebarVisibilityInputs
    Expected: string list
}

let private enumerate (ids: string list) =
    match ids with
    | [] -> "(empty sidebar)"
    | _ -> ids |> List.map (sprintf "'%s'") |> String.concat ", "

/// Runs one matrix cell and, on a mismatch, names the ids that differ in
/// each direction before printing the two full lists — so a broken filter
/// reads as "'_sdk.PlatformAdmin' appeared" rather than "expected true".
let private check (case: Case) =
    let actual = SidebarVisibility.visibleIds id case.Inputs case.Modules

    if actual <> case.Expected then
        let actualSet = Set.ofList actual
        let expectedSet = Set.ofList case.Expected
        let missing = Set.difference expectedSet actualSet |> Set.toList
        let unexpected = Set.difference actualSet expectedSet |> Set.toList

        let ordering =
            if List.isEmpty missing && List.isEmpty unexpected then
                " The id SETS match — the sidebar ORDER changed; the fold only filters, so it must preserve registration order."
            else
                ""

        failtestf
            "Sidebar visibility mismatch at [%s].\n  Hidden but expected visible: %s\n  Visible but expected hidden: %s\n  Expected: %s\n  Actual:   %s%s"
            case.Coordinate
            (enumerate missing)
            (enumerate unexpected)
            (enumerate case.Expected)
            (enumerate actual)
            ordering

let private matrixCase coordinate modules inputs expected = {
    Coordinate = coordinate
    Modules = modules
    Inputs = inputs
    Expected = expected
}

// ─── The matrix ───────────────────────────────────────────────────────

let private cases = [
    // ── Anonymous mode ────────────────────────────────────────────────
    // Every SDK built-in declares `visibleToAuthenticated`, so an
    // Anonymous-surface deployment shows exactly the consumer modules its
    // authors marked visible-to-anonymous — Phase 66 B.3's structural
    // replacement for Phase 55's deployment-wide blanket-hide.
    matrixCase "anonymous × Anonymous surface × exposed" registered (anonymousSubject (Some exposed) |> anonymousMode) [
        "Sales"
        "Marketing"
    ]

    matrixCase
        "anonymous × Anonymous surface × Marketing hidden"
        registered
        (anonymousSubject (Some hidden) |> anonymousMode)
        [ "Sales" ]

    // Pre-load: the RBAC fetch has not resolved. Everything shows rather
    // than flickering empty; the server's per-route guard is the real
    // boundary (GP 12).
    matrixCase "anonymous × Anonymous surface × RBAC not yet loaded" registered (anonymousSubject None |> anonymousMode) [
        "Sales"
        "Marketing"
    ]

    // ── Individual mode ───────────────────────────────────────────────
    matrixCase
        "authenticated Member × Individual surface × exposed"
        registered
        (member' (Some exposed) |> individualMode)
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
        ]

    matrixCase
        "authenticated Member × Individual surface × Marketing hidden"
        registered
        (member' (Some hidden) |> individualMode)
        [ "Sales"; "_sdk.DataManager"; "_sdk.TeamManager"; "_sdk.UsageDashboard" ]

    matrixCase
        "platform admin × Individual surface × exposed"
        registered
        (platformAdminSubject (Some exposed) |> individualMode)
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
            "_sdk.PlatformAdmin"
            "_sdk.HealthMonitor"
        ]

    // Phase 245's own bug fix: an admin acting on a team respects that
    // team's exposure. "Marketing" stays hidden until the escape below.
    matrixCase
        "platform admin × Individual surface × Marketing hidden"
        registered
        (platformAdminSubject (Some hidden) |> individualMode)
        [
            "Sales"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
            "_sdk.PlatformAdmin"
            "_sdk.HealthMonitor"
        ]

    // Phase 245's "show all modules" escape — stage 1 short-circuits.
    matrixCase
        "platform admin × Individual surface × Marketing hidden + ShowAllModules"
        registered
        {
            individualMode (platformAdminSubject (Some hidden)) with
                ShowAllModules = true
        }
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
            "_sdk.PlatformAdmin"
            "_sdk.HealthMonitor"
        ]

    // The escape is admin-only: a member toggling nothing (and, if the
    // flag were ever set for one, a member with it set) still sees the
    // team's exposure.
    matrixCase
        "authenticated Member × Individual surface × Marketing hidden + ShowAllModules set"
        registered
        {
            individualMode (member' (Some hidden)) with
                ShowAllModules = true
        }
        [ "Sales"; "_sdk.DataManager"; "_sdk.TeamManager"; "_sdk.UsageDashboard" ]

    // ── Team mode, active team picked ─────────────────────────────────
    matrixCase "team Member × Team surface (active team) × exposed" registered (member' (Some exposed) |> teamMode) [
        "Sales"
        "Marketing"
        "_sdk.DataManager"
        "_sdk.TeamManager"
        "_sdk.UsageDashboard"
    ]

    // Identical to the row above BY CONSTRUCTION — the shell model carries
    // no team role, so the fold cannot tell an Owner/Admin from a Member
    // and "Team Management" stays ungated (server-side guards enforce).
    matrixCase
        "team Owner/Admin × Team surface (active team) × exposed"
        registered
        (teamOwnerAdmin (Some exposed) |> teamMode)
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
        ]

    matrixCase
        "team Owner/Admin × Team surface (active team) × Marketing hidden"
        registered
        (teamOwnerAdmin (Some hidden) |> teamMode)
        [ "Sales"; "_sdk.DataManager"; "_sdk.TeamManager"; "_sdk.UsageDashboard" ]

    matrixCase
        "platform admin × Team surface (active team) × Marketing hidden"
        registered
        (platformAdminSubject (Some hidden) |> teamMode)
        [
            "Sales"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
            "_sdk.PlatformAdmin"
            "_sdk.HealthMonitor"
        ]

    // ── Team mode, no active team (the landing collapse) ──────────────
    // Landing configured: the sidebar collapses to the landing module
    // alone, so a freshly-signed-in user sees only "you have no team yet".
    matrixCase
        "authenticated Member × Team surface (no active team) × exposed + landing configured"
        registeredWithLanding
        {
            teamModeNoActiveTeam (member' (Some exposed)) with
                NoActiveTeamLandingId = Some NoActiveTeamLanding.moduleId
        }
        [ NoActiveTeamLanding.moduleId ]

    // The admin carve-out: a team-less platform admin keeps the admin AND
    // management groups (the union — including team-scoped "Team
    // Management") so they can reach the team-assignment tools and unblock
    // themselves. "Data Management" is not an admin group, so the data
    // manager collapses away like any consumer module.
    matrixCase
        "platform admin × Team surface (no active team) × exposed + landing configured"
        registeredWithLanding
        {
            teamModeNoActiveTeam (platformAdminSubject (Some exposed)) with
                NoActiveTeamLandingId = Some NoActiveTeamLanding.moduleId
        }
        [
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
            "_sdk.PlatformAdmin"
            "_sdk.HealthMonitor"
            NoActiveTeamLanding.moduleId
        ]

    // Landing NOT configured (the default) — the gate is inert and the
    // sidebar is byte-identical to its pre-gate self (GP 11 / GP 13).
    matrixCase
        "authenticated Member × Team surface (no active team) × exposed + landing NOT configured"
        registered
        (member' (Some exposed) |> teamModeNoActiveTeam)
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
        ]

    // Landing configured but the deployment declares no team surface —
    // also inert, because the collapse is scoped by `HasTeamScope`.
    matrixCase
        "authenticated Member × Individual surface × landing configured (no team scope ⇒ inert)"
        registeredWithLanding
        {
            individualMode (member' (Some exposed)) with
                NoActiveTeamLandingId = Some NoActiveTeamLanding.moduleId
        }
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
            NoActiveTeamLanding.moduleId
        ]

    // ── ClaimBearer surface ───────────────────────────────────────────
    // A single-shape claim-bearer embed resolves to `ClaimBearerKind`,
    // which `visibleToAuthenticated` admits — the claim has already
    // authenticated the request.
    matrixCase
        "claim bearer × ClaimBearer surface × exposed"
        registered
        {
            member' (Some exposed) with
                SubjectKind = ClaimBearerKind
        }
        [
            "Sales"
            "Marketing"
            "_sdk.DataManager"
            "_sdk.TeamManager"
            "_sdk.UsageDashboard"
        ]
]

[<Tests>]
let tests =
    testList "Phase 570 — sidebar visibility matrix" [

        for case in cases do
            test case.Coordinate { check case }

        test "the matrix spans every axis it claims to" {
            // Cheap guard against a future edit quietly deleting rows: the
            // pack is only a contract while the coordinates are present.
            Expect.equal (List.length cases) 18 "eighteen matrix cells"

            let coordinates = cases |> List.map _.Coordinate
            Expect.isTrue (coordinates |> List.forall (fun c -> c.Contains "×")) "every case names its coordinate"

            Expect.equal
                (coordinates |> List.distinct |> List.length)
                (List.length cases)
                "coordinates are unique — a duplicated label hides a cell"
        }
    ]

// ─── Filter-order pins (Phase 570.C) ─────────────────────────────────
//
// The matrix above says WHAT each caller sees; these say WHY the four
// stages compose in the order `SidebarVisibility.visible` documents. Each
// is a case a wrong order would silently pass.

[<Tests>]
let orderingTests =
    testList "Phase 570 — sidebar filter composition order" [

        test "the role gate composes with RBAC — it never re-admits what RBAC removed" {
            // A consumer module in a platform-scoped group, managed AND
            // hidden by the team. An admin holds the role, so stage 2
            // admits it — but stage 1 already removed it. Running the role
            // gate first (or as an `||`) would show the admin a module the
            // team's exposure hides.
            let adminGroupModule =
                facts "AdminReports" (Some "Platform Management") Visibility.visibleToAll

            let response: AccessibleModulesResponse = {
                Managed = [ "AdminReports" ]
                Accessible = []
            }

            let inputs = {
                platformAdminSubject (Some response) with
                    SubjectKind = UserKind
            }

            Expect.equal
                (SidebarVisibility.visibleIds id inputs [ adminGroupModule ])
                []
                "RBAC removal survives the role gate — the gates compose, they do not race"
        }

        test "a module's own Visibility cannot re-admit what RBAC removed" {
            // Module-author intent (stage 3) is subordinate to deployment
            // authority (stage 1): `visibleToAll` is not a bypass.
            let response: AccessibleModulesResponse = {
                Managed = [ "Sales" ]
                Accessible = []
            }

            let inputs = {
                member' (Some response) with
                    SubjectKind = UserKind
            }

            Expect.equal (SidebarVisibility.visibleIds id inputs [ sales ]) [] "stage 3 cannot widen stage 1"
        }

        test "the no-team collapse runs LAST — it cannot re-admit a role-gated module" {
            // A non-admin in the pre-team-pick window. `_sdk.PlatformAdmin`
            // sits in an admin group, so a collapse that ran BEFORE the
            // role gate (or that consulted the full module list) would put
            // the platform-admin rail back in front of a plain user.
            let inputs = {
                teamModeNoActiveTeam (member' (Some exposed)) with
                    NoActiveTeamLandingId = Some NoActiveTeamLanding.moduleId
            }

            Expect.equal
                (SidebarVisibility.visibleIds id inputs registeredWithLanding)
                [ NoActiveTeamLanding.moduleId ]
                "the collapse narrows an already-authorised set; it never reopens one"
        }

        test "the fold only filters — it preserves registration order and never duplicates" {
            let inputs = platformAdminSubject None |> individualMode
            let actual = SidebarVisibility.visibleIds id inputs registered

            Expect.equal actual (registered |> List.map _.Id) "an all-admitting input returns the input list verbatim"
        }

        test "`defaults` leaves a declare-nothing deployment untouched (GP 11)" {
            // The pre-filter baseline: no RBAC response, no role, no team
            // scope, no landing gate — modules that declare no `Visibility`
            // all survive.
            let plain = [ sales; marketing; facts "Finance" (Some "Reports") Visibility.visibleToAll ]

            Expect.equal
                (SidebarVisibility.visibleIds id SidebarVisibility.defaults plain)
                [ "Sales"; "Marketing"; "Finance" ]
                "every stage is inert against defaults for modules declaring nothing"
        }
    ]

// ─── Admin group-set pins ─────────────────────────────────────────────
//
// The group labels stage 2 and stage 4 key off. `ClientConfig` re-exports
// both predicates, but `ClientConfig`'s module initialiser is Fable-only
// (it reaches `AgGridModuleConfig.community`), so the .NET-side pins call
// `SidebarVisibility` directly — the canonical home since Phase 570.

[<Tests>]
let groupSetTests =
    testList "Phase 570 — admin sidebar group sets" [

        test "platform-scoped groups are exactly the two role-gated labels" {
            Expect.isTrue
                (SidebarVisibility.isPlatformAdminSidebarGroup (Some "Platform Admin"))
                "the consumer `withGroup` convention keeps its role gate"

            Expect.isTrue
                (SidebarVisibility.isPlatformAdminSidebarGroup (Some "Platform Management"))
                "the SDK admin built-ins' declared group must be role-gated — the 4f.2 regression"

            Expect.isFalse
                (SidebarVisibility.isPlatformAdminSidebarGroup (Some "Team Management"))
                "team-scoped management must not be gated on PlatformRole"

            Expect.isFalse
                (SidebarVisibility.isPlatformAdminSidebarGroup (Some "Data Management"))
                "an app-domain group passes freely"

            Expect.isFalse (SidebarVisibility.isPlatformAdminSidebarGroup None) "no group ⇒ never role-gated"
        }

        test "the no-team admin escape spans the union of platform- and team-scoped groups" {
            for g in [ "Platform Admin"; "Platform Management"; "Team Management" ] do
                Expect.isTrue
                    (SidebarVisibility.isAdminSidebarGroup (Some g))
                    (sprintf "\"%s\" must stay visible to a team-less platform admin under the collapse" g)

            Expect.isFalse
                (SidebarVisibility.isAdminSidebarGroup (Some "Data Management"))
                "app group is not in the union"

            Expect.isFalse (SidebarVisibility.isAdminSidebarGroup None) "no group is not in the union"
        }
    ]