// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.Program

open Expecto
open ToolUp.Algorithms.Tests.Contracts
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

// ─── Phase 11.E.3 — the Math.NET provider test pack ─────────────────
//
// Two halves, deliberately:
//
//   * the SHARED contract packs from `ToolUp.Algorithms.Tests`, bound
//     here against the real Math.NET provider. They are the same cases
//     the in-tree reference provider passes, which is the point — a
//     provider companion validates against the SDK's bar, not against
//     one it wrote for itself. The echoed-convention pack in particular
//     is the eval's four findings turned into an executable obligation.
//
//   * this companion's OWN known-answer cases, which the shared packs
//     deliberately do not attempt: numerical accuracy is a provider's
//     own test surface, and for hand-written estimators it is the only
//     backstop there is.

let allTests =
    testList "ToolUp.AlgorithmProviders.Tests" [
        testList "Math.NET — IAlgorithmProvider contract" [
            IAlgorithmProviderContract.tests provider sampleInvocations

            IAlgorithmProviderContract.echoedConventionTests
                provider
                MathNetAlgorithmIds.Describe
                MathNetAlgorithmIds.Smooth
        ]

        MathNetDescriptiveTests.tests
        MathNetRegressionTests.tests
        MathNetDistributionTests.tests
        MathNetSmoothingTests.tests
        MathNetProviderCompositionTests.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests