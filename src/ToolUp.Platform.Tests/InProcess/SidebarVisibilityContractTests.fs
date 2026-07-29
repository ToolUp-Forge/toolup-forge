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