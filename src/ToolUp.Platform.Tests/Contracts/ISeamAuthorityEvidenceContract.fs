// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ISeamAuthorityEvidenceContract

open Expecto
open ToolUp.Platform

// ─── ISeamAuthorityEvidence contract pack (Phase 944) ─────────────────
//
// Sibling of `IRemotingDecoderEvidenceContract` (Phase 912) — see that
// file's header for the rationale, which applies unchanged. The facet is
// a standalone interface resolved by type test (`seamAuthorityOf`), whose
// `| _ -> None` arm reads a missing facet as absent. A producer that
// stops threading the facet through therefore does not fail to compile
// and does not throw: the report's seam-authority section quietly reads
// `NotComposed`. This pack makes that an executable law.
//
// The member is a VALUE (not a thunk), so the stability law is plain
// equality across reads and across fresh instances.
let tests (name: string) (expected: SeamAuthorityIntegrity option) (factory: unit -> IDeploymentVerificationEvidence) =

    let seamAuthority (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? ISeamAuthorityEvidence as source -> Some source.SeamAuthority
        | _ -> None

    testList $"{name} — ISeamAuthorityEvidence contract" [

        test "implements ISeamAuthorityEvidence explicitly" {
            Expect.isSome (seamAuthority (factory ())) $"'{name}' must implement ISeamAuthorityEvidence"
        }

        test "reports the expected seam-authority posture" {
            Expect.equal (seamAuthority (factory ())) (Some expected) "SeamAuthority"
        }

        test "the posture is stable across reads and instances" {
            let evidence = factory ()
            let first = seamAuthority evidence

            Expect.equal (seamAuthority evidence) first "same instance, second read"
            Expect.equal (seamAuthority (factory ())) first "a fresh instance"
        }
    ]