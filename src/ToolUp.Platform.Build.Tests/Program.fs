// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.Program

open Expecto

let allTests =
    testList "ToolUp.Platform.Build.Tests" [ SbomTests.tests; PackagedModuleConformanceTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests