// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Classification.Tests.Program

open Expecto
open ToolUp.Platform.Classification.Tests.InProcess

let allTests =
    testList "ToolUp.Platform.Classification.Tests" [ ClassificationGateTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests