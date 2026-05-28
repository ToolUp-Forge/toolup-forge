# ToolUp.Secrets.HashiCorpVault

HashiCorp Vault `ISecretStore` companion for `ToolUp.Platform`. Pure BCL `HttpClient` against Vault's KV v2 secrets engine — no vendor SDK dependency.

Token auth in the MVP (`VAULT_TOKEN`); AppRole / Kubernetes / OIDC auth methods are follow-up enhancements (each issues a Vault token that the operator pipes into `VAULT_TOKEN` via their orchestrator's secret-injection flow). Vault Enterprise namespaces supported via the optional `VAULT_NAMESPACE` env var.

## Minimum Vault policy

```hcl
path "secret/data/toolup/*" {
  capabilities = ["create", "read", "update", "delete"]
}

path "secret/metadata/toolup/*" {
  capabilities = ["list", "delete"]
}
```

`secret/` is the default KV v2 mount path; if the deployment uses a different mount, swap the prefix above and set `VaultConfig.MountPath` accordingly. The companion assumes the configured mount runs KV v2; KV v1 mounts surface as 404s on data calls.

## Activation

Set in the deployment's environment:

```
TOOLUP_SECRET_STORE=vault
VAULT_ADDR=https://vault.example.com:8200
VAULT_TOKEN=<token>
VAULT_NAMESPACE=<namespace>    # Vault Enterprise only; omit otherwise
```

## Soft-delete semantics

`DeleteSecret` issues `DELETE /v1/{mount}/metadata/toolup/{scope}/{key}`, which wipes ALL versions of the secret and its metadata — `GetSecret` returns 404 immediately, satisfying the `ISecretStore` "delete then get returns None" contract. Vault's per-version soft-delete (which keeps deleted versions recoverable for a configurable retention period) lives on the `/data/` path, not the `/metadata/` path, and is bypassed here in favour of the simpler full-wipe behaviour. A future enhancement could expose per-version delete + restore as additional methods if a deployment requires it.

Licensed under Apache-2.0. Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
