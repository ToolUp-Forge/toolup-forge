// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Emission: `DocModel` → `.docx`.
///
/// Imported payloads carried verbatim (`RawProperties` / `RawXml` /
/// `OpaqueBlock`) are re-attached unchanged, so import → emit
/// round-trips the captured structure. Programmatic models (no raw
/// payloads) get sensible generated parts: a minimal styles part
/// covering the heading levels in use, a decimal numbering
/// definition per numbering id in use, and bordered table defaults.
/// Revision marks lower to native `w:ins` / `w:del` (runs and
/// paragraph marks) with per-author + timestamp attribution and
/// document-unique revision ids; paragraph comment anchors lower to
/// `w:commentRangeStart` / `End` + `w:commentReference` plus the
/// comments part.
module ToolUp.OpenXml.Emit

open System
open System.IO
open System.Text
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging

/// Namespace declarations Word itself stamps on `w:document`.
/// Declaring them up front lets opaque-carried content (drawings,
/// content controls) that references the standard prefixes
/// round-trip without re-deriving declarations per element.
let private standardNamespaces = [
    "wpc", "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas"
    "mc", "http://schemas.openxmlformats.org/markup-compatibility/2006"
    "o", "urn:schemas-microsoft-com:office:office"
    "r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
    "m", "http://schemas.openxmlformats.org/officeDocument/2006/math"
    "v", "urn:schemas-microsoft-com:vml"
    "wp", "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
    "w10", "urn:schemas-microsoft-com:office:word"
    "w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
    "w14", "http://schemas.microsoft.com/office/word/2010/wordml"
    "a", "http://schemas.openxmlformats.org/drawingml/2006/main"
    "pic", "http://schemas.openxmlformats.org/drawingml/2006/picture"
]

/// Emission-wide counters plus the part every figure hangs off.
///
/// Revision ids must be unique document-wide; one counter spans all
/// `w:ins` / `w:del` the emission produces. Figure ordinals do the
/// same job for `wp:docPr/@id` and for the relationship ids the
/// drawing XML names — see `figureRelationshipId`.
type private EmitContext = {
    mutable NextRevisionId: int
    mutable NextFigureOrdinal: int
    /// The main document part figure image parts are added to. Figure
    /// parts must be reached by a MAIN-DOCUMENT relationship — a
    /// package-root relationship (what `CustomPart` attaches) is not
    /// addressable from `a:blip/@r:embed`.
    MainPart: MainDocumentPart
}

module private EmitContext =
    let nextId (ctx: EmitContext) : string =
        let id = ctx.NextRevisionId
        ctx.NextRevisionId <- id + 1
        string id

    let nextFigureOrdinal (ctx: EmitContext) : int =
        let ordinal = ctx.NextFigureOrdinal
        ctx.NextFigureOrdinal <- ordinal + 1
        ordinal

// ─── Model walks (for generated parts) ───────────────────────────

let rec private collectFromBlocks (pick: Block -> 'a option) (blocks: Block list) : 'a list =
    blocks
    |> List.collect (fun block ->
        let nested =
            match block with
            | Table t ->
                t.Rows
                |> List.collect (fun row -> row.Cells |> List.collect (fun cell -> collectFromBlocks pick cell.Blocks))
            | _ -> []

        (pick block |> Option.toList) @ nested)

let private collectAll (pick: Block -> 'a option) (model: DocModel) : 'a list =
    model.Sections
    |> List.collect (fun section -> collectFromBlocks pick section.Blocks)

// ─── Generated parts (programmatic models) ───────────────────────

let private generatedStyles (headingLevels: int list) : Wordprocessing.Styles =
    let styles = Wordprocessing.Styles()

    let normal =
        Wordprocessing.Style(
            Type = EnumValue Wordprocessing.StyleValues.Paragraph,
            StyleId = StringValue "Normal",
            Default = OnOffValue true
        )

    normal.AppendChild(Wordprocessing.StyleName(Val = StringValue "Normal"))
    |> ignore

    styles.AppendChild normal |> ignore

    for level in headingLevels |> List.distinct |> List.sort do
        let style =
            Wordprocessing.Style(
                Type = EnumValue Wordprocessing.StyleValues.Paragraph,
                StyleId = StringValue(sprintf "Heading%d" level)
            )

        style.AppendChild(Wordprocessing.StyleName(Val = StringValue(sprintf "heading %d" level)))
        |> ignore

        style.AppendChild(Wordprocessing.BasedOn(Val = StringValue "Normal")) |> ignore

        let paragraphProps = Wordprocessing.StyleParagraphProperties()

        paragraphProps.AppendChild(Wordprocessing.OutlineLevel(Val = Int32Value(level - 1)))
        |> ignore

        style.AppendChild paragraphProps |> ignore

        let runProps = Wordprocessing.StyleRunProperties()
        runProps.AppendChild(Wordprocessing.Bold()) |> ignore

        // Half-points: 16pt for Heading1, stepping down to 12pt.
        let halfPoints = max 24 (34 - 2 * level)

        runProps.AppendChild(Wordprocessing.FontSize(Val = StringValue(string halfPoints)))
        |> ignore

        style.AppendChild runProps |> ignore
        styles.AppendChild style |> ignore

    styles

let private generatedNumbering (numberingIds: int list) : Wordprocessing.Numbering =
    let numbering = Wordprocessing.Numbering()
    let ids = numberingIds |> List.distinct |> List.sort

    // Schema order: every w:abstractNum precedes the first w:num.
    for id in ids do
        let abstractNum = Wordprocessing.AbstractNum(AbstractNumberId = Int32Value id)

        for levelIndex in 0..3 do
            let level = Wordprocessing.Level(LevelIndex = Int32Value levelIndex)

            level.AppendChild(Wordprocessing.StartNumberingValue(Val = Int32Value 1))
            |> ignore

            level.AppendChild(Wordprocessing.NumberingFormat(Val = EnumValue Wordprocessing.NumberFormatValues.Decimal))
            |> ignore

            level.AppendChild(Wordprocessing.LevelText(Val = StringValue(sprintf "%%%d." (levelIndex + 1))))
            |> ignore

            level.AppendChild(
                Wordprocessing.LevelJustification(Val = EnumValue Wordprocessing.LevelJustificationValues.Left)
            )
            |> ignore

            abstractNum.AppendChild level |> ignore

        numbering.AppendChild abstractNum |> ignore

    for id in ids do
        let instance = Wordprocessing.NumberingInstance(NumberID = Int32Value id)

        instance.AppendChild(Wordprocessing.AbstractNumId(Val = Int32Value id))
        |> ignore

        numbering.AppendChild instance |> ignore

    numbering

let private defaultTableProperties () : Wordprocessing.TableProperties =
    let props = Wordprocessing.TableProperties()
    let borders = Wordprocessing.TableBorders()

    let single () =
        EnumValue Wordprocessing.BorderValues.Single

    // Schema sequence: top, left, bottom, right, insideH, insideV.
    borders.AppendChild(Wordprocessing.TopBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(Wordprocessing.LeftBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(Wordprocessing.BottomBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(Wordprocessing.RightBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(Wordprocessing.InsideHorizontalBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(Wordprocessing.InsideVerticalBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    props.AppendChild borders |> ignore
    props

// ─── Run / paragraph emission ────────────────────────────────────

let private buildRunProperties (formatting: RunFormatting) : Wordprocessing.RunProperties option =
    match formatting.RawProperties with
    | Some raw -> Some(Wordprocessing.RunProperties raw)
    | None ->
        let props = Wordprocessing.RunProperties()

        formatting.StyleId
        |> Option.iter (fun styleId -> props.AppendChild(Wordprocessing.RunStyle(Val = StringValue styleId)) |> ignore)

        if formatting.Bold then
            props.AppendChild(Wordprocessing.Bold()) |> ignore

        if formatting.Italic then
            props.AppendChild(Wordprocessing.Italic()) |> ignore

        if formatting.Strikethrough then
            props.AppendChild(Wordprocessing.Strike()) |> ignore

        if formatting.Underline then
            props.AppendChild(Wordprocessing.Underline(Val = EnumValue Wordprocessing.UnderlineValues.Single))
            |> ignore

        if props.HasChildren then Some props else None

/// Lower the run's text into `w:t` / `w:delText` segments with
/// `w:tab` / `w:br` for the normalised `'\t'` / `'\n'` characters.
let private appendTextContent (run: Wordprocessing.Run) (text: string) (deleted: bool) =
    let buffer = StringBuilder()

    let flush () =
        if buffer.Length > 0 then
            let segment = buffer.ToString()
            buffer.Clear() |> ignore

            let element: OpenXmlElement =
                if deleted then
                    Wordprocessing.DeletedText(Text = segment, Space = EnumValue SpaceProcessingModeValues.Preserve)
                else
                    Wordprocessing.Text(Text = segment, Space = EnumValue SpaceProcessingModeValues.Preserve)

            run.AppendChild element |> ignore

    for ch in text do
        match ch with
        | '\t' ->
            flush ()
            run.AppendChild(Wordprocessing.TabChar()) |> ignore
        | '\n' ->
            flush ()
            run.AppendChild(Wordprocessing.Break()) |> ignore
        | other -> buffer.Append other |> ignore

    flush ()

// w:ins / w:del around runs (RunTrackChangeType) and on paragraph
// marks (TrackChangeType) share the id / author / date attribute
// trio but not a common base type carrying it.
let private runRevisionAttributes (ctx: EmitContext) (info: RevisionInfo) (element: Wordprocessing.RunTrackChangeType) =
    element.Id <- StringValue(EmitContext.nextId ctx)
    element.Author <- StringValue info.Author

    info.Date
    |> Option.iter (fun date -> element.Date <- DateTimeValue date.UtcDateTime)

let private markRevisionAttributes (ctx: EmitContext) (info: RevisionInfo) (element: Wordprocessing.TrackChangeType) =
    element.Id <- StringValue(EmitContext.nextId ctx)
    element.Author <- StringValue info.Author

    info.Date
    |> Option.iter (fun date -> element.Date <- DateTimeValue date.UtcDateTime)

let private emitRun (ctx: EmitContext) (run: Run) : OpenXmlElement =
    let isDeleted =
        match run.Revision with
        | Some(Deleted _) -> true
        | _ -> false

    let r = Wordprocessing.Run()

    buildRunProperties run.Formatting
    |> Option.iter (fun props -> r.AppendChild props |> ignore)

    appendTextContent r run.Text isDeleted

    match run.Revision with
    | None -> r :> OpenXmlElement
    | Some(Inserted info) ->
        let wrapper = Wordprocessing.InsertedRun()
        runRevisionAttributes ctx info wrapper
        wrapper.AppendChild r |> ignore
        wrapper
    | Some(Deleted info) ->
        let wrapper = Wordprocessing.DeletedRun()
        runRevisionAttributes ctx info wrapper
        wrapper.AppendChild r |> ignore
        wrapper

/// What a paragraph's properties should say when the model carries
/// no verbatim `w:pPr`: the style implied by the block kind, or the
/// numbering reference for list items.
type private GeneratedParagraphProps =
    | FromStyle of styleId: string
    | FromNumbering of NumberingRef
    | NoProps

let private buildParagraphProperties
    (ctx: EmitContext)
    (paragraph: ParagraphModel)
    (generated: GeneratedParagraphProps)
    : Wordprocessing.ParagraphProperties option =
    let props =
        match paragraph.RawProperties with
        | Some raw -> Some(Wordprocessing.ParagraphProperties raw)
        | None ->
            match generated, paragraph.StyleId with
            | FromStyle styleId, _ ->
                let props = Wordprocessing.ParagraphProperties()

                props.AppendChild(Wordprocessing.ParagraphStyleId(Val = StringValue styleId))
                |> ignore

                Some props
            | FromNumbering numbering, _ ->
                let props = Wordprocessing.ParagraphProperties()
                let numberingProps = Wordprocessing.NumberingProperties()

                numberingProps.AppendChild(Wordprocessing.NumberingLevelReference(Val = Int32Value numbering.Level))
                |> ignore

                numberingProps.AppendChild(Wordprocessing.NumberingId(Val = Int32Value numbering.NumberingId))
                |> ignore

                props.AppendChild numberingProps |> ignore
                Some props
            | NoProps, Some styleId ->
                let props = Wordprocessing.ParagraphProperties()

                props.AppendChild(Wordprocessing.ParagraphStyleId(Val = StringValue styleId))
                |> ignore

                Some props
            | NoProps, None -> None

    // Paragraph-mark revision (whole-paragraph insert / delete)
    // lowers to w:pPr/w:rPr/w:ins|w:del.
    match paragraph.MarkRevision with
    | None -> props
    | Some mark ->
        let props = props |> Option.defaultWith Wordprocessing.ParagraphProperties

        let markProps =
            match props.ParagraphMarkRunProperties with
            | null ->
                let created = Wordprocessing.ParagraphMarkRunProperties()
                props.AppendChild created |> ignore
                created
            | existing -> existing

        let markElement: OpenXmlElement =
            match mark with
            | Inserted info ->
                let ins = Wordprocessing.Inserted()
                markRevisionAttributes ctx info ins
                ins
            | Deleted info ->
                let del = Wordprocessing.Deleted()
                markRevisionAttributes ctx info del
                del

        markProps.PrependChild markElement |> ignore
        Some props

let private emitParagraph
    (ctx: EmitContext)
    (paragraph: ParagraphModel)
    (generated: GeneratedParagraphProps)
    : Wordprocessing.Paragraph =
    let p = Wordprocessing.Paragraph()

    buildParagraphProperties ctx paragraph generated
    |> Option.iter (fun props -> p.AppendChild props |> ignore)

    for commentId in paragraph.CommentIds do
        p.AppendChild(Wordprocessing.CommentRangeStart(Id = StringValue(string commentId)))
        |> ignore

    for run in paragraph.Runs do
        p.AppendChild(emitRun ctx run) |> ignore

    for commentId in paragraph.CommentIds do
        p.AppendChild(Wordprocessing.CommentRangeEnd(Id = StringValue(string commentId)))
        |> ignore

        let referenceRun = Wordprocessing.Run()

        referenceRun.AppendChild(Wordprocessing.CommentReference(Id = StringValue(string commentId)))
        |> ignore

        p.AppendChild referenceRun |> ignore

    p

// ─── Figure emission (Phase 576) ─────────────────────────────────
//
// A figure lowers to a paragraph holding one inline `w:drawing`, plus
// the image part(s) that drawing references off the MAIN DOCUMENT
// part's relationships. The drawing XML is built as a string and
// re-parsed rather than assembled through the DrawingML object model:
// the fragment is fixed, `svgBlip` lives in an extension namespace the
// SDK has no typed element for, and building it as text keeps the
// whole shape readable in one place.

/// The `a:extLst` extension uri Office uses for the SVG blip, and the
/// namespace of the `svgBlip` element inside it. Both are fixed by
/// Office's published extension, not chosen here.
[<Literal>]
let private SvgExtensionUri = "{96DAC541-7B7A-43D3-8B79-37D633B846F1}"

[<Literal>]
let private SvgExtensionNs =
    "http://schemas.microsoft.com/office/drawing/2016/SVG/main"

/// SVG text lowers to bytes as UTF-8 **without** a BOM: the payload is
/// the caller's byte-for-byte, so a deterministic renderer's output
/// survives into the package unchanged.
let private utf8NoBom = UTF8Encoding false

/// Relationship ids for figure parts are assigned **explicitly**,
/// never left to the SDK.
///
/// `MainDocumentPart.AddImagePart` without an id mints a random one
/// (`R97b4636297444240`), and that id lands verbatim in the drawing
/// XML — so two emits of the same model would differ in bytes while
/// rendering identically. That is the shape that survives review and
/// then breaks a content-addressed or golden-file consumer, so the
/// emitter owns the ids: `rTuFig<kind><ordinal>`, ordinal being the
/// figure's 1-based position in emission order. The prefix cannot
/// collide with the SDK's own allocations, which are either `rId<n>`
/// or `R<hex>`.
let private figureRelationshipId (kind: string) (ordinal: int) : string = sprintf "rTuFig%s%d" kind ordinal

/// The image-part content type for a figure's declared MIME type. An
/// unrecognised type is embedded as PNG — the shape a caller supplying
/// one almost always meant, and never a failed emit.
let private imagePartType (mimeType: string) : PartTypeInfo =
    match
        (if isNull mimeType then
             ""
         else
             mimeType.Trim().ToLowerInvariant())
    with
    | "image/jpeg"
    | "image/jpg" -> ImagePartType.Jpeg
    | "image/gif" -> ImagePartType.Gif
    | "image/bmp" -> ImagePartType.Bmp
    | "image/tiff" -> ImagePartType.Tiff
    | _ -> ImagePartType.Png

let private addImagePart (main: MainDocumentPart) (partType: PartTypeInfo) (relationshipId: string) (bytes: byte[]) =
    let part = main.AddImagePart(partType, relationshipId)
    use payload = new MemoryStream(bytes)
    part.FeedData payload

/// The `pic:blipFill` for a figure. `primaryRelationshipId` is what
/// `a:blip/@r:embed` points at — the PNG fallback part when one
/// exists, otherwise the SVG part itself, which a current Office
/// client renders directly. `svgRelationshipId` adds the `svgBlip`
/// extension so a 2016-or-later client renders the vector in
/// preference to whatever the blip resolved to.
let private blipFillXml (primaryRelationshipId: string) (svgRelationshipId: string option) : string =
    let blip =
        match svgRelationshipId with
        | None -> sprintf "<a:blip r:embed=\"%s\"/>" primaryRelationshipId
        | Some svgRelationshipId ->
            sprintf "<a:blip r:embed=\"%s\">" primaryRelationshipId
            + sprintf "<a:extLst><a:ext uri=\"%s\">" SvgExtensionUri
            + sprintf "<asvg:svgBlip xmlns:asvg=\"%s\" r:embed=\"%s\"/>" SvgExtensionNs svgRelationshipId
            + "</a:ext></a:extLst></a:blip>"

    "<pic:blipFill>" + blip + "<a:stretch><a:fillRect/></a:stretch></pic:blipFill>"

/// The inline `w:drawing` referencing a figure's parts at the given
/// EMU extents. The fragment declares its own namespace prefixes,
/// because the SDK parses it as standalone element XML.
let private drawingXml
    (blipFill: string)
    (ordinal: int)
    (name: string)
    (description: string option)
    (cx: int64)
    (cy: int64)
    : string =
    let escape (value: string) =
        System.Security.SecurityElement.Escape(if isNull value then "" else value)

    let safeName = escape name

    let descriptionAttribute =
        description
        |> Option.map (fun text -> sprintf " descr=\"%s\"" (escape text))
        |> Option.defaultValue ""

    "<w:drawing xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
    + "<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">"
    + sprintf "<wp:extent cx=\"%d\" cy=\"%d\"/>" cx cy
    + "<wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/>"
    + sprintf "<wp:docPr id=\"%d\" name=\"%s\"%s/>" ordinal safeName descriptionAttribute
    + "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>"
    + sprintf
        "<pic:nvPicPr><pic:cNvPr id=\"%d\" name=\"%s\"%s/><pic:cNvPicPr/></pic:nvPicPr>"
        ordinal
        safeName
        descriptionAttribute
    + blipFill
    + sprintf
        "<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"%d\" cy=\"%d\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>"
        cx
        cy
    + "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing>"

let private emitFigure (ctx: EmitContext) (figure: FigureModel) : Wordprocessing.Paragraph =
    let ordinal = EmitContext.nextFigureOrdinal ctx
    let cx, cy = Figures.extents figure

    let blipFill =
        match figure.Content with
        | RasterImage(bytes, mimeType) ->
            let relationshipId = figureRelationshipId "Img" ordinal
            addImagePart ctx.MainPart (imagePartType mimeType) relationshipId bytes
            blipFillXml relationshipId None
        | VectorSvg(svgText, pngFallback) ->
            let svgRelationshipId = figureRelationshipId "Svg" ordinal

            addImagePart
                ctx.MainPart
                ImagePartType.Svg
                svgRelationshipId
                (utf8NoBom.GetBytes(if isNull svgText then "" else svgText))

            let fallbackRelationshipId =
                pngFallback
                |> Option.filter (fun bytes -> not (isNull bytes) && bytes.Length > 0)
                |> Option.map (fun bytes ->
                    let relationshipId = figureRelationshipId "Fbk" ordinal
                    addImagePart ctx.MainPart ImagePartType.Png relationshipId bytes
                    relationshipId)

            blipFillXml (defaultArg fallbackRelationshipId svgRelationshipId) (Some svgRelationshipId)

    let paragraph = Wordprocessing.Paragraph()
    // Build the run then append the drawing explicitly: a
    // `Run(Drawing …)` ctor resolves to the IEnumerable overload
    // (a Drawing enumerates its own children) and re-parents them.
    let run = Wordprocessing.Run()

    run.AppendChild(Wordprocessing.Drawing(drawingXml blipFill ordinal figure.Name figure.Description cx cy))
    |> ignore

    paragraph.AppendChild run |> ignore
    paragraph

// ─── Block / table emission ──────────────────────────────────────

let rec private emitBlock (ctx: EmitContext) (block: Block) : OpenXmlElement =
    match block with
    | Figure figure -> emitFigure ctx figure
    | Paragraph paragraph -> emitParagraph ctx paragraph NoProps
    | Heading(level, paragraph) -> emitParagraph ctx paragraph (FromStyle(sprintf "Heading%d" level))
    | ListItem(numbering, paragraph) -> emitParagraph ctx paragraph (FromNumbering numbering)
    | Table table -> emitTable ctx table
    | OpaqueBlock outerXml ->
        // Verbatim re-attachment: parse the captured XML inside a
        // w:body wrapper so the SDK re-types known elements (an
        // OuterXml capture is standalone — it carries its own
        // namespace declarations) and detach the result for
        // appending into the emitted tree.
        let wrapped =
            sprintf
                "<w:body xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">%s</w:body>"
                outerXml

        let container = Wordprocessing.Body wrapped
        let element = container.FirstChild
        element.Remove()
        element

and private emitTable (ctx: EmitContext) (table: TableModel) : OpenXmlElement =
    let tbl = Wordprocessing.Table()

    let props =
        match table.RawProperties with
        | Some raw -> Wordprocessing.TableProperties raw
        | None -> defaultTableProperties ()

    tbl.AppendChild props |> ignore

    match table.RawGrid with
    | Some raw -> tbl.AppendChild(Wordprocessing.TableGrid raw) |> ignore
    | None ->
        // w:tblGrid is required before the first row; generate one
        // column per widest row for programmatic tables.
        let columns = table.Rows |> List.map _.Cells.Length |> List.fold max 0

        if columns > 0 then
            let grid = Wordprocessing.TableGrid()

            for _ in 1..columns do
                grid.AppendChild(Wordprocessing.GridColumn()) |> ignore

            tbl.AppendChild grid |> ignore

    for row in table.Rows do
        let tr = Wordprocessing.TableRow()

        row.RawProperties
        |> Option.iter (fun raw -> tr.AppendChild(Wordprocessing.TableRowProperties raw) |> ignore)

        for cell in row.Cells do
            let tc = Wordprocessing.TableCell()

            cell.RawProperties
            |> Option.iter (fun raw -> tc.AppendChild(Wordprocessing.TableCellProperties raw) |> ignore)

            for cellBlock in cell.Blocks do
                tc.AppendChild(emitBlock ctx cellBlock) |> ignore

            // WordprocessingML requires every cell to end with a
            // paragraph.
            match tc.LastChild with
            | :? Wordprocessing.Paragraph -> ()
            | _ -> tc.AppendChild(Wordprocessing.Paragraph()) |> ignore

            tr.AppendChild tc |> ignore

        tbl.AppendChild tr |> ignore

    tbl

// ─── Section / part emission ─────────────────────────────────────

let private ensureParagraphProperties (p: Wordprocessing.Paragraph) : Wordprocessing.ParagraphProperties =
    match p.ParagraphProperties with
    | null ->
        let props = Wordprocessing.ParagraphProperties()
        p.PrependChild props |> ignore
        props
    | existing -> existing

let private emitSection (ctx: EmitContext) (body: Wordprocessing.Body) (section: Section) (isLast: bool) =
    for block in section.Blocks do
        body.AppendChild(emitBlock ctx block) |> ignore

    if isLast then
        // The final section's properties sit directly under w:body.
        section.RawProperties
        |> Option.iter (fun raw -> body.AppendChild(Wordprocessing.SectionProperties raw) |> ignore)
    else
        // Intermediate section breaks ride the section's last
        // paragraph (Word's own shape); a section that does not end
        // in a paragraph gets a fresh break paragraph.
        let sectPr =
            match section.RawProperties with
            | Some raw -> Wordprocessing.SectionProperties raw
            | None -> Wordprocessing.SectionProperties()

        match body.LastChild with
        | :? Wordprocessing.Paragraph as p when not section.Blocks.IsEmpty ->
            (ensureParagraphProperties p).AppendChild sectPr |> ignore
        | _ ->
            let props = Wordprocessing.ParagraphProperties()
            props.AppendChild sectPr |> ignore
            let breakParagraph = Wordprocessing.Paragraph()
            breakParagraph.AppendChild props |> ignore
            body.AppendChild breakParagraph |> ignore

let private emitStyles (doc: WordprocessingDocument) (model: DocModel) =
    match model.Styles.RawXml with
    | Some raw -> (Package.ensureStylesPart doc).Styles <- Wordprocessing.Styles raw
    | None ->
        let headingLevels =
            model
            |> collectAll (function
                | Heading(level, _) -> Some level
                | _ -> None)

        if not headingLevels.IsEmpty then
            (Package.ensureStylesPart doc).Styles <- generatedStyles headingLevels

let private emitNumbering (doc: WordprocessingDocument) (model: DocModel) =
    match model.Numbering.RawXml with
    | Some raw -> (Package.ensureNumberingPart doc).Numbering <- Wordprocessing.Numbering raw
    | None ->
        let numberingIds =
            model
            |> collectAll (function
                | ListItem(numbering, _) -> Some numbering.NumberingId
                | _ -> None)

        if not numberingIds.IsEmpty then
            (Package.ensureNumberingPart doc).Numbering <- generatedNumbering numberingIds

let private emitComments (doc: WordprocessingDocument) (comments: Comment list) =
    if not comments.IsEmpty then
        let element = Wordprocessing.Comments()

        for comment in comments do
            let c =
                Wordprocessing.Comment(Id = StringValue(string comment.Id), Author = StringValue comment.Author)

            comment.Initials
            |> Option.iter (fun initials -> c.Initials <- StringValue initials)

            comment.Date
            |> Option.iter (fun date -> c.Date <- DateTimeValue date.UtcDateTime)

            let bodyRun = Wordprocessing.Run()
            bodyRun.AppendChild(Wordprocessing.Text comment.Text) |> ignore
            let bodyParagraph = Wordprocessing.Paragraph()
            bodyParagraph.AppendChild bodyRun |> ignore
            c.AppendChild bodyParagraph |> ignore

            element.AppendChild c |> ignore

        (Package.ensureCommentsPart doc).Comments <- element

// ─── Entry points ────────────────────────────────────────────────

/// Emit the model's document parts as a `.docx` package onto the
/// stream. The stream is written and left open; the caller owns it.
let private toStreamCore (model: DocModel) (stream: Stream) : unit =
    use doc = Package.create stream
    let main = Package.mainPart doc
    let document = main.Document

    for prefix, uri in standardNamespaces do
        document.AddNamespaceDeclaration(prefix, uri)

    emitStyles doc model
    emitNumbering doc model
    emitComments doc model.Comments

    let ctx = {
        NextRevisionId = 1
        NextFigureOrdinal = 1
        MainPart = main
    }

    let lastIndex = model.Sections.Length - 1

    model.Sections
    |> List.iteri (fun i section -> emitSection ctx document.Body section (i = lastIndex))

    document.Save()

/// Emit the model plus any out-of-band custom parts as a `.docx`
/// package onto the stream. The document parts are written first;
/// each `CustomPart` is then attached at the OPC level (a part with a
/// content-type override + a package-root relationship). Passing `[]`
/// is byte-for-byte equivalent to a custom-part-free emit. The stream
/// must be seekable and writable; it is left open and the caller owns
/// it.
let toStreamWith (customParts: CustomPart list) (model: DocModel) (stream: Stream) : unit =
    toStreamCore model stream
    Package.attachCustomParts stream customParts

/// Emit the model as a `.docx` package onto the stream. The stream is
/// written and left open; the caller owns it.
let toStream (model: DocModel) (stream: Stream) : unit = toStreamWith [] model stream

/// Emit the model plus any out-of-band custom parts as `.docx` bytes.
/// Round-trips through `Import.fromBytes`, which surfaces the same
/// parts on `ImportedDocument.CustomParts`.
let toBytesWith (customParts: CustomPart list) (model: DocModel) : byte[] =
    use stream = new MemoryStream()
    toStreamWith customParts model stream
    stream.ToArray()

/// Emit the model as `.docx` bytes.
let toBytes (model: DocModel) : byte[] = toBytesWith [] model