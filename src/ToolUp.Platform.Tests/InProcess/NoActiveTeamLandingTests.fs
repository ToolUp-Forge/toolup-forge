module ToolUp.Platform.Tests.InProcess.NoActiveTeamLandingTests

open Expecto
open ToolUp.Platform

// ─── Parameterized no-active-team landing pack ───────────────────────
//
// The SDK built-in landing surface is the lightweight alternative to a
// consumer hand-rolling a module + setting
// `ClientConfig.NoActiveTeamLandingModuleId`. The gate-target precedence
// is the one piece of branching logic; it lives in the Fable-safe
// `NoActiveTeamLanding.resolveLandingId` pure helper precisely so it can
// be exercised here in the .NET test harness (the rest of the client
// shell — `ClientConfig.defaults`, `prepareModules`, the module factory —
// is Fable-runtime-only: it hits `JsInterop.import` dummy bindings under
// .NET, so it is verified by `dotnet build` + the `dotnet fable` compile
// of `samples/MinimalClient`, not here).

let private cfg: NoActiveTeamLandingConfig = {
    Label = "Welcome"
    Title = "You're not on a team yet"
    Body = "An administrator needs to add you to a team."
    Icon = None
}

let tests =
    testList "NoActiveTeamLanding" [

        test "moduleId is the stable built-in id" {
            Expect.equal NoActiveTeamLanding.moduleId "AwaitingTeam" "stable id consumed by the gate + factory"
        }

        test "resolveLandingId — neither path set → None (gate inert)" {
            Expect.equal (NoActiveTeamLanding.resolveLandingId None None) None "no gate target when nothing opted in"
        }

        test "resolveLandingId — explicit custom module id only → that id" {
            Expect.equal
                (NoActiveTeamLanding.resolveLandingId (Some "CustomLanding") None)
                (Some "CustomLanding")
                "explicit custom module id is the gate target"
        }

        test "resolveLandingId — parameterized config only → SDK built-in id" {
            Expect.equal
                (NoActiveTeamLanding.resolveLandingId None (Some cfg))
                (Some NoActiveTeamLanding.moduleId)
                "the parameterized config resolves to the built-in module id"
        }

        test "resolveLandingId — both set → explicit custom id wins" {
            Expect.equal
                (NoActiveTeamLanding.resolveLandingId (Some "CustomLanding") (Some cfg))
                (Some "CustomLanding")
                "explicit custom module takes precedence over the built-in config"
        }
    ]