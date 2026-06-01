# ToolUp.Secrets.GcpSecretManager

GCP Secret Manager `ISecretStore` companion for `ToolUp.Platform`. Reads, writes, and lists secrets per call against a configured GCP project; supports scope-prefixed secret-ID conventions and a per-key `name@versionId` version-pin convention.

Credentials flow through Google's Application Default Credentials (ADC) chain — on Cloud Run / GKE / GCE / App Engine the attached workload-identity-bound service account; off-GCP the path in `GOOGLE_APPLICATION_CREDENTIALS` pointing at a service-account JSON key file. Secrets are never cached in process beyond the call boundary — rotation in Secret Manager is picked up on next request.

## Minimum IAM policy

```yaml
bindings:
- members:
  - serviceAccount:<service-account>@<project>.iam.gserviceaccount.com
  role: roles/secretmanager.secretAccessor   # read (versions.access)
- members:
  - serviceAccount:<service-account>@<project>.iam.gserviceaccount.com
  role: roles/secretmanager.secretVersionAdder   # write (secrets.create + versions.add)
- members:
  - serviceAccount:<service-account>@<project>.iam.gserviceaccount.com
  role: roles/secretmanager.admin   # delete + list (or use a custom role with the two perms)
```

`secrets.list` is project-wide by Secret Manager design — the companion filters server-side by name prefix (`name:toolup_<scopeId>_*`).

## Version pinning

`GetSecret name` resolves the `latest` version (the canonical alias for the highest-numbered enabled version). Callers that need a pinned version pass `name@versionId`:

```fsharp
// Latest version (default)
let! current = secretStore.GetSecret("_platform", "db-password")

// Pinned to version 3 — useful during a rotation where the
// previous-version reader needs to keep working until the cutover.
let! previous = secretStore.GetSecret("_platform", "db-password@3")
```

`SetSecret` always creates a new version; the `@version` suffix is stripped on write because version IDs are assigned by Secret Manager, not the caller.

## Activation

Set in the deployment's environment:

```
TOOLUP_SECRET_STORE=gcp-secret-manager
TOOLUP_GCP_PROJECT_ID=my-project-12345
```

Off-GCP additionally set:

```
GOOGLE_APPLICATION_CREDENTIALS=/path/to/service-account.json
```

Licensed under Apache-2.0. Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
