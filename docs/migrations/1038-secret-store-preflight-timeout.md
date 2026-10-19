# A cloud secret store survives its cold preflight read

**Ships in:** ToolUp.Platform.Core (`IConfigValidator.remoteSecretStoreTimeout`), ToolUp.Platform.Server
(`ServerApp.withSecretStorePreflightTimeout`, `ServerApp.SecretStorePreflightTimeout`,
`ConfigValidator.secretStorePreflightTimeout`; `compose` gains a trailing `secretStorePreflightTimeout` argument and
`ComposeConfigValidators.registerFirstPartyConfigValidators` a `secretStorePreflightTimeout` argument after
`secretStore`) (Phase 1038).

**Affected:** an app that supplies a network-backed secret store through `ServerApp.withSecretStore` (Azure Key Vault,
AWS Secrets Manager, GCP Secret Manager, Vault). Also code that builds `ServerApp` as a full record literal, and a direct
caller of the low-level `compose` or `registerFirstPartyConfigValidators`. An app that supplies no store is unchanged.

## What changes

**1. The secret-store preflight's timeout follows the store's kind.** The `secret-store` validator used to run under
the fixed 5 s `IConfigValidator.defaultTimeout`. A cloud store's first read is cold: it pays the managed-identity token
acquisition plus the vault round trip. A Key Vault-backed store, read through a system-assigned managed identity on a
cold B1 App Service container, measured 6.1-6.8 s over four cold boots. That refused a correctly provisioned deployment.

Now a store supplied through `ServerApp.withSecretStore` is treated as network-backed and gets
`IConfigValidator.remoteSecretStoreTimeout` (10 s). The SDK's own in-process `FileSecretStore` keeps 5 s. Ten seconds
is the preflight aggregator's overall ceiling: it clamps any declared timeout above that.

**2. The composition root can override it.** `ServerApp.withSecretStorePreflightTimeout timeout` sets the bound
explicitly, over the store's kind. Use it to fail faster, for example. A value above the aggregator's ten-second budget
is clamped. A zero or negative value is refused when the builder runs.

## Migrating

- **An app on a cloud secret store:** nothing. The default is enough to boot on a cold read. Remove
  `TOOLUP_SKIP_PREFLIGHT` if it was set only to get past this probe.
- **A full `ServerApp` record literal:** add `SecretStorePreflightTimeout = None`, or build from `ServerApp.empty`.
- **A direct `compose` call:** append `None` (or `Some timeout`) after the `secretStore` argument.
- **A direct `registerFirstPartyConfigValidators` call:** pass the timeout after `secretStore`. Use
  `ConfigValidator.secretStorePreflightTimeout storeSupplied None` to get what `compose` would choose.

## Verifying

Boot against the cloud store on a cold instance with `TOOLUP_SKIP_PREFLIGHT` unset. The preflight logs
`[preflight] secret-store: Ok`. The pinned cases, slow-but-inside-budget, over-budget, local default and override, are
in `src/ToolUp.Platform.Tests/InProcess/ConfigValidatorAggregatorTests.fs`.

## Rolling back

Revert the Phase 1038 commit. A `ServerApp.withSecretStorePreflightTimeout` call then no longer compiles: remove it.
A supplied store goes back to the 5 s bound.
