module internal KnowledgeBase.ServerExtractors

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open ToolUp.Platform
open ToolUp.Platform.IOcrProvider
open ToolUp.Platform.ITableExtractor
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.Chunking
open SharedTypes
open KnowledgeBase.ServerJsonHelpers

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

/// Extract (TextChunk * SourceReference) pairs from a PDF file.
/// Three-stage path:
///   1. `IOcrProvider.IsScanned` — if the document looks like a scanned
///      image PDF, route the whole file through `ExtractText` and emit
///      one chunk per OCR page.
///   2. `ITableExtractor.ExtractTables` — emit any extracted tables as
///      `chunkSpreadsheet`-formatted chunks (citable row ranges, repeated
///      column headers). Same chunk shape as XLSX/CSV ingestion.
///   3. PdfPig text extraction — emit page-text chunks, token-split via
///      `splitByTokens` when a page exceeds the budget.
///
/// With the no-op providers from `ToolUp.RAG.NoOpDocUnderstanding`,
/// step 1 returns `false` and step 2 returns `[]`, so the path collapses
/// to step 3 — byte-equivalent to the pre-Phase-14i extractor.
let private extractPdf
    (ocr: IOcrProvider)
    (tables: ITableExtractor)
    (docId: string)
    (fileName: string)
    (bytes: byte[])
    : Async<(TextChunk * SourceReference) list> =
    async {
        let mimeType = "application/pdf"
        let config = ChunkingConfig.defaults

        let makeSrc (location: SourceLocation) : SourceReference = {
            DocumentId = docId
            DocumentName = fileName
            FileType = "pdf"
            Location = location
            IndexedAt = DateTimeOffset.UtcNow
        }

        let! isScanned = ocr.IsScanned bytes mimeType

        if isScanned then
            let! pages = ocr.ExtractText bytes mimeType

            return [
                for p in pages do
                    let text = p.Text.Trim()

                    if text.Length > 10 then
                        let src = makeSrc (Page p.PageNumber)
                        let header = sprintf "Page %d (OCR)" p.PageNumber
                        let chunk = makeChunk Document fileName header text src
                        (chunk, src)
            ]
        else
            let! extractedTables = tables.ExtractTables bytes mimeType

            let tableChunks = [
                for t in extractedTables do
                    let sheetName =
                        match t.Page with
                        | Some n -> sprintf "Page %d" n
                        | None -> "Table"

                    let sheetData: SheetData = {
                        SheetName = sheetName
                        Headers = t.Headers
                        Rows = t.Rows
                    }

                    let pieces = chunkSpreadsheet config sheetData

                    for i, piece in List.indexed pieces do
                        let location =
                            match t.Page with
                            | Some n -> Page n
                            | None -> Page 0

                        let src = makeSrc location

                        let header =
                            if pieces.Length = 1 then
                                sprintf "%s (table)" sheetName
                            else
                                sprintf "%s (table, part %d of %d)" sheetName (i + 1) pieces.Length

                        let chunk = makeChunk Document fileName header piece src
                        (chunk, src)
            ]

            use doc = UglyToad.PdfPig.PdfDocument.Open(bytes)

            let textChunks = [
                for page in doc.GetPages() do
                    let text = page.GetWords() |> Seq.map _.Text |> String.concat " " |> _.Trim()

                    if text.Length > 10 then
                        let pieces = splitByTokens config text

                        // splitByTokens returns [] for inputs below MinTokens; in
                        // that case keep the original page as a single short chunk
                        // so we never silently drop content.
                        let parts = if pieces.IsEmpty then [ text ] else pieces

                        let total = parts.Length

                        for i, piece in List.indexed parts do
                            let header =
                                if total = 1 then
                                    sprintf "Page %d" page.Number
                                else
                                    sprintf "Page %d (part %d of %d)" page.Number (i + 1) total

                            let src = makeSrc (Page page.Number)
                            let chunk = makeChunk Document fileName header piece src
                            (chunk, src)
            ]

            return textChunks @ tableChunks
    }

/// Extract (TextChunk * SourceReference) pairs from a PPTX file.
let private extractPptx (docId: string) (fileName: string) (bytes: byte[]) : (TextChunk * SourceReference) list =
    use stream = new MemoryStream(bytes)

    use presentation =
        DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(stream, false)

    let pres = presentation.PresentationPart.Presentation

    let results = System.Collections.Generic.List<TextChunk * SourceReference>()
    let mutable slideNum = 0

    for slideId in pres.SlideIdList.Elements<DocumentFormat.OpenXml.Presentation.SlideId>() do
        slideNum <- slideNum + 1
        let rId = slideId.RelationshipId.Value

        let slidePart =
            presentation.PresentationPart.GetPartById(rId) :?> DocumentFormat.OpenXml.Packaging.SlidePart

        // Title placeholder shapes have PlaceholderShape.Type = Title or CenteredTitle.
        // Fall back to None when the slide has no title shape.
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
            let titleStr = title |> Option.defaultValue ""

            let baseHeader =
                if titleStr = "" then
                    sprintf "Slide %d" slideNum
                else
                    sprintf "Slide %d: \"%s\"" slideNum titleStr

            let pieces = splitByTokens ChunkingConfig.defaults allText

            let parts = if pieces.IsEmpty then [ allText ] else pieces

            let total = parts.Length

            for i, piece in List.indexed parts do
                let header =
                    if total = 1 then
                        baseHeader
                    else
                        sprintf "%s (part %d of %d)" baseHeader (i + 1) total

                let src: SourceReference = {
                    DocumentId = docId
                    DocumentName = fileName
                    FileType = "pptx"
                    Location = Slide(slideNum, title)
                    IndexedAt = DateTimeOffset.UtcNow
                }

                let chunk = makeChunk Document fileName header piece src
                results.Add((chunk, src))

    results |> Seq.toList

/// Extract (TextChunk * SourceReference) pairs from a DOCX file.
let private extractDocx (docId: string) (fileName: string) (bytes: byte[]) : (TextChunk * SourceReference) list =
    use stream = new MemoryStream(bytes)

    use doc =
        DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(stream, false)

    let body = doc.MainDocumentPart.Document.Body

    let results = System.Collections.Generic.List<TextChunk * SourceReference>()
    let mutable currentHeading = "Document"
    let mutable currentParagraphs = System.Collections.Generic.List<string>()

    let flushSection () =
        if currentParagraphs.Count > 0 then
            let text = String.concat " " currentParagraphs
            let pieces = splitByTokens ChunkingConfig.defaults text

            let parts = if pieces.IsEmpty then [ text ] else pieces

            let total = parts.Length

            for i, piece in List.indexed parts do
                let header =
                    if total = 1 then
                        sprintf "Section: \"%s\"" currentHeading
                    else
                        sprintf "Section: \"%s\" (part %d of %d)" currentHeading (i + 1) total

                let src: SourceReference = {
                    DocumentId = docId
                    DocumentName = fileName
                    FileType = "docx"
                    Location = Section currentHeading
                    IndexedAt = DateTimeOffset.UtcNow
                }

                let chunk = makeChunk Document fileName header piece src
                results.Add((chunk, src))

            currentParagraphs <- System.Collections.Generic.List<string>()

    for para in body.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>() do
        let styleId =
            match para.ParagraphProperties with
            | null -> ""
            | props ->
                match props.ParagraphStyleId with
                | null -> ""
                | sid -> sid.Val.Value

        let text = para.InnerText.Trim()

        if text.Length > 0 then
            if styleId.StartsWith "Heading" then
                flushSection ()
                currentHeading <- text
            else
                currentParagraphs.Add(text)

    flushSection ()
    results |> Seq.toList

/// Parse the row range "rows N–M of T" emitted by `Chunking.chunkSpreadsheet`'s
/// header line. Used to round-trip the row range into the chunk's
/// `SourceReference.Location` for click-through citations.
let private parseRowRange (chunkText: string) : (int * int) option =
    let m = Regex.Match(chunkText, @"rows (\d+)\u2013(\d+)", RegexOptions.None)

    if m.Success then
        let startRow = int m.Groups[1].Value
        let endRow = int m.Groups[2].Value
        Some(startRow, endRow)
    else
        None

/// Extract (TextChunk * SourceReference) pairs from an XLSX file.
let private extractXlsx (docId: string) (fileName: string) (bytes: byte[]) : (TextChunk * SourceReference) list =
    use stream = new MemoryStream(bytes)

    use workbook =
        DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false)

    let sheets =
        workbook.WorkbookPart.Workbook.Sheets.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>()

    let results = System.Collections.Generic.List<TextChunk * SourceReference>()

    let sharedStrings =
        match workbook.WorkbookPart.SharedStringTablePart with
        | null -> [||]
        | sst ->
            sst.SharedStringTable.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>()
            |> Seq.map _.InnerText
            |> Seq.toArray

    let getCellValue (cell: DocumentFormat.OpenXml.Spreadsheet.Cell) =
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

    let getCells (row: DocumentFormat.OpenXml.Spreadsheet.Row) =
        row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>() |> Seq.toArray

    for sheet in sheets do
        let sheetName = sheet.Name.Value
        let rId = sheet.Id.Value

        let worksheetPart =
            workbook.WorkbookPart.GetPartById(rId) :?> DocumentFormat.OpenXml.Packaging.WorksheetPart

        let rows =
            worksheetPart.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>()
            |> Seq.toArray

        if rows.Length >= 2 then
            let headers = getCells rows[0] |> Array.map getCellValue

            let dataRows =
                rows
                |> Array.skip 1
                |> Array.mapi (fun i r ->
                    let values = getCells r |> Array.map getCellValue
                    // 1-based source row index: header is row 1, first data row is 2.
                    (i + 2, values))
                |> Array.toList

            let sheetData: SheetData = {
                SheetName = sheetName
                Headers = headers
                Rows = dataRows
            }

            // Schema-+-sample chunk first — gives retrieval a single
            // chunk that's high-recall for "what columns does this
            // sheet have?" / "show me an example row" queries.
            let sampleRowsText =
                dataRows
                |> List.truncate 5
                |> List.map (fun (_, values) -> formatRow headers values)
                |> String.concat "\n"

            let schemaSrc: SourceReference = {
                DocumentId = docId
                DocumentName = fileName
                FileType = "xlsx"
                Location = Sheet(sheetName, None)
                IndexedAt = DateTimeOffset.UtcNow
            }

            let schemaHeader = sprintf "Sheet \"%s\", schema + sample" sheetName

            let schemaContent =
                let colList = String.concat ", " headers
                sprintf "Columns: %s\n%s" colList sampleRowsText

            let schemaChunk = makeChunk Document fileName schemaHeader schemaContent schemaSrc
            results.Add((schemaChunk, schemaSrc))

            // Token-aware row-group chunks. `chunkSpreadsheet` packs as
            // many rows as fit within `MaxTokens`, repeating the column
            // headers per chunk and emitting a `rows N–M of T` header line.
            let rowChunks = chunkSpreadsheet ChunkingConfig.tabular sheetData

            for groupContent in rowChunks do
                let rowRange =
                    match parseRowRange groupContent with
                    | Some(s, e) -> sprintf "rows %d\u2013%d" s e
                    | None -> "rows"

                let groupSrc: SourceReference = {
                    DocumentId = docId
                    DocumentName = fileName
                    FileType = "xlsx"
                    Location = Sheet(sheetName, Some rowRange)
                    IndexedAt = DateTimeOffset.UtcNow
                }

                let groupHeader = sprintf "Sheet \"%s\", %s" sheetName rowRange
                let groupChunk = makeChunk Document fileName groupHeader groupContent groupSrc
                results.Add((groupChunk, groupSrc))

    results |> Seq.toList

/// Extract (TextChunk * SourceReference) pairs from a CSV file.
let private extractCsv (docId: string) (fileName: string) (bytes: byte[]) : (TextChunk * SourceReference) list =
    let text = Encoding.UTF8.GetString bytes

    let lines =
        text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries)

    if lines.Length < 2 then
        []
    else
        let results = System.Collections.Generic.List<TextChunk * SourceReference>()

        let splitCsv (line: string) = line.Split(',') |> Array.map _.Trim()

        let headers = splitCsv lines[0]
        let dataLines = lines |> Array.skip 1

        let dataRows =
            dataLines
            // 1-based source row index: header is row 1, first data row is 2.
            |> Array.mapi (fun i l -> (i + 2, splitCsv l))
            |> Array.toList

        let sheetData: SheetData = {
            SheetName = "data"
            Headers = headers
            Rows = dataRows
        }

        // Schema-+-sample chunk first — high-recall for "what columns
        // does this file have?" / "show me an example row" queries.
        let sampleRowsText =
            dataRows
            |> List.truncate 5
            |> List.map (fun (_, values) -> formatRow headers values)
            |> String.concat "\n"

        let schemaSrc: SourceReference = {
            DocumentId = docId
            DocumentName = fileName
            FileType = "csv"
            Location = Sheet("data", None)
            IndexedAt = DateTimeOffset.UtcNow
        }

        let schemaContent =
            let colList = String.concat ", " headers
            sprintf "Columns: %s\n%s" colList sampleRowsText

        let schemaChunk =
            makeChunk Document fileName "schema + sample" schemaContent schemaSrc

        results.Add((schemaChunk, schemaSrc))

        // Token-aware row-group chunks via `chunkSpreadsheet`. The chunker
        // packs as many rows as fit within `MaxTokens`, repeats the column
        // headers per chunk, and emits a `rows N–M of T` header line we
        // round-trip back into `Location = RowGroup(startRow, endRow)`.
        let rowChunks = chunkSpreadsheet ChunkingConfig.tabular sheetData

        for groupContent in rowChunks do
            let startRow, endRow =
                match parseRowRange groupContent with
                | Some(s, e) -> s, e
                | None -> 2, dataLines.Length + 1

            let groupSrc: SourceReference = {
                DocumentId = docId
                DocumentName = fileName
                FileType = "csv"
                Location = RowGroup(startRow, endRow)
                IndexedAt = DateTimeOffset.UtcNow
            }

            let groupHeader = sprintf "rows %d\u2013%d" startRow endRow
            let groupChunk = makeChunk Document fileName groupHeader groupContent groupSrc
            results.Add((groupChunk, groupSrc))

        results |> Seq.toList

/// Extract (TextChunk * SourceReference) pairs from a plain-text file.
/// Splits into token-budgeted chunks via `splitByTokens` rather than fixed
/// line counts — preserves sentence boundaries and applies overlap so a
/// query straddling two chunks still hits at least one of them.
let private extractTxt (docId: string) (fileName: string) (bytes: byte[]) : (TextChunk * SourceReference) list =
    let text = (Encoding.UTF8.GetString bytes).Trim()

    if text.Length = 0 then
        []
    else
        let pieces = splitByTokens ChunkingConfig.defaults text
        let results = System.Collections.Generic.List<TextChunk * SourceReference>()

        let total = pieces.Length

        pieces
        |> List.iteri (fun i chunkText ->
            let header =
                if total = 1 then
                    "text"
                else
                    sprintf "part %d of %d" (i + 1) total

            let src: SourceReference = {
                DocumentId = docId
                DocumentName = fileName
                FileType = "txt"
                Location = Section header
                IndexedAt = DateTimeOffset.UtcNow
            }

            let chunk = makeChunk Document fileName header chunkText src
            results.Add((chunk, src)))

        results |> Seq.toList

/// Dispatch to the appropriate extractor based on file extension. The
/// PDF path is async because it consults `IOcrProvider` /
/// `ITableExtractor` (Phase 14i). Other extractors operate on already
/// structured formats and stay synchronous, lifted into `async` for a
/// uniform return type.
let extractChunks
    (ocr: IOcrProvider)
    (tables: ITableExtractor)
    (docId: string)
    (fileName: string)
    (bytes: byte[])
    : Async<(TextChunk * SourceReference) list> =
    async {
        match Path.GetExtension(fileName).ToLowerInvariant() with
        | ".pdf" -> return! extractPdf ocr tables docId fileName bytes
        | ".pptx" -> return extractPptx docId fileName bytes
        | ".docx" -> return extractDocx docId fileName bytes
        | ".xlsx" -> return extractXlsx docId fileName bytes
        | ".csv" -> return extractCsv docId fileName bytes
        | ".txt" -> return extractTxt docId fileName bytes
        | _ -> return []
    }

// ─── Document index persistence ───────────────────────────────────