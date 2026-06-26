module ToolUp.AI.Wire.Tests.OpenAIWireTests

open Expecto
open ToolUp.AI.Wire.Tests

// .NET host pack for the portable OpenAI mapper. Asserts the golden
// request bytes + parsed-response shapes from the shared
// OpenAIWireFixtures.fs (compiled into the Fable smoke too). A green run
// here + a green Fable run proves cross-host byte-parity for the OpenAI
// request build (Wave 32, Phase 252).

let tests =
    testList "OpenAI wire mapping (.NET host)" [
        testList "buildRequestBody (byte-stable golden)" [
            for name, actual, golden in OpenAIWireFixtures.requestFixtures do
                testCase (sprintf "request %s" name) (fun () -> Expect.equal actual golden "request body bytes")
        ]

        testList "parseResponse (shape parity)" [
            for name, parsed, expected in OpenAIWireFixtures.responseFixtures do
                testCase (sprintf "response %s" name) (fun () -> Expect.equal parsed expected "parsed response")
        ]
    ]