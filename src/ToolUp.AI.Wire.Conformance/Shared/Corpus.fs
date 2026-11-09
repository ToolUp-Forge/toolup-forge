// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.Corpus

open ToolUp.Platform.AI
open ToolUp.AI.Wire.Tests
open ToolUp.Remoting.Json

// ─── The consolidated conformance corpus ────────
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

/// Every provider's request body for a history whose
/// assistant turn holds block parts (`BlockPartFixtures.providerHistory`).
/// A block reaches the model as its `redactedSummary` text block; the node
/// is in no golden, and `ConformanceSuite` also asserts its marker absent
/// from every built body.
let blockRequestFixtures: (string * string * string) list = [
    "openai/block-part",
    OpenAIProviderWire.buildRequestBody "gpt-4o" BlockPartFixtures.providerHistory [] None false None,
    """{"model":"gpt-4o","messages":[{"role":"user","content":"Show me revenue by site."},{"role":"assistant","content":[{"type":"text","text":"Revenue by site for the last delivery."},{"type":"text","text":"[block: table, table=daily-trade, metric=revenue, period=2026-10-26, facts=3, withheld=2]"},{"type":"text","text":"[block: trend, table=daily-trade, metric=revenue, period=2026-09-29..2026-10-26, facts=2, withheld=0]"}]}]}"""

    "gemini/block-part",
    GeminiAIProviderWire.buildRequestBody BlockPartFixtures.providerHistory [] None None,
    """{"contents":[{"role":"user","parts":[{"text":"Show me revenue by site."}]},{"role":"model","parts":[{"text":"Revenue by site for the last delivery."},{"text":"[block: table, table=daily-trade, metric=revenue, period=2026-10-26, facts=3, withheld=2]"},{"text":"[block: trend, table=daily-trade, metric=revenue, period=2026-09-29..2026-10-26, facts=2, withheld=0]"}]}]}"""

    "claude/block-part",
    ClaudeAIProviderWire.buildRequestBody "claude-sonnet-4-5" 1024 BlockPartFixtures.providerHistory [] None false None,
    """{"model":"claude-sonnet-4-5","max_tokens":1024,"messages":[{"role":"user","content":[{"type":"text","text":"Show me revenue by site.","cache_control":{"type":"ephemeral"}}]},{"role":"assistant","content":[{"type":"text","text":"Revenue by site for the last delivery."},{"type":"text","text":"[block: table, table=daily-trade, metric=revenue, period=2026-10-26, facts=3, withheld=2]"},{"type":"text","text":"[block: trend, table=daily-trade, metric=revenue, period=2026-09-29..2026-10-26, facts=2, withheld=0]"}]}]}"""
]

/// Every connector's four call shapes, built through its wire mapper with the
/// arguments the connector passes. `ToolUp.AIProviders.Tests` holds each
/// connector's SENT bytes to the same goldens.
let connectorRequestFixtures: (string * string * string) list =
    let f = ConnectorFixtures.systemPrompt
    let msgs = ConnectorFixtures.messages
    let tools = ConnectorFixtures.tools
    let vision = ConnectorFixtures.visionMessages
    let schemaValue = ToolUp.AI.Wire.JsonHost.parse ConnectorFixtures.schema

    let claude messages tools stream schema =
        ClaudeAIProviderWire.buildRequestBodyWithEffort
            None
            ConnectorFixtures.claudeModel
            ConnectorFixtures.claudeMaxTokens
            messages
            tools
            f
            stream
            schema

    let openAI model messages tools stream schema =
        OpenAIProviderWire.buildRequestBody model messages tools f stream schema

    let gemini messages tools schema =
        GeminiAIProviderWire.buildRequestBody messages tools f schema

    let openAIShapes name model = [
        name + "/buffered", openAI model msgs tools false None
        name + "/streaming", openAI model msgs tools true None
        name + "/structured", openAI model msgs tools false schemaValue
        name + "/vision", openAI model vision [] false None
    ]

    [
        "connector/claude/buffered", claude msgs tools false None, ConnectorFixtures.claudeBuffered
        "connector/claude/streaming", claude msgs tools true None, ConnectorFixtures.claudeStreaming
        "connector/claude/structured",
        claude msgs tools false (Some ConnectorFixtures.schema),
        ConnectorFixtures.claudeStructured
        "connector/claude/vision", claude vision [] false None, ConnectorFixtures.claudeVision
        "connector/gemini/buffered", gemini msgs tools None, ConnectorFixtures.geminiBuffered
        "connector/gemini/streaming", gemini msgs tools None, ConnectorFixtures.geminiStreaming
        "connector/gemini/structured", gemini msgs tools schemaValue, ConnectorFixtures.geminiStructured
        "connector/gemini/vision", gemini vision [] None, ConnectorFixtures.geminiVision
    ]
    @ List.map2
        (fun (name, built) golden -> name, built, golden)
        (openAIShapes "connector/openai" ConnectorFixtures.openAIModel
         @ openAIShapes "connector/copilot" ConnectorFixtures.copilotModel)
        [
            ConnectorFixtures.openAIBuffered
            ConnectorFixtures.openAIStreaming
            ConnectorFixtures.openAIStructured
            ConnectorFixtures.openAIVision
            ConnectorFixtures.copilotBuffered
            ConnectorFixtures.copilotStreaming
            ConnectorFixtures.copilotStructured
            ConnectorFixtures.copilotVision
        ]

/// The full cross-provider request corpus, `(name, builtBytes, golden)`.
let requestFixtures: (string * string * string) list =
    openAIRequests
    @ geminiRequests
    @ claudeRequests
    @ blockRequestFixtures
    @ connectorRequestFixtures

// ─── Response structural-parity corpus ───────────────────────────────

/// OpenAI ships `(name, parsed, expected)` response triples directly.
let openAIResponseFixtures: (string * AIProviderResponse * AIProviderResponse) list =
    OpenAIWireFixtures.responseFixtures
    |> List.map (fun (name, parsed, expected) -> "openai/" + name, parsed, expected)

/// Gemini `parseResponse` returns `Result`; a parse `Error` surfaces as a
/// sentinel so the harness fails loudly.
let private unwrapGemini = WireFixtures.parsedOrSentinel

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