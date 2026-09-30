// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IEvidenceChainEvidenceContract

open Expecto
open ToolUp.Platform

// ─── IEvidenceChainEvidence contract pack (Phase 944) ─────────────────
//
// Sibling of `IRemotingDecoderEvidenceContract` (Phase 912) — see that
// file's header for the rationale, which applies unchanged. The facet is
// resolved by type test (`evidenceChainOf`), so a producer that stops
// threading it through silently reads `NotComposed`.
//
// UNLIKE the sibling facets, `EvidenceChain` is an option of a walk
// FUNCTION, not a value: a chain walk reads live substrate, so it cannot
// be compared as a member. The stability law therefore covers two things
// only: (1) whether a walker is PRESENT is stable across reads and across
// fresh instances, and (2) two walks of an UNCHANGED chain agree — the
// caller-supplied `expected` names the walk result the factory's walker
// answers, and every walk of it (same instance twice, and a fresh
// instance) must equal it. `expected = None` means no walker is composed.
let tests
    (name: string)
    (expected: Result<EvidenceChain, EvidenceChainError> option)
    (factory: unit -> IDeploymentVerificationEvidence)
    =

    let walker (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? IEvidenceChainEvidence as source -> Some source.EvidenceChain
        | _ -> None

    let walk (evidence: IDeploymentVerificationEvidence) =
        match walker evidence with
        | Some(Some w) -> Some(w () |> Async.RunSynchronously)
        | _ -> None

    testList $"{name} — IEvidenceChainEvidence contract" [

        test "implements IEvidenceChainEvidence explicitly" {
            Expect.isSome (walker (factory ())) $"'{name}' must implement IEvidenceChainEvidence"
        }

        test "reports the expected walker presence and walk" {
            let evidence = factory ()

            Expect.equal
                (walker evidence |> Option.map Option.isSome)
                (Some expected.IsSome)
                "a walker is present exactly when one is expected"

            Expect.equal (walk evidence) expected "the walk result"
        }

        test "the posture is stable across reads and instances" {
            let evidence = factory ()
            let first = walk evidence

            Expect.equal
                (walker evidence |> Option.map Option.isSome)
                (walker (factory ()) |> Option.map Option.isSome)
                "walker presence agrees across instances"

            Expect.equal (walk evidence) first "same instance, second walk"
            Expect.equal (walk (factory ())) first "a fresh instance"
        }
    ]