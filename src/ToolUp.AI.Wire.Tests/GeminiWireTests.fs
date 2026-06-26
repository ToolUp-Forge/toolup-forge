module ToolUp.AI.Wire.Tests.GeminiWireTests

// ─── Phase 253 — Gemini portable wire mapper (.NET host) ─────────
//
// Offline regression for `GeminiAIProviderWire` (the source file is
// linked into this pack — see the .fsproj — so the SAME mapper that ships
// in the Gemini provider is exercised here with no live API). The Fable
// smoke (`ToolUp.AI.Wire.Fable.Tests`) drives the identical fixtures, so a
// green run on each host proves the mapper builds byte-identical request
// bytes and parses the identical response shape across hosts.

open Expecto
open ToolUp.Platform.AI
open ToolUp.AI.Wire.Tests.GeminiWireFixtures

let tests =
    testList "GeminiWire" [
        testList "buildRequestBody" [
            test "minimal user message → canonical contents shape" {
                let body = GeminiAIProviderWire.buildRequestBody simpleRequestMessages [] None None
                Expect.equal body simpleRequestGolden "byte-stable request"
            }

            test "system + user + tool → systemInstruction + functionDeclarations + toolConfig (in order)" {
                let body =
                    GeminiAIProviderWire.buildRequestBody
                        toolRequestMessages
                        toolRequestTools
                        toolRequestSystemPrompt
                        None

                Expect.equal body toolRequestGolden "byte-stable request with tools"
            }
        ]

        testList "parseResponse" [
            test "text response → content + end_turn + usage" {
                match GeminiAIProviderWire.parseResponse textResponseJson with
                | Error d -> failtestf "expected Ok; got %s" d
                | Ok r ->
                    Expect.equal r.Content textResponseExpectedContent "content"
                    Expect.equal r.StopReason textResponseExpectedStopReason "stop reason"
                    Expect.isEmpty r.ToolCalls "no tool calls"

                    match r.Usage with
                    | Some u ->
                        Expect.equal u.PromptTokens textResponseExpectedPromptTokens "prompt tokens"
                        Expect.equal u.CachedPromptTokens textResponseExpectedCachedTokens "cached tokens"
                        Expect.equal u.OutputTokens textResponseExpectedOutputTokens "output tokens"
                        Expect.equal u.CacheCreationTokens None "Gemini never reports cache-creation"
                    | None -> failtest "expected usage to be populated"
            }

            test "functionCall response → synthetic id + canonical args + tool_use" {
                match GeminiAIProviderWire.parseResponse functionCallResponseJson with
                | Error d -> failtestf "expected Ok; got %s" d
                | Ok r ->
                    Expect.equal r.StopReason functionCallExpectedStopReason "tool_use overrides finishReason"

                    match r.ToolCalls with
                    | [ tc ] ->
                        Expect.equal tc.Name functionCallExpectedName "tool name"
                        Expect.equal tc.Id functionCallExpectedSyntheticId "synthetic id (name + index)"
                        Expect.equal tc.Arguments functionCallExpectedArgs "canonical args json"
                    | other -> failtestf "expected exactly one tool call, got %A" other
            }

            test "invalid JSON → Error (provider maps to MalformedResponse)" {
                match GeminiAIProviderWire.parseResponse "{not json" with
                | Error _ -> ()
                | Ok r -> failtestf "expected Error on malformed body, got %A" r
            }
        ]

        testList "name-keyed functionResponse round-trip" [
            // The id-correlation invariant: a synthetic id minted on parse
            // strips back to the tool NAME when a tool-result turn is built.
            test "synthetic id strips back to the tool name" {
                let recovered =
                    GeminiAIProviderWire.toolNameFromSyntheticId functionCallExpectedSyntheticId "fallback"

                Expect.equal recovered functionCallExpectedName "id → name recovery"
            }

            test "an unknown-shaped id falls back to the supplied name" {
                let recovered =
                    GeminiAIProviderWire.toolNameFromSyntheticId "call_abc123" "the_name"

                Expect.equal recovered "the_name" "non-synthetic id uses fallback"
            }

            test "tool-result turn serialises functionResponse keyed by the recovered name" {
                let body = GeminiAIProviderWire.buildRequestBody [ toolResultMessage ] [] None None
                Expect.equal body toolResultRequestGolden "name-keyed functionResponse round-trip"
            }
        ]
    ]