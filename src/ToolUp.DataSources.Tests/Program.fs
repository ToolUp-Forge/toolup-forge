// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Program

open System.Reflection
open Expecto
open ToolUp.DataSources.Tests.Tests
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.DataSources.Tests" [
        ConnectorSupportTests.tests
        SqlDataSourceTests.tests
        BigQueryDataSourceTests.tests
        AthenaDataSourceTests.tests
        RedshiftDataSourceTests.tests
        SynapseDataSourceTests.tests
        SnowflakeDataSourceTests.tests
    ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from
/// the list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write
// to the console. `--parallel` still overrides. See
// docs/platform/testing-conventions.md § "Every Expecto pack runs
// sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests