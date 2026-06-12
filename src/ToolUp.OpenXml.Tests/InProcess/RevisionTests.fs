// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.Tests.InProcess.RevisionTests

open System
open System.IO
open Expecto
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open ToolUp.OpenXml
open ToolUp.OpenXml.Tests

let private reviewer = {
    Name = "Reviewer"
    Initials = Some "R"
}

let private timestamp = DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero)

let private baseModel () =
    DocModel.ofBlocks [
        Heading(
            1,
            {
                ParagraphModel.create [ Run.plain "Title" ] with
                    StyleId = Some "Heading1"
            }
        )
        Paragraph(
            ParagraphModel.create [
                Run.plain "Alpha "
                {
                    Run.plain "bold" with
                        Formatting = { RunFormatting.none with Bold = true }
                }
                Run.plain " beta"
            ]
        )
        Paragraph(ParagraphModel.create [ Run.plain "Delete me entirely." ])
    ]

let private standardEdits = {
    Author = reviewer
    Timestamp = timestamp
    Edits = [
        ReplaceText({ Section = 0; Block = 1 }, "pha bold be", "X")
        DeleteParagraph { Section = 0; Block = 2 }
        InsertParagraphAfter({ Section = 0; Block = 2 }, ParagraphModel.create [ Run.plain "Brand new paragraph." ])
        AddComment({ Section = 0; Block = 1 }, "please check")
    ]
}

let private applied () =
    match Revisions.applyTracked standardEdits (baseModel ()) with
    | Ok model -> model
    | Error e -> failtestf "expected edits to apply, got %A" e

/// Open emitted bytes for OOXML-part assertions.
let private withDocument (bytes: byte[]) (assertions: WordprocessingDocument -> unit) =
    use stream = new MemoryStream(bytes)
    use doc = WordprocessingDocument.Open(stream, false)
    assertions doc

let tests =
    testList "Revisions" [
        testCase "replace splits runs at match boundaries across formatting runs"
        <| fun () ->
            let model = applied ()

            match model.Sections.Head.Blocks[1] with
            | Paragraph p ->
                Expect.equal (ParagraphModel.text p) "AlXta" "visible text after replace"

                let deletedTexts =
                    p.Runs
                    |> List.choose (fun r ->
                        match r.Revision with
                        | Some(Deleted info) -> Some(r.Text, info.Author)
                        | _ -> None)

                Expect.equal
                    deletedTexts
                    [ "pha ", "Reviewer"; "bold", "Reviewer"; " be", "Reviewer" ]
                    "matched span deleted per covered run"

                let inserted =
                    p.Runs
                    |> List.tryFind (fun r ->
                        match r.Revision with
                        | Some(Inserted _) -> true
                        | _ -> false)

                match inserted with
                | Some run ->
                    Expect.equal run.Text "X" "replacement text"
                    Expect.isFalse run.Formatting.Bold "replacement takes the first covered run's formatting"
                | None -> failtest "expected an inserted replacement run"
            | other -> failtestf "expected paragraph, got %A" other

        testCase "delete marks every run and the paragraph mark"
        <| fun () ->
            let model = applied ()

            match model.Sections.Head.Blocks[2] with
            | Paragraph p ->
                Expect.equal (ParagraphModel.text p) "" "no visible text remains"

                match p.MarkRevision with
                | Some(Deleted info) -> Expect.equal info.Author "Reviewer" "paragraph mark deleted"
                | other -> failtestf "expected deleted paragraph mark, got %A" other
            | other -> failtestf "expected paragraph, got %A" other

        testCase "insert adds a fully-marked paragraph after the address"
        <| fun () ->
            let model = applied ()
            Expect.equal model.Sections.Head.Blocks.Length 4 "one block added"

            match model.Sections.Head.Blocks[3] with
            | Paragraph p ->
                Expect.equal (ParagraphModel.text p) "Brand new paragraph." "inserted text"

                match p.MarkRevision, p.Runs.Head.Revision with
                | Some(Inserted _), Some(Inserted _) -> ()
                | other -> failtestf "expected insertion marks on paragraph mark and runs, got %A" other
            | other -> failtestf "expected paragraph, got %A" other

        testCase "comments are allocated ids and anchored at the address"
        <| fun () ->
            let model = applied ()

            match model.Comments with
            | [ comment ] ->
                Expect.equal comment.Id 1 "first comment id"
                Expect.equal comment.Author "Reviewer" "comment author"
                Expect.equal comment.Initials (Some "R") "comment initials"
                Expect.equal comment.Text "please check" "comment text"
                Expect.equal comment.Date (Some timestamp) "comment date"
            | other -> failtestf "expected one comment, got %A" other

            match model.Sections.Head.Blocks[1] with
            | Paragraph p -> Expect.equal p.CommentIds [ 1 ] "anchored on the edited paragraph"
            | other -> failtestf "expected paragraph, got %A" other

        testCase "emitted OOXML carries native w:del runs with attribution"
        <| fun () ->
            withDocument (Emit.toBytes (applied ())) (fun doc ->
                let body = doc.MainDocumentPart.Document.Body

                let deletions = body.Descendants<Wordprocessing.DeletedRun>() |> List.ofSeq

                Expect.equal deletions.Length 4 "three replace segments + one deleted paragraph"

                for deletion in deletions do
                    Expect.equal deletion.Author.Value "Reviewer" "author attribution"
                    Expect.isTrue deletion.Date.HasValue "timestamp attribution"

                let deletedTexts =
                    body.Descendants<Wordprocessing.DeletedText>() |> Seq.map _.Text |> List.ofSeq

                Expect.equal deletedTexts [ "pha "; "bold"; " be"; "Delete me entirely." ] "w:delText payloads")

        testCase "emitted OOXML carries native w:ins runs and paragraph marks"
        <| fun () ->
            withDocument (Emit.toBytes (applied ())) (fun doc ->
                let body = doc.MainDocumentPart.Document.Body

                let insertions = body.Descendants<Wordprocessing.InsertedRun>() |> List.ofSeq

                Expect.equal
                    (insertions |> List.map (fun ins -> ins.InnerText, ins.Author.Value))
                    [ "X", "Reviewer"; "Brand new paragraph.", "Reviewer" ]
                    "insertion payloads + attribution"

                let markInsertions =
                    body.Descendants<Wordprocessing.ParagraphMarkRunProperties>()
                    |> Seq.collect _.Elements<Wordprocessing.Inserted>()
                    |> List.ofSeq

                let markDeletions =
                    body.Descendants<Wordprocessing.ParagraphMarkRunProperties>()
                    |> Seq.collect _.Elements<Wordprocessing.Deleted>()
                    |> List.ofSeq

                Expect.equal markInsertions.Length 1 "inserted paragraph mark"
                Expect.equal markDeletions.Length 1 "deleted paragraph mark")

        testCase "revision ids are unique document-wide"
        <| fun () ->
            withDocument (Emit.toBytes (applied ())) (fun doc ->
                let body = doc.MainDocumentPart.Document.Body

                let ids = [
                    yield! body.Descendants<Wordprocessing.InsertedRun>() |> Seq.map _.Id.Value

                    yield! body.Descendants<Wordprocessing.DeletedRun>() |> Seq.map _.Id.Value

                    yield! body.Descendants<Wordprocessing.Inserted>() |> Seq.map _.Id.Value
                    yield! body.Descendants<Wordprocessing.Deleted>() |> Seq.map _.Id.Value
                ]

                Expect.equal (List.distinct ids).Length ids.Length (sprintf "unique ids, got %A" ids))

        testCase "emitted OOXML carries the comment part and anchors"
        <| fun () ->
            withDocument (Emit.toBytes (applied ())) (fun doc ->
                let comments =
                    doc.MainDocumentPart.WordprocessingCommentsPart.Comments.Elements<Wordprocessing.Comment>()
                    |> List.ofSeq

                match comments with
                | [ comment ] ->
                    Expect.equal comment.Author.Value "Reviewer" "comment author"
                    Expect.equal comment.Initials.Value "R" "comment initials"
                    Expect.equal comment.InnerText "please check" "comment body"

                    let commentId = comment.Id.Value
                    let body = doc.MainDocumentPart.Document.Body

                    let rangeStarts =
                        body.Descendants<Wordprocessing.CommentRangeStart>()
                        |> Seq.map _.Id.Value
                        |> List.ofSeq

                    let references =
                        body.Descendants<Wordprocessing.CommentReference>()
                        |> Seq.map _.Id.Value
                        |> List.ofSeq

                    Expect.equal rangeStarts [ commentId ] "range anchor"
                    Expect.equal references [ commentId ] "reference run"
                | other -> failtestf "expected one comment, got %A" other)

        testCase "redline document validates against the OOXML schema"
        <| fun () ->
            let errors = Fixtures.validate (Emit.toBytes (applied ()))

            Expect.isEmpty (errors |> List.map (fun e -> sprintf "%s: %s" e.Id e.Description)) "no validation errors"

        testCase "tracked edits also apply to imported documents"
        <| fun () ->
            // The acceptance-criteria path end-to-end: import a real
            // fixture, apply a tracked replace, emit, and find the
            // native redline in the output.
            let imported = Import.fromBytes (Fixtures.buildRichDocx ())

            let edits = {
                Author = reviewer
                Timestamp = timestamp
                Edits = [ ReplaceText({ Section = 0; Block = 1 }, "Alpha", "Omega") ]
            }

            match Revisions.applyTracked edits imported.Model with
            | Error e -> failtestf "expected edits to apply, got %A" e
            | Ok model ->
                withDocument (Emit.toBytes model) (fun doc ->
                    let body = doc.MainDocumentPart.Document.Body

                    let deleted =
                        body.Descendants<Wordprocessing.DeletedText>() |> Seq.map _.Text |> List.ofSeq

                    Expect.equal deleted [ "Alpha" ] "original text struck out"

                    let inserted =
                        body.Descendants<Wordprocessing.InsertedRun>()
                        |> Seq.filter (fun ins -> ins.Author.Value = "Reviewer")
                        |> Seq.map _.InnerText
                        |> List.ofSeq

                    Expect.equal inserted [ "Omega" ] "replacement inserted")

        testCase "error paths come back as data"
        <| fun () ->
            let model = baseModel ()

            let outOfRange = {
                Author = reviewer
                Timestamp = timestamp
                Edits = [ DeleteParagraph { Section = 0; Block = 99 } ]
            }

            Expect.equal
                (Revisions.applyTracked outOfRange model)
                (Error(AddressOutOfRange { Section = 0; Block = 99 }))
                "address out of range"

            let notFound = {
                Author = reviewer
                Timestamp = timestamp
                Edits = [ ReplaceText({ Section = 0; Block = 1 }, "zzz", "y") ]
            }

            Expect.equal
                (Revisions.applyTracked notFound model)
                (Error(TextNotFound({ Section = 0; Block = 1 }, "zzz")))
                "text not found"

            let tableModel =
                DocModel.ofBlocks [
                    Table {
                        Rows = []
                        RawProperties = None
                        RawGrid = None
                    }
                ]

            let onTable = {
                Author = reviewer
                Timestamp = timestamp
                Edits = [ DeleteParagraph { Section = 0; Block = 0 } ]
            }

            Expect.equal
                (Revisions.applyTracked onTable tableModel)
                (Error(NotAParagraphBlock({ Section = 0; Block = 0 }, "table")))
                "table is not a paragraph block"

        testCase "a pure deletion needs no replacement run"
        <| fun () ->
            let edits = {
                Author = reviewer
                Timestamp = timestamp
                Edits = [ ReplaceText({ Section = 0; Block = 1 }, "bold ", "") ]
            }

            match Revisions.applyTracked edits (baseModel ()) with
            | Error e -> failtestf "expected edits to apply, got %A" e
            | Ok model ->
                match model.Sections.Head.Blocks[1] with
                | Paragraph p ->
                    Expect.equal (ParagraphModel.text p) "Alpha beta" "visible text after pure deletion"

                    Expect.isFalse
                        (p.Runs
                         |> List.exists (fun r ->
                             match r.Revision with
                             | Some(Inserted _) -> true
                             | _ -> false))
                        "no insertion mark emitted"
                | other -> failtestf "expected paragraph, got %A" other
    ]