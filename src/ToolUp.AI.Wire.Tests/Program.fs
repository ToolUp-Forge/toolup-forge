module ToolUp.AI.Wire.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs. `[<Tests>]` alone does
/// NOT get a list into this run — `runTestsWithCLIArgs` executes exactly
/// what is registered here.
let private registeredTests =
    testList "ToolUp.AI.Wire.Tests" [
        JsonValueTests.tests
        TransportTests.tests
        OpenAIWireTests.tests
        GeminiWireTests.tests
        ClaudeWireTests.tests
    ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests