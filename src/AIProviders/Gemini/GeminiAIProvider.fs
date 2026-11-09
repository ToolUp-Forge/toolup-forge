module GeminiAIProvider

open System
open System.Net.Http
open ToolUp.AI
open ToolUp.AI.Wire
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets

// ─── Gemini generateContent as a connector ───────────────────────
//
// Everything Gemini-specific is in the `Connector` below: the per-model
// routes, the `x-goog-api-key` credential, the body
// (`GeminiAIProviderWire.callBody`), the parse and the stream fold. The call
// itself is `ProviderCall.send`, so a streaming call is under the same
// per-call timeout as a buffered one.

/// Default Gemini model. Flash is the lower-cost, lower-latency tier of the
/// 2.5 family; Pro is the larger sibling.
[<Literal>]
let DefaultModel = "models/gemini-2.5-flash"

/// Known-good model identifiers surfaced by the settings UI. Users may enter
/// custom strings for models released after the build.
let KnownModels = [
    "models/gemini-2.5-pro"
    "models/gemini-2.5-flash"
    "models/gemini-1.5-pro"
    "models/gemini-1.5-flash"
]

/// Stable provider identifier used in user configs and secret-store key names.
[<Literal>]
let ProviderId = "google-gemini"

/// Secret-store key the secret-store path reads on every call.
[<Literal>]
let SecretKeyName = "GEMINI_API_KEY"

/// The path segment Gemini's REST API expects (`models/gemini-…`), from
/// either form of the id.
let private modelPath (model: string) =
    if model.StartsWith("models/") then
        model
    else
        "models/" + model

// One shared client per process (see `ClaudeAIProvider`), built through the
// platform client factory so the egress policy handler sits in front of it.
let private sharedClient =
    lazy
        (let c =
            ToolUp.Platform.Compose.PlatformHttpClient.create ToolUp.Platform.EgressSurface.AIProvider

         c.BaseAddress <- Uri("https://generativelanguage.googleapis.com")
         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

/// Whether this connector can serve `modelId` at all: a Google id
/// (`gemini-…` / `gemma-…`, with or without the `models/` prefix). A
/// vocabulary test, not a probe.
let canServeModel (modelId: string) : bool = ModelIdFamily.isGoogle modelId

[<Literal>]
let private UnservedReason = "not a Google (gemini-*/gemma-*) model id"

/// Resolve the model one call runs on (see `ModelOverrideOutcome.resolve`).
let resolveCallModel (configured: string) (options: AIProviderCallOptions) : string * ModelOverrideOutcome =
    ModelOverrideOutcome.resolve canServeModel UnservedReason configured options

/// The Gemini connector over `client`, credentialled by `apiKey` (read on
/// every call, so a rotated key flows through).
let connector (client: HttpClient) (apiKey: unit -> Async<string option>) : Connector<GeminiStreaming.StreamState> = {
    Capabilities =
        fun model -> {
            Streaming = true
            ToolUse = true
            Vision = true
            SupportsPromptCaching = true
            SupportsTriage = true
            TriageModelId = Some "models/gemini-2.5-flash"
            ProviderName = ProviderId
            Model = model
        }
    CanServe = canServeModel
    UnservedReason = UnservedReason
    IsVisionCapable = GeminiAIProviderWire.isVisionCapable
    StructuredOutput = StructuredOutput.Native
    Client = client
    Credential =
        Connector.secretHeader
            apiKey
            "GEMINI_API_KEY not configured. Set it as an environment variable or in your secret store."
            (fun key -> [ "x-goog-api-key", key ])
    Endpoint =
        fun call ->
            if call.Streaming then
                Endpoint.At(sprintf "/v1beta/%s:streamGenerateContent?alt=sse" (modelPath call.Model))
            else
                Endpoint.At(sprintf "/v1beta/%s:generateContent" (modelPath call.Model))
    Body = fun call -> GeminiAIProviderWire.callBody call.Messages call.Tools call.SystemPrompt call.Schema
    ParseResponse = fun _ body -> GeminiAIProviderWire.parseResponse body
    Stream = fun _ -> GeminiStreaming.processor
    RateLimitFloor = None
    Pause = Connector.sleep
}

/// A Gemini provider on `model`, its key read through `apiKey` on every call.
let createWithKeySource (apiKey: unit -> Async<string option>) (model: string) : IAIProvider =
    ConnectorProvider(connector sharedClient.Value apiKey, model) :> IAIProvider

let private fromSecretStore (secretStore: ISecretStore) =
    fun () -> secretStore.GetSecret(ScopeKey.PlatformContainer, SecretKeyName)

/// Read `GEMINI_API_KEY` from the `_platform` scope on every call.
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