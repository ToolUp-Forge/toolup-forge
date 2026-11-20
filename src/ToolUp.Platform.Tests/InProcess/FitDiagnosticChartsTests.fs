// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// A released fit diagnostic drawn as a Fuaran-UI chart on the modeller's
/// side: released cells drawn exactly, withheld cells never drawn and always
/// named, a released zero drawn as zero, and a wholly withheld result drawn
/// as nothing.
module ToolUp.Platform.Tests.InProcess.FitDiagnosticChartsTests

open Expecto
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.InterPlatform

/// The floor every template here declares.
let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Aggregate; Histogram ]
}

let private cell (label: string) (count: int) (value: float option) : PrivacyCell = {
    Label = label
    Count = count
    Value = value
}

let private named (shape: OutputShape) (cells: PrivacyCell list) (withheld: string list) =
    FitDiagnosticAnswer.Named {
        Shape = shape
        Cells = cells
        Withheld = withheld
    }

let private declaration (precision: decimal) : FitDiagnosticDeclaration = {
    Bins = None
    Points = []
    Precision = precision
    ResidualThreshold = None
    LeverageThreshold = None
}

let private draw (diagnostic: string) (answer: FitDiagnosticAnswer) : FitDiagnosticChart<unit> =
    match FitDiagnosticCharts.toChart "fit" diagnostic (declaration 0.01m) answer with
    | Ok chart -> chart
    | Error refusal -> failtestf "the answer must be drawn: %s" (FitDiagnosticChartRefusal.describe refusal)

let private specOf (chart: FitDiagnosticChart<'Msg>) : ChartSpec<'Msg> =
    match chart.Chart with
    | Some node ->
        match node.Kind with
        | NodeKind.Chart spec -> spec
        | other -> failtestf "the node is not a chart: %A" other
    | None -> failtest "a chart is drawn"

/// A chart's table, column by column, read back from the embedded source.
let private columnsOf (spec: ChartSpec<'Msg>) : Map<string, Cell list> =
    match spec.Source with
    | Binding.Transform(TransformSource.Data(Embedded table), [], _) ->
        List.zip table.Schema table.Columns
        |> List.map (fun (field, column) -> field.Name, Column.toCells column)
        |> Map.ofList
    | other -> failtestf "the chart is not drawn from an embedded table: %A" other

let private xs (spec: ChartSpec<'Msg>) : string list =
    Map.find spec.XField (columnsOf spec)
    |> List.map (function
        | Cell.Str s -> s
        | Cell.Float f -> string f
        | other -> failtestf "an x cell is %A" other)

let private ys (spec: ChartSpec<'Msg>) (field: string) : float list =
    Map.find field (columnsOf spec)
    |> List.map (function
        | Cell.Float f -> f
        | other -> failtestf "a y cell is %A" other)

let private notesName (chart: FitDiagnosticChart<'Msg>) (text: string) =
    chart.Notes |> List.exists (fun n -> n.Contains text)

let private projectionTests =
    testList "each projection draws its released cells" [
        test "a residual histogram is a bar per released bin, and the withheld bin is named and not drawn" {
            let chart =
                draw
                    FitDiagnostics.ResidualHistogram
                    (named Histogram [ cell "[-2,-1)" 7 None; cell "[-1,0)" 20 None; cell "[0,1)" 22 None ] [
                        "[3,+inf)"
                    ])

            let spec = specOf chart
            Expect.equal spec.Kind ChartKind.Bar "a histogram is a bar chart"
            Expect.equal (xs spec) [ "[-2,-1)"; "[-1,0)"; "[0,1)" ] "the axis is the released bins only"
            Expect.equal (ys spec "points") [ 7.0; 20.0; 22.0 ] "each bar is the released count"
            Expect.equal chart.Withheld (FitDiagnosticWithheld.Named [ "[3,+inf)" ]) "the withheld bin is carried"
            Expect.isTrue (notesName chart "[3,+inf)") "the withheld bin is named beside the chart"
            Expect.equal spec.ValueFormat (Some(Format.Number(Some 0))) "counts are whole numbers"
        }

        test "a residual summary is a line, a released zero is drawn as zero, and values keep the template's precision" {
            let chart =
                draw
                    FitDiagnostics.ResidualSummaryByBin
                    (named Aggregate [
                        cell "[0.05,0.28)" 15 (Some -0.12)
                        cell "[0.28,0.5)" 14 (Some 0.0)
                        cell "[0.5,0.73)" 16 (Some 0.08)
                    ] [])

            let spec = specOf chart
            Expect.equal spec.Kind ChartKind.Line "a residual summary is a line chart"
            Expect.equal (ys spec "mean residual") [ -0.12; 0.0; 0.08 ] "the released zero is drawn as zero"
            Expect.equal spec.ValueFormat (Some(Format.Number(Some 2))) "a precision of 0.01 is two decimals"
            Expect.isEmpty chart.Notes "nothing was withheld, so nothing is named"
        }

        test "a calibration draws predicted and observed per bin, and a withheld bin is left out and named" {
            let chart =
                draw
                    FitDiagnostics.Calibration
                    (named Aggregate [
                        cell "predicted:[0.05,0.35)" 20 (Some 0.2)
                        cell "observed:[0.05,0.35)" 20 (Some 0.15)
                        cell "predicted:[0.35,0.65)" 20 (Some 0.5)
                        cell "observed:[0.35,0.65)" 20 (Some 0.55)
                    ] [ "predicted:[0.65,0.95]"; "observed:[0.65,0.95]" ])

            let spec = specOf chart
            Expect.equal spec.YFields [ "predicted"; "observed" ] "two series"
            Expect.equal (xs spec) [ "[0.05,0.35)"; "[0.35,0.65)" ] "the withheld bin is not on the axis"
            Expect.equal (ys spec "predicted") [ 0.2; 0.5 ] "predicted as released"
            Expect.equal (ys spec "observed") [ 0.15; 0.55 ] "observed as released"
            Expect.isTrue (notesName chart "observed:[0.65,0.95]") "the withheld cells are named"
        }

        test "a threshold curve draws both rates per threshold, a released zero rate included" {
            let chart =
                draw
                    FitDiagnostics.ThresholdCurve
                    (named Aggregate [
                        cell "tpr:0.25" 30 (Some 1.0)
                        cell "fpr:0.25" 30 (Some 0.6)
                        cell "tpr:0.75" 30 (Some 0.4)
                        cell "fpr:0.75" 30 (Some 0.0)
                    ] [])

            let spec = specOf chart
            Expect.equal spec.Kind ChartKind.Line "a threshold curve is a line chart"
            Expect.equal (xs spec) [ "0.25"; "0.75" ] "one point per threshold"
            Expect.equal (ys spec "false positive rate") [ 0.6; 0.0 ] "the released zero rate is drawn as zero"
        }

        test "a quantile comparison is a scatter over the probabilities, the withheld tails named" {
            let chart =
                draw
                    FitDiagnostics.QuantileComparison
                    (named Aggregate [
                        cell "0.25" 15 (Some -0.9)
                        cell "0.5" 30 (Some 0.1)
                        cell "0.75" 15 (Some 1.1)
                    ] [ "0.05"; "0.95" ])

            let spec = specOf chart
            Expect.equal spec.Kind ChartKind.Scatter "a quantile comparison is a scatter"

            let x = Map.find spec.XField (columnsOf spec)

            Expect.equal
                x
                [ Cell.Float 0.25; Cell.Float 0.5; Cell.Float 0.75 ]
                "the x is the released probability, as a number"

            Expect.equal (ys spec "quantile") [ -0.9; 0.1; 1.1 ] "each quantile as released"
            Expect.isTrue (notesName chart "0.05" && notesName chart "0.95") "both tails are named"
        }

        test "an influence summary draws a series the answer neither releases nor withholds as the zero it counted" {
            let chart =
                draw
                    FitDiagnostics.InfluenceSummary
                    (named Histogram [
                        cell "residual:[0,0.5)" 6 None
                        cell "residual:[0.5,1]" 9 None
                        cell "leverage:[0.5,1]" 8 None
                    ] [])

            let spec = specOf chart
            Expect.equal spec.Kind ChartKind.Bar "an influence summary is a bar chart"
            Expect.equal (ys spec "beyond the residual threshold") [ 6.0; 9.0 ] "the released counts"

            Expect.equal
                (ys spec "beyond the leverage threshold")
                [ 0.0; 8.0 ]
                "a named answer that carries no leverage cell at a bin, and withholds none, counted no rows there"
        }

        test "an influence bin whose companion was withheld is left out, never drawn as zero" {
            let chart =
                draw
                    FitDiagnostics.InfluenceSummary
                    (named Histogram [
                        cell "residual:[0,0.5)" 6 None
                        cell "residual:[0.5,1]" 9 None
                        cell "leverage:[0.5,1]" 8 None
                    ] [ "leverage:[0,0.5)" ])

            let spec = specOf chart
            Expect.equal (xs spec) [ "[0.5,1]" ] "the bin with a withheld companion is not drawn"
            Expect.isTrue (notesName chart "[0,0.5) is not drawn") "the bin left out is named with its reason"
            Expect.isTrue (notesName chart "'leverage:[0,0.5)' was withheld") "the reason names the withheld cell"
        }
    ]

let private withheldTests =
    testList "withheld and unstated cells" [
        test "a wholly withheld result yields no chart, and the note gives the gate's reason" {
            let template =
                ModelExecutionProfile.template "fit" floor Set.empty
                |> ModelExecutionProfile.withFitDiagnostic FitDiagnostics.Calibration (declaration 0.01m)

            let answer =
                FitDiagnosticAnswer.ofReply template (Error(PeerCleanRoomWithheld "fit"))
                |> Result.defaultWith (fun e -> failtestf "a gate withhold reads as an answer: %A" e)

            let chart = draw FitDiagnostics.Calibration answer
            Expect.isNone chart.Chart "nothing is drawn"

            match chart.Withheld with
            | FitDiagnosticWithheld.Whole reason ->
                Expect.stringContains reason "'fit' withheld this answer" "the reason is the gate's own words"
                Expect.isTrue (notesName chart reason) "the note carries the reason"
            | other -> failtestf "a wholly withheld result is Whole; got %A" other
        }

        test "an answer that does not name withheld cells says so plainly, and never infers a zero" {
            let chart =
                draw
                    FitDiagnostics.InfluenceSummary
                    (FitDiagnosticAnswer.Unnamed {
                        Shape = Histogram
                        Cells = [
                            cell "residual:[0,0.5)" 6 None
                            cell "residual:[0.5,1]" 9 None
                            cell "leverage:[0.5,1]" 8 None
                        ]
                    })

            let spec = specOf chart
            Expect.equal chart.Withheld FitDiagnosticWithheld.Unstated "the answer states nothing about withholding"
            Expect.isTrue (notesName chart "does not say whether any cell was withheld") "said plainly"
            Expect.equal (xs spec) [ "[0.5,1]" ] "a missing companion is not drawn as zero without the names"
        }

        test "a named answer that withholds everything draws nothing and names every cell" {
            let chart =
                draw FitDiagnostics.ResidualHistogram (named Histogram [] [ "[-1,0)"; "[0,1)" ])

            Expect.isNone chart.Chart "nothing to draw"
            Expect.isTrue (notesName chart "2 cell(s) withheld") "every withheld cell is counted"
            Expect.isTrue (notesName chart "[-1,0)" && notesName chart "[0,1)") "and named"
        }

        test "a reply decodes as the template's release terms say" {
            let released: ReleasedCohort = {
                Shape = Histogram
                Cells = [ cell "[0,1)" 12 None ]
                Withheld = [ "[1,2)" ]
            }

            let json = JsonRpc.serialize released

            let namingTemplate =
                ModelExecutionProfile.template "fit" floor Set.empty
                |> ModelExecutionProfile.withFitDiagnostic FitDiagnostics.ResidualHistogram (declaration 0.01m)

            let plainTemplate = {
                namingTemplate with
                    Release.NamesWithheld = false
            }

            match FitDiagnosticAnswer.ofReply namingTemplate (Ok json) with
            | Ok(FitDiagnosticAnswer.Named r) -> Expect.equal r.Withheld [ "[1,2)" ] "the withheld names arrive"
            | other -> failtestf "a naming template reads Named; got %A" other

            match
                FitDiagnosticAnswer.ofReply
                    plainTemplate
                    (Ok(
                        JsonRpc.serialize {
                            Shape = Histogram
                            Cells = released.Cells
                        }
                    ))
            with
            | Ok(FitDiagnosticAnswer.Unnamed r) -> Expect.equal r.Cells released.Cells "the cells arrive"
            | other -> failtestf "a non-naming template reads Unnamed; got %A" other

            match FitDiagnosticAnswer.ofReply namingTemplate (Error(PeerUnauthorized "no")) with
            | Error(PeerUnauthorized _) -> ()
            | other -> failtestf "any other error stays an error; got %A" other
        }
    ]

let private refusalTests =
    testList "an answer that is not the diagnostic's is refused" [
        test "a name that is not a fit diagnostic" {
            match
                FitDiagnosticCharts.toChart<unit> "fit" "Collinearity" (declaration 0.01m) (named Aggregate [] [])
            with
            | Error(FitDiagnosticChartRefusal.NotAFitDiagnostic "Collinearity") -> ()
            | other -> failtestf "refused by name; got %A" other
        }

        test "a shape the diagnostic does not answer with" {
            match
                FitDiagnosticCharts.toChart<unit>
                    "fit"
                    FitDiagnostics.ResidualHistogram
                    (declaration 0.01m)
                    (named Aggregate [ cell "[0,1)" 12 (Some 1.0) ] [])
            with
            | Error(FitDiagnosticChartRefusal.ShapeMismatch(_, Histogram, Aggregate)) -> ()
            | other -> failtestf "a shape mismatch is refused; got %A" other
        }

        test "a label naming none of the diagnostic's series, a missing aggregate value, a repeated cell" {
            let refusedOn diagnostic shape cells =
                match FitDiagnosticCharts.toChart<unit> "fit" diagnostic (declaration 0.01m) (named shape cells []) with
                | Error(FitDiagnosticChartRefusal.CellUnreadable _) -> ()
                | other -> failtestf "refused as unreadable; got %A" other

            refusedOn FitDiagnostics.Calibration Aggregate [ cell "expected:[0,1)" 12 (Some 0.5) ]
            refusedOn FitDiagnostics.ResidualSummaryByBin Aggregate [ cell "[0,1)" 12 None ]
            refusedOn FitDiagnostics.ResidualHistogram Histogram [ cell "[0,1)" 12 None; cell "[0,1)" 13 None ]
            refusedOn FitDiagnostics.QuantileComparison Aggregate [ cell "median" 12 (Some 0.1) ]
            refusedOn FitDiagnostics.ResidualHistogram Histogram [ cell ("a" + CellLabel.GridSeparator + "b") 12 None ]
        }
    ]

// ── The live path: the shipped projection, released by the shipped broker ──

let private observations: ModelExecutionFitObservation list = [
    for i in 0..59 ->
        let fitted = 0.05 + 0.9 * float i / 59.0
        let observed = if (i * 7) % 10 < int (fitted * 10.0) then 1.0 else 0.0
        let residual = observed - fitted

        {
            Fitted = fitted
            Residual = residual
            Standardised = if i = 59 then 4.0 else residual / 0.5
            Leverage = Some(if i % 4 = 0 then 0.4 else 0.05)
        }
]

let private liveDeclarations: (string * FitDiagnosticDeclaration) list =
    let plain = declaration 0.01m

    [
        FitDiagnostics.ResidualHistogram,
        {
            plain with
                Bins = Some(FitDiagnosticBins.Edges [ -3m; -2m; -1m; 0m; 1m; 2m; 3m ])
        }
        FitDiagnostics.ResidualSummaryByBin,
        {
            plain with
                Bins = Some(FitDiagnosticBins.FixedWidth 4)
        }
        FitDiagnostics.QuantileComparison,
        {
            plain with
                Points = [ 0.05m; 0.25m; 0.5m; 0.75m; 0.95m ]
        }
        FitDiagnostics.Calibration,
        {
            plain with
                Bins = Some(FitDiagnosticBins.Quantiles 3)
        }
        FitDiagnostics.ThresholdCurve,
        {
            plain with
                Points = [ 0.25m; 0.5m; 0.75m ]
        }
        FitDiagnostics.InfluenceSummary,
        {
            plain with
                Bins = Some(FitDiagnosticBins.FixedWidth 2)
                ResidualThreshold = Some 2m
                LeverageThreshold = Some 0.2m
        }
    ]

/// The answer a modeller receives for `diagnostic` over the fixture fit.
let private released (diagnostic: string) (decl: FitDiagnosticDeclaration) : ReleasedCohort =
    let template =
        ModelExecutionProfile.template "fit" floor Set.empty
        |> ModelExecutionProfile.withFitDiagnostic diagnostic decl

    let result =
        FitDiagnostics.project diagnostic decl floor observations
        |> Result.defaultWith (fun e -> failtestf "'%s' projects: %s" diagnostic e)

    match (CleanRoomBroker.create ()).Enforce(template, diagnostic, None, result) with
    | Released(answer, suppressed) -> {
        Shape = answer.Shape
        Cells = answer.Cells
        Withheld = suppressed
      }
    | other -> failtestf "'%s' over the fixture fit is released; got %A" diagnostic other

let private liveTests =
    testList "the shipped projections, released by the broker, draw exactly their released cells" [
        for diagnostic, decl in liveDeclarations ->
            test diagnostic {
                let release = released diagnostic decl

                let chart =
                    match
                        FitDiagnosticCharts.toChart<unit> "fit" diagnostic decl (FitDiagnosticAnswer.Named release)
                    with
                    | Ok chart -> chart
                    | Error refusal -> failtestf "drawn: %s" (FitDiagnosticChartRefusal.describe refusal)

                let spec = specOf chart
                let columns = columnsOf spec

                let drawn = spec.YFields |> List.collect (fun field -> ys spec field) |> List.sort

                let releasedValues =
                    release.Cells
                    |> List.map (fun c ->
                        match release.Shape with
                        | Histogram -> float c.Count
                        | _ -> Option.get c.Value)

                // Every drawn value is a released cell's, and every released cell
                // is drawn once; a drawn zero beyond them is only the counted-none
                // of a count diagnostic.
                let extra =
                    releasedValues
                    |> List.fold
                        (fun (left: float list) v ->
                            match List.tryFindIndex ((=) v) left with
                            | Some i -> List.removeAt i left
                            | None -> failtestf "the released value %g is not drawn" v)
                        drawn

                Expect.isTrue
                    (extra |> List.forall ((=) 0.0)
                     && (release.Shape = Histogram || List.isEmpty extra))
                    $"no value but the released cells appears in the chart (extra: %A{extra})"

                let axis = xs spec |> Set.ofList

                for label in release.Withheld do
                    let x =
                        match label.IndexOf ':' with
                        | -1 -> label
                        | at -> label.Substring(at + 1)

                    Expect.isTrue (notesName chart label) $"the withheld '{label}' is named"

                    if diagnostic = FitDiagnostics.QuantileComparison then
                        Expect.isFalse
                            (Map.find spec.XField columns |> List.contains (Cell.Float(float x)))
                            $"the withheld '{label}' is not drawn"
                    elif not (List.isEmpty spec.YFields) && spec.YFields.Length = 1 then
                        Expect.isFalse (Set.contains x axis) $"the withheld '{label}' is not on the axis"
            }
    ]

let tests =
    testList "fit-diagnostic charts on the modeller's side" [ projectionTests; withheldTests; refusalTests; liveTests ]