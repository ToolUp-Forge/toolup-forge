// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Golden-file machinery for the round-trip fidelity corpus: the three
/// textual projections each fixture is pinned by, and the compare-or-
/// regenerate gate that reads and writes them.
///
/// **Why textual goldens rather than package bytes.** A `.docx` is a
/// zip; comparing packages byte-for-byte would fail on container noise
/// and succeed on nothing a reviewer could read. Each projection here
/// is line-oriented and one fact per line, so a fidelity regression
/// arrives as a reviewable diff naming the run, the property bag or the
/// residue entry that moved — not as "the bytes differ".
///
/// **The regeneration switch is deliberately loud.** Setting
/// `TOOLUP_APPROVE_OPENXML_GOLDENS` rewrites the goldens AND fails the
/// case that rewrote each one, so a regeneration run can never read as
/// green and a run with the variable set by accident cannot pass. The
/// api-baseline gate's lesson, applied at the point it is easiest to
/// forget: approve mode passing trivially means its green proves
/// nothing.
module ToolUp.OpenXml.Tests.Corpus.Goldens

open System
open System.IO
open System.Reflection
open System.Text
open System.Text.RegularExpressions
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open ToolUp.OpenXml

// ─── Grounding ───────────────────────────────────────────────────

/// The env var that arms regeneration. Named for the gate it rewrites,
/// in the shape of the repo's existing `TOOLUP_APPROVE_API`.
[<Literal>]
let ApproveVariable = "TOOLUP_APPROVE_OPENXML_GOLDENS"

/// `src/ToolUp.OpenXml.Tests/Corpus/` — resolved from the running test
/// assembly (`bin/<Config>/net10.0/…dll` → up 3 = the project dir), so
/// a regeneration writes the SOURCE goldens rather than a copy under
/// `bin/` that the next clean build discards.
let corpusDir () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "Corpus"))

let approveModeOn () =
    match Environment.GetEnvironmentVariable ApproveVariable with
    | null
    | "" -> false
    | _ -> true

// ─── Rendering helpers ───────────────────────────────────────────

/// Namespace declarations are re-derived on every serialisation and
/// carry no document content, so they are stripped before a verbatim
/// payload reaches a golden — otherwise an OpenXml SDK bump churns
/// every file for a difference no reader cares about. Everything else
/// in the payload is pinned exactly.
let private stripNamespaceDeclarations (xml: string) =
    Regex.Replace(xml, @"\s+xmlns(:\w+)?=""[^""]*""", "")

/// Raw payloads reach the goldens on ONE line, so a diff names the
/// property bag that moved rather than re-flowing a block.
let private normaliseRaw (xml: string) =
    let stripped = stripNamespaceDeclarations xml
    Regex.Replace(stripped, @"\s*\r?\n\s*", " ").Trim()

let private renderRaw (label: string) (raw: string option) =
    match raw with
    | Some xml -> sprintf "%s %s" label (normaliseRaw xml)
    | None -> sprintf "%s -" label

/// Escaped so a tab or newline inside run text is visible in the
/// golden rather than silently reshaping the file.
let private quote (s: string) =
    let escaped =
        s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\"", "\\\"")

    sprintf "\"%s\"" escaped

let private renderOption (value: string option) =
    match value with
    | Some v -> quote v
    | None -> "-"

let private renderDate (date: DateTimeOffset option) =
    match date with
    | Some d -> d.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Globalization.CultureInfo.InvariantCulture)
    | None -> "-"

let private renderRevision (revision: RevisionMark option) =
    match revision with
    | Some(Inserted info) -> sprintf "ins author=%s date=%s" (quote info.Author) (renderDate info.Date)
    | Some(Deleted info) -> sprintf "del author=%s date=%s" (quote info.Author) (renderDate info.Date)
    | None -> "-"

// ─── Projection 1 — the DocModel ─────────────────────────────────

let private renderRun (out: StringBuilder) (indent: string) (index: int) (run: Run) =
    let flags =
        [
            if run.Formatting.Bold then
                "b"
            if run.Formatting.Italic then
                "i"
            if run.Formatting.Underline then
                "u"
            if run.Formatting.Strikethrough then
                "s"
        ]
        |> String.concat ""

    out
        .AppendLine(
            sprintf
                "%srun %d text=%s fmt=%s rStyle=%s rev=[%s]"
                indent
                index
                (quote run.Text)
                (if flags = "" then "-" else flags)
                (renderOption run.Formatting.StyleId)
                (renderRevision run.Revision)
        )
        .AppendLine(sprintf "%s  %s" indent (renderRaw "rPr" run.Formatting.RawProperties))
    |> ignore

let private renderParagraph (out: StringBuilder) (indent: string) (paragraph: ParagraphModel) =
    out
        .AppendLine(sprintf "%sstyle %s" indent (renderOption paragraph.StyleId))
        .AppendLine(sprintf "%smark [%s]" indent (renderRevision paragraph.MarkRevision))
        .AppendLine(sprintf "%scomments [%s]" indent (paragraph.CommentIds |> List.map string |> String.concat ", "))
        .AppendLine(sprintf "%s%s" indent (renderRaw "pPr" paragraph.RawProperties))
    |> ignore

    paragraph.Runs |> List.iteri (fun i run -> renderRun out indent (i + 1) run)

let rec private renderBlock (out: StringBuilder) (indent: string) (index: int) (block: Block) =
    let inner = indent + "  "

    match block with
    | Heading(level, paragraph) ->
        out.AppendLine(sprintf "%sblock %d heading level=%d" indent index level)
        |> ignore

        renderParagraph out inner paragraph
    | Paragraph paragraph ->
        out.AppendLine(sprintf "%sblock %d paragraph" indent index) |> ignore
        renderParagraph out inner paragraph
    | ListItem(numbering, paragraph) ->
        out.AppendLine(
            sprintf "%sblock %d listItem numId=%d ilvl=%d" indent index numbering.NumberingId numbering.Level
        )
        |> ignore

        renderParagraph out inner paragraph
    | Table table ->
        out.AppendLine(sprintf "%sblock %d table rows=%d" indent index table.Rows.Length)
        |> ignore

        out
            .AppendLine(sprintf "%s%s" inner (renderRaw "tblPr" table.RawProperties))
            .AppendLine(sprintf "%s%s" inner (renderRaw "tblGrid" table.RawGrid))
        |> ignore

        table.Rows
        |> List.iteri (fun rowIndex row ->
            out
                .AppendLine(sprintf "%srow %d cells=%d" inner (rowIndex + 1) row.Cells.Length)
                .AppendLine(sprintf "%s  %s" inner (renderRaw "trPr" row.RawProperties))
            |> ignore

            row.Cells
            |> List.iteri (fun cellIndex cell ->
                let cellIndent = inner + "    "

                out
                    .AppendLine(sprintf "%s  cell %d blocks=%d" inner (cellIndex + 1) cell.Blocks.Length)
                    .AppendLine(sprintf "%s%s" cellIndent (renderRaw "tcPr" cell.RawProperties))
                |> ignore

                cell.Blocks
                |> List.iteri (fun blockIndex nested -> renderBlock out cellIndent (blockIndex + 1) nested)))
    | OpaqueBlock outerXml ->
        out
            .AppendLine(sprintf "%sblock %d opaque" indent index)
            .AppendLine(sprintf "%s  xml %s" indent (normaliseRaw outerXml))
        |> ignore

/// The structural projection a fixture is pinned by: every section,
/// block, run and property bag the model captures, plus the styles,
/// numbering and comment parts. Nothing is summarised — a golden that
/// elided a payload could not detect that payload being dropped.
let renderModel (model: DocModel) : string =
    let out = StringBuilder()

    out.AppendLine(sprintf "sections %d" model.Sections.Length) |> ignore

    model.Sections
    |> List.iteri (fun sectionIndex section ->
        out
            .AppendLine(sprintf "section %d blocks=%d" (sectionIndex + 1) section.Blocks.Length)
            .AppendLine(sprintf "  %s" (renderRaw "sectPr" section.RawProperties))
        |> ignore

        section.Blocks
        |> List.iteri (fun blockIndex block -> renderBlock out "  " (blockIndex + 1) block))

    out.AppendLine(sprintf "styles %d" model.Styles.Styles.Length) |> ignore

    for style in model.Styles.Styles do
        out.AppendLine(
            sprintf
                "  style id=%s name=%s type=%s basedOn=%s"
                (quote style.StyleId)
                (renderOption style.Name)
                (renderOption style.Type)
                (renderOption style.BasedOn)
        )
        |> ignore

    out.AppendLine(sprintf "  %s" (renderRaw "stylesXml" model.Styles.RawXml))
    |> ignore

    out.AppendLine(sprintf "numbering %d" model.Numbering.Instances.Length)
    |> ignore

    for instance in model.Numbering.Instances do
        out.AppendLine(
            sprintf
                "  num id=%d abstract=%s"
                instance.NumberingId
                (instance.AbstractNumberingId |> Option.map string |> Option.defaultValue "-")
        )
        |> ignore

    out.AppendLine(sprintf "  %s" (renderRaw "numberingXml" model.Numbering.RawXml))
    |> ignore

    out.AppendLine(sprintf "comments %d" model.Comments.Length) |> ignore

    for comment in model.Comments do
        out.AppendLine(
            sprintf
                "  comment id=%d author=%s initials=%s date=%s text=%s"
                comment.Id
                (quote comment.Author)
                (renderOption comment.Initials)
                (renderDate comment.Date)
                (quote comment.Text)
        )
        |> ignore

    out.ToString()

// ─── Projection 2 — the emitted OpenXml package ──────────────────

let private qualifiedName (el: OpenXmlElement) =
    if String.IsNullOrEmpty el.Prefix then
        el.LocalName
    else
        sprintf "%s:%s" el.Prefix el.LocalName

let private renderAttributes (el: OpenXmlElement) =
    // `GetAttributes` excludes namespace declarations by construction;
    // sorting makes the golden independent of attribute emission order.
    el.GetAttributes()
    |> Seq.map (fun attr ->
        let name =
            if String.IsNullOrEmpty attr.Prefix then
                attr.LocalName
            else
                sprintf "%s:%s" attr.Prefix attr.LocalName

        sprintf "%s=%s" name (quote attr.Value))
    |> Seq.sort
    |> String.concat " "

let rec private renderElement (out: StringBuilder) (depth: int) (el: OpenXmlElement) =
    let indent = String(' ', depth * 2)
    let attributes = renderAttributes el

    let head =
        if attributes = "" then
            sprintf "%s%s" indent (qualifiedName el)
        else
            sprintf "%s%s %s" indent (qualifiedName el) attributes

    let line =
        if el.HasChildren then
            head
        else
            match el with
            | :? Wordprocessing.Text as t -> sprintf "%s %s" head (quote t.Text)
            | :? Wordprocessing.DeletedText as t -> sprintf "%s %s" head (quote t.Text)
            | _ -> head

    out.AppendLine line |> ignore

    for child in el.ChildElements do
        renderElement out (depth + 1) child

let private allParts (doc: WordprocessingDocument) =
    let seen = Collections.Generic.HashSet<string>()
    let collected = ResizeArray<string * string>()

    let rec walk (parts: seq<IdPartPair>) =
        for pair in parts do
            let uri = pair.OpenXmlPart.Uri.ToString()

            if seen.Add uri then
                collected.Add(uri, pair.OpenXmlPart.ContentType)
                walk pair.OpenXmlPart.Parts

    walk doc.Parts
    collected |> List.ofSeq |> List.sortBy fst

/// The OpenXml-level projection: the package's parts, then the full
/// element tree of the main document part and of every part the model
/// writes. Attribute values are included, so a lost `w:author`, a
/// duplicated revision id, or a `w:rPr` that stopped being re-attached
/// all surface as a line-level diff.
let renderPackage (bytes: byte[]) : string =
    use stream = new MemoryStream(bytes)
    use doc = WordprocessingDocument.Open(stream, false)
    let out = StringBuilder()

    out.AppendLine "== parts ==" |> ignore

    for uri, contentType in allParts doc do
        out.AppendLine(sprintf "%s %s" uri contentType) |> ignore

    let main = doc.MainDocumentPart

    let namedTrees: (string * OpenXmlElement option) list = [
        "/word/document.xml",
        (match main.Document with
         | null -> None
         | d -> Some(d :> OpenXmlElement))
        "/word/styles.xml",
        (match main.StyleDefinitionsPart with
         | null -> None
         | part ->
             match part.Styles with
             | null -> None
             | s -> Some(s :> OpenXmlElement))
        "/word/numbering.xml",
        (match main.NumberingDefinitionsPart with
         | null -> None
         | part ->
             match part.Numbering with
             | null -> None
             | n -> Some(n :> OpenXmlElement))
        "/word/comments.xml",
        (match main.WordprocessingCommentsPart with
         | null -> None
         | part ->
             match part.Comments with
             | null -> None
             | c -> Some(c :> OpenXmlElement))
    ]

    for name, tree in namedTrees do
        match tree with
        | None -> ()
        | Some element ->
            out.AppendLine(sprintf "== %s ==" name) |> ignore
            renderElement out 0 element

    out.ToString()

// ─── Projection 3 — the residue report ───────────────────────────

/// The expected-residue baseline. A newly-lossy import adds a line
/// here; an import that silently stopped REPORTING a loss removes one.
/// Either way the build fails rather than the change vanishing.
let renderResidue (residue: ResidueReport) : string =
    let out = StringBuilder()
    out.AppendLine(sprintf "entries %d" residue.Entries.Length) |> ignore

    for entry in residue.Entries do
        let disposition =
            match entry.Disposition with
            | CarriedOpaque -> "carried-opaque"
            | Dropped -> "dropped"

        out.AppendLine(sprintf "%s | %s | %s | %s" entry.ElementKind entry.Location disposition entry.Reason)
        |> ignore

    out.ToString()

// ─── The compare-or-regenerate gate ──────────────────────────────

/// Goldens are committed LF and compared LF, so a CRLF checkout is not
/// a permanent diff.
let private normaliseLineEndings (s: string) =
    s.Replace("\r\n", "\n").Replace("\r", "\n")

let private goldenPath (name: string) = Path.Combine(corpusDir (), name)

/// The message every failure path ends with. Deliberately spells the
/// PowerShell form the operator actually types (this repo's operator
/// shell), and says what regeneration is FOR — an intentional, reviewed
/// format change — rather than presenting it as the way to clear a red.
let private regenerationInstructions (name: string) =
    sprintf
        "Regenerate ONLY for an intentional, reviewed change to the import/emit format — never to clear a red:\n  $env:%s = \"1\"\n  dotnet run --project src/ToolUp.OpenXml.Tests/ToolUp.OpenXml.Tests.fsproj\n  $env:%s = $null\nThe regeneration run FAILS by design (it rewrote goldens; it verified nothing). Re-run WITHOUT the variable to verify, and commit src/ToolUp.OpenXml.Tests/Corpus/%s in the same PR so the fidelity change is reviewed alongside the code that caused it."
        ApproveVariable
        ApproveVariable
        name

/// Compare `actual` against the committed golden `name`, or — under
/// `TOOLUP_APPROVE_OPENXML_GOLDENS` — rewrite it and fail.
///
/// Returns `Ok ()` when the projection matches, `Error message` in
/// every other case: a mismatch, a missing golden (never silently
/// created), and a regeneration (which rewrites, then reports what it
/// did). The caller turns the message into an Expecto failure; keeping
/// the decision here means all four paths are stated once.
let check (name: string) (actual: string) : Result<unit, string> =
    let path = goldenPath name
    let actual = normaliseLineEndings actual

    if approveModeOn () then
        Directory.CreateDirectory(corpusDir ()) |> ignore
        File.WriteAllText(path, actual, UTF8Encoding false)

        Error(
            sprintf
                "%s: REGENERATED under %s — this run rewrote the golden and verified nothing.\nRe-run without %s to verify the round trip against the file just written, and review the diff before committing it."
                name
                ApproveVariable
                ApproveVariable
        )
    elif not (File.Exists path) then
        Error(
            sprintf
                "%s: no committed golden at src/ToolUp.OpenXml.Tests/Corpus/%s. A fixture's golden is never created silently — an absent golden pins nothing.\n\n%s"
                name
                name
                (regenerationInstructions name)
        )
    else
        let expected = normaliseLineEndings (File.ReadAllText path)

        if expected = actual then
            Ok()
        else
            let expectedLines = expected.Split '\n'
            let actualLines = actual.Split '\n'

            let firstDifference =
                Seq.init (max expectedLines.Length actualLines.Length) id
                |> Seq.tryPick (fun i ->
                    let e =
                        if i < expectedLines.Length then
                            expectedLines[i]
                        else
                            "<end of file>"

                    let a =
                        if i < actualLines.Length then
                            actualLines[i]
                        else
                            "<end of file>"

                    if e = a then
                        None
                    else
                        Some(sprintf "  line %d\n    golden: %s\n    actual: %s" (i + 1) e a))
                |> Option.defaultValue "  (files differ only in trailing whitespace)"

            Error(
                sprintf
                    "%s: the round trip no longer matches the committed golden — a FIDELITY REGRESSION unless the format change was intended.\n%s\n(golden %d lines, actual %d lines)\n\n%s"
                    name
                    firstDifference
                    expectedLines.Length
                    actualLines.Length
                    (regenerationInstructions name)
            )