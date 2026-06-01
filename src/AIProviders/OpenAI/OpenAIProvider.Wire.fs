module internal OpenAIProviderWire

open System.Text.Json
open ToolUp.Platform.AI

// ─── JSON helpers ────────────────────────────────────────────────

let private jsonOptions =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.DefaultIgnoreCondition <- Serialization.JsonIgnoreCondition.WhenWritingNull
    opts

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
let parseUsage (root: JsonElement) : TokenUsage option =
    match root.TryGetProperty("usage") with
    | true, usage ->
        let getInt (name: string) =
            match usage.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0

        let cachedTokens =
            match usage.TryGetProperty("prompt_tokens_details") with
            | true, details ->
                match details.TryGetProperty("cached_tokens") with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                | _ -> 0
            | _ -> 0

        Some {
            PromptTokens = getInt "prompt_tokens"
            CachedPromptTokens = cachedTokens
            OutputTokens = getInt "completion_tokens"
            CacheCreationTokens = None
        }
    | _ -> None

/// Parse an OpenAI non-streaming `chat/completions` response.
/// Shape: `choices[0].message.{content, tool_calls[]}` + `choices[0].finish_reason`.
let parseResponse (json: string) : AIProviderResponse =
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let usage = parseUsage root

    let firstChoice =
        match root.TryGetProperty("choices") with
        | true, arr when arr.ValueKind = JsonValueKind.Array && arr.GetArrayLength() > 0 -> Some(arr[0])
        | _ -> None

    match firstChoice with
    | None -> {
        Content = ""
        ToolCalls = []
        StopReason = "end_turn"
        Usage = usage
      }
    | Some choice ->
        let finishReason =
            match choice.TryGetProperty("finish_reason") with
            | true, fr when fr.ValueKind = JsonValueKind.String -> fr.GetString()
            | _ -> "stop"

        // Map OpenAI's finish_reason to the provider-neutral vocabulary
        // the agent loop recognises (end_turn / tool_use / max_tokens).
        let stopReason =
            match finishReason with
            | "tool_calls" -> "tool_use"
            | "length" -> "max_tokens"
            | "stop"
            | _ -> "end_turn"

        let textContent =
            match choice.TryGetProperty("message") with
            | true, msg ->
                match msg.TryGetProperty("content") with
                | true, c when c.ValueKind = JsonValueKind.String -> c.GetString()
                | _ -> ""
            | _ -> ""

        let toolCalls =
            match choice.TryGetProperty("message") with
            | true, msg ->
                match msg.TryGetProperty("tool_calls") with
                | true, arr when arr.ValueKind = JsonValueKind.Array -> [
                    for tc in arr.EnumerateArray() ->
                        let id =
                            match tc.TryGetProperty("id") with
                            | true, v -> v.GetString()
                            | _ -> ""

                        let name, args =
                            match tc.TryGetProperty("function") with
                            | true, fn ->
                                let n =
                                    match fn.TryGetProperty("name") with
                                    | true, v -> v.GetString()
                                    | _ -> ""

                                let a =
                                    match fn.TryGetProperty("arguments") with
                                    | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                    | _ -> "{}"

                                n, a
                            | _ -> "", "{}"

                        {
                            AIProviderToolCall.Id = id
                            Name = name
                            Arguments = args
                        }
                  ]
                | _ -> []
            | _ -> []

        {
            Content = textContent
            ToolCalls = toolCalls
            StopReason = stopReason
            Usage = usage
        }

// ─── Request building ────────────────────────────────────────────

/// Convert a neutral `AIProviderMessage` into OpenAI's chat-completions
/// message shape. OpenAI splits "assistant with tool_calls" and
/// "tool result for a previous call" into distinct role types, unlike
/// Anthropic which fits both into content blocks.
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
let private buildMultipartContent (parts: AIContentPart list) : obj list =
    parts
    |> List.map (fun part ->
        match part with
        | TextPart s -> box {| ``type`` = "text"; text = s |}
        | ImagePart payload ->
            let url =
                match payload.Source with
                | Base64Bytes bytes ->
                    let encoded = System.Convert.ToBase64String bytes
                    sprintf "data:%s;base64,%s" payload.MediaType encoded
                | Url u -> u

            box {|
                ``type`` = "image_url"
                image_url = {| url = url |}
            |})

let private toOpenAIMessages (messages: AIProviderMessage list) = [
    for m in messages do
        // Messages carrying tool results: emit one "tool" message per
        // result, referencing the tool_call_id.
        if not m.ToolResults.IsEmpty then
            for tr in m.ToolResults do
                yield
                    box {|
                        role = "tool"
                        tool_call_id = tr.ToolCallId
                        content = tr.Content
                    |}
        elif not m.Parts.IsEmpty then
            // Phase 6o — multipart user message. Emit OpenAI's
            // content array shape. Tool-call envelopes don't apply
            // here (multimodal turns are user→assistant, never
            // assistant→tool).
            yield
                box {|
                    role = m.Role
                    content = buildMultipartContent m.Parts
                |}
        elif not m.ToolCalls.IsEmpty then
            // Assistant turn that invoked tools. Content may be empty
            // when the model chose to call a tool without
            // commentary.
            let toolCalls =
                m.ToolCalls
                |> List.map (fun tc -> {|
                    id = tc.Id
                    ``type`` = "function"
                    ``function`` = {|
                        name = tc.Name
                        arguments = tc.Arguments
                    |}
                |})

            yield
                box {|
                    role = m.Role
                    content = (if m.Content = "" then null else m.Content)
                    tool_calls = toolCalls
                |}
        else
            // Plain user / assistant / system turn.
            yield box {| role = m.Role; content = m.Content |}
]

let private buildTools (tools: AIProviderToolDef list) =
    tools
    |> List.map (fun t -> {|
        ``type`` = "function"
        ``function`` = {|
            name = t.Name
            description = t.Description
            parameters = JsonSerializer.Deserialize<JsonElement>(t.InputSchema)
        |}
    |})

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
    (structuredOutputSchema: JsonElement option)
    =
    let msgs = toOpenAIMessages messages

    // Prepend system message if provided. OpenAI takes system as a
    // role-tagged message, not a top-level field (unlike Anthropic).
    let allMessages =
        match systemPrompt with
        | Some prompt -> box {| role = "system"; content = prompt |} :: msgs
        | None -> msgs

    let dict = System.Collections.Generic.Dictionary<string, obj>()
    dict["model"] <- model
    dict["messages"] <- allMessages

    let toolDefs = buildTools tools

    if not toolDefs.IsEmpty then
        dict["tools"] <- toolDefs
        // "auto" lets the model decide; matches Anthropic's default behaviour.
        dict["tool_choice"] <- "auto"

    if stream then
        dict["stream"] <- true
        // Request usage stats in the final stream chunk for future token accounting.
        dict["stream_options"] <- {| include_usage = true |}

    // Phase 67b — structured output. `strict: true` activates OpenAI's
    // constrained-decoding mode (gpt-4o-2024-08-06+ and gpt-4o-mini);
    // older models reject the request rather than silently degrading.
    // `name` is required by the wire format; "structured_response" is a
    // recipe-neutral identifier (callers cannot supply a name without
    // expanding the IAIProvider signature, and the provider-side name
    // has no semantic effect on the response).
    match structuredOutputSchema with
    | Some schema ->
        dict["response_format"] <- {|
            ``type`` = "json_schema"
            json_schema = {|
                name = "structured_response"
                schema = schema
                strict = true
            |}
        |}
    | None -> ()

    JsonSerializer.Serialize(dict, jsonOptions)

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

/// Apply one SSE data payload to the accumulator. Mirrors the Claude
/// streaming parser's control flow but for OpenAI's delta shape.
let applyStreamChunk (state: StreamState) (onStream: (string -> unit) option) (data: string) =
    if data = "[DONE]" then
        ()
    else
        try
            let doc = JsonDocument.Parse(data)
            let root = doc.RootElement

            // Phase 6i.B: capture the final usage chunk. With
            // `stream_options.include_usage = true`, OpenAI sends a chunk
            // immediately before `[DONE]` with empty `choices: []` and a
            // populated `usage` at the root. Earlier chunks omit `usage`.
            // We accept any chunk that carries it — if more than one
            // arrives, the last write wins.
            match parseUsage root with
            | Some u -> state.Usage <- Some u
            | None -> ()

            match root.TryGetProperty("choices") with
            | true, choices when choices.ValueKind = JsonValueKind.Array && choices.GetArrayLength() > 0 ->
                let choice = choices[0]

                // Text-content delta: append to Content and fire onStream.
                match choice.TryGetProperty("delta") with
                | true, delta ->
                    match delta.TryGetProperty("content") with
                    | true, c when c.ValueKind = JsonValueKind.String ->
                        let text = c.GetString()

                        if text <> null && text <> "" then
                            state.Content <- state.Content + text
                            onStream |> Option.iter (fun cb -> cb text)
                    | _ -> ()

                    // Tool-call delta: OpenAI sends `tool_calls[].index` +
                    // partial fields. First chunk for an index has
                    // `id` + `function.name`; subsequent chunks have
                    // `function.arguments` fragments.
                    match delta.TryGetProperty("tool_calls") with
                    | true, tcs when tcs.ValueKind = JsonValueKind.Array ->
                        for tc in tcs.EnumerateArray() do
                            let index =
                                match tc.TryGetProperty("index") with
                                | true, i when i.ValueKind = JsonValueKind.Number -> i.GetInt32()
                                | _ -> 0

                            let id =
                                match tc.TryGetProperty("id") with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> null

                            let name, argsFragment =
                                match tc.TryGetProperty("function") with
                                | true, fn ->
                                    let n =
                                        match fn.TryGetProperty("name") with
                                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                        | _ -> null

                                    let a =
                                        match fn.TryGetProperty("arguments") with
                                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                        | _ -> ""

                                    n, a
                                | _ -> null, ""

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
                                Id = (if id <> null then id else current.Id)
                                Name = (if name <> null then name else current.Name)
                                Arguments = current.Arguments + argsFragment
                            }

                            state.ToolCalls <-
                                state.ToolCalls |> List.mapi (fun i c -> if i = index then updated else c)
                    | _ -> ()
                | _ -> ()

                // Final chunk carries `finish_reason`.
                match choice.TryGetProperty("finish_reason") with
                | true, fr when fr.ValueKind = JsonValueKind.String ->
                    let reason = fr.GetString()

                    state.StopReason <-
                        match reason with
                        | "tool_calls" -> "tool_use"
                        | "length" -> "max_tokens"
                        | "stop"
                        | _ -> "end_turn"
                | _ -> ()
            | _ -> ()
        with _ ->
            ()