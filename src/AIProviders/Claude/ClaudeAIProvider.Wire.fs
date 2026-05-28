module internal ClaudeAIProviderWire

open System
open System.Text.Json
open ToolUp.Platform.AI

// ─── JSON helpers ────────────────────────────────────────────────

let private jsonOptions =
    let opts = JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
    opts.DefaultIgnoreCondition <- Serialization.JsonIgnoreCondition.WhenWritingNull
    opts

let private toJson obj =
    JsonSerializer.Serialize(obj, jsonOptions)

// ─── Claude API types ────────────────────────────────────────────

type private ContentBlock =
    | TextBlock of text: string
    | ToolUseBlock of id: string * name: string * input: JsonElement

type private ClaudeMessage = { Role: string; Content: JsonElement }

// ─── Response parsing ────────────────────────────────────────────

/// Parse Anthropic's `usage` block into the provider-neutral `TokenUsage`
/// shape (Phase 6i.B). Returns `None` when no `usage` field is present —
/// healthy responses always carry one, so `None` typically signals a
/// malformed response.
///
/// Anthropic vocabulary:
///   - `input_tokens` — uncached input tokens for this turn
///   - `cache_creation_input_tokens` — tokens written to the prompt cache
///     this turn (cache miss); 0 / absent on subsequent turns that re-read
///     the same prefix
///   - `cache_read_input_tokens` — tokens served from cache (cache hit)
///   - `output_tokens` — generated tokens
///
/// `PromptTokens` aggregates all three input categories so it remains a
/// total across providers; `CachedPromptTokens` exposes the cache-read
/// portion; `CacheCreationTokens` exposes the Anthropic-specific
/// cache-write count (useful for cost analysis — writes are billed at a
/// premium over reads).
let parseUsage (root: JsonElement) : TokenUsage option =
    match root.TryGetProperty("usage") with
    | true, usage ->
        let getInt (name: string) =
            match usage.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
            | _ -> 0

        let getOptInt (name: string) =
            match usage.TryGetProperty(name) with
            | true, v when v.ValueKind = JsonValueKind.Number -> Some(v.GetInt32())
            | _ -> None

        let inputTokens = getInt "input_tokens"
        let cacheRead = getInt "cache_read_input_tokens"
        let cacheCreation = getInt "cache_creation_input_tokens"
        let outputTokens = getInt "output_tokens"

        Some {
            PromptTokens = inputTokens + cacheRead + cacheCreation
            CachedPromptTokens = cacheRead
            OutputTokens = outputTokens
            CacheCreationTokens = getOptInt "cache_creation_input_tokens"
        }
    | _ -> None

let parseResponse (json: string) : AIProviderResponse =
    let doc = JsonDocument.Parse(json)
    let root = doc.RootElement

    let stopReason =
        match root.TryGetProperty("stop_reason") with
        | true, sr -> sr.GetString()
        | _ -> "end_turn"

    let mutable textContent = ""
    let mutable toolCalls = []

    match root.TryGetProperty("content") with
    | true, content when content.ValueKind = JsonValueKind.Array ->
        for block in content.EnumerateArray() do
            match block.TryGetProperty("type") with
            | true, t when t.GetString() = "text" ->
                match block.TryGetProperty("text") with
                | true, txt -> textContent <- textContent + txt.GetString()
                | _ -> ()
            | true, t when t.GetString() = "tool_use" ->
                let id =
                    match block.TryGetProperty("id") with
                    | true, v -> v.GetString()
                    | _ -> ""

                let name =
                    match block.TryGetProperty("name") with
                    | true, v -> v.GetString()
                    | _ -> ""

                let input =
                    match block.TryGetProperty("input") with
                    | true, v -> v.GetRawText()
                    | _ -> "{}"

                toolCalls <-
                    toolCalls
                    @ [
                        {
                            AIProviderToolCall.Id = id
                            Name = name
                            Arguments = input
                        }
                    ]
            | _ -> ()
    | _ -> ()

    {
        Content = textContent
        ToolCalls = toolCalls
        StopReason = stopReason
        Usage = parseUsage root
    }

// ─── Request building ────────────────────────────────────────────

/// Anthropic prompt-caching marker (Phase 6i.B). Attached as `cache_control`
/// metadata on a content block; the API stores a cache breakpoint at that
/// position. The marker itself is metadata, not content — moving the marker
/// between turns does not invalidate prior-turn cache writes; a longer
/// prefix from an earlier turn can still hit on a later request that marks
/// a different position.
///
/// Anthropic permits up to 4 markers per request. We use three:
///   1. Last text block of `system` (always — caches the static system prompt)
///   2. Last entry in `tools` (always — caches the static tools list)
///   3. Last content block of the second-to-last message, if present
///      (caches conversation history through the prior turn / prior tool
///      exchange within an agent loop)
///
/// Sub-threshold prefixes (<1024 tokens for Sonnet/Haiku, <2048 for Opus)
/// are silently processed without caching — Anthropic does not reject the
/// request. No client-side guard.
let private cacheControlMarker: obj = box {| ``type`` = "ephemeral" |}

// ─── Phase 6o vision support ─────────────────────────────────────
//
// Anthropic vision-capable models. Used to short-circuit a request
// that carries `ImagePart` content against a non-vision model: the
// provider returns `AIProviderError.UnsupportedCapability` rather
// than shipping the (potentially large, often PII) image bytes to
// Anthropic and waiting for HTTP 400. The list is conservative —
// add new vision-capable model ids here as they ship.
let isVisionCapable (model: string) =
    let m = model.ToLowerInvariant()
    // Sonnet 4 / Opus 4 family + earlier sonnet-3.5/3.7 support
    // vision. Haiku-Compact and the older haiku-3 family do not.
    (m.Contains "claude-sonnet" || m.Contains "sonnet")
    || m.Contains "claude-opus"
    || (m.Contains "haiku-3.5" || m.Contains "haiku-3-5")

/// Serialise an `ImagePayload` into Anthropic's `image` content
/// block shape. Base64 sources are emitted as
/// `{ type: "image", source: { type: "base64", media_type, data } }`;
/// URL sources as `{ type: "image", source: { type: "url", url } }`
/// (Anthropic Messages API fetches URLs server-side; the SDK does
/// NOT fetch — that's a deliberate division so the SDK doesn't take
/// on URL-fetch network policy / SSRF concerns).
let private imageBlock (payload: ImagePayload) : obj =
    let source = System.Collections.Generic.Dictionary<string, obj>()

    match payload.Source with
    | Base64Bytes bytes ->
        source["type"] <- "base64"
        source["media_type"] <- payload.MediaType
        source["data"] <- System.Convert.ToBase64String bytes
    | Url u ->
        source["type"] <- "url"
        source["url"] <- u

    let block = System.Collections.Generic.Dictionary<string, obj>()
    block["type"] <- "image"
    block["source"] <- source
    block :> obj

/// Build the Anthropic content-block array from a non-empty
/// `Parts` list. Text parts become `{ type: "text", text }`; image
/// parts become the `image` block via `imageBlock`. Returns the
/// JsonElement so `buildMessageContent` can plug it in directly.
let private buildMultipartContent (parts: AIContentPart list) (markForCache: bool) : JsonElement =
    let blocks =
        parts
        |> List.map (fun part ->
            match part with
            | TextPart s ->
                let d = System.Collections.Generic.Dictionary<string, obj>()
                d["type"] <- "text"
                d["text"] <- s
                d :> obj
            | ImagePart payload -> imageBlock payload)

    if markForCache && not blocks.IsEmpty then
        let last =
            blocks[blocks.Length - 1] :?> System.Collections.Generic.Dictionary<string, obj>

        last["cache_control"] <- cacheControlMarker

    JsonSerializer.SerializeToElement(blocks, jsonOptions)

/// Serialise a single message for the Claude request body.
///
/// Three shapes, all required by the Anthropic content-block protocol:
///   1. Plain text message (user prompt, assistant answer with no tool use)
///      → `content` is a string (or array form when `markForCache` is true,
///        since `cache_control` requires array form).
///   2. User message carrying tool results
///      → `content` is an array of `tool_result` content blocks.
///   3. Assistant message that invoked tools
///      → `content` is an array of `text` + `tool_use` blocks. The tool_use
///        blocks MUST be present so the next turn's tool_result blocks can
///        be paired back to their tool_use by id — otherwise Claude rejects
///        the request with HTTP 400 "each tool_result block must have a
///        corresponding tool_use block in the previous message".
///
/// When `markForCache` is true, the LAST content block of the produced array
/// (or the single text block, in case 1) carries a `cache_control` marker.
let private buildMessageContent (msg: AIProviderMessage) (markForCache: bool) : JsonElement =
    // Phase 6o — multipart content. When `Parts` is populated the
    // message overrides the plain-text path entirely: providers see
    // the per-part wire blocks (text + image) instead of the legacy
    // `Content: string`. Tool-call / tool-result envelopes are
    // disjoint with multipart content per the contract (the agent
    // loop doesn't build messages that carry both); if a caller
    // somehow does, multipart wins.
    if not msg.Parts.IsEmpty then
        buildMultipartContent msg.Parts markForCache
    elif not msg.ToolResults.IsEmpty then
        // Case 2 — user message with tool_result blocks.
        let lastIdx = msg.ToolResults.Length - 1

        let blocks =
            msg.ToolResults
            |> List.mapi (fun i tr ->
                let d = System.Collections.Generic.Dictionary<string, obj>()
                d["type"] <- "tool_result"
                d["tool_use_id"] <- tr.ToolCallId
                d["content"] <- tr.Content

                if markForCache && i = lastIdx then
                    d["cache_control"] <- cacheControlMarker

                d :> obj)

        JsonSerializer.SerializeToElement(blocks, jsonOptions)
    elif not msg.ToolCalls.IsEmpty then
        // Case 3 — assistant message with tool_use blocks. Include any text
        // content as a `text` block before the tool_use blocks so the model
        // can still narrate ("I'll load your data…") alongside the calls.
        let textBlocks =
            if System.String.IsNullOrEmpty msg.Content then
                []
            else
                let d = System.Collections.Generic.Dictionary<string, obj>()
                d["type"] <- "text"
                d["text"] <- msg.Content
                [ d :> obj ]

        let toolUseBlocks =
            msg.ToolCalls
            |> List.map (fun tc ->
                let inputJson =
                    if System.String.IsNullOrWhiteSpace tc.Arguments then
                        JsonSerializer.Deserialize<JsonElement>("{}")
                    else
                        JsonSerializer.Deserialize<JsonElement>(tc.Arguments)

                let d = System.Collections.Generic.Dictionary<string, obj>()
                d["type"] <- "tool_use"
                d["id"] <- tc.Id
                d["name"] <- tc.Name
                d["input"] <- inputJson
                d :> obj)

        let allBlocks = textBlocks @ toolUseBlocks

        if markForCache && not allBlocks.IsEmpty then
            // Mutate the last block in place to add cache_control.
            let last =
                allBlocks[allBlocks.Length - 1] :?> System.Collections.Generic.Dictionary<string, obj>

            last["cache_control"] <- cacheControlMarker

        JsonSerializer.SerializeToElement(allBlocks, jsonOptions)
    // Case 1 — simple text message. cache_control requires array form, so
    // promote the string to a single text block carrying the marker when
    // markForCache is true; otherwise emit the legacy string shape.
    elif markForCache then
        let d = System.Collections.Generic.Dictionary<string, obj>()
        d["type"] <- "text"
        d["text"] <- msg.Content
        d["cache_control"] <- cacheControlMarker
        JsonSerializer.SerializeToElement([ d :> obj ], jsonOptions)
    else
        JsonSerializer.SerializeToElement(msg.Content, jsonOptions)

/// Build the tool array, marking the last entry with `cache_control` when
/// any tools are present. Anthropic caches the entire tools array as a
/// contiguous prefix from the marker position, so a single marker on the
/// last tool covers all of them.
let private buildTools (tools: AIProviderToolDef list) =
    let lastIdx = tools.Length - 1

    tools
    |> List.mapi (fun i t ->
        let d = System.Collections.Generic.Dictionary<string, obj>()
        d["name"] <- t.Name
        d["description"] <- t.Description
        d["input_schema"] <- JsonSerializer.Deserialize<JsonElement>(t.InputSchema)

        if i = lastIdx then
            d["cache_control"] <- cacheControlMarker

        d :> obj)

let buildRequestBody
    (model: string)
    (maxTokens: int)
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (stream: bool)
    =
    // Build the request as a dictionary to control which fields are included.
    // Claude API rejects null/unknown fields.
    //
    // Phase 6i.B: mark the second-to-last message for cache_control. On the
    // first turn (length < 2) no message-level breakpoint fires — the
    // system+tools breakpoints handle the static prefix. On later turns this
    // marker progresses naturally as the conversation grows; markers are
    // metadata so prior-turn cache writes still hit.
    let messageCount = messages.Length

    let msgs =
        messages
        |> List.mapi (fun i m ->
            let markForCache = messageCount >= 2 && i = messageCount - 2

            {|
                role = m.Role
                content = buildMessageContent m markForCache
            |})

    let dict = System.Collections.Generic.Dictionary<string, obj>()
    dict["model"] <- model
    dict["max_tokens"] <- maxTokens
    dict["messages"] <- msgs

    // System prompt as array form so the text block can carry a
    // cache_control marker — caches the static system prompt across
    // every request.
    match systemPrompt with
    | Some prompt ->
        let block = System.Collections.Generic.Dictionary<string, obj>()
        block["type"] <- "text"
        block["text"] <- prompt
        block["cache_control"] <- cacheControlMarker
        dict["system"] <- [ block :> obj ]
    | None -> ()

    let toolDefs = buildTools tools

    if not toolDefs.IsEmpty then
        dict["tools"] <- toolDefs

    if stream then
        dict["stream"] <- true

    JsonSerializer.Serialize(dict, jsonOptions)

// ─── Streaming response parsing ──────────────────────────────────

let parseStreamLine (line: string) (onStream: (string -> unit) option) =
    if line.StartsWith("data: ") then
        let data = line.Substring(6)

        if data <> "[DONE]" then
            try
                let doc = JsonDocument.Parse(data)
                let root = doc.RootElement

                match root.TryGetProperty("type") with
                | true, t when t.GetString() = "content_block_delta" ->
                    match root.TryGetProperty("delta") with
                    | true, delta ->
                        match delta.TryGetProperty("type") with
                        | true, dt when dt.GetString() = "text_delta" ->
                            match delta.TryGetProperty("text") with
                            | true, txt ->
                                let text = txt.GetString()
                                onStream |> Option.iter (fun cb -> cb text)
                            | _ -> ()
                        | _ -> ()
                    | _ -> ()
                | _ -> ()
            with _ ->
                ()