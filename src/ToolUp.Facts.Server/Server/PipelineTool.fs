// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── evaluate_pipeline (Phase 1145) ──────────────────────────────────
//
// The assistant computes over a gated fact slice with a `Fuaran.Compute`
// pipeline it writes, and never a number it types. The executor:
//
//   1. reads the request, refusing a malformed one by name;
//   2. decodes the pipeline step by step, refusing a malformed step naming
//      its position, and refuses a step that reads another source (`join`,
//      `union`, `intersect`, `except`), names a parameter or reads the clock:
//      the slice is the only source and nothing is bound;
//   3. reads the slice through the fact browse's view-slice read
//      (`FactBrowseHandler.querySlice`) under the caller's resolved scope and
//      principal, with the gate asked at the TOOL-RESULT door. The read checks
//      the cell cap before the gate and the gate judges every cell before the
//      slice is encoded, so the executor never builds a table from ungated
//      facts. A slice with no fact, or with every cell withheld, is refused:
//      a principal the policy denies gets a refusal, not an empty table;
//   4. walks the pipeline statically against the slice's schema
//      (`SchemaWalk`): a step reading a column its input does not have, or
//      leaving output columns the walk cannot name, is refused by name;
//   5. evaluates it with `DataFrame.evalPrepared` over the prepared slice, with
//      no source resolver;
//   6. applies the withheld rule (below), bounds the result by the slice's
//      cell cap, hashes the pipeline and answers the result record.
//
// **The withheld rule.** A withheld cell is a null the evaluator would
// otherwise skip, so a sum over it is a partial sum wearing the whole's
// label. The walk derives every output column's LINEAGE — the slice columns
// its values depend on — and every output column whose lineage reads a
// metric with any withheld cell in the slice is withheld: its cells are null
// in the result, and the result reports it with the count by policy. The
// lineage is conservative by construction:
//
//   - a value column reads the columns its expression, aggregate or window
//     names, through every earlier step that produced them;
//   - a step that decides WHICH rows survive (`filter`, `distinct`, a
//     `groupBy`'s keys, a `limit` after a `sort`) adds the columns it read to
//     the lineage of every column after it, because a list of the subjects
//     whose revenue exceeds a threshold is partial when one revenue is
//     withheld, whichever column shows it;
//   - a column the walk cannot attribute reads every slice column.
//
// A metric reads as withheld through its value column and its rendering
// column; its fact ids, periods, freshness and ranking positions are the
// existence-level facts every slice read already serves.
//
// **What the model reads** (`summary`): the shape, the result's column names
// and the withheld report — never a cell value. The values are the block's,
// which the user sees: a figure restated in the model's prose has no fact
// id, so the tool description says to present the result and never retype it.

/// The `evaluate_pipeline` AI tool: its definition, and the executor over the
/// fact browse's view-slice read.
[<RequireQualifiedAccess>]
module PipelineTool =

    open Fuaran.Core
    open Fuaran.Compute

    /// The tool's name.
    [<Literal>]
    let Name = "evaluate_pipeline"

    // ── The definition ────────────────────────────────────────────

    let private parameters: ToolParameterSchema list = [
        ToolSchema.parameter "table" "The fact table to read." true ToolSchema.text
        ToolSchema.parameter
            "period_from"
            "The first instant of the slice, yyyy-MM-dd or an ISO-8601 UTC instant (inclusive): a fact is read when its period overlaps the range."
            true
            ToolSchema.text
        ToolSchema.parameter
            "period_to"
            "The end of the slice, yyyy-MM-dd or an ISO-8601 UTC instant (exclusive)."
            true
            ToolSchema.text
        ToolSchema.parameter
            "path_prefix"
            "Member ids from the hierarchy root narrowing the subjects. Omit for every subject of the table."
            false
            (ToolSchema.arrayOf ToolSchema.text)
        ToolSchema.parameter
            "metrics"
            "The metric columns of the table to read. Omit for all of them."
            false
            (ToolSchema.arrayOf ToolSchema.text)
        ToolSchema.parameter
            "pipeline"
            "The computation: a JSON array of transform steps, each an object tagged by \"$type\"."
            true
            (ToolSchema.arrayOf (ToolSchema.objectOf []))
    ]

    let private description =
        "Compute over the deployment's verified facts without typing a number. Name one fact table, a period range and optionally a subject path prefix and a metric set: the server reads that slice under the disclosure gate and evaluates your pipeline over it. The slice has one row per subject and period, with the columns row, subject, subject_path, period_from, period_to and period_label, then for each metric m the columns m (the value), m/fact_id, m/as_of, m/freshness, m/rendering, m/withheld, m/rank_desc and m/rank_asc. The pipeline is a JSON array of Fuaran.Compute transform steps, each tagged by \"$type\": filter, project, derive, groupBy (with aggregates sum, mean, min, max, count, median, stddev, first, last, countDistinct), window, unpivot, sort, distinct and limit, with expressions over columns by name and literal constants. The slice is the only source, so join, union, intersect and except are refused; so is pivot, whose columns the data would name, and so are parameters and now. The table, metric and level ids come from the deployment's standing context (the fact tables its modules declared); use ids given in the conversation or system context, since this tool cannot list them. A malformed step, an unknown column or a result over the cap is refused by name: repair it from the message. An output column computed from a metric with a withheld cell in the slice is withheld, with its count: narrow the slice to a region with none and evaluate again. The result tells you its shape, its columns and what was withheld, and carries none of its values: its figures are shown to the user in the result block and are never retyped, so present the block and state no number from it."

    /// The tool's declaration: server-resident (the result is the server's
    /// evaluation of a read the server made), on both surfaces, declaring
    /// `ReadFacts` only, under the fact store's reserved source.
    let definition: AIToolDefinition = {
        Name = Name
        Description = description
        Parameters = parameters
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    // ── The request ───────────────────────────────────────────────

    let private malformed (reason: string) =
        Error(PipelineRefusal.MalformedArguments reason)

    let private instantOf (name: string) (text: string) : Result<DateTime, PipelineRefusal> =
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

        match DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, styles) with
        | true, day -> Ok day
        | false, _ ->
            match DateTime.TryParse(text, CultureInfo.InvariantCulture, styles) with
            | true, instant -> Ok instant
            | false, _ -> malformed (sprintf "'%s' is not a date (yyyy-MM-dd) or an ISO-8601 instant: '%s'" name text)

    let private textsOf (name: string) (root: JsonElement) : Result<string list, PipelineRefusal> =
        match root.TryGetProperty name with
        | false, _ -> Ok []
        | true, v when v.ValueKind = JsonValueKind.Null -> Ok []
        | true, v when v.ValueKind = JsonValueKind.Array ->
            let items = v.EnumerateArray() |> List.ofSeq

            if items |> List.forall (fun i -> i.ValueKind = JsonValueKind.String) then
                Ok(items |> List.map _.GetString())
            else
                malformed (sprintf "'%s' must be an array of strings" name)
        | true, _ -> malformed (sprintf "'%s' must be an array of strings" name)

    let private textOf (name: string) (root: JsonElement) : Result<string, PipelineRefusal> =
        match root.TryGetProperty name with
        | true, v when
            v.ValueKind = JsonValueKind.String
            && not (String.IsNullOrWhiteSpace(v.GetString()))
            ->
            Ok(v.GetString())
        | _ -> malformed (sprintf "'%s' is required: a non-empty string" name)

    /// The request the tool's arguments name. The pipeline is carried as the
    /// arguments wrote it; `answerWith` re-encodes it canonically.
    let requestOf (argsJson: string) : Result<PipelineToolRequest, PipelineRefusal> =
        let parsed =
            try
                Ok((JsonDocument.Parse argsJson).RootElement.Clone())
            with _ ->
                malformed "the arguments are not valid JSON"

        match parsed with
        | Error e -> Error e
        | Ok root when root.ValueKind <> JsonValueKind.Object -> malformed "the arguments must be a JSON object"
        | Ok root ->
            let pipeline =
                match root.TryGetProperty "pipeline" with
                | true, v when v.ValueKind = JsonValueKind.Array -> Ok(v.GetRawText())
                | _ -> malformed "'pipeline' is required: a JSON array of transform steps"

            match
                textOf "table" root,
                textOf "period_from" root |> Result.bind (instantOf "period_from"),
                textOf "period_to" root |> Result.bind (instantOf "period_to"),
                textsOf "path_prefix" root,
                textsOf "metrics" root,
                pipeline
            with
            | Ok table, Ok from, Ok until, Ok prefix, Ok metrics, Ok pipeline ->
                Ok {
                    TableId = table
                    PeriodFrom = from
                    PeriodTo = until
                    PathPrefix = prefix
                    Metrics = metrics
                    Pipeline = pipeline
                }
            | Error e, _, _, _, _, _
            | _, Error e, _, _, _, _
            | _, _, Error e, _, _, _
            | _, _, _, Error e, _, _
            | _, _, _, _, Error e, _
            | _, _, _, _, _, Error e -> Error e

    // ── The pipeline, step by step ────────────────────────────────

    /// The pipeline's steps, each decoded at its position: a malformed step
    /// is refused naming its 1-based position.
    let decode (pipeline: string) : Result<Transform list, PipelineRefusal> =
        match Canonical.read pipeline with
        | Error e -> Error(PipelineRefusal.MalformedPipeline(None, e.Message))
        | Ok(JArr steps) ->
            let rec go (i: int) (acc: Transform list) (rest: JVal list) =
                match rest with
                | [] -> Ok(List.rev acc)
                | step :: rest ->
                    match DataFrameCodec.decodeTransform step with
                    | Ok t -> go (i + 1) (t :: acc) rest
                    | Error e -> Error(PipelineRefusal.MalformedPipeline(Some i, ColumnCodec.errorString e))

            go 1 [] steps
        | Ok _ -> Error(PipelineRefusal.MalformedPipeline(None, "it is not a JSON array"))

    /// The verb name a refusal cites for a step that reads another source.
    let private otherSource (step: Transform) : string option =
        match step with
        | Join _ -> Some "join"
        | Union _ -> Some "union"
        | Intersect _ -> Some "intersect"
        | Except _ -> Some "except"
        | Filter _
        | Project _
        | Derive _
        | GroupBy _
        | Window _
        | Pivot _
        | Unpivot _
        | Sort _
        | Distinct
        | Limit _ -> None

    /// The refusals decided by the pipeline alone, before anything is read:
    /// another source, a parameter, the clock.
    let private preflight (pipeline: Transform list) : Result<unit, PipelineRefusal> =
        pipeline
        |> List.mapi (fun i step -> i + 1, step)
        |> List.tryPick (fun (i, step) ->
            match otherSource step with
            | Some verb -> Some(PipelineRefusal.NamedSource(i, verb))
            | None ->
                match Transform.paramsOf [ step ] with
                | [] when Transform.usesNow [ step ] -> Some(PipelineRefusal.UnpinnedClock i)
                | [] -> None
                | names -> Some(PipelineRefusal.UnboundParameter(i, names)))
        |> function
            | Some refusal -> Error refusal
            | None -> Ok()

    // ── Lineage ───────────────────────────────────────────────────

    /// The columns an expression reads. Total over the closed `ColExpr`
    /// union: a case the algebra gains is a compile error here, never a
    /// column silently left out of a lineage.
    let rec private exprColumns (e: ColExpr) : string list =
        match e with
        | Col name -> [ name ]
        | Lit _
        | Param _
        | Now _ -> []
        | Binary(_, a, b)
        | Quotient(a, b, _) -> exprColumns a @ exprColumns b
        | Not a
        | IsNull a
        | InParam(a, _)
        | Cast(_, a)
        | Rounded(a, _)
        | Convert(a, _) -> exprColumns a
        | Coalesce xs
        | ApplyFn(_, xs) -> List.collect exprColumns xs
        | Case(cases, otherwise) ->
            (cases |> List.collect (fun (w, t) -> exprColumns w @ exprColumns t))
            @ exprColumns otherwise
        | InList(a, items) -> List.collect exprColumns (a :: items)

    /// True when a window function reads its `Of` column; a ranking reads only
    /// its keys.
    let private readsSource (fn: WindowFn) : bool =
        match fn with
        | RowNumber
        | Rank
        | DenseRank
        | CompetitionRank
        | NTile _ -> false
        | Lag
        | Lead
        | CumulSum
        | RollingMean
        | CumulMax
        | CumulMin
        | RollingSum -> true

    /// The columns of its input a step reads.
    let private stepReads (step: Transform) : string list =
        match step with
        | Filter e
        | Derive(_, e) -> exprColumns e
        | Project pairs -> List.map fst pairs
        | GroupBy(keys, aggs) -> keys @ (aggs |> List.map _.Of)
        | Window spec ->
            spec.PartitionBy
            @ (spec.OrderBy |> List.map fst)
            @ (if readsSource spec.Fn then [ spec.Of ] else [])
        | Pivot spec -> spec.Index @ [ spec.On; spec.Values ]
        | Unpivot(ids, values) -> ids @ values
        | Sort keys -> keys |> List.choose (fst >> Slot.tryLit)
        | Distinct
        | Limit _
        | Join _
        | Union _
        | Intersect _
        | Except _ -> []

    /// What the walk knows between steps: each column's lineage (the slice
    /// columns its values depend on), the slice columns the surviving ROW SET
    /// depends on, and those the row ORDER depends on.
    type private Lineage = {
        Columns: Map<string, Set<string>>
        Rows: Set<string>
        Order: Set<string>
    }

    /// One step's lineage, given the output columns the schema walk names
    /// after it. `all` is every slice column — the lineage of a column the
    /// rules below do not attribute.
    let private lineageAfter (all: Set<string>) (outputs: string list) (lineage: Lineage) (step: Transform) : Lineage =
        let of' (name: string) =
            lineage.Columns.TryFind name |> Option.defaultValue all

        let ofMany (names: string list) = names |> List.map of' |> Set.unionMany

        let (produced: Map<string, Set<string>>), (rows: Set<string>), (order: Set<string>) =
            match step with
            | Filter e -> lineage.Columns, Set.union lineage.Rows (ofMany (exprColumns e)), lineage.Order
            | Project pairs ->
                pairs |> List.map (fun (source, out) -> out, of' source) |> Map.ofList, lineage.Rows, lineage.Order
            | Derive(name, e) -> lineage.Columns.Add(name, ofMany (exprColumns e)), lineage.Rows, lineage.Order
            | GroupBy(keys, aggs) ->
                let byKeys = ofMany keys

                (keys |> List.map (fun k -> k, of' k))
                @ (aggs |> List.map (fun a -> a.Name, Set.union (of' a.Of) byKeys))
                |> Map.ofList,
                Set.union lineage.Rows byKeys,
                lineage.Order
            | Window spec ->
                lineage.Columns.Add(spec.As, Set.unionMany [ ofMany (stepReads step); lineage.Order; lineage.Rows ]),
                lineage.Rows,
                lineage.Order
            | Unpivot(ids, values) ->
                let read = ofMany values

                (ids |> List.map (fun k -> k, of' k)) @ [ "variable", read; "value", read ]
                |> Map.ofList,
                lineage.Rows,
                lineage.Order
            | Sort _ -> lineage.Columns, lineage.Rows, Set.union lineage.Order (ofMany (stepReads step))
            | Limit _ -> lineage.Columns, Set.union lineage.Rows lineage.Order, lineage.Order
            | Distinct ->
                lineage.Columns, Set.union lineage.Rows (lineage.Columns |> Map.values |> Set.unionMany), lineage.Order
            | Pivot _
            | Join _
            | Union _
            | Intersect _
            | Except _ -> Map.empty, all, all

        {
            Columns =
                outputs
                |> List.map (fun name -> name, produced.TryFind name |> Option.defaultValue all)
                |> Map.ofList
            Rows = rows
            Order = order
        }

    /// The static walk: each step's reads checked against the columns its input
    /// has, the output kept nameable, and the lineage of every output column —
    /// its own lineage joined with what decided the row set.
    let private walk (schema: Schema) (pipeline: Transform list) : Result<Map<string, Set<string>>, PipelineRefusal> =
        let all = schema |> List.map _.Name |> Set.ofList

        let start = {
            Columns = all |> Seq.map (fun c -> c, Set.singleton c) |> Map.ofSeq
            Rows = Set.empty
            Order = Set.empty
        }

        let rec go (i: int) (knowledge: SchemaKnowledge) (lineage: Lineage) (rest: Transform list) =
            match rest with
            | [] -> Ok(lineage.Columns |> Map.map (fun _ read -> Set.union read lineage.Rows))
            | step :: rest ->
                let available = SchemaWalk.names knowledge

                match stepReads step |> List.tryFind (fun c -> not (SchemaWalk.has c knowledge)) with
                | Some missing -> Error(PipelineRefusal.UnknownColumn(i, missing, available))
                | None ->
                    let after = SchemaWalk.ofTransform SchemaWalk.noSources knowledge step

                    match SchemaWalk.reason after with
                    | Some reason -> Error(PipelineRefusal.OpenSchema(i, reason))
                    | None -> go (i + 1) after (lineageAfter all (SchemaWalk.names after) lineage step) rest

        go 1 (SchemaWalk.ofSchema schema) start pipeline

    // ── The slice's withheld cells ────────────────────────────────

    let private cellsOf (name: string) (table: Table) : Cell list =
        table
        |> Table.tryColumn name
        |> Option.map Column.toCells
        |> Option.defaultValue []

    /// Each metric's withheld cells in the slice, by policy, in policy order.
    /// A metric with none is absent.
    let private withheldByMetric (slice: FactSlice) (table: Table) : Map<string, PipelineWithheldCount list> =
        slice.Metrics
        |> List.choose (fun m ->
            let policies =
                cellsOf (FactBrowseSlice.metricColumn m.Metric "withheld") table
                |> List.choose (function
                    | Str policy -> Some policy
                    | _ -> None)

            match policies with
            | [] -> None
            | policies ->
                Some(
                    m.Metric,
                    policies
                    |> List.countBy id
                    |> List.sortBy fst
                    |> List.map (fun (policy, count) -> ({ PolicyRef = policy; Count = count }: PipelineWithheldCount))
                ))
        |> Map.ofList

    /// The slice columns through which a metric's withheld cells are read: its
    /// value and its rendering.
    let private withheldSources (metric: string) : Set<string> =
        set [
            FactBrowseSlice.metricColumn metric ""
            FactBrowseSlice.metricColumn metric "rendering"
        ]

    /// True when a lineage reads any of metric `m`'s columns.
    let private readsMetric (metric: string) (read: Set<string>) : bool =
        read
        |> Set.exists (fun c -> c = metric || c.StartsWith(metric + "/", StringComparison.Ordinal))

    let private mergeCounts (counts: PipelineWithheldCount list) : PipelineWithheldCount list =
        counts
        |> List.groupBy _.PolicyRef
        |> List.sortBy fst
        |> List.map (fun (policy, group) -> {
            PipelineWithheldCount.PolicyRef = policy
            Count = group |> List.sumBy _.Count
        })

    // ── Evaluation ────────────────────────────────────────────────

    let private nulled (field: Field) (column: Column) : Result<Column, PipelineRefusal> =
        Column.ofCells column.Name field.Type (List.replicate (Column.length column) Null)
        |> Result.mapError (fun e -> PipelineRefusal.EvaluationFailed(ColumnCodec.errorString e))

    /// The admitted fact ids of the metrics a lineage reads, in slice order.
    let private factIdsRead (slice: FactSlice) (table: Table) (read: Set<string>) : string list =
        slice.Metrics
        |> List.filter (fun m -> readsMetric m.Metric read)
        |> List.collect (fun m ->
            List.zip
                (cellsOf (FactBrowseSlice.metricColumn m.Metric "fact_id") table)
                (cellsOf (FactBrowseSlice.metricColumn m.Metric "withheld") table)
            |> List.choose (function
                | Str id, Null -> Some id
                | _ -> None))
        |> List.distinct

    /// Evaluate a decoded pipeline over a gated slice: the static walk, the
    /// evaluation, the withheld rule, the bound and the record. Pure: the
    /// slice is the only input. `request` is the request as the caller made it;
    /// `readAt` is when the slice was read.
    let evaluate
        (request: PipelineToolRequest)
        (pipeline: Transform list)
        (slice: FactSlice)
        (readAt: DateTime)
        : Result<PipelineToolResult, PipelineRefusal> =
        match FactBrowseSlice.hold slice with
        | Error reason -> Error(PipelineRefusal.SliceRefused reason)
        | Ok held ->
            let source = DataFrame.toTable held.Prepared

            match walk source.Schema pipeline with
            | Error refusal -> Error refusal
            | Ok lineage ->
                match
                    DataFrame.evalPrepared (fun name -> Error(UnresolvedSource name)) Map.empty pipeline held.Prepared
                with
                | Error e -> Error(PipelineRefusal.EvaluationFailed(DataFrame.errorString e))
                | Ok result ->
                    let rows = Table.rowCount result
                    let cells = rows * List.length result.Columns

                    if cells > FactBrowseApi.MaxSliceCells then
                        Error(PipelineRefusal.ResultOverCap(cells, FactBrowseApi.MaxSliceCells))
                    else
                        let withheld = withheldByMetric slice source
                        let all = source.Schema |> List.map _.Name |> Set.ofList

                        let lineageOf (column: string) =
                            lineage.TryFind column |> Option.defaultValue all

                        let withheldColumns =
                            result.Schema
                            |> List.choose (fun field ->
                                let read = lineageOf field.Name

                                match
                                    withheld
                                    |> Map.toList
                                    |> List.filter (fun (m, _) ->
                                        not (Set.isEmpty (Set.intersect read (withheldSources m))))
                                with
                                | [] -> None
                                | metrics ->
                                    Some {
                                        Column = field.Name
                                        Metrics = List.map fst metrics
                                        Withheld = metrics |> List.collect snd |> mergeCounts
                                    })

                        let withheldNames = withheldColumns |> List.map _.Column |> Set.ofList

                        let columns =
                            List.zip result.Schema result.Columns
                            |> List.map (fun (field, column) ->
                                if withheldNames.Contains field.Name then
                                    nulled field column
                                else
                                    Ok column)

                        match
                            columns
                            |> List.tryPick (function
                                | Error e -> Some e
                                | Ok _ -> None)
                        with
                        | Some refusal -> Error refusal
                        | None ->
                            let table = {
                                result with
                                    Columns = columns |> List.choose Result.toOption
                            }

                            match FactBrowseSlice.encode table with
                            | Error reason -> Error(PipelineRefusal.EvaluationFailed reason)
                            | Ok encoded ->
                                let read = result.Schema |> List.map (fun f -> lineageOf f.Name) |> Set.unionMany

                                let ids = factIdsRead slice source read

                                Ok {
                                    Request = {
                                        request with
                                            Pipeline = DataFrameCodec.encodePipeline pipeline
                                    }
                                    Table = encoded
                                    Rows = rows
                                    Columns = result.Schema |> List.map _.Name
                                    Withheld = withheldColumns
                                    PipelineHash = IncrementalCodec.pipelineHash pipeline
                                    RunStamp = slice.RunStamp
                                    ReadAt = readAt
                                    SliceCells = slice.CellCount
                                    FactIds = List.truncate PopulationQuery.MaxTopK ids
                                    FactIdCount = List.length ids
                                }

    // ── The answer ────────────────────────────────────────────────

    let private periodText (from: DateTime) (until: DateTime) : string =
        let text (t: DateTime) =
            if t.TimeOfDay = TimeSpan.Zero then
                t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            else
                t.ToString("o", CultureInfo.InvariantCulture)

        text from + ".." + text until

    /// The text the model reads for an evaluated pipeline: the shape, the
    /// column names and the withheld report — never a cell value.
    let summary (result: PipelineToolResult) : string =
        let withheld =
            match result.Withheld with
            | [] -> "Nothing is withheld."
            | columns ->
                columns
                |> List.map (fun c ->
                    sprintf
                        "'%s' (%s; it reads %s)"
                        c.Column
                        (PipelineRefusal.withheldPhrase c.Withheld)
                        (String.concat ", " c.Metrics))
                |> String.concat ", "
                |> sprintf "Withheld, with their cells empty: %s."

        sprintf
            "Evaluated the pipeline over fact table '%s' for %s%s (%d cell(s) read): the result has %d row(s) and %d column(s): %s. %s The user sees the result; it carries none of its values here, so state none and retype no figure."
            result.Request.TableId
            (periodText result.Request.PeriodFrom result.Request.PeriodTo)
            (match result.Request.PathPrefix with
             | [] -> ""
             | prefix -> sprintf " under %s" (String.concat ">" prefix))
            result.SliceCells
            result.Rows
            (List.length result.Columns)
            (result.Columns |> List.map (sprintf "'%s'") |> String.concat ", ")
            withheld

    /// The text a refusal answers with.
    let refusalText (refusal: PipelineRefusal) : string =
        sprintf "%s refused the request: %s." Name (PipelineRefusal.describe refusal)

    /// The executor over explicit dependencies. `deps` are the fact browse's;
    /// the slice is read under `scope` for `principal`, with the gate asked at
    /// `FactToolResult`.
    let answerWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<Result<PipelineToolResult, PipelineRefusal>> =
        async {
            match requestOf argsJson with
            | Error refusal -> return Error refusal
            | Ok request ->
                match
                    decode request.Pipeline
                    |> Result.bind (fun p -> preflight p |> Result.map (fun () -> p))
                with
                | Error refusal -> return Error refusal
                | Ok pipeline ->
                    let gated = {
                        deps with
                            Gate = FactBrowseHandler.atSurface FactToolResult deps.Gate
                    }

                    let readAt = deps.Clock()

                    match!
                        FactBrowseHandler.querySlice gated scope principal {
                            TableId = request.TableId
                            PeriodFrom = request.PeriodFrom
                            PeriodTo = request.PeriodTo
                            SubjectPrefix = request.PathPrefix
                            Metrics = request.Metrics
                        }
                    with
                    | Error reason -> return Error(PipelineRefusal.SliceRefused reason)
                    | Ok slice when slice.CellCount = 0 -> return Error(PipelineRefusal.NoFacts request.TableId)
                    | Ok slice when slice.WithheldCount >= slice.CellCount ->
                        return
                            Error(
                                PipelineRefusal.NothingDisclosable(
                                    slice.CellCount,
                                    slice.Withheld
                                    |> List.map (fun w ->
                                        ({
                                            PolicyRef = w.PolicyRef
                                            Count = w.Count
                                        }
                                        : PipelineWithheldCount))
                                )
                            )
                    | Ok slice -> return evaluate request pipeline slice readAt
        }