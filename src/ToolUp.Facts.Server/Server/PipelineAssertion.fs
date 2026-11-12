// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics

// ─── A pipeline result asserted as facts ─────────────────────────────
//
// `evaluate_pipeline` computes over a gated slice and shows the user the
// result; a figure the model restates from it has no fact id, so neither the
// answer verifier nor the grounding gate can check it. A deployment that
// composes derived assertions lets the model ask, in the same call, that one
// numeric column of the result be asserted: one fact per result row, so the
// figure has an id, a lineage, a disclosure stance and a supersession edge,
// and every gate that checks a fact checks it. The arithmetic runs once, in
// the pinned evaluator; nothing re-derives it inside a gate.
//
// **The draft** (`draft`, pure). One `FactDraft` per result row:
//
//   - the subject is the row's key: a result column that is a COPY of the
//     slice's `subject` or `subject_path` column (carried through projections,
//     plain derives and group keys), resolved against the slice's rows to the
//     subject path at the slice's level, which is the level the request names;
//   - the period is the slice's period range;
//   - the value is the asserted column's number at that row;
//   - the method is `Computed(OperationId, ComputeVersion, pipelineHash)`, so
//     the pipeline and the evaluator's version key the lineage: re-running the
//     same pipeline after an input moved mints a new content address that
//     supersedes within the lineage, and an unchanged re-run is idempotent;
//   - the evidence names, as `fact:<id>` (`FactInput`), every fact of the
//     slice the row was computed from, so the content address moves when an
//     input does and the taint walk reads a computed lineage.
//
// **The inputs of a row.** The asserted column's lineage (the slice columns
// its values and the rows it was computed over depend on) names the metrics it
// reads. A row's inputs are those metrics' facts in the slice rows of the
// row's own subject when the pipeline is SUBJECT-LOCAL — every aggregation
// keeps the subject among its keys, every window partitions by it, no limit
// precedes an aggregation, and the column reads no ranking or row position —
// and in every slice row otherwise. Both are sound: the second is a superset
// of what the row depends on, never a subset, so the taint it carries and the
// supersession it triggers err towards more, never less. A row whose value
// reads no fact is refused: its inputs cannot be named.
//
// **Refusals, by name.** A column the result lacks, a withheld column (its
// values are partial), a non-numeric or host-rounded column, an unregistered
// metric or one not registered as produced by the pipeline operation, a level
// other than the slice's, a result whose rows name no subject or one subject
// twice, a row with no value, no inputs or too many.
//
// **Disclosure.** A derived fact is `Surfaceable` in its own right, as a
// registered metric's fact is; its taint is the join of its inputs' labels,
// which the disclosure gate's taint walk computes from the facts its evidence
// names, so a fact computed from a taint-propagating restricted input is
// denied at every door until a declassification routine registered for
// `OperationId` clears it.
//
// **The write** (`answerWith`, through the spec-tool door's `assertRows`).
// Through `IFactStore.AssertBatch`: one tool
// call is one audited batch with its receipt. The facts are read back and
// judged at the tool-result door, so the model is told the rendering of each
// fact it may be shown and the policy that withholds each it may not.

/// Asserting a pipeline result's column as facts of a registered derived
/// metric.
[<RequireQualifiedAccess>]
module PipelineAssertion =

    open Fuaran.Core
    open Fuaran.Compute

    /// The `Computed` operation id every asserted fact carries: the identity a
    /// derived metric's `ProducingOperation` names, and a declassification
    /// routine is registered under.
    [<Literal>]
    let OperationId = PipelineTool.OperationId

    /// The version of the evaluator the pipeline ran under: the
    /// `Fuaran.Compute.DataFrame` package version.
    let ComputeVersion: string = PipelineTool.ComputeVersion

    /// The method an asserted fact carries for a pipeline of hash
    /// `pipelineHash`.
    let methodOf (pipelineHash: string) : MethodRef =
        Computed(OperationId, ComputeVersion, pipelineHash)

    /// The most facts one asserted value may name as its inputs.
    let MaxInputs: int = SpecTool.MaxInputs

    // ── Subject locality ──────────────────────────────────────────

    /// What the locality walk knows between steps: the columns that copy a
    /// slice subject column (output column → slice column), whether every
    /// value so far is computed within one subject's rows, and whether a limit
    /// has cut the row set.
    type private Locality = {
        Copies: Map<string, string>
        Local: bool
        Limited: bool
    }

    let private keep (names: string list) (copies: Map<string, string>) : Map<string, string> =
        names
        |> List.choose (fun n -> copies.TryFind n |> Option.map (fun source -> n, source))
        |> Map.ofList

    /// One step of the walk. Total over the closed `Transform` union.
    let private localityAfter (l: Locality) (step: Transform) : Locality =
        match step with
        | Filter _
        | Sort _
        | Distinct
        | Define _ -> l
        | Limit _ -> { l with Limited = true }
        | Project pairs -> {
            l with
                Copies =
                    pairs
                    |> List.choose (fun (source, out) -> l.Copies.TryFind source |> Option.map (fun s -> out, s))
                    |> Map.ofList
          }
        | Derive(name, Col source) when l.Copies.ContainsKey source -> {
            l with
                Copies = l.Copies.Add(name, l.Copies[source])
          }
        | Derive(name, _) -> { l with Copies = l.Copies.Remove name }
        | GroupBy(keys, _) ->
            let kept = keep keys l.Copies

            {
                Copies = kept
                Local = l.Local && not l.Limited && not kept.IsEmpty
                Limited = l.Limited
            }
        | Window spec -> {
            l with
                Copies = l.Copies.Remove spec.As
                Local =
                    l.Local
                    && not l.Limited
                    && (spec.PartitionBy |> List.exists l.Copies.ContainsKey)
          }
        | Unpivot(ids, _) -> { l with Copies = keep ids l.Copies }
        | Fit _
        | Predict _
        | Pivot _
        | Join _
        | Union _
        | Intersect _
        | Except _ -> {
            l with
                Copies = Map.empty
                Local = false
          }

    /// True when a slice column's value depends on other subjects' rows: the
    /// row position and the ranking positions.
    let private crossSubject (column: string) : bool =
        column = FactBrowseSlice.RowColumn
        || column.EndsWith("/rank_desc", StringComparison.Ordinal)
        || column.EndsWith("/rank_asc", StringComparison.Ordinal)

    // ── The draft ─────────────────────────────────────────────────

    let private decodeTable (encoded: string) : Result<Table, PipelineRefusal> =
        match ColumnCodec.decode encoded with
        | Ok(Embedded table) -> Ok table
        | Ok(Ref name) -> Error(PipelineRefusal.EvaluationFailed(sprintf "the result names a source '%s'" name))
        | Error e -> Error(PipelineRefusal.EvaluationFailed(ColumnCodec.errorString e))

    let private typeName (t: ColumnType) : string =
        match t with
        | StringType -> "text"
        | BoolType -> "boolean"
        | DateType -> "date"
        | TimestampType _ -> "instant"
        | other -> sprintf "%A" other

    let private numberOf (cell: Cell) : decimal option =
        match cell with
        | Decimal text -> Some(Decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture))
        | Int i -> Some(decimal i)
        | Float f -> Some(decimal f)
        | _ -> None

    let private textOf (cell: Cell) : string option =
        match cell with
        | Str s -> Some s
        | _ -> None

    let private orRefuse (refusal: PipelineRefusal) (value: 'T option) : Result<'T, PipelineRefusal> =
        match value with
        | Some v -> Ok v
        | None -> Error refusal

    /// The rows asserting `request.Column` of `result`, evaluated over
    /// `slice`, as `request.AsMetric` — one per result row, in result order —
    /// or the refusal naming what to repair. Pure. `operationId` is the
    /// operation the metric must be registered as produced by.
    let internal rowsOf
        (operationId: string)
        (registry: IMetricRegistry option)
        (slice: FactSlice)
        (evaluation: PipelineToolResult)
        (request: PipelineAssertionRequest)
        : Result<SpecFactRow list, PipelineRefusal> =
        let column = request.Column

        let require (holds: bool) (refusal: PipelineRefusal) : Result<unit, PipelineRefusal> =
            if holds then Ok() else Error refusal

        result {
            do!
                require
                    (List.contains column evaluation.Columns)
                    (PipelineRefusal.AssertedColumnUnknown(column, evaluation.Columns))

            do!
                match evaluation.Withheld |> List.tryFind (fun w -> w.Column = column) with
                | Some withheld -> Error(PipelineRefusal.AssertedColumnWithheld(column, withheld.Withheld))
                | None -> Ok()

            let! source = FactBrowseSlice.decode slice |> Result.mapError PipelineRefusal.SliceRefused
            let! table = decodeTable evaluation.Table
            let! pipeline = PipelineTool.decode evaluation.Request.Pipeline
            let field = table.Schema |> List.find (fun f -> f.Name = column)

            do!
                match field.Type with
                | DecimalType
                | IntType
                | FloatType -> Ok()
                | other -> Error(PipelineRefusal.AssertedColumnNotNumeric(column, typeName other))

            do!
                require
                    (SchemaWalk.exactnessOf column (SchemaWalk.ofPipeline source.Schema pipeline)
                     <> Some Exactness.HostRounded)
                    (PipelineRefusal.AssertedColumnHostRounded column)

            let! _ =
                SpecTool.derivedMetric registry operationId request.AsMetric
                |> Result.mapError PipelineRefusal.ofDoor

            do!
                require
                    (request.Level = slice.Level)
                    (PipelineRefusal.SubjectKeyUnresolved(
                        sprintf
                            "the slice's rows sit at level '%s' of hierarchy '%s', and the assertion names level '%s'"
                            slice.Level
                            slice.Hierarchy
                            request.Level
                    ))

            let! lineage = PipelineTool.lineageOf source.Schema pipeline

            let read =
                lineage.TryFind column
                |> Option.defaultValue (source.Schema |> List.map _.Name |> Set.ofList)

            let walked =
                pipeline
                |> List.fold localityAfter {
                    Copies =
                        Map.ofList [
                            FactBrowseSlice.SubjectColumn, FactBrowseSlice.SubjectColumn
                            FactBrowseSlice.PathColumn, FactBrowseSlice.PathColumn
                        ]
                    Local = true
                    Limited = false
                }

            let local = walked.Local && not (read |> Set.exists crossSubject)

            let! key =
                evaluation.Columns
                |> List.tryFind walked.Copies.ContainsKey
                |> orRefuse (
                    PipelineRefusal.SubjectKeyUnresolved
                        "no column of the result carries the slice's subject or subject_path, so its rows name no subject; keep one of them as a group key"
                )

            let sliceKeys =
                PipelineTool.cellsOf walked.Copies[key] source
                |> List.map textOf
                |> Array.ofList

            let slicePaths =
                PipelineTool.cellsOf FactBrowseSlice.PathColumn source
                |> List.map textOf
                |> Array.ofList

            // Per slice row, the admitted facts of the metrics the column reads.
            let factsByRow: string list array =
                let perMetric =
                    slice.Metrics
                    |> List.map _.Metric
                    |> List.filter (fun m -> PipelineTool.readsMetric m read)
                    |> List.map (fun m ->
                        List.zip
                            (PipelineTool.cellsOf (FactBrowseSlice.metricColumn m "fact_id") source)
                            (PipelineTool.cellsOf (FactBrowseSlice.metricColumn m "withheld") source)
                        |> List.map (function
                            | Str id, Null -> Some id
                            | _ -> None)
                        |> Array.ofList)

                Array.init (Table.rowCount source) (fun i -> perMetric |> List.choose (fun cells -> cells[i]))

            let everyRow = [ 0 .. Table.rowCount source - 1 ]

            let rowOf (keyCell: Cell) (valueCell: Cell) : Result<SpecFactRow, PipelineRefusal> = result {
                let! keyValue =
                    textOf keyCell
                    |> orRefuse (
                        PipelineRefusal.SubjectKeyUnresolved(
                            sprintf "a row's '%s' is empty, so it names no subject" key
                        )
                    )

                let rows = everyRow |> List.filter (fun i -> sliceKeys[i] = Some keyValue)

                let! path =
                    rows
                    |> List.tryHead
                    |> Option.bind (fun i -> slicePaths[i])
                    |> orRefuse (
                        PipelineRefusal.SubjectKeyUnresolved(
                            sprintf "'%s' in column '%s' names no subject of the slice" keyValue key
                        )
                    )

                let subject: SubjectRef = {
                    Hierarchy = slice.Hierarchy
                    Path = path.Split('>') |> List.ofArray
                }

                let named = SubjectRef.toString subject
                let! value = numberOf valueCell |> orRefuse (PipelineRefusal.ValueMissing named)

                let inputs =
                    (if local then rows else everyRow)
                    |> List.collect (fun i -> factsByRow[i])
                    |> List.distinct
                    |> List.sort

                let row: SpecFactRow = {
                    Subject = subject
                    Metric = request.AsMetric
                    Period = {
                        From = evaluation.Request.PeriodFrom
                        To = evaluation.Request.PeriodTo
                        Label = None
                    }
                    Value = value
                    Inputs = inputs
                }

                do! SpecTool.checkRow row |> Result.mapError PipelineRefusal.ofDoor
                return row
            }

            let rec collect (seen: Set<SubjectRef>) (acc: SpecFactRow list) (rows: (Cell * Cell) list) =
                match rows with
                | [] -> Ok(List.rev acc)
                | (keyCell, valueCell) :: rest ->
                    match rowOf keyCell valueCell with
                    | Error e -> Error e
                    | Ok d when seen.Contains d.Subject ->
                        Error(PipelineRefusal.SubjectRepeated(SubjectRef.toString d.Subject))
                    | Ok d -> collect (seen.Add d.Subject) (d :: acc) rest

            return! collect Set.empty [] (List.zip (PipelineTool.cellsOf key table) (PipelineTool.cellsOf column table))
        }

    /// The drafts asserting `request.Column` of `result`, evaluated over
    /// `slice`, as `request.AsMetric` — one per result row, in result order —
    /// or the refusal naming what to repair. Pure. `trigger` is the evidence's
    /// trigger ref.
    let draft
        (registry: IMetricRegistry option)
        (slice: FactSlice)
        (evaluation: PipelineToolResult)
        (request: PipelineAssertionRequest)
        (trigger: string option)
        : Result<FactDraft list, PipelineRefusal> =
        rowsOf OperationId registry slice evaluation request
        |> Result.map (List.map (SpecTool.draftOf (methodOf evaluation.PipelineHash) trigger))

    // ── The write ─────────────────────────────────────────────────

    /// The content address a draft is stored under.
    let factIdOf (d: FactDraft) : string = SpecTool.factIdOf d

    /// The position of the first join in an evaluated pipeline. A figure is
    /// asserted only from the facts of the table the request reads, whose
    /// inputs the draft names; a joined table's are not among them.
    let private joinStep (pipeline: Transform list) : int option =
        pipeline
        |> List.tryFindIndex (function
            | Join _ -> true
            | _ -> false)
        |> Option.map ((+) 1)

    /// An evaluation with the rows its `assert` argument asks for.
    type internal Asserting = {
        Evaluation: PipelineToolResult
        Rows: (PipelineAssertionRequest * SpecFactRow list) option
    }

    /// The evaluation over the door's gated read, and, when the arguments
    /// carry `assert`, the rows to assert. A pipeline with a join is evaluated
    /// and never asserted.
    let private evaluateAsserting
        (read: SpecRead)
        (request: SpecRequest<PipelineTool.Spec>)
        : Async<Result<Asserting, PipelineRefusal>> =
        async {
            match! PipelineTool.evaluateRead read request with
            | Error refusal -> return Error refusal
            | Ok evaluation ->
                match request.Spec.Assert, joinStep request.Spec.Pipeline with
                | None, _ -> return Ok { Evaluation = evaluation; Rows = None }
                | Some _, Some step ->
                    return
                        Error(
                            PipelineRefusal.JoinRefused(
                                step,
                                "a figure is asserted only from the facts of the table the request reads, so a pipeline with a join is evaluated and not asserted; evaluate it without 'assert'"
                            )
                        )
                | Some assertion, None ->
                    return
                        rowsOf OperationId read.Registry read.Slice evaluation assertion
                        |> Result.map (fun rows -> {
                            Evaluation = evaluation
                            Rows = Some(assertion, rows)
                        })
        }

    /// `evaluate_pipeline` with derived assertions over the spec-tool door.
    let internal specToolUnder
        (profile: RegisteredPipelineProfile option)
        : SpecTool<PipelineTool.Spec, PipelineRefusal, Asserting> =
        let plain = PipelineTool.specToolUnder profile

        {
            Definition = PipelineTool.definitionWithAssertions
            OperationId = plain.OperationId
            Version = plain.Version
            Decode = PipelineTool.decodeRoot true
            Validate = plain.Validate
            Evaluate = evaluateAsserting
            Hash = plain.Hash
            Project =
                fun a -> {
                    plain.Project a.Evaluation with
                        Facts = a.Rows |> Option.map snd |> Option.defaultValue []
                }
            Summary = fun a -> plain.Summary a.Evaluation
            OfDoor = plain.OfDoor
        }

    /// The executor composed with derived assertions, under the deployment's
    /// pipeline profile (`None`: the full algebra): the evaluation exactly as
    /// `PipelineTool.answerUnder` answers it, and, when the arguments carry
    /// `assert`, the drafts asserted through `AssertBatch` in `scope`, read
    /// back and judged at the tool-result door for `principal`. The facts'
    /// trigger ref names the tool and the principal. A pipeline with a join is
    /// evaluated and never asserted.
    let answerUnder
        (profile: RegisteredPipelineProfile option)
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<Result<PipelineToolResult * PipelineAssertionResult option, PipelineRefusal>> =
        async {
            let tool = specToolUnder profile

            match! SpecTool.evaluateSliced tool deps scope principal argsJson with
            | Error refusals -> return Error(List.head refusals)
            | Ok(_, _, { Evaluation = result; Rows = None }) -> return Ok(result, None)
            | Ok(_,
                 request,
                 {
                     Evaluation = result
                     Rows = Some(assertion, rows)
                 }) ->
                // Asserted even when the result has no row, as one empty batch
                // with its receipt.
                match! SpecTool.assertRows tool deps scope principal request.Spec rows with
                | Error refusal -> return Error refusal
                | Ok asserted ->
                    return
                        Ok(
                            result,
                            Some {
                                Evaluation = result
                                Column = assertion.Column
                                Metric = assertion.AsMetric
                                Facts = asserted.Facts
                                AssertedCount = asserted.AssertedCount
                                SupersedingCount = asserted.SupersedingCount
                                IdempotentCount = asserted.IdempotentCount
                                ReceiptDigest = asserted.ReceiptDigest
                            }
                        )
        }

    /// `answerUnder` with no profile: the full algebra and no join.
    let answerWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<Result<PipelineToolResult * PipelineAssertionResult option, PipelineRefusal>> =
        answerUnder None deps scope principal argsJson

    /// The text the model reads for an assertion: the evaluation's summary and
    /// the asserted facts as JSON, each with its id and, where the model may
    /// be shown it, its rendering, which is what the answer verifier reads.
    let text (assertion: PipelineAssertionResult) : string =
        FactTool.serialize {|
            summary = PipelineTool.summary assertion.Evaluation
            asserted = {|
                column = assertion.Column
                metric = assertion.Metric
                facts = SpecTool.factsJson assertion.Facts
                newFacts = assertion.AssertedCount
                superseding = assertion.SupersedingCount
                unchanged = assertion.IdempotentCount
                receiptDigest = assertion.ReceiptDigest
            |}
            note = SpecTool.AssertedNote
        |}
// ─── `evaluate_pipeline` as a method card ────────────────────────────
//
// The pipeline tool is the method registry's first card, so the general case
// and the special one are one mechanism. Its card is generated from the
// shipped tool's own declaration (the description is its meaning, its
// arguments after the slice are its parameters), so the tool a registry
// builds from it declares exactly what the tool always declared, and the
// binding keeps the tool's decoding, validation, evaluation and asserting
// form unchanged. Under a pipeline profile the profile's fragment is the
// card's grammar, and the method profile carries it in the card's entry: a
// deployment that serves the method profile does not serve the pipeline
// profile's fragment beside it.

/// The pipeline tool's method card and its binding.
[<RequireQualifiedAccess>]
module PipelineCard =

    /// The definition the pipeline card binds to: SHA-256, lower-case hex,
    /// over the pinned evaluator's package and version.
    let DefinitionHash: string =
        Text.Encoding.UTF8.GetBytes("Fuaran.Compute.DataFrame@" + PipelineTool.ComputeVersion)
        |> Security.Cryptography.SHA256.HashData
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    let private sliceArguments =
        SpecTool.sliceParameters |> List.map _.Name |> Set.ofList

    /// The card for the tool `definition` declares: `PipelineTool.definition`,
    /// or `PipelineTool.definitionWithAssertions` where a deployment composes
    /// derived assertions.
    let cardOf (definition: AIToolDefinition) : MethodCard = {
        Identity = {
            Id = PipelineTool.OperationId
            Version = PipelineTool.ComputeVersion
            DefinitionHash = DefinitionHash
            Package = "Fuaran.Compute.DataFrame"
            Determinism = MethodDeterminism.HostRounded
        }
        Meaning = {
            Summary = definition.Description
            Answers =
                "What a computation over one gated slice of one fact table gives: totals, rankings, filters, running and lagged values, bands and reshapes."
            Assumes = [
                "the slice has one row per subject and period, with a value column per metric"
            ]
        }
        Applicability = {
            Preconditions = []
            DoesNotApply = [
                "the question needs a fitted model or a forecast, which no pipeline step computes"
                "the question is answered by one fact, which the fact tools read directly"
            ]
            Alternatives = []
            Choice = MethodChoice.Mechanical
        }
        Signature = {
            ToolName = definition.Name
            Parameters =
                definition.Parameters
                |> List.filter (fun p -> not (sliceArguments.Contains p.Name))
                |> List.map (fun p -> {
                    Name = p.Name
                    Meaning = p.Description
                    Schema = p.Type
                    Required = p.Required
                    Default = p.Default
                    Range = None
                    Role = None
                })
            Refusals = []
        }
        Outputs = {
            Tables = [
                {
                    Name = "result"
                    Meaning = "The pipeline's output table, computed over the cells the gate admitted."
                    Columns = []
                }
            ]
            Diagnostics = []
            Warnings = [ "an output column computed from a withheld cell is withheld, with its count" ]
            Facts = []
        }
        Interpretation = {
            Rules = [
                "the answer names the result's shape, its columns and what was withheld, never its values"
                "a withheld column reads a withheld cell: narrow the slice to a region with none and evaluate again"
            ]
            Say = [ "the result's shape and what it computed, presenting the result as shown" ]
            NeverSay = [ "a figure from the result that is not an asserted fact" ]
        }
        Presentation = {
            Blocks = [ "table" ]
            BaseCard = None
        }
        Examples = [
            {
                Title = "Total per site"
                Call =
                    """{"table":"<fact table id>","period_from":"2026-01-01","period_to":"2026-02-01","metrics":["revenue"],"pipeline":[{"$type":"groupBy","keys":["subject"],"aggregates":[{"name":"total","fn":"sum","of":"revenue"}]}]}"""
                Result = "One row per site, with the column 'total'."
                Explanation =
                    "Each row is a site's revenue summed over the month; present the result rather than restating its totals."
            }
        ]
    }

    /// `evaluate_pipeline` bound to its card, under the deployment's pipeline
    /// profile (`None`: the full algebra), whose fragment becomes the card's
    /// grammar. `asserting`: whether the deployment composes derived
    /// assertions, which binds the asserting form and its declaration.
    let binding (profile: RegisteredPipelineProfile option) (asserting: bool) : MethodBinding =
        let grammar = profile |> Option.map _.Fragment

        if asserting then
            MethodTool.ofSpecTool
                (cardOf PipelineTool.definitionWithAssertions)
                DefinitionHash
                grammar
                (PipelineAssertion.specToolUnder profile)
        else
            MethodTool.ofSpecTool
                (cardOf PipelineTool.definition)
                DefinitionHash
                grammar
                (PipelineTool.specToolUnder profile)