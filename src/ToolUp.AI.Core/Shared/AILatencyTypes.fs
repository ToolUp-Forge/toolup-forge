// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System

// ─── Phase 6i.A — per-turn AI latency record ─────────────────────
//
// One `AILatencyRecord` per agent-loop turn, written through
// `IEventStore` under `SourceModule = "_platform.ai.latency"` and
// `EventType = "AITurnLatency"`. The `/dev/ai-latency` handler
// reads these back via `IEventStore.ReadBySource` (Phase 9f
// `_by-source` index resolves in O(matches)) and rolls up
// p50 / p95 / p99 over a 60-minute window.
//
// **Token fields are `int option`.** Phase 6i.B populates these from
// `AIProviderResponse.Usage`; pre-6i.B records persist as `None` and
// rolling-window aggregates exclude them. `CacheCreationTokens` is
// Anthropic-specific (cache writes are billed at a premium over
// reads) — OpenAI returns `None`. The schema is forward-compatible:
// moving from `None` to a populated value does not require migrating
// historical events.

/// Where a tool executed in a turn. Mirrors `AIToolDefinition.Location`
/// (`ServerResident | ClientResident`); the server vs client split
/// matters for diagnosing latency because client-resident tools
/// add a SSE round trip the server can't directly observe.
type ToolExecutionLocation =
    | ServerSide
    | ClientSide

/// Per-tool-call timing record. Captured in the order tools were
/// dispatched within a turn.
type ToolCallTiming = {
    Name: string
    Location: ToolExecutionLocation
    DurationMs: float
    /// True when the tool returned an error string (matches the
    /// `isErrorToolResult` predicate in `AIAgentEngine`). Useful
    /// for "tools that fail are slow" analysis.
    Errored: bool
}

/// Per-turn AI latency record. Emitted once per agent-loop turn.
type AILatencyRecord = {
    TaskId: Guid
    ConversationId: Guid
    /// 1-based turn index within this agent-loop run.
    TurnNumber: int
    ProviderName: string
    ProviderModel: string
    /// Time from `provider.SendMessage` start to the first
    /// `MessageDelta` callback the provider emitted. `None` on
    /// turns where the model went straight to a tool call without
    /// narrating (no delta arrived) or when the provider doesn't
    /// stream.
    TtftMs: float option
    /// End-to-end turn duration: provider call + parallel tool
    /// execution + message bookkeeping.
    TurnDurationMs: float
    /// One entry per tool call in dispatch order. Empty list on
    /// turns where the model produced an `end_turn` response with
    /// no tool calls.
    ToolCalls: ToolCallTiming list
    /// Stop reason reported by the provider this turn. `"end_turn"`,
    /// `"tool_use"`, `"max_tokens"`, or `""` when the loop bailed
    /// before reading one.
    StopReason: string
    // ─── Token fields — populated by Phase 6i.B ──────────────────
    PromptTokens: int option
    CachedPromptTokens: int option
    OutputTokens: int option
    /// Anthropic-specific: tokens written to the prompt cache this
    /// turn (cache miss). `None` on providers without a cache-write
    /// signal (OpenAI), and on pre-6i.B records.
    CacheCreationTokens: int option
}

module AILatencyRecord =
    /// Reserved `IEventStore` source-module namespace for AI latency
    /// telemetry. `/dev/ai-latency` reads via `ReadBySource(scope, _)`.
    [<Literal>]
    let SourceModule = "_platform.ai.latency"

    /// Reserved `IEventStore` event-type for per-turn latency records.
    [<Literal>]
    let EventType = "AITurnLatency"