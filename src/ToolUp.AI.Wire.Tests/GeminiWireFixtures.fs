module ToolUp.AI.Wire.Tests.GeminiWireFixtures

open ToolUp.Platform.AI

/// Shared Gemini wire-mapping golden data, compiled into BOTH the .NET
/// Expecto pack (`GeminiWireTests.fs`) and the Fable smoke
/// (`ToolUp.AI.Wire.Fable.Tests`). Holding the canned strings + the
/// expected primitives in one source file means a green run on each host
/// proves the Gemini mapper (`GeminiAIProviderWire`) builds the identical
/// request bytes and parses the identical response shape on both hosts —
/// the Phase 255 parity property, seeded for Gemini (Phase 253).
///
/// Data only — no assertions and no calls into `GeminiAIProviderWire`, so
/// each host's test file drives the mapper with its own `Expect` API.

// ─── Request golden (minimal, hand-verifiable) ───────────────────
//
// One plain user message, no tools / system / schema. Exercises the
// role mapping + the `{ role, parts:[{ text }] }` content shape. The
// golden is the canonical compact serialization JObjects emit in
// build order.

let simpleRequestMessages = [ AIProviderMessage.text "user" "hi" ]

let simpleRequestGolden =
    """{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}"""

// A system prompt + a user message + one tool. Exercises
// `systemInstruction`, `tools.functionDeclarations`, `toolConfig`, and
// the member ordering (contents → systemInstruction → tools →
// toolConfig).

let toolRequestMessages = [ AIProviderMessage.text "user" "what time is it?" ]

let toolRequestSystemPrompt = Some "You are concise."

let toolRequestTools: AIProviderToolDef list = [
    {
        Name = "get_time"
        Description = "Get the current time."
        InputSchema = """{"type":"object","properties":{}}"""
    }
]

let toolRequestGolden =
    """{"contents":[{"role":"user","parts":[{"text":"what time is it?"}]}],"systemInstruction":{"parts":[{"text":"You are concise."}]},"tools":[{"functionDeclarations":[{"name":"get_time","description":"Get the current time.","parameters":{"type":"object","properties":{}}}]}],"toolConfig":{"functionCallingConfig":{"mode":"AUTO"}}}"""

// ─── Response parse goldens ───────────────────────────────────────

/// A plain text response with usage metadata.
let textResponseJson =
    """{"candidates":[{"content":{"parts":[{"text":"Hello there"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":10,"cachedContentTokenCount":4,"candidatesTokenCount":3}}"""

let textResponseExpectedContent = "Hello there"
let textResponseExpectedStopReason = "end_turn"
let textResponseExpectedPromptTokens = 10
let textResponseExpectedCachedTokens = 4
let textResponseExpectedOutputTokens = 3

/// A `functionCall` response — no `finishReason` text-stop should still
/// surface `tool_use` because a call part is present. `args` is an
/// object that the mapper re-serialises canonically.
let functionCallResponseJson =
    """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"get_time","args":{"tz":"utc"}}}]},"finishReason":"STOP"}]}"""

let functionCallExpectedName = "get_time"
let functionCallExpectedSyntheticId = "gemini-fc-get_time-0"
let functionCallExpectedArgs = """{"tz":"utc"}"""
let functionCallExpectedStopReason = "tool_use"

// ─── Name-keyed functionResponse round-trip ──────────────────────
//
// Gemini omits tool-call ids; the mapper synthesises a stable id on the
// way IN (parse) and strips it back to the tool NAME on the way OUT
// (request build of a tool-result message). The round-trip: feed the
// synthetic id from `functionCallExpectedSyntheticId` back as a
// `ToolResult`, build a request, and the emitted `functionResponse.name`
// must equal `functionCallExpectedName`.

let toolResultBody = """{"result":"ok"}"""

/// The message a caller assembles after executing the tool the model
/// requested — the `ToolCallId` is the synthetic id the parse produced.
let toolResultMessage: AIProviderMessage = {
    Role = "user"
    Content = ""
    ToolCalls = []
    ToolResults = [
        {
            ToolCallId = functionCallExpectedSyntheticId
            Content = toolResultBody
        }
    ]
    Parts = []
}

/// The full request a tool-result turn serialises to — `functionResponse`
/// keyed by the recovered tool NAME, with the result body preserved as a
/// structured object (not stringified).
let toolResultRequestGolden =
    """{"contents":[{"role":"user","parts":[{"functionResponse":{"name":"get_time","response":{"result":"ok"}}}]}]}"""