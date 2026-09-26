// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.TriageCalibration.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Support
open ToolUp.TriageCalibration.Tests.Tests

let private registeredTests =
    testList "ToolUp.TriageCalibration" [ eligibilityTests; rescoringTests; failureTests; schemaTests ]

/// Phase 722 - the registered list plus the unregistered-`[<Tests>]`
/// guard, as every VerifyAll pack carries it.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 4 registeredTests

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests