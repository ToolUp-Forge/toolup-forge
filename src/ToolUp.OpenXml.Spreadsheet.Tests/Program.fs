// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.Spreadsheet.Tests.Program

open Expecto
open ToolUp.OpenXml.Spreadsheet.Tests.InProcess
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.OpenXml.Spreadsheet.Tests" [ WorkbookModelTests.tests; EmitTests.tests; ReopenTests.tests ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

/// Sequenced by default, for the reason `docs/platform/testing-
/// conventions.md` records: Expecto replaces the console writers with a
/// synchronized pair, and a parallel test writing through them can
/// deadlock against the real console stream lock.
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests