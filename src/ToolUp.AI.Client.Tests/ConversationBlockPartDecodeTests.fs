// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1128 — the browser half of the block part's cross-host contract.
///
/// The server's conversation-blob writer (System.Text.Json with the
/// remoting converters) is held to the literal in `BlockPartFixtures` by
/// the .NET Platform pack; this pack holds the client's reflection decoder
/// (`Fable.SimpleJson`, transpiled, as the remoting proxy runs it in a
/// browser) to the same values. Neither host can execute the other's codec,
/// so the literal is the meeting point.
module ToolUp.AI.Client.Tests.ConversationBlockPartDecodeTests

open Fable.SimpleJson
open ToolUp.Platform.AI
open ToolUp.AI.Wire.Conformance
open ToolUp.AI.Client.Tests.NodeTest

/// What the client's reflection decode makes of `text` at `'T`.
let inline private decode<'T> (text: string) : 'T =
    Convert.fromJsonAs (SimpleJson.parseNative text) (createTypeInfo typeof<'T>) :?> 'T

let tests =
    testList "Conversation block part (Phase 1128)" [

        testCase "the browser decodes the parts the server wrote, blocks included"
        <| fun () ->
            let decoded = decode<AIContentPart list> BlockPartFixtures.partsJson
            Expect.equal decoded BlockPartFixtures.parts "prose, a table block and a trend block, as written"

        testCase "a decoded block keeps its node and provenance byte for byte"
        <| fun () ->
            match decode<AIContentPart list> BlockPartFixtures.partsJson with
            | [ TextPart _; BlockPart(node, provenance); BlockPart _ ] ->
                Expect.equal node BlockPartFixtures.tableNode "the node's wire JSON is the string the server held"

                Expect.equal
                    provenance.Request
                    BlockPartFixtures.tableProvenance.Request
                    "the request survives as the model wrote it"

                Expect.equal
                    provenance.Withheld
                    [
                        {
                            PolicyRef = "trade-figures"
                            Count = 2
                        }
                    ]
                    "the withheld count"
            | other -> failwithf "expected prose then two blocks, got %A" other

        testCase "the summary and export computed in the browser match the server's"
        <| fun () ->
            match decode<AIContentPart list> BlockPartFixtures.partsJson with
            | [ _; table; trend ] ->
                Expect.equal (AIContentPart.redactedSummary table) BlockPartFixtures.tableSummary "table summary"
                Expect.equal (AIContentPart.exportPlaceholder 2 table) BlockPartFixtures.tableExport "table export"
                Expect.equal (AIContentPart.exportPlaceholder 3 trend) BlockPartFixtures.trendExport "trend export"
            | other -> failwithf "expected three parts, got %A" other
    ]