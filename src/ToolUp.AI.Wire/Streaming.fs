// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.Wire

open ToolUp.Platform.AI
open ToolUp.Remoting.Json

// ─── Pure SSE stream assembly ────────────────────────────────────
//
// A provider's streaming response assembly — accumulate text deltas,
// stitch tool-call argument fragments, capture token usage from the
// terminal events — is a stateful protocol. The host owns the SSE read
// loop (it cannot ride a portable string-body record — see
// `IHttpTransport`); the *assembly* is pure data-in/data-out and lives in
// the Fable-safe wire tier, so it is unit-tested without a socket and a
// browser host reuses the identical logic.
//
// Each vendor's assembly is a `StreamProcessor<'S>`: an initial state, a
// `(state, line) -> (state', text)` step and a finaliser — immutable
// records and string concatenation, parsing each chunk through
// `JsonHost.parse`, so the same source compiles to both hosts (GP 8 /
// GP 12). Three vendors live here: Anthropic, OpenAI (and every
// OpenAI-compatible endpoint) and Gemini.

/// One vendor's stream assembly as a pure fold over the SSE lines.
type StreamProcessor<'S> = {
    /// The state before the first line.
    Initial: 'S
    /// Apply one SSE line; returns the new state and any text to forward.
    Step: 'S -> string -> 'S * string option
    /// Collapse the state into the response once the stream ends, with any
    /// text the end of the stream releases.
    Finish: 'S -> string option * AIProviderResponse
}

module StreamProcessor =
    /// The payload of an SSE `data: ` line, or `None` for any other line
    /// and for the `[DONE]` sentinel.
    let dataPayload (line: string) : string option =
        if isNull line || not (line.StartsWith "data: ") then
            None
        else
            match line.Substring 6 with
            | "[DONE]" -> None
            | data -> Some data

    /// A processor over `data:` payloads: every other line passes the state
    /// through, and the end of the stream releases no text.
    let overData
        (initial: 'S)
        (step: 'S -> string -> 'S * string option)
        (finish: 'S -> AIProviderResponse)
        : StreamProcessor<'S> =
        {
            Initial = initial
            Step =
                fun state line ->
                    match dataPayload line with
                    | Some data -> step state data
                    | None -> state, None
            Finish = fun state -> None, finish state
        }

    /// Fold `lines` through `processor`: the forwarded text, in order, and
    /// the response — what a host's read loop does, without the socket.
    let replay (processor: StreamProcessor<'S>) (lines: string list) : string list * AIProviderResponse =
        let state, emitted =
            lines
            |> List.fold
                (fun (state, emitted) line ->
                    let next, text = processor.Step state line
                    next, (Option.toList text) @ emitted)
                (processor.Initial, [])

        let trailing, response = processor.Finish state
        List.rev ((Option.toList trailing) @ emitted), response

/// Claude (Anthropic Messages API) streaming assembly.
///
/// Anthropic's SSE protocol opens a content block (`content_block_start`),
/// streams its deltas (`content_block_delta` — `text_delta` for prose,
/// `input_json_delta` for a tool call's `input` JSON), then closes it
/// (`content_block_stop`) before the next block starts — so at most one
/// tool-use block is active at a time and `input_json_delta` fragments
/// always append onto the LAST opened tool call. Token usage arrives split:
/// input/cache counts on `message_start.message.usage`, the cumulative
/// `output_tokens` on the terminal `message_delta.usage`; `stop_reason`
/// rides `message_delta.delta`.
module ClaudeStreaming =

    /// The accumulator threaded through the read loop. Immutable — each
    /// chunk produces a new value (`processData`), so the machine is a pure
    /// fold over the chunk sequence and a test can assert the final state
    /// without any I/O.
    ///
    /// The usage fields mirror the split-event shape: `*Tokens` carry the
    /// running counts and `UsageSeen` / `CacheCreationSeen` record whether
    /// the corresponding event was observed, so `finalize` can distinguish
    /// "0 cache-creation tokens reported" from "no usage event at all".
    type StreamState = {
        Content: string
        ToolCalls: AIProviderToolCall list
        StopReason: StopReason
        InputTokens: int
        CacheReadTokens: int
        CacheCreationTokens: int
        CacheCreationSeen: bool
        OutputTokens: int
        UsageSeen: bool
    }

    /// The empty accumulator — `StopReason` defaults to `EndTurn`, the
    /// same default the non-streaming parser uses when a response omits it.
    let initial: StreamState = {
        Content = ""
        ToolCalls = []
        StopReason = StopReason.EndTurn
        InputTokens = 0
        CacheReadTokens = 0
        CacheCreationTokens = 0
        CacheCreationSeen = false
        OutputTokens = 0
        UsageSeen = false
    }

    /// Read an integer object member, defaulting to 0 when absent or
    /// non-numeric (matches the non-streaming `parseUsage` helper).
    let private getInt (name: string) (obj: JsonValue) : int =
        obj
        |> JsonValue.tryMember name
        |> Option.bind JsonValue.asInt
        |> Option.defaultValue 0

    /// Apply one SSE `data:` payload (the JSON after the `data: ` prefix,
    /// never the `[DONE]` sentinel — `StreamProcessor.overData` strips both) to the
    /// accumulator. Returns the new state and, for a `text_delta`, the text
    /// to forward to the live `onStream` callback (`None` for every other
    /// event). A chunk that fails to parse is swallowed — the state passes
    /// through unchanged — exactly as the inline egress did (`with _ -> ()`).
    let processData (state: StreamState) (data: string) : StreamState * string option =
        match JsonHost.parse data with
        | None -> state, None
        | Some root ->
            match root |> JsonValue.tryMember "type" |> Option.bind JsonValue.asString with
            | Some "content_block_delta" ->
                match root |> JsonValue.tryMember "delta" with
                | None -> state, None
                | Some delta ->
                    match delta |> JsonValue.tryMember "type" |> Option.bind JsonValue.asString with
                    | Some "text_delta" ->
                        match delta |> JsonValue.tryMember "text" |> Option.bind JsonValue.asString with
                        | Some t ->
                            {
                                state with
                                    Content = state.Content + t
                            },
                            Some t
                        | None -> state, None
                    | Some "input_json_delta" ->
                        // Append `partial_json` onto the last opened tool
                        // call's Arguments buffer. Correct because Anthropic
                        // keeps at most one tool-use block active at a time.
                        match delta |> JsonValue.tryMember "partial_json" |> Option.bind JsonValue.asString with
                        | Some pj when not state.ToolCalls.IsEmpty ->
                            let lastIdx = state.ToolCalls.Length - 1

                            let updated =
                                state.ToolCalls
                                |> List.mapi (fun i tc ->
                                    if i = lastIdx then
                                        {
                                            tc with
                                                Arguments = tc.Arguments + pj
                                        }
                                    else
                                        tc)

                            { state with ToolCalls = updated }, None
                        | _ -> state, None
                    | _ -> state, None
            | Some "content_block_start" ->
                // A new tool-use block opens with its id + name; the
                // Arguments buffer starts empty and the `input_json_delta`
                // chunks above fill it. An empty buffer is defaulted to "{}"
                // by `finalize` (a zero-input tool call emits no deltas).
                match root |> JsonValue.tryMember "content_block" with
                | None -> state, None
                | Some cb ->
                    match cb |> JsonValue.tryMember "type" |> Option.bind JsonValue.asString with
                    | Some "tool_use" ->
                        let id =
                            cb
                            |> JsonValue.tryMember "id"
                            |> Option.bind JsonValue.asString
                            |> Option.defaultValue ""

                        let name =
                            cb
                            |> JsonValue.tryMember "name"
                            |> Option.bind JsonValue.asString
                            |> Option.defaultValue ""

                        let added: AIProviderToolCall = { Id = id; Name = name; Arguments = "" }

                        {
                            state with
                                ToolCalls = state.ToolCalls @ [ added ]
                        },
                        None
                    | _ -> state, None
            | Some "message_start" ->
                // Initial usage: input/cache counts + the role-marker
                // output_tokens (later replaced by the cumulative value).
                match
                    root
                    |> JsonValue.tryMember "message"
                    |> Option.bind (JsonValue.tryMember "usage")
                with
                | None -> state, None
                | Some usage ->
                    let cacheCreationOpt =
                        usage
                        |> JsonValue.tryMember "cache_creation_input_tokens"
                        |> Option.bind JsonValue.asInt

                    {
                        state with
                            InputTokens = getInt "input_tokens" usage
                            CacheReadTokens = getInt "cache_read_input_tokens" usage
                            CacheCreationTokens = defaultArg cacheCreationOpt state.CacheCreationTokens
                            CacheCreationSeen = cacheCreationOpt.IsSome || state.CacheCreationSeen
                            OutputTokens = getInt "output_tokens" usage
                            UsageSeen = true
                    },
                    None
            | Some "message_delta" ->
                // stop_reason on `delta`; cumulative output_tokens on
                // `usage` (input/cache are stable from message_start).
                let state =
                    match
                        root
                        |> JsonValue.tryMember "delta"
                        |> Option.bind (JsonValue.tryMember "stop_reason")
                        |> Option.bind JsonValue.asString
                    with
                    | Some sr -> {
                        state with
                            StopReason = StopReason.ofToken sr
                      }
                    | None -> state

                let state =
                    match
                        root
                        |> JsonValue.tryMember "usage"
                        |> Option.bind (JsonValue.tryMember "output_tokens")
                        |> Option.bind JsonValue.asInt
                    with
                    | Some o -> {
                        state with
                            OutputTokens = o
                            UsageSeen = true
                      }
                    | None -> state

                state, None
            | _ -> state, None

    /// Collapse the accumulator into the provider-neutral response. Empty
    /// tool-call Arguments default to "{}" so a zero-input tool call
    /// deserialises as an empty object rather than failing with "input does
    /// not contain any JSON tokens". `Usage` is `Some` only when a usage
    /// event was observed.
    let finalize (state: StreamState) : AIProviderResponse =
        let fixedToolCalls =
            state.ToolCalls
            |> List.map (fun tc ->
                if System.String.IsNullOrWhiteSpace tc.Arguments then
                    { tc with Arguments = "{}" }
                else
                    tc)

        let usage =
            if state.UsageSeen then
                Some {
                    PromptTokens = state.InputTokens + state.CacheReadTokens + state.CacheCreationTokens
                    CachedPromptTokens = state.CacheReadTokens
                    OutputTokens = state.OutputTokens
                    CacheCreationTokens =
                        (if state.CacheCreationSeen then
                             Some state.CacheCreationTokens
                         else
                             None)
                }
            else
                None

        {
            Content = state.Content
            ToolCalls = fixedToolCalls
            StopReason = state.StopReason
            Usage = usage
        }

    /// Anthropic's stream as a `StreamProcessor`.
    let processor: StreamProcessor<StreamState> =
        StreamProcessor.overData initial processData finalize

/// OpenAI chat/completions streaming assembly (OpenAI itself and every
/// OpenAI-compatible endpoint, Azure OpenAI included).
///
/// Each chunk's `choices[0].delta` appends to the assistant message; a tool
/// call arrives as an `index` plus partial fields, its `id` and
/// `function.name` on the first chunk and `function.arguments` fragments
/// after. With `stream_options.include_usage` (always requested) the last
/// chunk before `[DONE]` carries empty `choices` and the root `usage`.
module OpenAIStreaming =

    /// Read OpenAI's `usage` block. Prompt caching is automatic; the cached
    /// portion is `usage.prompt_tokens_details.cached_tokens`. OpenAI reports
    /// no separate cache-write count, so `CacheCreationTokens` is `None`.
    let parseUsage (root: JsonValue) : TokenUsage option =
        match JsonValue.tryMember "usage" root with
        | Some usage ->
            let getInt (name: string) =
                usage
                |> JsonValue.tryMember name
                |> Option.bind JsonValue.asInt
                |> Option.defaultValue 0

            let cachedTokens =
                usage
                |> JsonValue.tryMember "prompt_tokens_details"
                |> Option.bind (JsonValue.tryMember "cached_tokens")
                |> Option.bind JsonValue.asInt
                |> Option.defaultValue 0

            Some {
                PromptTokens = getInt "prompt_tokens"
                CachedPromptTokens = cachedTokens
                OutputTokens = getInt "completion_tokens"
                CacheCreationTokens = None
            }
        | None -> None

    /// Decode OpenAI's `finish_reason`: `tool_calls` and `length` are named;
    /// `stop` and every other reason end the turn.
    let stopReason (finishReason: string) : StopReason =
        match finishReason with
        | "tool_calls" -> StopReason.ToolUse
        | "length" -> StopReason.MaxTokens
        | _ -> StopReason.EndTurn

    /// The accumulator threaded through the read loop.
    type StreamState = {
        /// The text so far.
        Content: string
        /// The tool calls so far, their arguments still being stitched.
        ToolCalls: AIProviderToolCall list
        /// The latest finish reason seen.
        StopReason: StopReason
        /// The usage chunk, once it arrives.
        Usage: TokenUsage option
    }

    /// The state before the first chunk.
    let initial: StreamState = {
        Content = ""
        ToolCalls = []
        StopReason = StopReason.EndTurn
        Usage = None
    }

    let private emptyCall: AIProviderToolCall = { Id = ""; Name = ""; Arguments = "" }

    /// Apply one tool-call delta to the calls so far.
    let private applyToolCall (calls: AIProviderToolCall list) (tc: JsonValue) : AIProviderToolCall list =
        let index =
            tc
            |> JsonValue.tryMember "index"
            |> Option.bind JsonValue.asInt
            |> Option.defaultValue 0

        if index < 0 then
            calls
        else
            let fn = tc |> JsonValue.tryMember "function"
            let id = tc |> JsonValue.tryMember "id" |> Option.bind JsonValue.asString

            let name =
                fn |> Option.bind (JsonValue.tryMember "name") |> Option.bind JsonValue.asString

            let argsFragment =
                fn
                |> Option.bind (JsonValue.tryMember "arguments")
                |> Option.bind JsonValue.asString
                |> Option.defaultValue ""

            let padded =
                if calls.Length > index then
                    calls
                else
                    calls @ List.replicate (index + 1 - calls.Length) emptyCall

            padded
            |> List.mapi (fun i current ->
                if i <> index then
                    current
                else
                    {
                        Id = defaultArg id current.Id
                        Name = defaultArg name current.Name
                        Arguments = current.Arguments + argsFragment
                    })

    /// Apply one `data:` payload. A chunk that does not parse passes the
    /// state through, so one bad frame cannot abort the stream.
    let processData (state: StreamState) (data: string) : StreamState * string option =
        match JsonHost.parse data with
        | None -> state, None
        | Some root ->
            let state =
                match parseUsage root with
                | Some usage -> { state with Usage = Some usage }
                | None -> state

            match root |> JsonValue.tryMember "choices" |> Option.bind (JsonValue.tryItem 0) with
            | None -> state, None
            | Some choice ->
                let delta = choice |> JsonValue.tryMember "delta"

                let text =
                    delta
                    |> Option.bind (JsonValue.tryMember "content")
                    |> Option.bind JsonValue.asString
                    |> Option.filter (fun t -> t <> "")

                let toolCalls =
                    match
                        delta
                        |> Option.bind (JsonValue.tryMember "tool_calls")
                        |> Option.bind JsonValue.asArray
                    with
                    | Some tcs -> tcs |> Array.fold applyToolCall state.ToolCalls
                    | None -> state.ToolCalls

                let stop =
                    match choice |> JsonValue.tryMember "finish_reason" |> Option.bind JsonValue.asString with
                    | Some reason -> stopReason reason
                    | None -> state.StopReason

                {
                    state with
                        Content = state.Content + defaultArg text ""
                        ToolCalls = toolCalls
                        StopReason = stop
                },
                text

    /// Collapse the accumulator into the response.
    let finalize (state: StreamState) : AIProviderResponse = {
        Content = state.Content
        ToolCalls = state.ToolCalls
        StopReason = state.StopReason
        Usage = state.Usage
    }

    /// OpenAI's stream as a `StreamProcessor`.
    let processor: StreamProcessor<StreamState> =
        StreamProcessor.overData initial processData finalize

/// Gemini `streamGenerateContent?alt=sse` assembly.
///
/// Each `data:` line is a JSON object shaped like the non-streaming response
/// (`candidates[0].content.parts[]`, an optional `finishReason`, optional
/// `usageMetadata`) carrying a partial set of parts that append. A function
/// call arrives whole in one chunk. There is no `[DONE]` sentinel.
module GeminiStreaming =

    /// Gemini's `functionCall` carries no id; the SDK contract needs one, so
    /// one is synthesised from the call's name and position:
    /// `gemini-fc-<name>-<index>`.
    let syntheticToolCallId (name: string) (index: int) : string = sprintf "gemini-fc-%s-%d" name index

    /// Read Gemini's `usageMetadata`: `promptTokenCount` (cached portion
    /// included), `cachedContentTokenCount`, `candidatesTokenCount`. The
    /// response reports no cache-write count, so `CacheCreationTokens` is `None`.
    let parseUsage (root: JsonValue) : TokenUsage option =
        match JsonValue.tryMember "usageMetadata" root with
        | Some usage ->
            let getInt (name: string) =
                usage
                |> JsonValue.tryMember name
                |> Option.bind JsonValue.asInt
                |> Option.defaultValue 0

            Some {
                PromptTokens = getInt "promptTokenCount"
                CachedPromptTokens = getInt "cachedContentTokenCount"
                OutputTokens = getInt "candidatesTokenCount"
                CacheCreationTokens = None
            }
        | None -> None

    /// Decode Gemini's `finishReason`: `MAX_TOKENS` is named; `STOP` and every
    /// other reason end the turn. A turn carrying a function call is
    /// `ToolUse` whatever Gemini said; the callers apply that rule.
    let stopReason (finishReason: string) : StopReason =
        match finishReason with
        | "MAX_TOKENS" -> StopReason.MaxTokens
        | _ -> StopReason.EndTurn

    /// The accumulator threaded through the read loop.
    type StreamState = {
        /// The text so far.
        Content: string
        /// The tool calls so far, their arguments still being stitched.
        ToolCalls: AIProviderToolCall list
        /// The latest finish reason seen.
        StopReason: StopReason
        /// The usage chunk, once it arrives.
        Usage: TokenUsage option
    }

    /// The state before the first chunk.
    let initial: StreamState = {
        Content = ""
        ToolCalls = []
        StopReason = StopReason.EndTurn
        Usage = None
    }

    /// Apply one part: text appends (and is forwarded), a function call
    /// becomes the next tool call.
    let private applyPart (state: StreamState, emitted: string) (part: JsonValue) : StreamState * string =
        match part |> JsonValue.tryMember "text" |> Option.bind JsonValue.asString with
        | Some text when text <> "" ->
            {
                state with
                    Content = state.Content + text
            },
            emitted + text
        | Some _ -> state, emitted
        | None ->
            match part |> JsonValue.tryMember "functionCall" with
            | None -> state, emitted
            | Some fc ->
                let name =
                    fc
                    |> JsonValue.tryMember "name"
                    |> Option.bind JsonValue.asString
                    |> Option.defaultValue ""

                let args =
                    fc
                    |> JsonValue.tryMember "args"
                    |> Option.map JsonHost.serialize
                    |> Option.defaultValue "{}"

                let call: AIProviderToolCall = {
                    Id = syntheticToolCallId name state.ToolCalls.Length
                    Name = name
                    Arguments = args
                }

                {
                    state with
                        ToolCalls = state.ToolCalls @ [ call ]
                },
                emitted

    /// Apply one parsed chunk. A non-streaming `generateContent` response is
    /// one chunk carrying the whole turn, so its parse is this step from
    /// `initial` (a turn with a function call is `ToolUse` there even with
    /// no `finishReason`).
    let applyChunk (state: StreamState) (root: JsonValue) : StreamState * string option =
        let state =
            match parseUsage root with
            | Some usage -> { state with Usage = Some usage }
            | None -> state

        match root |> JsonValue.tryMember "candidates" |> Option.bind (JsonValue.tryItem 0) with
        | None -> state, None
        | Some candidate ->
            let parts =
                candidate
                |> JsonValue.tryMember "content"
                |> Option.bind (JsonValue.tryMember "parts")
                |> Option.bind JsonValue.asArray
                |> Option.defaultValue [||]

            let state, emitted = parts |> Array.fold applyPart (state, "")

            let state =
                match
                    candidate
                    |> JsonValue.tryMember "finishReason"
                    |> Option.bind JsonValue.asString
                with
                | Some reason -> {
                    state with
                        StopReason =
                            if state.ToolCalls.IsEmpty then
                                stopReason reason
                            else
                                StopReason.ToolUse
                  }
                | None -> state

            state, (if emitted = "" then None else Some emitted)

    /// Apply one `data:` payload. A blank or unparseable chunk passes the
    /// state through.
    let processData (state: StreamState) (data: string) : StreamState * string option =
        if System.String.IsNullOrWhiteSpace data then
            state, None
        else
            match JsonHost.parse data with
            | None -> state, None
            | Some root -> applyChunk state root

    /// Collapse the accumulator into the response.
    let finalize (state: StreamState) : AIProviderResponse = {
        Content = state.Content
        ToolCalls = state.ToolCalls
        StopReason = state.StopReason
        Usage = state.Usage
    }

    /// Gemini's stream as a `StreamProcessor`.
    let processor: StreamProcessor<StreamState> =
        StreamProcessor.overData initial processData finalize