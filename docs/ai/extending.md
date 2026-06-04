# Extending ToolUp.AI

How to write a new `IAIProvider`, register custom tools, author a `SystemPromptBuilder`, and declare capability flags.

## Writing a new `IAIProvider`

A new provider goes in its own NuGet package. The convention is `ToolUp.AIProviders.<VendorName>` for the package id; the F# namespace matches.

### Minimum implementation

Implement `IAIProvider`:

```fsharp
module MyVendor.AIProvider

open ToolUp.Platform

type MyVendorProvider(apiKey: string, model: string, httpClient: HttpClient) =
    let capabilities = {
        ProviderName = "myvendor"
        Model = model
        SupportsStreaming = true
        SupportsToolUse = true
        SupportsVision = false
        SupportsPromptCaching = false
    }

    interface IAIProvider with
        member _.Capabilities = capabilities
        member _.SendMessage(req) = async {
            // Translate AIProviderRequest -> vendor wire format
            let wireRequest = translateRequest req
            // POST to the vendor's endpoint
            use! response =
                httpClient.PostAsJsonAsync(
                    "https://api.myvendor.com/v1/messages",
                    wireRequest)
                |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            // Translate vendor response -> AIProviderResponse
            let! wireResponse = response.Content.ReadFromJsonAsync<WireResponse>() |> Async.AwaitTask
            return translateResponse wireResponse
        }
```

The agent loop is provider-agnostic — every provider gets the same `AIProviderRequest`, returns the same `AIProviderResponse`. The translation layer per-provider is the bulk of the work.

### Expose a builder + descriptor

```fsharp
module MyVendor.AIProvider

let descriptor: AIProviderDescriptor = {
    Id = "myvendor"                   // unique; used by IUserAIConfigStore
    DisplayName = "MyVendor AI"
    DefaultModel = "myvendor-pro-1"
    Capabilities = {
        ProviderName = "myvendor"
        Model = ""                    // overridden by builder
        SupportsStreaming = true
        SupportsToolUse = true
        SupportsVision = false
        SupportsPromptCaching = false
    }
}

let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider =
    let httpClient = new HttpClient()
    MyVendorProvider(apiKey, model, httpClient) :> IAIProvider

let builder: AIProviderBuilder = {
    Descriptor = descriptor
    Build = fun apiKey model -> createWithApiKeyAndModel apiKey model
}
```

### Wire into the consuming app

```fsharp
open MyVendor.AIProvider

let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder
          OpenAIProvider.builder
          MyVendor.AIProvider.builder ]   // append your builder
        aiConfigStore
        secretStore
        AllowUserProviders
```

No other wiring changes. Users can now register a `MyVendor` provider instance via the AI Settings UI, selecting `myvendor` from the provider dropdown.

### Streaming

For providers that stream SSE responses, the implementation reads the response stream and emits incremental tokens via a streaming callback. The default agent loop handles streaming if `Capabilities.SupportsStreaming = true` and the request's `Stream` flag is true.

Pattern (skeleton — vendor-specific stream parsing varies):

```fsharp
member _.SendMessageStreaming(req, emit) = async {
    // Open the SSE response
    use! response =
        httpClient.GetStreamAsync("https://api.myvendor.com/v1/messages/stream")
        |> Async.AwaitTask

    use reader = new StreamReader(response)

    let mutable accumulated = []
    let mutable usage = None

    while not reader.EndOfStream do
        let! line = reader.ReadLineAsync() |> Async.AwaitTask
        if line.StartsWith("data: ") then
            let payload = line.Substring(6)
            match parseStreamChunk payload with
            | TextDelta delta ->
                emit (StreamDelta delta)
                accumulated <- delta :: accumulated
            | ToolUseStart (id, name) ->
                emit (ToolCallBegin (id, name))
            | UsageUpdate u ->
                usage <- Some u
            | Done reason ->
                let final = String.concat "" (List.rev accumulated)
                return {
                    Messages = [ { Role = Assistant; Content = final } ]
                    StopReason = reason
                    ToolCalls = collectToolCalls accumulated
                    Usage = usage
                }
            | Heartbeat -> ()
    return {
        Messages = []
        StopReason = EndTurn
        ToolCalls = []
        Usage = usage
    }
}
```

### Token usage reporting

Populate `AIProviderResponse.Usage` with the provider's reported token counts:

```fsharp
{
    Messages = [...]
    StopReason = EndTurn
    ToolCalls = []
    Usage = Some {
        PromptTokens = response.Usage.InputTokens
        CachedPromptTokens = response.Usage.CachedInputTokens |> Option.defaultValue 0
        OutputTokens = response.Usage.OutputTokens
        CacheCreationTokens = response.Usage.CacheCreationTokens
    }
}
```

This feeds `AILatencyRecord` per-turn metrics. Providers that don't report usage leave `Usage = None`; the latency record still records latency (TTFT, total duration) just not token counts.

### Prompt caching

For Anthropic-style explicit caching, mark cache points in the request translation. The SDK delegates this decision to the provider — there's no SDK-side cache marker propagation.

The Claude provider marks three locations:
1. Last text block of `system` — caches the static system prompt.
2. Last entry in `tools` — caches the tool schema.
3. Last content block of the second-to-last message (when `Messages.Length >= 2`) — caches the conversation prefix.

For providers with automatic caching (OpenAI), no markers are needed — set `Capabilities.SupportsPromptCaching = true` and consume the cached-token field in the usage response.

### Provider rules

- **Receive `ISecretStore` through the builder.** Never read env vars / config files directly. Builders accept the resolved API key as a parameter; the factory pulls the key from `ISecretStore` per-call.
- **Never log the API key.** Even at trace level. Log a hashed prefix if you must.
- **Capabilities declared truthfully.** `SupportsToolUse = false` for providers that don't, even if the vendor's docs claim partial support — `false` is the safer floor that won't break the agent loop on unsupported features.
- **Author an `IHealthCheck` probe.** Verifies the API key is valid + the endpoint is reachable. Self-register via DI; auto-wired into `/ready`.
- **Author an `IConfigValidator` probe.** Verifies the configuration is correct at preflight. Refuse to start with helpful error messages when keys / endpoints are misconfigured.
- **Wire the builder into a `Server.props` extension contract.** Companion files extend `_ToolUpPlatformServerSources`; the consuming server project picks them up via the props chain.

### Provider authoring checklist

- [ ] `IAIProvider` impl with translation layer.
- [ ] `SendStructuredMessage` — either a native implementation against the vendor's JSON-Schema mode, or a one-line delegation to `IAIProviderDefaults.sendStructuredViaFallback` (see [Structured-output support](#structured-output-support) below).
- [ ] `AIProviderDescriptor` with unique `Id` matching the package vendor name.
- [ ] `AIProviderBuilder` pairing descriptor + Build function.
- [ ] Streaming support (if vendor supports it) — emits `StreamDelta` / `ToolCallBegin` / `Done` callbacks.
- [ ] Token usage reporting — `Usage` populated from vendor response.
- [ ] Prompt caching markers (if vendor supports it) — explicit cache_control in request, or implicit (no markers needed).
- [ ] `IHealthCheck` probe + DI registration.
- [ ] `IConfigValidator` probe — preflight rejects misconfigured deployments.
- [ ] README + version metadata in the fsproj.
- [ ] `Server.props` extension contract.
- [ ] At least one integration test (against a mock endpoint or the real API with a test key).

For a complete reference, see [`ToolUp.AIProviders.Claude`](https://github.com/ToolUp-Forge/toolup-forge/tree/main/src/AIProviders/Claude) (~300 lines of code, handles the full Anthropic API surface).

### Structured-output support

`IAIProvider` carries a sibling `SendStructuredMessage` method for JSON-Schema-respecting structured output. The schema rides as a string (same convention as `AIProviderToolDef.InputSchema`); providers parse internally and translate to their native wire format.

#### Provider-side: choose native or fallback

If the vendor supports server-side structured-output natively, implement against it:

| Vendor    | Native shape                                                                                                      |
|-----------|-------------------------------------------------------------------------------------------------------------------|
| Gemini    | `generationConfig.responseSchema` + `responseMimeType: "application/json"`.                                       |
| OpenAI    | `response_format: { type: "json_schema", json_schema: { name, schema, strict: true } }` (gpt-4o-2024-08-06+).      |
| Anthropic | No native mode. Tool-based workaround: synthesise a tool whose `input_schema` is the schema; force `tool_choice`. |

For vendors without a native mode (or for an MVP provider you'll harden later), delegate one line to the helper:

```fsharp
interface IAIProvider with
    member _.Capabilities = ...
    member _.SendMessage(...) = ...
    member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
        IAIProviderDefaults.sendStructuredViaFallback
            (this :> IAIProvider)
            messages tools systemPrompt schema retryPolicy
```

The fallback prepends the schema as a system-prompt instruction, calls `SendMessage`, and post-validates the response is parseable JSON. Non-JSON responses surface as `AIProviderError.SchemaUnsupported`.

#### Consumer-side: dispatch a structured request

Once an `IAIProvider` is resolved (via `DefaultAIProviderFactory.Resolve` or any factory path), call `SendStructuredMessage` directly:

```fsharp
let schema = """{
    "type": "object",
    "properties": {
        "verdict": { "type": "string", "enum": ["yes", "no", "uncertain"] },
        "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
        "reasoning": { "type": "string" }
    },
    "required": ["verdict", "confidence"]
}"""

let messages = [
    AIProviderMessage.text "user" "Is this image a cat? Respond per the schema."
]

let! result =
    provider.SendStructuredMessage(
        messages,
        [],                          // tools — see limitation below
        Some "You are a strict classifier.",
        schema,
        RetryPolicy.defaults
    )

match result with
| Ok response ->
    // response.Content is JSON conforming to the schema.
    let parsed = JsonDocument.Parse(response.Content)
    ...
| Error (SchemaUnsupported(feature, detail)) ->
    // Provider could not honour the schema (or the fallback couldn't
    // extract JSON from the response).
    ...
| Error err -> ...
```

#### Limitations (v1)

- **Non-streaming only.** Streaming structured-output is deferred to a follow-on phase.
- **Tool use is provider-dependent.** Gemini and OpenAI honour `tools` alongside the schema; Claude's workaround forces `tool_choice` on the synthesised schema-tool, so user-supplied tools become unreachable in the same turn. The canonical pattern: run any free-form tool-dispatch turns with `SendMessage` first, then a final `SendStructuredMessage` for the structured response.
- **Advanced schema features** (`oneOf`, `anyOf`, `$ref`, …) that one provider can't honour return `AIProviderError.SchemaUnsupported(feature, detail)` rather than degrading silently. Stick to the lowest common denominator for portability.

## Registering custom tools

### Server-side tools

```fsharp
let myAnalysisTool : AIToolDefinition = {
    Name = "my_module.analyse"
    Description = "Run analysis over selected items in the active dataset."
    Parameters = {
        Properties = [
            "item_ids", StringArray, "List of item IDs"
            "metric", EnumP ["revenue"; "units"; "margin"], "Metric to compute"
            "weeks", Integer, "Weeks of history"
        ]
    }
    Executor = fun args -> async {
        let itemIds = args |> JsonValue.getStringArray "item_ids"
        let metric = args |> JsonValue.getString "metric"
        let weeks = args |> JsonValue.getInt "weeks"

        let! result = MyModule.Server.runAnalysis itemIds metric weeks
        return ToolResult.ok (Json.serialize result)
    }
    Visibility = ServerSide
    Capabilities = ToolCapabilities.defaults
}
```

Register via `ServerModule.withAITools`:

```fsharp
let myModule =
    ServerModule.create "MyModule"
    |> ServerModule.withGuardedApi myApi
    |> ServerModule.withAITools [ myAnalysisTool ]
```

The agent loop sees the tool in `GetAvailableTools`; the LLM can call it. When called, the executor runs server-side in-process with the caller's `AccessContext` available via the ambient context.

### Client-resident tools

The substrate (`ClientToolRuntime` + `ClientToolDispatch` + `AICancellationRegistry`) is generic — any companion can register `ClientResident` tools. A typical use is to let the LLM drive the UI (set form fields, click buttons, select rows, navigate). Server-side, a `ClientResident` tool dispatches to the client over SSE; the browser runs the tool and returns the result.

```fsharp
let setFieldTool : AIToolDefinition = {
    Name = "_platform.ui.set_field"
    Description = "Set the value of a field in the current page."
    Parameters = ...
    Executor = fun args -> async {
        // Dispatch to the client via ClientToolDispatch
        let clientResponse = ClientToolDispatch.dispatch ...
        return clientResponse |> ToolResult.fromClient
    }
    Visibility = ClientResident
    Capabilities = { ToolCapabilities.defaults with RequiresFullPage = true }
}
```

`RequiresFullPage = true` means the tool only works when the user is using the full-page AI assistant (Mode 2 — "watch me work"). The side panel (Mode 1 — "just do it") doesn't support client-resident tools because the side panel doesn't have the active-page context.

The client-side runtime (`ClientToolRuntime` in `ToolUp.AI.Client`) handles the dispatch lifecycle — opens a session per tool call, waits for the result, returns it to the server. Cancellation cascades both ways.

### Tool authoring rules

- **Tool name format**: `<scope>.<verb>` — e.g. `my_module.analyse`, `_platform.list_documents`, `_platform.ui.set_field`. The `_platform.` prefix is reserved for platform / companion-contributed tools.
- **Parameter schema is JSON-Schema-shaped.** The model sees `parameters: { type: "object", properties: { ... } }`. Required vs optional is currently implicit (all properties required); future schema versions may add explicit `required` lists.
- **Executor must handle missing / malformed args gracefully.** Return `ToolResult.error` with a useful message — the agent will retry or surface the error to the user.
- **Executor must NOT throw.** Catch exceptions and return `ToolResult.error`; an unhandled exception aborts the agent turn with `Failed` status.
- **Idempotency**: if a tool writes data, design it idempotent. The agent may retry on transient errors. Idempotency keys flow through the tool args.
- **Permissions**: tools enforce their own permission checks against `AccessContext`. The SDK's `makePermissionGuardedApi` covers HTTP API permissions but does NOT auto-wrap tool executors.

### `ClientResident` tool authorization — `IClientToolAuthorizer` seam

`ClientResident` tools dispatch from the server agent loop to the user's browser; their args may be influenced by prompt injection. forge exposes `IClientToolAuthorizer` in `ToolUp.AI.Core` as the single seam the agent loop consults **before** emitting any `ClientToolInvoke` SSE — register an implementation to gate which `(module, field|button|row|page)` tuples the model may drive. Denied calls never reach the browser; the model is told the action was refused (typed `Denied` tool-result), and a `_platform.ai.tool_allowlist_denial` event is written to `IEventStore` for operator observability.

**forge ships no implementation of this seam out of the box.** Without a registered authorizer, the agent loop consult resolves to "allow" — full dispatch behaviour with zero gating. The reserved `_sdk.*` Id namespace (Platform Admin, Health Monitor, Team Manager) stays permanently hard-denied independent of any authorizer (that's enforced inside `ToolUp.AI` itself).

Consumers wanting allowlist enforcement implement `IClientToolAuthorizer` against their own policy shape — typically a default-deny allowlist keyed by module / field / button / page with bounded refusal-event audit. See [SECURITY.md](../../SECURITY.md) for the threat model.

#### Client-resident tool authorization contract

Any companion implementing `IClientToolAuthorizer` must clear the SDK's portability bar — the seam is intentionally narrow (sync, value-in / value-out, never-throws), and forge ships two reusable conformance packs so a new implementation can validate against the same invariants the platform default does:

1. **`IClientToolAuthorizerContract`** (`src/ToolUp.Platform.Tests/Contracts/IClientToolAuthorizerContract.fs`) — per-decision invariants on *any* authorizer:
   - allowed-call returns `Allow`,
   - denied-call returns `Deny` with a non-empty reason,
   - identical inputs return identical decisions (rule 4 — stateless between invocations),
   - never throws on malformed / empty `argsJson` (the seam doc explicitly mandates "malformed argsJson is a `Deny`, not an exception"),
   - never throws on `None` active module / page,
   - structurally-equal-but-distinct input string instances resolve to the same decision (rule 1 — identity by value),
   - parallel authorisations are independent (rule 5 — no cross-call ordering).

   Bind it from your own test pack by handing the pack a fixture: the authorizer plus two anchor calls — one the impl MUST allow and one the impl MUST deny:

   ```fsharp
   open ToolUp.Platform.Tests.Contracts

   let tests =
       IClientToolAuthorizerContract.tests {
           Name = "MyCompanyAuthorizer"
           Authorizer = MyCompanyAuthorizer(myPolicy) :> IClientToolAuthorizer
           AllowedCall = ("my.tool", "{}", Some "MyModule", Some "/page")
           DeniedCall = ("blocked.tool", "{}", Some "MyModule", Some "/page")
       }
   ```

2. **`IClientToolDispatchContract`** (`src/ToolUp.Platform.Tests/Contracts/IClientToolDispatchContract.fs`) — full dispatch round-trip behavioural pack. Drives `AIAgentEngine.runAgentLoop` end-to-end with a scripted `IAIProvider` + the companion's authorizer + a caller-supplied client simulator. Asserts:
   - Allow round-trip — exactly one `ClientToolInvoke` SSE per call, and the simulator's result reaches the loop cleanly (no Denied / timeout shape on the result envelope);
   - Deny short-circuit — no `ClientToolInvoke` emitted, a `Denied`-shaped tool-result returned to the model, and a `_platform.ai.tool_allowlist_denial` event written to `IEventStore`;
   - Concurrent tool calls in one turn receive distinct `ToolCallId` Guids (rules 1 + 5 — identity-by-value + no cross-shard ordering);
   - Completing one pending TCS in the dispatch registry does not affect another (rule 4 — stateless dispatcher between TCS keys).

   Bind it with the same fixture-style ergonomics — the pack owns the registry, dispatch registry, `IEventStore`, `HttpContext`, and scripted provider:

   ```fsharp
   let dispatchTests =
       IClientToolDispatchContract.tests {
           Name = "MyCompanyAuthorizer + handler"
           Authorizer = MyCompanyAuthorizer(myPolicy) :> IClientToolAuthorizer
           AllowedToolName = "my.tool"
           DeniedToolName = "blocked.tool"
           Simulator = fun _evt -> Some """{"ok": true}"""
       }
   ```

Forge ships three in-tree subjects bound to the packs:

- **`SyntheticClientToolAuthorizer`** (`src/ToolUp.Platform.Tests/InProcess/SyntheticClientToolAuthorizerTests.fs`) — trivial allow / deny stub, bound to pack (1).
- **`DenyOnlyAuthorizer`** (`src/ToolUp.Platform.Tests/InProcess/ClientToolDispatchContractBindings.fs`) — bound to pack (2).
- **`ToolUp.AI.SampleClientTool`** (`src/AI.Samples/ToolUp.AI.SampleClientTool.{Core,Server,Client}/`) — the reference companion that pairs server-side compose + a real Fable browser handler against a calculator tool. Bound to pack (2) via `src/ToolUp.Platform.Tests/InProcess/SampleClientToolDispatchTests.fs`, exercising the same `CalcOps.compute` the real handler ships. **Read [`src/AI.Samples/ToolUp.AI.SampleClientTool.Client/README.md`](../../src/AI.Samples/ToolUp.AI.SampleClientTool.Client/README.md) for the ≤10-min worked example of authoring your own client-resident-tool companion.**

The first two are conformance subjects (synthetic, never compose into production); the sample is reference-only and stays in-tree so the dispatch substrate has a permanent compose-clean smoke test plus an end-to-end shape new companions can mirror. All three together fulfil the GP 12 "attempt a second implementation" discipline — proves the seams stay companion-agnostic.

For the full companion-authoring walkthrough — wiring the authorizer + handler against the contract packs, integrating with `IServiceCollection`, and the trust-boundary semantics that make the Deny path load-bearing for prompt-injection mitigation — see [`src/ToolUp.AI/TECHNICAL_GUIDE.md` §"Client-resident companion authoring"](../../src/ToolUp.AI/TECHNICAL_GUIDE.md#client-resident-companion-authoring).

## Authoring a custom `SystemPromptBuilder`

For complex prompts that pull from runtime state:

```fsharp
let dataSummaryPromptBuilder : SystemPromptBuilder = fun ctx -> async {
    match ctx.ActiveModule with
    | Some "SalesAnalysis" ->
        let! catalog = dataCatalog.ListObjects(ctx.Access.TeamId |> Option.defaultValue "", "SalesData")
        let summary =
            catalog
            |> List.map (fun obj -> $"  - {obj.ObjectId}: {obj.RowCount} rows, last modified {obj.Updated:yyyy-MM-dd}")
            |> String.concat "\n"
        return $"""The user is viewing Sales Analysis. Available datasets:
{summary}

Always cite the dataset name when answering questions about specific data."""
    | _ -> return ""
}
```

Compose it into the default builder:

```fsharp
let composedBuilder =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic "You are an analytics assistant. ..."
        SystemPromptBuilder.activeModuleContext
        dataSummaryPromptBuilder
    ]

AIServerApp.create (aiProviderFactory, aiConfigStore)
|> ...
|> AIServerApp.withAIConfig {
    AIAssistantServerConfig.defaults with
        SystemPrompt = Some composedBuilder
}
|> ...
```

### Composition rules

- **Builders run in parallel** — order in the list affects join order, not execution order.
- **A builder returning `""` is silently dropped** — no double blank lines.
- **A builder that throws aborts the whole compose** — wrap risky logic in try/with.
- **Network calls in builders block the turn** — every chat message waits for every builder to complete. Keep builders fast; cache aggressively. The default builders are sub-millisecond.
- **AccessContext.TeamId is scope-validated upstream** — the builder can trust the team scope. Team A's builder never sees Team B's context.

## Declaring capability flags

`AIProviderCapabilities` flags propagate from the provider to consumers (the agent loop, the AI Settings UI, downstream features that need vision input, etc.). Declare truthfully:

- `SupportsStreaming` — true if the provider's `SendMessage` honours `req.Stream = true` and emits incremental tokens.
- `SupportsToolUse` — true if the provider correctly translates the `Tools` array into the vendor's tool schema and parses tool calls in the response.
- `SupportsVision` — reserved for future multimodal content support. Today the `AIProviderMessage.Content` is `string`; image / audio blocks are not yet shipped. Set this `true` only when the SDK supports multimodal protocol (future SDK version).
- `SupportsPromptCaching` — true if the provider implements cache markers (explicit or implicit). Drives `CacheHitRate` reporting in `/dev/ai-latency`.

The agent loop respects these:
- `SupportsStreaming = false` → loop ignores `req.Stream`, treats response as non-streaming.
- `SupportsToolUse = false` → loop doesn't include `Tools` in the request; tool calls in the response are warned as invariant violations.
- `SupportsVision = false` → multimodal feature flags upstream of the agent gate to disabled for this provider.

## Companion conventions

If you're writing a provider companion to live alongside `ToolUp.AIProviders.Claude` / `OpenAI`, the package layout:

```
src/AIProviders/<VendorName>/
├── <VendorName>AIProvider.Wire.fs       # vendor wire-format types + helpers
├── <VendorName>AIProvider.fs            # IAIProvider impl + builder + descriptor
├── <VendorName>AIProviderHealth.fs      # IHealthCheck impl
├── <VendorName>AIProviderValidator.fs   # IConfigValidator impl (optional)
├── <VendorName>AIProvider.fsproj
├── <VendorName>AIProvider.Server.props  # extension contract
└── README.md
```

The `.Server.props` file extends `_ToolUpPlatformServerSources`:

```xml
<Project>
  <ItemGroup>
    <_ToolUpPlatformServerSources Include="$(MSBuildThisFileDirectory)\<VendorName>AIProvider.Wire.fs" />
    <_ToolUpPlatformServerSources Include="$(MSBuildThisFileDirectory)\<VendorName>AIProvider.fs" />
    <_ToolUpPlatformServerSources Include="$(MSBuildThisFileDirectory)\<VendorName>AIProviderHealth.fs" />
  </ItemGroup>
</Project>
```

The consuming server project imports your `.Server.props` after `ToolUp.Platform.Server.props`. The source files end up in the consuming project's compile chain.

For pure-DLL companions (no source injection), package as a regular .NET library — `<PackageReference>` in the consuming project, no `.props` file needed. The provider's types are visible after restore.

## Testing a provider

The SDK ships `ToolUp.Platform.Tests` with reusable test helpers. For provider integration tests:

```fsharp
open Expecto
open ToolUp.AI
open MyVendor.AIProvider

let tests =
    testList "MyVendor provider" [
        testCaseAsync "round-trips a simple message" <| async {
            let provider = MyVendor.AIProvider.createWithApiKeyAndModel testApiKey "test-model"
            let! response =
                provider.SendMessage {
                    SystemPrompt = "You are helpful."
                    Messages = [ { Role = User; Content = "What's 2 + 2?" } ]
                    Tools = []
                    MaxTokens = 100
                    Temperature = 0.0
                    Stream = false
                }
            Expect.isNotEmpty response.Messages "expected at least one assistant message"
            Expect.equal response.StopReason EndTurn "expected EndTurn stop reason"
        }
    ]
```

For unit tests of the wire-format translation layer, no provider key is needed — test the `translateRequest` / `translateResponse` functions directly with synthetic inputs.

For SDK-level integration tests (agent loop + provider), the SDK ships an `InMemoryProvider` test double consumers can use:

```fsharp
let provider =
    InMemoryProvider.create {
        OnSendMessage = fun req -> async {
            // Custom response logic for the test
            return { Messages = [ ... ]; StopReason = EndTurn; ToolCalls = []; Usage = None }
        }
    }
```

This lets you test agent-loop behaviour, tool dispatch, system-prompt composition, etc. without burning real LLM tokens in CI.
