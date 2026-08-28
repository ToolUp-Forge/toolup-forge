// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Hosts.DeliveredEgress.FieldMappedParser

open System
open System.Globalization
open System.Text.Json
open ToolUp.MediaLibrary.DeliveredEgress

// ─── Phase 742 — field-mapped access-log parsers ──────────────────────
//
// The reference implementations that prove `IDeliveredLogSource` and
// `DeliveredRecord` from OUTSIDE the SDK (GP 12), and the concrete form
// of the phase's design finding: once the FIELD NAMES are a parameter,
// there is nothing vendor-specific left to write.
//
// The 2026-08-28 survey found that the two edge classes Phase 472 shaped
// its adapter for differ in three ways that all reduce to configuration
// rather than format:
//
//   * The field SET is chosen per delivery, not fixed by the vendor. Two
//     deployments on the same edge emit different records.
//   * The CONTAINER is chosen per delivery — delimited text with a
//     `#Fields:` header line and a configurable delimiter, or
//     newline-delimited JSON.
//   * The field NAMES and their VOCABULARIES differ (bytes-sent versus
//     bytes-returned; `Hit`/`RefreshHit`/`Miss` versus
//     `hit`/`miss`/`expired`/`dynamic`/`bypass`).
//
// So this module ships exactly two container parsers and takes the names
// and the vocabulary as a `FieldMap`. No vendor name appears in any
// identifier or string literal here; the README names them, as generic
// API-shape references, so a reader can build their own map.

/// Which of this deployment's log field names carry the facts a
/// `DeliveredRecord` needs.
///
/// Every field is a NAME, supplied by the deployment, because the name is
/// the only vendor-shaped thing left. `TimestampFormat` and
/// `EdgeOutcomes` cover the two places where the VALUE shape also varies.
type FieldMap = {
    /// Field carrying the request path. Required.
    PathField: string
    /// Field carrying the query string, when the edge logs it
    /// separately from the path. `None` when the path field already
    /// includes the query — both shapes exist, and the difference
    /// matters here because the signed-URL token lives in the query.
    QueryField: string option
    /// Field carrying the bytes the edge returned. Required.
    BytesField: string
    /// Field carrying the HTTP status the edge returned. Required.
    StatusField: string
    /// Field carrying the timestamp. Required.
    TimestampField: string
    /// Second field to join onto `TimestampField` with a space before
    /// parsing — for the deliveries that split a timestamp into separate
    /// date and time columns. `None` for a single-field timestamp.
    TimestampSecondField: string option
    /// Field carrying the cache disposition. `None` maps every record to
    /// `DeliveredOutcomeUnknown`, which is honest but makes the hit rate
    /// permanently zero.
    OutcomeField: string option
    /// Field carrying the edge's unique request id. `None` falls back to
    /// the content-derived dedup key — see `DeliveredEgress.dedupKey` for
    /// what that costs.
    RequestIdField: string option
    /// Timestamp shapes to try, in order, before falling back to the
    /// invariant-culture default parse and then to Unix epoch
    /// milliseconds. Empty is fine; the fallbacks still apply.
    TimestampFormats: string list
    /// The values of `OutcomeField` that mean "the edge served this from
    /// its own cache". Compared case-INSENSITIVELY, because the two
    /// surveyed vocabularies differ in case for the same concept.
    ///
    /// Everything not in this set and not in `OriginOutcomes` is
    /// `DeliveredOutcomeUnknown` — deliberately, so a vocabulary this
    /// deployment did not enumerate degrades to "I do not know" rather
    /// than to a silent "miss" that would understate the hit rate.
    EdgeOutcomes: Set<string>
    /// The values meaning "the edge went to the origin".
    OriginOutcomes: Set<string>
}

module FieldMap =

    /// The token an edge writes for an absent value in delimited output.
    [<Literal>]
    let AbsentToken = "-"

    /// A map with only the required fields named, every optional field
    /// absent, and empty outcome vocabularies. Refine with record-update
    /// syntax; there is deliberately no vendor preset, because a preset
    /// would be wrong for any deployment that selected a different field
    /// set — which is every deployment that selected one at all.
    let required (pathField: string) (bytesField: string) (statusField: string) (timestampField: string) : FieldMap = {
        PathField = pathField
        QueryField = None
        BytesField = bytesField
        StatusField = statusField
        TimestampField = timestampField
        TimestampSecondField = None
        OutcomeField = None
        RequestIdField = None
        TimestampFormats = []
        EdgeOutcomes = Set.empty
        OriginOutcomes = Set.empty
    }

/// Why one line could not become a `DeliveredRecord`. Returned rather
/// than thrown: a malformed line in a million-line file must not abort
/// the file, and it must not vanish either.
type LineParseError = {
    /// 1-based line number within the input, for diagnostics.
    Line: int
    Reason: string
}

/// What a parse produced: the records it recovered, and the lines it
/// could not.
///
/// Both halves are returned because either alone is misleading. Records
/// without errors hides a field-map that has silently stopped matching;
/// errors without records makes a 0.01% malformation rate look like a
/// failure.
type ParseOutput = {
    Records: DeliveredRecord list
    Errors: LineParseError list
}

module private Convert' =

    let isAbsent (raw: string) =
        String.IsNullOrWhiteSpace raw || raw = FieldMap.AbsentToken

    let bytes (raw: string) : Result<int64, string> =
        if isAbsent raw then
            Ok 0L
        else
            match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, v -> Ok v
            | _ -> Error(sprintf "bytes field is not an integer: %s" raw)

    let status (raw: string) : Result<int, string> =
        match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, v -> Ok v
        | _ -> Error(sprintf "status field is not an integer: %s" raw)

    /// Parse a timestamp by the declared formats, then the invariant
    /// default, then Unix epoch milliseconds.
    ///
    /// A value with no zone is read as UTC rather than as machine-local:
    /// every edge surveyed logs UTC, and reading it as local time would
    /// shift whole days of attribution on any host that is not on UTC —
    /// a defect that would be invisible in the developer's own timezone.
    let timestamp (formats: string list) (raw: string) : Result<DateTimeOffset, string> =
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

        let byFormat =
            formats
            |> List.tryPick (fun f ->
                match DateTimeOffset.TryParseExact(raw, f, CultureInfo.InvariantCulture, styles) with
                | true, v -> Some v
                | _ -> None)

        match byFormat with
        | Some v -> Ok v
        | None ->
            match DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, styles) with
            | true, v -> Ok v
            | _ ->
                match Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                | true, ms -> Ok(DateTimeOffset.FromUnixTimeMilliseconds ms)
                | _ -> Error(sprintf "timestamp field is not parseable: %s" raw)

    let outcome (map: FieldMap) (raw: string option) : DeliveredCacheOutcome =
        match raw with
        | None -> DeliveredOutcomeUnknown
        | Some value ->
            let lowered = value.Trim().ToLowerInvariant()

            let contains (set: Set<string>) =
                set |> Set.exists (fun v -> v.ToLowerInvariant() = lowered)

            if contains map.EdgeOutcomes then ServedFromEdge
            elif contains map.OriginOutcomes then ServedFromOrigin
            else DeliveredOutcomeUnknown

/// Build one `DeliveredRecord` from a field lookup. Shared by both
/// container parsers, so the two cannot drift apart in how they read a
/// map — the container is the only difference between them, and this is
/// the code that makes that true rather than a claim.
let private recordFrom (map: FieldMap) (lookup: string -> string option) : Result<DeliveredRecord, string> =
    let required name =
        match lookup name with
        | Some v when not (Convert'.isAbsent v) -> Ok v
        | _ -> Error(sprintf "required field is missing or absent: %s" name)

    let optional name =
        name |> Option.bind lookup |> Option.filter (Convert'.isAbsent >> not)

    match required map.PathField with
    | Error e -> Error e
    | Ok path ->
        let url =
            match optional map.QueryField with
            | Some q when not (path.Contains "?") -> path + "?" + q
            | _ -> path

        let timestampRaw =
            match required map.TimestampField, optional map.TimestampSecondField with
            | Ok first, Some second -> Ok(first + " " + second)
            | Ok first, None -> Ok first
            | Error e, _ -> Error e

        match timestampRaw with
        | Error e -> Error e
        | Ok tsRaw ->
            let bytesResult = required map.BytesField |> Result.bind Convert'.bytes
            let statusResult = required map.StatusField |> Result.bind Convert'.status
            let atResult = Convert'.timestamp map.TimestampFormats tsRaw

            match bytesResult, statusResult, atResult with
            | Ok bytes, Ok status, Ok at ->
                Ok {
                    Url = url
                    Bytes = bytes
                    At = at
                    Status = status
                    Outcome = Convert'.outcome map (optional map.OutcomeField)
                    RequestId = optional map.RequestIdField
                }
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e

/// Parse delimited text — the shape a `#Fields:` header line plus
/// delimiter-separated rows produces.
///
/// The header may come from the input itself (a `#Fields:` comment line,
/// which is how the W3C-extended shape declares its own columns) or be
/// supplied by the caller when the delivery emits no header. Supplying it
/// wins, because a caller who names the columns knows something the file
/// does not.
let parseDelimited
    (map: FieldMap)
    (delimiter: char)
    (declaredFields: string list option)
    (content: string)
    : ParseOutput =
    let mutable header: string[] option = declaredFields |> Option.map Array.ofList
    let records = ResizeArray<DeliveredRecord>()
    let errors = ResizeArray<LineParseError>()

    let lines = content.Split('\n')

    for index in 0 .. lines.Length - 1 do
        let line = lines[index].TrimEnd('\r')
        let lineNumber = index + 1

        if String.IsNullOrWhiteSpace line then
            ()
        elif line.StartsWith "#" then
            // Only a `#Fields:` comment carries meaning; a `#Version:`
            // or any other comment is skipped rather than reported, so a
            // header block does not read as a page of errors.
            let marker = "#Fields:"

            if
                line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)
                && declaredFields.IsNone
            then
                header <-
                    line.Substring(marker.Length).Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Some
        else
            match header with
            | None ->
                errors.Add {
                    Line = lineNumber
                    Reason = "no field header: the input declared none and the caller supplied none"
                }
            | Some columns ->
                let cells = line.Split delimiter

                if cells.Length <> columns.Length then
                    errors.Add {
                        Line = lineNumber
                        Reason = sprintf "expected %d fields, found %d" columns.Length cells.Length
                    }
                else
                    let lookup (name: string) =
                        columns
                        |> Array.tryFindIndex (fun c -> String.Equals(c, name, StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun i -> cells[i])

                    match recordFrom map lookup with
                    | Ok record -> records.Add record
                    | Error reason -> errors.Add { Line = lineNumber; Reason = reason }

    {
        Records = List.ofSeq records
        Errors = List.ofSeq errors
    }

/// Parse newline-delimited JSON — one flat JSON object per line.
///
/// Values are read as strings whatever their JSON type, so a numeric
/// `status` and a quoted `"status"` parse identically. That tolerance is
/// deliberate: the same logical field arrives typed on one delivery and
/// stringly on another, and a parser that refused one of them would be
/// refusing a configuration choice rather than a malformation.
let parseJsonLines (map: FieldMap) (content: string) : ParseOutput =
    let records = ResizeArray<DeliveredRecord>()
    let errors = ResizeArray<LineParseError>()
    let lines = content.Split('\n')

    for index in 0 .. lines.Length - 1 do
        let line = lines[index].TrimEnd('\r').Trim()
        let lineNumber = index + 1

        if String.IsNullOrWhiteSpace line then
            ()
        else
            let parsed =
                try
                    Ok(JsonDocument.Parse line)
                with ex ->
                    Error ex.Message

            match parsed with
            | Error reason ->
                errors.Add {
                    Line = lineNumber
                    Reason = sprintf "not valid JSON: %s" reason
                }
            | Ok document ->
                use document = document

                if document.RootElement.ValueKind <> JsonValueKind.Object then
                    errors.Add {
                        Line = lineNumber
                        Reason = "expected a JSON object"
                    }
                else
                    let lookup (name: string) =
                        let mutable found = Unchecked.defaultof<JsonElement>

                        if document.RootElement.TryGetProperty(name, &found) then
                            match found.ValueKind with
                            | JsonValueKind.Null -> None
                            | JsonValueKind.String -> Some(found.GetString())
                            | _ -> Some(found.GetRawText())
                        else
                            None

                    match recordFrom map lookup with
                    | Ok record -> records.Add record
                    | Error reason -> errors.Add { Line = lineNumber; Reason = reason }

    {
        Records = List.ofSeq records
        Errors = List.ofSeq errors
    }