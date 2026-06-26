module ClaudeAIProvider

open System
open System.IO
open System.Net.Http
open System.Threading
open ToolUp.AI.Wire // ClaudeStreaming — the pure SSE state machine (Phase 254)
open ToolUp.Platform // RetryPolicy (Phase 11.C.5 Tier 3 — unified)
open ToolUp.Platform.AI // IHttpTransport / HttpClientTransport / HttpRequest / ErrorClassifier (Phase 251/254)
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
///
/// Refreshed 2026-06-12 — the prior list pinned `claude-sonnet-4-20250514`
/// (now returns `not_found_error` on the API) and the long-retired
/// haiku-3.5. Current Anthropic ids are date-suffix-free aliases except
/// Haiku 4.5, whose canonical full id retains the date.
let KnownModels = [
    "claude-haiku-4-5-20251001"
    "claude-sonnet-4-6"
    "claude-opus-4-8"
    "claude-fable-5"
]

// Shared per-process HttpClient. Phase 70's factory builds a fresh
// `ClaudeAIProvider` per `Resolve` call (the curried `Build` closure
// is invoked at request time, not at composition time). Constructing
// a new HttpClient per provider per request — the prior shape — is
// the classic .NET socket-exhaustion antipattern: each HttpClient owns
// its own connection pool, and connections linger in TIME_WAIT for
// 60–120 seconds after disposal, so under sustained chat volume the
// process eventually runs out of ephemeral ports.
//
// The provider snapshots no per-request state on the HttpClient
// itself — `BaseAddress` and `Timeout` are stable across every
// instance, and the per-request `x-api-key` header lives on the
// `HttpRequestMessage`, not on the client. Sharing is therefore safe.
let private sharedClient =
    lazy
        (let c = new HttpClient()
         c.BaseAddress <- Uri("https://api.anthropic.com")
         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

// API-key fetching is abstracted to a thunk so the same core
// implementation serves both the legacy secret-store path (fetch on
// every request — supports rotation) and the Phase-A factory path
// (fetch once at construction — per-user BYOK). Each fetch call inside
// SendMessage re-executes the thunk; the thunk decides whether to hit
// a store or return a captured value.
type ClaudeAIProvider private (apiKeyFetcher: unit -> Async<string option>, model: string, maxTokens: int) =
    let client = sharedClient.Value

    // Guard a misconfigured cap. <= 0 would make the Anthropic API
    // reject every request; fall back to the safe default rather than
    // hard-fail every call.
    let maxTokens = if maxTokens > 0 then maxTokens else DefaultMaxTokens

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

                    // The portable wire mapping builds the request body over
                    // `JsonValue` (Phase 254); the .NET HttpClientTransport
                    // (Phase 251) reproduces the egress the provider used to
                    // inline — same shared client, same per-request headers on
                    // the message, same per-call timeout derived from
                    // RetryPolicy.Timeout. Status classification is the
                    // ErrorClassifier's; the streaming assembly is the pure
                    // ClaudeStreaming state machine.
                    let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)
                    let requestHeaders = [ "x-api-key", key; "anthropic-version", "2023-06-01" ]

                    // One attempt — whole request lifecycle, including the
                    // per-call timeout and HTTP status classification. Returns
                    // a Result so the retry loop can classify before deciding
                    // to back off or propagate.
                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        let body =
                            buildRequestBody model maxTokens messages tools systemPrompt useStreaming None

                        let request = HttpRequest.post "/v1/messages" requestHeaders body

                        if useStreaming then
                            // Streaming path: accumulate partial text so that if
                            // the stream aborts mid-delivery we can report what
                            // the client already received. Streaming is NEVER
                            // retried — see StreamingAborted doc in IAIProvider.fs.
                            // The SSE read loop stays host-specific (it cannot
                            // ride the portable buffered transport); it feeds each
                            // `data:` payload to the pure ClaudeStreaming
                            // processor, which owns the text/tool-call/usage
                            // assembly and the post-stream `{}` default-fill.
                            let mutable accumulated = ""

                            // Per-call timeout. HttpClient.Timeout is instance-wide
                            // (5 min in the constructor); RetryPolicy.Timeout lets
                            // the caller impose a tighter per-request budget, applied
                            // to both SendForStreaming and the line reads below.
                            use cts =
                                match retryPolicy.Timeout with
                                | Some t ->
                                    let clampedMs = RetryPolicy.clampTimeoutMs (int t.TotalMilliseconds)
                                    new CancellationTokenSource(TimeSpan.FromMilliseconds(float clampedMs))
                                | None -> new CancellationTokenSource()

                            try
                                use! response = transport.SendForStreaming(request, cts.Token)

                                if not response.IsSuccessStatusCode then
                                    let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                                    // Status-code classification applies to
                                    // streaming requests that fail BEFORE any
                                    // content is delivered (no partial output
                                    // yet, so the transient/permanent split still
                                    // applies — retry budget still meaningful).
                                    return Error(ErrorClassifier.classifyStatus (int response.StatusCode) errorBody)
                                else
                                    let! stream = response.Content.ReadAsStreamAsync() |> Async.AwaitTask
                                    use reader = new StreamReader(stream)

                                    let mutable state = ClaudeStreaming.initial
                                    let mutable reading = true

                                    while reading do
                                        // Pass cts.Token so the per-call timeout
                                        // actually cancels a stalled stream. Without
                                        // the token, ReadLineAsync ignores the CTS —
                                        // a hung stream waits until HttpClient.Timeout
                                        // (5 min instance-wide) and the agent loop
                                        // never surfaces the failure as AITaskFailed
                                        // in time. The ValueTask overload landed in
                                        // .NET 7; net10 has it.
                                        let! line = reader.ReadLineAsync(cts.Token).AsTask() |> Async.AwaitTask

                                        if isNull line then
                                            reading <- false
                                        elif line.StartsWith("data: ") then
                                            let data = line.Substring(6)

                                            if data <> "[DONE]" then
                                                let newState, emitted = ClaudeStreaming.processData state data
                                                state <- newState

                                                match emitted with
                                                | Some t ->
                                                    onStream |> Option.iter (fun cb -> cb t)
                                                    accumulated <- accumulated + t
                                                | None -> ()

                                    return Ok(ClaudeStreaming.finalize state)
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
                            // Non-streaming path — buffered request/response over
                            // the portable IHttpTransport seam.
                            try
                                let! response = (transport :> IHttpTransport).Send(request)

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

        // Phase 67b — Anthropic has no native structured-output mode.
        // The documented workaround is to synthesise a tool whose
        // `input_schema` is the supplied schema, then force
        // `tool_choice = { type: "tool", name: "structured_response" }`
        // so the assistant is required to call that tool. The
        // tool-call's `input` field IS the structured response — this
        // method extracts it and surfaces it as `AIProviderResponse.
        // Content` so callers see the same shape as the Gemini /
        // OpenAI native paths.
        //
        // Limitation: with `tool_choice` forcing the schema-tool,
        // user-supplied tools become unreachable in the same turn.
        // Callers should run any free-form tool-dispatch loop on
        // SendMessage first, then a final SendStructuredMessage for
        // the structured response.
        //
        // Non-streaming only — Anthropic does emit `tool_use` blocks
        // during streaming, but parsing partial `input` JSON
        // mid-stream is fragile. Streaming structured-output is
        // deferred to a follow-on phase per the Out-of-scope list.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (ClaudeAIProviderWire.isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else
                let! apiKey = apiKeyFetcher ()

                match apiKey with
                | None ->
                    return
                        Error(
                            PermanentClient(
                                0,
                                "ANTHROPIC_API_KEY not configured. Set it as an environment variable or in your secret store."
                            )
                        )
                | Some key ->
                    let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)
                    let requestHeaders = [ "x-api-key", key; "anthropic-version", "2023-06-01" ]

                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        let body =
                            buildRequestBody model maxTokens messages tools systemPrompt false (Some schema)

                        let request = HttpRequest.post "/v1/messages" requestHeaders body

                        try
                            let! response = (transport :> IHttpTransport).Send(request)

                            if HttpResponse.isSuccess response then
                                let responseBody = response.Body

                                try
                                    let parsed = parseResponse responseBody

                                    // Find the schema-tool call. With
                                    // tool_choice forcing it +
                                    // disable_parallel_tool_use, exactly
                                    // one tool_use block is expected.
                                    let schemaCall =
                                        parsed.ToolCalls
                                        |> List.tryFind (fun tc ->
                                            tc.Name = ClaudeAIProviderWire.StructuredResponseToolName)

                                    match schemaCall with
                                    | Some call ->
                                        // Replace .Content with the
                                        // tool-call's `input` JSON so
                                        // the response shape matches
                                        // Gemini / OpenAI's native
                                        // paths. The payload first runs
                                        // through the deterministic
                                        // normalisation pass — Claude
                                        // models sometimes wrap the
                                        // response in a single-key
                                        // `{ "input": ... }` envelope
                                        // and/or emit it as a
                                        // JSON-encoded string, and
                                        // Anthropic does not validate
                                        // tool inputs against the
                                        // schema server-side. Strip the
                                        // schema-tool from ToolCalls;
                                        // surface any user-tool calls
                                        // (should be empty per the
                                        // forced tool_choice, but
                                        // defensively preserved).
                                        let userToolCalls =
                                            parsed.ToolCalls
                                            |> List.filter (fun tc ->
                                                tc.Name <> ClaudeAIProviderWire.StructuredResponseToolName)

                                        return
                                            Ok {
                                                parsed with
                                                    Content =
                                                        ClaudeAIProviderWire.normaliseStructuredPayload
                                                            schema
                                                            call.Arguments
                                                    ToolCalls = userToolCalls
                                                    StopReason = "end_turn"
                                            }
                                    | None ->
                                        // Model declined to call the
                                        // forced tool — should be
                                        // impossible per Anthropic's
                                        // tool_choice contract, but
                                        // surface honestly rather than
                                        // silently fabricate.
                                        return
                                            Error(
                                                SchemaUnsupported(
                                                    "structured-output",
                                                    "Claude did not return the forced structured_response tool call. This is unexpected — the request was issued with tool_choice forcing the call."
                                                )
                                            )
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
                        | :? HttpRequestException as ex -> return Error(TransientNetwork ex.Message)
                        | ex -> return Error(TransientNetwork ex.Message)
                    }

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