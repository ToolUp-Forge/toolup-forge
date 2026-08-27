# ToolUp.DataSources.Redshift

An `IDataSource` companion for **AWS Redshift**, over the **Redshift Data API** — not a JDBC/ODBC wire
connection. Covers both provisioned clusters and Redshift Serverless workgroups.

Production-ready. Stateless between calls (portability rule 4): the client is rebuilt per call from
credentials resolved through the `ISecretStore` thunk, or from the AWS default credential chain.

```fsharp
open ToolUp.DataSources.Redshift

let source = RedshiftDataSource.create secretStore
// …or, for an instance-profile / IRSA deployment:
let source = RedshiftDataSource.createWithDefaultCredentials ()
```

`Kind` is `"Redshift"`.

## Why the Data API and not the Postgres wire

Redshift speaks the Postgres wire protocol, so `ToolUp.DataSources.Sql` with
`backend = redshift-wire` reaches it too — and for a small, low-latency query that is the **better**
connector. This one exists for the cases where it is not:

- **No VPC route needed.** The Data API is an AWS control-plane call, so the application does not have
  to sit inside (or peer into) the cluster's VPC.
- **IAM rather than a database password on the wire.** The database credential is named by ARN and
  resolved by Redshift itself; it never passes through this process.
- **Asynchronous by design** — submit, poll, page — which is exactly the shape of a scheduled
  ingestion that may run for minutes.
- **A Serverless workgroup has no externally reachable wire endpoint at all.**

Pick per workload, not per deployment; both connectors can be registered at once under different
`Kind` values.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `region` | yes | AWS region in SDK string form (`us-east-1`). |
| `database` | yes | Database within the cluster / workgroup. |
| `cluster_identifier` | one of | Provisioned cluster. **Mutually exclusive** with `workgroup_name`. |
| `workgroup_name` | one of | Redshift Serverless workgroup. **Mutually exclusive** with `cluster_identifier`. |
| `secret_arn` | see below | Secrets Manager ARN holding the database credential. |
| `db_user` | see below | Database user, for temporary-credential auth. |
| `schema` | no | Schema `ListTables` / `GetSchema` scope to. Defaults to `public`. |
| `poll_interval_ms` | no | Defaults to 500 ms. Must be positive. |
| `query_timeout_seconds` | no | Defaults to 300 s. Must be positive. |

**A provisioned cluster requires `secret_arn` or `db_user`.** A Serverless workgroup requires
neither — it authenticates from the caller's IAM identity. The connector checks this at configuration
time because the Data API otherwise rejects every call with a message that does not say which key is
missing.

## Two credentials, and they are not the same thing

This is the point people get wrong first:

- The **`ISecretStore` credential signs the AWS API call.** Same JSON access-key blob shape as the
  Athena companion, and equally optional — with none stored, the AWS default credential chain applies
  (instance profile, ECS task role, IRSA), which is what most deployments want.
- The **database credential is named by `secret_arn` or `db_user`** in `ConnectionScope`. Redshift
  resolves it server-side. It never reaches this process, and it is never stored in `ISecretStore`.

**Minimum IAM:** `redshift-data:ExecuteStatement` / `DescribeStatement` / `GetStatementResult` /
`CancelStatement` / `ListDatabases` / `ListTables` / `DescribeTable`, plus
`secretsmanager:GetSecretValue` on the named secret (or `redshift:GetClusterCredentials` for the
`db_user` route). Ingestion is read-only — grant the database role `SELECT` and catalogue rights only.

## The async statement model, and the timeout that is the connector's

`ExecuteStatement` returns immediately with a statement id; `DescribeStatement` is polled to a
terminal state; results are paged out of `GetStatementResult`.

`query_timeout_seconds` is enforced **by the connector**. On expiry it asks Redshift to cancel the
statement (best effort — a failure to cancel never masks the timeout that caused it) and returns
`SourceUnreachable` naming the statement id and the ceiling; without that, an ingestion that outran its
window would hold a scheduler slot indefinitely.

Terminal states map as: `FINISHED` → results; `FAILED` → `SchemaMismatch` carrying Redshift's own
error text (a failed statement is nearly always the SQL or the schema); `ABORTED` →
`SourceUnreachable`.

## Queries and output

`sql` is Redshift SQL. Results are **RFC 4180 CSV** with a header row taken from the statement's
column metadata (the column **label** where one is set, so `SELECT a AS total` is headed `total`).

The Data API returns each cell as a tagged value with one populated arm. The connector renders the
populated arm — string, long, double, boolean, or base64 for a blob — and an explicitly-null cell as
the empty field, invariant-culture throughout.

## Schema mapping — Redshift type → `ColumnType`

`ColumnInfo.DataType` carries the **raw** Redshift type name; `RedshiftDataSource.toColumnType`
projects it down to the coarse `ColumnType`. Redshift is Postgres-derived, so most spellings classify
via the shared ANSI table. The overrides are the ones that would otherwise be wrong:

| Redshift | `ColumnType` | Why it needs saying |
|---|---|---|
| `bool` / `boolean` | `BooleanColumn` | |
| `interval` | `DateColumn` | carries no ANSI date token |
| `super` | `StringColumn` | semi-structured; renders as its JSON |
| `hllsketch` | `StringColumn` | opaque sketch |
| `varbyte` | `StringColumn` | binary; renders base64 |
| `geometry` / `geography` | `StringColumn` | |

**Nullability** comes from `DescribeTable`'s per-column flag, which the API reports as an integer
(`1` = nullable). The connector reads either the integer or a boolean shape, so an SDK change to the
nullable modelling does not silently invert it.

## Gotchas

- **`DescribeTable` and `ListTables` are paged**, and the connector drains every page. A wide database
  makes `ListTables` several round trips — set `schema` to keep it bounded.
- **`schema` scoping uses `SchemaPattern`**, which is a LIKE pattern in the Data API. A schema name
  containing `_` or `%` will over-match. The connector does not escape it, because a schema name
  containing a LIKE metacharacter is rare enough that silently rewriting the operator's input would be
  the worse surprise.
- **Cancellation rides the ambient `Async` cancellation token** and is honoured between polls, so a
  cancelled ingestion stops within one `poll_interval_ms`.

## Testing

The always-on arm of `src/ToolUp.DataSources.Tests` covers the cluster-vs-workgroup exclusivity rule,
the provisioned-cluster credential requirement, both poll-knob refusals, the AWS credential-blob
parser and the type map — no AWS account needed.

The remote arm binds `RemoteDataSourceContract` against a real cluster or workgroup when these are
set. It is **read-only**:

| Variable | Required |
|---|---|
| `TOOLUP_REDSHIFT_REGION` | yes |
| `TOOLUP_REDSHIFT_DATABASE` | yes |
| `TOOLUP_REDSHIFT_TABLE` | yes |
| `TOOLUP_REDSHIFT_CLUSTER_IDENTIFIER` / `TOOLUP_REDSHIFT_WORKGROUP_NAME` | one of |
| `TOOLUP_REDSHIFT_SECRET_ARN` / `TOOLUP_REDSHIFT_DB_USER` | for a provisioned cluster |
| `TOOLUP_REDSHIFT_SCHEMA` | no (defaults to `public`) |
| `TOOLUP_REDSHIFT_CREDENTIAL_JSON` | no (defaults to the AWS credential chain) |
| `TOOLUP_REDSHIFT_SAMPLE_SQL` | no (defaults to a `LIMIT 5` select) |

With them unset the arm reports one `Pending` case naming what it wanted.
