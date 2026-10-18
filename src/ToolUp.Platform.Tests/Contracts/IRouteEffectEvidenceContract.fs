// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IRouteEffectEvidenceContract

open Expecto
open ToolUp.Platform

// ─── IRouteEffectEvidence contract pack (Phase 777) ──────────────────
//
// Sibling of `IHostingBaselineEvidenceContract`, whose rationale applies
// unchanged. The member is resolved by type test (`routeEffectsOf`), so a
// producer that stops threading it through silently reads as "no declared
// routes" rather than failing — and the report would then neither name the
// contained routes nor narrow its decision-point statement. The member is a
// VALUE (the declarations are fixed at composition), so the stability law
// is plain equality across reads and across fresh instances.
let tests (name: string) (expected: RouteEffectIntegrity option) (factory: unit -> IDeploymentVerificationEvidence) =
    let routeEffects (evidence: IDeploymentVerificationEvidence) =
        match box evidence with
        | :? IRouteEffectEvidence as source -> Some source.RouteEffects
        | _ -> None

    testList $"{name} — IRouteEffectEvidence contract" [
        test "implements IRouteEffectEvidence explicitly" {
            Expect.isSome (routeEffects (factory ())) $"'{name}' must implement IRouteEffectEvidence"
        }

        test "reports the expected declared routes" {
            Expect.equal (routeEffects (factory ())) (Some expected) "RouteEffects"
        }

        test "the declared routes are stable across reads and instances" {
            let evidence = factory ()
            let first = routeEffects evidence
            Expect.equal (routeEffects evidence) first "same instance, second read"
            Expect.equal (routeEffects (factory ())) first "a fresh instance"
        }
    ]