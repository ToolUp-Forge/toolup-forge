// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataSubject.Tests.Program

open Expecto
open ToolUp.Platform.DataSubject.Tests.InProcess

let allTests =
    testList "ToolUp.Platform.DataSubject.Tests" [ BackgroundExportStoreTests.tests; AsyncExportApiTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests