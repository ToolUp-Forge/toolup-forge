// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.ProgramFable

open ToolUp.AI.Wire.Conformance.NodeTest

// Fable (browser) host entry point for the conformance pack. Transpiles via
// Fable and runs the SAME `ConformanceSuite.suite` the .NET host runs, under
// Node's built-in test runner (`node:test`, zero npm deps). Run procedure
// (from this directory): `dotnet tool restore`, `dotnet fable -o output
// --noCache`, then `node --test output/ProgramFable.js`.

[<EntryPoint>]
let main _argv = runTests ConformanceSuite.suite