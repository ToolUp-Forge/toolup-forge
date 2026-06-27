// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeFromDataTests

open System
open System.Text.Json
open Expecto
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering
open ToolUp.Remoting.Json.SystemTextJson
open ProcessedDataTypes
open DataManagementTypes

// ─── Phase 85 — NarrativeFromData projector output-shape tests ───────
//
// Projectors are PURE (`data -> NarrativeElement`), so these tests
// exercise them directly — no DI, no content-source plumbing. The
// emphasis is on (a) the projected element STRUCTURE, (b) LOCALE-STABLE
// formatting (money / percent / date), (c) DETERMINISM across two
// projection + render runs (the prerender-determinism contract), and
// (d) the graceful-degradation paths (unknown type, throwing projector,
// empty snapshot) returning callouts rather than throwing.

/// Normalise CRLF → LF so render-byte comparisons are platform-stable.
let private norm (s: string) = s.Replace("\r\n", "\n")

/// Wrap elements in a minimal single-section document for the renderers.
let private docWith (elements: NarrativeElement list) : NarrativeDocument = {
    Title = "T"
    Subtitle = None
    Sections = [
        {
            Id = "s"
            Heading = "H"
            Subheading = None
            Elements = elements
        }
    ]
    Provenance = None
    Lang = None
    CanonicalUrl = None
}

let private jsonOptions = FableConverters.create ()

/// A module result type the registerTyped test round-trips through JSON.
type private SalesSummary = {
    Region: string
    Spend: decimal
    Delta: float
}

let tests =
    testList "Phase 85 NarrativeFromData projectors" [

        // ─── Tabular projector ───────────────────────────────────────
        test "table projects columns + typed cells into a Table element with locale-stable formatting" {
            let columns = [ "Region", TableAlignment.Left; "Spend", TableAlignment.Right ]

            let rows = [
                [ CellText "North"; CellMoney(12_500m, "£") ]
                [ CellText "South"; CellMoney(9_300.5m, "£") ]
            ]

            match NarrativeFromData.table columns rows with
            | Table(cols, renderedRows) ->
                let row0 = renderedRows[0]
                let row1cell1 = renderedRows[1][1]
                Expect.equal cols columns "columns preserved verbatim"
                Expect.equal (List.length renderedRows) 2 "two rows"
                Expect.equal row0 [ [ Text "North" ]; [ Text "£12,500.00" ] ] "row 0 cells formatted invariantly"
                Expect.equal row1cell1 [ Text "£9,300.50" ] "money grouped + 2dp under InvariantCulture"
            | other -> failtestf "expected Table, got %A" other
        }

        test "table formats number / percent / int / date cells under InvariantCulture" {
            let fixedDate = DateTime(2026, 6, 10)

            let rows = [
                [ CellNumber 1234.5; CellPercent 0.23; CellInt 1_000_000L; CellDate fixedDate ]
            ]

            match
                NarrativeFromData.table
                    [
                        "n", TableAlignment.Right
                        "p", TableAlignment.Right
                        "i", TableAlignment.Right
                        "d", TableAlignment.Left
                    ]
                    rows
            with
            | Table(_, [ [ n; p; i; d ] ]) ->
                Expect.equal n [ Text "1234.5" ] "round-trip float (no grouping on None decimals)"
                Expect.equal p [ Text "23.0%" ] "percent fixed to 1dp (no IEEE-754 fuzz)"
                Expect.equal i [ Text "1,000,000" ] "int grouped invariantly"
                Expect.equal d [ Text "2026-06-10" ] "date ISO-formatted"
            | other -> failtestf "unexpected table shape %A" other
        }

        // ─── Determinism ─────────────────────────────────────────────
        test "projection + render is identical across two runs (prerender determinism)" {
            let columns = [ "KPI", TableAlignment.Left; "Value", TableAlignment.Right ]

            let rows () = [
                [ CellText "Spend"; CellMoney(21_800m, "£") ]
                [ CellText "Rate"; CellPercent 0.2345 ]
            ]

            let el1 = NarrativeFromData.table columns (rows ())
            let el2 = NarrativeFromData.table columns (rows ())
            Expect.equal el1 el2 "identical element trees"

            let html1 = norm (NarrativeHtml.render (docWith [ el1 ]))
            let html2 = norm (NarrativeHtml.render (docWith [ el2 ]))
            Expect.equal html1 html2 "byte-identical HTML render across runs"
            Expect.stringContains html1 "£21,800.00" "money rendered in HTML"
            Expect.stringContains html1 "23.5%" "percent rounded to 1dp in HTML"
        }

        // ─── Metric / KPI projector ──────────────────────────────────
        test "metricGrid emits a KeyValueGrid with up / down / no-change delta hooks" {
            match
                NarrativeFromData.metricGrid [
                    "Spend", "£21,800", Some 0.23
                    "Conv", "1,204", Some -0.04
                    "Flat", "10", Some 0.0
                    "Plain", "5", None
                ]
            with
            | KeyValueGrid pairs ->
                Expect.equal (List.length pairs) 4 "four KPI rows"
                let (l0, v0) = pairs[0]
                Expect.equal l0 "Spend" "label is the grid key"

                Expect.equal
                    v0
                    [ Strong "£21,800"; Text " "; Metric("▲", "+23.0%") ]
                    "positive delta: up arrow + signed percent"

                let (_, v1) = pairs[1]
                Expect.equal v1 [ Strong "1,204"; Text " "; Metric("▼", "-4.0%") ] "negative delta: down arrow"
                let (_, v2) = pairs[2]
                Expect.equal v2 [ Strong "10"; Text " "; Metric("■", "0.0%") ] "zero delta: neutral marker"
                let (_, v3) = pairs[3]
                Expect.equal v3 [ Strong "5" ] "no delta: value only"
            | other -> failtestf "expected KeyValueGrid, got %A" other
        }

        // ─── ProcessedData projector ─────────────────────────────────
        test "fromProcessed degrades to a graceful callout for an unknown type (no exception)" {
            let data = {
                TypeName = "Unregistered"
                Payload = "{}"
            }

            let opts = NarrativeFromDataProjectors.options NarrativeFromDataProjectors.empty

            match NarrativeFromData.fromProcessed data opts with
            | [ Callout(Notice, spans) ] ->
                match spans with
                | [ Text t ] -> Expect.stringContains t "Unregistered" "names the missing type"
                | _ -> failtest "unexpected callout spans"
            | other -> failtestf "expected a single Notice callout, got %A" other
        }

        test "fromProcessed routes a registered typed projector and decodes the payload" {
            let summary = {
                Region = "North"
                Spend = 12_500m
                Delta = 0.23
            }

            let payload = JsonSerializer.Serialize(summary, jsonOptions)

            let data = {
                TypeName = "SalesData"
                Payload = payload
            }

            let registry =
                NarrativeFromDataProjectors.empty
                |> NarrativeFromDataProjectors.registerTyped<SalesSummary> "SalesData" (fun s -> [
                    NarrativeFromData.table [ "Region", TableAlignment.Left; "Spend", TableAlignment.Right ] [
                        [ CellText s.Region; CellMoney(s.Spend, "£") ]
                    ]
                ])

            match NarrativeFromData.fromProcessed data (NarrativeFromDataProjectors.options registry) with
            | [ Table(_, [ [ region; spend ] ]) ] ->
                Expect.equal region [ Text "North" ] "decoded region"
                Expect.equal spend [ Text "£12,500.00" ] "decoded + formatted spend"
            | other -> failtestf "expected projected Table, got %A" other
        }

        test "fromProcessed contains a throwing projector as a Critical callout (no 500)" {
            let data = { TypeName = "Boom"; Payload = "{}" }

            let registry =
                NarrativeFromDataProjectors.empty
                |> NarrativeFromDataProjectors.register "Boom" (fun _ -> failwith "kaboom")

            match NarrativeFromData.fromProcessed data (NarrativeFromDataProjectors.options registry) with
            | [ Callout(Critical, spans) ] ->
                match spans with
                | [ Text t ] -> Expect.stringContains t "Boom" "names the failing type"
                | _ -> failtest "unexpected callout spans"
            | other -> failtestf "expected a Critical callout, got %A" other
        }

        // ─── File-snapshot projector ─────────────────────────────────
        test "fromFileSnapshot renders a processed-file status table joining Processed to Files" {
            let snapshot = {
                Files = [
                    {
                        FileName = "sales.csv"
                        DataType = "SalesData"
                        SizeBytes = 2048L
                        RowCount = 120
                        UploadedAt = DateTime(2026, 6, 1)
                    }
                    {
                        FileName = "broken.csv"
                        DataType = "SalesData"
                        SizeBytes = 10L
                        RowCount = 0
                        UploadedAt = DateTime(2026, 6, 2)
                    }
                ]
                Processed = [
                    {
                        FileName = "sales.csv"
                        DataType = "SalesData"
                        ProcessedAt = DateTime(2026, 6, 1)
                        Info = None
                        Error = None
                    }
                    {
                        FileName = "broken.csv"
                        DataType = "SalesData"
                        ProcessedAt = DateTime(2026, 6, 2)
                        Info = None
                        Error = Some "detector mismatch"
                    }
                ]
                Ingestion = []
            }

            match NarrativeFromData.fromFileSnapshot snapshot with
            | [ Table(cols, rows) ] ->
                // sales.csv: 2048 bytes → "2.0 KB", OK status.
                let salesSize = rows[0][2]
                let salesStatus = rows[0][5]
                // broken.csv: error status carries the message.
                let brokenStatus = rows[1][5]
                Expect.equal (List.length cols) 6 "six columns"
                Expect.equal (List.length rows) 2 "one row per file"
                Expect.equal salesSize [ Text "2.0 KB" ] "size humanised invariantly"
                Expect.equal salesStatus [ Strong "OK" ] "processed file shows OK"
                Expect.equal brokenStatus [ Strong "Error"; Text ": detector mismatch" ] "error file shows the reason"
            | other -> failtestf "expected a single Table, got %A" other
        }

        test "fromFileSnapshot degrades to an empty-state callout when no files exist" {
            match
                NarrativeFromData.fromFileSnapshot {
                    Files = []
                    Processed = []
                    Ingestion = []
                }
            with
            | [ Callout(Info, _) ] -> ()
            | other -> failtestf "expected an Info empty-state callout, got %A" other
        }

        // ─── Callout / threshold helpers ─────────────────────────────
        test "thresholdCallout maps a value through the spend ladder to a severity" {
            let sev v =
                match NarrativeFromData.thresholdCallout NarrativeFromData.spendThresholds v "msg" with
                | Callout(s, _) -> s
                | other -> failtestf "expected callout, got %A" other

            Expect.equal (sev 0.60) Critical "≥50% over target → Critical"
            Expect.equal (sev 0.23) Warning "≥20% over target → Warning"
            Expect.equal (sev 0.05) Notice "≥0 over target → Notice"
            Expect.equal (sev -0.10) Info "under target → Info default"
        }

        // ─── Synthesis composition ───────────────────────────────────
        test "withSynthesis prepends a summary; withoutSynthesis is a no-op" {
            let body = [ NarrativeFromData.annotate Info "row" ]
            let hook: SynthesisHook = fun _ -> Some(Paragraph [ Text "Executive summary." ])

            let withSummary = NarrativeFromData.withSynthesis hook body
            Expect.equal (List.head withSummary) (Paragraph [ Text "Executive summary." ]) "summary prepended"
            Expect.equal (List.length withSummary) 2 "summary + original element"

            let unchanged =
                NarrativeFromData.withSynthesis NarrativeFromData.withoutSynthesis body

            Expect.equal unchanged body "no-op hook leaves the body unchanged"
        }
    ]