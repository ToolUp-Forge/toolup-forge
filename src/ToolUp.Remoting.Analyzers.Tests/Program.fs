module ToolUp.Remoting.Analyzers.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv AnalyzerAstTests.tests