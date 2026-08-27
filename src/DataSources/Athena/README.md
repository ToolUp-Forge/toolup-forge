# ToolUp.DataSources.Athena

An `IDataSource` companion for **AWS Athena** — a query engine over data already sitting in S3,
addressed through a Glue data catalogue.

Production-ready. Stateless between calls (portability rule 4): the client is rebuilt per call from
credentials resolved through the `ISecretStore` thunk, or from the AWS default credential chain.

```fsharp skip=fragment
open ToolUp.DataSources.Athena

// A JSON access-key blob per source out of ISecretStore; a source
// with no stored credential still falls through to the default chain.
let source = AthenaDataSource.create secretStore

// Or the AWS default credential chain only — instance profile, IRSA,
// ECS task role. What most AWS deployments actually want.
let source = AthenaDataSource.createWithDefaultCredentials ()
```

`Kind` is `"Athena"`.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `region` | yes | AWS region in SDK string form (`eu-west-2`). |
| `database` | yes | Glue database whose tables `ListTables` / `GetSchema` enumerate. |
| `output_location` | no* | `s3://bucket/prefix/` Athena stages result sets to. *Required unless the workgroup enforces its own output location.* |
| `catalog` | no | Data catalogue. Defaults to `AwsDataCatalog`. |
| `workgroup` | no | Athena workgroup. Omitted uses the account's `primary`. |
| `poll_interval_ms` | no | How often to poll `GetQueryExecution`. Defaults to 500 ms. Must be positive. |
| `query_timeout_seconds` | no | Ceiling on the total wait for a terminal state. Defaults to 300 s. Must be positive. |

## Credentials

**Optional by design.** With no stored credential the connector uses the **AWS default credential
chain** — environment variables, the shared credentials file, an EC2 instance profile, an ECS task
role, IRSA on EKS. That is how most AWS deployments want it, so an absent credential here is a
deployment choice rather than an error.

When a credential *is* stored it is a JSON object:

```json
{ "accessKeyId": "AKIA…", "secretAccessKey": "…", "sessionToken": "…" }
```

`sessionToken` is optional (present for temporary credentials). Each key is accepted under its
several common spellings (`aws_access_key_id`, `access_key_id`, …). A blob carrying only one half of
the pair is refused as `SchemaMismatch`, and the message says how to opt out of stored credentials
entirely.

**Minimum IAM:** `athena:StartQueryExecution` / `GetQueryExecution` / `GetQueryResults` /
`StopQueryExecution` / `GetDatabase` / `GetTableMetadata` / `ListTableMetadata`, plus
`glue:GetDatabase` / `GetTable` / `GetTables` / `GetPartitions`, plus `s3:GetObject` on the *data*
prefix and `s3:GetObject` + `s3:PutObject` + `s3:ListBucket` on the **staging** prefix. Ingestion is
read-only against the data; the staging write is Athena's, not the connector's.

## The async query model, and the timeout that is the connector's

Athena's shape maps onto `IDataSource.Query` directly: `StartQueryExecution` returns immediately with
an id, `GetQueryExecution` is polled to a terminal state, and the result set is paged out of
`GetQueryResults`.

`query_timeout_seconds` is enforced **by the connector**, not by Athena. On expiry the connector asks
Athena to stop the query (best effort — a failure to cancel never masks the timeout that caused it)
and returns `SourceUnreachable` naming the execution id and the ceiling. Without that, a scheduled
ingestion that outran its window would hold a scheduler slot indefinitely.

Terminal states map as: `SUCCEEDED` → results; `FAILED` → `SchemaMismatch` carrying Athena's own
`StateChangeReason` (a failed query is nearly always the statement or the catalogue, both the
operator's to fix); `CANCELLED` → `SourceUnreachable`.

## S3 staging

Athena writes every result set to `output_location` before the connector reads it back. That bucket is
the same substrate `src/Storage/AwsS3Storage/` composes over — point them at one bucket with different
prefixes and a single lifecycle policy expires both. **Set an expiry**: result sets accumulate
indefinitely otherwise, and they are a copy of your data.

## Queries and output

`sql` is Athena SQL (Trino/Presto dialect). The configured database and catalogue are set as the
query's execution context, so unqualified table names resolve against them.

Results are **RFC 4180 CSV** with a header row — which is also what Athena natively stages to S3, so
this connector's wire format is a re-emission rather than a translation. Athena repeats the column
names as the first row of the first page for `SELECT` statements but not for DDL; the connector drops
that row only when it genuinely matches the header, because comparing is cheaper than being wrong in
either direction.

## Schema mapping — Hive type → `ColumnType`

`ColumnInfo.DataType` carries the **raw** Hive type name; `AthenaDataSource.toColumnType` projects it
down to the SDK's coarse `ColumnType`.

| Hive / Athena | `ColumnType` |
|---|---|
| `boolean` | `BooleanColumn` |
| `tinyint` / `smallint` / `int` / `bigint` / `float` / `double` / `decimal(p,s)` | `NumberColumn` |
| `date` / `timestamp` | `DateColumn` |
| `string` / `char` / `varchar` / `binary` | `StringColumn` |
| `array<…>` / `map<…>` / `struct<…>` | `StringColumn` |

Note the **angle brackets**: Hive spells generic types `array<int>`, not `array(int)`. The shared
normaliser cuts at both bracket styles, so a generic type classifies as its container rather than as
its element — without that, `array<int>` would read as a number.

**Nullability:** the Glue catalogue carries no nullability flag for ordinary columns, so they are
reported nullable. **Partition keys** are reported non-nullable and are appended to the column list
after the data columns — matching how Athena presents them.

## Gotchas

- **`GetSchema` reads the CATALOGUE, not the data.** A table whose underlying S3 prefix is empty still
  introspects to its declared columns. That is correct, but it means a green `GetSchema` is not
  evidence there is anything to ingest.
- **Partition projection is invisible here.** A table using partition projection lists no partitions
  in `PartitionKeys` metadata beyond its declared keys; query cost and behaviour are unchanged.
- **A workgroup with an enforced output location overrides `output_location`.** If the two disagree,
  the workgroup wins and the connector's key is inert — set one or the other, not both.
- **Cancellation rides the ambient `Async` cancellation token**, and it is honoured *between* polls,
  so a cancelled ingestion stops within one `poll_interval_ms`.

## Testing

The always-on arm of `src/ToolUp.DataSources.Tests` covers configuration parsing (including both
poll-knob refusals), the credential-blob parser under every accepted key spelling, and the type map —
no AWS account needed.

The remote arm binds `RemoteDataSourceContract` against a real account when these are set. It is
**read-only**: it starts `SELECT` queries against a pre-provisioned table and creates nothing.

| Variable | Required |
|---|---|
| `TOOLUP_ATHENA_REGION` | yes |
| `TOOLUP_ATHENA_DATABASE` | yes |
| `TOOLUP_ATHENA_TABLE` | yes |
| `TOOLUP_ATHENA_OUTPUT_LOCATION` | yes |
| `TOOLUP_ATHENA_WORKGROUP` | no |
| `TOOLUP_ATHENA_CREDENTIAL_JSON` | no (defaults to the AWS credential chain) |
| `TOOLUP_ATHENA_SAMPLE_SQL` | no (defaults to a `LIMIT 5` select) |
| `TOOLUP_ATHENA_ISOLATED_DATABASE` | no |

With them unset the arm reports one `Pending` case naming what it wanted.
