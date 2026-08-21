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
- `Streaming = true` — SSE; emits incremental tokens.
- `ToolUse = true` — multi-turn tool calling.
- `Vision = true` — multipart image input (Phase 6o); model ids the provider classifies as non-vision reject synchronously with `UnsupportedCapability("vision", …)`.
- `SupportsPromptCaching = true` — explicit `cache_control` markers; the SDK marks system prompt + tools + conversation prefix.
- `SupportsTriage = true` — the tool-based structured-output workaround serves the triage tier; `TriageModelId` names the Haiku-grade id.

Setup:

Each provider package exposes its identifiers (`ProviderId`, `DefaultModel`, `KnownModels`) and its constructor (`createWithApiKeyAndModel`); the app assembles the `AIProviderBuilder` at compose time, so a provider package never takes a dependency on `ToolUp.AI`:

```fsharp
open ToolUp.Platform.AI
open ToolUp.AI

let claudeBuilder: AIProviderBuilder = {
    Descriptor = {
        Id = ClaudeAIProvider.ProviderId
        DisplayName = "Anthropic Claude"
        SupportedModels = ClaudeAIProvider.KnownModels
        DefaultModel = ClaudeAIProvider.DefaultModel
        Capabilities = {
            AIProviderCapabilities.unknown with
                ProviderName = ClaudeAIProvider.ProviderId
                Model = ClaudeAIProvider.DefaultModel
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                SupportsTriage = true
                TriageModelId = Some ClaudeAIProvider.DefaultModel
        }
    }
    Build = ClaudeAIProvider.createWithApiKeyAndModel
}
```

Then hand the builder to the factory:

```fsharp skip=fragment
let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ claudeBuilder ]
        providerProfile      // IProviderProfile — the platform-wide BYOK store
        secretStore          // ISecretStore
        PlatformOnly         // AIFallbackPolicy
        platformProviders    // DefaultAIProviderFactory.AIPlatformProvider list
        None                 // IPlatformAIKeyStore option — auto-promoted when None
```

Store API key under `_platform` scope, key name `ANTHROPIC_API_KEY`. The provider reads per-call.

Model selection: `DefaultModel` is `claude-haiku-4-5-20251001`; the other ids the package knows are in `ClaudeAIProvider.KnownModels`. The factory invokes `Build` per-call with the configured `(apiKey, model)`, so a user can change model via the AI Settings UI under `AllowUserProviders`.

### `ToolUp.AIProviders.OpenAI` (OpenAI)

Use when:
- You're invested in OpenAI's ecosystem.
- You need GPT-4o's image input (multipart shipped in Phase 6o; audio modalities are not yet wired).
- You want automatic prompt caching (OpenAI caches automatically; no markers needed).
- Lower latency on smaller models for non-critical paths.

Capabilities:
- `Streaming = true` — SSE; emits incremental tokens + usage chunk on `[DONE]`.
- `ToolUse = true` — function calling.
- `Vision = true` — multipart image input (Phase 6o); non-vision model ids reject synchronously with `UnsupportedCapability("vision", …)`.
- `SupportsPromptCaching = true` — automatic; cached-token counts reported via `stream_options.include_usage`.
- `SupportsTriage = true` — native `response_format: json_schema` serves the triage tier; `TriageModelId = Some "gpt-4o-mini"`.

Setup:

```fsharp
let openAiBuilder: AIProviderBuilder = {
    Descriptor = {
        Id = OpenAIProvider.ProviderId
        DisplayName = "OpenAI"
        SupportedModels = OpenAIProvider.KnownModels
        DefaultModel = OpenAIProvider.DefaultModel
        Capabilities = {
            AIProviderCapabilities.unknown with
                ProviderName = OpenAIProvider.ProviderId
                Model = OpenAIProvider.DefaultModel
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                SupportsTriage = true
                TriageModelId = Some "gpt-4o-mini"
        }
    }
    Build = OpenAIProvider.createWithApiKeyAndModel
}
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
- `SupportsTriage = true` — the native `responseSchema` path serves the triage tier; `TriageModelId = Some "models/gemini-2.5-flash"`.

Setup:

```fsharp
let geminiBuilder: AIProviderBuilder = {
    Descriptor = {
        Id = GeminiAIProvider.ProviderId
        DisplayName = "Google Gemini"
        SupportedModels = GeminiAIProvider.KnownModels
        DefaultModel = GeminiAIProvider.DefaultModel
        Capabilities = {
            AIProviderCapabilities.unknown with
                ProviderName = GeminiAIProvider.ProviderId
                Model = GeminiAIProvider.DefaultModel
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                SupportsTriage = true
                TriageModelId = Some GeminiAIProvider.DefaultModel
        }
    }
    Build = GeminiAIProvider.createWithApiKeyAndModel
}
```

Store API key under `_platform` scope, key name `GEMINI_API_KEY`. Endpoint targets `generativelanguage.googleapis.com` (v1beta); Vertex AI managed endpoints are out of scope for this package — see `ToolUp.AIProviders.GoogleVertex` (when shipped) for that path.

## Using multiple providers

The `DefaultAIProviderFactory` accepts a list of builders. Users (or the platform default) pick the active provider:

```fsharp skip=fragment
let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ claudeBuilder; openAiBuilder ]
        providerProfile
        secretStore
        PermissiveWithPlatformFallback
        platformProviders
        None
```

`AIFallbackPolicy` decides what happens when a user or team has configured nothing:

- **`PlatformOnly`** — user and team configuration is ignored; every request uses the deployment's platform provider. The deployment carries 100% of the cost. `Available` is empty, and the settings UI surfaces a platform-provider + model picker driven by `PlatformDescriptors`.
- **`PermissiveWithPlatformFallback`** — BYOK where configured, the platform provider otherwise. Free-tier-plus-upgrade deployments.
- **`StrictBYOK`** — the platform never pays; missing configuration surfaces `ProviderResolutionError.NoProviderConfigured` to the UI.

Under the two BYOK policies, per request the factory resolves the routed entry from `IProviderProfile`, picks the matching builder by `ProviderId`, pulls the API key from `ISecretStore`, and calls `Build apiKey model`.

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
- `MaxTokens: int` — the per-call output cap the agent loop supplies.
- `Temperature: float` — sampling temperature the agent loop supplies.
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
    ProviderName: string            // "anthropic-claude", "openai-gpt", "google-gemini"
    ProviderModel: string           // "claude-haiku-4-5-20251001", "gpt-4o", etc.
    TtftMs: float option            // time-to-first-token (streaming only)
    TurnDurationMs: float
    ToolCalls: ToolCallTiming list
    StopReason: string              // "end_turn" | "tool_use" | "max_tokens" | ""
    PromptTokens: int option
    CachedPromptTokens: int option
    OutputTokens: int option
    CacheCreationTokens: int option // Anthropic-specific cache-write cost
}
```

`/dev/ai-latency` (when `EnableDevEndpoints`) shows rolling 60-min p50/p95/p99 per provider/model + `CacheHitRate`. Use for cost analysis (token-usage / model) + latency analysis (TTFT / turn duration).

For production observability, the same data flows through `IMetricsSink` (Prometheus + OpenTelemetry).

## Writing a new provider

For a vendor not covered (Mistral, DeepSeek, Cohere, custom in-house LLM):

```fsharp skip=fragment
module MyVendor.AIProvider

let descriptor = {
    Id = "myvendor"
    DisplayName = "MyVendor AI"
    DefaultModel = "myvendor-pro-1"
    Capabilities = {
        ProviderName = "myvendor"
        Model = ""    // overridden by builder
        Streaming = true
        ToolUse = true
        Vision = false
        SupportsPromptCaching = false
        SupportsTriage = false    // see docs/ai/extending.md "Triage capability"
        TriageModelId = None
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

```fsharp skip=fragment
let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ claudeBuilder; openAiBuilder; MyVendor.AIProvider.builder ]
        providerProfile
        secretStore
        PermissiveWithPlatformFallback
        platformProviders
        None
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
- `AIFallbackPolicy = PermissiveWithPlatformFallback` (or `StrictBYOK`) for deployments where users should supply their own keys (cost-attribution).
- Per-user / per-team rate limits via `ServerConfig.RateLimit`.
- Per-tenant cost ceilings via custom middleware reading `AILatencyRecord` events.
- `AIServerApp.withConfigValidator` for provider preflight probes — `ClaudeAIProviderValidator` / `OpenAIProviderValidator` (when shipped per-provider).
- `/health/ai` probes per-provider — verifies API key + endpoint reachability.
- `Anonymous` mode + AI is a cost-control red flag; see [`ai/README.md`](../ai/README.md) "When NOT to use this companion".

## Cost-control patterns

- **Agent-loop turn cap** — the loop stops after 15 turns rather than running away; a chat that hits it ends with an explicit message rather than more provider calls.
- **`AIAssistantServerConfig.MaxHistoryMessages`** — caps the prior history replayed to the model each turn (default 60), so a long-lived conversation cannot grow per-turn token spend without bound.
- **Token-usage caps** — middleware that short-circuits before hitting the provider when the user/team's daily/monthly cap is exceeded. Build atop `AILatencyRecord` events.
- **Cheaper models for non-critical paths** — use Haiku / GPT-4o-mini for tool dispatch in modules where Opus / GPT-4o would be overkill.
- **Cached system prompts** — long system prompts that don't change across users (platform-layer prompts) benefit most from caching. Make module-private prompts shorter than platform-shared ones to maximise hit rate.
