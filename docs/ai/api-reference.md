# API reference

Public surface of `ToolUp.AI`. Types are listed by package; method signatures are F# notation.

## `ToolUp.AI.Core`

Shared types — referenced by both `Server` and `Client`. Source ships under `fable/` in the nupkg for Fable consumers.

### `AIAssistantMode`

```fsharp
type AIAssistantMode =
    | NoAIAssistant
    | DefaultAIAssistant
    | ConfiguredAIAssistant of AIAssistantBranding

and AIAssistantBranding = {
    Name: string
    Icon: string         // SVG path, served from /svg/
    ShowSidePanel: bool
}
```

Branding only. System-prompt content stays server-side.

### `AIAssistantApi` (ToolUp.Remoting contract)

```fsharp
type AIAssistantApi = {
    SubmitMessage: AIMessageRequest -> Async<AITask>
    GetConversation: Guid -> Async<ConversationMessage list>
    ListConversations: unit -> Async<Conversation list>
    GetAvailableTools: unit -> Async<AIToolDefinition list>
    GetTaskStatus: Guid -> Async<AITask option>
    DeleteConversation: Guid -> Async<Result<unit, string>>
    SetConversationOverride: Guid * string option -> Async<Result<unit, string>>
}
```

Auto-injected via ToolUp.Remoting when AI is enabled. Caller binds the `AIAssistantApi` proxy on the client. Every method carries an authorization attribute (`[<AllowAnonymous>]` throughout — per-scope isolation and the conversation-ownership gate run inside the handlers); `SubmitMessage` additionally carries `[<RateLimit>]` and `DeleteConversation` an `[<Audit>]` annotation. `SetConversationOverride` persists the per-conversation provider override read back as `Conversation.OverrideProviderLabel`.

### `AIMessageRequest`

```fsharp
type AIMessageRequest = {
    ConversationId: ConversationId  // None = new conversation
    Content: string
    ActiveModule: string option     // Some "MyModule" = chat from MyModule's view
}
```

### `AIStreamEvent`

```fsharp
type AIStreamEvent =
    | MessageDelta of conversationId: Guid * content: string
    | ToolCallStarted of conversationId: Guid * toolName: string * toolCallId: Guid
    | ToolCallCompleted of conversationId: Guid * toolCallId: Guid * result: string
    | TaskStatusChanged of taskId: Guid * status: AITaskStatus
    | MessageComplete of conversationId: Guid * messageId: Guid
    | StreamError of message: string
    /// The user cancelled the in-flight agent loop; it exits at the next turn boundary.
    | StreamCancelled of conversationId: Guid
    /// Ask the client to execute a `ClientResident` tool and POST the result
    /// to `/api/ai/tool-result`; the agent loop suspends on `toolCallId`.
    | ClientToolInvoke of
        taskId: Guid *
        toolCallId: Guid *
        toolName: string *
        argsJson: string *
        activeModule: string option *
        activePage: string option
    /// Per-message numeric-fidelity verdict; emitted only when the answer
    /// gate ran in `Annotate` / `Strict` mode.
    | AnswerVerified of conversationId: Guid * verification: AnswerVerification
```

Streamed over SSE. Client routes by `NotificationKind = AIStream`.

### `AIToolDefinition`

```fsharp
type AIToolDefinition = {
    Name: string                                // e.g. "media_optimisation.run"
    Description: string                         // shown to the model
    Parameters: ToolParameterSchema list
    SourceModule: string                        // which module provides the tool
    EmitsActions: ActionDeclaration list option // client-side actions the tool may publish
    Location: ToolLocation
    Surface: AISurfaceFilter
    IsLiveInterface: bool                       // reads or drives live browser-resident state
}

and ToolLocation =
    | ServerResident
    | ClientResident

and AISurfaceFilter =
    | Both
    | SidePanelOnly
    | FullPageOnly
```

Metadata only — the executor is server-side and lives in `ToolUp.AI.RegisteredTool` alongside the rest of the AI runtime, so a module can declare tools without referencing the AI companion. Module-declared tools live in `Server.fs`; the composition root registers them via `ServerModule.withAITools [...]` or directly via `AIServerApp.withAITools`.

### `ToolParameterSchema`

```fsharp
type ToolParameterSchema = {
    Name: string
    Type: string            // "string" | "number" | "boolean" | "object" | "array"
    Description: string
    Required: bool
    Default: string option  // JSON-encoded default value
}
```

One entry per parameter; `AIToolDefinition.Parameters` is a list of them. Deliberately Fable-compatible (no `System.Text.Json` dependency) so the declaration shape can live in the core SDK. Translates to JSON Schema at provider request time — the model sees `parameters: { type: "object", properties: { ... } }`.

### `ModuleAIContext`

```fsharp
type ModuleAIContext = {
    ModuleName: string       // matches the module's name in the registry
    SystemPrompt: string     // static text; injected when ActiveModule matches
}
```

### `Conversation`

```fsharp
type Conversation = {
    Id: Guid
    Title: string option
    CreatedAt: DateTime
    UpdatedAt: DateTime
    MessageCount: int
    /// Per-conversation provider override, set via `SetConversationOverride`.
    OverrideProviderLabel: string option
}

and ConversationMessage = {
    Id: Guid
    ConversationId: Guid
    Participant: ParticipantType     // User | AIAssistant | System
    Content: string
    Timestamp: DateTime
    ToolCalls: ToolCallRecord list
    /// Knowledge-base chunks retrieved for this message; `[]` for user and
    /// system turns, and for assistant turns where retrieval did not run.
    RetrievedSources: RetrievedSource list
    /// Multimodal content blocks; `[]` for plain-text turns, where `Content`
    /// carries the whole message body.
    Parts: AIContentPart list
    /// The conversation's owner, stamped when the server persists the first
    /// message. `""` on client-side construction.
    CreatedBy: string
    /// Idempotency key of the fast-path beacon that appended this message;
    /// `""` for ordinary agent-loop turns.
    BeaconId: string
    /// Numeric-fidelity verdict; `None` unless the answer gate ran.
    Verification: AnswerVerification option
}
```

`Conversation` is metadata only — `ListConversations` returns these, and `GetConversation` returns the `ConversationMessage list` for one.

### `AITask`

```fsharp
type AITask = {
    TaskId: Guid
    ConversationId: Guid
    Prompt: string
    Status: AITaskStatus
    CreatedAt: DateTime
    CompletedAt: DateTime option
}

and AITaskStatus =
    | Queued
    | InProgress
    | AITaskCompleted
    | AITaskFailed of string
```

### `IAIProvider`

```fsharp
type IAIProvider =
    abstract Capabilities: AIProviderCapabilities
    abstract SendMessage: AIProviderRequest -> Async<AIProviderResponse>
    // Schema-respecting structured output. `schema` is a JSON Schema as
    // a string; providers translate to their native wire format
    // (Gemini `responseSchema`, OpenAI `response_format`, Claude
    // tool-based workaround). Non-streaming only. Non-native providers
    // compose `IAIProviderDefaults.sendStructuredViaFallback`.
    abstract SendStructuredMessage:
        messages: AIProviderMessage list *
        tools: AIProviderToolDef list *
        systemPrompt: string option *
        schema: string *
        retryPolicy: RetryPolicy ->
            Async<Result<AIProviderResponse, AIProviderError>>

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

and AIProviderRequest = {
    SystemPrompt: string
    Messages: AIProviderMessage list
    Tools: AIProviderToolDef list
    MaxTokens: int
    Temperature: float
    Stream: bool
}

and AIProviderResponse = {
    Messages: AIProviderMessage list
    StopReason: StopReason
    ToolCalls: AIProviderToolCall list
    Usage: TokenUsage option
}

and AIProviderMessage = {
    Role: string                // "user" | "assistant" | "system"
    Content: string             // plain-text body; for multipart messages, the concatenated text parts
    Parts: AIContentPart list   // multimodal content blocks (Phase 6o); [] for plain-text messages
}

and AIProviderToolCall = {
    ToolCallId: string
    Name: string
    Arguments: JsonValue
}

and TokenUsage = {
    PromptTokens: int
    CachedPromptTokens: int
    OutputTokens: int
    CacheCreationTokens: int option
}

and StopReason = EndTurn | ToolUse | MaxTokens | StopSequence
```

(Defined in `ToolUp.Platform.Core`; aliased here for completeness.)

### `IProviderProfile`

The canonical platform-wide BYOK store (it replaced the earlier per-user `IUserAIConfigStore`). Defined in `ToolUp.Platform.Core`, keyed by `StorageScope` so tenant isolation is structural.

```fsharp
type IProviderProfile =
    abstract Get: scope: StorageScope -> Async<ProviderProfile option>
    abstract Set: scope: StorageScope * profile: ProviderProfile -> Async<Result<unit, string>>
    abstract Clear: scope: StorageScope -> Async<unit>
    /// Resolve the entry a (surface, context) pair routes to. A context-specific
    /// rule wins over the surface default; a stale label yields `None`.
    abstract ResolveEntry: scope: StorageScope * surface: string * context: string option -> Async<ProviderEntry option>
    /// Write only the advisory health of one entry, so a background probe cannot
    /// race a user editing routing through `Set`.
    abstract SetEntryHealth: scope: StorageScope * label: string * health: ProviderHealth -> Async<Result<unit, string>>
```

### `AILatencyRecord`

```fsharp
type AILatencyRecord = {
    TaskId: Guid
    ConversationId: Guid
    TurnNumber: int              // 1-based turn index within this agent-loop run
    ProviderName: string
    ProviderModel: string
    /// Time to first `MessageDelta`. `None` when the model went straight to a
    /// tool call without narrating, or when the provider does not stream.
    TtftMs: float option
    TurnDurationMs: float        // provider call + parallel tool execution + bookkeeping
    ToolCalls: ToolCallTiming list
    /// `"end_turn"`, `"tool_use"`, `"max_tokens"`, or `""` when the loop bailed
    /// before reading one.
    StopReason: string
    PromptTokens: int option
    CachedPromptTokens: int option
    OutputTokens: int option
    /// Anthropic-specific cache-write cost; `None` on providers without one.
    CacheCreationTokens: int option
}

and ToolCallTiming = {
    Name: string
    Location: ToolExecutionLocation   // ServerSide | ClientSide
    DurationMs: float
    Errored: bool
}
```

## `ToolUp.AI.Server`

### `AIServerApp`

Flat superset of `ServerApp`. The fluent shape:

```fsharp
open ToolUp.Platform.Providers
open ToolUp.AI.SystemPromptBuilder

type AIServerApp = {
    Base: ServerApp
    AIProviderFactory: IAIProviderFactory
    /// Canonical platform-wide BYOK store. Mirrored onto `Base.ProviderProfile`
    /// by `create` so a non-AI handler in the same app can resolve it from DI.
    ProviderProfile: IProviderProfile
    /// `None` lets the composer auto-promote a blob-backed store when an
    /// `ISecretStore` is registered in DI.
    PlatformKeyStore: IPlatformAIKeyStore option
    /// Declarative accumulator for the wired platform providers, populated
    /// additively via `withPlatformProvider`.
    PlatformProviders: DefaultAIProviderFactory.AIPlatformProvider list
    AIConfig: AIAssistantServerConfig option
    ModuleAIContexts: ModuleAIContext list
}
```

AI tools are not an `AIServerApp` field — each module contributes them through `ServerModule.withAITools`, and they aggregate on the inner `Base.AITools`.

Constructors:

```fsharp skip=signature
module AIServerApp =
    val create: IAIProviderFactory -> IProviderProfile -> AIServerApp
    /// Lift an existing `ServerApp` so AI contributions stack onto whatever it
    /// already carries.
    val createFrom: IAIProviderFactory -> IProviderProfile -> ServerApp -> AIServerApp
```

Mirrored `ServerApp` builders (each delegates to the inner `Base`):
- `withConfig`, `withAuth`, `withLogger`, `withStorage`, `withNotifications`, `withUserDirectory`, `withCspContributor`, `withTransactionalSink`, `withHealthCheck`, `withConfigValidator`, `withEncryptedBlobStorage`, `withMetricsSink`, `withRateLimitDescriptor`, `withEntity`, `withJobHandler`, `withScheduledJob`, `withBackfillMissedTicks`, `withEventTriggerCatchUp`, `withAuditFailurePolicy`, `withEntityOutbox`, `withExtensions`, `withPreMiddleware`, `withPostMiddleware`, `addModule`, `addModules`.

AI-specific builders:
- `withProviderProfile: IProviderProfile -> AIServerApp -> AIServerApp`
- `withPlatformAIKeyStore: IPlatformAIKeyStore -> AIServerApp -> AIServerApp`
- `withPlatformProvider: DefaultAIProviderFactory.AIPlatformProvider -> AIServerApp -> AIServerApp`
- `withAIConfig: AIAssistantServerConfig -> AIServerApp -> AIServerApp`
- `withModuleAIContexts: ModuleAIContext list -> AIServerApp -> AIServerApp`
- `withAnswerVerifier`, `withNumericFidelityGate`, `withFastPathTriage`

Terminal:
- `run: AIServerApp -> int` — `composeAI >> ServerApp.run`.
- `composeAI: AIServerApp -> ServerApp` — the composition seam, for stacking AI contributions alongside another companion on one composition root.

### `AIAssistantServerConfig`

```fsharp
type AIAssistantServerConfig = {
    Branding: AIAssistantBranding
    /// `None` sends no system prompt at all.
    SystemPrompt: SystemPromptBuilder option
    /// Cap on prior provider-history messages replayed each turn. `None` uses
    /// `AIAssistantServerConfig.DefaultMaxHistoryMessages` (60); older turns are
    /// dropped with a Warn log so the truncation is observable.
    MaxHistoryMessages: int option
    /// How the server decides each turn's authoritative `AISurface`.
    AISurfaceDerivation: AISurfaceDerivationMode   // TrustClient | DeriveFromCookie of byte[]
}
```

Passed via `withAIConfig`. Both this record and the `SystemPromptBuilder` abbreviation live in the `ToolUp.AI.SystemPromptBuilder` module, so a composition root needs `open ToolUp.AI.SystemPromptBuilder` as well as `open ToolUp.AI`.

### `SystemPromptBuilder`

```fsharp skip=signature
type PromptContext = {
    Access: AccessContext
    ActiveModule: string option
    ActivePage: string option
    ActivePageNarrative: NarrativeDocument option
    ModuleContexts: Map<string, ModuleAIContext>
}

type SystemPromptBuilder = PromptContext -> Async<string>

module SystemPromptBuilder =
    val fromStatic: string -> SystemPromptBuilder
    val activeModuleContext: SystemPromptBuilder
    val currentNarrativeContext: SystemPromptBuilder
    val compose: SystemPromptBuilder list -> SystemPromptBuilder
```

`compose` runs builders in parallel and joins outputs with blank lines. Each builder's failure isolates (one returning `""` does not abort the others).

### `AIToolRegistry`

```fsharp skip=signature
type RegisteredTool = {
    Definition: AIToolDefinition
    Source: ToolSource           // PlatformBuiltin | ModuleDeclared of moduleName | CompanionContributed of companionName
}

module AIToolRegistry =
    val create: AIToolDefinition list -> AIToolRegistry
    val createTool:
        name: string ->
        description: string ->
        parameters: ToolParameterSchema ->
        executor: (JsonValue -> Async<ToolResult>) ->
        AIToolDefinition
    val toProviderDef: AIToolDefinition -> AIProviderToolDef
```

The agent loop pulls `toProviderDef` to translate `AIToolDefinition`s into the provider's tool schema format.

### `DefaultAIProviderFactory`

```fsharp skip=signature
module DefaultAIProviderFactory =
    val create:
        builders: AIProviderBuilder list ->
        configStore: IUserAIConfigStore ->
        secretStore: ISecretStore ->
        mode: BYOKMode ->
        AIProviderFactory

and BYOKMode =
    | PlatformOnly
    | AllowUserProviders
```

The factory returned closes over the builder list + stores. Per call, it resolves the active provider instance and instantiates an `IAIProvider`. Builders are looked up by `Descriptor.Id`.

### `AIProviderBuilder` + `AIProviderDescriptor`

```fsharp skip=fragment
type AIProviderDescriptor = {
    Id: string                  // unique provider id, e.g. "claude", "openai"
    DisplayName: string         // user-visible
    DefaultModel: string
    Capabilities: AIProviderCapabilities
}

type AIProviderBuilder = {
    Descriptor: AIProviderDescriptor
    Build: apiKey: string -> model: string -> IAIProvider
}
```

Each provider companion exposes one or more builders; `DefaultAIProviderFactory.create` consumes them.

### `AIAgentEngine`

```fsharp skip=signature
module AIAgentEngine =
    val runAgentLoop:
        provider: IAIProvider ->
        toolRegistry: AIToolRegistry ->
        systemPromptBuilder: SystemPromptBuilder ->
        context: AgentLoopContext ->
        Async<ConversationMessage>

and AgentLoopContext = {
    Conversation: Conversation
    UserMessage: ConversationMessage
    PromptContext: PromptContext
    MaxTurns: int
    EmitEvent: AIStreamEvent -> Async<unit>
    Cancellation: CancellationToken
}
```

The function is the agent's heart. Most apps don't call it directly — `AIServerApp.run` wires it via `AIAssistantHandler`. Exposed for advanced cases (custom assistant flows, agent-as-a-tool patterns).

### `AICompose`

```fsharp skip=signature
module AICompose =
    val composeWithAI:
        baseApp: ServerApp ->
        aiFactory: AIProviderFactory ->
        aiConfigStore: IUserAIConfigStore ->
        aiTools: AIToolDefinition list ->
        aiConfig: AIAssistantServerConfig option ->
        moduleAIContexts: ModuleAIContext list ->
        int
```

Called internally by `AIServerApp.run`. Returns the same `int` exit code as `ServerApp.run`. Exposed for callers that want to bypass the `AIServerApp` record shape and pass arguments directly.

## `ToolUp.AI.Client`

### `AIClientConfig`

```fsharp skip=signature
module AIClientConfig =
    val withAIAssistant:
        mode: AIAssistantMode ->
        config: ClientConfig ->
        modules: ErasedModule list ->
        Program<unit, OuterModel, OuterMsg, ReactElement>
```

Wraps the shell's Elmish `Program`. Adds the AI side-panel MVU + chrome around the base shell. The returned `Program` is fed into `Program.withReactSynchronous "elmish-app" |> Program.run`.

For deployments wanting just the side panel but not the full-page module, pass `ConfiguredAIAssistant { ... ShowSidePanel = true }` with `Branding.Name = ""` to suppress the sidebar entry.

### `SidePanelModel` / `OuterModel` / `OuterMsg`

Internal types exposed for apps that want to compose additional Elmish wrappers around `AIClientConfig.withAIAssistant`. Most apps don't need these.

### `ConversationPanel` (Feliz component)

```fsharp skip=fragment
ConversationPanel.render
    {| ConversationId = ConversationId
       Messages = ConversationMessage list
       OnSubmit = string -> unit
       ActiveModule = string option |}
```

Reusable chat panel. Used internally by the AI assistant module + the side panel. Apps can drop it into their own modules for chat-shaped UI without adopting the full assistant.

### `SSEClient`

```fsharp skip=signature
module SSEClient =
    val openConnection:
        baseUrl: string ->
        scopeId: string ->
        handler: AIStreamEvent -> unit ->
        IDisposable
```

EventSource wrapper. Handles reconnect (browser default) + mode-aware query parameter for SSE auth. Dispose to close the connection.

## Provider companion API surface

Each provider companion exposes:

```fsharp skip=signature
module ClaudeAIProvider =
    val builder: AIProviderBuilder
    val createWithApiKeyAndModel: apiKey: string -> model: string -> IAIProvider
    val descriptor: AIProviderDescriptor
```

The companion package's `.Server.props` injects supporting source files (e.g. wire-format helpers) into the consuming server project; the consuming app sees only the public surface above.

## Events emitted to `IEventStore`

Under `SourceModule = "_platform.ai"`:
- `ConversationCreated`, `MessageSent`, `MessageReceived`, `ToolCallExecuted`, `ConversationDeleted`

Under `SourceModule = "_platform.ai.latency"`:
- `AILatencyRecord` per turn (above).

Under `SourceModule = "_platform.ai.fastpath"`:
- `FastPathHit` per Tier 1 fast-path resolver hit (emitted when a downstream fast-path resolver is registered).

## HTTP endpoints

Auto-injected by `AIServerApp.run`:

- `POST /api/IAIAssistantApi/SubmitMessage` — ToolUp.Remoting
- `POST /api/IAIAssistantApi/GetConversation`
- `POST /api/IAIAssistantApi/ListConversations`
- `POST /api/IAIAssistantApi/GetAvailableTools`
- `POST /api/IAIAssistantApi/DeleteConversation`
- `POST /api/IAIAssistantApi/GetTaskStatus`
- `GET /api/notifications` — SSE; AI stream events ride this single endpoint alongside notifications

When `EnableDevEndpoints` is true:
- `GET /dev/ai-latency` — 60-min rolling stats (JSON)
- `GET /dev/ai-fastpath` — fast-path Tier stats (JSON; only when a fast-path consumer is registered)

## Configuration knobs

`AIAssistantServerConfig` (above):
- `MaxTurns` — default 10
- `DefaultMaxTokens` — default 4096
- `DefaultTemperature` — default 0.7
- `StreamingEnabled` — default true

`BYOKMode`:
- `PlatformOnly` (default)
- `AllowUserProviders`

Environment variables (read by `ClaudeAIProvider` / `OpenAIProvider` via `ISecretStore`):
- Provider API keys never come from env vars directly. Operators write keys into `ISecretStore` at setup; the SDK reads them per-call.
