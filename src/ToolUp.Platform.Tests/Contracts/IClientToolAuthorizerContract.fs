// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IClientToolAuthorizerContract

// ─── IClientToolAuthorizer conformance pack ──────────────────────────
//
// A reusable Expecto list asserting the *uniform* invariants every
// compliant `IClientToolAuthorizer` MUST uphold, per the contract
// documented on the interface in `ToolUp.AI.Core/Shared/AITypes.fs`:
//
//   • value-in / value-out (sync hot-path decision)
//   • MUST NOT throw — a malformed `argsJson` is a `Deny`, not an
//     exception
//   • pure / idempotent for identical inputs (rule 4: stateless
//     between invocations)
//   • identity-by-value on the input parameters (rule 1)
//
// The pack is companion-agnostic: callers hand it a fully-constructed
// authorizer plus two example calls — one the impl MUST allow and one
// the impl MUST deny. The pack drives the seam's no-throw / idempotency
// / identity-by-value assertions through those two anchors.
//
// The dispatch-side assertions (no `ClientToolInvoke` SSE on Deny,
// denial-audit emission, single-`ClientResident`-branch enumeration)
// are intentionally NOT in this pack — they belong on the agent loop
// itself, not on any single authorizer. See
// `InProcess/AIAgentEngineClientResidentAuthorizationTests.fs`.

open Expecto
open ToolUp.AI

/// Everything the pack needs to exercise one authorizer.
type ClientToolAuthorizerContractFixture = {
    /// Human label, suffixed onto the test-list name.
    Name: string
    /// The implementation under test.
    Authorizer: IClientToolAuthorizer
    /// A `(toolName, argsJson, activeModule, activePage)` tuple the
    /// authorizer MUST allow.
    AllowedCall: string * string * string option * string option
    /// A `(toolName, argsJson, activeModule, activePage)` tuple the
    /// authorizer MUST deny. The pack asserts `Deny` is returned with
    /// a non-empty reason; the reason text itself is impl-specific
    /// and not part of the contract.
    DeniedCall: string * string * string option * string option
}

let private call (auth: IClientToolAuthorizer) (toolName, argsJson, activeModule, activePage) =
    auth.Authorize(toolName, argsJson, activeModule, activePage)

let tests (f: ClientToolAuthorizerContractFixture) : Test =
    let allowedToolName, _, _, _ = f.AllowedCall

    testList $"IClientToolAuthorizer contract — {f.Name}" [
        testCase "AllowedCall returns Allow"
        <| fun _ ->
            match call f.Authorizer f.AllowedCall with
            | Allow -> ()
            | Deny reason -> failtestf "AllowedCall must resolve Allow; got Deny: %s" reason

        testCase "DeniedCall returns Deny with a non-empty reason"
        <| fun _ ->
            match call f.Authorizer f.DeniedCall with
            | Allow -> failtest "DeniedCall must resolve Deny — the impl claimed it would refuse this call"
            | Deny reason ->
                Expect.isNotEmpty
                    reason
                    "Deny reason must be non-empty so the model can surface a meaningful refusal back to the user"

        testCase "Idempotent — repeated identical calls return the same decision (rule 4: stateless)"
        <| fun _ ->
            let allow1 = call f.Authorizer f.AllowedCall
            let allow2 = call f.Authorizer f.AllowedCall
            Expect.equal allow2 allow1 "two identical AllowedCall invocations must return the same decision"

            let deny1 = call f.Authorizer f.DeniedCall
            let deny2 = call f.Authorizer f.DeniedCall
            Expect.equal deny2 deny1 "two identical DeniedCall invocations must return the same decision"

        testCase "Never throws on malformed argsJson"
        <| fun _ ->
            let _, _, activeModule, activePage = f.AllowedCall

            try
                call f.Authorizer (allowedToolName, "{ not valid json", activeModule, activePage)
                |> ignore
            with ex ->
                failtestf
                    "Authorize must not throw on malformed argsJson (seam doc: 'a malformed argsJson is a Deny, not an exception'); got %s: %s"
                    (ex.GetType().Name)
                    ex.Message

        testCase "Never throws on empty argsJson"
        <| fun _ ->
            let _, _, activeModule, activePage = f.AllowedCall

            try
                call f.Authorizer (allowedToolName, "", activeModule, activePage) |> ignore
            with ex ->
                failtestf "Authorize must not throw on empty argsJson; got %s: %s" (ex.GetType().Name) ex.Message

        testCase "Never throws on None active module / page"
        <| fun _ ->
            try
                call f.Authorizer (allowedToolName, "{}", None, None) |> ignore
            with ex ->
                failtestf
                    "Authorize must not throw when activeModule / activePage are None (e.g. prompts submitted from a non-module context); got %s: %s"
                    (ex.GetType().Name)
                    ex.Message

        testCase "Identity-by-value — distinct-but-equal inputs produce the same decision (rule 1)"
        <| fun _ ->
            let toolName, argsJson, activeModule, activePage = f.AllowedCall

            // Build structurally-equal but distinct string instances via
            // `String.Concat` so the impl can't (accidentally) be relying
            // on reference equality. The contract requires the decision
            // to depend on the value of the inputs, not on which
            // construction site produced them.
            let toolName' = System.String.Concat(toolName, "")
            let argsJson' = System.String.Concat(argsJson, "")

            let activeModule' =
                activeModule |> Option.map (fun s -> System.String.Concat(s, ""))

            let activePage' = activePage |> Option.map (fun s -> System.String.Concat(s, ""))

            let original = call f.Authorizer (toolName, argsJson, activeModule, activePage)

            let reconstructed =
                call f.Authorizer (toolName', argsJson', activeModule', activePage')

            Expect.equal
                reconstructed
                original
                "structurally-equal input parameters must resolve to the same decision (identity-by-value, GP 12 rule 1)"

        testCase "No cross-call ordering — parallel authorizations are independent (rule 5)"
        <| fun _ ->
            // Rule 5: cross-shard ordering is not promised. For the
            // authorizer, the "shard" boundary is each call — two
            // concurrent decisions must not interfere. Driving Allow +
            // Deny in parallel (via `Async.Parallel`) and asserting
            // each branch's decision matches its fixture is the
            // strongest single-line statement of the rule.
            let allowAsync = async { return call f.Authorizer f.AllowedCall }
            let denyAsync = async { return call f.Authorizer f.DeniedCall }

            let results =
                [ allowAsync; denyAsync; allowAsync; denyAsync ]
                |> Async.Parallel
                |> Async.RunSynchronously

            match results[0] with
            | Allow -> ()
            | Deny reason -> failtestf "parallel AllowedCall (1/2) must resolve Allow; got Deny: %s" reason

            match results[1] with
            | Allow -> failtest "parallel DeniedCall (1/2) must resolve Deny"
            | Deny _ -> ()

            match results[2] with
            | Allow -> ()
            | Deny reason -> failtestf "parallel AllowedCall (2/2) must resolve Allow; got Deny: %s" reason

            match results[3] with
            | Allow -> failtest "parallel DeniedCall (2/2) must resolve Deny"
            | Deny _ -> ()
    ]