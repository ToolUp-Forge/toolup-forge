module ToolUp.Forms.Tests.Program

open Expecto
open ToolUp.Forms.Tests.InProcess

let allTests =
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
    ]

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests