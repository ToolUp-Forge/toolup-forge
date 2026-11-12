# ToolUp.DataSources.Sql

An `IDataSource` companion spanning six operational databases behind one package —
**PostgreSQL, MySQL / MariaDB, SQL Server (and Azure SQL), SQLite, Oracle and ClickHouse** — selected
per data source by a `backend` key in `DataSourceConfig.ConnectionScope`.

Production-ready. Stateless between calls (portability rule 4): every method opens a connection, uses
it and disposes it, and the password is re-read through the `ISecretStore` thunk on **every** call, so
rotating it takes effect without reconstructing the connector.

```fsharp skip=fragment
open ToolUp.DataSources.Sql

// One registration serves every backend; the `backend` key routes.
let source = SqlDataSource.create secretStore

// A deployment whose connection string already carries every
// credential (integrated security, a managed identity, a local
// SQLite file) needs no secret store at all:
let source = SqlDataSource.createWithoutSecrets ()
```

`Kind` is `"Sql"`. A deployment that prefers to route by engine registers the same connector again
under its own discriminator with `SqlDataSource.createWithKind "Postgres" (Some secretStore)`; the
`backend` key still selects the engine.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `backend` | yes | One of `postgres` / `postgresql` / `npgsql` / `redshift-wire` / `mysql` / `mariadb` / `sqlserver` / `mssql` / `azuresql` / `sqlite` / `oracle` / `clickhouse` (case-insensitive). |
| `connection_string` | yes | ADO connection string for that backend, **without** the password when the password comes from `ISecretStore`. |
| `schema` | no | Schema / owner the catalogue queries scope to. Defaults to `public` (Postgres) or `dbo` (SQL Server); the other four self-scope — see below. SQLite **refuses** this key, because it has no schema catalogue and silently ignoring it would leave an operator believing a scope had been applied. |
| `command_timeout_seconds` | no | Per-command timeout. Must be positive. Omitted leaves the provider default. |

The credential is **optional**: when present it is folded into the connection string as `Password`,
and it never overwrites a password the operator put in the connection string themselves.

## Credentials

The connector reads `DataSourceCallContext.Credential` first (the shipped `DataIngestor` pre-resolves
it), and falls back to `ISecretStore.GetSecret(ctx.ScopeId, ctx.Config.CredentialKey)`. The value is
the **database password** and nothing else — no connection string, no key blob.

Store it under the team's storage scope (`team-{teamId}` in Team / MultiTeam mode, `user-{userId}` in
Individual mode) and encrypt it at rest with `EncryptedSecretStore` where the deployment has a master
key configured.

## What each backend's introspection actually runs

Six backends collapse to **three** catalogue shapes. All of it is a pure function of strings
(`SqlDialect`, and `SqlBinding` for the markers), so every query in this table is unit-tested without
a database.

| Backend | `ListTables` | `GetSchema` | Unscoped behaviour |
|---|---|---|---|
| PostgreSQL, MySQL, SQL Server, ClickHouse | `information_schema.tables` | `information_schema.columns`, ordinal order | MySQL scopes to `DATABASE()`; the others exclude their system schemas (`pg_catalog`, `sys`, `system`) |
| Oracle | `ALL_TABLES` | `ALL_TAB_COLUMNS`, `COLUMN_ID` order | scopes to `OWNER = USER` |
| SQLite | `sqlite_master` (excluding `sqlite_%`) | `pragma_table_info(...)` | the attached file |

Oracle folds unquoted identifiers to **upper case** and stores them that way, so the connector
upper-cases the owner and table values it compares against. Getting this wrong is the classic
"the table exists but `GetSchema` returns nothing" symptom.

### What is bound, and what is rendered

A catalogue query compares the schema and the table name against the catalogue as **values**, so
`ListTables` and `GetSchema` bind them (`@schema`, `@table`, rewritten to each backend's marker form
below) and render no value into the statement text. Oracle's values are bound upper-cased, as
above.

`selectAllSql` (the bare-identifier expansion `Query` applies) names a table as an **identifier**,
and no backend binds an identifier: it is still rendered, with the backend's own quoting. What keeps
it safe is the identifier check in front of it: every name is validated against a deliberately
narrow pattern — `^[A-Za-z_][A-Za-z0-9_$]{0,127}$` — and **refused** as `SchemaMismatch` if it does
not match, so nothing that could end a quoted identifier reaches the string builder. The catalogue
queries keep the same check in front of their bound values, so a name no catalogue could hold is
refused before a connection opens. Both are enumerated in the test pack, per backend.

## Bound statements

The connector declares `Binds` (`ConnectorCapabilities`): `BoundQuery.run source ctx statement` runs a
statement whose values travel beside its text and are bound by the provider, never rendered into it.
The statement names each parameter `@name`; the connector rewrites the markers to the backend's form
and sets each provider parameter's type from the value's kind, never letting the provider infer it
from a boxed object. The result is the same CSV `Query` returns.

| Backend | Marker form | Kinds bound (provider type) | Kinds refused |
|---|---|---|---|
| PostgreSQL | positional `$n` (Npgsql sends the text unparsed) | Float (`double precision`), Int (`bigint`), Bool (`boolean`), Text and Categorical (`text`), Timestamp (`timestamptz`, as UTC), Decimal (its text, `CAST($n AS numeric)`) | none |
| MySQL / MariaDB | named `@name` | Float (`DOUBLE`), Int (`BIGINT`), Bool (`BOOL`), Text and Categorical (`VARCHAR`), Timestamp (`DATETIME`, as UTC) | **Decimal**: `DECIMAL` holds at most 30 fractional digits, so some canonical decimals have no exact MySQL value, and a decimal bound as text is compared through a double |
| SQL Server | named `@name` | Float (`float`), Int (`bigint`), Bool (`bit`), Text and Categorical (`nvarchar`), Timestamp (`datetimeoffset`), Decimal (`SqlDecimal`, up to 38 digits) | none |
| SQLite | named `@name` | Float (`REAL`), Int and Bool (`INTEGER`, 1 / 0), Text and Categorical (`TEXT`), Timestamp (`TEXT`, ISO 8601), Decimal (`TEXT`, the digits) | none |
| Oracle | prefixed `:bN`, bound by name | Float (`BINARY_DOUBLE`), Int (`NUMBER`, Int64), Text and Categorical (`NVARCHAR2`), Timestamp (`TIMESTAMP WITH TIME ZONE`, as UTC), Decimal (`OracleDecimal`, up to 38 significant digits) | **Bool**: before 23ai Oracle has no SQL boolean; bind an Int |
| ClickHouse | typed `{name:Type}` | Float (`Float64`), Int (`Int64`), Bool (`Bool`), Text and Categorical (`String`), Timestamp (`DateTime64(7, 'UTC')`), Decimal (`Decimal(P, S)`, up to 76 digits); a typed null is `Nullable(Type)` | none |

A refused kind is `BindingRefusal.UnbindableKind`, naming the parameter and the kind, before any
connection opens. The connector serves all six backends, so its declared `BindableKinds` is every
kind and the backend's set above is applied per call, from the source's `backend` key. A decimal
wider than the backend's exact type (the limits in the table) is refused before the provider, naming
the parameter, rather than rounded. SQLite has no exact numeric: the digits arrive exactly as given,
as text, which is how the provider stores a `System.Decimal` itself, and SQLite's own arithmetic on
them is by its affinity rules.

**The marker scanner reads the text as the backend does.** A marker-shaped sequence inside a string
literal, a quoted identifier (`"…"`, `` `…` ``, `[…]` where the backend has them), a comment (`--`,
`/* */`, and `#` on MySQL and ClickHouse; nested where PostgreSQL and SQL Server nest them) or a
PostgreSQL dollar-quoted string is left alone, and so is a separator. MySQL's `--` is a comment only
before whitespace, and the body of its `/*! … */` runs, so it is scanned as code. The statement is
refused, before it reaches the provider, when:

- it holds a second statement after a `;`: **one statement per call**. A trailing `;` is admitted.
  SQL Server runs a batch of statements with no separator between them, which a lexical check cannot
  see; on that backend the rule covers the separated form, and the bound values themselves can never
  add a statement;
- a quoted run holds a backslash whose meaning a session setting decides (MySQL's `sql_mode`;
  PostgreSQL's `standard_conforming_strings` outside an `E'…'` string), because the scanner cannot
  know which reading the server will apply;
- it uses the backend's own marker syntax (`$1`, `:name`, `?`), which would bind outside the
  statement's parameter list;
- a marker has no parameter (`MissingParameter`), or a parameter has no marker (`UnusedParameter`).

A statement's `Timeout` is the command's, overriding `command_timeout_seconds`.

## Queries and output

`Query` executes `sql` verbatim against the backend, **except** that a bare identifier is expanded to
`SELECT * FROM <schema>.<table>` with that backend's own quoting (`"…"` / `[…]` / `` `…` ``). That is
the documented convenience which lets an admin UI offer "ingest this whole table" without composing
SQL; anything containing whitespace or punctuation is a statement and passes through untouched.

Results are returned as **RFC 4180 CSV**: a header row of column names, `\r\n` terminated, UTF-8 with
no BOM. Values render invariant-culture (so a comma-decimal host cannot corrupt every number),
`DateTime` as ISO-8601 round-trip (`"O"`), `byte[]` as base64, `NULL` as the empty field.

A failure *after* the connection opened is reported as `SchemaMismatch`, not `SourceUnreachable` — the
server is reachable; the statement or the schema is the operator's to fix.

## Schema mapping — native type → `ColumnType`

`ColumnInfo.DataType` carries the **raw** native type name (`NUMERIC(38,9)`,
`timestamp with time zone`, `Nullable(DateTime64(3))`). That is what an admin UI should render, and
collapsing it to one of four coarse tokens would be lossy for no gain. The projection down to the
SDK's `ColumnType` is `SqlDialect.toColumnType backend nativeType`, which a consumer calls when it
needs to reason uniformly.

The ANSI table classifies the obvious families. Per-backend overrides exist only where it would be
**wrong**, and those are the gotchas worth knowing:

- **T-SQL `timestamp` is a ROW VERSION, not a date.** It classifies as `StringColumn`. So does
  `rowversion`. `datetime2` / `datetimeoffset` are the real date types.
- **ClickHouse type names are wrapped** — `Nullable(DateTime64(3))`,
  `LowCardinality(Nullable(String))`, `SimpleAggregateFunction(sum, UInt64)`. The connector peels the
  transparent wrappers before classifying (and takes the **last** argument of
  `SimpleAggregateFunction`, which is the type). `Array(...)` is deliberately **not** peeled: an array
  of numbers is not a number, and renders in CSV as text.
- **SQLite's `pragma_table_info` reports `notnull`, whose sense is INVERTED** — `1` means *not*
  nullable. Every other backend here reports `YES`/`NO` or `Y`/`N` the obvious way.
- Postgres `interval` → `DateColumn`; `oid`/`xid`/`cid` → `NumberColumn`; `json`/`jsonb`/`uuid`/
  `bytea`/`inet` → `StringColumn`.
- MySQL `year` → `DateColumn`; the `blob` / `binary` family → `StringColumn`.
- Oracle `raw` / `long raw` / `blob` / `clob` / `rowid` / `xmltype` → `StringColumn`.

## Gotchas

- **All six ADO providers restore, whichever backend you use.** They are lazily loaded, so the unused
  five cost disk rather than startup. If your deployment must not ship an Oracle or ClickHouse
  assembly at all, this is not the package for you — take the single-backend provider directly and
  implement `IDataSource` over it.
- **Npgsql is shared, not re-pinned.** It is already pinned by the Timescale time-series store; two
  versions of one provider in a single graph is a restore conflict waiting to happen.
- **`redshift-wire` reaches Redshift over the Postgres protocol** and is the better connector for a
  small, low-latency query. For a scheduled ingestion that may run for minutes — or against a
  Serverless workgroup with no externally reachable wire endpoint — use `ToolUp.DataSources.Redshift`,
  which goes through the Redshift Data API instead.
- **Ingestion is read-only by design.** Nothing here issues DDL or DML. Give the connector's database
  role `SELECT` and catalogue-read rights and nothing more.
- **Cancellation rides the ambient `Async` cancellation token.** `DataSourceCallContext` carries no
  token field, so the connector reads `Async.CancellationToken` and threads it into every ADO
  `*Async` call — a cancelled ingestion aborts the in-flight statement rather than waiting it out.

## The linq2db evaluation — verified compatible, deliberately not adopted

The parked question was whether one query-abstraction library should span every backend here.
[linq2db](https://github.com/linq2db/linq2db) 6.4.0 was re-assessed at implementation time. **It is
compatible**: it ships a first-class `net10.0` target, it compiled and its `SchemaProvider` surface
resolved against `net10.0` in a scratch project, and its transitive footprint is genuinely small
(itself plus `Microsoft.Extensions.{DependencyInjection,Logging}.Abstractions`, both already in the
graph). No newer closer-fit option has appeared.

It is **not adopted**, for four reasons:

1. **Its principal value is unusable here.** linq2db earns its keep by materialising query results
   into typed records. `IDataSource.Query` returns opaque `byte[]` — there is no record to map to, so
   the "F# record-mapping ergonomics" half of the parked question turns out to be moot rather than
   favourable.
2. **Its remaining value is three pure functions.** What would genuinely have helped is one uniform
   `SchemaProvider` across six backends. Measured against the alternative, that is three
   `INFORMATION_SCHEMA`-shaped catalogue queries (ANSI / Oracle / SQLite) — pure string builders,
   testable offline with no server and no provider registration, which is what `SqlDialect` is.
3. **It would add runtime coupling for a feature we do not use.** linq2db routes on a provider-name
   registry, and that registry is not stable across its own majors: the ClickHouse provider constant
   was renamed between 5.x and 6.x. A raw-SQL connector should not take a dependency whose *string
   constants* are a breaking-change surface.
4. **GP 1 favours the minimum vendor surface.** The ADO providers are unavoidable — something has to
   open a socket. linq2db is avoidable, so it is avoided.

The decision is worth revisiting if the substrate ever grows a typed row contract (an `IDataSource`
that yields `DatasetSchema`-shaped rows rather than bytes). At that point reason 1 inverts, and
reason 2 stops being the whole story.

## Testing

The pure surface — backend parsing, catalogue statements for all three dialect shapes, the marker
scanner and its translation per backend, connection-string composition, statement resolution,
identifier refusal and type classification — is covered by the always-on arm of
`src/ToolUp.DataSources.Tests`, which needs no database. The binding laws of the data source contract
pack (`IDataSourceContract.bindingLaws`) run there too, against an in-memory SQLite database, always.

The five server backends run the same laws as armed legs, one variable each, holding the connection
string of a **scratch** database: the laws seed one table (`kept`) and drop it again. With the
variable unset the leg reports `Pending`, naming it.

| Variable | Backend |
|---|---|
| `TOOLUP_SQL_BIND_POSTGRES` | PostgreSQL |
| `TOOLUP_SQL_BIND_MYSQL` | MySQL / MariaDB |
| `TOOLUP_SQL_BIND_SQLSERVER` | SQL Server |
| `TOOLUP_SQL_BIND_ORACLE` | Oracle |
| `TOOLUP_SQL_BIND_CLICKHOUSE` | ClickHouse |

The remote arm binds `RemoteDataSourceContract` against a real server when these are set. It is
**read-only** — it creates nothing, writes nothing and drops nothing:

| Variable | Required | Meaning |
|---|---|---|
| `TOOLUP_SQL_BACKEND` | yes | as the `backend` key |
| `TOOLUP_SQL_CONNECTION_STRING` | yes | as the `connection_string` key |
| `TOOLUP_SQL_TABLE` | yes | a pre-provisioned, readable table |
| `TOOLUP_SQL_SCHEMA` | no | schema containing that table |
| `TOOLUP_SQL_PASSWORD` | no | password, if the connection string omits it |
| `TOOLUP_SQL_SAMPLE_SQL` | no | a bounded sample statement; defaults to `SELECT * FROM <table>` |

With them unset the arm reports one `Pending` case naming what it wanted — a fresh checkout is clean,
and a CI job that was supposed to have credentials shows "skipped" rather than a green that proves
nothing.
