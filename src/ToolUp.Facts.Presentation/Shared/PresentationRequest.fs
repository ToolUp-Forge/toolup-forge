// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open ToolUp.AI.Wire
open ToolUp.Platform.AI
open ToolUp.Facts

// ─── The presentation request (Phase 1129) ──────────────────────────
//
// A block a reader sees — a chart or a table of facts — is built from a
// REQUEST and a gated read, never from rows anyone wrote. The request names
// what to draw: the kind, the fact table, its metric(s), the subjects, the
// period and the labels. It carries no number, so a model that authors one
// cannot put a figure on the page: every number comes from the fact series
// read, under the disclosure gate the facts are published under.
//
// The JSON codec is the request's one wire shape — a tool argument, a stored
// block's provenance, a report's projection request. It refuses a malformed
// request BY NAME (the field it is missing, the kind it does not know), so the
// author repairs from the message alone.
//
// Compiled by both compilers: no reflection, no System.Text.Json, and dates
// are parsed and rendered by hand in their one canonical form, `yyyy-MM-dd`.

/// What a presentation request draws.
[<RequireQualifiedAccess>]
type PresentationKind =
    /// One line per subject over the days of the period.
    | Trend
    /// One bar per subject for one delivery, largest first.
    | Breakdown
    /// One delivery against the delivery before it, per subject.
    | Comparison
    /// Subjects by metrics for one delivery, one Fact node per cell.
    | Table

/// The period a request covers. Every period is whole UTC days.
[<RequireQualifiedAccess>]
type PresentationPeriod =
    /// The `rangeDays` days ending on `day` (inclusive). A breakdown, a
    /// comparison and a table draw `day`; a trend draws the whole range.
    | Ending of day: DateTime * rangeDays: int
    /// The days from `from` (inclusive) to `until` (exclusive). A breakdown,
    /// a comparison and a table draw the last day of the range.
    | Between of from: DateTime * until: DateTime

/// What a comparison draws its delivery against.
[<RequireQualifiedAccess>]
type PresentationCompareTo =
    /// The latest earlier day the series holds any fact for.
    | PreviousDelivery

/// A labelled day the author marks: an event marker on a trend's axis, and a
/// note beside any other kind. A label is text, never a number to plot.
type PresentationAnnotation = {
    /// The day marked.
    On: DateTime
    /// What the mark says.
    Label: string
}

/// What a block draws — the whole of what a model may author for it.
type PresentationRequest = {
    /// What to draw.
    Kind: PresentationKind
    /// The fact table to read.
    TableId: string
    /// The metric columns to read. A trend, a breakdown and a comparison draw
    /// exactly one; a table draws one column per metric.
    Metrics: string list
    /// Subject path prefix (member ids from the hierarchy root). Empty admits
    /// every subject of the table.
    SubjectPrefix: string list
    /// The hierarchy level the subjects sit at. `None` is the table's
    /// declared level.
    SubjectLevel: string option
    /// The period covered.
    Period: PresentationPeriod
    /// What a comparison draws against. `None` on any other kind; a
    /// comparison with `None` compares to the previous delivery.
    CompareTo: PresentationCompareTo option
    /// The block's title. `None` derives one from the table and metric.
    Title: string option
    /// Days the author marks.
    Annotations: PresentationAnnotation list
}

/// Why a presentation request was refused. Each case names what to repair.
[<RequireQualifiedAccess>]
type PresentationRequestError =
    /// The text is not JSON, or not a JSON object.
    | NotAnObject
    /// A required field is absent (or null).
    | MissingField of field: string
    /// The `kind` names no presentation kind.
    | UnknownKind of kind: string
    /// A field is present and malformed.
    | InvalidField of field: string * reason: string

/// Reading a refusal.
[<RequireQualifiedAccess>]
module PresentationRequestError =
    /// The refusal as one sentence naming what to repair.
    let describe (error: PresentationRequestError) : string =
        match error with
        | PresentationRequestError.NotAnObject -> "the presentation request is not a JSON object"
        | PresentationRequestError.MissingField field -> sprintf "the presentation request has no '%s'" field
        | PresentationRequestError.UnknownKind kind ->
            sprintf
                "the presentation request's kind '%s' is not one of 'trend', 'breakdown', 'comparison', 'table'"
                kind
        | PresentationRequestError.InvalidField(field, reason) ->
            sprintf "the presentation request's '%s' is invalid: %s" field reason

/// Days in the one canonical form, `yyyy-MM-dd`, parsed and rendered by hand
/// so both compilers agree byte for byte.
[<RequireQualifiedAccess>]
module PresentationDay =
    /// The UTC midnight starting the day `d` falls on.
    let ofDateTime (d: DateTime) : DateTime =
        DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc)

    /// `yyyy-MM-dd`.
    let iso (d: DateTime) : string =
        sprintf "%04d-%02d-%02d" d.Year d.Month d.Day

    /// The UTC day `yyyy-MM-dd` names, or `None` when the text is not a
    /// calendar day in that form.
    let tryParse (s: string) : DateTime option =
        let digitsAt (start: int) (count: int) : int option =
            let mutable value = 0
            let mutable ok = true

            for i in start .. start + count - 1 do
                let c = s.[i]

                if c >= '0' && c <= '9' then
                    value <- value * 10 + int c - int '0'
                else
                    ok <- false

            if ok then Some value else None

        if isNull s || s.Length <> 10 || s.[4] <> '-' || s.[7] <> '-' then
            None
        else
            match digitsAt 0 4, digitsAt 5 2, digitsAt 8 2 with
            | Some y, Some m, Some d when y >= 1 && m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ->
                Some(DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc))
            | _ -> None

/// The presentation request: its vocabulary, its checks, its read and its
/// JSON codec.
[<RequireQualifiedAccess>]
module PresentationRequest =

    /// A kind's name in the request vocabulary — the `kind` the JSON carries
    /// and the `Kind` a block's provenance records. The table kind is
    /// `BlockProvenance.TableKind`, so a table block exports as a table.
    let kindName (kind: PresentationKind) : string =
        match kind with
        | PresentationKind.Trend -> "trend"
        | PresentationKind.Breakdown -> "breakdown"
        | PresentationKind.Comparison -> "comparison"
        | PresentationKind.Table -> BlockProvenance.TableKind

    /// The kind a name denotes (compared without case).
    let tryKind (name: string) : PresentationKind option =
        match (if isNull name then "" else name.Trim().ToLowerInvariant()) with
        | "trend" -> Some PresentationKind.Trend
        | "breakdown" -> Some PresentationKind.Breakdown
        | "comparison" -> Some PresentationKind.Comparison
        | "table" -> Some PresentationKind.Table
        | _ -> None

    /// The half-open day range `[from, until)` the request reads.
    let range (request: PresentationRequest) : DateTime * DateTime =
        match request.Period with
        | PresentationPeriod.Ending(day, rangeDays) ->
            let last = PresentationDay.ofDateTime day
            last.AddDays(float (1 - max 1 rangeDays)), last.AddDays 1.0
        | PresentationPeriod.Between(from, until) -> PresentationDay.ofDateTime from, PresentationDay.ofDateTime until

    /// The one delivery a breakdown, a comparison or a table draws: the last
    /// day of the range.
    let delivery (request: PresentationRequest) : DateTime = (snd (range request)).AddDays -1.0

    /// The period in its canonical rendering: the half-open range as an
    /// ISO 8601 interval, `yyyy-MM-dd/yyyy-MM-dd` (the end exclusive).
    let periodText (request: PresentationRequest) : string =
        let from, until = range request
        PresentationDay.iso from + "/" + PresentationDay.iso until

    /// The block's title: the request's own, or one derived from the table and
    /// the metrics.
    let title (request: PresentationRequest) : string =
        match request.Title with
        | Some t when not (String.IsNullOrWhiteSpace t) -> t
        | _ -> sprintf "%s: %s" request.TableId (String.concat ", " request.Metrics)

    let private blank (s: string) = String.IsNullOrWhiteSpace s

    /// The request when it is well formed, or the first refusal naming what
    /// to repair. The codec runs this on every decode, and every builder runs
    /// it before drawing, so a request built in code is held to the same
    /// rules as one read from JSON.
    let validate (request: PresentationRequest) : Result<PresentationRequest, PresentationRequestError> =
        let invalid field reason =
            Error(PresentationRequestError.InvalidField(field, reason))

        if blank request.TableId then
            Error(PresentationRequestError.MissingField "tableId")
        elif List.isEmpty request.Metrics then
            Error(PresentationRequestError.MissingField "metrics")
        elif request.Metrics |> List.exists blank then
            invalid "metrics" "a metric name is blank"
        elif List.length (List.distinct request.Metrics) <> List.length request.Metrics then
            invalid "metrics" "a metric is named twice"
        elif request.Kind <> PresentationKind.Table && List.length request.Metrics <> 1 then
            invalid
                "metrics"
                (sprintf
                    "a %s draws exactly one metric, and %d were named"
                    (kindName request.Kind)
                    (List.length request.Metrics))
        elif request.SubjectPrefix |> List.exists blank then
            invalid "subjectPrefix" "a member id is blank"
        elif request.SubjectLevel |> Option.exists blank then
            invalid "subjectLevel" "the level is blank"
        elif request.CompareTo.IsSome && request.Kind <> PresentationKind.Comparison then
            invalid "compareTo" (sprintf "a %s compares to nothing" (kindName request.Kind))
        elif request.Annotations |> List.exists (fun a -> blank a.Label) then
            invalid "annotations" "an annotation's label is blank"
        else
            match request.Period with
            | PresentationPeriod.Ending(_, rangeDays) when rangeDays < 1 ->
                invalid "period" (sprintf "rangeDays is %d; it must be at least 1" rangeDays)
            | PresentationPeriod.Between(from, until) when
                PresentationDay.ofDateTime until <= PresentationDay.ofDateTime from
                ->
                invalid
                    "period"
                    (sprintf "'to' (%s) is not after 'from' (%s)" (PresentationDay.iso until) (PresentationDay.iso from))
            | _ -> Ok request

    /// The series reads the request asks for: one per metric, in the order
    /// the request names them, at `maxPoints` (clamped by the read itself).
    let toQueries (maxPoints: int) (request: PresentationRequest) : FactSeriesQuery list =
        let from, until = range request

        request.Metrics
        |> List.map (fun metric -> {
            TableId = request.TableId
            Metric = metric
            SubjectPrefix = request.SubjectPrefix
            SubjectLevel = request.SubjectLevel
            PeriodFrom = from
            PeriodTo = until
            MaxPoints = maxPoints
        })

    // ── The JSON codec ────────────────────────────────────────────────

    /// The request as a JSON value. Absent options and empty lists are
    /// omitted, so the shape is the smallest that says the request.
    let toJsonValue (request: PresentationRequest) : JsonValue =
        let period =
            match request.Period with
            | PresentationPeriod.Ending(day, rangeDays) ->
                jobj [ "day", jstr (PresentationDay.iso day); "rangeDays", jint rangeDays ]
            | PresentationPeriod.Between(from, until) ->
                jobj [
                    "from", jstr (PresentationDay.iso from)
                    "to", jstr (PresentationDay.iso until)
                ]

        jobj [
            "kind", jstr (kindName request.Kind)
            "tableId", jstr request.TableId
            "metrics", jarr (request.Metrics |> List.map jstr)
            if not (List.isEmpty request.SubjectPrefix) then
                "subjectPrefix", jarr (request.SubjectPrefix |> List.map jstr)
            match request.SubjectLevel with
            | Some level -> "subjectLevel", jstr level
            | None -> ()
            "period", period
            match request.CompareTo with
            | Some PresentationCompareTo.PreviousDelivery -> "compareTo", jstr "previousDelivery"
            | None -> ()
            match request.Title with
            | Some t -> "title", jstr t
            | None -> ()
            if not (List.isEmpty request.Annotations) then
                "annotations",
                jarr (
                    request.Annotations
                    |> List.map (fun a -> jobj [ "on", jstr (PresentationDay.iso a.On); "label", jstr a.Label ])
                )
        ]

    /// The request as compact JSON text.
    let toJson (request: PresentationRequest) : string =
        JsonHost.serialize (toJsonValue request)

    let private field (name: string) (v: JsonValue) : JsonValue option =
        match JsonValue.tryField name v with
        | Some JNull
        | None -> None
        | some -> some

    let private requiredString (name: string) (v: JsonValue) : Result<string, PresentationRequestError> =
        match field name v with
        | None -> Error(PresentationRequestError.MissingField name)
        | Some j ->
            match JsonValue.asString j with
            | Some s when not (String.IsNullOrWhiteSpace s) -> Ok s
            | Some _ -> Error(PresentationRequestError.MissingField name)
            | None -> Error(PresentationRequestError.InvalidField(name, "it is not a string"))

    let private optionalString (name: string) (v: JsonValue) : Result<string option, PresentationRequestError> =
        match field name v with
        | None -> Ok None
        | Some j ->
            match JsonValue.asString j with
            | Some s -> Ok(Some s)
            | None -> Error(PresentationRequestError.InvalidField(name, "it is not a string"))

    let private stringList (name: string) (v: JsonValue) : Result<string list, PresentationRequestError> =
        match field name v with
        | None -> Ok []
        | Some j ->
            match JsonValue.asArray j with
            | None -> Error(PresentationRequestError.InvalidField(name, "it is not an array"))
            | Some items ->
                let strings = items |> List.choose JsonValue.asString

                if List.length strings = List.length items then
                    Ok strings
                else
                    Error(PresentationRequestError.InvalidField(name, "an item is not a string"))

    let private day (name: string) (j: JsonValue) : Result<DateTime, PresentationRequestError> =
        match JsonValue.asString j |> Option.bind PresentationDay.tryParse with
        | Some d -> Ok d
        | None -> Error(PresentationRequestError.InvalidField(name, "it is not a day written yyyy-MM-dd"))

    let private requiredDay (owner: string) (name: string) (v: JsonValue) =
        match field name v with
        | None -> Error(PresentationRequestError.MissingField(owner + "." + name))
        | Some j -> day (owner + "." + name) j

    let private period (v: JsonValue) : Result<PresentationPeriod, PresentationRequestError> =
        match field "period" v with
        | None -> Error(PresentationRequestError.MissingField "period")
        | Some p when (JsonValue.asObject p).IsNone ->
            Error(PresentationRequestError.InvalidField("period", "it is not an object"))
        | Some p ->
            match field "day" p, field "from" p with
            | Some _, Some _ -> Error(PresentationRequestError.InvalidField("period", "it names both 'day' and 'from'"))
            | Some _, None ->
                requiredDay "period" "day" p
                |> Result.bind (fun d ->
                    match field "rangeDays" p with
                    | None -> Ok(PresentationPeriod.Ending(d, 1))
                    | Some r ->
                        match JsonValue.asInt r with
                        | Some n -> Ok(PresentationPeriod.Ending(d, n))
                        | None ->
                            Error(
                                PresentationRequestError.InvalidField("period.rangeDays", "it is not a whole number")
                            ))
            | None, Some _ ->
                requiredDay "period" "from" p
                |> Result.bind (fun from ->
                    requiredDay "period" "to" p
                    |> Result.map (fun until -> PresentationPeriod.Between(from, until)))
            | None, None -> Error(PresentationRequestError.MissingField "period.day")

    let private compareTo (v: JsonValue) : Result<PresentationCompareTo option, PresentationRequestError> =
        optionalString "compareTo" v
        |> Result.bind (fun s ->
            match s |> Option.map (fun s -> s.Trim().ToLowerInvariant()) with
            | None -> Ok None
            | Some "previousdelivery" -> Ok(Some PresentationCompareTo.PreviousDelivery)
            | Some other ->
                Error(
                    PresentationRequestError.InvalidField(
                        "compareTo",
                        sprintf "'%s' is not 'previousDelivery'" (Option.defaultValue other s)
                    )
                ))

    let private annotations (v: JsonValue) : Result<PresentationAnnotation list, PresentationRequestError> =
        match field "annotations" v with
        | None -> Ok []
        | Some j ->
            match JsonValue.asArray j with
            | None -> Error(PresentationRequestError.InvalidField("annotations", "it is not an array"))
            | Some items ->
                let folder (acc: Result<PresentationAnnotation list, PresentationRequestError>) (item: JsonValue) =
                    acc
                    |> Result.bind (fun done' ->
                        requiredDay "annotations" "on" item
                        |> Result.bind (fun on ->
                            requiredString "label" item
                            |> Result.mapError (fun _ -> PresentationRequestError.MissingField "annotations.label")
                            |> Result.map (fun label -> { On = on; Label = label } :: done')))

                items |> List.fold folder (Ok []) |> Result.map List.rev

    /// The request a JSON value says, or the refusal naming what to repair.
    let ofJsonValue (v: JsonValue) : Result<PresentationRequest, PresentationRequestError> =
        match JsonValue.asObject v with
        | None -> Error PresentationRequestError.NotAnObject
        | Some _ ->
            requiredString "kind" v
            |> Result.bind (fun k ->
                match tryKind k with
                | Some kind -> Ok kind
                | None -> Error(PresentationRequestError.UnknownKind k))
            |> Result.bind (fun kind ->
                requiredString "tableId" v
                |> Result.bind (fun tableId ->
                    stringList "metrics" v
                    |> Result.bind (fun metrics ->
                        stringList "subjectPrefix" v
                        |> Result.bind (fun prefix ->
                            optionalString "subjectLevel" v
                            |> Result.bind (fun level ->
                                period v
                                |> Result.bind (fun period ->
                                    compareTo v
                                    |> Result.bind (fun compare ->
                                        optionalString "title" v
                                        |> Result.bind (fun title ->
                                            annotations v
                                            |> Result.map (fun annotations -> {
                                                Kind = kind
                                                TableId = tableId
                                                Metrics = metrics
                                                SubjectPrefix = prefix
                                                SubjectLevel = level
                                                Period = period
                                                CompareTo = compare
                                                Title = title
                                                Annotations = annotations
                                            })))))))))
            |> Result.bind validate

    /// The request JSON text says, or the refusal naming what to repair.
    let ofJson (json: string) : Result<PresentationRequest, PresentationRequestError> =
        match
            (if String.IsNullOrWhiteSpace json then
                 None
             else
                 JsonHost.parse json)
        with
        | Some v -> ofJsonValue v
        | None -> Error PresentationRequestError.NotAnObject

/// Phase 1136 — a presentation request drawn as the narrative elements a
/// grounded narrative publishes: a `Table` whose every cell is a `Metric`
/// span citing the Fact it states, so the narrative grounding gate checks
/// each number as it checks the model's own citations. A chart is not drawn
/// here: the gate reads a chart's props as prose, so its points would be
/// refused, and a grounded narrative projects tables until a chart-shaped
/// narrative element exists.
///
/// The builders' rule holds: a gap is never drawn as a number. A point the
/// read withheld, or one with no single number, leaves its cell empty and
/// is named, with its reason, in a note after the table. The note names a
/// subject and a metric, never a count or a value.
///
/// What the gate would refuse is refused here first, by name and before
/// any read: a trend (its rows are days, and a date is a figure to the
/// gate) and annotations (they carry dates). A subject or metric whose NAME
/// carries a digit is drawn, and the gate refuses the table — the grounded
/// run then refuses that request on its audit row; it is never published
/// with the figure unchecked.
[<RequireQualifiedAccess>]
module PresentationNarrative =
    open ToolUp.Platform.Narrative

    let private dayOf = PresentationDay.ofDateTime

    let private pointsOn (day: DateTime) (series: FactSeries) : FactSeriesPoint list =
        series.Points |> List.filter (fun p -> dayOf p.PeriodFrom = day)

    /// A display label per subject: its own member where that is unique,
    /// its readable path where two subjects share a member.
    let private labels (points: FactSeriesPoint list) : Map<string, string> =
        let subjects = points |> List.distinctBy _.Subject

        let short (p: FactSeriesPoint) =
            p.SubjectPath |> List.tryLast |> Option.defaultValue p.Subject

        let counts = subjects |> List.countBy short |> Map.ofList

        subjects
        |> List.map (fun p -> p.Subject, (if Map.find (short p) counts = 1 then short p else p.Subject))
        |> Map.ofList

    /// The cell stating `p`, or why it states nothing.
    let private cell (metric: string) (p: FactSeriesPoint) : Result<InlineSpan list, string> =
        match p.Withheld, p.Value with
        | true, _ -> Error "withheld from you by the disclosure gate"
        | false, Some v ->
            Ok [
                InlineSpan.Metric(metric, p.Rendering |> Option.defaultValue (string v), Some p.FactId)
            ]
        | false, None -> Error "a fact with no single number (an absence or an interval)"

    /// The title, the table, then a note per point not drawn. `headers` is
    /// the subject column's then one per value column; each row is a
    /// subject's label and one cell per value column.
    let private tableOf
        (request: PresentationRequest)
        (headers: string list)
        (rows: (string * Result<InlineSpan list, string> list) list)
        : NarrativeElement list =
        let level = List.head headers
        let valueHeaders = List.tail headers

        let heading =
            match request.Title with
            | Some t when not (String.IsNullOrWhiteSpace t) -> t
            | _ -> sprintf "%s by %s" (String.concat ", " request.Metrics) level

        let drawn =
            rows
            |> List.filter (fun (_, cells) -> cells |> List.exists Result.isOk)
            |> List.map (fun (subject, cells) ->
                [ InlineSpan.Text subject ]
                :: (cells
                    |> List.map (function
                        | Ok spans -> spans
                        | Error _ -> [])))

        let gaps =
            rows
            |> List.collect (fun (subject, cells) ->
                List.zip valueHeaders cells
                |> List.choose (fun (header, c) ->
                    match c with
                    | Error why ->
                        Some(
                            NarrativeElement.Paragraph [
                                InlineSpan.Text(sprintf "Not shown: %s, %s, %s." subject header why)
                            ]
                        )
                    | Ok _ -> None))

        [
            NarrativeElement.Heading(3, [ InlineSpan.Text heading ])
            NarrativeElement.Table(
                (level, TableAlignment.Left)
                :: (valueHeaders |> List.map (fun h -> h, TableAlignment.Right)),
                drawn
            )
            yield! gaps
        ]

    /// Why `request` draws nothing in a grounded narrative whatever its
    /// facts, when it does not.
    let private undrawable (request: PresentationRequest) : string option =
        if request.Kind = PresentationKind.Trend then
            Some
                "a trend is not drawn in a grounded narrative: its rows are days, which the grounding gate reads as figures; request a table, a breakdown or a comparison"
        elif not (List.isEmpty request.Annotations) then
            Some "annotations carry dates, which the grounding gate reads as figures; a projected table takes none"
        else
            None

    /// The elements `request` draws from `series` — one series per metric
    /// the request names, in its order, each as the gated read returned it.
    /// Pure. `Error` names why the request draws nothing.
    let draw (request: PresentationRequest) (series: FactSeries list) : Result<NarrativeElement list, string> =
        match PresentationRequest.validate request with
        | Error e -> Error(PresentationRequestError.describe e)
        | Ok request ->
            match undrawable request, series with
            | Some why, _ -> Error why
            | None, _ when List.map (fun (s: FactSeries) -> s.Metric) series <> request.Metrics ->
                Error "the series are not one per metric the request names, in its order"
            | None, _ when series |> List.exists (fun s -> s.TableId <> request.TableId) ->
                Error(sprintf "a series is not of table '%s'" request.TableId)
            | None, [] -> Error "the request names no metric"
            | None, first :: _ ->
                let level = first.SubjectLevel
                let day = PresentationRequest.delivery request
                let onDay = series |> List.map (pointsOn day)
                let names = labels (List.concat onDay)

                let subjects (points: FactSeriesPoint list) =
                    points |> List.map _.Subject |> List.distinct

                let labelOf (known: Map<string, string>) (s: string) =
                    known |> Map.tryFind s |> Option.defaultValue s

                let at (missing: string) (metric: string) (points: FactSeriesPoint list) (s: string) =
                    match points |> List.tryFind (fun p -> p.Subject = s) with
                    | Some p -> cell metric p
                    | None -> Error missing

                match request.Kind with
                | _ when onDay |> List.forall List.isEmpty -> Error "the period's last day holds no fact to draw"
                | PresentationKind.Breakdown ->
                    // Largest first; a point with no published number last.
                    let metric = request.Metrics.Head

                    let order =
                        onDay.Head
                        |> List.sortByDescending (fun p -> if p.Withheld then None else p.Value)
                        |> subjects

                    Ok(
                        tableOf
                            request
                            [ level; metric ]
                            (order
                             |> List.map (fun s -> labelOf names s, [ at "no fact" metric onDay.Head s ]))
                    )
                | PresentationKind.Comparison ->
                    let metric = request.Metrics.Head

                    let before =
                        first.Points
                        |> List.map (fun p -> dayOf p.PeriodFrom)
                        |> List.filter (fun d -> d < day)
                        |> List.sortDescending
                        |> List.tryHead

                    match before with
                    | None -> Error "the period holds no delivery before the one compared"
                    | Some before ->
                        let earlier = pointsOn before first
                        let known = labels (onDay.Head @ earlier)

                        Ok(
                            tableOf
                                request
                                [ level; metric; sprintf "%s, previous delivery" metric ]
                                (subjects (onDay.Head @ earlier)
                                 |> List.map (fun s ->
                                     labelOf known s,
                                     [
                                         at "no fact for that delivery" metric onDay.Head s
                                         at "no fact for that delivery" metric earlier s
                                     ]))
                        )
                | _ ->
                    // A table: subjects by metrics.
                    Ok(
                        tableOf
                            request
                            (level :: request.Metrics)
                            (subjects (List.concat onDay)
                             |> List.map (fun s ->
                                 labelOf names s,
                                 List.zip request.Metrics onDay
                                 |> List.map (fun (metric, points) -> at "no fact" metric points s)))
                    )

    /// Decode `requestJson` (the request's one wire shape), read each of its
    /// series through `read` — the gated read, which judges every point for
    /// the reader before anything is drawn — and draw it. `Error` names the
    /// malformed field, the refused read, or why the request draws nothing.
    let project
        (read: FactSeriesQuery -> Async<Result<FactSeries, string>>)
        (requestJson: string)
        : Async<Result<NarrativeElement list, string>> =
        let rec readAll (queries: FactSeriesQuery list) (acc: FactSeries list) = async {
            match queries with
            | [] -> return Ok(List.rev acc)
            | q :: rest ->
                match! read q with
                | Ok s -> return! readAll rest (s :: acc)
                | Error why -> return Error(sprintf "the fact series read refused %s: %s" q.Metric why)
        }

        async {
            match PresentationRequest.ofJson requestJson with
            | Error e -> return Error(PresentationRequestError.describe e)
            | Ok request ->
                match undrawable request with
                | Some why -> return Error why
                | None ->
                    match! readAll (PresentationRequest.toQueries FactBrowseApi.MaxSeriesPoints request) [] with
                    | Error why -> return Error why
                    | Ok series -> return draw request series
        }