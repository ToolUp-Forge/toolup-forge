// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open System
open Fuaran.Core
open Fuaran.UI
open Fuaran.UI.Types
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI.Wire
open ToolUp.Facts

// ─── The pipeline block ─────────────────────────────────────────────
//
// `evaluate_pipeline` answers with a block part: the slice spec
// and the pipeline the model wrote, the pipeline's hash, the run stamp the
// slice was read at and the withheld report. Never the rows: a block persists
// its request, and a reader's panel computes the result again under the
// reader's own gate (`IPipelineBlockApi.Evaluate`, served beside the tool by
// `ToolUp.Facts.Presentation.Server`), so every figure drawn was admitted for
// that reader at the moment of reading.
//
// This file is the block's Fable-clean half, compiled by both compilers: the
// request a part persists and its JSON, the re-run's wire result, the remoting
// contract the panel calls, and the grid a result is drawn as. The server
// draws the same grid with no rows as the node a part stores, so the stored
// node names the result's columns and carries no cell value.
//
// **What a reader whose view differs sees.** The re-run is the reader's: a
// reader the gate admits fewer facts to sees more columns withheld, and a run
// committed since the block was written moves the run stamp. `differences`
// names each such change against what the part recorded, and the panel shows
// it beside the grid.

/// The request a pipeline block part persists: the tool's own arguments, as
/// the server read them, with the pipeline in its canonical wire form, and
/// what the evaluation recorded — the pipeline's hash and the run stamp.
type PipelineBlockRequest = {
    /// The fact table the slice reads.
    TableId: string
    /// Start of the slice's period range (inclusive).
    PeriodFrom: DateTime
    /// End of the slice's period range (exclusive).
    PeriodTo: DateTime
    /// Subject path prefix. Empty reads every subject of the table.
    PathPrefix: string list
    /// The metric columns the slice carries. Empty carries every column.
    Metrics: string list
    /// The pipeline as a JSON array of `$type`-tagged steps, in
    /// `Fuaran.Compute`'s canonical wire form.
    Pipeline: string
    /// The pipeline's hash when the block was written.
    PipelineHash: string
    /// The run stamp the slice was read at when the block was written; `None`
    /// when the table had committed no run.
    RunStamp: string option
}

/// One result column the withheld rule withheld: its cells are empty.
type PipelineBlockColumn = {
    /// The result column.
    Column: string
    /// The slice's metrics with a withheld cell that the column's lineage
    /// reads.
    Metrics: string list
    /// Those metrics' withheld cells, by disclosure policy.
    Withheld: BlockWithheld list
}

/// A pipeline evaluated for one reader, under that reader's gate: what the
/// panel draws.
type PipelineBlockResult = {
    /// The result as a `Fuaran.Core.Column` table in its canonical codec. A
    /// withheld column's cells are all null.
    Table: string
    /// The result's row count.
    Rows: int
    /// The result's column names, in order.
    Columns: string list
    /// The columns the withheld rule withheld, in result order.
    Withheld: PipelineBlockColumn list
    /// The pipeline's hash.
    PipelineHash: string
    /// The run stamp of the slice read; `None` when the table had committed
    /// no run.
    RunStamp: string option
    /// The ids of the admitted facts the result's lineage read, in slice
    /// order, capped.
    FactIds: string list
    /// How many such facts there were.
    FactIdCount: int
}

/// The re-run a pipeline block part is drawn from: the persisted request,
/// evaluated for the signed-in reader under the disclosure gate at
/// `FactBrowse`.
type IPipelineBlockApi = {
    /// Evaluate a pipeline block's persisted request (its
    /// `BlockProvenance.Request`) for the caller. Refused, in the evaluation's
    /// own words, when the request is malformed, the slice read refuses it,
    /// or nothing the slice holds may be shown to the caller.
    [<RequiresClaim "scope">]
    Evaluate: string -> Async<Result<PipelineBlockResult, string>>
}

/// The pipeline block: its request's JSON, its provenance, the comparison of a
/// re-run with what the part recorded, and the grid.
[<RequireQualifiedAccess>]
module PipelineBlock =

    /// The `BlockProvenance.Kind` of a pipeline block.
    [<Literal>]
    let Kind = "pipeline"

    /// True when a block part is a pipeline block.
    let isPipeline (provenance: BlockProvenance) : bool = provenance.Kind = Kind

    /// ToolUp.Remoting route builder: `/api/_facts/pipeline-block/<method>`.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_facts/pipeline-block/%s" methodName

    let private member' (name: string) (value: string) =
        JsonHost.serialize (JString name) + ":" + value

    let private text (s: string) = JsonHost.serialize (JString s)

    let private texts (items: string list) =
        JsonHost.serialize (JArray(items |> List.map JString))

    /// The request as the JSON a part persists: the tool's argument shape
    /// (`table`, `period_from`, `period_to`, `path_prefix`, `metrics`,
    /// `pipeline`), so the server re-reads it with the tool's own parser, and
    /// `pipeline_hash` and `run_stamp`, which that parser ignores. The pipeline
    /// is written as it stands, never re-encoded: a number in it keeps every
    /// digit.
    let toJson (request: PipelineBlockRequest) : string =
        [
            member' "table" (text request.TableId)
            member' "period_from" (text (FactBrowseSlice.timestampText request.PeriodFrom))
            member' "period_to" (text (FactBrowseSlice.timestampText request.PeriodTo))
            member' "path_prefix" (texts request.PathPrefix)
            member' "metrics" (texts request.Metrics)
            member' "pipeline" request.Pipeline
            member' "pipeline_hash" (text request.PipelineHash)
            member'
                "run_stamp"
                (match request.RunStamp with
                 | Some stamp -> text stamp
                 | None -> "null")
        ]
        |> String.concat ","
        |> sprintf "{%s}"

    /// The run stamp a persisted request recorded; `None` when it recorded
    /// none or the JSON does not read.
    let recordedRunStamp (requestJson: string) : string option =
        JsonHost.parse requestJson
        |> Option.bind (JsonValue.tryField "run_stamp")
        |> Option.bind JsonValue.asString

    /// The withheld report as a block's `Withheld`: each withheld column's
    /// withheld slice cells, by policy, summed over the withheld columns, in
    /// the order the policies first appear.
    let withheldByPolicy (columns: PipelineBlockColumn list) : BlockWithheld list =
        columns
        |> List.collect _.Withheld
        |> List.fold
            (fun (acc: (string * int) list) w ->
                if acc |> List.exists (fun (p, _) -> p = w.PolicyRef) then
                    acc |> List.map (fun (p, c) -> if p = w.PolicyRef then p, c + w.Count else p, c)
                else
                    acc @ [ w.PolicyRef, w.Count ])
            []
        |> List.map (fun (p, c) -> ({ PolicyRef = p; Count = c }: BlockWithheld))

    /// The slice's period range in its canonical rendering.
    let periodText (request: PipelineBlockRequest) : string =
        FactBrowseSlice.timestampText request.PeriodFrom
        + "/"
        + FactBrowseSlice.timestampText request.PeriodTo

    /// The provenance a pipeline block part carries: the persisted request,
    /// the fact ids the result's lineage read, the withheld report, the table,
    /// the metrics the slice named (`""` for all) and the period.
    let provenance (request: PipelineBlockRequest) (result: PipelineBlockResult) : BlockProvenance = {
        Kind = Kind
        Request = toJson request
        FactIds = result.FactIds
        Withheld = withheldByPolicy result.Withheld
        TableId = request.TableId
        Metric = String.concat "," request.Metrics
        Period = periodText request
    }

    let private plural (n: int) (one: string) (many: string) = if n = 1 then one else many

    /// How a reader's re-run differs from what the part recorded: a run
    /// committed since, columns withheld now that were not then (or the
    /// reverse), and facts read now that were not then (or the reverse). Empty
    /// when the reader sees what the writer saw.
    let differences (recorded: BlockProvenance) (now: PipelineBlockResult) : string list =
        let recordedWithheld = recorded.Withheld |> List.sumBy _.Count
        let nowWithheld = withheldByPolicy now.Withheld |> List.sumBy _.Count
        let stamp = recordedRunStamp recorded.Request
        let recordedFacts = Set.ofList recorded.FactIds
        let nowFacts = Set.ofList now.FactIds

        [
            if stamp <> now.RunStamp then
                "A run has been committed to the table since this result was written, so it is computed over the newer facts."
            if nowWithheld > recordedWithheld then
                sprintf
                    "More is withheld from you than from the writer: %d withheld %s read now, against %d when this result was written."
                    nowWithheld
                    (plural nowWithheld "cell" "cells")
                    recordedWithheld
            elif nowWithheld < recordedWithheld then
                sprintf
                    "Less is withheld from you than from the writer: %d withheld %s read now, against %d when this result was written."
                    nowWithheld
                    (plural nowWithheld "cell" "cells")
                    recordedWithheld
            if recordedFacts <> nowFacts then
                let gone = Set.difference recordedFacts nowFacts |> Set.count
                let added = Set.difference nowFacts recordedFacts |> Set.count

                sprintf
                    "It reads different facts for you: %d of the facts it was written with %s not read now, and %d %s read now that %s not."
                    gone
                    (plural gone "is" "are")
                    added
                    (plural added "is" "are")
                    (plural added "was" "were")
        ]

    /// A cell's text in the grid: its canonical text; an absent cell is empty.
    let cellText (cell: Cell) : string =
        match cell with
        | Cell.Int i -> string i
        | Cell.Float f -> string f
        | Cell.Bool b -> if b then "true" else "false"
        | Cell.Str s -> s
        | Cell.Date d -> d
        | Cell.Timestamp t -> t
        | Cell.Decimal d -> d
        | Cell.Null -> ""

    /// The withheld note for one column.
    let withheldText (column: PipelineBlockColumn) : string =
        let count = column.Withheld |> List.sumBy _.Count

        sprintf
            "'%s' is withheld: it reads %s, with %d %s withheld (%s)."
            column.Column
            (String.concat ", " column.Metrics)
            count
            (plural count "cell" "cells")
            (column.Withheld
             |> List.map (fun w -> sprintf "%d under %s" w.Count w.PolicyRef)
             |> String.concat ", ")

    let private idSegment (s: string) : string =
        s
        |> String.map (fun c ->
            if Char.IsLetterOrDigit c then
                Char.ToLowerInvariant c
            else
                '-')

    let private grid<'Msg>
        (provenance: BlockProvenance)
        (result: PipelineBlockResult)
        (rows: string list list)
        : Result<PresentationBlock<'Msg>, PresentationDefect> =
        let id =
            "pipeline-"
            + idSegment (
                if result.PipelineHash.Length > 12 then
                    result.PipelineHash.Substring(0, 12)
                else
                    result.PipelineHash
            )

        let title = sprintf "%s, %s" provenance.TableId provenance.Period

        let notes = result.Withheld |> List.map withheldText

        let node =
            Fuaran.box id {
                Defaults.box with
                    Children = [
                        Fuaran.heading (id + "-title") {
                            Defaults.heading with
                                Level = 3
                                Text = TextSource.Literal title
                        }
                        Fuaran.table (id + "-grid") {
                            Defaults.table with
                                Headers = result.Columns |> List.map TextSource.Literal
                                Rows = rows |> List.map (List.map TextSource.Literal)
                        }
                        if not (List.isEmpty notes) then
                            Fuaran.list (id + "-notes") notes
                    ]
            }

        match Presentation.findings node with
        | [] ->
            Ok {
                Node = node
                Provenance = provenance
                Notes = notes
            }
        | findings -> Error(PresentationDefect.NodeRefused findings)

    /// The result's rows as grid text, row by row, in result column order.
    let rowsOf (result: PipelineBlockResult) : Result<string list list, string> =
        match ColumnCodec.decode result.Table with
        | Ok(Embedded table) ->
            let columns =
                result.Columns
                |> List.map (fun name ->
                    table
                    |> Table.tryColumn name
                    |> Option.map Column.toCells
                    |> Option.defaultValue (List.replicate (Table.rowCount table) Cell.Null))

            Ok(
                [ 0 .. Table.rowCount table - 1 ]
                |> List.map (fun i -> columns |> List.map (fun cells -> cellText cells.[i]))
            )
        | Ok(Ref name) -> Error(sprintf "the result names a source '%s' rather than carrying its rows" name)
        | Error e -> Error("the result could not be decoded: " + ColumnCodec.errorString e)

    /// The block a part stores: the grid's title and columns and the withheld
    /// notes, with no row — so the stored node carries no cell value.
    let frame<'Msg>
        (request: PipelineBlockRequest)
        (result: PipelineBlockResult)
        : Result<PresentationBlock<'Msg>, PresentationDefect> =
        grid (provenance request result) result []

    /// The block a reader sees for a part that recorded `recorded`: the
    /// reader's result drawn as a grid, a withheld column's cells empty and
    /// named in the notes. The provenance keeps the part's request verbatim
    /// and carries the reader's fact ids and withheld report.
    let redraw<'Msg>
        (recorded: BlockProvenance)
        (result: PipelineBlockResult)
        : Result<PresentationBlock<'Msg>, PresentationDefect> =
        match rowsOf result with
        | Error reason -> Error(PresentationDefect.AnswersMismatch reason)
        | Ok rows ->
            grid
                {
                    recorded with
                        FactIds = result.FactIds
                        Withheld = withheldByPolicy result.Withheld
                }
                result
                rows