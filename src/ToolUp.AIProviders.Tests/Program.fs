// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Program

open Expecto
open ToolUp.AIProviders.Tests.Tests

let allTests =
    testList "ToolUp.AIProviders.Tests" [
        ClaudeProviderTests.tests
        OpenAIProviderTests.tests
        GeminiProviderTests.tests
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests