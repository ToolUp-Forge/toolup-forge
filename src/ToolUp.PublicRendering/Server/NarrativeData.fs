// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 101 — tabular data extraction + CSV / JSON serialisation.
///
/// Pulls every `Table` element out of a `NarrativeDocument` (recursing
/// into `Card` / `Accordion` / `Tabs` bodies) into a flat `TabularData`
/// shape, and serialises it as RFC 4180 CSV or structured JSON rows — so
/// a data-bound page (e.g. a [`NarrativeFromData`](NarrativeFromData.fs)
/// report table) exposes a clean downloadable representation alongside
/// its HTML / Markdown / Atom forms via `?format=csv` / `?format=json`
/// ([`NarrativeExportHandler`](NarrativeExportHandler.fs)). Pure +
/// deterministic (`InvariantCulture`-free string ops only).
namespace ToolUp.PublicRendering

open System.Text
open ToolUp.Platform.Narrative

/// A flattened table: column headers + rows of plaintext cell values.
type TabularData = {
    Columns: string list
    Rows: string list list
}

module NarrativeData =

    /// Flatten an inline span to plaintext for a cell value. Mirrors the
    /// plaintext renderer's intent: emphasis / strong / code keep their
    /// text, a metric joins label + value, a link keeps its visible text,
    /// an image falls back to alt text, a hard break becomes a space.
    let rec private spanText (span: InlineSpan) : string =
        match span with
        | Text t -> t
        | Emphasis t -> t
        | Strong t -> t
        | Code t -> t
        | Metric(label, value) -> sprintf "%s %s" label value
        | Link(_, spans) -> spans |> List.map spanText |> String.concat ""
        | Image(_, alt, _) -> alt
        | Br -> " "

    let private cellText (spans: InlineSpan list) : string =
        spans |> List.map spanText |> String.concat ""

    /// Every `Table` in the document, in document order (recursing into
    /// `Card` / `Accordion` / `Tabs` nested bodies).
    let tables (doc: NarrativeDocument) : TabularData list =
        let rec collect (elements: NarrativeElement list) : TabularData list =
            elements
            |> List.collect (fun el ->
                match el with
                | Table(columns, rows) -> [
                    {
                        Columns = columns |> List.map fst
                        Rows = rows |> List.map (List.map cellText)
                    }
                  ]
                | Card spec -> collect spec.Body
                | Accordion panels -> panels |> List.collect (fun (_, body) -> collect body)
                | Tabs panels -> panels |> List.collect (fun (_, body) -> collect body)
                | _ -> [])

        doc.Sections |> List.collect (fun s -> collect s.Elements)

    // ─── CSV (RFC 4180) ──────────────────────────────────────────────

    let private csvField (s: string) : string =
        if s.Contains "," || s.Contains "\"" || s.Contains "\n" || s.Contains "\r" then
            "\"" + s.Replace("\"", "\"\"") + "\""
        else
            s

    /// Serialise a table as RFC 4180 CSV: quote-on-demand (comma / quote /
    /// newline), doubled quotes, CRLF line endings, header row first.
    let toCsv (table: TabularData) : string =
        let line (cells: string list) =
            cells |> List.map csvField |> String.concat ","

        table.Columns :: table.Rows |> List.map line |> String.concat "\r\n"

    // ─── JSON (structured rows) ──────────────────────────────────────

    let private jsonString (s: string) : string =
        let sb = StringBuilder()
        sb.Append('"') |> ignore

        for c in s do
            match c with
            | '"' -> sb.Append("\\\"") |> ignore
            | '\\' -> sb.Append("\\\\") |> ignore
            | '\n' -> sb.Append("\\n") |> ignore
            | '\r' -> () // drop CR
            | '\t' -> sb.Append("\\t") |> ignore
            | _ -> sb.Append(c) |> ignore

        sb.Append('"') |> ignore
        sb.ToString()

    /// Serialise a table as a JSON array of objects keyed by column name,
    /// preserving column order. A row shorter than the header is padded
    /// with empty strings; a longer row is truncated to the header width.
    let toJson (table: TabularData) : string =
        let width = List.length table.Columns

        let obj (row: string list) =
            let padded =
                let r = row |> List.truncate width
                r @ List.replicate (width - List.length r) ""

            List.zip table.Columns padded
            |> List.map (fun (col, value) -> sprintf "%s:%s" (jsonString col) (jsonString value))
            |> String.concat ","
            |> sprintf "{%s}"

        table.Rows |> List.map obj |> String.concat "," |> sprintf "[%s]"