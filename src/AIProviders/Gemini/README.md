# ToolUp.AIProviders.Gemini

Google Gemini `IAIProvider` implementation for the `ToolUp.AI` companion. BYOK-capable — API keys are resolved per call from the injected `ISecretStore`, scoped to `_platform`. Targets the Generative Language API (`generativelanguage.googleapis.com`, v1beta), with multi-turn tool calling, SSE streaming via `streamGenerateContent`, and structured output via `responseSchema`.

Wires into a deployment via `DefaultAIProviderFactory.create`. See the package's parent docs for the full integration walkthrough and provider-authoring contract.

Vertex AI managed endpoints are out of scope for this package — they live behind GCP IAM rather than a pasted API key. A separate `ToolUp.AIProviders.GoogleVertex` companion is the natural follow-on if a deployment needs them.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
