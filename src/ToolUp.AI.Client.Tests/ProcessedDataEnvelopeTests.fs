// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 817 — the browser half of the `ProcessedData` summary envelope's
/// cross-host contract.
///
/// `ProcessedDataCodec.encode` (server, System.Text.Json + `FableConverters`)
/// is held to the literal in `ProcessedDataEnvelopeFixture` by the .NET
/// pack; this pack holds `DataTypeDisplay.typed`'s decoder
/// (`Fable.SimpleJson`, running under Fable as it does in a browser) to
/// the value. Neither host can execute the other's codec, so the literal
/// is the meeting point — the same discipline the wire corpus applies to
/// its bytes.
module ToolUp.AI.Client.Tests.ProcessedDataEnvelopeTests

open ToolUp.AI.Client.Tests.NodeTest
open Feliz
open ToolUp.Platform
open ToolUp.Platform.Tests.Remoting.ProcessedDataEnvelopeFixture

let tests =
    testList "ProcessedData envelope (Phase 817)" [

        testCase "the browser decoder reads the server's pinned envelope to the sample value"
        <| fun () ->
            match DataTypeDisplay.tryDecode<SampleSummary> (envelope SamplePayload) with
            | Ok decoded -> Expect.equal decoded sample "primitive, Some and list all read as the server wrote them"
            | Error why -> failwith ("the pinned envelope must decode in the browser: " + why)

        testCase "an absent option reads as None and an empty list as []"
        <| fun () ->
            match DataTypeDisplay.tryDecode<SampleSummary> (envelope SampleWithoutNotePayload) with
            | Ok decoded -> Expect.equal decoded sampleWithoutNote "null is None"
            | Error why -> failwith ("the pinned envelope must decode in the browser: " + why)

        testCase "a payload that is not the type asked for is a named refusal, not a throw"
        <| fun () ->
            match DataTypeDisplay.tryDecode<SampleSummary> (envelope """[1,2,3]""") with
            | Ok _ -> failwith "an array is not a SampleSummary"
            | Error why -> Expect.isTrue (why.Contains "SampleSummary") "the refusal names the type"

        testCase "a typed display renders exactly the entries that carry a decodable envelope"
        <| fun () ->
            let seen = ref []

            let display =
                DataTypeDisplay.typed
                    {
                        Id = "sample"
                        DisplayName = "Sample"
                        Schema = None
                    }
                    (fun (summaries: SampleSummary list) ->
                        seen.Value <- summaries
                        Html.none)

            let entries = [
                ProcessedDataTypes.ProcessedFileEntry.summarised
                    "a.csv"
                    "sample"
                    System.DateTime.UtcNow
                    (envelope SamplePayload)
                ProcessedDataTypes.ProcessedFileEntry.summarised
                    "b.csv"
                    "sample"
                    System.DateTime.UtcNow
                    (envelope """{"not":"a summary"}""")
                ProcessedDataTypes.ProcessedFileEntry.failed "c.csv" "sample" System.DateTime.UtcNow "boom"
            ]

            DataTypeDisplay.render display entries |> ignore

            Expect.equal
                seen.Value
                [ sample ]
                "one decodable envelope, one summary rendered; the undecodable and the failed are dropped"
    ]