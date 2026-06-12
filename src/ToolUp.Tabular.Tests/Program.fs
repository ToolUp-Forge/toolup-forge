// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.Program

open Expecto
open ToolUp.Tabular.Tests.InProcess

let allTests =
    testList "ToolUp.Tabular.Tests" [ CsvTests.tests; XlsxTests.tests; ReaderTests.tests; WorkedExampleTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests