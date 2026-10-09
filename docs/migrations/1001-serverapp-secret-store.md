# An app supplies ServerApp its secret store

**Ships in:** ToolUp.Platform.Server (`ServerApp.withSecretStore`, `ServerApp.SecretStore`, `composeSecretStore`,
`SecretStore.declaresEncryptionAtRest`; `compose` gains a trailing `secretStore` argument), ToolUp.Secrets.AzureKeyVault
(`AzureKeyVaultSecretStore(client: SecretClient)`, `createWithClient`). Ships in 0.25.1 (developed in the 0.25.0 draft) (Phase 1001).

**Affected:** an app that selects a KMS-backed secret store (Azure Key Vault, AWS Secrets Manager, GCP Secret Manager,
Vault) and today boots only with `TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1`; code that builds `ServerApp` as a full record
literal; a direct caller of the low-level `compose`. An app that supplies no store is unchanged.

## What changes

**1. ServerApp composes the app's store.** Until now `compose` built a raw `FileSecretStore` unconditionally. Every
preflight validator, the webhook, notification, OAuth refresher, provider-OAuth and data-ingestion stores, and the
DI-registered `ISecretStore` received it. A vault registered in DI was never seen by the platform, so its own stores
wrote `secrets*.json`. `ServerApp.withSecretStore store` now replaces that default everywhere. `withSecretResilience`
still applies, wrapping the supplied store.

**2. One route, and how it meets the environment.** ServerApp never reads `TOOLUP_SECRET_STORE` itself.
`SecretStore.fromEnv` stays the environment-driven selector. Pipe its result in:

```fsharp skip=fragment
app
|> ServerApp.withSecretStore (
    SecretStore.fromEnv logger [ { Name = "azure-key-vault"; Resolve = ToolUp.Secrets.AzureKeyVault.fromEnv } ]
)
```

If the environment names one backend and the app supplies another, the supplied store is the one composed, and the
at-rest preflight judges what that store declares. A spelling never overrides a declaration: a supplied
`FileSecretStore` is refused by `secret-store-at-rest-posture` whatever `TOOLUP_SECRET_STORE` says.

**3. The preflight reads the composed store's declaration.** `encrypted-secret-store-mode` and
`oauth-secret-encryption-mode` now also pass when the composed store declares `EncryptsAtRest` through
`ISecretStoreAtRestPosture`, as `secret-store-at-rest-posture` already did. The resilience wrapper forwards the
declaration, so a wrapped vault reads as encrypting and a wrapped `FileSecretStore` reads as plaintext. A vault-selecting
app passes all three checks with no acknowledgement, no master key and no environment switch.

**Trust model.** The validators take a store's declared posture at its word. A custom `ISecretStore` that declares
`EncryptsAtRest` passes all three checks, so it must declare it only when it is true. That claim is the supplier's
responsibility. Every shipped KMS companion declares it, and `FileSecretStore` declares plaintext.

**4. A Key Vault test seam.** `AzureKeyVaultSecretStore(client)` / `createWithClient client` accept a `SecretClient`:
a custom credential or `SecretClientOptions`, or a test double (the Azure SDK's `SecretClient` is substitutable). The
`AzureKeyVaultConfig` constructor and `create` are unchanged.

## Migrating

- **A vault-selecting app:** add `ServerApp.withSecretStore` with the vault. Then remove
  `TOOLUP_ACCEPT_PLAINTEXT_SECRETS` / `AcceptPlaintextSecretsWhenAuthRequired` if they were set only for this.
- **A full `ServerApp` record literal:** add `SecretStore = None`, or build from `ServerApp.empty`.
- **A direct `compose` call:** append `None` (or `Some store`) after the `secretResilience` argument.

## Verifying

Boot with the acknowledgement unset. The preflight logs `[preflight] secret-store-at-rest-posture: Ok`,
`encrypted-secret-store-mode: Ok` and, with connector OAuth on, `oauth-secret-encryption-mode: Ok`. No `secrets*.json`
appears beside the process. The pinned cases are in `src/ToolUp.Platform.Tests/InProcess/ServerAppSecretStoreTests.fs`.

## Rolling back

Drop the `withSecretStore` call. The app then composes the `FileSecretStore` default again, and an authenticated
deployment needs the plaintext acknowledgement to boot, exactly as before.
