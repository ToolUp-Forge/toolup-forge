# ToolUp.DataSources.Csv

An `IDataSource` companion for **delimited files** — CSV, TSV, pipe-delimited exports. The ingestion
shape most consumers meet first: "we get a daily CSV".

Production-ready, and **zero third-party dependencies**. Stateless between calls (portability rule
4): every method re-reads its settings from the call's `DataSourceConfig` and re-acquires the file,
so a file replaced between two runs is simply read again.

```fsharp skip=fragment
open ToolUp.DataSources.Csv

// `storage` is whatever the deployment composed — LocalFileStorage for
// files on disk, or any cloud IBlobStorage companion. The connector
// never touches System.IO, so one config works against either.
let source = CsvDataSource.create storage
```

`Kind` is `"Csv"`.

## File acquisition — `IBlobStorage`, never `System.IO`

Every byte is acquired through `IBlobStorage`. That is not a stylistic preference: reading a path off
disk directly would bypass the storage abstraction and with it scope isolation (GP 4), the
encryption-at-rest decorator, and every cloud backend. A deployment reading files from a local folder
composes `LocalFileStorage` and points the source at the container it serves; a deployment reading
from object storage composes the matching companion. The connector cannot tell the difference.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `container` | yes | Scope-derived container the files live in. |
| `prefix` | no | Blob-name prefix within the container. Omitted reads the container root. Normalised to one trailing `/`; backslashes are folded to `/`, because blob names are `/`-delimited on every backend whatever the host OS is. |
| `extension` | no | Defaults to `.csv`. A bare suffix (`tsv`) is dot-prefixed for you. |
| `delimiter` | no | Field separator in the **source** file. One character, or one of `tab` / `pipe` / `semicolon` / `comma` / `space` — a literal tab has no representation in most admin UIs. Defaults to `,`. |
| `quote` | no | Quote character in the **source** file. Defaults to `"`. Must differ from `delimiter`. |
| `has_header` | no | Defaults to `true`. When `false`, columns are named `column_1` … `column_n` and the first record is data. |
| `encoding` | no | Defaults to `utf-8`. See the encoding table below. |
| `sample_rows` | no | Rows the type probe samples for `GetSchema`. Defaults to 1000. Must be positive. |

**Credentials:** none. This connector reads no secret — the credential, if any, belongs to the
`IBlobStorage` backend and was resolved when the deployment composed it. `CredentialKey` on the
config is therefore inert here.

## Tables

A **table is a file**. `ListTables` lists the container under the prefix and reports each matching
blob with its extension stripped; `GetSchema` and `Query` map a table name back to
`prefix + table + extension`.

Two things it deliberately does **not** report: a blob in a nested folder (its recovered name would
collide with a sibling's and address the wrong file), and a blob with a different extension (the
`manifest.json` sitting beside the exports is not a table).

A table name carrying `/`, `\` or `..` is **refused**, not sanitised. The container is scope-derived
and safe, but the table name is concatenated onto the prefix, so a name with `..` would address a
file the source was never pointed at. Refusing is louder than rewriting: a silently-corrected name
fails later, as a missing file, somewhere less informative.

## Query output

`sql` is the **table name**. There is no dialect: a file has no query engine, and inventing a
mini-SQL over one would be a filter the operator could not reason about. Constrain what a source
exposes with the config's `Tables` list, or with the prefix.

Output is **RFC 4180 CSV** with a header row, UTF-8, no BOM, `\r\n` terminated — the uniform wire
format of every connector in this family. Note this is a **re-emission, not an echo**: a
tab-delimited, Latin-1, single-quoted source file comes back comma-delimited, UTF-8 and
double-quoted. That is the point — a module parsing an ingested object does not have to know which
connector or which dialect produced it.

## Encodings

Deliberately limited to what the BCL carries without `System.Text.Encoding.CodePages`:

| `encoding` | |
|---|---|
| `utf-8` / `utf8` | Default. |
| `ascii` | |
| `latin1` / `iso-8859-1` | |
| `utf-16` / `utf-16le` / `unicode` | |
| `utf-16be` | |
| `utf-32` | |

Adding the code-pages package for `windows-1252` would put a dependency into a companion whose whole
point is not having one; a deployment with legacy-codepage exports transcodes on the way in. An
unrecognised value is **refused, naming the accepted set** — rather than silently defaulting to UTF-8
and producing mojibake nobody traces back to here.

**A byte-order mark always wins over the configured encoding.** A file that says what it is in its
first bytes is more trustworthy than a config key typed months ago, and honouring the BOM is what
stops a stray character appearing inside the first header cell.

## Supported subset — RFC 4180, with one documented leniency

Quoted fields may contain the delimiter, line breaks, and doubled quotes (`""` → `"`). A **bare quote
inside an unquoted field** (`5" pipe`) is taken literally. RFC 4180 forbids that; real exporters emit
it constantly and every spreadsheet opens such a file without complaint, so refusing the record would
reject more real files than it would catch real defects.

**Malformed quoting does not throw and does not abandon the file.** An unterminated quote at EOF, or
content after a closing quote, is reported as a `SchemaMismatch` naming the record's 1-based
position. The reader resynchronises at the next line break, so one bad record costs one record.

## Schema inference

`GetSchema` type-probes the first `sample_rows` data rows and reports `string` / `number` / `boolean`
/ `date` per column, via the same `ColumnMapping.inferColumnType` the SDK's mapping UI runs on — so
an inferred schema and the same file's mapping preview cannot disagree.

`ColumnInfo.DataType` carries `"number (inferred)"` rather than a bare `"number"`. The marker is
load-bearing: an admin UI showing it beside a warehouse's `NUMERIC(38,9)` is telling the operator
something true, namely that this one is a guess.

A column is reported **nullable when any sampled cell is blank**. A delimited file declares no
nullability, so an observed blank is the only evidence there is.

### Gotchas

- **Type inference is locale-sensitive.** The probe parses candidate numbers and dates with the
  running process's culture. On a comma-decimal host `1,5` may probe as a number; on an invariant
  host it will not, and the same file then infers a different schema. Pin the deployment's culture,
  or treat an inferred schema as a starting point an operator confirms — which is why the type name
  says `(inferred)`.
- **Inference reads a SAMPLE.** A column that is numeric for its first 1000 rows and carries `N/A` at
  row 40,000 still infers as a number. Raise `sample_rows` where that matters, and remember it costs
  a proportional read.
- **`GetSchema` and `Query` read the file twice.** They are independent calls over `IBlobStorage`; a
  file replaced between them yields a schema from one version and rows from another. That is inherent
  to a stateless connector and is why the ingestor treats a run as a snapshot rather than a
  transaction.

## Worked example

A deployment with a `LocalFileStorage` over `./data`, ingesting `./data/team-acme/exports/sales.csv`:

```fsharp skip=fragment
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.DataSources.Csv

// 1. Compose. `storage` is whatever IBlobStorage this deployment built —
//    a LocalFileStorage over ./data here. The connector registers as an
//    IDataSource and the ingestor resolves it by Kind. Advanced
//    behaviour is opt-in (GP 13): a deployment that never sets
//    EnabledDataIngestion pays nothing.
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
            Some(fun services -> services.AddSingleton<IDataSource>(CsvDataSource.create storage))
}
|> ServerApp.run

// 2. Register the source (an admin UI does this through
//    IDataIngestionApi.SaveDataSource).
let salesSource = {
    Id = "sales-csv"
    Name = "Daily sales export"
    Kind = CsvDataSource.Kind // "Csv"
    ConnectionScope = Map.ofList [ "container", "team-acme"; "prefix", "exports" ]
    CredentialKey = "" // unused — the credential belongs to IBlobStorage
    Tables = None
    Tags = Map.empty
}

// 3. Refresh. TriggerRefresh schedules a Manual job; the ingestor calls
//    Connect then Query and writes the returned bytes through
//    IDataObjectStore with a Versioned policy. Poll ListRecentRuns for
//    completion.
async {
    let! _ = api.SaveDataSource salesSource
    let! jobId = api.TriggerRefresh("sales-csv", "sales")
    return jobId
}
```

## Testing

`src/ToolUp.DataSources.Tests` runs the seven-point local-file `IDataSource` contract against this
connector on two dialects (comma and tab), plus unit tests over `ConnectionScope` parsing, the RFC
4180 reader's quoting rules and resynchronisation, the type probe, and the prefix-escape refusal.

All of it is **always on**: fixtures are written into an in-process `IBlobStorage`, so the pack needs
no credential, no network and no filesystem, and runs clean on a fresh checkout.

## See also

- [`ToolUp.DataSources.Excel`](../Excel/README.md) — the same file-source model over `.xlsx` workbooks.
- [`ToolUp.DataSources.Parquet`](../Parquet/README.md) — the same model over Parquet extracts.
- [`ToolUp.DataSources.Common`](../Common/README.md) — the shared, vendor-free connector support layer.
