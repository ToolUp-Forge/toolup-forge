# ToolUp.Storage.GoogleCloud

Google Cloud Storage `IBlobStorage` companion for `ToolUp.Platform`. Wraps the GCP SDK to back the platform's storage interface with GCS buckets; supports object versioning, customer-managed encryption keys, and bucket-level retention policies.

Configuration via Application Default Credentials, or an inline service-account JSON string. Production deployments typically pair with `GcsEncryptionAtRestValidator` to confirm encryption-at-rest is enabled.

## Credential rotation

**Depends on the credential model.** With Application Default Credentials (`CredentialsJson = None` — the `GOOGLE_APPLICATION_CREDENTIALS` path, gcloud auth, GCE metadata server, or GKE workload identity), rotation is **transparent**: the ADC chain refreshes tokens itself, no application change needed.

With an **inline service-account JSON** (`CredentialsJson = Some …`), the credential is static — the `StorageClient` pins it for the process lifetime, so a rolled key fails every call with `403` until restart. To survive rotation without a restart, construct via `create` with `CredentialsJsonProvider = Some f` (Phase 2c). `f ()` is read on each operation and the client is rebuilt **only when the resolved JSON changes** (a change-detection cache, not a per-call reconstruction); the closure typically closes over an `ISecretStore.GetSecret` read. `fromEnv` wires the static `None` path. Prefer workload identity / ADC over inline JSON where the platform allows it. See [`docs/operations/credential-rotation.md`](../../../docs/operations/credential-rotation.md).

The `blob_storage:gcs` health probe performs a **live authenticated list** (Phase 2c) against the `_platform` health prefix, so a rolled-out key surfaces as `Unhealthy` with the GCS `403` message within one probe cycle — the earlier `Exists`-based probe swallowed the `403` and read Healthy.

Set `AuditLog = Some log` on the config (Phase 2c) and every GCS call rejected `401` / `403` also records a **`BlobStorageAuthFailed`** audit event under the `_platform` scope, naming the companion, the bucket, the `IBlobStorage` operation, the status and a sanitised SDK message. The probe is the alarm; this is the trail that says when the rejections started and what they cost. Worth composing even on ADC, which is rotation-transparent but can still lose an IAM binding. `None` (the default, and what `fromEnv` wires) emits nothing.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
