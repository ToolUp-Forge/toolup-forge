// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Narrative.NarrativeHtml

open System.Text

/// How `Image` elements declare their loading hint. `Eager` (default)
/// emits no attribute — the browser loads the image as part of the
/// initial render. `Lazy` emits `loading="lazy"` — defers off-screen
/// images until the user scrolls. The Lazy choice trades a tiny
/// hydration cost for materially better Lighthouse / Core Web Vitals
/// scores on long pages, so most marketing / docs deployments want it
/// on; analytical-render contexts (in-app narratives that mount
/// already-visible) should leave it Eager.
type ImageLoading =
    | Eager
    | Lazy

/// Tunables for `renderWith`. Defaults preserve the historical output
/// byte-for-byte (GP 11) — every option's default value matches what
/// the no-options `render` produced before this record existed.
type RenderOptions = {
    /// `<img loading="...">` hint. Defaults to `Eager` (no attribute
    /// emitted) for back-compat; flip to `Lazy` for SSR pages where
    /// most images sit below the fold.
    ImageLoading: ImageLoading
    /// When `Some rel`, every `Link` whose `href` is an absolute URL
    /// (starts with `http://` or `https://`) gains `rel="..."`. The
    /// canonical marketing-site value is `"noopener nofollow"`
    /// (security + crawl-budget signal). `None` (default) emits no
    /// `rel` attribute.
    ExternalLinkRel: string option
    /// Offset added to every `Heading` level before clamping. Useful
    /// when the rendered fragment will be embedded inside an existing
    /// document heading hierarchy (e.g. a Narrative section embedded
    /// inside a Markdown layout whose own H2 already exists). `0`
    /// (default) preserves the document's declared levels.
    HeadingLevelOffset: int
    /// When `true` (default), every `NarrativeSection` is wrapped in
    /// `<section id="...">`. When `false`, sections render without
    /// the wrapping `<section>` element — only the H2 + content. Used
    /// by render contexts that already supply their own sectioning
    /// (e.g. a single-section embed inside a layout that handles the
    /// wrapper).
    EmitSectionAnchors: bool
} with

    static member Default = {
        ImageLoading = Eager
        ExternalLinkRel = None
        HeadingLevelOffset = 0
        EmitSectionAnchors = true
    }

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

let private isAbsoluteHref (href: string) =
    href.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase)
    || href.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase)

let rec private renderSpan (options: RenderOptions) (span: InlineSpan) : string =
    match span with
    | Text t -> escape t
    | Emphasis t -> sprintf "<em>%s</em>" (escape t)
    | Strong t -> sprintf "<strong>%s</strong>" (escape t)
    | Metric(label, value) ->
        sprintf "<span class=\"narrative-metric\"><strong>%s</strong> %s</span>" (escape label) (escape value)
    | Code t -> sprintf "<code>%s</code>" (escape t)
    | Link(href, spans) ->
        let relAttr =
            match options.ExternalLinkRel with
            | Some rel when isAbsoluteHref href -> sprintf " rel=\"%s\"" (escape rel)
            | _ -> ""

        sprintf "<a href=\"%s\"%s>%s</a>" (escape href) relAttr (renderSpans options spans)
    | Image(src, alt, title) ->
        let loadingAttr =
            match options.ImageLoading with
            | Lazy -> " loading=\"lazy\""
            | Eager -> ""

        match title with
        | Some t ->
            sprintf "<img src=\"%s\" alt=\"%s\" title=\"%s\"%s />" (escape src) (escape alt) (escape t) loadingAttr
        | None -> sprintf "<img src=\"%s\" alt=\"%s\"%s />" (escape src) (escape alt) loadingAttr
    | Br -> "<br />"

and private renderSpans (options: RenderOptions) (spans: InlineSpan list) : string =
    spans |> List.map (renderSpan options) |> String.concat ""

let private clampHeadingLevel (level: int) : int =
    if level < 3 then 3
    elif level > 6 then 6
    else level

let private alignAttr (a: TableAlignment) =
    match a with
    | Left -> " style=\"text-align:left\""
    | Right -> " style=\"text-align:right\""
    | Center -> " style=\"text-align:center\""

let private renderElement (options: RenderOptions) (sb: StringBuilder) (el: NarrativeElement) : unit =
    let renderSpans' = renderSpans options

    match el with
    | Paragraph spans -> sb.AppendFormat("<p>{0}</p>", renderSpans' spans).AppendLine() |> ignore
    | Heading(level, spans) ->
        let effective = clampHeadingLevel (level + options.HeadingLevelOffset)
        let tag = sprintf "h%d" effective

        sb.AppendFormat("<{0}>{1}</{0}>", tag, renderSpans' spans).AppendLine()
        |> ignore
    | BulletList items ->
        sb.AppendLine("<ul>") |> ignore

        for spans in items do
            sb.AppendFormat("  <li>{0}</li>", renderSpans' spans).AppendLine() |> ignore

        sb.AppendLine("</ul>") |> ignore
    | OrderedList items ->
        sb.AppendLine("<ol>") |> ignore

        for spans in items do
            sb.AppendFormat("  <li>{0}</li>", renderSpans' spans).AppendLine() |> ignore

        sb.AppendLine("</ol>") |> ignore
    | KeyValueGrid pairs ->
        sb.AppendLine("<dl class=\"narrative-kv\">") |> ignore

        for (label, spans) in pairs do
            sb.AppendFormat("  <dt>{0}</dt>", escape label).AppendLine() |> ignore
            sb.AppendFormat("  <dd>{0}</dd>", renderSpans' spans).AppendLine() |> ignore

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
                sb.AppendFormat("<td{0}>{1}</td>", alignAttr align, renderSpans' spans)
                |> ignore

            sb.AppendLine("</tr>") |> ignore

        sb.AppendLine("  </tbody>") |> ignore
        sb.AppendLine("</table>") |> ignore
    | Callout(severity, spans) ->
        sb
            .AppendFormat("<aside class=\"{0}\">", severityClass severity)
            .AppendFormat("<strong>{0}:</strong> ", severityLabel severity)
            .Append(renderSpans' spans)
            .AppendLine("</aside>")
        |> ignore
    | CodeBlock(language, content) ->
        let classAttr =
            match language with
            | Some lang -> sprintf " class=\"language-%s\"" (escape lang)
            | None -> ""

        sb.Append("<pre><code").Append(classAttr).Append(">").Append(escape content).AppendLine("</code></pre>")
        |> ignore
    | Blockquote(citation, spans) ->
        sb.Append("<blockquote>") |> ignore
        sb.AppendFormat("<p>{0}</p>", renderSpans' spans) |> ignore

        match citation with
        | Some c -> sb.AppendFormat("<cite>{0}</cite>", escape c) |> ignore
        | None -> ()

        sb.AppendLine("</blockquote>") |> ignore
    | Divider -> sb.AppendLine("<hr />") |> ignore

let private renderSection (options: RenderOptions) (sb: StringBuilder) (section: NarrativeSection) : unit =
    if options.EmitSectionAnchors then
        sb.AppendFormat("<section id=\"{0}\">", escape section.Id).AppendLine()
        |> ignore

    sb.AppendFormat("  <h2>{0}</h2>", escape section.Heading).AppendLine() |> ignore

    match section.Subheading with
    | Some sh ->
        sb.AppendFormat("  <p class=\"narrative-subheading\"><em>{0}</em></p>", escape sh).AppendLine()
        |> ignore
    | None -> ()

    for el in section.Elements do
        renderElement options sb el

    if options.EmitSectionAnchors then
        sb.AppendLine("</section>") |> ignore

/// Render a `NarrativeDocument` to an HTML fragment with caller-
/// supplied options. Output is *not* wrapped in `<html>` / `<body>` —
/// the caller embeds the fragment inside whatever page template they
/// own. Inline content is HTML-escaped; this is not a sanitiser.
let renderWith (options: RenderOptions) (doc: NarrativeDocument) : string =
    let sb = StringBuilder()

    sb.AppendLine("<article class=\"narrative\">") |> ignore

    sb.AppendFormat("  <h1>{0}</h1>", escape doc.Title).AppendLine() |> ignore

    match doc.Subtitle with
    | Some sub ->
        sb.AppendFormat("  <p class=\"narrative-subtitle\"><em>{0}</em></p>", escape sub).AppendLine()
        |> ignore
    | None -> ()

    for section in doc.Sections do
        renderSection options sb section

    sb.AppendLine("</article>") |> ignore
    sb.ToString().TrimEnd()

/// Render a `NarrativeDocument` to an HTML fragment with default
/// options. Byte-for-byte identical to the prior `render` output
/// (GP 11).
let render (doc: NarrativeDocument) : string = renderWith RenderOptions.Default doc

/// Render a `<nav class="narrative-toc">` listing every section as a
/// `<a href="#section-id">heading</a>` link. Optionally walks the
/// section's `Heading` elements (level 3 onwards) and surfaces them as
/// nested `<ul>` entries — when `includeHeadings = false` (default),
/// only top-level sections appear, matching the most common layout-
/// embedded table of contents.
///
/// Sub-heading anchors use `section.Id + "-" + slug(heading-text)`;
/// the renderer assumes the layout emits matching `id="..."` on the
/// heading elements (a future option-level flag may control this).
/// Caller embeds the returned fragment directly inside `<aside>` /
/// `<nav>` / wherever the layout wants the ToC to surface.
let tableOfContents (includeHeadings: bool) (doc: NarrativeDocument) : string =
    let sb = StringBuilder()
    sb.AppendLine("<nav class=\"narrative-toc\">") |> ignore
    sb.AppendLine("  <ul>") |> ignore

    let slug (s: string) =
        // Lowercase + replace runs of non-alphanumeric with `-` + trim
        // edge dashes. Deterministic across runs (no culture-sensitive
        // ops), Fable-safe (no `Regex`).
        let lower = s.ToLowerInvariant()

        let chars =
            lower
            |> Seq.map (fun c ->
                if System.Char.IsLetterOrDigit c then c
                elif c = '-' then '-'
                else '-')

        let mutable lastDash = false
        let buf = StringBuilder()

        for c in chars do
            if c = '-' then
                if not lastDash && buf.Length > 0 then
                    buf.Append('-') |> ignore
                    lastDash <- true
            else
                buf.Append(c) |> ignore
                lastDash <- false

        let s2 = buf.ToString()

        if s2.EndsWith("-") then
            s2.Substring(0, s2.Length - 1)
        else
            s2

    for section in doc.Sections do
        sb.AppendFormat("    <li><a href=\"#{0}\">{1}</a>", escape section.Id, escape section.Heading)
        |> ignore

        if includeHeadings then
            let sectionHeadings =
                section.Elements
                |> List.choose (fun el ->
                    match el with
                    | Heading(level, spans) when level <= 4 ->
                        let text =
                            spans
                            |> List.map (fun s ->
                                match s with
                                | Text t
                                | Emphasis t
                                | Strong t
                                | Code t -> t
                                | Metric(label, value) -> sprintf "%s %s" label value
                                | Link(_, inner) ->
                                    inner
                                    |> List.map (fun s ->
                                        match s with
                                        | Text t -> t
                                        | _ -> "")
                                    |> String.concat ""
                                | Image(_, alt, _) -> alt
                                | Br -> " ")
                            |> String.concat ""

                        Some(level, text)
                    | _ -> None)

            if not (List.isEmpty sectionHeadings) then
                sb.AppendLine() |> ignore
                sb.AppendLine("      <ul>") |> ignore

                for (_, text) in sectionHeadings do
                    sb
                        .AppendFormat(
                            "        <li><a href=\"#{0}-{1}\">{2}</a></li>",
                            escape section.Id,
                            slug text,
                            escape text
                        )
                        .AppendLine()
                    |> ignore

                sb.Append("      </ul>    ") |> ignore

        sb.AppendLine("</li>") |> ignore

    sb.AppendLine("  </ul>") |> ignore
    sb.AppendLine("</nav>") |> ignore
    sb.ToString().TrimEnd()