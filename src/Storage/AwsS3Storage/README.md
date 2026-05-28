# ToolUp.Storage.AwsS3

AWS S3 `IBlobStorage` companion for `ToolUp.Platform`. Wraps the AWS SDK to back the platform's storage interface with S3 buckets; supports versioning, server-side encryption (SSE-S3 / SSE-KMS), and bucket-level Object Lock (used by the Phase 9g audit-replication WORM path).

Configuration is read from standard AWS resolution (environment, profile, IMDS). Production deployments typically pair with `AwsS3EncryptionAtRestValidator` to confirm encryption-at-rest is enabled at the bucket level.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
