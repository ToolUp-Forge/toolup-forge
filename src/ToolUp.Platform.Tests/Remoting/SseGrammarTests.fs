// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The streaming wire's bytes, pinned. Every literal here was captured from
/// the server's framing and the client's splitter as they stood before the
/// grammar moved into Core; they are the oracle the one grammar answers to.
module ToolUp.Platform.Tests.Remoting.SseGrammarTests

open System.Text
open Expecto

let private utf8 (bytes: byte[]) = Encoding.UTF8.GetString bytes

/// An error message carrying every class the JSON encoder escapes: quotes,
/// HTML-sensitive characters, a line feed, a non-ASCII letter.
let private hostileMessage = "<a href=\"x\">\nline é & ' +"

let private pins = [
    "chunk with an id", "event: chunk\nid: corr-0\ndata: {\"x\":1}\n\n"
    "multi-line chunk with an id", "event: chunk\nid: corr-1\ndata: line-a\ndata: line-b\n\n"
    "CRLF chunk payload", "event: chunk\nid: corr-2\ndata: a\ndata: b\ndata: c\n\n"
    "complete", "event: complete\ndata: {}\n\n"
    "error", "event: error\ndata: {\"message\":\"boom\"}\n\n"
    "hostile error",
    "event: error\ndata: {\"message\":\"\\u003Ca href=\\u0022x\\u0022\\u003E\\nline \\u00E9 \\u0026 \\u0027 \\u002B\"}\n\n"
]

let private emitted = [
    "chunk with an id", utf8 (ToolUp.Remoting.Server.Streaming.formatChunkWithId "corr-0" "{\"x\":1}")
    "multi-line chunk with an id", utf8 (ToolUp.Remoting.Server.Streaming.formatChunkWithId "corr-1" "line-a\nline-b")
    "CRLF chunk payload", utf8 (ToolUp.Remoting.Server.Streaming.formatChunkWithId "corr-2" "a\r\nb\rc")
    "complete", utf8 (ToolUp.Remoting.Server.Streaming.formatComplete ())
    "error", utf8 (ToolUp.Remoting.Server.Streaming.formatError "boom")
    "hostile error", utf8 (ToolUp.Remoting.Server.Streaming.formatError hostileMessage)
]

[<Tests>]
let tests =
    testList "SSE grammar — the pinned wire" [
        test "the server frames every pinned case byte-for-byte" {
            for (name, expected), (_, actual) in List.zip pins emitted do
                Expect.equal actual expected name
        }

        test "the client splits a body ending in complete at its last event boundary" {
            let body =
                (pins |> List.take 3 |> List.map snd |> String.concat "")
                + "event: complete\ndata: {}\n\n"

            Expect.equal
                (ToolUp.Remoting.Client.SseFrame.splitComplete (body + "event: chunk\nid: corr-9\ndata: par"))
                (body, "event: chunk\nid: corr-9\ndata: par")
                "every complete event before the last delimiter; the partial event after it"

            Expect.equal
                (ToolUp.Remoting.Client.SseFrame.splitComplete (body.Replace("\n", "\r\n")))
                (body, "")
                "CRLF terminators are normalised; nothing is held after the terminal frame"
        }
    ]