// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System

// ─── evaluate_pipeline (Phase 1145) ──────────────────────────────────
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