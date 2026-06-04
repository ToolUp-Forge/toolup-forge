# Getting started with ToolUp.AI

End-to-end walkthrough: from zero to a working AI assistant in your app.

## Prerequisites

- A working ToolUp Platform app — at minimum a server composition root calling `ServerApp.run` and a client composition root calling `Client.run`. See [`samples/HelloWorld/`](../../samples/HelloWorld/).
- A platform mode other than `Anonymous` — `Individual`, `Team`, or `MultiTeam`. The walkthrough assumes `Individual`.
- An API key for at least one LLM provider. Anthropic and OpenAI both work; this walkthrough uses Anthropic.

## 1. Add the packages

In your server project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.AI.Server" />
  <PackageReference Include="ToolUp.AIProviders.Claude" />
</ItemGroup>
```

In your client project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.AI.Client" />
</ItemGroup>
```

`ToolUp.AI.Core` is pulled in transitively by the Server and Client packages.

## 2. Store the provider API key

Provider API keys are resolved per-call from `ISecretStore` — never hardcoded, never read directly from env vars by the provider code itself. The platform-default API key lives under the `_platform` scope.

Easiest setup for local dev: `FileSecretStore` writes encrypted JSON to disk under `data/secrets/`.

```fsharp
let masterKey =
    Environment.GetEnvironmentVariable("TOOLUP_SECRET_MASTER_KEY")
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "TOOLUP_SECRET_MASTER_KEY must be set")

let secretStore =
    EncryptedSecretStore(FileSecretStore(), masterKey) :> ISecretStore

// One-time setup — store the API key under the platform scope.
do secretStore.SetSecret("_platform", "ANTHROPIC_API_KEY", "<your-key>")
   |> Async.RunSynchronously
```

Production: replace `FileSecretStore` with `ToolUp.Secrets.AzureKeyVault` (or a custom `ISecretStore` against AWS Secrets Manager / GCP Secret Manager / HashiCorp Vault).

## 3. Wire the provider factory

The factory translates "this request, this user" into a configured `IAIProvider` instance. It reads the user's BYOK config (if any) from `IUserAIConfigStore`, falls back to the platform-default if BYOK is disabled or the user hasn't configured one, and pulls the API key from `ISecretStore`.

```fsharp
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AIProviders.Claude
open ToolUp.AIProviders.OpenAI

let aiConfigStore =
    DefaultUserAIConfigStore.create blobStorage secretStore :> IUserAIConfigStore

let aiProviderFactory =
    DefaultAIProviderFactory.create
        [ ClaudeAIProvider.builder; OpenAIProvider.builder ]
        aiConfigStore
        secretStore
        PlatformOnly    // or AllowUserProviders for full BYOK
```

`BYOKMode`:
- `PlatformOnly` — every user uses the deployment's API key. Simplest; deployment carries 100% of cost.
- `AllowUserProviders` — users may supply their own provider via the AI Settings UI. Each call uses the user's key (if configured), falling back to the platform default. Cost shifts to users for those who BYOK.

## 4. Compose the AI server app

```fsharp
[<EntryPoint>]
let main _ =
    AIServerApp.create (aiProviderFactory, aiConfigStore)
    |> AIServerApp.withConfig {
        ServerConfig.defaults with
            Port = 5000
            Mode = Individual
    }
    |> AIServerApp.withAuth authProvider
    |> AIServerApp.withStorage blobStorage
    |> AIServerApp.addModules modules
    |> AIServerApp.withAITools AITools.allTools
    |> AIServerApp.run
```

`AIServerApp` is a flat superset of `ServerApp` — every `ServerApp.with*` is mirrored on `AIServerApp`, plus AI-specific helpers (`withAIFactory`, `withAIConfigStore`, `withAITools`, `withAIConfig`, `withModuleAIContexts`). `AIServerApp.run` fails loudly if `aiProviderFactory` or `aiConfigStore` is missing — the create overload above requires both.

`AITools.allTools` is the default tool registry; pass `[]` for a tool-less deployment, or a custom list to add module-declared tools. See [extending.md](extending.md).

## 5. Wire the client wrapper

In the client entry point:

```fsharp
open ToolUp.Elmish
open ToolUp.Elmish.React
open ToolUp.Platform
open ToolUp.AI

let aiMode =
    ConfiguredAIAssistant {
        Name = "Aria"
        Icon = "/svg/spark.svg"
        ShowSidePanel = true
    }

let clientConfig = { ClientConfig.defaults with AppName = "MyApp"; Mode = Individual }
let modules = [ (* your module registrations *) ]

AIClientConfig.withAIAssistant aiMode clientConfig modules
|> Program.withReactSynchronous "elmish-app"
|> Program.run
```

`AIAssistantMode`:
- `NoAIAssistant` — no AI module or side panel. Use `Client.run` directly instead.
- `DefaultAIAssistant` — built-in module with SDK defaults (name "AI Assistant", generic icon, side panel on).
- `ConfiguredAIAssistant of AIAssistantBranding` — custom name + icon + side panel toggle. Branding only; system-prompt content stays server-side.

`ShowSidePanel = true` adds a togglable side panel that floats over every page. `false` puts the AI assistant in its own full-page module accessible from the sidebar. Some apps enable both; the side panel can call any module-declared tool, the full-page module is for longer back-and-forth.

## 6. Verify the wiring

Start the server. Visit the app in the browser; sign in. The "AI Assistant" sidebar entry should be present, and if you configured `ShowSidePanel = true`, a side-panel toggle (spark icon by default) should be in the header.

Open the side panel. Type "what tools are available?". The agent loop should respond with a list of registered tools.

Curl the API directly:

```bash
curl -X POST http://localhost:5000/api/IAIAssistantApi/GetAvailableTools \
  -H "X-User-Id: dev-user-id" \
  -H "Content-Type: application/json" \
  -d '[]'
```

Should return the list of tools registered via `AITools.allTools` plus any module-declared tools.

## 7. Register a module-private AI context (optional)

For modules where the assistant should know domain context, declare a `ModuleAIContext`:

```fsharp
// In MyModule/Server.fs
let aiContext : ModuleAIContext = {
    ModuleName = "MyModule"
    SystemPrompt = """You are helping with sales analysis. The active dataset has
                      columns: sku, date, units_sold, revenue. Quantities are in units;
                      revenue is in GBP. Date format is YYYY-MM-DD."""
}
```

In the server composition root:

```fsharp
let moduleAIContexts = [
    MyModule.Server.aiContext
    // ... other modules with AI contexts
]

AIServerApp.create (aiProviderFactory, aiConfigStore)
|> ...
|> AIServerApp.withModuleAIContexts moduleAIContexts
|> AIServerApp.run
```

When the user chats from `MyModule`'s view, the client attaches `ActiveModule = Some "MyModule"` to the request. The agent's system-prompt builder looks up that context and injects it. The user never sees this in their chat history — it's metadata to the model.

This is the only sanctioned "private prompt" mechanism. There's no per-user invisible prompt mode and no client-side prompt injection — anything that feeds the model is either visible in the user's chat or declared at the deployment boundary.

## Next steps

- [concepts.md](concepts.md) — understand the agent loop, SSE streaming, conversation persistence.
- [api-reference.md](api-reference.md) — the full public surface.
- [extending.md](extending.md) — write a new provider, register custom tools, author a custom `SystemPromptBuilder`.
