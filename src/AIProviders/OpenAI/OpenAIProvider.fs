module OpenAIProvider

open System
open System.IO
open System.Net.Http
open System.Threading
open ToolUp.AI.Wire // JsonHost (portable schema parse)
open ToolUp.Platform // RetryPolicy (Phase 11.C.5 Tier 3 — unified)
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open OpenAIProviderWire

// ─── Provider implementation ─────────────────────────────────────
//
// Phase 252 (Wave 32): the wire mapping now runs through the portable
// `JsonValue` layer (`OpenAIProvider.Wire.fs`), and the HTTP egress is
// injected via the Phase 251 `IHttpTransport` seam — the .NET
// `HttpClientTransport` (buffered `Send` for the non-streaming + structured
// paths; `SendForStreaming` for the SSE read loop). The retry/backoff loop
// and the status→error classification are the shared portable
// `RetryRunner` / `ErrorClassifier`. Behaviour is identical to the prior
// inline egress (GP 11); the mapper is now host-portable (GP 12) and
// Fable-compiles (GP 8).

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

// Shared per-process HttpClient — see the matching note in
// `ClaudeAIProvider`. Phase 70's factory builds a fresh `OpenAIProvider`
// per `Resolve` call; without sharing, the prior shape opened a new
// connection pool per request and exhausted ephemeral ports under
// sustained load. `BaseAddress` and `Timeout` are stable; per-request
// `Authorization` rides on `HttpRequestMessage`, never on the client.
let private sharedClient =
    lazy
        (let c = new HttpClient()
         c.BaseAddress <- Uri("https://api.openai.com")
         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

type OpenAIProvider private (apiKeyFetcher: unit -> Async<string option>, model: string) =
    let client = sharedClient.Value

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
            // Phase 6j.B — native `response_format: json_schema`, so a
            // small closed-enum schema is served exactly rather than
            // coaxed.
            SupportsTriage = true
            TriageModelId = Some "gpt-4o-mini"
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
                    // Inject the .NET egress (Phase 251). The per-call timeout
                    // (`RetryPolicy.Timeout`) is applied inside the transport's
                    // buffered `Send`; the streaming path drives its own CTS so
                    // the read loop can cancel a stalled stream.
                    let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)

                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        let body = buildRequestBody model messages tools systemPrompt useStreaming None
                        let headers = [ "Authorization", $"Bearer {key}" ]
                        let request = HttpRequest.post "/v1/chat/completions" headers body

                        if useStreaming then
                            let state = {
                                Content = ""
                                ToolCalls = []
                                StopReason = "end_turn"
                                Usage = None
                            }

                            use cts =
                                match retryPolicy.Timeout with
                                | Some t ->
                                    let clampedMs = RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)
                                    new CancellationTokenSource(TimeSpan.FromMilliseconds(float clampedMs))
                                | None -> new CancellationTokenSource()

                            try
                                // ResponseHeadersRead lives inside SendForStreaming —
                                // the byte-identical streaming egress the transport owns.
                                let! response = transport.SendForStreaming(request, cts.Token)

                                if not response.IsSuccessStatusCode then
                                    let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                                    return Error(ErrorClassifier.classifyStatus (int response.StatusCode) errorBody)
                                else
                                    let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                                    use reader = new StreamReader(stream)

                                    let mutable reading = true

                                    while reading do
                                        // Pass cts.Token so the per-call timeout
                                        // (RetryPolicy.Timeout) actually cancels
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
                                let! response = (transport :> IHttpTransport).Send request

                                if HttpResponse.isSuccess response then
                                    try
                                        return Ok(parseResponse response.Body)
                                    with ex ->
                                        return Error(MalformedResponse ex.Message)
                                else
                                    return Error(ErrorClassifier.classifyStatus response.StatusCode response.Body)
                            with
                            | :? OperationCanceledException ->
                                return
                                    Error(
                                        TransientNetwork
                                            $"Request timed out after {RetryPolicy.timeoutDescription retryPolicy}"
                                    )
                            | :? HttpRequestException as ex ->
                                return Error(ErrorClassifier.classifyTransportFailure ex.Message)
                            | ex -> return Error(ErrorClassifier.classifyTransportFailure ex.Message)
                    }

                    // Portable retry loop (Phase 251) — exponential backoff,
                    // non-retryable errors propagate immediately, budget
                    // exhaustion wraps as `RetriesExhausted` unless
                    // `MaxAttempts = 1` (fail-fast). Byte-identical to the
                    // prior inline loop.
                    return! RetryRunner.run retryPolicy singleAttempt
        }

        // Phase 67b — native structured output via OpenAI's
        // `response_format: { type: "json_schema", json_schema: { name,
        // schema, strict: true } }`. Requires gpt-4o-2024-08-06+ or
        // gpt-4o-mini; older models reject with HTTP 400 (surfaced as
        // PermanentClient — see retry classification).
        //
        // Non-streaming only (streaming structured-output is supported
        // by OpenAI but deferred to a follow-on phase per the
        // Out-of-scope list). Vision pre-check mirrors SendMessage.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (OpenAIProviderWire.isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else
                // Parse the JSON Schema string into a portable `JsonValue`. A
                // malformed schema is a caller defect — surface as
                // PermanentClient(0). Same precedent as the Gemini
                // structured path.
                let parsedSchema =
                    match JsonHost.parse schema with
                    | Some v -> Ok v
                    | None -> Error(PermanentClient(0, "structuredOutputSchema is not valid JSON"))

                match parsedSchema with
                | Error e -> return Error e
                | Ok schemaValue ->
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
                        let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)

                        let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                            let body =
                                buildRequestBody model messages tools systemPrompt false (Some schemaValue)

                            let headers = [ "Authorization", $"Bearer {key}" ]
                            let request = HttpRequest.post "/v1/chat/completions" headers body

                            try
                                let! response = (transport :> IHttpTransport).Send request

                                if HttpResponse.isSuccess response then
                                    try
                                        return Ok(parseResponse response.Body)
                                    with ex ->
                                        return Error(MalformedResponse ex.Message)
                                else
                                    return Error(ErrorClassifier.classifyStatus response.StatusCode response.Body)
                            with
                            | :? OperationCanceledException ->
                                return
                                    Error(
                                        TransientNetwork
                                            $"Request timed out after {RetryPolicy.timeoutDescription retryPolicy}"
                                    )
                            | :? HttpRequestException as ex ->
                                return Error(ErrorClassifier.classifyTransportFailure ex.Message)
                            | ex -> return Error(ErrorClassifier.classifyTransportFailure ex.Message)
                        }

                        return! RetryRunner.run retryPolicy singleAttempt
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