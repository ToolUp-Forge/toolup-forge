module ToolUp.Platform.Tests.InProcess.AnonymousModeContractTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.PlatformApiHandler

// ─── Phase 55 — AnonymousMode contract pack ──────────────────────────
//
// Pins the three intertwined branches of `computeAccessibleModules`
// (extracted from the `accessibilityApiHandler` body that Phase 55's
// server-side fix landed at `PlatformApiHandler.fs` against forge
// `04257ee`). Post Phase 66 Stream A.1 the dispatch is keyed on
// `AccessContext.Subject` shape, not the retired `PlatformMode`
// enum: the load-bearing invariant is the `Subject = AnonymousSession`
// short-circuit being the first decision branch, with
// `AuthenticatedUser` / `TeamMember` deployments depending on the
// short-circuit NOT firing for them.
//
// The pack is deliberately scoped to the pure helper rather than the
// full `accessibilityApiHandler` so it doesn't carry an `HttpContext`
// / DI / Saturn dependency set. The handler body is now a thin shell
// around the helper.

let private cfg (surfaces: SurfaceProfile list) (moduleNames: string list) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
        ModuleNames = moduleNames
}

let private unrestricted (subject: Subject) : AccessContext = AccessContext.unrestricted subject

let private restricted (ctx: AccessContext) (perms: Map<string, ModulePermission list>) : AccessContext = {
    ctx with
        ModulePermissions = perms
}

[<Tests>]
let tests =
    testList "Phase 55 — AnonymousMode contract" [

        test "Anonymous surface + unrestricted context → every Managed module accessible" {
            let names = [ "Convert"; "Calculate"; "Review" ]
            let config = cfg Surfaces.anonymous names
            let ctx = unrestricted (AnonymousSession "anonymous")

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Managed names "Managed is the configured ModuleNames"
            Expect.equal r.Accessible names "Anonymous short-circuit surfaces every Managed module"
        }

        test "Anonymous surface + restrictive permissions → still every Managed module accessible" {
            // The load-bearing invariant: AnonymousSession short-
            // circuits before `canAccessModule` runs, so even a non-
            // empty ModulePermissions map (which would otherwise gate
            // every module) does NOT hide the sidebar. Without this
            // short-circuit the freshly-signed-up Anonymous shape hides
            // every module — the empty-sidebar bug forge `04257ee`
            // fixed.
            let names = [ "Convert" ]
            let config = cfg Surfaces.anonymous names

            let ctx =
                restricted
                    (unrestricted (AnonymousSession "anon-session"))
                    (Map.ofList [ "Convert", [ ModulePermission.Read ] ])

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Accessible names "Anonymous short-circuits even when ModulePermissions is non-empty"
        }

        test "Anonymous surface + empty ModuleNames → empty Accessible (no managed to surface)" {
            let config = cfg Surfaces.anonymous []
            let ctx = unrestricted (AnonymousSession "anon")
            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Managed [] "no modules registered"
            Expect.equal r.Accessible [] "no modules to surface"
        }

        test "Individual surface + unrestricted context → every Managed module accessible (no regression)" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Surfaces.individual names
            let ctx = unrestricted (AuthenticatedUser "user-1")

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Accessible names "empty ModulePermissions = unrestricted, every module accessible"
        }

        test "Individual surface + restrictive permissions → strict intersection (no regression)" {
            // Establishes the Anonymous fix didn't accidentally
            // loosen the intersection behaviour for Individual surfaces.
            let names = [ "Sales"; "Marketing"; "Finance" ]
            let config = cfg Surfaces.individual names

            let ctx =
                restricted
                    (unrestricted (AuthenticatedUser "user-1"))
                    (Map.ofList [ "Sales", [ ModulePermission.Read ]; "Marketing", [] ])

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Managed names "Managed surfaces every registered module"

            Expect.equal
                r.Accessible
                [ "Sales" ]
                "Marketing has empty perms (revoked); Finance is absent from the map; both excluded"
        }

        test "Team surface + active team + restrictive permissions → strict intersection (no regression)" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Surfaces.team names

            let ctx =
                restricted
                    (unrestricted (TeamMember("user-1", "team-a")))
                    (Map.ofList [ "Sales", [ ModulePermission.Read ] ])

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Accessible [ "Sales" ] "intersection applies for Team surface with active team"
        }

        test "Team surface + no active team → empty Accessible (team-onboarding shape)" {
            // Freshly-signed-up user with Team surface: hasn't created
            // or joined a team yet. Reporting non-team modules as
            // Accessible would be click-then-NoActiveTeam UX. The
            // handler's `noActiveTeamInTeamMode` branch returns empty
            // Accessible while keeping Managed populated, so the
            // client-side TeamManager auto-injection still has a
            // sidebar position for the user to land on.
            let names = [ "Sales" ]
            let config = cfg Surfaces.team names
            let ctx = unrestricted (AuthenticatedUser "user-1")

            let r = computeAccessibleModules config ctx true false

            Expect.equal r.Managed names "Managed is still populated so the auto-injected TeamManager renders"
            Expect.equal r.Accessible [] "team-onboarding shape returns empty Accessible"
        }

        test "MultiTeam surface + no active team → empty Accessible (same shape as Team)" {
            let names = [ "Sales" ]
            let config = cfg Surfaces.multiTeam names
            let ctx = unrestricted (AuthenticatedUser "user-1")

            let r = computeAccessibleModules config ctx true false

            Expect.equal r.Accessible [] "MultiTeam shares the team-onboarding shape with Team"
        }

        test "Anonymous surface short-circuits BEFORE the team-onboarding branch" {
            // Defensive: if the handler ever propagated a stale
            // `noActiveTeamInTeamMode = true` into an Anonymous call,
            // AnonymousSession must still win. (The handler currently
            // computes `noActiveTeamInTeamMode` from `hasTeamScope`
            // which is false for an Anonymous surface; this pin makes
            // the branch ordering explicit at the contract level.)
            let names = [ "Convert" ]
            let config = cfg Surfaces.anonymous names
            let ctx = unrestricted (AnonymousSession "anon")

            let r = computeAccessibleModules config ctx true false

            Expect.equal r.Accessible names "Anonymous wins over noActiveTeamInTeamMode"
        }
    ]

// ─── Phase 245 — per-team module exposure ────────────────────────────
//
// Pins the exposure axis folded into `computeAccessibleModules`: a
// module in `AccessContext.HiddenModules` disappears from the sidebar
// for ordinary members AND for a platform admin acting on the team,
// while a teamless admin (empty HiddenModules) still sees everything.

let private withHidden (ctx: AccessContext) (hidden: string list) : AccessContext = {
    ctx with
        HiddenModules = Set.ofList hidden
}

[<Tests>]
let exposureTests =
    testList "Phase 245 — per-team module exposure" [

        test "Team member: a hidden module is removed from Accessible even with permission" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Surfaces.team names

            let ctx =
                withHidden
                    (restricted
                        (unrestricted (TeamMember("u", "team-a")))
                        (Map.ofList [ "Sales", [ ModulePermission.Read ]; "Marketing", [ ModulePermission.Read ] ]))
                    [ "Marketing" ]

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Accessible [ "Sales" ] "Marketing is hidden in this team despite holding Read"
        }

        test "Team member: nothing hidden → exposure is a no-op (default exposed)" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Surfaces.team names

            let ctx =
                restricted
                    (unrestricted (TeamMember("u", "team-a")))
                    (Map.ofList [ "Sales", [ ModulePermission.Read ]; "Marketing", [ ModulePermission.Read ] ])

            let r = computeAccessibleModules config ctx false false

            Expect.equal r.Accessible names "empty Hidden set ⇒ every permitted module exposed"
        }

        test "Platform admin WITH an active team respects the team's exposure (the bug fix)" {
            // The operator's own account. Pre-245 the admin branch
            // returned every module, so hiding a module had no visible
            // effect for the admin who configured it. Now the admin
            // respects the team's exposure.
            let names = [ "Sales"; "Marketing"; "Finance" ]
            let config = cfg Surfaces.multiTeam names
            let ctx = withHidden (unrestricted (TeamMember("admin", "team-a"))) [ "Finance" ]

            let r = computeAccessibleModules config ctx false true

            Expect.equal r.Accessible [ "Sales"; "Marketing" ] "admin no longer sees the team-hidden Finance module"
        }

        test "Platform admin keeps full permission visibility on exposed modules (no grant needed)" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Surfaces.multiTeam names
            // No ModulePermissions, nothing hidden.
            let ctx = unrestricted (TeamMember("admin", "team-a"))

            let r = computeAccessibleModules config ctx false true

            Expect.equal r.Accessible names "admin sees every exposed module without an explicit permission grant"
        }

        test "Platform admin with NO active team still sees everything (escape preserved)" {
            // No team ⇒ HiddenModules is empty ⇒ the admin branch returns
            // the full Managed list, so a teamless admin is never stranded.
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Surfaces.multiTeam names
            let ctx = unrestricted (AuthenticatedUser "admin")

            let r = computeAccessibleModules config ctx true true

            Expect.equal r.Accessible names "teamless admin sees all managed modules"
        }
    ]