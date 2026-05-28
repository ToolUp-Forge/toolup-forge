module internal GeminiAIProviderWire

open System.Text.Json
open ToolUp.Platform.AI

// ─── JSON helpers ────────────────────────────────────────────────

let private jsonOptions =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.DefaultIgnoreCondition <- Serialization.JsonIgnoreCondition.WhenWritingNull
    opts

// ─── Vision support ──────────────────────────────────────────────
//
// Gemini's 2.5 and 1.5 model families are multimodal-by-default —
// every model accepts image / audio / video input. Older
// text-completion-only models (gemini-1.0-pro etc.) are not part of
// the v1 supported set this provider exposes. The check stays in
// place to keep the symmetry with OpenAI / Claude (and so a future
// text-only Gemini variant has somewhere obvious to be excluded).
let isVisionCapable (model: string) =
    let m = model.ToLowerInvariant()

    m.Contains "gemini-2"
    || m.Contains "gemini-1.5"
    || m.Contains "gemini-pro-vision"

// ─── Wire ↔ forge tool-call id correlation ───────────────────────
//
// Gemini's `functionCall` parts carry only a `name` + `args` — no id.
// The forge contract requires every `AIProviderToolCall.Id` to be
// non-empty so the agent loop can pair the model's call with its
// later `functionResponse`. We synthesise a deterministic id from
// the call's name + position; the response path strips it back to
// the name when emitting `functionResponse` to Gemini.
//
// Format: "gemini-fc-<name>-<index>". The index disambiguates two
// calls to the same tool in one assistant turn (rare but legal).
let private syntheticToolCallId (name: string) (index: int) : string = sprintf "gemini-fc-%s-%d" name index

/// Recover the tool name from a synthetic Gemini call id. When the
/// id was produced upstream by another provider (e.g. a multi-provider
/// session that started on Claude and switched to Gemini), the
/// stored id may not match the synthetic shape — fall back to the
/// raw id, which Gemini ignores anyway (it correlates by `name`).
let toolNameFromSyntheticId (id: string) (fallbackName: string) : string =
    if id.StartsWith("gemini-fc-") then
        let body = id.Substring("gemini-fc-".Length)
        // Strip the trailing "-<index>" if present.
        let lastDash = body.LastIndexOf('-')

        if lastDash > 0 then body.Substring(0, lastDash) else body
    else
        fallbackName

// ─── Response parsing ────────────────────────────────────────────

/// Parse Gemini's `usageMetadata` block into the provider-neutral
/// `TokenUsage` shape. Gemini's vocabulary:
///   - `promptTokenCount` — total input tokens for this turn
///     (including any cached portion).
///   - `cachedContentTokenCount` — portion served from cache. Absent
///     when no cache hit.
///   - `candidatesTokenCount` — generated output tokens.
///
/// `CacheCreationTokens` is `None` — Gemini's explicit cache API
/// (cachedContents) is a separate resource lifecycle the provider
/// doesn't currently manage, and the per-request response doesn't
/// expose a cache-write count.
let parseUsage (root: JsonElement) : TokenUsage option =
    match root.TryGetProperty("usageMetadata") with
    | true, usage ->
        let getInt (name: string) =
            match usage.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0

        Some {
            PromptTokens = getInt "promptTokenCount"
            CachedPromptTokens = getInt "cachedContentTokenCount"
            OutputTokens = getInt "candidatesTokenCount"
            CacheCreationTokens = None
        }
    | _ -> None

/// Map Gemini's `finishReason` enum to the provider-neutral
/// vocabulary the agent loop recognises. Tool calls override this
/// — when the response carries any `functionCall` parts the stop
/// reason is `tool_use` regardless of what Gemini said.
let private mapFinishReason (reason: string) =
    match reason with
    | "MAX_TOKENS" -> "max_tokens"
    | "STOP"
    | "FINISH_REASON_STOP"
    | _ -> "end_turn"

/// Parse a Gemini non-streaming `generateContent` response.
///
/// Shape: `candidates[0].content.parts[]` carrying `text` and / or
/// `functionCall` blocks; `candidates[0].finishReason`;
/// top-level `usageMetadata`.
let parseResponse (json: string) : AIProviderResponse =
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let usage = parseUsage root

    let firstCandidate =
        match root.TryGetProperty("candidates") with
        | true, arr when arr.ValueKind = JsonValueKind.Array && arr.GetArrayLength() > 0 -> Some(arr[0])
        | _ -> None

    match firstCandidate with
    | None -> {
        Content = ""
        ToolCalls = []
        StopReason = "end_turn"
        Usage = usage
      }
    | Some candidate ->
        let finishReason =
            match candidate.TryGetProperty("finishReason") with
            | true, fr when fr.ValueKind = JsonValueKind.String -> fr.GetString()
            | _ -> "STOP"

        let parts =
            match candidate.TryGetProperty("content") with
            | true, content ->
                match content.TryGetProperty("parts") with
                | true, p when p.ValueKind = JsonValueKind.Array -> p.EnumerateArray() |> Seq.toList
                | _ -> []
            | _ -> []

        let mutable textContent = ""
        let mutable toolCalls: AIProviderToolCall list = []
        let mutable toolCallIndex = 0

        for part in parts do
            match part.TryGetProperty("text") with
            | true, t when t.ValueKind = JsonValueKind.String -> textContent <- textContent + t.GetString()
            | _ ->
                match part.TryGetProperty("functionCall") with
                | true, fc ->
                    let name =
                        match fc.TryGetProperty("name") with
                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                        | _ -> ""

                    let argsJson =
                        match fc.TryGetProperty("args") with
                        | true, v -> v.GetRawText()
                        | _ -> "{}"

                    toolCalls <-
                        toolCalls
                        @ [
                            {
                                AIProviderToolCall.Id = syntheticToolCallId name toolCallIndex
                                Name = name
                                Arguments = argsJson
                            }
                        ]

                    toolCallIndex <- toolCallIndex + 1
                | _ -> ()

        let stopReason =
            if not toolCalls.IsEmpty then
                "tool_use"
            else
                mapFinishReason finishReason

        {
            Content = textContent
            ToolCalls = toolCalls
            StopReason = stopReason
            Usage = usage
        }

// ─── Request building ────────────────────────────────────────────

/// Build the `parts` array for a multipart user message. Text
/// parts become `{ text }`; base64 image parts become
/// `{ inlineData: { mimeType, data } }`; URL image parts become
/// `{ fileData: { mimeType, fileUri } }` (Gemini fetches the URL
/// server-side, mirroring Anthropic's behaviour).
let private buildMultipartParts (parts: AIContentPart list) : obj list =
    parts
    |> List.map (fun part ->
        match part with
        | TextPart s -> box {| text = s |}
        | ImagePart payload ->
            match payload.Source with
            | Base64Bytes bytes ->
                box {|
                    inlineData = {|
                        mimeType = payload.MediaType
                        data = System.Convert.ToBase64String bytes
                    |}
                |}
            | Url u ->
                box {|
                    fileData = {|
                        mimeType = payload.MediaType
                        fileUri = u
                    |}
                |})

/// Convert a single forge `AIProviderMessage` into Gemini's
/// `Content` shape: `{ role: "user" | "model", parts: [...] }`. A
/// message carrying tool results becomes a `user`-role content
/// whose parts are `functionResponse` blocks (Gemini does not have
/// a "tool"-role channel — tool results ride on user-role turns).
/// A message carrying tool calls becomes a `model`-role content
/// with `functionCall` parts plus any narrative `text`.
let private toGeminiContent (msg: AIProviderMessage) : obj =
    // Gemini uses "model" rather than "assistant".
    let role =
        match msg.Role with
        | "assistant" -> "model"
        | "system" -> "system" // handled separately at the request level; never reached here in practice
        | r -> r

    if not msg.ToolResults.IsEmpty then
        let parts =
            msg.ToolResults
            |> List.map (fun tr ->
                let name = toolNameFromSyntheticId tr.ToolCallId ""

                // Gemini expects `response` to be an object; the
                // forge contract stores tool output as a string
                // (often JSON). Try to parse as JSON to preserve
                // structure; fall back to `{ output: <string> }`
                // when the content isn't JSON.
                let response =
                    try
                        JsonSerializer.Deserialize<JsonElement>(tr.Content) |> box
                    with _ ->
                        box {| output = tr.Content |}

                box {|
                    functionResponse = {| name = name; response = response |}
                |})

        box {| role = "user"; parts = parts |}
    elif not msg.Parts.IsEmpty then
        box {|
            role = role
            parts = buildMultipartParts msg.Parts
        |}
    elif not msg.ToolCalls.IsEmpty then
        let textPart =
            if System.String.IsNullOrEmpty msg.Content then
                []
            else
                [ box {| text = msg.Content |} ]

        let callParts =
            msg.ToolCalls
            |> List.map (fun tc ->
                let args =
                    try
                        if System.String.IsNullOrWhiteSpace tc.Arguments then
                            JsonSerializer.Deserialize<JsonElement>("{}")
                        else
                            JsonSerializer.Deserialize<JsonElement>(tc.Arguments)
                    with _ ->
                        JsonSerializer.Deserialize<JsonElement>("{}")

                box {|
                    functionCall = {| name = tc.Name; args = args |}
                |})

        box {|
            role = role
            parts = textPart @ callParts
        |}
    else
        box {|
            role = role
            parts = [| box {| text = msg.Content |} |]
        |}

let private toGeminiContents (messages: AIProviderMessage list) =
    messages
    |> List.filter (fun m -> m.Role <> "system")
    |> List.map toGeminiContent

let private buildFunctionDeclarations (tools: AIProviderToolDef list) =
    tools
    |> List.map (fun t -> {|
        name = t.Name
        description = t.Description
        parameters = JsonSerializer.Deserialize<JsonElement>(t.InputSchema)
    |})

/// Build the Gemini v1beta `generateContent` request body.
/// `structuredOutputSchema` flips `generationConfig.responseMimeType`
/// to `application/json` and supplies a `responseSchema` — when
/// `None`, the request runs in normal free-text / tool-use mode.
let buildRequestBody
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (structuredOutputSchema: JsonElement option)
    =
    let dict = System.Collections.Generic.Dictionary<string, obj>()
    dict["contents"] <- toGeminiContents messages

    match systemPrompt with
    | Some prompt -> dict["systemInstruction"] <- {| parts = [| {| text = prompt |} |] |}
    | None -> ()

    let fnDecls = buildFunctionDeclarations tools

    if not fnDecls.IsEmpty then
        dict["tools"] <- [ {| functionDeclarations = fnDecls |} ]

        dict["toolConfig"] <- {|
            functionCallingConfig = {| mode = "AUTO" |}
        |}

    match structuredOutputSchema with
    | Some schema ->
        dict["generationConfig"] <- {|
            responseMimeType = "application/json"
            responseSchema = schema
        |}
    | None -> ()

    JsonSerializer.Serialize(dict, jsonOptions)

// ─── Streaming accumulator ───────────────────────────────────────

/// Mutable streaming state. Gemini's SSE stream emits one JSON
/// object per `data:` line whose shape is the same as the
/// non-streaming response (`candidates[0].content.parts[]` +
/// optional `finishReason` + optional `usageMetadata`). Each chunk
/// carries a *partial* set of parts that append to the accumulator;
/// the final chunk carries `finishReason` and `usageMetadata`. No
/// `[DONE]` sentinel — the stream just closes.
type StreamState = {
    mutable Content: string
    mutable ToolCalls: AIProviderToolCall list
    mutable StopReason: string
    mutable Usage: TokenUsage option
    mutable ToolCallIndex: int
}

let initialStreamState () = {
    Content = ""
    ToolCalls = []
    StopReason = "end_turn"
    Usage = None
    ToolCallIndex = 0
}

/// Apply one SSE `data:` payload to the accumulator.
let applyStreamChunk (state: StreamState) (onStream: (string -> unit) option) (data: string) =
    if System.String.IsNullOrWhiteSpace data then
        ()
    else
        try
            let doc = JsonDocument.Parse(data)
            let root = doc.RootElement

            match parseUsage root with
            | Some u -> state.Usage <- Some u
            | None -> ()

            match root.TryGetProperty("candidates") with
            | true, candidates when candidates.ValueKind = JsonValueKind.Array && candidates.GetArrayLength() > 0 ->
                let candidate = candidates[0]

                match candidate.TryGetProperty("content") with
                | true, content ->
                    match content.TryGetProperty("parts") with
                    | true, parts when parts.ValueKind = JsonValueKind.Array ->
                        for part in parts.EnumerateArray() do
                            match part.TryGetProperty("text") with
                            | true, t when t.ValueKind = JsonValueKind.String ->
                                let text = t.GetString()

                                if text <> null && text <> "" then
                                    state.Content <- state.Content + text
                                    onStream |> Option.iter (fun cb -> cb text)
                            | _ ->
                                match part.TryGetProperty("functionCall") with
                                | true, fc ->
                                    let name =
                                        match fc.TryGetProperty("name") with
                                        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                        | _ -> ""

                                    let argsJson =
                                        match fc.TryGetProperty("args") with
                                        | true, v -> v.GetRawText()
                                        | _ -> "{}"

                                    // Gemini emits function calls as
                                    // complete objects per chunk (unlike
                                    // OpenAI which streams arguments as
                                    // partial JSON fragments). Append as a
                                    // new tool call entry.
                                    state.ToolCalls <-
                                        state.ToolCalls
                                        @ [
                                            {
                                                AIProviderToolCall.Id = syntheticToolCallId name state.ToolCallIndex
                                                Name = name
                                                Arguments = argsJson
                                            }
                                        ]

                                    state.ToolCallIndex <- state.ToolCallIndex + 1
                                | _ -> ()
                    | _ -> ()
                | _ -> ()

                match candidate.TryGetProperty("finishReason") with
                | true, fr when fr.ValueKind = JsonValueKind.String ->
                    let reason = fr.GetString()

                    state.StopReason <-
                        if not state.ToolCalls.IsEmpty then
                            "tool_use"
                        else
                            mapFinishReason reason
                | _ -> ()
            | _ -> ()
        with _ ->
            ()