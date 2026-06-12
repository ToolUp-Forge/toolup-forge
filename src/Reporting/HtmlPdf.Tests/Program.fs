// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.HtmlPdf.Tests.Program

open Expecto

let allTests =
    testList "ToolUp.Reporting.HtmlPdf.Tests" [ HtmlPdfRendererTests.tests ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests