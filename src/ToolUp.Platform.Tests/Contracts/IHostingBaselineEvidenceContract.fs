// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IHostingBaselineEvidenceContract

open Expecto
open ToolUp.Platform

// ─── IHostingBaselineEvidence contract pack (Phase 771) ──────────────
//
// Sibling of `IEgressEvidenceContract` (Phase 944), whose header gives the
// rationale, which applies unchanged. The facet is resolved by type test
// (`hostingBaselineOf`), so a producer that stops threading it through
// silently reads `NotComposed` rather than failing. The member is a VALUE
// (the baseline observed once at boot), so the stability law is plain
// equality across reads and across fresh instances.
let tests
    (name: string)
    (expected: HostingBaselineIntegrity option)
    (factory: unit -> IDeploymentVerificationEvidence)
    =

    let hostingBaseline (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? IHostingBaselineEvidence as source -> Some source.HostingBaseline
        | _ -> None

    testList $"{name} — IHostingBaselineEvidence contract" [

        test "implements IHostingBaselineEvidence explicitly" {
            Expect.isSome (hostingBaseline (factory ())) $"'{name}' must implement IHostingBaselineEvidence"
        }

        test "reports the expected hardening baseline" {
            Expect.equal (hostingBaseline (factory ())) (Some expected) "HostingBaseline"
        }

        test "the baseline is stable across reads and instances" {
            let evidence = factory ()
            let first = hostingBaseline evidence

            Expect.equal (hostingBaseline evidence) first "same instance, second read"
            Expect.equal (hostingBaseline (factory ())) first "a fresh instance"
        }
    ]