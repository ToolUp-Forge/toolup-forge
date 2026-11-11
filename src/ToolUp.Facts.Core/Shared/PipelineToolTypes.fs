// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System

// ─── evaluate_pipeline ───────────────────────────────────────────────
//
// The assistant computes over the deployment's facts by WRITING a
// computation, never by typing a number: it names one bounded slice of one
// fact table (the fact browse's view-slice read, judged by the disclosure
// gate before anything is evaluated) and a `Fuaran.Compute` pipeline over it,
// and the server evaluates the pipeline.
//
// These are the tool's request, its result and its refusals. They sit here,
// beside the other fact vocabularies, so a client can render a result; they
// carry the pipeline and the result table as their canonical codec strings
// (`DataFrameCodec.encodePipeline`, `ColumnCodec`), so this tier takes no
// dependency on the columnar strand.
//
// **The withheld rule.** A withheld cell is a null the evaluator would
// otherwise skip, so a sum over it is a partial sum wearing the whole's
// label. Every output column whose lineage reads a metric with any withheld
// cell in the slice is WITHHELD: its cells are empty in the result, and the
// result reports it with the count of withheld cells it read, by policy.

/// What the model asks: a slice of one fact table and a pipeline over it.
type PipelineToolRequest = {
    /// The fact table the slice reads.
    TableId: string
    /// Start of the slice's period range (inclusive): a head is read when its
    /// period overlaps `[PeriodFrom, PeriodTo)`.
    PeriodFrom: DateTime
    /// End of the slice's period range (exclusive).
    PeriodTo: DateTime
    /// Subject path prefix (member ids from the hierarchy root). Empty reads
    /// every subject of the table.
    PathPrefix: string list
    /// The metric columns the slice carries. Empty carries every column of the
    /// table.
    Metrics: string list
    /// The pipeline, in `Fuaran.Compute`'s canonical wire form: a JSON array
    /// of `$type`-tagged transform steps (`DataFrameCodec.encodePipeline`).
    Pipeline: string
}

// The withheld report (`PipelineWithheldCount`, `PipelineWithheldColumn`) is
// the spec-tool seam's, in `SpecToolTypes.fs`.

/// What an evaluation answers. Nothing here is a value the model typed: the
/// table is the evaluator's, over cells the gate admitted.
type PipelineToolResult = {
    /// The request, with the pipeline re-encoded in its canonical wire form —
    /// the slice spec and the pipeline a reader re-runs under their own gate.
    Request: PipelineToolRequest
    /// The result as a `Fuaran.Core.Column` table in its canonical codec
    /// (`ColumnCodec.encode`). A withheld column's cells are all null.
    Table: string
    /// The result's row count.
    Rows: int
    /// The result's column names, in order.
    Columns: string list
    /// The columns the withheld rule withheld, in result order. Empty when the
    /// slice held no withheld cell the result's lineage reads.
    Withheld: PipelineWithheldColumn list
    /// `IncrementalCodec.pipelineHash` of the pipeline: one hash per canonical
    /// pipeline.
    PipelineHash: string
    /// The stamp of the latest run folded into the slice; `None` when the table
    /// had committed no run.
    RunStamp: string option
    /// When the slice was read. The slice answers the table's current heads as
    /// of this instant.
    ReadAt: DateTime
    /// The cells the slice carried (withheld included).
    SliceCells: int
    /// The ids of the admitted facts the result's lineage read, in slice
    /// order, at most `PopulationQuery.MaxTopK` of them.
    FactIds: string list
    /// How many such facts there were; more than `FactIds` holds when the list
    /// was capped.
    FactIdCount: int
}

// ─── Asserting a result as facts ─────────────────────────────────────
//
// A deployment that composes derived assertions lets the model ask, in the
// same call, that one numeric column of the result be ASSERTED: one fact per
// result row, of a registered derived metric, with a `Computed` method naming
// the pipeline's hash and evidence naming every fact the row was computed from
// (`FactInput`). The figure then has an id, a lineage, a disclosure stance and
// a supersession edge, so the answer verifier and the grounding gate check it
// as they check any fact.

/// What the model asks to assert from an evaluation: one result column, as a
/// registered derived metric, at the hierarchy level the result's key names.
type PipelineAssertionRequest = {
    /// The result column whose values are asserted.
    Column: string
    /// The registered derived metric the values are facts of.
    AsMetric: string
    /// The hierarchy level the result's subject key names: the slice's level.
    Level: string
}

/// One fact an assertion stored, or found already stored.
type PipelineAssertedFact = {
    /// The fact's content-addressed id.
    FactId: string
    /// The fact's subject, rendered (`hierarchy/member>member`).
    Subject: string
    /// The derived metric.
    Metric: string
    /// The value in the metric's display format. `None` when the disclosure
    /// gate withholds the fact at the tool-result door.
    Rendering: string option
    /// The policy that withheld it, when one did.
    WithheldPolicy: string option
    /// How many facts the value was computed from.
    InputCount: int
}

/// What an assertion answers: the evaluation it asserted from, and the facts
/// the store holds for it.
type PipelineAssertionResult = {
    /// The evaluation, exactly as the tool answers it without an assertion.
    Evaluation: PipelineToolResult
    /// The asserted column.
    Column: string
    /// The derived metric.
    Metric: string
    /// One fact per result row, in result order.
    Facts: PipelineAssertedFact list
    /// Rows newly stored as the first fact of their lineage.
    AssertedCount: int
    /// Rows newly stored that superseded a predecessor.
    SupersedingCount: int
    /// Rows whose fact was already stored.
    IdempotentCount: int
    /// The batch receipt's digest over its full ordered disposition.
    ReceiptDigest: string
}

/// Why an evaluation was refused. Every case names what to repair.
[<RequireQualifiedAccess>]
type PipelineRefusal =
    /// The tool's arguments are not the request's shape.
    | MalformedArguments of reason: string
    /// The slice read refused the request: an unknown table or metric, an
    /// empty period range, or a slice over the cell cap. The read's own words.
    | SliceRefused of reason: string
    /// No fact of the table answers the slice.
    | NoFacts of tableId: string
    /// Every cell of the slice is withheld from the caller: a refusal, never an
    /// empty table.
    | NothingDisclosable of cells: int * withheld: PipelineWithheldCount list
    /// The pipeline does not decode. `step` is the 1-based position of the step
    /// at fault, or `None` when the pipeline is not a JSON array of steps.
    | MalformedPipeline of step: int option * reason: string
    /// A step reads another source (`join`, `union`, `intersect`, `except`):
    /// the slice is the only source.
    | NamedSource of step: int * verb: string
    /// A step names a parameter; nothing binds one, so every value is written
    /// literally.
    | UnboundParameter of step: int * names: string list
    /// A step reads the clock (`now`); the evaluation pins none.
    | UnpinnedClock of step: int
    /// A step reads a column its input does not have.
    | UnknownColumn of step: int * column: string * available: string list
    /// After a step the output columns can no longer be named (a `pivot`
    /// names its columns from the data).
    | OpenSchema of step: int * reason: string
    /// The evaluator refused the pipeline over the slice (a type the verb
    /// cannot take, an arity, an overflow).
    | EvaluationFailed of reason: string
    /// The result would hold more cells than the cap.
    | ResultOverCap of cells: int * cap: int
    /// The request asks for an assertion and this deployment does not compose
    /// derived assertions.
    | AssertionNotOffered
    /// The `assert` argument is not the assertion's shape.
    | MalformedAssertion of reason: string
    /// The asserted column is not a column of the result.
    | AssertedColumnUnknown of column: string * available: string list
    /// The asserted column is withheld: its values are partial.
    | AssertedColumnWithheld of column: string * withheld: PipelineWithheldCount list
    /// The asserted column does not hold numbers.
    | AssertedColumnNotNumeric of column: string * columnType: string
    /// The asserted column passes through a host-rounded function, so two hosts
    /// may differ in its last place: it is not one fact.
    | AssertedColumnHostRounded of column: string
    /// The metric is not registered.
    | UnregisteredMetric of metric: string
    /// The metric is registered, but not as produced by the pipeline operation.
    | NotADerivedMetric of metric: string * producingOperation: string option
    /// The result's rows do not name subjects at the level asked.
    | SubjectKeyUnresolved of reason: string
    /// Two rows of the result name one subject.
    | SubjectRepeated of subject: string
    /// The asserted column has no value for a subject.
    | ValueMissing of subject: string
    /// A row's value reads no fact the slice carried, so its inputs cannot be
    /// named.
    | InputsUnnamed of subject: string
    /// A row's value reads more facts than one fact may name.
    | InputsOverCap of subject: string * inputs: int * cap: int
    /// The fact store refused the batch.
    | AssertionFailed of reason: string
    /// A step uses a verb, expression, operator, function or field outside
    /// the deployment's pipeline profile. `kind` says which (`verb`,
    /// `expression`, `operator`, `function`, or `<verb> field`), `name` is its
    /// wire spelling and `admitted` what the profile admits of that kind.
    | OutsideProfile of step: int * kind: string * name: string * admitted: string list
    /// A join step is refused: by the deployment's relation check, or because
    /// it joins a table written into the pipeline.
    | JoinRefused of step: int * reason: string

    static member private Quoted(names: string list) : string =
        match names with
        | [] -> "none"
        | names -> names |> List.map (sprintf "'%s'") |> String.concat ", "

    interface ISpecRefusal with
        member this.Name =
            match this with
            | PipelineRefusal.MalformedArguments _ -> "MalformedArguments"
            | PipelineRefusal.SliceRefused _ -> "SliceRefused"
            | PipelineRefusal.NoFacts _ -> "NoFacts"
            | PipelineRefusal.NothingDisclosable _ -> "NothingDisclosable"
            | PipelineRefusal.MalformedPipeline _ -> "MalformedPipeline"
            | PipelineRefusal.NamedSource _ -> "NamedSource"
            | PipelineRefusal.UnboundParameter _ -> "UnboundParameter"
            | PipelineRefusal.UnpinnedClock _ -> "UnpinnedClock"
            | PipelineRefusal.UnknownColumn _ -> "UnknownColumn"
            | PipelineRefusal.OpenSchema _ -> "OpenSchema"
            | PipelineRefusal.EvaluationFailed _ -> "EvaluationFailed"
            | PipelineRefusal.ResultOverCap _ -> "ResultOverCap"
            | PipelineRefusal.AssertionNotOffered -> "AssertionNotOffered"
            | PipelineRefusal.MalformedAssertion _ -> "MalformedAssertion"
            | PipelineRefusal.AssertedColumnUnknown _ -> "AssertedColumnUnknown"
            | PipelineRefusal.AssertedColumnWithheld _ -> "AssertedColumnWithheld"
            | PipelineRefusal.AssertedColumnNotNumeric _ -> "AssertedColumnNotNumeric"
            | PipelineRefusal.AssertedColumnHostRounded _ -> "AssertedColumnHostRounded"
            | PipelineRefusal.UnregisteredMetric _ -> "UnregisteredMetric"
            | PipelineRefusal.NotADerivedMetric _ -> "NotADerivedMetric"
            | PipelineRefusal.SubjectKeyUnresolved _ -> "SubjectKeyUnresolved"
            | PipelineRefusal.SubjectRepeated _ -> "SubjectRepeated"
            | PipelineRefusal.ValueMissing _ -> "ValueMissing"
            | PipelineRefusal.InputsUnnamed _ -> "InputsUnnamed"
            | PipelineRefusal.InputsOverCap _ -> "InputsOverCap"
            | PipelineRefusal.AssertionFailed _ -> "AssertionFailed"
            | PipelineRefusal.OutsideProfile _ -> "OutsideProfile"
            | PipelineRefusal.JoinRefused _ -> "JoinRefused"

        member this.Remedy =
            match this with
            | PipelineRefusal.MalformedArguments reason ->
                SpecDoorRefusal.describe (SpecDoorRefusal.MalformedArguments reason)
            | PipelineRefusal.SliceRefused reason -> SpecDoorRefusal.describe (SpecDoorRefusal.SliceRefused reason)
            | PipelineRefusal.NoFacts tableId -> SpecDoorRefusal.describe (SpecDoorRefusal.NoFacts tableId)
            | PipelineRefusal.NothingDisclosable(cells, withheld) ->
                SpecDoorRefusal.describe (SpecDoorRefusal.NothingDisclosable(cells, withheld))
            | PipelineRefusal.MalformedPipeline(None, reason) ->
                sprintf "the pipeline is not a JSON array of transform steps: %s" reason
            | PipelineRefusal.MalformedPipeline(Some step, reason) ->
                sprintf "step %d of the pipeline does not decode: %s" step reason
            | PipelineRefusal.NamedSource(step, verb) ->
                sprintf
                    "step %d is a %s, which reads another source; the slice is the only source, so derive what you need from its columns"
                    step
                    verb
            | PipelineRefusal.UnboundParameter(step, names) ->
                sprintf
                    "step %d names the parameter(s) %s, and nothing binds a parameter here; write each value literally"
                    step
                    (PipelineRefusal.Quoted names)
            | PipelineRefusal.UnpinnedClock step ->
                sprintf
                    "step %d reads the clock ('now'), and the evaluation pins none; write the instant literally"
                    step
            | PipelineRefusal.UnknownColumn(step, column, available) ->
                sprintf
                    "step %d reads column '%s', which its input does not have (its columns: %s)"
                    step
                    column
                    (PipelineRefusal.Quoted available)
            | PipelineRefusal.OpenSchema(step, reason) ->
                sprintf
                    "after step %d the result's columns can no longer be named (%s); every output column must be named by the pipeline"
                    step
                    reason
            | PipelineRefusal.EvaluationFailed reason -> sprintf "the pipeline failed over the slice: %s" reason
            | PipelineRefusal.ResultOverCap(cells, cap) ->
                sprintf
                    "the result would hold %d cells, over the cap of %d; end the pipeline with a limit step or keep fewer columns"
                    cells
                    cap
            | PipelineRefusal.AssertionNotOffered ->
                "this deployment does not assert derived figures as facts; evaluate without the 'assert' argument"
            | PipelineRefusal.MalformedAssertion reason -> sprintf "the 'assert' argument is malformed: %s" reason
            | PipelineRefusal.AssertedColumnUnknown(column, available) ->
                sprintf
                    "the result has no column '%s' to assert (its columns: %s)"
                    column
                    (PipelineRefusal.Quoted available)
            | PipelineRefusal.AssertedColumnWithheld(column, withheld) ->
                sprintf
                    "column '%s' is withheld (%s), so its values are partial and none is asserted; narrow the slice to a region with nothing withheld"
                    column
                    (PipelineWithheldCount.Phrase withheld)
            | PipelineRefusal.AssertedColumnNotNumeric(column, columnType) ->
                sprintf "column '%s' holds %s values, and only a number is asserted" column columnType
            | PipelineRefusal.AssertedColumnHostRounded column ->
                sprintf
                    "column '%s' passes through a host-rounded function (exp, ln, log10 or a non-integer power), so two hosts may differ in its last place and it is not one fact"
                    column
            | PipelineRefusal.UnregisteredMetric metric ->
                SpecDoorRefusal.describe (SpecDoorRefusal.UnregisteredMetric metric)
            | PipelineRefusal.NotADerivedMetric(metric, producing) ->
                sprintf
                    "metric '%s' is registered as produced by %s, not by the pipeline operation, so the assistant does not assert it"
                    metric
                    (match producing with
                     | Some operation -> sprintf "'%s'" operation
                     | None -> "no declared operation")
            | PipelineRefusal.SubjectKeyUnresolved reason -> sprintf "the result's rows do not name subjects: %s" reason
            | PipelineRefusal.SubjectRepeated subject ->
                sprintf
                    "two rows of the result name subject '%s'; group by the subject so each row is one subject"
                    subject
            | PipelineRefusal.ValueMissing subject ->
                sprintf "the asserted column has no value for subject '%s'" subject
            | PipelineRefusal.InputsUnnamed subject -> SpecDoorRefusal.describe (SpecDoorRefusal.InputsUnnamed subject)
            | PipelineRefusal.InputsOverCap(subject, inputs, cap) ->
                SpecDoorRefusal.describe (SpecDoorRefusal.InputsOverCap(subject, inputs, cap))
            | PipelineRefusal.AssertionFailed reason ->
                SpecDoorRefusal.describe (SpecDoorRefusal.AssertionFailed reason)
            | PipelineRefusal.OutsideProfile(step, kind, name, admitted) ->
                sprintf
                    "step %d uses the %s '%s', which this deployment's pipeline profile does not admit; the %ss it admits are %s, so rewrite the step with those"
                    step
                    kind
                    name
                    kind
                    (PipelineRefusal.Quoted admitted)
            | PipelineRefusal.JoinRefused(step, reason) ->
                sprintf "step %d is a join this deployment refuses: %s" step reason

        member this.Admissible =
            match this with
            | PipelineRefusal.UnknownColumn(_, _, available)
            | PipelineRefusal.AssertedColumnUnknown(_, available)
            | PipelineRefusal.OutsideProfile(_, _, _, available) -> available
            | _ -> []

/// Reading a `PipelineRefusal`.
[<RequireQualifiedAccess>]
module PipelineRefusal =

    /// Withheld counts, by policy, as one phrase.
    let withheldPhrase (withheld: PipelineWithheldCount list) : string = PipelineWithheldCount.Phrase withheld

    /// The sentence a refusal answers with, naming what to repair. It carries
    /// counts and names, never a value.
    let describe (refusal: PipelineRefusal) : string = (refusal :> ISpecRefusal).Remedy

    /// The refusal's name: its case.
    let name (refusal: PipelineRefusal) : string = (refusal :> ISpecRefusal).Name

    /// What the refusal names as admissible: the columns a step may read, the
    /// columns an assertion may name, or what the profile admits.
    let admissible (refusal: PipelineRefusal) : string list = (refusal :> ISpecRefusal).Admissible

    /// The pipeline refusal a door refusal is: every door case is one of the
    /// pipeline's own, so `evaluate_pipeline` reads exactly as it did before
    /// the door was shared.
    let ofDoor (refusal: SpecDoorRefusal) : PipelineRefusal =
        match refusal with
        | SpecDoorRefusal.MalformedArguments reason -> PipelineRefusal.MalformedArguments reason
        | SpecDoorRefusal.SliceRefused reason -> PipelineRefusal.SliceRefused reason
        | SpecDoorRefusal.NoFacts tableId -> PipelineRefusal.NoFacts tableId
        | SpecDoorRefusal.NothingDisclosable(cells, withheld) -> PipelineRefusal.NothingDisclosable(cells, withheld)
        | SpecDoorRefusal.ResultOverCap(cells, cap) -> PipelineRefusal.ResultOverCap(cells, cap)
        | SpecDoorRefusal.UnregisteredMetric metric -> PipelineRefusal.UnregisteredMetric metric
        | SpecDoorRefusal.NotADerivedMetric(metric, producing, _) ->
            PipelineRefusal.NotADerivedMetric(metric, producing)
        | SpecDoorRefusal.InputsUnnamed subject -> PipelineRefusal.InputsUnnamed subject
        | SpecDoorRefusal.InputsOverCap(subject, inputs, cap) -> PipelineRefusal.InputsOverCap(subject, inputs, cap)
        | SpecDoorRefusal.AssertionFailed reason -> PipelineRefusal.AssertionFailed reason