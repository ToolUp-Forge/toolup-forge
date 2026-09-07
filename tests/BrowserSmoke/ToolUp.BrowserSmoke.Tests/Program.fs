// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.BrowserSmoke.Tests.Program

open Expecto
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.BrowserSmoke.Tests" [ CoEditSmokeTests.tests; OfflineSmokeTests.tests ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

[<EntryPoint>]
let main argv =
    // Sequenced, per the repo-wide default: Expecto deadlocks when
    // parallel tests write to the console (Phase 617), and these
    // scenarios each own a browser process — running two at once would
    // multiply the machine's load without shortening the gate.
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests