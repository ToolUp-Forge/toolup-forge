// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Pure, Fable-safe engine for the CSV column-mapping Data Manager:
/// header fingerprinting, column type inference, fuzzy field→column
/// suggestion, and CSV rewrite to a target schema's canonical shape.
///
/// Everything here is deterministic and dependency-free so it compiles
/// to the client (the wizard runs `parsePreview` + `suggest` + `rewriteCsv`
/// in the browser) and is exhaustively unit-testable server-side.
/// No `System.Net`, no I/O, no .NET-only BCL surface that Fable can't
/// transpile.
module ColumnMapping

open System
open DataManagementTypes
open ColumnMappingTypes

// ─── Minimal CSV parsing / writing (RFC-4180-ish, quote-aware) ────

/// Split one CSV record into fields, honouring double-quoted fields and
/// `""` escapes. Deliberately small — the SDK's data path is CSV-first
/// and this stays Fable-transpilable.
let private splitCsvLine (line: string) : string list =
    let result = System.Collections.Generic.List<string>()
    let sb = System.Text.StringBuilder()
    let mutable inQuotes = false
    let mutable i = 0

    while i < line.Length do
        let c = line[i]

        if inQuotes then
            if c = '"' then
                if i + 1 < line.Length && line[i + 1] = '"' then
                    sb.Append('"') |> ignore
                    i <- i + 1
                else
                    inQuotes <- false
            else
                sb.Append(c) |> ignore
        else if c = '"' then
            inQuotes <- true
        elif c = ',' then
            result.Add(sb.ToString())
            sb.Clear() |> ignore
        else
            sb.Append(c) |> ignore

        i <- i + 1

    result.Add(sb.ToString())
    result |> List.ofSeq

/// Quote a field for output when it contains a comma, quote, or newline.
let private writeCell (s: string) : string =
    if s.Contains "," || s.Contains "\"" || s.Contains "\n" then
        "\"" + s.Replace("\"", "\"\"") + "\""
    else
        s

let private splitLines (raw: string) (opts: StringSplitOptions) : string list =
    raw.Split([| "\r\n"; "\n"; "\r" |], opts) |> Array.toList

// ─── Name similarity ──────────────────────────────────────────────

/// Lower-case, collapse separators (`_ - . space`) to single spaces,
/// drop other punctuation. The normal form for both name comparison
/// and token extraction.
let private normalize (s: string) : string =
    let lowered = s.Trim().ToLower()

    let mapped =
        lowered
        |> String.collect (fun c ->
            if Char.IsLetterOrDigit c then string c
            elif c = ' ' || c = '_' || c = '-' || c = '.' then " "
            else "")

    // collapse runs of spaces
    mapped.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
    |> String.concat " "

let private tokenSet (s: string) : Set<string> =
    (normalize s).Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
    |> Set.ofArray

let private levenshtein (a: string) (b: string) : int =
    let n = a.Length
    let m = b.Length

    if n = 0 then
        m
    elif m = 0 then
        n
    else
        let prev = Array.init (m + 1) id
        let curr = Array.zeroCreate (m + 1)

        for i in 1..n do
            curr[0] <- i

            for j in 1..m do
                let cost = if a[i - 1] = b[j - 1] then 0 else 1
                curr[j] <- min (min (curr[j - 1] + 1) (prev[j] + 1)) (prev[j - 1] + cost)

            Array.blit curr 0 prev 0 (m + 1)

        prev[m]

/// Similarity of two column names in [0.0, 1.0]. Exact normalised match
/// short-circuits to 1.0; otherwise the best of token-set Jaccard,
/// Levenshtein ratio, and substring containment.
let nameSimilarity (a: string) (b: string) : float =
    let na = normalize a
    let nb = normalize b

    if na = nb && na <> "" then
        1.0
    elif na = "" || nb = "" then
        0.0
    else
        let ta = tokenSet a
        let tb = tokenSet b

        let jaccard =
            let inter = Set.intersect ta tb |> Set.count |> float
            let union = Set.union ta tb |> Set.count |> float
            if union = 0.0 then 0.0 else inter / union

        let lev =
            let d = levenshtein na nb |> float
            let maxLen = float (max na.Length nb.Length)
            if maxLen = 0.0 then 0.0 else 1.0 - (d / maxLen)

        let substr = if na.Contains nb || nb.Contains na then 0.85 else 0.0

        [ jaccard; lev; substr ] |> List.max

// ─── Column type inference ────────────────────────────────────────

let private isNumeric (s: string) : bool =
    match Double.TryParse s with
    | true, _ -> true
    | _ -> false

let private isDate (s: string) : bool =
    match DateTime.TryParse s with
    | true, _ -> true
    | _ -> false

let private boolTokens = set [ "true"; "false"; "yes"; "no" ]

/// Infer a column's `ColumnType` from its sample cell values. Blank
/// cells are ignored; an all-blank / empty sample falls back to
/// `StringColumn`. Booleans are recognised only from the explicit token
/// set (not `0`/`1`) so numeric columns aren't mis-typed.
let inferColumnType (cells: string list) : ColumnType =
    let values = cells |> List.map (fun c -> c.Trim()) |> List.filter (fun c -> c <> "")

    match values with
    | [] -> StringColumn
    | vs ->
        let all pred = vs |> List.forall pred

        if all (fun v -> boolTokens.Contains(v.ToLower())) then
            BooleanColumn
        elif all isNumeric then
            NumberColumn
        elif all isDate then
            DateColumn
        else
            StringColumn

/// Whether a CSV column's sample data is acceptable for a declared
/// field type. A `String` field accepts anything. For a typed field, an
/// *empty* sample can't contradict the field (lenient — we don't know),
/// but a non-empty sample must infer to the same type — so genuine text
/// where a number/date/boolean is expected is a real mismatch.
let private typeAcceptable (expected: ColumnType) (cells: string list) : bool =
    let inferred = inferColumnType cells
    let hasValues = cells |> List.exists (fun c -> c.Trim() <> "")

    match expected with
    | StringColumn -> true
    | _ when not hasValues -> true
    | e -> e = inferred

// ─── Fingerprint ──────────────────────────────────────────────────

module Fingerprint =
    /// Order-independent column-structure key: normalise (trim + lower),
    /// drop blanks, sort, join with `|`. Two CSVs with the same set of
    /// header names — regardless of column order — share a fingerprint,
    /// so a saved mapping is reused.
    let ofHeaders (headers: string list) : string =
        headers
        |> List.map (fun h -> h.Trim().ToLower())
        |> List.filter (fun h -> h <> "")
        |> List.sort
        |> String.concat "|"

// ─── Preview extraction ───────────────────────────────────────────

/// Pull the header row and up to `sampleSize` rows of per-column sample
/// values from raw CSV text. The wizard feeds the samples into
/// `suggest` for type inference. Single tested parse site shared by
/// client and server.
let parsePreview (sampleSize: int) (rawCsv: string) : string list * Map<string, string list> =
    match splitLines rawCsv StringSplitOptions.RemoveEmptyEntries with
    | [] -> [], Map.empty
    | header :: rows ->
        let headers = splitCsvLine header |> List.map (fun h -> h.Trim())
        let sampleRows = rows |> List.truncate sampleSize |> List.map splitCsvLine

        let samples =
            headers
            |> List.mapi (fun i h ->
                let col =
                    sampleRows |> List.map (fun cells -> if i < cells.Length then cells[i] else "")

                h, col)
            |> Map.ofList

        headers, samples

// ─── Suggestion engine ────────────────────────────────────────────

[<Literal>]
let private ConfidentThreshold = 0.72

[<Literal>]
let private UnmatchedThreshold = 0.4

[<Literal>]
let private AmbiguityDelta = 0.08

/// Auto-map a target schema's fields to a CSV's columns. For each field,
/// score every header by name similarity weighted by type compatibility,
/// pick the best, and classify the match (`Confident` / `LowConfidence`
/// / `TypeMismatch` / `Ambiguous` / `Unmatched`). Pure — the wizard
/// renders the result and lets the user override before confirming.
let suggest
    (targetTypeId: DataTypeId)
    (schema: DataTypeSchema)
    (headers: string list)
    (samples: Map<string, string list>)
    : MappingSuggestion =

    let fields =
        schema.Columns
        |> List.map (fun field ->
            // Score is the *name* confidence. Type compatibility is a
            // selection tiebreaker (a type-compatible header wins a name
            // tie) and a flag — never folded into the score, so a strong
            // name match against a wrong-typed column stays high enough
            // to surface as `TypeMismatch` rather than being suppressed
            // to `LowConfidence`.
            let scored =
                headers
                |> List.map (fun h ->
                    let nameScore = nameSimilarity field.Name h
                    let cells = samples |> Map.tryFind h |> Option.defaultValue []
                    let typeOk = typeAcceptable field.Type cells
                    let selection = nameScore + (if typeOk then 0.0 else -0.15)

                    {|
                        Header = h
                        Name = nameScore
                        TypeOk = typeOk
                        Selection = selection
                    |})
                |> List.sortByDescending _.Selection

            match scored with
            | [] -> {
                Field = field
                SuggestedColumn = None
                Score = 0.0
                Flag = (if field.Required then Unmatched else LowConfidence)
                Alternatives = []
              }
            | best :: rest ->
                let secondName =
                    rest |> List.tryHead |> Option.map _.Name |> Option.defaultValue 0.0

                let alternatives = rest |> List.truncate 3 |> List.map _.Header

                let flag =
                    if best.Name < ConfidentThreshold then
                        if field.Required && best.Name < UnmatchedThreshold then
                            Unmatched
                        else
                            LowConfidence
                    elif not best.TypeOk then
                        TypeMismatch
                    elif best.Name - secondName < AmbiguityDelta && secondName >= ConfidentThreshold then
                        Ambiguous
                    else
                        Confident

                {
                    Field = field
                    SuggestedColumn = (if flag = Unmatched then None else Some best.Header)
                    Score = best.Name
                    Flag = flag
                    Alternatives = alternatives
                })

    {
        TargetTypeId = targetTypeId
        Fields = fields
        Fingerprint = Fingerprint.ofHeaders headers
    }

// ─── CSV rewrite ──────────────────────────────────────────────────

/// Rewrite the raw CSV into the target schema's canonical shape: header
/// row = schema field names (in schema order), one column per mapped
/// field, body cells pulled from the mapped source column. Columns the
/// mapping doesn't cover are dropped — "minimally, just the required
/// data". The result is fed to the existing `DataType.Process` for the
/// chosen target type.
let rewriteCsv (schema: DataTypeSchema) (mapping: Map<string, string>) (rawCsv: string) : string =
    match splitLines rawCsv StringSplitOptions.None with
    | [] -> ""
    | header :: rows ->
        let srcHeaders = splitCsvLine header

        let srcIndex (name: string) =
            let target = name.Trim().ToLower()
            srcHeaders |> List.tryFindIndex (fun h -> h.Trim().ToLower() = target)

        // schema columns that have a mapping AND resolve to a real source column
        let emitted =
            schema.Columns
            |> List.choose (fun col ->
                match mapping |> Map.tryFind col.Name with
                | Some src -> srcIndex src |> Option.map (fun idx -> col.Name, idx)
                | None -> None)

        let outHeader = emitted |> List.map (fst >> writeCell) |> String.concat ","

        let outRows =
            rows
            |> List.filter (fun r -> r.Trim() <> "")
            |> List.map (fun r ->
                let cells = splitCsvLine r

                emitted
                |> List.map (fun (_, idx) -> (if idx < cells.Length then cells[idx] else "") |> writeCell)
                |> String.concat ",")

        String.concat "\n" (outHeader :: outRows)