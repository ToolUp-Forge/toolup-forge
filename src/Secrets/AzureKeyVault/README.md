# ToolUp.Secrets.AzureKeyVault

Azure Key Vault `ISecretStore` companion for `ToolUp.Platform`. Reads secrets per call from a configured vault; supports versioned secret resolution and scope-specific name conventions.

Configuration via `DefaultAzureCredential` (env vars, managed identity, etc.). Secrets are never cached in process beyond the call boundary — rotation at the vault is picked up on next request.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
