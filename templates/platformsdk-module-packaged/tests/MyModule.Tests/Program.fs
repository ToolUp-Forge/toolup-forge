module MODULE_NAMESPACE_ROOT.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    // Sequenced by default. Expecto swaps the console writer for a
    // synchronized one, and a parallel test writing through it can
    // deadlock against the real console stream lock — cheap to avoid,
    // expensive to diagnose. Pass `--parallel` to override.
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv Conformance.tests