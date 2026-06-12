// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// In-memory `.docx` fixture builders, constructed with the OpenXml
/// SDK's typed classes directly (the same approach as the workbook
/// builders in the Tabular test pack) so the corpus needs no binary
/// files in the repo.
module ToolUp.OpenXml.Tests.Fixtures

open System
open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging

let private buildDocx (build: WordprocessingDocument -> MainDocumentPart -> unit) : byte[] =
    use stream = new MemoryStream()
    let doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    // Explicit append — the `Document(child)` ctor overload set
    // resolves a single composite argument to IEnumerable and copies
    // the child's children instead of attaching the child.
    let document = Wordprocessing.Document()
    document.AppendChild(Wordprocessing.Body()) |> ignore
    main.Document <- document
    build doc main
    main.Document.Save()
    (doc :> IDisposable).Dispose()
    stream.ToArray()

let private text (s: string) =
    Wordprocessing.Text(Text = s, Space = EnumValue SpaceProcessingModeValues.Preserve)

let plainRun (s: string) =
    let r = Wordprocessing.Run()
    r.AppendChild(text s) |> ignore
    r

let boldRun (s: string) =
    let props = Wordprocessing.RunProperties()
    props.AppendChild(Wordprocessing.Bold()) |> ignore
    let r = Wordprocessing.Run()
    r.AppendChild props |> ignore
    r.AppendChild(text s) |> ignore
    r

let styledParagraph (styleId: string option) (runs: Wordprocessing.Run list) =
    let p = Wordprocessing.Paragraph()

    styleId
    |> Option.iter (fun sid ->
        let props = Wordprocessing.ParagraphProperties()

        props.AppendChild(Wordprocessing.ParagraphStyleId(Val = StringValue sid))
        |> ignore

        p.AppendChild props |> ignore)

    for r in runs do
        p.AppendChild r |> ignore

    p

let paragraph (s: string) = styledParagraph None [ plainRun s ]

let listItem (numId: int) (level: int) (s: string) =
    let numbering = Wordprocessing.NumberingProperties()

    numbering.AppendChild(Wordprocessing.NumberingLevelReference(Val = Int32Value level))
    |> ignore

    numbering.AppendChild(Wordprocessing.NumberingId(Val = Int32Value numId))
    |> ignore

    let props = Wordprocessing.ParagraphProperties()
    props.AppendChild numbering |> ignore
    let p = Wordprocessing.Paragraph()
    p.AppendChild props |> ignore
    p.AppendChild(plainRun s) |> ignore
    p

let table (rows: string list list) =
    let tbl = Wordprocessing.Table()
    let borders = Wordprocessing.TableBorders()

    borders.AppendChild(Wordprocessing.TopBorder(Val = EnumValue Wordprocessing.BorderValues.Single))
    |> ignore

    borders.AppendChild(Wordprocessing.BottomBorder(Val = EnumValue Wordprocessing.BorderValues.Single))
    |> ignore

    let tblProps = Wordprocessing.TableProperties()
    tblProps.AppendChild borders |> ignore
    tbl.AppendChild tblProps |> ignore

    let grid = Wordprocessing.TableGrid()

    for _ in (List.head rows) do
        grid.AppendChild(Wordprocessing.GridColumn(Width = StringValue "2400"))
        |> ignore

    tbl.AppendChild grid |> ignore

    for row in rows do
        let tr = Wordprocessing.TableRow()

        for cell in row do
            let tc = Wordprocessing.TableCell()
            tc.AppendChild(paragraph cell) |> ignore
            tr.AppendChild tc |> ignore

        tbl.AppendChild tr |> ignore

    tbl

let private headingStyle (level: int) =
    let style =
        Wordprocessing.Style(
            Type = EnumValue Wordprocessing.StyleValues.Paragraph,
            StyleId = StringValue(sprintf "Heading%d" level)
        )

    style.AppendChild(Wordprocessing.StyleName(Val = StringValue(sprintf "heading %d" level)))
    |> ignore

    style.AppendChild(Wordprocessing.BasedOn(Val = StringValue "Normal")) |> ignore

    style

let private addStandardStyles (main: MainDocumentPart) =
    let part = main.AddNewPart<StyleDefinitionsPart>()
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
    styles.AppendChild(headingStyle 1) |> ignore
    styles.AppendChild(headingStyle 2) |> ignore
    part.Styles <- styles

let private addStandardNumbering (main: MainDocumentPart) =
    let part = main.AddNewPart<NumberingDefinitionsPart>()
    let numbering = Wordprocessing.Numbering()
    let abstractNum = Wordprocessing.AbstractNum(AbstractNumberId = Int32Value 0)
    let level = Wordprocessing.Level(LevelIndex = Int32Value 0)

    level.AppendChild(Wordprocessing.StartNumberingValue(Val = Int32Value 1))
    |> ignore

    level.AppendChild(Wordprocessing.NumberingFormat(Val = EnumValue Wordprocessing.NumberFormatValues.Decimal))
    |> ignore

    level.AppendChild(Wordprocessing.LevelText(Val = StringValue "%1.")) |> ignore

    level.AppendChild(Wordprocessing.LevelJustification(Val = EnumValue Wordprocessing.LevelJustificationValues.Left))
    |> ignore

    abstractNum.AppendChild level |> ignore
    numbering.AppendChild abstractNum |> ignore

    let instance = Wordprocessing.NumberingInstance(NumberID = Int32Value 1)

    instance.AppendChild(Wordprocessing.AbstractNumId(Val = Int32Value 0)) |> ignore

    numbering.AppendChild instance |> ignore
    part.Numbering <- numbering

let private addComment (main: MainDocumentPart) (id: int) (author: string) (initials: string) (body: string) =
    let part = main.AddNewPart<WordprocessingCommentsPart>()
    let comments = Wordprocessing.Comments()

    let comment =
        Wordprocessing.Comment(
            Id = StringValue(string id),
            Author = StringValue author,
            Initials = StringValue initials,
            Date = DateTimeValue(DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc))
        )

    let bodyRun = Wordprocessing.Run()
    bodyRun.AppendChild(text body) |> ignore
    let bodyParagraph = Wordprocessing.Paragraph()
    bodyParagraph.AppendChild bodyRun |> ignore
    comment.AppendChild bodyParagraph |> ignore

    comments.AppendChild comment |> ignore
    part.Comments <- comments

/// The fixture timestamp pre-existing revisions carry.
let revisionDate = DateTime(2026, 6, 2, 14, 30, 0, DateTimeKind.Utc)

/// Everything the model captures: headings on two levels, mixed-
/// formatting runs, list items, a table, a comment anchor, a
/// pre-existing tracked insertion, and explicit section properties.
let buildRichDocx () : byte[] =
    buildDocx (fun _ main ->
        addStandardStyles main
        addStandardNumbering main
        addComment main 1 "Alice" "A" "Looks good"

        let body = main.Document.Body

        body.AppendChild(styledParagraph (Some "Heading1") [ plainRun "Introduction" ])
        |> ignore

        body.AppendChild(styledParagraph None [ plainRun "Alpha "; boldRun "bold"; plainRun " beta" ])
        |> ignore

        body.AppendChild(styledParagraph (Some "Heading2") [ plainRun "Background" ])
        |> ignore

        body.AppendChild(listItem 1 0 "First point") |> ignore
        body.AppendChild(listItem 1 0 "Second point") |> ignore

        body.AppendChild(table [ [ "Name"; "Value" ]; [ "temp"; "42" ] ]) |> ignore

        // Comment anchored on a paragraph.
        let commented = Wordprocessing.Paragraph()

        commented.AppendChild(Wordprocessing.CommentRangeStart(Id = StringValue "1"))
        |> ignore

        commented.AppendChild(plainRun "Commented text") |> ignore

        commented.AppendChild(Wordprocessing.CommentRangeEnd(Id = StringValue "1"))
        |> ignore

        let referenceRun = Wordprocessing.Run()

        referenceRun.AppendChild(Wordprocessing.CommentReference(Id = StringValue "1"))
        |> ignore

        commented.AppendChild referenceRun |> ignore

        body.AppendChild commented |> ignore

        // Pre-existing tracked insertion.
        let revised = Wordprocessing.Paragraph()
        revised.AppendChild(plainRun "Original ") |> ignore

        let inserted =
            Wordprocessing.InsertedRun(
                Id = StringValue "99",
                Author = StringValue "Bob",
                Date = DateTimeValue revisionDate
            )

        inserted.AppendChild(plainRun "added text") |> ignore
        revised.AppendChild inserted |> ignore
        body.AppendChild revised |> ignore

        let sectPr = Wordprocessing.SectionProperties()

        sectPr.AppendChild(Wordprocessing.PageSize(Width = UInt32Value 11906u, Height = UInt32Value 16838u))
        |> ignore

        body.AppendChild sectPr |> ignore)

/// Elements outside the model's vocabulary: a content control
/// (carried opaquely), a hyperlink wrapper, a bookmark pair, and an
/// inline drawing (all reported as dropped).
let buildExoticDocx () : byte[] =
    buildDocx (fun _ main ->
        let body = main.Document.Body

        body.AppendChild(paragraph "Before") |> ignore

        let sdt = Wordprocessing.SdtBlock()
        let sdtContent = Wordprocessing.SdtContentBlock()
        sdtContent.AppendChild(paragraph "inside control") |> ignore
        sdt.AppendChild sdtContent |> ignore
        body.AppendChild sdt |> ignore

        let mixed = Wordprocessing.Paragraph()
        let link = Wordprocessing.Hyperlink(Anchor = StringValue "target")
        link.AppendChild(plainRun "link text") |> ignore
        mixed.AppendChild link |> ignore

        mixed.AppendChild(Wordprocessing.BookmarkStart(Id = StringValue "0", Name = StringValue "mark"))
        |> ignore

        mixed.AppendChild(Wordprocessing.BookmarkEnd(Id = StringValue "0")) |> ignore

        let drawingRun = Wordprocessing.Run()
        drawingRun.AppendChild(Wordprocessing.Drawing()) |> ignore
        mixed.AppendChild drawingRun |> ignore

        body.AppendChild mixed |> ignore)

/// Minimal flat-vs-structured comparison corpus: heading paths two
/// levels deep plus a table the flat extractor never reaches.
let buildKnowledgeBaseDocx () : byte[] =
    buildDocx (fun _ main ->
        addStandardStyles main
        let body = main.Document.Body

        body.AppendChild(styledParagraph (Some "Heading1") [ plainRun "Intro" ])
        |> ignore

        body.AppendChild(paragraph "Alpha beta gamma") |> ignore

        body.AppendChild(styledParagraph (Some "Heading2") [ plainRun "Sub" ]) |> ignore

        body.AppendChild(paragraph "One") |> ignore
        body.AppendChild(paragraph "Two") |> ignore

        body.AppendChild(table [ [ "Col"; "Val" ]; [ "a"; "1" ]; [ "b"; "2" ] ])
        |> ignore)

/// Open emitted bytes and validate against the OOXML schema.
let validate (bytes: byte[]) : DocumentFormat.OpenXml.Validation.ValidationErrorInfo list =
    use stream = new MemoryStream(bytes)
    use doc = WordprocessingDocument.Open(stream, false)
    let validator = DocumentFormat.OpenXml.Validation.OpenXmlValidator()
    validator.Validate doc |> List.ofSeq