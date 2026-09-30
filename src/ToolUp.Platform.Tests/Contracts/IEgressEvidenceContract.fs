// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IEgressEvidenceContract

open Expecto
open ToolUp.Platform

// ─── IEgressEvidence contract pack (Phase 944) ────────────────────────
//
// Sibling of `IRemotingDecoderEvidenceContract` (Phase 912) — see that
// file's header for the rationale, which applies unchanged. The facet is
// resolved by type test (`egressOf`), so a producer that stops threading
// it through silently reads `NotComposed` rather than failing. The member
// is a VALUE (the posture the composition root installed at boot), so the
// stability law is plain equality across reads and across fresh instances.
let tests (name: string) (expected: EgressIntegrity option) (factory: unit -> IDeploymentVerificationEvidence) =

    let egress (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? IEgressEvidence as source -> Some source.Egress
        | _ -> None

    testList $"{name} — IEgressEvidence contract" [

        test "implements IEgressEvidence explicitly" {
            Expect.isSome (egress (factory ())) $"'{name}' must implement IEgressEvidence"
        }

        test "reports the expected egress posture" { Expect.equal (egress (factory ())) (Some expected) "Egress" }

        test "the posture is stable across reads and instances" {
            let evidence = factory ()
            let first = egress evidence

            Expect.equal (egress evidence) first "same instance, second read"
            Expect.equal (egress (factory ())) first "a fresh instance"
        }
    ]