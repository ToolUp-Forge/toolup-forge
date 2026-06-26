module internal OpenAIProviderWire

open ToolUp.AI.Wire
open ToolUp.Platform.AI

// ─── Portable wire mapping (Wave 32, Phase 252) ──────────────────
//
// The OpenAI chat/completions request build + response parse, rewritten
// over the portable `JsonValue` model (`ToolUp.AI.Wire`) — no
// `System.Text.Json` here. The same source compiles to both the .NET
// server host and a Fable browser host: `JsonHost.serialize` is the
// canonical byte-stable writer shared by both, and `JsonHost.parse` is the
// host-bridged reader. The provider (`OpenAIProvider.fs`) injects the HTTP
// egress via `IHttpTransport` and runs this mapping inside it; this file
// stays 100% pure (GP 8 Fable-native, GP 12 host-portable). The change is
// behaviour-preserving (GP 11): identical fields, identical member order,
// identical null-omission to the prior STJ mapping.

// ─── Response parsing ────────────────────────────────────────────

/// Parse OpenAI's `usage` block into the provider-neutral `TokenUsage`
/// shape (Phase 6i.B). OpenAI's prompt caching is automatic above ~1024
/// tokens — no request-side opt-in needed; the cached portion is reported
/// at `usage.prompt_tokens_details.cached_tokens` (absent on responses
/// where no caching occurred).
///
/// `CacheCreationTokens` is always `None` here — OpenAI does not expose
/// a separate cache-write count; it bills cached vs uncached input
/// differently but doesn't surface the write boundary.
let parseUsage (root: JsonValue) : TokenUsage option =
    match JsonValue.tryField "usage" root with
    | Some usage ->
        let getInt (name: string) =
            usage
            |> JsonValue.tryField name
            |> Option.bind JsonValue.asInt
            |> Option.defaultValue 0

        let cachedTokens =
            usage
            |> JsonValue.tryField "prompt_tokens_details"
            |> Option.bind (JsonValue.tryField "cached_tokens")
            |> Option.bind JsonValue.asInt
            |> Option.defaultValue 0

        Some {
            PromptTokens = getInt "prompt_tokens"
            CachedPromptTokens = cachedTokens
            OutputTokens = getInt "completion_tokens"
            CacheCreationTokens = None
        }
    | None -> None

/// Map OpenAI's `finish_reason` to the provider-neutral stop vocabulary
/// the agent loop recognises (end_turn / tool_use / max_tokens).
let private toStopReason (finishReason: string) =
    match finishReason with
    | "tool_calls" -> "tool_use"
    | "length" -> "max_tokens"
    | "stop"
    | _ -> "end_turn"

/// Parse an OpenAI non-streaming `chat/completions` response.
/// Shape: `choices[0].message.{content, tool_calls[]}` + `choices[0].finish_reason`.
/// Throws on input that is not valid JSON — the provider wraps the call so
/// a malformed body surfaces as `MalformedResponse` (unchanged behaviour).
let parseResponse (json: string) : AIProviderResponse =
    let root =
        match JsonHost.parse json with
        | Some r -> r
        | None -> failwith "OpenAI chat/completions response was not valid JSON"

    let usage = parseUsage root

    let firstChoice =
        root |> JsonValue.tryField "choices" |> Option.bind (JsonValue.tryItem 0)

    match firstChoice with
    | None -> {
        Content = ""
        ToolCalls = []
        StopReason = "end_turn"
        Usage = usage
      }
    | Some choice ->
        let stopReason =
            choice
            |> JsonValue.tryField "finish_reason"
            |> Option.bind JsonValue.asString
            |> Option.defaultValue "stop"
            |> toStopReason

        let message = choice |> JsonValue.tryField "message"

        let textContent =
            message
            |> Option.bind (JsonValue.tryField "content")
            |> Option.bind JsonValue.asString
            |> Option.defaultValue ""

        let toolCalls =
            match
                message
                |> Option.bind (JsonValue.tryField "tool_calls")
                |> Option.bind JsonValue.asArray
            with
            | Some arr -> [
                for tc in arr ->
                    let id =
                        tc
                        |> JsonValue.tryField "id"
                        |> Option.bind JsonValue.asString
                        |> Option.defaultValue ""

                    let fn = tc |> JsonValue.tryField "function"

                    let name =
                        fn
                        |> Option.bind (JsonValue.tryField "name")
                        |> Option.bind JsonValue.asString
                        |> Option.defaultValue ""

                    let args =
                        fn
                        |> Option.bind (JsonValue.tryField "arguments")
                        |> Option.bind JsonValue.asString
                        |> Option.defaultValue "{}"

                    {
                        AIProviderToolCall.Id = id
                        Name = name
                        Arguments = args
                    }
              ]
            | None -> []

        {
            Content = textContent
            ToolCalls = toolCalls
            StopReason = stopReason
            Usage = usage
        }

// ─── Request building ────────────────────────────────────────────

// ─── Phase 6o vision support ─────────────────────────────────────
//
// OpenAI vision-capable models (Chat Completions API). GPT-4o /
// GPT-4-Turbo accept image_url content blocks; GPT-3.5 does not.
// As with the Claude side, this list is conservative — add new
// vision model ids here as they ship.
let isVisionCapable (model: string) =
    let m = model.ToLowerInvariant()

    m.Contains "gpt-4o"
    || m.Contains "gpt-4-turbo"
    || m.Contains "gpt-4-vision"
    || m.Contains "gpt-4.1"
    || m.Contains "o3"
    || m.Contains "o1"

/// Build OpenAI's content array for a multipart message. Text
/// parts become `{ type: "text", text }`; image parts use the
/// `image_url` shape — URL sources flow through verbatim; base64
/// sources become data URLs (`data:image/jpeg;base64,...`).
let private buildMultipartContent (parts: AIContentPart list) : JsonValue =
    parts
    |> List.map (fun part ->
        match part with
        | TextPart s -> jobj [ "type", jstr "text"; "text", jstr s ]
        | ImagePart payload ->
            let url =
                match payload.Source with
                | Base64Bytes bytes ->
                    let encoded = System.Convert.ToBase64String bytes
                    sprintf "data:%s;base64,%s" payload.MediaType encoded
                | Url u -> u

            jobj [ "type", jstr "image_url"; "image_url", jobj [ "url", jstr url ] ])
    |> jarr

/// Convert a neutral `AIProviderMessage` into OpenAI's chat-completions
/// message shape. OpenAI splits "assistant with tool_calls" and
/// "tool result for a previous call" into distinct role types, unlike
/// Anthropic which fits both into content blocks.
let private toOpenAIMessages (messages: AIProviderMessage list) : JsonValue list = [
    for m in messages do
        // Messages carrying tool results: emit one "tool" message per
        // result, referencing the tool_call_id.
        if not m.ToolResults.IsEmpty then
            for tr in m.ToolResults do
                yield
                    jobj [
                        "role", jstr "tool"
                        "tool_call_id", jstr tr.ToolCallId
                        "content", jstr tr.Content
                    ]
        elif not m.Parts.IsEmpty then
            // Phase 6o — multipart user message. Emit OpenAI's content
            // array shape. Tool-call envelopes don't apply here
            // (multimodal turns are user→assistant, never assistant→tool).
            yield jobj [ "role", jstr m.Role; "content", buildMultipartContent m.Parts ]
        elif not m.ToolCalls.IsEmpty then
            // Assistant turn that invoked tools. Content may be empty when
            // the model chose to call a tool without commentary — omit the
            // `content` member entirely in that case (the prior STJ mapping
            // emitted `null` content under `WhenWritingNull`, i.e. omitted).
            let toolCalls =
                m.ToolCalls
                |> List.map (fun tc ->
                    jobj [
                        "id", jstr tc.Id
                        "type", jstr "function"
                        "function", jobj [ "name", jstr tc.Name; "arguments", jstr tc.Arguments ]
                    ])
                |> jarr

            yield
                jobj (
                    [ "role", jstr m.Role ]
                    @ (if m.Content = "" then [] else [ "content", jstr m.Content ])
                    @ [ "tool_calls", toolCalls ]
                )
        else
            // Plain user / assistant / system turn.
            yield jobj [ "role", jstr m.Role; "content", jstr m.Content ]
]

let private buildTools (tools: AIProviderToolDef list) : JsonValue =
    tools
    |> List.map (fun t ->
        // The tool's JSON Schema embeds as a nested object. A malformed
        // schema is a caller defect — throw (the prior STJ mapping threw
        // from `JsonSerializer.Deserialize<JsonElement>` too).
        let parameters =
            match JsonHost.parse t.InputSchema with
            | Some v -> v
            | None -> failwith (sprintf "tool '%s' InputSchema is not valid JSON" t.Name)

        jobj [
            "type", jstr "function"
            "function",
            jobj [
                "name", jstr t.Name
                "description", jstr t.Description
                "parameters", parameters
            ]
        ])
    |> jarr

/// Build the chat-completions request body. `structuredOutputSchema`
/// (Phase 67b) flips on `response_format: { type: "json_schema",
/// strict: true }` when supplied; `None` runs the request in normal
/// free-text / tool-use mode. Mirrors Gemini's wire-layer
/// `structuredOutputSchema` parameter.
let buildRequestBody
    (model: string)
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (stream: bool)
    (structuredOutputSchema: JsonValue option)
    : string =
    let msgs = toOpenAIMessages messages

    // Prepend system message if provided. OpenAI takes system as a
    // role-tagged message, not a top-level field (unlike Anthropic).
    let allMessages =
        match systemPrompt with
        | Some prompt -> jobj [ "role", jstr "system"; "content", jstr prompt ] :: msgs
        | None -> msgs

    // Member order matches the prior `Dictionary<string,obj>` insertion
    // order exactly (model, messages, tools, tool_choice, stream,
    // stream_options, response_format) — load-bearing for byte-stability.
    let members =
        [ "model", jstr model; "messages", jarr allMessages ]
        @ (if List.isEmpty tools then
               []
           else
               // "auto" lets the model decide; matches Anthropic's default.
               [ "tools", buildTools tools; "tool_choice", jstr "auto" ])
        @ (if stream then
               // Request usage stats in the final stream chunk (Phase 6i.B).
               [ "stream", jbool true; "stream_options", jobj [ "include_usage", jbool true ] ]
           else
               [])
        @ (match structuredOutputSchema with
           | Some schema ->
               // Phase 67b — `strict: true` activates OpenAI's
               // constrained-decoding mode (gpt-4o-2024-08-06+ and
               // gpt-4o-mini); older models reject the request rather than
               // silently degrading. `name` is required by the wire format;
               // "structured_response" is a recipe-neutral identifier with
               // no semantic effect on the response.
               [
                   "response_format",
                   jobj [
                       "type", jstr "json_schema"
                       "json_schema",
                       jobj [ "name", jstr "structured_response"; "schema", schema; "strict", jbool true ]
                   ]
               ]
           | None -> [])

    JsonHost.serialize (jobj members)

// ─── Streaming accumulator ───────────────────────────────────────

/// Mutable state for a streaming response. OpenAI streams `choices[0].delta`
/// chunks that append to the assistant message; tool calls arrive incrementally
/// with an index and arguments-fragment-per-chunk.
///
/// Phase 6i.B: when `stream_options.include_usage = true` is requested
/// (always — see `buildRequestBody`), OpenAI emits a final pre-`[DONE]`
/// chunk with empty `choices: []` and a populated `usage` block at the
/// root. We capture that into `Usage` so the provider's response carries
/// token counts on the streaming path too.
type StreamState = {
    mutable Content: string
    mutable ToolCalls: AIProviderToolCall list
    mutable StopReason: string
    mutable Usage: TokenUsage option
}

/// Apply one SSE data payload to the accumulator — the pure `chunk → delta`
/// parser the provider's server-side read loop calls per line. Mirrors the
/// Claude streaming parser's control flow but for OpenAI's delta shape.
/// Never throws: a malformed chunk (or any unexpected shape) is swallowed
/// so one bad frame can't abort the stream.
let applyStreamChunk (state: StreamState) (onStream: (string -> unit) option) (data: string) =
    if data = "[DONE]" then
        ()
    else
        try
            match JsonHost.parse data with
            | None -> ()
            | Some root ->
                // Phase 6i.B: capture the final usage chunk (empty
                // `choices: []`, populated root `usage`). Last write wins.
                match parseUsage root with
                | Some u -> state.Usage <- Some u
                | None -> ()

                match root |> JsonValue.tryField "choices" |> Option.bind (JsonValue.tryItem 0) with
                | Some choice ->
                    match choice |> JsonValue.tryField "delta" with
                    | Some delta ->
                        // Text-content delta: append to Content and fire onStream.
                        match delta |> JsonValue.tryField "content" |> Option.bind JsonValue.asString with
                        | Some text when text <> "" ->
                            state.Content <- state.Content + text
                            onStream |> Option.iter (fun cb -> cb text)
                        | _ -> ()

                        // Tool-call delta: OpenAI sends `tool_calls[].index`
                        // + partial fields. First chunk for an index has
                        // `id` + `function.name`; subsequent chunks have
                        // `function.arguments` fragments.
                        match delta |> JsonValue.tryField "tool_calls" |> Option.bind JsonValue.asArray with
                        | Some tcs ->
                            for tc in tcs do
                                let index =
                                    tc
                                    |> JsonValue.tryField "index"
                                    |> Option.bind JsonValue.asInt
                                    |> Option.defaultValue 0

                                let id = tc |> JsonValue.tryField "id" |> Option.bind JsonValue.asString
                                let fn = tc |> JsonValue.tryField "function"

                                let name =
                                    fn |> Option.bind (JsonValue.tryField "name") |> Option.bind JsonValue.asString

                                let argsFragment =
                                    fn
                                    |> Option.bind (JsonValue.tryField "arguments")
                                    |> Option.bind JsonValue.asString
                                    |> Option.defaultValue ""

                                // Ensure list has entries up to `index`.
                                while state.ToolCalls.Length <= index do
                                    state.ToolCalls <-
                                        state.ToolCalls
                                        @ [
                                            {
                                                AIProviderToolCall.Id = ""
                                                Name = ""
                                                Arguments = ""
                                            }
                                        ]

                                let current = state.ToolCalls[index]

                                let updated = {
                                    Id =
                                        (match id with
                                         | Some v -> v
                                         | None -> current.Id)
                                    Name =
                                        (match name with
                                         | Some v -> v
                                         | None -> current.Name)
                                    Arguments = current.Arguments + argsFragment
                                }

                                state.ToolCalls <-
                                    state.ToolCalls |> List.mapi (fun i c -> if i = index then updated else c)
                        | None -> ()
                    | None -> ()

                    // Final chunk carries `finish_reason`.
                    match choice |> JsonValue.tryField "finish_reason" |> Option.bind JsonValue.asString with
                    | Some reason -> state.StopReason <- toStopReason reason
                    | None -> ()
                | None -> ()
        with _ ->
            ()