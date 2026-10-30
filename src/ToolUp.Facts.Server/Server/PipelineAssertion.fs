// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Reflection
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes

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
// **The write** (`answerWith`). Through `IFactStore.AssertBatch`: one tool
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
    let OperationId = "fuaran.compute.pipeline"

    /// The version of the evaluator the pipeline ran under: the
    /// `Fuaran.Compute.DataFrame` package version.
    let ComputeVersion: string =
        let assembly = typeof<Transform>.Assembly

        match assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> string (assembly.GetName().Version)
        | attribute -> attribute.InformationalVersion.Split('+').[0]

    /// The method an asserted fact carries for a pipeline of hash
    /// `pipelineHash`.
    let methodOf (pipelineHash: string) : MethodRef =
        Computed(OperationId, ComputeVersion, pipelineHash)

    /// The most facts one asserted value may name as its inputs.
    let MaxInputs: int = PopulationQuery.MaxTopK

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
        | Distinct -> l
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

            let! metric =
                registry
                |> Option.bind (fun r -> r.TryGetMetric request.AsMetric)
                |> orRefuse (PipelineRefusal.UnregisteredMetric request.AsMetric)

            do!
                require
                    (metric.ProducingOperation = Some OperationId)
                    (PipelineRefusal.NotADerivedMetric(request.AsMetric, metric.ProducingOperation))

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

            let rowDraft (keyCell: Cell) (valueCell: Cell) : Result<FactDraft, PipelineRefusal> = result {
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

                do! require (not (List.isEmpty inputs)) (PipelineRefusal.InputsUnnamed named)

                do!
                    require
                        (List.length inputs <= MaxInputs)
                        (PipelineRefusal.InputsOverCap(named, List.length inputs, MaxInputs))

                return {
                    Subject = subject
                    Metric = MetricRef request.AsMetric
                    Value = Scalar value
                    Period = {
                        From = evaluation.Request.PeriodFrom
                        To = evaluation.Request.PeriodTo
                        Label = None
                    }
                    Method = methodOf evaluation.PipelineHash
                    Evidence = {
                        ResultRef = None
                        InputHashes = inputs |> List.map FactInput.ofFactId
                        TriggerRef = trigger
                    }
                    Confidence = None
                    Disclosure = Surfaceable
                }
            }

            let rec collect (seen: Set<SubjectRef>) (acc: FactDraft list) (rows: (Cell * Cell) list) =
                match rows with
                | [] -> Ok(List.rev acc)
                | (keyCell, valueCell) :: rest ->
                    match rowDraft keyCell valueCell with
                    | Error e -> Error e
                    | Ok d when seen.Contains d.Subject ->
                        Error(PipelineRefusal.SubjectRepeated(SubjectRef.toString d.Subject))
                    | Ok d -> collect (seen.Add d.Subject) (d :: acc) rest

            return! collect Set.empty [] (List.zip (PipelineTool.cellsOf key table) (PipelineTool.cellsOf column table))
        }

    // ── The write ─────────────────────────────────────────────────

    /// The content address a draft is stored under.
    let factIdOf (d: FactDraft) : string =
        Fact.compute d.Subject d.Metric d.Period d.Method (Fact.effectiveInputHashes d.Method d.Evidence d.Value)

    /// The executor composed with derived assertions: the evaluation exactly as
    /// `PipelineTool.answerWith` answers it, and, when the arguments carry
    /// `assert`, the drafts asserted through `AssertBatch` in `scope`, read
    /// back and judged at the tool-result door for `principal`. The facts'
    /// trigger ref names the tool and the principal.
    let answerWith
        (deps: FactBrowseHandler.FactBrowseDeps)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<Result<PipelineToolResult * PipelineAssertionResult option, PipelineRefusal>> =
        async {
            match PipelineTool.assertionOf argsJson with
            | Error refusal -> return Error refusal
            | Ok assertion ->
                match! PipelineTool.evaluateSliced deps scope principal argsJson with
                | Error refusal -> return Error refusal
                | Ok(_, result) when Option.isNone assertion -> return Ok(result, None)
                | Ok(slice, result) ->
                    let request = Option.get assertion
                    let trigger = Some(sprintf "%s:%s" PipelineTool.Name principal)

                    match draft deps.Registry slice result request trigger with
                    | Error refusal -> return Error refusal
                    | Ok drafts ->
                        match! deps.Store.AssertBatch(scope, drafts) with
                        | Error reason -> return Error(PipelineRefusal.AssertionFailed(FactStoreError.describe reason))
                        | Ok receipt ->
                            let ids = drafts |> List.map factIdOf

                            let! stored =
                                ids
                                |> List.map (fun id -> deps.Store.Get(scope, id) |> FactStoreResult.orRaiseAsync)
                                |> Async.Sequential

                            let held = stored |> Array.choose id |> List.ofArray

                            let! verdicts =
                                (FactBrowseHandler.atSurface FactToolResult deps.Gate)
                                    .CheckHeld(scope, principal, FactToolResult, ids, held)

                            let displayFormat =
                                deps.Registry
                                |> Option.bind (fun r -> r.TryGetMetric request.AsMetric)
                                |> Option.map _.DisplayFormat
                                |> Option.defaultValue ""

                            let facts =
                                List.zip ids drafts
                                |> List.map (fun (factId, d) ->
                                    let withheld =
                                        match verdicts.TryFind factId with
                                        | Some FactDisclosable -> None
                                        | Some(FactNotDisclosable policy) -> Some policy
                                        | None -> Some "unknown-fact"

                                    {
                                        FactId = factId
                                        Subject = SubjectRef.toString d.Subject
                                        Metric = request.AsMetric
                                        Rendering =
                                            match withheld with
                                            | None -> Some(FactRendering.render displayFormat d.Value)
                                            | Some _ -> None
                                        WithheldPolicy = withheld
                                        InputCount = List.length d.Evidence.InputHashes
                                    })

                            return
                                Ok(
                                    result,
                                    Some {
                                        Evaluation = result
                                        Column = request.Column
                                        Metric = request.AsMetric
                                        Facts = facts
                                        AssertedCount = receipt.AssertedCount
                                        SupersedingCount = receipt.SupersedingCount
                                        IdempotentCount = receipt.IdempotentCount
                                        ReceiptDigest = receipt.Digest
                                    }
                                )
        }

    /// The text the model reads for an assertion: the evaluation's summary and
    /// the asserted facts as JSON, each with its id and, where the model may
    /// be shown it, its rendering, which is what the answer verifier reads.
    let text (assertion: PipelineAssertionResult) : string =
        let facts: obj list =
            assertion.Facts
            |> List.map (fun f ->
                match f.Rendering with
                | Some rendering ->
                    box {|
                        factId = f.FactId
                        subject = f.Subject
                        metric = f.Metric
                        rendering = rendering
                        inputs = f.InputCount
                    |}
                | None ->
                    box {|
                        factId = f.FactId
                        subject = f.Subject
                        metric = f.Metric
                        withheld = f.WithheldPolicy |> Option.defaultValue ""
                        inputs = f.InputCount
                    |})

        FactTool.serialize {|
            summary = PipelineTool.summary assertion.Evaluation
            asserted = {|
                column = assertion.Column
                metric = assertion.Metric
                facts = facts
                newFacts = assertion.AssertedCount
                superseding = assertion.SupersedingCount
                unchanged = assertion.IdempotentCount
                receiptDigest = assertion.ReceiptDigest
            |}
            note =
                "Each asserted figure is a fact: quote it by its rendering, which verifies. State no other figure from the result."
        |}