module CopilotAIProvider

open System
open System.Net.Http
open Azure.Core
open Azure.Identity
open ToolUp.AI
open ToolUp.AI.Wire
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets

// ─── Azure OpenAI ("Microsoft Copilot") as a connector ───────────
//
// Azure OpenAI's chat/completions surface is wire-identical to OpenAI's
// (request body, response shape, SSE frames), so this connector runs on the
// OpenAI wire mapping (`OpenAIProvider.Wire.fs` is compiled into this
// assembly — see the `.fsproj` link) and carries only the three deltas that
// make it Azure:
//
//   1. Endpoint — per resource: the absolute URL
//      `{endpoint}/openai/deployments/{deployment}/chat/completions
//      ?api-version={apiVersion}`, so the shared client has no `BaseAddress`.
//   2. Auth — a static `api-key` header (secret store or direct) or a
//      Microsoft Entra ID bearer token from an `Azure.Core` `TokenCredential`
//      (scope `https://cognitiveservices.azure.com/.default`).
//   3. "Model" is the Azure deployment name (in the URL); the body's `model`
//      field is accepted and ignored by Azure.
//
// DisplayName is "Microsoft Copilot" (the client-facing brand); the stable
// `ProviderId` is the accurate `azure-openai`.

/// Default deployment name: the name the customer gave their deployment,
/// commonly the model id.
[<Literal>]
let DefaultModel = "gpt-4o"

/// Known-good deployment names surfaced by the settings UI.
let KnownModels = [ "gpt-4o"; "gpt-4o-mini"; "gpt-4.1"; "o1"; "o1-mini" ]

/// Stable provider identifier.
[<Literal>]
let ProviderId = "azure-openai"

/// Default Azure OpenAI REST api-version: GA, with tools and structured
/// outputs (`response_format: json_schema`).
[<Literal>]
let DefaultApiVersion = "2024-10-21"

/// Entra ID token scope for the Azure OpenAI (Cognitive Services) data plane.
[<Literal>]
let CognitiveServicesScope = "https://cognitiveservices.azure.com/.default"

/// Secret-store key name for the api-key auth path (`_platform` scope).
[<Literal>]
let SecretKeyName = "AZURE_OPENAI_API_KEY"

// One shared client per process, built through the platform client factory
// so the egress policy handler sits in front of it. No `BaseAddress`: every
// request uses an absolute per-resource URL.
let private sharedClient =
    lazy
        (let c =
            ToolUp.Platform.Compose.PlatformHttpClient.create ToolUp.Platform.EgressSurface.AIProvider

         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

/// How each request is authenticated.
type CopilotAuth =
    /// An `api-key` header, read on every call.
    | ApiKeyAuth of fetchKey: (unit -> Async<string option>)
    /// A Microsoft Entra ID bearer token from `credential`.
    | EntraAuth of credential: TokenCredential

/// Whether this connector can serve `modelId` at all: any deployment name
/// that does not carry another vendor's family prefix (`claude-…`,
/// `gemini-…`). A vocabulary test, not a probe.
let canServeModel (modelId: string) : bool =
    ModelIdFamily.isOpenAICompatible modelId

[<Literal>]
let private UnservedReason =
    "not an Azure OpenAI deployment name (carries another vendor's family prefix)"

/// Resolve the model one call runs on (see `ModelOverrideOutcome.resolve`).
let resolveCallModel (configured: string) (options: AIProviderCallOptions) : string * ModelOverrideOutcome =
    ModelOverrideOutcome.resolve canServeModel UnservedReason configured options

let private credential (auth: CopilotAuth) : unit -> Async<Result<(string * string) list, AIProviderError>> =
    match auth with
    | ApiKeyAuth fetchKey ->
        Connector.secretHeader
            fetchKey
            (sprintf
                "%s not configured. Set it in your secret store, supply it directly, or use Entra ID auth (a TokenCredential)."
                SecretKeyName)
            (fun key -> [ "api-key", key ])
    | EntraAuth tokenCredential ->
        fun () -> async {
            try
                let! ct = Async.CancellationToken
                let request = TokenRequestContext [| CognitiveServicesScope |]
                let! token = tokenCredential.GetTokenAsync(request, ct).AsTask() |> Async.AwaitTask
                return Ok [ "Authorization", sprintf "Bearer %s" token.Token ]
            with ex ->
                return
                    Error(TransientNetwork(sprintf "Entra ID token acquisition failed for Azure OpenAI: %s" ex.Message))
        }

/// The Azure OpenAI connector over `client` for the resource at `endpoint`
/// and `apiVersion`, authenticated by `auth`.
let connector
    (client: HttpClient)
    (endpoint: string)
    (apiVersion: string)
    (auth: CopilotAuth)
    : Connector<OpenAIStreaming.StreamState> =
    let resource = endpoint.TrimEnd('/')

    {
        Capabilities =
            fun model -> {
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                SupportsTriage = true
                TriageModelId = Some "gpt-4o-mini"
                ProviderName = ProviderId
                Model = model
            }
        CanServe = canServeModel
        UnservedReason = UnservedReason
        IsVisionCapable = OpenAIProviderWire.isVisionCapable
        StructuredOutput = StructuredOutput.Native
        Client = client
        Credential = credential auth
        Endpoint =
            fun call ->
                Endpoint.At(
                    sprintf "%s/openai/deployments/%s/chat/completions?api-version=%s" resource call.Model apiVersion
                )
        Body =
            fun call ->
                OpenAIProviderWire.callBody
                    call.Model
                    call.Messages
                    call.Tools
                    call.SystemPrompt
                    call.Streaming
                    call.Schema
        ParseResponse = fun _ body -> OpenAIProviderWire.parseResponse body
        Stream = fun _ -> OpenAIStreaming.processor
        RateLimitFloor = None
        Pause = Connector.sleep
    }

/// An Azure OpenAI provider for deployment `model` of the resource at
/// `endpoint`, on `apiVersion`, authenticated by `auth`.
let createWithAuth (endpoint: string) (apiVersion: string) (auth: CopilotAuth) (model: string) : IAIProvider =
    ConnectorProvider(connector sharedClient.Value endpoint apiVersion auth, model) :> IAIProvider

let private fromSecretStore (secretStore: ISecretStore) =
    ApiKeyAuth(fun () -> secretStore.GetSecret(ScopeKey.PlatformContainer, SecretKeyName))

let private fromKey (apiKey: string) =
    ApiKeyAuth(fun () -> async { return Some apiKey })

/// Api-key from the secret store (`AZURE_OPENAI_API_KEY`, `_platform`).
let create (secretStore: ISecretStore) (endpoint: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (fromSecretStore secretStore) DefaultModel

/// Api-key from the secret store, explicit deployment name.
let createWithModel (secretStore: ISecretStore) (endpoint: string) (model: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (fromSecretStore secretStore) model

/// Directly-supplied api-key. BYOK factory path (partial-apply the endpoint).
let createWithApiKey (endpoint: string) (apiKey: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (fromKey apiKey) DefaultModel

/// Directly-supplied api-key + explicit deployment name: the shape the BYOK
/// factory's `Build: apiKey -> model -> IAIProvider` expects once the
/// endpoint is partially applied.
let createWithApiKeyAndModel (endpoint: string) (apiKey: string) (model: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (fromKey apiKey) model

/// Entra ID via `DefaultAzureCredential` (managed identity in prod, `az
/// login` locally, env client-secret in CI). No static secret.
let createWithEntra (endpoint: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (EntraAuth(DefaultAzureCredential())) DefaultModel

/// Entra ID via `DefaultAzureCredential`, explicit deployment name.
let createWithEntraAndModel (endpoint: string) (model: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (EntraAuth(DefaultAzureCredential())) model

/// Entra ID with a caller-supplied `TokenCredential`.
let createWithCredential (endpoint: string) (credential: TokenCredential) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (EntraAuth credential) DefaultModel

/// Entra ID with a caller-supplied credential + explicit deployment name.
let createWithCredentialAndModel (endpoint: string) (credential: TokenCredential) (model: string) : IAIProvider =
    createWithAuth endpoint DefaultApiVersion (EntraAuth credential) model