# Concepts

How ToolUp.AI works under the hood. Read this when you need to understand the agent loop, why prompts compose the way they do, or how the SDK keeps the provider boundary clean.

## The agent loop

`AIAgentEngine.runAgentLoop` is the core. Each "turn" is:

1. Build the system prompt for this request via the configured `SystemPromptBuilder` against the `PromptContext`.
2. Call `IAIProvider.SendMessage` with the system prompt, conversation history, the user's new message, and the tool definitions.
3. Inspect the response.
   - If `StopReason = EndTurn`, the turn completes — final assistant message persisted, SSE `MessageComplete` event emitted.
   - If `StopReason = ToolUse`, dispatch every tool call to its executor (in parallel where the model emitted them in parallel), append tool results to the conversation history, loop back to step 2.
4. The loop runs until `EndTurn` or until `MaxTurns` is hit (default 10; configurable via `AIConfig.MaxTurns`).

Each step emits SSE events the client streams live:
- `MessageDelta` — incremental text tokens from the model
- `ToolCallStarted` / `ToolCallCompleted` — tool dispatch markers
- `TaskStatusChanged` — overall task lifecycle
- `MessageComplete` — final assistant message persisted
- `StreamError` — fatal error in the loop

The user sees text appear character-by-character (streaming), tool calls expand inline (collapsed by default), and the conversation persists turn-by-turn — survives reload, survives server restart.

## SSE streaming

A single SSE endpoint at `/api/notifications` carries every kind of notification (system messages, job progress, refresh requests, etc.). AI messages ride a separate channel: `POST /api/IAIAssistantApi/SubmitMessage` returns a `TaskId` immediately; the client subscribes to the SSE stream and routes named events (`AIMessageDelta`, `AIToolCallStarted`, etc.) to the active conversation.

The streaming wire format uses `System.Text.Json` with the F# converter set registered via `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()` (ships inside `ToolUp.Platform.Server`) so F# discriminated unions land as `{ "Case": "X", "Fields": [...] }` — the format `Fable.SimpleJson` on the client expects.

Cross-process compatibility: if a deployment is multi-instance and the user's SSE connection lands on a different node than the one running the agent loop, the SSE event would never reach them. The default `InMemoryNotificationChannel` handles single-instance. For multi-instance, swap in `ToolUp.NotificationChannels.Redis` (or any future distributed channel companion) — per-scope topic isolation is structural (one topic per `ScopeId`), so routing across nodes works without extra config.

## System-prompt composition

The agent builds its system prompt per-request via a `SystemPromptBuilder`, not a static string:

```fsharp
type PromptContext = {
    Access: AccessContext
    ActiveModule: string option
    ModuleContexts: Map<string, ModuleAIContext>
}

type SystemPromptBuilder = PromptContext -> Async<string>
```

The default builder composes three layers:

1. **Platform layer** — a default system prompt describing the assistant's role.
2. **Team / deployment layer** — optional; reads team profile from `IConfigStore` or team-scoped `IBlobStorage`. Lets deployments inject team-private context ("the current team is Acme Corp, category 'Health & Beauty'").
3. **Module layer** — when the user chats from a module's view, the active module's `ModuleAIContext.SystemPrompt` is injected via `SystemPromptBuilder.activeModuleContext`.

The builder combinators live in the `ToolUp.AI.SystemPromptBuilder` module, which nests a module of the same name — so a composition root opens it rather than reaching through `ToolUp.AI` alone:

```fsharp skip=fragment
open ToolUp.AI.SystemPromptBuilder

let teamAwarePrompt =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic "You are an analytics assistant. ..."
        SystemPromptBuilder.activeModuleContext
        fun ctx -> async {
            match ctx.Access.TeamId with
            | None -> return ""
            | Some teamId ->
                let! profile = teamStore.GetTeamProfile teamId
                return $"The current team is {profile.Name}, category {profile.Category}."
        }
    ]
```

`SystemPromptBuilder.compose` runs builders in parallel and joins their outputs with blank lines. The result becomes the system message on the LLM call.

**There is no client-side mechanism to send invisible prompts.** "Private" always means **module-registered at compose time or team-loaded server-side**. Anything that feeds the model is either visible in the user's chat history or declared at the deployment boundary. This is a deliberate property — the alternative (per-user invisible prompts) is a footgun for prompt-leakage attacks.

## Tool registry

Tools are declared as `AIToolDefinition` records:

```fsharp
type AIToolDefinition = {
    Name: string
    Description: string
    Parameters: ToolParameterSchema list
    SourceModule: string
    EmitsActions: ActionDeclaration list option
    Location: ToolLocation
    Surface: AISurfaceFilter
    IsLiveInterface: bool
}

and ToolLocation =
    | ServerResident  // executed on the server in-process
    | ClientResident  // dispatched to the client; the user's browser runs the tool
```

The record is metadata only — it lives in the core SDK so a module can declare tools without referencing the AI companion, and the executor is paired to it server-side by `ToolUp.AI.RegisteredTool`.

Tools come from three sources:
1. **Platform built-ins** in `AITools.allTools` — generic tools like `_platform.inspect_team`, `_platform.list_modules`, etc.
2. **Module-declared tools** registered via `ServerModule.withAITools [...]`.
3. **Companion-contributed tools** — any companion registering through `AIToolRegistry`. A common pattern is a companion that exposes AI-driven UI control (inspect active module, set field, click button, navigate, select row) by registering server-side handlers + paired client-resident tools. forge ships no such companion out of the box; consumers register their own AI tools through the substrate or pull in a third-party companion.

The agent receives the union of all registered tools as the LLM's tool schema. Tool calls route through the registry's executor lookup by `Name`.

### Server-side vs client-resident tools

- **`ServerSide`** — default. Tool executes on the server in the agent process. The handler runs against the caller's `AccessContext`. Used for "read data", "kick off a job", "search the knowledge base".
- **`ClientResident`** — the server-side agent loop dispatches the call back to the client over SSE; the client runs the tool in the user's browser; the result returns to the agent over WebSocket / HTTP. The forge substrate that ships this dispatch path (`ClientToolRuntime` + `ClientToolDispatch`) is generic — any companion registering `ClientResident` tools can use it to drive UI (set form fields, click buttons, navigate, select grid rows) with the user watching.

Client-resident tools require the AI assistant module is **full-page** (not the side panel) — Mode 2 vs Mode 1. The side panel is "just do it" (server-side tools only); the full-page module is "watch me work" (UI tools enabled). This is documented per-tool via `ToolCapabilities`.

## Conversation persistence

Conversations live in `IBlobStorage` under `_platform/ai-conversations/{scopeId}/{conversationId}.json`. Each `Conversation` carries:

- `ConversationId: Guid`
- `Participants: Participant list` — the user + any agents
- `Messages: ConversationMessage list` — every turn, in order
- `Created: DateTime`, `Updated: DateTime`
- `Title: string option` — caller-supplied or auto-generated from the first message

The shipped persistence implementation is blob-backed. Distributed deployments using a Redis-backed cache can swap in a different `IConversationStore` impl — same interface, faster reads.

`AIAssistantApi` exposes:
- `ListConversations: unit -> Async<ConversationSummary list>`
- `GetConversation: ConversationId -> Async<Conversation option>`
- `DeleteConversation: ConversationId -> Async<unit>`
- `SubmitMessage: AIMessageRequest -> Async<AITask>`

Conversations are scope-isolated. Team A's user-John cannot read Team B's conversations even if he switches teams; on team switch, the conversation list refetches against the new team scope. KB documents, file lists, and AI conversations all swap together.

## Provider abstraction

`IAIProvider` is the boundary:

```fsharp
type IAIProvider =
    abstract Capabilities: AIProviderCapabilities
    abstract SendMessage: AIProviderRequest -> Async<AIProviderResponse>

and AIProviderRequest = {
    SystemPrompt: string
    Messages: AIProviderMessage list
    Tools: AIProviderToolDef list
    MaxTokens: int
    Temperature: float
}

and AIProviderResponse = {
    Messages: AIProviderMessage list
    StopReason: StopReason
    ToolCalls: AIProviderToolCall list
    Usage: TokenUsage option
}

and TokenUsage = {
    PromptTokens: int
    CachedPromptTokens: int
    OutputTokens: int
    CacheCreationTokens: int option
}

and StopReason = EndTurn | ToolUse | MaxTokens | StopSequence

and AIProviderCapabilities = {
    ProviderName: string
    Model: string
    Streaming: bool
    ToolUse: bool
    Vision: bool
    SupportsPromptCaching: bool
    SupportsTriage: bool          // provider can serve the fast-path triage tier
    TriageModelId: string option  // cheaper-tier model id, when the family has one
}
```

The agent loop is provider-agnostic — every provider gets the same `AIProviderRequest`, returns the same `AIProviderResponse`, and the loop doesn't care whether it's Claude, OpenAI, or a future Gemini / Mistral / DeepSeek companion.

The translation layer per-provider:
- Claude — `/v1/messages` endpoint. SSE event stream. `cache_control: { type: "ephemeral" }` markers for prompt caching.
- OpenAI — `/v1/chat/completions`. SSE event stream. `stream_options.include_usage: true` for accurate token reporting on streamed responses.

## Prompt caching

Anthropic and OpenAI both support prompt caching — system prompts and conversation history that repeat across turns get cached at the provider, dramatically reducing token cost and TTFT (time-to-first-token) on subsequent turns.

The SDK marks three locations for caching in the Claude provider:
1. Last text block of `system` — caches the static system prompt.
2. Last entry in `tools` — caches the tool schema.
3. Last content block of the second-to-last message (when conversation length ≥ 2) — caches the conversation prefix.

Sub-threshold prefixes (<1024 tokens for Sonnet/Haiku, <2048 for Opus) silently process without caching — Anthropic doesn't reject the request, it just doesn't cache. The SDK does not pre-check size — caching kicks in once the prefix grows past the threshold.

OpenAI prompt caching is automatic (no API marker required); `stream_options.include_usage: true` makes the cached-token count visible in the final `[DONE]`-preceding usage chunk.

Token-usage data goes to `IAIProvider.AIProviderResponse.Usage`, then into the per-turn `AILatencyRecord` event emitted to `IEventStore` under `_platform.ai.latency`. The `/dev/ai-latency` endpoint (when `EnableDevEndpoints` is on) surfaces rolling 60-min p50/p95/p99 + `CacheHitRate` per provider/model.

## BYOK + per-user provider configs

`BYOKMode` on the provider factory:

- **`PlatformOnly`** — every request uses the deployment's API key from the `_platform` scope. Simplest; deployment carries 100% of cost. Users see one provider option in the AI Settings UI (or none, if you hide it).
- **`AllowUserProviders`** — users may register their own `AIProviderInstance` via the AI Settings UI. Each call resolves the active provider from `IUserAIConfigStore`, pulls the user's API key from `ISecretStore`, and invokes the matching builder. Deployment's `_platform`-scoped key is the fallback when the user hasn't configured one.

`AIProviderInstance` carries:
- `InstanceId: Guid` — caller's reference
- `ProviderId: string` — matches a registered builder's `Descriptor.Id`
- `Model: string` — provider-specific (`claude-opus-4-1-20250109`, `gpt-4o`, etc.)
- `SecretKeyRef: string` — pointer into `ISecretStore`
- `DisplayName: string` — user-visible label

The AI Settings UI (auto-injected when `AIAssistantMode != NoAIAssistant`) lets users add / edit / delete instances. The API keys never leave `ISecretStore` — the UI passes the key value through once at setup, the store encrypts it, and from then on the factory pulls it per-call.

## Latency + observability

Each agent turn emits an `AILatencyRecord` to `IEventStore` under `_platform.ai.latency`:

```fsharp
type AILatencyRecord = {
    TaskId: Guid
    ConversationId: Guid
    TurnNumber: int
    ProviderName: string
    ProviderModel: string
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

`/dev/ai-latency` (debug + `EnableDevEndpoints`) returns rolling 60-min stats:
- Per `(provider, model)` — TTFT p50/p95/p99, turn duration p50/p95/p99, `CacheHitRate`.
- Per tool — call count, p50/p95 duration, error rate.
- Server-side vs client-resident breakdown.

For production observability, the same data flows through `IMetricsSink` (Prometheus + OpenTelemetry companions) as request-level metrics.

## What this companion does NOT cover

- **Audio input** — image (vision) input shipped in Phase 6o: `AIProviderMessage.Parts` carries multimodal content blocks, gated by `Capabilities.Vision` with synchronous `UnsupportedCapability("vision", …)` rejection on non-vision models. Audio content blocks are not yet shipped.
- **Tool result streaming** — tools return a single `ToolResult` value. Streaming partial tool results (for long-running tool calls) is not yet supported.
- **Cross-conversation memory** — each conversation is isolated. There's no shared memory across conversations for the same user; "the assistant remembers our prior chat" is an opt-in pattern via the `ToolUp.KnowledgeBase` companion (chat history can be ingested as KB content).
- **Native multi-agent orchestration** — the agent loop runs one agent at a time. Multi-agent workflows (one agent delegates to another) can be expressed with tool calls but aren't a first-class concept in the API.

For any of these, the right shape is a follow-up companion or a custom layer; the core agent loop stays small and stable.
