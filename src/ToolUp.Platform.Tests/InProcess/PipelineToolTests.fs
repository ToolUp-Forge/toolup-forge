// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.PipelineToolTests

// ─── Phase 1145 — evaluate_pipeline ──────────────────────────────────
//
// The model writes a `Fuaran.Compute` pipeline; the server evaluates it over
// the fact browse's gated view slice. What this pack pins:
//
//   1. **Refused by name.** Malformed arguments, a malformed step (naming its
//      position), an unknown column, an open output schema, another source, a
//      parameter, the clock, an empty slice and a result over the cap.
//   2. **The result is the reference evaluator's.** A `filter > groupBy >
//      sort > limit` pipeline answers exactly `DataFrame.evalPipeline` over
//      the same slice, and the record carries the canonical pipeline, its hash,
//      the run stamp and the fact ids the lineage read.
//   3. **The withheld rule.** One withheld revenue cell withholds every column
//      computed from revenue — through a derive, an aggregate, and a filter
//      that decides which rows survive — and leaves a count-only column and
//      the keys intact; the same pipeline over a narrowed slice with no
//      withheld cell answers in full.
//   4. **The door and the summary.** The slice is judged at `FactToolResult`;
//      the summary names the shape, the columns and the withheld report, and
//      never a value.
//   5. **The answering form and the block (Phase 1156).** The model reads the
//      summary and the message carries one pipeline block: the request, its
//      hash and run stamp, and a node naming the columns, with no cell value
//      anywhere it is persisted. A reader's re-run is judged at `FactBrowse`,
//      draws the reference evaluator's grid, and says how a reader whose gate
//      differs sees a different result.
//
// The egress instances (a withheld value, a denied principal) live in the
// disclosure egress pack beside every other door.

open System
open Expecto
open Fuaran.Core
open Fuaran.Compute
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.AI
open ToolUp.Platform.AI
open ToolUp.Facts.Presentation
open ToolUp.Platform.Tests.InProcess.FactRenderToolTests.RenderFixtures

// ─── Fixtures (shared with the disclosure egress pack) ──────────────

/// Building requests and reading answers.
module PipelineFixtures =

    /// The tool's arguments: daily-trade revenue over `days` days from the
    /// first, and `pipeline`.
    let argsFor (tableId: string) (metric: string) (days: int) (pipeline: Transform list) : string =
        pipelineArgs tableId metric days pipeline

    /// Revenue over the three seeded days.
    let revenueArgs (pipeline: Transform list) = argsFor trade.Id "revenue" 3 pipeline

    /// The answer in `w` for principal `u`.
    let answer (w: World) (args: string) : Result<PipelineToolResult, PipelineRefusal> =
        PipelineTool.answerWith w.Deps w.Scope "u" args |> Async.RunSynchronously

    /// The result, or the test fails naming the refusal.
    let answered (w: World) (args: string) : PipelineToolResult =
        match answer w args with
        | Ok result -> result
        | Error refusal -> failtestf "expected a result, got %s" (PipelineRefusal.describe refusal)

    /// The result's table.
    let tableOf (result: PipelineToolResult) : Table =
        match ColumnCodec.decode result.Table with
        | Ok(Embedded table) -> table
        | other -> failtestf "the result table does not decode: %A" other

    /// A column's cells.
    let cells (name: string) (table: Table) : Cell list =
        match Table.tryColumn name table with
        | Some column -> Column.toCells column
        | None -> failtestf "no column '%s' in %A" name (Table.columnNames table)

    /// Revenue per subject: the total and a count of the rows, by subject.
    let perSubject: Transform list = [
        GroupBy(
            [ FactBrowseSlice.SubjectColumn ],
            [
                {
                    Name = "total"
                    Fn = Sum
                    Of = "revenue"
                }
                {
                    Name = "days"
                    Fn = Count
                    Of = FactBrowseSlice.RowColumn
                }
            ]
        )
        Transform.sortBy [ FactBrowseSlice.SubjectColumn, Asc ]
    ]

    /// The grouped, derived and ranked view: days above a threshold, revenue
    /// per subject, the largest first, the top one.
    let ranked: Transform list = [
        Filter(Binary(Gt, Col "revenue", Lit(Decimal "1000")))
        Derive("net", Binary(Sub, Col "revenue", Lit(Decimal "100")))
        GroupBy(
            [ FactBrowseSlice.SubjectColumn ],
            [
                {
                    Name = "net_total"
                    Fn = Sum
                    Of = "net"
                }
            ]
        )
        Transform.sortBy [ "net_total", Desc ]
        Transform.limit 1 0
    ]

open PipelineFixtures

let private refusedWith (expected: PipelineRefusal) (answer: Result<PipelineToolResult, PipelineRefusal>) =
    match answer with
    | Error refusal -> Expect.equal refusal expected (PipelineRefusal.describe refusal)
    | Ok result -> failtestf "expected %A, got a result of %d row(s)" expected result.Rows

let private refusalOf (answer: Result<PipelineToolResult, PipelineRefusal>) : PipelineRefusal =
    match answer with
    | Error refusal -> refusal
    | Ok result -> failtestf "expected a refusal, got a result of %d row(s)" result.Rows

// ─── 1. Refused by name ──────────────────────────────────────────────

let private refusalTests =
    testList "refused by name" [
        test "arguments that are not the request's shape" {
            let w = world ()

            refusedWith (PipelineRefusal.MalformedArguments "the arguments are not valid JSON") (answer w "{")

            refusedWith
                (PipelineRefusal.MalformedArguments "'pipeline' is required: a JSON array of transform steps")
                (answer w """{"table":"daily-trade","period_from":"2026-10-01","period_to":"2026-10-04"}""")

            refusedWith
                (PipelineRefusal.MalformedArguments
                    "'period_to' is not a date (yyyy-MM-dd) or an ISO-8601 instant: 'soon'")
                (answer w """{"table":"daily-trade","period_from":"2026-10-01","period_to":"soon","pipeline":[]}""")
        }

        test "a malformed step is refused naming its position" {
            let w = world ()

            let args =
                (revenueArgs [ Transform.limit 2 0 ])
                    .Replace("""{"$type":"limit""", """{"$type":"nope"},{"$type":"limit""")

            match refusalOf (answer w args) with
            | PipelineRefusal.MalformedPipeline(Some 1, reason) ->
                Expect.stringContains reason "nope" "the refusal names the unknown step"
            | other -> failtestf "expected step 1 malformed, got %A" other

            let second =
                (revenueArgs [ Transform.limit 2 0 ]).Replace("""}]}""", """},{"$type":"filter"}]}""")

            match refusalOf (answer w second) with
            | PipelineRefusal.MalformedPipeline(Some 2, _) -> ()
            | other -> failtestf "expected step 2 malformed, got %A" other
        }

        test "a step reading a column its input does not have" {
            let w = world ()

            match refusalOf (answer w (revenueArgs [ Transform.limit 2 0; Filter(IsNull(Col "profit")) ])) with
            | PipelineRefusal.UnknownColumn(2, "profit", available) ->
                Expect.contains available "revenue" "the refusal lists the input's columns"
            | other -> failtestf "expected an unknown column at step 2, got %A" other

            match
                refusalOf (answer w (revenueArgs [ Project [ "subject", "who" ]; Transform.sortBy [ "subject", Asc ] ]))
            with
            | PipelineRefusal.UnknownColumn(2, "subject", [ "who" ]) -> ()
            | other -> failtestf "a projected-away column is unknown after it, got %A" other
        }

        test "an output schema the walk cannot name" {
            let w = world ()

            match
                refusalOf (
                    answer
                        w
                        (revenueArgs [
                            Pivot {
                                Index = [ "subject" ]
                                On = "period_label"
                                Values = "revenue"
                                Agg = Sum
                            }
                        ])
                )
            with
            | PipelineRefusal.OpenSchema(1, reason) -> Expect.stringContains reason "pivot" "the walk's reason"
            | other -> failtestf "expected an open schema at step 1, got %A" other
        }

        test "another source, a parameter and the clock" {
            let w = world ()

            refusedWith
                (PipelineRefusal.NamedSource(2, "union"))
                (answer w (revenueArgs [ Distinct; Union(Ref "elsewhere") ]))

            refusedWith
                (PipelineRefusal.NamedSource(1, "join"))
                (answer w (revenueArgs [ Join(Ref "elsewhere", [ "subject", "subject" ], Inner) ]))

            refusedWith
                (PipelineRefusal.UnboundParameter(1, [ "threshold" ]))
                (answer w (revenueArgs [ Filter(Binary(Gt, Col "revenue", Param "threshold")) ]))

            refusedWith
                (PipelineRefusal.UnpinnedClock 1)
                (answer w (revenueArgs [ Filter(Binary(Lt, Col "period_from", Now NowGrain.Timestamp)) ]))
        }

        test "the slice read's refusals, verbatim, and an empty slice" {
            let w = world ()

            match refusalOf (answer w (argsFor "no-such-table" "revenue" 3 [])) with
            | PipelineRefusal.SliceRefused reason -> Expect.stringContains reason "no-such-table" "names the table"
            | other -> failtestf "expected the slice read's refusal, got %A" other

            let empty =
                (revenueArgs []).Replace("2026-10-01", "2027-01-01").Replace("2026-10-04", "2027-01-02")

            refusedWith (PipelineRefusal.NoFacts trade.Id) (answer w empty)
        }

        test "a result over the cap is refused naming the cap" {
            // A synthetic slice: two subjects by 900 days is 1,800 heads (under
            // the slice's cap) and 25,200 cells as a table — the identity
            // pipeline's result is over it.
            let cellsOf = [
                for site in [ "s1"; "s2" ] do
                    for d in 0..899 do
                        let from = day1.AddDays(float d)

                        {
                            Metric = "revenue"
                            FactId = sprintf "f-%s-%d" site d
                            Subject = "sites/north>" + site
                            SubjectPath = [ "north"; site ]
                            PeriodFrom = from
                            PeriodTo = from.AddDays 1.0
                            PeriodLabel = None
                            Value = Some 1M
                            Rendering = Some "1"
                            AsOf = day1
                            Freshness = "fresh"
                            WithheldPolicy = None
                            RankDescending = None
                            RankAscending = None
                        }
                        : FactSliceCell
            ]

            let table =
                match
                    FactBrowseSlice.build [ "revenue" ] cellsOf
                    |> Result.bind FactBrowseSlice.encode
                with
                | Ok t -> t
                | Error e -> failtest e

            let slice: FactSlice = {
                TableId = trade.Id
                Hierarchy = "sites"
                Level = "site"
                Method = "m"
                PeriodFrom = day1
                PeriodTo = day1.AddDays 900.0
                SubjectPrefix = []
                Metrics = [
                    {
                        Metric = "revenue"
                        DisplayFormat = ""
                    }
                ]
                Table = table
                RunStamp = None
                CellCount = 1800
                MaxCells = FactBrowseApi.MaxSliceCells
                RankCeiling = 1000
                WithheldCount = 0
                Withheld = []
            }

            let request =
                match PipelineTool.requestOf (revenueArgs []) with
                | Ok r -> r
                | Error e -> failtestf "%A" e

            match PipelineTool.evaluate request [] slice day1 with
            | Error(PipelineRefusal.ResultOverCap(25200, cap)) ->
                Expect.equal cap FactBrowseApi.MaxSliceCells "the cap is the slice's"

                Expect.stringContains
                    (PipelineRefusal.describe (PipelineRefusal.ResultOverCap(25200, cap)))
                    "limit step"
                    "and the refusal suggests a limit"
            | other -> failtestf "expected the result to be over the cap, got %A" other

            match PipelineTool.evaluate request [ Transform.limit 10 0 ] slice day1 with
            | Ok result -> Expect.equal result.Rows 10 "a limit brings it under"
            | Error e -> failtestf "%A" e
        }
    ]

// ─── 2. The reference evaluator's answer ─────────────────────────────

/// The slice the tool reads for `args`, as the reference evaluator's input.
let private sliceTable (w: World) (days: int) : Table =
    match
        FactBrowseHandler.querySlice w.Deps w.Scope "u" {
            TableId = trade.Id
            PeriodFrom = day1
            PeriodTo = day1.AddDays(float days)
            SubjectPrefix = []
            Metrics = [ "revenue" ]
        }
        |> Async.RunSynchronously
        |> Result.bind FactBrowseSlice.decode
    with
    | Ok table -> table
    | Error e -> failtest e

let private resultTests =
    testList "the reference evaluator's answer" [
        test "a filter, groupBy, sort and limit pipeline answers exactly what the reference evaluator answers" {
            let w = world ()
            let result = answered w (revenueArgs ranked)

            let expected =
                match DataFrame.evalPipeline ranked (sliceTable w 3) with
                | Ok t -> t
                | Error e -> failtestf "%s" (DataFrame.errorString e)

            Expect.equal (tableOf result) expected "the evaluator's own table"
            Expect.equal result.Rows 1 "one row"
            Expect.equal result.Columns [ "subject"; "net_total" ] "its columns"
            Expect.isEmpty result.Withheld "nothing withheld"
            Expect.equal (cells "subject" expected) [ Str "sites/north>s2" ] "the largest is s2"
        }

        test "the record carries the canonical pipeline, its hash, the slice's stamp and the fact ids it read" {
            let w = world ()
            let result = answered w (revenueArgs ranked)

            Expect.equal result.Request.Pipeline (DataFrameCodec.encodePipeline ranked) "the canonical pipeline"
            Expect.equal result.PipelineHash (IncrementalCodec.pipelineHash ranked) "its hash"
            Expect.isSome result.RunStamp "the run that wrote the slice"
            Expect.equal result.SliceCells 6 "six cells read"
            Expect.equal result.FactIdCount 6 "the lineage read every revenue fact"
            Expect.equal (List.length result.FactIds) 6 "and lists them"
            Expect.equal result.Request.TableId trade.Id "the slice spec"
        }

        test "a column the result's lineage does not read cites no fact" {
            let w = world ()

            let result =
                answered w (revenueArgs [ Project [ FactBrowseSlice.SubjectColumn, "subject" ]; Distinct ])

            Expect.equal result.Rows 2 "two subjects"
            Expect.isEmpty result.FactIds "the subjects read no metric"
        }
    ]

// ─── 3. The withheld rule ────────────────────────────────────────────

let private withheldOne (w: World) = w.Gate.Deny [ revenueFactId w "s2" 1 ]

let private trade1: PipelineWithheldCount list = [
    {
        PipelineWithheldCount.PolicyRef = "trade-figures"
        Count = 1
    }
]

let private withheldTests =
    testList "the withheld rule" [
        test "one withheld revenue cell withholds the total computed from it and leaves the count and the keys" {
            let w = world ()
            withheldOne w
            let result = answered w (revenueArgs perSubject)
            let table = tableOf result

            Expect.equal
                result.Withheld
                [
                    {
                        PipelineWithheldColumn.Column = "total"
                        Metrics = [ "revenue" ]
                        Withheld = trade1
                    }
                ]
                "only the total is withheld, with its count"

            Expect.equal (cells "total" table) [ Null; Null ] "its cells are empty"
            Expect.equal (cells "days" table) [ Int 3; Int 3 ] "the count of rows is intact"

            Expect.equal (cells "subject" table) [ Str "sites/north>s1"; Str "sites/north>s2" ] "the keys are intact"
        }

        test "a derive, a chained aggregate and a row-deciding filter all carry the withheld lineage" {
            let w = world ()
            withheldOne w

            let derived =
                answered w (revenueArgs [ Derive("double", Binary(Mul, Col "revenue", Lit(Int 2))) ])

            Expect.contains (derived.Withheld |> List.map _.Column) "double" "a derive from revenue"

            Expect.isFalse
                (derived.Withheld |> List.exists (fun c -> c.Column = "subject"))
                "a column the derive does not touch stays"

            let filtered =
                answered
                    w
                    (revenueArgs [
                        Filter(Binary(Gt, Col "revenue", Lit(Decimal "1100")))
                        Project [ "subject", "who" ]
                    ])

            Expect.equal
                (filtered.Withheld |> List.map _.Column)
                [ "who" ]
                "the subjects a revenue filter kept are a partial list"

            let ranked = answered w (revenueArgs ranked)

            Expect.equal
                (ranked.Withheld |> List.map _.Column)
                [ "subject"; "net_total" ]
                "a ranking over revenue withholds even its key"
        }

        test "the same pipeline over a narrowed slice with no withheld cell answers in full" {
            let w = world ()
            withheldOne w
            let narrowed = argsFor trade.Id "revenue" 1 perSubject
            let result = answered w narrowed

            Expect.isEmpty result.Withheld "nothing withheld on the first day"

            let expected =
                match DataFrame.evalPipeline perSubject (sliceTable w 1) with
                | Ok t -> t
                | Error e -> failtestf "%s" (DataFrame.errorString e)

            Expect.equal (tableOf result) expected "the reference evaluator's answer"
            Expect.equal (cells "total" expected) [ Decimal "1000.25"; Decimal "1000.75" ] "the totals"
        }

        test "a slice whose every cell is withheld is refused, never an empty table" {
            let w = world ()

            match answer w (argsFor margins.Id "margin" 3 perSubject) with
            | Error(PipelineRefusal.NothingDisclosable(cells, withheld)) ->
                Expect.equal
                    (cells, withheld)
                    (6,
                     [
                         {
                             PipelineWithheldCount.PolicyRef = "trade-figures"
                             Count = 6
                         }
                     ])
                    "every cell withheld, counted under its policy"
            | other -> failtestf "expected the denied principal's refusal, got %A" other
        }
    ]

// ─── 4. The door, the summary and the definition ─────────────────────

let private answerTests =
    testList "the door, the summary and the definition" [
        test "the slice is judged at the tool-result door, never the browse door" {
            let w = world ()
            answered w (revenueArgs perSubject) |> ignore

            Expect.isNonEmpty w.Gate.Surfaces "the gate was asked"

            Expect.all w.Gate.Surfaces (fun s -> s = FactToolResult) "every judgement at FactToolResult"
        }

        test "the summary names the shape, the columns and the withheld report, and no value" {
            let w = world ()
            withheldOne w
            let result = answered w (revenueArgs perSubject)

            Expect.equal
                (PipelineTool.summary result)
                "Evaluated the pipeline over fact table 'daily-trade' for 2026-10-01..2026-10-04 (6 cell(s) read): the result has 2 row(s) and 3 column(s): 'subject', 'total', 'days'. Withheld, with their cells empty: 'total' (1 withheld under trade-figures; it reads revenue). The user sees the result; it carries none of its values here, so state none and retype no figure."
                "the summary, word for word"
        }

        test "the definition is server-resident, on both surfaces, declaring ReadFacts only" {
            let d = PipelineTool.definition
            Expect.equal d.Name "evaluate_pipeline" "its name"
            Expect.equal d.Location ServerResident "server-resident"
            Expect.equal d.Surface Both "both surfaces"
            Expect.equal d.Effects ToolEffectDeclaration.readFacts "ReadFacts only"

            Expect.stringContains d.Description "never retyped" "the description says the figures are never retyped"

            Expect.equal
                (d.Parameters |> List.map _.Name)
                [ "table"; "period_from"; "period_to"; "path_prefix"; "metrics"; "pipeline" ]
                "its arguments"
        }
    ]

// ─── 5. The answering form and the block ─────────────────────────────

/// The answering form's answer in `w` for principal `u`.
let private answeredForm (w: World) (args: string) : ToolHelpers.ToolAnswer =
    PipelineAnsweringTool.answerWith w.Deps w.Scope "u" args
    |> Async.RunSynchronously

/// The reader's re-run of a persisted request in `w`, for principal `u`.
let private rerun (w: World) (requestJson: string) : Result<PipelineBlockResult, string> =
    PipelineAnsweringTool.rerunWith w.Deps w.Scope "u" requestJson
    |> Async.RunSynchronously

let private rerunOk (w: World) (requestJson: string) : PipelineBlockResult =
    match rerun w requestJson with
    | Ok result -> result
    | Error e -> failtestf "expected a re-run, got %s" e

/// Every slice cell's revenue as it stands: the raw values, so a persisted
/// copy of any of them is caught.
let private revenues: Transform list = [ Project [ FactBrowseSlice.SubjectColumn, "subject"; "revenue", "revenue" ] ]

let private blockTests =
    testList "the answering form and the pipeline block" [
        test
            "the model reads the executor's summary and the message carries one pipeline block, the request and no value" {
            let w = world ()
            let expected = answered w (revenueArgs ranked)
            let form = answeredForm w (revenueArgs ranked)

            Expect.equal form.Text (PipelineTool.summary expected) "the model reads the summary"

            let node, provenance = blockOf form
            Expect.equal provenance.Kind PipelineBlock.Kind "a pipeline block"
            Expect.equal provenance.TableId trade.Id "the table"
            Expect.equal provenance.Metric "revenue" "the metrics the slice named"
            Expect.equal provenance.Period "2026-10-01T00:00:00Z/2026-10-04T00:00:00Z" "the slice's period"
            Expect.equal provenance.FactIds expected.FactIds "the facts the lineage read"
            Expect.isEmpty provenance.Withheld "nothing withheld"

            match PipelineTool.requestOf provenance.Request with
            | Error refusal -> failtestf "the persisted request does not read: %s" (PipelineRefusal.describe refusal)
            | Ok request ->
                Expect.equal request expected.Request "the request the executor answered, its pipeline canonical"

            Expect.equal (PipelineBlock.recordedRunStamp provenance.Request) expected.RunStamp "the run stamp"

            Expect.stringContains
                provenance.Request
                (sprintf "\"pipeline_hash\":\"%s\"" expected.PipelineHash)
                "the pipeline's hash"

            Expect.stringContains node "net_total" "the stored node names the result's columns"
        }

        test "no cell value reaches the model's text, the stored node or the stored request" {
            let w = world ()
            let form = answeredForm w (revenueArgs revenues)
            let node, provenance = blockOf form

            let values =
                match PipelineBlock.rowsOf (rerunOk w provenance.Request) with
                | Ok rows -> rows |> List.map List.last
                | Error e -> failtest e

            Expect.contains values (string DistinctiveValue) "the re-run carries the distinctive value"

            for text in [ form.Text; node; provenance.Request ] do
                for value in values do
                    Expect.isFalse (text.Contains value) (sprintf "'%s' is in no persisted text" value)
        }

        test "the model's read is judged at the tool-result door and the reader's re-run at the browse door" {
            let w = world ()
            let _, provenance = blockOf (answeredForm w (revenueArgs perSubject))
            let asked = List.length w.Gate.Surfaces
            Expect.isGreaterThan asked 0 "the model's read was judged"
            Expect.all w.Gate.Surfaces (fun s -> s = FactToolResult) "at FactToolResult"

            rerunOk w provenance.Request |> ignore
            let reread = w.Gate.Surfaces |> List.skip asked
            Expect.isNonEmpty reread "the re-run was judged"
            Expect.all reread (fun s -> s = FactBrowse) "at FactBrowse"
        }

        test "the reader's grid is the reference evaluator's over the same slice" {
            let w = world ()
            let _, provenance = blockOf (answeredForm w (revenueArgs ranked))
            let result = rerunOk w provenance.Request

            let expected =
                match DataFrame.evalPipeline ranked (sliceTable w 3) with
                | Ok t -> t
                | Error e -> failtestf "%s" (DataFrame.errorString e)

            let grid =
                [ 0 .. Table.rowCount expected - 1 ]
                |> List.map (fun i ->
                    Table.columnNames expected
                    |> List.map (fun c -> PipelineBlock.cellText (cells c expected).[i]))

            Expect.equal (PipelineBlock.rowsOf result) (Ok grid) "the evaluator's own figures"
            Expect.equal result.Columns [ "subject"; "net_total" ] "its columns"
            Expect.isEmpty (PipelineBlock.differences provenance result) "the writer reads what was recorded"
        }

        test "a reader the gate withholds a fact from sees the column withheld and a note saying so" {
            let w = world ()
            let _, provenance = blockOf (answeredForm w (revenueArgs perSubject))
            withheldOne w
            let result = rerunOk w provenance.Request

            Expect.equal (result.Withheld |> List.map _.Column) [ "total" ] "the total reads the withheld revenue"

            match PipelineBlock.redraw<unit> provenance result with
            | Error defect -> failtestf "the block did not draw: %s" (PresentationDefect.describe defect)
            | Ok block ->
                Expect.equal
                    block.Notes
                    [
                        "'total' is withheld: it reads revenue, with 1 cell withheld (1 under trade-figures)."
                    ]
                    "the grid names the withheld column"

                Expect.equal
                    block.Provenance.Withheld
                    [
                        {
                            PolicyRef = "trade-figures"
                            Count = 1
                        }
                    ]
                    "the reader's withheld report"

                Expect.equal block.Provenance.Request provenance.Request "the part's request, verbatim"

            Expect.equal
                (PipelineBlock.differences provenance result)
                [
                    "More is withheld from you than from the writer: 1 withheld cell read now, against 0 when this result was written."
                    "It reads different facts for you: 1 of the facts it was written with is not read now, and 0 are read now that were not."
                ]
                "the differences, word for word"
        }

        test "a reader the policy denies every fact gets a refusal from the re-run, never an empty grid" {
            let w = world ()
            let _, provenance = blockOf (answeredForm w (revenueArgs perSubject))

            w.Gate.Deny [
                for site in [ "s1"; "s2" ] do
                    for offset in 0..2 do
                        revenueFactId w site offset
            ]

            Expect.equal
                (rerun w provenance.Request)
                (Error
                    "no cell of the slice carries a value you may be shown: 6 cell(s) read, 6 withheld under trade-figures")
                "the executor's refusal, verbatim"
        }

        test "a refused evaluation answers the refusal and attaches nothing" {
            let w = world ()
            let form = answeredForm w "{}"
            Expect.isEmpty form.Parts "nothing attached"
            Expect.stringStarts form.Text "evaluate_pipeline refused the request: " "the refusal"
        }
    ]

let tests =
    testList "Phase 1145 — evaluate_pipeline computes over a gated fact slice" [
        refusalTests
        resultTests
        withheldTests
        answerTests
        blockTests
    ]