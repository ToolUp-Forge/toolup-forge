module internal ClaudeAIProviderWire

open ToolUp.AI.Wire
open ToolUp.Platform.AI

// ─── Portable Claude wire mapping (Wave 32, Phase 254) ───────────
//
// The Anthropic Messages API request build + response parse, mapped over
// the portable `JsonValue` model (`ToolUp.AI.Wire`) instead of
// `System.Text.Json`. The file depends only on `ToolUp.AI.Wire`
// (JsonValue / JsonHost + the `ToolUp.Platform.AI` contract types) and
// FSharp.Core, so the same source compiles to a Fable browser host as
// well as the .NET server host (GP 12). Object members are built in a
// fixed order and serialized by the canonical `JsonHost.serialize` writer,
// so the request bytes are deterministic and byte-stable across hosts —
// the property the Phase 255 conformance corpus pins.
//
// The streaming assembly lives in the shared `ToolUp.AI.Wire.ClaudeStreaming`
// state machine; this file owns the non-streaming request/response mapping
// plus the request builder both paths share.

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
let parseUsage (root: JsonValue) : TokenUsage option =
    match root |> JsonValue.tryField "usage" with
    | Some usage ->
        let getInt (name: string) =
            usage
            |> JsonValue.tryField name
            |> Option.bind JsonValue.asInt
            |> Option.defaultValue 0

        let inputTokens = getInt "input_tokens"
        let cacheRead = getInt "cache_read_input_tokens"
        let cacheCreation = getInt "cache_creation_input_tokens"
        let outputTokens = getInt "output_tokens"

        Some {
            PromptTokens = inputTokens + cacheRead + cacheCreation
            CachedPromptTokens = cacheRead
            OutputTokens = outputTokens
            CacheCreationTokens =
                (usage
                 |> JsonValue.tryField "cache_creation_input_tokens"
                 |> Option.bind JsonValue.asInt)
        }
    | None -> None

/// Parse a non-streaming Claude response body. Throws on un-parseable JSON
/// (`JsonHost.parse` returns `None`) so the caller's `try/with` surfaces it
/// as `MalformedResponse`, preserving the prior `JsonDocument.Parse`
/// behaviour. `content` is the block array: `text` blocks concatenate into
/// `Content`; `tool_use` blocks become `ToolCalls` with the raw `input`
/// JSON re-serialized as the Arguments string.
let parseResponse (json: string) : AIProviderResponse =
    match JsonHost.parse json with
    | None -> failwith "Claude response was not valid JSON"
    | Some root ->
        let stopReason =
            root
            |> JsonValue.tryField "stop_reason"
            |> Option.bind JsonValue.asString
            |> Option.defaultValue "end_turn"

        let blocks =
            root
            |> JsonValue.tryField "content"
            |> Option.bind JsonValue.asArray
            |> Option.defaultValue []

        let mutable textContent = ""
        let mutable toolCalls: AIProviderToolCall list = []

        for block in blocks do
            match block |> JsonValue.tryField "type" |> Option.bind JsonValue.asString with
            | Some "text" ->
                match block |> JsonValue.tryField "text" |> Option.bind JsonValue.asString with
                | Some txt -> textContent <- textContent + txt
                | None -> ()
            | Some "tool_use" ->
                let id =
                    block
                    |> JsonValue.tryField "id"
                    |> Option.bind JsonValue.asString
                    |> Option.defaultValue ""

                let name =
                    block
                    |> JsonValue.tryField "name"
                    |> Option.bind JsonValue.asString
                    |> Option.defaultValue ""

                let input =
                    match block |> JsonValue.tryField "input" with
                    | Some v -> JsonHost.serialize v
                    | None -> "{}"

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
let private cacheControlMarker: JsonValue = jobj [ "type", jstr "ephemeral" ]

/// Append a `cache_control` marker onto an object block, preserving member
/// order (the marker lands last, mirroring the prior dictionary mutation).
/// A non-object value passes through unchanged.
let private withCacheControl (v: JsonValue) : JsonValue =
    match v with
    | JObject members -> JObject(members @ [ "cache_control", cacheControlMarker ])
    | other -> other

/// Embed a JSON-Schema / arguments string as JSON structure. Parses the
/// string through `JsonHost.parse`; a blank or un-parseable string degrades
/// to an empty object so the request body stays well-formed (the prior STJ
/// path threw on invalid input — schemas/arguments the SDK assembles are
/// always valid, so this is only a softer failure mode).
let private embeddedJson (raw: string) : JsonValue =
    if System.String.IsNullOrWhiteSpace raw then
        jobj []
    else
        match JsonHost.parse raw with
        | Some v -> v
        | None -> jobj []

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
    // Sonnet / Opus / Fable families and Haiku 4.5+ support vision, as
    // did the retired haiku-3.5. Only the original haiku-3 family does
    // not. (Refreshed 2026-06-12 alongside KnownModels — haiku-4-5 is
    // the provider default and was previously mis-rejected here.)
    (m.Contains "claude-sonnet" || m.Contains "sonnet")
    || m.Contains "claude-opus"
    || m.Contains "claude-fable"
    || m.Contains "haiku-4"
    || (m.Contains "haiku-3.5" || m.Contains "haiku-3-5")

/// Serialise an `ImagePayload` into Anthropic's `image` content
/// block shape. Base64 sources are emitted as
/// `{ type: "image", source: { type: "base64", media_type, data } }`;
/// URL sources as `{ type: "image", source: { type: "url", url } }`
/// (Anthropic Messages API fetches URLs server-side; the SDK does
/// NOT fetch — that's a deliberate division so the SDK doesn't take
/// on URL-fetch network policy / SSRF concerns).
let private imageBlock (payload: ImagePayload) : JsonValue =
    let source =
        match payload.Source with
        | Base64Bytes bytes ->
            jobj [
                "type", jstr "base64"
                "media_type", jstr payload.MediaType
                "data", jstr (System.Convert.ToBase64String bytes)
            ]
        | Url u -> jobj [ "type", jstr "url"; "url", jstr u ]

    jobj [ "type", jstr "image"; "source", source ]

/// Build the Anthropic content-block array from a non-empty
/// `Parts` list. Text parts become `{ type: "text", text }`; image
/// parts become the `image` block via `imageBlock`. When `markForCache`
/// is true the last block carries a `cache_control` marker.
let private buildMultipartContent (parts: AIContentPart list) (markForCache: bool) : JsonValue =
    let blocks =
        parts
        |> List.map (fun part ->
            match part with
            | TextPart s -> jobj [ "type", jstr "text"; "text", jstr s ]
            | ImagePart payload -> imageBlock payload)

    let blocks =
        if markForCache && not blocks.IsEmpty then
            let lastIdx = blocks.Length - 1
            blocks |> List.mapi (fun i b -> if i = lastIdx then withCacheControl b else b)
        else
            blocks

    jarr blocks

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
let private buildMessageContent (msg: AIProviderMessage) (markForCache: bool) : JsonValue =
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
                let block =
                    jobj [
                        "type", jstr "tool_result"
                        "tool_use_id", jstr tr.ToolCallId
                        "content", jstr tr.Content
                    ]

                if markForCache && i = lastIdx then
                    withCacheControl block
                else
                    block)

        jarr blocks
    elif not msg.ToolCalls.IsEmpty then
        // Case 3 — assistant message with tool_use blocks. Include any text
        // content as a `text` block before the tool_use blocks so the model
        // can still narrate ("I'll load your data…") alongside the calls.
        let textBlocks =
            if System.String.IsNullOrEmpty msg.Content then
                []
            else
                [ jobj [ "type", jstr "text"; "text", jstr msg.Content ] ]

        let toolUseBlocks =
            msg.ToolCalls
            |> List.map (fun tc ->
                jobj [
                    "type", jstr "tool_use"
                    "id", jstr tc.Id
                    "name", jstr tc.Name
                    "input", embeddedJson tc.Arguments
                ])

        let allBlocks = textBlocks @ toolUseBlocks

        let allBlocks =
            if markForCache && not allBlocks.IsEmpty then
                let lastIdx = allBlocks.Length - 1

                allBlocks
                |> List.mapi (fun i b -> if i = lastIdx then withCacheControl b else b)
            else
                allBlocks

        jarr allBlocks
    // Case 1 — simple text message. cache_control requires array form, so
    // promote the string to a single text block carrying the marker when
    // markForCache is true; otherwise emit the legacy string shape.
    elif markForCache then
        jarr [
            jobj [
                "type", jstr "text"
                "text", jstr msg.Content
                "cache_control", cacheControlMarker
            ]
        ]
    else
        jstr msg.Content

/// Build the tool array, marking the last entry with `cache_control` when
/// any tools are present. Anthropic caches the entire tools array as a
/// contiguous prefix from the marker position, so a single marker on the
/// last tool covers all of them.
let private buildTools (tools: AIProviderToolDef list) : JsonValue list =
    let lastIdx = tools.Length - 1

    tools
    |> List.mapi (fun i t ->
        let block =
            jobj [
                "name", jstr t.Name
                "description", jstr t.Description
                "input_schema", embeddedJson t.InputSchema
            ]

        if i = lastIdx then withCacheControl block else block)

/// Phase 67b — fixed name for the synthesised schema-tool used in
/// Claude's tool-based structured-output workaround. Anthropic has no
/// native equivalent to Gemini's `responseSchema` or OpenAI's
/// `response_format: json_schema`; the documented workaround is to
/// define a tool whose `input_schema` is the caller's schema, then
/// force `tool_choice = { type: "tool", name: ... }` so the model is
/// forced to call it. The tool-call's `input` field IS the structured
/// response.
[<Literal>]
let StructuredResponseToolName = "structured_response"

/// Phase 67b follow-up — deterministic repair of the two malformed
/// structured-output shapes Claude models have been observed emitting
/// under the tool-based workaround (Haiku 4.5 / Sonnet 4.6 / Opus 4.8),
/// even with `tool_choice` forcing the schema-tool:
///
///   1. Envelope wrapping — the model reads the schema-tool description
///      literally and returns `{ "input": <response> }` instead of the
///      response object itself.
///   2. String encoding — the response (or the envelope's value) arrives
///      as a JSON-encoded *string* rather than JSON structure.
///
/// Anthropic does not hard-validate tool inputs against `input_schema`,
/// so both shapes parse as syntactically valid tool calls and would
/// otherwise reach callers as broken content. Repairs apply only when
/// unambiguous:
///   - a whole-payload string is replaced by its parsed content only
///     when that content is a JSON object or array;
///   - the `input` envelope is unwrapped only when the payload is a
///     single-key object whose key is `input`, the supplied schema does
///     NOT itself declare a top-level `input` property, and the wrapped
///     value (after un-stringing) is an object or array.
/// Anything else passes through byte-identical, including payloads that
/// fail to parse as JSON at all (callers surface those as schema errors
/// with the original evidence intact).
let normaliseStructuredPayload (schemaJson: string) (payload: string) : string =
    // Does the caller's schema legitimately have a top-level "input"
    // property? If so, a single-key { "input": ... } payload is (or at
    // least may be) the real response — never unwrap it.
    let schemaDeclaresInput =
        match JsonHost.parse schemaJson with
        | Some schema ->
            match schema |> JsonValue.tryField "properties" with
            | Some props -> (props |> JsonValue.tryField "input").IsSome
            | None -> false
        | None -> false

    // A JSON string whose content itself parses as a JSON object or
    // array — the string-encoding disease. Scalar-content strings stay
    // strings (a field value of "123" or "true" is plausibly literal).
    let tryUnstring (v: JsonValue) : JsonValue option =
        match v with
        | JString s ->
            match JsonHost.parse s with
            | Some(JObject _ as inner) -> Some inner
            | Some(JArray _ as inner) -> Some inner
            | _ -> None
        | _ -> None

    match JsonHost.parse payload with
    | None -> payload
    | Some parsed ->
        // Whole payload as a JSON-encoded string.
        let el =
            match tryUnstring parsed with
            | Some inner -> inner
            | None -> parsed

        // Single-key { "input": ... } envelope.
        let el =
            if not schemaDeclaresInput then
                match el with
                | JObject [ ("input", v) ] ->
                    let unwrapped =
                        match tryUnstring v with
                        | Some inner -> inner
                        | None -> v

                    // Only unwrap to JSON structure — a bare scalar under
                    // "input" is more plausibly a (degenerate) legitimate
                    // payload than the envelope disease.
                    match unwrapped with
                    | JObject _
                    | JArray _ -> unwrapped
                    | _ -> el
                | _ -> el
            else
                el

        JsonHost.serialize el

let buildRequestBody
    (model: string)
    (maxTokens: int)
    (messages: AIProviderMessage list)
    (tools: AIProviderToolDef list)
    (systemPrompt: string option)
    (stream: bool)
    (structuredOutputSchema: string option)
    : string =
    // Build the request as an ordered JObject to control which fields are
    // included (Claude rejects null/unknown fields) and the member order
    // (so the serialized bytes are deterministic across hosts).
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
            jobj [ "role", jstr m.Role; "content", buildMessageContent m markForCache ])

    // System prompt as array form so the text block can carry a
    // cache_control marker — caches the static system prompt across
    // every request.
    let systemFields =
        match systemPrompt with
        | Some prompt -> [
            "system",
            jarr [
                jobj [
                    "type", jstr "text"
                    "text", jstr prompt
                    "cache_control", cacheControlMarker
                ]
            ]
          ]
        | None -> []

    // Phase 67b — when a schema is supplied, append the synthesised
    // schema-tool to user tools. The `tool_choice` directive below
    // forces the model to call only the schema-tool, so user tools
    // become unreachable on a structured-output turn (documented
    // limitation — callers should run free-form tool-dispatch turns
    // first, then a final SendStructuredMessage for the structured
    // response).
    let effectiveTools =
        match structuredOutputSchema with
        | Some schema ->
            // Description wording is load-bearing. The previous text said
            // "the `input` argument MUST conform to the supplied JSON
            // Schema" — models took "the `input` argument" literally and
            // wrapped the whole response as `{ "input": <doc> }` (or a
            // JSON-encoded string thereof), and Anthropic does not
            // hard-validate tool inputs against input_schema, so the
            // malformed shape reached callers. Observed across Haiku 4.5,
            // Sonnet 4.6 and Opus 4.8. Do not reintroduce the words
            // "input argument" here.
            let schemaTool: AIProviderToolDef = {
                Name = StructuredResponseToolName
                Description =
                    "Return the structured response. The tool input IS the response object itself "
                    + "and MUST conform to the tool's input schema — top-level fields are the "
                    + "schema's top-level properties. Do NOT wrap the response in any envelope "
                    + "(no outer `input` key). Field values must be JSON structure (objects, "
                    + "arrays, numbers, booleans), never serialised JSON strings."
                InputSchema = schema
            }

            tools @ [ schemaTool ]
        | None -> tools

    let toolDefs = buildTools effectiveTools

    let toolFields = if toolDefs.IsEmpty then [] else [ "tools", jarr toolDefs ]

    // Phase 67b — force the schema-tool when structured output is
    // requested. `disable_parallel_tool_use` keeps the assistant's
    // turn to exactly one tool call so the response parser picks up
    // a single structured-response payload (the model otherwise may
    // emit multiple tool_use blocks in one turn).
    let toolChoiceFields =
        match structuredOutputSchema with
        | Some _ -> [
            "tool_choice",
            jobj [
                "type", jstr "tool"
                "name", jstr StructuredResponseToolName
                "disable_parallel_tool_use", jbool true
            ]
          ]
        | None -> []

    let streamFields = if stream then [ "stream", jbool true ] else []

    jobj (
        [ "model", jstr model; "max_tokens", jint maxTokens; "messages", jarr msgs ]
        @ systemFields
        @ toolFields
        @ toolChoiceFields
        @ streamFields
    )
    |> JsonHost.serialize