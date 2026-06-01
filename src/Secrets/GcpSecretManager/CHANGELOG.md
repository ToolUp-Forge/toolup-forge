# Changelog — ToolUp.Secrets.GcpSecretManager

All notable changes to the `ToolUp.Secrets.GcpSecretManager` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [0.4.5] - 2026-06-01

- Initial release. Phase 2b: GCP Secret Manager `ISecretStore`
  companion. Closes the GCP gap in Phase 2a — the three cloud-secret
  managers (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault) now
  have a fourth peer for deployments targeting Google Cloud
  (Cloud Run / GKE / GCE / App Engine).
