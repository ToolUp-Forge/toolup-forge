// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 218 — default `IMappingDryRunValidator`. Validates an
/// already-mapped (canonical-header) CSV against the platform's coarse
/// `DataTypeSchema`, returning a per-row / per-cell `DryRunReport` as
/// data (GP 12.3) — never throwing on a bad row, never touching
/// `DataType.Process`.
///
/// BCL only — no vendor dependency reaches `ToolUp.Platform.*` (GP 1).
/// The coarse schema carries only `String / Number / Date / Boolean`
/// + `Required`, so the checks are: an empty cell in a Required column
/// is a `required value missing` issue; a non-empty cell that doesn't
/// parse as its declared type is a type issue. A deployment wanting
/// constraint / pattern / choice validation composes a richer
/// `IMappingDryRunValidator` (e.g. `ToolUp.Tabular`-backed) over the
/// same seam.
module ToolUp.Platform.MappingDryRunValidator

open System
open System.Globalization
open System.Text
open DataManagementTypes
open ColumnMappingTypes

/// Cap on the sampled issue lists carried over the wire — the counts
/// (`TotalRows` / `PassedRows` / `FailedRows`) stay exact; only the
/// "here's why and where" detail is bounded so a pathological file
/// can't produce a giant payload.
[<Literal>]
let private SampleCap = 50

/// Quote-aware split of one canonical CSV record into fields (RFC-4180
/// `""` escape). Kept local so the validator stays BCL-only and
/// self-contained; the canonical CSV it reads was written with the same
/// quoting convention by `ColumnMapping.rewriteCsv`.
let private splitCsvLine (line: string) : string[] =
    let result = ResizeArray<string>()
    let sb = StringBuilder()
    let mutable inQuotes = false
    let mutable i = 0

    while i < line.Length do
        let c = line[i]

        if inQuotes then
            if c = '"' then
                if i + 1 < line.Length && line[i + 1] = '"' then
                    sb.Append '"' |> ignore
                    i <- i + 1
                else
                    inQuotes <- false
            else
                sb.Append c |> ignore
        elif c = '"' then
            inQuotes <- true
        elif c = ',' then
            result.Add(sb.ToString())
            sb.Clear() |> ignore
        else
            sb.Append c |> ignore

        i <- i + 1

    result.Add(sb.ToString())
    result.ToArray()

let private boolSpellings =
    set [ "true"; "false"; "yes"; "no"; "y"; "n"; "t"; "f"; "1"; "0" ]

/// Whether a non-empty cell value parses as its declared coarse type.
let private parsesAs (columnType: ColumnType) (value: string) : bool =
    let v = value.Trim()

    match columnType with
    | StringColumn -> true
    | NumberColumn ->
        match Double.TryParse(v, NumberStyles.Float ||| NumberStyles.AllowThousands, CultureInfo.InvariantCulture) with
        | true, _ -> true
        | _ -> false
    | DateColumn ->
        match DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, _ -> true
        | _ -> false
    | BooleanColumn -> boolSpellings.Contains(v.ToLowerInvariant())

/// The commit-blocked verdict for a report under a dry-run policy.
/// `WarnOnValidationFailure` never blocks (GP 11); `BlockOnValidationFailure`
/// blocks when any row would fail (cell-level or structural).
let commitBlocked (policy: MappingDryRunPolicy) (report: DryRunReport) : bool =
    match policy with
    | WarnOnValidationFailure -> false
    | BlockOnValidationFailure -> report.FailedRows > 0 || not report.RowIssues.IsEmpty

let private expectedText (columnType: ColumnType) : string =
    match columnType with
    | StringColumn -> "text"
    | NumberColumn -> "a number"
    | DateColumn -> "a date"
    | BooleanColumn -> "a boolean (true/false, yes/no, 1/0)"

/// The default coarse validator. Pure + stateless: every call reads only
/// its arguments, so it satisfies the portability stateless-handler rule.
let create () : IMappingDryRunValidator =
    { new IMappingDryRunValidator with
        member _.Validate(schema, mappedCsv) =
            // The canonical CSV emits one column per *mapped* schema
            // field (others were dropped by `rewriteCsv`); the header is
            // the schema field name. Split on any newline flavour.
            let lines = mappedCsv.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)

            // No header row at all → nothing to validate (an empty source
            // produced an empty rewrite). Report a clean zero-row result.
            if lines.Length = 0 || lines[0].Trim() = "" then
                {
                    // TargetTypeId + CommitBlocked are stamped by the handler.
                    TargetTypeId = ""
                    TotalRows = 0
                    PassedRows = 0
                    FailedRows = 0
                    CellIssues = []
                    RowIssues = []
                    Truncated = false
                    CommitBlocked = false
                }
            else
                let header = splitCsvLine lines[0]

                let indexOf (name: string) =
                    let target = name.Trim().ToLowerInvariant()
                    header |> Array.tryFindIndex (fun h -> h.Trim().ToLowerInvariant() = target)

                // Pre-resolve each schema column's position once. A Required
                // column absent from the canonical header (mapped away) is a
                // single structural issue rather than a per-row flood — the
                // wizard blocks unmapped required fields upstream, so this is
                // a defensive belt.
                let positioned = schema.Columns |> List.map (fun col -> col, indexOf col.Name)

                let missingRequired =
                    positioned
                    |> List.choose (fun (col, idx) ->
                        if idx.IsNone && col.Required then
                            Some {
                                Row = 0
                                Detail = sprintf "Required field '%s' is not mapped to any column." col.Name
                            }
                        else
                            None)

                let present =
                    positioned
                    |> List.choose (fun (col, idx) -> idx |> Option.map (fun i -> col, i))

                let cellIssues = ResizeArray<DryRunCellIssue>()
                let mutable cellIssueCount = 0
                let mutable totalRows = 0
                let mutable failedRows = 0

                for lineIdx in 1 .. lines.Length - 1 do
                    let line = lines[lineIdx]

                    if line.Trim() <> "" then
                        totalRows <- totalRows + 1
                        let cells = splitCsvLine line
                        let mutable rowFailed = false

                        for col, i in present do
                            let raw = if i < cells.Length then cells[i] else ""

                            let issue =
                                if String.IsNullOrWhiteSpace raw then
                                    if col.Required then
                                        Some(expectedText col.Type, "required value missing")
                                    else
                                        None
                                elif not (parsesAs col.Type raw) then
                                    Some(expectedText col.Type, null)
                                else
                                    None

                            match issue with
                            | Some(expected, violation) ->
                                rowFailed <- true
                                cellIssueCount <- cellIssueCount + 1

                                if cellIssues.Count < SampleCap then
                                    cellIssues.Add {
                                        // canonical header is row 1, first data row is row 2
                                        Row = lineIdx + 1
                                        Column = col.Name
                                        Expected = expected
                                        Actual = raw
                                        Violation = (if isNull violation then None else Some violation)
                                    }
                            | None -> ()

                        if rowFailed then
                            failedRows <- failedRows + 1

                {
                    TargetTypeId = ""
                    TotalRows = totalRows
                    PassedRows = totalRows - failedRows
                    FailedRows = failedRows
                    CellIssues = List.ofSeq cellIssues
                    RowIssues = missingRequired
                    Truncated = cellIssueCount > cellIssues.Count
                    CommitBlocked = false
                }
    }