// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System

// ─── Spec in, Facts out ──────────────────────────────────────────────
//
// A "spec tool" is an AI tool whose argument is a closed, hashable
// specification the model writes (a pipeline, an analysis spec, an
// authorised calculation) over one gated slice of one fact table. The server
// decodes and validates the spec before anything is read, refuses by name,
// evaluates it over the slice the disclosure gate admitted, returns the
// result table to the user without the model retyping it, and can assert
// result rows that name a subject, a metric and a period as `Computed` facts
// whose method names the spec's hash.
//
// These are the seam's Fable-safe vocabulary: the refusal contract every spec
// tool's refusal type implements, the door's own refusals, and what an
// evaluation answers. The builder over them is `SpecTool` in
// `ToolUp.Facts.Server`.

/// A refusal a spec tool answers with, read the same way whichever tool
/// refused: the model repairs its call from these, by name.
type ISpecRefusal =
    /// The refusal's name: a stable identifier of its cause.
    abstract Name: string
    /// The sentence the model reads, naming what to repair. It carries counts
    /// and names, never a value.
    abstract Remedy: string
    /// What would have been admitted where the refusal names a closed set (the
    /// columns a step may read, the verbs a profile admits); empty otherwise.
    abstract Admissible: string list

/// Withheld cells an output column read, counted by the disclosure policy
/// that withheld them. Named for `evaluate_pipeline`, its first reader; every
/// spec tool's withheld report uses it.
type PipelineWithheldCount = {
    /// The disclosure policy that withheld the cells.
    PolicyRef: string
    /// How many cells it withheld.
    Count: int
} with

    /// Withheld counts, by policy, as one phrase.
    static member Phrase(withheld: PipelineWithheldCount list) : string =
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

/// One result row a spec declares as a fact: the subject, metric and period
/// it is of, its value, and the admitted facts it was computed from. The
/// assertion door mints it with the spec tool's method; a row that names
/// nothing to assert is never written.
type SpecFactRow = {
    /// The subject the row is of.
    Subject: SubjectRef
    /// The registered derived metric the value is a fact of.
    Metric: string
    /// The period the value is of.
    Period: TemporalExtent
    /// The value.
    Value: decimal
    /// The ids of the admitted facts the value was computed from.
    Inputs: string list
}

/// What a spec tool's evaluation answers, as the door reads it. The table is
/// the evaluator's, over cells the gate admitted, and reaches the user, never
/// the model.
type SpecResult = {
    /// The result as a `Fuaran.Core.Column` table in its canonical codec.
    Table: string
    /// The result's row count.
    Rows: int
    /// The result's column names, in order.
    Columns: string list
    /// The columns withheld because their lineage reads a withheld cell.
    Withheld: PipelineWithheldColumn list
    /// The rows the spec declares as facts. Empty: the result is a block
    /// only, and no fact is asserted from it.
    Facts: SpecFactRow list
}

/// The refusals the spec-tool door decides itself, whichever spec it serves.
[<RequireQualifiedAccess>]
type SpecDoorRefusal =
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
    /// The result would hold more cells than the cap.
    | ResultOverCap of cells: int * cap: int
    /// A row asserts a metric that is not registered.
    | UnregisteredMetric of metric: string
    /// A row asserts a metric registered as produced by another operation.
    | NotADerivedMetric of metric: string * producingOperation: string option * operation: string
    /// A row's value reads no admitted fact, so its inputs cannot be named.
    | InputsUnnamed of subject: string
    /// A row's value reads more facts than one fact may name.
    | InputsOverCap of subject: string * inputs: int * cap: int
    /// The fact store refused the batch.
    | AssertionFailed of reason: string

    interface ISpecRefusal with
        member this.Name =
            match this with
            | SpecDoorRefusal.MalformedArguments _ -> "MalformedArguments"
            | SpecDoorRefusal.SliceRefused _ -> "SliceRefused"
            | SpecDoorRefusal.NoFacts _ -> "NoFacts"
            | SpecDoorRefusal.NothingDisclosable _ -> "NothingDisclosable"
            | SpecDoorRefusal.ResultOverCap _ -> "ResultOverCap"
            | SpecDoorRefusal.UnregisteredMetric _ -> "UnregisteredMetric"
            | SpecDoorRefusal.NotADerivedMetric _ -> "NotADerivedMetric"
            | SpecDoorRefusal.InputsUnnamed _ -> "InputsUnnamed"
            | SpecDoorRefusal.InputsOverCap _ -> "InputsOverCap"
            | SpecDoorRefusal.AssertionFailed _ -> "AssertionFailed"

        member this.Remedy =
            match this with
            | SpecDoorRefusal.MalformedArguments reason -> reason
            | SpecDoorRefusal.SliceRefused reason -> sprintf "the slice read refused it: %s" reason
            | SpecDoorRefusal.NoFacts tableId ->
                sprintf "no fact of table '%s' answers the slice; widen the period range or the subject prefix" tableId
            | SpecDoorRefusal.NothingDisclosable(cells, withheld) ->
                sprintf
                    "no cell of the slice carries a value you may be shown: %d cell(s) read, %s"
                    cells
                    (PipelineWithheldCount.Phrase withheld)
            | SpecDoorRefusal.ResultOverCap(cells, cap) ->
                sprintf
                    "the result would hold %d cells, over the cap of %d; narrow the request so the result keeps fewer rows or columns"
                    cells
                    cap
            | SpecDoorRefusal.UnregisteredMetric metric ->
                sprintf
                    "metric '%s' is not registered; a derived metric is registered, with its display format and roll-up, before it is asserted"
                    metric
            | SpecDoorRefusal.NotADerivedMetric(metric, producing, operation) ->
                sprintf
                    "metric '%s' is registered as produced by %s, not by the operation '%s', so the assistant does not assert it"
                    metric
                    (match producing with
                     | Some p -> sprintf "'%s'" p
                     | None -> "no declared operation")
                    operation
            | SpecDoorRefusal.InputsUnnamed subject ->
                sprintf
                    "the value for subject '%s' reads no fact of the slice, so its inputs cannot be named and it is not asserted"
                    subject
            | SpecDoorRefusal.InputsOverCap(subject, inputs, cap) ->
                sprintf
                    "the value for subject '%s' reads %d facts, over the %d one fact may name; narrow the slice"
                    subject
                    inputs
                    cap
            | SpecDoorRefusal.AssertionFailed reason -> sprintf "the fact store refused the assertion: %s" reason

        member _.Admissible = []

/// Reading a `SpecDoorRefusal`.
[<RequireQualifiedAccess>]
module SpecDoorRefusal =

    /// The sentence a door refusal answers with, naming what to repair.
    let describe (refusal: SpecDoorRefusal) : string = (refusal :> ISpecRefusal).Remedy

    /// The refusal's name.
    let name (refusal: SpecDoorRefusal) : string = (refusal :> ISpecRefusal).Name