// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.GoogleAnalyticsTests

open System.Text
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.DataSources
open ToolUp.DataSources.Common

// ─── Phase 838 — provider sentinels are declared at the connector ───
//
// GA4 answers a missing dimension value with the literal `(not set)`. The
// connector declares that vocabulary per column on its schema; the schema
// travels with the payload (the stored schema object, Phase 832); and the
// reader turns a declared sentinel into absence once, at
// `IngestedPayload.readCell`. The same text anywhere undeclared is data.

/// The schema as a reader weeks later sees it: the connector's
/// `GetSchema` answer, through the stored schema object's bytes.
let private stored (schema: TableSchema) : TableSchema option =
    schema |> IngestedPayload.serializeSchema |> IngestedPayload.tryParseSchema

/// Read every row of a GA4 report envelope (the connector's documented
/// JSON shape) through the payload boundary.
let private readGa4Rows (schema: TableSchema option) (json: string) : Map<string, string option> list =
    use doc = JsonDocument.Parse(json: string)

    doc.RootElement.GetProperty("rows").EnumerateArray()
    |> Seq.map (fun row ->
        row.EnumerateObject()
        |> Seq.map (fun cell -> cell.Name, cell.Value.GetString())
        |> IngestedPayload.readRow schema)
    |> List.ofSeq

let private ga4Payload =
    """{"property":"properties/1","dimensionHeaders":["date","country","sessionMedium","sessionSource","city"],"metricHeaders":["activeUsers"],"rowCount":3,"rows":[{"date":"20260101","country":"(not set)","sessionMedium":"(none)","sessionSource":"(direct)","city":"Leeds","activeUsers":"17"},{"date":"20260102","country":"United Kingdom","sessionMedium":"organic","sessionSource":"google","city":"(other)","activeUsers":"23"},{"date":"20260103","country":"France","sessionMedium":"(not set)","sessionSource":"(not set)","city":"(not set)","activeUsers":"(not set)"}]}"""

let private gaColumn (name: string) =
    GoogleAnalyticsSchema.columns |> List.find (fun c -> c.Name = name)

[<Tests>]
let tests =
    testList "GoogleAnalytics — declared sentinels (Phase 838)" [

        test "a GA4 (not set) cell reads as absent through the stored schema" {
            let schema = stored (GoogleAnalyticsSchema.tableSchema "properties/1")
            Expect.isSome schema "the declared schema round-trips through its stored bytes"
            let rows = readGa4Rows schema ga4Payload
            Expect.equal rows[0].["country"] None "(not set) in a GA4 dimension is absent"
            Expect.equal rows[2].["city"] None "(not set) is declared on every dimension"
            Expect.equal rows[2].["sessionMedium"] None "(not set) on the medium dimension too"
            Expect.equal rows[1].["country"] (Some "United Kingdom") "ordinary values read as themselves"
        }

        test "(none) is absent on the medium dimension it is declared for" {
            let rows =
                readGa4Rows (stored (GoogleAnalyticsSchema.tableSchema "properties/1")) ga4Payload

            Expect.equal rows[0].["sessionMedium"] None "(none) is a declared absence for sessionMedium"
        }

        test "(other) is a cardinality bucket and reads as its own text, as 838.B decided" {
            let rows =
                readGa4Rows (stored (GoogleAnalyticsSchema.tableSchema "properties/1")) ga4Payload

            Expect.equal rows[1].["city"] (Some "(other)") "(other) aggregates real values; it is not absence"

            let other =
                GoogleAnalyticsSchema.sentinelVocabulary
                |> List.find (fun s -> s.Token = "(other)")

            Expect.equal other.Meaning GoogleAnalyticsSchema.CardinalityBucket "recorded as a cardinality bucket"

            Expect.isFalse
                (GoogleAnalyticsSchema.columns
                 |> List.exists (fun c -> IngestedPayload.isAbsentSentinel c "(other)"))
                "no column declares (other) as absent"
        }

        test "(direct) is a classification and reads as its own text" {
            let rows =
                readGa4Rows (stored (GoogleAnalyticsSchema.tableSchema "properties/1")) ga4Payload

            Expect.equal rows[0].["sessionSource"] (Some "(direct)") "direct traffic is a known source"
        }

        test "sentinels are per column: a metric declares none, and (none) is not declared off the medium family" {
            Expect.isNone (gaColumn "activeUsers").AbsentSentinels "metrics declare nothing"

            let rows =
                readGa4Rows (stored (GoogleAnalyticsSchema.tableSchema "properties/1")) ga4Payload

            Expect.equal rows[2].["activeUsers"] (Some "(not set)") "undeclared column reads the text as itself"
            Expect.isFalse (IngestedPayload.isAbsentSentinel (gaColumn "country") "(none)") "(none) is medium-only"

            Expect.isFalse
                (IngestedPayload.isAbsentSentinel (gaColumn "sessionSourceMedium") "(none)")
                "a composite source / medium cell is never wholly (none)"
        }

        test "every declared token is an Absent entry of the recorded vocabulary" {
            let absent =
                GoogleAnalyticsSchema.sentinelVocabulary
                |> List.filter (fun s -> s.Meaning = GoogleAnalyticsSchema.Absent)
                |> List.map _.Token
                |> Set.ofList

            for c in GoogleAnalyticsSchema.columns do
                for token in defaultArg c.AbsentSentinels Set.empty do
                    Expect.contains absent token $"{c.Name} declares only recorded absence tokens"

            for s in GoogleAnalyticsSchema.sentinelVocabulary do
                Expect.isNotEmpty s.Note $"{s.Token} records its meaning"
        }

        test "the same (not set) text in a SQL varchar column reads as itself" {
            let sqlSchema =
                stored (TypeMap.schema "orders" [ TypeMap.column "country" "varchar" true ])

            Expect.equal
                (IngestedPayload.readCell sqlSchema "country" "(not set)")
                (Some "(not set)")
                "a source that declared nothing keeps the literal text"
        }

        test "a connector declaring nothing serialises exactly as before" {
            let schema =
                TypeMap.schema "t" [ TypeMap.column "a" "INT64" false; TypeMap.column "b" "STRING" true ]

            Expect.equal
                (Encoding.UTF8.GetString(IngestedPayload.serializeSchema schema))
                """{"tableName":"t","columns":[{"name":"a","dataType":"INT64","nullable":false},{"name":"b","dataType":"STRING","nullable":true}]}"""
                "no absentSentinels member, so the content hash and schema-drift are unchanged"

            Expect.equal (stored schema) (Some schema) "round-trips with AbsentSentinels = None"
        }

        test "no schema, or a column the schema does not name, reads the text as itself" {
            Expect.equal (IngestedPayload.readCell None "country" "(not set)") (Some "(not set)") "no schema"

            Expect.equal
                (IngestedPayload.readCell (Some(GoogleAnalyticsSchema.tableSchema "p")) "customDim" "(not set)")
                (Some "(not set)")
                "an uncatalogued column declares nothing"
        }

        test "the declaration persists in the stored schema bytes, sorted" {
            let json =
                Encoding.UTF8.GetString(IngestedPayload.serializeSchema (GoogleAnalyticsSchema.tableSchema "p"))

            Expect.stringContains
                json
                """{"name":"sessionMedium","dataType":"string","nullable":true,"absentSentinels":["(none)","(not set)"]}"""
                "per-column declaration in the stored schema"

            Expect.stringContains
                json
                """{"name":"activeUsers","dataType":"number","nullable":true}"""
                "a metric carries no member"
        }
    ]