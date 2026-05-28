// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Narrative.NarrativePlaintext

open System
open System.Text

let private severityPrefix (s: Severity) =
    match s with
    | Info -> "[INFO] "
    | Notice -> "[NOTE] "
    | Warning -> "[WARNING] "
    | Critical -> "[CRITICAL] "

let private renderSpan (span: InlineSpan) : string =
    match span with
    | Text t -> t
    | Emphasis t -> t
    | Strong t -> t
    | Metric(label, value) -> sprintf "%s = %s" label value
    | Code t -> t

let private renderSpans (spans: InlineSpan list) : string =
    spans |> List.map renderSpan |> String.concat ""

let private renderElement (sb: StringBuilder) (el: NarrativeElement) : unit =
    match el with
    | Paragraph spans -> sb.AppendLine(renderSpans spans) |> ignore
    | BulletList items ->
        for spans in items do
            sb.Append("  - ").AppendLine(renderSpans spans) |> ignore
    | OrderedList items ->
        items
        |> List.iteri (fun i spans -> sb.AppendFormat("  {0}. ", i + 1).AppendLine(renderSpans spans) |> ignore)
    | KeyValueGrid pairs ->
        let maxLabel = pairs |> List.map (fst >> String.length) |> List.fold max 0

        for (label, spans) in pairs do
            sb.AppendFormat("  {0}: {1}", label.PadRight(maxLabel), renderSpans spans).AppendLine()
            |> ignore
    | Table(columns, rows) ->
        let cellText (spans: InlineSpan list) = renderSpans spans
        let headers = columns |> List.map fst
        let aligns = columns |> List.map snd
        let renderedRows = rows |> List.map (List.map cellText)

        let widths =
            let headerWidths = headers |> List.map String.length
            let rowWidths = renderedRows |> List.map (List.map String.length)
            let zip = (headerWidths :: rowWidths) |> List.reduce (fun a b -> List.map2 max a b)
            zip

        let pad (align: TableAlignment) (width: int) (s: string) =
            match align with
            | Left -> s.PadRight(width)
            | Right -> s.PadLeft(width)
            | Center ->
                let extra = width - s.Length

                if extra <= 0 then
                    s
                else
                    let left = extra / 2
                    let right = extra - left
                    String.replicate left " " + s + String.replicate right " "

        let line (cells: string list) =
            let padded = List.zip3 aligns widths cells |> List.map (fun (a, w, s) -> pad a w s)
            "  " + String.concat "  " padded

        sb.AppendLine(line headers) |> ignore
        let separator = widths |> List.map (fun w -> String.replicate w "-")
        sb.AppendLine(line separator) |> ignore

        for row in renderedRows do
            sb.AppendLine(line row) |> ignore
    | Callout(severity, spans) -> sb.Append(severityPrefix severity).AppendLine(renderSpans spans) |> ignore
    | Divider -> sb.AppendLine("---") |> ignore

let private renderSection (sb: StringBuilder) (section: NarrativeSection) : unit =
    sb.AppendLine(section.Heading.ToUpperInvariant()) |> ignore
    sb.AppendLine(String.replicate section.Heading.Length "=") |> ignore

    match section.Subheading with
    | Some sh -> sb.AppendLine(sh) |> ignore
    | None -> ()

    for el in section.Elements do
        renderElement sb el

    sb.AppendLine() |> ignore

/// Render a `NarrativeDocument` to a single plaintext string. Matches the
/// shape of the legacy `formatInsight` output: title, blank line, sections
/// separated by blank lines, bullets prefixed with "  - ", callouts prefixed
/// with "[SEVERITY] ".
let render (doc: NarrativeDocument) : string =
    let sb = StringBuilder()
    sb.AppendLine(doc.Title) |> ignore
    sb.AppendLine(String.replicate doc.Title.Length "=") |> ignore

    match doc.Subtitle with
    | Some sub ->
        sb.AppendLine(sub) |> ignore
        sb.AppendLine() |> ignore
    | None -> sb.AppendLine() |> ignore

    for section in doc.Sections do
        renderSection sb section

    sb.ToString().TrimEnd()