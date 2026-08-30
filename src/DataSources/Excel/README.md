# ToolUp.DataSources.Excel

An `IDataSource` companion for **`.xlsx` / `.xlsm` workbooks** — "the team drops a spreadsheet into a
shared folder", an ingestion shape no warehouse connector covers.

Production-ready. Stateless between calls (portability rule 4): every method re-reads its settings,
re-acquires the workbook, and closes it before returning. Nothing is cached.

```fsharp skip=fragment
open ToolUp.DataSources.Excel

// `storage` is whatever the deployment composed — LocalFileStorage for
// workbooks on disk, or any cloud IBlobStorage companion.
let source = ExcelDataSource.create storage
```

`Kind` is `"Excel"`.

## File acquisition — `IBlobStorage`, never `System.IO`

Every byte is acquired through `IBlobStorage`, for the reasons set out in
[`ToolUp.DataSources.Csv`](../Csv/README.md#file-acquisition--iblobstorage-never-systemio): reading a
path off disk directly would bypass scope isolation (GP 4), encryption at rest, and every cloud
backend.

## `ConnectionScope` keys

| Key | Required | Meaning |
|---|---|---|
| `container` | yes | Scope-derived container the workbooks live in. |
| `prefix` | no | Blob-name prefix within the container. Omitted reads the container root. |
| `extension` | no | Defaults to `.xlsx`. Set `xlsm` for macro-enabled workbooks. |
| `sheet` | no | Default sheet name, applied when a table name carries no selector. |
| `sheet_index` | no | Default sheet by 0-based position. Mutually exclusive with `sheet` — setting both is refused, because a disagreement between them has no correct reading. |
| `has_header` | no | Defaults to `true`. When `false`, columns are named `column_1` … `column_n`. |
| `sample_rows` | no | Rows the type probe samples for `GetSchema`. Defaults to 1000. |

**Credentials:** none — see the CSV companion's note. `CredentialKey` is inert here.

## Tables — sheets, Excel **Tables** and named ranges are all first-class

A workbook is not one table, so a table name here is **`<file>` or `<file>#<selector>`**.

`ListTables` enumerates, for every workbook under the prefix: the workbook itself (its default
sheet), plus one name per **sheet**, per **Excel Table** (`ListObject`), and per **workbook-scoped
named range**. So both structures a spreadsheet author uses to say "this rectangle is the data" are
addressable *by the name they gave it* — not by a cell range someone has to keep in step with the
file as rows are added.

Selector resolution is ordered: **Excel Table → named range → sheet name → `@index`**. A workbook may
legitimately carry a Table and a sheet with the same name, and the Table is the narrower, more
deliberate answer. An unresolvable selector fails with a `SchemaMismatch` that **lists what the
workbook does carry**, so an operator correcting a typo does not have to open the file.

```
orders                    → the default sheet (config `sheet` / `sheet_index`, else the first)
orders#Sales              → the sheet named Sales
orders#SalesTable         → the Excel Table named SalesTable, clipped to its own rectangle
orders#Q3Range            → the named range Q3Range, clipped to its own rectangle
orders#@1                 → the second sheet, positionally
```

**Sheet-local defined names are deliberately skipped.** Two sheets may carry the same local name, so
the selector would be ambiguous; a `ListTables` entry that sometimes addresses a different rectangle
is worse than one that is absent.

A table name carrying `/`, `\` or `..` in its **file** part is refused — see the CSV companion's
note on prefix escape.

## Query output

`sql` is the table name in the form above. Output is **RFC 4180 CSV** with a header row, UTF-8, no
BOM, `\r\n` terminated — the family's uniform wire format.

Cells render invariant-culture: numbers round-trip (`R`), dates as ISO-8601 (`O`), booleans as
`true`/`false`. An **error cell** (`#N/A`, `#DIV/0!`) carries its token forward rather than becoming
blank — "missing" and "the spreadsheet's own formula failed" are not the same fact about the data.

Sparse rows keep their true column positions, so a blank cell mid-row stays a blank field rather than
shifting every value after it one column left.

## Supported subset

- **`.xlsx` and `.xlsm`.** A macro-enabled workbook is the same OPC package plus a `vbaProject.bin`
  part. Macros are never executed, evaluated or extracted — the reader resolves only the workbook,
  shared-string, styles and worksheet parts.
- **Cached values only.** The connector reads what Excel wrote when the workbook was last saved.
  There is **no formula evaluation and no Power Query refresh**: an `.xlsx` stores each formula's
  cached result, and re-evaluating would need a calculation engine this package deliberately does not
  carry. A workbook whose formulas have never been calculated reads as empty cells, and that is the
  honest answer.
- **Shared strings, inline strings, and number-format-aware date detection** are all handled, as is
  the 1904 date system. Real-world workbooks store dates as styled numeric serials rather than typed
  date cells, so the reader uses the cell's `numFmtId` to disambiguate.
- **Not supported:** array formulas, multi-area named ranges (`A1:B2,D1:E2` — reading the first area
  would produce a plausible table that silently omits the rest, so it is refused), broken `#REF!`
  references, charts, pivot caches, `.xls` (the pre-2007 binary format — a different format
  entirely, not a variant of this one).

## Schema inference

The header row and the first `sample_rows` data rows of the resolved region are type-probed exactly
as in the [CSV companion](../Csv/README.md#schema-inference), with the same `(inferred)` marker and
the same locale-sensitivity gotcha. A workbook declares no column types the connector can trust — a
cell's type is per-cell, not per-column — so inference is the only available answer.

### Gotchas

- **`ListTables` opens every workbook under the prefix.** That is the cost of reporting names an
  operator can actually pass to `Query`; a listing of bare filenames would hide every Table and named
  range the file carries. Constrain a large prefix with the config's `Tables` list.
- **A workbook that will not open is listed by its bare name and no further.** One unreadable file in
  a prefix must not hide the readable ones; the failure surfaces at `Query` time, named.
- **Clipping is by the rectangle the Table or name declares**, which is what the workbook stored at
  last save. An Excel Table that has grown since is clipped to its stored `ref` — reopen and save the
  workbook, or address the sheet instead.

## Worked example

Ingesting the `SalesTable` out of `./data/team-acme/inbox/orders.xlsx`:

```fsharp skip=fragment
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.DataSources.Excel

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
            Some(fun services -> services.AddSingleton<IDataSource>(ExcelDataSource.create storage))
}
|> ServerApp.run

let ordersSource = {
    Id = "orders-xlsx"
    Name = "Weekly orders workbook"
    Kind = ExcelDataSource.Kind // "Excel"
    ConnectionScope = Map.ofList [ "container", "team-acme"; "prefix", "inbox" ]
    CredentialKey = ""
    Tables = None
    Tags = Map.empty
}

// TriggerRefresh schedules a Manual job; the ingestor calls Connect
// then Query and writes the returned CSV through IDataObjectStore with
// a Versioned policy. Poll ListRecentRuns for completion.
async {
    let! _ = api.SaveDataSource ordersSource
    let! jobId = api.TriggerRefresh("orders-xlsx", "orders#SalesTable")
    return jobId
}
```

## Testing

`src/ToolUp.DataSources.Tests` runs the seven-point local-file `IDataSource` contract against this
connector twice — once on the default first-sheet selector and once against a workbook whose data
sheet is *not* first, so a connector ignoring `sheet` would pass the first run and fail the second —
plus tests over A1 range parsing, Table and named-range resolution, range clipping, and the
selector-not-found message.

Fixtures are **real `.xlsx` packages built in the test pack** with the same OpenXml SDK the connector
reads through, then written into an in-process `IBlobStorage`. Nothing is committed as a binary, and
a fixture cannot drift out of step with the assertion that reads it. No credential, no network, no
filesystem.

## Implementation note

The grid reader is `ToolUp.Tabular`'s XLSX leg — shared strings, number-format-aware dates and the
1904 date system are fiddly, already solved there and already tested. This companion adds the
workbook-level structure a typed-row reader has no reason to expose: sheet, Table and defined-name
enumeration, and range slicing. `ToolUp.Tabular` carries the same `DocumentFormat.OpenXml` dependency
this package needs anyway, so the reuse adds no vendor surface, and the vendor dependency stays in
the companion (GP 1) — nothing in `ToolUp.Platform.*` references OpenXml.

## See also

- [`ToolUp.DataSources.Csv`](../Csv/README.md) — the same file-source model over delimited files.
- [`ToolUp.DataSources.Parquet`](../Parquet/README.md) — the same model over Parquet extracts.
- [`ToolUp.Tabular`](../../ToolUp.Tabular/README.md) — typed CSV / XLSX ingestion against a declared schema.
