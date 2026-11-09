module ToolUp.Platform.Tests.InProcess.ExtractorTests

// The golden net below holds every format's chunks byte for byte.
//
// The knowledge base's CSV extractor reads records through the RFC 4180
// reader `ToolUp.Tabular` ships, so a quoted field keeps its embedded
// delimiters and line breaks. An unquoted file reads exactly as the
// line-and-comma split read it, so its chunk text does not move.

open System
open System.IO
open Expecto
open SkiaSharp
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open UglyToad.PdfPig.Core
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Writer
open ToolUp.Platform.Infra.IOcrProvider
open ToolUp.Platform.Infra.ITableExtractor
open ToolUp.Platform.VectorKnowledgeTypes
open SharedTypes
open KnowledgeBase.ServerExtractors

let private noOpOcr = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
let private noOpTables = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

/// The rule the extractor applied before it used the quoted reader:
/// non-empty lines, each split on ',' with every cell trimmed.
let private lineSplit (text: string) : string[] list =
    text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun line -> line.Split(',') |> Array.map _.Trim())
    |> Array.toList

let private quotedReader (text: string) : string[] list =
    use reader = new StringReader(text)

    ToolUp.Tabular.Csv.parseRecords ',' reader
    |> Seq.choose (fun (_, record) ->
        match record with
        | Ok [| "" |] -> None
        | Ok fields -> Some(fields |> Array.map _.Trim())
        | Error _ -> None)
    |> Seq.toList

let private quoted =
    "name,notes,amount\r\n\"Smith, J\",\"line one\nline two\",12\r\nJones,plain,7\r\n"

let private unquoted =
    "region,quarter,revenue\nnorth,Q1,100\n\nsouth,Q1,  200 \neast,Q2,300\n"

let private csvTests =
    testList "KnowledgeBase CSV extraction" [
        test "a quoted comma and a quoted newline yield the quoted reader's cells" {
            let rows = csvRows quoted
            Expect.equal rows (quotedReader quoted) "the cells are the quoted reader's"

            Expect.equal
                rows[1]
                [| "Smith, J"; "line one\nline two"; "12" |]
                "the quoted fields keep their comma and their line break"

            Expect.notEqual rows (lineSplit quoted) "a ',' split would have broken the quoted fields apart"
        }

        test "the quoted cells reach the chunk text" {
            let chunks =
                extractChunks noOpOcr noOpTables "doc-1" "people.csv" (Text.Encoding.UTF8.GetBytes quoted)
                |> Async.RunSynchronously
                |> List.map (fun (chunk, _) -> chunk.Content)
                |> String.concat "\n"

            let expectedRow =
                ToolUp.RAG.Chunking.formatRow [| "name"; "notes"; "amount" |] [|
                    "Smith, J"
                    "line one\nline two"
                    "12"
                |]

            Expect.stringContains chunks expectedRow "the row is formatted from its real cells"
        }

        test "an unquoted CSV reads exactly as the line-and-comma split read it" {
            Expect.equal (csvRows unquoted) (lineSplit unquoted) "same cells, so the same chunk text"
        }
    ]

// ─── The golden net: one fixture per format ──────────────────────────
//
// Each format's input is synthesised here, deterministically, and its
// `(TextChunk * SourceReference) list` is rendered to text and compared
// with `fixtures/extractors/<name>.golden.txt`. `IndexedAt` is the one
// field a rendering may not carry (it is the clock), so it renders as a
// placeholder, in the source reference and in the `_source` metadata.

let private goldenDir =
    Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", "extractors")

let private indexedAt =
    System.Text.RegularExpressions.Regex(
        "\"IndexedAt\"\\s*:\\s*\"[^\"]*\"",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )

let private render (pairs: (TextChunk * SourceReference) list) =
    let builder = System.Text.StringBuilder()

    for i, (chunk, src) in List.indexed pairs do
        builder.Append(sprintf "=== chunk %d\n" (i + 1)) |> ignore

        builder.Append(sprintf "source: %s | %s | %s | %A\n" src.DocumentId src.DocumentName src.FileType src.Location)
        |> ignore

        for KeyValue(key, value) in chunk.Metadata do
            builder.Append(sprintf "meta %s = %s\n" key (indexedAt.Replace(value, "\"IndexedAt\":\"<clock>\"")))
            |> ignore

        builder.Append("content:\n").Append(chunk.Content).Append("\n") |> ignore

    builder.ToString()

/// Compare `actual` with the named golden file. A mismatch writes the
/// rendering under the system temp directory and names it, so the two
/// can be diffed.
let private expectGolden (name: string) (actual: string) =
    let path = Path.Combine(goldenDir, name + ".golden.txt")

    let expected =
        if File.Exists path then
            Some((File.ReadAllText path).Replace("\r\n", "\n"))
        else
            None

    if expected <> Some actual then
        let scratch = Path.Combine(Path.GetTempPath(), "toolup-extractor-golden")
        Directory.CreateDirectory scratch |> ignore
        let written = Path.Combine(scratch, name + ".golden.txt")
        File.WriteAllText(written, actual)
        failtestf "the %s chunks differ from %s; the rendering is at %s" name path written

type private StubOcr(scanned: bool, pages: OcrPage list) =
    interface IOcrProvider with
        member _.Name = "stub-ocr"
        member _.IsScanned _ _ = async { return scanned }
        member _.ExtractText _ _ = async { return pages }

let private stubTables (tables: ExtractedTable list) =
    { new ITableExtractor with
        member _.Name = "stub-tables"
        member _.ExtractTables _ _ = async { return tables }
    }

/// Sentences enough to carry a prose block past the chunker's token budget.
let private longProse (topic: string) (sentences: int) =
    [
        for i in 1..sentences ->
            sprintf
                "The %s review number %d recorded steady progress across the northern region and the southern region alike."
                topic
                i
    ]
    |> String.concat " "

/// Wrap prose into lines of at most `width` words.
let private lines (width: int) (text: string) =
    text.Split(' ')
    |> Array.chunkBySize width
    |> Array.map (String.concat " ")
    |> List.ofArray

let private pdfOf (pages: string list list) : byte[] =
    use builder = new PdfDocumentBuilder()
    let font = builder.AddStandard14Font Standard14Font.Helvetica

    for pageLines in pages do
        let page = builder.AddPage(612.0, 792.0)

        for i, line in List.indexed pageLines do
            page.AddText(line, 9.0, PdfPoint(30.0, 760.0 - 11.0 * float i), font) |> ignore

    builder.Build()

/// A small raster, the content of the scanned PDF and the image upload.
let private rasterJpeg () : byte[] =
    let info = SKImageInfo(64, 32, SKColorType.Rgba8888, SKAlphaType.Premul)
    use bitmap = new SKBitmap(info)
    use canvas = new SKCanvas(bitmap)
    canvas.Clear SKColors.White
    canvas.Flush()
    use image = SKImage.FromBitmap bitmap
    use data = image.Encode(SKEncodedImageFormat.Jpeg, 90)
    data.ToArray()

/// A PDF whose one page carries an image and no text layer.
let private scanPdf () : byte[] =
    use builder = new PdfDocumentBuilder()
    let page = builder.AddPage(612.0, 792.0)

    page.AddJpeg(rasterJpeg (), PdfRectangle(50.0, 400.0, 550.0, 700.0)) |> ignore

    builder.Build()

let private docxOf (build: Wordprocessing.Body -> unit) : byte[] =
    use stream = new MemoryStream()

    do
        use doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document)

        let main = doc.AddMainDocumentPart()
        let document = Wordprocessing.Document()
        let body = Wordprocessing.Body()
        document.AppendChild body |> ignore
        main.Document <- document
        build body
        main.Document.Save()

    stream.ToArray()

let private docxParagraph (style: string option) (text: string) =
    let p = Wordprocessing.Paragraph()

    style
    |> Option.iter (fun sid ->
        let props = Wordprocessing.ParagraphProperties()

        props.AppendChild(Wordprocessing.ParagraphStyleId(Val = StringValue sid))
        |> ignore

        p.AppendChild props |> ignore)

    let run = Wordprocessing.Run()

    run.AppendChild(Wordprocessing.Text(text, Space = EnumValue SpaceProcessingModeValues.Preserve))
    |> ignore

    p.AppendChild run |> ignore
    p

let private docxTable (rows: string list list) =
    let table = Wordprocessing.Table()

    for row in rows do
        let tr = Wordprocessing.TableRow()

        for cell in row do
            let tc = Wordprocessing.TableCell()
            tc.AppendChild(docxParagraph None cell) |> ignore
            tr.AppendChild tc |> ignore

        table.AppendChild tr |> ignore

    table

let private knowledgeDocx () =
    docxOf (fun body ->
        let add (element: DocumentFormat.OpenXml.OpenXmlElement) = body.AppendChild element |> ignore
        add (docxParagraph None "Preamble before any heading.")
        add (docxParagraph (Some "Heading1") "Intro")
        add (docxParagraph None "Alpha beta gamma.")
        add (docxParagraph (Some "Heading2") "Sub")
        add (docxParagraph None "One")
        add (docxParagraph None "Two")
        add (docxTable [ [ "Col"; "Val" ]; [ "a"; "1" ]; [ "b"; "2" ] ])
        add (docxTable [ [ "only"; "row" ] ])
        add (docxParagraph (Some "Heading1") "Long")
        add (docxParagraph None (longProse "docx" 60)))

let private pptxOf (slides: (string option * string list) list) : byte[] =
    use stream = new MemoryStream()

    do
        use doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation)

        let presentationPart = doc.AddPresentationPart()
        let presentation = Presentation.Presentation()
        let slideIds = Presentation.SlideIdList()

        for i, (title, bodies) in List.indexed slides do
            let relationshipId = sprintf "rId%d" (i + 1)
            let slidePart = presentationPart.AddNewPart<SlidePart>(relationshipId)
            let tree = Presentation.ShapeTree()

            let shape (placeholder: Presentation.PlaceholderShape option) (text: string) =
                let appProps = Presentation.ApplicationNonVisualDrawingProperties()
                placeholder |> Option.iter (fun ph -> appProps.AppendChild ph |> ignore)
                let nv = Presentation.NonVisualShapeProperties()

                nv.AppendChild(Presentation.NonVisualDrawingProperties(Id = UInt32Value 2u, Name = StringValue "s"))
                |> ignore

                nv.AppendChild(Presentation.NonVisualShapeDrawingProperties()) |> ignore
                nv.AppendChild appProps |> ignore
                let run = Drawing.Run()
                run.AppendChild(Drawing.Text(Text = text)) |> ignore
                let paragraph = Drawing.Paragraph()
                paragraph.AppendChild run |> ignore
                let textBody = Presentation.TextBody()
                textBody.AppendChild(Drawing.BodyProperties()) |> ignore
                textBody.AppendChild paragraph |> ignore
                let sp = Presentation.Shape()
                sp.AppendChild nv |> ignore
                sp.AppendChild(Presentation.ShapeProperties()) |> ignore
                sp.AppendChild textBody |> ignore
                sp

            title
            |> Option.iter (fun t ->
                let ph =
                    Presentation.PlaceholderShape(Type = EnumValue Presentation.PlaceholderValues.Title)

                tree.AppendChild(shape (Some ph) t) |> ignore)

            for body in bodies do
                tree.AppendChild(shape None body) |> ignore

            let commonSlideData = Presentation.CommonSlideData()
            commonSlideData.AppendChild tree |> ignore
            let slide = Presentation.Slide()
            slide.AppendChild commonSlideData |> ignore
            slidePart.Slide <- slide
            slidePart.Slide.Save()

            slideIds.AppendChild(
                Presentation.SlideId(Id = UInt32Value(uint32 (256 + i)), RelationshipId = StringValue relationshipId)
            )
            |> ignore

        presentation.AppendChild slideIds |> ignore
        presentationPart.Presentation <- presentation
        presentationPart.Presentation.Save()

    stream.ToArray()

let private xlsxOf (sheets: (string * string list list) list) : byte[] =
    use stream = new MemoryStream()

    do
        use doc = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook)

        let workbookPart = doc.AddWorkbookPart()
        workbookPart.Workbook <- Spreadsheet.Workbook()
        let sheetList = Spreadsheet.Sheets()
        let sharedPart = workbookPart.AddNewPart<SharedStringTablePart>()
        let shared = Spreadsheet.SharedStringTable()
        let mutable sharedCount = 0

        for i, (name, rows) in List.indexed sheets do
            let worksheetPart = workbookPart.AddNewPart<WorksheetPart>()
            let data = Spreadsheet.SheetData()

            for r, cells in List.indexed rows do
                let row = Spreadsheet.Row(RowIndex = UInt32Value(uint32 (r + 1)))

                for c, value in List.indexed cells do
                    let reference = sprintf "%c%d" (char (int 'A' + c)) (r + 1)

                    let numeric, _ =
                        Double.TryParse(
                            value,
                            Globalization.NumberStyles.Float,
                            Globalization.CultureInfo.InvariantCulture
                        )

                    let cell = Spreadsheet.Cell(CellReference = StringValue reference)

                    if numeric then
                        cell.CellValue <- Spreadsheet.CellValue(value)
                    else
                        shared.AppendChild(Spreadsheet.SharedStringItem(Spreadsheet.Text(value)))
                        |> ignore

                        cell.DataType <- EnumValue Spreadsheet.CellValues.SharedString
                        cell.CellValue <- Spreadsheet.CellValue(string sharedCount)
                        sharedCount <- sharedCount + 1

                    row.AppendChild cell |> ignore

                data.AppendChild row |> ignore

            let worksheet = Spreadsheet.Worksheet()
            worksheet.AppendChild data |> ignore
            worksheetPart.Worksheet <- worksheet

            sheetList.AppendChild(
                Spreadsheet.Sheet(
                    Id = StringValue(workbookPart.GetIdOfPart worksheetPart),
                    SheetId = UInt32Value(uint32 (i + 1)),
                    Name = StringValue name
                )
            )
            |> ignore

        sharedPart.SharedStringTable <- shared
        workbookPart.Workbook.AppendChild sheetList |> ignore
        workbookPart.Workbook.Save()

    stream.ToArray()

let private extractWith (ocr: IOcrProvider) (tables: ITableExtractor) (fileName: string) (bytes: byte[]) =
    extractChunks ocr tables "doc-golden" fileName bytes |> Async.RunSynchronously

let private ocrPages = [
    {
        PageNumber = 1
        Text = "Invoice 4471 total due forty two pounds."
    }
    { PageNumber = 2; Text = "short" }
    {
        PageNumber = 3
        Text = longProse "scanned" 50
    }
]

let private longCsv =
    "id,description\n"
    + String.concat "\n" [
        for i in 1..300 -> sprintf "%d,row %d carries a description long enough to fill" i i
    ]

/// The golden scenarios: a name, and the extraction that produces it.
let private goldenScenarios: (string * (unit -> (TextChunk * SourceReference) list)) list = [
    "pdf-text",
    fun () ->
        let bytes =
            pdfOf [
                [ "Quarterly revenue rose by eleven percent in the north." ]
                lines 14 (longProse "pdf" 70)
                [ "tiny" ]
            ]

        extractWith noOpOcr noOpTables "report.pdf" bytes
    "pdf-scanned-ocr", (fun () -> extractWith (StubOcr(true, ocrPages)) noOpTables "scan.pdf" (scanPdf ()))
    "pdf-ocr-fallback", (fun () -> extractWith (StubOcr(false, ocrPages)) noOpTables "stamped.pdf" (scanPdf ()))
    "pdf-scanned-no-ocr", (fun () -> extractWith noOpOcr noOpTables "unread.pdf" (scanPdf ()))
    "pdf-tables",
    fun () ->
        let tables =
            stubTables [
                {
                    Page = Some 1
                    Headers = [| "region"; "revenue" |]
                    Rows = [ 2, [| "north"; "100" |]; 3, [| "south"; "200" |] ]
                }
                {
                    Page = None
                    Headers = [| "k"; "v" |]
                    Rows = [
                        for i in 2..400 -> i, [| sprintf "key %d with some padding words" i; string i |]
                    ]
                }
            ]

        extractWith noOpOcr tables "tables.pdf" (pdfOf [ [ "A page of text that carries the two tables below." ] ])
    "pptx",
    fun () ->
        let bytes =
            pptxOf [
                Some "Overview", [ "First slide body text here." ]
                None, [ "Untitled slide body text." ]
                Some "Long", [ longProse "pptx" 60 ]
                Some "Bare", []
            ]

        extractWith noOpOcr noOpTables "deck.pptx" bytes
    "docx-flat", (fun () -> extractWith noOpOcr noOpTables "notes.docx" (knowledgeDocx ()))
    "docx-structured",
    fun () ->
        try
            KnowledgeBase.DocxExtraction.enableStructuredDocxExtraction ()
            extractWith noOpOcr noOpTables "notes.docx" (knowledgeDocx ())
        finally
            KnowledgeBase.DocxExtraction.resetToFlatDocxExtraction ()
    "xlsx",
    fun () ->
        let bytes =
            xlsxOf [
                "Sales",
                [
                    [ "region"; "quarter"; "revenue" ]
                    [ "north"; "Q1"; "100" ]
                    [ "south"; "Q1"; "200.5" ]
                ]
                "Wide",
                [ "id"; "description" ]
                :: [
                    for i in 1..300 -> [ string i; sprintf "row %d carries a description long enough to fill" i ]
                ]
                "HeaderOnly", [ [ "a"; "b" ] ]
            ]

        extractWith noOpOcr noOpTables "book.xlsx" bytes
    "csv", (fun () -> extractWith noOpOcr noOpTables "people.csv" (System.Text.Encoding.UTF8.GetBytes quoted))
    "csv-long", (fun () -> extractWith noOpOcr noOpTables "long.csv" (System.Text.Encoding.UTF8.GetBytes longCsv))
    "txt",
    (fun () -> extractWith noOpOcr noOpTables "notes.txt" (System.Text.Encoding.UTF8.GetBytes(longProse "txt" 80)))
    "txt-short",
    (fun () -> extractWith noOpOcr noOpTables "short.txt" (System.Text.Encoding.UTF8.GetBytes "  A short note.  "))
    "image-ocr", (fun () -> extractWith (StubOcr(false, ocrPages)) noOpTables "receipt.png" (rasterJpeg ()))
    "image-no-ocr", (fun () -> extractWith noOpOcr noOpTables "receipt.png" (rasterJpeg ()))
]

let private goldenTests =
    testList "Phase 1086 — every format's chunks match their golden fixture" [
        for name, extract in goldenScenarios do
            test name { expectGolden name (render (extract ())) }
    ]

let tests = testList "KnowledgeBase extraction" [ csvTests; goldenTests ]