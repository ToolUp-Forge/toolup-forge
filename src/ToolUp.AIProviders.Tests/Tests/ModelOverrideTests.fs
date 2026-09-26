// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Tests.ModelOverrideTests

// ─── Phase 661 — per-call model override, per shipped connector ────
//
// Offline, like `ProviderTestPack.toolSchemaHandOffTests`: no key, no
// network. Two things are pinned per connector.
//
//   1. `canServeModel` / `resolveCallModel` — the family check each
//      connector applies before naming a model on a call. The
//      request bytes for an HONOURED override are the bytes the wire
//      builders emit for that model id, which `ToolUp.AI.Wire.Tests`
//      already pins per model; what is new here is WHICH id reaches
//      the builder, and that a foreign id never does.
//   2. Every shipped connector implements `IAIProviderModelOverride`,
//      so the resolver's `SendStructuredMessageWith` reaches the
//      override rather than the fallback on all four.
//
// The live half — the override actually reaching the vendor — rides
// the env-gated per-provider packs when a key is present.

open Expecto
open ToolUp.Platform.AI

let private honoured (served: string, outcome: ModelOverrideOutcome) (expected: string) =
    Expect.equal served expected "served on the requested model"
    Expect.equal outcome (OverrideHonoured expected) "and reported as honoured"

let private fellBack (served: string, outcome: ModelOverrideOutcome) (configured: string) (requested: string) =
    Expect.equal served configured "served on the configured model"

    match outcome with
    | OverrideFellBack(r, s, reason) ->
        Expect.equal r requested "names the requested id"
        Expect.equal s configured "and the model that served"
        Expect.isNotEmpty reason "with a reason"
    | other -> failtestf "expected a fallback, got %A" other

let private configured (served: string, outcome: ModelOverrideOutcome) (expected: string) =
    Expect.equal served expected "the configured model"
    Expect.equal outcome (ConfiguredModel expected) "no override asked for"

let tests =
    testList "Phase 661 — per-call model override (offline, per connector)" [
        testCase "Claude serves claude-* ids and falls back on anything else"
        <| fun _ ->
            Expect.isTrue (ClaudeAIProvider.canServeModel "claude-sonnet-4-5") "claude family"
            Expect.isFalse (ClaudeAIProvider.canServeModel "gpt-4o-mini") "not gpt"
            Expect.isFalse (ClaudeAIProvider.canServeModel "models/gemini-2.5-flash") "not gemini"

            honoured
                (ClaudeAIProvider.resolveCallModel
                    "claude-sonnet-4-5"
                    (AIProviderCallOptions.forModel "claude-haiku-4-5-20251001"))
                "claude-haiku-4-5-20251001"

            fellBack
                (ClaudeAIProvider.resolveCallModel "claude-sonnet-4-5" (AIProviderCallOptions.forModel "gpt-4o-mini"))
                "claude-sonnet-4-5"
                "gpt-4o-mini"

            configured
                (ClaudeAIProvider.resolveCallModel "claude-sonnet-4-5" AIProviderCallOptions.none)
                "claude-sonnet-4-5"

        testCase "OpenAI serves any id that is not another vendor's"
        <| fun _ ->
            Expect.isTrue (OpenAIProvider.canServeModel "gpt-4o-mini") "gpt"
            Expect.isTrue (OpenAIProvider.canServeModel "o1-mini") "o-series"
            Expect.isTrue (OpenAIProvider.canServeModel "ft:gpt-4o-mini:acme::abc123") "a fine-tune id"
            Expect.isFalse (OpenAIProvider.canServeModel "claude-haiku-4-5-20251001") "not claude"
            Expect.isFalse (OpenAIProvider.canServeModel "gemini-2.5-flash") "not gemini"

            honoured
                (OpenAIProvider.resolveCallModel "gpt-4o" (AIProviderCallOptions.forModel "gpt-4o-mini"))
                "gpt-4o-mini"

            fellBack
                (OpenAIProvider.resolveCallModel "gpt-4o" (AIProviderCallOptions.forModel "claude-haiku-4-5-20251001"))
                "gpt-4o"
                "claude-haiku-4-5-20251001"

        testCase "Gemini serves gemini-*/gemma-* ids, prefixed or bare"
        <| fun _ ->
            Expect.isTrue (GeminiAIProvider.canServeModel "models/gemini-2.5-flash") "prefixed"
            Expect.isTrue (GeminiAIProvider.canServeModel "gemini-2.5-pro") "bare"
            Expect.isTrue (GeminiAIProvider.canServeModel "gemma-3-27b-it") "gemma"
            Expect.isFalse (GeminiAIProvider.canServeModel "gpt-4o-mini") "not gpt"

            honoured
                (GeminiAIProvider.resolveCallModel
                    "models/gemini-2.5-pro"
                    (AIProviderCallOptions.forModel "models/gemini-2.5-flash"))
                "models/gemini-2.5-flash"

            fellBack
                (GeminiAIProvider.resolveCallModel
                    "models/gemini-2.5-pro"
                    (AIProviderCallOptions.forModel "claude-haiku-4-5-20251001"))
                "models/gemini-2.5-pro"
                "claude-haiku-4-5-20251001"

        testCase "Copilot serves any deployment name that is not another vendor's id"
        <| fun _ ->
            Expect.isTrue (CopilotAIProvider.canServeModel "gpt-4o-mini") "a common deployment name"
            Expect.isTrue (CopilotAIProvider.canServeModel "my-triage-deployment") "an arbitrary deployment name"
            Expect.isFalse (CopilotAIProvider.canServeModel "claude-haiku-4-5-20251001") "not claude"

            honoured
                (CopilotAIProvider.resolveCallModel "gpt-4o" (AIProviderCallOptions.forModel "gpt-4o-mini"))
                "gpt-4o-mini"

            fellBack (CopilotAIProvider.resolveCallModel "gpt-4o" (AIProviderCallOptions.forModel "")) "gpt-4o" ""

        testCase "every shipped connector implements the override, so the resolver never hits the fallback on them"
        <| fun _ ->
            let providers = [
                "Claude", ClaudeAIProvider.createWithApiKeyAndModel "test-key" "claude-sonnet-4-5"
                "OpenAI", OpenAIProvider.createWithApiKeyAndModel "test-key" "gpt-4o"
                "Gemini", GeminiAIProvider.createWithApiKeyAndModel "test-key" "models/gemini-2.5-pro"
                "Copilot",
                CopilotAIProvider.createWithApiKeyAndModel "https://example.openai.azure.com" "test-key" "gpt-4o"
            ]

            for name, provider in providers do
                Expect.isTrue
                    (match provider with
                     | :? IAIProviderModelOverride -> true
                     | _ -> false)
                    $"{name} implements IAIProviderModelOverride"
    ]