// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IEgressPolicyContract

open Expecto
open ToolUp.Platform

// Phase 772 — contract for the server-side egress policy seam. Any
// `IEgressPolicy` — the shipped permit-all default, the declared-
// destinations policy, or a consumer's own — must satisfy the laws the
// handler in front of every platform client relies on:
//   * TOTAL — every (component, origin, surface) gets a verdict; the
//     handler never has to guess what an exception from the policy means,
//     because there is none to catch;
//   * PURE — the same three coordinates always produce the same verdict,
//     so a decision the handler made is the decision the report describes;
//   * a Deny carries a non-empty reason that names the ORIGIN it refused,
//     and no reason ever carries a path or a query string. The policy only
//     ever sees an origin, so the second half holds by construction; the
//     law pins it against a policy that reconstructs a URL from elsewhere.

let private components = [
    EgressPolicy.platformComponent
    ComponentId.ofModule "reports"
    ComponentId.forCompanionSlot "INotificationChannel"
]

let private destinations = [
    EgressDestination.parse "https://api.example.com"
    EgressDestination.parse "http://localhost:8080"
    EgressDestination.parse "https://[::1]:8443"
]

let private surfaces = [
    EgressSurface.ModuleHandler
    EgressSurface.AIProvider
    EgressSurface.AuthProvider
    EgressSurface.Notification
    EgressSurface.Webhook
    EgressSurface.Other
]

let private samples = [
    for c in components do
        for d in destinations do
            for s in surfaces do
                c, d, s
]

/// The pack. `name` labels the implementation under test; `policy` is the
/// implementation. Bind it once per shipped policy.
let tests (name: string) (policy: IEgressPolicy) =
    testList (sprintf "IEgressPolicy contract — %s" name) [
        test "total: every (component, origin, surface) gets a verdict without raising" {
            for (c, d, s) in samples do
                policy.Decide(c, d, s) |> ignore
        }

        test "pure: the same coordinates always produce the same verdict" {
            for (c, d, s) in samples do
                let first = policy.Decide(c, d, s)
                let second = policy.Decide(c, d, s)
                Expect.equal second first (sprintf "%O -> %O (%A) decided twice" c d s)
        }

        test "a deny names the origin it refused and carries no path or query" {
            for (c, d, s) in samples do
                match policy.Decide(c, d, s) with
                | EgressVerdict.Permit -> ()
                | EgressVerdict.Deny reason ->
                    Expect.isNotEmpty reason "a deny has a reason"
                    Expect.stringContains reason (EgressDestination.origin d) "the reason names the origin"

                    Expect.isFalse
                        (reason.Contains(EgressDestination.origin d + "/") || reason.Contains "?")
                        "the reason carries no path or query"
        }
    ]