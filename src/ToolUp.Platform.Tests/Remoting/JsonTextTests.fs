// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 843 — `JsonText`, the browser's JSON reader, held to the
/// server's on the .NET host.
///
/// `JsonText` is FSharpCore-only so that this pack can run it: the
/// browser decodes a response through it, and here it is DIFFERENTIAL
/// against `JsonRead` (System.Text.Json) — every Phase 784 corpus text,
/// every prefix of the short ones, and the pinned agreement cases the
/// Fable pack asserts too (`JsonClientDecodeFixture`). A reading the two
/// disagree on is a red run here rather than a field report there.
module ToolUp.Platform.Tests.Remoting.JsonTextTests

open System
open System.Text.Json
open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Platform.Tests.Remoting
open ToolUp.Platform.Tests.Remoting.JsonClientDecodeFixture

/// Accept-or-refuse, and on accept the model. Refusal TEXT is not
/// compared: System.Text.Json words its own syntax errors.
let private outcome (result: Result<JsonValue, DecodeError>) : JsonValue option =
    match result with
    | Ok value -> Some value
    | Error _ -> None

let private corpusTexts () : (string * string) list = [
    for c in WireCorpus.pinnedCases do
        c.Name, c.WriteJson()
    for c in WireCorpus.generatedCases () do
        c.Name, c.WriteJson()
]

[<Tests>]
let tests =
    testList "Phase 843 — JsonText, the client's JSON reader" [

        testList "agreement with JsonRead" [
            testCase "every corpus text reads to the same model on both readers"
            <| fun () ->
                let texts = corpusTexts ()
                Expect.isGreaterThan (List.length texts) 40 "the corpus is close to empty; the differential is vacuous"

                for name, text in texts do
                    match JsonRead.tryParse text, JsonText.tryParse text with
                    | Ok server, Ok client -> Expect.equal client server (sprintf "`%s` read differently" name)
                    | Error e, _ ->
                        failtestf
                            "`%s`: the server reader refused its own writer's text: %s"
                            name
                            (DecodeError.render e)
                    | _, Error e ->
                        failtestf "`%s`: JsonText refused a text the server reads: %s" name (DecodeError.render e)

            testCase "every PREFIX of the short corpus texts is accepted or refused alike"
            <| fun () ->
                // Truncation is the malformation a response actually
                // suffers, and every prefix of a JSON text is a distinct
                // syntax edge. Both readers must agree on each.
                let mutable compared = 0

                for name, text in corpusTexts () do
                    if text.Length <= 400 then
                        for length in 0 .. text.Length - 1 do
                            let prefix = text.Substring(0, length)
                            compared <- compared + 1

                            Expect.equal
                                (outcome (JsonText.tryParse prefix))
                                (outcome (JsonRead.tryParse prefix))
                                (sprintf "`%s` cut at %d (`%s`)" name length prefix)

                Expect.isGreaterThan compared 1000 "too few prefixes compared; the probe is close to vacuous"

            testCase "the pinned agreement cases hold on this host, for both readers"
            <| fun () ->
                for name, text, expected in agreementCases do
                    Expect.equal (outcome (JsonRead.tryParse text)) expected (sprintf "JsonRead: %s" name)
                    Expect.equal (outcome (JsonText.tryParse text)) expected (sprintf "JsonText: %s" name)

            testCase "the bounds are the server reader's bounds"
            <| fun () ->
                Expect.equal JsonText.DefaultMaxDepth JsonRead.DefaultMaxDepth "depth bound"
                Expect.equal JsonText.DefaultMaxMembers JsonRead.DefaultMaxMembers "width bound"
        ]

        testList "the pinned cross-host payload (the .NET half)" [
            testCase "the remoting writer emits exactly the pinned text"
            <| fun () ->
                Expect.equal
                    (JsonSerializer.Serialize<LedgerLine>(sample, WireCorpus.jsonOptions))
                    SamplePayload
                    "the writer's text moved; re-pin SamplePayload (and sampleModel) in JsonClientDecodeFixture"

            testCase "both readers read the pinned text to the pinned model"
            <| fun () ->
                Expect.equal (JsonRead.tryParse SamplePayload) (Ok sampleModel) "JsonRead"
                Expect.equal (JsonText.tryParse SamplePayload) (Ok sampleModel) "JsonText"

            testCase "the decoder reads the pinned model to the sample, every digit and tick intact"
            <| fun () -> Expect.equal (decoder sampleModel) (Ok sample) "decoded value"
        ]

        testList "bounds" [
            testCase "a container past the width bound is refused at its path, before it is built"
            <| fun () ->
                match JsonText.tryParseWith 64 3 """{"rows":[[1],[1,2,3,4]]}""" with
                | Error e ->
                    Expect.equal e.Path [ "rows"; "[1]" ] "the path names the container that breached"
                    Expect.stringContains e.Expected "at most 3" "the bound is named"
                | Ok v -> failtestf "accepted %A" v

            testCase "nesting past the depth bound is refused at its path"
            <| fun () ->
                match JsonText.tryParseWith 2 100 """{"a":{"b":{"c":1}}}""" with
                | Error e ->
                    Expect.equal e.Path [ "a"; "b" ] "the path names where the nesting breached"
                    Expect.stringContains e.Expected "at most 2" "the bound is named"
                | Ok v -> failtestf "accepted %A" v

            testCase "a syntax refusal names the offset and carries no path"
            <| fun () ->
                match JsonText.tryParse """{"a":[1,2,}""" with
                | Error e ->
                    Expect.equal e.Path [] "no path on a syntax refusal"
                    Expect.equal e.Expected "a JSON document" "what was expected"
                    Expect.stringContains e.Found "offset 10" "where the reader stopped"
                | Ok v -> failtestf "accepted %A" v
        ]

        testCase "total: no text makes it throw"
        <| fun () ->
            // A fixed seed over the characters JSON's grammar turns on,
            // so a failure reproduces. Totality is the claim, not a
            // particular refusal.
            let alphabet = "{}[]\":,.-+eE0123456789tfnrul \\/bu\t\né\uD800x"
            let random = Random 843

            for _ in 1..20000 do
                let length = random.Next(0, 24)

                let text =
                    String(Array.init length (fun _ -> alphabet.[random.Next alphabet.Length]))

                match JsonText.tryParse text with
                | Ok _
                | Error _ -> ()

            Expect.isError (JsonText.tryParse null) "a null text is a refusal, not an exception"
    ]