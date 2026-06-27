// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ITimeSeriesStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 161 — ITimeSeriesStore conformance pack ──────────────────────
//
// Bound to BOTH the in-memory default and any companion (the Timescale
// companion runs this env-gated). Asserts the portable contract: append +
// range round-trip ascending within a series, half-open `[from, until)`
// bounds, correct + identically-bucketed downsample aggregation, structural
// scope isolation (GP 4), and the documented error shapes.

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

let private at (seconds: float) (value: float) : TimeSeriesPoint = {
    Timestamp = t0.AddSeconds seconds
    Value = value
}

let private ok =
    function
    | Ok v -> v
    | Error e -> failtestf "expected Ok; got %s" (TimeSeriesError.describe e)

let tests (name: string) (factory: unit -> ITimeSeriesStore) =
    testList $"{name} — ITimeSeriesStore contract" [
        testCaseAsync "Append + QueryRange round-trips ascending within a series"
        <| async {
            let store = factory ()
            // Append out of order; expect ascending-by-timestamp back.
            let! _ = store.Append("scope-a", "temperature", [ at 30.0 3.0; at 10.0 1.0; at 20.0 2.0 ])
            let! result = store.QueryRange("scope-a", "temperature", t0, t0.AddSeconds 60.0, None)
            let points = ok result
            Expect.equal (points |> List.map _.Value) [ 1.0; 2.0; 3.0 ] "ascending by timestamp"
        }

        testCaseAsync "QueryRange is half-open [from, until) — excludes the upper bound"
        <| async {
            let store = factory ()
            let! _ = store.Append("scope-a", "s", [ at 0.0 1.0; at 10.0 2.0; at 20.0 3.0 ])
            // [t0+10, t0+20) — includes the t0+10 point, excludes t0+20 and t0.
            let! result = store.QueryRange("scope-a", "s", t0.AddSeconds 10.0, t0.AddSeconds 20.0, None)
            Expect.equal (ok result |> List.map _.Value) [ 2.0 ] "only the in-range point"
        }

        testCaseAsync "Downsample buckets aggregate correctly (Average / Sum / Count), aligned to from"
        <| async {
            let store = factory ()
            // bucket0 [t0, t0+10): values 10, 20 ; bucket1 [t0+10, t0+20): 30, 50
            let! _ = store.Append("scope-a", "s", [ at 0.0 10.0; at 5.0 20.0; at 10.0 30.0; at 15.0 50.0 ])
            let from = t0
            let until = t0.AddSeconds 20.0
            let bucket = TimeSpan.FromSeconds 10.0

            let! avg =
                store.QueryRange(
                    "scope-a",
                    "s",
                    from,
                    until,
                    Some {
                        Bucket = bucket
                        Aggregation = TimeSeriesAggregation.Average
                    }
                )

            let avgPts = ok avg

            Expect.equal
                (avgPts |> List.map _.Timestamp)
                [ t0; t0.AddSeconds 10.0 ]
                "two buckets, stamped at bucket start"

            Expect.equal (avgPts |> List.map _.Value) [ 15.0; 40.0 ] "per-bucket averages"

            let! sum =
                store.QueryRange(
                    "scope-a",
                    "s",
                    from,
                    until,
                    Some {
                        Bucket = bucket
                        Aggregation = TimeSeriesAggregation.Sum
                    }
                )

            Expect.equal (ok sum |> List.map _.Value) [ 30.0; 80.0 ] "per-bucket sums"

            let! count =
                store.QueryRange(
                    "scope-a",
                    "s",
                    from,
                    until,
                    Some {
                        Bucket = bucket
                        Aggregation = TimeSeriesAggregation.Count
                    }
                )

            Expect.equal (ok count |> List.map _.Value) [ 2.0; 2.0 ] "per-bucket counts"
        }

        testCaseAsync "Downsample Min / Max fold correctly"
        <| async {
            let store = factory ()
            let! _ = store.Append("scope-a", "s", [ at 0.0 7.0; at 5.0 3.0; at 10.0 9.0; at 15.0 4.0 ])

            let! mn =
                store.QueryRange(
                    "scope-a",
                    "s",
                    t0,
                    t0.AddSeconds 20.0,
                    Some {
                        Bucket = TimeSpan.FromSeconds 10.0
                        Aggregation = TimeSeriesAggregation.Min
                    }
                )

            Expect.equal (ok mn |> List.map _.Value) [ 3.0; 4.0 ] "per-bucket minima"

            let! mx =
                store.QueryRange(
                    "scope-a",
                    "s",
                    t0,
                    t0.AddSeconds 20.0,
                    Some {
                        Bucket = TimeSpan.FromSeconds 10.0
                        Aggregation = TimeSeriesAggregation.Max
                    }
                )

            Expect.equal (ok mx |> List.map _.Value) [ 7.0; 9.0 ] "per-bucket maxima"
        }

        testCaseAsync "Scope isolation — a query in scope A never returns scope B's points (GP 4)"
        <| async {
            let store = factory ()
            let! _ = store.Append("scope-a", "s", [ at 0.0 1.0 ])
            let! _ = store.Append("scope-b", "s", [ at 0.0 999.0 ])
            let! a = store.QueryRange("scope-a", "s", t0, t0.AddSeconds 10.0, None)
            Expect.equal (ok a |> List.map _.Value) [ 1.0 ] "scope A sees only its own point"
            let! b = store.QueryRange("scope-b", "s", t0, t0.AddSeconds 10.0, None)
            Expect.equal (ok b |> List.map _.Value) [ 999.0 ] "scope B sees only its own point"
        }

        testCaseAsync "ListSeries returns the series that have points under a scope"
        <| async {
            let store = factory ()
            let! _ = store.Append("scope-a", "cpu", [ at 0.0 1.0 ])
            let! _ = store.Append("scope-a", "mem", [ at 0.0 2.0 ])
            let! _ = store.Append("scope-b", "disk", [ at 0.0 3.0 ])
            let! series = store.ListSeries "scope-a"
            Expect.containsAll series [ "cpu"; "mem" ] "both scope-a series listed"
            Expect.isFalse (series |> List.contains "disk") "scope-b's series is not listed under scope-a"
        }

        testCaseAsync "Empty append is a no-op Ok"
        <| async {
            let store = factory ()
            let! result = store.Append("scope-a", "s", [])
            Expect.isOk result "empty append succeeds"
            let! series = store.ListSeries "scope-a"
            Expect.isEmpty series "no series created by an empty append"
        }

        testCaseAsync "until <= from → InvalidRange"
        <| async {
            let store = factory ()

            match! store.QueryRange("scope-a", "s", t0.AddSeconds 10.0, t0, None) with
            | Error(TimeSeriesError.InvalidRange _) -> ()
            | other -> failtestf "expected InvalidRange; got %A" other
        }

        testCaseAsync "Non-positive downsample bucket → InvalidRange"
        <| async {
            let store = factory ()

            let bad = {
                Bucket = TimeSpan.Zero
                Aggregation = TimeSeriesAggregation.Average
            }

            match! store.QueryRange("scope-a", "s", t0, t0.AddSeconds 10.0, Some bad) with
            | Error(TimeSeriesError.InvalidRange _) -> ()
            | other -> failtestf "expected InvalidRange; got %A" other
        }
    ]