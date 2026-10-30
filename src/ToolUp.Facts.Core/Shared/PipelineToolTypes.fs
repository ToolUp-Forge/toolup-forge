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

/// Withheld cells an output column read, counted by the disclosure policy
/// that withheld them.
type PipelineWithheldCount = {
    /// The disclosure policy that withheld the cells.
    PolicyRef: string
    /// How many cells it withheld.
    Count: int
}

/// One output column the withheld rule withheld: its cells are empty in the
/// result table.
type PipelineWithheldColumn = {
    /// The output column.
    Column: string
    /// The slice's metrics, each with at least one withheld cell, that the
    /// column's lineage reads.
    Metrics: string list
    /// Those metrics' withheld cells, by policy.
    Withheld: PipelineWithheldCount list
}

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

/// Reading a `PipelineRefusal`.
[<RequireQualifiedAccess>]
module PipelineRefusal =

    let private quoted (names: string list) : string =
        match names with
        | [] -> "none"
        | names -> names |> List.map (sprintf "'%s'") |> String.concat ", "

    /// Withheld counts, by policy, as one phrase.
    let withheldPhrase (withheld: PipelineWithheldCount list) : string =
        match withheld with
        | [] -> "none withheld"
        | [ w ] -> sprintf "%d withheld under %s" w.Count w.PolicyRef
        | many ->
            sprintf
                "%d withheld (%s)"
                (many |> List.sumBy _.Count)
                (many
                 |> List.map (fun w -> sprintf "%d under %s" w.Count w.PolicyRef)
                 |> String.concat ", ")

    /// The sentence a refusal answers with, naming what to repair. It carries
    /// counts and names, never a value.
    let describe (refusal: PipelineRefusal) : string =
        match refusal with
        | PipelineRefusal.MalformedArguments reason -> reason
        | PipelineRefusal.SliceRefused reason -> sprintf "the slice read refused it: %s" reason
        | PipelineRefusal.NoFacts tableId ->
            sprintf "no fact of table '%s' answers the slice; widen the period range or the subject prefix" tableId
        | PipelineRefusal.NothingDisclosable(cells, withheld) ->
            sprintf
                "no cell of the slice carries a value you may be shown: %d cell(s) read, %s"
                cells
                (withheldPhrase withheld)
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
                (quoted names)
        | PipelineRefusal.UnpinnedClock step ->
            sprintf "step %d reads the clock ('now'), and the evaluation pins none; write the instant literally" step
        | PipelineRefusal.UnknownColumn(step, column, available) ->
            sprintf
                "step %d reads column '%s', which its input does not have (its columns: %s)"
                step
                column
                (quoted available)
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
            sprintf "the result has no column '%s' to assert (its columns: %s)" column (quoted available)
        | PipelineRefusal.AssertedColumnWithheld(column, withheld) ->
            sprintf
                "column '%s' is withheld (%s), so its values are partial and none is asserted; narrow the slice to a region with nothing withheld"
                column
                (withheldPhrase withheld)
        | PipelineRefusal.AssertedColumnNotNumeric(column, columnType) ->
            sprintf "column '%s' holds %s values, and only a number is asserted" column columnType
        | PipelineRefusal.AssertedColumnHostRounded column ->
            sprintf
                "column '%s' passes through a host-rounded function (exp, ln, log10 or a non-integer power), so two hosts may differ in its last place and it is not one fact"
                column
        | PipelineRefusal.UnregisteredMetric metric ->
            sprintf
                "metric '%s' is not registered; a derived metric is registered, with its display format and roll-up, before it is asserted"
                metric
        | PipelineRefusal.NotADerivedMetric(metric, producing) ->
            sprintf
                "metric '%s' is registered as produced by %s, not by the pipeline operation, so the assistant does not assert it"
                metric
                (match producing with
                 | Some operation -> sprintf "'%s'" operation
                 | None -> "no declared operation")
        | PipelineRefusal.SubjectKeyUnresolved reason -> sprintf "the result's rows do not name subjects: %s" reason
        | PipelineRefusal.SubjectRepeated subject ->
            sprintf "two rows of the result name subject '%s'; group by the subject so each row is one subject" subject
        | PipelineRefusal.ValueMissing subject -> sprintf "the asserted column has no value for subject '%s'" subject
        | PipelineRefusal.InputsUnnamed subject ->
            sprintf
                "the value for subject '%s' reads no fact of the slice, so its inputs cannot be named and it is not asserted"
                subject
        | PipelineRefusal.InputsOverCap(subject, inputs, cap) ->
            sprintf
                "the value for subject '%s' reads %d facts, over the %d one fact may name; narrow the slice"
                subject
                inputs
                cap
        | PipelineRefusal.AssertionFailed reason -> sprintf "the fact store refused the assertion: %s" reason