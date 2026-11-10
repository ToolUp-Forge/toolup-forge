module internal KnowledgeBase.ServerExtractors

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.Infra.IOcrProvider
open ToolUp.Platform.Infra.ITableExtractor
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Chunking
open SharedTypes
open KnowledgeBase.ServerJsonHelpers
open ToolUp.Platform.Infra

// ─── Text extraction ──────────────────────────────────────────────

let makeChunk
    (origin: ChunkOrigin)
    (fileName: string)
    (header: string)
    (content: string)
    (src: SourceReference)
    : TextChunk =
    {
        Content = String.concat "\n" [ sprintf "[%s — %s]" fileName header; content ]
        Metadata =
            Map.ofList [
                "_source", toJson src
                "dataTypeId", "KnowledgeDocument"
                ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue origin
                ChunkMetadata.LocationHintKey, header
            ]
    }

// ─── OCR availability ─────────────────────────────────────────────
//
// The `IOcrProvider` seam always resolves to *something* — DI hands back
// `NoOpOcrProvider` when a deployment composed no companion, so every call
// site can consult it unconditionally. What `ocrComposed` adds is the
// ability to ASK which one you got, because two situations that look
// identical to the extractor are completely different to the user:
//
//   * a real OCR provider ran and recovered nothing — the page really is
//     blank;
//   * no OCR provider exists, so nothing was even attempted.
//
// The discriminator is a type test against the no-op, not a string
// comparison on `Name`. `Name` is documented as a free-form trace label,
// so a companion is entitled to call itself anything; the identity of
// the default is a fact about this codebase and is checked as one. The
// `Name` fallback below only catches a hand-rolled stand-in that
// deliberately adopts the no-op's identifier.

/// `true` when the composed `IOcrProvider` is a real companion rather
/// than the no-op default (or a null the DI probe failed to fill).
let ocrComposed (ocr: IOcrProvider) : bool =
    not (isNull (box ocr))
    && not (ocr :? ToolUp.RAG.NoOpDocUnderstanding.NoOpOcrProvider)
    && not (String.Equals(ocr.Name, "noop", StringComparison.OrdinalIgnoreCase))

/// Extensions (lower-case, no leading dot) that carry no text layer at
/// all and are therefore *only* indexable through OCR. Distinct from
/// `supportedExtensions` on purpose: these are not "supported" in the
/// upload-policy sense — a deployment with no OCR companion cannot index
/// them, and the policy's allowlist / reject levers must keep behaving
/// exactly as they did (GP 11).
let ocrOnlyExtensions: Set<string> =
    Set.ofList [ "png"; "jpg"; "jpeg"; "tif"; "tiff"; "bmp"; "gif"; "webp" ]

/// MIME type for an OCR-eligible image extension. `IOcrProvider` takes
/// `mimeType` rather than a filename, so the extractor is the one place
/// that knows the mapping.
let private imageMimeType (ext: string) : string =
    match ext with
    | "png" -> "image/png"
    | "jpg"
    | "jpeg" -> "image/jpeg"
    | "tif"
    | "tiff" -> "image/tiff"
    | "bmp" -> "image/bmp"
    | "gif" -> "image/gif"
    | "webp" -> "image/webp"
    | other -> sprintf "image/%s" other

// ─── The extractor model: format → parts → chunks ─────────────────

/// How a block of prose is headed.
[<RequireQualifiedAccess>]
type ProseHeading =
    /// Under a title, at a location: its chunks are headed `title`, or
    /// `title (part i of n)` when it splits, and a block too short to
    /// split is kept whole.
    | Titled of title: string * location: SourceLocation
    /// Running text with no title: its chunks are headed `text`, or
    /// `part i of n`, each located by that header, and a block too short
    /// to split yields nothing.
    | Running

/// How a sheet's chunks are headed and located.
[<RequireQualifiedAccess>]
type SheetKind =
    /// A workbook sheet: headed `Sheet "<name>", …`, located by sheet and row range.
    | Workbook
    /// A delimited file: headed by the row range alone, located by row group.
    | Delimited

/// What a document carries that has no text the extractor could read.
[<RequireQualifiedAccess>]
type Textless =
    /// An image upload.
    | Image
    /// A PDF whose pages carry images but (almost) no text layer.
    | ScannedPdf

/// One thing an extractor read out of a document, before chunking.
type Part =
    /// Prose, split by tokens.
    | Prose of heading: ProseHeading * text: string
    /// Text taken whole as one chunk (an OCR page).
    | Verbatim of header: string * location: SourceLocation * text: string
    /// A table in a document: its rows chunked with the column headers
    /// repeated, headed `title (table)`, or `title (table, part i of n)`.
    | Table of title: string * location: SourceLocation * data: SheetData
    /// A whole sheet: a schema-and-sample chunk, then row-group chunks.
    | Sheet of kind: SheetKind * data: SheetData
    /// Content whose text only OCR could recover, and no OCR is composed.
    /// It yields no chunk; it is what the empty-extraction status reads.
    | Unread of Textless

/// The document a list of parts was read from.
type PartSource = {
    DocumentId: string
    FileName: string
    /// The lower-case extension, no leading dot.
    FileType: string
}

/// Parse the row range "rows N–M of T" emitted by `Chunking.chunkSpreadsheet`'s
/// header line, to round-trip it into the chunk's `SourceReference.Location`.
let private parseRowRange (chunkText: string) : (int * int) option =
    let m = Regex.Match(chunkText, @"rows (\d+)–(\d+)", RegexOptions.None)

    if m.Success then
        Some(int m.Groups[1].Value, int m.Groups[2].Value)
    else
        None

/// The one fold from parts to chunks. Every chunk carries the same
/// metadata (`makeChunk`) and a source reference located by its part.
let chunksOf (origin: ChunkOrigin) (source: PartSource) (parts: Part list) : (TextChunk * SourceReference) list =
    let emit (header: string) (content: string) (location: SourceLocation) =
        let src: SourceReference = {
            DocumentId = source.DocumentId
            DocumentName = source.FileName
            FileType = source.FileType
            Location = location
            IndexedAt = DateTimeOffset.UtcNow
        }

        makeChunk origin source.FileName header content src, src

    // Prose shorter than the splitter's minimum is kept whole, so a short
    // text is one chunk in every format rather than none.
    let prosePieces (text: string) =
        match splitByTokens ChunkingConfig.defaults text with
        | [] -> [ text ]
        | pieces -> pieces

    let numbered (pieces: string list) (whole: string) (part: int -> int -> string) =
        let total = pieces.Length

        pieces
        |> List.mapi (fun i piece -> (if total = 1 then whole else part (i + 1) total), piece)

    [
        for part in parts do
            match part with
            | Prose(ProseHeading.Titled(title, location), text) ->
                for header, piece in numbered (prosePieces text) title (sprintf "%s (part %d of %d)" title) do
                    emit header piece location
            | Prose(ProseHeading.Running, text) ->
                for header, piece in numbered (prosePieces text) "text" (sprintf "part %d of %d") do
                    emit header piece (SharedTypes.Section header)
            | Verbatim(header, location, text) -> emit header text location
            | Table(title, location, data) ->
                let pieces = chunkSpreadsheet ChunkingConfig.defaults data

                for header, piece in numbered pieces (title + " (table)") (sprintf "%s (table, part %d of %d)" title) do
                    emit header piece location
            | Sheet(kind, data) ->
                let name = data.SheetName

                let schemaHeader =
                    match kind with
                    | SheetKind.Workbook -> sprintf "Sheet \"%s\", schema + sample" name
                    | SheetKind.Delimited -> "schema + sample"

                // The schema-and-sample chunk first: one chunk that is high-
                // recall for "what columns does this have?" and "show me an
                // example row".
                let sample =
                    data.Rows
                    |> List.truncate 5
                    |> List.map (fun (_, values) -> formatRow data.Headers values)
                    |> String.concat "\n"

                emit
                    schemaHeader
                    (sprintf "Columns: %s\n%s" (String.concat ", " data.Headers) sample)
                    (SourceLocation.Sheet(name, None))

                // Token-aware row groups: as many rows as fit within the
                // budget, the column headers repeated in each.
                for group in chunkSpreadsheet ChunkingConfig.tabular data do
                    match kind with
                    | SheetKind.Workbook ->
                        let rowRange =
                            match parseRowRange group with
                            | Some(s, e) -> sprintf "rows %d–%d" s e
                            | None -> "rows"

                        emit
                            (sprintf "Sheet \"%s\", %s" name rowRange)
                            group
                            (SourceLocation.Sheet(name, Some rowRange))
                    | SheetKind.Delimited ->
                        let startRow, endRow =
                            parseRowRange group |> Option.defaultValue (2, data.Rows.Length + 1)

                        emit (sprintf "rows %d–%d" startRow endRow) group (RowGroup(startRow, endRow))
            | Unread _ -> ()
    ]

/// What an extraction needs beside the document: the OCR and table
/// companions, and which DOCX extractor the deployment composed.
type ExtractDeps = {
    Ocr: IOcrProvider
    Tables: ITableExtractor
    Docx: DocxExtraction.DocxExtractionMode
}

module ExtractDeps =
    /// The deps for these companions, with the DOCX mode the deployment
    /// composed (`DocxExtraction.enableStructuredDocxExtraction`).
    let composed (ocr: IOcrProvider) (tables: ITableExtractor) : ExtractDeps = {
        Ocr = ocr
        Tables = tables
        Docx = DocxExtraction.composedMode ()
    }

/// The document an extractor reads.
type ExtractInput = {
    FileName: string
    /// The lower-case extension, no leading dot.
    Extension: string
    Bytes: byte[]
}

/// A format's extractor: the extensions it reads, and the parts it reads.
type Extractor = {
    Formats: string list
    Parts: ExtractDeps -> ExtractInput -> Async<Part list>
}

/// The text of each OCR page that carries more than a stray mark.
let private ocrPages (pages: OcrPage list) (header: OcrPage -> string) (location: OcrPage -> SourceLocation) = [
    for p in pages do
        let text = p.Text.Trim()

        if text.Length > 10 then
            Verbatim(header p, location p, text)
]

/// Threshold below which a PDF's text layer counts as absent rather than
/// sparse. A scanned page routinely carries a handful of stray characters
/// (a stamped page number, a scanner watermark); a text page does not stop
/// at 32.
[<Literal>]
let private ScannedTextThreshold = 32

/// PDF, read three ways:
///   1. `IOcrProvider.IsScanned` — a scanned image PDF goes through
///      `ExtractText`, one part per OCR page.
///   2. `ITableExtractor.ExtractTables` — each table a `Table` part.
///   3. PdfPig — each page's words a prose part.
/// When the text layer yields nothing, a composed OCR companion reads the
/// file anyway (`IsScanned` is a heuristic and can be wrong); with none
/// composed, a document whose pages carry images and almost no text is
/// reported `Unread`, judged in the same open. With the no-op providers
/// steps 1 and 2 contribute nothing.
let private pdf: Extractor = {
    Formats = [ "pdf" ]
    Parts =
        fun deps input -> async {
            let mimeType = "application/pdf"
            let pageOf (p: OcrPage) = Page p.PageNumber
            let ocrHeader (p: OcrPage) = sprintf "Page %d (OCR)" p.PageNumber
            let! isScanned = deps.Ocr.IsScanned input.Bytes mimeType

            if isScanned then
                let! pages = deps.Ocr.ExtractText input.Bytes mimeType
                return ocrPages pages ocrHeader pageOf
            else
                let! extractedTables = deps.Tables.ExtractTables input.Bytes mimeType

                let tables = [
                    for t in extractedTables do
                        if not t.Rows.IsEmpty && t.Headers.Length > 0 then
                            let sheetName =
                                match t.Page with
                                | Some n -> sprintf "Page %d" n
                                | None -> "Table"

                            Table(
                                sheetName,
                                Page(defaultArg t.Page 0),
                                {
                                    SheetName = sheetName
                                    Headers = t.Headers
                                    Rows = t.Rows
                                }
                            )
                ]

                let text, unread =
                    use doc = UglyToad.PdfPig.PdfDocument.Open(input.Bytes)
                    let pages = doc.GetPages() |> Seq.toList

                    let text = [
                        for page in pages do
                            let words = page.GetWords() |> Seq.map _.Text |> String.concat " " |> _.Trim()

                            if words.Length > 10 then
                                Prose(ProseHeading.Titled(sprintf "Page %d" page.Number, Page page.Number), words)
                    ]

                    let unread =
                        text.IsEmpty
                        && tables.IsEmpty
                        && not (ocrComposed deps.Ocr)
                        && (pages |> List.exists (fun page -> not (Seq.isEmpty (page.GetImages()))))
                        && (pages
                            |> List.sumBy (fun page -> if isNull page.Text then 0 else page.Text.Trim().Length)) < ScannedTextThreshold

                    text, unread

                if text.IsEmpty && tables.IsEmpty && ocrComposed deps.Ocr then
                    let! pages = deps.Ocr.ExtractText input.Bytes mimeType
                    return ocrPages pages ocrHeader pageOf
                elif unread then
                    return [ Unread Textless.ScannedPdf ]
                else
                    return text @ tables
        }
}

/// An image has no text layer, so the composed `IOcrProvider` is the only
/// route; with none composed the image is `Unread`.
let private image: Extractor = {
    Formats = Set.toList ocrOnlyExtensions
    Parts =
        fun deps input -> async {
            if not (ocrComposed deps.Ocr) then
                return [ Unread Textless.Image ]
            else
                let! pages = deps.Ocr.ExtractText input.Bytes (imageMimeType input.Extension)

                // An image is one page; `Page 1` keeps the locator uniform
                // with the PDF path, so a Sources panel opens it the same way.
                return ocrPages pages (fun _ -> sprintf "%s (OCR)" input.FileName) (fun p -> Page(max 1 p.PageNumber))
        }
}

/// PPTX: each slide's text, headed by its number and title.
let private pptx: Extractor = {
    Formats = [ "pptx" ]
    Parts =
        fun _ input -> async {
            use stream = new MemoryStream(input.Bytes)

            use presentation =
                DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(stream, false)

            let slideIds =
                presentation.PresentationPart.Presentation.SlideIdList.Elements<
                    DocumentFormat.OpenXml.Presentation.SlideId
                 >()

            return [
                for slideNum, slideId in Seq.indexed slideIds |> Seq.map (fun (i, s) -> i + 1, s) do
                    let slidePart =
                        presentation.PresentationPart.GetPartById(slideId.RelationshipId.Value)
                        :?> DocumentFormat.OpenXml.Packaging.SlidePart

                    // A title placeholder has PlaceholderShape.Type = Title or
                    // CenteredTitle (or no type at all).
                    let title =
                        slidePart.Slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>()
                        |> Seq.tryPick (fun sp ->
                            let appNv =
                                if sp.NonVisualShapeProperties <> null then
                                    sp.NonVisualShapeProperties.ApplicationNonVisualDrawingProperties
                                else
                                    null

                            if appNv <> null && appNv.PlaceholderShape <> null then
                                let ph = appNv.PlaceholderShape

                                let isTitle =
                                    ph.Type = null
                                    || (ph.Type.HasValue
                                        && (ph.Type.Value = DocumentFormat.OpenXml.Presentation.PlaceholderValues.Title
                                            || ph.Type.Value = DocumentFormat.OpenXml.Presentation.PlaceholderValues.CenteredTitle))

                                if isTitle then
                                    let txt =
                                        sp.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                                        |> Seq.map _.Text
                                        |> String.concat " "
                                        |> _.Trim()

                                    if txt.Length > 0 then Some txt else None
                                else
                                    None
                            else
                                None)

                    let allText =
                        slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                        |> Seq.map _.Text
                        |> Seq.filter (fun s -> s.Trim().Length > 0)
                        |> String.concat " "

                    if allText.Length > 5 then
                        let header =
                            match title with
                            | Some t -> sprintf "Slide %d: \"%s\"" slideNum t
                            | None -> sprintf "Slide %d" slideNum

                        Prose(ProseHeading.Titled(header, Slide(slideNum, title)), allText)
            ]
        }
}

let private sectionTitle (heading: string) = sprintf "Section: \"%s\"" heading

/// DOCX, flat: the top-level paragraphs under each heading, one section
/// per heading (tables are not read).
let private docxFlat: Extractor = {
    Formats = [ "docx" ]
    Parts =
        fun _ input -> async {
            use stream = new MemoryStream(input.Bytes)

            use doc =
                DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false)

            let paragraphs =
                doc.MainDocumentPart.Document.Body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
                |> Seq.choose (fun para ->
                    let text = para.InnerText.Trim()

                    if text.Length = 0 then
                        None
                    else
                        let styleId =
                            match para.ParagraphProperties with
                            | null -> ""
                            | props ->
                                match props.ParagraphStyleId with
                                | null -> ""
                                | sid -> sid.Val.Value

                        Some(styleId.StartsWith "Heading", text))
                |> List.ofSeq

            // Each heading opens a section; the paragraphs before the
            // first heading are the "Document" section.
            let sections =
                paragraphs
                |> List.fold
                    (fun (closed, heading, body) (isHeading, text) ->
                        if isHeading then
                            (heading, List.rev body) :: closed, text, []
                        else
                            closed, heading, text :: body)
                    ([], "Document", [])
                |> fun (closed, heading, body) -> List.rev ((heading, List.rev body) :: closed)

            return [
                for heading, body in sections do
                    if not body.IsEmpty then
                        Prose(
                            ProseHeading.Titled(sectionTitle heading, SharedTypes.Section heading),
                            String.concat " " body
                        )
            ]
        }
}

/// DOCX, structured: the `ToolUp.OpenXml` model, keyed by the full heading
/// path (`Intro › Background`); list items keep their bullet, and tables
/// are read as tables.
let private docxStructured: Extractor = {
    Formats = [ "docx" ]
    Parts =
        fun _ input -> async {
            let imported = ToolUp.OpenXml.Import.fromBytes input.Bytes
            let parts = ResizeArray<Part>()
            let headingPath = ResizeArray<int * string>()
            let pendingText = ResizeArray<string>()

            let currentHeading () =
                if headingPath.Count = 0 then
                    "Document"
                else
                    headingPath |> Seq.map snd |> String.concat " › "

            let flushText () =
                if pendingText.Count > 0 then
                    let heading = currentHeading ()

                    parts.Add(
                        Prose(
                            ProseHeading.Titled(sectionTitle heading, SharedTypes.Section heading),
                            String.concat "\n" pendingText
                        )
                    )

                    pendingText.Clear()

            let cellText (cell: ToolUp.OpenXml.TableCell) =
                cell.Blocks
                |> List.map ToolUp.OpenXml.Block.text
                |> String.concat " "
                |> _.Trim()

            let addTable (table: ToolUp.OpenXml.TableModel) =
                match table.Rows with
                | [] -> ()
                // A single-row table has no data rows: its cells join the
                // running section text.
                | [ onlyRow ] -> pendingText.Add(onlyRow.Cells |> List.map cellText |> String.concat "\t")
                | headerRow :: dataRows ->
                    let heading = currentHeading ()

                    parts.Add(
                        Table(
                            sectionTitle heading,
                            SharedTypes.Section heading,
                            {
                                SheetName = heading
                                Headers = headerRow.Cells |> List.map cellText |> Array.ofList
                                // 1-based source rows as a spreadsheet shows them.
                                Rows =
                                    dataRows
                                    |> List.mapi (fun i row -> (i + 2, row.Cells |> List.map cellText |> Array.ofList))
                            }
                        )
                    )

            for section in imported.Model.Sections do
                for block in section.Blocks do
                    match block with
                    | ToolUp.OpenXml.Block.Heading(level, paragraph) ->
                        let text = (ToolUp.OpenXml.ParagraphModel.text paragraph).Trim()

                        if text.Length > 0 then
                            flushText ()

                            while headingPath.Count > 0 && fst headingPath[headingPath.Count - 1] >= level do
                                headingPath.RemoveAt(headingPath.Count - 1)

                            headingPath.Add(level, text)
                    | ToolUp.OpenXml.Block.Paragraph paragraph ->
                        let text = (ToolUp.OpenXml.ParagraphModel.text paragraph).Trim()

                        if text.Length > 0 then
                            pendingText.Add text
                    | ToolUp.OpenXml.Block.ListItem(_, paragraph) ->
                        let text = (ToolUp.OpenXml.ParagraphModel.text paragraph).Trim()

                        if text.Length > 0 then
                            pendingText.Add("- " + text)
                    | ToolUp.OpenXml.Block.Table table ->
                        flushText ()
                        addTable table
                    | ToolUp.OpenXml.Block.Figure figure ->
                        // A figure's only text is its accessible description.
                        match figure.Description with
                        | Some description when description.Trim().Length > 0 -> pendingText.Add(description.Trim())
                        | Some _
                        | None -> ()
                    | ToolUp.OpenXml.Block.OpaqueBlock _ -> ()

            flushText ()
            return List.ofSeq parts
        }
}

/// XLSX: each sheet with a header row and at least one data row.
let private xlsx: Extractor = {
    Formats = [ "xlsx" ]
    Parts =
        fun _ input -> async {
            use stream = new MemoryStream(input.Bytes)

            use workbook =
                DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false)

            let sharedStrings =
                match workbook.WorkbookPart.SharedStringTablePart with
                | null -> [||]
                | sst ->
                    sst.SharedStringTable.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
                    |> Seq.map _.InnerText
                    |> Seq.toArray

            let cellValue (cell: DocumentFormat.OpenXml.Spreadsheet.Cell) =
                if cell = null || cell.CellValue = null then
                    ""
                else
                    let raw = cell.CellValue.InnerText

                    if
                        cell.DataType <> null
                        && cell.DataType.Value = DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString
                    then
                        match Int32.TryParse(raw) with
                        | true, idx when idx < sharedStrings.Length -> sharedStrings[idx]
                        | _ -> raw
                    else
                        raw

            let values (row: DocumentFormat.OpenXml.Spreadsheet.Row) =
                row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                |> Seq.map cellValue
                |> Seq.toArray

            return [
                for sheet in workbook.WorkbookPart.Workbook.Sheets.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>() do
                    let worksheetPart =
                        workbook.WorkbookPart.GetPartById(sheet.Id.Value)
                        :?> DocumentFormat.OpenXml.Packaging.WorksheetPart

                    let rows =
                        worksheetPart.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>()
                        |> Seq.toArray

                    if rows.Length >= 2 then
                        Sheet(
                            SheetKind.Workbook,
                            {
                                SheetName = sheet.Name.Value
                                Headers = values rows[0]
                                // 1-based source rows: the header is row 1.
                                Rows =
                                    rows
                                    |> Array.skip 1
                                    |> Array.mapi (fun i r -> (i + 2, values r))
                                    |> Array.toList
                            }
                        )
            ]
        }
}

/// The records of a CSV file, header first, each cell trimmed, read by the
/// RFC 4180 reader so a quoted field keeps its delimiters and line breaks.
/// A blank line is no record, and a record whose quoting is malformed is
/// skipped, as the reader reports it rather than guessing at its cells.
let internal csvRows (text: string) : string[] list =
    use reader = new StringReader(text)

    ToolUp.Tabular.Csv.parseRecords ',' reader
    |> Seq.choose (fun (_, record) ->
        match record with
        | Ok [| "" |] -> None
        | Ok fields -> Some(fields |> Array.map _.Trim())
        | Error _ -> None)
    |> Seq.toList

/// CSV: one sheet, `data`, when the file has a header and a record.
let private csv: Extractor = {
    Formats = [ "csv" ]
    Parts =
        fun _ input -> async {
            return
                match csvRows (Encoding.UTF8.GetString input.Bytes) with
                | []
                | [ _ ] -> []
                | headers :: records -> [
                    Sheet(
                        SheetKind.Delimited,
                        {
                            SheetName = "data"
                            Headers = headers
                            // 1-based source rows: the header is row 1.
                            Rows = records |> List.mapi (fun i values -> (i + 2, values))
                        }
                    )
                  ]
        }
}

/// Plain text: running text, split by tokens with overlap so a query
/// straddling two chunks still hits one of them.
let private txt: Extractor = {
    Formats = [ "txt" ]
    Parts =
        fun _ input -> async {
            let text = (Encoding.UTF8.GetString input.Bytes).Trim()

            return
                if text.Length = 0 then
                    []
                else
                    [ Prose(ProseHeading.Running, text) ]
        }
}

/// The searchable formats' extractors, with the DOCX extractor `docx`
/// selects. An image is not among them: without an OCR companion it is not
/// searchable, and the upload policy reads this list.
let extractors (docx: DocxExtraction.DocxExtractionMode) : Extractor list = [
    pdf
    pptx
    (match docx with
     | DocxExtraction.DocxExtractionMode.Flat -> docxFlat
     | DocxExtraction.DocxExtractionMode.Structured -> docxStructured)
    xlsx
    csv
    txt
]

/// Extensions (lower-case, no leading dot) a searchable extractor reads:
/// the upload policy's "is this type searchable?", distinguishing an
/// unrecognised type (stored but unsearchable, or rejected) from a
/// recognised but empty file.
let supportedExtensions: Set<string> =
    extractors DocxExtraction.DocxExtractionMode.Flat
    |> List.collect _.Formats
    |> Set.ofList

/// `true` when an extractor recognises `ext` (lower-case, no leading
/// dot). Unrecognised types yield no parts.
let isSupportedExtension (ext: string) : bool =
    supportedExtensions.Contains(ext.ToLowerInvariant())

let private extensionOf (fileName: string) =
    Path.GetExtension(fileName).ToLowerInvariant().TrimStart('.')

/// The parts the extractor for `fileName`'s format reads; none for a
/// format no extractor reads.
let extractParts (deps: ExtractDeps) (fileName: string) (bytes: byte[]) : Async<Part list> =
    let input = {
        FileName = fileName
        Extension = extensionOf fileName
        Bytes = bytes
    }

    match
        image :: extractors deps.Docx
        |> List.tryFind (fun e -> List.contains input.Extension e.Formats)
    with
    | Some extractor -> extractor.Parts deps input
    | None -> async.Return []

/// An uploaded document's parts, and the chunks `chunksOf` folds them to.
let extract
    (deps: ExtractDeps)
    (docId: string)
    (fileName: string)
    (bytes: byte[])
    : Async<Part list * (TextChunk * SourceReference) list> =
    async {
        let! parts = extractParts deps fileName bytes

        let source = {
            DocumentId = docId
            FileName = fileName
            FileType = extensionOf fileName
        }

        return parts, chunksOf Document source parts
    }

/// The chunks of an uploaded document, with the deployment's composed DOCX
/// mode: the entry every caller that needs no parts reads.
let extractChunks
    (ocr: IOcrProvider)
    (tables: ITableExtractor)
    (docId: string)
    (fileName: string)
    (bytes: byte[])
    : Async<(TextChunk * SourceReference) list> =
    async {
        let! _, chunks = extract (ExtractDeps.composed ocr tables) docId fileName bytes
        return chunks
    }

/// The terminal status of an upload whose extraction yielded no chunk,
/// decided from the parts the extractor reported:
///
///   * a part only OCR could read, and none composed → `OcrUnavailable`
///   * a recognised format, genuinely empty         → `Complete 0`
///   * no extractor for the format                   → `UnsupportedFormat`
///
/// With an OCR companion composed the extractor reports no `Unread` part:
/// an empty result means the engine looked and found nothing. The detail
/// names the remedy, because it reaches the user verbatim.
let emptyExtractionStatus (fileName: string) (parts: Part list) : IngestionStatus =
    let unread =
        parts
        |> List.tryPick (fun part ->
            match part with
            | Unread textless -> Some textless
            | _ -> None)

    match unread with
    | Some Textless.Image ->
        OcrUnavailable(
            sprintf
                "'%s' is an image, so its text can only be recovered by OCR — no IOcrProvider companion is composed, so it was stored but not indexed. Compose one (e.g. ToolUp.OcrProviders.Tesseract) to make image uploads searchable."
                fileName
        )
    | Some Textless.ScannedPdf ->
        OcrUnavailable(
            sprintf
                "'%s' looks like a scanned PDF — its pages carry images but no text layer, so nothing could be indexed. No IOcrProvider companion is composed; compose one (e.g. ToolUp.OcrProviders.Tesseract) to make scanned documents searchable."
                fileName
        )
    | None ->
        let ext = extensionOf fileName

        if isSupportedExtension ext then
            Complete 0
        else
            UnsupportedFormat(sprintf "no extractor for '.%s' — stored but not searchable" ext)

/// Stamp the structured original-document reference into each chunk's
/// metadata at ingestion, so retrieval reads the ref
/// (`ChunkMetadata.OriginalRefKey`) instead of rebuilding the blob path.
/// Applied by the upload path only — an uploaded file is the one source
/// kind with a fetchable binary original; note and narrative chunks carry
/// no ref. The per-chunk `Location` is the neutral projection of the
/// chunk's provenance, so a Sources panel can open the original at the
/// cited page / slide / sheet / section.
let stampOriginalRefs
    (docId: string)
    (fileName: string)
    (fileType: string)
    (sizeBytes: int64)
    (pairs: (TextChunk * SourceReference) list)
    : (TextChunk * SourceReference) list =
    pairs
    |> List.map (fun (chunk, src) ->
        let originalRef: OriginalDocumentRef = {
            DocumentId = docId
            FileName = fileName
            FileType = fileType
            SizeBytes = sizeBytes
            Location = Some(SourceLocation.toLocator src.Location)
        }

        {
            chunk with
                Metadata = chunk.Metadata.Add(ChunkMetadata.OriginalRefKey, toJson originalRef)
        },
        src)