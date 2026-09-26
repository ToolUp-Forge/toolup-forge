# Data-source companions

The Platform's `IDataSource` interface is the connector seam of the ingestion substrate: `IDataIngestor` resolves a connector by `DataSourceConfig.Kind`, probes it with `Connect`, and stores whatever `Query` returns as an opaque, versioned payload. Every shipped connector is a companion package under `src/DataSources/`, so a deployment references only the vendor SDKs it actually uses (GP 1).

This page is a cross-cutting overview of the shipped connectors and — the part a connector author needs — **the conformance bar a new `IDataSource` has to pass**. The substrate itself (ingestion runs, the stored payload's metadata, the schema object recorded beside it) is described in [`src/ToolUp.Platform/technical-guide/06-jobs-ingestion-and-diagnostics.md`](../../src/ToolUp.Platform/technical-guide/06-jobs-ingestion-and-diagnostics.md); the shared connector helpers in [`src/DataSources/Common/README.md`](../../src/DataSources/Common/README.md).

## What's shipped

| Package | `Kind` | Backend | Declared payload format |
|---|---|---|---|
| `ToolUp.DataSources.Sql` | `Sql` | PostgreSQL, MySQL / MariaDB, SQL Server, SQLite, Oracle, ClickHouse (per-source `backend` key) | Csv |
| `ToolUp.DataSources.BigQuery` | `BigQuery` | Google BigQuery | Csv |
| `ToolUp.DataSources.Athena` | `Athena` | Amazon Athena | Csv |
| `ToolUp.DataSources.Redshift` | `Redshift` | Amazon Redshift (Data API) | Csv |
| `ToolUp.DataSources.Synapse` | `Synapse` | Azure Synapse | Csv |
| `ToolUp.DataSources.Snowflake` | `Snowflake` | Snowflake | Csv |
| `ToolUp.DataSources.Csv` | `Csv` | delimited files in an `IBlobStorage` container | Csv |
| `ToolUp.DataSources.Excel` | `Excel` | workbooks in an `IBlobStorage` container | Csv |
| `ToolUp.DataSources.Parquet` | `Parquet` | Parquet files in an `IBlobStorage` container | Csv |
| `ToolUp.DataSources.GoogleAnalytics` | `GoogleAnalytics` (default; configurable) | GA4 Data API | Json |

`InMemoryDataSource` (in `ToolUp.Platform.Server`) is the dev / test byte store. `ToolUp.DataSources.Common` is not a connector: it carries the shared RFC 4180 writer, credential and `ConnectionScope` helpers, and the native-type classifier every warehouse connector composes its overrides in front of.

**The payload format is declared, not documented.** A connector states what its `Query` bytes are by also implementing `IDeclaresPayloadFormat`, and the ingestor records the declaration on the stored payload as its `content-format`. A connector that declares nothing reads as `Csv`; one that declares an `Other` format is refused at ingestion before any probe.

**NULL is not the empty string.** The Csv family writes the null convention inside plain RFC 4180 bytes: an **unquoted** empty field is NULL, a **quoted** `""` is the empty string. A reader applies it only to payloads whose `payload-format` declares it (format `2` onwards); an older payload's empty fields are reported as ambiguous rather than guessed. A connector renders a NULL as the `null` string handed to the shared writer — never as `""`, which the writer would faithfully record as an asserted empty string.

## The conformance bar for a new `IDataSource`

Three contract packs in `src/ToolUp.Platform.Tests/Contracts/` define what a connector has to do. They are shipped as source, not as a package: a connector's test project links the pack file and calls its `tests` entry point against its own implementation — the same adoption route as every other SDK contract pack (GP 12: an interface is proven by a second implementation passing the same bar).

| Pack | Proves | Bound by |
|---|---|---|
| [`IDataSourceContract`](../../src/ToolUp.Platform.Tests/Contracts/IDataSourceContract.fs) | the five members answer: `Kind`, `Connect`, `ListTables` (scoped to the source), `GetSchema`, `Query`, and `Error` for an unknown table | `InMemoryDataSource` and `GoogleAnalyticsDataSource`; the warehouse and file connectors bind re-expressions of it (`RemoteDataSourceContract`, env-gated; `LocalFileDataSourceContract`, always on) in `src/ToolUp.DataSources.Tests/Support/` |
| [`IDeclaresPayloadFormatContract`](../../src/ToolUp.Platform.Tests/Contracts/IDeclaresPayloadFormatContract.fs) | the payload format is declared explicitly (not by the `Csv` default), stably, and is a format the ingestor stores | every shipped connector |
| [`DataSourceFidelityContract`](../../src/ToolUp.Platform.Tests/Contracts/DataSourceFidelityContract.fs) | the connector loses nothing: the bytes say what the source held, and `GetSchema` tells the truth about them | every shipped connector, or a recorded reason it cannot be bound |

**A new connector passes all three.** The first two prove it answers; only the third proves it answers *correctly*, and the loss it catches is silent by construction — a NULL rendered as `""` reads back as a perfectly good empty string, and no consumer downstream can tell. That is why the bar is a pack rather than a review habit.

### The fidelity pack

The pack seeds nothing itself. It publishes a fixture table — `Fixture.standard`, six rows keyed by an `id` column, holding a NULL, an empty string, a bare hyphen, a quoted comma, and one wide row whose long value carries an embedded quote, comma and line break — and the binding writes that table into its own backend, in whatever way the backend is written: DDL against a local database, a file in the connector's own format, or a recorded result set. It then hands the pack a `FidelityTarget` naming the connector, a call context addressing the seeded table, the connector's own native-type classifier, and two honest answers about the source:

- **`Schema`** — `Declared` when the source states nullability and type (a catalogue, a file footer), `Inferred` when the connector guesses them from sampled text. A declared schema is held to the fixture's own nullability and coarse type; an inferred one only to consistency with the payload.
- **`EmptyString`** — `Distinguishes`, or `CannotState <reason>` for a source that cannot express an empty string at all (an uploaded CSV or workbook, where a blank cell is all the file says). The concession is not free: the pack then requires the blank to read as NULL — never as an asserted empty string — and prints the reason in the test name, so it is visible in every run.

The laws, each a named test:

1. the payload is the format the connector declares — for `Csv`, strict UTF-8 without a BOM, well-formed and rectangular RFC 4180; for `Json`, a parseable document; any other format is refused as unverifiable rather than passed on trust;
2. NULL and the empty string in the same column are distinguishable;
3. a hyphen is a value, not a NULL;
4. a quoted comma stays inside one field;
5. the wide row survives intact;
6. every fixture cell round-trips (numbers, dates and booleans compared by value, text exactly);
7. `GetSchema` names every emitted column;
8. `GetSchema`'s `Nullable` agrees with what `Query` emits — a column declared NOT NULL never emits a NULL;
9. no emitted value contradicts its column's declared type;
10. where the schema is declared, its nullability is the source's own;
11. where the schema is declared, its coarse type is the source's own.

The pack reads the payload with **its own** RFC 4180 parser, never the connector family's: a round trip read back through the writer's own reader agrees with itself by construction and proves nothing.

**The pack proves itself before it judges anyone.** Its `selfTests` list binds an honest in-process fixture source, and a set of deliberately defective twins — one that renders NULL as `""`, one that never quotes, one that lies about nullability, one about type, one about its payload format — each of which must be refused by the law it breaks, **by name**. Without them the pack would only prove that the connectors it already agrees with agree with it.

### How the shipped connectors are bound

The bindings live in [`src/ToolUp.DataSources.Tests/Tests/FidelityContractTests.fs`](../../src/ToolUp.DataSources.Tests/Tests/FidelityContractTests.fs), in three shapes, strongest first:

- **Live, local** — the connector's own `IDataSource` against a real backend in-process: `SqlDataSource` against an in-memory SQLite database, and the Csv, Excel and Parquet connectors against files written in their own formats into an in-memory `IBlobStorage`. The Csv and Excel bindings declare `CannotState`; Parquet states nullability and type in its own schema and binds as `Declared`.
- **Recorded** — for a warehouse with no local or emulated backend, the result set the vendor returns is recorded in the test and pushed through the connector's **own** render path: Redshift's Data API records through its public cell renderer, and the Synapse and Snowflake result sets through the shared ADO reader-to-CSV writer their `Query` calls. What is recorded is the vendor's side of the wire; what is tested is the connector's.
- **Unbound, with a reason** — BigQuery and Athena render inside private implementations that cannot be reached without the vendor; Google Analytics emits JSON in the GA4 report shape, which the pack does not decode into cells; `InMemoryDataSource` returns exactly the bytes it was seeded with. Each is listed with `unbound`, which shows as a pending case carrying its reason, so the gap is visible in every run and never an absence.

A new connector should reach for the strongest shape its backend allows. When a backend has neither a local engine nor a reachable render path, say so with `unbound` and the reason — and treat that reason as the first thing to fix, because an unbound connector is one the pack cannot protect.

## Authoring a connector

The general companion rules apply (see the companion-authoring guide in the repository's `CLAUDE.md`): its own `.fsproj` and vendor `PackageReference`s, credentials read per call through `ISecretStore` (never env vars or config files), a packed `README.md`, and an explicit dev-only vs production-ready statement in the file header. Specific to `IDataSource`:

- **Declare the payload format** by implementing `IDeclaresPayloadFormat`, even when it is `Csv`.
- **Render through the shared writer** in `ToolUp.DataSources.Common` rather than a private one, and hand it `null` for an absent cell. A source that genuinely cannot distinguish NULL from the empty string passes its blanks through `Csv.absentIfEmpty` so it never asserts an empty string the source did not state.
- **Keep the vendor's type name** on `ColumnInfo.DataType` and ship a `toColumnType` projection onto the coarse `ColumnType`, composed in front of the shared ANSI classifier.
- **Report nullability the source states**, or — for a format that states none — the evidence actually seen (a column is nullable when a blank was sampled).
- **Bind all three packs** in the connector's test project, the fidelity pack in the strongest shape the backend allows.
