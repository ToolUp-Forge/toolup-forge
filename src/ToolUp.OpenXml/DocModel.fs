// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.OpenXml

open System

// ─── Phase 124 — structural document model ──────────────────────
//
// Immutable value records describing a WordprocessingML document at
// the altitude round-trip work needs: sections → block elements
// (headings, paragraphs of styled runs, tables, list items), plus
// styles, numbering definitions, comments and revisions. Identity
// by value throughout (GP 12 rule 1) — no live OpenXml SDK handle
// ever leaks through the model. Where the model deliberately does
// not decompose a payload (run / paragraph property bags, the
// styles part, section properties, unmodelled block elements) it
// carries the verbatim OuterXml string — a plain value — so
// emission can re-attach it byte-equivalently instead of
// approximating it.

/// Author + timestamp attribution for a tracked change. `Date` is
/// optional because OOXML's `w:date` attribute is optional on
/// `w:ins` / `w:del`.
type RevisionInfo = {
    Author: string
    Date: DateTimeOffset option
}

/// A revision mark on a run (or on a paragraph mark). Imported from
/// existing `w:ins` / `w:del` wrappers; produced by `Revisions`
/// edits; lowered back to `w:ins` / `w:del` by `Emit`.
type RevisionMark =
    | Inserted of RevisionInfo
    | Deleted of RevisionInfo

/// Parsed formatting conveniences plus the verbatim `w:rPr` payload.
/// The booleans are read-side conveniences (heading detection,
/// formatting-aware chunking, tests); `RawProperties` is what
/// emission re-attaches when present, so imported runs round-trip
/// fonts / colours / everything else the booleans don't name.
type RunFormatting = {
    Bold: bool
    Italic: bool
    Underline: bool
    Strikethrough: bool
    /// `w:rStyle` character style id, when present.
    StyleId: string option
    /// Verbatim `w:rPr` outer XML captured at import; `None` for
    /// runs authored programmatically (emission generates a minimal
    /// `w:rPr` from the booleans instead).
    RawProperties: string option
}

module RunFormatting =
    let none = {
        Bold = false
        Italic = false
        Underline = false
        Strikethrough = false
        StyleId = None
        RawProperties = None
    }

/// A contiguous span of identically-formatted text. Tabs and line
/// breaks inside the source run are normalised into the text as
/// `'\t'` / `'\n'`; emission lowers them back to `w:tab` / `w:br`.
type Run = {
    Text: string
    Formatting: RunFormatting
    /// `Some` when the run sits inside a tracked change (`w:ins` /
    /// `w:del`) — pre-existing on import, or produced by `Revisions`.
    Revision: RevisionMark option
}

module Run =
    /// A plain unformatted run — the common case when authoring
    /// programmatic content.
    let plain (text: string) : Run = {
        Text = text
        Formatting = RunFormatting.none
        Revision = None
    }

/// Reference from a list item into the numbering definitions
/// (`w:numPr`): which numbering instance, at which indent level.
type NumberingRef = { NumberingId: int; Level: int }

/// A paragraph's content + properties. Headings and list items wrap
/// this same record — they are paragraphs with a recognised style /
/// numbering reference.
type ParagraphModel = {
    Runs: Run list
    /// `w:pStyle` paragraph style id, when present.
    StyleId: string option
    /// Verbatim `w:pPr` outer XML (with any `w:sectPr` stripped —
    /// section properties live on `Section.RawProperties`); `None`
    /// for paragraphs authored programmatically.
    RawProperties: string option
    /// Revision mark on the paragraph mark itself (`w:pPr/w:rPr/
    /// w:ins|w:del`) — set when a whole paragraph is inserted or
    /// deleted as a tracked change.
    MarkRevision: RevisionMark option
    /// Ids of comments anchored on this paragraph. Anchoring is
    /// paragraph-grained: emission wraps the paragraph's runs in
    /// `w:commentRangeStart` / `End` + a `w:commentReference` run.
    CommentIds: int list
}

module ParagraphModel =
    let create (runs: Run list) : ParagraphModel = {
        Runs = runs
        StyleId = None
        RawProperties = None
        MarkRevision = None
        CommentIds = []
    }

    /// The paragraph's visible plain text: deleted-revision runs are
    /// excluded (they are struck out of the accepted document).
    let text (paragraph: ParagraphModel) : string =
        paragraph.Runs
        |> List.choose (fun run ->
            match run.Revision with
            | Some(Deleted _) -> None
            | _ -> Some run.Text)
        |> String.concat ""

/// Block elements. Table cells nest blocks, so the table shape and
/// the block DU are mutually recursive.
type Block =
    /// A paragraph whose style id matches `Heading1`–`Heading9`.
    | Heading of level: int * paragraph: ParagraphModel
    | Paragraph of ParagraphModel
    /// A paragraph carrying a `w:numPr` numbering reference.
    | ListItem of numbering: NumberingRef * paragraph: ParagraphModel
    | Table of TableModel
    /// A block-level element outside the model's vocabulary, carried
    /// verbatim (outer XML) so emission re-attaches it unchanged.
    /// Every `OpaqueBlock` has a matching `ResidueEntry` with
    /// `Disposition = CarriedOpaque`.
    | OpaqueBlock of outerXml: string

and TableModel = {
    Rows: TableRow list
    /// Verbatim `w:tblPr` outer XML, when imported.
    RawProperties: string option
    /// Verbatim `w:tblGrid` outer XML (column widths), when imported.
    RawGrid: string option
}

and TableRow = {
    Cells: TableCell list
    /// Verbatim `w:trPr` outer XML, when imported.
    RawProperties: string option
}

and TableCell = {
    Blocks: Block list
    /// Verbatim `w:tcPr` outer XML, when imported.
    RawProperties: string option
}

module Block =
    /// Heading detection: a paragraph style id of `Heading1`–
    /// `Heading9` (Word's built-in convention) marks a heading of
    /// that level.
    let headingLevel (styleId: string option) : int option =
        match styleId with
        | Some sid when
            sid.Length = 8
            && sid.StartsWith("Heading", StringComparison.Ordinal)
            && sid[7] >= '1'
            && sid[7] <= '9'
            ->
            Some(int sid[7] - int '0')
        | _ -> None

    /// Classify a paragraph into its block kind: heading style wins
    /// (a numbered heading stays a heading — its numbering reference
    /// still round-trips via `RawProperties`), then a numbering
    /// reference makes a list item, otherwise a plain paragraph.
    let classify (numbering: NumberingRef option) (paragraph: ParagraphModel) : Block =
        match headingLevel paragraph.StyleId with
        | Some level -> Heading(level, paragraph)
        | None ->
            match numbering with
            | Some numberingRef -> ListItem(numberingRef, paragraph)
            | None -> Paragraph paragraph

    /// The block's visible plain text (deleted-revision runs
    /// excluded). Tables join cells with tabs and rows with
    /// newlines; opaque blocks contribute nothing.
    let rec text (block: Block) : string =
        match block with
        | Heading(_, p)
        | Paragraph p
        | ListItem(_, p) -> ParagraphModel.text p
        | Table t ->
            t.Rows
            |> List.map (fun row ->
                row.Cells
                |> List.map (fun cell -> cell.Blocks |> List.map text |> String.concat "\n")
                |> String.concat "\t")
            |> String.concat "\n"
        | OpaqueBlock _ -> ""

/// A document section: a run of blocks closed by a `w:sectPr`
/// (page size, margins, columns — carried verbatim). Single-section
/// documents are the common case.
type Section = {
    Blocks: Block list
    /// Verbatim `w:sectPr` outer XML; `None` for programmatically
    /// authored sections (emission lets Word apply its defaults).
    RawProperties: string option
}

module Section =
    let create (blocks: Block list) : Section = {
        Blocks = blocks
        RawProperties = None
    }

/// One style definition's identity row, parsed for convenience. The
/// authoritative styles payload is `StyleDefinitions.RawXml`.
type StyleInfo = {
    StyleId: string
    Name: string option
    /// `w:type` — `"paragraph"` / `"character"` / `"table"` /
    /// `"numbering"`, when declared.
    Type: string option
    BasedOn: string option
}

/// The document's styles part: parsed identity rows + the verbatim
/// part XML. When `RawXml` is present emission writes it back
/// unchanged; when absent (programmatic documents) emission
/// generates a minimal styles part covering the heading levels the
/// model uses.
type StyleDefinitions = {
    Styles: StyleInfo list
    RawXml: string option
}

module StyleDefinitions =
    let empty = { Styles = []; RawXml = None }

/// One numbering instance's identity row (`w:num`), parsed for
/// convenience. The authoritative numbering payload is
/// `NumberingDefinitions.RawXml`.
type NumberingInfo = {
    NumberingId: int
    AbstractNumberingId: int option
}

type NumberingDefinitions = {
    Instances: NumberingInfo list
    RawXml: string option
}

module NumberingDefinitions =
    let empty = { Instances = []; RawXml = None }

/// A comment from the comments part. The body is flattened to plain
/// text (multi-paragraph comment bodies join with newlines);
/// emission recreates a single-paragraph body.
type Comment = {
    Id: int
    Author: string
    Initials: string option
    Date: DateTimeOffset option
    Text: string
}

/// The structural model of one `.docx`.
type DocModel = {
    Sections: Section list
    Styles: StyleDefinitions
    Numbering: NumberingDefinitions
    Comments: Comment list
}

/// Address of a block inside a model: zero-based section index +
/// zero-based block index within that section. Addresses are
/// positions in the model value they are applied to — `Revisions`
/// edits that insert blocks shift the addresses of later blocks in
/// the same section.
type BlockAddress = { Section: int; Block: int }

module DocModel =
    let empty = {
        Sections = []
        Styles = StyleDefinitions.empty
        Numbering = NumberingDefinitions.empty
        Comments = []
    }

    /// A single-section document from a block list — the common
    /// programmatic-authoring entry point.
    let ofBlocks (blocks: Block list) : DocModel = {
        empty with
            Sections = [ Section.create blocks ]
    }

    let tryBlock (address: BlockAddress) (model: DocModel) : Block option =
        model.Sections
        |> List.tryItem address.Section
        |> Option.bind (fun section -> section.Blocks |> List.tryItem address.Block)

// ─── Lossy-residue report ────────────────────────────────────────

/// What happened to an element the model's vocabulary does not
/// capture.
type ResidueDisposition =
    /// Carried verbatim as an `OpaqueBlock` — re-emitted unchanged,
    /// but opaque to the model (no text, no addresses inside it).
    | CarriedOpaque
    /// Not representable in the model at its position (inline
    /// content inside a paragraph) — absent from emitted output.
    | Dropped

/// One uncaptured element. The report names every such element
/// exactly — it is a first-class return value of import, never a
/// log line.
type ResidueEntry = {
    /// The element's qualified tag name, e.g. `"w:sdt"`,
    /// `"w:drawing"`, `"w:bookmarkStart"`.
    ElementKind: string
    /// Human-readable model address, e.g. `"section 1, block 4"` or
    /// `"section 1, block 2, run 3"` (1-based).
    Location: string
    Reason: string
    Disposition: ResidueDisposition
}

type ResidueReport = { Entries: ResidueEntry list }

module ResidueReport =
    let empty = { Entries = [] }
    let isEmpty (report: ResidueReport) = report.Entries.IsEmpty

    /// Entries carried opaquely (round-trip safe, model-invisible).
    let carried (report: ResidueReport) =
        report.Entries |> List.filter (fun e -> e.Disposition = CarriedOpaque)

    /// Entries dropped from the model (absent from emitted output).
    let dropped (report: ResidueReport) =
        report.Entries |> List.filter (fun e -> e.Disposition = Dropped)

/// Import result: the structural model plus the honest account of
/// what the model did not capture.
type ImportedDocument = {
    Model: DocModel
    Residue: ResidueReport
}