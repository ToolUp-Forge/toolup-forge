# Phase 161 — `ITimeSeriesStore` substrate

**What changes.** A new opt-in storage seam for high-frequency
numeric/analytical series (IoT readings, operational metrics, financial
ticks) — distinct from `IEventStore` (audit/domain events, wrong shape for
high-frequency analytical series).

New surface:
- `ITimeSeriesStore` (`ToolUp.Platform.Core`): `Append` / `QueryRange`
  (range + optional downsample) / `ListSeries`, all async, scope-isolated,
  six-rule compliant (GP 12). Downsample is **data** (`TimeSeriesDownsample
  = { Bucket; Aggregation }`), not a callback.
- `InMemoryTimeSeriesStore` — dev/test default (unbounded in-memory, marked
  dev-only).
- `ToolUp.TimeSeriesStores.Timescale` — a TimescaleDB/PostgreSQL companion
  over Npgsql (GP 1 isolation; GP 2 — Npgsql is MIT, no paid default),
  pushing the downsample to `time_bucket`.
- `ServerConfig.TimeSeriesStore: TimeSeriesStoreMode` (`NoTimeSeriesStore`
  default / `InMemoryTimeSeries` / `CustomTimeSeriesStore`).

**Consumer action: none by default (GP 11 / GP 13).** `NoTimeSeriesStore`
(the default) registers nothing — zero cost. A deployment opts in only if it
has high-frequency series.

## Adopt (opt-in)

Dev / single-instance:

```fsharp
ServerConfig.defaults with
    TimeSeriesStore = InMemoryTimeSeries   // dev/test only — unbounded in-memory
```

Production (TimescaleDB):

```fsharp
open Npgsql
open ToolUp.TimeSeriesStores.Timescale

// provision the toolup_timeseries hypertable first — see the companion README
let dataSource = NpgsqlDataSource.Create connString
// register the companion singleton + select CustomTimeSeriesStore:
services.AddSingleton<ITimeSeriesStore>(TimescaleTimeSeriesStore.create dataSource) |> ignore
ServerConfig.defaults with TimeSeriesStore = CustomTimeSeriesStore
```

Then resolve `ITimeSeriesStore` from DI in your module's server code.

## Portability

`ITimeSeriesStore` satisfies the six portability rules; the
`ITimeSeriesStoreContract` conformance pack (`ToolUp.Platform.Tests`)
validates any implementation — round-trip, half-open `[from, until)` bounds,
downsample-aggregation correctness, and structural scope isolation (GP 4).
The in-memory default runs it always-on; the Timescale companion runs it
env-gated (`TOOLUP_TIMESCALE_CONN`). Both produce byte-identical downsample
output because `time_bucket(bucket, ts, origin => from)` aligns to the same
`from` bound as the in-memory `TimeSeriesDownsample.apply`.

## Rollback

Set `TimeSeriesStore = NoTimeSeriesStore` (the default) and drop the
companion `PackageReference`. No persisted-state or wire change.
