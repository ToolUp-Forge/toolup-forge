// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.GeminiProviderTests

open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AIProviders.Tests.Support
open ToolUp.AIProviders.Tests.Support.ProviderTestPack

// Per-provider binding for the Gemini companion. Same shape as the
// Claude + OpenAI bindings — no Gemini-specific test surface here on
// purpose; the bundle's goal is to close Phase 67's deferred tail
// without privileging Gemini over the two reference providers.

let private spec: ProviderSpec = {
    DisplayName = "Gemini (Google)"
    EnvVarName = "GEMINI_API_KEY"
    Descriptor = {
        Id = GeminiAIProvider.ProviderId
        DisplayName = "Gemini"
        SupportedModels = GeminiAIProvider.KnownModels
        DefaultModel = GeminiAIProvider.DefaultModel
        Capabilities = {
            Streaming = true
            ToolUse = true
            Vision = true
            SupportsPromptCaching = true
            SupportsTriage = true
            TriageModelId = Some "models/gemini-2.5-flash"
            ProviderName = GeminiAIProvider.ProviderId
            Model = GeminiAIProvider.DefaultModel
        }
    }
    CreateWithApiKey = GeminiAIProvider.createWithApiKey
    CreateWithApiKeyAndModel = GeminiAIProvider.createWithApiKeyAndModel
    // Legacy text-only Gemini 1.0 — `GeminiAIProviderWire.isVisionCapable`
    // matches gemini-2/gemini-1.5/gemini-pro-vision and rejects this.
    NonVisionModel = "models/gemini-1.0-pro"
}

let tests = ProviderTestPack.tests spec