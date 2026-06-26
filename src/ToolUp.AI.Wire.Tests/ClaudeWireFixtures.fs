// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Tests.ClaudeWireFixtures

open ToolUp.AI.Wire
open ToolUp.Platform.AI

/// Shared Claude wire fixtures, compiled into BOTH the .NET Expecto pack
/// and the Fable smoke (alongside `ClaudeAIProvider.Wire.fs` source). The
/// portable mapper + the pure `ClaudeStreaming` machine are exercised over
/// the SAME canned inputs on each host, so a green run on both proves the
/// request bytes are byte-identical across hosts and the streaming assembly
/// is host-independent — seeding the Phase 255 conformance corpus.

// ─── Non-streaming request build (golden bytes) ──────────────────
//
// `(name, builtRequest, goldenJson)` triples. The builder runs the same
// `JsonValue` mapping on each host; the golden literal is the canonical
// serialization both must emit. Kept small + hand-verifiable so the
// golden bytes are trustworthy.

let private msgUser text = AIProviderMessage.text "user" text

/// Minimal request — model + max_tokens + one plain-text user message; no
/// system, tools, or stream. Exercises the base object + plain string
/// content path (single turn ⇒ no cache marker).
let requestMinimal () =
    ClaudeAIProviderWire.buildRequestBody "m" 16 [ msgUser "hi" ] [] None false None

let requestMinimalGolden =
    """{"model":"m","max_tokens":16,"messages":[{"role":"user","content":"hi"}]}"""

/// System-prompt request — the system block is array form carrying a
/// `cache_control` marker (caches the static system prefix).
let requestSystem () =
    ClaudeAIProviderWire.buildRequestBody "claude-x" 100 [ msgUser "hi" ] [] (Some "sys") false None

let requestSystemGolden =
    """{"model":"claude-x","max_tokens":100,"messages":[{"role":"user","content":"hi"}],"system":[{"type":"text","text":"sys","cache_control":{"type":"ephemeral"}}]}"""

/// Tool + streaming request — the tools array carries a `cache_control`
/// marker on its last entry, the `input_schema` is embedded as JSON
/// structure, and `stream:true` is appended.
let requestToolStream () =
    ClaudeAIProviderWire.buildRequestBody
        "m"
        50
        [ msgUser "q" ]
        [
            {
                Name = "t"
                Description = "d"
                InputSchema = """{"type":"object"}"""
            }
        ]
        None
        true
        None

let requestToolStreamGolden =
    """{"model":"m","max_tokens":50,"messages":[{"role":"user","content":"q"}],"tools":[{"name":"t","description":"d","input_schema":{"type":"object"},"cache_control":{"type":"ephemeral"}}],"stream":true}"""

/// Two-turn request — the second-to-last message (index 0) is promoted to
/// array form to carry the `cache_control` marker; the last stays a plain
/// string. Exercises the markForCache simple-text promotion.
let requestTwoTurn () =
    ClaudeAIProviderWire.buildRequestBody
        "m"
        8
        [ AIProviderMessage.text "user" "a"; AIProviderMessage.text "assistant" "b" ]
        []
        None
        false
        None

let requestTwoTurnGolden =
    """{"model":"m","max_tokens":8,"messages":[{"role":"user","content":[{"type":"text","text":"a","cache_control":{"type":"ephemeral"}}]},{"role":"assistant","content":"b"}]}"""

/// Every request fixture as `(name, thunk, golden)` so a host iterates them.
let requestFixtures: (string * (unit -> string) * string) list = [
    "minimal", requestMinimal, requestMinimalGolden
    "system", requestSystem, requestSystemGolden
    "tool+stream", requestToolStream, requestToolStreamGolden
    "two-turn", requestTwoTurn, requestTwoTurnGolden
]

// ─── Non-streaming response parse ────────────────────────────────

/// A canned non-streaming response: one text block + one tool_use block,
/// `stop_reason: tool_use`, full usage incl. cache tokens.
let responseJson =
    """{"id":"msg_1","type":"message","role":"assistant","stop_reason":"tool_use","content":[{"type":"text","text":"Hello"},{"type":"tool_use","id":"toolu_1","name":"get_time","input":{"tz":"UTC"}}],"usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":2,"cache_creation_input_tokens":3}}"""

let parsedResponse () =
    ClaudeAIProviderWire.parseResponse responseJson

// ─── Streaming assembly ──────────────────────────────────────────

/// Fold a canned `data:` payload sequence through the pure processor.
/// Returns the finalized response and the concatenation of the text deltas
/// the processor surfaced for the live `onStream` callback.
let foldStream (chunks: string list) : AIProviderResponse * string =
    let mutable state = ClaudeStreaming.initial
    let mutable emitted = ""

    for chunk in chunks do
        let next, delta = ClaudeStreaming.processData state chunk
        state <- next

        match delta with
        | Some t -> emitted <- emitted + t
        | None -> ()

    ClaudeStreaming.finalize state, emitted

/// Full streaming round: message_start (usage) → a text block (two
/// text_deltas) → a tool_use block (two input_json_delta fragments) →
/// message_delta (stop_reason + cumulative output_tokens). Exercises text
/// accumulation, out-of-band tool-argument assembly, and split usage.
let streamingChunks: string list = [
    """{"type":"message_start","message":{"usage":{"input_tokens":12,"cache_read_input_tokens":4,"cache_creation_input_tokens":6,"output_tokens":1}}}"""
    """{"type":"content_block_start","index":0,"content_block":{"type":"text"}}"""
    """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}"""
    """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" world"}}"""
    """{"type":"content_block_stop","index":0}"""
    """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_9","name":"lookup"}}"""
    """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}"""
    """{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Paris\"}"}}"""
    """{"type":"content_block_stop","index":1}"""
    """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}"""
    """{"type":"message_stop"}"""
]

/// Expected assembled tool-call Arguments from the two `input_json_delta`
/// fragments above.
let streamingExpectedArguments = """{"city":"Paris"}"""

/// A zero-input tool call: a `tool_use` block opens and closes with NO
/// `input_json_delta` chunks. `finalize` must default the empty Arguments
/// buffer to "{}" (the post-stream default-fill), and no usage event means
/// `Usage = None`.
let streamingZeroArgChunks: string list = [
    """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_x","name":"ping"}}"""
    """{"type":"content_block_stop","index":0}"""
    """{"type":"message_delta","delta":{"stop_reason":"tool_use"}}"""
]