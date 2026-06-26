// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI.Wire

open ToolUp.Platform.AI

// ─── Pure SSE streaming state machine (Wave 32, Phase 254) ───────
//
// A provider's streaming response assembly — accumulate text deltas,
// stitch tool-call argument fragments, capture token usage from the
// terminal events — is a stateful protocol. The host owns the SSE read
// loop (it cannot ride a portable string-body record — see
// `IHttpTransport`); but the *assembly* is pure data-in/data-out and
// belongs in the Fable-safe wire tier so it can be unit-tested without a
// live socket and so a browser host reuses the identical logic.
//
// This module hosts the per-provider stream processors. Each is a pure,
// deterministic `(state, chunk) -> (state', emitted)` step plus a
// `state -> AIProviderResponse` finalizer: immutable records + string
// concatenation, parsing each chunk through `JsonHost.parse` (no
// `System.Text.Json`), so the same source compiles to both hosts (GP 8 /
// GP 12). Claude lands here first (Phase 254); OpenAI's `chunk -> delta`
// parser folds into the same file in a follow-up.

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
        StopReason: string
        InputTokens: int
        CacheReadTokens: int
        CacheCreationTokens: int
        CacheCreationSeen: bool
        OutputTokens: int
        UsageSeen: bool
    }

    /// The empty accumulator — `StopReason` defaults to `"end_turn"`, the
    /// same default the non-streaming parser uses when a response omits it.
    let initial: StreamState = {
        Content = ""
        ToolCalls = []
        StopReason = "end_turn"
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
        |> JsonValue.tryField name
        |> Option.bind JsonValue.asInt
        |> Option.defaultValue 0

    /// Apply one SSE `data:` payload (the JSON after the `data: ` prefix,
    /// never the `[DONE]` sentinel — the host strips both) to the
    /// accumulator. Returns the new state and, for a `text_delta`, the text
    /// to forward to the live `onStream` callback (`None` for every other
    /// event). A chunk that fails to parse is swallowed — the state passes
    /// through unchanged — exactly as the inline egress did (`with _ -> ()`).
    let processData (state: StreamState) (data: string) : StreamState * string option =
        match JsonHost.parse data with
        | None -> state, None
        | Some root ->
            match root |> JsonValue.tryField "type" |> Option.bind JsonValue.asString with
            | Some "content_block_delta" ->
                match root |> JsonValue.tryField "delta" with
                | None -> state, None
                | Some delta ->
                    match delta |> JsonValue.tryField "type" |> Option.bind JsonValue.asString with
                    | Some "text_delta" ->
                        match delta |> JsonValue.tryField "text" |> Option.bind JsonValue.asString with
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
                        match delta |> JsonValue.tryField "partial_json" |> Option.bind JsonValue.asString with
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
                match root |> JsonValue.tryField "content_block" with
                | None -> state, None
                | Some cb ->
                    match cb |> JsonValue.tryField "type" |> Option.bind JsonValue.asString with
                    | Some "tool_use" ->
                        let id =
                            cb
                            |> JsonValue.tryField "id"
                            |> Option.bind JsonValue.asString
                            |> Option.defaultValue ""

                        let name =
                            cb
                            |> JsonValue.tryField "name"
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
                match root |> JsonValue.tryField "message" |> Option.bind (JsonValue.tryField "usage") with
                | None -> state, None
                | Some usage ->
                    let cacheCreationOpt =
                        usage
                        |> JsonValue.tryField "cache_creation_input_tokens"
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
                        |> JsonValue.tryField "delta"
                        |> Option.bind (JsonValue.tryField "stop_reason")
                        |> Option.bind JsonValue.asString
                    with
                    | Some sr -> { state with StopReason = sr }
                    | None -> state

                let state =
                    match
                        root
                        |> JsonValue.tryField "usage"
                        |> Option.bind (JsonValue.tryField "output_tokens")
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