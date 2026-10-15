# A connector declares whether it takes a credential

**Ships in:** ToolUp.Platform.Server (`CredentialNeed`, `IDeclaresCredentialNeed`, `CredentialNeed.declaredBy`; the
`DataIngestor` reads the secret store only for a connector that takes a credential), ToolUp.DataSources.Common
(`Credentials.resolveDeclared`), and every shipped `IDataSource` connector, each now declaring its need. Rides the
0.27.0 draft slot (Phase 1017). Additive: no existing type or member changes.

**Affected:** deployments whose secret store can be unavailable (Azure Key Vault behind a placeholder URL on a dev
machine, a DNS or auth outage in production); authors of their own `IDataSource` connectors.

## What changes

Until this release `DataIngestor` called `ISecretStore.GetSecret(scopeId, config.CredentialKey)` for EVERY connector
before `Connect`. The file connectors (CSV, Excel, Parquet) read the stored file through `IBlobStorage` and never use
the credential, but the read still happened, so an unreachable vault failed the upload with an unhandled exception
before the file was opened. A vault outage stopped all ingestion, including sources that would have read nothing.

A connector now states its need by implementing `IDeclaresCredentialNeed` beside `IDataSource`, on the
`IDeclaresPayloadFormat` pattern:

| `CredentialNeed` | What the ingestor does |
|---|---|
| `NoCredential` | Never reads the secret store. `ctx.Credential` is `None`. |
| `Required` | Reads the store. A missing or blank secret fails the run as `CredentialMissing <CredentialKey>` **before** `Connect` is called (the job handler treats it as a permanent failure, as before). |
| `Optional` | Reads the store; `None` is permitted. **The answer for a connector that declares nothing** — exactly the behaviour before this release. |

The shipped declarations: CSV, Excel, Parquet and `InMemoryDataSource` declare `NoCredential`. Athena, BigQuery,
Redshift, Snowflake, SQL, Synapse and Google Analytics declare `Optional`, because each one's need depends on its
configuration (an ambient AWS / Azure / Google credential chain, a key file, a connection string carrying its own
password, or Google Analytics' out-of-band `CredentialKey` escape hatch beside the OAuth substrate's own slot); each
connector's comment names its reason. None of them reads differently than before.

## What a deployment does

**Nothing.** A file ingest no longer touches the secret store; credentialed sources behave exactly as before.

## What a connector author does

Nothing is required: an undeclared connector reads as `Optional` and keeps its behaviour. To decouple a connector
that never uses a credential from secret-store availability, or to have a missing secret refused before `Connect`,
declare it:

```fsharp skip=fragment
type MyFileSource(storage: IBlobStorage) =
    interface IDeclaresCredentialNeed with
        member _.CredentialNeed = CredentialNeed.NoCredential

    interface IDataSource with
        // …
```

A connector that resolves its own credential through `ConnectorSupport` can follow the same declaration with
`Credentials.resolveDeclared source secretStore ctx`, which returns `Ok None` for `NoCredential` without a read,
`CredentialMissing` for an absent `Required` secret, and `Ok None` for an absent `Optional` one.

## Verification

`ToolUp.DataSources.Tests` → "Connector declarations": every shipped connector's declaration is explicit and
stable; a CSV ingest succeeds while the secret store throws on every read; a `Required` connector with no secret fails
as `CredentialMissing` without `Connect` being called; an undeclared connector still reads the store once.

## Rollback

Remove the `IDeclaresCredentialNeed` implementation from a connector; it reads as `Optional`, which is the previous
behaviour.
