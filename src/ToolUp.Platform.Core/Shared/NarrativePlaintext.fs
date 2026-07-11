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

let rec private renderSpan (span: InlineSpan) : string =
    match span with
    | Text t -> t
    | Emphasis t -> t
    | Strong t -> t
    // Phase 521 — plaintext has no attribute channel, so the optional
    // `factRef` is intentionally dropped (the plaintext form is unchanged).
    // An empty label degrades to the bare value (the fact-bearing metric
    // grid carries the label elsewhere).
    | Metric(label, value, _) -> if label = "" then value else sprintf "%s = %s" label value
    | Code t -> t
    | Link(href, spans) ->
        // Plaintext readers can't follow href on their own; surface the
        // URL after the visible label so RSS / email / terminal readers
        // can still copy-paste it.
        sprintf "%s (%s)" (renderSpans spans) href
    | Image(_, alt, _) ->
        // Image renders as bracketed alt text — the universally-portable
        // accessibility fallback. Title is dropped (no readable place
        // for it in plaintext).
        sprintf "[%s]" alt
    // Br collapses to a single newline. Within a Paragraph (which
    // appends its own terminator), this produces the visual break the
    // author intended; consecutive `Br`s degrade gracefully to blank
    // lines.
    | Br -> "\n"

and private renderSpans (spans: InlineSpan list) : string =
    spans |> List.map renderSpan |> String.concat ""

let private clampHeadingLevel (level: int) : int =
    if level < 3 then 3
    elif level > 6 then 6
    else level

let rec private renderElement (sb: StringBuilder) (el: NarrativeElement) : unit =
    match el with
    | Paragraph spans -> sb.AppendLine(renderSpans spans) |> ignore
    | Heading(level, spans) ->
        let rendered = renderSpans spans
        let _ = clampHeadingLevel level
        // Plaintext expresses heading hierarchy through indentation +
        // underline weight. H3 uses a `-` underline; H4 uses a `.` underline.
        // Deeper levels collapse to indented bold text without an underline.
        let underline =
            match clampHeadingLevel level with
            | 3 -> Some "-"
            | 4 -> Some "."
            | _ -> None

        sb.AppendLine(rendered) |> ignore

        match underline with
        | Some marker -> sb.AppendLine(String.replicate rendered.Length marker) |> ignore
        | None -> ()
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
    | CodeBlock(_, content) ->
        // Plaintext can't express syntax; indent every line by four
        // spaces — the universally-portable preformatted convention.
        for line in content.Replace("\r\n", "\n").Split('\n') do
            sb.AppendFormat("    {0}", line).AppendLine() |> ignore
    | Blockquote(citation, spans) ->
        sb.Append("    ").AppendLine(renderSpans spans) |> ignore

        match citation with
        | Some c -> sb.AppendFormat("      — {0}", c).AppendLine() |> ignore
        | None -> ()
    | Divider -> sb.AppendLine("---") |> ignore
    // ─── Phase 87 — media + layout blocks (caption / alt-text only) ──
    | Video spec ->
        match spec.Caption with
        | Some c -> sb.AppendFormat("[Video: {0}]", c).AppendLine() |> ignore
        | None -> sb.AppendLine("[Video]") |> ignore
    | Audio spec ->
        match spec.Caption with
        | Some c -> sb.AppendFormat("[Audio: {0}]", c).AppendLine() |> ignore
        | None -> sb.AppendLine("[Audio]") |> ignore
    | ImageGallery images ->
        for img in images do
            match img.Caption with
            | Some c -> sb.AppendFormat("  [{0}] — {1}", img.Alt, c).AppendLine() |> ignore
            | None -> sb.AppendFormat("  [{0}]", img.Alt).AppendLine() |> ignore
    | Embed spec ->
        // Surface the URL after the title so terminal / email / RSS
        // readers can copy-paste it (same convention as `Link`).
        sb.AppendFormat("[Embed: {0}] ({1})", spec.Title, spec.Url).AppendLine()
        |> ignore
    | Card spec ->
        match spec.Heading with
        | Some h -> sb.AppendLine(h) |> ignore
        | None -> ()

        for child in spec.Body do
            renderElement sb child
    | Accordion panels ->
        for (heading, body) in panels do
            sb.AppendLine(heading) |> ignore

            for child in body do
                renderElement sb child
    | Tabs panels ->
        for (label, body) in panels do
            sb.AppendLine(label) |> ignore

            for child in body do
                renderElement sb child
    | Component(name, _) -> sb.AppendFormat("[component: {0}]", name).AppendLine() |> ignore

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