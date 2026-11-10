// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// KB default-path regression fixtures (Phase 124 / GP 11): the
/// DOCX ingestion default stays the flat extractor — its output is
/// pinned here — and the structured mode is reachable only by the
/// explicit compose-time opt-in. Drives the internal
/// `ServerExtractors.extractChunks` directly via the
/// InternalsVisibleTo grant in the KB Server fsproj.
module ToolUp.OpenXml.Tests.InProcess.KnowledgeBaseTests

open Expecto
open ToolUp.OpenXml.Tests
open KnowledgeBase

let private extract (mode: SharedTypes.DocxExtractionMode) (bytes: byte[]) =
    let ocr = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    let tables = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()

    ServerExtractors.extractChunks (ServerExtractors.ExtractDeps.composed ocr tables mode) "doc-1" "fixture.docx" bytes
    |> Async.RunSynchronously

let private flatGolden = [
    "[fixture.docx — Section: \"Intro\"]\nAlpha beta gamma"
    "[fixture.docx — Section: \"Sub\"]\nOne Two"
]

let tests =
    testList "KnowledgeBase" [
        testCase "default DOCX extraction output is unchanged (flat-path golden)"
        <| fun () ->
            let chunks =
                extract SharedTypes.DocxExtractionMode.Flat (Fixtures.buildKnowledgeBaseDocx ())

            Expect.equal (chunks |> List.map (fun (chunk, _) -> chunk.Content)) flatGolden "flat chunk contents"

            Expect.equal
                (chunks |> List.map (fun (_, src) -> src.Location))
                [ SharedTypes.Section "Intro"; SharedTypes.Section "Sub" ]
                "flat source locations"

        testCase "structured mode is reachable only by explicit opt-in"
        <| fun () ->
            let chunks =
                extract SharedTypes.DocxExtractionMode.Structured (Fixtures.buildKnowledgeBaseDocx ())

            let contents = chunks |> List.map (fun (chunk, _) -> chunk.Content)

            // Heading PATH keys the chunks (the flat path keys by
            // innermost heading only)...
            Expect.equal contents[0] "[fixture.docx — Section: \"Intro\"]\nAlpha beta gamma" "top-level section chunk"

            Expect.equal contents[1] "[fixture.docx — Section: \"Intro › Sub\"]\nOne\nTwo" "heading-path section chunk"

            // ...and the table — which the flat path never reaches
            // (it walks top-level paragraphs only) — becomes a
            // citable chunk.
            Expect.equal chunks.Length 3 "table chunk emitted"
            Expect.stringContains contents[2] "(table)" "table header"
            Expect.stringContains contents[2] "Col" "table column header in content"

            Expect.equal
                (chunks |> List.map (fun (_, src) -> src.Location))
                [
                    SharedTypes.Section "Intro"
                    SharedTypes.Section "Intro › Sub"
                    SharedTypes.Section "Intro › Sub"
                ]
                "structured source locations"

        testCase "a structured extraction leaves the flat default byte-for-byte"
        <| fun () ->
            extract SharedTypes.DocxExtractionMode.Structured (Fixtures.buildKnowledgeBaseDocx ())
            |> ignore

            let chunks =
                extract SharedTypes.DocxExtractionMode.Flat (Fixtures.buildKnowledgeBaseDocx ())

            Expect.equal
                (chunks |> List.map (fun (chunk, _) -> chunk.Content))
                flatGolden
                "flat golden output unchanged"
    ]