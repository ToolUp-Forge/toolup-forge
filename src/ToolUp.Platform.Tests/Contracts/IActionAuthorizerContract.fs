// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IActionAuthorizerContract

open Expecto
open ToolUp.Platform

// ─── Phase 113 — IActionAuthorizer contract pack ──────────────────────
//
// The conformance bar any `IActionAuthorizer` implementation validates
// against (the shipped `PermissionStoreActionAuthorizer`, a future
// distributed / external-policy impl, a consumer's own). Mirrors the
// Phase 46 `IClientToolAuthorizerContract` shape: a fixture supplies
// one call the impl MUST allow and one it MUST deny; the pack asserts
// the decision invariants (deterministic, value-keyed, never-throwing,
// order-independent) around them.

type ActionAuthorizerContractFixture = {
    /// Human label, suffixed onto the test-list name.
    Name: string
    /// The implementation under test.
    Authorizer: IActionAuthorizer
    /// A `(descriptor, ctx)` pair the authorizer MUST allow.
    AllowedCall: ActionDescriptor * AccessContext
    /// A `(descriptor, ctx)` pair the authorizer MUST deny. The pack
    /// asserts `Deny` with a non-empty reason; the reason text itself
    /// is impl-specific and not part of the contract.
    DeniedCall: ActionDescriptor * AccessContext
}

let private call (auth: IActionAuthorizer) ((action, ctx): ActionDescriptor * AccessContext) =
    auth.Authorize action ctx |> Async.RunSynchronously

let tests (f: ActionAuthorizerContractFixture) : Test =
    testList $"IActionAuthorizer contract — {f.Name}" [
        testCase "AllowedCall returns Allow"
        <| fun _ ->
            match call f.Authorizer f.AllowedCall with
            | AuthorizationDecision.Allow -> ()
            | AuthorizationDecision.Deny reason -> failtestf "AllowedCall must resolve Allow; got Deny: %s" reason

        testCase "DeniedCall returns Deny with a non-empty reason"
        <| fun _ ->
            match call f.Authorizer f.DeniedCall with
            | AuthorizationDecision.Allow -> failtest "DeniedCall must resolve Deny"
            | AuthorizationDecision.Deny reason -> Expect.isNotEmpty reason "Deny reason must be non-empty"

        testCase "Idempotent — repeated identical calls return the same decision"
        <| fun _ ->
            let allow1 = call f.Authorizer f.AllowedCall
            let allow2 = call f.Authorizer f.AllowedCall

            Expect.equal allow2 allow1 "two identical AllowedCall invocations must return the same decision"

            let deny1 = call f.Authorizer f.DeniedCall
            let deny2 = call f.Authorizer f.DeniedCall

            Expect.equal deny2 deny1 "two identical DeniedCall invocations must return the same decision"

        testCase "Never throws on a degenerate descriptor (empty kind / target)"
        <| fun _ ->
            let _, ctx = f.AllowedCall

            try
                call f.Authorizer ({ Kind = ""; Target = ""; Scope = None }, ctx) |> ignore
            with ex ->
                failtestf
                    "Authorize must not throw on a degenerate descriptor; got %s: %s"
                    (ex.GetType().Name)
                    ex.Message

        testCase "Identity-by-value — distinct-but-equal inputs produce the same decision"
        <| fun _ ->
            let action, ctx = f.AllowedCall

            let action' = {
                Kind = System.String.Concat(action.Kind, "")
                Target = System.String.Concat(action.Target, "")
                Scope = action.Scope |> Option.map (fun s -> System.String.Concat(s, ""))
            }

            let original = call f.Authorizer (action, ctx)
            let reconstructed = call f.Authorizer (action', ctx)

            Expect.equal reconstructed original "structurally-equal inputs must resolve to the same decision"

        testCase "No cross-call ordering — parallel authorizations are independent"
        <| fun _ ->
            let allowAsync = async {
                let action, ctx = f.AllowedCall
                return! f.Authorizer.Authorize action ctx
            }

            let denyAsync = async {
                let action, ctx = f.DeniedCall
                return! f.Authorizer.Authorize action ctx
            }

            let results =
                [ allowAsync; denyAsync; allowAsync; denyAsync ]
                |> Async.Parallel
                |> Async.RunSynchronously

            match results[0], results[2] with
            | AuthorizationDecision.Allow, AuthorizationDecision.Allow -> ()
            | _ -> failtest "parallel AllowedCalls must resolve Allow"

            match results[1], results[3] with
            | AuthorizationDecision.Deny _, AuthorizationDecision.Deny _ -> ()
            | _ -> failtest "parallel DeniedCalls must resolve Deny"
    ]