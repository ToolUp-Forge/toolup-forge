# Extending ToolUp.AI

How to write a new `IAIProvider`, register custom tools, author a `SystemPromptBuilder`, and declare capability flags.

## Writing a new `IAIProvider`

A new provider goes in its own NuGet package. The convention is `ToolUp.AIProviders.<VendorName>` for the package id; the F# namespace matches.

### Minimum implementation

Implement `IAIProvider`. All three members are abstract, so all three must be implemented. `SendMessage`
carries the whole request as ordinary arguments — there is no request record —
and returns `Result<AIProviderResponse, AIProviderError>` rather than throwing.
`SendStructuredMessage` can be a one-line delegation (see
[Structured-output support](#structured-output-support) below).

```fsharp
// MyVendorAIProvider.fs, in `module MyVendor.AIProvider`.
open ToolUp.Platform
open ToolUp.Platform.AI

type MyVendorProvider(apiKey: string, model: string, httpClient: HttpClient) =
    let capabilities: AIProviderCapabilities = {
        ProviderName = "myvendor"
        Model = model
        Streaming = true
        ToolUse = true
        Vision = false
        SupportsPromptCaching = false
        SupportsTriage = false            // see "Triage capability" below
        TriageModelId = None
    }

    interface IAIProvider with
        member _.Capabilities = capabilities

        member _.SendMessage(messages, tools, systemPrompt, onStream, retryPolicy) = async {
            // Translate the SDK's request values -> vendor wire format
            let wireRequest = translateRequest messages tools systemPrompt

            let body =
                new StringContent(
                    JsonSerializer.Serialize(wireRequest),
                    System.Text.Encoding.UTF8,
                    "application/json")

            // POST to the vendor's endpoint
            use! response =
                httpClient.PostAsync("https://api.myvendor.com/v1/messages", body)
                |> Async.AwaitTask

            if not response.IsSuccessStatusCode then
                // Errors are values, not exceptions — the agent loop
                // reasons about the case, and the provider's own retry
                // loop absorbs the transient ones.
                return Error(TransientServer(int response.StatusCode, response.ReasonPhrase))
            else
                // Translate vendor response -> AIProviderResponse
                let! raw = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                return Ok(translateResponse (JsonSerializer.Deserialize<WireResponse>(raw)))
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy
```

The agent loop is provider-agnostic — every provider gets the same message /
tool / prompt values, returns the same `AIProviderResponse`. The translation
layer per-provider is the bulk of the work.

### Expose a builder + descriptor

```fsharp
// Still MyVendorAIProvider.fs, in `module MyVendor.AIProvider`.

let descriptor: AIProviderDescriptor = {
    Id = "myvendor"                   // unique; used by IProviderProfile
    DisplayName = "MyVendor AI"
    SupportedModels = [ "myvendor-pro-1"; "myvendor-lite-1" ]
    DefaultModel = "myvendor-pro-1"
    Capabilities = {
        ProviderName = "myvendor"
        Model = ""                    // overridden by builder
        Streaming = true
        ToolUse = true
        Vision = false
        SupportsPromptCaching = false
        SupportsTriage = false        // see "Triage capability" below
        TriageModelId = None
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
        [ claudeBuilder; openAiBuilder; MyVendor.AIProvider.builder ]   // append yours
        providerProfile      // IProviderProfile
        secretStore
        PermissiveWithPlatformFallback   // AIFallbackPolicy
        platformProviders
        None
```

No other wiring changes. Users can now register a `MyVendor` provider instance via the AI Settings UI, selecting `myvendor` from the provider dropdown.

### Streaming

There is no separate streaming method. `SendMessage` takes an
`onStream: (string -> unit) option` argument: `Some cb` means the caller wants
incremental output, and the provider calls `cb` with each **text** delta as it
arrives. Tool calls are not streamed through the callback — they come back on
`AIProviderResponse.ToolCalls` when the turn completes. A provider that
declares `Capabilities.Streaming = false` ignores the argument.

Two contract obligations, both load-bearing:

- **Never retry after any partial content has reached the callback** — a retry
  would duplicate output the user has already seen.
- **Classify a mid-stream failure as `StreamingAborted(partialText, detail)`**,
  carrying what was already emitted, so the caller can surface a diagnostic
  rather than a silent truncation.

Pattern (skeleton — vendor-specific stream parsing varies):

```fsharp
// The streaming leg of `SendMessage`, taken when `onStream` is `Some cb`.
let sendStreaming (cb: string -> unit) : Async<Result<AIProviderResponse, AIProviderError>> = async {
    // Open the SSE response
    use! stream =
        httpClient.GetStreamAsync("https://api.myvendor.com/v1/messages/stream")
        |> Async.AwaitTask

    use reader = new StreamReader(stream)

    let mutable accumulated = []
    let mutable usage = None
    let mutable stopReason = "end_turn"
    let mutable reading = true

    while reading do
        let! line = reader.ReadLineAsync() |> Async.AwaitTask

        if isNull line then
            reading <- false
        elif line.StartsWith("data: ") then
            let payload = line.Substring(6)

            match parseStreamChunk payload with
            | TextDelta delta ->
                cb delta
                accumulated <- delta :: accumulated
            | UsageUpdate u -> usage <- Some u
            | Done reason ->
                stopReason <- reason
                reading <- false
            | Heartbeat -> ()

    let content = String.concat "" (List.rev accumulated)

    return
        Ok {
            Content = content
            StopReason = stopReason
            ToolCalls = collectToolCalls content
            Usage = usage
        }
}
```

### Token usage reporting

Populate `AIProviderResponse.Usage` with the provider's reported token counts:

```fsharp
let providerResponse: AIProviderResponse = {
    Content = text
    // A wire-level string, not a DU — "end_turn" | "tool_use" | "max_tokens".
    StopReason = "end_turn"
    ToolCalls = []
    Usage = Some {
        PromptTokens = response.Usage.InputTokens
        CachedPromptTokens = response.Usage.CachedInputTokens |> Option.defaultValue 0
        OutputTokens = response.Usage.OutputTokens
        // `int option` — `None` for providers with no separate cache-write count.
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

### Triage capability

`Capabilities.SupportsTriage` declares that the provider can serve a *triage* turn: one tool-free, schema-constrained call over a four-field schema, whose only job is to classify a trivial UI instruction. The fast-path resolver treats it as a hard gate — a provider declaring `false` is never called for triage, and the request goes straight to the full agent loop.

`false` is the correct default, and the value `AIProviderCapabilities.unknown` carries. Declare `true` only when a small structured-output request is genuinely cheap and reliable for your provider; a connector whose only model is a frontier model gains nothing from the tier and should leave it off.

`Capabilities.TriageModelId` names the cheaper model your provider family would use, when it has one. It is a **declaration a composition root reads**, not a dispatch instruction: `IAIProvider` has no per-call model override, so the resolver cannot re-point your provider at that id. A deployment reads it, builds a second provider instance at that model, and hands it to the triage config. `None` means "no cheaper tier"; triage then runs on `Model` itself. See [`docs/migrations/6j-B-fastpath-triage.md`](../migrations/6j-B-fastpath-triage.md).

### Provider rules

- **Receive `ISecretStore` through the builder.** Never read env vars / config files directly. Builders accept the resolved API key as a parameter; the factory pulls the key from `ISecretStore` per-call.
- **Never log the API key.** Even at trace level. Log a hashed prefix if you must.
- **Capabilities declared truthfully.** `ToolUse = false` for providers that don't, even if the vendor's docs claim partial support — `false` is the safer floor that won't break the agent loop on unsupported features. Same for `SupportsTriage`: an over-claimed triage capability spends a call per instruction to fall through.
- **Author an `IHealthCheck` probe.** Verifies the API key is valid + the endpoint is reachable. Self-register via DI; auto-wired into `/ready`.
- **Author an `IConfigValidator` probe.** Verifies the configuration is correct at preflight. Refuse to start with helpful error messages when keys / endpoints are misconfigured.
- **Wire the builder into a `Server.props` extension contract.** Companion files extend `_ToolUpPlatformServerSources`; the consuming server project picks them up via the props chain.

### Provider authoring checklist

- [ ] `IAIProvider` impl with translation layer.
- [ ] `SendStructuredMessage` — either a native implementation against the vendor's JSON-Schema mode, or a one-line delegation to `IAIProviderDefaults.sendStructuredViaFallback` (see [Structured-output support](#structured-output-support) below).
- [ ] `AIProviderDescriptor` with unique `Id` matching the package vendor name.
- [ ] `AIProviderBuilder` pairing descriptor + Build function.
- [ ] Streaming support (if vendor supports it) — `SendMessage` honours its `onStream` argument, and a mid-stream failure returns `StreamingAborted` carrying the partial text.
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

For vendors without a native mode (or for an MVP provider you'll harden later), delegate one line to the helper — the member in context is shown whole in [Minimum implementation](#minimum-implementation) above:

```fsharp skip=fragment
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

Once an `IAIProvider` is resolved (via `IAIProviderFactory.Resolve` on the factory `DefaultAIProviderFactory.create` built, or any other factory path), call `SendStructuredMessage` directly:

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

let classify (provider: IAIProvider) = async {
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
        return Some(JsonDocument.Parse response.Content)
    | Error(SchemaUnsupported(feature, detail)) ->
        // Provider could not honour the schema (or the fallback couldn't
        // extract JSON from the response).
        eprintfn $"schema feature '{feature}' unsupported: {detail}"
        return None
    | Error err ->
        eprintfn $"structured call failed: {AIProviderError.toMessage err}"
        return None
}
```

#### Limitations (v1)

- **Non-streaming only.** Streaming structured-output is deferred to a follow-on phase.
- **Tool use is provider-dependent.** Gemini and OpenAI honour `tools` alongside the schema; Claude's workaround forces `tool_choice` on the synthesised schema-tool, so user-supplied tools become unreachable in the same turn. The canonical pattern: run any free-form tool-dispatch turns with `SendMessage` first, then a final `SendStructuredMessage` for the structured response.
- **Advanced schema features** (`oneOf`, `anyOf`, `$ref`, …) that one provider can't honour return `AIProviderError.SchemaUnsupported(feature, detail)` rather than degrading silently. Stick to the lowest common denominator for portability.

## Registering custom tools

### Server-side tools

`AIToolDefinition` is **metadata only** — it lives in `ToolUp.Platform.Core` so a module can declare tools without referencing the AI companion, and the executor is paired to it at registration time. `Parameters` is a list of one record per parameter, each with a JSON-Schema type name:

```fsharp
let myAnalysisTool: AIToolDefinition = {
    Name = "my_module.analyse"
    Description = "Run analysis over selected items in the active dataset."
    Parameters = [
        {
            Name = "item_ids"
            Type = "array"
            Description = "List of item IDs."
            Required = true
            Default = None
        }
        {
            Name = "metric"
            Type = "string"
            Description = "Metric to compute — one of 'revenue', 'units', 'margin'."
            Required = true
            Default = None
        }
        {
            Name = "weeks"
            Type = "number"
            Description = "Weeks of history."
            Required = false
            Default = Some "12"
        }
    ]
    SourceModule = "MyModule"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
}
```

The executor is `HttpContext -> string -> Async<string>` — raw JSON in, raw JSON out — and is registered **as a pair** with the definition. Helpers in `ToolUp.AI.ToolHelpers` (`requireString`, `requireDecimal`, `fableSerialize`) cover the recurring argument-extraction and serialisation boilerplate:

```fsharp
open System.Text.Json
open ToolUp.AI.ToolHelpers

let myAnalysisExecutor (_ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let args = JsonDocument.Parse(argsJson).RootElement
    let metric = requireString args "metric" "one of 'revenue', 'units', 'margin'"
    let weeks = requireDecimal args "weeks" "weeks of history"

    let! result = MyModule.Server.runAnalysis metric (int weeks)
    return fableSerialize result
}

let myModule =
    ServerModule.create "MyModule"
    |> ServerModule.withGuardedApi myApi
    |> ServerModule.withAITools [ myAnalysisTool, myAnalysisExecutor ]
```

The agent loop sees the tool in `GetAvailableTools`; the LLM can call it. When called, the executor runs server-side in-process with the caller's `AccessContext` available via the ambient context.

### Client-resident tools

The substrate (`ClientToolRuntime` + `ClientToolDispatch` + `AICancellationRegistry`) is generic — any companion can register `ClientResident` tools. A typical use is to let the LLM drive the UI (set form fields, click buttons, select rows, navigate). Server-side, a `ClientResident` tool dispatches to the client over SSE; the browser runs the tool and returns the result.

```fsharp
let setFieldTool: AIToolDefinition = {
    Name = "_platform.ui.set_field"
    Description = "Set the value of a field in the current page."
    Parameters = [ (* one record per parameter *) ]
    SourceModule = "MyCompanion"
    EmitsActions = None
    Location = ClientResident
    Surface = FullPageOnly
    IsLiveInterface = true
    ResultBudget = DefaultResultBudget
}
```

A `ClientResident` tool still registers an executor alongside its definition, but the agent loop branches on `Location` and dispatches over SSE **before** reaching it — so the paired executor is a stub that fails loudly if it is ever called, which would mean a regression in the dispatch wiring. The reference companion at `src/AI.Samples/ToolUp.AI.SampleClientTool.Server/` shows the shape.

`Surface = FullPageOnly` means the tool is filtered out of the per-turn tool list when the chat comes from the side panel (Mode 1 — "just do it"), which has no active-page context to drive. `IsLiveInterface = true` declares that the tool reads or drives browser-resident state, which is what a RAG deployment's prompt framing keys off — `Location = ClientResident` implies it, so the flag exists for the case `Location` cannot express: a *server*-resident tool that nonetheless projects live interface state.

The client-side runtime (`ClientToolRuntime` in `ToolUp.AI.Client`) handles the dispatch lifecycle — opens a session per tool call, waits for the result, returns it to the server. Cancellation cascades both ways.

### Tool authoring rules

- **Tool name format**: `<scope>.<verb>` — e.g. `my_module.analyse`, `_platform.list_documents`, `_platform.ui.set_field`. The `_platform.` prefix is reserved for platform / companion-contributed tools.
- **Parameter schema is JSON-Schema-shaped.** The model sees `parameters: { type: "object", properties: { ... } }`. Required vs optional is currently implicit (all properties required); future schema versions may add explicit `required` lists.
- **Executor must handle missing / malformed args gracefully.** An executor returns a plain JSON `string`; there is no error-result type. The `ToolHelpers` argument validators (`requireString`, `requireDecimal`, …) signal a bad argument by *raising* — `ToolArgumentError` for a missing / wrong-typed value, and `JsonException` from the parse — and the agent loop catches both at its dispatch site and classifies them as `InvalidArguments`, so the model is told to repair its arguments rather than to retry the same call.
- **Any other exception is classified as `ToolThrew`.** The turn is not aborted: the loop renders the failure as a tool-result string the model can read (`ToolInvocationError.toToolResultContent`) and continues. Prefer returning a domain-shaped JSON error the model can act on over throwing, and reserve `ToolArgumentError` for genuine argument defects.
- **Result size**: every tool result passes a per-tool context budget at agent-loop dispatch (`ResultBudget` on the definition). `DefaultResultBudget` resolves to a generous SDK-wide ceiling no well-behaved result approaches; a tool whose result grows with data cardinality declares its own `ResultBudgetChars n` (characters of the returned JSON, must be positive), and an export-shaped tool whose whole point is the payload declares `NoResultBudget`. An over-budget result reaches the model as a typed JSON marker naming the tool and the elided size, with a steer to narrow the query — the call still counts as a success, not an error.
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
        // IDataCatalog.ListObjects is (scopeId, typeId) and returns the
        // latest `DataObject` per stored object.
        let! catalog = dataCatalog.ListObjects(ctx.Access.TeamId |> Option.defaultValue "", "SalesData")
        let summary =
            catalog
            |> List.map (fun o ->
                let created = o.CreatedAt.ToString "yyyy-MM-dd"
                $"  - {o.ObjectId}: v{o.Version}, created {created}")
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
        SystemPromptBuilder.fromStatic "You are an analytics assistant."
        SystemPromptBuilder.activeModuleContext
        dataSummaryPromptBuilder
    ]

AIServerApp.create aiProviderFactory providerProfile
|> AIServerApp.withAIConfig {
    Branding = branding
    SystemPrompt = Some composedBuilder
    MaxHistoryMessages = None
    AISurfaceDerivation = TrustClient
}
|> AIServerApp.run
```

### Composition rules

- **Builders run in parallel** — order in the list affects join order, not execution order.
- **A builder returning `""` is silently dropped** — no double blank lines.
- **A builder that throws aborts the whole compose** — wrap risky logic in try/with.
- **Network calls in builders block the turn** — every chat message waits for every builder to complete. Keep builders fast; cache aggressively. The default builders are sub-millisecond.
- **AccessContext.TeamId is scope-validated upstream** — the builder can trust the team scope. Team A's builder never sees Team B's context.

## Declaring capability flags

`AIProviderCapabilities` flags propagate from the provider to consumers (the agent loop, the AI Settings UI, downstream features that need vision input, etc.). Declare truthfully:

- `Streaming` — true if the provider's `SendMessage` honours a `Some cb` `onStream` argument and emits incremental tokens through it.
- `ToolUse` — true if the provider correctly translates the `Tools` array into the vendor's tool schema and parses tool calls in the response.
- `Vision` — true if the provider accepts image content in messages (the `AIProviderMessage.Parts` multipart payload). Providers that declare `false` — or whose configured model isn't vision-capable — reject multipart messages synchronously with `AIProviderError.UnsupportedCapability("vision", …)`, no network round-trip.
- `SupportsPromptCaching` — true if the provider implements cache markers (explicit or implicit). Drives `CacheHitRate` reporting in `/dev/ai-latency`.
- `SupportsTriage` / `TriageModelId` — the fast-path triage gate + cheaper-tier declaration; see [Triage capability](#triage-capability) above.

The agent loop respects these:
- `Streaming = false` → loop passes `onStream = None`, treats the response as non-streaming.
- `ToolUse = false` → loop doesn't include `Tools` in the request; tool calls in the response are warned as invariant violations.
- `Vision = false` → multimodal feature flags upstream of the agent gate to disabled for this provider.

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

`ToolUp.Platform.Tests` carries the SDK's reusable contract packs. It is
`IsPackable=false`, so it is not a NuGet dependency you take — the documented
adoption route is to copy the pack you need into your own test project (the
same route the packaged-module template uses). For provider integration tests:

```fsharp
open Expecto
open ToolUp.AI
open MyVendor.AIProvider

let tests =
    testList "MyVendor provider" [
        testCaseAsync "round-trips a simple message" <| async {
            let provider = MyVendor.AIProvider.createWithApiKeyAndModel testApiKey "test-model"

            let! result =
                provider.SendMessage(
                    [ AIProviderMessage.text "user" "What's 2 + 2?" ],
                    [],                             // tools
                    Some "You are helpful.",        // system prompt
                    None,                           // onStream — non-streaming turn
                    RetryPolicy.defaults
                )

            match result with
            | Ok response ->
                Expect.isTrue (response.Content.Length > 0) "expected assistant content"
                Expect.equal response.StopReason "end_turn" "expected an end_turn stop reason"
            | Error err -> failtest (AIProviderError.toMessage err)
        }
    ]
```

For unit tests of the wire-format translation layer, no provider key is needed — test the `translateRequest` / `translateResponse` functions directly with synthetic inputs.

For SDK-level integration tests (agent loop + provider), there is no shipped
test-double type to configure — `IAIProvider` is a three-member interface, so
the double *is* an object expression. `SendStructuredMessage` delegates to the
same fallback helper a real non-native provider uses, and
`AIProviderCapabilities.unknown` is the all-false floor to start from:

```fsharp
let scriptedProvider =
    { new IAIProvider with
        member _.Capabilities = {
            AIProviderCapabilities.unknown with
                ProviderName = "scripted"
                Model = "scripted-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            // Custom response logic for the test
            return
                Ok {
                    Content = """{"ok": true}"""
                    StopReason = "end_turn"
                    ToolCalls = []
                    Usage = None
                }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy }
```

Wrap it with `DefaultAIProviderFactory.singleProvider descriptor scriptedProvider`
to hand the agent loop an `IAIProviderFactory` that resolves to it for every
context, or `DefaultAIProviderFactory.empty` to exercise the
`NoProviderConfigured` path. This lets you test agent-loop behaviour, tool
dispatch, system-prompt composition, etc. without burning real LLM tokens in CI.
