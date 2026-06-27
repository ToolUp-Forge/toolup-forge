// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.Program

open Expecto

let allTests =
    testList "ToolUp.Cli.Tests" [
        DispatchTests.tests
        DockerEmitTests.tests
        StampRoundTripTests.tests
        ModuleAddRemoveTests.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests