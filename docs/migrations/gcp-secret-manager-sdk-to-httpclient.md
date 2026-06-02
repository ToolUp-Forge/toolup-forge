# Migration — `ToolUp.Secrets.GcpSecretManager` Google-SDK → HttpClient + REST rewrite

## What changes

The `ToolUp.Secrets.GcpSecretManager` companion is reimplemented over BCL `HttpClient` against the Secret Manager REST API at `secretmanager.googleapis.com/v1`, replacing the `Google.Cloud.SecretManager.V1` SDK. The public companion API surface is byte-identical — `create`, `fromEnv`, `GcpSecretManagerConfig`, and the `ISecretStore` contract behave the same — so no consumer-side code change is required.

The change conforms to the forge "Companion-authoring guide" convention in [`toolup-forge/CLAUDE.md`](../../CLAUDE.md): *"For HTTP-shaped companions: use BCL `HttpClient` rather than a vendor SDK where the API is permissive."* The Secret Manager REST API is permissive (well-documented OpenAPI surface, JSON-shaped requests, OAuth2 bearer-token auth) and the auth piece — service-account JWT + token exchange — is small enough to maintain in-tree.

## Why

The `Google.Cloud.SecretManager.V1` SDK pulled ~20 transitive packages into every consumer that referenced the companion:

```
Google.Api.CommonProtos                                    2.17.0
Google.Api.Gax                                             4.12.1
Google.Api.Gax.Grpc                                        4.12.1
Google.Apis                                                1.72.0
Google.Apis.Auth                                           1.72.0
Google.Apis.Core                                           1.72.0
Google.Cloud.Iam.V1                                        3.5.0
Google.Cloud.Location                                      2.4.0
Google.Protobuf                                            3.31.1
Grpc.Auth                                                  2.71.0
Grpc.Core.Api                                              2.71.0
Grpc.Net.Client                                            2.71.0
Grpc.Net.Common                                            2.71.0
Microsoft.Bcl.AsyncInterfaces                              6.0.0
Microsoft.Extensions.DependencyInjection.Abstractions      6.0.0
Microsoft.Extensions.Logging.Abstractions                  6.0.0
Newtonsoft.Json                                            13.0.4
System.CodeDom                                             7.0.0
System.Management                                          7.0.2
```

After the rewrite the companion's direct dependency set is just `FSharp.Core` + `ToolUp.Platform.Core` — no Google-namespace packages, no gRPC, no `Newtonsoft.Json`, no `Google.Protobuf`. Consumers of the companion no longer pay the supply-chain surface for those packages unless another companion (e.g. `ToolUp.Storage.GoogleCloud`) brings them in independently.

## What stays the same

- **Public API surface.** `ToolUp.Secrets.GcpSecretManager.create config` and `ToolUp.Secrets.GcpSecretManager.fromEnv ()` are byte-identical signatures.
- **`GcpSecretManagerConfig` record.** Still `{ ProjectId: string }`.
- **Activation env vars.** `TOOLUP_SECRET_STORE=gcp-secret-manager` + `TOOLUP_GCP_PROJECT_ID` continue to work exactly as before.
- **Credential resolution env contract.** Off-GCP, `GOOGLE_APPLICATION_CREDENTIALS` still points at a service-account JSON key file. On GCP compute (Cloud Run / GKE / GCE / App Engine), no credentials env var is needed — the companion uses the metadata server at `http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/token`.
- **Secret-name convention.** Still `toolup_{scopeId}_{key}` under `projects/{projectId}/secrets/...`. Tests, IAM bindings, and audit-log queries written against the existing convention work unchanged.
- **Version-pin convention.** `name@versionId` (e.g. `"db-password@3"` or default `"latest"`) is preserved.
- **`EncryptedSecretStoreModeValidator` exemption.** The `gcp-secret-manager` mode is still recognised as cloud-KMS-backed; the master-key gate is still skipped.

## What changes

- **Credential chain is narrower.** The Google SDK's Application Default Credentials (ADC) chain probes several sources (gcloud CLI cache, well-known config paths, etc.) before falling back to the metadata server. The rewrite implements only the two modes that matter for ToolUp deployment shapes: `GOOGLE_APPLICATION_CREDENTIALS` pointing at a service-account JSON file, and the GCE metadata server. If a deployment relied on a gcloud-CLI-cached user credential, set `GOOGLE_APPLICATION_CREDENTIALS` explicitly.
- **In-process OAuth token cache.** The companion caches the access token in-process (refreshed 30s before expiry) behind a `SemaphoreSlim`. Per-call cost is one cached-token read + one REST round-trip for ~55 minutes per process. Previously the Google SDK managed the token cache internally.
- **Error surfaces are HTTP status codes, not `RpcException`.** Direct callers who match on `RpcException` (none should exist — the companion is consumed only through `ISecretStore`) would need to update; the `ISecretStore` interface surface (`Async<string option>`, `Async<Result<unit, string>>`) is unchanged.

## Diff to apply (per consumer)

**None.** This is a pure substitution behind a stable API surface. Existing consumers compile + run unchanged after the package upgrade. The deployment env vars (`TOOLUP_SECRET_STORE`, `TOOLUP_GCP_PROJECT_ID`, optional `GOOGLE_APPLICATION_CREDENTIALS`) are unchanged.

## Verification steps

1. **`dotnet build`** clean against the consumer's solution after upgrading `ToolUp.Secrets.GcpSecretManager`.
2. **`dotnet list <consumer.fsproj> package --include-transitive`** no longer shows `Google.*`, `Grpc.*`, `Google.Protobuf`, or `Newtonsoft.Json 13.0.4` arriving via `ToolUp.Secrets.GcpSecretManager`. (Other companions like `ToolUp.Storage.GoogleCloud` may still bring them in; this verifies the secret-manager package isn't the source.)
3. **Startup log diff.** With `TOOLUP_SECRET_STORE=gcp-secret-manager` + `TOOLUP_GCP_PROJECT_ID` set, the SDK still emits `Secret store: gcp-secret-manager` (Info-level). No log-shape change.
4. **End-to-end smoke test.** Against a CI-dedicated GCP project, `secretStore.SetSecret("_platform", "smoke-test", "hello")` → `secretStore.GetSecret("_platform", "smoke-test")` returns `Some "hello"`; `DeleteSecret` then `GetSecret` returns `None`. Same as before.
5. **Contract pack.** With `TOOLUP_GCP_PROJECT_ID` set, `src/ToolUp.Platform.Tests/InProcess/GcpSecretManagerSecretStoreTests.fs` runs the full `ISecretStoreContract` pack against the rewritten binding (9 cases — Get-missing, Set+Get, overwrite, delete, idempotent-delete, list-empty, list-populated, scope-isolation × 2). Without the env var the pack still emits a single skipped (`pending`) test so a missing CI-side project ID is visible.

## Rollback

Pin `ToolUp.Secrets.GcpSecretManager` to the previous SDK-based version in `Directory.Packages.props`. No code change required. The on-disk secret-name format and IAM contract are unchanged across versions, so the rollback round-trips against any secrets created under either binding.

## Risks

- **gcloud-CLI-cached user credentials no longer work.** If a developer relied on `gcloud auth application-default login` populating `~/.config/gcloud/application_default_credentials.json`, the rewrite doesn't probe that path. Set `GOOGLE_APPLICATION_CREDENTIALS` to point at the file explicitly, or use a service-account JSON key directly.
- **Metadata-server endpoint hard-coded.** The companion targets `metadata.google.internal` (the canonical hostname; aliases to `169.254.169.254` link-local on GCE). Deployments running on non-GCP hosts that masquerade as GCE through a custom metadata proxy would need to set `GOOGLE_APPLICATION_CREDENTIALS` instead.
- **No automatic retry on transient 5xx.** The previous SDK's gRPC client applied automatic retries on `Unavailable` etc. The rewrite passes HTTP errors straight through to the `ISecretStore.Set/Delete` `Error` surface. Operationally this is the same shape as the Azure / AWS / Vault peers (none of them retry inside the companion either); the gRPC SDK was the outlier.
- **Single `HttpClient` per store instance.** Steady-state is identical to the previous binding (one connection-pooled HTTP client; no per-call socket churn). Composition roots that instantiate the store once per process (the documented pattern) see no behaviour change.
