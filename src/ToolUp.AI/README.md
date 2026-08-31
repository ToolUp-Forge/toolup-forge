# ToolUp.AI

Companion package providing the AI assistant integration for applications built on [`ToolUp.Platform`](../ToolUp.Platform/). Ships the agent loop, SSE streaming, conversation persistence, tool registry, and system-prompt composition — everything except the provider itself (Claude lives in a sub-companion at [`src/AIProviders/Claude/`](../AIProviders/Claude/), and other providers can be added the same way).

For deep technical detail, see [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md). This README covers the shape of the package, how to enable it in a deployment, and the extension points.

## Why a separate companion

Two reasons it doesn't live in `ToolUp.Platform`:

1. **AI is an optional platform capability.** Deployments that don't use AI shouldn't pay for its types, dependencies, or runtime. Stripping the `ToolUp.AI` reference removes all AI surface from the app.
2. **The runtime surface is substantial.** Agent loop, SSE plumbing, conversation persistence, tool registry, system-prompt composition — keeping this in core would conflate platform infrastructure with feature code. Companion packages (the same pattern as [`AgGridEnterprise`](../Feliz.AgGrid.Enterprise/)) keep the boundary clean.

What stays in core:

- **`IAIProvider`** (`src/ToolUp.Platform/Shared/IAIProvider.fs`) — extension point interface. Analogous to `IBlobStorage` and `IAuthProvider`. Providers implement it; the agent loop depends on it.
- **`AIToolDefinition`** and **`ToolParameterSchema`** (`src/ToolUp.Platform/Shared/ModuleAITypes.fs`) — module-facing tool declarations. Modules declare tools without referencing `ToolUp.AI`. The *runtime* (registry, agent, execution) lives in this companion.

## Authentication requirement (deployment design decision)

**ToolUp.AI is designed for authenticated platform modes** (`AuthenticatedEphemeral`, `Individual`, `Team`, `MultiTeam`). Deployments running in `Anonymous` mode (no sign-in, public/demo) **typically should not enable AI access**.

The reason is cost control. LLM API calls cost money per request. Without an authenticated identity, a deployment cannot:

- Attribute calls per user
- Enforce per-user rate limits
- Apply per-tenant cost ceilings

A public Anonymous-mode deployment with AI enabled is a wide-open cost surface — anyone with the URL can drive arbitrary token consumption against the deployment's API key.

This is a platform-level design principle, not a hard runtime block. `AIServerApp.run` does not refuse to start when `ServerConfig.Mode = Anonymous`, because legitimate exceptions exist:

- Single-user local development
- Demos or trials with strong network-level rate limiting
- BYOK-only deployments where every user supplies their own API key (configured via `BYOKMode = AllowUserProviders`)

Deployments choosing to enable AI in Anonymous mode accept the cost-control responsibility and should layer in their own protections via `ServerConfig.RateLimit`, IP gating at the proxy, or BYOK-only provider configuration.

See [`CLAUDE.md`](../../CLAUDE.md#ai-in-anonymous-mode--deployment-design-decision) for the full design discussion.

## What this package ships

### Shared types — [Shared/AITypes.fs](Shared/AITypes.fs)

Compiled into `ToolUp.AI.dll`. Referenced by both server and client.

| Type | Purpose |
|---|---|
| `AIAssistantBranding` | Client-visible name, icon, side-panel toggle |
| `AIAssistantMode` | `NoAIAssistant \| DefaultAIAssistant \| ConfiguredAIAssistant of Branding` |
| `AIMessageRequest` | Record on the `SubmitMessage` API — carries `ConversationId`, `Content`, and the user's `ActiveModule` |
| `ModuleAIContext` | `{ ModuleName; SystemPrompt }` — a module's private domain-expert prompt, registered at compose time |
| `AIAssistantApi` | ToolUp.Remoting API: `SubmitMessage`, `GetConversation`, `ListConversations`, `GetAvailableTools`, `GetTaskStatus`, `DeleteConversation` |
| `AIStreamEvent` | SSE payloads: `MessageDelta`, `ToolCallStarted`, `ToolCallCompleted`, `TaskStatusChanged`, `MessageComplete`, `StreamError` |
| `AIProviderMessage`, `AIProviderToolCall`, `AIProviderToolResult`, `AIProviderToolDef`, `AIProviderResponse` | Provider-level protocol types — used by `IAIProvider` implementations |
| `Conversation`, `ConversationMessage`, `Participant`, `AITask`, `AITaskStatus` | Persistence and task-tracking types |

### Server-side runtime — [Server/](Server/)

Injected into the consuming server project via `ToolUp.AI.Server.props`. Compiles alongside the Platform server files.

| File | Purpose |
|---|---|
| `SystemPromptBuilder.fs` | `PromptContext`, `SystemPromptBuilder`, `fromStatic`, `activeModuleContext`, `compose`, `AIAssistantServerConfig` |
| `AIToolRegistry.fs` | `RegisteredTool`, `AIToolRegistry`, `createTool`, `toProviderDef` |
| `SSEHandler.fs` | `SSEConnection`, `SSEConnectionManager` (zombie-aware), `sseHandler` Giraffe endpoint |
| `AIAgentEngine.fs` | `runAgentLoop` (multi-turn with tool dispatch), `ToolInvocationError` |
| `AIAssistantHandler.fs` | `AIAssistantApi` implementation — `SubmitMessage`, conversation persistence, background agent execution |
| `AICompose.fs` | `composeWithAI` + `AIServerApp` record — drop-in replacement for `Server.compose` / `ServerApp`. `AIServerApp` wraps a `ServerApp.Base` and adds an `AIProviderFactory`, `AIConfigStore`, `AITools`, `AIConfig`, and `ModuleAIContexts`; `AIServerApp.run` calls `composeWithAI` internally via `ComposeExtensions` |

### Client-side UI — [Client/](Client/)

Injected into the consuming client project via `ToolUp.AI.Client.props`. The Platform shell (`ToolUp.Platform.Client.Client`) is AI-agnostic; this companion layers the AI MVU + chrome back on via an Elmish outer-program wrapper (`AIClientConfig.withAIAssistant`).

| File | Purpose |
|---|---|
| `SSEClient.fs` | EventSource wrapper with mode-aware query parameter |
| `ConversationPanel.fs` | Reusable chat panel component |
| `AIAssistantUI.fs` | Built-in AI assistant module page (full conversation view) |
| `AIClientConfig.fs` | `SidePanelModel` / `SidePanelMsg` / `sidePanelUpdate`, `OuterModel`/`OuterMsg`, `appendAssistantModule`, `withSidePanel`, `withAIAssistant` — the outer-program composition that wraps the shell's Elmish program |

## How to enable AI in a deployment

### 1. Reference the companion

Server project (`ToolupApp-Server.fsproj`):

```xml
<Import Project="..\ToolUp.Platform\ToolUp.Platform.Server.props" />
<Import Project="..\ToolUp.AI\ToolUp.AI.Server.props" />
<Import Project="..\AIProviders\Claude\ClaudeAIProvider.Server.props" />

<ItemGroup>
  <ProjectReference Include="..\ToolUp.Platform\ToolUp.Platform.fsproj" />
  <ProjectReference Include="..\ToolUp.AI\ToolUp.AI.fsproj" />
</ItemGroup>
```

Client project (`ToolupApp-Client.fsproj`) — add the companion's client props after the Platform's:

```xml
<Import Project="..\ToolUp.Platform\ToolUp.Platform.Client.props" />
<Import Project="..\ToolUp.AI\ToolUp.AI.Client.props" />
```

### 2. Wire a provider factory and run via `AIServerApp`

In the server entry point:

```fsharp skip=fragment
open ToolUp.Platform.Server
open ToolUp.AI
open ToolUp.AI.AICompose

let secretStore  = FileSecretStore.FileSecretStore() :> ISecretStore
let blobStorage  = LocalFileStorage.LocalFileStorage("data") :> IBlobStorage
let logger       = ConsoleLogger.ConsoleLogger()
// BYOK-capable factory — registers one builder per provider.
// Each builder reads the API key from the platform IProviderProfile
// store (falling back to the `_platform` scope secret store for the
// platform-default provider).
let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ claudeBuilder; openAiBuilder ]
        providerProfile          // IProviderProfile
        secretStore
        PlatformOnly             // AIFallbackPolicy
        platformProviders
        None                     // IPlatformAIKeyStore option

AIServerApp.createFrom aiProviderFactory providerProfile (
    ServerApp.empty
    |> ServerApp.withConfig config
    |> ServerApp.withAuth authProvider
    |> ServerApp.withLogger logger
    |> ServerApp.withStorage blobStorage
    |> ServerApp.addModules modules)        // each module as a ServerModule
|> AIServerApp.run
```

Deployments that don't want AI use `ServerApp.run` directly (no `AIServerApp` wrapper). The factory indirection is what lets users configure per-user BYOK providers via the AI Settings UI without changing server wiring.

`AIProviderFactory` and `ProviderProfile` are constructor parameters of `AIServerApp.create` / `createFrom` rather than optional fields — the wrapper exists precisely because AI needs both and the core `ServerApp` cannot reasonably default them.

### 3. Wire the client wrapper

In the client entry point, wrap the shell Program with `AIClientConfig.withAIAssistant`:

```fsharp skip=fragment
open Elmish
open Elmish.React
open ToolUp.Platform

let aiMode =
    ConfiguredAIAssistant {
        Name = "Claude"
        Icon = Icon.ofUrl "/svg/claude.svg"
        ShowSidePanel = true
    }

let config = { ClientConfig.defaults with (* ... *) }
let modules = [ (* module registrations *) ]

AIClientConfig.run aiMode config modules
```

Apps without AI drop the `ToolUp.AI.Client.props` import (step 1) and call `Client.run config modules` instead — zero AI surface, zero AI types leaked into shell state.

Branding fields only — no system-prompt content here. Prompt composition is server-side.

## Team-, module-, and session-aware system prompts

The agent loop builds its system prompt per-request via a `SystemPromptBuilder`, not a static string. The builder receives a `PromptContext`:

```fsharp
type PromptContext = {
    Access: AccessContext                            // user + team + mode + permissions
    ActiveModule: string option                      // which module the user is viewing
    ModuleContexts: Map<string, ModuleAIContext>     // compose-time module contributions
}

type SystemPromptBuilder = PromptContext -> Async<string>
```

Three built-in helpers:

- **`SystemPromptBuilder.fromStatic "..."`** — constant prefix
- **`SystemPromptBuilder.activeModuleContext`** — injects the active module's `SystemPrompt` when one is registered
- **`SystemPromptBuilder.compose [...]`** — layers multiple builders; parallel-resolved, joined by blank lines

### Module-contributed private prompts

Each module can export a `ModuleAIContext`:

```fsharp
// In NBDDirichlet/Server.fs
let aiContext : ModuleAIContext = {
    ModuleName = "NBDDirichlet"
    SystemPrompt = """You are helping with NBD-Dirichlet category analysis.
                      Typical inputs: penetration, average buy rate.
                      Key outputs: expected brand duplication, heavy-buyer share."""
}
```

The app collects them:

```fsharp skip=fragment
let moduleAIContexts = [
    NBDDirichlet.Server.aiContext
    MediaOptimisation.Server.aiContext
    SkuAnalysis.Server.aiContext
    // PriceElasticity, SOVSM skip — no domain prompt needed
]

AIServerApp.createFrom aiProviderFactory providerProfile serverApp
|> AIServerApp.withModuleAIContexts moduleAIContexts
|> AIServerApp.run
```

When the user chats from the NBDDirichlet view, the client attaches `ActiveModule = Some "NBDDirichlet"` to the request. The `activeModuleContext` builder looks up the module's contribution and injects it. The user never sees this in their chat history — it's metadata to the model.

### Team-private context

For `Team` mode deployments, team-specific context is loaded per request:

```fsharp skip=fragment
let teamAwarePrompt =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic "You are ToolUp, an analytics assistant..."
        SystemPromptBuilder.activeModuleContext
        fun ctx -> async {
            match ctx.Access.TeamId with
            | None -> return ""
            | Some teamId ->
                let! profile = teamStore.GetTeamProfile teamId
                return $"The current team is {profile.Name}, category {profile.Category}."
        }
    ]

let aiConfig = Some {
    Branding = { Name = "Claude"; Icon = "/svg/claude.svg"; ShowSidePanel = true }
    SystemPrompt = Some teamAwarePrompt
}

AIServerApp.createFrom aiProviderFactory providerProfile serverApp
|> AIServerApp.withAIConfig aiConfig
|> AIServerApp.withModuleAIContexts moduleAIContexts
|> AIServerApp.run
```

The builder runs per request. `AccessContext.TeamId` is scope-validated upstream by `ScopeResolutionMiddleware` — Team A's context can never leak to Team B's conversation.

**The SDK has no mechanism for the user to send invisible prompts.** "Private" always means *module-registered at compose time* — anything that feeds the model is either visible in chat history or declared at the deployment boundary.

## Writing a new AI provider

Follow the pattern in [`src/AIProviders/Claude/`](../AIProviders/Claude/) and [`src/AIProviders/OpenAI/`](../AIProviders/OpenAI/). Minimum:

1. Implement `IAIProvider` (in `src/ToolUp.Platform/Shared/IAIProvider.fs`):
   - `Capabilities : AIProviderCapabilities` — declare Streaming, ToolUse, Vision, ProviderName, Model
   - `SendMessage` — single request/response turn, honours the `RetryPolicy`
2. Expose a factory function, `createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider`, that builds a provider instance from a resolved key + model.
3. Export an `AIProviderDescriptor` (provider id, display name, default model, capability hints) and pair it with the factory in an `AIProviderBuilder`:

   ```fsharp skip=fragment
   let descriptor: AIProviderDescriptor = { (* provider id, name, defaults *) }
   let builder: AIProviderBuilder = {
       Descriptor = descriptor
       Build = fun apiKey model -> createWithApiKeyAndModel apiKey model
   }
   ```
4. Create a `.fsproj` and `.Server.props` in `src/AIProviders/<Name>/`. Deployments pull the builder into `DefaultAIProviderFactory.create [ claudeBuilder; openAIBuilder; yourBuilder ] ...` — no other wiring changes needed.

The factory is what selects the correct builder per-request: it reads the user's configured `AIProviderInstance.ProviderId` (or the platform-default when `PlatformOnly`), pulls the API key from the appropriate scope of `ISecretStore`, and invokes the builder. The agent loop, tool dispatch, system prompt building, SSE streaming, and conversation persistence stay provider-agnostic.

A new provider only needs to translate the `AIProviderMessage` / `AIProviderResponse` protocol.

## Observability and metrics (Phase 9 / 9e delegations)

`AIServerApp` mirrors the `ServerApp` observability surface so AI deployments can wire health probes, config validators, and metrics sinks fluently:

- `AIServerApp.withHealthCheck` (Phase 9k) — register a companion-contributed `IHealthCheck` (e.g. `ClaudeAIProviderHealth.create secretStore`).
- `AIServerApp.withConfigValidator` (Phase 9m) — register a companion-contributed `IConfigValidator` for startup preflight.
- `AIServerApp.withMetricsSink` (Phase 9e) — register a companion-contributed `IMetricsSink` (e.g. `OtelMetricsSink.create regs logger`) alongside the in-process Prometheus default. The fan-out wrapper makes a single `Increment` call dispatch to every registered sink. Wire `MetricsEndpoint = EnabledMetricsEndpoint` on `ServerConfig` to mount `/metrics` and activate emission.

Each helper delegates to its `ServerApp` counterpart — see [`src/ToolUp.Platform/README.md`](../ToolUp.Platform/README.md) and `TECHNICAL_GUIDE.md` for the full contract.

### Auto-registered AI config validators (Phase 9m.A)

`AICompose` wires two always-on `IConfigValidator`s and one opt-in network probe so operator-typo classes of misconfiguration surface at startup instead of at first chat request:

| Validator | Env var(s) consulted | Default outcome |
|---|---|---|
| `AIProviderEnvValidator` | `TOOLUP_AI_PROVIDER` | Self-skips with `Ok` when unset. `Warning` when set to a value not in `IAIProviderFactory.Available ∪ PlatformDescriptor`. |
| `AIModelEnvValidator` | `TOOLUP_AI_MODEL` (+ `TOOLUP_AI_PROVIDER` for scoping) | Self-skips with `Ok` when unset. `Warning` when set to a model not in the relevant descriptor's `SupportedModels`. |
| `AIProviderProbeValidator` | `TOOLUP_AI_PROBE_ON_STARTUP=1` to enable; reads `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY` to probe | Not registered when probe disabled. `Warning` for refused keys / model-not-in-access-list / unknown providers. `Error` (startup abort) when the provider is unreachable. |

All three are GP 13 lightweight defaults — deployments that don't set the env vars pay nothing. See [`docs/companions/ai-providers.md`](../../docs/companions/ai-providers.md#operator-config--startup-validation-env-vars-phase-9ma) for the operator-facing table.

## Deferred follow-ups

- **Dynamic module AI contributions.** Today `ModuleAIContext.SystemPrompt` is a static string. A `Model -> string` form (reading runtime module state) is possible but needs special type-erasure handling. Deferred.
