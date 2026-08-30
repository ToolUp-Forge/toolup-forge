// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.Program

open Expecto
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.Cli.Tests" [
        DispatchTests.tests
        DockerEmitTests.tests
        StampRoundTripTests.tests
        ModuleAddRemoveTests.tests
        MembershipsDoctorTests.tests
        TenantUserVerbsTests.tests
    ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console, and this is the pack that proved it: every test here runs
// a real `toolup` command and every command prints. In CI it hung for 47
// minutes, consumed the job's whole timeout budget, and stopped every
// later pack from running. `--parallel` still overrides if you want to
// reproduce that. Full analysis + measurements:
// docs/platform/testing-conventions.md § "Every Expecto pack runs
// sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests