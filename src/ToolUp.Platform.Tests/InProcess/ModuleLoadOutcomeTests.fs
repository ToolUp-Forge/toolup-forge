// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ModuleLoadOutcomeTests

open Expecto
open ToolUp.Platform

// ─── Phase 169 — module-load startup observability ──────────────────────
//
// `addModule` resolves each module to exactly one `ModuleLoadOutcome`,
// accumulated by value on `ServerApp.ModuleLoadOutcomes` and emitted
// through the startup logger at `run`. These tests assert on the
// machine-readable accumulator directly (no log scraping). No crypto is
// needed — a tiny inline `IModuleBindingVerifier` stands in for the
// deployment's verifier so the binding-gate branches are reachable from
// the Core-only test pack.

/// Admits an unstamped module (so the unbound-allowed branch is reachable)
/// and rejects any present stamp with a fixed neutral reason.
let private rejectingVerifier =
    { new IModuleBindingVerifier with
        member _.Verify(_moduleId, stamp) =
            match stamp with
            | None -> Allowed
            | Some _ -> Rejected "test-rejection"
    }

let private outcomeFor (moduleId: string) (app: ServerApp) =
    app.ModuleLoadOutcomes
    |> List.tryPick (fun (id, o) -> if id = moduleId then Some o else None)

let tests =
    testList "Phase 169 — module-load startup observability" [

        test "a registered module records exactly one ModuleRegistered outcome" {
            let app = ServerApp.empty |> ServerApp.addModule (ServerModule.create "Alpha")

            Expect.equal
                (app.ModuleLoadOutcomes
                 |> List.filter (fun (id, _) -> id = "Alpha")
                 |> List.length)
                1
                "exactly one event per module"

            Expect.equal (outcomeFor "Alpha" app) (Some ModuleRegistered) "registered"
        }

        test "a name-filtered module records ModuleFiltered naming the active filter" {
            let baseApp = {
                ServerApp.empty with
                    Config = {
                        ServerApp.empty.Config with
                            ModuleFilter = Some "keep"
                    }
            }

            // "Alpha" does not contain "keep" → filtered out.
            let app = baseApp |> ServerApp.addModule (ServerModule.create "Alpha")

            Expect.equal (outcomeFor "Alpha" app) (Some(ModuleFiltered "keep")) "filtered; names the filter"
            Expect.isFalse (app.ModuleNames |> List.contains "Alpha") "dropped from the loaded module list"
        }

        test "an unstamped module under a configured verifier records ModuleUnboundAllowed" {
            let app =
                ServerApp.empty
                |> ServerApp.withModuleBindingVerifier rejectingVerifier
                |> ServerApp.addModule (ServerModule.create "Alpha")

            Expect.equal (outcomeFor "Alpha" app) (Some ModuleUnboundAllowed) "unbound-allowed"
            Expect.isTrue (app.ModuleNames |> List.contains "Alpha") "still loads"
        }

        test "a stamped module rejected by the verifier records ModuleBindingRejected with the reason" {
            let stamped =
                ServerModule.create "Alpha"
                |> ServerModule.withBindingStamp (MacStamp("k", "dGFn"))

            let app =
                ServerApp.empty
                |> ServerApp.withModuleBindingVerifier rejectingVerifier
                |> ServerApp.addModule stamped

            Expect.equal
                (outcomeFor "Alpha" app)
                (Some(ModuleBindingRejected "test-rejection"))
                "binding-rejected carries the verifier's reason"

            Expect.isFalse (app.ModuleNames |> List.contains "Alpha") "dropped"
        }

        test "a stock multi-module app records one outcome per module, all registered (GP 13 quiet)" {
            let app =
                ServerApp.empty
                |> ServerApp.addModule (ServerModule.create "Alpha")
                |> ServerApp.addModule (ServerModule.create "Beta")

            Expect.equal (app.ModuleLoadOutcomes |> List.map fst) [ "Alpha"; "Beta" ] "one outcome per module, in order"

            Expect.isTrue
                (app.ModuleLoadOutcomes |> List.forall (fun (_, o) -> o = ModuleRegistered))
                "every outcome is ModuleRegistered (nothing filtered or rejected)"
        }
    ]