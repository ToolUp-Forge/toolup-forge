// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 161 — ITimeSeriesStore ───────────────────────────────────────
//
// A dedicated storage seam for high-frequency numerical / analytical series
// (IoT readings, operational metrics, financial ticks). Today such data
// lands as `ModuleEvent` rows or in `IEventStore` — fine for low-cardinality
// observability, wrong for high-frequency analytical series (no range +
// downsample query shape, unbounded row growth in an event log). This
// interface gives those deployments a purpose-built seam without forcing the
// event store to do a job it is wrong for.
//
// **Default impl** — `InMemoryTimeSeriesStore` (dev/test, cardinality-bounded,
// marked dev-only). A distributed deployment composes a companion
// (`ToolUp.TimeSeriesStores.Timescale`, …) against the same contract; the
// `ITimeSeriesStoreContract` conformance pack validates any implementation.
//
// **Six portability rules (GP 12).**
// 1. *Identity by value* — `series` is a string, points are records, scope is
//    a string id; no live handles cross the boundary.
// 2. *Async at every boundary* — every method returns `Async<_>`.
// 3. *Retry / behaviour as data* — failures surface as `Result.Error
//    TimeSeriesError`; the downsample directive is a `TimeSeriesDownsample`
//    record (bucket + aggregation), never a caller-supplied callback, so any
//    backend can honour it (a SQL `time_bucket`, an in-memory fold, …).
// 4. *Stateless handlers between invocations* — each call carries its full
//    scope + series + range; the store caches nothing across calls.
// 5. *No cross-shard ordering* — ordering is promised only *within* a
//    (`scopeId`, `series`) pair (the natural shard key), ascending by
//    timestamp. No ordering is promised across series or scopes.
// 6. *Precision at the lower bound* — timestamps are `DateTimeOffset`; a
//    backend coarser than its inputs (e.g. second-granular) documents that in
//    its companion README. Downsample buckets align to the query's `from`
//    bound so every backend produces identical bucket boundaries.

/// A single timestamped numeric sample in a series.
type TimeSeriesPoint = {
    Timestamp: DateTimeOffset
    Value: float
}

/// Aggregation applied to the points falling in a downsample bucket.
/// `[<RequireQualifiedAccess>]` — `Min` / `Max` / `Count` / `First` / `Last`
/// / `Sum` / `Average` are generic enough to collide with other DUs in the
/// widely-opened `ToolUp.Platform` namespace.
[<RequireQualifiedAccess>]
type TimeSeriesAggregation =
    | Average
    | Sum
    | Min
    | Max
    | Count
    | First
    | Last

module TimeSeriesAggregation =
    /// Stable case-name string (companion SQL mapping, telemetry).
    let name =
        function
        | TimeSeriesAggregation.Average -> "Average"
        | TimeSeriesAggregation.Sum -> "Sum"
        | TimeSeriesAggregation.Min -> "Min"
        | TimeSeriesAggregation.Max -> "Max"
        | TimeSeriesAggregation.Count -> "Count"
        | TimeSeriesAggregation.First -> "First"
        | TimeSeriesAggregation.Last -> "Last"

    /// Fold a non-empty bucket of points (already ascending by timestamp)
    /// into its aggregate value. The shared definition every in-process
    /// backend uses, so an implementation that does the fold itself (rather
    /// than pushing it to the store engine) stays contract-identical.
    let fold (agg: TimeSeriesAggregation) (points: TimeSeriesPoint list) : float =
        match points with
        | [] -> 0.0
        | _ ->
            let values = points |> List.map _.Value

            match agg with
            | TimeSeriesAggregation.Average -> List.average values
            | TimeSeriesAggregation.Sum -> List.sum values
            | TimeSeriesAggregation.Min -> List.min values
            | TimeSeriesAggregation.Max -> List.max values
            | TimeSeriesAggregation.Count -> float (List.length values)
            | TimeSeriesAggregation.First -> (List.head points).Value
            | TimeSeriesAggregation.Last -> (List.last points).Value

/// Downsample directive — bucket width + aggregation, expressed as **data**
/// (GP 12 rule 3) so any backend honours it. Buckets align to the query's
/// `from` bound; bucket `n` covers `[from + n·Bucket, from + (n+1)·Bucket)`
/// and is emitted with `Timestamp = from + n·Bucket`. Empty buckets are
/// omitted.
type TimeSeriesDownsample = {
    Bucket: TimeSpan
    Aggregation: TimeSeriesAggregation
}

module TimeSeriesDownsample =
    /// Canonical bucketing — the single definition every backend that folds
    /// in-process shares (the in-memory default; a SQL backend reproduces it
    /// with `time_bucket(bucket, ts, origin => from)` so results are
    /// byte-identical). `points` must already be filtered to `[from, until)`
    /// and sorted ascending by timestamp. Bucket `n` covers `[from + n·Bucket,
    /// from + (n+1)·Bucket)`; each non-empty bucket folds to one point stamped
    /// at its start. Empty buckets are omitted; output is ascending.
    let apply (from: DateTimeOffset) (d: TimeSeriesDownsample) (points: TimeSeriesPoint list) : TimeSeriesPoint list =
        let bucketTicks = d.Bucket.Ticks

        points
        |> List.groupBy (fun p -> (p.Timestamp - from).Ticks / bucketTicks)
        |> List.sortBy fst
        |> List.map (fun (idx, pts) -> {
            Timestamp = from.AddTicks(idx * bucketTicks)
            Value = TimeSeriesAggregation.fold d.Aggregation (pts |> List.sortBy _.Timestamp)
        })

/// Failure modes for `ITimeSeriesStore`. `[<RequireQualifiedAccess>]` —
/// `InvalidRange` / `StorageFailure` are generic enough to collide with
/// other DUs in the widely-opened `ToolUp.Platform` namespace (e.g.
/// `DataObjectError.StorageFailure`), so qualified access keeps them out of
/// unqualified scope.
[<RequireQualifiedAccess>]
type TimeSeriesError =
    /// The range was malformed (`until` <= `from`, or a non-positive
    /// downsample bucket).
    | InvalidRange of reason: string
    /// The backing store operation threw / the backend was unreachable.
    | StorageFailure of reason: string

module TimeSeriesError =
    let describe =
        function
        | TimeSeriesError.InvalidRange r -> $"invalid time-series range: {r}"
        | TimeSeriesError.StorageFailure r -> $"time-series storage failure: {r}"

/// Scope-isolated storage for high-frequency numeric series (GP 4: a query
/// in scope A can never observe scope B's points — the scope is a structural
/// partition, not a post-filter convention). See the six-rule note above.
type ITimeSeriesStore =
    /// Append `points` to (`scopeId`, `series`). Idempotency is not promised
    /// (appending the same point twice stores it twice) — callers that need
    /// dedup carry their own key. An empty `points` list is a no-op `Ok`.
    abstract Append:
        scopeId: string * series: string * points: TimeSeriesPoint list -> Async<Result<unit, TimeSeriesError>>

    /// Range query over [`from`, `until`) for (`scopeId`, `series`), ascending
    /// by timestamp. With `downsample = None` the raw points are returned;
    /// with `Some d` the points are bucketed (aligned to `from`) and each
    /// non-empty bucket is folded to one point per `d.Aggregation`.
    abstract QueryRange:
        scopeId: string *
        series: string *
        from: DateTimeOffset *
        until: DateTimeOffset *
        downsample: TimeSeriesDownsample option ->
            Async<Result<TimeSeriesPoint list, TimeSeriesError>>

    /// The series names that have at least one point under `scopeId`.
    abstract ListSeries: scopeId: string -> Async<string list>

    /// Phase 439 — delete every point of (`scopeId`, `series`). Idempotent:
    /// deleting an absent series is `Ok ()`, not an error. Scope-isolated
    /// (GP 4) — a delete in scope A can never remove scope B's identically-
    /// named series. After a successful delete a `QueryRange` for the series
    /// returns `[]` and `ListSeries` no longer lists it. Whole-series only —
    /// range/partial deletion is intentionally out of scope (a retention
    /// sweep re-appends what it wants to keep; the series key is the shard).
    abstract DeleteSeries: scopeId: string * series: string -> Async<Result<unit, TimeSeriesError>>