// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TimescaleTimeSeriesStoreTests

open System
open Expecto
open Npgsql
open ToolUp.Platform.Tests.Contracts
open ToolUp.TimeSeriesStores.Timescale

// ─── Phase 161 — Timescale ITimeSeriesStore (env-gated live arm) ────────
//
// Runs the shared `ITimeSeriesStoreContract` pack against a live
// TimescaleDB / PostgreSQL when `TOOLUP_TIMESCALE_CONN` is set to a
// connection string; unset, the arm reports a single skipped case so a
// fresh checkout is green without a database (mirrors the storage / secret
// companions' env-gated convention). Each contract factory call provisions
// a fresh uniquely-named table so the parallel cases don't alias.

[<Tests>]
let tests =
    match Environment.GetEnvironmentVariable "TOOLUP_TIMESCALE_CONN" with
    | null
    | "" ->
        testList "Phase 161 — TimescaleTimeSeriesStore" [
            ptestCase "skipped — TOOLUP_TIMESCALE_CONN not set" <| fun _ -> ()
        ]
    | conn ->
        let dataSource = NpgsqlDataSource.Create conn

        ITimeSeriesStoreContract.tests "TimescaleTimeSeriesStore" (fun () ->
            // Fresh table per factory call so concurrent contract cases are
            // isolated (the in-memory default gets a fresh object instead).
            let tbl = "ts_" + Guid.NewGuid().ToString("N")

            use cmd =
                dataSource.CreateCommand(
                    $"CREATE TABLE {tbl} (scope text NOT NULL, series text NOT NULL, "
                    + "ts timestamptz NOT NULL, value double precision NOT NULL)"
                )

            cmd.ExecuteNonQuery() |> ignore
            TimescaleTimeSeriesStore.createWithTable dataSource tbl)