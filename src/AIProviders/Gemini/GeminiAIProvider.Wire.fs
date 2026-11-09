module internal GeminiAIProviderWire

// ─── Portable Gemini wire mapping ────────────────────────────────
//
// The Gemini request build + response parse over the portable `JsonValue`
// model: it depends only on `ToolUp.AI.Wire`, so the same source compiles to
// the .NET server host and a Fable browser host (GP 8, GP 12).
//
// Gemini's wire shape (vs OpenAI's): `contents` carry roles `user` /
// `model`; tools ride on `tools: [{ functionDeclarations: [...] }]`; a tool
// CALL is a `functionCall:{name, args}` part, a tool RESULT a
// `functionResponse:{name, response}` part — both **name-keyed**, never
// id-keyed. The SDK contract requires a non-empty `AIProviderToolCall.Id`,
// so a deterministic id is synthesised from the call's name + position
// (`GeminiStreaming.syntheticToolCallId`) and stripped back to the name on
// the request path. Structured output sets
// `generationConfig.responseSchema`. Usage is `usageMetadata`; the stream
// assembly is `GeminiStreaming` in `ToolUp.AI.Wire`.

open ToolUp.AI.Wire
open ToolUp.Remoting.Json
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

/// A `generateContent` response is one chunk carrying the whole turn: fold it
/// once through the stream assembly. A turn holding a function call is
/// `ToolUse` whatever `finishReason` said.
let private parseRoot (root: JsonValue) : AIProviderResponse =
    let state, _ = GeminiStreaming.applyChunk GeminiStreaming.initial root

    GeminiStreaming.finalize {
        state with
            StopReason =
                if state.ToolCalls.IsEmpty then
                    state.StopReason
                else
                    StopReason.ToolUse
    }

/// Parse a Gemini non-streaming `generateContent` response body. A body
/// that is not JSON is a `MalformedResponse`.
let parseResponse (json: string) : Result<AIProviderResponse, AIProviderError> =
    match JsonHost.parse json with
    | Some root -> Ok(parseRoot root)
    | None -> Error(MalformedResponse "Gemini response body was not valid JSON")

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
            | Url u -> jobj [ "fileData", jobj [ "mimeType", jstr payload.MediaType; "fileUri", jstr u ] ]
        // A block reaches the model as its text summary; the
        // node never enters a request body.
        | BlockPart _ -> jobj [ "text", jstr (AIContentPart.redactedSummary part) ])

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
/// stable across both hosts.
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

/// The body of one connector call. A structured call's schema must parse as
/// JSON: a malformed one is the caller's defect, refused as `PermanentClient`
/// before anything is sent. A streaming call posts the buffered body to the
/// streaming route.
let callBody
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (schema: string option)
    : Result<string, AIProviderError> =
    match schema with
    | None -> Ok(buildRequestBody messages tools systemPrompt None)
    | Some schema ->
        match JsonHost.parse schema with
        | Some value -> Ok(buildRequestBody messages tools systemPrompt (Some value))
        | None -> Error(PermanentClient(0, "structuredOutputSchema is not valid JSON"))