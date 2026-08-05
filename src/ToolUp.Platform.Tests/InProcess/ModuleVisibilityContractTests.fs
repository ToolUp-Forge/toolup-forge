// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModuleVisibilityContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 637 — module-visibility profile contract pack ─────────────
//
// Three things are pinned here, and they are the three that would
// otherwise be prose:
//
//   1. **The narrowing walk.** Layers compose monotonically — an inner
//      scope may only remove. This is where the mechanism differs from
//      the feature-flag walk it borrows its scope set from (first-hit
//      wins there, so an inner layer OVERRIDES), and a regression to
//      flag semantics would be invisible until the day a user-scoped
//      profile re-admitted a module the deployment excluded.
//   2. **The ungoverned escape.** A module id absent from the
//      registered set is admitted unconditionally, which is what keeps
//      an operator saving their first allowlist from hiding the
//      `_sdk.` admin surface they saved it FROM. The bug this prevents
//      is a lock-out, so it is asserted directly rather than inferred
//      from the fold.
//   3. **Stage 0 of the visibility fold**, including the two
//      compositions worth stating: `ShowAllModules` does NOT lift a
//      profile (unlike RBAC, which it exists to see past), and the
//      denial a curated-out module reports is the curation, not
//      whichever later gate would also have refused it.
//
// The fold cases reuse `SidebarVisibility.defaults` as their baseline
// for the same reason `SidebarVisibilityContractTests` does: every stage
// is a no-op against it, so a case states exactly the fields it is
// about.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private facts (id: string) : SidebarVisibility.SidebarModuleFacts = {
    Id = id
    Group = None
    NavRole = None
    Visibility = fun _ -> true
}

let private gated (id: string) (navRole: NavRole) : SidebarVisibility.SidebarModuleFacts = {
    facts id with
        NavRole = Some navRole
}

/// The deployment's registered (app-domain) modules. `_sdk.admin` below
/// deliberately is NOT in this list — that is the point of the
/// ungoverned-escape cases, and it mirrors the shipped reality that SDK
/// built-ins carry `_sdk.` ids absent from `ServerConfig.ModuleNames`.
let private registered = [ "sales"; "forecast"; "inventory" ]

let private profile (scope: FlagScope) (rule: ModuleVisibilityRule) : ModuleVisibilityProfile = {
    Scope = scope
    Rule = rule
    ExcludedEntryIds = []
    Note = None
}

// ─── 1. The narrowing walk ────────────────────────────────────────────

let resolutionTests =
    testList "ModuleVisibility.resolve" [
        test "no layers resolves to None — the unconfigured deployment" {
            Expect.isNone
                (ModuleVisibility.resolve registered [])
                "a deployment declaring no profile must resolve to None, not to an inert resolution — see the doc-comment for why the distinction is load-bearing"
        }

        test "an Allow layer keeps only the named ids, in the declared order" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Allow [ "forecast"; "sales" ])
                ]

            Expect.equal
                (resolved |> Option.map _.SelectedModuleIds)
                (Some [ "forecast"; "sales" ])
                "an allowlist is an ORDERED curation — the operator's order survives, and `inventory` is dropped"
        }

        test "a Deny layer removes the named ids and preserves registration order" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Deny [ "forecast" ])
                ]

            Expect.equal
                (resolved |> Option.map _.SelectedModuleIds)
                (Some [ "sales"; "inventory" ])
                "a deny-list subtracts; nothing else moves"
        }

        test "an id naming no registered module is ignored, not invented" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Allow [ "sales"; "retired-module" ])
                ]

            Expect.equal
                (resolved |> Option.map _.SelectedModuleIds)
                (Some [ "sales" ])
                "a profile can outlive the composition it was written against — a stale id must vanish rather than resurrect a module"
        }

        test "an inner layer NARROWS the outer one" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Allow [ "sales"; "forecast" ])
                    profile (FlagScope.Team "t1") (ModuleVisibilityRule.Allow [ "sales" ])
                ]

            Expect.equal (resolved |> Option.map _.SelectedModuleIds) (Some [ "sales" ]) "team narrows platform"
        }

        test "an inner layer CANNOT re-admit what an outer layer excluded" {
            // The whole reason this walk is not `FlagEvaluator`'s. Under
            // first-hit-wins the user layer would win outright and
            // `inventory` would come back — i.e. the least authoritative
            // scope would widen the operator's curation.
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Allow [ "sales"; "forecast" ])
                    profile (FlagScope.User "u1") (ModuleVisibilityRule.Allow [ "sales"; "inventory" ])
                ]

            Expect.equal
                (resolved |> Option.map _.SelectedModuleIds)
                (Some [ "sales" ])
                "a user-scoped profile may only narrow — `inventory`, excluded at platform scope, must stay excluded"
        }

        test "page exclusions union across layers" {
            let resolved =
                ModuleVisibility.resolve registered [
                    {
                        profile FlagScope.Platform (ModuleVisibilityRule.Deny []) with
                            ExcludedEntryIds = [ "sales/reports" ]
                    }
                    {
                        profile (FlagScope.Team "t1") (ModuleVisibilityRule.Deny []) with
                            ExcludedEntryIds = [ "forecast/detail"; "sales/reports" ]
                    }
                ]

            Expect.equal
                (resolved |> Option.map _.ExcludedEntryIds)
                (Some [ "sales/reports"; "forecast/detail" ])
                "exclusions accumulate and de-duplicate; an inner layer adds, never removes"
        }

        test "ContributingScopes names every layer that spoke, outermost-first" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Deny [])
                    profile (FlagScope.Team "t1") (ModuleVisibilityRule.Deny [])
                ]

            Expect.equal
                (resolved |> Option.map _.ContributingScopes)
                (Some [ FlagScope.Platform; FlagScope.Team "t1" ])
                "an operator asking 'where did this come from' reads this field"
        }
    ]

// ─── 2. The ungoverned escape ─────────────────────────────────────────

let ungovernedTests =
    testList "ModuleVisibility.admitsModule — the ungoverned escape" [
        test "an id outside the registered set is admitted by a strict allowlist" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Allow [ "sales" ])
                ]
                |> Option.get

            Expect.isTrue
                (ModuleVisibility.admitsModule resolved "_sdk.admin")
                "SDK built-ins carry `_sdk.` ids absent from ServerConfig.ModuleNames — if an allowlist excluded them, the first profile an operator saved would hide the surface they saved it from"
        }

        test "a governed id absent from the selection is refused" {
            let resolved =
                ModuleVisibility.resolve registered [
                    profile FlagScope.Platform (ModuleVisibilityRule.Allow [ "sales" ])
                ]
                |> Option.get

            Expect.isFalse
                (ModuleVisibility.admitsModule resolved "forecast")
                "the escape is scoped to ids the profile was never stated over"
        }

        test "the None resolution admits everything" {
            Expect.isTrue
                (ModuleVisibility.admitsModuleOpt None "anything")
                "an unconfigured deployment must be byte-for-byte unchanged (GP 11)"
        }

        test "page exclusions gate composite entry ids" {
            let resolved =
                ModuleVisibility.resolve registered [
                    {
                        profile FlagScope.Platform (ModuleVisibilityRule.Deny []) with
                            ExcludedEntryIds = [ "sales/reports" ]
                    }
                ]
                |> Option.get

            Expect.isFalse (ModuleVisibility.admitsEntry resolved "sales/reports") "the named page entry is excluded"
            Expect.isTrue (ModuleVisibility.admitsEntry resolved "sales/summary") "its siblings are not"
        }
    ]

// ─── 3. The server-side scope walk ────────────────────────────────────

let scopeWalkTests =
    testList "ModuleVisibilityResolver.scopesFor" [
        test "a team member's walk is Platform → Team → User" {
            Expect.equal
                (ModuleVisibilityResolver.scopesFor (AccessContext.unrestricted (TeamMember("u1", "t1"))))
                [ FlagScope.Platform; FlagScope.Team "t1"; FlagScope.User "u1" ]
                "outermost-first — this is FlagEvaluator's scope SET in the opposite order, because these layers compose rather than override"
        }

        test "an authenticated user's walk is Platform → User" {
            Expect.equal
                (ModuleVisibilityResolver.scopesFor (AccessContext.unrestricted (AuthenticatedUser "u1")))
                [ FlagScope.Platform; FlagScope.User "u1" ]
                "no team layer outside team scope"
        }

        test "anonymous and claim-bearer callers read Platform only" {
            Expect.equal
                (ModuleVisibilityResolver.scopesFor (AccessContext.unrestricted (AnonymousSession "anon")))
                [ FlagScope.Platform ]
                "an anonymous caller has no persistent scope to narrow by"
        }
    ]

// ─── 4. Stage 0 of the visibility fold ────────────────────────────────

let private withProfile (rule: ModuleVisibilityRule) = {
    SidebarVisibility.defaults with
        VisibilityProfile = ModuleVisibility.resolve registered [ profile FlagScope.Platform rule ]
        SubjectKind = UserKind
}

let foldTests =
    testList "SidebarVisibility — stage 0 (visibility profile)" [
        test "an unconfigured deployment sees every module" {
            let modules = [ facts "sales"; facts "forecast"; facts "inventory" ]

            Expect.equal
                (SidebarVisibility.visibleIds
                    id
                    {
                        SidebarVisibility.defaults with
                            SubjectKind = UserKind
                    }
                    modules)
                [ "sales"; "forecast"; "inventory" ]
                "no profile ⇒ the input list back, unchanged (GP 11)"
        }

        test "a curated deployment sees exactly the selected subset" {
            let modules = [ facts "sales"; facts "forecast"; facts "inventory" ]

            Expect.equal
                (SidebarVisibility.visibleIds
                    id
                    (withProfile (ModuleVisibilityRule.Allow [ "sales"; "inventory" ]))
                    modules)
                [ "sales"; "inventory" ]
                "the sidebar is the curated set — in the module list's own order, since the fold only ever filters"
        }

        test "SDK built-ins survive an allowlist that names none of them" {
            let modules = [ facts "sales"; facts "_sdk.platform-admin" ]

            Expect.equal
                (SidebarVisibility.visibleIds id (withProfile (ModuleVisibilityRule.Allow [ "sales" ])) modules)
                [ "sales"; "_sdk.platform-admin" ]
                "the ungoverned escape reaches the fold — otherwise an operator's first allowlist locks them out of the admin surface"
        }

        test "ShowAllModules does NOT lift a profile" {
            let modules = [ facts "sales"; facts "forecast" ]

            let inputs = {
                withProfile (ModuleVisibilityRule.Allow [ "sales" ]) with
                    PlatformRole = Some PlatformRole.PlatformAdmin
                    ShowAllModules = true
            }

            Expect.equal
                (SidebarVisibility.visibleIds id inputs modules)
                [ "sales" ]
                "Phase 245's escape exists to see past per-team RBAC exposure; a profile is the operator's own curation, changed by editing it — not by a debug toggle that would make one admin's sidebar silently disagree with everyone else's"
        }

        test "NavRole gating still applies WITHIN the selected subset" {
            let modules = [ facts "sales"; gated "forecast" NavRole.PlatformAdminOnly ]

            Expect.equal
                (SidebarVisibility.visibleIds
                    id
                    (withProfile (ModuleVisibilityRule.Allow [ "sales"; "forecast" ]))
                    modules)
                [ "sales" ]
                "a profile admitting a module does not clear its role gate — the stages compose, each only removing"
        }

        test "a curated-out module reports the curation as its denial" {
            let inputs = withProfile (ModuleVisibilityRule.Allow [ "sales" ])

            Expect.equal
                (SidebarVisibility.decide id inputs (facts "forecast"))
                (SidebarVisibility.NavigationDecision.Denied SidebarVisibility.NavigationDenial.NotInVisibilityProfile)
                "not NotExposedToTeam — the remedy differs, and the denial view's wording is chosen from this value"
        }

        test "an anonymous caller's curation denial still collapses to NotSignedIn" {
            let inputs = {
                withProfile (ModuleVisibilityRule.Allow [ "sales" ]) with
                    SubjectKind = AnonymousKind
            }

            Expect.equal
                (SidebarVisibility.decide id inputs (facts "forecast"))
                (SidebarVisibility.NavigationDecision.Denied SidebarVisibility.NavigationDenial.NotSignedIn)
                "naming the gate to a caller with no identity narrates the deployment's shape to the internet for no gain — the pre-existing collapse must cover the new stage too"
        }
    ]

// ─── 5. Route attribution (the hardening registry) ────────────────────

let routeRegistryTests =
    testList "ModuleRouteRegistry" [
        test "longest declared prefix wins" {
            let registry =
                ModuleVisibilityRoutes.ModuleRouteRegistry.create registered [
                    "sales", "/api/sales/"
                    "forecast", "/api/sales/forecast/"
                ]

            Expect.equal
                (ModuleVisibilityRoutes.ModuleRouteRegistry.owningModule registry "/api/sales/forecast/run")
                (Some "forecast")
                "a module mounted under a sub-tree of another's prefix must win, else its declaration is unreachable"
        }

        test "an unattributable path owns to no module" {
            let registry =
                ModuleVisibilityRoutes.ModuleRouteRegistry.create registered [ "sales", "/api/sales/" ]

            Expect.isNone
                (ModuleVisibilityRoutes.ModuleRouteRegistry.owningModule registry "/api/TeamApi/GetMyTeams")
                "hardening can only reach routes a module DECLARES — stated on EnforcedModuleVisibility rather than silently discovered"
        }

        test "a blank prefix declaration is dropped, not treated as a wildcard" {
            let registry =
                ModuleVisibilityRoutes.ModuleRouteRegistry.create registered [ "sales", "  " ]

            Expect.isNone
                (ModuleVisibilityRoutes.ModuleRouteRegistry.owningModule registry "/api/anything")
                "an empty prefix would claim every path, turning one mis-declaration into a deployment-wide 404"
        }

        test "prefix matching is case-insensitive" {
            let registry =
                ModuleVisibilityRoutes.ModuleRouteRegistry.create registered [ "sales", "/api/Sales/" ]

            Expect.equal
                (ModuleVisibilityRoutes.ModuleRouteRegistry.owningModule registry "/API/SALES/list")
                (Some "sales")
                "clients differ on path casing — same normalisation SurfaceRequirementRegistry applies"
        }
    ]