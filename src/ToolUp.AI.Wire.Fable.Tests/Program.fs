module ToolUp.AI.Wire.Fable.Tests.Program

open ToolUp.AI.Wire
open ToolUp.AI.Wire.Fable.Tests.NodeTest
open ToolUp.AI.Wire.Tests

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
    ]

[<EntryPoint>]
let main _argv = runTests tests