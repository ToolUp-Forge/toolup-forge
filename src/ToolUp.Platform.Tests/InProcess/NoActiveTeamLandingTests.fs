module ToolUp.Platform.Tests.InProcess.NoActiveTeamLandingTests

open Expecto
open ToolUp.Platform

// ─── Parameterized no-active-team landing pack ───────────────────────
//
// The SDK built-in landing surface is the lightweight alternative to a
// consumer hand-rolling a module. Its stable id is the one piece the .NET
// harness can pin: the rest of the client shell (`ClientConfig.defaults`,
// `prepareModules`, the module factory) is Fable-runtime-only, and the
// choice between the built-in landing and a deployment's own module is
// one value since Phase 1074 (`Chrome.NoActiveTeamLanding`), so the
// precedence helper these tests used to pin is gone.

let tests =
    testList "NoActiveTeamLanding" [

        test "moduleId is the stable built-in id" {
            Expect.equal NoActiveTeamLanding.moduleId "AwaitingTeam" "stable id consumed by the gate + factory"
        }
    ]