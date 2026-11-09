// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Tests.StreamingFoldTests

// The OpenAI and Gemini stream folds, replayed over recorded transcripts. The
// expected deltas and responses are what the mutable accumulators they
// replaced produced for the same lines (recorded before those were deleted),
// so the fold is held to the old output, not to a re-derivation of it. One
// difference is deliberate: a Gemini chunk carrying two text parts forwards
// them as one delta (the mutable version called back once per part); the
// forwarded text, concatenated, is identical.

open Expecto
open ToolUp.Platform.AI
open ToolUp.AI.Wire

let private call id name args : AIProviderToolCall = {
    Id = id
    Name = name
    Arguments = args
}

let tests =
    testList "Stream folds replay the recorded transcripts" [
        test "OpenAI: prose, two interleaved tool calls, the finish reason and the usage chunk" {
            let deltas, response =
                StreamProcessor.replay OpenAIStreaming.processor StreamTranscripts.openAI

            Expect.equal deltas [ "Checking"; " the time." ] "the forwarded deltas"

            Expect.equal
                response
                {
                    Content = "Checking the time."
                    ToolCalls = [
                        call "call_a" "get_time" """{"tz":"Europe/Paris"}"""
                        call "call_b" "get_zone" """{"city":"Paris"}"""
                    ]
                    StopReason = StopReason.ToolUse
                    Usage =
                        Some {
                            PromptTokens = 40
                            CachedPromptTokens = 8
                            OutputTokens = 12
                            CacheCreationTokens = None
                        }
                }
                "the assembled response"
        }

        test "OpenAI: a reply cut at its length cap" {
            let deltas, response =
                StreamProcessor.replay OpenAIStreaming.processor StreamTranscripts.openAITruncated

            Expect.equal deltas [ "Once upon"; " a time" ] "the forwarded deltas"
            Expect.equal response.Content "Once upon a time" "the text"
            Expect.equal response.StopReason StopReason.MaxTokens "length is max_tokens"
            Expect.isNone response.Usage "no usage chunk was sent"
        }

        test "Gemini: text chunks, a whole function call, a blank line, the finish reason and usage" {
            let deltas, response =
                StreamProcessor.replay GeminiStreaming.processor StreamTranscripts.gemini

            Expect.equal deltas [ "Checking"; " the time." ] "the forwarded deltas, one per chunk"
            Expect.equal (String.concat "" deltas) "Checking the time." "the same text the per-part callbacks forwarded"

            Expect.equal
                response
                {
                    Content = "Checking the time."
                    ToolCalls = [ call "gemini-fc-get_time-0" "get_time" """{"tz":"Europe/Paris"}""" ]
                    StopReason = StopReason.ToolUse
                    Usage =
                        Some {
                            PromptTokens = 30
                            CachedPromptTokens = 4
                            OutputTokens = 9
                            CacheCreationTokens = None
                        }
                }
                "the assembled response: a function call makes the turn tool_use whatever finishReason said"
        }

        test "Gemini: a reply cut at its length cap" {
            let deltas, response =
                StreamProcessor.replay GeminiStreaming.processor StreamTranscripts.geminiTruncated

            Expect.equal deltas [ "Once upon"; " a time" ] "the forwarded deltas"
            Expect.equal response.StopReason StopReason.MaxTokens "MAX_TOKENS is max_tokens"
        }

        test "Claude: the processor is the existing fold over data lines" {
            let lines =
                ClaudeWireFixtures.streamingChunks |> List.map (fun chunk -> "data: " + chunk)

            let deltas, response =
                StreamProcessor.replay ClaudeStreaming.processor (lines @ [ "data: [DONE]"; ": ping" ])

            let expected, text =
                ClaudeWireFixtures.foldStream ClaudeWireFixtures.streamingChunks

            Expect.equal response expected "the same response the bare fold builds"
            Expect.equal (String.concat "" deltas) text "the same forwarded text"
        }

        test "StopReason reads its own tokens back and keeps a vendor's verbatim" {
            for reason in [ StopReason.EndTurn; StopReason.ToolUse; StopReason.MaxTokens ] do
                Expect.equal (StopReason.ofToken reason.Token) reason $"{reason.Token} round-trips"

            Expect.equal (StopReason.ofToken "stop_sequence") (StopReason.Other "stop_sequence") "kept verbatim"
            Expect.equal (StopReason.Other "refusal").Token "refusal" "and spelled back"
        }
    ]