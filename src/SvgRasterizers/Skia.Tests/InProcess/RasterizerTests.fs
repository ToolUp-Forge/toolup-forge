// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 576.C — the Skia `ISvgRasterizer` companion.
///
/// These cases render REAL pixels: the point of a companion is that it
/// does the thing the seam only describes, so a double here would
/// verify nothing the base package's own pack does not already cover.
module ToolUp.OpenXml.SvgRasterizer.Skia.Tests.InProcess.RasterizerTests

open System.IO
open Expecto
open DocumentFormat.OpenXml.Packaging
open ToolUp.OpenXml
open ToolUp.OpenXml.SvgRasterizer.Skia

let private rasterizer = SkiaSvgRasterizer.create ()

let private run (svg: string) (widthPx: int) =
    rasterizer.Rasterize(svg, widthPx) |> Async.RunSynchronously

let private squareSvg =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#3366cc\"/></svg>"

let private wideSvg =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 400 100\"><rect width=\"400\" height=\"100\" fill=\"#cc3366\"/></svg>"

let private textSvg =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 320 80\"><rect width=\"320\" height=\"80\" fill=\"white\"/><text x=\"10\" y=\"48\" font-family=\"sans-serif\" font-size=\"24\">Revenue Q3</text></svg>"

let private expectPng (label: string) (result: Result<byte[], string>) : byte[] =
    match result with
    | Error reason -> failtestf "%s: expected PNG bytes, got Error \"%s\"" label reason
    | Ok bytes ->
        Expect.isGreaterThan bytes.Length 8 (sprintf "%s: non-trivial payload" label)

        Expect.sequenceEqual
            bytes[0..7]
            [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
            (sprintf "%s: PNG signature" label)

        bytes

/// Read the produced PNG's dimensions back through the base package's
/// own header reader — the same code the `Intrinsic` size path uses,
/// so the two agree by construction rather than by a second parser.
let private dimensionsOf (bytes: byte[]) =
    match Figures.rasterIntrinsicSize bytes with
    | Some size -> size
    | None -> failtest "the produced bytes carry no readable PNG header"

let tests =
    testList "skia svg rasterizer" [
        test "renders an SVG to PNG bytes" { run squareSvg 128 |> expectPng "square" |> ignore }

        test "honours the requested width" {
            let bytes = run squareSvg 256 |> expectPng "square"
            Expect.equal (fst (dimensionsOf bytes)) 256 "width as requested"
        }

        test "derives the height from the document's own aspect ratio" {
            let bytes = run wideSvg 400 |> expectPng "wide"
            Expect.equal (dimensionsOf bytes) (400, 100) "4:1 preserved"

            let scaled = run wideSvg 200 |> expectPng "wide scaled"
            Expect.equal (dimensionsOf scaled) (200, 50) "aspect held under scaling"
        }

        test "sizes from width/height when there is no viewBox" {
            let svg =
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"60\" height=\"30\"><rect width=\"60\" height=\"30\" fill=\"black\"/></svg>"

            let bytes = run svg 120 |> expectPng "sized"
            Expect.equal (dimensionsOf bytes) (120, 60) "2:1 preserved"
        }

        test "renders text without failing" {
            // Glyph fidelity depends on the host's font set, which a
            // bare container may not have, so this asserts the text
            // path completes and produces a real image — not what the
            // glyphs look like.
            let bytes = run textSvg 320 |> expectPng "text"
            Expect.equal (fst (dimensionsOf bytes)) 320 "text document rendered at the requested width"
        }

        testList "failures are values, never exceptions" [
            test "a payload that is not XML" {
                match run "this is not an svg <<<" 100 with
                | Ok _ -> failtest "expected Error for a malformed payload"
                | Error reason -> Expect.isNotEmpty reason "a human-readable reason"
            }

            test "an empty payload" {
                match run "" 100 with
                | Ok _ -> failtest "expected Error for an empty payload"
                | Error reason -> Expect.stringContains reason "empty" "names the problem"
            }

            test "XML that is well-formed but not an SVG" {
                match run "<note><to>nobody</to></note>" 100 with
                | Ok _ -> failtest "expected Error for a non-SVG document"
                | Error reason -> Expect.isNotEmpty reason "a human-readable reason"
            }

            test "a non-positive width" {
                for width in [ 0; -10 ] do
                    match run squareSvg width with
                    | Ok _ -> failtestf "expected Error for width %d" width
                    | Error reason -> Expect.stringContains reason "positive" "names the constraint"
            }

            test "a width above the ceiling" {
                match run squareSvg (SkiaSvgRasterizer.MaxWidthPx + 1) with
                | Ok _ -> failtest "expected Error above the ceiling"
                | Error reason -> Expect.stringContains reason "ceiling" "names the constraint"
            }
        ]

        test "the rasteriser is stateless across calls" {
            let first = run wideSvg 200 |> expectPng "first"
            let second = run wideSvg 200 |> expectPng "second"
            Expect.equal (dimensionsOf second) (dimensionsOf first) "same input, same output geometry"
        }

        test "composed through the seam it supplies a figure's fallback part" {
            let block =
                Figures.svgWith (Some rasterizer) wideSvg (Pixels(400, 100))
                |> Async.RunSynchronously

            let docx = Emit.toBytes (DocModel.ofBlocks [ block ])

            use stream = new MemoryStream(docx)
            use document = WordprocessingDocument.Open(stream, false)

            let parts =
                document.MainDocumentPart.Parts
                |> Seq.choose (fun pair ->
                    match pair.OpenXmlPart with
                    | :? ImagePart as part -> Some(pair.RelationshipId, part.ContentType)
                    | _ -> None)
                |> Seq.sortBy fst
                |> List.ofSeq

            Expect.equal
                parts
                [ "rTuFigFbk1", "image/png"; "rTuFigSvg1", "image/svg+xml" ]
                "the vector part plus a rendered raster fallback"
        }
    ]