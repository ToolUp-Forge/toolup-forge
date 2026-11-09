module internal OpenAIProviderWire

open ToolUp.AI.Wire
open ToolUp.Remoting.Json
open ToolUp.Platform.AI

// ─── Portable wire mapping ───────────────────────────────────────
//
// The OpenAI chat/completions request build + response parse over the
// portable `JsonValue` model (`ToolUp.AI.Wire`) — no `System.Text.Json`.
// The same source compiles to the .NET server host and a Fable browser
// host: `JsonHost.serialize` is the canonical byte-stable writer, and
// `JsonHost.parse` the host-bridged reader. The stream assembly is
// `OpenAIStreaming` in `ToolUp.AI.Wire`.

// ─── Response parsing ────────────────────────────────────────────

/// Walk a parsed `chat/completions` response.
/// Shape: `choices[0].message.{content, tool_calls[]}` + `choices[0].finish_reason`.
let private parseRoot (root: JsonValue) : AIProviderResponse =
    let usage = OpenAIStreaming.parseUsage root

    let firstChoice =
        root |> JsonValue.tryMember "choices" |> Option.bind (JsonValue.tryItem 0)

    match firstChoice with
    | None -> {
        Content = ""
        ToolCalls = []
        StopReason = StopReason.EndTurn
        Usage = usage
      }
    | Some choice ->
        let stopReason =
            choice
            |> JsonValue.tryMember "finish_reason"
            |> Option.bind JsonValue.asString
            |> Option.defaultValue "stop"
            |> OpenAIStreaming.stopReason

        let message = choice |> JsonValue.tryMember "message"

        let textContent =
            message
            |> Option.bind (JsonValue.tryMember "content")
            |> Option.bind JsonValue.asString
            |> Option.defaultValue ""

        let toolCalls =
            match
                message
                |> Option.bind (JsonValue.tryMember "tool_calls")
                |> Option.bind JsonValue.asArray
            with
            | Some arr -> [
                for tc in arr ->
                    let id =
                        tc
                        |> JsonValue.tryMember "id"
                        |> Option.bind JsonValue.asString
                        |> Option.defaultValue ""

                    let fn = tc |> JsonValue.tryMember "function"

                    let name =
                        fn
                        |> Option.bind (JsonValue.tryMember "name")
                        |> Option.bind JsonValue.asString
                        |> Option.defaultValue ""

                    let args =
                        fn
                        |> Option.bind (JsonValue.tryMember "arguments")
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

/// Parse an OpenAI non-streaming `chat/completions` response body. A body
/// that is not JSON is a `MalformedResponse`.
let parseResponse (json: string) : Result<AIProviderResponse, AIProviderError> =
    match JsonHost.parse json with
    | Some root -> Ok(parseRoot root)
    | None -> Error(MalformedResponse "OpenAI chat/completions response was not valid JSON")

// ─── Request building ────────────────────────────────────────────

// ─── Vision support ──────────────────────────────────────────────
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

            jobj [ "type", jstr "image_url"; "image_url", jobj [ "url", jstr url ] ]
        // A block reaches the model as its text summary; the
        // node never enters a request body.
        | BlockPart _ -> jobj [ "type", jstr "text"; "text", jstr (AIContentPart.redactedSummary part) ])
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
                jobj [
                    "role", jstr "tool"
                    "tool_call_id", jstr tr.ToolCallId
                    "content", jstr tr.Content
                ]
        elif not m.Parts.IsEmpty then
            // Multipart user message. Emit OpenAI's content
            // array shape. Tool-call envelopes don't apply here
            // (multimodal turns are user→assistant, never assistant→tool).
            jobj [ "role", jstr m.Role; "content", buildMultipartContent m.Parts ]
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

            jobj (
                [ "role", jstr m.Role ]
                @ (if m.Content = "" then [] else [ "content", jstr m.Content ])
                @ [ "tool_calls", toolCalls ]
            )
        else
            // Plain user / assistant / system turn.
            jobj [ "role", jstr m.Role; "content", jstr m.Content ]
]

let private buildTools (tools: AIProviderToolDef list) : JsonValue =
    tools
    |> List.map (fun t ->
        // The tool's JSON Schema embeds as a nested object. A connector
        // refuses a malformed one before building (`callBody`); the pure
        // mapper stays total and carries it as a string.
        let parameters =
            JsonHost.parse t.InputSchema |> Option.defaultValue (jstr t.InputSchema)

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
/// flips on `response_format: { type: "json_schema",
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
               // Request usage stats in the final stream chunk.
               [ "stream", jbool true; "stream_options", jobj [ "include_usage", jbool true ] ]
           else
               [])
        @ (match structuredOutputSchema with
           | Some schema ->
               // `strict: true` activates OpenAI's
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

/// The body of one connector call. A structured call's schema, and every
/// tool's input schema, must parse as JSON: a malformed one is the caller's
/// defect, refused as `PermanentClient` before anything is sent.
let callBody
    (model: string)
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (streaming: bool)
    (schema: string option)
    : Result<string, AIProviderError> =
    match tools |> List.tryFind (fun t -> (JsonHost.parse t.InputSchema).IsNone) with
    | Some t -> Error(PermanentClient(0, sprintf "tool '%s' InputSchema is not valid JSON" t.Name))
    | None ->
        match schema with
        | None -> Ok(buildRequestBody model messages tools systemPrompt streaming None)
        | Some schema ->
            match JsonHost.parse schema with
            | Some value -> Ok(buildRequestBody model messages tools systemPrompt false (Some value))
            | None -> Error(PermanentClient(0, "structuredOutputSchema is not valid JSON"))