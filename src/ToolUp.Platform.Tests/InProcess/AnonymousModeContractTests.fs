module ToolUp.Platform.Tests.InProcess.AnonymousModeContractTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.PlatformApiHandler

// ─── Phase 55 — AnonymousMode contract pack ──────────────────────────
//
// Pins the three intertwined branches of `computeAccessibleModules`
// (extracted from the `accessibilityApiHandler` body that Phase 55's
// server-side fix landed at `PlatformApiHandler.fs` against forge
// `04257ee`). The contract this pack pins is the source-of-truth
// behaviour Phase 66 Stream D will expand / replace: until that
// ships, every Anonymous-mode deployment depends on the
// `Mode = Anonymous → all Managed visible` short-circuit being the
// first decision branch, and Individual / Team / MultiTeam deployments
// depend on the short-circuit NOT firing for them.
//
// The pack is deliberately scoped to the pure helper rather than the
// full `accessibilityApiHandler` so it doesn't carry an `HttpContext`
// / DI / Saturn dependency set. The handler body is now a thin shell
// around the helper — every Mode-specific decision lives in the
// helper.

let private cfg (mode: PlatformMode) (moduleNames: string list) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = Surfaces.fromLegacyMode mode
        ModuleNames = moduleNames
}

let private unrestricted (mode: PlatformMode) (userId: string) (teamId: string option) : AccessContext =
    AccessContext.unrestricted (Subject.fromLegacyMode mode userId teamId)

let private restricted (ctx: AccessContext) (perms: Map<string, ModulePermission list>) : AccessContext = {
    ctx with
        ModulePermissions = perms
}

[<Tests>]
let tests =
    testList "Phase 55 — AnonymousMode contract" [

        test "Anonymous mode + unrestricted context → every Managed module accessible" {
            let names = [ "Convert"; "Calculate"; "Review" ]
            let config = cfg Anonymous names
            let ctx = unrestricted Anonymous "anonymous" None

            let r = computeAccessibleModules config ctx false

            Expect.equal r.Managed names "Managed is the configured ModuleNames"
            Expect.equal r.Accessible names "Anonymous short-circuit surfaces every Managed module"
        }

        test "Anonymous mode + restrictive permissions → still every Managed module accessible" {
            // The load-bearing invariant: Anonymous short-circuits
            // before `canAccessModule` runs, so even a non-empty
            // ModulePermissions map (which would otherwise gate every
            // module) does NOT hide the sidebar. Without this
            // short-circuit the freshly-signed-up Anonymous shape
            // hides every module — the empty-sidebar bug forge
            // `04257ee` fixed.
            let names = [ "Convert" ]
            let config = cfg Anonymous names

            let ctx =
                restricted
                    (unrestricted Anonymous "anon-session" None)
                    (Map.ofList [ "Convert", [ ModulePermission.Read ] ])

            let r = computeAccessibleModules config ctx false

            Expect.equal r.Accessible names "Anonymous short-circuits even when ModulePermissions is non-empty"
        }

        test "Anonymous mode + empty ModuleNames → empty Accessible (no managed to surface)" {
            let config = cfg Anonymous []
            let ctx = unrestricted Anonymous "anon" None
            let r = computeAccessibleModules config ctx false

            Expect.equal r.Managed [] "no modules registered"
            Expect.equal r.Accessible [] "no modules to surface"
        }

        test "Individual mode + unrestricted context → every Managed module accessible (no regression)" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Individual names
            let ctx = unrestricted Individual "user-1" None

            let r = computeAccessibleModules config ctx false

            Expect.equal r.Accessible names "empty ModulePermissions = unrestricted, every module accessible"
        }

        test "Individual mode + restrictive permissions → strict intersection (no regression)" {
            // Establishes the Anonymous fix didn't accidentally
            // loosen the intersection behaviour for Individual mode.
            let names = [ "Sales"; "Marketing"; "Finance" ]
            let config = cfg Individual names

            let ctx =
                restricted
                    (unrestricted Individual "user-1" None)
                    (Map.ofList [ "Sales", [ ModulePermission.Read ]; "Marketing", [] ])

            let r = computeAccessibleModules config ctx false

            Expect.equal r.Managed names "Managed surfaces every registered module"

            Expect.equal
                r.Accessible
                [ "Sales" ]
                "Marketing has empty perms (revoked); Finance is absent from the map; both excluded"
        }

        test "Team mode + active team + restrictive permissions → strict intersection (no regression)" {
            let names = [ "Sales"; "Marketing" ]
            let config = cfg Team names

            let ctx =
                restricted
                    (unrestricted Team "user-1" (Some "team-a"))
                    (Map.ofList [ "Sales", [ ModulePermission.Read ] ])

            let r = computeAccessibleModules config ctx false

            Expect.equal r.Accessible [ "Sales" ] "intersection applies for Team mode with active team"
        }

        test "Team mode + no active team → empty Accessible (team-onboarding shape)" {
            // Freshly-signed-up user in Team mode: hasn't created
            // or joined a team yet. Reporting non-team modules as
            // Accessible would be click-then-NoActiveTeam UX. The
            // handler's `noActiveTeamInTeamMode` branch returns
            // empty Accessible while keeping Managed populated, so
            // the client-side TeamManager auto-injection still has
            // a sidebar position for the user to land on.
            let names = [ "Sales" ]
            let config = cfg Team names
            let ctx = unrestricted Team "user-1" None

            let r = computeAccessibleModules config ctx true

            Expect.equal r.Managed names "Managed is still populated so the auto-injected TeamManager renders"
            Expect.equal r.Accessible [] "team-onboarding shape returns empty Accessible"
        }

        test "MultiTeam mode + no active team → empty Accessible (same shape as Team)" {
            let names = [ "Sales" ]
            let config = cfg MultiTeam names
            let ctx = unrestricted MultiTeam "user-1" None

            let r = computeAccessibleModules config ctx true

            Expect.equal r.Accessible [] "MultiTeam shares the team-onboarding shape with Team"
        }

        test "Anonymous mode short-circuits BEFORE the team-onboarding branch" {
            // Defensive: if the handler ever propagated a stale
            // `noActiveTeamInTeamMode = true` into an Anonymous
            // call, Anonymous must still win. (The handler
            // currently computes `noActiveTeamInTeamMode` from
            // `hasTeamScope` which is false for Anonymous; this
            // pin makes the branch ordering explicit at the
            // contract level.)
            let names = [ "Convert" ]
            let config = cfg Anonymous names
            let ctx = unrestricted Anonymous "anon" None

            let r = computeAccessibleModules config ctx true

            Expect.equal r.Accessible names "Anonymous wins over noActiveTeamInTeamMode"
        }
    ]