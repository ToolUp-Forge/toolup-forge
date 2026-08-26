module ToolUp.Remoting.Analyzers.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs. `[<Tests>]` alone does
/// NOT get a list into this run — `runTestsWithCLIArgs` executes exactly
/// what is registered here.
let private registeredTests = AnalyzerAstTests.tests

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 1 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are
// enough). `--parallel` still overrides. See
// docs/platform/testing-conventions.md § "Every Expecto pack runs
// sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests