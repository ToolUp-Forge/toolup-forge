// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.Tests.InProcess.ImportTests

open System
open Expecto
open ToolUp.OpenXml
open ToolUp.OpenXml.Tests

// ─── Deterministic model rendering (shared with round-trip tests) ─
//
// A compact structural projection: block kinds, texts, formatting
// flags, numbering / comment / revision attribution — everything the
// model captures except the verbatim raw-XML payloads (whose exact
// strings may be re-serialised with cosmetic differences).

let private renderRun (run: Run) =
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

    let revision =
        match run.Revision with
        | Some(Inserted info) -> sprintf "+%s" info.Author
        | Some(Deleted info) -> sprintf "-%s" info.Author
        | None -> ""

    sprintf "(%s|%s|%s)" run.Text flags revision

let private renderParagraph (paragraph: ParagraphModel) =
    let mark =
        match paragraph.MarkRevision with
        | Some(Inserted info) -> sprintf " mark+%s" info.Author
        | Some(Deleted info) -> sprintf " mark-%s" info.Author
        | None -> ""

    let comments =
        if paragraph.CommentIds.IsEmpty then
            ""
        else
            sprintf " c%A" paragraph.CommentIds

    let style = paragraph.StyleId |> Option.defaultValue ""

    sprintf "%s{%s}%s%s" style (paragraph.Runs |> List.map renderRun |> String.concat "") mark comments

let rec renderBlock (block: Block) =
    match block with
    | Heading(level, p) -> sprintf "H%d%s" level (renderParagraph p)
    | Paragraph p -> sprintf "P%s" (renderParagraph p)
    | ListItem(numbering, p) -> sprintf "L%d.%d%s" numbering.NumberingId numbering.Level (renderParagraph p)
    | Table t ->
        t.Rows
        |> List.map (fun row ->
            row.Cells
            |> List.map (fun cell -> cell.Blocks |> List.map renderBlock |> String.concat ",")
            |> String.concat "|")
        |> String.concat ";"
        |> sprintf "T[%s]"
    | Figure _ -> "F"
    | OpaqueBlock _ -> "O"

let renderModel (model: DocModel) =
    let sections =
        model.Sections
        |> List.map (fun section ->
            let props = if section.RawProperties.IsSome then "+sectPr" else ""
            sprintf "S%s(%s)" props (section.Blocks |> List.map renderBlock |> String.concat " "))
        |> String.concat "\n"

    let comments =
        model.Comments
        |> List.map (fun c -> sprintf "#%d %s(%s): %s" c.Id c.Author (c.Initials |> Option.defaultValue "") c.Text)
        |> String.concat "\n"

    sections + "\n---\n" + comments

let tests =
    testList "Import" [
        testCase "rich fixture imports with an empty residue report"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())
            Expect.isTrue (ResidueReport.isEmpty imported.Residue) (sprintf "no residue, got %A" imported.Residue)

        testCase "headings carry their level and text"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())
            let blocks = imported.Model.Sections.Head.Blocks

            match blocks[0], blocks[2] with
            | Heading(1, intro), Heading(2, background) ->
                Expect.equal (ParagraphModel.text intro) "Introduction" "level-1 text"
                Expect.equal (ParagraphModel.text background) "Background" "level-2 text"
            | other -> failtestf "expected Heading blocks at 0 and 2, got %A" other

        testCase "runs keep their per-span formatting"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())

            match imported.Model.Sections.Head.Blocks[1] with
            | Paragraph p ->
                Expect.equal (p.Runs |> List.map _.Text) [ "Alpha "; "bold"; " beta" ] "three runs"
                Expect.equal (p.Runs |> List.map _.Formatting.Bold) [ false; true; false ] "middle run bold"
            | other -> failtestf "expected mixed-formatting paragraph, got %A" other

        testCase "list items carry their numbering reference"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())
            let blocks = imported.Model.Sections.Head.Blocks

            match blocks[3], blocks[4] with
            | ListItem(first, p1), ListItem(second, p2) ->
                Expect.equal first { NumberingId = 1; Level = 0 } "numbering ref"
                Expect.equal second { NumberingId = 1; Level = 0 } "numbering ref"
                Expect.equal (ParagraphModel.text p1) "First point" "first item"
                Expect.equal (ParagraphModel.text p2) "Second point" "second item"
            | other -> failtestf "expected ListItem blocks, got %A" other

        testCase "tables capture rows, cells and cell text"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())

            match imported.Model.Sections.Head.Blocks[5] with
            | Table t ->
                Expect.equal t.Rows.Length 2 "two rows"
                Expect.equal (t.Rows |> List.map _.Cells.Length) [ 2; 2 ] "two cells per row"
                Expect.equal (Block.text (Table t)) "Name\tValue\ntemp\t42" "cell text projection"
                Expect.isSome t.RawProperties "tblPr captured"
                Expect.isSome t.RawGrid "tblGrid captured"
            | other -> failtestf "expected Table block, got %A" other

        testCase "comments and their paragraph anchors are captured"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())

            match imported.Model.Comments with
            | [ comment ] ->
                Expect.equal comment.Id 1 "comment id"
                Expect.equal comment.Author "Alice" "author"
                Expect.equal comment.Initials (Some "A") "initials"
                Expect.equal comment.Text "Looks good" "body text"
                Expect.isSome comment.Date "date captured"
            | other -> failtestf "expected one comment, got %A" other

            match imported.Model.Sections.Head.Blocks[6] with
            | Paragraph p ->
                Expect.equal p.CommentIds [ 1 ] "anchor recorded"
                Expect.equal (ParagraphModel.text p) "Commented text" "anchored text"
            | other -> failtestf "expected commented paragraph, got %A" other

        testCase "pre-existing tracked insertions surface as revision marks"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())

            match imported.Model.Sections.Head.Blocks[7] with
            | Paragraph p ->
                match p.Runs with
                | [ original; added ] ->
                    Expect.equal original.Revision None "first run unrevised"

                    match added.Revision with
                    | Some(Inserted info) ->
                        Expect.equal info.Author "Bob" "revision author"
                        Expect.equal info.Date (Some(DateTimeOffset Fixtures.revisionDate)) "revision date"
                    | other -> failtestf "expected an insertion mark, got %A" other

                    Expect.equal added.Text "added text" "inserted text"
                | other -> failtestf "expected two runs, got %A" other
            | other -> failtestf "expected revised paragraph, got %A" other

        testCase "styles and numbering parts are parsed and carried verbatim"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())
            let model = imported.Model

            let heading1 = model.Styles.Styles |> List.find (fun s -> s.StyleId = "Heading1")

            Expect.equal heading1.Name (Some "heading 1") "style name"
            Expect.equal heading1.BasedOn (Some "Normal") "basedOn"
            Expect.equal heading1.Type (Some "paragraph") "style type"
            Expect.isSome model.Styles.RawXml "styles part carried verbatim"

            Expect.equal
                model.Numbering.Instances
                [
                    {
                        NumberingId = 1
                        AbstractNumberingId = Some 0
                    }
                ]
                "numbering instance"

            Expect.isSome model.Numbering.RawXml "numbering part carried verbatim"

        testCase "section properties are captured on the section"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())

            match imported.Model.Sections with
            | [ section ] ->
                match section.RawProperties with
                | Some raw -> Expect.stringContains raw "pgSz" "page size present"
                | None -> failtest "expected captured sectPr"
            | other -> failtestf "expected one section, got %A" other

        testCase "residue report names every uncaptured element exactly"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildExoticDocx ())

            let entries =
                imported.Residue.Entries
                |> List.map (fun e -> e.ElementKind, e.Location, e.Disposition)

            Expect.equal
                entries
                [
                    "w:sdt", "section 1, block 2", CarriedOpaque
                    "w:hyperlink", "section 1, block 3", Dropped
                    "w:bookmarkStart", "section 1, block 3", Dropped
                    "w:drawing", "section 1, block 3, run 2", Dropped
                ]
                "exact residue entries"

        testCase "content controls are carried opaquely; hyperlink text is flattened"
        <| fun () ->
            let imported = Import.fromBytes (Fixtures.buildExoticDocx ())
            let blocks = imported.Model.Sections.Head.Blocks

            match blocks[1] with
            | OpaqueBlock xml -> Expect.stringContains xml "inside control" "sdt content carried verbatim"
            | other -> failtestf "expected OpaqueBlock, got %A" other

            match blocks[2] with
            | Paragraph p -> Expect.equal (ParagraphModel.text p) "link text" "hyperlink runs flattened"
            | other -> failtestf "expected flattened paragraph, got %A" other

        testCase "GP 13 — companion references no ToolUp.Platform assembly (strip-imports proof)"
        <| fun () ->
            // The companion is additive by construction: nothing in
            // ToolUp.Platform.* references it, and it references
            // nothing in ToolUp.Platform.* — so a deployment that
            // doesn't compose it pays nothing.
            let references =
                typeof<DocModel>.Assembly.GetReferencedAssemblies() |> Array.map _.Name

            Expect.isFalse
                (references
                 |> Array.exists (fun name -> name <> null && name.StartsWith "ToolUp."))
                (sprintf "no ToolUp.* references, got: %A" references)

        testCase "dependency surface is OpenXml-only outside the BCL"
        <| fun () ->
            let nonBcl =
                typeof<DocModel>.Assembly.GetReferencedAssemblies()
                |> Array.map _.Name
                |> Array.filter (fun name ->
                    name <> null
                    && not (name.StartsWith "System")
                    && not (name.StartsWith "Microsoft")
                    && name <> "FSharp.Core"
                    && name <> "netstandard"
                    && name <> "mscorlib")

            Expect.isTrue
                (nonBcl |> Array.forall (fun name -> name.StartsWith "DocumentFormat.OpenXml"))
                (sprintf "only OpenXml expected outside the BCL, got: %A" nonBcl)
    ]