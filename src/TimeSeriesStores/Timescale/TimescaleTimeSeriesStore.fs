// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.TimeSeriesStores.Timescale

open System
open System.Text
open Npgsql
open ToolUp.Platform

// ─── Phase 161 — TimescaleDB / PostgreSQL ITimeSeriesStore ──────────────
//
// A distributed-ready `ITimeSeriesStore` over an `NpgsqlDataSource`. Points
// land in one table (a Timescale hypertable for production); range queries
// push the downsample to the engine via `time_bucket(bucket, ts, origin =>
// from)`, so bucket boundaries are byte-identical to the in-memory default's
// `TimeSeriesDownsample.apply` (both align to the query `from`). The
// aggregate maps directly: `avg` / `sum` / `min` / `max` / `count`, and
// Timescale's `first(value, ts)` / `last(value, ts)` for `First` / `Last`.
//
// **GP 1** — the Npgsql dependency is isolated to this companion; the SDK
// core carries no driver. **GP 12** — stateless between calls (the data
// source is a connection pool, no per-call state), scope-isolated by a
// `scope` column (a structural partition, GP 4), identity-by-value.
//
// **Schema (the deployment provisions it; see README).** A single table —
// for production make it a Timescale hypertable on `ts`:
//
//   CREATE TABLE toolup_timeseries (
//       scope  text             NOT NULL,
//       series text             NOT NULL,
//       ts     timestamptz      NOT NULL,
//       value  double precision NOT NULL);
//   SELECT create_hypertable('toolup_timeseries', 'ts');
//   CREATE INDEX ON toolup_timeseries (scope, series, ts DESC);
//
// The table/column names are overridable via the ctor for a deployment with
// its own naming convention.

/// TimescaleDB / PostgreSQL-backed `ITimeSeriesStore`. `dataSource` is built
/// by the deployment from its connection string (`NpgsqlDataSource.Create`);
/// `table` defaults to `toolup_timeseries`.
type TimescaleTimeSeriesStore(dataSource: NpgsqlDataSource, ?table: string) =
    let table = defaultArg table "toolup_timeseries"

    // Map an aggregation to its SQL aggregate over the `value` column. First
    // / Last use Timescale's `first(value, ts)` / `last(value, ts)` so the
    // bucket's earliest / latest point by timestamp is selected — matching
    // the in-memory fold's head / last over the ascending bucket.
    let sqlAggregate =
        function
        | TimeSeriesAggregation.Average -> "avg(value)"
        | TimeSeriesAggregation.Sum -> "sum(value)"
        | TimeSeriesAggregation.Min -> "min(value)"
        | TimeSeriesAggregation.Max -> "max(value)"
        | TimeSeriesAggregation.Count -> "count(value)::double precision"
        | TimeSeriesAggregation.First -> "first(value, ts)"
        | TimeSeriesAggregation.Last -> "last(value, ts)"

    let readPoints (reader: System.Data.Common.DbDataReader) : Async<TimeSeriesPoint list> = async {
        let acc = ResizeArray<TimeSeriesPoint>()
        let mutable go = true

        while go do
            let! has = reader.ReadAsync() |> Async.AwaitTask

            if has then
                let ts = reader.GetFieldValue<DateTime>(0)

                acc.Add {
                    Timestamp = DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc))
                    Value = reader.GetDouble(1)
                }
            else
                go <- false

        return List.ofSeq acc
    }

    interface ITimeSeriesStore with
        member _.Append(scopeId: string, series: string, points: TimeSeriesPoint list) = async {
            match points with
            | [] -> return Ok() // empty append is a no-op (contract)
            | _ ->
                try
                    let sb = StringBuilder($"INSERT INTO {table} (scope, series, ts, value) VALUES ")

                    points
                    |> List.iteri (fun i _ ->
                        if i > 0 then
                            sb.Append(", ") |> ignore

                        sb.Append($"(@s, @series, @t{i}, @v{i})") |> ignore)

                    use cmd = dataSource.CreateCommand(sb.ToString())
                    cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                    cmd.Parameters.AddWithValue("series", series) |> ignore

                    points
                    |> List.iteri (fun i p ->
                        cmd.Parameters.AddWithValue($"t{i}", p.Timestamp.UtcDateTime) |> ignore
                        cmd.Parameters.AddWithValue($"v{i}", p.Value) |> ignore)

                    let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                    return Ok()
                with ex ->
                    return Error(TimeSeriesError.StorageFailure ex.Message)
        }

        member _.QueryRange(scopeId, series, from, until, downsample) = async {
            if until <= from then
                return Error(TimeSeriesError.InvalidRange "until must be strictly after from")
            else
                match downsample with
                | Some d when d.Bucket <= TimeSpan.Zero ->
                    return Error(TimeSeriesError.InvalidRange "downsample bucket must be a positive duration")
                | _ ->
                    try
                        let sql =
                            match downsample with
                            | None ->
                                $"SELECT ts, value FROM {table} "
                                + "WHERE scope = @s AND series = @series AND ts >= @from AND ts < @until "
                                + "ORDER BY ts"
                            | Some d ->
                                // time_bucket aligns to @from (origin), matching
                                // TimeSeriesDownsample.apply; bucket start is the
                                // emitted timestamp.
                                $"SELECT time_bucket(@bucket, ts, @from) AS bucket, {sqlAggregate d.Aggregation} "
                                + $"FROM {table} "
                                + "WHERE scope = @s AND series = @series AND ts >= @from AND ts < @until "
                                + "GROUP BY bucket ORDER BY bucket"

                        use cmd = dataSource.CreateCommand(sql)
                        cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                        cmd.Parameters.AddWithValue("series", series) |> ignore
                        cmd.Parameters.AddWithValue("from", from.UtcDateTime) |> ignore
                        cmd.Parameters.AddWithValue("until", until.UtcDateTime) |> ignore

                        match downsample with
                        | Some d -> cmd.Parameters.AddWithValue("bucket", d.Bucket) |> ignore
                        | None -> ()

                        use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                        let! points = readPoints reader
                        return Ok points
                    with ex ->
                        return Error(TimeSeriesError.StorageFailure ex.Message)
        }

        member _.DeleteSeries(scopeId: string, series: string) = async {
            // Idempotent by construction — a DELETE that matches no rows
            // affects 0 rows and still returns Ok. Scope-isolated by the
            // `scope = @s` predicate (GP 4).
            try
                use cmd =
                    dataSource.CreateCommand($"DELETE FROM {table} WHERE scope = @s AND series = @series")

                cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                cmd.Parameters.AddWithValue("series", series) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                return Ok()
            with ex ->
                return Error(TimeSeriesError.StorageFailure ex.Message)
        }

        member _.ListSeries(scopeId: string) = async {
            try
                use cmd =
                    dataSource.CreateCommand($"SELECT DISTINCT series FROM {table} WHERE scope = @s")

                cmd.Parameters.AddWithValue("s", scopeId) |> ignore
                use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                let acc = ResizeArray<string>()
                let mutable go = true

                while go do
                    let! has = reader.ReadAsync() |> Async.AwaitTask

                    if has then acc.Add(reader.GetString 0) else go <- false

                return List.ofSeq acc
            with _ ->
                // ListSeries has no error channel (contract) — an unreachable
                // backend reads as "no series" rather than throwing across the
                // boundary; QueryRange/Append surface storage failures.
                return []
        }

module TimescaleTimeSeriesStore =
    /// Construct a Timescale-backed `ITimeSeriesStore` over `dataSource`
    /// (built by the deployment via `NpgsqlDataSource.Create connString`).
    /// The `toolup_timeseries` table must exist (see the companion README).
    let create (dataSource: NpgsqlDataSource) : ITimeSeriesStore =
        TimescaleTimeSeriesStore(dataSource) :> ITimeSeriesStore

    /// As `create`, with a custom table name.
    let createWithTable (dataSource: NpgsqlDataSource) (table: string) : ITimeSeriesStore =
        TimescaleTimeSeriesStore(dataSource, table) :> ITimeSeriesStore