# ToolUp.DataSources.Parquet

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

## Dependencies

[`Parquet.Net`](https://www.nuget.org/packages/Parquet.Net) (Apache-2.0),
fully managed. This vendor dependency never reaches `ToolUp.Platform.*` — it
is isolated in this companion behind the codec seam.
