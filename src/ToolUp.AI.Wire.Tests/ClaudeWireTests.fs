// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Tests.ClaudeWireTests

open Expecto
open ToolUp.Platform.AI
open ToolUp.AI.Wire.Tests.ClaudeWireFixtures

// .NET (Expecto) host pack for the portable Claude wire mapper + the pure
// ClaudeStreaming state machine (Phase 254). The Fable smoke runs the SAME
// fixtures under node:test, so a green run on both proves cross-host parity
// of the request bytes + streaming assembly with no live SSE.

let tests =
    testList "Claude wire (portable mapper + streaming)" [

        testList "request build — canonical bytes" [
            for name, build, golden in requestFixtures do
                testCase $"buildRequestBody {name}" (fun () ->
                    Expect.equal (build ()) golden "request serializes to the canonical bytes")
        ]

        testList "response parse" [
            testCase "text + tool_use + usage" (fun () ->
                let r = parsedResponse ()
                Expect.equal r.Content "Hello" "text content concatenated"
                Expect.equal r.StopReason "tool_use" "stop reason"
                Expect.equal r.ToolCalls.Length 1 "one tool call"
                let tc = r.ToolCalls.Head
                Expect.equal tc.Id "toolu_1" "tool call id"
                Expect.equal tc.Name "get_time" "tool call name"
                Expect.equal tc.Arguments """{"tz":"UTC"}""" "tool input re-serialized as Arguments"

                match r.Usage with
                | Some u ->
                    Expect.equal u.PromptTokens 15 "PromptTokens = input + cache_read + cache_creation"
                    Expect.equal u.CachedPromptTokens 2 "cache_read tokens"
                    Expect.equal u.OutputTokens 5 "output tokens"
                    Expect.equal u.CacheCreationTokens (Some 3) "cache_creation tokens"
                | None -> failtest "expected Usage to be populated")
        ]

        testList "streaming state machine" [
            testCase "text + out-of-band tool-call assembly + split usage" (fun () ->
                let response, emitted = foldStream streamingChunks
                Expect.equal emitted "Hello world" "onStream surfaced the text deltas in order"
                Expect.equal response.Content "Hello world" "Content accumulates the text deltas"
                Expect.equal response.StopReason "tool_use" "stop_reason from message_delta"
                Expect.equal response.ToolCalls.Length 1 "one tool call assembled"
                let tc = response.ToolCalls.Head
                Expect.equal tc.Id "toolu_9" "tool call id from content_block_start"
                Expect.equal tc.Name "lookup" "tool call name from content_block_start"
                Expect.equal tc.Arguments streamingExpectedArguments "input_json_delta fragments concatenated"

                match response.Usage with
                | Some u ->
                    Expect.equal u.PromptTokens 22 "PromptTokens = 12 + 4 + 6"
                    Expect.equal u.CachedPromptTokens 4 "cache_read tokens from message_start"
                    Expect.equal u.OutputTokens 7 "cumulative output_tokens from message_delta"
                    Expect.equal u.CacheCreationTokens (Some 6) "cache_creation tokens from message_start"
                | None -> failtest "expected Usage to be populated")

            testCase "zero-input tool call defaults Arguments to {}" (fun () ->
                let response, emitted = foldStream streamingZeroArgChunks
                Expect.equal emitted "" "no text deltas emitted"
                Expect.equal response.ToolCalls.Length 1 "one tool call"
                Expect.equal response.ToolCalls.Head.Arguments "{}" "empty Arguments buffer default-filled to {}"
                Expect.isNone response.Usage "no usage event ⇒ Usage None")
        ]
    ]