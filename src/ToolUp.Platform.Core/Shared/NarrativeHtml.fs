// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Narrative.NarrativeHtml

open System.Text

let private escape (s: string) : string =
    // Minimum-viable HTML escape — sufficient for narrative prose
    // emission, not a substitute for a sanitiser on untrusted input.
    s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

let private severityClass (s: Severity) =
    match s with
    | Info -> "narrative-callout narrative-callout--info"
    | Notice -> "narrative-callout narrative-callout--notice"
    | Warning -> "narrative-callout narrative-callout--warning"
    | Critical -> "narrative-callout narrative-callout--critical"

let private severityLabel (s: Severity) =
    match s with
    | Info -> "Info"
    | Notice -> "Note"
    | Warning -> "Warning"
    | Critical -> "Critical"

let private renderSpan (span: InlineSpan) : string =
    match span with
    | Text t -> escape t
    | Emphasis t -> sprintf "<em>%s</em>" (escape t)
    | Strong t -> sprintf "<strong>%s</strong>" (escape t)
    | Metric(label, value) ->
        sprintf "<span class=\"narrative-metric\"><strong>%s</strong> %s</span>" (escape label) (escape value)
    | Code t -> sprintf "<code>%s</code>" (escape t)

let private renderSpans (spans: InlineSpan list) : string =
    spans |> List.map renderSpan |> String.concat ""

let private alignAttr (a: TableAlignment) =
    match a with
    | Left -> " style=\"text-align:left\""
    | Right -> " style=\"text-align:right\""
    | Center -> " style=\"text-align:center\""

let private renderElement (sb: StringBuilder) (el: NarrativeElement) : unit =
    match el with
    | Paragraph spans -> sb.AppendFormat("<p>{0}</p>", renderSpans spans).AppendLine() |> ignore
    | BulletList items ->
        sb.AppendLine("<ul>") |> ignore

        for spans in items do
            sb.AppendFormat("  <li>{0}</li>", renderSpans spans).AppendLine() |> ignore

        sb.AppendLine("</ul>") |> ignore
    | OrderedList items ->
        sb.AppendLine("<ol>") |> ignore

        for spans in items do
            sb.AppendFormat("  <li>{0}</li>", renderSpans spans).AppendLine() |> ignore

        sb.AppendLine("</ol>") |> ignore
    | KeyValueGrid pairs ->
        sb.AppendLine("<dl class=\"narrative-kv\">") |> ignore

        for (label, spans) in pairs do
            sb.AppendFormat("  <dt>{0}</dt>", escape label).AppendLine() |> ignore
            sb.AppendFormat("  <dd>{0}</dd>", renderSpans spans).AppendLine() |> ignore

        sb.AppendLine("</dl>") |> ignore
    | Table(columns, rows) ->
        sb.AppendLine("<table class=\"narrative-table\">") |> ignore
        sb.AppendLine("  <thead>") |> ignore
        sb.Append("    <tr>") |> ignore

        for (header, align) in columns do
            sb.AppendFormat("<th{0}>{1}</th>", alignAttr align, escape header) |> ignore

        sb.AppendLine("</tr>") |> ignore
        sb.AppendLine("  </thead>") |> ignore
        sb.AppendLine("  <tbody>") |> ignore

        let aligns = columns |> List.map snd

        for row in rows do
            sb.Append("    <tr>") |> ignore

            let cells = List.zip aligns row

            for (align, spans) in cells do
                sb.AppendFormat("<td{0}>{1}</td>", alignAttr align, renderSpans spans) |> ignore

            sb.AppendLine("</tr>") |> ignore

        sb.AppendLine("  </tbody>") |> ignore
        sb.AppendLine("</table>") |> ignore
    | Callout(severity, spans) ->
        sb
            .AppendFormat("<aside class=\"{0}\">", severityClass severity)
            .AppendFormat("<strong>{0}:</strong> ", severityLabel severity)
            .Append(renderSpans spans)
            .AppendLine("</aside>")
        |> ignore
    | Divider -> sb.AppendLine("<hr />") |> ignore

let private renderSection (sb: StringBuilder) (section: NarrativeSection) : unit =
    sb.AppendFormat("<section id=\"{0}\">", escape section.Id).AppendLine()
    |> ignore

    sb.AppendFormat("  <h2>{0}</h2>", escape section.Heading).AppendLine() |> ignore

    match section.Subheading with
    | Some sh ->
        sb.AppendFormat("  <p class=\"narrative-subheading\"><em>{0}</em></p>", escape sh).AppendLine()
        |> ignore
    | None -> ()

    for el in section.Elements do
        renderElement sb el

    sb.AppendLine("</section>") |> ignore

/// Render a `NarrativeDocument` to an HTML fragment. Output is *not*
/// wrapped in `<html>` / `<body>` — the caller embeds the fragment
/// inside whatever page template they own.
///
/// Inline content is HTML-escaped; this is not a sanitiser. Trust
/// `NarrativeDocument` input as much as you would the rendered markdown
/// for the same document.
let render (doc: NarrativeDocument) : string =
    let sb = StringBuilder()

    sb.AppendLine("<article class=\"narrative\">") |> ignore

    sb.AppendFormat("  <h1>{0}</h1>", escape doc.Title).AppendLine() |> ignore

    match doc.Subtitle with
    | Some sub ->
        sb.AppendFormat("  <p class=\"narrative-subtitle\"><em>{0}</em></p>", escape sub).AppendLine()
        |> ignore
    | None -> ()

    for section in doc.Sections do
        renderSection sb section

    sb.AppendLine("</article>") |> ignore
    sb.ToString().TrimEnd()