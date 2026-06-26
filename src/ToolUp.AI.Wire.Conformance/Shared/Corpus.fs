// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.Corpus

open ToolUp.Platform.AI
open ToolUp.AI.Wire.Tests

// ─── The consolidated conformance corpus (Wave 32, Phase 255) ────────
//
// Lifts the three per-provider golden fixture sets (seeded by Phases
// 252 / 253 / 254) into ONE uniform corpus the dual-run harness iterates:
//
//   • `requestFixtures`  — `(name, builtBytes, goldenBytes)` for every
//     provider's request build. The harness asserts `builtBytes = golden`
//     on BOTH hosts, so a green run on each proves the request bytes are
//     byte-identical across the .NET and Fable backends.
//   • `<provider>ResponseFixtures` — `(name, parsed, expected)` so the
//     harness asserts structural parity of the parsed `AIProviderResponse`
//     (content + tool calls + stop reason + `TokenUsage`).
//
// The golden DATA is NOT re-encoded here — it is the existing
// `*WireFixtures.fs` source (one source of truth), source-linked into this
// project. This module only adapts the three heterogeneous fixture shapes
// into the two uniform shapes the harness consumes.

// ─── Request byte-parity corpus (all three providers) ────────────────

/// OpenAI fixtures already ship as `(name, actual, golden)` triples.
let private openAIRequests =
    OpenAIWireFixtures.requestFixtures
    |> List.map (fun (name, actual, golden) -> "openai/" + name, actual, golden)

/// Gemini fixtures hold the canned inputs + goldens as individual values;
/// build the request bytes here (mirroring `GeminiWireTests`).
let private geminiRequests = [
    "gemini/simple",
    GeminiAIProviderWire.buildRequestBody GeminiWireFixtures.simpleRequestMessages [] None None,
    GeminiWireFixtures.simpleRequestGolden

    "gemini/system+tool",
    GeminiAIProviderWire.buildRequestBody
        GeminiWireFixtures.toolRequestMessages
        GeminiWireFixtures.toolRequestTools
        GeminiWireFixtures.toolRequestSystemPrompt
        None,
    GeminiWireFixtures.toolRequestGolden

    // Synthetic-id → tool-NAME recovery on the request leg.
    "gemini/tool-result-roundtrip",
    GeminiAIProviderWire.buildRequestBody [ GeminiWireFixtures.toolResultMessage ] [] None None,
    GeminiWireFixtures.toolResultRequestGolden
]

/// Claude fixtures ship as `(name, thunk, golden)` — run the thunk.
let private claudeRequests =
    ClaudeWireFixtures.requestFixtures
    |> List.map (fun (name, build, golden) -> "claude/" + name, build (), golden)

/// The full cross-provider request corpus, `(name, builtBytes, golden)`.
let requestFixtures: (string * string * string) list =
    openAIRequests @ geminiRequests @ claudeRequests

// ─── Response structural-parity corpus ───────────────────────────────

/// OpenAI ships `(name, parsed, expected)` response triples directly.
let openAIResponseFixtures: (string * AIProviderResponse * AIProviderResponse) list =
    OpenAIWireFixtures.responseFixtures
    |> List.map (fun (name, parsed, expected) -> "openai/" + name, parsed, expected)

/// Gemini `parseResponse` returns `Result`; unwrap to a uniform
/// `(name, parsed, expected)` triple. A parse `Error` is surfaced as a
/// sentinel `AIProviderResponse` so the harness FAILS loudly (the records
/// won't compare equal) rather than crashing module init with `failwith`.
let private unwrapGemini (r: Result<AIProviderResponse, string>) : AIProviderResponse =
    match r with
    | Ok r -> r
    | Error e -> {
        Content = "GEMINI-PARSE-ERROR: " + e
        ToolCalls = []
        StopReason = ""
        Usage = None
      }

let geminiResponseFixtures: (string * AIProviderResponse * AIProviderResponse) list = [
    "gemini/text+usage",
    unwrapGemini (GeminiAIProviderWire.parseResponse GeminiWireFixtures.textResponseJson),
    {
        Content = GeminiWireFixtures.textResponseExpectedContent
        ToolCalls = []
        StopReason = GeminiWireFixtures.textResponseExpectedStopReason
        Usage =
            Some {
                PromptTokens = GeminiWireFixtures.textResponseExpectedPromptTokens
                CachedPromptTokens = GeminiWireFixtures.textResponseExpectedCachedTokens
                OutputTokens = GeminiWireFixtures.textResponseExpectedOutputTokens
                CacheCreationTokens = None
            }
    }

    "gemini/functionCall→synthetic-id",
    unwrapGemini (GeminiAIProviderWire.parseResponse GeminiWireFixtures.functionCallResponseJson),
    {
        Content = ""
        ToolCalls = [
            {
                AIProviderToolCall.Id = GeminiWireFixtures.functionCallExpectedSyntheticId
                Name = GeminiWireFixtures.functionCallExpectedName
                Arguments = GeminiWireFixtures.functionCallExpectedArgs
            }
        ]
        StopReason = GeminiWireFixtures.functionCallExpectedStopReason
        Usage = None
    }
]