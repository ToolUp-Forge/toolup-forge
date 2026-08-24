// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.Program

open Expecto
open ToolUp.ArtefactSigning.Tests.InProcess

let allTests =
    testList "ToolUp.ArtefactSigning.Tests" [
        DefaultArtefactSignerTests.tests
        JwsBuilderTests.tests
        CloudKmsArtefactSignerTests.tests
        SignedExportBundleTests.tests
        ModuleBindingVerifierTests.tests
        ModuleBindingTrustResolverTests.tests
        BindingRevocationContractTests.tests
        ModuleSbomManifestTests.tests
        ApplicationSigningTests.tests
        SigningProviderConformanceTests.tests
        DeployRecordSealingTests.tests
        ApplicationKeyedSigningTests.tests
    ]

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console. See docs/platform/testing-conventions.md § "Every Expecto
// pack runs sequenced by default". It matters twice over here: the
// provider-conformance probe evaluates contract packs in-process.
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests