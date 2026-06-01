# Phase 2b — `ToolUp.Secrets.GcpSecretManager` companion

## What changes

A new cloud-secret-manager companion ships at `src/Secrets/GcpSecretManager/`. It implements `ISecretStore` over Google Cloud Secret Manager via the `Google.Cloud.SecretManager.V1` SDK, closing the GCP gap in [Phase 2a](../../src/Secrets/) (which shipped Azure Key Vault, AWS Secrets Manager, and HashiCorp Vault). Deployments targeting Cloud Run / GKE / GCE / App Engine can now wire a managed-secret store without falling back to `EncryptedSecretStore` over `FileSecretStore`.

The composition root's existing `TOOLUP_SECRET_STORE` env-driven switch gains one new value, `gcp-secret-manager`. The SDK's `EncryptedSecretStoreModeValidator` (Phase 6l.E) recognises that value as cloud-KMS-backed and skips the master-key requirement, matching the existing Azure / AWS / Vault behaviour. The SDK's `SecretStore.fromEnv` resolver helper is unchanged in shape — consumers thread one additional `CloudSecretStoreResolver` entry through the helper to wire the companion in.

No consumer-side behaviour changes by default: the new companion is opt-in via the env switch + `<PackageReference>`.

## Diff to apply

### `Directory.Packages.props` (CPM consumer)

Add the companion to the consumer's `Directory.Packages.props` if it doesn't already arrive via the `ToolUp.Sdk` meta-manifest:

```xml
<PackageVersion Include="ToolUp.Secrets.GcpSecretManager" Version="$(ToolUpSdkVersion)" />
```

### Consumer's `Server.fsproj` (or wherever cloud-companion `PackageReference`s live)

Add the package reference next to the existing cloud-secret-manager companions:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Secrets.AzureKeyVault" />
  <PackageReference Include="ToolUp.Secrets.AwsSecretsManager" />
  <PackageReference Include="ToolUp.Secrets.HashiCorpVault" />
+ <PackageReference Include="ToolUp.Secrets.GcpSecretManager" />
</ItemGroup>
```

### Consumer's composition root (e.g. `Server.fs`)

Add the GCP resolver to the `CloudSecretStoreResolver` list passed into `SecretStore.fromEnv`:

```fsharp
open ToolUp.Platform.SecretStore

let secretStore =
    SecretStore.fromEnv logger [
        { Name = "azure-key-vault"; Resolve = ToolUp.Secrets.AzureKeyVault.fromEnv }
        { Name = "aws-secrets-manager"; Resolve = ToolUp.Secrets.AwsSecretsManager.fromEnv }
        { Name = "vault"; Resolve = ToolUp.Secrets.HashiCorpVault.fromEnv }
+       { Name = "gcp-secret-manager"; Resolve = ToolUp.Secrets.GcpSecretManager.fromEnv }
    ]
```

The resolver list is order-independent (matched by `Name` case-insensitively); append at the tail or wherever feels natural alongside the existing three.

### Deployment environment

Set the activating env vars in the deployment's environment configuration:

```
TOOLUP_SECRET_STORE=gcp-secret-manager
TOOLUP_GCP_PROJECT_ID=<your-gcp-project-id>
```

On Cloud Run / GKE / GCE / App Engine, ADC auto-resolves to the attached workload-identity-bound service account — no additional env vars are needed. Off-GCP (dev shells, cross-cloud deploys) additionally set `GOOGLE_APPLICATION_CREDENTIALS` to a service-account JSON key file path.

The service account needs:

- `roles/secretmanager.secretAccessor` — read (`secretmanager.versions.access`)
- `roles/secretmanager.secretVersionAdder` — write (`secretmanager.secrets.create` + `secretmanager.versions.add`)
- A custom role granting `secretmanager.secrets.delete` + `secretmanager.secrets.list` — or use `roles/secretmanager.admin` for full access.

Full IAM detail in the companion's [README](../../src/Secrets/GcpSecretManager/README.md).

## Verification steps

After applying the diff above:

1. **`dotnet restore`** — confirms the new `<PackageReference>` resolves cleanly against the configured feed.
2. **`dotnet build`** — confirms the resolver list still type-checks (the new `CloudSecretStoreResolver` entry must satisfy the record type).
3. **Startup log diff** — with `TOOLUP_SECRET_STORE=gcp-secret-manager` + `TOOLUP_GCP_PROJECT_ID` set, the SDK's `SecretStore.fromEnv` helper emits `Secret store: gcp-secret-manager` (Info-level). Without the env vars set, the deployment continues to use whatever default the composition root chose (typically `EncryptedSecretStore` over `FileSecretStore`).
4. **`EncryptedSecretStoreModeValidator` passes** with `TOOLUP_SECRETS_MASTER_KEY` unset when `TOOLUP_SECRET_STORE=gcp-secret-manager` — the validator recognises the new value as cloud-KMS-backed and skips the master-key gate. Verify in the `HealthMonitorUI` admin tab or `/dev/inspect` Validators panel.
5. **End-to-end smoke test**: with a real GCP project + service account configured, write a secret via `secretStore.SetSecret("_platform", "smoke-test", "hello")` and read it back via `secretStore.GetSecret("_platform", "smoke-test")` — should return `Some "hello"`. Then `DeleteSecret` and re-`GetSecret` — should return `None`.
6. **Contract test pack**: with `TOOLUP_GCP_PROJECT_ID` set against a CI-dedicated project, the `ISecretStoreContract` pack passes against the new binding at `src/ToolUp.Platform.Tests/InProcess/GcpSecretManagerSecretStoreTests.fs`. Without the env var the pack emits a single skipped (`pending`) test rather than green-by-default — a missing CI-side project ID is visible in the test report.

## Rollback

Remove the `<PackageReference>` and the resolver-list entry; unset `TOOLUP_SECRET_STORE` (or set it to `encrypted` / one of the other cloud values). The composition root falls back to its prior store choice without further code change. No data migration required — secrets stored via the companion live in GCP Secret Manager, not the SDK's blob storage; reverting the composition leaves them in GCP untouched.

## Risks

- **Version-pin breakage on `name@versionId` keys.** Callers passing keys containing a literal `@` will see surprising behaviour because the companion treats `@` as the version-pin separator (e.g. `GetSecret "email@example.com"` parses to `(name="email", version="example.com")`). The other cloud companions don't reserve `@` as a separator; consumers porting keys across vendors should normalise or avoid embedded `@`.
- **Project-ID conflation.** `TOOLUP_GCP_PROJECT_ID` is read at composition time; rotating between projects requires a restart. Mirrors AWS region / Azure vault URL behaviour.
- **No soft-delete.** Unlike Azure Key Vault (90-day soft-delete) and AWS Secrets Manager (7-30 day scheduled deletion), GCP Secret Manager removes secrets irreversibly. There is no recovery path for an accidental `DeleteSecret`. Operators relying on the Azure / AWS soft-delete safety net should establish backup or audit-log-based recovery procedures before depending on the GCP companion in production.
