// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AICookbooks.Tests.Program

open Expecto

let allTests = testList "ToolUp.AICookbooks.Tests" [ LicensingBoundaryTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests