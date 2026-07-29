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
// **The Owner/Admin row split from the Member row at Phase 568.** It was
// pinned as byte-identical here, deliberately: the shell model carried no
// TEAM role, so the fold could not tell an Owner from a Member and the
// "Team Management" group had to stay ungated (hiding it on `PlatformRole`
// would have stripped team Owners of their own tools). 568.B carries
// `ActiveTeamRole` on the model and the built-ins declare
// `NavRole.TeamOwnerAdmin`, so the two rows now differ — which is the pack
// doing its job, not a regression. The equality note is kept here because
// the reason it held for so long is the reason the typed gate was needed.
//
// Phase 568 adds a second matrix below (`navRoleCases`) for the gate
// itself: undeclared parity, each `NavRole` against each role, the
// deprecated group-name fallback, and composition with stages 1 and 3.

// ─── Fixtures ─────────────────────────────────────────────────────────

/// A module declaring no `NavRole` — the pre-568 shape, and every
/// consumer module that has not opted in.
let private facts
    (id: string)
    (group: string option)
    (visibility: SubjectKind -> bool)
    : SidebarVisibility.SidebarModuleFacts =
    {
        Id = id
        Group = group
        NavRole = None
        Visibility = visibility
    }

/// Phase 568 — the same, with a declared navigation-role gate.
let private gatedFacts
    (id: string)
    (group: string option)
    (navRole: NavRole)
    (visibility: SubjectKind -> bool)
    : SidebarVisibility.SidebarModuleFacts =
    {
        facts id group visibility with
            NavRole = Some navRole
    }

// The registered set, in the order `prepareModules` composes it: the
// consumer's own modules first, then the SDK built-ins. Ids, groups,
// `NavRole` and `Visibility` declarations mirror the shipped built-ins
// (`FileManagerUI` / `TeamManagerUI` / `UsageDashboard` / `PlatformAdminUI`
// / `HealthMonitorUI`) — the built-ins themselves cannot be constructed
// here (their `create` bodies touch `Icons`, Fable `importDefault` dummy
// code that throws under .NET; that is why
// `BuiltInModuleSurfaceTests.visibilityTests` is a Fable-tier pack), so
// their four visibility-relevant facts are restated as fixtures. That
// makes each built-in a DELIBERATE two-file edit: a `withNavRole` added
// at its registration site is added here too, or this pack pins a gate
// the shipped module no longer declares.
let private sales = facts "Sales" None Visibility.visibleToAll
let private marketing = facts "Marketing" None Visibility.visibleToAll

let private dataManager =
    facts "_sdk.DataManager" (Some "Data Management") Visibility.visibleToAuthenticated

let private teamManager =
    gatedFacts "_sdk.TeamManager" (Some "Team Management") NavRole.TeamOwnerAdmin Visibility.visibleToAuthenticated

let private usageDashboard =
    gatedFacts "_sdk.UsageDashboard" (Some "Team Management") NavRole.TeamOwnerAdmin Visibility.visibleToAuthenticated

let private platformAdmin =
    gatedFacts
        "_sdk.PlatformAdmin"
        (Some "Platform Management")
        NavRole.PlatformAdminOnly
        Visibility.visibleToAuthenticated

let private healthMonitor =
    gatedFacts
        "_sdk.HealthMonitor"
        (Some "Platform Management")
        NavRole.PlatformAdminOnly
        Visibility.visibleToAuthenticated

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

/// Subject axis — role + exposure. `member'` carries no team role: it is
/// the subject for the modes where there is none to carry (Anonymous,
/// Individual, and the pre-team-pick window). The two team-scoped
/// subjects below state theirs, which is what Phase 568 made
/// distinguishable.
let private anonymousSubject (response: AccessibleModulesResponse option) = {
    SidebarVisibility.defaults with
        Accessibility = response
        PlatformRole = None
}

let private member' (response: AccessibleModulesResponse option) = anonymousSubject response

/// A plain `TeamRole.Member` on the active team — the subject
/// `NavRole.TeamOwnerAdmin` exists to exclude.
let private teamMemberSubject (response: AccessibleModulesResponse option) = {
    anonymousSubject response with
        ActiveTeamRole = Some TeamRole.Member
}

/// A team Owner (an `Admin` is pinned equal to it in the 568 matrix).
/// Holds no platform role — the point is that team-scoped management
/// surfaces reach them WITHOUT one.
let private teamOwnerAdmin (response: AccessibleModulesResponse option) = {
    anonymousSubject response with
        ActiveTeamRole = Some TeamRole.Owner
}

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
    // No team scope ⇒ no active-team role to resolve ⇒ the Phase 568
    // `NavRole.TeamOwnerAdmin` gate on `_sdk.TeamManager` /
    // `_sdk.UsageDashboard` fails OPEN, so these rows are byte-identical
    // to their pre-568 selves (GP 11). A deployment with no teams must not
    // lose entries to a gate about team roles.
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
    // Phase 568 — with a resolved `TeamRole.Member`, the two built-ins
    // declaring `NavRole.TeamOwnerAdmin` drop out. This row was identical
    // to the Owner/Admin one below until the typed gate landed.
    matrixCase
        "team Member × Team surface (active team) × exposed"
        registered
        (teamMemberSubject (Some exposed) |> teamMode)
        [ "Sales"; "Marketing"; "_sdk.DataManager" ]

    // The row the Member one used to equal: an Owner keeps the
    // team-management surfaces without holding any platform role.
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

// ─── Phase 568 — the typed nav-role gate ──────────────────────────────
//
// The matrix above is stated over the SHIPPED built-ins, so it exercises
// the gate only where a built-in happens to declare one. This one is
// stated over purpose-built modules and holds the module fixed while the
// caller varies — the shape a gate is actually specified in.
//
// The acceptance criterion the last two blocks pin: **no gating decision
// reads a group display name except the documented deprecated fallback**,
// so renaming a sidebar group changes zero access behaviour.

/// Declares `PlatformAdminOnly` while sitting in an ordinary,
/// non-admin-looking group — if a rename could re-open it, this is the
/// module that would show it.
let private gatedByRole =
    gatedFacts "Ledger" (Some "Reports") NavRole.PlatformAdminOnly Visibility.visibleToAll

/// Declares `TeamOwnerAdmin`, likewise outside any admin group.
let private teamGatedByRole =
    gatedFacts "Roster" (Some "Reports") NavRole.TeamOwnerAdmin Visibility.visibleToAll

/// Declares nothing and sits in an ordinary group — the undeclared-parity
/// control: it must survive every row below.
let private ungated = facts "Ledger.Public" (Some "Reports") Visibility.visibleToAll

/// Declares nothing but sits in a platform-scoped group — the DEPRECATED
/// fallback's one subject.
let private fallbackOnly =
    facts "LegacyAdmin" (Some "Platform Admin") Visibility.visibleToAll

let private navRoleModules = [ ungated; gatedByRole; teamGatedByRole; fallbackOnly ]

/// Subject helper: a caller with no RBAC response (so stage 1 is inert)
/// and the given platform / team roles.
let private caller (platformRole: PlatformRole option) (teamRole: TeamRole option) = {
    SidebarVisibility.defaults with
        PlatformRole = platformRole
        ActiveTeamRole = teamRole
        SubjectKind = TeamMemberKind
}

let private navRoleCases = [
    // ── Each role against the full declared set ───────────────────────
    // `TeamOwnerAdmin` fails open on an unknown team role; the two
    // platform gates (declared and fallback) do not.
    matrixCase "no role at all × declared set" navRoleModules (caller None None) [ "Ledger.Public"; "Roster" ]

    matrixCase "team Member × declared set" navRoleModules (caller None (Some TeamRole.Member)) [ "Ledger.Public" ]

    matrixCase "team Admin × declared set" navRoleModules (caller None (Some TeamRole.Admin)) [
        "Ledger.Public"
        "Roster"
    ]

    matrixCase "team Owner × declared set" navRoleModules (caller None (Some TeamRole.Owner)) [
        "Ledger.Public"
        "Roster"
    ]

    // A platform admin clears BOTH gates — including `TeamOwnerAdmin`
    // while holding the LOWEST team role, which is the composition that
    // lets an admin reach team-management tools to unblock a team.
    matrixCase
        "platform admin (team Member) × declared set"
        navRoleModules
        (caller (Some PlatformRole.PlatformAdmin) (Some TeamRole.Member))
        [ "Ledger.Public"; "Ledger"; "Roster"; "LegacyAdmin" ]

    matrixCase "platform admin (no team) × declared set" navRoleModules (caller (Some PlatformRole.PlatformAdmin) None) [
        "Ledger.Public"
        "Ledger"
        "Roster"
        "LegacyAdmin"
    ]

    // ── Group renames must not move a gate ────────────────────────────
    // The same three modules re-grouped under a PLATFORM-scoped label. A
    // group-keyed gate would now hide `Roster` from its team Owner; the
    // typed gate is unmoved, so `Roster` survives and `Ledger` (declared
    // `PlatformAdminOnly`) stays hidden. `Ledger.Public` DOES disappear —
    // it declares nothing, so the deprecated fallback reads its new group
    // and gates it. That asymmetry is the fallback's whole cost, and the
    // reason it is deprecated: only an UNDECLARED module's access still
    // moves when its label does.
    matrixCase
        "team Owner × declared set re-grouped under \"Platform Management\""
        [
            {
                ungated with
                    Group = Some "Platform Management"
            }
            {
                gatedByRole with
                    Group = Some "Platform Management"
            }
            {
                teamGatedByRole with
                    Group = Some "Platform Management"
            }
        ]
        (caller None (Some TeamRole.Owner))
        [ "Roster" ]

    // …and the reverse: a declared `PlatformAdminOnly` module moved OUT
    // of every admin group stays gated for a non-admin.
    matrixCase
        "team Owner × PlatformAdminOnly module in a plain group"
        [ gatedByRole ]
        (caller None (Some TeamRole.Owner))
        []
]

[<Tests>]
let navRoleTests =
    testList "Phase 568 — typed nav-role gating" [

        for case in navRoleCases do
            test case.Coordinate { check case }

        test "the nav-role matrix spans every role it claims to" {
            Expect.equal (List.length navRoleCases) 8 "eight nav-role cells"

            Expect.equal
                (navRoleCases |> List.map _.Coordinate |> List.distinct |> List.length)
                (List.length navRoleCases)
                "coordinates are unique — a duplicated label hides a cell"
        }

        test "an undeclared module is ungated for every caller (GP 11)" {
            // Undeclared parity, stated directly rather than read off the
            // matrix: the same module, the whole role cross-product, one
            // answer. This is the property that makes 568 additive.
            for platformRole in [ None; Some PlatformRole.PlatformAdmin ] do
                for teamRole in [ None; Some TeamRole.Member; Some TeamRole.Admin; Some TeamRole.Owner ] do
                    Expect.equal
                        (SidebarVisibility.visibleIds id (caller platformRole teamRole) [ ungated ])
                        [ "Ledger.Public" ]
                        (sprintf "undeclared module hidden at platform=%A team=%A" platformRole teamRole)
        }

        test "the deprecated group-name fallback still gates a module that declares no NavRole" {
            // 568.D — consumer modules built against `withGroup
            // "Platform Admin"` keep their gate until the next major.
            Expect.equal
                (SidebarVisibility.visibleIds id (caller None (Some TeamRole.Owner)) [ fallbackOnly ])
                []
                "fallback hides from a non-platform-admin"

            Expect.equal
                (SidebarVisibility.visibleIds id (caller (Some PlatformRole.PlatformAdmin) None) [ fallbackOnly ])
                [ "LegacyAdmin" ]
                "fallback admits a platform admin"

            // The fallback maps platform-scoped groups ONLY. Inferring
            // `TeamOwnerAdmin` from "Team Management" would newly hide
            // entries from Members that no consumer asked to gate.
            let teamGroupUndeclared =
                facts "LegacyTeamTool" (Some "Team Management") Visibility.visibleToAll

            Expect.equal
                (SidebarVisibility.visibleIds id (caller None (Some TeamRole.Member)) [ teamGroupUndeclared ])
                [ "LegacyTeamTool" ]
                "an undeclared \"Team Management\" module stays ungated — pre-568 behaviour"
        }

        test "a declared NavRole overrides the group-name fallback" {
            // Both signals present and disagreeing: the typed one wins, in
            // the direction that OPENS the entry — otherwise a consumer
            // could never un-gate a module while keeping its group label.
            let openedUp =
                gatedFacts "LegacyAdmin" (Some "Platform Admin") NavRole.TeamOwnerAdmin Visibility.visibleToAll

            Expect.equal
                (SidebarVisibility.visibleIds id (caller None (Some TeamRole.Owner)) [ openedUp ])
                [ "LegacyAdmin" ]
                "the declared TeamOwnerAdmin gate replaces the inferred PlatformAdminOnly one"
        }

        test "the gate composes with RBAC and with the module's own Visibility" {
            // 568.E's composition cell. A declared-and-held gate is not a
            // bypass of stage 1 (Phase 245 exposure) or stage 3.
            let response: AccessibleModulesResponse = {
                Managed = [ "Ledger" ]
                Accessible = []
            }

            let admin = {
                caller (Some PlatformRole.PlatformAdmin) None with
                    Accessibility = Some response
            }

            Expect.equal
                (SidebarVisibility.visibleIds id admin [ gatedByRole ])
                []
                "holding the nav role does not re-admit what the team's exposure hid"

            let hiddenFromKind =
                gatedFacts "Ledger" (Some "Reports") NavRole.PlatformAdminOnly Visibility.visibleToAnonymous

            Expect.equal
                (SidebarVisibility.visibleIds id (caller (Some PlatformRole.PlatformAdmin) None) [ hiddenFromKind ])
                []
                "holding the nav role does not override the module's own SubjectKind predicate"
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

// ─── Phase 609 — accessible names for the rail's rows ─────────────────
//
// The narrow (w-20) rail is icon-only — nothing in a row renders visible
// text — so a row carrying neither `aria-label` nor `title` is an unnamed
// button: no tooltip for a sighted pointer user, a bare "button" read
// aloud for a screen-reader user. Section HEADERS carried a `title`; the
// ROWS carried nothing. That was worst for the two landings
// (`_sdk.home` / `_sdk.admin-home`) and the Phase 567 area switchers
// (`_area.admin` / `_area.product`), because the switcher is the only
// route INTO the administration area and the landing the only guaranteed
// way back — an unnamed icon there is a dead end, not an inconvenience.
//
// **Why these assertions are textual.** The renderers live in
// `Toolup.Sidebar`, whose module initialiser reaches
// `importDefault "../icons/toolup-forge-dark.png"` — a Fable binding that
// throws outside a Fable compilation, so nothing in that module can be
// CALLED from the .NET harness. That is the same constraint
// `SidebarHidingContractTests` and `ModuleParityValidator` document, and
// the textual form is the same one `SubjectKindClientFlowTests` uses for
// the Fable-only client tier.
//
// **Migration.** Phase 610 is the structural home for shell-a11y
// assertions. When its fixture set lands, these should be promoted to
// rendered-DOM assertions over both rail widths (every row exposes a
// non-empty accessible name equal to its display name) and this list
// retired — the pins below are what holds the contract until then.

let private sidebarSourcePath () =
    let assemblyDir =
        System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    // …/src/ToolUp.Platform.Tests/bin/Debug/net10.0 → the repo root
    let repoRoot =
        System.IO.Path.GetFullPath(System.IO.Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

    System.IO.Path.Combine(repoRoot, "src", "ToolUp.Platform.Client", "Client", "UI", "Sidebar.fs")

/// The renderer source with full-line comments stripped, and CRLF
/// normalised to LF. Every assertion below runs against this rather than
/// the raw text: the file's own comments quote both the attribute names
/// and the retired `Option.defaultValue ""` spelling, so a raw search
/// would match the prose explaining the fix instead of the code applying
/// it — and would keep passing if the code were reverted.
let private sidebarCode () =
    let path = sidebarSourcePath ()
    Expect.isTrue (System.IO.File.Exists path) (sprintf "expected Sidebar.fs at %s" path)

    (System.IO.File.ReadAllText path).Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
    |> String.concat "\n"

/// One chunk per `Html.button [` occurrence, each running to the start of
/// the next button. Coarse, but sufficient: a Feliz button's props sit
/// between its own opening bracket and the next button's, so a chunk
/// missing `prop.ariaLabel` is a button with no accessible name.
let private buttonBlocks (code: string) =
    code.Split([| "Html.button [" |], System.StringSplitOptions.None)
    |> Array.skip 1

[<Tests>]
let accessibleNameTests =
    testList "Phase 609 — rail row accessible names" [

        test "every interactive control in the sidebar declares an accessible name" {
            let blocks = buttonBlocks (sidebarCode ())

            Expect.isTrue
                (blocks.Length >= 5)
                (sprintf
                    "expected at least the five sidebar buttons (the row, its pin and hide \
                     affordances, the section header, the collapsed-group icon) — found %d"
                    blocks.Length)

            blocks
            |> Array.iteri (fun i block ->
                Expect.stringContains
                    block
                    "prop.ariaLabel"
                    (sprintf
                        "Sidebar.fs button #%d carries no `prop.ariaLabel`. Phase 609 — every \
                         interactive control in the rail names itself; in the narrow (w-20) rail \
                         nothing renders visible text, so a button without one is announced as an \
                         unnamed button and offers no tooltip. The rule, and when to add `title` \
                         as well, is in the `rowAccessibleName` doc-comment."
                        (i + 1)))
        }

        test "the row renderer names the row, not its section" {
            let code = sidebarCode ()

            Expect.stringContains
                code
                "let accessibleName = rowAccessibleName rowId name"
                "renderRow must resolve the accessible name from the ROW's own id and display \
                 name. The pre-609 rail named only sections (the collapsed-group icon's \
                 `prop.title`), which is exactly the wrong granularity: every row inside a \
                 section would have shared one name."

            Expect.stringContains
                code
                "prop.ariaLabel accessibleName"
                "the row button must carry the resolved name as its accessible name in BOTH rail \
                 widths — `title` alone is the weakest source in the accessible-name computation \
                 and never appears on touch."

            Expect.stringContains
                code
                "prop.title accessibleName"
                "the narrow rail must also carry the name as a tooltip — it is the sighted \
                 pointer user's equivalent of the label span the w-20 rail does not render."
        }

        test "the reserved landing and area-switcher rows are named explicitly" {
            let code = sidebarCode ()

            let arms = [
                "| HomeId ->", "the product landing"
                "| AdminHomeId ->", "the administration landing"
                "| AdminAreaId ->", "the switcher INTO the administration area"
                "| ProductAreaId ->", "the switcher back out to the app"
            ]

            for arm, what in arms do
                Expect.stringContains
                    code
                    arm
                    (sprintf
                        "`rowAccessibleName` must name %s (%s) even when its display name arrives \
                         blank. These four rows are the ones whose loss strands the user, so they \
                         do not rely on a caller supplying a name."
                        what
                        arm)
        }

        test "no rail control resolves its label to the empty string" {
            let code = sidebarCode ()

            Expect.isFalse
                (code.Contains "Option.defaultValue \"\"")
                "the collapsed-group icon resolved an untitled section to an EMPTY tooltip \
                 (`section.Title |> Option.defaultValue \"\"`), which is reachable today: the \
                 `_other` section drops its title when it is the only section, and it is \
                 collapsed by default. An empty accessible name is the same defect as a missing \
                 one — fall back to a real name (the lead module's) instead."
        }

        test "the narrow rail keeps both PLACED sections fully visible" {
            // Phase 611's narrow-rail leg. The `_home` / `_trailing` sections
            // are built with `IsCollapsed = false`, so the `not
            // section.IsCollapsed` arm would render them anyway — naming them
            // in `alwaysVisible` is what makes the guarantee local to the
            // render decision instead of resting on a value computed three
            // hundred lines away. The rail's two widths are separate render
            // paths (608.D), and this is the one the .NET harness can only
            // read, not run.
            let code = sidebarCode ()

            Expect.stringContains
                code
                "|| section.Key = TrailingKey"
                "the narrow (w-20) rail's `alwaysVisible` must name `TrailingKey` alongside \
                 `HomeKey`. A placed row is reachable in BOTH rail widths without expanding \
                 anything — that is the whole point of declaring its slot, and the narrow rail \
                 is where the Phase 608 defect was worst (an icon inside a collapsed catch-all \
                 is not a route)."
        }

        test "the app mark carries alt text" {
            let code = sidebarCode ()

            Expect.stringContains
                code
                "prop.alt appName"
                "the shell logo had no `alt`, so in the narrow rail — where the app-name span is \
                 not rendered — the shell's only branding was an unnamed image. Found by the \
                 609.C sweep of the other icon-only chrome in the rail."
        }
    ]

// ─── Phase 611 — rail placement as declared data ──────────────────────
//
// A row's position used to be an implicit consequence of a field that
// means something else: `Group = None` was read by `buildSections` as
// "bottom of the rail, inside the collapsed `_other` catch-all". Nothing
// declared it, and the two landings escaped it only because the fold
// special-cased their literal ids — so the Phase 567 area-switcher rows,
// added later with `Group = None` and no special case, bucketed into
// `_other`: last in the rail and collapsed on a fresh profile. Observed
// live in a `SeparateArea` deployment, where a platform admin had to
// expand "Other" to find the only route into the administration area
// (Phase 608, superseded by 611).
//
// **The behavioural pack is Fable-side, and that placement was measured.**
// `buildSections` reads no module-level value of `Toolup.Sidebar`, so it
// looks callable from here — but F# emits a static-init check on module
// function entry, so the first call fires the file's initialiser, which
// reaches `importDefault "../icons/toolup-forge-dark.png"` and throws
// "You've hit dummy code used for Fable bindings". The pack was written
// here first and every case errored on exactly that, which is why the
// prose above this file's Phase 609 arm says what it says. The executing
// assertions — undeclared ⇒ grouped, declared ⇒ placed, no reserved row in
// `_other`, and the fresh-profile switcher-reachability case — therefore
// live in `ToolUp.AI.Client.Tests/SidebarPlacementTests.fs`, beside the
// other two packs over the same fold.
//
// What is left here is the half a .NET harness CAN hold: the code shape.
// These are cheap and they fail in the one direction that matters —
// someone reintroducing an id-keyed placement rule, or a new reserved row
// arriving without a declaration.

[<Tests>]
let placementTests =
    testList "Phase 611 — declared rail placement (shape)" [

        test "the fold resolves a DECLARED slot, defaulting to grouped" {
            let code = sidebarCode ()

            Expect.stringContains
                code
                "m.Placement |> Option.defaultValue GroupedSlot"
                "`buildSections` must resolve each row's slot from its own `Placement` declaration, \
                 with absent ⇒ `GroupedSlot` — that default IS the GP 11 guarantee that an existing \
                 composition buckets exactly as it did before the field existed."

            for slot, key in [ "LeadingSlot", "HomeKey"; "TrailingSlot", "TrailingKey" ] do
                Expect.stringContains
                    code
                    (sprintf "placedSection %s %s" key slot)
                    (sprintf
                        "the fold must lift `%s` rows into the `%s` section before any bucketing. \
                         A placed section is built with `IsCollapsed = false` and no title, which is \
                         what makes it reachable on a fresh profile in both rail widths."
                        slot
                        key)
        }

        test "no id literal drives placement any more" {
            let code = sidebarCode ()

            Expect.isFalse
                (code.Contains "isLandingId")
                "`isLandingId` was the id special-case this phase retired: `id = HomeId || id = \
                 AdminHomeId`, a hardcoded list the Phase 567 switcher rows were never added to — \
                 which is precisely why they fell into `_other`. Placement is declared at the row's \
                 construction site now; a fold that recognises rows by id will grow the same gap \
                 again the next time a reserved row is added."

            // The four reserved ids still appear in this file — in
            // `rowAccessibleName` (Phase 609) and `isHideableId` (Phase 572),
            // both of which are deliberately id-keyed and neither of which
            // decides position. So the assertion is specifically that no
            // *placement* decision reads one, expressed as: the section keys a
            // row can resolve to are chosen from the declared slot, and the
            // grouped branch keys off `Group` alone.
            Expect.stringContains
                code
                "let groupable = rail |> List.filter (fun m -> slotOf m = GroupedSlot)"
                "the bucketed set must be exactly the rows that resolved to `GroupedSlot` — derived \
                 from the declaration, never from an id list or a `Group = None` inference."
        }

        test "the render layer suppresses pinning for a placed row by section, not by id" {
            let code = sidebarCode ()

            Expect.stringContains
                code
                "let pinnable = not inPlacedSection && not inHiddenSection"
                "the pin affordance must be suppressed for any row in a placed section. This read \
                 `m.Id <> HomeId`, which covered ONE of the four reserved rows by name — the admin \
                 landing and both switchers each offered a pin control whose click was already \
                 inert, because `buildSections` leaves a placed row out of the pinning index."

            Expect.stringContains
                code
                "let inPlacedSection = section.Key = HomeKey || section.Key = TrailingKey"
                "…and it must derive that from the reserved SECTION keys, which is the placement \
                 model's own vocabulary, rather than from any row id."
        }

        test "the section order puts the trailing slot after the rail and before the reveal list" {
            // Line-joined so the assertion survives Fantomas moving the `@`
            // operators around; the section names themselves are what is
            // being pinned, in order.
            let oneLine = sidebarCode().Split('\n') |> Array.map _.Trim() |> String.concat " "

            Expect.stringContains
                oneLine
                "homeSection @ pinnedSection @ declaredSections @ otherSection @ trailingSection @ hiddenSection"
                "the fold's final concatenation must read in rail order: the leading placed section, \
                 pinned, declared groups, `_other`, the trailing placed section, then the Hidden \
                 items reveal list. `_hidden` stays last so the Phase 572 invariant (\"rendered \
                 after every rail section\") still holds, and `_trailing` sits after `_other` so a \
                 placed row reads as rail chrome rather than as another destination."
        }
    ]