// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IRemotingDecoderEvidenceContract

open Expecto
open ToolUp.Platform

// ─── IRemotingDecoderEvidence contract pack (Phase 912) ───────────────
//
// Parametrised laws for any producer of `IDeploymentVerificationEvidence`
// against the `IRemotingDecoderEvidence` sibling facet (Phase 785). The
// factory builds a fresh evidence value (no seeding, no network: the
// posture is a constant of the value, carried through at construction);
// `expected` is the `RemotingDecoderIntegrity option` the binding says
// that value reports.
//
// The laws:
//   - the value implements `IRemotingDecoderEvidence` EXPLICITLY — a
//     value that does not is read as `NotComposed` everywhere, silently,
//     by the report's GP 11 fallback (`remotingDecodersOf`'s type-test
//     `| _ -> None` arm). That fallback is the correct behaviour for a
//     value that never heard of the facet; it is the wrong behaviour for
//     a producer that is supposed to carry the facet and silently
//     stopped, so this pack must fail loudly there rather than read the
//     regression as an absence;
//   - `RemotingDecoders` answers exactly what the binding expects;
//   - the posture is stable across reads and across fresh instances — the
//     tenth report section is a VALUE captured at construction, not a
//     re-read (`DeploymentVerificationReport.fs`'s own doc comment: "A
//     VALUE rather than a thunk … re-reading the registry at report time
//     would answer a weaker question"), so a producer that recomputed it
//     per call would be a regression this pins.
//
// This formalises, as an executable law bound against every wither, the
// warning `DeploymentVerificationEvidence.fs` already carries by hand on
// `withGroundingContinuity`: "A wither that dropped a member it does not
// name would silently delete the [] section for every root that supplies
// both." Binding every wither (`withGroundingContinuity` /
// `withSeamAuthority` / `withEvidenceChain` / `withEgress` /
// `withRemotingArgumentDecoders`) against a NON-None posture turns that
// comment into a pack that reddens the moment a future edit stops
// threading `remotingDecodersOf` through.
let tests
    (name: string)
    (expected: RemotingDecoderIntegrity option)
    (factory: unit -> IDeploymentVerificationEvidence)
    =

    let decoders (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? IRemotingDecoderEvidence as source -> Some source.RemotingDecoders
        | _ -> None

    testList $"{name} — IRemotingDecoderEvidence contract" [

        test "implements IRemotingDecoderEvidence explicitly" {
            Expect.isSome (decoders (factory ())) $"'{name}' must implement IRemotingDecoderEvidence"
        }

        test "reports the expected decode-edge posture" {
            Expect.equal (decoders (factory ())) (Some expected) "RemotingDecoders"
        }

        test "the posture is stable across reads and instances" {
            let evidence = factory ()
            let first = decoders evidence

            Expect.equal (decoders evidence) first "same instance, second read"
            Expect.equal (decoders (factory ())) first "a fresh instance"
        }
    ]