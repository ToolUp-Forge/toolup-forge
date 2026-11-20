// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types

// ─── A released fit diagnostic, drawn on the modeller's side ──────────
//
// A fit diagnostic crosses the governed-diagnostics contract as a gated
// `CohortResult` (`FitDiagnostics.project`, released by the clean-room
// gate). A modeller wants to look at it, so this module turns the answer it
// received into a Fuaran-UI `Chart` node. It is pure and it reads the
// released answer only: it takes no outcome, spec or row, so nothing it draws
// can carry more than the gate released. The disclosure control is on the
// release, never on the drawing.
//
// **A cell is released, absent or withheld, and the three never collapse.**
//
//   * A RELEASED cell is drawn, with exactly the value the answer carries; a
//     released zero is drawn as zero.
//   * A WITHHELD cell held 1 to k−1 rows and its value was suppressed. It is
//     never drawn: the shipped chart arms read a missing value as 0.0, so a
//     withheld cell handed to a chart would draw as zero, the one reading
//     that is certainly false. It is named beside the chart instead, when the
//     template names withheld cells; when it does not, the notes say plainly
//     that the answer does not say whether any cell was withheld.
//   * An ABSENT cell held no rows, and the projection does not emit it. The
//     axis is the released cells' only, so an absent bin is not on it. Inside
//     a drawn bin of a count diagnostic, a series the answer neither releases
//     nor names as withheld counted no rows, and is drawn as the zero it is;
//     that inference needs the withheld names, so an answer without them
//     never makes it.
//
// A bin is drawn only when every series drawn has a value there; otherwise it
// is left out and named, with the reason, rather than drawn with a gap the
// chart would fill with a zero.

/// A fit-diagnostic answer as the modeller received it.
[<RequireQualifiedAccess>]
type FitDiagnosticAnswer =
    /// A release under a template that names withheld cells: the released
    /// cells and the labels of the cells the floor withheld.
    | Named of release: ReleasedCohort
    /// A release under a template that does not name withheld cells. The
    /// answer cannot say whether any cell was withheld.
    | Unnamed of result: CohortResult
    /// The gate withheld the whole result, for the reason it gave.
    | Withheld of reason: string

/// What a drawn fit diagnostic says the floor withheld.
[<RequireQualifiedAccess>]
type FitDiagnosticWithheld =
    /// The answer names the withheld cells' labels; none when empty.
    | Named of labels: string list
    /// The answer does not say whether any cell was withheld.
    | Unstated
    /// The whole result was withheld, for the reason the gate gave.
    | Whole of reason: string

/// A fit diagnostic drawn for the modeller.
type FitDiagnosticChart<'Msg> = {
    /// The `Chart` node, validated, drawn from an embedded table whose rows
    /// are exactly the released cells it draws. `None` when nothing is
    /// drawn: the whole result was withheld, or no bin could be drawn.
    Chart: Node<'Msg> option
    /// What the floor withheld, as the answer states it.
    Withheld: FitDiagnosticWithheld
    /// The sentences to show beside the chart: each withheld cell named (or
    /// that the answer does not say), each bin left out and why, or why
    /// nothing is drawn.
    Notes: string list
}

/// Why a fit-diagnostic answer could not be drawn.
[<RequireQualifiedAccess>]
type FitDiagnosticChartRefusal =
    /// The name is not one of the profile's fit diagnostics.
    | NotAFitDiagnostic of name: string
    /// The answer's shape is not the one the diagnostic answers with.
    | ShapeMismatch of diagnostic: string * expected: OutputShape * actual: OutputShape
    /// A released cell does not read as one of the diagnostic's cells.
    | CellUnreadable of diagnostic: string * label: string * reason: string
    /// The chart lowering or the pre-emit validator refused the built chart.
    | ChartRefused of reason: string

/// Reading a fit-diagnostic answer and a refusal to draw one.
[<RequireQualifiedAccess>]
module FitDiagnosticAnswer =

    /// Read the reply to a fit-diagnostic call under `template`, the
    /// template both parties approved: a release decodes as `Named` when the
    /// template names withheld cells and as `Unnamed` otherwise, and the
    /// gate's whole-result withhold becomes `Withheld` in the gate's words.
    /// Any other error, and a body that does not decode, stays an error.
    let ofReply
        (template: CleanRoomTemplate)
        (reply: Result<string, PeerError>)
        : Result<FitDiagnosticAnswer, PeerError> =
        match reply with
        | Ok json ->
            try
                if template.Release.NamesWithheld then
                    Ok(FitDiagnosticAnswer.Named(JsonRpc.deserialize<ReleasedCohort> json))
                else
                    Ok(FitDiagnosticAnswer.Unnamed(JsonRpc.deserialize<CohortResult> json))
            with e ->
                Error(PeerDeserialization e.Message)
        | Error(PeerCleanRoomWithheld _ as withheld) -> Ok(FitDiagnosticAnswer.Withheld(JsonRpc.errorMessage withheld))
        | Error e -> Error e

/// Reading a refusal to draw a fit diagnostic.
[<RequireQualifiedAccess>]
module FitDiagnosticChartRefusal =

    /// The refusal as one sentence.
    let describe (refusal: FitDiagnosticChartRefusal) : string =
        match refusal with
        | FitDiagnosticChartRefusal.NotAFitDiagnostic name -> $"'{name}' is not a fit diagnostic"
        | FitDiagnosticChartRefusal.ShapeMismatch(diagnostic, expected, actual) ->
            $"'{diagnostic}' answers with shape {expected}; this answer is {actual}"
        | FitDiagnosticChartRefusal.CellUnreadable(diagnostic, label, reason) ->
            $"'{diagnostic}' cannot read the cell '{label}': {reason}"
        | FitDiagnosticChartRefusal.ChartRefused reason -> $"the chart was refused: {reason}"

/// Draws a released fit diagnostic as a Fuaran-UI chart.
[<RequireQualifiedAccess>]
module FitDiagnosticCharts =

    /// One series: the label prefix its cells carry ("" for an unprefixed
    /// diagnostic) and the column it is drawn as.
    type private Series = { Prefix: string; Field: string }

    /// How one diagnostic is drawn.
    type private Arm = {
        Title: string
        Kind: ChartKind
        Shape: OutputShape
        XField: string
        XTitle: string
        YTitle: string
        /// The x is a number (a scatter's linear axis) rather than a category.
        NumericX: bool
        Series: Series list
    }

    let private single (field: string) = [ { Prefix = ""; Field = field } ]

    let private armOf (diagnostic: string) : Arm option =
        let arm title kind shape xField xTitle yTitle series = {
            Title = title
            Kind = kind
            Shape = shape
            XField = xField
            XTitle = xTitle
            YTitle = yTitle
            NumericX = false
            Series = series
        }

        match diagnostic with
        | FitDiagnostics.ResidualHistogram ->
            Some(
                arm
                    "Residual histogram"
                    ChartKind.Bar
                    Histogram
                    "bin"
                    "Standardised residual"
                    "Points"
                    (single "points")
            )
        | FitDiagnostics.ResidualSummaryByBin ->
            Some(
                arm
                    "Mean residual by fitted value"
                    ChartKind.Line
                    Aggregate
                    "bin"
                    "Fitted value"
                    "Mean residual"
                    (single "mean residual")
            )
        | FitDiagnostics.Calibration ->
            Some(
                arm "Calibration" ChartKind.Line Aggregate "bin" "Predicted value" "Mean" [
                    {
                        Prefix = "predicted"
                        Field = "predicted"
                    }
                    {
                        Prefix = "observed"
                        Field = "observed"
                    }
                ]
            )
        | FitDiagnostics.ThresholdCurve ->
            Some(
                arm "Threshold curve" ChartKind.Line Aggregate "threshold" "Threshold" "Rate" [
                    {
                        Prefix = "tpr"
                        Field = "true positive rate"
                    }
                    {
                        Prefix = "fpr"
                        Field = "false positive rate"
                    }
                ]
            )
        | FitDiagnostics.QuantileComparison ->
            Some {
                arm
                    "Standardised residual quantiles"
                    ChartKind.Scatter
                    Aggregate
                    "probability"
                    "Probability"
                    "Standardised residual"
                    (single "quantile") with
                    NumericX = true
            }
        | FitDiagnostics.InfluenceSummary ->
            Some(
                arm "Influence summary" ChartKind.Bar Histogram "bin" "Fitted value" "Points" [
                    {
                        Prefix = "residual"
                        Field = "beyond the residual threshold"
                    }
                    {
                        Prefix = "leverage"
                        Field = "beyond the leverage threshold"
                    }
                ]
            )
        | _ -> None

    /// The number of decimal places `precision` carries: 0.01 is 2, 0.5 is
    /// 1, 5 is 0.
    let private decimalsOf (precision: decimal) : int =
        let rec go (d: decimal) (n: int) =
            if d = Math.Truncate d || n >= 28 then
                n
            else
                go (d * 10m) (n + 1)

        go (abs precision) 0

    /// A released cell read as (series, x, value).
    let private read
        (diagnostic: string)
        (arm: Arm)
        (cell: PrivacyCell)
        : Result<Series * string * float, FitDiagnosticChartRefusal> =
        let unreadable reason =
            Error(FitDiagnosticChartRefusal.CellUnreadable(diagnostic, cell.Label, reason))

        let series, x =
            match arm.Series with
            | [ only ] when only.Prefix = "" -> Some only, cell.Label
            | prefixed ->
                match cell.Label.IndexOf ':' with
                | -1 -> None, cell.Label
                | at ->
                    let prefix = cell.Label.Substring(0, at)
                    prefixed |> List.tryFind (fun s -> s.Prefix = prefix), cell.Label.Substring(at + 1)

        let value =
            match arm.Shape with
            | Histogram -> Some(float cell.Count)
            | _ -> cell.Value

        match series, value with
        | _ when not (CellLabel.isPlain cell.Label) ->
            unreadable "a two-dimensional label in a one-dimensional diagnostic"
        | None, _ ->
            let prefixes = arm.Series |> List.map _.Prefix |> String.concat ", "
            unreadable $"its label names none of the series ({prefixes})"
        | Some _, None -> unreadable "an aggregate cell carries no value"
        | Some _, Some v when Double.IsNaN v || Double.IsInfinity v -> unreadable "its value is not finite"
        | Some _, Some _ when
            arm.NumericX
            && not (
                fst (Double.TryParse(x, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture))
            )
            ->
            unreadable "its label is not a number"
        | Some s, Some v -> Ok(s, x, v)

    let private labelOf (series: Series) (x: string) =
        if series.Prefix = "" then x else $"{series.Prefix}:{x}"

    /// Draw `answer`, the answer to `diagnostic`, as a chart node with id
    /// `id`, its value axis formatted to `declaration`'s precision (counts
    /// as whole numbers). The rows are the released cells and nothing else;
    /// a withheld cell is never drawn, and is named in the notes.
    let toChart<'Msg>
        (id: string)
        (diagnostic: string)
        (declaration: FitDiagnosticDeclaration)
        (answer: FitDiagnosticAnswer)
        : Result<FitDiagnosticChart<'Msg>, FitDiagnosticChartRefusal> =
        match armOf diagnostic, answer with
        | None, _ -> Error(FitDiagnosticChartRefusal.NotAFitDiagnostic diagnostic)
        | Some _, FitDiagnosticAnswer.Withheld reason ->
            Ok {
                Chart = None
                Withheld = FitDiagnosticWithheld.Whole reason
                Notes = [ $"Nothing is drawn: {reason}" ]
            }
        | Some arm, (FitDiagnosticAnswer.Named { Shape = shape } | FitDiagnosticAnswer.Unnamed { Shape = shape }) when
            shape <> arm.Shape
            ->
            Error(FitDiagnosticChartRefusal.ShapeMismatch(diagnostic, arm.Shape, shape))
        | Some arm, released ->
            let cells, withheld =
                match released with
                | FitDiagnosticAnswer.Named release -> release.Cells, FitDiagnosticWithheld.Named release.Withheld
                | FitDiagnosticAnswer.Unnamed result -> result.Cells, FitDiagnosticWithheld.Unstated
                | FitDiagnosticAnswer.Withheld reason -> [], FitDiagnosticWithheld.Whole reason

            let withheldLabels =
                match withheld with
                | FitDiagnosticWithheld.Named labels -> Set.ofList labels
                | _ -> Set.empty

            let readCells =
                cells
                |> List.fold
                    (fun acc cell ->
                        match acc, read diagnostic arm cell with
                        | Error e, _
                        | _, Error e -> Error e
                        | Ok seen, Ok(series, x, _) when Map.containsKey (series.Field, x) (snd seen) ->
                            Error(
                                FitDiagnosticChartRefusal.CellUnreadable(
                                    diagnostic,
                                    labelOf series x,
                                    "the answer carries it twice"
                                )
                            )
                        | Ok(xs, values), Ok(series, x, v) ->
                            Ok((if List.contains x xs then xs else xs @ [ x ]), Map.add (series.Field, x) v values))
                    (Ok([], Map.empty))

            match readCells with
            | Error e -> Error e
            | Ok(xs, values) ->
                let drawnSeries =
                    arm.Series
                    |> List.filter (fun s -> xs |> List.exists (fun x -> Map.containsKey (s.Field, x) values))

                // A series missing at `x`: Ok 0.0 when the answer says it
                // counted no rows there, else the reason the bin is not drawn.
                let missing (x: string) (s: Series) : Result<float, string> =
                    let label = labelOf s x

                    match withheld with
                    | FitDiagnosticWithheld.Named _ when Set.contains label withheldLabels ->
                        Error $"'{label}' was withheld"
                    | FitDiagnosticWithheld.Named _ when arm.Shape = Histogram -> Ok 0.0
                    | FitDiagnosticWithheld.Named _ -> Error $"the answer carries no '{label}'"
                    | _ -> Error $"the answer carries no '{label}' and does not say whether it was withheld"

                // Each x with every drawn series' value is a row; any other x
                // is left out, with the reasons it is.
                let drawnOrLeft =
                    xs
                    |> List.map (fun x ->
                        let cellsAt =
                            drawnSeries
                            |> List.map (fun s ->
                                match Map.tryFind (s.Field, x) values with
                                | Some v -> s.Field, Ok v
                                | None -> s.Field, missing x s)

                        let reasons =
                            cellsAt
                            |> List.choose (function
                                | _, Error why -> Some why
                                | _, Ok _ -> None)

                        if List.isEmpty reasons then
                            Choice1Of2(x, cellsAt |> List.map (fun (field, v) -> field, Result.defaultValue 0.0 v))
                        else
                            Choice2Of2 $"""{x} is not drawn: {String.concat "; " reasons}.""")

                let rows =
                    drawnOrLeft
                    |> List.choose (function
                        | Choice1Of2 row -> Some row
                        | Choice2Of2 _ -> None)

                let left =
                    drawnOrLeft
                    |> List.choose (function
                        | Choice2Of2 note -> Some note
                        | Choice1Of2 _ -> None)

                let withheldNotes =
                    match withheld with
                    | FitDiagnosticWithheld.Named [] -> []
                    | FitDiagnosticWithheld.Named labels -> [
                        $"""{List.length labels} cell(s) withheld by the data host's privacy floor and not drawn: {String.concat ", " labels}."""
                      ]
                    | FitDiagnosticWithheld.Unstated -> [
                        "The data host's template does not name withheld cells, so this answer does not say whether any cell was withheld; a bin it does not carry held no rows or too few to release."
                      ]
                    | FitDiagnosticWithheld.Whole _ -> []

                let notes =
                    withheldNotes
                    @ left
                    @ (if List.isEmpty rows then
                           [ "Nothing is drawn: the answer releases no cell that can be drawn." ]
                       else
                           [])

                if List.isEmpty rows then
                    Ok {
                        Chart = None
                        Withheld = withheld
                        Notes = notes
                    }
                else
                    let column (name: string) (ty: ColumnType) (cells: Cell list) =
                        Column.ofCells name ty cells
                        |> Result.map (fun c -> Field.create name ty, c)
                        |> Result.mapError (fun e -> $"column '{name}': %A{e}")

                    let xOf (x: string) =
                        if arm.NumericX then
                            Cell.Float(Double.Parse(x, Globalization.CultureInfo.InvariantCulture))
                        else
                            Cell.Str x

                    let columns =
                        column
                            arm.XField
                            (if arm.NumericX then FloatType else StringType)
                            (rows |> List.map (fst >> xOf))
                        :: (drawnSeries
                            |> List.map (fun s ->
                                column
                                    s.Field
                                    FloatType
                                    (rows
                                     |> List.map (fun (_, ys) ->
                                         Cell.Float(ys |> List.find (fst >> (=) s.Field) |> snd)))))

                    let rowMaps: Row list =
                        rows
                        |> List.map (fun (x, ys) ->
                            Map.ofList [
                                arm.XField,
                                (if arm.NumericX then
                                     box (Double.Parse(x, Globalization.CultureInfo.InvariantCulture))
                                 else
                                     box x)
                                yield! ys |> List.map (fun (field, v) -> field, box v)
                            ])

                    match
                        columns
                        |> List.tryPick (function
                            | Error e -> Some e
                            | Ok _ -> None)
                    with
                    | Some e ->
                        Error(FitDiagnosticChartRefusal.ChartRefused $"the chart's table could not be built: {e}")
                    | None ->
                        let pairs = columns |> List.choose Result.toOption

                        let table: Table = {
                            Schema = pairs |> List.map fst
                            Columns = pairs |> List.map snd
                        }

                        let decimals =
                            if arm.Shape = Histogram then
                                0
                            else
                                decimalsOf declaration.Precision

                        let spec = {
                            Defaults.chart with
                                Kind = arm.Kind
                                Source = Binding.Transform(TransformSource.Data(Embedded table), [], None)
                                XField = arm.XField
                                YFields = drawnSeries |> List.map _.Field
                                Title = Some(TextSource.Literal arm.Title)
                                XTitle = Some(TextSource.Literal arm.XTitle)
                                YTitle = Some(TextSource.Literal arm.YTitle)
                                ValueFormat = Some(Format.Number(Some decimals))
                        }

                        match Fuaran.UI.Charts.tryLower spec rowMaps with
                        | Error refusal ->
                            Error(FitDiagnosticChartRefusal.ChartRefused(Fuaran.UI.Charts.describeRefusal refusal))
                        | Ok _ ->
                            let node = Fuaran.chart id spec

                            match PreEmitValidate.validateForTransport node with
                            | Error defects ->
                                defects
                                |> List.map (fun d ->
                                    let code, _, message = PreEmitValidate.describe d
                                    $"{code}: {message}")
                                |> String.concat "; "
                                |> FitDiagnosticChartRefusal.ChartRefused
                                |> Error
                            | Ok() ->
                                Ok {
                                    Chart = Some node
                                    Withheld = withheld
                                    Notes = notes
                                }