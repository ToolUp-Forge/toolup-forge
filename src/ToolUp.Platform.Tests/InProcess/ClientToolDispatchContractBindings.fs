// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ClientToolDispatchContractBindings

// ─── In-process binding — IClientToolDispatchContract ────────────────
//
// Binds the Phase 46.A dispatch round-trip pack to a deny-only
// authorizer stub. The stub denies one tool name and allows
// everything else — that covers both the Allow round-trip and the
// Deny short-circuit paths the pack exercises.
//
// Phase 46.B will add a parallel binding against the in-tree
// `ToolUp.AI.SampleClientTool` reference companion's authorizer +
// handler — same pack, second binding, no test-infrastructure
// changes (the GP 12 "attempt a second implementation" payoff).
//
// The simulator returns a constant calculator-shape result for any
// Allow-path invocation. The pack asserts only that the round-trip
// reaches `ToolCallCompleted` cleanly — not on the result content —
// so the constant shape is sufficient.

open ToolUp.AI
open ToolUp.Platform.Tests.Contracts

type private DenyOnlyAuthorizer(denyTool: string) =
    interface IClientToolAuthorizer with
        member _.Authorize(toolName, _argsJson, _activeModule, _activePage) =
            if toolName = denyTool then
                Deny $"binding stub denied tool '{toolName}'"
            else
                Allow

let private constantSimulator (_evt: AIStreamEvent) : string option = Some """{"result": 42}"""

let tests =
    IClientToolDispatchContract.tests {
        Name = "DenyOnlyAuthorizer"
        Authorizer = DenyOnlyAuthorizer("denied.tool") :> IClientToolAuthorizer
        AllowedToolName = "allowed.tool"
        DeniedToolName = "denied.tool"
        Simulator = constantSimulator
    }