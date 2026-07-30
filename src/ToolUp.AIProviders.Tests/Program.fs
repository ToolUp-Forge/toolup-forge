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
        CopilotProviderTests.tests
    ]

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests