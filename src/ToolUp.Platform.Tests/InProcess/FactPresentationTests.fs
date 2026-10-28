// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FactPresentationTests

// ─── Phase 1129 — fact presentation builders ─────────────────────────
//
// From a presentation request and a gated fact series to a Fuaran-UI node and
// its provenance. The claims, each held to fixture facts under .NET:
//
//   1. The request's JSON codec round-trips every kind, and refuses a
//      malformed request BY NAME (an unknown kind, a missing table id, a
//      chart naming two metrics, a compare-to on a trend, a malformed day).
//   2. A withheld or absent point is never a number in any row: a trend drops
//      the day and marks it, a breakdown and a comparison drop the subject, a
//      table drops the cell — and each is named in the notes.
//   3. A series the read refused becomes a callout naming the refusal, and a
//      chart the lowering would refuse becomes a callout naming that.
//   4. A table draws a Fact node per published cell carrying its fact id.
//   5. Every built node passes the Fuaran-UI pre-emit validator, and the
//      validator probe is not vacuous: it refuses an ungrounded chart field.
//   6. The provenance names the request, the read's fact ids and its
//      withheld counts by policy, with the request kind as `Kind`.

open System
open Expecto
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.Platform.AI
open ToolUp.Facts
open ToolUp.Facts.Presentation

let private day (d: int) =
    DateTime(2026, 10, d, 0, 0, 0, DateTimeKind.Utc)

/// One point: `value = None` with `withheld = true` is a gated point.
let private point (subject: string) (d: int) (value: decimal option) (withheld: bool) : FactSeriesPoint = {
    Subject = "site/" + subject
    SubjectPath = [ subject ]
    PeriodFrom = day d
    PeriodTo = day (d + 1)
    PeriodLabel = None
    Value = (if withheld then None else value)
    Rendering =
        (if withheld then
             None
         else
             value |> Option.map (sprintf "£%M"))
    FactId = sprintf "fact-%s-%d" subject d
    AsOf = day 20
    Freshness = "fresh"
    Withheld = withheld
}

let private series (metric: string) (points: FactSeriesPoint list) : FactSeries = {
    TableId = "trade.site"
    Metric = metric
    SubjectPrefix = []
    SubjectLevel = "site"
    PeriodFrom = day 1
    PeriodTo = day 4
    DisplayFormat = ""
    Points = points
    MaxPoints = 2000
    WithheldCount = points |> List.filter _.Withheld |> List.length
    Withheld =
        match points |> List.filter _.Withheld |> List.length with
        | 0 -> []
        | n -> [ { PolicyRef = "small-cells"; Count = n } ]
}

/// Revenue for north and south over three days; south's 2 October point is
/// withheld by the gate.
let private revenue =
    series "revenue" [
        point "north" 1 (Some 100m) false
        point "north" 2 (Some 110m) false
        point "north" 3 (Some 120m) false
        point "south" 1 (Some 300m) false
        point "south" 2 None true
        point "south" 3 (Some 330m) false
    ]

let private orders =
    series "orders" [ point "north" 2 (Some 11m) false; point "south" 2 (Some 33m) false ]

let private request (kind: PresentationKind) (metrics: string list) (lastDay: int) : PresentationRequest = {
    Kind = kind
    TableId = "trade.site"
    Metrics = metrics
    SubjectPrefix = []
    SubjectLevel = None
    Period = PresentationPeriod.Ending(day lastDay, 3)
    CompareTo = None
    Title = None
    Annotations = []
}

let private built (r: PresentationRequest) (answers: SeriesAnswer list) : PresentationBlock<unit> =
    match Presentation.build r answers with
    | Ok block -> block
    | Error defect -> failtestf "the builder refused: %s" (PresentationDefect.describe defect)

/// Every node of a tree, pre-order.
let rec private nodes (n: Node<'Msg>) : Node<'Msg> list =
    n
    :: (match n.Kind with
        | NodeKind.Box spec -> spec.Children |> List.collect nodes
        | _ -> [])

let private chartOf (n: Node<'Msg>) : ChartSpec<'Msg> option =
    nodes n
    |> List.tryPick (fun x ->
        match x.Kind with
        | NodeKind.Chart spec -> Some spec
        | _ -> None)

/// A chart's rows, read back from the columnar table it is drawn from.
let private rowsOf (spec: ChartSpec<'Msg>) : Row list =
    match spec.Source with
    | Binding.Transform(TransformSource.Data(Embedded table), [], _) ->
        let columns =
            List.zip table.Schema table.Columns
            |> List.map (fun (field, column) -> field.Name, Column.toCells column)

        let count = columns |> List.map (snd >> List.length) |> List.max

        [
            for i in 0 .. count - 1 ->
                columns
                |> List.map (fun (name, cells) ->
                    name,
                    match List.item i cells with
                    | Cell.Date s
                    | Cell.Str s -> box s
                    | Cell.Float f -> box f
                    | other -> box other)
                |> Map.ofList
        ]
    | other -> failtestf "the chart is not drawn from an embedded table: %A" other

let private callouts (n: Node<'Msg>) : CalloutSpec list =
    nodes n
    |> List.choose (fun x ->
        match x.Kind with
        | NodeKind.Callout spec -> Some spec
        | _ -> None)

let private literal (t: TextSource) =
    match t with
    | TextSource.Literal s -> s
    | other -> failtestf "not a literal: %A" other

let private notesContain (block: PresentationBlock<'Msg>) (text: string) =
    block.Notes |> List.exists (fun n -> n.Contains text)

let private codecTests =
    testList "the request codec" [
        test "every kind round-trips through JSON" {
            let full = {
                Kind = PresentationKind.Comparison
                TableId = "trade.channel"
                Metrics = [ "orders" ]
                SubjectPrefix = [ "uk"; "web" ]
                SubjectLevel = Some "channel"
                Period = PresentationPeriod.Between(day 1, day 15)
                CompareTo = Some PresentationCompareTo.PreviousDelivery
                Title = Some "Orders by channel"
                Annotations = [
                    {
                        On = day 4
                        Label = "Promotion starts"
                    }
                ]
            }

            let requests = [
                full
                request PresentationKind.Trend [ "revenue" ] 3
                request PresentationKind.Breakdown [ "revenue" ] 2
                request PresentationKind.Table [ "revenue"; "orders" ] 2
            ]

            for r in requests do
                let json = PresentationRequest.toJson r
                Expect.equal (PresentationRequest.ofJson json) (Ok r) (sprintf "%s round-trips" json)
        }

        test "an unknown kind is refused by name" {
            let json =
                """{"kind":"pie","tableId":"trade.site","metrics":["revenue"],"period":{"day":"2026-10-02"}}"""

            let result = PresentationRequest.ofJson json
            Expect.equal result (Error(PresentationRequestError.UnknownKind "pie")) "refused as an unknown kind"

            match result with
            | Error e -> Expect.stringContains (PresentationRequestError.describe e) "'pie'" "the message names it"
            | Ok _ -> ()
        }

        test "a missing table id is refused by name" {
            let json =
                """{"kind":"trend","metrics":["revenue"],"period":{"day":"2026-10-02"}}"""

            let result = PresentationRequest.ofJson json
            Expect.equal result (Error(PresentationRequestError.MissingField "tableId")) "refused as missing"

            match result with
            | Error e -> Expect.stringContains (PresentationRequestError.describe e) "'tableId'" "the message names it"
            | Ok _ -> ()

            let blank =
                """{"kind":"trend","tableId":"  ","metrics":["revenue"],"period":{"day":"2026-10-02"}}"""

            Expect.equal
                (PresentationRequest.ofJson blank)
                (Error(PresentationRequestError.MissingField "tableId"))
                "a blank table id is missing too"
        }

        test "every other malformed request is refused naming its field" {
            let field json =
                match PresentationRequest.ofJson json with
                | Error(PresentationRequestError.InvalidField(f, _)) -> f
                | Error(PresentationRequestError.MissingField f) -> f
                | other -> failtestf "expected a named refusal for %s, got %A" json other

            Expect.equal
                (field """{"kind":"trend","tableId":"t","metrics":["a","b"],"period":{"day":"2026-10-02"}}""")
                "metrics"
                "a chart names one metric"

            Expect.equal
                (field
                    """{"kind":"trend","tableId":"t","metrics":["a"],"period":{"day":"2026-10-02"},"compareTo":"previousDelivery"}""")
                "compareTo"
                "a trend compares to nothing"

            Expect.equal
                (field """{"kind":"trend","tableId":"t","metrics":["a"],"period":{"day":"2026-02-30"}}""")
                "period.day"
                "a day that is not on the calendar"

            Expect.equal (field """{"kind":"trend","tableId":"t","metrics":["a"]}""") "period" "no period"

            Expect.equal
                (field """{"kind":"table","tableId":"t","period":{"day":"2026-10-02"}}""")
                "metrics"
                "no metric"

            Expect.equal
                (field
                    """{"kind":"trend","tableId":"t","metrics":["a"],"period":{"from":"2026-10-05","to":"2026-10-01"}}""")
                "period"
                "a range that ends before it starts"

            Expect.equal (PresentationRequest.ofJson "not json") (Error PresentationRequestError.NotAnObject) "not JSON"
        }

        test "the request asks for one series read per metric over its range" {
            let queries =
                PresentationRequest.toQueries 2000 (request PresentationKind.Table [ "revenue"; "orders" ] 3)

            Expect.equal (queries |> List.map _.Metric) [ "revenue"; "orders" ] "one per metric, in order"
            Expect.equal queries.Head.PeriodFrom (day 1) "the range starts rangeDays - 1 days before the day"
            Expect.equal queries.Head.PeriodTo (day 4) "and ends after the day (exclusive)"
        }
    ]

let private builderTests =
    testList "the builders" [
        test "a trend drops a day with a withheld point, marks it, and names it" {
            let block =
                built (request PresentationKind.Trend [ "revenue" ] 3) [ SeriesAnswer.Answered revenue ]

            let chart = chartOf block.Node |> Option.get
            let rows = rowsOf chart
            let days = rows |> List.map (fun r -> unbox<string> r.["day"])
            Expect.equal days [ "2026-10-01"; "2026-10-03" ] "the gap day is not a row"
            Expect.equal chart.YFields [ "north"; "south" ] "one line per subject"

            for r in rows do
                Expect.equal r.Count 3 "every drawn row carries every subject's number"

            Expect.isTrue (notesContain block "2026-10-02 is not drawn") "the gap day is named"
            Expect.isTrue (notesContain block "withheld from you by the disclosure gate") "with its reason"

            let markers =
                chart.Annotations
                |> Option.defaultValue []
                |> List.choose (function
                    | ChartAnnotation.EventMarker(ChartAnnotationX.Date d, _) -> Some d
                    | _ -> None)

            Expect.equal markers [ "2026-10-02" ] "the gap day is marked on the axis"
        }

        test "a breakdown leaves a withheld subject out of the rows and names it" {
            let block =
                built (request PresentationKind.Breakdown [ "revenue" ] 2) [ SeriesAnswer.Answered revenue ]

            let rows = chartOf block.Node |> Option.get |> rowsOf
            Expect.equal (rows |> List.map (fun r -> unbox<string> r.["subject"])) [ "north" ] "south is not a row"
            Expect.isTrue (notesContain block "south is not drawn") "south is named"
        }

        test "a breakdown draws its bars largest first" {
            let block =
                built (request PresentationKind.Breakdown [ "revenue" ] 3) [ SeriesAnswer.Answered revenue ]

            let rows = chartOf block.Node |> Option.get |> rowsOf
            let subjects = rows |> List.map (fun r -> unbox<string> r.["subject"])
            Expect.equal subjects [ "south"; "north" ] "largest first"

            Expect.equal
                (rows |> List.map (fun r -> unbox<float> r.["revenue"]))
                [ 330.0; 120.0 ]
                "the published numbers"
        }

        test "a comparison draws only subjects published on both deliveries" {
            let block =
                built (request PresentationKind.Comparison [ "revenue" ] 3) [ SeriesAnswer.Answered revenue ]

            let chart = chartOf block.Node |> Option.get
            Expect.equal chart.YFields [ "2026-10-02"; "2026-10-03" ] "this delivery against the one before"
            let rows = rowsOf chart
            Expect.equal (rows |> List.map (fun r -> unbox<string> r.["subject"])) [ "north" ] "south is not a row"
            Expect.isTrue (notesContain block "south is not drawn: 2026-10-02 withheld") "south is named, with why"
        }

        test "a table draws a Fact node per published cell carrying its fact id" {
            let block =
                built (request PresentationKind.Table [ "revenue"; "orders" ] 2) [
                    SeriesAnswer.Answered revenue
                    SeriesAnswer.Answered orders
                ]

            let facts =
                nodes block.Node
                |> List.choose (fun n ->
                    match n.Kind with
                    | NodeKind.Fact spec -> Some(literal spec.Label, literal spec.Value, n.ExtraAttributes)
                    | _ -> None)

            Expect.equal
                facts
                [
                    "revenue", "£110", Some(Map.ofList [ "data-fact", "fact-north-2" ])
                    "orders", "£11", Some(Map.ofList [ "data-fact", "fact-north-2" ])
                    "orders", "£33", Some(Map.ofList [ "data-fact", "fact-south-2" ])
                ]
                "three published cells; south's withheld revenue is not one"

            Expect.isTrue (notesContain block "south, revenue is not shown") "the withheld cell is named"
            Expect.isNone (chartOf block.Node) "a table draws no chart"
        }

        test "a series the read refused becomes a callout naming the refusal" {
            let why = "the series has 2,400 points; the cap is 2,000"

            let block =
                built (request PresentationKind.Trend [ "revenue" ] 3) [ SeriesAnswer.Refused("revenue", why) ]

            Expect.isNone (chartOf block.Node) "nothing is drawn"
            let bodies = callouts block.Node |> List.map (fun c -> literal c.Body)
            Expect.equal bodies [ why ] "the refusal, verbatim, in a callout"
            Expect.isEmpty block.Provenance.FactIds "a refused read cites no fact"
        }

        test "a table drawn around a refused column names the refusal and draws the rest" {
            let block =
                built (request PresentationKind.Table [ "revenue"; "orders" ] 2) [
                    SeriesAnswer.Answered revenue
                    SeriesAnswer.Refused("orders", "over the cap")
                ]

            Expect.equal (callouts block.Node |> List.map (fun c -> literal c.Body)) [ "over the cap" ] "a callout"

            let factCount =
                nodes block.Node
                |> List.filter (fun n ->
                    match n.Kind with
                    | NodeKind.Fact _ -> true
                    | _ -> false)
                |> List.length

            Expect.equal factCount 1 "north's revenue is still drawn"
        }

        test "a chart the lowering would refuse is a callout, never a truncated picture" {
            let wide =
                series "revenue" [ for i in 1..40 -> point (sprintf "s%02d" i) 1 (Some(decimal i)) false ]

            let block =
                built (request PresentationKind.Trend [ "revenue" ] 1) [ SeriesAnswer.Answered wide ]

            Expect.isNone (chartOf block.Node) "the chart is not emitted"

            Expect.exists
                (callouts block.Node)
                (fun c -> (literal c.Body).Contains "40 series")
                "the lowering's refusal is named"
        }

        test "subjects sharing a member are labelled by their whole path" {
            let twins =
                series "revenue" [
                    {
                        point "a" 1 (Some 1m) false with
                            Subject = "site/north>a"
                            SubjectPath = [ "north"; "a" ]
                    }
                    {
                        point "a" 1 (Some 2m) false with
                            Subject = "site/south>a"
                            SubjectPath = [ "south"; "a" ]
                            FactId = "fact-south-a-1"
                    }
                ]

            let block =
                built (request PresentationKind.Trend [ "revenue" ] 1) [ SeriesAnswer.Answered twins ]

            Expect.equal (chartOf block.Node |> Option.get).YFields [ "site/north>a"; "site/south>a" ] "two lines"
        }

        test "answers that are not the request's reads are refused" {
            let r = request PresentationKind.Trend [ "revenue" ] 3

            let refusedWith answers =
                match Presentation.build r answers with
                | Error(PresentationDefect.AnswersMismatch _) -> true
                | _ -> false

            Expect.isTrue (refusedWith []) "no answer"
            Expect.isTrue (refusedWith [ SeriesAnswer.Answered orders ]) "another metric"
            Expect.isTrue (refusedWith [ SeriesAnswer.Answered { revenue with TableId = "other" } ]) "another table"

            Expect.isTrue
                (refusedWith [ SeriesAnswer.Answered revenue; SeriesAnswer.Answered revenue ])
                "the metric twice"

            match Presentation.build { r with TableId = "" } [ SeriesAnswer.Answered revenue ] with
            | Error(PresentationDefect.Request(PresentationRequestError.MissingField "tableId")) -> ()
            | other -> failtestf "a malformed request is refused by name: %A" other
        }
    ]

let private validationTests =
    testList "the pre-emit validator" [
        test "every built node passes it" {
            let blocks: PresentationBlock<unit> list = [
                built (request PresentationKind.Trend [ "revenue" ] 3) [ SeriesAnswer.Answered revenue ]
                built (request PresentationKind.Breakdown [ "revenue" ] 2) [ SeriesAnswer.Answered revenue ]
                built (request PresentationKind.Comparison [ "revenue" ] 3) [ SeriesAnswer.Answered revenue ]
                built (request PresentationKind.Table [ "revenue"; "orders" ] 2) [
                    SeriesAnswer.Answered revenue
                    SeriesAnswer.Answered orders
                ]
                built (request PresentationKind.Trend [ "revenue" ] 3) [ SeriesAnswer.Refused("revenue", "no") ]
                built
                    {
                        request PresentationKind.Trend [ "revenue" ] 3 with
                            Annotations = [ { On = day 1; Label = "Launch" }; { On = day 9; Label = "Later" } ]
                    }
                    [ SeriesAnswer.Answered revenue ]
            ]

            for b in blocks do
                Expect.isEmpty (Presentation.findings b.Node) (sprintf "%s passes" b.Node.Id)
        }

        test "the probe is not vacuous: an ungrounded chart field is refused" {
            let block =
                built (request PresentationKind.Breakdown [ "revenue" ] 3) [ SeriesAnswer.Answered revenue ]

            let chart = chartOf block.Node |> Option.get

            let broken: Node<unit> =
                Fuaran.chart "broken" {
                    chart with
                        YFields = [ "not-a-column" ]
                }

            Expect.isNonEmpty (Presentation.findings broken) "the validator names the ungrounded field"
        }
    ]

let private provenanceTests =
    testList "the provenance" [
        test "it records the request, the fact ids and the withheld counts" {
            let r = request PresentationKind.Trend [ "revenue" ] 3
            let block = built r [ SeriesAnswer.Answered revenue ]
            let p = block.Provenance
            Expect.equal p.Kind "trend" "the request's kind"
            Expect.isFalse (BlockProvenance.isTable p) "a trend is not a table"
            Expect.equal (PresentationRequest.ofJson p.Request) (Ok r) "the request, as JSON"
            Expect.equal p.FactIds (revenue.Points |> List.map _.FactId) "every point's id, in read order"
            Expect.equal p.Withheld [ { PolicyRef = "small-cells"; Count = 1 } ] "withheld by policy"
            Expect.equal (p.TableId, p.Metric, p.Period) ("trade.site", "revenue", "2026-10-01/2026-10-04") "the read"
        }

        test "a table block's kind is the table kind" {
            let block =
                built (request PresentationKind.Table [ "revenue"; "orders" ] 2) [
                    SeriesAnswer.Answered revenue
                    SeriesAnswer.Answered orders
                ]

            Expect.isTrue (BlockProvenance.isTable block.Provenance) "exports as a table"
            Expect.equal block.Provenance.Metric "revenue,orders" "both metrics"
            Expect.equal block.Provenance.Withheld [ { PolicyRef = "small-cells"; Count = 1 } ] "summed by policy"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 1129 — fact presentation builders" [ codecTests; builderTests; validationTests; provenanceTests ]