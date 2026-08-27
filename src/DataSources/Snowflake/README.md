# ToolUp.DataSources.Snowflake

An `IDataSource` companion for **Snowflake**, over the official `Snowflake.Data` ADO.NET driver.

Production-ready. Stateless between calls (portability rule 4): every method opens a connection, uses
it and disposes it, and the credential is re-read through the `ISecretStore` thunk on **every** call,
so rotating a password or key takes effect without reconstructing the connector.

```fsharp
open ToolUp.DataSources.Snowflake

// Password, or an inline PEM for key-pair auth, out of ISecretStore.
let source = SnowflakeDataSource.create secretStore

// Or key-pair auth with a mounted key file — no stored secret at all.
let source = SnowflakeDataSource.createWithKeyFile ()
```

`Kind` is `"Snowflake"`.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `account` | yes | Account identifier, e.g. `xy12345.eu-west-1` or `myorg-myaccount`. |
| `user` | yes | Login name. |
| `database` | yes | **Required**, because `INFORMATION_SCHEMA` is per-database in Snowflake and the catalogue queries resolve against it. |
| `schema` | no | Defaults to `PUBLIC`. |
| `warehouse` | no | Virtual warehouse that runs the queries. Omitted uses the user's default — and a user with no default **cannot run a query at all**, so setting it is strongly recommended. |
| `role` | no | Role to assume. Omitted uses the user's default. |
| `authenticator` | no | `password` (default, alias `snowflake`) or `snowflake_jwt` (aliases `keypair`, `key-pair`). |
| `private_key_file` | for key-pair | Path to a PEM private key. Preferred over an inline PEM — see below. |
| `case_sensitive_identifiers` | no | Defaults to `false`. See "identifier case". |
| `command_timeout_seconds` | no | Omitted leaves the driver default. |

**Per-warehouse and per-schema routing is `ConnectionScope` data, not connector state** — two
`DataSourceConfig` records pointing at different warehouses share one registered connector.

## Credentials

The connector reads `DataSourceCallContext.Credential` first (the shipped `DataIngestor` pre-resolves
it) and falls back to `ISecretStore.GetSecret(ctx.ScopeId, ctx.Config.CredentialKey)`.

- Under `authenticator = password`, the credential **is** the password. It is required.
- Under `snowflake_jwt`, **prefer `private_key_file`** pointing at a mounted secret — the connector
  then needs no `ISecretStore` credential at all, and the key never enters process memory as a
  string. Passing the PEM itself as the credential also works.

Grant the role `USAGE` on the warehouse, database and schema plus `SELECT` on the tables, and nothing
more — ingestion is read-only.

### The inline-PEM caveat

`Snowflake.Data` parses a plain `key=value;` connection string, so **any value containing `;` would be
mis-split**. A PEM body contains no semicolons, so an inline key works — but a *password* containing
one would not. Rather than emit a string the driver would silently truncate (presenting as an
authentication failure nobody could explain from the message), the connector **refuses** such a value
up front with a `SchemaMismatch` naming the offending key. Change the password, or move to key-pair
auth with a key file.

## Identifier case — the thing to get right first

**Snowflake folds unquoted identifiers to UPPER CASE and stores them that way.** So a catalogue query
comparing against `'orders'` matches nothing, and `GetSchema` returns an empty column list for a table
that plainly exists.

The connector therefore upper-cases the schema and table literals it compares against, and upper-cases
both parts of the `SELECT * FROM "SCHEMA"."TABLE"` expansion. A deployment that genuinely created its
objects with quoted lower-case identifiers sets `case_sensitive_identifiers = true` to turn the
folding off.

## Introspection

`ListTables` and `GetSchema` run `INFORMATION_SCHEMA.TABLES` / `.COLUMNS` within the connection's
current database, scoped to `schema` and in ordinal order. Both are pure functions of strings
(`SnowflakeCatalogue`), unit-tested without an account. Identifiers are validated against a narrow
pattern and **refused** rather than escaped — see `ToolUp.DataSources.Common`'s README for why the
whole family interpolates rather than binds.

## Queries and output

`Query` executes `sql` verbatim, **except** that a bare identifier is expanded to
`SELECT * FROM "SCHEMA"."TABLE"` — the documented convenience that lets an admin UI offer "ingest this
whole table" without composing SQL.

Results are **RFC 4180 CSV**: a header row, `\r\n` terminated, UTF-8 with no BOM, invariant-culture
values, `DateTime` as ISO-8601 round-trip, `BINARY` as base64, `NULL` as the empty field.

A failure *after* the connection opened is reported as `SchemaMismatch`, not `SourceUnreachable`.

## Schema mapping — Snowflake type → `ColumnType`

`ColumnInfo.DataType` carries the **raw** Snowflake type name; `SnowflakeCatalogue.toColumnType`
projects it down to the coarse `ColumnType`.

| Snowflake | `ColumnType` |
|---|---|
| `BOOLEAN` | `BooleanColumn` |
| `NUMBER(p,s)` / `DECIMAL` / `INT` / `FLOAT` / `DOUBLE` | `NumberColumn` |
| `DATE` / `TIME` / `TIMESTAMP_NTZ` / `TIMESTAMP_LTZ` / `TIMESTAMP_TZ` | `DateColumn` |
| `VARCHAR` / `TEXT` / `STRING` / `CHAR` | `StringColumn` |
| `VARIANT` / `OBJECT` / `ARRAY` | `StringColumn` |
| `GEOGRAPHY` / `GEOMETRY` / `BINARY` | `StringColumn` |

The semi-structured family (`VARIANT` / `OBJECT` / `ARRAY`) renders as its JSON text in the CSV — flat
columns are recovered by projecting in the query (`payload:field::string`), not by the connector.

**Nullability** comes from `INFORMATION_SCHEMA`'s `IS_NULLABLE` (`YES` / `NO`).

## Gotchas

- **No warehouse, no query.** A user with no default warehouse gets an error that names neither the
  warehouse nor the connector. Set `warehouse` on every source.
- **A suspended warehouse resumes on first query**, which adds latency to the first ingestion after an
  idle period — and bills for it. That is Snowflake behaviour, not connector behaviour.
- **`INFORMATION_SCHEMA` is per-database**, which is why `database` is required rather than optional.
  Cross-database introspection would need `SNOWFLAKE.ACCOUNT_USAGE`, which has a latency of up to a
  few hours and is deliberately not used here.
- **Cancellation rides the ambient `Async` cancellation token** — the connector threads it into every
  ADO `*Async` call.

## Testing

The always-on arm of `src/ToolUp.DataSources.Tests` covers configuration parsing, the authenticator
aliases, connection-string composition (including the `;` refusal and the key-file precedence),
identifier folding, both catalogue queries, identifier refusal, statement resolution and the type map
— no Snowflake account needed. One case asserts that key-file auth gets **past** credential
resolution, because a regression there would mean a key-file deployment could never start.

The remote arm binds `RemoteDataSourceContract` against a real account when these are set. It is
**read-only**:

| Variable | Required |
|---|---|
| `TOOLUP_SNOWFLAKE_ACCOUNT` | yes |
| `TOOLUP_SNOWFLAKE_USER` | yes |
| `TOOLUP_SNOWFLAKE_DATABASE` | yes |
| `TOOLUP_SNOWFLAKE_TABLE` | yes |
| `TOOLUP_SNOWFLAKE_SCHEMA` | no (defaults to `PUBLIC`) |
| `TOOLUP_SNOWFLAKE_WAREHOUSE` | no (but see the gotcha above) |
| `TOOLUP_SNOWFLAKE_ROLE` | no |
| `TOOLUP_SNOWFLAKE_AUTHENTICATOR` | no (defaults to `password`) |
| `TOOLUP_SNOWFLAKE_CREDENTIAL` | password or PEM, per the authenticator |
| `TOOLUP_SNOWFLAKE_PRIVATE_KEY_FILE` | for key-pair auth |
| `TOOLUP_SNOWFLAKE_SAMPLE_SQL` | no (defaults to a `LIMIT 5` select) |

With them unset the arm reports one `Pending` case naming what it wanted.
