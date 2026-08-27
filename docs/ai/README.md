# ToolUp.AI

AI assistant runtime for apps built on ToolUp Platform. Agent loop, SSE streaming, conversation persistence, tool registry, system-prompt composition (platform + team + module layers).

`ToolUp.AI` does NOT ship an LLM provider. Providers are sub-companions (`ToolUp.AIProviders.Claude`, `ToolUp.AIProviders.OpenAI`, etc.) that implement the `IAIProvider` extension point — pick the one(s) you want and wire them via the BYOK-capable factory.

## When to use this companion

- Multi-turn AI chat as a peer of the rest of the UI (side panel + full-page module).
- Tool calling — the model can read app state, call into modules, write back results.
- Per-tenant conversation persistence + audit trail.
- Provider portability — swap Claude for OpenAI without rewriting the app.
- BYOK (bring-your-own-key) where each user supplies their own provider API key.

## When NOT to use this companion

- **Anonymous-mode public deployments.** No authenticated identity = no way to attribute API spend per user. A public Anonymous-mode app with AI enabled is a wide-open cost surface. AI is designed for `AuthenticatedEphemeral` / `Individual` / `Team` / `MultiTeam` modes. Exceptions: BYOK-only mode (every user supplies their own key) or strong network-level rate limiting.
- **Stateless single-shot completions.** If you just want one LLM call per request and don't need streaming, multi-turn, or tool calling, depend on `IAIProvider` from `ToolUp.Platform.Core` directly and skip the agent loop.
- **Non-conversational use cases** (RAG-only, classification, embeddings). RAG lives in `ToolUp.RAG`; embeddings in `ToolUp.EmbeddingProviders.*`. The AI companion is specifically for chat-style multi-turn agentic interactions.

## What's in the box

Three packages (cross-tier shape):

| Package | What it is |
|---|---|
| `ToolUp.AI.Core` | Shared types: `AIAssistantMode`, `AIToolDefinition`, `AISettings`, `AILatencyTypes`. The "minimum viable consumer-of-AI" floor for downstream packages (RAG, KnowledgeBase). |
| `ToolUp.AI.Server` | Agent loop, SSE streaming, tool registry, system-prompt composition, `AICompose`. Built on `IAIProvider` from `ToolUp.Platform.Core`. |
| `ToolUp.AI.Client` | Fable + Elmish + Feliz side-panel MVU, SSE client, conversation panel, AI assistant module UI, AI settings UI. |

Plus provider sub-companions in separate packages:
- `ToolUp.AIProviders.Claude` — Anthropic Claude
- `ToolUp.AIProviders.OpenAI` — OpenAI

## Quick start

Add the packages:

```xml
<PackageReference Include="ToolUp.AI.Server" />
<PackageReference Include="ToolUp.AIProviders.Claude" />
```

Wire the server composition root:

The app assembles the `AIProviderBuilder` from the provider package's own identifiers (see [`companions/ai-providers.md`](../companions/ai-providers.md)), then hands it to the factory:

```fsharp skip=fragment
open ToolUp.AI

let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ claudeBuilder ]
        providerProfile      // IProviderProfile — the platform-wide BYOK store
        secretStore
        PlatformOnly         // AIFallbackPolicy
        platformProviders    // DefaultAIProviderFactory.AIPlatformProvider list
        None                 // IPlatformAIKeyStore option — auto-promoted when None

AIServerApp.create aiProviderFactory providerProfile
|> AIServerApp.withConfig serverConfig
|> AIServerApp.withAuth authProvider
|> AIServerApp.addModules modules
|> AIServerApp.run
```

AI tools are not passed at this level — each module contributes its own through `ServerModule.withAITools`, and they aggregate on the inner `ServerApp`.

Wire the client composition root:

```fsharp skip=fragment
open ToolUp.AI.Client

let aiMode =
    ConfiguredAIAssistant {
        Name = "Aria"
        Icon = Icon.ofUrl "/svg/spark.svg"
        ShowSidePanel = true
    }

// `run` owns the whole Elmish program — mounting included.
AIClientConfig.run aiMode clientConfig modules
```

That's it. The agent loop, SSE endpoint, conversation persistence, and side-panel UI are now in place. See [getting-started.md](getting-started.md) for the end-to-end walkthrough.

## Concepts

See [concepts.md](concepts.md) for the agent loop, SSE flow, system-prompt composition, conversation persistence, prompt caching, and BYOK model.

## API reference

See [api-reference.md](api-reference.md) for the full public surface: `AIServerApp`, `AIAssistantApi`, `AIToolDefinition`, `ModuleAIContext`, `PromptContext`, `IAIProvider`, etc.

## Extending

See [extending.md](extending.md) for writing a new `IAIProvider`, registering custom tools, authoring a custom `SystemPromptBuilder`, and declaring capability flags.

## Cost-control posture

Even in authenticated modes, deployments should consider:

- **Per-user / per-team rate limits** via `ServerConfig.RateLimit`.
- **Cost ceilings per tenant** via custom middleware that reads `AILatencyRecord` events and short-circuits before hitting the provider.
- **BYOK** for the highest-cost users — `BYOKMode = AllowUserProviders` lets users supply their own API keys; deployment-side keys become the fallback.

The SDK records token usage (input + cached + output + cache-creation) per turn and emits it to the audit log under `_platform.ai.latency`. Operators query that for cost analysis.
