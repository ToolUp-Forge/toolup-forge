# ToolUp.Storage.Azure

Azure Blob Storage `IBlobStorage` companion for `ToolUp.Platform`. Wraps `Azure.Storage.Blobs` to back the platform's storage interface with Azure containers; supports versioning, server-side encryption, and immutability policies.

Configuration via standard Azure resolution (`DefaultAzureCredential` — env vars, managed identity, etc.). Production deployments typically pair with `AzureBlobEncryptionAtRestValidator` to confirm encryption-at-rest is enabled.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
