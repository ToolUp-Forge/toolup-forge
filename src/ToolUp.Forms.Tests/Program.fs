module ToolUp.Forms.Tests.Program

open Expecto
open ToolUp.Forms.Tests.InProcess
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.Forms.Tests" [
        FormStoreTests.tests
        WorkflowEngineTests.tests
        WorkedExampleTests.tests
        AnalyserCacheTests.tests
        WorkflowEngineDurabilityTests.tests
        WorkflowEngineDurabilityTests.contractTests
        FormsServerHygieneTests.tests
        PublishableHardeningTests.tests
        PublicSubmitSurfaceTests.tests
        ValidationBridgeTests.tests
        MatrixFieldTests.tests
        FormApiInHandlerGateTests.tests
    ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 1 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests