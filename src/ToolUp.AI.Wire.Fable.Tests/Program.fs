module ToolUp.AI.Wire.Fable.Tests.Program

open ToolUp.AI.Wire
open ToolUp.AI.Wire.Fable.Tests.NodeTest
open ToolUp.AI.Wire.Tests
open ToolUp.Platform.AI // Phase 251 — ErrorClassifier / AIProviderError on the Fable host

// Fable smoke for the portable JSON value model. Runs the SAME fixture set
// the .NET Expecto pack runs (WireFixtures.fs, compiled into both). Asserting
// `serialize value = golden` on this host and on .NET proves the two hosts
// emit byte-identical output for every fixture — the parity property the
// later gate depends on. Round-trip checks `serialize ∘ parse = id` over the
// golden bytes, exercising the #if FABLE_COMPILER JSON.parse bridge.

let tests =
    testList "ToolUp.AI.Wire (Fable host)" [
        testList "serialize (canonical, byte-stable)" [
            for name, value, golden in WireFixtures.fixtures do
                testCase (sprintf "serialize %s" name) (fun () ->
                    Expect.equal (JsonHost.serialize value) golden "canonical serialization")
        ]

        testList "round-trip (serialize ∘ parse = id over the golden bytes)" [
            for name, _, golden in WireFixtures.fixtures do
                testCase (sprintf "round-trip %s" name) (fun () ->
                    match JsonHost.parse golden with
                    | Some v -> Expect.equal (JsonHost.serialize v) golden "serialize ∘ parse identity"
                    | None -> failwithf "parse returned None for %s" golden)
        ]

        testList "parse rejects malformed input" [
            testCase "malformed JSON → None" (fun () ->
                Expect.isNone (JsonHost.parse "{not json") "truncated object"
                Expect.isNone (JsonHost.parse "[1,2,") "truncated array")
        ]

        // Phase 252 — the OpenAI portable mapper Fable-compiles; assert
        // buildRequestBody emits bytes identical to the .NET host over the
        // shared golden fixtures (the byte-parity gate) and parseResponse
        // reifies identical AIProviderResponse shapes.
        testList "OpenAI wire mapping (Fable host)" [
            testList "buildRequestBody (byte-stable golden)" [
                for name, actual, golden in OpenAIWireFixtures.requestFixtures do
                    testCase (sprintf "request %s" name) (fun () -> Expect.equal actual golden "request body bytes")
            ]

            testList "parseResponse (shape parity)" [
                for name, parsed, expected in OpenAIWireFixtures.responseFixtures do
                    testCase (sprintf "response %s" name) (fun () -> Expect.equal parsed expected "parsed response")
            ]
        ]

        // Phase 251 — the egress classifier is pure and portable; assert it
        // maps statuses identically on the Fable host (the same rule the
        // .NET TransportTests pin), proving Transport/ Fable-compiles + runs.
        testList "ErrorClassifier (Fable host)" [
            testCase "401 → PermanentClient" (fun () ->
                Expect.equal
                    (ErrorClassifier.classifyStatus 401 "x")
                    (PermanentClient(401, "x"))
                    "401 maps to PermanentClient")
            testCase "429 → TransientServer" (fun () ->
                Expect.equal
                    (ErrorClassifier.classifyStatus 429 "y")
                    (TransientServer(429, "y"))
                    "429 maps to TransientServer")
            testCase "503 → TransientServer" (fun () ->
                Expect.equal
                    (ErrorClassifier.classifyStatus 503 "z")
                    (TransientServer(503, "z"))
                    "5xx maps to TransientServer")
            testCase "transport failure → TransientNetwork" (fun () ->
                Expect.equal
                    (ErrorClassifier.classifyTransportFailure "refused")
                    (TransientNetwork "refused")
                    "transport failure maps to TransientNetwork")
        ]
    ]

[<EntryPoint>]
let main _argv = runTests tests