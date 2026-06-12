// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Structure-preserving import: `.docx` → `DocModel` + `ResidueReport`.
///
/// Everything inside the model's vocabulary (headings, paragraphs of
/// styled runs, tables, list items, comments, tracked changes,
/// section properties) is captured as typed values; property bags
/// the model does not decompose (`w:pPr` / `w:rPr` / `w:sectPr` /
/// table properties / the styles and numbering parts) are carried as
/// verbatim OuterXml strings so emission re-attaches them unchanged.
/// Everything OUTSIDE the vocabulary lands in the residue report —
/// a first-class return value, never a log line: block-level
/// strangers are carried opaquely (round-trip safe), inline
/// strangers the run list cannot host are reported as dropped.
///
/// Deliberately not reported: spec noise with no document content —
/// `w:proofErr` spell-check markers, `w:lastRenderedPageBreak`
/// pagination hints, and `w:bookmarkEnd` (each bookmark is reported
/// once, at its `w:bookmarkStart`).
module ToolUp.OpenXml.Import

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging

// ─── Small value helpers over OpenXml simple types ───────────────

let private stringValue (v: StringValue) : string option =
    if isNull (box v) || isNull v.Value then
        None
    else
        Some v.Value

let private parseId (id: StringValue) : int option =
    stringValue id
    |> Option.bind (fun s ->
        match Int32.TryParse s with
        | true, v -> Some v
        | _ -> None)

let private dateValue (v: DateTimeValue) : DateTimeOffset option =
    if isNull (box v) || not v.HasValue then
        None
    else
        Some(DateTimeOffset(DateTime.SpecifyKind(v.Value, DateTimeKind.Utc)))

/// `w:b` / `w:i` / `w:strike` toggles: present with no `w:val`
/// means on; an explicit `w:val` decides.
let private onOff (element: Wordprocessing.OnOffType) : bool =
    not (isNull (box element))
    && (isNull (box element.Val) || not element.Val.HasValue || element.Val.Value)

let private underlineOn (u: Wordprocessing.Underline) : bool =
    not (isNull (box u))
    && (isNull (box u.Val)
        || not u.Val.HasValue
        || u.Val.Value <> Wordprocessing.UnderlineValues.None)

let private qualifiedName (el: OpenXmlElement) : string =
    if String.IsNullOrEmpty el.Prefix then
        el.LocalName
    else
        sprintf "%s:%s" el.Prefix el.LocalName

let private revisionInfo (author: StringValue) (date: DateTimeValue) : RevisionInfo = {
    Author = stringValue author |> Option.defaultValue ""
    Date = dateValue date
}

// ─── Run import ──────────────────────────────────────────────────

let private importFormatting (rPr: Wordprocessing.RunProperties) : RunFormatting =
    match rPr with
    | null -> RunFormatting.none
    | props -> {
        Bold = onOff props.Bold
        Italic = onOff props.Italic
        Underline = underlineOn props.Underline
        Strikethrough = onOff props.Strike
        StyleId =
            match props.RunStyle with
            | null -> None
            | style -> stringValue style.Val
        RawProperties = Some props.OuterXml
      }

/// Import one `w:r`. Returns `None` for runs that contribute no
/// text (their unmodelled content, if any, is already in the
/// residue). `noteCommentId` reports `w:commentReference` anchors
/// up to the owning paragraph.
let private importRun
    (residue: ResizeArray<ResidueEntry>)
    (location: string)
    (noteCommentId: int -> unit)
    (revision: RevisionMark option)
    (r: Wordprocessing.Run)
    : Run option =
    let text = StringBuilder()

    for child in r.ChildElements do
        match child with
        | :? Wordprocessing.RunProperties -> ()
        | :? Wordprocessing.Text as t -> text.Append t.Text |> ignore
        | :? Wordprocessing.DeletedText as t -> text.Append t.Text |> ignore
        | :? Wordprocessing.TabChar -> text.Append '\t' |> ignore
        | :? Wordprocessing.Break -> text.Append '\n' |> ignore
        | :? Wordprocessing.CarriageReturn -> text.Append '\n' |> ignore
        | :? Wordprocessing.CommentReference as cr -> parseId cr.Id |> Option.iter noteCommentId
        | :? Wordprocessing.LastRenderedPageBreak -> ()
        | other ->
            residue.Add {
                ElementKind = qualifiedName other
                Location = location
                Reason = "inline run content outside the model's vocabulary"
                Disposition = Dropped
            }

    let content = text.ToString()

    if content = "" then
        None
    else
        Some {
            Text = content
            Formatting = importFormatting r.RunProperties
            Revision = revision
        }

// ─── Paragraph import ────────────────────────────────────────────

/// The paragraph's captured `w:pPr` with any `w:sectPr` stripped
/// out — section properties are modelled on `Section`, so leaving
/// them in the paragraph payload would duplicate them on emission.
let private paragraphRawProperties (pPr: Wordprocessing.ParagraphProperties) : string option =
    match pPr with
    | null -> None
    | props ->
        let clone = props.CloneNode(true) :?> Wordprocessing.ParagraphProperties

        clone.Elements<Wordprocessing.SectionProperties>()
        |> Seq.toArray
        |> Array.iter _.Remove()

        if clone.HasChildren then Some clone.OuterXml else None

let private paragraphMarkRevision (pPr: Wordprocessing.ParagraphProperties) : RevisionMark option =
    match pPr with
    | null -> None
    | props ->
        match props.ParagraphMarkRunProperties with
        | null -> None
        | markProps ->
            let inserted = markProps.Elements<Wordprocessing.Inserted>() |> Seq.tryHead
            let deleted = markProps.Elements<Wordprocessing.Deleted>() |> Seq.tryHead

            match inserted, deleted with
            | Some ins, _ -> Some(Inserted(revisionInfo ins.Author ins.Date))
            | _, Some del -> Some(Deleted(revisionInfo del.Author del.Date))
            | None, None -> None

let private numberingReference (pPr: Wordprocessing.ParagraphProperties) : NumberingRef option =
    match pPr with
    | null -> None
    | props ->
        match props.NumberingProperties with
        | null -> None
        | numbering ->
            let numId =
                match numbering.NumberingId with
                | null -> None
                | nid ->
                    if isNull (box nid.Val) || not nid.Val.HasValue then
                        None
                    else
                        Some nid.Val.Value

            numId
            |> Option.map (fun id ->
                let level =
                    match numbering.NumberingLevelReference with
                    | null -> 0
                    | levelRef ->
                        if isNull (box levelRef.Val) || not levelRef.Val.HasValue then
                            0
                        else
                            levelRef.Val.Value

                { NumberingId = id; Level = level })

/// Import one `w:p`: the paragraph model, its numbering reference
/// (for list-item classification), and the `w:sectPr` if this
/// paragraph is a section break.
let private importParagraph
    (residue: ResizeArray<ResidueEntry>)
    (locationPrefix: string)
    (p: Wordprocessing.Paragraph)
    : ParagraphModel * NumberingRef option * Wordprocessing.SectionProperties option =
    let pPr = p.ParagraphProperties

    let styleId =
        match pPr with
        | null -> None
        | props ->
            match props.ParagraphStyleId with
            | null -> None
            | style -> stringValue style.Val

    let runs = ResizeArray<Run>()
    let commentIds = ResizeArray<int>()
    let mutable runIndex = 0

    let noteCommentId (id: int) =
        if not (commentIds.Contains id) then
            commentIds.Add id

    let addRun (revision: RevisionMark option) (r: Wordprocessing.Run) =
        runIndex <- runIndex + 1
        let location = sprintf "%s, run %d" locationPrefix runIndex

        importRun residue location noteCommentId revision r |> Option.iter runs.Add

    for child in p.ChildElements do
        match child with
        | :? Wordprocessing.ParagraphProperties -> ()
        | :? Wordprocessing.Run as r -> addRun None r
        | :? Wordprocessing.InsertedRun as ins ->
            let mark = Inserted(revisionInfo ins.Author ins.Date)

            for inner in ins.Elements<Wordprocessing.Run>() do
                addRun (Some mark) inner
        | :? Wordprocessing.DeletedRun as del ->
            let mark = Deleted(revisionInfo del.Author del.Date)

            for inner in del.Elements<Wordprocessing.Run>() do
                addRun (Some mark) inner
        | :? Wordprocessing.Hyperlink as link ->
            residue.Add {
                ElementKind = "w:hyperlink"
                Location = locationPrefix
                Reason = "hyperlink wrapper flattened to its text runs; the link target is not modelled"
                Disposition = Dropped
            }

            for inner in link.Elements<Wordprocessing.Run>() do
                addRun None inner
        | :? Wordprocessing.CommentRangeStart as start -> parseId start.Id |> Option.iter noteCommentId
        | :? Wordprocessing.CommentRangeEnd -> ()
        | :? Wordprocessing.BookmarkStart as bookmark ->
            let name = stringValue bookmark.Name |> Option.defaultValue "?"

            residue.Add {
                ElementKind = "w:bookmarkStart"
                Location = locationPrefix
                Reason = sprintf "bookmark \"%s\" is not modelled" name
                Disposition = Dropped
            }
        | :? Wordprocessing.BookmarkEnd -> ()
        | :? Wordprocessing.ProofError -> ()
        | other ->
            residue.Add {
                ElementKind = qualifiedName other
                Location = locationPrefix
                Reason = "inline element outside the model's vocabulary at a position the model cannot carry opaquely"
                Disposition = Dropped
            }

    let paragraph = {
        Runs = List.ofSeq runs
        StyleId = styleId
        RawProperties = paragraphRawProperties pPr
        MarkRevision = paragraphMarkRevision pPr
        CommentIds = List.ofSeq commentIds
    }

    let sectPr =
        match pPr with
        | null -> None
        | props -> Option.ofObj props.SectionProperties

    paragraph, numberingReference pPr, sectPr

// ─── Block / table import (mutually recursive via cells) ─────────

let rec private importTable
    (residue: ResizeArray<ResidueEntry>)
    (locationPrefix: string)
    (t: Wordprocessing.Table)
    : TableModel =
    let rows = ResizeArray<TableRow>()
    let mutable rawProperties = None
    let mutable rawGrid = None

    for child in t.ChildElements do
        match child with
        | :? Wordprocessing.TableProperties as props -> rawProperties <- Some props.OuterXml
        | :? Wordprocessing.TableGrid as grid -> rawGrid <- Some grid.OuterXml
        | :? Wordprocessing.TableRow as tr ->
            let rowLocation = sprintf "%s, row %d" locationPrefix (rows.Count + 1)
            let cells = ResizeArray<TableCell>()
            let mutable rowProps = None

            for rowChild in tr.ChildElements do
                match rowChild with
                | :? Wordprocessing.TableRowProperties as props -> rowProps <- Some props.OuterXml
                | :? Wordprocessing.TableCell as tc ->
                    let cellLocation = sprintf "%s, cell %d" rowLocation (cells.Count + 1)
                    let blocks = ResizeArray<Block>()
                    let mutable cellProps = None

                    for cellChild in tc.ChildElements do
                        match cellChild with
                        | :? Wordprocessing.TableCellProperties as props -> cellProps <- Some props.OuterXml
                        | other ->
                            let blockLocation = sprintf "%s, block %d" cellLocation (blocks.Count + 1)

                            importBlockElement residue blockLocation other
                            |> Option.iter (fun (block, _) -> blocks.Add block)

                    cells.Add {
                        Blocks = List.ofSeq blocks
                        RawProperties = cellProps
                    }
                | other ->
                    residue.Add {
                        ElementKind = qualifiedName other
                        Location = rowLocation
                        Reason = "table-row content outside the model's vocabulary"
                        Disposition = Dropped
                    }

            rows.Add {
                Cells = List.ofSeq cells
                RawProperties = rowProps
            }
        | other ->
            residue.Add {
                ElementKind = qualifiedName other
                Location = locationPrefix
                Reason = "table content outside the model's vocabulary"
                Disposition = Dropped
            }

    {
        Rows = List.ofSeq rows
        RawProperties = rawProperties
        RawGrid = rawGrid
    }

/// Classify one body-level (or cell-level) element. Returns the
/// block plus the `w:sectPr` when the element was a section-break
/// paragraph; `None` for elements that produce no block (the
/// body-level `w:sectPr`, handled by the caller).
and private importBlockElement
    (residue: ResizeArray<ResidueEntry>)
    (location: string)
    (el: OpenXmlElement)
    : (Block * Wordprocessing.SectionProperties option) option =
    match el with
    | :? Wordprocessing.Paragraph as p ->
        let paragraph, numbering, sectPr = importParagraph residue location p
        Some(Block.classify numbering paragraph, sectPr)
    | :? Wordprocessing.Table as t -> Some(Table(importTable residue location t), None)
    | :? Wordprocessing.SectionProperties -> None
    | other ->
        residue.Add {
            ElementKind = qualifiedName other
            Location = location
            Reason = "block-level element outside the model's vocabulary; carried verbatim"
            Disposition = CarriedOpaque
        }

        Some(OpaqueBlock other.OuterXml, None)

// ─── Part imports ────────────────────────────────────────────────

let private importComments (main: MainDocumentPart) : Comment list =
    match main.WordprocessingCommentsPart with
    | null -> []
    | part ->
        match part.Comments with
        | null -> []
        | comments ->
            comments.Elements<Wordprocessing.Comment>()
            |> Seq.choose (fun c ->
                parseId c.Id
                |> Option.map (fun id -> {
                    Id = id
                    Author = stringValue c.Author |> Option.defaultValue ""
                    Initials = stringValue c.Initials
                    Date = dateValue c.Date
                    Text =
                        c.Elements<Wordprocessing.Paragraph>()
                        |> Seq.map _.InnerText
                        |> String.concat "\n"
                }))
            |> List.ofSeq

let private importStyles (main: MainDocumentPart) : StyleDefinitions =
    match main.StyleDefinitionsPart with
    | null -> StyleDefinitions.empty
    | part ->
        match part.Styles with
        | null -> StyleDefinitions.empty
        | styles ->
            let infos =
                styles.Elements<Wordprocessing.Style>()
                |> Seq.choose (fun s ->
                    stringValue s.StyleId
                    |> Option.map (fun styleId -> {
                        StyleId = styleId
                        Name =
                            match s.StyleName with
                            | null -> None
                            | name -> stringValue name.Val
                        Type =
                            if isNull (box s.Type) || not s.Type.HasValue then
                                None
                            else
                                Some s.Type.InnerText
                        BasedOn =
                            match s.BasedOn with
                            | null -> None
                            | basedOn -> stringValue basedOn.Val
                    }))
                |> List.ofSeq

            {
                Styles = infos
                RawXml = Some styles.OuterXml
            }

let private importNumbering (main: MainDocumentPart) : NumberingDefinitions =
    match main.NumberingDefinitionsPart with
    | null -> NumberingDefinitions.empty
    | part ->
        match part.Numbering with
        | null -> NumberingDefinitions.empty
        | numbering ->
            let instances =
                numbering.Elements<Wordprocessing.NumberingInstance>()
                |> Seq.choose (fun n ->
                    if isNull (box n.NumberID) || not n.NumberID.HasValue then
                        None
                    else
                        Some {
                            NumberingId = n.NumberID.Value
                            AbstractNumberingId =
                                match n.AbstractNumId with
                                | null -> None
                                | abstractId ->
                                    if isNull (box abstractId.Val) || not abstractId.Val.HasValue then
                                        None
                                    else
                                        Some abstractId.Val.Value
                        })
                |> List.ofSeq

            {
                Instances = instances
                RawXml = Some numbering.OuterXml
            }

// ─── Entry points ────────────────────────────────────────────────

/// Import a `.docx` from a stream. The stream must be readable and
/// positioned at the package start; it is not disposed.
let fromStream (stream: Stream) : ImportedDocument =
    use doc = Package.openRead stream
    let main = Package.mainPart doc

    let body =
        match main.Document with
        | null -> failwith "Main document part has no w:document content."
        | document ->
            match document.Body with
            | null -> failwith "w:document has no w:body."
            | body -> body

    let residue = ResizeArray<ResidueEntry>()
    let sections = ResizeArray<Section>()
    let blocks = ResizeArray<Block>()

    let closeSection (rawSectPr: string option) =
        sections.Add {
            Blocks = List.ofSeq blocks
            RawProperties = rawSectPr
        }

        blocks.Clear()

    for child in body.ChildElements do
        match child with
        | :? Wordprocessing.SectionProperties as sectPr ->
            // The final section's properties sit directly under
            // w:body; intermediate section breaks ride a paragraph.
            closeSection (Some sectPr.OuterXml)
        | el ->
            let location =
                sprintf "section %d, block %d" (sections.Count + 1) (blocks.Count + 1)

            match importBlockElement residue location el with
            | Some(block, Some sectPr) ->
                blocks.Add block
                closeSection (Some sectPr.OuterXml)
            | Some(block, None) -> blocks.Add block
            | None -> ()

    if blocks.Count > 0 || sections.Count = 0 then
        closeSection None

    {
        Model = {
            Sections = List.ofSeq sections
            Styles = importStyles main
            Numbering = importNumbering main
            Comments = importComments main
        }
        Residue = { Entries = List.ofSeq residue }
    }

/// Import a `.docx` from raw bytes.
let fromBytes (bytes: byte[]) : ImportedDocument =
    use stream = new MemoryStream(bytes)
    fromStream stream