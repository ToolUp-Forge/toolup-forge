// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The round-trip fidelity corpus: six `.docx` fixtures, each
/// exercising one region of the import/emit contract that drifts
/// silently as the layer evolves — styled runs, numbering and lists,
/// section breaks, tables, content outside the model's vocabulary, and
/// tracked changes.
///
/// **The fixtures are BUILT, not committed as binaries.** A `.docx` is
/// an OPC zip container whose entry timestamps and compression are not
/// byte-stable, so a committed package could be neither diffed in
/// review nor re-verified; and a fixture nobody can read is a fixture
/// nobody can correct. Building them from this file makes the corpus
/// minimal, deterministic, reviewable in an ordinary diff, and clean of
/// any provenance question — nothing here is copied from a real
/// document. Author attribution uses neutral placeholders throughout.
///
/// Every fixture is deliberately tiny (well under a page) and every
/// value in it is a literal: no clock, no `Guid`, no environment. Two
/// runs on two machines must produce identical bytes at the model
/// altitude, because the committed goldens beside this file pin exactly
/// that.
module ToolUp.OpenXml.Tests.Corpus.CorpusFixtures

open System
open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

// ─── Package scaffolding ─────────────────────────────────────────

let private buildDocx (build: MainDocumentPart -> unit) : byte[] =
    use stream = new MemoryStream()
    let doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document)
    let main = doc.AddMainDocumentPart()
    // Explicit append — the `Document(child)` ctor overload set
    // resolves a single composite argument to IEnumerable and copies
    // the child's children instead of attaching the child.
    let document = Document()
    document.AppendChild(Body()) |> ignore
    main.Document <- document
    build main
    main.Document.Save()
    (doc :> IDisposable).Dispose()
    stream.ToArray()

let private text (s: string) =
    Text(Text = s, Space = EnumValue SpaceProcessingModeValues.Preserve)

// ─── Run / paragraph helpers ─────────────────────────────────────

/// A run whose `w:rPr` is assembled in schema order: `w:rStyle`,
/// `w:b`, `w:i`, `w:strike`, `w:u`. Out-of-order children make the
/// package fail OOXML validation, which the corpus asserts.
let private run
    (charStyle: string option)
    (bold: bool)
    (italic: bool)
    (strike: bool)
    (underline: bool)
    (content: string)
    =
    let r = Run()
    let props = RunProperties()

    charStyle
    |> Option.iter (fun sid -> props.AppendChild(RunStyle(Val = StringValue sid)) |> ignore)

    if bold then
        props.AppendChild(Bold()) |> ignore

    if italic then
        props.AppendChild(Italic()) |> ignore

    if strike then
        props.AppendChild(Strike()) |> ignore

    if underline then
        props.AppendChild(Underline(Val = EnumValue UnderlineValues.Single)) |> ignore

    if props.HasChildren then
        r.AppendChild props |> ignore

    r.AppendChild(text content) |> ignore
    r

let private plain (content: string) =
    run None false false false false content

/// A run carrying a tab and a hard break either side of its text —
/// the two characters the model normalises into the run text and
/// emission lowers back to `w:tab` / `w:br`.
let private runWithBreaks (before: string) (after: string) =
    let r = Run()
    r.AppendChild(text before) |> ignore
    r.AppendChild(TabChar()) |> ignore
    r.AppendChild(Break()) |> ignore
    r.AppendChild(text after) |> ignore
    r

let private paragraphOf (styleId: string option) (runs: OpenXmlElement list) =
    let p = Paragraph()

    styleId
    |> Option.iter (fun sid ->
        let props = ParagraphProperties()
        props.AppendChild(ParagraphStyleId(Val = StringValue sid)) |> ignore
        p.AppendChild props |> ignore)

    for r in runs do
        p.AppendChild r |> ignore

    p

let private para (content: string) = paragraphOf None [ plain content ]

// ─── Shared parts ────────────────────────────────────────────────

let private style (styleId: string) (name: string) (isDefault: bool) (basedOn: string option) =
    let s = Style(Type = EnumValue StyleValues.Paragraph, StyleId = StringValue styleId)

    if isDefault then
        s.Default <- OnOffValue true

    s.AppendChild(StyleName(Val = StringValue name)) |> ignore

    basedOn
    |> Option.iter (fun b -> s.AppendChild(BasedOn(Val = StringValue b)) |> ignore)

    s

let private characterStyle (styleId: string) (name: string) =
    let s = Style(Type = EnumValue StyleValues.Character, StyleId = StringValue styleId)

    s.AppendChild(StyleName(Val = StringValue name)) |> ignore
    let runProps = StyleRunProperties()
    runProps.AppendChild(Italic()) |> ignore
    s.AppendChild runProps |> ignore
    s

/// `Normal` + `Heading1`/`Heading2` + a character style, so the
/// styled-run fixture has a real `w:rStyle` target and the heading
/// classifier has real style definitions to resolve against.
let private addStyles (main: MainDocumentPart) =
    let part = main.AddNewPart<StyleDefinitionsPart>()
    let styles = Styles()
    styles.AppendChild(style "Normal" "Normal" true None) |> ignore
    styles.AppendChild(style "Heading1" "heading 1" false (Some "Normal")) |> ignore
    styles.AppendChild(style "Heading2" "heading 2" false (Some "Normal")) |> ignore
    styles.AppendChild(characterStyle "Emphasis" "Emphasis") |> ignore
    part.Styles <- styles

/// One abstract numbering definition, four levels deep, in the given
/// format — decimal for the ordered list, bullet for the unordered.
let private abstractNumbering (abstractId: int) (format: NumberFormatValues) (levelText: string) =
    let abstractNum = AbstractNum(AbstractNumberId = Int32Value abstractId)

    for levelIndex in 0..2 do
        let level = Level(LevelIndex = Int32Value levelIndex)
        level.AppendChild(StartNumberingValue(Val = Int32Value 1)) |> ignore
        level.AppendChild(NumberingFormat(Val = EnumValue format)) |> ignore

        let rendered =
            if format = NumberFormatValues.Bullet then
                levelText
            else
                sprintf "%%%d." (levelIndex + 1)

        level.AppendChild(LevelText(Val = StringValue rendered)) |> ignore

        level.AppendChild(LevelJustification(Val = EnumValue LevelJustificationValues.Left))
        |> ignore

        abstractNum.AppendChild level |> ignore

    abstractNum

/// Two numbering instances — an ordered list (`w:numId` 1) and a
/// bulleted one (`w:numId` 2) — so the corpus pins that a list item's
/// numbering reference survives with the RIGHT instance, not merely
/// with some instance.
let private addNumbering (main: MainDocumentPart) =
    let part = main.AddNewPart<NumberingDefinitionsPart>()
    let numbering = Numbering()

    // Schema order: every w:abstractNum precedes the first w:num.
    numbering.AppendChild(abstractNumbering 0 NumberFormatValues.Decimal "%1.")
    |> ignore

    numbering.AppendChild(abstractNumbering 1 NumberFormatValues.Bullet "•")
    |> ignore

    for numId, abstractId in [ 1, 0; 2, 1 ] do
        let instance = NumberingInstance(NumberID = Int32Value numId)
        instance.AppendChild(AbstractNumId(Val = Int32Value abstractId)) |> ignore
        numbering.AppendChild instance |> ignore

    part.Numbering <- numbering

let private listItem (numId: int) (level: int) (content: string) =
    let numberingProps = NumberingProperties()

    numberingProps.AppendChild(NumberingLevelReference(Val = Int32Value level))
    |> ignore

    numberingProps.AppendChild(NumberingId(Val = Int32Value numId)) |> ignore
    let props = ParagraphProperties()
    props.AppendChild numberingProps |> ignore
    let p = Paragraph()
    p.AppendChild props |> ignore
    p.AppendChild(plain content) |> ignore
    p

/// `w:sectPr` with an explicit page size + margins. `landscape`
/// swaps the page dimensions and stamps the orientation, so two
/// sections in one fixture are distinguishable in the golden.
let private sectionProperties (landscape: bool) =
    let sectPr = SectionProperties()

    let pageSize =
        if landscape then
            PageSize(
                Width = UInt32Value 16838u,
                Height = UInt32Value 11906u,
                Orient = EnumValue PageOrientationValues.Landscape
            )
        else
            PageSize(Width = UInt32Value 11906u, Height = UInt32Value 16838u)

    sectPr.AppendChild pageSize |> ignore

    sectPr.AppendChild(
        PageMargin(Top = Int32Value 1440, Bottom = Int32Value 1440, Left = UInt32Value 1440u, Right = UInt32Value 1440u)
    )
    |> ignore

    sectPr

// ─── Fixture 1 — styled runs ─────────────────────────────────────

/// Every run-formatting axis the model decomposes (bold, italic,
/// underline, strikethrough, character style) plus the two characters
/// it normalises (tab, break) and a paragraph whose runs mix several
/// at once. The fidelity question this pins: does a run's `w:rPr`
/// survive import → emit → import unchanged, including the parts the
/// model's booleans do not name?
let styledRuns () : byte[] =
    buildDocx (fun main ->
        addStyles main
        let body = main.Document.Body

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Styled runs" ])
        |> ignore

        body.AppendChild(
            paragraphOf None [
                plain "plain "
                run None true false false false "bold"
                plain " "
                run None false true false false "italic"
                plain " "
                run None false false false true "underlined"
                plain " "
                run None false false true false "struck"
            ]
        )
        |> ignore

        body.AppendChild(paragraphOf None [ plain "combined "; run None true true true true "all four" ])
        |> ignore

        body.AppendChild(paragraphOf (Some "Heading2") [ plain "Character styles" ])
        |> ignore

        body.AppendChild(
            paragraphOf None [
                plain "a "
                run (Some "Emphasis") false false false false "character-styled"
                plain " span"
            ]
        )
        |> ignore

        body.AppendChild(paragraphOf None [ runWithBreaks "before" "after" ]) |> ignore

        body.AppendChild(sectionProperties false) |> ignore)

// ─── Fixture 2 — numbering / lists ───────────────────────────────

/// Two numbering instances at three indent levels, interleaved with
/// ordinary paragraphs. Pins that `w:numPr` survives with the right
/// `w:numId` AND `w:ilvl`, that the numbering part round-trips
/// verbatim, and that a numbered paragraph carrying a heading style
/// still classifies as a heading (the `Block.classify` precedence).
let numberingLists () : byte[] =
    buildDocx (fun main ->
        addStyles main
        addNumbering main
        let body = main.Document.Body

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Numbering and lists" ])
        |> ignore

        body.AppendChild(para "An ordered list at three levels:") |> ignore
        body.AppendChild(listItem 1 0 "First") |> ignore
        body.AppendChild(listItem 1 1 "First, nested") |> ignore
        body.AppendChild(listItem 1 2 "First, nested twice") |> ignore
        body.AppendChild(listItem 1 0 "Second") |> ignore

        body.AppendChild(para "A bulleted list on a second instance:") |> ignore
        body.AppendChild(listItem 2 0 "Alpha") |> ignore
        body.AppendChild(listItem 2 1 "Beta") |> ignore

        // A numbered HEADING: numbering reference plus a heading
        // style. Heading classification wins; the numbering reference
        // rides along in the verbatim w:pPr.
        let numberedHeading = listItem 1 0 "Numbered heading"

        numberedHeading.ParagraphProperties.PrependChild(ParagraphStyleId(Val = StringValue "Heading2"))
        |> ignore

        body.AppendChild numberedHeading |> ignore

        body.AppendChild(sectionProperties false) |> ignore)

// ─── Fixture 3 — section breaks ──────────────────────────────────

/// Three sections: two closed by an intermediate `w:sectPr` riding
/// the section's last paragraph (Word's own shape), the third closed
/// by the body-level `w:sectPr`. The middle section is landscape, so
/// a section's properties being attached to the WRONG section is
/// visible in the golden rather than silently plausible.
let sectionBreaks () : byte[] =
    buildDocx (fun main ->
        addStyles main
        let body = main.Document.Body

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Portrait section" ])
        |> ignore

        body.AppendChild(para "Content of the first section.") |> ignore

        let firstBreak = para "Last paragraph before the first break."
        let firstProps = ParagraphProperties()
        firstProps.AppendChild(sectionProperties false) |> ignore
        firstBreak.PrependChild firstProps |> ignore
        body.AppendChild firstBreak |> ignore

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Landscape section" ])
        |> ignore

        let secondBreak = para "Last paragraph before the second break."
        let secondProps = ParagraphProperties()
        secondProps.AppendChild(sectionProperties true) |> ignore
        secondBreak.PrependChild secondProps |> ignore
        body.AppendChild secondBreak |> ignore

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Final section" ])
        |> ignore

        body.AppendChild(para "Content of the final section.") |> ignore
        body.AppendChild(sectionProperties false) |> ignore)

// ─── Fixture 4 — tables ──────────────────────────────────────────

let private cell (spanned: int option) (blocks: OpenXmlElement list) =
    let tc = TableCell()

    let props = TableCellProperties()

    props.AppendChild(TableCellWidth(Width = StringValue "2400", Type = EnumValue TableWidthUnitValues.Dxa))
    |> ignore

    spanned
    |> Option.iter (fun span -> props.AppendChild(GridSpan(Val = Int32Value span)) |> ignore)

    tc.AppendChild props |> ignore

    for block in blocks do
        tc.AppendChild block |> ignore

    tc

let private tableRow (header: bool) (cells: TableCell list) =
    let tr = TableRow()

    if header then
        let props = TableRowProperties()
        props.AppendChild(TableHeader()) |> ignore
        tr.PrependChild props |> ignore

    for c in cells do
        tr.AppendChild c |> ignore

    tr

let private tableProperties () =
    let props = TableProperties()

    props.AppendChild(TableWidth(Width = StringValue "5000", Type = EnumValue TableWidthUnitValues.Pct))
    |> ignore

    let borders = TableBorders()
    let single () = EnumValue BorderValues.Single

    // Schema sequence: top, left, bottom, right, insideH, insideV.
    borders.AppendChild(TopBorder(Val = single (), Size = UInt32Value 4u)) |> ignore

    borders.AppendChild(LeftBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(BottomBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(RightBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(InsideHorizontalBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    borders.AppendChild(InsideVerticalBorder(Val = single (), Size = UInt32Value 4u))
    |> ignore

    props.AppendChild borders |> ignore
    props

let private grid (columns: int) =
    let g = TableGrid()

    for _ in 1..columns do
        g.AppendChild(GridColumn(Width = StringValue "2400")) |> ignore

    g

/// A table carrying every property bag the model preserves verbatim
/// (`w:tblPr`, `w:tblGrid`, `w:trPr`, `w:tcPr`), a spanned cell, a
/// styled paragraph inside a cell, and a NESTED table — the recursion
/// `importTable` and `emitTable` reach through each other for.
let tables () : byte[] =
    buildDocx (fun main ->
        addStyles main
        let body = main.Document.Body

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Tables" ]) |> ignore

        let nested = Table()
        nested.AppendChild(tableProperties ()) |> ignore
        nested.AppendChild(grid 2) |> ignore

        nested.AppendChild(tableRow false [ cell None [ para "n1" ]; cell None [ para "n2" ] ])
        |> ignore

        let outer = Table()
        outer.AppendChild(tableProperties ()) |> ignore
        outer.AppendChild(grid 3) |> ignore

        outer.AppendChild(
            tableRow true [
                cell None [ paragraphOf (Some "Heading2") [ plain "Name" ] ]
                cell None [ para "Value" ]
                cell None [ para "Notes" ]
            ]
        )
        |> ignore

        outer.AppendChild(
            tableRow false [
                cell None [ para "temperature" ]
                cell None [ paragraphOf None [ plain "42 "; run None true false false false "C" ] ]
                cell None [ para "measured" ]
            ]
        )
        |> ignore

        // A spanned cell followed by a cell holding a nested table —
        // the row is three grid columns wide either way.
        outer.AppendChild(
            tableRow false [
                cell (Some 2) [ para "spans two columns" ]
                // WordprocessingML requires a cell to end with a
                // paragraph, so the nested table is followed by one.
                cell None [ nested; Paragraph() ]
            ]
        )
        |> ignore

        body.AppendChild outer |> ignore
        body.AppendChild(para "A paragraph after the table.") |> ignore
        body.AppendChild(sectionProperties false) |> ignore)

// ─── Fixture 5 — tracked changes ─────────────────────────────────

/// The fixture timestamps. Two distinct authors at two distinct
/// instants, so an emission that loses attribution — or collapses two
/// authors into one — cannot pass by coincidence.
let firstRevisionDate = DateTime(2026, 6, 2, 14, 30, 0, DateTimeKind.Utc)
let secondRevisionDate = DateTime(2026, 6, 3, 9, 15, 0, DateTimeKind.Utc)

let private insertedRun (author: string) (date: DateTime) (revisionId: string) (content: string) =
    let ins =
        InsertedRun(Id = StringValue revisionId, Author = StringValue author, Date = DateTimeValue date)

    ins.AppendChild(plain content) |> ignore
    ins

let private deletedRun (author: string) (date: DateTime) (revisionId: string) (content: string) =
    let del =
        DeletedRun(Id = StringValue revisionId, Author = StringValue author, Date = DateTimeValue date)

    let r = Run()

    r.AppendChild(DeletedText(Text = content, Space = EnumValue SpaceProcessingModeValues.Preserve))
    |> ignore

    del.AppendChild r |> ignore
    del

/// Pre-existing `w:ins` / `w:del` from two reviewers, on runs and on
/// paragraph marks. The corpus asserts these survive import → emit →
/// import with author AND date intact, and that emission renumbers
/// them to document-unique ids (the source ids here deliberately
/// COLLIDE across paragraphs — `101` appears twice — so an emission
/// that merely copied them through would fail the uniqueness leg).
let trackedChanges () : byte[] =
    buildDocx (fun main ->
        addStyles main
        let body = main.Document.Body

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Tracked changes" ])
        |> ignore

        let insertion = Paragraph()
        insertion.AppendChild(plain "The original text ") |> ignore

        insertion.AppendChild(insertedRun "Reviewer A" firstRevisionDate "101" "with an insertion")
        |> ignore

        body.AppendChild insertion |> ignore

        let deletion = Paragraph()
        deletion.AppendChild(plain "Kept ") |> ignore

        deletion.AppendChild(deletedRun "Reviewer B" secondRevisionDate "101" "removed")
        |> ignore

        deletion.AppendChild(plain " kept") |> ignore
        body.AppendChild deletion |> ignore

        // A whole paragraph inserted as a tracked change: the runs
        // carry w:ins and so does the paragraph MARK.
        let insertedParagraph = Paragraph()
        let insertedProps = ParagraphProperties()
        let markProps = ParagraphMarkRunProperties()

        markProps.AppendChild(
            Inserted(Id = StringValue "102", Author = StringValue "Reviewer A", Date = DateTimeValue firstRevisionDate)
        )
        |> ignore

        insertedProps.AppendChild markProps |> ignore
        insertedParagraph.AppendChild insertedProps |> ignore

        insertedParagraph.AppendChild(insertedRun "Reviewer A" firstRevisionDate "103" "A wholly new paragraph.")
        |> ignore

        body.AppendChild insertedParagraph |> ignore

        // A whole paragraph deleted as a tracked change.
        let deletedParagraph = Paragraph()
        let deletedProps = ParagraphProperties()
        let deletedMarkProps = ParagraphMarkRunProperties()

        deletedMarkProps.AppendChild(
            Deleted(Id = StringValue "104", Author = StringValue "Reviewer B", Date = DateTimeValue secondRevisionDate)
        )
        |> ignore

        deletedProps.AppendChild deletedMarkProps |> ignore
        deletedParagraph.AppendChild deletedProps |> ignore

        deletedParagraph.AppendChild(deletedRun "Reviewer B" secondRevisionDate "105" "This paragraph is struck out.")
        |> ignore

        body.AppendChild deletedParagraph |> ignore

        body.AppendChild(sectionProperties false) |> ignore)

// ─── Fixture 6 — lossy content (the residue baseline) ────────────

/// Content OUTSIDE the model's vocabulary, at every position the
/// import distinguishes: a content control (carried opaquely), a
/// hyperlink wrapper (flattened to its runs, target lost), a bookmark
/// pair, and an inline drawing. The four other fixtures have an EMPTY
/// residue baseline, which is the strongest possible statement about
/// them — but an all-empty corpus would never exercise the baseline's
/// content, so a change that garbled a residue entry's location or
/// reason could not be caught. This fixture is where that is pinned.
///
/// The positions are load-bearing: `Location` is a 1-based model
/// address, so inserting a paragraph before the lossy content SHOULD
/// move every entry, and the baseline records where each one sits.
let mixedResidue () : byte[] =
    buildDocx (fun main ->
        addStyles main
        let body = main.Document.Body

        body.AppendChild(paragraphOf (Some "Heading1") [ plain "Lossy content" ])
        |> ignore

        body.AppendChild(para "An ordinary paragraph before the content control.")
        |> ignore

        let sdt = SdtBlock()
        let sdtContent = SdtContentBlock()
        sdtContent.AppendChild(para "inside a content control") |> ignore
        sdt.AppendChild sdtContent |> ignore
        body.AppendChild sdt |> ignore

        let mixed = Paragraph()
        let link = Hyperlink(Anchor = StringValue "somewhere")
        link.AppendChild(plain "link text") |> ignore
        mixed.AppendChild link |> ignore

        mixed.AppendChild(BookmarkStart(Id = StringValue "0", Name = StringValue "a-bookmark"))
        |> ignore

        mixed.AppendChild(BookmarkEnd(Id = StringValue "0")) |> ignore
        mixed.AppendChild(plain " and some kept text") |> ignore

        // Text and a drawing in ONE run: the text survives, the
        // drawing is reported dropped at that run's address. (Append
        // the `w:t` directly — `plain` builds a whole `w:r`, and a run
        // nested in a run is invalid OOXML the fixture-validity case
        // rejects.)
        //
        // The drawing is spelled out to a complete `wp:inline` rather
        // than left as a bare `w:drawing`: an empty one is schema-
        // INCOMPLETE, so a fixture carrying it would be pinning the
        // layer's behaviour against markup Word cannot produce.
        let drawingRun = Run()
        drawingRun.AppendChild(text "caption") |> ignore

        let inline' = Drawing.Wordprocessing.Inline()

        inline'.AppendChild(Drawing.Wordprocessing.Extent(Cx = Int64Value 914400L, Cy = Int64Value 914400L))
        |> ignore

        inline'.AppendChild(
            Drawing.Wordprocessing.DocProperties(Id = UInt32Value 1u, Name = StringValue "placeholder")
        )
        |> ignore

        let graphic = Drawing.Graphic()

        graphic.AppendChild(
            Drawing.GraphicData(Uri = StringValue "http://schemas.openxmlformats.org/drawingml/2006/picture")
        )
        |> ignore

        inline'.AppendChild graphic |> ignore
        let drawing = Drawing()
        drawing.AppendChild inline' |> ignore
        drawingRun.AppendChild drawing |> ignore
        mixed.AppendChild drawingRun |> ignore

        body.AppendChild mixed |> ignore
        body.AppendChild(sectionProperties false) |> ignore)

// ─── The corpus ──────────────────────────────────────────────────

/// One corpus entry: the fixture, the stable name its goldens are
/// filed under, and — where the round trip is known NOT to be clean —
/// the defect that makes it so.
type Fixture = {
    Name: string
    Build: unit -> byte[]
    /// `None` when import → emit → import is a fixpoint and the
    /// emitted package validates. `Some description` names a defect in
    /// the CURRENT `Import` / `Emit` behaviour that the corpus PINS
    /// rather than asserts away.
    ///
    /// A declared defect is not an exemption. The fixture keeps every
    /// golden, and the harness swaps its two clean-round-trip cases
    /// for cases asserting the defect is still EXACTLY present — so
    /// the corpus reddens if the defect worsens AND if it is fixed.
    /// Making a red green by deleting the case that saw it is the one
    /// move this corpus exists to prevent; declaring the loss in the
    /// data, where a reader meets it before the test output does, is
    /// the opposite move.
    KnownDefect: string option
}

/// The fidelity corpus. Adding a fixture here adds its golden files;
/// nothing else in the harness needs an edit.
let all: Fixture list = [
    {
        Name = "styled-runs"
        Build = styledRuns
        KnownDefect = None
    }
    {
        Name = "numbering-lists"
        Build = numberingLists
        KnownDefect = None
    }
    {
        Name = "section-breaks"
        Build = sectionBreaks
        KnownDefect = None
    }
    {
        Name = "tables"
        Build = tables
        KnownDefect = None
    }
    {
        Name = "mixed-residue"
        Build = mixedResidue
        KnownDefect = None
    }
    {
        // Carried a `KnownDefect` from Phase 206 until Phase 736: a
        // pre-existing paragraph-mark revision was emitted twice,
        // because import left it in the verbatim `w:pPr` as well as
        // capturing it on `MarkRevision`. Import now strips the
        // captured element, so this fixture is clean like the rest —
        // see docs/migrations/736-openxml-emit-paragraph-mark-revision-duplication-fix.md.
        Name = "tracked-changes"
        Build = trackedChanges
        KnownDefect = None
    }
]