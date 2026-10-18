module ToolUp.Remoting.Analyzers.Tests.Program

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.Support
open ToolUp.Remoting.Analyzers

/// The explicitly-enumerated list this pack runs. `[<Tests>]` alone does
/// NOT get a list into this run — `runTestsWithCLIArgs` executes exactly
/// what is registered here.
let private registeredTests =
    testList "ToolUp.Remoting.Analyzers" [ AnalyzerAstTests.tests; ContainmentRuleTests.tests ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 2 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are
// enough). `--parallel` still overrides. See
// docs/platform/testing-conventions.md § "Every Expecto pack runs
// sequenced by default". (Phase 617.)
// Phase 776 — two host modes beside the test run, because this pack's exe is
// the one process that already loads the analyser and the compiler service:
//
//   --scan-tree <root>          the `Analyzers` leg of VerifyAll: run the TUA
//                               rules over every tracked src/ file, reconcile
//                               with the inventory, check the rule document.
//   --emit-rules-doc <path>     regenerate docs/platform/analyzer-rules.md.
[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | [ "--scan-tree"; root ] ->
        match TreeScan.verify root with
        | [] ->
            printfn "Analyzers: clean."
            0
        | findings ->
            for f in findings do
                eprintfn "Analyzers: %s" f

            eprintfn "Analyzers: %d finding(s)." findings.Length
            1
    | [ "--emit-rules-doc"; path ] ->
        System.IO.File.WriteAllText(path, Rules.renderDocument ())
        printfn "Analyzers: wrote %s" path
        0
    | _ -> runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests