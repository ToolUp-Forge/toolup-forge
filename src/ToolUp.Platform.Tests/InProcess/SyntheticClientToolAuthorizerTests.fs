// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SyntheticClientToolAuthorizerTests

// ─── In-process binding — IClientToolAuthorizerContract ──────────────
//
// Binds the Phase 46 contract pack to a trivial allow / deny stub that
// is independent of any companion. This is the GP 12 "attempt a second
// implementation" discipline: this synthetic stub is the first in-tree
// subject the contract pack runs against. Phase 46.B adds
// `ToolUp.AI.SampleClientTool` as a second substrate-shape consumer.
//
// The stub's policy is the simplest possible:
//   • the tool name `"denied.tool"` is always denied
//   • every other tool name is always allowed
//
// That trivial shape exercises both Allow and Deny branches without
// borrowing any companion-specific semantics (no module/field
// allowlist, no JSON-arg parsing, no read-only special-casing).

open Expecto
open ToolUp.AI
open ToolUp.Platform.Tests.Contracts

type private SyntheticAuthorizer(denyTool: string) =
    interface IClientToolAuthorizer with
        member _.Authorize(toolName, _argsJson, _activeModule, _activePage) =
            if toolName = denyTool then
                Deny $"synthetic stub denied tool '{toolName}'"
            else
                Allow

let tests =
    IClientToolAuthorizerContract.tests {
        Name = "SyntheticClientToolAuthorizer"
        Authorizer = SyntheticAuthorizer("denied.tool") :> IClientToolAuthorizer
        AllowedCall = ("allowed.tool", "{}", Some "AnyModule", Some "/any-page")
        DeniedCall = ("denied.tool", "{}", Some "AnyModule", Some "/any-page")
    }