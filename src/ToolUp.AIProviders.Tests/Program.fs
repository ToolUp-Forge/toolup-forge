// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Program

open Expecto
open ToolUp.AIProviders.Tests.Tests
open ToolUp.AIProviders.Tests.Support
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.AIProviders.Tests" [
        // the offline half of the rich-schema acceptance, once
        // rather than per provider: every provider is handed the same
        // `AIProviderToolDef`, so asserting it four times would assert one
        // thing four times. The per-provider live half rides each list below.
        ProviderTestPack.toolSchemaHandOffTests
        ClaudeProviderTests.tests
        OpenAIProviderTests.tests
        GeminiProviderTests.tests
        CopilotProviderTests.tests
        Microsoft365CopilotProviderTests.tests
        // the per-call model override, offline, per connector.
        ModelOverrideTests.tests
        RateLimitFloorTests.tests
        ClaudeEffortTests.tests
        ConnectorRequestBytesTests.tests
        ProviderCallTests.tests
        // a refused AI-settings revert is reported, never as "not saved".
        AISettingsRevertTests.tests
    ]

/// the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default".
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests