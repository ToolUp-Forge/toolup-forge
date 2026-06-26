// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.ProgramNet

open Expecto

// .NET (Expecto) host entry point for the conformance pack. Runs the SAME
// `ConformanceSuite.suite` the Fable host runs under node:test — a green run
// on each host is the byte-parity / structural-parity gate. Expecto console
// runner: invoke via `dotnet run`, NOT `dotnet test` (which exits 0 having
// run nothing). IsPackable=false; ships nothing.

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv ConformanceSuite.suite