# ToolUp.DataSources.BigQuery

An `IDataSource` companion for **Google BigQuery**, over `Google.Cloud.BigQuery.V2`.

Production-ready. Stateless between calls (portability rule 4): a client is built per call from the
credential resolved through the `ISecretStore` thunk, so rotating a service-account key takes effect
without reconstructing the connector. BigQuery clients are cheap; pooling would be a
profiling-driven follow-up, not a correctness concern.

```fsharp
open ToolUp.DataSources.BigQuery

// Service-account JSON out of ISecretStore, per source.
let source = BigQueryDataSource.create secretStore

// Or application-default credentials — workload identity on GKE, the
// GCE metadata server, GOOGLE_APPLICATION_CREDENTIALS. Every source
// wired to this instance sets `use_default_credentials = true`.
let source = BigQueryDataSource.createWithDefaultCredentials ()
```

`Kind` is `"BigQuery"`.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `project_id` | yes | The project queries are **billed to**. Not necessarily the project owning the data — a cross-project read bills the querying project. |
| `dataset_id` | yes | Dataset that `ListTables` / `GetSchema` enumerate, and that unqualified table names in `Query` resolve against. |
| `location` | no | Dataset location (`EU`, `us-central1`). Omitted lets the service infer it, which **fails on a location mismatch** — set it explicitly for a non-US dataset. |
| `maximum_bytes_billed` | no | Hard ceiling on bytes scanned per query. Must be a positive integer. |
| `use_legacy_sql` | no | Interpret `sql` as Legacy SQL. Defaults to `false` (Standard SQL). |
| `use_default_credentials` | no | Use application-default credentials instead of a stored service-account blob. Defaults to `false`. |

## Credentials

The credential is the **full contents of a downloaded service-account JSON key**. The connector reads
`DataSourceCallContext.Credential` first (the shipped `DataIngestor` pre-resolves it) and falls back
to `ISecretStore.GetSecret(ctx.ScopeId, ctx.Config.CredentialKey)`.

- **Scope:** the team's storage scope — `team-{teamId}` in Team / MultiTeam mode, `user-{userId}` in
  Individual mode.
- **Key:** whatever `DataSourceConfig.CredentialKey` names.
- **Value:** the whole JSON blob, encrypted at rest via `EncryptedSecretStore` where the deployment
  has a master key configured.
- **Minimum IAM:** `roles/bigquery.dataViewer` on the dataset plus `roles/bigquery.jobUser` on the
  billing project. Ingestion is read-only; nothing here needs write or DDL rights.

A blob that is not valid service-account JSON fails as **`SchemaMismatch` naming the credential key**,
not as an authentication error. That distinction is the difference between a two-minute fix and an
afternoon.

## Cost control — read this one

**BigQuery bills per byte SCANNED, and `SELECT *` on a partitioned fact table is the classic way to
spend a lot of money by accident.** The connector surfaces the lever rather than hiding it:

- Set **`maximum_bytes_billed`** on every source. A query whose dry-run estimate exceeds it is
  **refused by the service** rather than billed. The connector refuses a zero, negative or
  unparseable value at configuration time, because a mistyped ceiling silently becoming "no ceiling"
  is exactly the failure this key exists to prevent.
- Prefer explicit column lists and a `WHERE` clause on the partition column. BigQuery does not charge
  less for a `LIMIT`.
- `Connect` is a single dataset-metadata fetch — it scans nothing, so an admin UI's "Test connection"
  button costs nothing.

## Queries and output

`sql` is BigQuery Standard SQL (or Legacy SQL under `use_legacy_sql`). The configured dataset is set
as the query's default dataset, so unqualified table names resolve against it.

Results are **RFC 4180 CSV**: a header row of column names taken from the result schema, `\r\n`
terminated, UTF-8 with no BOM. Values render invariant-culture, `DateTime` as ISO-8601 round-trip,
`byte[]` as base64, `NULL` as the empty field.

## Schema mapping — BigQuery type → `ColumnType`

`ColumnInfo.DataType` carries the **raw** BigQuery type name; `BigQueryDataSource.toColumnType`
projects it down to the SDK's coarse four-case `ColumnType` for consumers that need to reason
uniformly.

| BigQuery | `ColumnType` |
|---|---|
| `BOOL` / `BOOLEAN` | `BooleanColumn` |
| `INT64` / `FLOAT64` / `NUMERIC` / `BIGNUMERIC` | `NumberColumn` |
| `DATE` / `DATETIME` / `TIME` / `TIMESTAMP` | `DateColumn` |
| `STRING` / `BYTES` / `JSON` / `GEOGRAPHY` / `INTERVAL` | `StringColumn` |
| `RECORD` / `STRUCT` / `ARRAY` | `StringColumn` |

`INTERVAL` is structured rather than a point in time, so it renders as text and classifies as
`StringColumn` — the one entry above a reader is likely to expect elsewhere.

**Nullability follows the field MODE:** only `REQUIRED` is not nullable. `REPEATED` reads nullable
(elements may be absent), and an absent mode — which the API omits for a plain nullable field — reads
nullable too.

## Gotchas

- **A location mismatch is the most common first failure.** If the dataset is not in the US multi-
  region and `location` is unset, the job is created in the wrong location and fails with a message
  about the dataset not being found.
- **Nested and repeated fields flatten to text.** A `RECORD` column renders as its JSON in the CSV.
  Deployments that want columnar nested data should `UNNEST` in the query.
- **Cancellation rides the ambient `Async` cancellation token.** `DataSourceCallContext` carries no
  token field, so the connector reads `Async.CancellationToken` and passes it into the query
  execution.

## Testing

The always-on arm of `src/ToolUp.DataSources.Tests` covers configuration parsing (including every
`maximum_bytes_billed` refusal), the type map, mode-based nullability, and the credential failure
modes — no GCP account needed.

The remote arm binds `RemoteDataSourceContract` against a real dataset when these are set. It is
**read-only** and its default sample query carries a `LIMIT` and a byte ceiling:

| Variable | Required |
|---|---|
| `TOOLUP_BIGQUERY_PROJECT_ID` | yes |
| `TOOLUP_BIGQUERY_DATASET_ID` | yes |
| `TOOLUP_BIGQUERY_TABLE` | yes |
| `TOOLUP_BIGQUERY_CREDENTIAL_JSON` | yes |
| `TOOLUP_BIGQUERY_LOCATION` | no |
| `TOOLUP_BIGQUERY_MAX_BYTES` | no (defaults to 1 GB) |
| `TOOLUP_BIGQUERY_SAMPLE_SQL` | no |
| `TOOLUP_BIGQUERY_ISOLATED_DATASET` | no |

With them unset the arm reports one `Pending` case naming what it wanted — a fresh checkout is clean,
and a CI job that was supposed to have credentials shows "skipped" rather than a green that proves
nothing.
