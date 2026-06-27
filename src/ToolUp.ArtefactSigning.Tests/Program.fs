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
        ModuleBindingVerifierTests.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests