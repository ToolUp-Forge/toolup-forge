// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TimeSeriesStoreTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

// ─── Phase 161 — ITimeSeriesStore conformance ──────────────────────────
//
// The always-on binding of the `ITimeSeriesStoreContract` pack to the
// in-memory default. The Timescale companion runs the same pack env-gated
// (`TimescaleTimeSeriesStoreTests`, skipped without a live Postgres conn).

[<Tests>]
let tests =
    testList "Phase 161 — ITimeSeriesStore" [
        ITimeSeriesStoreContract.tests "InMemoryTimeSeriesStore" (fun () -> InMemoryTimeSeriesStore.create ())
    ]