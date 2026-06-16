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

        // ── 71.A.9 — admin-module / profile flat-case token resolvers ──
        // The full `applyAdminModuleConstants` returns a `ClientConfig`,
        // and `ClientConfig.defaults` cannot be materialised under .NET
        // (it carries Fable-only React/jsNative values) — so the token
        // logic is exercised here via the generic resolvers with
        // equatable string sentinels; the DU wiring + the
        // `{ config with ... }` plumbing are covered by the build's
        // type-check and the `samples/MinimalClient` Fable transpile.
        test "parseNoDefault: no/off/disabled → disabled case; default/on/enabled → default case" {
            Expect.equal (parseNoDefault "no" "OFF" "ON") (Some "OFF") "no"
            Expect.equal (parseNoDefault "off" "OFF" "ON") (Some "OFF") "off"
            Expect.equal (parseNoDefault "disabled" "OFF" "ON") (Some "OFF") "disabled"
            Expect.equal (parseNoDefault "DEFAULT" "OFF" "ON") (Some "ON") "default (case-insensitive)"
            Expect.equal (parseNoDefault "enabled" "OFF" "ON") (Some "ON") "enabled"
        }

        test "parseNoDefault: unset / unrecognised / Configured-shaped token → None (keeps config value)" {
            Expect.equal (parseNoDefault "" "OFF" "ON") None "empty → None"
            Expect.equal (parseNoDefault "configured" "OFF" "ON") None "Configured-shaped token → None"
            Expect.equal (parseNoDefault "external" "OFF" "ON") None "External-shaped token → None"
        }

        test "parseCaseToken: matches a token map; unknown / empty → None" {
            let cases = [ "narrow", "N"; "wide", "W"; "auto", "A" ]
            Expect.equal (parseCaseToken "WIDE" cases) (Some "W") "wide (case-insensitive)"
            Expect.equal (parseCaseToken "auto" cases) (Some "A") "auto"
            Expect.equal (parseCaseToken "" cases) None "empty → None"
            Expect.equal (parseCaseToken "nonsense" cases) None "unknown → None"
        }
    ]