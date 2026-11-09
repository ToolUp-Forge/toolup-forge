module ClaudeAIProvider

open System
open System.Net.Http
open ToolUp.AI
open ToolUp.AI.Wire
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Secrets
open ClaudeAIProviderWire

// ─── The Anthropic Messages API as a connector ──────────────────
//
// Everything Anthropic-specific is in the `Connector` below: the route, the
// `x-api-key` credential, the body (prompt-caching markers, the effort, the
// forced-tool structured-output workaround), the parse, the stream fold and
// the 15 s rate-limit floor. The call itself is `ProviderCall.send`.

/// Default Claude model. Haiku rather than Sonnet: Anthropic's rate limits
/// are materially higher for Haiku at every account tier, which keeps
/// development and demo deployments unblocked. Users who want Sonnet / Opus
/// override per user (`IProviderProfile`) or per conversation.
[<Literal>]
let DefaultModel = "claude-haiku-4-5-20251001"

/// Default response token cap.
[<Literal>]
let DefaultMaxTokens = 4096

/// Known-good model identifiers surfaced by the settings UI. Users may enter
/// custom strings for models released after the build. Current Anthropic ids
/// are date-suffix-free aliases except Haiku 4.5, whose canonical full id
/// retains the date.
let KnownModels = [
    "claude-haiku-4-5-20251001"
    "claude-sonnet-4-6"
    "claude-opus-4-8"
    "claude-fable-5"
]

/// Stable provider identifier. The factory and user-stored configs use this
/// as the key.
[<Literal>]
let ProviderId = "anthropic-claude"

/// Anthropic's per-minute buckets are not refilled by a sub-second pause: a
/// 429 waits at least this long before the first retry (twice it before the
/// second, …). Anthropic does not send `retry-after` on 429s reliably.
let RateLimitFloor = TimeSpan.FromSeconds 15.0

// One shared client per process: `BaseAddress` and `Timeout` are stable, the
// per-request headers ride the request message, and a client per call would
// exhaust sockets. Built through the platform client factory, so the egress
// policy handler sits in front of it.
let private sharedClient =
    lazy
        (let c =
            ToolUp.Platform.Compose.PlatformHttpClient.create ToolUp.Platform.EgressSurface.AIProvider

         c.BaseAddress <- Uri("https://api.anthropic.com")
         c.Timeout <- TimeSpan.FromMinutes(5.0)
         c)

/// Whether this connector can serve `modelId` at all: an Anthropic id
/// (`claude-…`). A vocabulary test, not a probe.
let canServeModel (modelId: string) : bool = ModelIdFamily.isAnthropic modelId

[<Literal>]
let private UnservedReason = "not an Anthropic (claude-*) model id"

/// Resolve the model one call runs on (see `ModelOverrideOutcome.resolve`).
let resolveCallModel (configured: string) (options: AIProviderCallOptions) : string * ModelOverrideOutcome =
    ModelOverrideOutcome.resolve canServeModel UnservedReason configured options

/// The reply cap one call is sent with: `options.MaxTokens` when it names a
/// positive budget, else the provider's constructed cap.
let resolveCallMaxTokens (configured: int) (options: AIProviderCallOptions) : int =
    match options.MaxTokens with
    | Some budget when budget > 0 -> budget
    | _ -> configured

/// The effort one call is sent with: `options.Effort` when it names one.
/// Otherwise the provider's configured effort, but only on the configured
/// model: that setting was chosen for that model, and a call moved to another
/// one (a triage or title call on a cheaper id) may be on a model that
/// rejects it.
let resolveCallEffort
    (configured: string option)
    (configuredModel: string)
    (servedModel: string)
    (options: AIProviderCallOptions)
    : string option =
    match options.Effort with
    | Some effort when not (String.IsNullOrWhiteSpace effort) -> Some effort
    | _ when servedModel = configuredModel -> configured
    | _ -> None

/// Anthropic has no native structured-output mode: the body forces a
/// synthesised `structured_response` tool whose `input_schema` is the schema,
/// and the tool call's `input` IS the structured response. It runs through
/// the deterministic normalisation (Claude sometimes wraps it in an
/// `{ "input": … }` envelope or emits it as a JSON-encoded string, and
/// Anthropic does not validate tool inputs server-side) and becomes
/// `Content`, so callers see the shape the native paths return.
let private structuredReply
    (schema: string)
    (parsed: AIProviderResponse)
    : Result<AIProviderResponse, AIProviderError> =
    match
        parsed.ToolCalls
        |> List.tryFind (fun tc -> tc.Name = StructuredResponseToolName)
    with
    | Some call ->
        Ok {
            parsed with
                Content = normaliseStructuredPayload schema call.Arguments
                ToolCalls =
                    parsed.ToolCalls
                    |> List.filter (fun tc -> tc.Name <> StructuredResponseToolName)
                StopReason = StopReason.EndTurn
        }
    | None ->
        Error(
            SchemaUnsupported(
                "structured-output",
                "Claude did not return the forced structured_response tool call. This is unexpected — the request was issued with tool_choice forcing the call."
            )
        )

/// The Anthropic connector over `client`, credentialled by `apiKey` (read on
/// every call, so a rotated key flows through), sending `maxTokens` (a value
/// below 1 is the default) and `effort` (`None` sends none) unless a call
/// names its own.
let connector
    (client: HttpClient)
    (apiKey: unit -> Async<string option>)
    (maxTokens: int)
    (effort: string option)
    : Connector<ClaudeStreaming.StreamState> =
    let maxTokens = if maxTokens > 0 then maxTokens else DefaultMaxTokens

    {
        Capabilities =
            fun model -> {
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                SupportsTriage = true
                // Also `DefaultModel`: on an untuned deployment the triage and
                // agent models coincide, as there is no cheaper tier.
                TriageModelId = Some DefaultModel
                ProviderName = ProviderId
                Model = model
            }
        CanServe = canServeModel
        UnservedReason = UnservedReason
        IsVisionCapable = isVisionCapable
        StructuredOutput = StructuredOutput.Native
        Client = client
        Credential =
            Connector.secretHeader
                apiKey
                "ANTHROPIC_API_KEY not configured. Set it as an environment variable or in your secret store."
                (fun key -> [ "x-api-key", key; "anthropic-version", "2023-06-01" ])
        Endpoint = fun _ -> Endpoint.At "/v1/messages"
        Body =
            fun call ->
                Ok(
                    buildRequestBodyWithEffort
                        (resolveCallEffort effort call.ConfiguredModel call.Model call.Options)
                        call.Model
                        (resolveCallMaxTokens maxTokens call.Options)
                        call.Messages
                        call.Tools
                        call.SystemPrompt
                        call.Streaming
                        call.Schema
                )
        ParseResponse =
            fun call body ->
                match call.Schema with
                | None -> parseResponse body
                | Some schema -> parseResponse body |> Result.bind (structuredReply schema)
        Stream = fun _ -> ClaudeStreaming.processor
        RateLimitFloor = Some RateLimitFloor
        Pause = Connector.sleep
    }

let private fromSecretStore (secretStore: ISecretStore) =
    fun () -> secretStore.GetSecret(ScopeKey.PlatformContainer, "ANTHROPIC_API_KEY")

let private fromKey (apiKey: string) = fun () -> async { return Some apiKey }

/// A Claude provider on `model`, its key read through `apiKey` on every call,
/// capped at `maxTokens` and run at `effort`.
let createWithKeySource
    (apiKey: unit -> Async<string option>)
    (model: string)
    (maxTokens: int)
    (effort: string option)
    : IAIProvider =
    ConnectorProvider(connector sharedClient.Value apiKey maxTokens effort, model) :> IAIProvider

/// Read `ANTHROPIC_API_KEY` from the `_platform` scope on every call (so a
/// rotated key flows through), on the default model.
let create (secretStore: ISecretStore) : IAIProvider =
    createWithKeySource (fromSecretStore secretStore) DefaultModel DefaultMaxTokens None

/// The secret-store path on `model`.
let createWithModel (secretStore: ISecretStore) (model: string) : IAIProvider =
    createWithKeySource (fromSecretStore secretStore) model DefaultMaxTokens None

/// The secret-store path on `model`, capped at `maxTokens`, at `effort`.
let createWithSecretStore
    (secretStore: ISecretStore)
    (model: string)
    (maxTokens: int)
    (effort: string option)
    : IAIProvider =
    createWithKeySource (fromSecretStore secretStore) model maxTokens effort

/// A directly supplied key: the BYOK factory path, where the factory resolves
/// the user's or team's key once per `Resolve`.
let createWithApiKey (apiKey: string) : IAIProvider =
    createWithKeySource (fromKey apiKey) DefaultModel DefaultMaxTokens None

/// A directly supplied key on `model`.
let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    createWithKeySource (fromKey apiKey) model DefaultMaxTokens None

/// A directly supplied key on `model` at `effort` (`None` sends none), for a
/// descriptor's `Build`.
let createWithApiKeyModelAndEffort (apiKey: string) (model: string) (effort: string option) : IAIProvider =
    createWithKeySource (fromKey apiKey) model DefaultMaxTokens effort

/// A directly supplied key on `model`, capped at `maxTokens`, at `effort`.
let createWithApiKeyModelMaxTokensAndEffort
    (apiKey: string)
    (model: string)
    (maxTokens: int)
    (effort: string option)
    : IAIProvider =
    createWithKeySource (fromKey apiKey) model maxTokens effort