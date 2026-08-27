// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Docx.NarrativeOoxml

open System.Xml.Linq
open ToolUp.OpenXml
open ToolUp.Platform.Narrative

// ─── NarrativeDocument → OOXML projection (Phase 575) ────────────────
//
// `NarrativeDocument` is the platform's structured prose model; this
// module projects one onto `ToolUp.OpenXml`'s structural block model, so
// a narrative reaches a `.docx` as native Word structures — styled
// heading paragraphs, numbered / bulleted list items, tables, shaded
// callout cells — rather than as flattened text.
//
// Everything is built through the structural model, never through raw
// OpenXml SDK calls (the Phase 23 design note). Where Word expresses a
// construct the model does not name in a typed field — cell shading,
// paragraph borders, monospace fonts, cell alignment — the projection
// uses the model's own verbatim-XML escape (`RawProperties`), which
// emission re-attaches unchanged. That is the sanctioned channel, and it
// keeps the OpenXml dependency exactly where it already is.
//
// Degradation posture. Every `NarrativeElement` case is projected; none
// is dropped. The cases Word has no analogue for degrade to their
// accessible text — a `Video` / `Audio` / `Embed` becomes its caption /
// title plus the source URL, an inline `Image` becomes its bracketed alt
// text — which is the same posture the Markdown and plaintext renderers
// take. Two things the richer formats carry are deliberately not
// projected, and are named here rather than left to be discovered: a
// `CodeBlock`'s language hint (Word has no fence to tag) and a `Link`'s
// click target as a live hyperlink (a `w:hyperlink` needs a relationship,
// which the structural model does not carry) — the href is emitted as
// visible text beside the label instead, exactly as plaintext does.

/// What a component renderer produced for a `Component(name, props)`
/// block. The registry is supplied by the caller — the composition root
/// registers a chart renderer against it — so Reporting takes no
/// dependency on any rendering companion (GP 1).
type ComponentResult =
    /// An SVG document as text.
    | Svg of svg: string
    /// A raster image plus its MIME type.
    | Image of bytes: byte[] * mimeType: string
    /// The renderer declines (or none is registered): render the
    /// component's data-table degradation.
    | Fallback

/// Component-renderer registry: block name → props → result. A name the
/// registry does not know returns `Fallback`.
type ComponentRenderers = string -> Map<string, string> -> ComponentResult

/// Tunables for `projectWith`. The two numbering ids name the numbering
/// instances the projected `BulletList` / `OrderedList` items reference;
/// `ensureListNumbering` defines them in the document's numbering part.
/// They must not collide with a numbering id the host document already
/// declares — `freeNumberingIds` picks a safe pair from an imported
/// document.
type ProjectionOptions = {
    ComponentRenderers: ComponentRenderers
    BulletNumberingId: int
    OrderedNumberingId: int
} with

    static member Default = {
        ComponentRenderers = fun _ _ -> Fallback
        BulletNumberingId = 900
        OrderedNumberingId = 901
    }

// ─── Verbatim property fragments ─────────────────────────────────────
//
// Each fragment declares its own `xmlns:w`, because the SDK parses these
// as standalone element XML (the same contract `Import` captures them
// under).

[<Literal>]
let private WordprocessingNs =
    "http://schemas.openxmlformats.org/wordprocessingml/2006/main"

let private wNs = XNamespace.Get WordprocessingNs

let private properties (tag: string) (inner: string) : string =
    sprintf "<w:%s xmlns:w=\"%s\">%s</w:%s>" tag WordprocessingNs inner tag

let private monospaceRunProperties =
    properties "rPr" "<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/>"

let private codeParagraphProperties =
    properties
        "pPr"
        "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/><w:spacing w:before=\"120\" w:after=\"120\"/>"

let private quoteParagraphProperties = properties "pPr" "<w:ind w:left=\"720\"/>"

let private dividerParagraphProperties =
    properties "pPr" "<w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"auto\"/></w:pBdr>"

/// A callout is a full-width single-cell table with no borders — the
/// Word idiom for a shaded advisory block.
let private calloutTableProperties =
    properties "tblPr" "<w:tblW w:w=\"5000\" w:type=\"pct\"/>"

let private shadedCellProperties (fill: string) =
    properties "tcPr" (sprintf "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"%s\"/>" fill)

let private alignmentParagraphProperties (alignment: TableAlignment) : string option =
    match alignment with
    | Left -> None
    | Right -> Some(properties "pPr" "<w:jc w:val=\"right\"/>")
    | Center -> Some(properties "pPr" "<w:jc w:val=\"center\"/>")

// ─── Runs ────────────────────────────────────────────────────────────

let private boldFormatting = { RunFormatting.none with Bold = true }

let private italicFormatting = {
    RunFormatting.none with
        Italic = true
}

let private monospaceFormatting = {
    RunFormatting.none with
        RawProperties = Some monospaceRunProperties
}

let private styled (formatting: RunFormatting) (text: string) : Run = {
    Run.plain text with
        Formatting = formatting
}

/// Add a boolean flag to a run's formatting. A run already carrying
/// verbatim `w:rPr` is left alone — emission honours the raw payload and
/// ignores the booleans, so setting one would be a silent no-op.
let private withFlag (set: RunFormatting -> RunFormatting) (run: Run) : Run =
    match run.Formatting.RawProperties with
    | Some _ -> run
    | None -> {
        run with
            Formatting = set run.Formatting
      }

let rec private spanRuns (span: InlineSpan) : Run list =
    match span with
    | InlineSpan.Text text -> [ Run.plain text ]
    | Emphasis text -> [ styled italicFormatting text ]
    | Strong text -> [ styled boldFormatting text ]
    | Metric(label, value, _) ->
        // Mirrors the Markdown form (bold label, plain value). An empty
        // label degrades to the bare value — the fact-bearing metric grid
        // carries the label as its own key.
        if label = "" then
            [ Run.plain value ]
        else
            [ styled boldFormatting label; Run.plain (" " + value) ]
    | InlineSpan.Code text -> [ styled monospaceFormatting text ]
    | Link(href, spans) ->
        // No live hyperlink (see the module header): the visible label is
        // underlined and the target follows it as text, so a printed or
        // pasted document keeps the address.
        (spans
         |> List.collect spanRuns
         |> List.map (withFlag (fun f -> { f with Underline = true })))
        @ [ Run.plain (sprintf " (%s)" href) ]
    | InlineSpan.Image(_, alt, _) -> [ Run.plain (sprintf "[%s]" alt) ]
    // The model normalises '\n' back to `w:br` at emission.
    | Br -> [ Run.plain "\n" ]

let private runsOf (spans: InlineSpan list) : Run list = spans |> List.collect spanRuns

// ─── Blocks ──────────────────────────────────────────────────────────

let private paragraphOf (rawProperties: string option) (runs: Run list) : ParagraphModel = {
    ParagraphModel.create runs with
        RawProperties = rawProperties
}

let private plainParagraph (runs: Run list) : Block =
    Block.Paragraph(ParagraphModel.create runs)

let private cellOf (fill: string option) (blocks: Block list) : TableCell = {
    Blocks = blocks
    RawProperties = fill |> Option.map shadedCellProperties
}

let private rowOf (cells: TableCell list) : TableRow = { Cells = cells; RawProperties = None }

let private tableOf (rawProperties: string option) (rows: TableRow list) : Block =
    Block.Table {
        Rows = rows
        RawProperties = rawProperties
        RawGrid = None
    }

let private severityLabel (severity: Severity) =
    match severity with
    | Info -> "Info"
    | Notice -> "Note"
    | Warning -> "Warning"
    | Critical -> "Critical"

/// Cell shading per severity. Word theme tints — light enough that black
/// body text stays legible when the document is printed greyscale.
let private severityFill (severity: Severity) =
    match severity with
    | Info -> "DEEAF6"
    | Notice -> "E2EFD9"
    | Warning -> "FFF2CC"
    | Critical -> "F4CCCC"

/// Element headings sit below the document title (H1) and the section
/// heading (H2), so they clamp into 3..6 exactly as the Markdown, HTML
/// and plaintext renderers do.
let private clampHeadingLevel (level: int) : int =
    if level < 3 then 3
    elif level > 6 then 6
    else level

/// The component's props as a two-column table — the data-table
/// degradation. Props come from an F# `Map`, so the row order is the
/// key order and the projection is deterministic.
let private propsTable (props: Map<string, string>) : Block list =
    if Map.isEmpty props then
        []
    else
        let header =
            rowOf [
                cellOf None [ plainParagraph [ styled boldFormatting "Property" ] ]
                cellOf None [ plainParagraph [ styled boldFormatting "Value" ] ]
            ]

        let dataRow (key: string) (value: string) =
            rowOf [
                cellOf None [ plainParagraph [ Run.plain key ] ]
                cellOf None [ plainParagraph [ Run.plain value ] ]
            ]

        [
            tableOf None (header :: (props |> Map.toList |> List.map (fun (k, v) -> dataRow k v)))
        ]

/// Render a resolved component result.
///
/// **Phase 576 closed the seam this comment used to describe.** A
/// resolved `Svg` / `Image` result now becomes a real embedded figure
/// through `Figures.svg` / `Figures.image` — the block that carries the
/// payload and lowers to a `w:drawing` plus its image parts. The
/// `Fallback` branch is unchanged: a component no renderer claims still
/// degrades to its bracketed marker plus the data table, so the reader
/// loses the picture but never the content.
///
/// Sizing is `FigureSize.Intrinsic`: an SVG is measured from its own
/// `viewBox`, a PNG or JPEG from its header. A renderer that wants a
/// specific on-page size scales its own payload, which is the only
/// place that knows what the figure is FOR.
///
/// **The PNG fallback part is deliberately not produced here.**
/// `ISvgRasterizer` is asynchronous (GP 12 rule 2) and this projection
/// is a pure synchronous function, so threading a rasteriser through it
/// would mean making the whole projection async for a part that only
/// pre-2016 Office clients read. A caller that needs the fallback
/// rasterises ahead of the projection and returns
/// `ComponentResult.Image`, or builds the figure with
/// `Figures.svgNamedWith` and places it directly.
let private componentBlocks (options: ProjectionOptions) (name: string) (props: Map<string, string>) : Block list =
    let unresolved marker =
        plainParagraph [ Run.plain marker ] :: propsTable props

    // The component's registered name is the only identity available
    // here, so it serves as both Word's selection-pane label and the
    // figure's accessible description — a figure with no alt text at
    // all would be worse for a screen reader than an imperfect one.
    let figureName = sprintf "Component: %s" name

    match options.ComponentRenderers name props with
    | ComponentResult.Svg svg -> [ Figures.svgNamed figureName (Some figureName) svg FigureSize.Intrinsic None ]
    | ComponentResult.Image(bytes, mimeType) -> [
        Figures.imageNamed figureName (Some figureName) bytes mimeType FigureSize.Intrinsic
      ]
    | Fallback -> unresolved (sprintf "[component: %s]" name)

let rec private elementBlocks (options: ProjectionOptions) (element: NarrativeElement) : Block list =
    let listItem (numberingId: int) (spans: InlineSpan list) =
        Block.ListItem({ NumberingId = numberingId; Level = 0 }, ParagraphModel.create (runsOf spans))

    match element with
    | Paragraph spans -> [ plainParagraph (runsOf spans) ]
    | Heading(level, spans) -> [ Block.Heading(clampHeadingLevel level, ParagraphModel.create (runsOf spans)) ]
    | BulletList items -> items |> List.map (listItem options.BulletNumberingId)
    | OrderedList items -> items |> List.map (listItem options.OrderedNumberingId)
    | KeyValueGrid pairs ->
        let row (label, spans) =
            rowOf [
                cellOf None [ plainParagraph [ styled boldFormatting label ] ]
                cellOf None [ plainParagraph (runsOf spans) ]
            ]

        [ tableOf None (pairs |> List.map row) ]
    | NarrativeElement.Table(columns, rows) ->
        let alignmentAt index =
            columns |> List.tryItem index |> Option.map snd |> Option.defaultValue Left

        let headerCell (name, alignment) =
            cellOf None [
                Block.Paragraph(paragraphOf (alignmentParagraphProperties alignment) [ styled boldFormatting name ])
            ]

        let dataRow cells =
            cells
            |> List.mapi (fun index spans ->
                cellOf None [
                    Block.Paragraph(paragraphOf (alignmentParagraphProperties (alignmentAt index)) (runsOf spans))
                ])
            |> rowOf

        [
            tableOf None (rowOf (columns |> List.map headerCell) :: (rows |> List.map dataRow))
        ]
    | Callout(severity, spans) ->
        let runs = styled boldFormatting (severityLabel severity + ": ") :: runsOf spans

        [
            tableOf (Some calloutTableProperties) [
                rowOf [ cellOf (Some(severityFill severity)) [ plainParagraph runs ] ]
            ]
        ]
    | CodeBlock(_, content) -> [
        Block.Paragraph(paragraphOf (Some codeParagraphProperties) [ styled monospaceFormatting content ])
      ]
    | NarrativeElement.Blockquote(citation, spans) ->
        let quoted = runsOf spans |> List.map (withFlag (fun f -> { f with Italic = true }))

        let quote = Block.Paragraph(paragraphOf (Some quoteParagraphProperties) quoted)

        match citation with
        | Some cite -> [
            quote
            Block.Paragraph(paragraphOf (Some quoteParagraphProperties) [ styled italicFormatting ("— " + cite) ])
          ]
        | None -> [ quote ]
    | Divider -> [ Block.Paragraph(paragraphOf (Some dividerParagraphProperties) []) ]
    | Video spec ->
        let label = spec.Caption |> Option.defaultValue "Video"
        [ plainParagraph (mediaRuns "Video" label (spec.Sources |> List.tryHead)) ]
    | Audio spec ->
        let label = spec.Caption |> Option.defaultValue "Audio"
        [ plainParagraph (mediaRuns "Audio" label (spec.Sources |> List.tryHead)) ]
    | ImageGallery images ->
        images
        |> List.collect (fun image ->
            let figure = plainParagraph [ Run.plain (sprintf "[%s] (%s)" image.Alt image.Src) ]

            match image.Caption with
            | Some caption -> [ figure; plainParagraph [ styled italicFormatting caption ] ]
            | None -> [ figure ])
    | Embed spec -> [ plainParagraph [ Run.plain (sprintf "%s (%s)" spec.Title spec.Url) ] ]
    | Card spec ->
        (spec.Heading
         |> Option.map (fun heading -> Block.Heading(3, ParagraphModel.create [ Run.plain heading ]))
         |> Option.toList)
        @ (spec.Image
           |> Option.map (fun image -> plainParagraph [ Run.plain (sprintf "[%s] (%s)" image.Alt image.Src) ])
           |> Option.toList)
        @ (spec.Body |> List.collect (elementBlocks options))
    | Accordion panels
    | Tabs panels ->
        // Word has no interactive disclosure; every panel is emitted with
        // its label as a bold lead paragraph, so no panel body is hidden.
        panels
        |> List.collect (fun (label, body) ->
            plainParagraph [ styled boldFormatting label ]
            :: (body |> List.collect (elementBlocks options)))
    | Component(name, props) -> componentBlocks options name props

/// A media block's degradation: the kind marker, the visible caption,
/// and the first source URL when the block declares one.
and private mediaRuns (kind: string) (label: string) (source: MediaSource option) : Run list =
    [ styled boldFormatting (sprintf "[%s] " kind); Run.plain label ]
    @ (source
       |> Option.map (fun media -> Run.plain (sprintf " (%s)" media.Src))
       |> Option.toList)

let private sectionBlocks (options: ProjectionOptions) (section: NarrativeSection) : Block list =
    Block.Heading(2, ParagraphModel.create [ Run.plain section.Heading ])
    :: (section.Subheading
        |> Option.map (fun subheading -> plainParagraph [ styled italicFormatting subheading ])
        |> Option.toList)
    @ (section.Elements |> List.collect (elementBlocks options))

/// Project a narrative document onto structural blocks with
/// caller-supplied options. The document title becomes a level-1 heading
/// and each section heading a level-2 heading, matching the implicit
/// hierarchy every other narrative renderer assumes.
let projectWith (options: ProjectionOptions) (document: NarrativeDocument) : Block list =
    Block.Heading(1, ParagraphModel.create [ Run.plain document.Title ])
    :: (document.Subtitle
        |> Option.map (fun subtitle -> plainParagraph [ styled italicFormatting subtitle ])
        |> Option.toList)
    @ (document.Sections |> List.collect (sectionBlocks options))

/// Project a narrative document onto structural blocks, resolving
/// `Component` blocks through the supplied registry and taking the
/// default numbering ids.
let project (componentRenderers: ComponentRenderers) (document: NarrativeDocument) : Block list =
    projectWith
        {
            ProjectionOptions.Default with
                ComponentRenderers = componentRenderers
        }
        document

// ─── Numbering definitions ───────────────────────────────────────────
//
// A `ListItem` block carries a numbering reference, and Word renders it
// as a list only when the numbering part defines that instance. A
// programmatic model gets a generated part for free, but a model imported
// from a template already carries its own — and the ids this projection
// mints are not in it. `ensureListNumbering` closes that gap by adding
// the missing definitions to whichever part the model has, so the same
// projection numbers correctly in both paths.

let private numberingLevel (bullet: bool) (levelIndex: int) : XElement =
    let indent = 720 * (levelIndex + 1)

    XElement(
        wNs + "lvl",
        XAttribute(wNs + "ilvl", levelIndex),
        XElement(wNs + "start", XAttribute(wNs + "val", 1)),
        XElement(wNs + "numFmt", XAttribute(wNs + "val", (if bullet then "bullet" else "decimal"))),
        XElement(
            wNs + "lvlText",
            XAttribute(
                wNs + "val",
                (if bullet then
                     string (char 0x2022)
                 else
                     sprintf "%%%d." (levelIndex + 1))
            )
        ),
        XElement(wNs + "lvlJc", XAttribute(wNs + "val", "left")),
        XElement(wNs + "pPr", XElement(wNs + "ind", XAttribute(wNs + "left", indent), XAttribute(wNs + "hanging", 360)))
    )

let private abstractNumElement (bullet: bool) (id: int) : XElement =
    XElement(
        wNs + "abstractNum",
        XAttribute(wNs + "abstractNumId", id),
        XElement(wNs + "multiLevelType", XAttribute(wNs + "val", "hybridMultilevel")),
        [ for levelIndex in 0..3 -> numberingLevel bullet levelIndex ]
    )

let private numElement (id: int) : XElement =
    XElement(wNs + "num", XAttribute(wNs + "numId", id), XElement(wNs + "abstractNumId", XAttribute(wNs + "val", id)))

let rec private listNumberingIds (blocks: Block list) : int list =
    blocks
    |> List.collect (fun block ->
        match block with
        | Block.ListItem(numbering, _) -> [ numbering.NumberingId ]
        | Block.Table table ->
            table.Rows
            |> List.collect (fun row -> row.Cells |> List.collect (fun cell -> listNumberingIds cell.Blocks))
        | _ -> [])

/// Pick a bullet / ordered numbering-id pair that cannot collide with an
/// id the document already declares. Both the numbering-instance ids and
/// the abstract ids they name are avoided, because the definitions this
/// module writes use one id for both.
let freeNumberingIds (numbering: NumberingDefinitions) : int * int =
    let used =
        numbering.Instances
        |> List.collect (fun instance -> instance.NumberingId :: Option.toList instance.AbstractNumberingId)

    let first =
        match used with
        | [] -> ProjectionOptions.Default.BulletNumberingId
        | ids -> List.max ids + 1

    first, first + 1

/// Define every numbering id the model's list items reference that its
/// numbering part does not already declare. A model whose lists are all
/// declared is returned unchanged, so a render that projected no
/// narrative is byte-for-byte as it was (GP 11).
let ensureListNumbering (options: ProjectionOptions) (model: DocModel) : DocModel =
    let declared = model.Numbering.Instances |> List.map _.NumberingId |> Set.ofList

    let missing =
        model.Sections
        |> List.collect (fun section -> listNumberingIds section.Blocks)
        |> List.distinct
        |> List.sort
        |> List.filter (declared.Contains >> not)

    if List.isEmpty missing then
        model
    else
        let root =
            match model.Numbering.RawXml with
            | Some xml -> XElement.Parse xml
            | None -> XElement(wNs + "numbering", XAttribute(XNamespace.Xmlns + "w", WordprocessingNs))

        // Schema order: every w:abstractNum precedes the first w:num.
        for id in missing do
            let element = abstractNumElement (id = options.BulletNumberingId) id

            match root.Elements(wNs + "abstractNum") |> Seq.tryLast with
            | Some last -> last.AddAfterSelf element
            | None -> root.AddFirst element

        for id in missing do
            root.Add(numElement id)

        {
            model with
                Numbering = {
                    Instances =
                        model.Numbering.Instances
                        @ (missing
                           |> List.map (fun id -> {
                               NumberingId = id
                               AbstractNumberingId = Some id
                           }))
                    RawXml = Some(root.ToString SaveOptions.DisableFormatting)
                }
        }