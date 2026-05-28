# ToolUp.AIProviders.OpenAI

OpenAI `IAIProvider` implementation for the `ToolUp.AI` companion. BYOK-capable — API keys are resolved per call from the injected `ISecretStore`, scoped to `_platform`. Supports `chat.completions` with multi-turn tool calling and SSE streaming (`stream_options.include_usage` is honoured for accurate token reporting).

Wires into a deployment via `DefaultAIProviderFactory.create`. See the package's parent docs for the full integration walkthrough and provider-authoring contract.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
