// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IDataIngestorContract

open System
open Expecto
open ToolUp.Platform

// ─── IDataIngestor contract pack — Phase 1042 ─────────────────────
//
// Parametrised laws for any `IDataIngestor`. Phase 1042 gave the seam its
// second production implementation — the delivery-completed recording
// decorator (`DeliveryCompleted.recording`) over the platform's
// `DataIngestor` — so the seam carries a pack, and both bind it (in
// `InProcess/DeliveryCompletedTests.fs`). A decorator that changed what a
// caller sees of an ingestion would break a module's delivery route as
// surely as a broken ingestor would.
//
// **Coverage:**
//   1. A seeded table settles as a committed run naming its scope, source
//      and table, with the object its payload landed in.
//   2. A table the source cannot serve is still a RECORDED run (`Ok`),
//      failed with its reason — not a setup error.
//   3. An unconfigured source is a setup error (`Error`), recorded as no
//      run the caller can name.
//   4. `GetRecentRuns` reports the runs `RunIngestion` returned, newest
//      first, by run id.
//   5. `GetRecentRuns` honours its count and is empty for an unknown
//      source.
//   6. Runs are scoped: one scope's history is invisible from another.

/// An ingestor under test over a fresh in-memory composition, and how to
/// configure a source in a scope with content for some of its tables.
type IngestorUnderTest = {
    Ingestor: IDataIngestor
    /// Configure `sourceId` in `scopeId` and seed each of `tables` with a
    /// small CSV payload.
    Seed: string -> DataSourceId -> string list -> unit
}

let tests (name: string) (factory: unit -> IngestorUnderTest) =

    let scope () = "scope-" + Guid.NewGuid().ToString("N")

    let committed (result: Result<IngestionRun, IngestionError>) =
        match result with
        | Ok run -> run
        | Error e -> failtestf "%s: expected a recorded run, got the setup error %A" name e

    testList $"{name} — IDataIngestor contract" [

        testCaseAsync "1. a seeded table settles as a committed run naming its scope, source and table"
        <| async {
            let sut = factory ()
            let scopeId = scope ()
            sut.Seed scopeId "sales" [ "orders" ]
            let! result = sut.Ingestor.RunIngestion(scopeId, "sales", "orders")
            let run = committed result
            Expect.equal run.Status IngestionStatus.Succeeded "committed"
            Expect.equal run.ScopeId scopeId "in the scope it was asked for"
            Expect.equal run.DataSourceId "sales" "for the source"
            Expect.equal run.Table "orders" "and the table"
            Expect.isSome run.ResultObjectId "naming where the payload landed"
            Expect.isNone run.Error "with no error"
            Expect.isSome run.CompletedAt "and settled"
        }

        testCaseAsync "2. a table the source cannot serve is a recorded, failed run with its reason"
        <| async {
            let sut = factory ()
            let scopeId = scope ()
            sut.Seed scopeId "sales" [ "orders" ]
            let! result = sut.Ingestor.RunIngestion(scopeId, "sales", "missing")
            let run = committed result
            Expect.equal run.Status IngestionStatus.Failed "failed"
            Expect.isSome run.Error "with its reason"
            Expect.isNone run.ResultObjectId "landing nowhere"
        }

        testCaseAsync "3. an unconfigured source is a setup error"
        <| async {
            let sut = factory ()
            let! result = sut.Ingestor.RunIngestion(scope (), "nowhere", "orders")
            Expect.isError result "no source, no attempt to name"
        }

        testCaseAsync "4. GetRecentRuns reports the returned runs, newest first"
        <| async {
            let sut = factory ()
            let scopeId = scope ()
            sut.Seed scopeId "sales" [ "orders" ]
            let! first = sut.Ingestor.RunIngestion(scopeId, "sales", "orders")
            do! Async.Sleep 20
            let! second = sut.Ingestor.RunIngestion(scopeId, "sales", "orders")
            let! recent = sut.Ingestor.GetRecentRuns(scopeId, "sales", 10)

            Expect.equal
                (recent |> List.map _.RunId)
                [ (committed second).RunId; (committed first).RunId ]
                "both runs, the newer first"
        }

        testCaseAsync "5. GetRecentRuns honours its count and is empty for an unknown source"
        <| async {
            let sut = factory ()
            let scopeId = scope ()
            sut.Seed scopeId "sales" [ "orders" ]
            let! _ = sut.Ingestor.RunIngestion(scopeId, "sales", "orders")
            do! Async.Sleep 20
            let! newest = sut.Ingestor.RunIngestion(scopeId, "sales", "orders")
            let! one = sut.Ingestor.GetRecentRuns(scopeId, "sales", 1)
            Expect.equal (one |> List.map _.RunId) [ (committed newest).RunId ] "only the newest"
            let! none = sut.Ingestor.GetRecentRuns(scopeId, "unknown", 10)
            Expect.isEmpty none "an unknown source has no history"
        }

        testCaseAsync "6. one scope's runs are invisible from another"
        <| async {
            let sut = factory ()
            let a = scope ()
            let b = scope ()
            sut.Seed a "sales" [ "orders" ]
            sut.Seed b "sales" [ "orders" ]
            let! _ = sut.Ingestor.RunIngestion(a, "sales", "orders")
            let! fromB = sut.Ingestor.GetRecentRuns(b, "sales", 10)
            Expect.isEmpty fromB "scope b sees none of scope a's history"
        }
    ]