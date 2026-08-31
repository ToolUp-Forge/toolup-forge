// Ambient context for `docs/ai/extending.md`.
//
// The page teaches a reader how to write their own AI provider
// companion, their own tools, their own prompt builder and their own
// authorizer — so nearly every block is an excerpt from a program the
// page never shows in full: the fictional vendor's wire types and
// translation layer, the composition root's `providerProfile` /
// `secretStore` / `branding`, the two already-wired provider builders a
// deployment appends to, and the consumer's own module / policy /
// authorizer. Declared here so the blocks compile exactly as a reader
// would copy them, with no `open`-ceremony added to the markdown.
//
// Note `MyVendor.AIProvider` is declared as a NESTED module below.
// A doc block is spliced in at the top level of its generated file, so
// the `module MyVendor.AIProvider` header the page's provider blocks
// used to carry could never compile there (a dotted module declaration
// must be a file's first declaration). The page states the module in
// prose instead, and the nested declaration here is what makes the
// later blocks' `open MyVendor.AIProvider` resolve.
open System.IO
open System.Net.Http
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI.SystemPromptBuilder

[<AutoOpen>]
module PageAmbient =

    // ── The fictional vendor's own wire format ───────────────────
    // Everything a provider companion owns on its side of the
    // translation layer. None of this is SDK surface.

    /// The vendor's per-turn token accounting, before it is normalised
    /// onto the SDK's `TokenUsage`.
    type MyVendorUsage = {
        InputTokens: int
        CachedInputTokens: int option
        OutputTokens: int
        CacheCreationTokens: int option
    }

    /// What the vendor's non-streaming endpoint returns.
    type WireResponse = {
        Text: string
        StopReason: string
        Usage: MyVendorUsage
    }

    /// What the companion POSTs to the vendor's endpoint.
    type WireRequest = { Model: string; Body: string }

    /// One decoded frame from the vendor's SSE stream.
    type StreamChunk =
        | TextDelta of string
        | UsageUpdate of TokenUsage
        | Done of stopReason: string
        | Heartbeat

    /// The two halves of the translation layer the page's "Testing a
    /// provider" section tells you to unit-test directly.
    let translateRequest
        (messages: AIProviderMessage list)
        (tools: AIProviderToolDef list)
        (systemPrompt: string option)
        : WireRequest =
        failwith "ambient"

    let translateResponse (wire: WireResponse) : AIProviderResponse = failwith "ambient"

    let parseStreamChunk (payload: string) : StreamChunk = failwith "ambient"

    let collectToolCalls (content: string) : AIProviderToolCall list = failwith "ambient"

    /// The companion's HTTP client, its declared capabilities, and one
    /// already-received vendor response — the locals the token-usage
    /// and streaming excerpts read.
    let httpClient: HttpClient = failwith "ambient"

    let capabilities: AIProviderCapabilities = failwith "ambient"

    let response: WireResponse = failwith "ambient"

    let text: string = failwith "ambient"

    /// The provider type the page builds in "Minimum implementation".
    /// Later blocks construct it without repeating it.
    type MyVendorProvider(apiKey: string, model: string, httpClient: HttpClient) =
        interface IAIProvider with
            member _.Capabilities = capabilities

            member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = failwith "ambient"

            member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = failwith "ambient"

    /// The companion's own module, as the page's later blocks address
    /// it. Declared nested so `open MyVendor.AIProvider` resolves —
    /// see the note at the top of this file.
    module MyVendor =
        module AIProvider =
            let descriptor: AIProviderDescriptor = failwith "ambient"

            let createWithApiKeyAndModel (apiKey: string) (model: string) : IAIProvider = failwith "ambient"

            let builder: AIProviderBuilder = failwith "ambient"

    // ── The consuming app's composition root ─────────────────────

    /// The two provider builders a deployment already had wired before
    /// it appended its own.
    let claudeBuilder: AIProviderBuilder = failwith "ambient"

    let openAiBuilder: AIProviderBuilder = failwith "ambient"

    let providerProfile: IProviderProfile = failwith "ambient"

    let secretStore: ISecretStore = failwith "ambient"

    let platformProviders: DefaultAIProviderFactory.AIPlatformProvider list =
        failwith "ambient"

    let aiProviderFactory: IAIProviderFactory = failwith "ambient"

    let branding: AIAssistantBranding = failwith "ambient"

    /// The deployment's data catalog, read by the custom prompt builder.
    let dataCatalog: IDataCatalog = failwith "ambient"

    /// The prompt builder authored in "Authoring a custom
    /// `SystemPromptBuilder`", composed by the block after it.
    let dataSummaryPromptBuilder: SystemPromptBuilder = failwith "ambient"

    // ── The consuming app's own module + tools ───────────────────

    /// The module's ToolUp.Remoting contract and its per-request
    /// factory, as `ServerModule.withGuardedApi` takes it.
    type MyApi = { DoThing: string -> Async<string> }

    let myApi: HttpContext -> MyApi = failwith "ambient"

    /// The tool metadata declared in "Server-side tools", paired with
    /// its executor one block later.
    let myAnalysisTool: AIToolDefinition = failwith "ambient"

    /// The module's own server-side routine the executor calls.
    module MyModule =
        module Server =
            let runAnalysis (metric: string) (weeks: int) : Async<string list> = failwith "ambient"

    // ── The consumer's own client-tool authorizer ────────────────

    /// The policy shape a consumer's allowlist authorizer is built
    /// from — deliberately not an SDK type: forge ships no
    /// implementation of `IClientToolAuthorizer`.
    type MyPolicy = {
        AllowedTools: string list
        AllowedPages: string list
    }

    let myPolicy: MyPolicy = failwith "ambient"

    type MyCompanyAuthorizer(policy: MyPolicy) =
        interface IClientToolAuthorizer with
            member _.Authorize(_toolName, _argsJson, _activeModule, _activePage) = failwith "ambient"

    // ── The provider integration test's fixture ──────────────────

    let testApiKey: string = failwith "ambient"