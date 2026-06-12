// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.Tests.Program

open Expecto
open ToolUp.OpenXml.Tests.InProcess

let allTests =
    testList "ToolUp.OpenXml.Tests" [
        ImportTests.tests
        RoundTripTests.tests
        RevisionTests.tests
        KnowledgeBaseTests.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests