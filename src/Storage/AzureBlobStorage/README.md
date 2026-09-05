# ToolUp.Storage.Azure

Azure Blob Storage `IBlobStorage` companion for `ToolUp.Platform`. Wraps `Azure.Storage.Blobs` to back the platform's storage interface with Azure containers; supports versioning, server-side encryption, and immutability policies.

Configuration via an Azure Storage connection string. Production deployments typically pair with `AzureBlobEncryptionAtRestValidator` to confirm encryption-at-rest is enabled.

## Credential rotation

**Static credential — provider seam or restart.** The connection string embeds an `AccountKey` (or SAS). Built once, the `BlobServiceClient` pins that key for the process lifetime: an out-of-band `AccountKey` regeneration or SAS expiry leaves the cached client failing every call with `403` until the process restarts.

To survive rotation without a restart, construct via `create` with `ConnectionStringProvider = Some f` (Phase 2c). `f ()` is read on each operation and the client is rebuilt **only when the resolved connection string changes** (a change-detection cache, not a per-call reconstruction). The closure typically closes over an `ISecretStore.GetSecret` read, so the rotated key is picked up on the next call. `fromEnv` wires the static `None` path (today's behaviour). See [`docs/operations/credential-rotation.md`](../../../docs/operations/credential-rotation.md).

The `blob_storage:azure` health probe performs a **live authenticated list** (Phase 2c) against the `_platform` health prefix, so a rotated-out key surfaces as `Unhealthy` with the Azure `403` message within one probe cycle — the earlier `Exists`-based probe swallowed the `403` and read Healthy.

Set `AuditLog = Some log` on the config (Phase 2c) and every Azure call rejected `401` / `403` also records a **`BlobStorageAuthFailed`** audit event under the `_platform` scope, naming the companion, the root container, the `IBlobStorage` operation, the status and a sanitised SDK message (an `AccountKey` echoed back by the SDK is redacted before it reaches the row). The probe is the alarm; this is the trail that says when the rejections started and what they cost — most valuable on a static connection string, which cannot recover without a restart. `None` (the default, and what `fromEnv` wires) emits nothing.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
