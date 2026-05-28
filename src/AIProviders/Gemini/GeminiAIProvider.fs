module GeminiAIProvider

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform // RetryPolicy (Phase 11.C.5 Tier 3 — unified)
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open GeminiAIProviderWire

// ─── Provider implementation ─────────────────────────────────────

/// Default Gemini model. Flash is the lower-cost / lower-latency
/// tier of the 2.5 family; Pro is the larger sibling. Users
/// override per-instance via `IProviderProfile`.
[<Literal>]
let DefaultModel = "models/gemini-2.5-flash"

/// Known-good model identifiers surfaced by the settings UI. Users
/// may enter custom strings for models released after the build.
let KnownModels = [
    "models/gemini-2.5-pro"
    "models/gemini-2.5-flash"
    "models/gemini-1.5-pro"
    "models/gemini-1.5-flash"
]

/// Stable provider identifier used in user configs and secret-store
/// key names.
[<Literal>]
let ProviderId = "google-gemini"

/// Secret-store key the secret-store constructor reads from when
/// resolving the API key on every request.
[<Literal>]
let SecretKeyName = "GEMINI_API_KEY"

/// Normalise the model id into the path segment Gemini's REST API
/// expects: every endpoint URL is `/v1beta/{model}:generateContent`
/// where `{model}` is `models/gemini-...`. Accept callers that
/// supply either form.
let private modelPath (model: string) =
    if model.StartsWith("models/") then
        model
    else
        "models/" + model

type GeminiAIProvider private (apiKeyFetcher: unit -> Async<string option>, model: string) =
    let client = new HttpClient()

    do
        client.BaseAddress <- Uri("https://generativelanguage.googleapis.com")
        client.Timeout <- TimeSpan.FromMinutes(5.0)

    /// Construct with an API key provided directly (factory path —
    /// user-supplied or deployment-resolved key).
    new(apiKey: string, ?model: string) =
        GeminiAIProvider((fun () -> async { return Some apiKey }), defaultArg model DefaultModel)

    /// Construct reading the API key from the secret store on every
    /// request. Reads `GEMINI_API_KEY` from the `_platform` scope.
    new(secretStore: ISecretStore, ?model: string) =
        GeminiAIProvider((fun () -> secretStore.GetSecret("_platform", SecretKeyName)), defaultArg model DefaultModel)

    interface IAIProvider with
        member _.Capabilities = {
            // Streaming supported via :streamGenerateContent?alt=sse.
            Streaming = true
            // Function calling supported across 2.5 + 1.5 families.
            ToolUse = true
            // Vision: multimodal input is core to 1.5 / 2.5 — every
            // model in `KnownModels` accepts image / audio / video
            // parts. The per-model `isVisionCapable` check exists for
            // a future text-only variant.
            Vision = true
            // Gemini's `cachedContents` API is a separate explicit-
            // cache resource lifecycle (named caches, TTL, billing).
            // This provider does not currently create or attach to
            // those caches — `cachedContentTokenCount` is still
            // surfaced when present (some accounts get implicit
            // server-side caching), so we declare the capability
            // truthfully: counts may flow through but caching is not
            // request-side controlled here.
            SupportsPromptCaching = true
            ProviderName = "google-gemini"
            Model = model
        }

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (GeminiAIProviderWire.isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else

                let! apiKey = apiKeyFetcher ()

                match apiKey with
                | None ->
                    return
                        Error(
                            PermanentClient(
                                0,
                                "GEMINI_API_KEY not configured. Set it as an environment variable or in your secret store."
                            )
                        )
                | Some key ->
                    let useStreaming = onStream.IsSome

                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        let body = buildRequestBody messages tools systemPrompt None

                        let endpoint =
                            if useStreaming then
                                sprintf "/v1beta/%s:streamGenerateContent?alt=sse" (modelPath model)
                            else
                                sprintf "/v1beta/%s:generateContent" (modelPath model)

                        let request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                        request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
                        request.Headers.Add("x-goog-api-key", key)

                        use cts =
                            match retryPolicy.Timeout with
                            | Some t ->
                                let clampedMs = RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)
                                new CancellationTokenSource(TimeSpan.FromMilliseconds(float clampedMs))
                            | None -> new CancellationTokenSource()

                        if useStreaming then
                            let state = initialStreamState ()

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

                    // Retry loop. Same shape as the Claude + OpenAI
                    // providers — exponential backoff via
                    // `RetryPolicy.delayFor` (capped at `MaxBackoff`),
                    // non-retryable errors propagate immediately, budget
                    // exhaustion wraps as `RetriesExhausted` unless
                    // `MaxAttempts = 1` (post-11.C.5 fail-fast contract).
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

/// Create using a secret-store read of `GEMINI_API_KEY` on every
/// request. Legacy single-provider deployment helper.
let create (secretStore: ISecretStore) : IAIProvider =
    GeminiAIProvider(secretStore) :> IAIProvider

/// Create with an explicit model using the secret-store path.
let createWithModel (secretStore: ISecretStore) (model: string) : IAIProvider =
    GeminiAIProvider(secretStore, model) :> IAIProvider

/// Create using a directly-supplied API key. Factory path for
/// user/team-configured BYOK deployments.
let createWithApiKey (apiKey: string) : IAIProvider = GeminiAIProvider(apiKey) :> IAIProvider

/// Create with an explicit API key and model.
let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    GeminiAIProvider(apiKey, model) :> IAIProvider