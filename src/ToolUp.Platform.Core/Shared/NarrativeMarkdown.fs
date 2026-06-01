// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Narrative.NarrativeMarkdown

open System.Text

/// How `Callout` elements are rendered in markdown.
///
/// `Blockquote` is the default and matches the historical output —
/// portable across every CommonMark reader. `Directive` emits the
/// `:::severity` admonition shape recognised by docs renderers
/// (Docusaurus, MkDocs Material, GH-flavoured viewers via plugins)
/// and preserves the semantic level a Blockquote loses.
type AdmonitionStyle =
    | Blockquote
    | Directive

/// Tunables for `render`. Defaults preserve the historical output
/// byte-for-byte (GP 11).
type RenderOptions = {
    AdmonitionStyle: AdmonitionStyle
} with

    static member Default = { AdmonitionStyle = Blockquote }

let private severityLabel (s: Severity) =
    match s with
    | Info -> "Info"
    | Notice -> "Note"
    | Warning -> "Warning"
    | Critical -> "Critical"

let private blockquoteMarker (s: Severity) = sprintf "> **%s:** " (severityLabel s)

let private directiveTag (s: Severity) =
    match s with
    | Info -> "info"
    | Notice -> "note"
    | Warning -> "warning"
    | Critical -> "danger"

let private escape (s: string) : string =
    // Minimal markdown escape — enough for narrative prose, not a full sanitiser.
    s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")

let private renderSpan (span: InlineSpan) : string =
    match span with
    | Text t -> escape t
    | Emphasis t -> sprintf "_%s_" (escape t)
    | Strong t -> sprintf "**%s**" (escape t)
    | Metric(label, value) -> sprintf "**%s** %s" (escape label) (escape value)
    | Code t -> sprintf "`%s`" t

let private renderSpans (spans: InlineSpan list) : string =
    spans |> List.map renderSpan |> String.concat ""

let private renderElement (options: RenderOptions) (sb: StringBuilder) (el: NarrativeElement) : unit =
    match el with
    | Paragraph spans ->
        sb.AppendLine(renderSpans spans) |> ignore
        sb.AppendLine() |> ignore
    | BulletList items ->
        for spans in items do
            sb.Append("- ").AppendLine(renderSpans spans) |> ignore

        sb.AppendLine() |> ignore
    | OrderedList items ->
        items
        |> List.iteri (fun i spans -> sb.AppendFormat("{0}. ", i + 1).AppendLine(renderSpans spans) |> ignore)

        sb.AppendLine() |> ignore
    | KeyValueGrid pairs ->
        for (label, spans) in pairs do
            sb.AppendFormat("- **{0}:** {1}", escape label, renderSpans spans).AppendLine()
            |> ignore

        sb.AppendLine() |> ignore
    | Table(columns, rows) ->
        let headerCells = columns |> List.map (fst >> escape)

        let alignMarker (a: TableAlignment) =
            match a with
            | Left -> ":---"
            | Right -> "---:"
            | Center -> ":---:"

        let alignCells = columns |> List.map (snd >> alignMarker)

        sb.Append("| ").Append(String.concat " | " headerCells).AppendLine(" |")
        |> ignore

        sb.Append("| ").Append(String.concat " | " alignCells).AppendLine(" |")
        |> ignore

        for row in rows do
            let cells = row |> List.map renderSpans
            sb.Append("| ").Append(String.concat " | " cells).AppendLine(" |") |> ignore

        sb.AppendLine() |> ignore
    | Callout(severity, spans) ->
        match options.AdmonitionStyle with
        | Blockquote ->
            sb.Append(blockquoteMarker severity).AppendLine(renderSpans spans) |> ignore
            sb.AppendLine() |> ignore
        | Directive ->
            sb.AppendFormat(":::{0}", directiveTag severity).AppendLine() |> ignore
            sb.AppendLine(renderSpans spans) |> ignore
            sb.AppendLine(":::") |> ignore
            sb.AppendLine() |> ignore
    | Divider ->
        sb.AppendLine("---") |> ignore
        sb.AppendLine() |> ignore

let private renderSection (options: RenderOptions) (sb: StringBuilder) (section: NarrativeSection) : unit =
    sb.AppendFormat("## {0}", section.Heading).AppendLine() |> ignore
    sb.AppendLine() |> ignore

    match section.Subheading with
    | Some sh ->
        sb.AppendFormat("_{0}_", escape sh).AppendLine() |> ignore
        sb.AppendLine() |> ignore
    | None -> ()

    for el in section.Elements do
        renderElement options sb el

/// Render a `NarrativeDocument` to a markdown string with caller-supplied
/// options. Title becomes `# `, sections `## `, `Strong` → `**`,
/// `Emphasis` → `_`, `Metric` → bold label followed by value. Callouts
/// follow `options.AdmonitionStyle` — blockquote (default) or `:::severity`
/// directive.
let renderWith (options: RenderOptions) (doc: NarrativeDocument) : string =
    let sb = StringBuilder()
    sb.AppendFormat("# {0}", doc.Title).AppendLine() |> ignore
    sb.AppendLine() |> ignore

    match doc.Subtitle with
    | Some sub ->
        sb.AppendFormat("_{0}_", escape sub).AppendLine() |> ignore
        sb.AppendLine() |> ignore
    | None -> ()

    for section in doc.Sections do
        renderSection options sb section

    sb.ToString().TrimEnd()

/// Render a `NarrativeDocument` to a markdown string with default options
/// (blockquote callouts). Byte-for-byte identical to the prior render
/// output (GP 11).
let render (doc: NarrativeDocument) : string = renderWith RenderOptions.Default doc