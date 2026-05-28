# ToolUp.Storage.GoogleCloud

Google Cloud Storage `IBlobStorage` companion for `ToolUp.Platform`. Wraps the GCP SDK to back the platform's storage interface with GCS buckets; supports object versioning, customer-managed encryption keys, and bucket-level retention policies.

Configuration via application default credentials. Production deployments typically pair with `GcsEncryptionAtRestValidator` to confirm encryption-at-rest is enabled.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
