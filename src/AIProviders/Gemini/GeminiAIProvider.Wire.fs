module internal GeminiAIProviderWire

// ─── Portable Gemini wire mapping (Wave 32, Phase 253) ───────────
//
// The Gemini request-build + response-parse, ported from `System.Text.Json`
// onto the portable `ToolUp.AI.Wire` `JsonValue` model (Phase 249) under the
// relocated `ToolUp.Platform.AI` connector contract (Phase 250). This file
// contains ZERO `System.Text.Json` — it depends only on `ToolUp.AI.Wire`
// (`JsonValue` / `JsonHost`) + `ToolUp.Platform.AI` (the contract records +
// `ErrorClassifier`), so the same source compiles to both the .NET server
// host and a Fable browser host (GP 8 — Fable native; GP 12 — host-portable
// mapping). The provider `.fs` owns the non-portable egress (the BCL
// `HttpClient` via the Phase 251 `HttpClientTransport`); this mapper is pure.
//
// Gemini's wire shape (vs OpenAI's): `contents` carry roles `user` /
// `model`; tools ride on `tools: [{ functionDeclarations: [...] }]`; a tool
// CALL is a `functionCall:{name, args}` part, a tool RESULT a
// `functionResponse:{name, response}` part — both **name-keyed**, never
// id-keyed. The forge contract requires a non-empty `AIProviderToolCall.Id`,
// so we synthesise a deterministic id from the call's name + position and
// strip it back to the name on the response path (see the id-correlation
// block below). Structured output flips `generationConfig.responseSchema`
// (Phase 67b). Usage is `usageMetadata.{promptTokenCount, …}`.

open ToolUp.AI.Wire
open ToolUp.Platform.AI

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
// Not `private` so the wire test pack can pin the round-trip directly.
let syntheticToolCallId (name: string) (index: int) : string = sprintf "gemini-fc-%s-%d" name index

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
let parseUsage (root: JsonValue) : TokenUsage option =
    match JsonValue.tryField "usageMetadata" root with
    | Some usage ->
        let getInt (name: string) =
            usage
            |> JsonValue.tryField name
            |> Option.bind JsonValue.asInt
            |> Option.defaultValue 0

        Some {
            PromptTokens = getInt "promptTokenCount"
            CachedPromptTokens = getInt "cachedContentTokenCount"
            OutputTokens = getInt "candidatesTokenCount"
            CacheCreationTokens = None
        }
    | None -> None

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

/// Walk a parsed Gemini `generateContent` response value into the
/// provider-neutral `AIProviderResponse`.
///
/// Shape: `candidates[0].content.parts[]` carrying `text` and / or
/// `functionCall` blocks; `candidates[0].finishReason`;
/// top-level `usageMetadata`.
let private parseRoot (root: JsonValue) : AIProviderResponse =
    let usage = parseUsage root

    let firstCandidate =
        root |> JsonValue.tryField "candidates" |> Option.bind (JsonValue.tryItem 0)

    match firstCandidate with
    | None -> {
        Content = ""
        ToolCalls = []
        StopReason = "end_turn"
        Usage = usage
      }
    | Some candidate ->
        let finishReason =
            candidate
            |> JsonValue.tryField "finishReason"
            |> Option.bind JsonValue.asString
            |> Option.defaultValue "STOP"

        let parts =
            candidate
            |> JsonValue.tryField "content"
            |> Option.bind (JsonValue.tryField "parts")
            |> Option.bind JsonValue.asArray
            |> Option.defaultValue []

        let mutable textContent = ""
        let mutable toolCalls: AIProviderToolCall list = []
        let mutable toolCallIndex = 0

        for part in parts do
            match part |> JsonValue.tryField "text" |> Option.bind JsonValue.asString with
            | Some t -> textContent <- textContent + t
            | None ->
                match part |> JsonValue.tryField "functionCall" with
                | Some fc ->
                    let name =
                        fc
                        |> JsonValue.tryField "name"
                        |> Option.bind JsonValue.asString
                        |> Option.defaultValue ""

                    // Re-serialise the args sub-tree canonically. The string
                    // is round-tripped back through `JsonHost.parse` in
                    // `toGeminiContent`, so canonical bytes here are
                    // semantically identical to the raw response bytes.
                    let argsJson =
                        fc
                        |> JsonValue.tryField "args"
                        |> Option.map JsonHost.serialize
                        |> Option.defaultValue "{}"

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
                | None -> ()

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

/// Parse a Gemini non-streaming `generateContent` response body.
/// `Error` carries a human-readable detail the provider surfaces as
/// `MalformedResponse` — byte-identical in effect to the old
/// `JsonDocument.Parse` throw caught at the provider boundary, but
/// without the host-specific exception.
let parseResponse (json: string) : Result<AIProviderResponse, string> =
    match JsonHost.parse json with
    | Some root -> Ok(parseRoot root)
    | None -> Error "Gemini response body was not valid JSON"

// ─── Request building ────────────────────────────────────────────

/// Build the `parts` array for a multipart user message. Text
/// parts become `{ text }`; base64 image parts become
/// `{ inlineData: { mimeType, data } }`; URL image parts become
/// `{ fileData: { mimeType, fileUri } }` (Gemini fetches the URL
/// server-side, mirroring Anthropic's behaviour).
let private buildMultipartParts (parts: AIContentPart list) : JsonValue list =
    parts
    |> List.map (fun part ->
        match part with
        | TextPart s -> jobj [ "text", jstr s ]
        | ImagePart payload ->
            match payload.Source with
            | Base64Bytes bytes ->
                jobj [
                    "inlineData",
                    jobj [
                        "mimeType", jstr payload.MediaType
                        "data", jstr (System.Convert.ToBase64String bytes)
                    ]
                ]
            | Url u -> jobj [ "fileData", jobj [ "mimeType", jstr payload.MediaType; "fileUri", jstr u ] ])

/// Parse a JSON-string payload (a tool's input schema, a stored
/// tool-call argument blob, or a tool-result body) into a
/// `JsonValue`, degrading a blank / unparseable string to the given
/// fallback. Replaces the old `JsonSerializer.Deserialize<JsonElement>`
/// + try/with, host-portably.
let private parseOr (fallback: JsonValue) (raw: string) : JsonValue =
    if System.String.IsNullOrWhiteSpace raw then
        fallback
    else
        JsonHost.parse raw |> Option.defaultValue fallback

/// Convert a single forge `AIProviderMessage` into Gemini's
/// `Content` shape: `{ role: "user" | "model", parts: [...] }`. A
/// message carrying tool results becomes a `user`-role content
/// whose parts are `functionResponse` blocks (Gemini does not have
/// a "tool"-role channel — tool results ride on user-role turns).
/// A message carrying tool calls becomes a `model`-role content
/// with `functionCall` parts plus any narrative `text`.
let private toGeminiContent (msg: AIProviderMessage) : JsonValue =
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
                // (often JSON). Parse to preserve structure; fall
                // back to `{ output: <string> }` when not JSON.
                let response = parseOr (jobj [ "output", jstr tr.Content ]) tr.Content

                jobj [ "functionResponse", jobj [ "name", jstr name; "response", response ] ])

        jobj [ "role", jstr "user"; "parts", jarr parts ]
    elif not msg.Parts.IsEmpty then
        jobj [ "role", jstr role; "parts", jarr (buildMultipartParts msg.Parts) ]
    elif not msg.ToolCalls.IsEmpty then
        let textPart =
            if System.String.IsNullOrEmpty msg.Content then
                []
            else
                [ jobj [ "text", jstr msg.Content ] ]

        let callParts =
            msg.ToolCalls
            |> List.map (fun tc ->
                let args = parseOr (jobj []) tc.Arguments
                jobj [ "functionCall", jobj [ "name", jstr tc.Name; "args", args ] ])

        jobj [ "role", jstr role; "parts", jarr (textPart @ callParts) ]
    else
        jobj [ "role", jstr role; "parts", jarr [ jobj [ "text", jstr msg.Content ] ] ]

let private toGeminiContents (messages: AIProviderMessage list) =
    messages
    |> List.filter (fun m -> m.Role <> "system")
    |> List.map toGeminiContent

let private buildFunctionDeclarations (tools: AIProviderToolDef list) : JsonValue list =
    tools
    |> List.map (fun t ->
        jobj [
            "name", jstr t.Name
            "description", jstr t.Description
            // A well-formed tool ships a valid JSON-Schema string; a
            // blank / malformed one degrades to `{}` rather than
            // throwing mid-request (the old STJ path threw before the
            // body even reached the egress try/with).
            "parameters", parseOr (jobj []) t.InputSchema
        ])

/// Build the Gemini v1beta `generateContent` request body.
/// `structuredOutputSchema` flips `generationConfig.responseMimeType`
/// to `application/json` and supplies a `responseSchema` — when
/// `None`, the request runs in normal free-text / tool-use mode.
///
/// Member order (contents → systemInstruction → tools → toolConfig →
/// generationConfig) reproduces the old `Dictionary` insertion order,
/// and `JsonHost.serialize` emits byte-stably, so the wire body is
/// stable across both hosts (seeds the Phase 255 parity corpus).
let buildRequestBody
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (structuredOutputSchema: JsonValue option)
    : string =
    let contents = [ "contents", jarr (toGeminiContents messages) ]

    let systemInstruction =
        match systemPrompt with
        | Some prompt -> [ "systemInstruction", jobj [ "parts", jarr [ jobj [ "text", jstr prompt ] ] ] ]
        | None -> []

    let toolMembers =
        match buildFunctionDeclarations tools with
        | [] -> []
        | fnDecls -> [
            "tools", jarr [ jobj [ "functionDeclarations", jarr fnDecls ] ]
            "toolConfig", jobj [ "functionCallingConfig", jobj [ "mode", jstr "AUTO" ] ]
          ]

    let generationConfig =
        match structuredOutputSchema with
        | Some schema -> [
            "generationConfig", jobj [ "responseMimeType", jstr "application/json"; "responseSchema", schema ]
          ]
        | None -> []

    jobj (contents @ systemInstruction @ toolMembers @ generationConfig)
    |> JsonHost.serialize

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

/// Apply one SSE `data:` payload to the accumulator. An unparseable
/// chunk is silently ignored (`JsonHost.parse` returns `None`),
/// matching the old `try … with _ -> ()`.
let applyStreamChunk (state: StreamState) (onStream: (string -> unit) option) (data: string) =
    if System.String.IsNullOrWhiteSpace data then
        ()
    else
        match JsonHost.parse data with
        | None -> ()
        | Some root ->
            match parseUsage root with
            | Some u -> state.Usage <- Some u
            | None -> ()

            match root |> JsonValue.tryField "candidates" |> Option.bind (JsonValue.tryItem 0) with
            | Some candidate ->
                let parts =
                    candidate
                    |> JsonValue.tryField "content"
                    |> Option.bind (JsonValue.tryField "parts")
                    |> Option.bind JsonValue.asArray
                    |> Option.defaultValue []

                for part in parts do
                    match part |> JsonValue.tryField "text" |> Option.bind JsonValue.asString with
                    | Some text when text <> "" ->
                        state.Content <- state.Content + text
                        onStream |> Option.iter (fun cb -> cb text)
                    | Some _ -> () // empty text delta — no-op
                    | None ->
                        match part |> JsonValue.tryField "functionCall" with
                        | Some fc ->
                            let name =
                                fc
                                |> JsonValue.tryField "name"
                                |> Option.bind JsonValue.asString
                                |> Option.defaultValue ""

                            let argsJson =
                                fc
                                |> JsonValue.tryField "args"
                                |> Option.map JsonHost.serialize
                                |> Option.defaultValue "{}"

                            // Gemini emits function calls as complete
                            // objects per chunk (unlike OpenAI which streams
                            // arguments as partial JSON fragments). Append as
                            // a new tool call entry.
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
                        | None -> ()

                match candidate |> JsonValue.tryField "finishReason" |> Option.bind JsonValue.asString with
                | Some reason ->
                    state.StopReason <-
                        if not state.ToolCalls.IsEmpty then
                            "tool_use"
                        else
                            mapFinishReason reason
                | None -> ()
            | None -> ()