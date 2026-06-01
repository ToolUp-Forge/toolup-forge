module ClaudeAIProvider

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform // RetryPolicy (Phase 11.C.5 Tier 3 — unified)
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open ClaudeAIProviderWire

// ─── Provider implementation ─────────────────────────────────────

/// Default Claude model. Exposed so the factory path (and the provider
/// descriptor in DefaultAIProviderFactory) can share the same constant.
///
/// Defaulting to Haiku rather than Sonnet — Anthropic's API rate limits
/// are materially higher for Haiku at every account tier, which keeps
/// development and demo deployments unblocked without an API tier
/// upgrade. Users who want Sonnet / Opus override per-user via the AI
/// settings panel (`IProviderProfile`) or per-conversation via the
/// model picker; both overrides take precedence over this default.
[<Literal>]
let DefaultModel = "claude-haiku-4-5-20251001"

/// Default response token cap. Was hardcoded inside the request
/// builder; promoted to a named default + optional constructor arg so
/// deployments that need longer completions (or want to cap spend
/// tighter) can tune it without forking the provider.
[<Literal>]
let DefaultMaxTokens = 4096

/// Known-good model identifiers surfaced by the Phase D settings UI.
/// Users may enter custom strings for models released after the build.
let KnownModels = [
    "claude-haiku-4-5-20251001"
    "claude-sonnet-4-20250514"
    "claude-opus-4-20250514"
    "claude-haiku-3-5-20241022"
]

// API-key fetching is abstracted to a thunk so the same core
// implementation serves both the legacy secret-store path (fetch on
// every request — supports rotation) and the Phase-A factory path
// (fetch once at construction — per-user BYOK). Each fetch call inside
// SendMessage re-executes the thunk; the thunk decides whether to hit
// a store or return a captured value.
type ClaudeAIProvider private (apiKeyFetcher: unit -> Async<string option>, model: string, maxTokens: int) =
    let client = new HttpClient()

    // Guard a misconfigured cap. <= 0 would make the Anthropic API
    // reject every request; fall back to the safe default rather than
    // hard-fail every call.
    let maxTokens = if maxTokens > 0 then maxTokens else DefaultMaxTokens

    do
        client.BaseAddress <- Uri("https://api.anthropic.com")
        client.Timeout <- TimeSpan.FromMinutes(5.0)

    /// Construct with an API key provided directly. Phase A+ factory
    /// path used by BYOK deployments — the factory resolves the user's
    /// or team's stored key and builds this provider once per Resolve.
    /// `maxTokens` defaults to `DefaultMaxTokens` (4096).
    new(apiKey: string, ?model: string, ?maxTokens: int) =
        ClaudeAIProvider(
            (fun () -> async { return Some apiKey }),
            defaultArg model DefaultModel,
            defaultArg maxTokens DefaultMaxTokens
        )

    /// Construct reading the API key from the secret store on every
    /// request. Legacy pre-Phase-A path; reads `ANTHROPIC_API_KEY` from
    /// the `_platform` scope and supports key rotation without
    /// reconstruction. `maxTokens` defaults to `DefaultMaxTokens` (4096).
    new(secretStore: ISecretStore, ?model: string, ?maxTokens: int) =
        ClaudeAIProvider(
            (fun () -> secretStore.GetSecret("_platform", "ANTHROPIC_API_KEY")),
            defaultArg model DefaultModel,
            defaultArg maxTokens DefaultMaxTokens
        )

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = true
            ToolUse = true
            Vision = true
            // Phase 6i.B: cache_control markers on system + tools +
            // history; usage block parsed on both streaming and
            // non-streaming paths.
            SupportsPromptCaching = true
            ProviderName = "anthropic-claude"
            Model = model
        }

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            // Phase 6o — short-circuit on multimodal content against a
            // non-vision-capable model. Cheaper failure than letting
            // Anthropic return HTTP 400, and avoids shipping
            // potentially large / PII image bytes over the wire to a
            // model that would reject them anyway.
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (ClaudeAIProviderWire.isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else

                let! apiKey = apiKeyFetcher ()

                match apiKey with
                | None ->
                    // No API key is a deployment misconfiguration — fail-fast,
                    // not retry-worthy.
                    return
                        Error(
                            PermanentClient(
                                0,
                                "ANTHROPIC_API_KEY not configured. Set it as an environment variable or in your secret store."
                            )
                        )
                | Some key ->
                    let useStreaming = onStream.IsSome

                    // One attempt — whole request lifecycle, including the
                    // per-call timeout and HTTP status classification. Returns
                    // a Result so the retry loop can classify before deciding
                    // to back off or propagate.
                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        // Build a fresh request per attempt — HttpRequestMessage
                        // cannot be reused after being sent.
                        let body = buildRequestBody model maxTokens messages tools systemPrompt useStreaming
                        let request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
                        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                        request.Headers.Add("x-api-key", key)
                        request.Headers.Add("anthropic-version", "2023-06-01")

                        // Per-call timeout. HttpClient.Timeout is instance-wide
                        // (set to 5 min in the constructor); RetryPolicy.Timeout
                        // allows the caller to impose a tighter per-request budget.
                        use cts =
                            match retryPolicy.Timeout with
                            | Some t ->
                                let clampedMs = RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)
                                new CancellationTokenSource(TimeSpan.FromMilliseconds(float clampedMs))
                            | None -> new CancellationTokenSource()

                        if useStreaming then
                            // Streaming path: accumulate partial text so that if
                            // the stream aborts mid-delivery we can report what
                            // the client already received. Streaming is NEVER
                            // retried — see StreamingAborted doc in IAIProvider.fs.
                            let mutable accumulated = ""

                            try
                                let! response =
                                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                    |> Async.AwaitTask

                                if not response.IsSuccessStatusCode then
                                    let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                                    let code = int response.StatusCode

                                    // Status-code classification applies to
                                    // streaming requests that fail BEFORE any
                                    // content is delivered (no partial output
                                    // yet, so TransientServer/PermanentClient
                                    // still apply — retry budget still meaningful).
                                    return
                                        if code = 429 || code >= 500 then
                                            Error(TransientServer(code, errorBody))
                                        else
                                            Error(PermanentClient(code, errorBody))
                                else
                                    let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                                    use reader = new StreamReader(stream)

                                    let mutable fullText = ""
                                    let mutable toolCalls: AIProviderToolCall list = []
                                    let mutable stopReason = "end_turn"
                                    let mutable reading = true

                                    // Phase 6i.B usage tracking. Anthropic's
                                    // streaming protocol reports input/cache
                                    // tokens on `message_start.message.usage`
                                    // (initial values; output_tokens starts at
                                    // 1 for the assistant role marker) and
                                    // updates the cumulative output_tokens on
                                    // `message_delta.usage`. We accumulate
                                    // mutables and build the final TokenUsage
                                    // after the stream closes.
                                    let mutable usageSeen = false
                                    let mutable inputTokens = 0
                                    let mutable cacheReadTokens = 0
                                    let mutable cacheCreationTokens = 0
                                    let mutable cacheCreationSeen = false
                                    let mutable outputTokens = 0

                                    while reading do
                                        // Pass cts.Token so the per-call timeout
                                        // (RetryPolicy.TimeoutMs) actually cancels
                                        // a stalled stream. Without the token,
                                        // ReadLineAsync ignores the CTS — a hung
                                        // Anthropic stream waits until
                                        // HttpClient.Timeout (5 min instance-wide)
                                        // and the agent loop never surfaces the
                                        // failure as AITaskFailed in time. The
                                        // ValueTask-returning overload landed in
                                        // .NET 7; net10 has it.
                                        let! line = reader.ReadLineAsync(cts.Token).AsTask() |> Async.AwaitTask

                                        if isNull line then
                                            reading <- false
                                        else
                                            parseStreamLine line onStream

                                            if line.StartsWith("data: ") then
                                                let data = line.Substring(6)

                                                if data <> "[DONE]" then
                                                    try
                                                        let doc = JsonDocument.Parse(data)
                                                        let root = doc.RootElement

                                                        match root.TryGetProperty("type") with
                                                        | true, t when t.GetString() = "content_block_delta" ->
                                                            // Handle BOTH delta types in a single arm —
                                                            // previously there were two separate match
                                                            // arms with identical patterns, which meant
                                                            // F# picked the first (text_delta only) and
                                                            // the second (input_json_delta) was dead code.
                                                            // The consequence was that every tool call's
                                                            // arguments silently vanished during
                                                            // streaming — Claude sent the JSON chunks but
                                                            // the parser never saw them, so tool handlers
                                                            // received empty input and failed with
                                                            // "key not present" errors on required
                                                            // arguments. Keep both handlers here,
                                                            // dispatched on the inner `delta.type`.
                                                            match root.TryGetProperty("delta") with
                                                            | true, delta ->
                                                                match delta.TryGetProperty("type") with
                                                                | true, dt when dt.GetString() = "text_delta" ->
                                                                    match delta.TryGetProperty("text") with
                                                                    | true, txt ->
                                                                        let t = txt.GetString()
                                                                        fullText <- fullText + t
                                                                        accumulated <- accumulated + t
                                                                    | _ -> ()
                                                                | true, dt when dt.GetString() = "input_json_delta" ->
                                                                    // Append partial_json onto the last
                                                                    // tool call's Arguments buffer. The
                                                                    // last tool call is correct when
                                                                    // there is at most one active tool
                                                                    // content block at a time — which
                                                                    // matches Anthropic's streaming
                                                                    // protocol: a content_block_start
                                                                    // opens a block, its deltas stream
                                                                    // in, a content_block_stop closes
                                                                    // it, and only then does the next
                                                                    // content_block_start fire.
                                                                    match delta.TryGetProperty("partial_json") with
                                                                    | true, pj ->
                                                                        match toolCalls with
                                                                        | [] -> ()
                                                                        | _ ->
                                                                            let last = toolCalls[toolCalls.Length - 1]

                                                                            toolCalls <-
                                                                                toolCalls[.. toolCalls.Length - 2]
                                                                                @ [
                                                                                    {
                                                                                        last with
                                                                                            Arguments =
                                                                                                last.Arguments
                                                                                                + pj.GetString()
                                                                                    }
                                                                                ]
                                                                    | _ -> ()
                                                                | _ -> ()
                                                            | _ -> ()

                                                        | true, t when t.GetString() = "message_start" ->
                                                            // Initial usage on `message.usage`. Anthropic
                                                            // emits input/cache counts here at stream start;
                                                            // output_tokens starts at 1 (the assistant role
                                                            // marker) and is replaced by the cumulative
                                                            // value on the final `message_delta`.
                                                            match root.TryGetProperty("message") with
                                                            | true, msg ->
                                                                match msg.TryGetProperty("usage") with
                                                                | true, usage ->
                                                                    let getInt (name: string) =
                                                                        match usage.TryGetProperty(name) with
                                                                        | true, v when
                                                                            v.ValueKind = JsonValueKind.Number
                                                                            ->
                                                                            v.GetInt32()
                                                                        | _ -> 0

                                                                    inputTokens <- getInt "input_tokens"
                                                                    cacheReadTokens <- getInt "cache_read_input_tokens"

                                                                    match
                                                                        usage.TryGetProperty(
                                                                            "cache_creation_input_tokens"
                                                                        )
                                                                    with
                                                                    | true, v when v.ValueKind = JsonValueKind.Number ->
                                                                        cacheCreationTokens <- v.GetInt32()
                                                                        cacheCreationSeen <- true
                                                                    | _ -> ()

                                                                    outputTokens <- getInt "output_tokens"
                                                                    usageSeen <- true
                                                                | _ -> ()
                                                            | _ -> ()

                                                        | true, t when t.GetString() = "message_delta" ->
                                                            match root.TryGetProperty("delta") with
                                                            | true, delta ->
                                                                match delta.TryGetProperty("stop_reason") with
                                                                | true, sr when sr.ValueKind <> JsonValueKind.Null ->
                                                                    stopReason <- sr.GetString()
                                                                | _ -> ()
                                                            | _ -> ()

                                                            // Cumulative output_tokens on `message_delta.usage`.
                                                            // Replaces the start-of-stream value with the final
                                                            // total. Anthropic does not re-report input/cache
                                                            // tokens here (they are stable from message_start).
                                                            match root.TryGetProperty("usage") with
                                                            | true, usage ->
                                                                match usage.TryGetProperty("output_tokens") with
                                                                | true, v when v.ValueKind = JsonValueKind.Number ->
                                                                    outputTokens <- v.GetInt32()
                                                                    usageSeen <- true
                                                                | _ -> ()
                                                            | _ -> ()

                                                        | true, t when t.GetString() = "content_block_start" ->
                                                            match root.TryGetProperty("content_block") with
                                                            | true, cb ->
                                                                match cb.TryGetProperty("type") with
                                                                | true, cbt when cbt.GetString() = "tool_use" ->
                                                                    let id =
                                                                        match cb.TryGetProperty("id") with
                                                                        | true, v -> v.GetString()
                                                                        | _ -> ""

                                                                    let name =
                                                                        match cb.TryGetProperty("name") with
                                                                        | true, v -> v.GetString()
                                                                        | _ -> ""

                                                                    // Start with an empty buffer — the streaming
                                                                    // deltas below append to it. Previously this
                                                                    // was "{}" as a placeholder and the post-stream
                                                                    // fix-up stripped the leading two chars, which
                                                                    // broke the zero-delta case (model calls a
                                                                    // tool with no input): Arguments became "" and
                                                                    // downstream JSON deserialisation threw
                                                                    // "input does not contain any JSON tokens".
                                                                    // The post-stream default now fills an empty
                                                                    // buffer with "{}" so an empty-input tool call
                                                                    // deserialises as an empty object.
                                                                    toolCalls <-
                                                                        toolCalls
                                                                        @ [
                                                                            {
                                                                                AIProviderToolCall.Id = id
                                                                                Name = name
                                                                                Arguments = ""
                                                                            }
                                                                        ]
                                                                | _ -> ()
                                                            | _ -> ()

                                                        | _ -> ()
                                                    with _ ->
                                                        ()

                                    // Default empty arguments to "{}" so tools that
                                    // the model called without providing any input
                                    // (no `input_json_delta` chunks arrived)
                                    // deserialise as an empty object rather than
                                    // failing with "input does not contain any JSON
                                    // tokens". Non-empty Arguments pass through
                                    // unchanged — the streaming deltas have already
                                    // assembled valid JSON.
                                    let fixedToolCalls =
                                        toolCalls
                                        |> List.map (fun tc ->
                                            if System.String.IsNullOrWhiteSpace tc.Arguments then
                                                { tc with Arguments = "{}" }
                                            else
                                                tc)

                                    let usage =
                                        if usageSeen then
                                            Some {
                                                PromptTokens = inputTokens + cacheReadTokens + cacheCreationTokens
                                                CachedPromptTokens = cacheReadTokens
                                                OutputTokens = outputTokens
                                                CacheCreationTokens =
                                                    if cacheCreationSeen then Some cacheCreationTokens else None
                                            }
                                        else
                                            None

                                    return
                                        Ok {
                                            Content = fullText
                                            ToolCalls = fixedToolCalls
                                            StopReason = stopReason
                                            Usage = usage
                                        }
                            with
                            | :? OperationCanceledException when cts.IsCancellationRequested ->
                                // Cancelled by our per-call timeout. If we already
                                // delivered partial content, this is StreamingAborted
                                // (not retry-worthy). Otherwise treat as a transient
                                // timeout that the retry loop may re-attempt.
                                if accumulated <> "" then
                                    return
                                        Error(
                                            StreamingAborted(
                                                accumulated,
                                                $"Timed out after {RetryPolicy.timeoutDescription retryPolicy} with partial content delivered"
                                            )
                                        )
                                else
                                    return
                                        Error(
                                            TransientNetwork
                                                $"Request timed out after {RetryPolicy.timeoutDescription retryPolicy}"
                                        )
                            | :? HttpRequestException as ex ->
                                // Transport error mid-stream with no content yet =
                                // transient; with partial content = StreamingAborted.
                                if accumulated <> "" then
                                    return Error(StreamingAborted(accumulated, ex.Message))
                                else
                                    return Error(TransientNetwork ex.Message)
                            | ex ->
                                if accumulated <> "" then
                                    return Error(StreamingAborted(accumulated, ex.Message))
                                else
                                    return Error(TransientNetwork ex.Message)
                        else
                            // Non-streaming path.
                            try
                                let! response = client.SendAsync(request, cts.Token) |> Async.AwaitTask

                                if response.IsSuccessStatusCode then
                                    let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                                    try
                                        return Ok(parseResponse responseBody)
                                    with ex ->
                                        return Error(MalformedResponse ex.Message)
                                else
                                    let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                                    let code = int response.StatusCode

                                    return
                                        if code = 429 || code >= 500 then
                                            Error(TransientServer(code, errorBody))
                                        else
                                            Error(PermanentClient(code, errorBody))
                            with
                            | :? OperationCanceledException when cts.IsCancellationRequested ->
                                return
                                    Error(
                                        TransientNetwork
                                            $"Request timed out after {RetryPolicy.timeoutDescription retryPolicy}"
                                    )
                            | :? HttpRequestException as ex -> return Error(TransientNetwork ex.Message)
                            | ex -> return Error(TransientNetwork ex.Message)
                    }

                    // Retry loop. Non-retryable errors propagate immediately.
                    // Retryable errors are attempted up to MaxRetries extra times.
                    //
                    // Backoff strategy per error class:
                    //   - HTTP 429 (rate_limit_error): Anthropic's per-minute
                    //     buckets can't be cleared by sub-second exponential
                    //     backoff. Wait at least 15s on the first retry and
                    //     scale up linearly (15s, 30s, 45s, …) so the bucket
                    //     actually refills. Anthropic does not currently send
                    //     `retry-after` on 429s reliably, so we apply a
                    //     conservative floor rather than parsing the header.
                    //   - Everything else retryable (5xx, timeouts, transient
                    //     network): InitialBackoff * 2^retryIndex (exponential,
                    //     capped at MaxBackoff via `RetryPolicy.delayFor`).
                    //     Fast enough for genuinely transient failures.
                    let backoffFor (err: AIProviderError) (retryIndex: int) : TimeSpan =
                        match err with
                        | TransientServer(429, _) ->
                            // Linear 15s/30s/45s minimum floor for per-minute buckets.
                            TimeSpan.FromMilliseconds(float (max 15_000 (15_000 * (retryIndex + 1))))
                        | _ ->
                            // `retryIndex` is 0-indexed here (0 = first retry); the
                            // unified helper takes a 1-indexed attempt number, so
                            // pass `retryIndex + 2` (delay before attempt 2 when
                            // retryIndex = 0).
                            RetryPolicy.delayFor retryPolicy (retryIndex + 2)

                    let rec retryLoop attemptsMade = async {
                        let! result = singleAttempt ()
                        let attemptsMade = attemptsMade + 1

                        match result with
                        | Ok r -> return Ok r
                        | Error err when not (AIProviderError.isRetryable err) -> return Error err
                        | Error err when attemptsMade >= retryPolicy.MaxAttempts ->
                            // Budget exhausted. When `MaxAttempts = 1` the provider
                            // never retried, so return the raw error rather than
                            // wrapping in `RetriesExhausted` — the name would be
                            // misleading for a fail-fast call. Post-11.C.5 semantic:
                            // `MaxAttempts` counts the first attempt itself.
                            return
                                if retryPolicy.MaxAttempts = 1 then
                                    Error err
                                else
                                    Error(RetriesExhausted(attemptsMade, err))
                        | Error err ->
                            // Retryable + budget remains: back off then retry.
                            let delay = backoffFor err (attemptsMade - 1)
                            do! Async.Sleep delay
                            return! retryLoop attemptsMade
                    }

                    return! retryLoop 0
        }

        // Phase 67b — temporary fallback impl. Replaced with the
        // native tool-based workaround in the next 67b commit
        // (synthesise a single tool whose `input_schema` is the
        // supplied schema; `tool_choice = { type: "tool", name: "..."
        // }` forces the model to call it; the tool-call `input` is
        // the structured response). The fallback prepends schema as
        // a system-prompt instruction and post-validates the
        // response is JSON.
        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

/// Create a Claude AI provider from a secret store. The API key is
/// read from `ANTHROPIC_API_KEY` in the `_platform` scope on every
/// request. Legacy single-provider deployment helper.
let create (secretStore: ISecretStore) : IAIProvider = ClaudeAIProvider(secretStore)

/// Create with an explicit model using the secret-store path.
let createWithModel (secretStore: ISecretStore) (model: string) : IAIProvider = ClaudeAIProvider(secretStore, model)

/// Create a Claude AI provider using a directly-supplied API key.
/// Phase A+ factory path — used by the user/team-configured BYOK flow
/// where the factory resolves the key once per `Resolve` call and
/// constructs a provider bound to it.
let createWithApiKey (apiKey: string) : IAIProvider = ClaudeAIProvider(apiKey) :> IAIProvider

/// Create with an explicit API key and model.
let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    ClaudeAIProvider(apiKey, model) :> IAIProvider

/// Stable provider identifier. The factory and user-stored configs use
/// this as the key. Pair with `ClaudeAIProvider.DefaultModel` /
/// `KnownModels` when constructing an `AIProviderDescriptor` in the
/// app's compose step.
[<Literal>]
let ProviderId = "anthropic-claude"