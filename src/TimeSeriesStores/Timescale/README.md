# ToolUp.TimeSeriesStores.Timescale

TimescaleDB / PostgreSQL-backed `ITimeSeriesStore` for ToolUp.Platform
(Phase 161). High-frequency numeric/analytical series (IoT readings,
operational metrics, financial ticks) persisted over [Npgsql], with range
queries pushed to the engine and downsampling via Timescale's `time_bucket`.

GP 1 — the Npgsql dependency is isolated to this companion; it never reaches
`ToolUp.Platform.*`. GP 2 — Npgsql is MIT-licensed (no paid default).
Server-only companion.

## Schema (provision before use)

The store reads/writes one table — for production make it a Timescale
hypertable on `ts`:

```sql
CREATE TABLE toolup_timeseries (
    scope  text             NOT NULL,
    series text             NOT NULL,
    ts     timestamptz      NOT NULL,
    value  double precision NOT NULL);

-- Production: turn it into a hypertable (TimescaleDB extension).
SELECT create_hypertable('toolup_timeseries', 'ts');

CREATE INDEX ON toolup_timeseries (scope, series, ts DESC);
```

`first(value, ts)` / `last(value, ts)` (used for the `First` / `Last`
aggregations) are TimescaleDB aggregates — install the extension. The other
aggregations (`Average`/`Sum`/`Min`/`Max`/`Count`) are plain PostgreSQL and
work on vanilla Postgres too. The table name is overridable via
`createWithTable`.

## Quick start

```fsharp
open Npgsql
open ToolUp.TimeSeriesStores.Timescale

let dataSource = NpgsqlDataSource.Create "Host=...;Database=...;Username=...;Password=..."
let store = TimescaleTimeSeriesStore.create dataSource
// Wire it as the deployment's ITimeSeriesStore:
//   ServerConfig.TimeSeriesStore = CustomTimeSeriesStore
//   services.AddSingleton<ITimeSeriesStore>(store)
```

`time_bucket(bucket, ts, origin => from)` aligns buckets to the query's
`from` bound, so this backend's downsample output is byte-identical to the
in-memory default's `TimeSeriesDownsample.apply` — both pass the same
`ITimeSeriesStoreContract` conformance pack.

## Verification

The store requires a live PostgreSQL/TimescaleDB (no offline fake). Mirrors
the env-gated live-arm convention of the storage / secret companions. The
`ITimeSeriesStoreContract` pack runs against this store in
`ToolUp.Platform.Tests` when `TOOLUP_TIMESCALE_CONN` is set to a connection
string (the test provisions + truncates a scratch `toolup_timeseries`
table); unset, the arm reports skipped, so a fresh checkout is green without
a database. The in-memory default runs the same pack always-on.

## License

Apache-2.0.

[Npgsql]: https://www.npgsql.org/
