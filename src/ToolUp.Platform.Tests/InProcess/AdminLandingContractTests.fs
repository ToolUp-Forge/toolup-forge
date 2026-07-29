// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AdminLandingContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 573 — administration-landing contract pack ────────────────
//
// The Administration area (Phase 567) now lands on a dashboard composed
// from module-contributed tiles rather than on whichever admin module
// sorted first. Three things about that composition are decisions rather
// than rendering, and this pack pins all three:
//
//   * **Composition** — a contributed tile is rendered, in weight order,
//     and a tile whose owning module the deployment does not register is
//     not (573.A / 573.B).
//   * **Role filtering** — a tile is admitted iff the caller may
//     navigate to the module it fronts, by the SAME decision the rail,
//     the route guard (Phase 569) and the command palette (Phase 571)
//     run (573.C).
//   * **The empty states** — the two are DISTINCT, and which one applies
//     is derived, not guessed (573.A).
//
// **What is NOT here, and why.** `AdminHome.fs` builds `ReactElement`s
// and `AdminTile.fs` holds a React context and `Icons`-touching tile
// values; neither is constructible under .NET (`importDefault` is Fable
// dummy code that throws), the same constraint
// `SidebarVisibilityContractTests` and `ModuleParityValidator` document
// for `ClientConfig`. So the tiles below are hand-built
// `AdminTiles.AdminTileFacts` — exactly the projection the shipped
// registry produces via `AdminTileRegistry.facts` — and the assertions
// are on the fold the view calls, not on the markup it emits.
//
// **The click-through arm is structural.** A tile's navigation target IS
// its `OwnerModuleId`, the same field the visibility filter keys on, so
// "every rendered tile navigates somewhere this caller may reach" is not
// a coincidence to be re-checked per subject but a consequence of one
// field serving both roles — stated below as a property over the whole
// subject axis. If someone ever gives a tile a separate navigation
// target, that arm is what fails.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private moduleFacts (id: string) (group: string option) (navRole: NavRole) : SidebarVisibility.SidebarModuleFacts = {
    Id = id
    Group = group
    NavRole = Some navRole
    Visibility = Visibility.visibleToAuthenticated
}

// The four SDK built-ins that contribute a landing tile, restated as
// facts (their `create` bodies touch `Icons`). Ids, groups and NavRoles
// mirror the shipped registrations — the same deliberate two-file edit
// `SidebarVisibilityContractTests` documents.
let private teamManager =
    moduleFacts "_sdk.TeamManager" (Some "Team Management") NavRole.TeamOwnerAdmin

let private usageDashboard =
    moduleFacts "_sdk.UsageDashboard" (Some "Team Management") NavRole.TeamOwnerAdmin

let private healthMonitor =
    moduleFacts "_sdk.HealthMonitor" (Some "Platform Management") NavRole.PlatformAdminOnly

let private serviceStatusBoard =
    moduleFacts "_sdk.ServiceStatusBoard" (Some "Platform Management") NavRole.PlatformAdminOnly

/// The landing module itself — ungrouped, area-declared, gated on the
/// weakest administration role so every caller with an admin area to
/// enter can see its landing (`AdminHome.create`).
let private adminHome = {
    moduleFacts "_sdk.admin-home" None NavRole.TeamOwnerAdmin with
        Group = None
}

/// A deployment running all four tile-contributing built-ins.
let private allRegistered = [ adminHome; teamManager; usageDashboard; healthMonitor; serviceStatusBoard ]

/// The same deployment with `HealthMonitor = NoHealthMonitor` — the
/// module is simply not composed, which is the whole of what "mode off"
/// means downstream of `prepareModules`.
let private withoutHealthMonitor =
    allRegistered |> List.filter (fun m -> m.Id <> "_sdk.HealthMonitor")

let private tile (id: string) (owner: string) (weight: int) : AdminTiles.AdminTileFacts = {
    Id = id
    OwnerModuleId = owner
    Weight = weight
}

// The tiles the four built-ins contribute, at their shipped weights and
// in contribution order (`SDK.Client.builtInAdminTiles`).
let private teamsTile = tile "_sdk.tile.teams" "_sdk.TeamManager" 10
let private usageTile = tile "_sdk.tile.usage" "_sdk.UsageDashboard" 20
let private healthTile = tile "_sdk.tile.health" "_sdk.HealthMonitor" 30

let private serviceStatusTile =
    tile "_sdk.tile.service-status" "_sdk.ServiceStatusBoard" 40

let private contributed = [ teamsTile; usageTile; healthTile; serviceStatusTile ]

/// `AdminTileFacts` are their own projection here — the registry's
/// `facts` does the same job for the dressed `AdminTile`.
let private self (t: AdminTiles.AdminTileFacts) = t

// ─── Subjects ─────────────────────────────────────────────────────────

/// A team-scoped deployment with an active team picked. The role axis is
/// the two fields Phase 568 made distinguishable.
let private teamSubject
    (platformRole: PlatformRole option)
    (teamRole: TeamRole option)
    : SidebarVisibility.SidebarVisibilityInputs =
    {
        SidebarVisibility.defaults with
            SubjectKind = TeamMemberKind
            HasTeamScope = true
            ActiveTeamId = Some "team-a"
            PlatformRole = platformRole
            ActiveTeamRole = teamRole
    }

let private platformAdmin =
    teamSubject (Some PlatformRole.PlatformAdmin) (Some TeamRole.Owner)

let private teamOwner = teamSubject None (Some TeamRole.Owner)
let private teamMember = teamSubject None (Some TeamRole.Member)

let private anonymous = {
    SidebarVisibility.defaults with
        SubjectKind = AnonymousKind
}

let private visibleTileIds (inputs: SidebarVisibility.SidebarVisibilityInputs) (modules) (tiles) =
    AdminTiles.visible self id inputs modules tiles |> List.map _.Id

// ─── Composition (573.A / 573.B) ──────────────────────────────────────

let compositionTests =
    testList "Phase 573 — landing composition" [
        test "contributed tiles render in ascending weight order" {
            let shuffled = [ serviceStatusTile; teamsTile; healthTile; usageTile ]

            Expect.equal
                (AdminTiles.ordered self shuffled |> List.map _.Id)
                [
                    "_sdk.tile.teams"
                    "_sdk.tile.usage"
                    "_sdk.tile.health"
                    "_sdk.tile.service-status"
                ]
                "tiles order by Weight ascending, whatever order they were contributed in"
        }

        test "equal weights keep contribution order" {
            let a = tile "a" "_sdk.TeamManager" 10
            let b = tile "b" "_sdk.UsageDashboard" 10
            let c = tile "c" "_sdk.HealthMonitor" 10

            Expect.equal
                (AdminTiles.ordered self [ c; a; b ] |> List.map _.Id)
                [ "c"; "a"; "b" ]
                "the sort is stable, so a contributor's own ordering survives a weight tie"
        }

        test "a registered built-in's tile is rendered" {
            Expect.equal
                (visibleTileIds platformAdmin allRegistered contributed)
                [
                    "_sdk.tile.teams"
                    "_sdk.tile.usage"
                    "_sdk.tile.health"
                    "_sdk.tile.service-status"
                ]
                "every tile whose owning module this admin may reach is on the landing"
        }

        test "a mode-off built-in has no tile" {
            // `NoHealthMonitor` means the module is never composed, so
            // even a stray contribution has no reachable owner. This is
            // the second of the two gates 573.B relies on — the first is
            // that the shell does not contribute the tile at all.
            let ids = visibleTileIds platformAdmin withoutHealthMonitor contributed

            Expect.isFalse
                (ids |> List.contains "_sdk.tile.health")
                "a deployment that does not register the health module shows no health tile"

            Expect.equal
                ids
                [ "_sdk.tile.teams"; "_sdk.tile.usage"; "_sdk.tile.service-status" ]
                "and the remaining tiles are unaffected"
        }

        test "a tile naming no registered module is dropped" {
            let orphan = tile "_sdk.tile.orphan" "_sdk.NeverRegistered" 5

            Expect.isFalse
                (visibleTileIds platformAdmin allRegistered (orphan :: contributed)
                 |> List.contains "_sdk.tile.orphan")
                "an owner id that names nothing is not a reachable destination"
        }

        test "no contributors ⇒ no tiles" {
            Expect.isEmpty
                (visibleTileIds platformAdmin allRegistered [])
                "the fold adds nothing of its own — the SDK never names a tile (GP 9 / GP 13)"
        }
    ]

// ─── Role filtering (573.C) ───────────────────────────────────────────

let roleFilterTests =
    testList "Phase 573 — tiles are filtered by their owning module's gate" [
        test "a platform admin sees platform-scoped and team-scoped tiles" {
            Expect.equal
                (visibleTileIds platformAdmin allRegistered contributed)
                [
                    "_sdk.tile.teams"
                    "_sdk.tile.usage"
                    "_sdk.tile.health"
                    "_sdk.tile.service-status"
                ]
                "PlatformAdmin composes over TeamOwnerAdmin, so both gates admit"
        }

        test "a team Owner sees team tiles and no platform tiles" {
            Expect.equal
                (visibleTileIds teamOwner allRegistered contributed)
                [ "_sdk.tile.teams"; "_sdk.tile.usage" ]
                "the health / service-status modules are PlatformAdminOnly, so their tiles go with them"
        }

        test "a plain Member sees no administration tiles" {
            Expect.isEmpty
                (visibleTileIds teamMember allRegistered contributed)
                "NavRole.TeamOwnerAdmin excludes TeamRole.Member, and every tile here fronts a gated module"
        }

        test "an anonymous caller sees no administration tiles" {
            Expect.isEmpty
                (visibleTileIds anonymous allRegistered contributed)
                "every owning module declares Visibility.visibleToAuthenticated"
        }

        test "tile visibility IS the module navigation decision, not a copy of it" {
            // The safety property of 573.C, stated as the equation
            // rather than as a list: whatever the subject, the tiles are
            // exactly those whose owner survives `visibleIds`. A second,
            // drifting predicate on the landing page would break this
            // without breaking any of the per-subject rows above.
            for inputs in [ platformAdmin; teamOwner; teamMember; anonymous ] do
                let reachable = SidebarVisibility.visibleIds id inputs allRegistered

                Expect.equal
                    (AdminTiles.visible self id inputs allRegistered contributed)
                    (AdminTiles.forOwners self reachable contributed)
                    "visible ≡ forOwners (visibleIds …) — one decision, two call shapes"
        }
    ]

// ─── Click-through (573.C) ────────────────────────────────────────────

let clickThroughTests =
    testList "Phase 573 — click-through navigates to the owning module" [
        test "every rendered tile's owner is a module the caller may navigate to" {
            for inputs in [ platformAdmin; teamOwner; teamMember; anonymous ] do
                for t in AdminTiles.visible self id inputs allRegistered contributed do
                    let owner = allRegistered |> List.tryFind (fun m -> m.Id = t.OwnerModuleId)

                    match owner with
                    | None -> failtestf "tile %s survived with no registered owner" t.Id
                    | Some m ->
                        Expect.isTrue
                            (SidebarVisibility.canNavigateTo id inputs m)
                            (sprintf "tile %s navigates to %s, which this caller may open" t.Id t.OwnerModuleId)
        }

        test "the navigation target is the owning module id verbatim" {
            // `AdminHome.update`'s `OpenModule` passes this straight to
            // `NavigationRequest.request`, so the tile's owner field is
            // the route — no id mangling between the two.
            Expect.equal
                (AdminTiles.visible self id teamOwner allRegistered contributed
                 |> List.map _.OwnerModuleId)
                [ "_sdk.TeamManager"; "_sdk.UsageDashboard" ]
                "the click target is the same id the visibility filter keyed on"
        }
    ]

// ─── Empty states (573.A) ─────────────────────────────────────────────

let emptyStateTests =
    testList "Phase 573 — the landing's two empty states are distinct" [
        test "nothing contributed ⇒ NoTilesContributed" {
            Expect.equal
                (AdminTiles.landingState 0 ([]: AdminTiles.AdminTileFacts list))
                AdminTiles.LandingState.NoTilesContributed
                "an operator-facing state: no module contributed a tile"
        }

        test "tiles contributed but none visible ⇒ NoTilesForSubject" {
            let visibleTiles = AdminTiles.visible self id teamMember allRegistered contributed

            Expect.equal
                (AdminTiles.landingState (List.length contributed) visibleTiles)
                AdminTiles.LandingState.NoTilesForSubject
                "a role-facing state — and NOT the operator one, which would send the reader after a config change they cannot make"
        }

        test "visible tiles ⇒ Tiles, in order" {
            let visibleTiles = AdminTiles.visible self id teamOwner allRegistered contributed

            Expect.equal
                (AdminTiles.landingState (List.length contributed) visibleTiles)
                (AdminTiles.LandingState.Tiles [ teamsTile; usageTile ])
                "the grid case carries the ordered, filtered list"
        }

        test "a contributed-but-invisible tile never reports as uncontributed" {
            // The distinction is the whole point: were the classifier
            // written off the visible list alone, every filtered-out
            // caller would be told the deployment contributes nothing.
            Expect.notEqual
                (AdminTiles.landingState (List.length contributed) ([]: AdminTiles.AdminTileFacts list))
                AdminTiles.LandingState.NoTilesContributed
                "an empty visible list with a non-empty contribution is the role state, not the operator state"
        }
    ]