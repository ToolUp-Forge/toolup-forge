// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.OpenAIProviderTests

open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AIProviders.Tests.Support
open ToolUp.AIProviders.Tests.Support.ProviderTestPack

// Per-provider binding for the OpenAI companion. Mirrors the Claude
// binding's shape; the only deltas are the env-var name + the
// descriptor / factory references.

let private spec: ProviderSpec = {
    DisplayName = "OpenAI"
    EnvVarName = "OPENAI_API_KEY"
    Descriptor = {
        Id = OpenAIProvider.ProviderId
        DisplayName = "OpenAI"
        SupportedModels = OpenAIProvider.KnownModels
        DefaultModel = OpenAIProvider.DefaultModel
        Capabilities = {
            Streaming = true
            ToolUse = true
            Vision = true
            SupportsPromptCaching = true
            ProviderName = OpenAIProvider.ProviderId
            Model = OpenAIProvider.DefaultModel
        }
    }
    CreateWithApiKey = OpenAIProvider.createWithApiKey
    CreateWithApiKeyAndModel = OpenAIProvider.createWithApiKeyAndModel
    // GPT-3.5 has no vision — `OpenAIProviderWire.isVisionCapable`
    // matches gpt-4o/gpt-4-turbo/gpt-4.1/o1/o3 and rejects this.
    NonVisionModel = "gpt-3.5-turbo"
}

let tests = ProviderTestPack.tests spec