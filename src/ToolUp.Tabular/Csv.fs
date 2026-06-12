// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Tabular

open System.IO
open System.Text

// ─── Phase 123 — CSV leg (BCL-only, RFC 4180) ───────────────────
//
// Deliberately zero third-party dependencies (Phase 123
// acceptance criterion): a single-pass character state machine
// over a `TextReader`. Handles RFC 4180 quoting (embedded
// delimiters, embedded quotes via `""`, embedded newlines),
// configurable delimiter, and BOM-first encoding detection.
// Streaming by construction — records are yielded one at a time
// off the reader; nothing buffers beyond the current record.
//
// Malformed quoting does not throw: the offending record is
// yielded as `Error message` and the parser resynchronises at
// the next newline, so one bad record costs one report entry,
// not the rest of the file.

/// Low-level RFC 4180 record reader. Most consumers want
/// `TabularReader.readCsv` (schema validation + typed binding);
/// this module is public for consumers that need raw records.
module Csv =

    /// BOM-first encoding detection: UTF-8 / UTF-16 LE / UTF-16
    /// BE / UTF-32 BOMs are honoured when present; BOM-less input
    /// decodes as UTF-8 (of which ASCII is a subset — the right
    /// default for machine-produced CSV). The reader leaves the
    /// underlying stream open; disposal stays with the caller.
    let openReader (stream: Stream) : StreamReader =
        new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks = true,
            bufferSize = 4096,
            leaveOpen = true
        )

    [<RequireQualifiedAccess>]
    type private State =
        /// Start of a field — quoting decision pending.
        | FieldStart
        /// Inside an unquoted field.
        | Unquoted
        /// Inside a quoted field.
        | Quoted
        /// Just consumed a quote inside a quoted field — either
        /// an escaped `""` or the closing quote.
        | QuoteInQuoted

    /// Parse RFC 4180 records off a `TextReader`, yielding
    /// `(recordIndex, Ok fields)` per well-formed record and
    /// `(recordIndex, Error message)` per record whose quoting is
    /// malformed (unterminated quote at EOF, or content after a
    /// closing quote). `recordIndex` is 1-based in record order —
    /// the row number a spreadsheet UI would show. Embedded
    /// newlines inside quotes do not advance it.
    ///
    /// Lazy: records are parsed as the sequence is consumed; a
    /// caller taking the first 10 records of a 100k-record file
    /// reads only those records' characters.
    let parseRecords (delimiter: char) (reader: TextReader) : seq<int * Result<string[], string>> = seq {
        let fields = ResizeArray<string>()
        let field = StringBuilder()
        let mutable state = State.FieldStart
        let mutable recordIndex = 0
        let mutable malformed: string option = None
        let mutable eof = false

        // Local helpers keep the per-character match flat.
        let commitField () =
            fields.Add(field.ToString())
            field.Clear() |> ignore

        let takeRecord () =
            recordIndex <- recordIndex + 1
            commitField ()
            let cells = fields.ToArray()
            fields.Clear()
            state <- State.FieldStart

            match malformed with
            | Some message ->
                malformed <- None
                recordIndex, Error message
            | None -> recordIndex, Ok cells

        while not eof do
            let code = reader.Read()

            if code < 0 then
                eof <- true

                match state with
                | State.Quoted ->
                    malformed <-
                        Some "unterminated quoted field — the file ended inside an opening \" with no closing \""
                | _ -> ()

                // A final record exists when any character
                // contributed to it. A file ending in a clean
                // newline yields nothing extra here.
                if field.Length > 0 || fields.Count > 0 || malformed.IsSome then
                    yield takeRecord ()
            else
                let c = char code

                match state with
                | State.FieldStart ->
                    if c = '"' then
                        state <- State.Quoted
                    elif c = delimiter then
                        commitField ()
                    elif c = '\n' then
                        yield takeRecord ()
                    elif c = '\r' then
                        if reader.Peek() = int '\n' then
                            reader.Read() |> ignore

                        yield takeRecord ()
                    else
                        field.Append c |> ignore
                        state <- State.Unquoted
                | State.Unquoted ->
                    if c = delimiter then
                        commitField ()
                        state <- State.FieldStart
                    elif c = '\n' then
                        yield takeRecord ()
                    elif c = '\r' then
                        if reader.Peek() = int '\n' then
                            reader.Read() |> ignore

                        yield takeRecord ()
                    else
                        // RFC 4180 forbids a bare quote inside an
                        // unquoted field; real-world exports contain
                        // them, so we take the lenient reading
                        // (literal character) rather than failing
                        // the record.
                        field.Append c |> ignore
                | State.Quoted ->
                    if c = '"' then
                        state <- State.QuoteInQuoted
                    else
                        field.Append c |> ignore
                | State.QuoteInQuoted ->
                    if c = '"' then
                        // Escaped "" — literal quote.
                        field.Append '"' |> ignore
                        state <- State.Quoted
                    elif c = delimiter then
                        commitField ()
                        state <- State.FieldStart
                    elif c = '\n' then
                        yield takeRecord ()
                    elif c = '\r' then
                        if reader.Peek() = int '\n' then
                            reader.Read() |> ignore

                        yield takeRecord ()
                    else
                        // Content after a closing quote (`"ab"x`) —
                        // malformed. Mark the record and resync:
                        // consume literally until the record
                        // terminator so the next record parses
                        // cleanly.
                        malformed <-
                            Some(
                                sprintf
                                    "malformed quoting — unexpected '%c' after a closing \" (a quoted field must be followed by the delimiter or a line break)"
                                    c
                            )

                        field.Append c |> ignore
                        state <- State.Unquoted
    }