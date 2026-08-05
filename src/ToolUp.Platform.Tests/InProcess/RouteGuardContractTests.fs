// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.RouteGuardContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 569 — deep-link route-guard contract pack ─────────────────
//
// Hiding a sidebar entry never blocked navigation. Until this phase a
// caller who pasted an admin URL got the module's view — mounted,
// initialised, and backed only by server 403s — because the sidebar's
// filter and the router's dispatch were two different pieces of code and
// only one of them asked the access question. Phase 569 makes them one
// call: `SidebarVisibility.decide` answers "may this caller reach this
// module", `canNavigateTo` is its predicate form, and
// `SidebarVisibility.visible` is now literally
// `List.filter canNavigateTo`.
//
// This pack pins the two things the 570 matrix cannot:
//
//  1. **The reason.** A route guard has to RENDER something, and "not
//     authorised" alone does not tell a caller whether to sign in, pick a
//     team, or ask an administrator. Each denial case is pinned to the
//     gate that produced it, because the denial view's wording — the only
//     user-visible output of this phase — switches on exactly that value.
//  2. **That the two call sites cannot diverge.** `visibleAgreesWithGuard`
//     below re-derives the sidebar from the guard across the whole matrix.
//     It is not a tautology test of an implementation detail: it is the
//     invariant the phase exists to guarantee, and it fails the moment
//     anyone re-introduces a second filter beside `visible`.
//
// **Not the security boundary (GP 4 / GP 12).** Every case here is UX
// coherence over a decision the server enforces independently. A caller
// who defeats this predicate reaches a 403, not data — which is why the
// pack asserts on the rendered *reason*, not on containment.
//
// Fixtures mirror `SidebarVisibilityContractTests`' (restated rather than
// shared: both packs are `private`, and a built-in's `create` cannot run
// under .NET — it touches `Icons`, whose Fable `importDefault` bindings
// throw outside a Fable compilation).

// ─── Fixtures ─────────────────────────────────────────────────────────

let private facts
    (id: string)
    (group: string option)
    (navRole: NavRole option)
    (visibility: SubjectKind -> bool)
    : SidebarVisibility.SidebarModuleFacts =
    {
        Id = id
        Group = group
        NavRole = navRole
        Visibility = visibility
    }

/// A consumer module: no group gate, no `NavRole`, visible to everyone.
/// Reachable by anyone RBAC admits.
let private sales = facts "Sales" None None Visibility.visibleToAll

/// Managed by RBAC like `sales`, and the module the exposure axis hides.
let private marketing = facts "Marketing" None None Visibility.visibleToAll

/// An SDK platform-admin built-in (`PlatformAdminUI` shape).
let private platformAdminModule =
    facts
        "_sdk.PlatformAdmin"
        (Some "Platform Management")
        (Some NavRole.PlatformAdminOnly)
        Visibility.visibleToAuthenticated

/// An SDK team-management built-in (`TeamManagerUI` shape).
let private teamManager =
    facts "_sdk.TeamManager" (Some "Team Management") (Some NavRole.TeamOwnerAdmin) Visibility.visibleToAuthenticated

/// A module that hides itself from anonymous callers via its own
/// `Visibility` — stage 3, not a role gate.
let private membersOnly =
    facts "MembersOnly" None None (Visibility.visibleTo [ UserKind; TeamMemberKind ])

/// The SDK built-in no-active-team landing.
let private awaitingTeam =
    facts NoActiveTeamLanding.moduleId (Some "Welcome") None (Visibility.visibleTo [ UserKind ])

let private registered = [ sales; marketing; membersOnly; teamManager; platformAdminModule ]

let private managed = [ "Sales"; "Marketing" ]

/// Phase 245 exposure — "Marketing" hidden from the active team.
let private marketingHidden: AccessibleModulesResponse = {
    Managed = managed
    Accessible = [ "Sales" ]
    // Phase 637 — this pack is about stages 1-4; the visibility profile is unconfigured, so stage 0 is inert.
    VisibilityProfile = None
}

let private allExposed: AccessibleModulesResponse = {
    Managed = managed
    Accessible = managed
    // Phase 637 — this pack is about stages 1-4; the visibility profile is unconfigured, so stage 0 is inert.
    VisibilityProfile = None
}

// ─── Subjects ─────────────────────────────────────────────────────────

/// A signed-in caller in a team deployment with an active team, holding
/// no platform role and no team role beyond `Member`.
let private teamMember = {
    SidebarVisibility.defaults with
        SubjectKind = TeamMemberKind
        HasTeamScope = true
        ActiveTeamId = Some "team-a"
        ActiveTeamRole = Some TeamRole.Member
        Accessibility = Some allExposed
}

let private teamOwner = {
    teamMember with
        ActiveTeamRole = Some TeamRole.Owner
}

let private platformAdmin = {
    teamMember with
        PlatformRole = Some PlatformRole.PlatformAdmin
        ActiveTeamRole = None
}

/// The signed-out visitor. An anonymous-surface deployment resolves to
/// this; so does the pre-sign-in side of mixed mode.
let private anonymous = {
    SidebarVisibility.defaults with
        SubjectKind = AnonymousKind
        Accessibility = Some allExposed
}

// ─── Helpers ──────────────────────────────────────────────────────────

let private decide inputs m = SidebarVisibility.decide id inputs m

let private expectDenied
    (inputs: SidebarVisibility.SidebarVisibilityInputs)
    (m: SidebarVisibility.SidebarModuleFacts)
    (expected: SidebarVisibility.NavigationDenial)
    (why: string)
    =
    match decide inputs m with
    | SidebarVisibility.NavigationDecision.Permitted ->
        failtestf "'%s' was PERMITTED but must be denied (%s) — %s" m.Id (string expected) why
    | SidebarVisibility.NavigationDecision.Denied actual ->
        Expect.equal
            actual
            expected
            (sprintf "'%s' is denied for the wrong reason, so the denial view would say the wrong thing — %s" m.Id why)

let private expectPermitted
    (inputs: SidebarVisibility.SidebarVisibilityInputs)
    (m: SidebarVisibility.SidebarModuleFacts)
    (why: string)
    =
    match decide inputs m with
    | SidebarVisibility.NavigationDecision.Permitted -> ()
    | SidebarVisibility.NavigationDecision.Denied reason ->
        failtestf "'%s' was denied (%s) but must be reachable — %s" m.Id (string reason) why

// ─── 569.D — the deep-link cases ──────────────────────────────────────

[<Tests>]
let routeGuardTests =
    testList "Phase 569 — deep-link route guard" [

        test "a Member deep-linking a PlatformAdminOnly route is refused, and the module is not initialised" {
            // The headline case. `canNavigateTo` returning false is what
            // every `Init` call site in the shell branches on — the
            // update-side arms run `Init` only inside the permitted
            // branch — so a false here IS "no module boot calls fired".
            expectDenied
                teamMember
                platformAdminModule
                SidebarVisibility.NavigationDenial.RequiresPlatformAdmin
                "a Member pasting an admin URL must get the role-shaped denial"

            Expect.isFalse
                (SidebarVisibility.canNavigateTo id teamMember platformAdminModule)
                "the predicate the shell's Init sites branch on must refuse — a denied module is never initialised"
        }

        test "a platform admin reaches the same route" {
            expectPermitted platformAdmin platformAdminModule "the gate admits the role it names"

            Expect.isTrue
                (SidebarVisibility.canNavigateTo id platformAdmin platformAdminModule)
                "the admin's page must render, and its Init must run"
        }

        test "a Member deep-linking a TeamOwnerAdmin route gets the team-role reason, an Owner does not" {
            expectDenied
                teamMember
                teamManager
                SidebarVisibility.NavigationDenial.RequiresTeamOwnerAdmin
                "the remedy is 'ask an owner', not 'ask a Platform Admin'"

            expectPermitted teamOwner teamManager "an Owner reaches their own team's management tools"

            expectPermitted
                platformAdmin
                teamManager
                "PlatformAdmin composes with TeamOwnerAdmin — the escape that unblocks a team-less admin"
        }

        test "an unknown active-team role fails OPEN, so the guard never denies during the load window" {
            // Mirrors `NavRole.TeamOwnerAdmin`'s documented fail-open.
            // A guard that denied here would flash a denial at a team
            // Owner on every boot and — worse — skip the module's Init.
            let roleStillLoading = {
                teamMember with
                    ActiveTeamRole = None
            }

            expectPermitted
                roleStillLoading
                teamManager
                "an unresolved team role must not strip an Owner of their own tools mid-boot"
        }

        test "an exposure-hidden module is refused for members and honours the admin ShowAllModules toggle" {
            let memberOfTeam = {
                teamMember with
                    Accessibility = Some marketingHidden
            }

            expectDenied
                memberOfTeam
                marketing
                SidebarVisibility.NavigationDenial.NotExposedToTeam
                "Phase 245 exposure denial reads as 'not switched on for this team'"

            let adminSameTeam = {
                platformAdmin with
                    Accessibility = Some marketingHidden
            }

            expectDenied
                adminSameTeam
                marketing
                SidebarVisibility.NavigationDenial.NotExposedToTeam
                "an admin viewing the member view sees what a member sees"

            expectPermitted
                {
                    adminSameTeam with
                        ShowAllModules = true
                }
                marketing
                "Phase 245's admin escape must lift the route guard too, not only the sidebar"
        }

        test "an anonymous caller gets the sign-in-shaped denial whichever gate refused" {
            // Signing in is the only actionable next step for a caller
            // with no identity, and naming the specific gate narrates the
            // role model to the internet for no gain.
            for m in [ platformAdminModule; teamManager; membersOnly ] do
                expectDenied
                    anonymous
                    m
                    SidebarVisibility.NavigationDenial.NotSignedIn
                    "every anonymous denial collapses to the sign-in shape"

            expectPermitted anonymous sales "a visible-to-all module is still reachable anonymously"
        }

        test "a signed-in caller excluded by a module's own Visibility gets the subject-shaped reason" {
            let personalScope = {
                SidebarVisibility.defaults with
                    SubjectKind = UserKind
                    Accessibility = Some allExposed
            }

            let teamOnly = facts "TeamOnly" None None (Visibility.visibleTo [ TeamMemberKind ])

            expectDenied
                personalScope
                teamOnly
                SidebarVisibility.NavigationDenial.NotAvailableToSubject
                "stage 3 is module-author intent, not a role gate — the wording must differ"

            expectPermitted personalScope membersOnly "the same stage admits a module that names UserKind"
        }

        test "the no-active-team collapse denies with the pick-a-team reason and spares the landing" {
            let preTeamPick = {
                SidebarVisibility.defaults with
                    SubjectKind = UserKind
                    HasTeamScope = true
                    ActiveTeamId = None
                    NoActiveTeamLandingId = Some NoActiveTeamLanding.moduleId
            }

            expectDenied
                preTeamPick
                sales
                SidebarVisibility.NavigationDenial.NoActiveTeam
                "the remedy is picking a team, not a role change"

            expectPermitted preTeamPick awaitingTeam "the landing module is the one surface the collapse keeps"
        }

        test "the reasons are reported narrowest-authority-first" {
            // A module refused by BOTH the RBAC stage and a role gate
            // reports the RBAC reason — stage order decides, so the
            // denial names the outermost gate rather than whichever
            // check happens to be written last.
            let doublyRefused =
                facts "Marketing" (Some "Platform Management") (Some NavRole.PlatformAdminOnly) Visibility.visibleToAll

            expectDenied
                {
                    teamMember with
                        Accessibility = Some marketingHidden
                }
                doublyRefused
                SidebarVisibility.NavigationDenial.NotExposedToTeam
                "stage 1 refuses before stage 2, so exposure is the reported reason"
        }
    ]

// ─── 569.A — one predicate, two call sites ────────────────────────────

[<Tests>]
let sharedPredicateTests =
    testList "Phase 569 — sidebar and router share one predicate" [

        test "`visible` is exactly the modules the route guard permits, across the matrix" {
            // The phase's whole claim, stated as an equation. If someone
            // re-introduces a sidebar-only filter — or guards a route on
            // anything the sidebar does not read — a cell here diverges.
            let subjects = [
                "anonymous", anonymous
                "team member", teamMember
                "team owner", teamOwner
                "platform admin", platformAdmin
                "platform admin (show all)",
                {
                    platformAdmin with
                        ShowAllModules = true
                }
                "member, exposure hidden",
                {
                    teamMember with
                        Accessibility = Some marketingHidden
                }
                "pre-team-pick",
                {
                    SidebarVisibility.defaults with
                        SubjectKind = UserKind
                        HasTeamScope = true
                        ActiveTeamId = None
                        NoActiveTeamLandingId = Some NoActiveTeamLanding.moduleId
                }
                "nothing loaded yet", SidebarVisibility.defaults
            ]

            let allModules = registered @ [ awaitingTeam ]

            for name, inputs in subjects do
                let sidebar = SidebarVisibility.visibleIds id inputs allModules

                let routable =
                    allModules
                    |> List.filter (SidebarVisibility.canNavigateTo id inputs)
                    |> List.map _.Id

                Expect.equal
                    sidebar
                    routable
                    (sprintf
                        "[%s] the sidebar and the route guard disagree — a module hidden from the rail must be unreachable by URL, and vice versa"
                        name)
        }

        test "every module the guard denies carries a reason, and every permitted one carries none" {
            // `decide` and `canNavigateTo` are one function; a divergence
            // would mean the shell's render path (which reads the reason)
            // and its Init paths (which read the predicate) could answer
            // differently for the same module.
            for m in registered @ [ awaitingTeam ] do
                for inputs in [ anonymous; teamMember; teamOwner; platformAdmin ] do
                    let permitted = SidebarVisibility.canNavigateTo id inputs m

                    match SidebarVisibility.decide id inputs m with
                    | SidebarVisibility.NavigationDecision.Permitted ->
                        Expect.isTrue permitted (sprintf "'%s' decided Permitted but the predicate said false" m.Id)
                    | SidebarVisibility.NavigationDecision.Denied _ ->
                        Expect.isFalse permitted (sprintf "'%s' decided Denied but the predicate said true" m.Id)
        }
    ]