// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Tests.Program

open Expecto

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console. `--parallel` still overrides. See
// docs/platform/testing-conventions.md § "Every Expecto pack runs
// sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsInAssemblyWithCLIArgs [ CLIArguments.Sequenced ] argv