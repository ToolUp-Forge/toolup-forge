module MyDataPlatform.Tests.Program

open Expecto

[<EntryPoint>]
let main argv =
    // Sequenced: Expecto's console writer can deadlock when parallel tests log.
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv (testList "MyDataPlatform" [ Reader.tests; EndToEnd.tests ])