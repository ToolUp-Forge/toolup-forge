# AI provider companions

The Platform's `IAIProvider` interface is the boundary between `ToolUp.AI`'s agent loop and specific LLM vendors. Each provider companion implements `IAIProvider` and exposes an `AIProviderBuilder` for the BYOK-capable factory.

For full details on the `IAIProvider` contract, agent loop, system-prompt composition, and tool registry, see [`ai/README.md`](../ai/README.md) + [`ai/concepts.md`](../ai/concepts.md). For provider authoring guide, see [`ai/extending.md`](../ai/extending.md).

## What's shipped

| Companion | Vendor | Default model |
|---|---|---|
| `ToolUp.AIProviders.Claude` | Anthropic | `claude-opus-4-1-20250109` (configurable) |
| `ToolUp.AIProviders.OpenAI` | OpenAI | `gpt-4o` (configurable) |
| `ToolUp.AIProviders.Gemini` | Google | `models/gemini-2.5-flash` (configurable) |

All three are BYOK-capable — API keys resolved per call from `ISecretStore`, never hardcoded.

## Picking a provider

Claude, OpenAI, and Gemini cover the same core capabilities (multi-turn chat, tool use, streaming, multimodal input). Picking between them is usually a business decision (vendor relationship, cost structure, model strengths) rather than a technical one.

### `ToolUp.AIProviders.Claude` (Anthropic)

Use when:
- You're invested in Anthropic's vision / pricing.
- You want explicit-marker prompt caching (Claude's caching is opt-in via `cache_control: { type: "ephemeral" }` markers — the SDK marks them automatically).
- You need 200K-context models (Claude Opus / Sonnet 4.x).
- Streaming tool use without sacrificing intermediate-result streaming.

Capabilities:
- `SupportsStreaming = true` — SSE; emits incremental tokens.
- `SupportsToolUse = true` — multi-turn tool calling.
- `SupportsVision = true` — but the SDK's multimodal protocol isn't yet wired (declared capability not yet active).
- `SupportsPromptCaching = true` — explicit `cache_control` markers; the SDK marks system prompt + tools + conversation prefix.

Setup:

```fsharp
open ToolUp.AIProviders.Claude

let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder ]
        aiConfigStore
        secretStore
        PlatformOnly
```

Store API key under `_platform` scope, key name `ANTHROPIC_API_KEY`. The provider reads per-call.

Model selection: pass model name in the builder. Default is `claude-opus-4-1-20250109`. Other models (Sonnet, Haiku) via `Build` parameter:

```fsharp
let builder = {
    Descriptor = ClaudeAIProvider.descriptor
    Build = fun apiKey model -> ClaudeAIProvider.createWithApiKeyAndModel apiKey model
}
```

The factory invokes the builder per-call with the configured `(apiKey, model)`. Users can change model via the AI Settings UI when `BYOKMode = AllowUserProviders`.

### `ToolUp.AIProviders.OpenAI` (OpenAI)

Use when:
- You're invested in OpenAI's ecosystem.
- You need GPT-4o's image / audio modalities (when the SDK wires multimodal — currently deferred).
- You want automatic prompt caching (OpenAI caches automatically; no markers needed).
- Lower latency on smaller models for non-critical paths.

Capabilities:
- `SupportsStreaming = true` — SSE; emits incremental tokens + usage chunk on `[DONE]`.
- `SupportsToolUse = true` — function calling.
- `SupportsVision = true` — declared, awaiting SDK multimodal wire protocol.
- `SupportsPromptCaching = true` — automatic; cached-token counts reported via `stream_options.include_usage`.

Setup:

```fsharp
open ToolUp.AIProviders.OpenAI

let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ OpenAIProvider.builder ]
        aiConfigStore
        secretStore
        PlatformOnly
```

Store API key under `_platform` scope, key name `OPENAI_API_KEY`.

### `ToolUp.AIProviders.Gemini` (Google)

Use when:
- You're invested in Google's AI stack (or Workspace / GCP-native deployments).
- You need long-context models (Gemini 1.5 Pro accepts 1M-token contexts; 2.5 Pro extends the same family).
- You want JSON-Schema-validated structured output via the `responseSchema` generation-config option.
- Multimodal input (image / audio / video) is core to your workflow — every 1.5 / 2.5 model is multimodal by default.

Capabilities:
- `Streaming = true` — SSE via `:streamGenerateContent?alt=sse`; emits incremental tokens + tool-call parts.
- `ToolUse = true` — `functionDeclarations` + `functionCall` / `functionResponse` parts (no per-call ids — the provider synthesises stable correlations).
- `Vision = true` — multimodal is the default; image / audio / video parts ride on `inlineData` or `fileData`.
- `SupportsPromptCaching = true` — surfaces `cachedContentTokenCount` when present (request-side cache management via Gemini's explicit `cachedContents` API is not yet wired here).

Setup:

```fsharp
open GeminiAIProvider

let geminiBuilder = {
    Descriptor = {
        Id = ProviderId
        DisplayName = "Google Gemini"
        SupportedModels = KnownModels
        DefaultModel = DefaultModel
        Capabilities = {
            AIProviderCapabilities.unknown with
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                ProviderName = "google-gemini"
                Model = DefaultModel
        }
    }
    Build = fun apiKey model -> createWithApiKeyAndModel apiKey model
}

let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ geminiBuilder ]
        providerProfile
        secretStore
        PlatformOnly
        None
```

Store API key under `_platform` scope, key name `GEMINI_API_KEY`. Endpoint targets `generativelanguage.googleapis.com` (v1beta); Vertex AI managed endpoints are out of scope for this package — see `ToolUp.AIProviders.GoogleVertex` (when shipped) for that path.

## Using multiple providers

The `DefaultAIProviderFactory` accepts a list of builders. Users (or the platform default) pick the active provider:

```fsharp
let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder
          OpenAIProvider.builder ]
        aiConfigStore
        secretStore
        AllowUserProviders
```

With `BYOKMode = AllowUserProviders`:
1. Each user can register their own `AIProviderInstance` via the AI Settings UI.
2. Per request, the factory looks up the active instance from `IUserAIConfigStore`.
3. The factory picks the matching builder by `ProviderId`.
4. The factory pulls the API key from `ISecretStore` (user's encrypted key).
5. The builder instantiates a provider with `(apiKey, model)`.

Deployment defaults: `PlatformOnly` mode uses the platform's `_platform`-scoped key for every user. `AllowUserProviders` falls back to the platform default when the user hasn't configured one.

## Operator config — startup validation env vars

Two always-on `IConfigValidator`s and one opt-in network probe catch operator typos at startup before the first chat request lands. All three self-skip when their gating env var is unset — zero cost for deployments that don't rely on them.

| Env var | Purpose | Outcome on mismatch |
|---|---|---|
| `TOOLUP_AI_PROVIDER` | Declares which AI provider id the operator intended (e.g. `anthropic-claude`, `openai-gpt`, `google-gemini`). Validated against `IAIProviderFactory.Available ∪ PlatformDescriptor`. | `Warning` naming the known provider ids. Runtime behaviour unchanged — a typo today silently falls through to the platform fallback. |
| `TOOLUP_AI_MODEL` | Declares the intended model id. Validated against the matching descriptor's `SupportedModels` (+ `DefaultModel`). When `TOOLUP_AI_PROVIDER` is also set, the check scopes to that provider; otherwise the check spans every known descriptor. | `Warning` naming the known models. Upstream will reject the call (HTTP 400 / 404) on the first chat request, but the validator surfaces the typo before any user hits it. |
| `TOOLUP_AI_PROBE_ON_STARTUP=1` | **Opt-in.** When `1`, the SDK runs a one-shot `GET /v1/models` (Anthropic / OpenAI) or `GET /v1beta/models` (Gemini) against the resolved provider using the API key from its documented env var (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY`). | `Warning` if the key is refused (HTTP 401 / 403) or if the configured model isn't in the list this key can access. `Error` (startup abort) if the endpoint is unreachable (DNS / network / 5xx) — a clear deploy failure. Unknown provider id → `Warning` ("probe has no built-in spec"). |

The probe stays off by default because many deployments prefer no outbound calls at boot (cold-start latency, sandboxed CI, etc.). Operators who want fail-fast detection of refused keys / unreachable upstreams flip the env var on for their `production` profile.

Validator outcomes are visible in the startup log (`[preflight] ai-provider-env: Warning — ...`), the `/dev/inspect` Validators panel (when `ServerConfig.EnableDevEndpoints = true`), and any registered `IConfigValidator`-watching health surface.

## Common configuration

Both providers share fields on `AIProviderRequest`:
- `SystemPrompt: string` — composed via `SystemPromptBuilder`.
- `Messages: AIProviderMessage list` — conversation history + current user message.
- `Tools: AIProviderToolDef list` — registered tools translated to vendor's tool schema.
- `MaxTokens: int` — default 4096; tune via `AIAssistantServerConfig.DefaultMaxTokens`.
- `Temperature: float` — default 0.7; tune via `AIAssistantServerConfig.DefaultTemperature`.
- `Stream: bool` — `true` for SSE streaming; `false` for buffered response.

Token usage reporting populates `AIProviderResponse.Usage`:

```fsharp
type TokenUsage = {
    PromptTokens: int           // input tokens
    CachedPromptTokens: int     // input tokens that hit the cache
    OutputTokens: int           // model output tokens
    CacheCreationTokens: int option  // Anthropic-specific cache-write cost
}
```

Both providers report all fields. Cache-creation tokens are zero for OpenAI (caching is implicit) and non-zero for Claude on the first request that creates a cache marker.

## Cost / latency observability

Each agent turn emits an `AILatencyRecord` to `IEventStore` under `_platform.ai.latency`:

```fsharp
type AILatencyRecord = {
    TaskId: Guid
    ConversationId: Guid
    TurnNumber: int
    ProviderName: string         // "claude" or "openai"
    ProviderModel: string        // "claude-opus-4-1-20250109", "gpt-4o", etc.
    TtftMs: int option           // time-to-first-token (streaming only)
    TurnDurationMs: int
    ToolCalls: ToolCallTiming list
    StopReason: StopReason
    Usage: TokenUsage option
}
```

`/dev/ai-latency` (when `EnableDevEndpoints`) shows rolling 60-min p50/p95/p99 per provider/model + `CacheHitRate`. Use for cost analysis (token-usage / model) + latency analysis (TTFT / turn duration).

For production observability, the same data flows through `IMetricsSink` (Prometheus + OpenTelemetry).

## Writing a new provider

For a vendor not covered (Mistral, DeepSeek, Cohere, custom in-house LLM):

```fsharp
module MyVendor.AIProvider

let descriptor = {
    Id = "myvendor"
    DisplayName = "MyVendor AI"
    DefaultModel = "myvendor-pro-1"
    Capabilities = {
        ProviderName = "myvendor"
        Model = ""    // overridden by builder
        SupportsStreaming = true
        SupportsToolUse = true
        SupportsVision = false
        SupportsPromptCaching = false
    }
}

let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    MyVendorProvider(apiKey, model, httpClient) :> _

let builder = {
    Descriptor = descriptor
    Build = createWithApiKeyAndModel
}
```

Wire into the factory:

```fsharp
let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder
          OpenAIProvider.builder
          MyVendor.AIProvider.builder ]
        aiConfigStore
        secretStore
        AllowUserProviders
```

See [`ai/extending.md`](../ai/extending.md) for the full provider authoring guide:
- Streaming wire-format parsing.
- Token usage reporting.
- Prompt caching markers (vendor-specific).
- Capability flag declarations.
- `IHealthCheck` + `IConfigValidator` self-registration.
- Conformance test suite.

## Hardening checklist for production

- API keys stored in `ISecretStore` (never hardcoded, never env-var-only).
- `BYOKMode = AllowUserProviders` for deployments where users should supply their own keys (cost-attribution).
- Per-user / per-team rate limits via `ServerConfig.RateLimit`.
- Per-tenant cost ceilings via custom middleware reading `AILatencyRecord` events.
- `AIServerApp.withConfigValidator` for provider preflight probes — `ClaudeAIProviderValidator` / `OpenAIProviderValidator` (when shipped per-provider).
- `/health/ai` probes per-provider — verifies API key + endpoint reachability.
- `Anonymous` mode + AI is a cost-control red flag; see [`ai/README.md`](../ai/README.md) "When NOT to use this companion".

## Cost-control patterns

- **`MaxTurns`** — caps the agent loop iterations per chat. Default 10; tune lower for cost-sensitive deployments.
- **Token-usage caps** — middleware that short-circuits before hitting the provider when the user/team's daily/monthly cap is exceeded. Build atop `AILatencyRecord` events.
- **Cheaper models for non-critical paths** — use Haiku / GPT-4o-mini for tool dispatch in modules where Opus / GPT-4o would be overkill.
- **Cached system prompts** — long system prompts that don't change across users (platform-layer prompts) benefit most from caching. Make module-private prompts shorter than platform-shared ones to maximise hit rate.
