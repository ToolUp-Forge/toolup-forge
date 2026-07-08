# ToolUp.AIProviders.Copilot

Azure OpenAI ("Microsoft Copilot") `IAIProvider` implementation for the `ToolUp.AI` companion.
Targets the Azure OpenAI chat-completions endpoint
(`{endpoint}/openai/deployments/{deployment}/chat/completions`), with two authentication modes:
a static `api-key` header (resolved per call from the injected `ISecretStore`, scoped to
`_platform`) or Microsoft Entra bearer tokens.

Wires into a deployment via `DefaultAIProviderFactory.create`. See the package's parent docs for
the full integration walkthrough and provider-authoring contract.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
