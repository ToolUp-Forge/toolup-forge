# ToolUp.DataSources.Parquet

Two independent legs over one vendor dependency and one file format:

- **Storage** — the `IDatasetCodec` seam, below. Encodes ToolUp's *own* typed dataset vintages as
  native Parquet so external compute can read them.
- **Ingestion** — [`ParquetDataSource`](#the-ingestion-leg--parquetdatasource), an `IDataSource`
  companion that reads Parquet somebody *else* wrote.

They share a package because they share `Parquet.Net` and the format, and nothing else. Neither
calls the other, and composing one does not compose the other.

---

## The storage leg — `ParquetDatasetCodec`

Parquet dataset codec for `ToolUp.Platform` — the companion implementation of
the `IDatasetCodec` seam. Compose it and dataset vintages are stored as
**native Parquet**: a `DatasetContentRef` tags `Format = "parquet"`, and an
external compute worker (Python, R, anything with a Parquet reader) parses the
content blob directly, with no ToolUp code.

```fsharp skip=fragment
open ToolUp.Platform
open ToolUp.DataSources.Parquet

let datasets = BlobDatasetStore.createWithCodec dataObjects (ParquetDatasetCodec())
```

The default composition is unchanged without this package: `BlobDatasetStore.create`
uses the BCL-only `JsonFrameDatasetCodec` (`Format = "toolup-frame-v1"`), so
deployments that do not need native-Parquet handoff pay nothing.

## Column mapping

| Dataset dtype | Parquet physical type |
|---|---|
| `Float` | double |
| `Int` | int64 |
| `Bool` | boolean |
| `Text` / `Categorical` | UTF-8 string |
| `Timestamp` | timestamp (microseconds, UTC-adjusted) |

Nullability maps to Parquet optional columns. `Text` vs `Categorical` and the
column roles (`PanelUnit` / `PanelPeriod` / `Target`) are not representable in
the Parquet physical schema, so the full declared `DatasetSchema` travels in
the file's custom key/value metadata under `toolup.dataset.schema`. `Decode`
verifies the physical schema against the declared one and refuses on any
mismatch. A plain Parquet reader ignores the metadata and still reads every
column natively.

## Precision contract

- **Timestamps** round-trip as UTC instants at **microsecond** precision:
  the original offset is not preserved (the same instant re-reads at offset
  zero — `DateTimeOffset` equality, which compares instants, is preserved),
  and sub-microsecond ticks truncate.
- All other dtypes round-trip exactly.

---

## The ingestion leg — `ParquetDataSource`

An `IDataSource` companion for **Parquet files somebody else wrote** — "we got a Parquet extract from
the warehouse", the one file shape a consumer receives precisely because someone upstream cared about
types.

Production-ready. Stateless between calls (portability rule 4): every method re-reads its settings
and re-acquires the file; nothing is cached.

```fsharp skip=fragment
open ToolUp.DataSources.Parquet

// `storage` is whatever the deployment composed — LocalFileStorage for
// extracts on disk, or any cloud IBlobStorage companion.
let source = ParquetDataSource.create storage
```

`Kind` is `"Parquet"`.

### File acquisition — `IBlobStorage`, never `System.IO`

Every byte is acquired through `IBlobStorage`, for the reasons set out in
[`ToolUp.DataSources.Csv`](../Csv/README.md#file-acquisition--iblobstorage-never-systemio): reading a
path off disk directly would bypass scope isolation (GP 4), encryption at rest, and every cloud
backend.

### `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `container` | yes | Scope-derived container the extracts live in. |
| `prefix` | no | Blob-name prefix within the container. Omitted reads the container root. |
| `extension` | no | Defaults to `.parquet`. |
| `sample_rows` | no | Accepted for uniformity with the other file connectors and **inert here** — Parquet declares its own schema, so nothing is sampled. |

**Credentials:** none — see the CSV companion's note. `CredentialKey` is inert here.

A **table is a file**, exactly as in the CSV companion: `ListTables` strips the extension, nested
blobs and foreign extensions are not reported, and a table name carrying `/`, `\` or `..` is refused
rather than sanitised.

### No inference, ever

`GetSchema` reads the file's **footer metadata** and reports what the writer declared — column names,
types and nullability. Unlike the CSV and Excel connectors it is not guessing, and the native type
name says so by carrying no `(inferred)` marker.

Nested schemas **flatten to dotted paths**: a `struct { customer { id, name } }` presents as
`customer.id` and `customer.name`, one value per row.

| Declared CLR type | `ColumnType` |
|---|---|
| `bool` | `BooleanColumn` |
| `sbyte` / `byte` / `int16` / `uint16` / `int` / `uint32` / `int64` / `uint64` / `float32` / `double` / `decimal` | `NumberColumn` |
| `DateTime` / `DateTimeOffset` / `DateOnly` / `TimeOnly` / `TimeSpan` | `DateColumn` |
| `string`, `byte[]`, anything else | `StringColumn` |

`ColumnInfo.DataType` carries the declared CLR type name with a trailing `?` when the column is
nullable — the most specific true thing available, since Parquet's logical types reach the reader as
CLR types.

### Query output

`sql` is the **table name**; there is no dialect, and no predicate pushdown (see the gotchas). Output
is **RFC 4180 CSV** with a header row, UTF-8, no BOM, `\r\n` terminated — the family's uniform wire
format. Values render invariant-culture (`byte[]` as base64, a null cell as the empty field).

**Row groups are read one at a time**, so only one row group's decoded columns are ever resident.
The honest caveat: `IDataSource.Query` returns `byte[]` and `IBlobStorage.Download` yields `byte[]`,
so the file's bytes and the emitted CSV are both materialised whatever the loop does — the bound is
on the *decoded* column data, which is the part that expands.

### Gotchas

- **Repeated fields (`LIST` / `MAP`) are refused, by column name.** Their values arrive flattened
  behind repetition levels, so aligning them into rows would mean inventing a flattening rule the
  writer never declared — and every row after the first list would silently carry another column's
  values. Nested **STRUCTs** are unaffected, which is why they are supported and lists are not.
- **A column whose declared type is outside the table above is refused**, not stringified. Emitting a
  column the connector does not understand would put values in the CSV that no consumer could parse
  back, and the operator would have no way to tell.
- **No predicate or column pushdown.** `Query` reads every row group and every column. Parquet's
  selective-read advantage is real, but `IDataSource.Query` takes a connector-defined string and
  returns opaque bytes, so a projection here would be a mini-dialect an operator could not reason
  about. Write narrower extracts upstream.
- **Re-emitting as CSV loses the types you came for.** That is the family's uniform wire contract, and
  `GetSchema` is where the declared types survive. A deployment that wants Parquet *end to end* wants
  the storage leg above, not this one.

### Worked example

Ingesting `./data/team-acme/extracts/orders.parquet`:

```fsharp skip=fragment
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.DataSources.Parquet

// `storage` is whatever IBlobStorage this deployment built.
let config = {
    ServerConfig.defaults with
        DataIngestion = EnabledDataIngestion
        JobScheduler = InProcessJobScheduler
}

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withStorage storage
|> ServerApp.withExtensions {
    ComposeExtensions.empty with
        ServiceConfig =
            Some(fun services -> services.AddSingleton<IDataSource>(ParquetDataSource.create storage))
}
|> ServerApp.run

let ordersSource = {
    Id = "orders-parquet"
    Name = "Nightly warehouse extract"
    Kind = ParquetDataSource.Kind // "Parquet"
    ConnectionScope = Map.ofList [ "container", "team-acme"; "prefix", "extracts" ]
    CredentialKey = ""
    Tables = None
    Tags = Map.empty
}

// TriggerRefresh schedules a Manual job; the ingestor calls Connect
// then Query and writes the returned CSV through IDataObjectStore with
// a Versioned policy. Poll ListRecentRuns for completion.
async {
    let! _ = api.SaveDataSource ordersSource
    let! jobId = api.TriggerRefresh("orders-parquet", "orders")
    return jobId
}
```

### Testing

`src/ToolUp.DataSources.Tests` runs the seven-point local-file `IDataSource` contract against this
connector, plus tests over the declared-schema read, the CLR-type mapping, invariant-culture
rendering, and the refusal of a blob that is not a Parquet file.

Fixtures are **real Parquet files written in the test pack** with `Parquet.Net` and read back through
the connector, so nothing is committed as a binary and the writer's schema cannot drift from the
assertion that reads it. No credential, no network, no filesystem.

---

## Dependencies

[`Parquet.Net`](https://www.nuget.org/packages/Parquet.Net) (Apache-2.0),
fully managed. This vendor dependency never reaches `ToolUp.Platform.*` — it
is isolated in this companion behind the codec and `IDataSource` seams.

## See also

- [`ToolUp.DataSources.Csv`](../Csv/README.md) — the same file-source model over delimited files.
- [`ToolUp.DataSources.Excel`](../Excel/README.md) — the same model over `.xlsx` workbooks.
