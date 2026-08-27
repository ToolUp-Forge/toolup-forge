# ToolUp.DataSources.Synapse

An `IDataSource` companion for **Azure Synapse Analytics**, over the Synapse SQL endpoint — serverless
or dedicated — using `Microsoft.Data.SqlClient`. The same connector works against Azure SQL Database,
which speaks the same protocol and catalogue.

Production-ready. Stateless between calls (portability rule 4): every method opens a connection, uses
it and disposes it (SqlClient pools underneath), and the credential is re-read through the
`ISecretStore` thunk on **every** call, so a rotated password or a re-minted AAD token takes effect
without reconstructing the connector.

```fsharp skip=fragment
open ToolUp.DataSources.Synapse

// SQL login or a supplied AAD token, out of ISecretStore.
let source = SynapseDataSource.create secretStore

// Or DefaultAzureCredential — managed identity in Azure, developer
// credentials locally. No stored secret at all. Every source wired to
// this instance sets `auth = aad-default`.
let source = SynapseDataSource.createWithDefaultCredentials ()
```

`Kind` is `"Synapse"`.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `server` | yes | Fully-qualified SQL endpoint, e.g. `myws-ondemand.sql.azuresynapse.net`. |
| `database` | yes | Serverless SQL pool database, or a dedicated pool. |
| `auth` | no | `sql` (default) / `aad-token` / `aad-default` (alias `managed-identity`). |
| `user` | when `auth = sql` | SQL login name. |
| `port` | no | TDS port. Defaults to 1433. |
| `schema` | no | Schema `ListTables` / `GetSchema` scope to. Defaults to `dbo`. |
| `connect_timeout_seconds` | no | Defaults to **30**. |
| `command_timeout_seconds` | no | Omitted leaves the provider default. |

## The three authentication modes

| `auth` | Credential in `ISecretStore` | Notes |
|---|---|---|
| `sql` | the **password** | `user` comes from `ConnectionScope`. Simplest, and the one to avoid in production. |
| `aad-token` | a **ready-minted AAD access token** for `https://database.windows.net/` | The deployment mints and rotates it; the connector presents it. Useful when tokens come from a broker the app already runs. |
| `aad-default` | none | `DefaultAzureCredential` mints a token per call. The mode a managed-identity deployment wants — **no stored secret anywhere.** |

Under both AAD modes the credential is set on the connection object, **never** in the connection
string — putting a bearer token into a connection string leaks it into every ADO diagnostic that
echoes one. The test pack asserts that directly.

`Encrypt=True` is stated explicitly rather than relied on, so a future provider default cannot
silently downgrade the transport.

## Credentials

The connector reads `DataSourceCallContext.Credential` first (the shipped `DataIngestor` pre-resolves
it) and falls back to `ISecretStore.GetSecret(ctx.ScopeId, ctx.Config.CredentialKey)`. Store the value
under the team's storage scope (`team-{teamId}` in Team / MultiTeam mode, `user-{userId}` in
Individual mode) and encrypt at rest with `EncryptedSecretStore` where a master key is configured.

Grant the login or identity `db_datareader` on the target database and nothing more — ingestion is
read-only. For a serverless pool over the lake, the identity also needs read access to the underlying
storage (a `DATABASE SCOPED CREDENTIAL`, or `Storage Blob Data Reader` on the container).

## Introspection

`ListTables` and `GetSchema` run `INFORMATION_SCHEMA.TABLES` / `.COLUMNS`, scoped to `schema` and in
ordinal order. Serverless pools surface **views over the lake and external tables as ordinary
`INFORMATION_SCHEMA` rows**, so one query covers dedicated pools, serverless views and external tables
alike.

Both queries are pure functions of strings (`SynapseCatalogue`), unit-tested without a workspace.
Identifiers are validated against a narrow pattern and **refused** rather than escaped — see
`ToolUp.DataSources.Common`'s README for why the whole family interpolates rather than binds.

## Queries and output

`Query` executes `sql` verbatim as T-SQL, **except** that a bare identifier is expanded to
`SELECT * FROM [schema].[table]` — the documented convenience that lets an admin UI offer "ingest this
whole table" without composing SQL.

Results are **RFC 4180 CSV**: a header row, `\r\n` terminated, UTF-8 with no BOM, invariant-culture
values, `DateTime` as ISO-8601 round-trip, `varbinary` as base64, `NULL` as the empty field.

A failure *after* the connection opened is reported as `SchemaMismatch`, not `SourceUnreachable` — the
endpoint is reachable; the statement or the schema is the operator's to fix.

## Schema mapping — T-SQL type → `ColumnType`

`ColumnInfo.DataType` carries the **raw** T-SQL type name; `SynapseCatalogue.toColumnType` projects it
down to the coarse `ColumnType`.

| T-SQL | `ColumnType` |
|---|---|
| `bit` | `BooleanColumn` |
| `int` / `bigint` / `decimal` / `numeric` / `float` / `real` / `money` | `NumberColumn` |
| `date` / `datetime` / `datetime2` / `datetimeoffset` / `smalldatetime` / `time` | `DateColumn` |
| `char` / `varchar` / `nchar` / `nvarchar` / `text` | `StringColumn` |
| `uniqueidentifier` / `xml` / `binary` / `varbinary` / `image` / `hierarchyid` / `sql_variant` | `StringColumn` |
| **`timestamp` / `rowversion`** | **`StringColumn`** |

That last row is the trap worth memorising: **T-SQL `timestamp` is a ROW VERSION, not a date.** Any
generic ANSI classifier sees the token `timestamp` and calls it a date. It is an 8-byte opaque
counter, and it renders here as base64 text. `datetime2` and `datetimeoffset` are the real date types.

**Nullability** comes from `INFORMATION_SCHEMA`'s `IS_NULLABLE` (`YES` / `NO`); an absent value reads
as not nullable.

## Gotchas

- **The 30-second default connect timeout is deliberate.** Synapse **serverless** pools cold-start,
  and the SqlClient default of 15 s is a false negative waiting to happen on the first query of the
  day.
- **A dedicated pool that is PAUSED refuses connections.** That surfaces as `SourceUnreachable`, which
  is accurate but easy to misread as a network fault — check the pool state first.
- **Serverless pools bill per byte processed.** There is no connector-side ceiling here (Synapse's
  lever is a workspace-level cost-control policy, not a per-query one), so set that policy on the
  workspace. Prefer explicit column lists and a partition predicate.
- **Cancellation rides the ambient `Async` cancellation token** — the connector threads it into every
  ADO `*Async` call, so a cancelled ingestion aborts the in-flight statement.

## Testing

The always-on arm of `src/ToolUp.DataSources.Tests` covers configuration parsing, the auth-mode
parser, connection-string composition (including the assertion that AAD credentials stay out of the
string), both catalogue queries, identifier refusal, statement resolution and the type map — no Azure
subscription needed.

The remote arm binds `RemoteDataSourceContract` against a real workspace when these are set. It is
**read-only**:

| Variable | Required |
|---|---|
| `TOOLUP_SYNAPSE_SERVER` | yes |
| `TOOLUP_SYNAPSE_DATABASE` | yes |
| `TOOLUP_SYNAPSE_TABLE` | yes |
| `TOOLUP_SYNAPSE_AUTH` | no (defaults to `aad-default`) |
| `TOOLUP_SYNAPSE_USER` | when `auth = sql` |
| `TOOLUP_SYNAPSE_CREDENTIAL` | password or AAD token, per the auth mode |
| `TOOLUP_SYNAPSE_SCHEMA` | no (defaults to `dbo`) |
| `TOOLUP_SYNAPSE_SAMPLE_SQL` | no (defaults to a `TOP 5` select) |

With them unset the arm reports one `Pending` case naming what it wanted.
