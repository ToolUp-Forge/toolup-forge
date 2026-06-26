module ToolUp.AI.Wire.Tests.OpenAIWireFixtures

open ToolUp.Platform.AI

// ─── OpenAI wire-mapping golden fixtures (Wave 32, Phase 252) ─────
//
// `(name, actual, golden)` request triples and `(name, parsed, expected)`
// response triples for the portable OpenAI mapper (`OpenAIProviderWire`).
// Both hosts compile this same file: the .NET Expecto pack
// (`ToolUp.AI.Wire.Tests`) and the Fable smoke
// (`ToolUp.AI.Wire.Fable.Tests`) each assert `actual = golden` over the
// SAME inputs, so a green run on each host proves cross-host byte-parity
// for the OpenAI request build and shape-parity for the response parse —
// seeding the Phase 255 conformance corpus.
//
// The goldens are hand-authored from the OpenAI chat/completions wire
// contract (field set + member order + null-omission), NOT copied from the
// mapper output — so they double as a behaviour-preserving regression gate
// against the prior System.Text.Json mapping.

// ─── Canonical inputs ────────────────────────────────────────────

let private userMsg = AIProviderMessage.text "user" "Say hi"

let private timeTool: AIProviderToolDef = {
    Name = "get_time"
    Description = "Get the time."
    InputSchema = """{"type":"object","properties":{}}"""
}

/// Assistant turn that called a tool with no text commentary (empty
/// Content — the `content` member must be omitted, matching the prior
/// `WhenWritingNull` STJ behaviour).
let private assistantToolCall: AIProviderMessage = {
    Role = "assistant"
    Content = ""
    ToolCalls = [
        {
            AIProviderToolCall.Id = "call_1"
            Name = "get_time"
            Arguments = "{}"
        }
    ]
    ToolResults = []
    Parts = []
}

/// Tool-result turn — emits a `role:"tool"` message per result.
let private toolResultMsg: AIProviderMessage = {
    Role = "tool"
    Content = "ignored-text"
    ToolCalls = []
    ToolResults = [
        {
            ToolCallId = "call_1"
            Content = "12:00"
        }
    ]
    Parts = []
}

let private structuredSchema =
    """{"type":"object","properties":{"greeting":{"type":"string"}},"required":["greeting"]}"""

let private imageUrlMessage =
    AIProviderMessage.multipart "user" [
        TextPart "Look:"
        ImagePart {
            MediaType = "image/png"
            Source = Url "https://example.com/y.png"
        }
    ]

/// Image-only multipart turn from raw bytes — exercises the portable
/// base64 data-URL encoding (`Convert.ToBase64String [|1;2;3|] = "AQID"`),
/// which must produce identical bytes under Fable.
let private imageBytesMessage =
    AIProviderMessage.multipart "user" [
        ImagePart {
            MediaType = "image/png"
            Source = Base64Bytes [| 1uy; 2uy; 3uy |]
        }
    ]

// ─── Request fixtures ────────────────────────────────────────────

/// `(name, buildRequestBody …, golden compact JSON)`.
let requestFixtures: (string * string * string) list = [
    "plain-system+user",
    OpenAIProviderWire.buildRequestBody "gpt-4o" [ userMsg ] [] (Some "You are helpful.") false None,
    """{"model":"gpt-4o","messages":[{"role":"system","content":"You are helpful."},{"role":"user","content":"Say hi"}]}"""

    "tools+stream",
    OpenAIProviderWire.buildRequestBody "gpt-4o" [ AIProviderMessage.text "user" "Hello" ] [ timeTool ] None true None,
    """{"model":"gpt-4o","messages":[{"role":"user","content":"Hello"}],"tools":[{"type":"function","function":{"name":"get_time","description":"Get the time.","parameters":{"type":"object","properties":{}}}}],"tool_choice":"auto","stream":true,"stream_options":{"include_usage":true}}"""

    "assistant-toolcall+toolresult",
    OpenAIProviderWire.buildRequestBody "gpt-4o" [ assistantToolCall; toolResultMsg ] [] None false None,
    """{"model":"gpt-4o","messages":[{"role":"assistant","tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_time","arguments":"{}"}}]},{"role":"tool","tool_call_id":"call_1","content":"12:00"}]}"""

    "structured-output",
    OpenAIProviderWire.buildRequestBody
        "gpt-4o"
        [ AIProviderMessage.text "user" "Give JSON" ]
        []
        None
        false
        (Some(
            match ToolUp.AI.Wire.JsonHost.parse structuredSchema with
            | Some v -> v
            | None -> failwith "fixture schema must parse"
        )),
    """{"model":"gpt-4o","messages":[{"role":"user","content":"Give JSON"}],"response_format":{"type":"json_schema","json_schema":{"name":"structured_response","schema":{"type":"object","properties":{"greeting":{"type":"string"}},"required":["greeting"]},"strict":true}}}"""

    "multipart-image-url",
    OpenAIProviderWire.buildRequestBody "gpt-4o" [ imageUrlMessage ] [] None false None,
    """{"model":"gpt-4o","messages":[{"role":"user","content":[{"type":"text","text":"Look:"},{"type":"image_url","image_url":{"url":"https://example.com/y.png"}}]}]}"""

    "multipart-image-base64",
    OpenAIProviderWire.buildRequestBody "gpt-4o" [ imageBytesMessage ] [] None false None,
    """{"model":"gpt-4o","messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]}]}"""
]

// ─── Response fixtures ───────────────────────────────────────────

/// `(name, parseResponse json, expected)`. Both hosts parse the same
/// canned body and must reify the identical `AIProviderResponse`.
let responseFixtures: (string * AIProviderResponse * AIProviderResponse) list = [
    "text+usage",
    OpenAIProviderWire.parseResponse
        """{"choices":[{"message":{"content":"Hi there"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4}}}""",
    {
        Content = "Hi there"
        ToolCalls = []
        StopReason = "end_turn"
        Usage =
            Some {
                PromptTokens = 10
                CachedPromptTokens = 4
                OutputTokens = 5
                CacheCreationTokens = None
            }
    }

    "tool_calls",
    OpenAIProviderWire.parseResponse
        """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"call_9","function":{"name":"get_time","arguments":"{\"tz\":\"UTC\"}"}}]},"finish_reason":"tool_calls"}]}""",
    {
        Content = ""
        ToolCalls = [
            {
                AIProviderToolCall.Id = "call_9"
                Name = "get_time"
                Arguments = """{"tz":"UTC"}"""
            }
        ]
        StopReason = "tool_use"
        Usage = None
    }

    "length-truncation",
    OpenAIProviderWire.parseResponse """{"choices":[{"message":{"content":"partial"},"finish_reason":"length"}]}""",
    {
        Content = "partial"
        ToolCalls = []
        StopReason = "max_tokens"
        Usage = None
    }

    "empty-choices",
    OpenAIProviderWire.parseResponse """{"choices":[]}""",
    {
        Content = ""
        ToolCalls = []
        StopReason = "end_turn"
        Usage = None
    }
]