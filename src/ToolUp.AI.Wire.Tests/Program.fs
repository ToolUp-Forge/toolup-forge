module ToolUp.AI.Wire.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs
        []
        argv
        (testList
            "ToolUp.AI.Wire.Tests"
            [ JsonValueTests.tests
              TransportTests.tests
              OpenAIWireTests.tests
              GeminiWireTests.tests ])
