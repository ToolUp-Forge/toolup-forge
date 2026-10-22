// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ICallerLabelledIngestorContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts.IDataIngestorContract

// ─── ICallerLabelledIngestor contract pack — Phase 1042 ───────────
//
// Parametrised laws for any `ICallerLabelledIngestor` (Phase 1041's caller
// label). The platform's `DataIngestor` implements it, and Phase 1042's
// delivery-completed recording decorator forwards it, so a deployment that
// registers a hook keeps labelled ingestion. Both bind this pack (in
// `InProcess/DeliveryCompletedTests.fs`).
//
// **Coverage:**
//   1. The ingestor is reachable as `ICallerLabelledIngestor` — the type
//      test the `RunIngestion(…, label)` extension makes.
//   2. A labelled run is filed under its label: the run record's `Table`
//      and its history, while the connector still ran the table it was
//      given.
//   3. `label = None` is the three-argument `RunIngestion`: filed under
//      the table.

let tests (name: string) (factory: unit -> IngestorUnderTest) =

    let scope () = "scope-" + Guid.NewGuid().ToString("N")

    let label (text: string) =
        match IngestionLabel.tryCreate text with
        | Ok l -> l
        | Error e -> failtestf "the test label was refused: %A" e

    let labelled (sut: IngestorUnderTest) =
        match box sut.Ingestor with
        | :? ICallerLabelledIngestor as l -> l
        | _ -> failtestf "%s is not reachable as ICallerLabelledIngestor" name

    testList $"{name} — ICallerLabelledIngestor contract" [

        testCase "1. the ingestor is reachable as ICallerLabelledIngestor"
        <| fun _ -> labelled (factory ()) |> ignore

        testCaseAsync "2. a labelled run is filed under its label"
        <| async {
            let sut = factory ()
            let scopeId = scope ()
            sut.Seed scopeId "sales" [ "orders" ]

            let! result = (labelled sut).RunLabelledIngestion(scopeId, "sales", "orders", Some(label "daily-orders"))

            match result with
            | Ok run ->
                Expect.equal run.Status IngestionStatus.Succeeded "the connector ran the table it was given"
                Expect.equal run.Table "daily-orders" "the run is filed under the label"
            | Error e -> failtestf "expected a recorded run, got %A" e

            let! recent = sut.Ingestor.GetRecentRuns(scopeId, "sales", 10)
            Expect.equal (recent |> List.map _.Table) [ "daily-orders" ] "and its history says so"
        }

        testCaseAsync "3. no label is the three-argument form"
        <| async {
            let sut = factory ()
            let scopeId = scope ()
            sut.Seed scopeId "sales" [ "orders" ]
            let! result = (labelled sut).RunLabelledIngestion(scopeId, "sales", "orders", None)

            match result with
            | Ok run -> Expect.equal run.Table "orders" "filed under the table"
            | Error e -> failtestf "expected a recorded run, got %A" e
        }
    ]