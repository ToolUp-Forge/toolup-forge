module CopilotAIProvider

open System
open System.IO
open System.Net.Http
open System.Threading
open Azure.Core
open Azure.Identity
open ToolUp.AI.Wire // JsonHost (portable schema parse)
open ToolUp.Platform // RetryPolicy
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open OpenAIProviderWire

// ─── Azure OpenAI ("Microsoft Copilot") provider ─────────────────────
//
// Azure OpenAI's chat/completions surface is wire-identical to OpenAI's
// (same request body, same response shape, same SSE stream frames), so this
// provider reuses the OpenAI wire mapping verbatim (`OpenAIProvider.Wire.fs`
// is compiled into this assembly — see the `.fsproj` link) and only carries
// the three deltas that make it Azure:
//
//   1. Endpoint — per-resource, not a constant. Requests go to the absolute
//      URL `{endpoint}/openai/deployments/{deployment}/chat/completions
//      ?api-version={apiVersion}`; the shared `HttpClient` therefore carries
//      NO `BaseAddress` (an absolute request URL overrides it anyway).
//   2. Auth — either a static `api-key` header (secret-store or direct) or a
//      Microsoft Entra ID bearer token acquired from an `Azure.Core`
//      `TokenCredential` (default: `DefaultAzureCredential`, i.e. managed
//      identity in prod, `az login` locally, env client-secret in CI). The
//      Entra scope is `https://cognitiveservices.azure.com/.default`.
//   3. "Model" is the Azure *deployment name* (embedded in the URL), not an
//      OpenAI model id; the body's `model` field is accepted but ignored by
//      Azure.
//
// DisplayName is "Microsoft Copilot" (the client-facing brand); the stable
// `ProviderId` is the accurate `azure-openai`.

/// Default deployment name. For Azure this is the name the customer gave
/// their deployment in the Azure OpenAI resource — commonly the model id,
/// which is why "gpt-4o" is a sensible default, but any string is valid.
[<Literal>]
let DefaultModel = "gpt-4o"

/// Common deployment-name suggestions for the settings UI dropdown. These
/// are deployment names, not fixed model ids — a deployment can be named
/// anything, so users may enter custom strings.
let KnownModels = [ "gpt-4o"; "gpt-4o-mini"; "gpt-4.1"; "o1"; "o1-mini" ]

/// Stable provider identifier used in user configs and secret-store key
/// names. Kept technically accurate even though the display name is
/// "Microsoft Copilot".
[<Literal>]
let ProviderId = "azure-openai"

/// Default Azure OpenAI REST api-version. `2024-10-21` is GA and supports
/// tools + structured outputs (`response_format: json_schema`). Overridable
/// per construction for deployments pinned to a different version.
[<Literal>]
let DefaultApiVersion = "2024-10-21"

/// Entra ID token scope for the Azure OpenAI (Cognitive Services) data
/// plane. The `/.default` suffix requests the app's statically-consented
/// permissions (app-only / managed-identity convention).
[<Literal>]
let CognitiveServicesScope = "https://cognitiveservices.azure.com/.default"

/// Secret-store key name for the api-key auth path (`_platform` scope).
[<Literal>]
let SecretKeyName = "AZURE_OPENAI_API_KEY"

// Shared per-process HttpClient — see the matching note in `OpenAIProvider`.
// No `BaseAddress`: every request uses an absolute per-resource Azure URL,
// which overrides `BaseAddress` regardless. `Timeout` is instance-wide;
// per-request auth rides on `HttpRequestMessage`, never on the client.
let private sharedClient =
    lazy
        (let c = new HttpClient()
         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

/// How the provider authenticates each request. Chosen at construction and
/// closed over — the BYOK factory's `apiKey -> model -> IAIProvider` builder
/// keeps working (api-key mode); Entra mode ignores the passed key.
type private CopilotAuth =
    | ApiKeyAuth of fetchKey: (unit -> Async<string option>)
    | EntraAuth of credential: TokenCredential

type CopilotAIProvider private (endpoint: string, apiVersion: string, auth: CopilotAuth, model: string) =
    let client = sharedClient.Value

    // The absolute Azure OpenAI request URL. `model` is the deployment name.
    let requestUrl =
        sprintf "%s/openai/deployments/%s/chat/completions?api-version=%s" (endpoint.TrimEnd('/')) model apiVersion

    /// Resolve the per-request auth header once (mirrors OpenAI fetching the
    /// api-key once, before the retry loop). ApiKey → `api-key: {key}`;
    /// Entra → `Authorization: Bearer {token}`.
    let acquireAuthHeaders () : Async<Result<(string * string) list, AIProviderError>> = async {
        match auth with
        | ApiKeyAuth fetchKey ->
            let! key = fetchKey ()

            match key with
            | Some k when not (String.IsNullOrWhiteSpace k) -> return Ok [ "api-key", k ]
            | _ ->
                return
                    Error(
                        PermanentClient(
                            0,
                            sprintf
                                "%s not configured. Set it in your secret store, supply it directly, or use Entra ID auth (a TokenCredential)."
                                SecretKeyName
                        )
                    )
        | EntraAuth credential ->
            try
                let! ct = Async.CancellationToken
                let request = TokenRequestContext [| CognitiveServicesScope |]
                let! token = credential.GetTokenAsync(request, ct).AsTask() |> Async.AwaitTask
                return Ok [ "Authorization", sprintf "Bearer %s" token.Token ]
            with ex ->
                return
                    Error(TransientNetwork(sprintf "Entra ID token acquisition failed for Azure OpenAI: %s" ex.Message))
    }

    /// Construct with a static api-key against an Azure OpenAI resource
    /// endpoint (e.g. `https://my-resource.openai.azure.com`). `model` is the
    /// deployment name.
    new(endpoint: string, apiKey: string, ?model: string, ?apiVersion: string) =
        CopilotAIProvider(
            endpoint,
            defaultArg apiVersion DefaultApiVersion,
            ApiKeyAuth(fun () -> async { return Some apiKey }),
            defaultArg model DefaultModel
        )

    /// Construct reading the api-key from the secret store on every request.
    /// Reads `AZURE_OPENAI_API_KEY` from the `_platform` scope.
    new(secretStore: ISecretStore, endpoint: string, ?model: string, ?apiVersion: string) =
        CopilotAIProvider(
            endpoint,
            defaultArg apiVersion DefaultApiVersion,
            ApiKeyAuth(fun () -> secretStore.GetSecret("_platform", SecretKeyName)),
            defaultArg model DefaultModel
        )

    /// Construct with Microsoft Entra ID auth via an `Azure.Core`
    /// `TokenCredential` (e.g. `DefaultAzureCredential` for managed
    /// identity / `az login`, or `ClientSecretCredential` for explicit
    /// client-credentials). No static secret required.
    new(endpoint: string, credential: TokenCredential, ?model: string, ?apiVersion: string) =
        CopilotAIProvider(
            endpoint,
            defaultArg apiVersion DefaultApiVersion,
            EntraAuth credential,
            defaultArg model DefaultModel
        )

    interface IAIProvider with
        member _.Capabilities = {
            // Streaming supported on chat/completions with stream=true.
            Streaming = true
            // Function calling (OpenAI's tool-use equivalent).
            ToolUse = true
            // Vision: gpt-4o / gpt-4-turbo deployments accept image input.
            // Declared true at the provider level; per-deployment gating is
            // best-effort (deployment names are arbitrary on Azure — see the
            // vision short-circuit note in SendMessage).
            Vision = true
            // Azure OpenAI prompt caching mirrors OpenAI's automatic caching.
            SupportsPromptCaching = true
            ProviderName = ProviderId
            Model = model
        }

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            // Short-circuit on multimodal content against a non-vision
            // deployment — cheaper than letting Azure return HTTP 400 after
            // the (potentially large) image upload. Best-effort: Azure
            // deployment names are arbitrary, so a mis-named vision
            // deployment may slip past and 400 at Azure instead.
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else
                let! authResult = acquireAuthHeaders ()

                match authResult with
                | Error e -> return Error e
                | Ok authHeaders ->
                    let useStreaming = onStream.IsSome
                    let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)

                    let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                        let body = buildRequestBody model messages tools systemPrompt useStreaming None
                        let request = HttpRequest.post requestUrl authHeaders body

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
                                let! response = transport.SendForStreaming(request, cts.Token)

                                if not response.IsSuccessStatusCode then
                                    let! errorBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                                    return Error(ErrorClassifier.classifyStatus (int response.StatusCode) errorBody)
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

        // Native structured output via Azure OpenAI's `response_format:
        // { type: "json_schema", strict: true }` — identical wire to OpenAI.
        // Requires a gpt-4o-2024-08-06+ / gpt-4o-mini deployment on an
        // api-version that supports structured outputs; older deployments
        // reject with HTTP 400 (surfaced as PermanentClient). Non-streaming
        // only. Vision pre-check mirrors SendMessage.
        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) = async {
            let hasImagePart =
                messages |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal

            if hasImagePart && not (isVisionCapable model) then
                return Error(UnsupportedCapability("vision", sprintf "Model '%s' does not accept image input." model))
            else
                let parsedSchema =
                    match JsonHost.parse schema with
                    | Some v -> Ok v
                    | None -> Error(PermanentClient(0, "structuredOutputSchema is not valid JSON"))

                match parsedSchema with
                | Error e -> return Error e
                | Ok schemaValue ->
                    let! authResult = acquireAuthHeaders ()

                    match authResult with
                    | Error e -> return Error e
                    | Ok authHeaders ->
                        let transport = HttpClientTransport(client, ?timeout = retryPolicy.Timeout)

                        let singleAttempt () : Async<Result<AIProviderResponse, AIProviderError>> = async {
                            let body =
                                buildRequestBody model messages tools systemPrompt false (Some schemaValue)

                            let request = HttpRequest.post requestUrl authHeaders body

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

// ─── Factory helpers ─────────────────────────────────────────────────
//
// All take the Azure resource `endpoint` first so a composition root can
// partially-apply it and hand the BYOK factory the standard
// `apiKey -> model -> IAIProvider` builder (`createWithApiKeyAndModel
// endpoint`). Entra variants ignore any BYOK key.

/// Api-key from the secret store (`AZURE_OPENAI_API_KEY`, `_platform`).
let create (secretStore: ISecretStore) (endpoint: string) : IAIProvider =
    CopilotAIProvider(secretStore, endpoint) :> IAIProvider

/// Api-key from the secret store, explicit deployment name.
let createWithModel (secretStore: ISecretStore) (endpoint: string) (model: string) : IAIProvider =
    CopilotAIProvider(secretStore, endpoint, model) :> IAIProvider

/// Directly-supplied api-key. BYOK factory path (partial-apply the endpoint).
let createWithApiKey (endpoint: string) (apiKey: string) : IAIProvider =
    CopilotAIProvider(endpoint, apiKey) :> IAIProvider

/// Directly-supplied api-key + explicit deployment name. This is the shape
/// the BYOK factory's `Build: apiKey -> model -> IAIProvider` expects once
/// the endpoint is partially applied.
let createWithApiKeyAndModel (endpoint: string) (apiKey: string) (model: string) : IAIProvider =
    CopilotAIProvider(endpoint, apiKey, model) :> IAIProvider

/// Entra ID via `DefaultAzureCredential` (managed identity in prod, `az
/// login` locally, env client-secret in CI). No static secret.
let createWithEntra (endpoint: string) : IAIProvider =
    CopilotAIProvider(endpoint, DefaultAzureCredential() :> TokenCredential) :> IAIProvider

/// Entra ID via `DefaultAzureCredential`, explicit deployment name.
let createWithEntraAndModel (endpoint: string) (model: string) : IAIProvider =
    CopilotAIProvider(endpoint, DefaultAzureCredential() :> TokenCredential, model) :> IAIProvider

/// Entra ID with a caller-supplied `TokenCredential` (e.g.
/// `ClientSecretCredential` for explicit client-credentials, or a
/// pre-configured `DefaultAzureCredential`).
let createWithCredential (endpoint: string) (credential: TokenCredential) : IAIProvider =
    CopilotAIProvider(endpoint, credential) :> IAIProvider

/// Entra ID with a caller-supplied credential + explicit deployment name.
let createWithCredentialAndModel (endpoint: string) (credential: TokenCredential) (model: string) : IAIProvider =
    CopilotAIProvider(endpoint, credential, model) :> IAIProvider