module ToolUp.Cloud.Parity.Tests.Program

open Expecto

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console. See docs/platform/testing-conventions.md § "Every Expecto
// pack runs sequenced by default". (Phase 617.)
//
// Sequencing matters twice over in this pack: the emulator legs talk to a
// single shared emulator instance per cloud, and the divergence fixture
// evaluates contract packs in-process.
[<EntryPoint>]
let main argv =
    runTestsInAssemblyWithCLIArgs [ CLIArguments.Sequenced ] argv