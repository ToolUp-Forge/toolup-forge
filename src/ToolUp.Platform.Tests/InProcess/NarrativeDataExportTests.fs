// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.NarrativeDataExportTests

open Expecto
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Phase 101 — tabular data export (CSV / JSON) ───────────────────
//
// Pure extractor + serialisers: pull Table elements out of a
// NarrativeDocument and emit RFC 4180 CSV / structured JSON rows.

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

let private table =
    Table(
        [ "Region", TableAlignment.Left; "Spend", TableAlignment.Right ],
        [
            [ [ Text "North" ]; [ Text "1,000" ] ]
            [ [ Text "O'Brien, Ltd" ]; [ Text "2,500" ] ] // embedded comma + apostrophe
        ]
    )

let tests =
    testList "Phase 101 tabular data export" [

        test "tables extracts Table elements (including nested in a Card)" {
            let doc =
                docWith [
                    Paragraph [ Text "intro" ]
                    table
                    Card {
                        Heading = None
                        Image = None
                        Body = [ table ]
                    }
                ]

            let extracted = NarrativeData.tables doc
            Expect.equal (List.length extracted) 2 "top-level + nested table"
            Expect.equal (List.head extracted).Columns [ "Region"; "Spend" ] "column headers"
            Expect.equal (List.head extracted).Rows[0] [ "North"; "1,000" ] "cell values flattened to text"
        }

        test "toCsv emits RFC 4180 — header row + quoted fields with embedded commas" {
            let csv = NarrativeData.toCsv (List.head (NarrativeData.tables (docWith [ table ])))
            let lines = csv.Split([| "\r\n" |], System.StringSplitOptions.None)
            Expect.equal lines[0] "Region,Spend" "header row"
            Expect.equal lines[1] "North,\"1,000\"" "comma-containing field is quoted"
            Expect.stringContains csv "\"O'Brien, Ltd\"" "embedded comma quoted; apostrophe left as-is"
        }

        test "toJson emits an array of column-keyed objects in order" {
            let json =
                NarrativeData.toJson (List.head (NarrativeData.tables (docWith [ table ])))

            Expect.stringContains json "{\"Region\":\"North\",\"Spend\":\"1,000\"}" "first row object, ordered keys"
            Expect.stringContains json "\"O'Brien, Ltd\"" "second row value preserved"
            Expect.isTrue (json.StartsWith "[" && json.EndsWith "]") "JSON array"
        }

        test "a document with no table extracts nothing (graceful empty export)" {
            let extracted =
                NarrativeData.tables (docWith [ Paragraph [ Text "no tables here" ] ])

            Expect.isEmpty extracted "no tables → empty list (handler emits an empty export)"
        }
    ]