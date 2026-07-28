// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.Tests.Program

open Expecto
open ToolUp.Algorithms.Tests.Contracts
open ToolUp.Algorithms.Tests.InProcess

let allTests =
    testList "ToolUp.Algorithms.Tests" [
        // The provider-independent half of the GP 12 audit — run once
        // against the shipped interfaces.
        IAlgorithmProviderContract.shapeTests

        // The per-provider half, bound to the in-tree reference
        // provider. A provider companion binds the same pack.
        IAlgorithmProviderContract.tests ReferenceProvider.provider ReferenceProvider.sampleInvocations

        IAlgorithmProviderContract.echoedConventionTests
            ReferenceProvider.provider
            ReferenceProvider.Ids.Describe
            ReferenceProvider.Ids.Smooth

        RegistryAndDispatcherTests.registryTests
        RegistryAndDispatcherTests.catalogTests
        RegistryAndDispatcherTests.dispatcherTests
        RegistryAndDispatcherTests.validationTests
        RegistryAndDispatcherTests.wireVocabularyTests

        AIToolSurfaceTests.definitionTests
        AIToolSurfaceTests.parserTests
        AIToolSurfaceTests.projectionTests
        AIToolSurfaceTests.contributorTests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests