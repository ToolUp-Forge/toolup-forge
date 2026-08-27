// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Program

open Expecto
open ToolUp.ArtefactSigning.Tests.InProcess
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.ArtefactSigning.Tests" [
        DefaultArtefactSignerTests.tests
        JwsBuilderTests.tests
        CloudKmsArtefactSignerTests.tests
        SignedExportBundleTests.tests
        ModuleBindingVerifierTests.tests
        ModuleBindingTrustResolverTests.tests
        BindingRevocationContractTests.tests
        ModuleSbomManifestTests.tests
        ModuleCertificationTests.tests
        ApplicationSigningTests.tests
        SigningProviderConformanceTests.tests
        DeployRecordSealingTests.tests
        ApplicationKeyedSigningTests.tests
    ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 7 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console. See docs/platform/testing-conventions.md § "Every Expecto
// pack runs sequenced by default". It matters twice over here: the
// provider-conformance probe evaluates contract packs in-process.
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests