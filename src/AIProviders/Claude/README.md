# ToolUp.AIProviders.Claude

Claude (Anthropic) `IAIProvider` implementation for the `ToolUp.AI` companion. BYOK-capable — API keys are resolved per call from the injected `ISecretStore`, scoped to `_platform`. Supports the standard `messages` endpoint plus prompt caching, multi-turn tool calling, and SSE streaming.

Wires into a deployment via `DefaultAIProviderFactory.create`. See the package's parent docs for the full integration walkthrough and provider-authoring contract.

Client tier ships separately as `ToolUp.AIProviders.Claude.Client` — a Fable-only package containing the typed Claude brand glyph (`ToolUp.AIProviders.Claude.Icons.claude`). Deployments that brand their AI assistant as Claude add a `<PackageReference>` to the Client package alongside this Server package.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
