module OpenAIProvider

open System
open System.Net.Http
open ToolUp.AI
open ToolUp.AI.Wire
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets

// ─── OpenAI chat/completions as a connector ──────────────────────
//
// Everything OpenAI-specific is in the `Connector` below: the route, the
// bearer credential, the body (`OpenAIProviderWire.callBody`), the parse and
// the stream fold. The call itself is `ProviderCall.send`.

/// Default OpenAI model.
[<Literal>]
let DefaultModel = "gpt-4o"

/// Known-good model identifiers surfaced by the settings UI.
let KnownModels = [ "gpt-4o"; "gpt-4o-mini"; "gpt-4-turbo"; "o1"; "o1-mini" ]

/// Stable provider identifier, the factory's and stored configs' key.
[<Literal>]
let ProviderId = "openai-gpt"

// One shared client per process (see `ClaudeAIProvider`), built through the
// platform client factory so the egress policy handler sits in front of it.
let private sharedClient =
    lazy
        (let c =
            ToolUp.Platform.Compose.PlatformHttpClient.create ToolUp.Platform.EgressSurface.AIProvider

         c.BaseAddress <- Uri("https://api.openai.com")
         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

/// Whether this connector can serve `modelId` at all: any id that does not
/// carry another vendor's family prefix (`claude-…`, `gemini-…`). A
/// vocabulary test, not a probe.
let canServeModel (modelId: string) : bool =
    ModelIdFamily.isOpenAICompatible modelId

[<Literal>]
let private UnservedReason =
    "not an OpenAI model id (carries another vendor's family prefix)"

/// Resolve the model one call runs on (see `ModelOverrideOutcome.resolve`).
let resolveCallModel (configured: string) (options: AIProviderCallOptions) : string * ModelOverrideOutcome =
    ModelOverrideOutcome.resolve canServeModel UnservedReason configured options

/// The OpenAI connector over `client`, credentialled by `apiKey` (read on
/// every call, so a rotated key flows through).
let connector (client: HttpClient) (apiKey: unit -> Async<string option>) : Connector<OpenAIStreaming.StreamState> = {
    Capabilities =
        fun model -> {
            Streaming = true
            ToolUse = true
            Vision = true
            // Prompt caching is automatic above ~1024 tokens; the cached
            // portion is reported on `usage.prompt_tokens_details`.
            SupportsPromptCaching = true
            SupportsTriage = true
            TriageModelId = Some "gpt-4o-mini"
            ProviderName = "openai"
            Model = model
        }
    CanServe = canServeModel
    UnservedReason = UnservedReason
    IsVisionCapable = OpenAIProviderWire.isVisionCapable
    StructuredOutput = StructuredOutput.Native
    Client = client
    Credential =
        Connector.secretHeader
            apiKey
            "OPENAI_API_KEY not configured. Set it as an environment variable or in your secret store."
            (fun key -> [ "Authorization", $"Bearer {key}" ])
    Endpoint = fun _ -> Endpoint.At "/v1/chat/completions"
    Body =
        fun call ->
            OpenAIProviderWire.callBody call.Model call.Messages call.Tools call.SystemPrompt call.Streaming call.Schema
    ParseResponse = fun _ body -> OpenAIProviderWire.parseResponse body
    Stream = fun _ -> OpenAIStreaming.processor
    RateLimitFloor = None
    Pause = Connector.sleep
}

/// An OpenAI provider on `model`, its key read through `apiKey` on every call.
let createWithKeySource (apiKey: unit -> Async<string option>) (model: string) : IAIProvider =
    ConnectorProvider(connector sharedClient.Value apiKey, model) :> IAIProvider

let private fromSecretStore (secretStore: ISecretStore) =
    fun () -> secretStore.GetSecret(ScopeKey.PlatformContainer, "OPENAI_API_KEY")

/// Read `OPENAI_API_KEY` from the `_platform` scope on every call.
let create (secretStore: ISecretStore) : IAIProvider =
    createWithKeySource (fromSecretStore secretStore) DefaultModel

/// The secret-store path on `model`.
let createWithModel (secretStore: ISecretStore) (model: string) : IAIProvider =
    createWithKeySource (fromSecretStore secretStore) model

/// A directly supplied key: the BYOK factory path.
let createWithApiKey (apiKey: string) : IAIProvider =
    createWithKeySource (fun () -> async { return Some apiKey }) DefaultModel

/// A directly supplied key on `model`.
let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    createWithKeySource (fun () -> async { return Some apiKey }) model