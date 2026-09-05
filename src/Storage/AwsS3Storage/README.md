# ToolUp.Storage.AwsS3

AWS S3 `IBlobStorage` companion for `ToolUp.Platform`. Wraps the AWS SDK to back the platform's storage interface with S3 buckets; supports versioning, server-side encryption (SSE-S3 / SSE-KMS), and bucket-level Object Lock (used by the Phase 9g audit-replication WORM path).

Configuration is read from standard AWS resolution (environment, profile, IMDS). Production deployments typically pair with `AwsS3EncryptionAtRestValidator` to confirm encryption-at-rest is enabled at the bucket level.

## Credential rotation

**Ambient — rotation transparent.** Credentials flow through the AWS SDK's default chain (env vars, shared-credentials file, EC2 instance profile, ECS/EKS task role). The SDK refreshes IMDS / role credentials itself, so an out-of-band key rotation is picked up without any application change or restart — this companion holds no static key. It therefore ships **no** credential-provider seam (unlike the Azure / GCS companions, whose static credentials need one). See [`docs/operations/credential-rotation.md`](../../../docs/operations/credential-rotation.md).

The `blob_storage:aws-s3` health probe performs a **live authenticated list** (Phase 2c) against the `_platform` health prefix, so a revoked role, an IAM-policy change, or a bucket rename surfaces as `Unhealthy` with the S3 `403` message within one probe cycle — the earlier `Exists`-based probe swallowed the `403` and read Healthy.

Set `AuditLog = Some log` on the config (Phase 2c) and every S3 call rejected `401` / `403` also records a **`BlobStorageAuthFailed`** audit event under the `_platform` scope, naming the companion, the bucket, the `IBlobStorage` operation, the status and a sanitised SDK message. The probe is the alarm; this is the trail that says when the rejections started and what they cost. `None` (the default, and what `fromEnv` wires) emits nothing.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
