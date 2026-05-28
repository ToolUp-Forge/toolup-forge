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

### `AIAssistantApi` (Fable.Remoting contract)

```fsharp
type IAIAssistantApi = {
    SubmitMessage: AIMessageRequest -> Async<AITask>
    GetConversation: ConversationId -> Async<Conversation option>
    ListConversations: unit -> Async<ConversationSummary list>
    DeleteConversation: ConversationId -> Async<unit>
    GetAvailableTools: unit -> Async<AIToolDescriptor list>
    GetTaskStatus: TaskId -> Async<AITask option>
}
```

Auto-injected via Fable.Remoting when AI is enabled. Caller binds the `IAIAssistantApi` proxy on the client; per-method auth gating handled by `makePermissionGuardedApi`.

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
    | MessageDelta of conversationId: Guid * taskId: Guid * delta: string
    | ToolCallStarted of conversationId: Guid * taskId: Guid * toolName: string * toolCallId: string * args: JsonValue
    | ToolCallCompleted of conversationId: Guid * taskId: Guid * toolCallId: string * result: ToolResult
    | TaskStatusChanged of taskId: Guid * status: AITaskStatus
    | MessageComplete of conversationId: Guid * taskId: Guid * messageId: Guid
    | StreamError of taskId: Guid * error: string
```

Streamed over SSE. Client routes by `NotificationKind = AIStream`.

### `AIToolDefinition`

```fsharp
type AIToolDefinition = {
    Name: string
    Description: string
    Parameters: ToolParameterSchema
    Executor: JsonValue -> Async<ToolResult>
    Visibility: ToolVisibility
    Capabilities: ToolCapabilities
}

and ToolVisibility =
    | ServerSide
    | ClientResident

and ToolCapabilities = {
    RequiresFullPage: bool      // ClientResident only
    RequiresPlatformAdmin: bool
}
```

Module-declared tools live in `Server.fs`; the composition root registers them via `ServerModule.withAITools [...]` or directly via `AIServerApp.withAITools`.

### `ToolParameterSchema`

```fsharp
type ToolParameterSchema = {
    Properties: (string * ParamType * string) list  // name, type, description
}

and ParamType =
    | StringP
    | StringArray
    | Integer
    | Number
    | Boolean
    | EnumP of cases: string list
```

Translates to JSON Schema at provider request time. The model sees `parameters: { type: "object", properties: { ... } }`.

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
    ConversationId: Guid
    Participants: Participant list
    Messages: ConversationMessage list
    Created: DateTime
    Updated: DateTime
    Title: string option
}

and ConversationMessage = {
    MessageId: Guid
    Role: MessageRole       // User | Assistant | System | Tool
    Content: string
    ToolCalls: AIProviderToolCall list
    ToolResults: AIProviderToolResult list
    Timestamp: DateTime
}
```

### `AITask`

```fsharp
type AITask = {
    TaskId: Guid
    ConversationId: ConversationId
    Status: AITaskStatus
}

and AITaskStatus =
    | Pending
    | Running
    | ToolCalling
    | Streaming
    | Completed
    | Failed of reason: string
    | Cancelled
```

### `IAIProvider`

```fsharp
type IAIProvider =
    abstract Capabilities: AIProviderCapabilities
    abstract SendMessage: AIProviderRequest -> Async<AIProviderResponse>

and AIProviderCapabilities = {
    ProviderName: string
    Model: string
    SupportsStreaming: bool
    SupportsToolUse: bool
    SupportsVision: bool
    SupportsPromptCaching: bool
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
    Role: ProviderMessageRole   // User | Assistant | System
    Content: string             // future: multimodal content blocks DU
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

### `IUserAIConfigStore`

```fsharp
type IUserAIConfigStore =
    abstract GetUserConfig: scopeId: string -> userId: string -> Async<UserAIConfig>
    abstract SaveUserConfig: scopeId: string -> userId: string -> config: UserAIConfig -> Async<unit>
    abstract GetPlatformDefault: unit -> Async<AIProviderInstance option>
    abstract SetPlatformDefault: AIProviderInstance -> Async<unit>

and UserAIConfig = {
    ActiveInstanceId: Guid option
    Instances: AIProviderInstance list
}

and AIProviderInstance = {
    InstanceId: Guid
    ProviderId: string
    Model: string
    SecretKeyRef: string
    DisplayName: string
}
```

### `AILatencyRecord`

```fsharp
type AILatencyRecord = {
    TaskId: Guid
    ConversationId: Guid
    TurnNumber: int
    ProviderName: string
    ProviderModel: string
    TtftMs: int option
    TurnDurationMs: int
    ToolCalls: ToolCallTiming list
    StopReason: StopReason
    Usage: TokenUsage option
}
```

## `ToolUp.AI.Server`

### `AIServerApp`

Flat superset of `ServerApp`. The fluent shape:

```fsharp
type AIServerApp = {
    Base: ServerApp
    AIFactory: AIProviderFactory
    AIConfigStore: IUserAIConfigStore
    AITools: AIToolDefinition list
    AIConfig: AIAssistantServerConfig option
    ModuleAIContexts: ModuleAIContext list
}
```

Constructors:

```fsharp
module AIServerApp =
    val create: AIProviderFactory * IUserAIConfigStore -> AIServerApp
    val empty: AIServerApp        // requires withAIFactory + withAIConfigStore before run
```

Mirrored `ServerApp` builders:
- `withConfig`, `withAuth`, `withLogger`, `withStorage`, `withSecretStore`, `withEventStore`, `withNotificationChannel`, `withConfigStore`, `withTeamStore`, `withPermissionStore`, `withScopeResolver`, `addModules`, `addModule`, `withHealthCheck`, `withConfigValidator`, `withMetricsSink`, `withAuditSink`, `withTransactionalSink`, `withNotificationAddressBook`, `withJobHandler`, `withDataSource`, `withEncryptedBlobStorage`, `withDevDiagnosticsContributor`, `withAnonymousRoute`, `withHttpsRedirection`, `withForwardedHeaders`, `withStaticPathBehaviour`.

AI-specific builders:
- `withAIFactory: AIProviderFactory -> AIServerApp -> AIServerApp`
- `withAIConfigStore: IUserAIConfigStore -> AIServerApp -> AIServerApp`
- `withAITools: AIToolDefinition list -> AIServerApp -> AIServerApp`
- `withAIConfig: AIAssistantServerConfig -> AIServerApp -> AIServerApp`
- `withModuleAIContexts: ModuleAIContext list -> AIServerApp -> AIServerApp`
- `withBase: ServerApp -> AIServerApp -> AIServerApp` (escape hatch for assembling an `AIServerApp` from a pre-built `ServerApp`)

Terminal:
- `run: AIServerApp -> int`

### `AIAssistantServerConfig`

```fsharp
type AIAssistantServerConfig = {
    Branding: AIAssistantBranding
    SystemPrompt: SystemPromptBuilder option
    MaxTurns: int                   // default 10
    DefaultMaxTokens: int           // default 4096
    DefaultTemperature: float       // default 0.7
    StreamingEnabled: bool          // default true
}
```

Passed via `withAIConfig`. When `SystemPrompt = None`, the default prompt builder is used (platform + active-module layers; no team layer).

### `SystemPromptBuilder`

```fsharp
type PromptContext = {
    Access: AccessContext
    ActiveModule: string option
    ModuleContexts: Map<string, ModuleAIContext>
}

type SystemPromptBuilder = PromptContext -> Async<string>

module SystemPromptBuilder =
    val fromStatic: string -> SystemPromptBuilder
    val activeModuleContext: SystemPromptBuilder
    val compose: SystemPromptBuilder list -> SystemPromptBuilder
```

`compose` runs builders in parallel and joins outputs with blank lines. Each builder's failure isolates (one returning `""` does not abort the others).

### `AIToolRegistry`

```fsharp
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

```fsharp
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

```fsharp
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

```fsharp
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

```fsharp
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

```fsharp
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

```fsharp
ConversationPanel.render
    {| ConversationId = ConversationId
       Messages = ConversationMessage list
       OnSubmit = string -> unit
       ActiveModule = string option |}
```

Reusable chat panel. Used internally by the AI assistant module + the side panel. Apps can drop it into their own modules for chat-shaped UI without adopting the full assistant.

### `SSEClient`

```fsharp
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

```fsharp
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

- `POST /api/IAIAssistantApi/SubmitMessage` — Fable.Remoting
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
