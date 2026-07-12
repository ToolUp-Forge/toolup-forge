// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Concurrent

// ─── Phase 161 — InMemoryTimeSeriesStore (dev/test default) ─────────────
//
// **Dev-only.** An in-process `ITimeSeriesStore` for local development,
// tests, and single-instance demos. It holds every point in memory with no
// cardinality cap and no persistence, so a process restart loses all series
// and a high-ingest production workload OOMs the host. A production
// deployment composes a companion (`ToolUp.TimeSeriesStores.Timescale`, …)
// against the same `ITimeSeriesStore` contract.
//
// Scope isolation (GP 4) is structural — points are partitioned by `scopeId`
// in the outer dictionary, so a `QueryRange` / `ListSeries` in one scope can
// never observe another's. Folding + bucketing use the canonical
// `TimeSeriesDownsample.apply` so this impl and any SQL backend produce
// identical results for the same query (the `ITimeSeriesStoreContract` pins
// it).

/// Dev/test in-memory `ITimeSeriesStore`. **Not for production** — unbounded
/// in-memory retention, no durability. Composed by
/// `ServerConfig.TimeSeriesStore = InMemoryTimeSeriesStore`.
type InMemoryTimeSeriesStore() =
    // scopeId -> series -> points (append order; sorted on query).
    let data =
        ConcurrentDictionary<string, ConcurrentDictionary<string, ResizeArray<TimeSeriesPoint>>>()

    let seriesMapFor (scopeId: string) =
        data.GetOrAdd(scopeId, (fun _ -> ConcurrentDictionary<string, ResizeArray<TimeSeriesPoint>>()))

    interface ITimeSeriesStore with
        member _.Append(scopeId: string, series: string, points: TimeSeriesPoint list) = async {
            match points with
            | [] -> return Ok() // empty append is a no-op (contract)
            | _ ->
                let sm = seriesMapFor scopeId
                let bucket = sm.GetOrAdd(series, (fun _ -> ResizeArray<TimeSeriesPoint>()))
                // Lock the per-series list — ResizeArray is not thread-safe and
                // two scopes/series never share a list, so contention is per
                // (scope, series) only.
                lock bucket (fun () -> bucket.AddRange points)
                return Ok()
        }

        member _.QueryRange(scopeId, series, from, until, downsample) = async {
            if until <= from then
                return Error(TimeSeriesError.InvalidRange "until must be strictly after from")
            else
                match downsample with
                | Some d when d.Bucket <= System.TimeSpan.Zero ->
                    return Error(TimeSeriesError.InvalidRange "downsample bucket must be a positive duration")
                | _ ->
                    let snapshot =
                        match data.TryGetValue scopeId with
                        | true, sm ->
                            match sm.TryGetValue series with
                            | true, bucket -> lock bucket (fun () -> bucket |> Seq.toList)
                            | _ -> []
                        | _ -> []

                    let inRange =
                        snapshot
                        |> List.filter (fun p -> p.Timestamp >= from && p.Timestamp < until)
                        |> List.sortBy _.Timestamp

                    match downsample with
                    | None -> return Ok inRange
                    | Some d -> return Ok(TimeSeriesDownsample.apply from d inRange)
        }

        member _.ListSeries(scopeId: string) = async {
            match data.TryGetValue scopeId with
            | true, sm -> return sm.Keys |> List.ofSeq
            | _ -> return []
        }

        member _.DeleteSeries(scopeId: string, series: string) = async {
            // Idempotent — dropping the per-series slot; an absent scope or
            // series is a no-op Ok. Scope isolation is structural (the outer
            // dictionary is keyed by scopeId), so this can never reach
            // another scope's identically-named series.
            match data.TryGetValue scopeId with
            | true, sm -> sm.TryRemove series |> ignore
            | _ -> ()

            return Ok()
        }

module InMemoryTimeSeriesStore =
    /// Construct the dev/test in-memory `ITimeSeriesStore`.
    let create () : ITimeSeriesStore =
        InMemoryTimeSeriesStore() :> ITimeSeriesStore