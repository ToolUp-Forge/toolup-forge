module ToolUp.AI.Client.Tests.Program

open ToolUp.AI.Client.Tests.NodeTest

let allTests =
    testList "ToolUp.AI.Client.Tests" [ PlatformAIKeysAdminUITests.tests ]

[<EntryPoint>]
let main _argv = runTests allTests