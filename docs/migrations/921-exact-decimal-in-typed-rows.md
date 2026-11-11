# Migration — an exact decimal in typed rows (`DatasetDType.Decimal`, `DatasetValue.Decimal`)

**What changes.** `DatasetDType` gains `Decimal` and `DatasetValue` gains `Decimal of digits: string`.
A database `DECIMAL` / `NUMERIC` column, and a Parquet column carrying the decimal logical type, now
arrive in typed rows as an exact decimal. Before this change they arrived as a `Float`, which rounds
past 15 to 17 significant digits and cannot even state `0.1`, or as `Text`, which every consumer
then parsed by its own rule.

**It is breaking.** Both unions are closed. Every exhaustive `match` over either one becomes
incomplete. Under the SDK's `FS0025`-as-error setting that is a compile error naming the file and
line, which is the point: each site is a decision the consumer has to make, not one the SDK should
guess.

## The representation, and why it is text

The value is **canonical decimal text**, with `DatasetDecimal` as its module. It is not
`System.Decimal`. This was measured on 2026-11-11, not assumed:

| Source | Widest exact decimal |
|---|---|
| SQL Server, Synapse, Redshift, Athena (`DECIMAL(38, s)`), Snowflake (`NUMBER(38, s)`), BigQuery `NUMERIC` | 38 significant digits |
| BigQuery `BIGNUMERIC` | 76 digits, scale 38 |
| PostgreSQL unconstrained `NUMERIC` | unbounded |
| Parquet decimal over a byte array | unbounded (Parquet.Net 6 writes at most 16 bytes, 38 digits) |

| Candidate | `DECIMAL(38,0)` max | 38-digit fraction | Same text on both hosts? |
|---|---|---|---|
| `System.Decimal` on .NET | **refused** (overflow) | **rounded** to 28 places | no |
| `decimal` under Fable (big.js) | **rounded** to `1e+38` | **rounded** at 29 significant digits | no |
| canonical decimal text | exact | exact | yes |

The host decimal holds less than the sources produce. Worse, .NET and Fable round the same text to
different values (`0.12345678901234567890123456789` is kept by Fable and rounded by .NET), so a
value would change as it crossed the wire. **What would overturn this:** a host decimal of at
least 76 significant digits that .NET and Fable both implement identically.

**The canonical form** is what `DatasetDecimal.tryParse` returns: an optional `-`, integer digits
with no leading zero (`0` when there are none), and a `.` followed by the fractional digits only
when a non-zero fractional digit exists, with no trailing zero. There is no exponent, no `+` and no
`-0`. Textual equality is therefore numeric equality: `12.50` and `12.5` are one value, and every
codec round trip is the identity. A column's declared scale is schema information
(`ColumnInfo.DataType`, `NUMERIC(10, 2)`), not a property of each cell. `DatasetValue.fits` refuses a
non-canonical `Decimal` cell, so a store never holds two spellings of one number.

`DatasetDecimal` gives you:

- `tryParse` — any plain decimal to canonical text, or `None`.
- `isCanonical`
- `compare` — exact numeric order at any width. `DatasetValue.compare` uses it, so page filters
  order decimals as numbers.
- `ofDecimal` — a host decimal to canonical text.
- `tryToDecimal` — the host decimal holding exactly the value, or `None`. It never rounds. Use it
  when you need arithmetic and the value fits.

## The census — what a consumer should expect to touch

Adding the cases reported **seven exhaustive matches** across the forge SDK. That is the number to
compare against your own build.

| Union | Site | Resolution |
|---|---|---|
| `DatasetDType` | `DatasetDType.name` (`DatasetTypes.fs`) | `"Decimal"`; `parse` accepts it |
| `DatasetDType` | `ParquetMapping.toParquetField` | Parquet decimal of the shape the values need (text column past 38 digits) |
| `DatasetDType` | `ParquetMapping.expectedClrType` | `BigDecimal` |
| `DatasetDType` | `ParquetMapping.writeColumn` | writes `BigDecimal` (or the canonical text) |
| `DatasetDType` | `ParquetMapping.readColumn` | reads `BigDecimal` (or the canonical text) |
| `DatasetValue` | `DatasetValue.dtypeOf` (`DatasetTypes.fs`) | `Some DatasetDType.Decimal` |
| `DatasetValue` | `DatasetAssemblyExecutor.cellText` | the canonical text (unit-hash splits) |

Some sites took a wildcard or an `if`/`elif` ladder, so the compiler did not flag them. Each was
reviewed:

- `DatasetValue.fits` and `DatasetValue.compare` gained explicit cases.
- `ParquetDataSource.datasetDType`, its column decoder, `toColumnType` and the cell conversion now
  map `decimal` and `BigDecimal` to `Decimal`. They were `Float`.
- `DatasetAssemblyExecutor.numOf` is unchanged. Assembly aggregations (`Sum`, `Average`, `Min`,
  `Max`) produce a `Float` by contract, and a decimal bucket is non-numeric to them, so it folds to
  `Null` (the documented outcome for a non-numeric bucket), never a rounded figure. An exact
  decimal aggregate is new capability, not this migration.

## The codemod

Find the sites:

```powershell
rg -n "DatasetDType\.Categorical\s*->|DatasetValue\.Categorical\b" --type fsharp
```

Every exhaustive match names `Categorical`, the previous last case. Each hit is a candidate site.

**A dtype projection** gains a line:

```fsharp
 | DatasetDType.Categorical -> "string"
+| DatasetDType.Decimal -> "decimal"
```

**A cell treated as its text** (hashing, display, keys) joins the text arm:

```fsharp
 | DatasetValue.Text s
-| DatasetValue.Categorical s -> Some s
+| DatasetValue.Categorical s
+| DatasetValue.Decimal s -> Some s
```

**A cell treated as a number** chooses, explicitly, between exact and approximate:

```fsharp
 | DatasetValue.Float f -> Some f
 | DatasetValue.Int i -> Some(float i)
+// Approximate on purpose: say so where you do it.
+| DatasetValue.Decimal d -> Some(float d)
```

or keeps it exact:

```fsharp
+| DatasetValue.Decimal d -> DatasetDecimal.tryToDecimal d   // None past System.Decimal: handle it
```

**A cell you construct from a database value** goes through the canonical form:

```fsharp
-DatasetValue.Float(float (reader.GetDecimal i))
+DatasetValue.Decimal(DatasetDecimal.ofDecimal (reader.GetDecimal i))
```

## The paths

- **CSV payloads.** The writer (`Csv.renderValue`) already wrote a `decimal` as its invariant
  digits. `Csv.ofReader` now also carries a `DECIMAL(38, s)` value that `GetValue` cannot. SqlClient
  throws `OverflowException` there, so the writer falls back to the provider's exact value
  (`GetProviderSpecificValue`, `SqlDecimal`), and a provider with none is a named failure, never a
  rounded number. On the read side, the schema sibling object already records each column's native
  type. `TypeMap.isExactDecimal` says whether that type is an exact decimal, and `Csv.readDecimal`
  reads such a field as `DatasetValue.Decimal`. An empty field is NULL, because a decimal cannot be
  the empty string. The reader never guesses a float.
- **Parquet, the dataset codec.** A `Decimal` column is written as the Parquet decimal logical type,
  at the precision and scale its values need. Past 38 digits, which is the most Parquet.Net 6
  writes, it is written as its canonical text in a string column. The declared schema in the file
  metadata still says `Decimal`, so it reads back exactly.
- **Parquet, the connector.** A Parquet file's decimal column reads as `Decimal` on the native
  payload path, and as its exact digits on the CSV path. The connector reads decimals with
  `ParquetOptions.UseBigDecimal`. Without that option Parquet.Net reads through `System.Decimal` and
  **silently truncates** a value wider than 29 digits. That was measured: a 39-digit value lost its
  fraction.

`TypeMap.isExactDecimal` covers `DECIMAL` / `NUMERIC` / `DEC` (the SQL family), `MONEY` /
`SMALLMONEY`, Snowflake's `NUMBER` / `FIXED`, BigQuery's `NUMERIC` / `BIGNUMERIC` / `BIGDECIMAL`,
and the Parquet connector's `Decimal` / `BigDecimal`. The coarse `ColumnType` is unchanged (still
`NumberColumn`).

## The fidelity contract

`DataSourceFidelityContract.DecimalFidelityContract` adds three laws:

- a decimal with more digits than a float holds round-trips exactly;
- a sum of decimals a float gets wrong (`0.1 + 0.2`) is right;
- `GetSchema` declares the column an exact decimal.

Each law is refused by name by a lossy twin. The pack is bound live to `ParquetDataSource`, and to
recorded results for Synapse (a SqlClient-shaped reader that overflows exactly as SqlClient does)
and Redshift (the Data API's `stringValue`). Every other connector is listed `unbound`, with its
reason. SQLite cannot hold the value at all: its `DECIMAL` is `REAL`.

## Not yet

- **Bound statements (Phase 915).** The parameter value union `BoundValue` was in flight in the
  same tier and carries no decimal kind. It gains one in a follow-up, so a decimal parameter binds
  as its digits.
