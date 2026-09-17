// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Companions.Fuzz.Tests.Program

open Expecto

/// Sequenced by default, and not overridable here: every case starts a
/// capped child, and one at a time is the whole point of running the
/// corpus through the seam rather than in this process.
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv FuzzTests.tests