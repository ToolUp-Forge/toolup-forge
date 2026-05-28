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
    ]

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests