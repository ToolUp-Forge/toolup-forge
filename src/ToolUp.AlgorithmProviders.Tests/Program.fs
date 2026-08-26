// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.Program

open Expecto
open ToolUp.Algorithms.Tests.Contracts
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures
open System.Reflection
open ToolUp.Platform.Tests.Support

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

let private registeredTests =
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

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests