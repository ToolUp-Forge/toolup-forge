// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Tests.StreamTranscripts

// Recorded SSE transcripts, line for line as each vendor sends them (keep-alive
// comments, a malformed frame, the `[DONE]` sentinel included), replayed
// through the stream folds in `StreamingFoldTests`.

/// OpenAI chat/completions: prose, two tool calls streamed as fragments
/// across interleaved indexes, the finish reason, then the usage chunk.
let openAI = [
    ": keep-alive"
    """data: {"choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}"""
    """data: {"choices":[{"index":0,"delta":{"content":"Checking"}}]}"""
    """data: {"choices":[{"index":0,"delta":{"content":" the time."}}]}"""
    ""
    """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"get_time","arguments":""}}]}}]}"""
    """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"get_zone","arguments":"{\"city\":"}}]}}]}"""
    """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"tz\":\"Europe/Paris\"}"}}]}}]}"""
    """data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"\"Paris\"}"}}]}}]}"""
    "data: {not json"
    """data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}"""
    """data: {"choices":[],"usage":{"prompt_tokens":40,"completion_tokens":12,"prompt_tokens_details":{"cached_tokens":8}}}"""
    "data: [DONE]"
]

/// OpenAI: a reply cut off at its length cap.
let openAITruncated = [
    """data: {"choices":[{"index":0,"delta":{"content":"Once upon"}}]}"""
    """data: {"choices":[{"index":0,"delta":{"content":" a time"},"finish_reason":"length"}]}"""
    "data: [DONE]"
]

/// Gemini streamGenerateContent: text chunks (one with two parts), a whole
/// function call, a blank data line, then the finish reason and usage.
let gemini = [
    """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Checking"}]}}]}"""
    ""
    """data: {"candidates":[{"content":{"role":"model","parts":[{"text":" the"},{"text":" time."}]}}]}"""
    "data: "
    """data: {"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"get_time","args":{"tz":"Europe/Paris"}}}]}}]}"""
    "data: {not json"
    """data: {"candidates":[{"content":{"role":"model","parts":[{"text":""}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":30,"cachedContentTokenCount":4,"candidatesTokenCount":9}}"""
]

/// Gemini: a reply cut off at its length cap.
let geminiTruncated = [
    """data: {"candidates":[{"content":{"role":"model","parts":[{"text":"Once upon"}]}}]}"""
    """data: {"candidates":[{"content":{"role":"model","parts":[{"text":" a time"}]},"finishReason":"MAX_TOKENS"}]}"""
]