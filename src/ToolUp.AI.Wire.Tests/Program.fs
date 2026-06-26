module ToolUp.AI.Wire.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [] argv JsonValueTests.tests