// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IGroundedNarrativeRunContract

open System
open Expecto
open ToolUp.Platform

// ─── IGroundedNarrativeRun contract pack ─────────────────────────────
//
// The laws any grounded run holds, whatever it does between the request
// and the outcome — the shipped run, and any run that wraps it (the offence
// learning decorator is the second implementation). A run that cannot
// produce a narrative to check answers `GroundedNarrativeFailed` with its
// reason; it never raises and never publishes. And it holds no state
// between invocations (GP 12 rule 4): the same request answers alike.
//
// `factory` builds the run over a composition that registers
// `registeredKey`, with a gate and a provider composed.

let private request (runKey: string) (scope: ResolvedScope) : GroundedNarrativeRequest = {
    RunKey = runKey
    Scope = scope
    Access = None
    Trigger = "contract"
}

let private failure (outcome: GroundedNarrativeOutcome) : string option =
    match outcome with
    | GroundedNarrativeFailed reason -> Some reason
    | _ -> None

let tests (name: string) (registeredKey: string) (scope: ResolvedScope) (factory: unit -> IGroundedNarrativeRun) =
    let runOf (key: string) (scope: ResolvedScope) =
        (factory ()).Run(request key scope) |> Async.RunSynchronously

    testList $"{name} — IGroundedNarrativeRun contract" [

        test "a run key nothing registered fails with its reason, and does not raise" {
            let unknown = "unregistered-" + Guid.NewGuid().ToString "N"

            match failure (runOf unknown scope) with
            | Some reason -> Expect.stringContains reason unknown "the reason names the key"
            | None -> failtest "an unregistered run key must answer GroundedNarrativeFailed"
        }

        test "the anonymous scope fails, and does not raise" {
            Expect.isSome
                (failure (runOf registeredKey ResolvedScope.anonymous))
                "a grounded run never runs without a resolved scope"
        }

        test "the same failing request answers alike on a fresh run (no state between invocations)" {
            let unknown = "unregistered-" + Guid.NewGuid().ToString "N"
            Expect.equal (runOf unknown scope) (runOf unknown scope) "two invocations, one answer"
        }
    ]