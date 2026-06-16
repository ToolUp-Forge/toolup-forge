module ToolUp.Platform.Tests.InProcess.ClientBrandLiftTests

// Phase 71.A.10 — client brand-string lifts. `foldBrandConstants` is the
// pure / explicit-values resolver behind `fromBundleConstants`; the
// jsNative `BundleConstants` reads are exercised by the Fable transpile
// (samples/MinimalClient), this pack pins the precedence logic.

open Expecto
open ToolUp.Platform.ClientConfigDefaults

let tests =
    testList "ClientConfigDefaults.foldBrandConstants (Phase 71.A.10)" [
        test "Vite define wins over the override-record value" {
            let overrides = {
                ClientConfigOverrides.empty with
                    AppName = Some "FromOverride"
            }

            let folded = foldBrandConstants (Some "FromVite") None None None None None overrides
            Expect.equal folded.AppName (Some "FromVite") "Vite define must win over the override"
        }

        test "an absent Vite define leaves the override-record value untouched" {
            let overrides = {
                ClientConfigOverrides.empty with
                    AppName = Some "FromOverride"
                    ShowDebugOnlyModules = Some true
            }

            let folded = foldBrandConstants None None None None None None overrides
            Expect.equal folded.AppName (Some "FromOverride") "override preserved when Vite absent"
            Expect.equal folded.ShowDebugOnlyModules (Some true) "override bool preserved when Vite absent"
        }

        test "all six brand fields fold with Vite precedence" {
            let folded =
                foldBrandConstants
                    (Some "Name")
                    (Some "logo.svg")
                    (Some "reports")
                    (Some "dev-admin")
                    (Some true)
                    (Some false)
                    ClientConfigOverrides.empty

            Expect.equal folded.AppName (Some "Name") "AppName"
            Expect.equal folded.AppLogo (Some "logo.svg") "AppLogo"
            Expect.equal folded.ActiveModule (Some "reports") "ActiveModule"
            Expect.equal folded.DevDefaultUserId (Some "dev-admin") "DevDefaultUserId"
            Expect.equal folded.EnableElmishConsoleTrace (Some true) "EnableElmishConsoleTrace"
            Expect.equal folded.ShowDebugOnlyModules (Some false) "ShowDebugOnlyModules"
        }
    ]