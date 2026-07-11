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

let private escapeUrl (s: string) : string =
    // CommonMark allows raw URLs inside `()` but `)` would close the
    // link target prematurely. Backslash-escape the two characters that
    // matter; everything else (spaces in URLs notwithstanding) is the
    // caller's problem to percent-encode before passing in.
    s.Replace("\\", "\\\\").Replace(")", "\\)")

let rec private renderSpan (span: InlineSpan) : string =
    match span with
    | Text t -> escape t
    | Emphasis t -> sprintf "_%s_" (escape t)
    | Strong t -> sprintf "**%s**" (escape t)
    | Metric(label, value, factRef) ->
        // Phase 521 — the number renders as before; an optional `factRef`
        // trails as an HTML annotation comment (Markdown passes raw HTML
        // through, and a comment is invisible once the Markdown is rendered
        // to HTML) so the fact pointer survives the Markdown form without
        // altering the visible prose. Absent a ref the output is
        // byte-identical to before (GP 11). An empty label drops the bold
        // wrapper (the fact-bearing metric grid supplies the label as the
        // grid key, not on the span).
        let core =
            if label = "" then
                escape value
            else
                sprintf "**%s** %s" (escape label) (escape value)

        match factRef with
        | Some f -> sprintf "%s<!--fact:%s-->" core f
        | None -> core
    | Code t -> sprintf "`%s`" t
    | Link(href, spans) -> sprintf "[%s](%s)" (renderSpans spans) (escapeUrl href)
    | Image(src, alt, title) ->
        match title with
        | Some t -> sprintf "![%s](%s \"%s\")" (escape alt) (escapeUrl src) (escape t)
        | None -> sprintf "![%s](%s)" (escape alt) (escapeUrl src)
    // CommonMark hard line break: two trailing spaces followed by a
    // newline. Renderers that strip trailing whitespace will break
    // this; the alternative `\` form is supported by GFM but not all
    // readers.
    | Br -> "  \n"

and private renderSpans (spans: InlineSpan list) : string =
    spans |> List.map renderSpan |> String.concat ""

let private clampHeadingLevel (level: int) : int =
    if level < 3 then 3
    elif level > 6 then 6
    else level

let rec private renderElement (options: RenderOptions) (sb: StringBuilder) (el: NarrativeElement) : unit =
    match el with
    | Paragraph spans ->
        sb.AppendLine(renderSpans spans) |> ignore
        sb.AppendLine() |> ignore
    | Heading(level, spans) ->
        let prefix = String.replicate (clampHeadingLevel level) "#"
        sb.AppendFormat("{0} {1}", prefix, renderSpans spans).AppendLine() |> ignore
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
        | AdmonitionStyle.Blockquote ->
            sb.Append(blockquoteMarker severity).AppendLine(renderSpans spans) |> ignore
            sb.AppendLine() |> ignore
        | Directive ->
            sb.AppendFormat(":::{0}", directiveTag severity).AppendLine() |> ignore
            sb.AppendLine(renderSpans spans) |> ignore
            sb.AppendLine(":::") |> ignore
            sb.AppendLine() |> ignore
    | CodeBlock(language, content) ->
        let fence = "```"
        let langTag = language |> Option.defaultValue ""
        sb.AppendFormat("{0}{1}", fence, langTag).AppendLine() |> ignore
        sb.AppendLine(content) |> ignore
        sb.AppendLine(fence) |> ignore
        sb.AppendLine() |> ignore
    | NarrativeElement.Blockquote(citation, spans) ->
        sb.Append("> ").AppendLine(renderSpans spans) |> ignore

        match citation with
        | Some c -> sb.AppendFormat("> — {0}", escape c).AppendLine() |> ignore
        | None -> ()

        sb.AppendLine() |> ignore
    | Divider ->
        sb.AppendLine("---") |> ignore
        sb.AppendLine() |> ignore
    // ─── Phase 87 — media + layout blocks (graceful degradation) ─────
    | Video spec ->
        // Markdown has no <video>; degrade to the poster image (if any)
        // plus a link to the first source. The caption is the link text.
        let label = spec.Caption |> Option.defaultValue "Video"

        match spec.Poster with
        | Some poster ->
            sb.AppendFormat("![{0}]({1})", escape label, escapeUrl poster).AppendLine()
            |> ignore
        | None -> ()

        match spec.Sources |> List.tryHead with
        | Some source ->
            sb.AppendFormat("[▶ {0}]({1})", escape label, escapeUrl source.Src).AppendLine()
            |> ignore
        | None -> sb.AppendLine(escape label) |> ignore

        sb.AppendLine() |> ignore
    | Audio spec ->
        let label = spec.Caption |> Option.defaultValue "Audio"

        match spec.Sources |> List.tryHead with
        | Some source ->
            sb.AppendFormat("[♪ {0}]({1})", escape label, escapeUrl source.Src).AppendLine()
            |> ignore
        | None -> sb.AppendLine(escape label) |> ignore

        sb.AppendLine() |> ignore
    | ImageGallery images ->
        for img in images do
            sb.AppendFormat("![{0}]({1})", escape img.Alt, escapeUrl img.Src).AppendLine()
            |> ignore

            match img.Caption with
            | Some c -> sb.AppendFormat("_{0}_", escape c).AppendLine() |> ignore
            | None -> ()

            sb.AppendLine() |> ignore
    | Embed spec ->
        sb.AppendFormat("[{0}]({1})", escape spec.Title, escapeUrl spec.Url).AppendLine().AppendLine()
        |> ignore
    | Card spec ->
        match spec.Heading with
        | Some h ->
            sb.AppendFormat("### {0}", escape h).AppendLine() |> ignore
            sb.AppendLine() |> ignore
        | None -> ()

        match spec.Image with
        | Some img ->
            sb.AppendFormat("![{0}]({1})", escape img.Alt, escapeUrl img.Src).AppendLine()
            |> ignore

            sb.AppendLine() |> ignore
        | None -> ()

        for child in spec.Body do
            renderElement options sb child
    | Accordion panels ->
        for (heading, body) in panels do
            sb.AppendFormat("**{0}**", escape heading).AppendLine() |> ignore
            sb.AppendLine() |> ignore

            for child in body do
                renderElement options sb child
    | Tabs panels ->
        for (label, body) in panels do
            sb.AppendFormat("**{0}**", escape label).AppendLine() |> ignore
            sb.AppendLine() |> ignore

            for child in body do
                renderElement options sb child
    | Component(name, _) ->
        // No Markdown analogue; leave a deterministic, harmless marker
        // so a round-tripping CMS editor can locate the block.
        sb.AppendFormat("<!-- narrative-component: {0} -->", name).AppendLine()
        |> ignore

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