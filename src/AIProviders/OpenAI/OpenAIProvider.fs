module OpenAIProvider

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open ToolUp.Platform // RetryPolicy (Phase 11.C.5 Tier 3 — unified)
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open OpenAIProviderWire

// ─── Provider implementation ─────────────────────────────────────

/// Default OpenAI model. Paired with `KnownModels` + `ProviderId` when
/// the app constructs an `AIProviderDescriptor` for this provider.
[<Literal>]
let DefaultModel = "gpt-4o"

/// Known-good model identifiers for the settings UI dropdown. Users
/// may enter custom strings for models released after the build.
let KnownModels = [ "gpt-4o"; "gpt-4o-mini"; "gpt-4-turbo"; "o1"; "o1-mini" ]

/// Stable provider identifier used in user configs and secret-store
/// key names.
[<Literal>]
let ProviderId = "openai-gpt"

type OpenAIProvider private (apiKeyFetcher: unit -> Async<string option>, model: string) =
    let client = new HttpClient()

    do
        client.BaseAddress <- Uri("https://api.openai.com")
        client.Timeout <- TimeSpan.FromMinutes(5.0)

    /// Construct with an API key provided directly (Phase A+ factory
    /// path — user-supplied or deployment-resolved key).
    new(apiKey: string, ?model: string) =
        OpenAIProvider((fun () -> async { return Some apiKey }), defaultArg model DefaultModel)

    /// Construct reading the API key from the secret store on every
    /// request. Reads `OPENAI_API_KEY` from the `_platform` scope.
    new(secretStore: ISecretStore, ?model: string) =
        OpenAIProvider((fun () -> secretStore.GetSecret("_platform", "OPENAI_API_KEY")), defaultArg model DefaultModel)

    interface IAIProvider with
        member _.Capabilities = {
            // Streaming supported on chat/completions with stream=true.
            Streaming = true
            // Function calling (OpenAI's tool-use equivalent).
            ToolUse = true
            // Vision: supported on gpt-4o / gpt-4-turbo-vision. Declared true
            // at the provider level; per-model gating is the app's
            // responsibility (see capability-flag worked examples in
            // ToolUp.AI/TECHNICAL_GUIDE.md).
            Vision = true
            // Phase 6i.B: usage parsed on both response paths.
            // OpenAI prompt caching is automatic ≥1024 tokens — no
            // request-side opt-in needed; cached portion reported at
            // usage.prompt_tokens_details.cached_tokens.
            SupportsPromptCaching = true
            ProviderName = "openai"
            Model = model
        }

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            // Phase 6o — short-circuit on multimodal content against a
            // non-vision-capable model. Match the Claude provider's
            // shape: cheaper failure than letting OpenAI return HTTP
            // 400 after the (potentially large) image upload.
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (OpenAIProviderWire.isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else

                let! apiKey = apiKeyFetcher ()

                match apiKey with
                | None ->
                    return
                        Error(
                            PermanentClient(
                                0,
                                "OPENAI_API_KEY not configured. Set it as an environment variable or in your secret store."
                            )
                        )
                | Some key ->
                    let useStreaming = onStream.IsSome

                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        let body = buildRequestBody model messages tools systemPrompt useStreaming
                        let request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                        request.Headers.Add("Authorization", $"Bearer {key}")

                        use cts =
                            match retryPolicy.Timeout with
                            | Some t ->
                                let clampedMs = RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)
                                new CancellationTokenSource(TimeSpan.FromMilliseconds(float clampedMs))
                            | None -> new CancellationTokenSource()

                        if useStreaming then
                            let state = {
                                Content = ""
                                ToolCalls = []
                                StopReason = "end_turn"
                                Usage = None
                            }

                            try
                                let! response =
                                    client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                    |> Async.AwaitTask

                                if not response.IsSuccessStatusCode then
                                    let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                                    let code = int response.StatusCode

                                    return
                                        if code = 429 || code >= 500 then
                                            Error(TransientServer(code, errorBody))
                                        else
                                            Error(PermanentClient(code, errorBody))
                                else
                                    let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                                    use reader = new StreamReader(stream)

                                    let mutable reading = true

                                    while reading do
                                        // Pass cts.Token so the per-call timeout
                                        // (RetryPolicy.TimeoutMs) actually cancels
                                        // a stalled stream. Without the token,
                                        // ReadLineAsync ignores the CTS — a hung
                                        // OpenAI stream waits until
                                        // HttpClient.Timeout (5 min instance-wide)
                                        // and the agent loop never surfaces the
                                        // failure as AITaskFailed in time. The
                                        // ValueTask-returning overload landed in
                                        // .NET 7; net10 has it.
                                        let! line = reader.ReadLineAsync(cts.Token).AsTask() |> Async.AwaitTask

                                        if isNull line then
                                            reading <- false
                                        elif line.StartsWith("data: ") then
                                            applyStreamChunk state onStream (line.Substring(6))

                                    return
                                        Ok {
                                            Content = state.Content
                                            ToolCalls = state.ToolCalls
                                            StopReason = state.StopReason
                                            Usage = state.Usage
                                        }
                            with
                            | :? OperationCanceledException when cts.IsCancellationRequested ->
                                if state.Content <> "" then
                                    return
                                        Error(
                                            StreamingAborted(
                                                state.Content,
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
                                if state.Content <> "" then
                                    return Error(StreamingAborted(state.Content, ex.Message))
                                else
                                    return Error(TransientNetwork ex.Message)
                            | ex ->
                                if state.Content <> "" then
                                    return Error(StreamingAborted(state.Content, ex.Message))
                                else
                                    return Error(TransientNetwork ex.Message)
                        else
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

                    // Retry loop. Same shape as ClaudeAIProvider — exponential
                    // backoff via `RetryPolicy.delayFor` (capped at
                    // `MaxBackoff`), non-retryable errors propagate
                    // immediately, budget exhaustion wraps as
                    // `RetriesExhausted` unless `MaxAttempts = 1`
                    // (the post-11.C.5 fail-fast contract — `MaxAttempts`
                    // counts the first attempt itself, so 1 = no retries).
                    let rec retryLoop attemptsMade = async {
                        let! result = singleAttempt ()
                        let attemptsMade = attemptsMade + 1

                        match result with
                        | Ok r -> return Ok r
                        | Error err when not (AIProviderError.isRetryable err) -> return Error err
                        | Error err when attemptsMade >= retryPolicy.MaxAttempts ->
                            return
                                if retryPolicy.MaxAttempts = 1 then
                                    Error err
                                else
                                    Error(RetriesExhausted(attemptsMade, err))
                        | Error _ ->
                            let delay = RetryPolicy.delayFor retryPolicy (attemptsMade + 1)
                            do! Async.Sleep delay
                            return! retryLoop attemptsMade
                    }

                    return! retryLoop 0
        }

/// Create using a secret-store read of `OPENAI_API_KEY` on every
/// request. Legacy single-provider deployment helper.
let create (secretStore: ISecretStore) : IAIProvider =
    OpenAIProvider(secretStore) :> IAIProvider

/// Create with an explicit model using the secret-store path.
let createWithModel (secretStore: ISecretStore) (model: string) : IAIProvider =
    OpenAIProvider(secretStore, model) :> IAIProvider

/// Create using a directly-supplied API key. Phase A+ factory path
/// for user/team-configured BYOK deployments.
let createWithApiKey (apiKey: string) : IAIProvider = OpenAIProvider(apiKey) :> IAIProvider

/// Create with an explicit API key and model.
let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    OpenAIProvider(apiKey, model) :> IAIProvider