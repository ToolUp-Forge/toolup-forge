// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.CommandPaletteContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 571.D — command-palette visibility parity ─────────────────
//
// A command palette is a second way into every page in the deployment.
// If its entry list were derived independently of the sidebar's, the
// palette would be a hole straight through the Phase 568/569/570
// visibility work: every admin page a Member cannot see in the rail
// would be two keystrokes away, and nothing would fail until someone
// noticed.
//
// `CommandPaletteNav.candidates` is therefore not an independent
// derivation — it is `SidebarVisibility.visible` piped into a page
// expansion. This pack pins that in two directions at once, for every
// subject in the Phase 570 matrix:
//
//   1. the exact destination list, written out by hand per cell (so a
//      change to the expansion rule has to be stated here, not merely
//      tracked); and
//   2. the modules those destinations belong to, compared against
//      `SidebarVisibility.visibleIds` on the same inputs — the OTHER
//      function, so cell 1 cannot pass by both sides drifting together.
//
// The matrix axes are the 570 pack's (subject × mode × exposure),
// restated here rather than shared: those fixtures are `private`, and a
// pack that borrowed them would fail for reasons belonging to the other
// pack. The module set differs deliberately — it carries multi-page
// modules, which the sidebar matrix has no reason to.
//
// **The .NET-constructibility constraint is the 570 one verbatim.**
// `ClientConfig` cannot be built outside Fable (its module initialiser
// reaches `AgGridModuleConfig.community`), and neither can a
// `ReactElement` icon — which is exactly why the candidate derivation
// takes a projection of plain facts and leaves icons to the shell.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private page (route: string) (title: string) : CommandPaletteNav.PalettePageFacts = { Route = route; Title = title }

/// A registered module as the palette sees it. `navRole` / `visibility`
/// are the gate facts; `pages` + `hasPageViews` decide leaf-vs-nested,
/// mirroring `ClientModule.withPages`.
let private moduleFacts
    (id: string)
    (name: string)
    (group: string option)
    (navRole: NavRole option)
    (visibility: SubjectKind -> bool)
    (pages: CommandPaletteNav.PalettePageFacts list)
    : CommandPaletteNav.PaletteModuleFacts =
    {
        Nav = {
            Id = id
            Group = group
            NavRole = navRole
            Visibility = visibility
        }
        Name = name
        Pages = pages
        HasPageViews = not (List.isEmpty pages)
    }

/// Plain single-page consumer module — no pages declared, so a leaf
/// whose destination is the bare module id.
let private sales = moduleFacts "Sales" "Sales" None None Visibility.visibleToAll []

/// The shape the phase exists for: one module, three destinations.
let private salesAnalysis =
    moduleFacts "SalesAnalysis" "Sales Analysis" (Some "Analytics") None Visibility.visibleToAll [
        page "/dataset" "Dataset"
        page "/sku-analysis" "SKU analysis"
        page "/elasticity" "Price elasticity"
    ]

/// Declares `withPages` with exactly ONE page. The rail renders it as a
/// leaf (no expandable subtree), so the palette must offer the bare
/// module id — not `"Forecast/overview"`. This is the boundary case the
/// nesting rule turns on.
let private forecast =
    moduleFacts "Forecast" "Forecast" (Some "Analytics") None Visibility.visibleToAll [ page "/overview" "Overview" ]

/// A platform-admin module with pages — the leak this pack exists to
/// refuse. Every destination under it must be absent for anyone who is
/// not a platform admin.
let private platformAdmin =
    moduleFacts
        "_sdk.PlatformAdmin"
        "Platform Admin"
        (Some "Platform Management")
        (Some NavRole.PlatformAdminOnly)
        Visibility.visibleToAuthenticated
        [ page "/tenants" "Tenants"; page "/api-keys" "API keys" ]

/// Team-scoped management, gated on the active team's Owner/Admin role.
let private teamManager =
    moduleFacts
        "_sdk.TeamManager"
        "Team Manager"
        (Some "Team Management")
        (Some NavRole.TeamOwnerAdmin)
        Visibility.visibleToAuthenticated
        []

let private registered = [ sales; salesAnalysis; forecast; teamManager; platformAdmin ]

let private managed = [ "Sales"; "SalesAnalysis"; "Forecast" ]

let private exposed: AccessibleModulesResponse = {
    Managed = managed
    Accessible = managed
}

/// "Forecast" hidden from this team (Phase 245 exposure).
let private hidden: AccessibleModulesResponse = {
    Managed = managed
    Accessible = [ "Sales"; "SalesAnalysis" ]
}

// ─── Axes (the Phase 570 matrix's, restated) ──────────────────────────

let private anonymousSubject (response: AccessibleModulesResponse option) = {
    SidebarVisibility.defaults with
        Accessibility = response
}

let private member' (response: AccessibleModulesResponse option) = anonymousSubject response

let private teamMemberSubject (response: AccessibleModulesResponse option) = {
    anonymousSubject response with
        ActiveTeamRole = Some TeamRole.Member
}

let private teamOwnerSubject (response: AccessibleModulesResponse option) = {
    anonymousSubject response with
        ActiveTeamRole = Some TeamRole.Owner
}

let private platformAdminSubject (response: AccessibleModulesResponse option) = {
    SidebarVisibility.defaults with
        Accessibility = response
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private anonymousMode (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = AnonymousKind
        HasTeamScope = false
}

let private individualMode (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = UserKind
        HasTeamScope = false
}

let private teamMode (inputs: SidebarVisibility.SidebarVisibilityInputs) = {
    inputs with
        SubjectKind = TeamMemberKind
        HasTeamScope = true
        ActiveTeamId = Some "team-a"
}

// ─── Matrix runner ────────────────────────────────────────────────────

type private Case = {
    Coordinate: string
    Inputs: SidebarVisibility.SidebarVisibilityInputs
    /// The composite sidebar ids the palette must offer, in rail order.
    Expected: string list
}

let private enumerate (ids: string list) =
    match ids with
    | [] -> "(no destinations)"
    | _ -> ids |> List.map (sprintf "'%s'") |> String.concat ", "

/// One matrix cell: the exact destination list, plus the structural
/// parity check against the sidebar fold.
let private check (case: Case) =
    let candidates = CommandPaletteNav.candidates id case.Inputs registered
    let actual = candidates |> List.map _.SidebarId

    if actual <> case.Expected then
        let missing =
            Set.difference (Set.ofList case.Expected) (Set.ofList actual) |> Set.toList

        let unexpected =
            Set.difference (Set.ofList actual) (Set.ofList case.Expected) |> Set.toList

        failtestf
            "Palette destinations mismatch at [%s].\n  Absent but expected: %s\n  Offered but expected hidden: %s\n  Expected: %s\n  Actual:   %s"
            case.Coordinate
            (enumerate missing)
            (enumerate unexpected)
            (enumerate case.Expected)
            (enumerate actual)

    // The parity leg. Compared against `SidebarVisibility.visibleIds` —
    // a different function on the same inputs — so a cell above cannot
    // pass because both the palette and its expectation drifted.
    let sidebarVisible =
        SidebarVisibility.visibleIds (fun (m: CommandPaletteNav.PaletteModuleFacts) -> m.Nav) case.Inputs registered

    Expect.equal
        (candidates |> List.map _.ModuleId |> List.distinct)
        sidebarVisible
        (sprintf
            "[%s] the palette's contributing modules must be exactly the sidebar-visible set, in rail order"
            case.Coordinate)

let private matrixCase coordinate inputs expected = {
    Coordinate = coordinate
    Inputs = inputs
    Expected = expected
}

// Every destination `salesAnalysis` contributes — spelled out once
// because it appears in most cells.
let private salesAnalysisPages = [
    "SalesAnalysis/dataset"
    "SalesAnalysis/sku-analysis"
    "SalesAnalysis/elasticity"
]

let private cases = [
    // ── Anonymous ─────────────────────────────────────────────────────
    // The two authenticated-only built-ins vanish, and with them BOTH
    // admin pages: an anonymous visitor's palette is the anonymous rail.
    matrixCase "anonymous × Anonymous surface × exposed" (anonymousSubject (Some exposed) |> anonymousMode) [
        "Sales"
        yield! salesAnalysisPages
        "Forecast"
    ]

    // ── Individual ────────────────────────────────────────────────────
    // No team scope ⇒ `TeamOwnerAdmin` fails open (Phase 568), so the
    // team manager is offered; `PlatformAdminOnly` does not.
    matrixCase "authenticated Member × Individual surface × exposed" (member' (Some exposed) |> individualMode) [
        "Sales"
        yield! salesAnalysisPages
        "Forecast"
        "_sdk.TeamManager"
    ]

    // Phase 245 exposure removes a module and therefore every
    // destination under it — not just its first page.
    matrixCase "authenticated Member × Individual surface × Forecast hidden" (member' (Some hidden) |> individualMode) [
        "Sales"
        yield! salesAnalysisPages
        "_sdk.TeamManager"
    ]

    // The admin's palette gains both admin pages, and nothing else
    // moves.
    matrixCase "platform admin × Individual surface × exposed" (platformAdminSubject (Some exposed) |> individualMode) [
        "Sales"
        yield! salesAnalysisPages
        "Forecast"
        "_sdk.TeamManager"
        "_sdk.PlatformAdmin/tenants"
        "_sdk.PlatformAdmin/api-keys"
    ]

    // ── Team surface, active team picked ──────────────────────────────
    // The case the phase's acceptance criterion names: a Member's
    // palette must not contain an admin page.
    matrixCase "team Member × Team surface × exposed" (teamMemberSubject (Some exposed) |> teamMode) [
        "Sales"
        yield! salesAnalysisPages
        "Forecast"
    ]

    matrixCase "team Owner × Team surface × exposed" (teamOwnerSubject (Some exposed) |> teamMode) [
        "Sales"
        yield! salesAnalysisPages
        "Forecast"
        "_sdk.TeamManager"
    ]

    matrixCase "team Owner × Team surface × Forecast hidden" (teamOwnerSubject (Some hidden) |> teamMode) [
        "Sales"
        yield! salesAnalysisPages
        "_sdk.TeamManager"
    ]

    matrixCase "platform admin × Team surface × Forecast hidden" (platformAdminSubject (Some hidden) |> teamMode) [
        "Sales"
        yield! salesAnalysisPages
        "_sdk.TeamManager"
        "_sdk.PlatformAdmin/tenants"
        "_sdk.PlatformAdmin/api-keys"
    ]

    // The Phase 245 admin escape reaches the palette for free — it is a
    // stage of the shared fold, not something the palette re-implements.
    matrixCase
        "platform admin × Team surface × Forecast hidden + ShowAllModules"
        {
            teamMode (platformAdminSubject (Some hidden)) with
                ShowAllModules = true
        }
        [
            "Sales"
            yield! salesAnalysisPages
            "Forecast"
            "_sdk.TeamManager"
            "_sdk.PlatformAdmin/tenants"
            "_sdk.PlatformAdmin/api-keys"
        ]

    // Pre-load: the RBAC fetch has not resolved, so stage 1 is
    // permissive — same as the rail, which shows everything rather than
    // flickering empty.
    matrixCase "authenticated Member × Individual surface × RBAC not yet loaded" (member' None |> individualMode) [
        "Sales"
        yield! salesAnalysisPages
        "Forecast"
        "_sdk.TeamManager"
    ]
]

[<Tests>]
let paletteVisibilityTests =
    testList "Phase 571 — command-palette visibility parity" [

        for case in cases do
            test case.Coordinate { check case }

        test "the parity matrix spans every subject it claims to" {
            Expect.equal (List.length cases) 10 "ten parity cells"

            Expect.equal
                (cases |> List.map _.Coordinate |> List.distinct |> List.length)
                (List.length cases)
                "coordinates are unique — a duplicated label hides a cell"
        }

        test "no admin destination reaches a caller who is not a platform admin" {
            // The acceptance criterion, stated directly rather than read
            // off the matrix: whatever the mode, whatever the exposure, a
            // non-admin's palette contains no `_sdk.PlatformAdmin`
            // destination. A future edit that widened the candidate
            // source to the raw registration list would fail here first.
            let nonAdmins = [
                "anonymous", (anonymousSubject (Some exposed) |> anonymousMode)
                "member (individual)", (member' (Some exposed) |> individualMode)
                "member (rbac unloaded)", (member' None |> individualMode)
                "team member", (teamMemberSubject (Some exposed) |> teamMode)
                "team owner", (teamOwnerSubject (Some exposed) |> teamMode)
            ]

            for label, inputs in nonAdmins do
                let offered =
                    CommandPaletteNav.candidates id inputs registered
                    |> List.filter (fun c -> c.ModuleId = platformAdmin.Nav.Id)

                Expect.isEmpty offered (sprintf "%s was offered a platform-admin destination" label)
        }
    ]

// ─── The page-expansion rule (571.B) ──────────────────────────────────
//
// The matrix pins WHICH modules contribute; these pin what each one
// contributes. The rule mirrors the rail's nesting exactly, and the
// two-page threshold is the part that is easy to get wrong.

[<Tests>]
let expansionTests =
    testList "Phase 571 — palette page expansion" [

        test "a module with no declared pages is one destination, the bare module id" {
            Expect.equal
                (CommandPaletteNav.entriesFor sales |> List.map _.SidebarId)
                [ "Sales" ]
                "a leaf contributes its module id"
        }

        test "a module with ONE declared page is still a leaf" {
            // The rail renders no expandable subtree for a single page,
            // so the palette must not offer a composite id the sidebar
            // never shows. `ModuleSelected "Forecast"` resolves the page
            // through `defaultPageRoute` anyway.
            let entries = CommandPaletteNav.entriesFor forecast

            Expect.equal (entries |> List.map _.SidebarId) [ "Forecast" ] "single-page module contributes the bare id"
            Expect.equal entries.Head.PageRoute None "and carries no page route"
        }

        test "a multi-page module contributes one destination per page, in declaration order" {
            let entries = CommandPaletteNav.entriesFor salesAnalysis

            Expect.equal (entries |> List.map _.SidebarId) salesAnalysisPages "composite ids, declaration order"

            Expect.equal
                (entries |> List.map _.PageTitle)
                [ Some "Dataset"; Some "SKU analysis"; Some "Price elasticity" ]
                "each destination carries its page title"

            Expect.equal
                (entries |> List.map _.ModuleName |> List.distinct)
                [ "Sales Analysis" ]
                "every page keeps the owning module's display name"

            Expect.equal (entries |> List.map _.Group |> List.distinct) [ Some "Analytics" ] "…and its group label"
        }

        test "the multi-page parent is not a separate destination" {
            // It navigates in the narrow rail, but it navigates to the
            // first page — already the first entry. Listing it too would
            // show the same destination twice under two names.
            Expect.isFalse
                (CommandPaletteNav.entriesFor salesAnalysis
                 |> List.exists (fun e -> e.SidebarId = "SalesAnalysis"))
                "no bare module id alongside the page destinations"
        }

        test "declared pages without `withPages` do not nest" {
            // `HasPageViews = false` with pages declared is the legacy
            // single-view shape: the shell renders one view for every
            // route, so there is one destination.
            let legacy = {
                salesAnalysis with
                    HasPageViews = false
            }

            Expect.equal
                (CommandPaletteNav.entriesFor legacy |> List.map _.SidebarId)
                [ "SalesAnalysis" ]
                "no PageViews ⇒ leaf"
        }

        test "search text spans module name and page title" {
            Expect.equal
                (CommandPaletteNav.entriesFor salesAnalysis
                 |> List.map CommandPaletteNav.searchText)
                [
                    "Sales Analysis Dataset"
                    "Sales Analysis SKU analysis"
                    "Sales Analysis Price elasticity"
                ]
                "both halves are searchable — 'sku' and 'sales' must each reach the page"

            Expect.equal
                (CommandPaletteNav.searchText (CommandPaletteNav.entriesFor sales).Head)
                "Sales"
                "leaf is its name"
        }
    ]

// ─── Fuzzy matching (571.C) ───────────────────────────────────────────

[<Tests>]
let fuzzyTests =
    testList "Phase 571 — palette fuzzy matching" [

        test "an empty query matches everything at score 0" {
            Expect.equal (CommandPaletteNav.score "" "Sales Analysis") (Some 0) "empty"
            Expect.equal (CommandPaletteNav.score "   " "Sales Analysis") (Some 0) "whitespace-only"
        }

        test "a subsequence matches; a non-subsequence does not" {
            Expect.isSome (CommandPaletteNav.score "sls" "Sales Analysis") "scattered characters in order match"
            Expect.isNone (CommandPaletteNav.score "zq" "Sales Analysis") "absent characters do not"
            Expect.isNone (CommandPaletteNav.score "sela" "Sales") "out-of-order characters do not"
        }

        test "matching is case-insensitive and ignores whitespace in the query" {
            Expect.isSome (CommandPaletteNav.score "SALES" "sales analysis") "case folds both ways"

            Expect.equal
                (CommandPaletteNav.score "sal an" "Sales Analysis")
                (CommandPaletteNav.score "salan" "Sales Analysis")
                "a space in the query is not a character to match"
        }

        test "a prefix outranks a mid-word match" {
            let prefix = CommandPaletteNav.score "ana" "Analysis" |> Option.defaultValue 0

            let midWord =
                CommandPaletteNav.score "ana" "Finance Analytics" |> Option.defaultValue 0

            Expect.isGreaterThan prefix midWord "the leading-gap penalty favours an earlier match"
        }

        test "a word-start match outranks a scattered one of the same length" {
            let wordStart =
                CommandPaletteNav.score "sa" "Sales Analysis" |> Option.defaultValue 0

            let scattered =
                CommandPaletteNav.score "sa" "Usage dashboard" |> Option.defaultValue 0

            Expect.isGreaterThan wordStart scattered "boundary + contiguity bonuses compound"
        }

        test "rank drops non-matches and orders by score" {
            let names = [ "Team Manager"; "Sales Analysis"; "Health Monitor" ]

            Expect.equal (CommandPaletteNav.rank id "sales" names) [ "Sales Analysis" ] "only the match survives"

            Expect.equal
                (CommandPaletteNav.rank id "" names)
                names
                "an empty query returns the input list verbatim, in rail order"

            Expect.isEmpty (CommandPaletteNav.rank id "zzz" names) "no matches ⇒ empty"
        }

        test "rank is stable — equal scores keep input order" {
            // Two identical texts can only be told apart by position, so
            // a sort that reordered them would make the palette's row
            // order depend on the sort's internals.
            let names = [ "Reports A"; "Reports B"; "Reports C" ]
            Expect.equal (CommandPaletteNav.rank id "reports" names) names "ties preserve registration order"
        }

        test "the ranked candidate list is the ranked candidate list" {
            // `filter` is `rank searchText` — pinned so a future
            // convenience wrapper cannot quietly change the projection.
            let cs = CommandPaletteNav.entriesFor salesAnalysis

            Expect.equal
                (CommandPaletteNav.filter "sku" cs |> List.map _.SidebarId)
                [ "SalesAnalysis/sku-analysis" ]
                "a page title reaches its own page"

            Expect.equal (CommandPaletteNav.filter "" cs) cs "an empty query is the identity"
        }
    ]

// ─── Overlay state (571.C) ────────────────────────────────────────────

[<Tests>]
let paletteStateTests =
    testList "Phase 571 — palette overlay state" [

        test "closed is shut, empty, and pointing at the first row" {
            Expect.isFalse CommandPaletteNav.closed.IsOpen "shut"
            Expect.equal CommandPaletteNav.closed.Query "" "empty query"
            Expect.equal CommandPaletteNav.closed.Highlight 0 "first row"
        }

        test "opening does not carry the previous query" {
            // A shortcut whose result depends on when it was last used
            // is a shortcut nobody trusts.
            Expect.equal CommandPaletteNav.opened.Query "" "opened is closed + IsOpen"
            Expect.isTrue CommandPaletteNav.opened.IsOpen "…and open"
        }

        test "a new query resets the highlight" {
            let state =
                CommandPaletteNav.opened
                |> CommandPaletteNav.moveHighlight 3
                |> CommandPaletteNav.withQuery "sku"

            Expect.equal state.Highlight 0 "the ranking moved, so the cursor returns to the top"
            Expect.equal state.Query "sku" "query recorded"
            Expect.isTrue state.IsOpen "still open"
        }

        test "the highlight counter is unbounded and wraps at render" {
            let moved n =
                CommandPaletteNav.opened |> CommandPaletteNav.moveHighlight n

            Expect.equal (moved 5).Highlight 5 "the model does not clamp — it cannot know the count"

            Expect.equal (CommandPaletteNav.highlightIndex 3 5) 2 "wraps forward"
            Expect.equal (CommandPaletteNav.highlightIndex 3 -1) 2 "ArrowUp from the first row lands on the last"
            Expect.equal (CommandPaletteNav.highlightIndex 3 -4) 2 "…and keeps wrapping backwards"
            Expect.equal (CommandPaletteNav.highlightIndex 3 0) 0 "identity in range"
        }

        test "an empty result list resolves to index 0 rather than dividing by zero" {
            Expect.equal (CommandPaletteNav.highlightIndex 0 7) 0 "no rows ⇒ no row to activate"
            Expect.equal (CommandPaletteNav.highlightIndex -1 7) 0 "defensive on a negative count"
        }
    ]