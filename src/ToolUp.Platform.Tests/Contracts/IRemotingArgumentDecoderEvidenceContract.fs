// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IRemotingArgumentDecoderEvidenceContract

open Expecto
open ToolUp.Platform

// ─── IRemotingArgumentDecoderEvidence contract pack (Phase 912) ───────
//
// The argument-side twin of `IRemotingDecoderEvidenceContract` — see that
// file's header for the full rationale, which applies here unchanged
// (Phase 842 mirrors Phase 785's shape exactly, reusing
// `RemotingDecoderIntegrity` as-is). This pack reads
// `IRemotingArgumentDecoderEvidence.RemotingArgumentDecoders` instead of
// the record-side member; a value that carries one facet and not the
// other (any evidence built before Phase 842) is exactly the case a
// caller must not accidentally bind here.
let tests
    (name: string)
    (expected: RemotingDecoderIntegrity option)
    (factory: unit -> IDeploymentVerificationEvidence)
    =

    let argumentDecoders (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? IRemotingArgumentDecoderEvidence as source -> Some source.RemotingArgumentDecoders
        | _ -> None

    testList $"{name} — IRemotingArgumentDecoderEvidence contract" [

        test "implements IRemotingArgumentDecoderEvidence explicitly" {
            Expect.isSome (argumentDecoders (factory ())) $"'{name}' must implement IRemotingArgumentDecoderEvidence"
        }

        test "reports the expected argument-decode-edge posture" {
            Expect.equal (argumentDecoders (factory ())) (Some expected) "RemotingArgumentDecoders"
        }

        test "the posture is stable across reads and instances" {
            let evidence = factory ()
            let first = argumentDecoders evidence

            Expect.equal (argumentDecoders evidence) first "same instance, second read"
            Expect.equal (argumentDecoders (factory ())) first "a fresh instance"
        }
    ]