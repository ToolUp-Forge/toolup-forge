// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuditSinks.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.AuditSinks.Tests" [
        S3ArchiveAuditSinkTests.tests
        SplunkHecAuditSinkTests.tests
        DatadogLogsAuditSinkTests.tests
        CefAuditSinkTests.tests
        ChainedAuditLedgerTests.tests
    ]

/// Phase 722 - the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

// Sequenced by default - Expecto deadlocks when parallel tests write to
// the console. `--parallel` still overrides (Phase 617).
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests