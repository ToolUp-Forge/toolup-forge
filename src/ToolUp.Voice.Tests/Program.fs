module ToolUp.Voice.Tests.Program

open Expecto

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are
// enough). `--parallel` still overrides. See
// docs/platform/testing-conventions.md § "Every Expecto pack runs
// sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsInAssemblyWithCLIArgs [ CLIArguments.Sequenced ] argv