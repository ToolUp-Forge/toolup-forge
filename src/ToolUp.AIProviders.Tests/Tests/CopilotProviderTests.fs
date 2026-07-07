module ToolUp.AIProviders.Tests.Tests.CopilotProviderTests

open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AIProviders.Tests.Support
open ToolUp.AIProviders.Tests.Support.ProviderTestPack

// Azure OpenAI ("Microsoft Copilot") binding to the shared provider
// conformance pack. The live arms gate on `AZURE_OPENAI_API_KEY` (per
// `spec.EnvVarName`) — absent on a fresh checkout, so they report Pending,
// not Failed. A real live run also needs `AZURE_OPENAI_ENDPOINT` set to the
// resource endpoint; without it the placeholder below is used and the live
// call fails fast (informative, not silent).
let private endpoint =
    System.Environment.GetEnvironmentVariable "AZURE_OPENAI_ENDPOINT"
    |> Option.ofObj
    |> Option.defaultValue "https://placeholder.openai.azure.com"

let private spec: ProviderSpec = {
    DisplayName = "Microsoft Copilot"
    EnvVarName = "AZURE_OPENAI_API_KEY"
    Descriptor = {
        Id = CopilotAIProvider.ProviderId
        DisplayName = "Microsoft Copilot"
        SupportedModels = CopilotAIProvider.KnownModels
        DefaultModel = CopilotAIProvider.DefaultModel
        Capabilities = {
            Streaming = true
            ToolUse = true
            Vision = true
            SupportsPromptCaching = true
            ProviderName = CopilotAIProvider.ProviderId
            Model = CopilotAIProvider.DefaultModel
        }
    }
    // Endpoint is partially applied so these match the pack's
    // `string -> IAIProvider` / `string -> string -> IAIProvider` shapes.
    CreateWithApiKey = CopilotAIProvider.createWithApiKey endpoint
    CreateWithApiKeyAndModel = CopilotAIProvider.createWithApiKeyAndModel endpoint
    // Azure deployment-name convention drops the dot (gpt-3.5 -> gpt-35);
    // not vision-capable, so the capability-gating arm rejects synchronously.
    NonVisionModel = "gpt-35-turbo"
}

let tests = ProviderTestPack.tests spec