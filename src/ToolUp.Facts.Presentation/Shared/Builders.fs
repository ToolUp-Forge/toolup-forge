// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.Platform.AI
open ToolUp.Facts

// ─── The builders (Phase 1129) ──────────────────────────────────────
//
// From a presentation request and the gated series that answered it to a
// Fuaran-UI node and the provenance that produced it. Pure: a builder never
// reads a store, so the same request and series build the same node on a
// server and in a browser.
//
// **A gap is never drawn as a number.** Fuaran-UI's chart lowering reads a
// missing cell as 0, so a withheld point left in a chart's rows would draw as
// a zero. Each builder therefore draws only points that carry a published
// number: a trend drops a day on which any subject has no such point and marks
// the day on the axis, a breakdown and a comparison leave the subject out, and
// a table leaves the cell out. Each one is named, with its reason, beside the
// block. A series the read refused is a callout naming the refusal.
//
// **Every built node is validated before it leaves the builder.** A node the
// Fuaran-UI pre-emit validator refuses — a chart field its rows do not ground,
// a duplicate id — is a defect of the builder, never of the caller, and is
// returned as one rather than emitted.

/// A built block: the node, the provenance that produced it, and each point
/// it leaves out with its reason (the same sentences the node shows).
type PresentationBlock<'Msg> = {
    /// The Fuaran-UI node, validated.
    Node: Node<'Msg>
    /// The request and the read that produced the node.
    Provenance: BlockProvenance
    /// Each point not drawn, each refusal, and each author annotation that
    /// is not a mark on an axis, as the node's notes show them.
    Notes: string list
}

/// What a block is drawn from, before it is drawn: the columnar
/// table and each point it leaves out. A stored page binds this table to a
/// node it already holds, so the page and a built block draw the same rows.
type PresentationData = {
    /// The table the block is drawn from. For a chart, the chart's own
    /// source: the x column, then one number column per series. For a table,
    /// a `subject` column, then one number column per metric, in the order
    /// the request names them; a cell that is not shown is empty (null),
    /// never zero. `None` when nothing is drawn.
    Table: Table option
    /// Each point not drawn, each refusal, and each author annotation that
    /// is not a mark on an axis, as a built block's notes show them.
    Notes: string list
    /// The request and the read the table was drawn from.
    Provenance: BlockProvenance
}

/// Why a builder produced no block.
[<RequireQualifiedAccess>]
type PresentationDefect =
    /// The request is malformed.
    | Request of PresentationRequestError
    /// The answers are not the reads the request asks for (a missing,
    /// repeated or foreign metric, or another table's series).
    | AnswersMismatch of reason: string
    /// The Fuaran-UI pre-emit validator refused the built node — a defect of
    /// the builder. Each finding is `code: message`.
    | NodeRefused of findings: string list

/// Reading a builder defect.
[<RequireQualifiedAccess>]
module PresentationDefect =
    /// The defect as one sentence.
    let describe (defect: PresentationDefect) : string =
        match defect with
        | PresentationDefect.Request e -> PresentationRequestError.describe e
        | PresentationDefect.AnswersMismatch reason ->
            sprintf "the series answers do not match the presentation request: %s" reason
        | PresentationDefect.NodeRefused findings ->
            sprintf "the built node failed the pre-emit validator: %s" (String.concat "; " findings)

/// What a chart draws.
type private Plot = {
    Kind: ChartKind
    XField: string
    YFields: string list
    Rows: Row list
    Temporal: bool
    Annotations: ChartAnnotation list
}

/// One published table cell: a Fact node carrying its fact id.
type private FactCell = {
    Id: string
    Label: string
    Value: string
    Number: float
    FactId: string
}

/// One subject's row of a table.
type private Group = {
    Id: string
    Heading: string
    Cells: FactCell list
}

/// One block before it becomes nodes.
type private Panel = {
    Id: string
    Title: string
    YTitle: string
    Plot: Plot option
    Table: Group list option
    Notes: string list
    Refusals: (string * string) list
}

/// Builds a block from a presentation request and the series that answered it.
[<RequireQualifiedAccess>]
module Presentation =

    let private iso = PresentationDay.iso
    let private dayOf = PresentationDay.ofDateTime

    /// The number a point carries, when it carries a published one.
    let private published (p: FactSeriesPoint) : decimal option = if p.Withheld then None else p.Value

    /// Why a point carries no number.
    let private reason (p: FactSeriesPoint) : string =
        if p.Withheld then
            "withheld from you by the disclosure gate"
        else
            "a fact with no single number (an absence or an interval)"

    /// A node-id segment: letters, digits and `-` only.
    let private idSegment (s: string) : string =
        s
        |> String.map (fun c ->
            if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') then
                c
            else
                '-')

    /// A display label per subject, unique across the subjects and distinct
    /// from every reserved column name: the subject's own member where that is
    /// unambiguous, its whole readable path where two subjects share a member.
    let private labels (reserved: string list) (points: FactSeriesPoint list) : Map<string, string> =
        let subjects = points |> List.distinctBy _.Subject

        let short (p: FactSeriesPoint) =
            p.SubjectPath |> List.tryLast |> Option.defaultValue p.Subject

        let shortCounts = subjects |> List.countBy short |> Map.ofList

        subjects
        |> List.map (fun p ->
            let preferred =
                if Map.find (short p) shortCounts = 1 then
                    short p
                else
                    p.Subject

            let label =
                if List.contains preferred reserved then
                    preferred + " (subject)"
                else
                    preferred

            p.Subject, label)
        |> Map.ofList

    /// The series's withheld count by policy, as the read reports it. Counts
    /// only: a policy is never a value.
    let private withheldSummary (series: FactSeries) : string list =
        if series.WithheldCount = 0 then
            []
        else
            let byPolicy =
                series.Withheld
                |> List.map (fun w -> sprintf "%s: %d" w.PolicyRef w.Count)
                |> String.concat ", "

            [
                sprintf "%d point(s) of %s are withheld from you (%s)." series.WithheldCount series.Metric byPolicy
            ]

    /// The author's annotations as notes (a kind with no date axis).
    let private annotationNotes (request: PresentationRequest) : string list =
        request.Annotations |> List.map (fun a -> sprintf "%s: %s" (iso a.On) a.Label)

    let private panelId (request: PresentationRequest) : string =
        sprintf
            "%s-%s-%s"
            (PresentationRequest.kindName request.Kind)
            (idSegment request.TableId)
            (idSegment (String.concat "-" request.Metrics))

    let private emptyPanel (request: PresentationRequest) : Panel = {
        Id = panelId request
        Title = PresentationRequest.title request
        YTitle = String.concat ", " request.Metrics
        Plot = None
        Table = None
        Notes = []
        Refusals = []
    }

    /// The points of `day`.
    let private pointsOn (day: DateTime) (series: FactSeries) : FactSeriesPoint list =
        series.Points |> List.filter (fun p -> dayOf p.PeriodFrom = day)

    /// One line per subject over the days of the series. A day is drawn only
    /// when every subject has a published number on it; any other day is
    /// marked on the axis and named.
    let private trend (request: PresentationRequest) (series: FactSeries) : Panel =
        let xField = "day"
        let names = labels [ xField ] series.Points
        let subjects = series.Points |> List.map _.Subject |> List.distinct

        let days =
            series.Points
            |> List.map (fun p -> dayOf p.PeriodFrom)
            |> List.distinct
            |> List.sort

        let at =
            series.Points
            |> List.map (fun p -> (p.Subject, dayOf p.PeriodFrom), p)
            |> Map.ofList

        let missing (day: DateTime) : string list =
            subjects
            |> List.choose (fun s ->
                match Map.tryFind (s, day) at with
                | Some p when (published p).IsSome -> None
                | Some p -> Some(sprintf "%s on %s: %s" names.[s] (iso day) (reason p))
                | None -> Some(sprintf "%s on %s: no fact for the day" names.[s] (iso day)))

        let drawn, gaps = days |> List.partition (fun d -> List.isEmpty (missing d))

        let row (day: DateTime) : Row =
            Map.ofList [
                xField, box (iso day)
                for s in subjects do
                    let value = Map.find (s, day) at |> published |> Option.get
                    names.[s], box (float value)
            ]

        let from, until = PresentationRequest.range request
        let onAxis (d: DateTime) = d >= from && d < until

        let marked, unmarked =
            request.Annotations |> List.partition (fun a -> onAxis (dayOf a.On))

        {
            emptyPanel request with
                Plot =
                    if List.isEmpty drawn then
                        None
                    else
                        Some {
                            Kind = ChartKind.Line
                            XField = xField
                            YFields = subjects |> List.map (fun s -> names.[s])
                            Rows = drawn |> List.map row
                            Temporal = true
                            Annotations =
                                (gaps
                                 |> List.map (fun d ->
                                     ChartAnnotation.EventMarker(
                                         ChartAnnotationX.Date(iso d),
                                         Some(TextSource.Literal "not drawn")
                                     )))
                                @ (marked
                                   |> List.map (fun a ->
                                       ChartAnnotation.EventMarker(
                                           ChartAnnotationX.Date(iso (dayOf a.On)),
                                           Some(TextSource.Literal a.Label)
                                       )))
                        }
                Notes = [
                    if List.isEmpty days then
                        "No facts in this range."
                    yield! withheldSummary series
                    for d in gaps do
                        sprintf "%s is not drawn: %s." (iso d) (String.concat "; " (missing d))
                    for a in unmarked do
                        sprintf "%s: %s" (iso a.On) a.Label
                ]
        }

    /// The y column for a one-metric bar chart, distinct from its x column.
    let private valueField (xField: string) (metric: string) =
        if metric = xField then metric + " (value)" else metric

    /// One bar per subject for the delivery, largest first. A subject without
    /// a published number is left out and named.
    let private breakdown (request: PresentationRequest) (series: FactSeries) : Panel =
        let day = PresentationRequest.delivery request
        let xField = "subject"
        let yField = valueField xField series.Metric
        let points = pointsOn day series
        let names = labels [] points

        let bars =
            points
            |> List.choose (fun p -> published p |> Option.map (fun v -> names.[p.Subject], v))
            |> List.sortByDescending snd

        {
            emptyPanel request with
                Title = sprintf "%s, %s" (PresentationRequest.title request) (iso day)
                Plot =
                    if List.isEmpty bars then
                        None
                    else
                        Some {
                            Kind = ChartKind.Bar
                            XField = xField
                            YFields = [ yField ]
                            Rows =
                                bars
                                |> List.map (fun (s, v) -> Map.ofList [ xField, box s; yField, box (float v) ])
                            Temporal = false
                            Annotations = []
                        }
                Notes = [
                    if List.isEmpty points then
                        sprintf "No facts for %s." (iso day)
                    yield! withheldSummary series
                    for p in points do
                        if (published p).IsNone then
                            sprintf "%s is not drawn: %s." names.[p.Subject] (reason p)
                    yield! annotationNotes request
                ]
        }

    /// The latest day before `day` that the series holds any fact for: the
    /// delivery before the drawn one.
    let private previousDelivery (day: DateTime) (series: FactSeries) : DateTime option =
        series.Points
        |> List.map (fun p -> dayOf p.PeriodFrom)
        |> List.filter (fun d -> d < day)
        |> List.sortDescending
        |> List.tryHead

    /// The delivery against the one before it, per subject. A subject is drawn
    /// only when both deliveries carry a published number for it; any other
    /// is named with what is missing.
    let private comparison (request: PresentationRequest) (series: FactSeries) : Panel =
        let day = PresentationRequest.delivery request
        let thisLabel = iso day

        let shell = {
            emptyPanel request with
                Title = sprintf "%s, %s against the delivery before" (PresentationRequest.title request) thisLabel
        }

        match previousDelivery day series with
        | None -> {
            shell with
                Notes = [
                    sprintf "No delivery before %s in this range." thisLabel
                    yield! annotationNotes request
                ]
          }
        | Some previous ->
            let prevLabel = iso previous
            let both = pointsOn day series @ pointsOn previous series
            let names = labels [] both

            let before =
                pointsOn previous series |> List.map (fun p -> p.Subject, p) |> Map.ofList

            let current = pointsOn day series |> List.map (fun p -> p.Subject, p) |> Map.ofList
            let subjects = both |> List.map _.Subject |> List.distinct

            let pair (s: string) =
                match Map.tryFind s before |> Option.bind published, Map.tryFind s current |> Option.bind published with
                | Some b, Some c -> Ok(names.[s], b, c)
                | _ ->
                    let side label (m: Map<string, FactSeriesPoint>) =
                        match Map.tryFind s m with
                        | Some p when (published p).IsSome -> None
                        | Some p -> Some(sprintf "%s %s" label (reason p))
                        | None -> Some(sprintf "%s has no fact for it" label)

                    [ side prevLabel before; side thisLabel current ]
                    |> List.choose id
                    |> String.concat "; "
                    |> fun why -> Error(sprintf "%s is not drawn: %s." names.[s] why)

            let pairs = subjects |> List.map pair

            let drawn =
                pairs
                |> List.choose (function
                    | Ok p -> Some p
                    | Error _ -> None)
                |> List.sortByDescending (fun (_, _, c) -> c)

            {
                shell with
                    Plot =
                        if List.isEmpty drawn then
                            None
                        else
                            Some {
                                Kind = ChartKind.Bar
                                XField = "subject"
                                YFields = [ prevLabel; thisLabel ]
                                Rows =
                                    drawn
                                    |> List.map (fun (s, b, c) ->
                                        Map.ofList [
                                            "subject", box s
                                            prevLabel, box (float b)
                                            thisLabel, box (float c)
                                        ])
                                Temporal = false
                                Annotations = []
                            }
                    Notes = [
                        yield! withheldSummary series
                        yield!
                            pairs
                            |> List.choose (function
                                | Error why -> Some why
                                | Ok _ -> None)
                        yield! annotationNotes request
                    ]
            }

    /// Subjects by metrics for the delivery: one group per subject, one Fact
    /// node per published cell carrying its fact id as `data-fact`. A cell
    /// without a published number is left out and named.
    let private table (request: PresentationRequest) (series: FactSeries list) : Panel =
        let day = PresentationRequest.delivery request
        let id = panelId request
        let onDay = series |> List.map (fun s -> s, pointsOn day s)
        let points = onDay |> List.collect snd
        let names = labels [] points
        let subjects = points |> List.map _.Subject |> List.distinct

        let cell (s: string) ((series: FactSeries), (dayPoints: FactSeriesPoint list)) =
            match dayPoints |> List.tryFind (fun p -> p.Subject = s) with
            | Some p ->
                match published p with
                | Some v ->
                    Ok {
                        Id = sprintf "%s-%s-%s" id (idSegment s) (idSegment series.Metric)
                        Label = series.Metric
                        Value = p.Rendering |> Option.defaultValue (string v)
                        Number = float v
                        FactId = p.FactId
                    }
                | None -> Error(sprintf "%s, %s is not shown: %s." names.[s] series.Metric (reason p))
            | None -> Error(sprintf "%s, %s is not shown: no fact for %s." names.[s] series.Metric (iso day))

        let rows =
            subjects
            |> List.map (fun s ->
                let cells = onDay |> List.map (cell s)

                let facts =
                    cells
                    |> List.choose (function
                        | Ok n -> Some n
                        | Error _ -> None)

                let gaps =
                    cells
                    |> List.choose (function
                        | Error why -> Some why
                        | Ok _ -> None)

                let group =
                    if List.isEmpty facts then
                        None
                    else
                        Some {
                            Id = sprintf "%s-%s" id (idSegment s)
                            Heading = names.[s]
                            Cells = facts
                        }

                group, gaps)

        {
            emptyPanel request with
                Title = sprintf "%s, %s" (PresentationRequest.title request) (iso day)
                Table =
                    match rows |> List.choose fst with
                    | [] -> None
                    | groups -> Some groups
                Notes = [
                    if List.isEmpty points then
                        sprintf "No facts for %s." (iso day)
                    yield! series |> List.collect withheldSummary
                    yield! rows |> List.collect snd
                    yield! annotationNotes request
                ]
        }

    /// A plot's rows as the canonical columnar table its chart is drawn from:
    /// the x column (a date column on a temporal axis, else text), then one
    /// number column per series. A chart drawn from a table is one the
    /// pre-emit validator grounds — every field it names must be a column of
    /// the right type — where a row-literal source is not checked.
    let private tableOf (plot: Plot) : Result<Table, string> =
        let column (name: string) (ty: ColumnType) (cells: Cell list) =
            Column.ofCells name ty cells
            |> Result.map (fun c -> Field.create name ty, c)
            |> Result.mapError (fun e -> sprintf "column '%s': %A" name e)

        let x =
            plot.Rows
            |> List.map (fun r ->
                let v = unbox<string> r.[plot.XField]
                if plot.Temporal then Cell.Date v else Cell.Str v)

        let columns =
            column plot.XField (if plot.Temporal then DateType else StringType) x
            :: (plot.YFields
                |> List.map (fun y ->
                    column y FloatType (plot.Rows |> List.map (fun r -> Cell.Float(unbox<float> r.[y])))))

        match
            columns
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            let pairs =
                columns
                |> List.choose (function
                    | Ok p -> Some p
                    | Error _ -> None)

            Ok {
                Schema = pairs |> List.map fst
                Columns = pairs |> List.map snd
            }

    /// The panel as Fuaran-UI nodes: the chart or table, every refusal as a
    /// callout, and the notes. A chart the lowering would refuse (more series
    /// or points than it draws) is a callout naming the refusal, never a
    /// silently truncated picture. `Error` is a builder defect.
    let private render<'Msg> (panel: Panel) : Result<Node<'Msg> * string list, string list> =
        let source = panel.Plot |> Option.map tableOf

        let lowered =
            match panel.Plot, source with
            | Some plot, Some(Ok table) ->
                let spec = {
                    Defaults.chart with
                        Kind = plot.Kind
                        Source = Binding.Transform(TransformSource.Data(Embedded table), [], None)
                        XField = plot.XField
                        YFields = plot.YFields
                        Title = Some(TextSource.Literal panel.Title)
                        YTitle = Some(TextSource.Literal panel.YTitle)
                        XScale = if plot.Temporal then Some ChartXScale.Temporal else None
                        Annotations =
                            if List.isEmpty plot.Annotations then
                                None
                            else
                                Some plot.Annotations
                }

                match Fuaran.UI.Charts.tryLower spec plot.Rows with
                | Ok _ -> Some(Ok spec)
                | Error refusal -> Some(Error(Fuaran.UI.Charts.describeRefusal refusal))
            | _ -> None

        let refusals =
            panel.Refusals
            @ (match lowered with
               | Some(Error why) -> [ "The chart lowering refused this chart", why ]
               | _ -> [])

        let notes =
            (refusals |> List.map (fun (heading, why) -> sprintf "%s: %s" heading why))
            @ panel.Notes

        let title () =
            Fuaran.heading (panel.Id + "-title") {
                Defaults.heading with
                    Level = 3
                    Text = TextSource.Literal panel.Title
            }

        let node =
            Fuaran.box panel.Id {
                Defaults.box with
                    Children = [
                        match lowered, panel.Table with
                        | Some(Ok spec), _ -> Fuaran.chart (panel.Id + "-chart") spec
                        | _, Some groups ->
                            title ()

                            for g in groups do
                                Fuaran.box g.Id {
                                    Defaults.box with
                                        Layout = BoxLayout.Grid(List.length g.Cells, None, None)
                                        Heading = Some(TextSource.Literal g.Heading)
                                        Children =
                                            g.Cells
                                            |> List.map (fun c ->
                                                Fuaran.factSpec c.Id {
                                                    Defaults.fact with
                                                        Label = TextSource.Literal c.Label
                                                        Value = TextSource.Literal c.Value
                                                }
                                                |> Node.withExtraAttribute "data-fact" c.FactId)
                                }
                        | _ -> title ()
                        yield!
                            refusals
                            |> List.mapi (fun i (heading, why) ->
                                Fuaran.callout (sprintf "%s-refused-%d" panel.Id i) {
                                    Defaults.callout with
                                        Tone = ToneVariant.Warning
                                        Heading = Some(TextSource.Literal heading)
                                        Body = TextSource.Literal why
                                })
                        if not (List.isEmpty panel.Notes) then
                            Fuaran.list (panel.Id + "-notes") panel.Notes
                    ]
            }

        match source with
        | Some(Error e) -> Error [ sprintf "the chart's table could not be built: %s" e ]
        | _ -> Ok(node, notes)

    /// The pre-emit validator's findings on `node`, as `code: message`; empty
    /// when the node is clean. Validated for transport, since a block travels
    /// as wire JSON.
    let findings (node: Node<'Msg>) : string list =
        match PreEmitValidate.validateForTransport node with
        | Ok() -> []
        | Error defects ->
            defects
            |> List.map (fun d ->
                let code, _, message = PreEmitValidate.describe d
                sprintf "%s: %s" code message)

    /// Checks the answers are exactly the reads the request asks for: one per
    /// metric, each of the request's table.
    let private checkAnswers (request: PresentationRequest) (answers: SeriesAnswer list) : Result<unit, string> =
        let metricOf =
            function
            | SeriesAnswer.Answered s -> s.Metric
            | SeriesAnswer.Refused(metric, _) -> metric

        let answered = answers |> List.map metricOf

        let foreignTable =
            answers
            |> List.tryPick (function
                | SeriesAnswer.Answered s when s.TableId <> request.TableId -> Some s.TableId
                | _ -> None)

        match foreignTable with
        | Some t -> Error(sprintf "a series is of table '%s', and the request names '%s'" t request.TableId)
        | None ->
            match answered |> List.tryFind (fun m -> not (List.contains m request.Metrics)) with
            | Some m -> Error(sprintf "metric '%s' was answered and the request does not name it" m)
            | None ->
                match request.Metrics |> List.tryFind (fun m -> not (List.contains m answered)) with
                | Some m -> Error(sprintf "metric '%s' was not answered" m)
                | None ->
                    if List.length answered <> List.length request.Metrics then
                        Error "a metric was answered twice"
                    else
                        Ok()

    /// The panel `request` draws from `answers`, with each refused read as a
    /// refusal: the one step `data` and `build` share, so a stored page and a
    /// built block are drawn from the same rows.
    let private panelOf
        (request: PresentationRequest)
        (answers: SeriesAnswer list)
        : Result<Panel, PresentationDefect> =
        match PresentationRequest.validate request with
        | Error e -> Error(PresentationDefect.Request e)
        | Ok request ->
            match checkAnswers request answers with
            | Error reason -> Error(PresentationDefect.AnswersMismatch reason)
            | Ok() ->
                // Answers in the order the request names its metrics.
                let ordered =
                    request.Metrics
                    |> List.map (fun m ->
                        answers
                        |> List.find (function
                            | SeriesAnswer.Answered s -> s.Metric = m
                            | SeriesAnswer.Refused(metric, _) -> metric = m))

                let served =
                    ordered
                    |> List.choose (function
                        | SeriesAnswer.Answered s -> Some s
                        | SeriesAnswer.Refused _ -> None)

                let refusals =
                    ordered
                    |> List.choose (function
                        | SeriesAnswer.Refused(metric, why) ->
                            Some(sprintf "The fact series read refused %s" metric, why)
                        | SeriesAnswer.Answered _ -> None)

                let panel =
                    match request.Kind, served with
                    | PresentationKind.Table, _ :: _ -> table request served
                    | PresentationKind.Trend, [ s ] -> trend request s
                    | PresentationKind.Breakdown, [ s ] -> breakdown request s
                    | PresentationKind.Comparison, [ s ] -> comparison request s
                    | _ -> emptyPanel request

                Ok { panel with Refusals = refusals }

    /// A table panel's groups as a columnar table: a `subject` column, then
    /// one number column per metric in the request's order. A cell the panel
    /// does not show is null, never zero.
    let private groupsTable (request: PresentationRequest) (groups: Group list) : Result<Table, string> =
        let xField = "subject"

        let subjects =
            Column.ofCells xField StringType (groups |> List.map (fun g -> Cell.Str g.Heading))
            |> Result.map (fun c -> Field.create xField StringType, c)
            |> Result.mapError (fun e -> sprintf "column '%s': %A" xField e)

        let metric (m: string) =
            let name = valueField xField m

            groups
            |> List.map (fun g ->
                match g.Cells |> List.tryFind (fun c -> c.Label = m) with
                | Some c -> Cell.Float c.Number
                | None -> Cell.Null)
            |> Column.ofCells name FloatType
            |> Result.map (fun c -> Field.create name FloatType, c)
            |> Result.mapError (fun e -> sprintf "column '%s': %A" name e)

        let columns = subjects :: (request.Metrics |> List.map metric)

        match
            columns
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some e -> Error e
        | None ->
            let pairs =
                columns
                |> List.choose (function
                    | Ok p -> Some p
                    | Error _ -> None)

            Ok {
                Schema = pairs |> List.map fst
                Columns = pairs |> List.map snd
            }

    /// The data `request` draws from `answers`, before any node is built: the
    /// table and every point left out, named. Pure, and the same rows `build`
    /// draws. A withheld or absent point is never a number in the table: a
    /// chart's row leaves it out, and a table's cell is null.
    let data
        (request: PresentationRequest)
        (answers: SeriesAnswer list)
        : Result<PresentationData, PresentationDefect> =
        match panelOf request answers with
        | Error defect -> Error defect
        | Ok panel ->
            let table =
                match panel.Plot, panel.Table with
                | Some plot, _ -> tableOf plot |> Result.map Some
                | None, Some groups -> groupsTable request groups |> Result.map Some
                | None, None -> Ok None

            match table with
            | Error e -> Error(PresentationDefect.NodeRefused [ sprintf "the block's table could not be built: %s" e ])
            | Ok table ->
                Ok {
                    Table = table
                    Notes =
                        (panel.Refusals |> List.map (fun (heading, why) -> sprintf "%s: %s" heading why))
                        @ panel.Notes
                    Provenance = Provenance.ofAnswers request answers
                }

    /// The block `request` draws from `answers` — one answer per metric the
    /// request names: the gated series, or the read's refusal. Pure. The
    /// node is validated before it is returned; a refusal is a builder defect.
    ///
    /// The node is `Node<'Msg>` for any message type: a block carries no
    /// action, so a caller composes it into its own tree.
    let build<'Msg>
        (request: PresentationRequest)
        (answers: SeriesAnswer list)
        : Result<PresentationBlock<'Msg>, PresentationDefect> =
        match panelOf request answers with
        | Error defect -> Error defect
        | Ok panel ->
            match render<'Msg> panel with
            | Error defects -> Error(PresentationDefect.NodeRefused defects)
            | Ok(node, notes) ->
                match findings node with
                | [] ->
                    Ok {
                        Node = node
                        Provenance = Provenance.ofAnswers request answers
                        Notes = notes
                    }
                | found -> Error(PresentationDefect.NodeRefused found)