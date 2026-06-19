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

// Returns an ARRAY, not a `string list`. A multi-MB / 50k-row CSV turned into
// an F# linked list (and then folded/concatenated) is the large-list /
// deep-recursion shape that overflows the JS call stack under Fable — and this
// engine runs in the browser (the mapping wizard's parsePreview + rewriteCsv).
let private splitLines (raw: string) (opts: StringSplitOptions) : string[] =
    raw.Split([| "\r\n"; "\n"; "\r" |], opts)

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
    let values = cells |> List.map _.Trim() |> List.filter (fun c -> c <> "")

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

// ─── Data-quality remediation ─────────────────────────────────────

let private currencySymbols = [ "$"; "£"; "€"; "¥"; "₹"; "₩"; "₪" ]

let private defaultNullMarkers = [ "n/a"; "na"; "null"; "none"; "nil"; "-"; "--"; "#n/a" ]

let private isNullMarker (s: string) =
    defaultNullMarkers |> List.contains (s.Trim().ToLower())

let private tryInt (s: string) : int option =
    match System.Int32.TryParse(s.Trim()) with
    | true, v -> Some v
    | _ -> None

let private expandYear (y: int) =
    if y >= 100 then y
    elif y >= 70 then 1900 + y
    else 2000 + y

let private daysInMonth (y: int) (m: int) =
    match m with
    | 1
    | 3
    | 5
    | 7
    | 8
    | 10
    | 12 -> 31
    | 4
    | 6
    | 9
    | 11 -> 30
    | 2 ->
        if (y % 4 = 0 && y % 100 <> 0) || y % 400 = 0 then
            29
        else
            28
    | _ -> 0

let private threeParts (s: string) : (int * int * int) option =
    let parts =
        s.Trim().Split([| '/'; '-'; '.' |], StringSplitOptions.RemoveEmptyEntries)

    if parts.Length = 3 then
        match tryInt parts[0], tryInt parts[1], tryInt parts[2] with
        | Some a, Some b, Some c -> Some(a, b, c)
        | _ -> None
    else
        None

/// Parse a 3-part date under `order` → ISO `yyyy-MM-dd`. `None` when the
/// parts don't form a valid date (used both to apply the transform and,
/// during profiling, to decide whether an order is forced or ambiguous).
let toIsoDate (order: DateOrder) (s: string) : string option =
    match threeParts s with
    | None -> None
    | Some(a, b, c) ->
        let parts = [| a; b; c |]
        let fourDigit = [ 0; 1; 2 ] |> List.tryFind (fun i -> parts[i] >= 1000)

        // (yearIdx, firstOther, secondOther) in source order
        let layout =
            match fourDigit with
            | Some 0 -> Some(0, 1, 2)
            | Some 1 -> Some(1, 0, 2)
            | Some 2 -> Some(2, 0, 1)
            | _ ->
                match order with
                | YearFirst -> Some(0, 1, 2)
                | _ -> Some(2, 0, 1) // 2-digit year assumed last

        match layout with
        | None -> None
        | Some(yi, i1, i2) ->
            let year = expandYear parts[yi]
            let v1 = parts[i1]
            let v2 = parts[i2]

            let month, day =
                match order with
                | MonthFirst -> v1, v2
                | DayFirst -> v2, v1
                | YearFirst -> v1, v2 // ISO: year, month, day

            if month >= 1 && month <= 12 && day >= 1 && day <= daysInMonth year month then
                Some(sprintf "%04d-%02d-%02d" year month day)
            else
                None

let private stripThousands (s: string) : string =
    let chars = s.ToCharArray()
    let sb = System.Text.StringBuilder()

    for i in 0 .. chars.Length - 1 do
        let c = chars[i]

        let isThousandsSep =
            c = ','
            && i > 0
            && i < chars.Length - 1
            && System.Char.IsDigit chars[i - 1]
            && System.Char.IsDigit chars[i + 1]

        if not isThousandsSep then
            sb.Append c |> ignore

    sb.ToString()

/// Apply one transform to one cell value.
let applyTransform (t: CellTransform) (s: string) : string =
    match t with
    | Trim -> s.Trim()
    | StripThousandsSeparators -> stripThousands s
    | StripCurrency _ ->
        let mutable r = s.Trim()

        for sym in currencySymbols do
            r <- r.Replace(sym, "")

        r.Trim()
    | StripPercent -> s.Replace("%", "").Trim()
    | DecimalCommaToDot -> s.Replace(".", "").Replace(",", ".")
    | StripLeadingApostrophe -> if s.StartsWith "'" then s.Substring 1 else s
    | BlankNullMarkers markers ->
        let v = s.Trim().ToLower()

        if markers |> List.exists (fun m -> m.Trim().ToLower() = v) then
            ""
        else
            s
    | NormaliseBoolean ->
        match s.Trim().ToLower() with
        | "y"
        | "yes"
        | "true"
        | "t"
        | "1" -> "true"
        | "n"
        | "no"
        | "false"
        | "f"
        | "0" -> "false"
        | _ -> s
    | ParseDateToIso order -> toIsoDate order s |> Option.defaultValue s

/// Apply an ordered list of transforms to one cell value.
let applyTransforms (ts: CellTransform list) (s: string) : string =
    ts |> List.fold (fun acc t -> applyTransform t acc) s

let private describeTransform =
    function
    | Trim -> "trimmed whitespace"
    | StripThousandsSeparators -> "removed thousands separators"
    | StripCurrency sym -> sprintf "stripped %s" sym
    | StripPercent -> "stripped %"
    | DecimalCommaToDot -> "normalised decimal comma"
    | StripLeadingApostrophe -> "removed leading apostrophe"
    | BlankNullMarkers _ -> "blanked null-markers"
    | NormaliseBoolean -> "normalised booleans"
    | ParseDateToIso DayFirst -> "parsed dates day-first → ISO"
    | ParseDateToIso MonthFirst -> "parsed dates month-first → ISO"
    | ParseDateToIso YearFirst -> "parsed dates → ISO"

/// One human-readable line summarising a column's remediation (for the
/// per-object `ConversionRecord`). `None` when the column had no
/// transforms.
let describeColumnRemediation (column: string) (ts: CellTransform list) : string option =
    match ts with
    | [] -> None
    | xs -> Some(sprintf "%s: %s" column (xs |> List.map describeTransform |> String.concat ", "))

[<Literal>]
let private DqThreshold = 0.8

let private examplesOf (xs: string list) = xs |> List.distinct |> List.truncate 3

/// Scan one source column's sample values for data-quality problems and
/// propose remediation. Pure — the wizard's "Review data" step renders
/// the result; the chosen transforms ride into the saved `ColumnMapping`.
let profileColumn (header: string) (cells: string list) : ColumnProfile =
    let nonBlank = cells |> List.map _.Trim() |> List.filter (fun c -> c <> "")

    let realValues = nonBlank |> List.filter (isNullMarker >> not)
    let n = List.length realValues

    if n = 0 then
        {
            Column = header
            InferredType = StringColumn
            DetectedUnit = None
            Issues = []
        }
    else
        let fractionWhere pred =
            (realValues |> List.filter pred |> List.length |> float) / float n

        // ── null markers ──
        let seenNulls = nonBlank |> List.filter isNullMarker

        let nullIssue =
            if not seenNulls.IsEmpty then
                Some {
                    Kind = NullMarkersPresent
                    Detail = "Null-marker tokens present — blank them so they don't parse as text."
                    Examples = examplesOf seenNulls
                    Suggested = [ BlankNullMarkers defaultNullMarkers ]
                    Safe = true
                    NeedsChoice = false
                }
            else
                None

        // ── numbers formatted as text ──
        let sym =
            currencySymbols
            |> List.tryFind (fun s -> realValues |> List.exists (fun v -> v.Contains s))

        let anyPercent = realValues |> List.exists (fun v -> v.Contains "%")
        let anyApostrophe = realValues |> List.exists (fun v -> v.StartsWith "'")

        let numericCleanup = [
            Trim
            if anyApostrophe then
                StripLeadingApostrophe
            match sym with
            | Some s -> StripCurrency s
            | None -> ()
            if anyPercent then
                StripPercent
            StripThousandsSeparators
        ]

        let cleanedNumericFraction =
            fractionWhere (fun v -> isNumeric (applyTransforms numericCleanup v))

        let rawNumericFraction = fractionWhere isNumeric
        let isNumberColumn = cleanedNumericFraction >= DqThreshold

        let detectedUnit =
            if isNumberColumn && sym.IsSome then sym
            elif isNumberColumn && anyPercent then Some "%"
            else None

        let numberIssue =
            if isNumberColumn && (cleanedNumericFraction > rawNumericFraction + 1e-9) then
                Some {
                    Kind = NumbersFormattedAsText
                    Detail = "Numeric values rendered as text (symbols / separators / spacing)."
                    Examples = examplesOf (realValues |> List.filter (isNumeric >> not))
                    Suggested = numericCleanup
                    Safe = true
                    NeedsChoice = false
                }
            else
                None

        // ── dates (only when not already a clean number column) ──
        let dateIssue =
            if isNumberColumn then
                None
            else
                let dayF = fractionWhere (fun v -> (toIsoDate DayFirst v).IsSome)
                let monthF = fractionWhere (fun v -> (toIsoDate MonthFirst v).IsSome)
                let yearF = fractionWhere (fun v -> (toIsoDate YearFirst v).IsSome)

                if max dayF (max monthF yearF) < DqThreshold then
                    None
                else
                    let isoFirst =
                        realValues
                        |> List.exists (fun v ->
                            match threeParts v with
                            | Some(a, _, _) -> a >= 1000
                            | None -> false)

                    let forcesDay =
                        realValues
                        |> List.exists (fun v -> (toIsoDate DayFirst v).IsSome && (toIsoDate MonthFirst v).IsNone)

                    let forcesMonth =
                        realValues
                        |> List.exists (fun v -> (toIsoDate MonthFirst v).IsSome && (toIsoDate DayFirst v).IsNone)

                    let resolved kind order detail =
                        Some {
                            Kind = kind
                            Detail = detail
                            Examples = examplesOf realValues
                            Suggested = [ ParseDateToIso order ]
                            Safe = true
                            NeedsChoice = false
                        }

                    if isoFirst && yearF >= DqThreshold then
                        resolved ResolvedDateFormat YearFirst "ISO dates (yyyy-mm-dd) — normalised as-is."
                    elif forcesDay && not forcesMonth then
                        resolved ResolvedDateFormat DayFirst "Dates resolved as day-first (a day value exceeds 12)."
                    elif forcesMonth && not forcesDay then
                        resolved
                            ResolvedDateFormat
                            MonthFirst
                            "Dates resolved as month-first (a month value exceeds 12)."
                    else
                        Some {
                            Kind = AmbiguousDateFormat
                            Detail = "Ambiguous date order (e.g. 01/02/2024) — choose day-first or month-first."
                            Examples = examplesOf realValues
                            Suggested = []
                            Safe = false
                            NeedsChoice = true
                        }

        // ── stray whitespace (only when nothing else already implies Trim) ──
        let whitespaceIssue =
            let hasWs = cells |> List.exists (fun c -> c.Trim() <> "" && c <> c.Trim())

            if hasWs && nullIssue.IsNone && numberIssue.IsNone && dateIssue.IsNone then
                Some {
                    Kind = LeadingTrailingWhitespace
                    Detail = "Leading / trailing whitespace on values."
                    Examples = examplesOf (cells |> List.filter (fun c -> c.Trim() <> "" && c <> c.Trim()))
                    Suggested = [ Trim ]
                    Safe = true
                    NeedsChoice = false
                }
            else
                None

        let issues =
            [ nullIssue; numberIssue; dateIssue; whitespaceIssue ] |> List.choose id

        // Type inferred after the safe (pre-checked) fixes.
        let safeTransforms = issues |> List.filter _.Safe |> List.collect _.Suggested

        let inferred =
            realValues |> List.map (applyTransforms safeTransforms) |> inferColumnType

        {
            Column = header
            InferredType = inferred
            DetectedUnit = detectedUnit
            Issues = issues
        }

// ─── Fingerprint ──────────────────────────────────────────────────

module Fingerprint =
    /// Order-independent column-structure key: normalise (trim + lower),
    /// drop blanks, sort, join with `|`. Two CSVs with the same set of
    /// header names — regardless of column order — share a fingerprint,
    /// so a saved mapping is reused.
    let ofHeaders (headers: string list) : string =
        headers
        |> List.map _.Trim().ToLower()
        |> List.filter (fun h -> h <> "")
        |> List.sort
        |> String.concat "|"

// ─── Preview extraction ───────────────────────────────────────────

/// Pull the header row and up to `sampleSize` rows of per-column sample
/// values from raw CSV text. The wizard feeds the samples into
/// `suggest` for type inference. Single tested parse site shared by
/// client and server.
let parsePreview (sampleSize: int) (rawCsv: string) : string list * Map<string, string list> =
    let lines = splitLines rawCsv StringSplitOptions.RemoveEmptyEntries

    if lines.Length = 0 then
        [], Map.empty
    else
        let headers = splitCsvLine lines[0] |> List.map _.Trim()

        // Materialise at most `sampleSize` body rows — never the whole file
        // (a 50k-row file should not build a 50k-element structure to sample 20).
        let sampleCount = min (max 0 sampleSize) (lines.Length - 1)

        let sampleRows = [ for i in 1..sampleCount -> splitCsvLine lines[i] |> List.toArray ]

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
/// field, body cells pulled from the mapped source column with that
/// column's `transforms` (data-quality remediation) applied first.
/// Columns the mapping doesn't cover are dropped — "minimally, just the
/// required data". The result is fed to the existing `DataType.Process`
/// for the chosen target type.
let rewriteCsv
    (schema: DataTypeSchema)
    (fieldToColumn: Map<string, string>)
    (transforms: Map<string, CellTransform list>)
    (rawCsv: string)
    : string =
    let lines = splitLines rawCsv StringSplitOptions.None

    if lines.Length = 0 then
        ""
    else
        let srcHeaders = splitCsvLine lines[0]

        let srcIndex (name: string) =
            let target = name.Trim().ToLower()
            srcHeaders |> List.tryFindIndex (fun h -> h.Trim().ToLower() = target)

        // schema columns that have a mapping AND resolve to a real source
        // column. Pre-resolve each one's source index + transforms ONCE so the
        // per-row loop does no Map lookups.
        let emitted =
            schema.Columns
            |> List.choose (fun col ->
                match fieldToColumn |> Map.tryFind col.Name with
                | Some src ->
                    srcIndex src
                    |> Option.map (fun idx -> col.Name, idx, (transforms |> Map.tryFind src |> Option.defaultValue []))
                | None -> None)

        // Build the output with a StringBuilder over the line ARRAY — no
        // 50k-element `string list`, no `String.concat` over every row (the
        // shape that overflows the Fable call stack on "Confirm & import").
        let sb = System.Text.StringBuilder()

        sb.Append(emitted |> List.map (fun (name, _, _) -> writeCell name) |> String.concat ",")
        |> ignore

        for i in 1 .. lines.Length - 1 do
            let r = lines[i]

            if r.Trim() <> "" then
                let cells = splitCsvLine r |> List.toArray

                let rowOut =
                    emitted
                    |> List.map (fun (_, idx, ts) ->
                        let raw = if idx >= 0 && idx < cells.Length then cells[idx] else ""
                        applyTransforms ts raw |> writeCell)
                    |> String.concat ","

                sb.Append('\n').Append(rowOut) |> ignore

        sb.ToString()