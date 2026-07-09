# ToolUp.AI Technical Guide

Deep technical reference for the `ToolUp.AI` companion package. Assumes familiarity with the [`ToolUp.Platform` Technical Guide](../ToolUp.Platform/TECHNICAL_GUIDE.md) (Giraffe / Fable / Elmish extensions, props injection, async chain, scope resolution).

## Architecture overview

```
Browser                            Server
──────                             ──────
AIAssistantUI / SidePanel          composeWithAI (ToolUp.AI)
   │                                     │
   │ ToolUp.Remoting                     │ wraps
   │ (SubmitMessage, etc.)               ▼
   ▼                               Server.compose (ToolUp.Platform)
AIAssistantApi proxy               │     │
   │                               │     │ ScopeResolutionMiddleware
   │ POST /api/ai/               ──┘     │   populates HttpContext.Items
   │      submitMessage                  │
   ▼                                     ▼
AIAssistantHandler                 Handler builds PromptContext
   │                                     │
   │ fires background async              │ SystemPromptBuilder runs
   │                                     │
   │                                     ▼
AIAgentEngine.runAgentLoop         System prompt (combines platform,
   │                                  active module, team content)
   │  IAIProvider.SendMessage          │
   │  (retry policy, capabilities)     ▼
   │                              IAIProvider (Claude, etc.)
   │ tool dispatch ←──────────────────  │
   │   try/with → ToolInvocationError   │
   │                                     │
   └─────────────────────────────────────┘
         │
         ▼
  SSEConnectionManager.Send
  (zombie-aware; removes dead conns)
         │
EventSource ←──────── /api/ai/events
```

## Anonymous mode and AI cost control

`AIServerApp.run` does not refuse to start when `ServerConfig.Mode = Anonymous`, but as a deployment design principle **AI is intended for authenticated modes** (`AuthenticatedEphemeral`, `Individual`, `Team`, `MultiTeam`). The README's "Authentication requirement" section is the canonical write-up; this section covers the technical rationale and what changes if a deployment runs AI in Anonymous mode anyway.

**Why authenticated modes are the design target.**

- `AccessContext.UserId` flows through `ScopeResolutionMiddleware` (in [`../ToolUp.Platform.Server/Server/Middleware.fs`](../ToolUp.Platform.Server/Server/Middleware.fs)) into every ToolUp.Remoting handler, every SSE connection, and every tool execution. In authenticated modes this identity is verified by `IAuthProvider.ValidateRequest`. In Anonymous mode it's a per-tab GUID generated client-side and trusted server-side via the `X-User-Id` header.
- Per-user cost attribution, per-user rate limits (`ServerConfig.RateLimit` partitioning), audit log entries, and conversation persistence all key off `UserId`. In authenticated modes those are real users; in Anonymous mode they're per-tab GUIDs that can be regenerated freely.
- BYOK (`AllowUserProviders`) requires the user to configure their provider in `IUserAIConfigStore`, which keys off `UserId`. In Anonymous mode this still works mechanically, but the configuration is per-tab and lost when the tab closes.

**What still works in Anonymous + AI.**

- Side panel and full-page surface both load and chat normally.
- SSE delivery works within a single tab — the per-tab GUID is the SSE subscription key, the same key publishes notifications.
- Conversation persistence works inside the per-tab session; `LocalFileStorage` writes under the `anonymous-{guid}` container, evicted by the ephemeral-mode background timer (default 60 minutes).
- `ModuleAction` notifications (Phase 6c) deliver to the same tab's open SSE connection.

**What does not work in Anonymous + AI.**

- Cross-tab fan-out: anonymous tabs are separate "users", so a notification published in tab 1 is invisible to tab 2.
- Per-user cost ceilings: there is no stable identity to attach a budget to. A motivated attacker can drive unlimited token consumption by opening tabs.
- Per-user rate limiting via `ServerConfig.RateLimit` with `Partition = ByUser` — partitions by per-tab GUID, easily bypassed. `ByIp` partitioning is the meaningful gate in Anonymous mode.
- Conversation continuity across sessions: closing the tab discards both the per-tab user identity and the conversation it was scoped to.

**Recommended layered protections for Anonymous + AI deployments.**

1. **BYOK-only**: configure `DefaultAIProviderFactory.create [...] BYOKMode = AllowUserProviders` so deployment never serves an in-house API key. Each user supplies their own.
2. **Network-level rate limiting**: WAF or reverse-proxy rules on `/api/ai/*` keyed by IP, with global concurrency caps.
3. **`ServerConfig.RateLimit` with `ByIp` partition**: the only partition that's meaningful when identity isn't trusted.
4. **CAPTCHA on first request**: gate the first AI call per browser session behind a human-verification check. Not currently built into the platform — would need to be layered as middleware.

## Agent loop — [`../ToolUp.AI.Server/Server/AIAgentEngine.fs`](../ToolUp.AI.Server/Server/AIAgentEngine.fs)

Multi-turn loop. Each iteration:

1. Call `IAIProvider.SendMessage` with the accumulated `AIProviderMessage` list, registered tools, system prompt, stream callback, and `RetryPolicy.defaults`. The provider returns `Async<Result<AIProviderResponse, AIProviderError>>`.
2. If the result is `Error err`, classify for the agent loop (see [Provider error classification](#provider-error-classification) below) and either retry once or raise — the handler's top-level `try/with` routes to `AITaskFailed`.
3. On `Ok response`: if no tool calls or `StopReason = "end_turn"`, append the assistant message and exit.
4. Otherwise, execute each tool call in parallel via `Async.Parallel`:
   - `registry.FindByName tc.Name` — locate the tool
   - `tool.Execute ctx tc.Arguments` — run it
   - Classification on failure:
     - No match → `ToolInvocationError.UnknownTool name`
     - `JsonException` → `InvalidArguments(name, detail)`
     - Any other `exn` → `ToolThrew(name, message)`
   - `ToolInvocationError.toToolResultContent` renders the error as a string so the model can react in its next turn
5. Append the tool results as a single user message (per provider protocol) and loop

The try/with sits inside a nested `async { }` so the error classification is itself part of the async computation — no exception leaks out of `tool.Execute` to break the parallel batch.

### Two error channels, two policies

Tool errors and provider errors are classified through distinct types and handled differently:

| | `ToolInvocationError` | `AIProviderError` |
|---|---|---|
| Source | Module-declared tool threw / registry miss / bad args | Provider transport or API response failed |
| Channel | Caught inside the tool-dispatch `try/with` | Returned via `SendMessage`'s `Result` |
| Recovery | Stringified and fed to the model as a tool result — model retries with different args, apologises, or recovers textually | Retried at the provider level (transient) or surfaced as `AITaskFailed` (catastrophic); never shown to the model |
| Loop effect | Loop continues — errors become part of the conversation | Loop exits with `failwith` → top-level `try/with` in handler → `AITaskFailed` |

The principle: tool errors are *in-conversation* signals the model can act on; provider errors are *out-of-band* signals the user (not the model) needs to see.

## Provider error classification

`SendMessage` returns `Async<Result<AIProviderResponse, AIProviderError>>`. Providers are responsible for classifying failures:

| Error case | Example trigger | Retry inside provider? | Agent-loop disposition |
|---|---|---|---|
| `TransientNetwork` | DNS failure, connection refused, TCP reset, client-side timeout | Yes (exponential backoff) | Fatal if it reaches the loop |
| `TransientServer(code, …)` | HTTP 429 or 5xx | Yes (exponential backoff) | Fatal if it reaches the loop |
| `PermanentClient(code, …)` | HTTP 4xx ≠ 429 — auth, bad request, model not found | No | Fatal |
| `MalformedResponse(detail)` | JSON parse or schema mismatch | No | Fatal |
| `StreamingAborted(partialText, detail)` | Stream broke after content was delivered to the client | **Never** | Fatal |
| `RetriesExhausted(attempts, last)` | Provider's own retry budget ran out | N/A (this *is* the exhaustion) | Outer-retryable — one extra attempt at the agent-loop level |

**Streaming is never retried** once any content has been delivered to the `onStream` callback. The accumulator has already emitted partial output; a re-attempt would duplicate the visible answer. Providers MUST classify mid-stream failures with delivered content as `StreamingAborted(partialText, …)` so callers can surface a diagnostic message rather than retry.

### Exponential backoff formula

`ClaudeAIProvider` delays `BackoffMs * 2^retryIndex` between attempts. With `RetryPolicy.defaults` (`BackoffMs = 500`, `MaxRetries = 3`): 500 ms, 1000 ms, 2000 ms — four total attempts across ~3.5 s. Non-retryable errors propagate immediately; retryable errors exhausting the budget wrap as `RetriesExhausted(4, lastError)`. `MaxRetries = 0` returns the raw error without wrapping (fail-fast contract).

### Per-call timeout

`RetryPolicy.TimeoutMs = Some ms` imposes a per-call deadline via `CancellationTokenSource`. The `HttpClient.Timeout` instance setting (5 min in Claude's ctor) remains as the upper ceiling. Caller sets `TimeoutMs` tighter than the client timeout to bound a single attempt without preventing future retries. Timeouts fire as `TransientNetwork` (no content delivered) or `StreamingAborted` (streaming, content delivered).

### Agent-loop outer retry

The agent loop applies **one** extra retry with a 2 s pause for `RetriesExhausted` specifically. Rationale: if the provider's backoff chain (~3.5 s for defaults) is shorter than the outage, the loop's pause may outlast it. All other fatal classes propagate immediately — they are definitionally not transient. See `AgentLoopOuterRetries` and `AgentLoopOuterRetryDelayMs` in `AIAgentEngine.fs`.

## Capability flags

`AIProviderCapabilities` (`src/ToolUp.Platform.Core/Shared/Interfaces/IAIProvider.fs`) declares what a provider supports:

```fsharp
type AIProviderCapabilities = {
    Streaming: bool             // provider streams partial responses via onStream
    ToolUse: bool               // provider can loop on tool_use stop_reason
    Vision: bool                // provider accepts image content in messages
    SupportsPromptCaching: bool // populates Usage.CachedPromptTokens on cache hits
    ProviderName: string        // diagnostics
    Model: string               // which model the instance is configured for
}
```

Flags are read by callers to gate features before invocation — they prevent silent mid-conversation failures when a deployment swaps providers (e.g., downgrades from Claude Sonnet to a model without Vision). `AIProviderCapabilities.unknown` is the conservative fallback; concrete providers declare explicitly.

### Current gating in the codebase

The flags exist and are declared by every provider implementation, but the SDK's agent loop and UI are **permissive by default** for non-fatal classes (Streaming, ToolUse, SupportsPromptCaching): features are invoked without checking flags first. Providers that don't support a capability must fail cleanly (typed `AIProviderError`, not silent truncation). **Vision is the exception** — Phase 6o wired synchronous capability rejection at `SendMessage` (see below). The patterns below document how callers *should* gate the remaining flags as the SDK tightens.

### Vision — shipped multipart wire protocol (Phase 6o)

`AIProviderMessage.Parts: AIContentPart list` carries the multipart payload. Plain-text turns leave it `[]` and continue to use `Content: string` — providers serialise to the vendor's legacy string shape, every existing call site is byte-for-byte unchanged. Multipart turns populate `Parts`; providers iterate it and emit the vendor-native content-block array (Anthropic `content: [{type: "text", ...}, {type: "image", source: {...}}]`; OpenAI Chat Completions `content: [{type: "text", text: ...}, {type: "image_url", image_url: {...}}]`).

```fsharp
open ToolUp.Platform.AI

// Build a multipart user message — text + one image.
let multipart =
    AIProviderMessage.multipart "user" [
        TextPart "What's on this receipt?"
        ImagePart {
            MediaType = "image/jpeg"
            Source = Base64Bytes receiptBytes  // or Url "https://..."
        }
    ]

// `Content` is auto-populated with the concatenated text parts so
// audit / latency events that read the text body have a fallback
// when image bytes are redacted.
```

`ImageSource` is either `Base64Bytes of byte[]` (in-memory image bytes; providers base64-encode at the wire boundary — the bytes never enter audit blobs) or `Url of string` (external URL the provider fetches server-side; providers that don't support URL sources reject with `UnsupportedCapability`).

**Capability rejection is synchronous.** Both Claude and OpenAI provider companions short-circuit on `AIProviderMessage.isMultimodal` against a per-vendor vision-capable model classifier; non-vision models return `AIProviderError.UnsupportedCapability("vision", ...)` without a network round-trip:

```fsharp
match! provider.SendMessage(msgs, tools, systemPrompt, onStream, retryPolicy) with
| Ok response -> ...
| Error (UnsupportedCapability("vision", detail)) ->
    // Provider can't handle multipart; surface the diagnostic
    // ("model 'claude-haiku-compact' does not accept image input")
    // and either retry without the image or pick a vision-capable
    // provider before resending.
    ...
```

**PII / payload-size redaction in audit + latency events.** Image bytes are PII-sensitive (faces, locations, sensitive documents) and large (typical mobile receipt: 1–5 MB base64). Never log them. The Core helpers `AIContentPart.redactedSummary` and `AIProviderMessage.redactedSummary` return a metadata-only string:

```fsharp
let summary = AIProviderMessage.redactedSummary multipart
// → "What's on this receipt? [image: 482133 bytes, type=image/jpeg]"
```

Use the helpers wherever an `AIProviderMessage` reaches an audit, latency, or trace emission site. The substrate is in place; per-site adoption is opt-in.

**Conversation export rendering.** `AIContentPart.exportPlaceholder` returns `![image #N]` for image parts (verbatim text for text parts) so paste-able markdown stays under the size budget. `ConversationPanel.exportAsMarkdown` routes through this helper when `ConversationMessage.Parts` is populated; plain-text turns continue to render `Content` verbatim.

**Out of scope.** Video / audio content blocks (defer until demand surfaces; harder to redact). Anthropic PDF-input (separate content-block type — Phase 6o.A follow-up if needed). Tool-call results carrying image content — the `query_receipt_image` pattern uses URL references via `Url` source, but tools returning image bytes to the LLM is Phase 6o.B.

### Worked example: gating streaming in the client

`AIAssistantUI` currently subscribes to SSE regardless of provider capability. When `Streaming = false`, the server ignores `onStream` and the non-streaming path delivers a single complete response — the UI streaming subscription is idle but harmless. To avoid the idle connection:

```fsharp
// Server — composeWithAI caller decides whether to register the SSE endpoint
if provider.Capabilities.Streaming then
    // register /api/ai/events
else
    // skip; the client falls back to polling GetTaskStatus
```

The streaming SSE endpoint and the underlying `SSEConnectionManager` remain useful for non-AI push events (team notifications, file-processing status). Gating the AI-specific registration, not the transport, keeps the channel generic.

**As of Phase 6a the `SSEConnectionManager` lives in core** (`src/ToolUp.Platform.Server/Server/Notifications/SSEConnectionManager.fs`) and is registered as a DI singleton by `SDK.Server.compose`. `composeWithAI` no longer constructs one — the AI handlers resolve it per-request from `ctx.RequestServices`. This is why `RAGCompose.aiHandlers` takes `HttpContext` and resolves the manager through `resolveManager ctx` rather than holding a captured reference. The wire format on `/api/ai/events` is unchanged; what changed is that notifications (`/api/notifications`, see core TECHNICAL_GUIDE) share the same connection registry.

### Worked example: gating tool use

`ToolUse = false` means the provider will never emit a `tool_use` stop reason. The agent loop currently checks `response.ToolCalls.IsEmpty` per turn, so a provider without tool-use support runs naturally — the empty list exits the loop on the first turn. The flag is informational for the UI ("Available tools" pane should hide when `ToolUse = false` to avoid raising expectations):

```fsharp
// AIAssistantUI.fs — conditionally render the tool list
if provider.Capabilities.ToolUse && not model.AvailableTools.IsEmpty then
    renderToolList model.AvailableTools
```

### Flag declaration contract

Providers MUST declare every flag explicitly (true or false) in their `Capabilities`. `AIProviderCapabilities.unknown` is for detection of mis-wired providers, not a legitimate state to ship. `ProviderName` and `Model` MUST be populated — logs and dashboards key off them.

## System prompt — [`../ToolUp.AI.Server/Server/SystemPromptBuilder.fs`](../ToolUp.AI.Server/Server/SystemPromptBuilder.fs)

The system prompt is built per request, not once at startup. The builder receives a `PromptContext` and returns an `Async<string>`.

### Where the context comes from

```
┌── Client ─────────────────┐       ┌── Server ──────────────────────┐
│                           │       │                                │
│ AIMessageRequest {        │       │ ScopeResolutionMiddleware      │
│   ConversationId          │────►  │   AccessContext (DI scoped)    │
│   Content                 │       │   HttpContext.Items            │
│   ActiveModule: Some "X"  │       │                                │
│ }                         │       │ AIAssistantHandler             │
│                           │       │   ctx = AccessContext          │
└───────────────────────────┘       │   req = AIMessageRequest       │
                                    │                                │
composeWithAI(..., moduleContexts)  │ PromptContext =                │
     └─── Map<ModuleName, Context> ─┴──► {                           │
                                        │   Access = ctx             │
                                        │   ActiveModule = req.Active│
                                        │   ModuleContexts = map     │
                                        │ }                          │
                                        │                            │
                                        │ config.SystemPrompt |> builder
                                        │       ▼                    │
                                        │ system prompt text         │
                                        └────────────────────────────┘
```

### Built-in builders

**`fromStatic s`** — back-compat shim. Identical to the pre-Phase-1b `SystemPromptPrefix = Some s`.

**`activeModuleContext`** — injects the active module's `ModuleAIContext.SystemPrompt`:

```fsharp
let activeModuleContext: SystemPromptBuilder =
    fun ctx -> async {
        match ctx.ActiveModule with
        | Some name ->
            match ctx.ModuleContexts.TryFind name with
            | Some moduleCtx -> return moduleCtx.SystemPrompt
            | None -> return ""
        | None -> return ""
    }
```

Empty strings are dropped by `compose`, so a client chatting from a non-module view silently contributes nothing here.

**`compose [b1; b2; ...]`** — parallel-resolves the builders, filters empty results, joins with `"\n\n"`:

```fsharp
let compose builders : SystemPromptBuilder =
    fun ctx -> async {
        let! parts = builders |> List.map (fun b -> b ctx) |> Async.Parallel
        return parts |> Array.filter (fun s -> s <> "") |> String.concat "\n\n"
    }
```

Parallel resolution matters when a builder makes a network call (team profile from blob storage, config lookup, etc.) — slow external fetches don't serialise.

### Team-private context pattern

```fsharp
let teamAwarePrompt (platformPrefix: string) (teamStore: TeamStore) : SystemPromptBuilder =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic platformPrefix
        SystemPromptBuilder.activeModuleContext
        fun ctx -> async {
            match ctx.Access.TeamId with
            | None -> return ""                     // anonymous or individual mode
            | Some teamId ->
                // Team A's request gets Team A's profile; B gets B's.
                // Middleware validated membership before this point.
                let! profile = teamStore.GetTeamProfile teamId
                return $"""The current team is {profile.Name}.
                          Category: {profile.Category}.
                          Brands in scope: {profile.Brands}."""
        }
    ]
```

**Isolation guarantee.** `ScopeResolutionMiddleware` runs before this handler; it validates team membership and rejects cross-team requests with `NotTeamMember` before reaching `PromptContext`. A builder that reads `ctx.Access.TeamId` is reading a value the middleware already verified.

**Caching.** If the team profile is expensive to fetch, cache it in the `TeamStore` layer or upstream. The builder shouldn't do its own caching — that makes invalidation a user-level concern.

### What "private" means here

The SDK has **no mechanism for users to send invisible prompts.** Everything in the user-visible conversation stays in the user-visible conversation. "Private" in this design refers exclusively to **module-registered system-prompt content** — declared at compose time, injected on every request where that module is active, never appearing in chat history but always reaching the model.

This intentional limit protects the audit trail (`AnalysisCompleted` events in Phase 8, audit logs in Phase 9). Hidden user prompts would create conversations that can't be reconstructed from persisted history.

## Built-in AI tools

The SDK registers two families of platform-owned tools automatically inside `composeAI` (`AICompose.fs`) — apps do **not** declare them. Both reserve platform-prefixed names so a module tool can never collide; `composeAI` fails loudly at compose time on any duplicate `Name` across built-ins + module tools.

### Narrative tools — [`../ToolUp.AI.Server/Server/NarrativeTools.fs`](../ToolUp.AI.Server/Server/NarrativeTools.fs)

`list_narratives` / `get_narrative` / `get_narrative_section` / `publish_narrative` / `list_layouts` — surface and publish narrative output produced on other pages, scoped to the caller's `StorageScope`.

### `_platform.ai.*` — cross-module read family (Phase 36.B) — [`../ToolUp.AI.Server/Server/PlatformAITools.fs`](../ToolUp.AI.Server/Server/PlatformAITools.fs)

Six adapters over already-audited substrate (`IDataCatalog`, `IModuleQueryBus`, `IEntityStore`, `IResultStore`) that let the model roam across a user's module data without each module re-exposing read primitives:

| Tool | Wraps | RBAC gate |
|---|---|---|
| `_platform.ai.list_accessible_modules` | `IDataCatalog` producers ∪ `AccessContext.ModulePermissions` | filtered by `canAccessModule`; `rbacConfigured=false` ⇒ all modules, `["unrestricted"]` |
| `_platform.ai.list_data_types` | `IDataCatalog.ListTypes` ∩ `GetProducers` | only types with ≥1 accessible producer |
| `_platform.ai.query_module` | `IModuleQueryBus.Ask` | the bus's own `hasPermission` Read gate → `PermissionDenied` |
| `_platform.ai.query_entity` | `IEntityStore.Query<JsonElement>` | scope-isolated by `scopeId` (GP 4); predicate references declared indexes only |
| `_platform.ai.list_results` | `IResultStore.ListResults` | explicit per-module `hasPermission` Read pre-check |
| `_platform.ai.get_latest_result` | `IResultStore.GetLatest` | explicit per-module `hasPermission` Read pre-check |

All six register `Location = ServerResident`, `Surface = Both`, `SourceModule = "_platform.ai"` (the shared `SourceModule` keeps Phase 36.A's per-module dispatch RBAC filter from hiding them — per-*target* RBAC is enforced inside each tool). Errors are returned as typed JSON objects (`PermissionDenied`, `ModuleNotFound`, `NoHandler`, `NotFound`, `EntityStoreUnavailable`, `ResultStoreUnavailable`, …) so the model can re-plan rather than treating a refusal as a transient fault.

**Access-context resolution (load-bearing seam).** Executors run inside the agent loop's *background* `HttpContext` (`createBackgroundContext`), whose DI `AccessContext` factory reads the now-disposed live request via `IHttpContextAccessor` and would yield an *unrestricted anonymous* context — bypassing RBAC. The tools instead reconstruct the `AccessContext` from the `ToolUp.StorageScope` / `ToolUp.UserId` / `ToolUp.ModulePermissions` items the background context copies forward, which is the RBAC-correct path and also works on a real request context.

**Predicate shape for `query_entity`.** A small JSON AST the model authors directly (not the `FableConverters` DU wire form): `{"op":"eq","field":"Mood","value":"happy"}`; ops `eq`/`ne`/`gt`/`gte`/`lt`/`lte` (string-ordered), `in` (`{"op":"in","field":"x","values":[…]}`), `and`/`or` (`{"op":"and","left":{…},"right":{…}}`), `not` (`{"op":"not","inner":{…}}`).

**Out of scope (deferred):** `_platform.ai.write_*` (cross-module mutation needs a different consent model); streaming / paging (initial impl soft-caps at 100 entities / ~1MB result content).

## Module tools

Modules declare tools via `AIToolDefinition` (in core, `src/ToolUp.Platform.Core/Shared/Types/ModuleAITypes.fs`). The declaration has no execution logic:

```fsharp
// In MediaOptimisation/Server.fs
let tools: AIToolDefinition list = [
    {
        Name = "media_optimisation.load_data"
        Description = "Load media response curve data from a file"
        Parameters = [
            { Name = "file_name"; Type = "string"; Description = "..."; Required = true; Default = None }
        ]
        SourceModule = "MediaOptimisation"
        EmitsActions = None
    }
    // ...
]
```

The application supplies the executor in `AITools.fs`:

```fsharp
let allTools: RegisteredTool list = [
    createTool
        MediaOptimisation.Server.loadDataTool
        (fun ctx argsJson -> async { ... })
    // ...
]
```

`createTool` auto-generates the JSON Schema (`AIProviderToolDef`) from the declaration — modules don't hand-write schema strings. Tool names are sanitised (`.` → `_`) to satisfy Claude's `^[a-zA-Z0-9_-]{1,128}$` constraint.

The registry is a mutable list populated via `RegisterAll(tools)` during compose. It's immutable after startup — no hot-swapping.

### Two tiers: chat-only vs client action (Phase 6c)

Every AI tool runs server-side and returns a JSON string to the agent loop. That JSON is the **Level 1** result — it reaches the user as text inside the chat. For many tools that is the whole story: the user reads the numbers, decides what to do, and takes the action themselves.

**Level 2** adds a second channel: the tool, in addition to returning its JSON, publishes a `Notification.ModuleAction(moduleId, actionKey, payloadJson)` targeting a specific client module. The shell's client-side router looks the module up, gates on `AccessibleModules`, calls the module's `ActionDecoder`, and dispatches the decoded `Msg` — the module's result panel updates in-place without the user copy-pasting anything.

**When to pick Level 1 (chat-only, `EmitsActions = None`):**

- Long-running / background tasks that should complete whether the user is looking at the platform or not (overnight re-fits, bulk ingestion). The JSON return is an audit-worthy artefact; a transient UI update is the wrong shape.
- Conversations initiated from the AI Assistant page where no specific module is in focus. There is nowhere for module-targeted state to land; text in the chat is the only coherent surface.
- Tools that answer questions the user will decide against (e.g. "what's my elasticity on product X?") rather than applying. Reading the chat is the right interaction.

**When to add Level 2 (`EmitsActions = Some [...]` + `ToolContext.emitAction`):**

- The user is actively viewing a module and asks the assistant to act on it ("optimise my budget", "apply these values").
- The result has a natural home in one specific module's UI — a chart to render, inputs to pre-fill, a result panel to populate.
- The numbers themselves are more useful *in context* (in the module's own tables / charts) than as a block of text the user has to re-type.

**Wiring a Level 2 tool.** Three pieces:

1. **Server-side tool declaration.** Add `EmitsActions = Some [...]` on the `AIToolDefinition`, enumerating one `ActionDeclaration` per `(moduleId, actionKey)` the executor can publish. The declaration is documentation and (future) catalog inspection — not an authorisation boundary. A tool that emits an undeclared action is a bug, not a permission violation.

2. **Server-side executor.** At the end of the executor, in addition to `return resultJson`, call `ToolUp.AI.ToolContext.emitAction ctx moduleId actionKey payloadJson`. The helper resolves `INotificationChannel` from DI and publishes to the caller's user-id scope (same scope the SSE endpoint subscribes under). No-ops silently when the channel isn't registered.

3. **Client-side `ActionDecoder` on the module.** Attach a `(actionKey, payloadJson) -> Msg option` function via `ClientModule.withActionDecoder` (or set `ActionDecoder = Some decoder` on the record). The decoder matches on `actionKey`, parses `payloadJson` with `Fable.SimpleJson` — the server serialises with `Fable.Remoting.Json.FableJsonConverter` so the shapes round-trip — and returns the module's own `Msg`. Unknown keys and parse failures return `None`; the shell silently drops those.

**Foreground vs background routing.** The shell inspects `ActiveModuleId` when the envelope arrives:

- **Active target.** The decoded `Msg` dispatches through the existing `ModuleMsg` pathway. The module's `update` runs, its view re-renders, the user sees the change. The tool's JSON also arrives in the chat.
- **Inactive but loaded target** (user has visited the module this session, so its `Model` lives in `ModuleStates`). The `Msg` still dispatches — against the inactive module's state directly — and the shell synthesises a `SystemMessage(Info, "Results available in {module}")` via `NotificationClient.publishLocal`, which the `ToastCentre` picks up on the same subscription. Switching to the module shows the populated panel.
- **Inactive and not loaded** (user has never visited the module this session). The shell init-s the module, applies the `Msg`, synthesises the toast. Init-time Cmds are discarded for inactive targets — `ModuleMsg` targets `ActiveModuleId` and rerouting a Cmd to a non-active module isn't a wire the shell provides today. Most `Init` implementations return `Cmd.none`; any that don't run on next navigation.
- **Not registered** (filtered out of the deployment, not in `AccessibleModules.Accessible` for this user, `DebugOnly` in Release). The action silently drops. The chat JSON is the only surface. The server-side tool guard is the real authorisation boundary — the client-side drop is defense-in-depth.

**Worked example: `media_optimisation.apply_optimised_budget`.**

Server-side (`Modules/MediaOptimisation/Server.fs` — the declaration):

```fsharp
{
    Name = "media_optimisation.apply_optimised_budget"
    Description = "Run budget optimisation AND apply the result to the Media Optimisation module in-place."
    Parameters = [ (* same shape as media_optimisation.run *) ]
    SourceModule = "MediaOptimisation"
    EmitsActions = Some [
        {
            ModuleId = "MediaOptimisation"
            ActionKey = "apply-optimised-budget"
            Description = "Replace the current allocation with the AI-computed result."
            PayloadSchema = None
        }
    ]
}
```

Server-side (`ToolUpApp-Server/AITools.fs` — the executor):

```fsharp
let private executeMediaOptApplyBudget ctx argsJson = async {
    // (same argument parsing + optimisation as media_optimisation.run)
    let result = MediaOptimisation.Server.optimiseCurvesRoutine request
    let resultJson = fableSerialize result

    // Side channel: push the result to the client module.
    do! ToolUp.AI.ToolContext.emitAction ctx "MediaOptimisation" "apply-optimised-budget" resultJson

    return resultJson  // also still returned to the agent loop for the chat
}
```

Client-side (`Modules/MediaOptimisation/ClientView.fs` — the decoder):

```fsharp
let private decodeAction: string * string -> Msg option =
    fun (actionKey, payloadJson) ->
        match actionKey with
        | "apply-optimised-budget" ->
            try
                let result = Json.parseAs<MediaOptimisationResult> payloadJson
                Some (OptimiseMedia (Finished result))  // reuses the existing completion pathway
            with _ -> None
        | _ -> None

// Then in register:
//   ...
//   ActionDecoder = Some decodeAction
//   ...
```

The decoder dispatches `OptimiseMedia(Finished result)` — the exact message the module already handles when a user-triggered optimise returns from the server. Curves, envelope, and the result panel update identically. The AI gets in-place UI integration without the module author writing any new update logic.

### Client-resident companion authoring

The third tier — `Location = ClientResident` tools — flips the dispatch direction entirely: the tool body runs in the browser. The server agent loop emits a `ClientToolInvoke` SSE event; the browser-side runtime (`ToolUp.AI.Client/Client/ClientToolRuntime.fs`) decodes it, runs the F# tool body, and POSTs the result to `/api/ai/tool-result`. The server's `ClientToolDispatchRegistry` matches the POST back to the suspended agent loop. Typical consumers use this path to let an LLM operate the UI in front of the user — set form fields, click buttons, navigate, select grid rows.

A new client-resident companion must clear two SDK conformance bars before it is composed into a deployment. Both ship as Expecto packs in `ToolUp.Platform.Tests/Contracts/` so any third-party companion can bind against them in its own test suite without re-authoring the harness:

1. **[`IClientToolAuthorizerContract`](../ToolUp.Platform.Tests/Contracts/IClientToolAuthorizerContract.fs)** — per-decision invariants on the companion's [`IClientToolAuthorizer`](../ToolUp.AI.Core/Shared/AITypes.fs) implementation. Covers: Allow / Deny shape, idempotency (rule 4 — stateless), never-throws on malformed / empty `argsJson`, never-throws on `None` active module / page, identity-by-value (rule 1 — structurally-equal inputs return identical decisions), and no cross-call ordering (rule 5 — parallel authorisations are independent).

2. **[`IClientToolDispatchContract`](../ToolUp.Platform.Tests/Contracts/IClientToolDispatchContract.fs)** — full round-trip behavioural contract on the dispatch path. Drives `AIAgentEngine.runAgentLoop` end-to-end with a scripted `IAIProvider` + the companion's authorizer + a caller-supplied client simulator. Asserts: Allow round-trip emits exactly one `ClientToolInvoke` per call and the simulator's result reaches the loop cleanly; Deny short-circuits before any SSE emit and writes the `_platform.ai.tool_allowlist_denial` event; concurrent tool calls in one turn receive distinct `ToolCallId` Guids (rules 1 + 5); completing one pending TCS in the dispatch registry does not affect another (rule 4).

Both packs are fixture-style — bind by handing over the authorizer + the two anchor tool names (one the policy allows, one it denies) + a simulator function. The pack owns the rest (scripted provider, registry, dispatch registry, `IEventStore`, `HttpContext`). Bind from your companion's test project:

```fsharp
open ToolUp.AI
open ToolUp.Platform.Tests.Contracts

type private MyCompanyAuthorizer(policy) =
    interface IClientToolAuthorizer with
        member _.Authorize(toolName, argsJson, activeModule, activePage) =
            // Your policy decision; MUST NOT throw on any input.
            if MyPolicy.allows policy toolName activeModule then Allow
            else Deny "not in MyCompany allowlist"

let authorizerTests =
    IClientToolAuthorizerContract.tests {
        Name = "MyCompanyAuthorizer"
        Authorizer = MyCompanyAuthorizer(myPolicy) :> IClientToolAuthorizer
        AllowedCall = ("mycompany.tool", "{}", Some "MyModule", Some "/page")
        DeniedCall = ("blocked.tool", "{}", Some "MyModule", Some "/page")
    }

let dispatchTests =
    IClientToolDispatchContract.tests {
        Name = "MyCompanyAuthorizer + handler"
        Authorizer = MyCompanyAuthorizer(myPolicy) :> IClientToolAuthorizer
        AllowedToolName = "mycompany.tool"
        DeniedToolName = "blocked.tool"
        Simulator = fun _evt -> Some """{"ok": true}"""
    }
```

Implementation rules the contract packs enforce: the authorizer is **sync** (mirrors `IMetricsSink`'s sync exemption — the consult runs on the agent-loop hot path before each `ClientToolInvoke`; awaiting buys nothing and would block the parallel dispatch branch), MUST NOT throw on any input (a malformed `argsJson` is a `Deny`, not an exception), and MUST be stateless between calls (the contract pack runs the same call twice and demands the same decision). The dispatch path itself enforces append-only registration into the platform's nexus files (composition root, `ServerConfig`, `*.sln`) — a companion registers its authorizer through `IServiceCollection.AddSingleton<IClientToolAuthorizer>` during its own `Compose.register`, never by editing platform files.

The agent loop's behavioural promise on the Deny path is the trust boundary that gives prompt-injection mitigation its load-bearing role: a denied call **never reaches the browser**, the model is told the action was refused via a typed `Denied of toolName * reason` value (the retry-as-data shape — rule 3), and an audit row lands in `IEventStore` so operator visibility is part of the contract, not an opt-in. See [`docs/ai/extending.md`](../../docs/ai/extending.md) §"Client-resident tool authorization contract" for the seam-level walkthrough.

**Reference companion.** The Apache-2.0 in-tree sample at [`src/AI.Samples/ToolUp.AI.SampleClientTool.{Core,Server,Client}/`](../AI.Samples/) is the worked example of every step above. Its server-side `Compose.register` / `registerWithPolicy` matches the recommended shape; its Fable handler in `SampleHandler.fs` runs `CalcOps.compute` shared with the server tier. The sample's own README walks new companion authors through the four-step pattern (Core types → server `register` → client handler → compose) in ≤10 minutes. Use it as the starting point for any new client-resident-tool companion — the calculator-shaped sample deliberately strips out the module + policy translation layers a larger companion might layer on top.

## Surface determination & trust model

`AIMessageRequest.Surface : AISurface` (`SidePanel | FullPage`) tells the server which chat surface the user submitted from. The agent loop filters the per-turn tool list by it (`AIToolRegistry.isToolVisibleOnSurface`, applied in `runAgentLoop`): a tool declaring `Surface = FullPageOnly` is offered to the model only when the request's `Surface = FullPage`; `SidePanelOnly` only on `SidePanel`; `Both` (the default every existing tool carries) always passes. This is how the lightweight side panel hides tools that only make sense on the full-page assistant.

### The field is client-supplied — surface gating is NOT a security boundary

`Surface` arrives on the wire inside `AIMessageRequest`, set by the browser that opened the chat. **In the default configuration the server does not verify it.** A client submitting from a side-panel context can put `Surface = FullPage` on the request and the agent loop will expose `FullPageOnly` tools to that turn. Treat the surface filter as a **UX affordance that keeps the model focused on the right tools for the surface — not as an authorization gate.**

### Blast radius — why the default is a bounded risk

`FullPageOnly` marks tools that only belong on the full-page assistant — in practice the **client-resident** family (`Location = ClientResident`) that drives the UI in front of the user (set a form field, click a button, navigate a page). A client-resident tool executes in **the calling client's own browser**: the agent loop emits a `ClientToolInvoke` SSE event that only that user's session receives, the tool body runs there, and the result POSTs back keyed by the same `ToolCallId` (see [Client-resident companion authoring](#client-resident-companion-authoring)). So a client that lies about its surface unlocks tools that act on **its own** browser and **its own** session — there is no cross-user or cross-tenant reach.

Crucially, the surface field gates nothing that the resolved `AccessContext` gates:

- **Tenant / team isolation (GP 4)** is carried by the storage-scope resolver and the `Subject` resolution, never by `Surface`. Spoofing the surface cannot read another tenant's data.
- **The per-invocation `IClientToolAuthorizer` Deny path** evaluates server-side on every client-resident call regardless of surface. A tool the policy denies never reaches the browser even if the surface filter let the model see it.

The residual risk is confined to a single client coaxing the model into offering it a UI action it could equally have performed by hand in its own browser. **Module authors wiring `FullPageOnly` tools must not treat the surface filter as withholding a capability the caller shouldn't have at all** — put that authorization in the tool's own `AccessContext` check or its `IClientToolAuthorizer` (both server-evaluated, neither client-supplied). The surface filter decides *which tools the model is shown*; it is not the boundary that decides *what the caller is allowed to do*.

### Opt-in: server-derived surface (`AISurfaceDerivation`)

Deployments that want defence-in-depth — a surface the server can corroborate rather than trust — set the mode on `AIAssistantServerConfig`:

```fsharp
type AISurfaceDerivationMode =
    | TrustClient                        // default — trust AIMessageRequest.Surface
    | DeriveFromCookie of signingKey: byte[]

// on AIAssistantServerConfig
{ …; AISurfaceDerivation = DeriveFromCookie signingKey }
```

- **`TrustClient` (default)** takes `AIMessageRequest.Surface` at face value. A deployment that leaves the default is **byte-for-byte unchanged** — no cookie read, no signing key, no behavioural difference (GP 11 / GP 13).
- **`DeriveFromCookie signingKey`** ignores the request field for the *authoritative* surface and derives it from a short-lived HMAC-SHA256-signed capability cookie: `toolup-ai-surface=fullpage; HttpOnly; Secure; SameSite=Strict`. `AIAssistantHandler` reads it off the **original** request context (`AISurfaceCapability.resolveSurfaceFromRequest`) — the synthetic background context the agent loop runs under carries no cookies, so the read must happen up front — and passes the resolved surface into `runAgentLoop`. A client presenting `Surface = FullPage` without a valid, unexpired, correctly-signed cookie is **demoted to `SidePanel`**, and the disagreement is logged at Warn.

**Issuing the cookie.** The server mints the capability cookie when the user actually navigates into a full-page AI module. Forge supplies the substrate — `AISurfaceCapability.issueFullPageCookie` (mint + `Set-Cookie` with the hardened attributes) and `clearCookie` — while the *navigation trigger* is a consumer concern: the consumer calls `issueFullPageCookie` from the server-side handler backing its full-page-module route, and `clearCookie` on navigation away or sign-out. The security-meaningful half (mint / validate / the pure `resolveSurface` demotion gate) lives entirely in forge and is covered by `AISurfaceCapabilityTests`.

**Signing key.** The key is carried inline on the mode (`DeriveFromCookie of byte[]`) so the resolver stays a pure function with no secret-store plumbing; a deployment sources it however it sources its other signing material (e.g. from `ISecretStore` at compose time). An empty key never validates — a mis-wired `DeriveFromCookie` demotes every turn to `SidePanel` rather than silently granting.

## SSE — [`../ToolUp.AI.Server/Server/SSEHandler.fs`](../ToolUp.AI.Server/Server/SSEHandler.fs) + [`../ToolUp.AI.Client/Client/SSEClient.fs`](../ToolUp.AI.Client/Client/SSEClient.fs)

### Connection lifecycle

1. Client opens `EventSource("/api/ai/events?userId=X")` (EventSource can't send custom headers, hence the query param)
2. Server handler sets `Content-Type: text/event-stream`, registers the connection with `SSEConnectionManager.Add(scopeId, conn)`, then holds the request open
3. Background loop writes a keepalive comment every 15 s
4. On client disconnect (`ctx.RequestAborted.IsCancellationRequested`), loop exits and `manager.Remove(scopeId, conn)` runs
5. `SSEConnectionManager.Send` also removes dead connections: cancellation-observed-but-not-yet-cleaned AND write-failures both trigger eager Remove (zombie protection added in Phase 1a)

### Serialisation rule

The SSE payload is raw `text/event-stream` with JSON that must round-trip through `Fable.SimpleJson` on the client. That means:

- **Server uses `Fable.Remoting.Json.FableJsonConverter`** for every `AIStreamEvent` serialisation
- **Not** Newtonsoft's `DiscriminatedUnionConverter` (produces `{"Case":"X","Fields":[...]}` which SimpleJson can't parse)
- **Not** `CamelCasePropertyNamesContractResolver` (SimpleJson expects PascalCase)

This is a load-bearing rule. As of the Phase 6h follow-up (2026-05-05), parse failures on the client now dispatch a `StreamError` event with a truncated preview of the offending payload — they no longer silently drop. Breaking the serialisation rule will surface as a visible "⚠️ Malformed SSE event: …" message in chat, not as a 60-second silent watchdog wait.

### Failure surfacing (Phase 6h follow-up)

The chat path was previously biased toward "silence on failure" — every layer swallowed errors so aggressively that an environmental hiccup (provider API down, slow blob storage, wedged SSE subscriber) presented to the user as a 60-second timeout with no diagnostic signal. The Phase 6h follow-up reversed this on the side panel:

| Layer | Was | Now |
|-------|-----|-----|
| `AIClientConfig.SidePanelMsg.ApiError` | discarded silently, only watchdog cleared | renders `⚠️ Couldn't reach the AI service: {reason}` inline |
| `AIClientConfig` `TaskStatusChanged AITaskFailed` | rendered only when `DebugMode = true` | rendered always, as `⚠️ The agent failed: {reason}` |
| `AIClientConfig` `StreamError` | rendered only when `DebugMode = true` | rendered always, as `⚠️ Stream error: {reason}` |
| `SSEClient` parse failure | silent `with _ -> None` swallow | dispatches `StreamError "Malformed SSE event: …"` |
| `SSEClient` connection error | console warn only | dispatches `StreamError "Stream connection error — retrying"` (warn still logged) |

The full-page AI module (`AIAssistantUI.fs`) already had a dedicated `ErrorMessage` banner for `ApiError` and was less affected; the side panel diverged and is now caught up.

Server-side, three call paths gained safety timeouts so a stalled backend produces a `TimeoutException` (caught by the existing `bgWork` `try/with` and dispatched as `AITaskFailed`) instead of hanging until the client-side watchdog:

| Site | File | Cap | Why |
|------|------|-----|-----|
| `IEventStore.Write` for latency telemetry | `AIAgentEngine.fs` `emitLatency` | 5 s | Telemetry must never crash OR block the conversation |
| Provider resolution (`Resolve` / `TryResolveByLabel`) | `AIAssistantHandler.fs` `withTimeout` | 10 s | Touches `IUserAIConfigStore` (blob) + `ISecretStore` |
| `loadConversationMeta` / `loadProviderHistory` / `loadConversation` | `AIAssistantHandler.fs` `withTimeout` | 10 s | Blob I/O on the chat hot path |
| `SSEConnectionManager.writeOne` | `SSEConnectionManager.fs` linked CTS | 5 s | One stalled subscriber must not wedge a whole scope |

Phase 6k will replace the `Async.Start bgWork` fire-and-forget pattern with a supervised hosted-service worker, fix `FileSecretStore` sync-over-async, and replace `SSEConnectionManager`'s `Task.WhenAll` fan-out with per-connection writer queues. The Phase 6h follow-up timeouts are the bounded-by-default safety nets in the meantime.

### Trace logging (Phase 6k Workstream B — shipped 2026-05-05)

The diagnostic lines we hand-rolled across the AI-not-responding bug chase are now permanent observability. Set `TOOLUP_LOG_LEVEL=Trace` and `TOOLUP_TRACE_CATEGORIES=ai.agent,ai.sse,platform.sse` to light them up — server stays at Info-and-above for everything else.

Trace category catalogue (current):

| Category | What it traces | Files |
|----------|---------------|-------|
| `ai.agent` | bgWork start (userId/taskId/conversationId), each `provider.SendMessage` attempt + return | `AIAssistantHandler.fs`, `AIAgentEngine.fs` |
| `ai.sse` | Reserved for future per-event SSE diagnostics; the ring-buffer (below) covers most cases today | `SSEHandler.fs` |
| `platform.sse` | Reserved for cross-channel SSE diagnostics in `SSEConnectionManager` | `SSEConnectionManager.fs` |
| `auth` | Reserved for `IAuthBridge` JWT refresh flow (Workstream A) | `UserSession.fs` |

Adding a new category: pick a dotted-namespace name (`subsystem.subcomponent`), call `Logger.trace logger "your.category" "message"` at the call site, document it in the table above. Categories are free — no central registry. Spell consistently.

### `/dev/sse-trace` panel (Phase 6k Workstream B)

When `EnableDevEndpoints = true`, `/dev/inspect` includes an "SSE trace" panel rendered by `SseTraceContributor`:

- **Recent broadcasts** (last 100, most-recent-first): `Timestamp`, `ScopeId`, `EventKind`, `PayloadBytes`, `ConnectionCount`, `Dropped` flag.
- **Registered scopes**: scopeId → connection count.
- **Summary**: TotalBroadcasts / DroppedBroadcasts / RegisteredScopeCount / TotalConnections.

A `Dropped=true` entry means a publish targeted a scopeId nobody is listening on — the smoking-gun pattern for the userId/scopeId mismatch class of bug. Cross-reference with `RegisteredScopes` to see whether the SSE side registered under a different scope than the agent loop is publishing to.

### User ID consistency

`SSEHandler` resolves `scopeId` with this precedence:

1. Query param `?userId=X` (set by client, matches `UserSession.getUserId()`)
2. `HttpContext.Items["ToolUp.UserId"]` (populated by `ScopeResolutionMiddleware`)
3. `X-User-Id` header (last-resort fallback)

`AIAssistantHandler` resolves the same way via `HttpContext.Items`. Both must agree so SSE events reach the originating client, not a different connection for the same user.

## `composeWithAI` mechanism — [`../ToolUp.AI.Server/Server/AICompose.fs`](../ToolUp.AI.Server/Server/AICompose.fs)

Wraps core `Server.compose` via the `ComposeExtensions` hook:

```fsharp
type ComposeExtensions = {
    Handlers: HttpHandler list
    ServiceConfig: (IServiceCollection -> IServiceCollection) option
}
```

`composeWithAI`:

1. Creates singletons: `AIToolRegistry` (populated with `aiTools`). As of Phase 6a, `SSEConnectionManager` is owned by core — `composeWithAI` no longer constructs it, and AI handlers resolve it per-request from `ctx.RequestServices`.
2. Builds the module context map: `List<ModuleAIContext>` → `Map<string, ModuleAIContext>`
3. Assembles AI handlers: `AIAssistantApi` (via `makeApi`) + the SSE endpoint, both using DI-resolved `SSEConnectionManager`
4. Builds `aiServiceConfig`: registers `IAIProvider`, `AIToolRegistry` in DI (core already registers `SSEConnectionManager` and `INotificationChannel`)
5. Constructs `ComposeExtensions` with the handlers + service config
6. Calls `compose handlers dataTypes config authProvider extensions`

Core `compose` knows nothing about AI. It sees `ComposeExtensions` as an opaque hook. The same mechanism will be used by future companions (Phase 9c distributed task frameworks, for instance).

### `AIServerApp` record — fluent composition surface

`AIServerApp` is the record-based counterpart to `composeWithAI`. It wraps a core `ServerApp` record in a `Base` field and carries the AI-specific fields alongside:

```fsharp
type AIServerApp = {
    Base: ServerApp
    AIProviderFactory: IAIProviderFactory option
    AIConfigStore: IUserAIConfigStore option
    AITools: RegisteredTool list
    AIConfig: AIAssistantServerConfig option
    ModuleAIContexts: ModuleAIContext list
}
```

`AIServerApp.run` flattens the inner `ServerApp` (modules → handlers / dataTypes / configSchemas) and calls `composeWithAI` with the collected state. It fails loudly if `AIProviderFactory` or `AIConfigStore` is missing — AI needs both, and the core `ServerApp` can't reasonably default them.

Apps that want RAG layer `RAGServerApp` on top of `AIServerApp`; see `src/ToolUp.RAG/README.md`. Apps without AI use `ServerApp.run` directly and never touch this companion.

## Client side — Elmish outer-program wrapper

`AIClientConfig.fs` wraps the Platform shell's Elmish Program to layer AI state and chrome on top without the shell knowing any AI types. The shell itself is AI-agnostic: it exposes `Client.init / update / view / program / run`, a public `Model` / `Msg`, and an `ExtraChrome = { HeaderAction; SidePanel }` slot record.

### What the wrapper does

```fsharp
AIClientConfig.withAIAssistant
    (mode: AIAssistantMode)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Program<unit, OuterModel, OuterMsg, ReactElement>
```

Under the hood, `withAIAssistant`:

1. **Prepends the AI assistant module page** to the module list via `appendAssistantModule` — `AIAssistantUI.create branding` shows up as a sidebar entry when mode ≠ `NoAIAssistant`.
2. **Builds an outer Elmish Program** parameterised over `OuterModel = { Shell: Client.Model; SidePanel: SidePanelModel }` and `OuterMsg = ShellMsg of Client.Msg | SidePanelMsg of SidePanelMsg`.
3. **Stitches the shell's init/update/view into the outer program** — ShellMsg dispatches to `Client.update`, SidePanelMsg dispatches to `sidePanelUpdate`, and outer `view` calls `Client.view` with an `ExtraChrome` record built from the side-panel state.
4. **Attaches the SSE subscription** as a `Cmd.ofEffect` in outer init, wiring `AIStreamEvent` deltas into `SidePanelMsg.SSEEvent`.
5. **Builds the header-action button** from `SidePanelModel.Open` — the chat toggle's highlight follows open/closed state. Injected via `ExtraChrome.HeaderAction`.

### What the app does

```fsharp
open Elmish
open Elmish.React
open ToolUp.Platform

AIClientConfig.withAIAssistant aiMode config modules
|> Program.withReactSynchronous "elmish-app"
|> Program.run
```

Apps without AI skip `AIClientConfig` entirely and call `Client.run config modules`. Stripping `ToolUp.AI.Client.props` from the app fsproj + the `withAIAssistant` call in `Client.fs` produces a clean build with zero AI surface — this is the Phase 1b acceptance criterion and is verified by a temp-edit-and-revert smoke test on every release of this extraction.

### Active-module plumbing

The outer view reads `Client.activeModuleName model.Shell` and passes it to `sidePanelView` and into the `Send` message. The user's currently-viewed module attaches to `AIMessageRequest.ActiveModule`, which the server's `SystemPromptBuilder.activeModuleContext` uses to inject module-registered domain prompts.

### Side-panel toolbar extension point

`Client/SidePanelExtensions.fs` exposes a tiny per-tab registry of `unit -> ReactElement` thunks that companion packages call from a module-load `do` block to add a button to the AI side-panel toolbar. `ConversationPanel` reads the list on each render and lays the thunks out alongside the built-in `✕ Cancel` button when the AI is streaming. The registered components decide their own visibility — a pause-style button, for example, can return `Html.none` once its companion's status flips to a paused phase so the toolbar doesn't carry an inert button while a floating banner is up.

```fsharp
// In a companion's module that's already in the import graph:
do ToolUp.AI.Client.SidePanelExtensions.registerStreamingAction (fun () -> MyButton())
```

Sanctioned mutable global, same precedent as `ClientToolRuntime.registry`. No deployment-side wiring — the registration runs at JS module load when whatever file holds the `do` block is imported (companions typically piggyback on a file that's already pulled by `ClientConfig.GlobalOverlays`).

## ToolUp.Remoting unit-body normalisation

Unrelated to AI but load-bearing: the in-tree ToolUp.Remoting dispatcher (shipped inside `ToolUp.Platform.Server`) folds body normalisation into its own request pipeline — it recognises requests with the `x-remoting-proxy` header and normalises empty bodies (`""`, `null`, GET with no body) to `[]` before invoking the handler. Without that, `unit -> Async<T>` API members would fail (`ListConversations`, `GetAvailableTools`, and the Platform equivalents would all break silently). Consumers no longer wire a separate `RemotingBodyNormalizationMiddleware` — `dotnet build` is the gate, not a middleware presence check.

## Latency telemetry — Phase 6i.A

`runAgentLoop` emits one `AILatencyRecord` per agent-loop turn through `IEventStore` (`SourceModule = "_platform.ai.latency"`, `EventType = "AITurnLatency"`). Always-on; emission silently no-ops in test harnesses that bypass `compose`.

### What's captured per turn

- `TurnDurationMs` — `Stopwatch` started right after `turn <- turn + 1` (after the cancellation check + max-turn guard); stopped at end-of-turn just before the emit. Covers `sendWithOuterRetry` + parallel tool dispatch + early-stop bookkeeping.
- `TtftMs` (`float option`) — captured inside `sendWithOuterRetry` via a closure that snapshots `sw.Elapsed` on the first non-empty `streamCb` invocation. `None` on tool-only turns where the model went straight to a tool call without narrating, or on turns served by a non-streaming provider. The percentile aggregator skips `None`s.
- `ToolCalls: ToolCallTiming list` — populated only by the tool-use branch; left `[]` for confusion-marker / end-turn branches. Per-tool `Stopwatch` starts right after `ToolCallStarted` emits and stops right after `ToolCallCompleted`. Each branch in the `Async.Parallel` returns `(toolResult, ToolCallTiming)`; the outer split keeps the existing `toolResults` shape and feeds the timings into a `mutable turnToolTimings` accumulator.
- `Errored` — set to `isErrorToolResult content` so "tools that fail are slow" stands out at the top of `/dev/ai-latency`'s per-tool breakdown.
- `Location` — `ServerSide | ClientSide`. Captured inside the existing `match tool.Definition.Location` so no extra `FindByName` is needed; defaults to `ServerSide` for the (rare) unknown-tool branch where the registry didn't resolve a tool.
- `StopReason` — copied verbatim from `response.StopReason`. Empty string when the loop bails before reading one.
- `PromptTokens` / `CachedPromptTokens` / `OutputTokens` / `CacheCreationTokens` — `int option`. Populated from `AIProviderResponse.Usage` (Phase 6i.B). `None` only when the provider can't extract usage (transient parse failure, streaming early-exit, or pre-6i.B records). `CacheCreationTokens` is Anthropic-specific (cache writes are billed at a premium over reads); OpenAI returns `None`. The `LatencyRecord` schema is forward-compatible so the `None → Some` move does not migrate historical events.

### What's not captured

- Cancellation and max-turn exits leave via `raise (OperationCanceledException ...)` / `failwith` and skip emission entirely. They have no useful timing to record (the turn never completed).
- Provider-error fatal exits inside `sendWithOuterRetry` propagate via `failwith` and similarly skip emission. Diagnose those via the structured error logs the helper already writes.
- Time spent in `SystemPromptBuilder.compose` / `ScopeResolutionMiddleware` / `AIAssistantHandler` setup before `runAgentLoop` is called. The record is per-turn-of-the-loop, not per-`SubmitMessage`. If the per-conversation top-line latency budget needs accounting outside the loop, that's a Phase 6i follow-up.

### Failure mode — telemetry must not crash a conversation

The `IEventStore.Write` call is wrapped in `try/with` and silently swallowed. A failing event store (full disk, blob auth expired, Redis unreachable) does not propagate into the agent loop. The trade-off is that lost telemetry is invisible at the conversation surface — operators discover it at `/dev/ai-latency` showing fewer turns than expected, or at the `IEventStore`'s own error log.

### Reading the data — `/dev/ai-latency`

Double-gated by `#if DEBUG` + `ServerConfig.EnableDevEndpoints` (mirror of `/dev/inspect` and `/dev/ai-fastpath`). Reads `IEventStore.ReadBySource(scope, _platform.ai.latency)` for the caller's resolved scope (never enumerates across teams), filters to a 60-min rolling window, and rolls up p50 / p95 / p99 across:

- `PerProviderModel` — `(ProviderName, ProviderModel) → Count + p{50,95,99}TurnMs + p{50,95}TtftMs option`. Surfaces the basic "is Sonnet 2× Haiku" question.
- `PerToolName` — `Name → Count + p{50,95,99}DurationMs + ErrorRate`. Surfaces the tool whose tail is dragging.
- `ServerVsClientTool` — `Location → Count + p{50,95,99}DurationMs`. Validates the 90s `ClientResidentToolTimeoutMs` is sensibly tuned for the long tail.

Rolling-window logic is recomputed on every request rather than maintained in an in-memory cache — process restarts don't lose data, multi-instance deployments work without coordination, and Phase 9f's `_by-source` secondary index makes the read O(matches) per scope.

### Cache hit rate (Phase 6i.B)

`PerProviderModel.CacheHitRate: float option` is the per-record average of `CachedPromptTokens / PromptTokens` across the bucket, restricted to records where both fields are populated (`Some`) and `PromptTokens > 0`. Per-record averaging (rather than sum-over-sum) keeps a single huge turn from dominating the ratio.

Returns `None` when no record qualifies — typical causes:
- Every record in the bucket is pre-6i.B (`PromptTokens = None`).
- The provider declares `Capabilities.SupportsPromptCaching = false`.
- All requests in the window were sub-threshold (see below).

Provider-specific notes:

- **Anthropic (Claude)** — three `cache_control: {type: "ephemeral"}` markers per request: last text block of `system`, last entry in `tools`, last content block of the second-to-last message (only when message-list length ≥ 2). Markers are metadata, not content — moving the marker between turns does not invalidate prior-turn cache writes; a longer prefix from an earlier turn can still hit on a later request that marks a different position. Sub-threshold prefixes (<1024 tokens for Sonnet/Haiku, <2048 for Opus) are silently processed without caching — Anthropic does not reject the request, so no client-side guard. Cache TTL is 5 minutes — well-suited to a typing-then-talking conversation. The `usage` block reports `cache_read_input_tokens` (cache hit) and `cache_creation_input_tokens` (cache write); both contribute to `PromptTokens`, only the read portion to `CachedPromptTokens`.
- **OpenAI** — prompt caching is automatic on prefixes ≥1024 tokens; no request-side opt-in. Cached portion is reported at `usage.prompt_tokens_details.cached_tokens`. `CacheCreationTokens` is always `None` — OpenAI doesn't surface a separate write count. The streaming path requires `stream_options.include_usage = true` (set in `OpenAIProvider.Wire.fs`'s `buildRequestBody`); the usage chunk arrives immediately before `[DONE]` with empty `choices: []` and a populated root `usage`.

To verify caching is actually hitting: send a multi-turn conversation through the AI assistant, then `curl http://localhost:5000/dev/ai-latency`. Expect `CacheHitRate` in the relevant `PerProviderModel` bucket to be substantially above zero on the second-and-later turns; turn 1 typically writes the cache (Anthropic reports `CacheCreationTokens > 0`, `CachedPromptTokens = 0`).

### Phase 9e forward-promise

The emission point in `AIAgentEngine.fs` carries a `// PHASE-9E-METRICS:` marker comment naming the keys to export when Phase 9e (Metrics / OpenTelemetry sink) lands: `ai.turn.duration.ms`, `ai.ttft.ms`, `ai.cached_prompt_ratio` (the third now fills in from the populated `CachedPromptTokens` / `PromptTokens` fields).

### Handshake latency (Phase 6i.F)

Separate from per-turn latency, the SSE *handshake* (browser sends GET, server flushes response headers, EventSource fires `open`) has its own budget: **p95 < 200ms** end-to-end including any reverse proxy. Anything more is felt as a stalled UI on first paint and a delay before notifications start flowing.

The 2026-05-03 incident (>2s observed against `/api/notifications` in dev) traced to three independent buffering layers, all now closed in `SSE.writeReadyResponse` (the shared helper in `src/ToolUp.Platform/Server/SSEConnectionManager.fs` that both `NotificationHandler` and `SSEHandler` call before any await on the per-connection subscriber):

1. **`X-Accel-Buffering: no` header** on every SSE response. Tells nginx and Cloudflare to stop buffering the streaming response. Production deployments behind a reverse proxy pay handshake delay every connect without it.
2. **Initial `: ready\n\n` comment frame** written before the first `FlushAsync`. Some proxies wait for a newline in the body before unbuffering; the comment is invisible to clients (SSE spec ignores `:` lines).
3. **Vite dev proxy `/api/notifications` rule** in `vite.config.mts` mirrors the existing `/api/ai/events` rule — both inject `cache-control: no-cache` and `x-accel-buffering: no` on the proxied response. http-proxy (Vite's underlying proxy) buffers by default; without the rule, dev paid 2s+ handshake while production ran fine.

Adjacent fix in the same commit: `RateLimiting.isBypassed` extended to skip `/api/ai/events` alongside `/api/notifications` — both are long-lived SSE connections that would otherwise saturate a per-scope bucket on connect.

**Regression coverage:** `src/ToolUp.Platform.Tests/InProcess/SSEHandshakeTests.fs` asserts headers + ready frame + sub-50ms wall-clock against a unit-test `DefaultHttpContext` (post-warmup so JIT / assembly-load cost doesn't flap the budget). The proxy layer cannot be exercised in-process; `ACCEPTANCE_TESTS.md` Test 7 covers end-to-end through the Vite proxy with browser DevTools.

## Conversation export & PII — Phase 6h.A

The chat side panel's `Export ▾` menu downloads a conversation as
Markdown or JSON. Tool payloads (`ToolCalls.Arguments` /
`ToolCalls.Result`) regularly contain user ids, internal database keys,
and RAG / KnowledgeBase chunks that may include **PII**. Two safeguards:

- **Sanitised by default.** The export menu has an
  *"Include tool details (⚠ may contain PII)"* checkbox, defaulting
  **off**. With it unticked, `exportAsMarkdown` / `exportAsJson`
  ([`../ToolUp.AI.Client/Client/ConversationPanel.fs`](../ToolUp.AI.Client/Client/ConversationPanel.fs)) write
  only `Participant`, `Content`, and `Timestamp` — the `ToolCalls`
  field is omitted entirely from the download. Ticking the box restores
  the full export.
- **Audit trail.** Every export click fires a fire-and-forget
  `POST /api/ai/conversation/export-audit`
  ([`../ToolUp.AI.Server/Server/ConversationExportAuditHandler.fs`](../ToolUp.AI.Server/Server/ConversationExportAuditHandler.fs)),
  which emits a `ConversationExported` `AuditEvent` via `IAuditLog`.
  The audit payload is **metadata only** —
  `{ ConversationId; IncludeToolDetails; ExportedBy }` — it never
  carries conversation content or tool payloads, so enabling export
  auditing does not turn the audit log itself into a PII sink.
  `ExportedBy` is taken from the server-side request identity, never
  trusted from the client. Query it with
  `IAuditLog.GetAuditTrail(scopeId, dateRange, Some "ConversationExported")`
  to see who exported which conversation and whether tool details were
  included — useful for spotting cross-team export patterns.

## Known limitations

- **Elmish.HMR loses SSE subscriptions on hot-reload.** The `EventSource` created in `init` is not re-run after a Fable-only recompile; `Cmd.none` is returned instead of the original commands. Full browser refresh (F5) re-initialises. Not a bug — a documented Elmish.HMR quirk.
- **Vite proxy SSE buffering (historical).** http-proxy (Vite's underlying proxy) buffers responses by default. SSE endpoints need `x-accel-buffering: no` injected on the proxied response. `vite.config.mts` has a dedicated rule for each SSE endpoint (`/api/notifications` and `/api/ai/events`) — they MUST NOT be merged into the generic `/api/` catch-all, because applying `cache-control: no-cache` to every API response would defeat browser caching of `GET` API calls.
- **Streaming retries are intentionally impossible** once any content has been delivered. `ClaudeAIProvider` reports mid-stream failures as `StreamingAborted(partialText, …)`; the agent loop propagates these as `AITaskFailed`. Partial text is preserved for diagnostics only — the UI does not attempt to "complete" a partially-streamed response. Non-streaming retries with exponential backoff are live (see [Provider error classification](#provider-error-classification)).
- **`AccessContext` tool scoping.** Tools currently receive `HttpContext` directly and can read any DI service, including the resolved `AccessContext` via `ctx.GetService<AccessContext>()`. Platform-level RBAC now enforces per-module permissions at the ToolUp.Remoting boundary (`makePermissionGuardedApi`), so module APIs the agent invokes are already gated. Tool authors should still read the resolved `AccessContext` for any additional per-tool checks rather than assuming the agent loop has authorised the action — tools can be called across module boundaries.

## Where files go

| Concern | Location |
|---|---|
| `IAIProvider` interface | `src/ToolUp.Platform.Core/Shared/Interfaces/IAIProvider.fs` (core) |
| Tool declarations (`AIToolDefinition`, `ToolParameterSchema`) | `src/ToolUp.Platform.Core/Shared/Types/ModuleAITypes.fs` (core) |
| Shared runtime types (conversation, streaming, protocol) | `src/ToolUp.AI.Core/Shared/AITypes.fs` (companion) |
| Server runtime | `src/ToolUp.AI.Server/Server/*.fs` (companion) |
| Client UI | `src/ToolUp.AI.Client/Client/*.fs` (companion; Fable source ships in nupkg under `fable/`) |
| Concrete providers | `src/AIProviders/<Name>/` (sub-companions) |

Modules depend on core only. Applications depend on core + `ToolUp.AI.*` when they want AI. Providers depend on core (`IAIProvider`, `ISecretStore`). The companion is removable.
