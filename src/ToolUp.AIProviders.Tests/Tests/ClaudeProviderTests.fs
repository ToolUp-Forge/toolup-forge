// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.ClaudeProviderTests

open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AIProviders.Tests.Support
open ToolUp.AIProviders.Tests.Support.ProviderTestPack

// Per-provider binding for the Claude companion. Lives in its own file
// (rather than parameterising via `testList` inside a shared file) so
// each provider's run shows up as its own labelled testList in the
// Expecto report and per-provider issues are easy to bisect.

let private spec: ProviderSpec = {
    DisplayName = "Claude (Anthropic)"
    EnvVarName = "ANTHROPIC_API_KEY"
    Descriptor = {
        Id = ClaudeAIProvider.ProviderId
        DisplayName = "Claude"
        SupportedModels = ClaudeAIProvider.KnownModels
        DefaultModel = ClaudeAIProvider.DefaultModel
        Capabilities = {
            Streaming = true
            ToolUse = true
            Vision = true
            SupportsPromptCaching = true
            ProviderName = ClaudeAIProvider.ProviderId
            Model = ClaudeAIProvider.DefaultModel
        }
    }
    CreateWithApiKey = ClaudeAIProvider.createWithApiKey
    CreateWithApiKeyAndModel = ClaudeAIProvider.createWithApiKeyAndModel
}

let tests = ProviderTestPack.tests spec