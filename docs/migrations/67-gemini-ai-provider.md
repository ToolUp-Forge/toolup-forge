# Migration — Phase 67: Gemini AI Provider

**Shape:** Additive opt-in companion. No consumer migration required.

## What changes

A new NuGet companion `ToolUp.AIProviders.Gemini` ships alongside `ToolUp.AIProviders.Claude` and `ToolUp.AIProviders.OpenAI`. The companion implements `IAIProvider` against Google's Generative Language API (v1beta), supports the `gemini-2.5-*` and `gemini-1.5-*` model families, and is BYOK-capable via `ISecretStore` under the `_platform` scope key `GEMINI_API_KEY`.

No SDK interfaces changed. No existing-provider code touched. No consumer is required to do anything — consumers wishing to expose Gemini add the package reference and append a builder to the `DefaultAIProviderFactory.create` builders list.

## Diff to apply (opt-in)

A consumer that wants to offer Gemini alongside its existing providers adds:

```xml
<!-- Server csproj / fsproj -->
<PackageReference Include="ToolUp.AIProviders.Gemini" />
```

…and appends a builder entry to the deployment's compose step. The shape is the same as Claude / OpenAI:

```fsharp
open GeminiAIProvider

let geminiBuilder = {
    Descriptor = {
        Id = ProviderId
        DisplayName = "Google Gemini"
        SupportedModels = KnownModels
        DefaultModel = DefaultModel
        Capabilities = {
            AIProviderCapabilities.unknown with
                Streaming = true
                ToolUse = true
                Vision = true
                SupportsPromptCaching = true
                ProviderName = "google-gemini"
                Model = DefaultModel
        }
    }
    Build = fun apiKey model -> createWithApiKeyAndModel apiKey model
}

// Then in the existing builder list:
DefaultAIProviderFactory.create
    [ ClaudeAIProvider.builder
      OpenAIProvider.builder
      geminiBuilder ]    // append; existing entries unchanged
    providerProfile
    secretStore
    fallbackPolicy
    platformProvider
```

Store the API key under `_platform` scope, name `GEMINI_API_KEY`.

## Verification steps

- `dotnet build` of the consuming app remains clean (additive `<PackageReference>`).
- A user-configured Gemini `ProviderEntry` resolves via `IAIProviderFactory.Resolve` and serves a `models/gemini-2.5-flash` or `models/gemini-2.5-pro` completion.
- Streaming, tool use, and multimodal input round-trip in the same forge-internal shape as Claude / OpenAI for the same inputs (semantically equivalent — exact bytes differ per vendor wire format).
- `/health/ready` includes a `ai_provider:google-gemini` probe verifying the secret-store key is set.

## Rollback

Remove the `<PackageReference>` + the appended builder entry. No data, schema, or stored-config migration is involved — the SDK's `IProviderProfile` quietly ignores entries whose `ProviderId` no longer matches a registered builder (surfaced as `UnknownProvider` per the existing `ProviderResolutionError` contract).
