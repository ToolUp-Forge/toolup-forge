# ToolUp.Platform Technical Guide — 11. AI Integration & Closing Notes

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 10. Notifications & Webhooks](10-notifications-and-webhooks.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 12. Hosting Models →](12-hosting-models.md)

---

## AI integration — see the companion

The AI assistant runtime (agent loop, SSE streaming, conversation persistence, tool registry, system-prompt composition including team-private and module-private context) lives in the [`ToolUp.AI`](../../ToolUp.AI/) companion package. The core SDK only ships:

- `IAIProvider` — the provider extension point interface ([`ToolUp.Platform.Core/Shared/Interfaces/IAIProvider.fs`](../../ToolUp.Platform.Core/Shared/Interfaces/IAIProvider.fs))
- `AIToolDefinition` / `ToolParameterSchema` — module-facing tool declarations ([`ToolUp.Platform.Core/Shared/Types/ModuleAITypes.fs`](../../ToolUp.Platform.Core/Shared/Types/ModuleAITypes.fs))
- `ComposeExtensions` — the hook `ToolUp.AI` uses to inject handlers and DI services without `compose` knowing about AI

Provider-reported token usage rides on `AIProviderResponse.Usage: TokenUsage option` (`PromptTokens`, `CachedPromptTokens`, `OutputTokens`, plus optional Anthropic-specific `CacheCreationTokens`). Vocabulary is normalised across providers — Anthropic's `cache_read_input_tokens` and OpenAI's `prompt_tokens_details.cached_tokens` both populate `CachedPromptTokens`. Providers without a usage block (or without a prompt cache) declare `Capabilities.SupportsPromptCaching = false` and consumer-side rollups (e.g. `/dev/ai-latency`) hide the cache-hit-rate column for those buckets. See `ToolUp.AI/TECHNICAL_GUIDE.md` "Cache hit rate (Phase 6i.B)" for the full surface.

For full AI architecture — SSE lifecycle, ToolUp.Remoting vs SSE serialisation rules, agent loop internals, `SystemPromptBuilder` composition, team- and module-aware prompts, tool error classification, client-side local-state pattern, Elmish.HMR limitations, Vite proxy configuration — see [`src/ToolUp.AI/TECHNICAL_GUIDE.md`](../../ToolUp.AI/TECHNICAL_GUIDE.md). Deployment overview and extension points are in [`src/ToolUp.AI/README.md`](../../ToolUp.AI/README.md).

The rest of this section covers the one AI-adjacent mechanism that stays in core: the ToolUp.Remoting body-normalisation behaviour.

### ToolUp.Remoting: unit function body normalisation

Browsers send `unit -> Async<T>` API calls in inconsistent shapes: GET with no body, POST with `""`, POST with `null`, POST with a missing body altogether. A naive dispatcher expecting a JSON array (`[]`) breaks on every one of them — methods like `ListConversations`, `GetAvailableTools`, `GetPlatformInfo`, `GetMyTeams`, `GetActiveTeam`, `ListFiles` would all fail.

The in-tree ToolUp.Remoting fork (over upstream Fable.Remoting) folds body normalisation **into the dispatcher itself**, inside `ToolUp.Platform.Server`. Requests carrying the `x-remoting-proxy` header have empty / `""` / `null` bodies promoted to `[]` before the dispatcher reads them; the unit-arg path then proceeds normally. There is no separate `RemotingBodyNormalizationMiddleware` to wire into `compose`, and no `app.UseMiddleware<…>` registration to remember — `dotnet build` is the gate, not a middleware presence check.

The change is invisible to consumer code: `unit -> Async<T>` API methods just work. If a consumer is migrating from a pre-fork SDK version that did rely on `RemotingBodyNormalizationMiddleware`, the registration can be deleted; the dispatcher already handles it.

<!-- AI-specific concerns moved to src/ToolUp.AI/TECHNICAL_GUIDE.md:
     SSE JSON serialisation (the STJ FableConverters options), SSE userId
     matching, client-side local-state input pattern, Elmish.HMR SSE
     subscription loss, Vite proxy SSE buffering. -->

AI-specific concerns (SSE JSON serialisation via the `ToolUp.Remoting.Json.SystemTextJson.FableConverters` options, SSE userId matching, client-side local-state input pattern, Elmish.HMR SSE subscription loss, Vite proxy SSE buffering) have all moved to [`src/ToolUp.AI/TECHNICAL_GUIDE.md`](../../ToolUp.AI/TECHNICAL_GUIDE.md). Look there for their full explanations.

## Key Design Constraints

**F# compile order is sacred.** Every file can only reference files that appear above it in the `.fsproj`. The props import order determines the compile order: SDK types -> shared domain types -> SDK UI -> module shared types -> module models -> module views -> entry point. Getting this wrong produces compile errors that can be confusing.

**Fable compiles one project.** All client-side F# must be in a single Fable compilation unit. This is why modules inject source files via props rather than being separate projects — separate Fable-compiled projects would create cross-assembly issues with anonymous records and type identity.

**The SDK `.fsproj` compiles only shared types.** Files like `UIToolkit.fs`, `SDK.Client.fs`, and `SDK.Server.fs` are marked `<None>` in the SDK project. They are compiled by the consuming Client or Server project via props injection. This is because they depend on packages (Feliz, Giraffe, the in-tree `Fable.Remoting.Giraffe` and `Fable.Remoting.Client` adapters under `ToolUp.Platform.{Server,Client}`) that the SDK project does not reference — the consuming project provides those dependencies.

**Do not add server packages to ToolUp.Platform's `paket.references`.** These would flow transitively to the client project, and Fable cannot handle ASP.NET Core assemblies. Server files must compile in the consuming server project's context via `.Server.props`.

**Consuming server projects need no separate JSON-serialisation reference.** The current wire is System.Text.Json with the `ToolUp.Remoting.Json.SystemTextJson.FableConverters` converter set, shipped inside `ToolUp.Platform.Server`; a server consumer's `PackageReference Include="ToolUp.Platform.Server"` is sufficient (`System.Text.Json` itself ships in the BCL). The historic guidance (kept here for context on older deployments still on a pre-fork, pre-STJ SDK) was: Twelve SDK server files (`Server/ConfigStore.fs`, `Server/DataObjectStore.fs`, `Server/ResultStore.fs`, `Server/LineageStore.fs`, `Server/AuditLog.fs`, `Server/FeatureFlagStore.fs`, `Server/FeatureFlagHandler.fs`, `Server/ModuleQueryBus.fs`, `Server/NotificationHandler.fs`, `Server/WebhookApiHandler.fs`, `Server/WebhookDispatcher.fs`, `Server/WebhookRegistry.fs`) `open Newtonsoft.Json` and `open Fable.Remoting.Json` to use `FableJsonConverter` for lossless F# DU persistence (`VersioningPolicy`, `LinkType`, and similar payload-carrying DUs). Newtonsoft arrives transitively through `Fable.Remoting.Json` — the SDK's own `paket.references` lists only `FSharp.Core`, by the rule above. Without the consumer-side reference, every store file errors with unresolved `Newtonsoft.Json`, surfacing the symptom rather than the cause. The canonical helper shape lives in `Server/ConfigStore.fs:16-26`; new SDK files persisting F# data should copy it. Symmetric on the client: Fable client projects parsing JSON from these stores or from SSE frames need `Fable.SimpleJson` — it reads the same `FableJsonConverter` wire format losslessly without an additional converter, and `Fable.Remoting` RPC is automatic via `Fable.Remoting.Client` and unaffected by this contract. See [README.md → Consumer dependency contract](../README.md#consumer-dependency-contract) for the canonical write-up.

**`importSideEffects` resolves relative to the source file.** When `SDK.Client.fs` was in the ToolUp.Platform directory, `importSideEffects "./index.css"` resolved to `ToolUp.Platform/index.css` — which doesn't exist. The CSS import must be in `Client.fs` (the app's entry point) where the relative path resolves to the consuming client project's own `index.css`. This is a Fable-specific constraint that affects where certain JS interop calls can live.

**`#if DEBUG` requires explicit Fable configuration.** Fable defaults to Release configuration unless `-c Debug` is passed. The `DefineConstants` in the `.fsproj` are only effective if Fable is invoked with the matching configuration. The SDK's `Run` FAKE target passes `-c Debug` to ensure `#if DEBUG` blocks are active during development.

## Runtime quirks observed during the .NET 10 / Fable 5 migration

Worth knowing when upgrading tools or adding new Feliz 3 consumers:

**Paket major bumps need `dotnet paket restore --force`.** Paket 10 does not regenerate its per-project `obj/*.paket.props` files during a normal `dotnet paket install` if it decides the lock file is up to date. Older paket-generated props survive the tool bump and can mis-resolve package references (seen on the .NET 8 → 10 bump with AzureBlobStorage failing to find `Azure.Storage.Blobs` types). Force-restore flushes the stale props.

**Fable tool major versions must co-move with Feliz major versions.** Feliz's `[<ReactComponent>]` / `[<Hook>]` attribute plugins are compiled against a specific `Fable.AST` shape. Fable 5 changed the `Fable.AST.Fable.Ident` constructor signature; Feliz 2's plugin DLL crashes with `Method not found: Fable.AST.Fable.Ident..ctor(...)` when loaded by the Fable 5 tool. The Fable tool bump and the Feliz bump need to land in the same commit; they cannot be separated.

**Feliz 3 moved `createElement` to `ReactLegacy`.** Feliz 2's `Interop.reactApi.createElement(component, props, children)` is gone. The direct port is `ReactLegacy.createElement(component, props, children)` — same 15 overloads, different host type. The `React` type in Feliz 3 is reserved for hooks (`useState`, `useEffect`, `useContext`) and modern component helpers (`Fragment`, `KeyedFragment`, `forwardRef`, `Imported`, `DynamicImported`). `ReactLegacy`'s overload signatures require the first argument to be `ReactElement`, `ReactNode`, or `string` — `obj`-typed imports need `unbox<ReactElement>` coercion at the call site. The idiomatic-modern alternative is `[<Import("Name", "pkg")>] let MyComponent (props: obj) : ReactElement = jsNative` and calling `MyComponent props` directly, avoiding `createElement` altogether.

**Feliz 3 dropped the `name` parameter from `React.createContext`.** Signature went from `createContext(name: string, defaultValue: 'a)` to `createContext(?defaultValue: 'a)`. Provider setup changed too: `React.contextProvider(ctx, value, children)` is no longer a static method; the replacement is `ctx.Provider(value, children)` — instance method on `ReactContext<'a>`.

**React 19 + Clerk peer-dep pinning.** `@clerk/react` uses tilde ranges in its `peerDependencies` (e.g. `^18.0.0 || ~19.0.3 || ~19.1.4 || ~19.2.3 || ~19.3.0-0`). A caret range like `react@^19` in `package.json` can resolve to a version outside all of these tildes and break Clerk's peer check. Pin React with a tilde (e.g. `~19.2.3`) to stay inside one of Clerk's accepted ranges.

---

> [← Prev: 10. Notifications & Webhooks](10-notifications-and-webhooks.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 12. Hosting Models →](12-hosting-models.md)
