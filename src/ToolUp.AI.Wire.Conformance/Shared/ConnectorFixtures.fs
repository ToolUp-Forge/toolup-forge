// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.ConnectorFixtures

// The request bytes every shipped connector sends for the four call shapes:
// a buffered call, a streaming call, a structured call and a vision call.
// One set of canonical inputs; one golden per connector and shape. Two packs
// assert against the same goldens:
//
//   * this corpus builds each body through the vendor's portable wire
//     mapper, on both hosts (`Corpus.connectorRequestFixtures`);
//   * `ToolUp.AIProviders.Tests` drives each connector end to end over a
//     capturing HTTP client and compares the body it SENT, so a change to how
//     a connector assembles its call cannot move a byte unseen.
//
// Microsoft 365 Copilot's mapper reads System.Text.Json, so its goldens are
// asserted by the .NET pack only.

open ToolUp.Platform.AI

let systemPrompt = Some "You are terse."

let messages = [ AIProviderMessage.text "user" "What time is it in Paris?" ]

let tools: AIProviderToolDef list = [
    {
        Name = "get_time"
        Description = "Get the time in a time zone."
        InputSchema = """{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}"""
    }
]

let schema =
    """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}"""

let visionMessages = [
    AIProviderMessage.multipart "user" [
        TextPart "What is in this picture?"
        ImagePart {
            MediaType = "image/png"
            Source = Base64Bytes [| 1uy; 2uy; 3uy |]
        }
    ]
]

let claudeModel = "claude-haiku-4-5-20251001"
let claudeMaxTokens = 4096
let openAIModel = "gpt-4o"
let geminiModel = "models/gemini-2.5-flash"
let copilotModel = "gpt-4o"

/// The four call shapes.
let shapes = [ "buffered"; "streaming"; "structured"; "vision" ]

let claudeBuffered =
    """{"model":"claude-haiku-4-5-20251001","max_tokens":4096,"messages":[{"role":"user","content":"What time is it in Paris?"}],"system":[{"type":"text","text":"You are terse.","cache_control":{"type":"ephemeral"}}],"tools":[{"name":"get_time","description":"Get the time in a time zone.","input_schema":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]},"cache_control":{"type":"ephemeral"}}]}"""

let claudeStreaming =
    """{"model":"claude-haiku-4-5-20251001","max_tokens":4096,"messages":[{"role":"user","content":"What time is it in Paris?"}],"system":[{"type":"text","text":"You are terse.","cache_control":{"type":"ephemeral"}}],"tools":[{"name":"get_time","description":"Get the time in a time zone.","input_schema":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]},"cache_control":{"type":"ephemeral"}}],"stream":true}"""

let claudeStructured =
    """{"model":"claude-haiku-4-5-20251001","max_tokens":4096,"messages":[{"role":"user","content":"What time is it in Paris?"}],"system":[{"type":"text","text":"You are terse.","cache_control":{"type":"ephemeral"}}],"tools":[{"name":"get_time","description":"Get the time in a time zone.","input_schema":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}},{"name":"structured_response","description":"Return the structured response. The tool input IS the response object itself and MUST conform to the tool's input schema — top-level fields are the schema's top-level properties. Do NOT wrap the response in any envelope (no outer `input` key). Field values must be JSON structure (objects, arrays, numbers, booleans), never serialised JSON strings.","input_schema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]},"cache_control":{"type":"ephemeral"}}],"tool_choice":{"type":"tool","name":"structured_response","disable_parallel_tool_use":true}}"""

let claudeVision =
    """{"model":"claude-haiku-4-5-20251001","max_tokens":4096,"messages":[{"role":"user","content":[{"type":"text","text":"What is in this picture?"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AQID"}}]}],"system":[{"type":"text","text":"You are terse.","cache_control":{"type":"ephemeral"}}]}"""

let openAIBuffered =
    """{"model":"gpt-4o","messages":[{"role":"system","content":"You are terse."},{"role":"user","content":"What time is it in Paris?"}],"tools":[{"type":"function","function":{"name":"get_time","description":"Get the time in a time zone.","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}}}],"tool_choice":"auto"}"""

let openAIStreaming =
    """{"model":"gpt-4o","messages":[{"role":"system","content":"You are terse."},{"role":"user","content":"What time is it in Paris?"}],"tools":[{"type":"function","function":{"name":"get_time","description":"Get the time in a time zone.","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}}}],"tool_choice":"auto","stream":true,"stream_options":{"include_usage":true}}"""

let openAIStructured =
    """{"model":"gpt-4o","messages":[{"role":"system","content":"You are terse."},{"role":"user","content":"What time is it in Paris?"}],"tools":[{"type":"function","function":{"name":"get_time","description":"Get the time in a time zone.","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}}}],"tool_choice":"auto","response_format":{"type":"json_schema","json_schema":{"name":"structured_response","schema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]},"strict":true}}}"""

let openAIVision =
    """{"model":"gpt-4o","messages":[{"role":"system","content":"You are terse."},{"role":"user","content":[{"type":"text","text":"What is in this picture?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}]}"""

let geminiBuffered =
    """{"contents":[{"role":"user","parts":[{"text":"What time is it in Paris?"}]}],"systemInstruction":{"parts":[{"text":"You are terse."}]},"tools":[{"functionDeclarations":[{"name":"get_time","description":"Get the time in a time zone.","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}}]}],"toolConfig":{"functionCallingConfig":{"mode":"AUTO"}}}"""

let geminiStructured =
    """{"contents":[{"role":"user","parts":[{"text":"What time is it in Paris?"}]}],"systemInstruction":{"parts":[{"text":"You are terse."}]},"tools":[{"functionDeclarations":[{"name":"get_time","description":"Get the time in a time zone.","parameters":{"type":"object","properties":{"tz":{"type":"string"}},"required":["tz"]}}]}],"toolConfig":{"functionCallingConfig":{"mode":"AUTO"}},"generationConfig":{"responseMimeType":"application/json","responseSchema":{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}}}"""

let geminiVision =
    """{"contents":[{"role":"user","parts":[{"text":"What is in this picture?"},{"inlineData":{"mimeType":"image/png","data":"AQID"}}]}],"systemInstruction":{"parts":[{"text":"You are terse."}]}}"""

/// Gemini streams the buffered body to its `streamGenerateContent` route.
let geminiStreaming = geminiBuffered

/// Azure OpenAI is driven over OpenAI's wire mapper: for the same deployment name the
/// bytes are OpenAI's.
let copilotBuffered = openAIBuffered
let copilotStreaming = openAIStreaming
let copilotStructured = openAIStructured
let copilotVision = openAIVision

/// Microsoft 365 Copilot: the chat body (both routes post it), and the
/// structured call, which runs as a chat call carrying the schema instruction.
let m365Chat =
    """{"message":{"text":"What time is it in Paris?"},"additionalContext":[{"text":"Instructions for this conversation:\nYou are terse."}],"locationHint":{"timeZone":"UTC"}}"""

let m365Structured =
    """{"message":{"text":"What time is it in Paris?"},"additionalContext":[{"text":"Instructions for this conversation:\nYou are terse.\n\nYou MUST respond with a single JSON document and nothing else (no prose, no Markdown code fences). The JSON document MUST conform to this JSON Schema:\n\n{\u0022type\u0022:\u0022object\u0022,\u0022properties\u0022:{\u0022answer\u0022:{\u0022type\u0022:\u0022string\u0022}},\u0022required\u0022:[\u0022answer\u0022]}"}],"locationHint":{"timeZone":"UTC"}}"""