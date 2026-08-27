// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 576 — figure emission: raster images, native SVG parts, the
/// `ISvgRasterizer` seam, and the two properties that make the whole
/// thing safe to build on — byte determinism, and the base package
/// carrying no rendering engine.
module ToolUp.OpenXml.Tests.InProcess.FigureTests

open System
open System.IO
open System.Text
open System.Xml.Linq
open Expecto
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing
open ToolUp.OpenXml

// ─── Payload fixtures ────────────────────────────────────────────
//
// Header-shaped rather than decoded: nothing under test decodes an
// image, and a committed binary fixture would buy nothing the bytes
// below do not.

let private bigEndian32 (value: int) = [| byte (value >>> 24); byte (value >>> 16); byte (value >>> 8); byte value |]

let private bigEndian16 (value: int) = [| byte (value >>> 8); byte value |]

/// A PNG whose IHDR declares the given dimensions.
let private pngBytes (widthPx: int) (heightPx: int) : byte[] =
    Array.concat [
        [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
        bigEndian32 13
        Encoding.ASCII.GetBytes "IHDR"
        bigEndian32 widthPx
        bigEndian32 heightPx
        // bit depth, colour type, compression, filter, interlace
        [| 8uy; 6uy; 0uy; 0uy; 0uy |]
        // CRC placeholder — nothing under test verifies it.
        bigEndian32 0
    ]

/// A JPEG carrying an APP0 segment and then a baseline SOF0 declaring
/// the given dimensions, so the marker walk has a segment to skip
/// before it finds the frame header.
let private jpegBytes (widthPx: int) (heightPx: int) : byte[] =
    Array.concat [
        [| 0xFFuy; 0xD8uy |]
        [| 0xFFuy; 0xE0uy |]
        bigEndian16 16
        [| 0x4Auy; 0x46uy; 0x49uy; 0x46uy; 0x00uy |]
        Array.zeroCreate 9
        [| 0xFFuy; 0xC0uy |]
        bigEndian16 11
        [| 8uy |]
        bigEndian16 heightPx
        bigEndian16 widthPx
        [| 1uy; 1uy; 0x11uy; 0uy |]
    ]

let private chartSvg =
    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 240 120\"><rect width=\"240\" height=\"120\" fill=\"#eee\"/><text x=\"8\" y=\"64\">revenue</text></svg>"

// ─── Reopen helpers ──────────────────────────────────────────────

let private relationshipsNs =
    XNamespace.Get "http://schemas.openxmlformats.org/officeDocument/2006/relationships"

let private svgExtensionNs =
    XNamespace.Get "http://schemas.microsoft.com/office/drawing/2016/SVG/main"

let private drawingNs =
    XNamespace.Get "http://schemas.openxmlformats.org/drawingml/2006/main"

let private wordDrawingNs =
    XNamespace.Get "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"

let private withPackage (bytes: byte[]) (act: MainDocumentPart -> 'a) : 'a =
    use stream = new MemoryStream(bytes)
    use document = WordprocessingDocument.Open(stream, false)
    act document.MainDocumentPart

/// Every `w:drawing` in the emitted body, parsed as XML.
let private drawingsOf (bytes: byte[]) : XElement list =
    withPackage bytes (fun main ->
        main.Document.Body.Descendants<Drawing>()
        |> Seq.map (fun drawing -> XElement.Parse drawing.OuterXml)
        |> List.ofSeq)

let private imagePartsOf (bytes: byte[]) : (string * string * byte[]) list =
    withPackage bytes (fun main ->
        main.Parts
        |> Seq.choose (fun pair ->
            match pair.OpenXmlPart with
            | :? ImagePart as part ->
                use stream = part.GetStream()
                use buffer = new MemoryStream()
                stream.CopyTo buffer
                Some(pair.RelationshipId, part.ContentType, buffer.ToArray())
            | _ -> None)
        |> List.ofSeq)

let private blipEmbed (drawing: XElement) : string option =
    drawing.Descendants(drawingNs + "blip")
    |> Seq.tryHead
    |> Option.bind (fun blip ->
        match blip.Attribute(relationshipsNs + "embed") with
        | null -> None
        | found -> Some found.Value)

let private svgBlipEmbed (drawing: XElement) : string option =
    drawing.Descendants(svgExtensionNs + "svgBlip")
    |> Seq.tryHead
    |> Option.bind (fun blip ->
        match blip.Attribute(relationshipsNs + "embed") with
        | null -> None
        | found -> Some found.Value)

let private extentOf (drawing: XElement) : int64 * int64 =
    let extent = drawing.Descendants(wordDrawingNs + "extent") |> Seq.head
    int64 (extent.Attribute(XName.Get "cx").Value), int64 (extent.Attribute(XName.Get "cy").Value)

let private docPrAttribute (name: string) (drawing: XElement) : string option =
    drawing.Descendants(wordDrawingNs + "docPr")
    |> Seq.tryHead
    |> Option.bind (fun docPr ->
        match docPr.Attribute(XName.Get name) with
        | null -> None
        | found -> Some found.Value)

let private emit (blocks: Block list) : byte[] = Emit.toBytes (DocModel.ofBlocks blocks)

/// The main document part's XML bytes. Package-level byte equality is
/// deliberately NOT the assertion: OPC ZIP entries carry a wall-clock
/// timestamp, so two emits differ in the container regardless of what
/// they contain. The parts are where the figure lives.
let private mainPartBytes (bytes: byte[]) : byte[] =
    withPackage bytes (fun main ->
        use stream = main.GetStream()
        use buffer = new MemoryStream()
        stream.CopyTo buffer
        buffer.ToArray())

// ─── Rasteriser doubles ──────────────────────────────────────────

let private stubPng = pngBytes 640 320

type private OkRasterizer(recorded: ResizeArray<string * int>) =
    interface ISvgRasterizer with
        member _.Rasterize(svg, widthPx) = async {
            recorded.Add(svg, widthPx)
            return Ok stubPng
        }

type private FailingRasterizer() =
    interface ISvgRasterizer with
        member _.Rasterize(_, _) = async { return Error "no rendering engine on this host" }

type private ThrowingRasterizer() =
    interface ISvgRasterizer with
        member _.Rasterize(_, _) = async { return failwith "the renderer blew up" }

type private EmptyRasterizer() =
    interface ISvgRasterizer with
        member _.Rasterize(_, _) = async { return Ok Array.empty<byte> }

// ─── Tests ───────────────────────────────────────────────────────

let tests =
    testList "figures" [
        testList "sizing" [
            test "svg viewBox drives the intrinsic size" {
                Expect.equal (Figures.svgIntrinsicSize chartSvg) (240, 120) "viewBox extents"
            }

            test "svg width/height are read when there is no viewBox" {
                let svg =
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"320px\" height=\"200\"><rect/></svg>"

                Expect.equal (Figures.svgIntrinsicSize svg) (320, 200) "width/height attributes"
            }

            test "a viewBox wins over width/height" {
                let svg =
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"50\" height=\"50\" viewBox=\"0 0 400 100\"><rect/></svg>"

                Expect.equal (Figures.svgIntrinsicSize svg) (400, 100) "viewBox is authoritative"
            }

            test "units this module does not interpret fall back to the default" {
                let svg =
                    "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10cm\" height=\"4cm\"><rect/></svg>"

                Expect.equal (Figures.svgIntrinsicSize svg) Figures.defaultSize "cm is not interpreted"
            }

            test "a payload that is not well-formed XML is still sizeable" {
                Expect.equal (Figures.svgIntrinsicSize "<svg garbage") Figures.defaultSize "default size, no throw"
                Expect.equal (Figures.svgIntrinsicSize "") Figures.defaultSize "empty payload"
            }

            test "png dimensions are read from IHDR" {
                Expect.equal (Figures.rasterIntrinsicSize (pngBytes 800 600)) (Some(800, 600)) "IHDR width/height"
            }

            test "jpeg dimensions are read from the frame header" {
                Expect.equal (Figures.rasterIntrinsicSize (jpegBytes 133 77)) (Some(133, 77)) "SOF0 width/height"
            }

            test "an unreadable raster payload declares no size" {
                Expect.isNone (Figures.rasterIntrinsicSize [| 1uy; 2uy; 3uy; 4uy |]) "not a recognised header"
                Expect.isNone (Figures.rasterIntrinsicSize Array.empty) "empty payload"
            }

            test "pixels lower to EMU at 96 dpi" {
                let figure = {
                    Content = RasterImage(pngBytes 10 10, "image/png")
                    Size = Pixels(100, 50)
                    Name = "f"
                    Description = None
                }

                Expect.equal (Figures.extents figure) (952500L, 476250L) "100x50 px at 9525 EMU per px"
            }

            test "an explicit EMU size is honoured verbatim" {
                let figure = {
                    Content = VectorSvg(chartSvg, None)
                    Size = Emu(1234567L, 89L)
                    Name = "f"
                    Description = None
                }

                Expect.equal (Figures.extents figure) (1234567L, 89L) "EMU override"
            }

            test "no declared size can produce a zero-extent drawing" {
                let zeroPixels = {
                    Content = VectorSvg(chartSvg, None)
                    Size = Pixels(0, -4)
                    Name = "f"
                    Description = None
                }

                Expect.equal (Figures.extents zeroPixels) (9525L, 9525L) "clamped to one pixel"

                let zeroEmu = { zeroPixels with Size = Emu(0L, 0L) }
                Expect.equal (Figures.extents zeroEmu) (1L, 1L) "clamped to one EMU"
            }
        ]

        testList "raster emission" [
            test "an image block emits a drawing referencing one image part" {
                let bytes = emit [ Figures.image (pngBytes 40 20) "image/png" (Pixels(40, 20)) ]

                let parts = imagePartsOf bytes
                Expect.hasLength parts 1 "exactly one image part"
                let relationshipId, contentType, payload = parts.Head
                Expect.equal relationshipId "rTuFigImg1" "explicit relationship id"
                Expect.equal contentType "image/png" "png content type"
                Expect.equal payload (pngBytes 40 20) "payload carried verbatim"

                let drawings = drawingsOf bytes
                Expect.hasLength drawings 1 "one drawing"
                Expect.equal (blipEmbed drawings.Head) (Some "rTuFigImg1") "blip points at the image part"
                Expect.isNone (svgBlipEmbed drawings.Head) "no svgBlip extension on a raster figure"
                Expect.equal (extentOf drawings.Head) (381000L, 190500L) "40x20 px in EMU"
            }

            test "the mime type selects the image part's content type" {
                let bytes = emit [ Figures.image (jpegBytes 8 8) "image/jpeg" (Pixels(8, 8)) ]
                let _, contentType, _ = (imagePartsOf bytes).Head
                Expect.equal contentType "image/jpeg" "jpeg content type"
            }

            test "an unrecognised mime type embeds as png rather than failing the emit" {
                let bytes = emit [ Figures.image (pngBytes 8 8) "image/heic" (Pixels(8, 8)) ]
                let _, contentType, _ = (imagePartsOf bytes).Head
                Expect.equal contentType "image/png" "falls back to png"
            }

            test "intrinsic sizing reads the raster header" {
                let bytes = emit [ Figures.image (pngBytes 64 16) "image/png" Intrinsic ]
                Expect.equal (extentOf (drawingsOf bytes).Head) (609600L, 152400L) "64x16 px in EMU"
            }
        ]

        testList "svg emission" [
            test "an svg block embeds the payload verbatim as an svg part" {
                let bytes = emit [ Figures.svg chartSvg Intrinsic None ]

                let parts = imagePartsOf bytes
                Expect.hasLength parts 1 "one part — svg only, no fallback"
                let relationshipId, contentType, payload = parts.Head
                Expect.equal relationshipId "rTuFigSvg1" "explicit relationship id"
                Expect.equal contentType "image/svg+xml" "native svg part"

                Expect.equal payload (UTF8Encoding(false).GetBytes chartSvg) "utf-8 with no BOM, byte for byte"

                Expect.isFalse
                    (payload.Length >= 3
                     && payload[0] = 0xEFuy
                     && payload[1] = 0xBBuy
                     && payload[2] = 0xBFuy)
                    "no byte-order mark"
            }

            test "with no fallback the blip points at the svg part itself" {
                let drawing = (drawingsOf (emit [ Figures.svg chartSvg Intrinsic None ])).Head
                Expect.equal (blipEmbed drawing) (Some "rTuFigSvg1") "blip resolves to the svg part"
                Expect.equal (svgBlipEmbed drawing) (Some "rTuFigSvg1") "svgBlip names the vector source"
                Expect.equal (extentOf drawing) (2286000L, 1143000L) "240x120 viewBox units in EMU"
            }

            test "a fallback adds a png part the blip resolves to" {
                let bytes = emit [ Figures.svg chartSvg (Pixels(240, 120)) (Some stubPng) ]

                let parts =
                    imagePartsOf bytes |> List.sortBy (fun (relationshipId, _, _) -> relationshipId)

                Expect.hasLength parts 2 "svg part plus fallback part"

                let ids = parts |> List.map (fun (relationshipId, _, _) -> relationshipId)
                Expect.equal ids [ "rTuFigFbk1"; "rTuFigSvg1" ] "both ids explicit"

                let contentTypes = parts |> List.map (fun (_, contentType, _) -> contentType)
                Expect.equal contentTypes [ "image/png"; "image/svg+xml" ] "png fallback beside the svg"

                let drawing = (drawingsOf bytes).Head
                Expect.equal (blipEmbed drawing) (Some "rTuFigFbk1") "older clients get the raster"
                Expect.equal (svgBlipEmbed drawing) (Some "rTuFigSvg1") "2016+ clients get the vector"
            }

            test "an empty fallback payload is treated as no fallback" {
                let bytes = emit [ Figures.svg chartSvg Intrinsic (Some Array.empty) ]
                Expect.hasLength (imagePartsOf bytes) 1 "no empty part attached"
                Expect.equal (blipEmbed (drawingsOf bytes).Head) (Some "rTuFigSvg1") "blip falls back to the svg part"
            }
        ]

        testList "identity" [
            test "name and description reach docPr" {
                let block =
                    Figures.svgNamed "Revenue chart" (Some "Quarterly revenue, rising") chartSvg Intrinsic None

                let drawing = (drawingsOf (emit [ block ])).Head
                Expect.equal (docPrAttribute "name" drawing) (Some "Revenue chart") "selection-pane name"
                Expect.equal (docPrAttribute "descr" drawing) (Some "Quarterly revenue, rising") "alt text"
            }

            test "no description writes no descr attribute" {
                let drawing = (drawingsOf (emit [ Figures.svg chartSvg Intrinsic None ])).Head
                Expect.isNone (docPrAttribute "descr" drawing) "attribute absent, not empty"
            }

            test "a name needing escaping does not break the drawing XML" {
                let block = Figures.svgNamed "a & b <c>" (Some "\"quoted\"") chartSvg Intrinsic None
                let drawing = (drawingsOf (emit [ block ])).Head
                Expect.equal (docPrAttribute "name" drawing) (Some "a & b <c>") "round-trips through escaping"
            }

            test "a figure's block text is its accessible description" {
                let described =
                    Figures.svgNamed "chart" (Some "Revenue rising") chartSvg Intrinsic None

                Expect.equal (Block.text described) "Revenue rising" "alt text is the extractable text"
                Expect.equal (Block.text (Figures.svg chartSvg Intrinsic None)) "" "no alt text, no text"
            }
        ]

        testList "determinism" [
            // The load-bearing one. `AddImagePart` without an explicit
            // id mints a RANDOM relationship id which lands verbatim in
            // the drawing XML, so an emitter that let the SDK choose
            // would render correctly and hash differently every run —
            // green under review, broken for any content-addressed or
            // golden-file consumer downstream.
            test "the same model emits byte-identical document and image parts" {
                let model () = [
                    Figures.svgNamed "chart" (Some "alt") chartSvg (Pixels(240, 120)) (Some stubPng)
                    Figures.image (pngBytes 40 20) "image/png" Intrinsic
                ]

                let first = emit (model ())
                let second = emit (model ())

                Expect.equal (mainPartBytes second) (mainPartBytes first) "main document part is byte-identical"

                Expect.equal
                    (imagePartsOf second |> List.sortBy (fun (id, _, _) -> id))
                    (imagePartsOf first |> List.sortBy (fun (id, _, _) -> id))
                    "image parts and their relationship ids are byte-identical"
            }

            test "no figure relationship id is SDK-generated" {
                let bytes =
                    emit [
                        Figures.svg chartSvg Intrinsic (Some stubPng)
                        Figures.image (pngBytes 4 4) "image/png" Intrinsic
                    ]

                for relationshipId, _, _ in imagePartsOf bytes do
                    Expect.stringStarts relationshipId "rTuFig" (sprintf "explicit id, got %s" relationshipId)

                // The SDK's generator emits `R` followed by 16 hex
                // characters; assert against the shape rather than the
                // prefix, since `rTuFig…` starts with an r too.
                let sdkGenerated =
                    Text.RegularExpressions.Regex(
                        @"r:embed=""R[0-9a-f]{16}""",
                        Text.RegularExpressions.RegexOptions.IgnoreCase
                    )

                for drawing in drawingsOf bytes do
                    Expect.isFalse
                        (sdkGenerated.IsMatch(drawing.ToString()))
                        "no randomly-generated relationship id in the drawing XML"
            }

            test "figures are numbered in document order" {
                let bytes =
                    emit [
                        Figures.svg chartSvg Intrinsic None
                        Figures.image (pngBytes 4 4) "image/png" Intrinsic
                        Figures.svg chartSvg Intrinsic (Some stubPng)
                    ]

                let ids = imagePartsOf bytes |> List.map (fun (id, _, _) -> id) |> List.sort

                Expect.equal ids [ "rTuFigFbk3"; "rTuFigImg2"; "rTuFigSvg1"; "rTuFigSvg3" ] "one ordinal per figure"

                let docPrIds = drawingsOf bytes |> List.map (docPrAttribute "id")
                Expect.equal docPrIds [ Some "1"; Some "2"; Some "3" ] "docPr ids unique and in order"
            }

            test "figures nested in table cells share the same ordinal sequence" {
                // Annotated: `{ Blocks; RawProperties }` is also the
                // shape of `Section`, and inference picks the last
                // declared match.
                let cell: TableCell = {
                    Blocks = [ Figures.svg chartSvg Intrinsic None ]
                    RawProperties = None
                }

                let bytes =
                    emit [
                        Table {
                            Rows = [
                                {
                                    Cells = [ cell ]
                                    RawProperties = None
                                }
                            ]
                            RawProperties = None
                            RawGrid = None
                        }
                        Figures.image (pngBytes 4 4) "image/png" Intrinsic
                    ]

                let ids = imagePartsOf bytes |> List.map (fun (id, _, _) -> id) |> List.sort
                Expect.equal ids [ "rTuFigImg2"; "rTuFigSvg1" ] "the nested figure took ordinal 1"
            }
        ]

        testList "the rasteriser seam" [
            test "no rasteriser composed yields the svg-only embed" {
                let block =
                    Figures.svgWith None chartSvg (Pixels(240, 120)) |> Async.RunSynchronously

                Expect.equal block (Figures.svg chartSvg (Pixels(240, 120)) None) "identical to the direct construction"
                Expect.hasLength (imagePartsOf (emit [ block ])) 1 "one part only"
            }

            test "a composed rasteriser supplies the fallback part" {
                let recorded = ResizeArray()

                let block =
                    Figures.svgWith (Some(OkRasterizer recorded)) chartSvg (Pixels(480, 240))
                    |> Async.RunSynchronously

                Expect.equal (List.ofSeq recorded) [ chartSvg, 480 ] "asked for the declared width, payload verbatim"
                Expect.hasLength (imagePartsOf (emit [ block ])) 2 "svg part plus fallback"
            }

            test "the fallback width follows the declared size" {
                let widthFor size =
                    let recorded = ResizeArray()

                    Figures.svgWith (Some(OkRasterizer recorded)) chartSvg size
                    |> Async.RunSynchronously
                    |> ignore

                    snd recorded[0]

                Expect.equal (widthFor Intrinsic) 240 "intrinsic width from the viewBox"
                Expect.equal (widthFor (Emu(2286000L, 1143000L))) 240 "EMU converted back to pixels"
                Expect.equal (widthFor (Pixels(96, 48))) 96 "declared pixel width"
            }

            test "a rasteriser that reports failure costs the fallback, never the figure" {
                let block =
                    Figures.svgWith (Some(FailingRasterizer())) chartSvg Intrinsic
                    |> Async.RunSynchronously

                Expect.equal block (Figures.svg chartSvg Intrinsic None) "degrades to svg-only"
            }

            test "a rasteriser that raises costs the fallback, never the figure" {
                let block =
                    Figures.svgWith (Some(ThrowingRasterizer())) chartSvg Intrinsic
                    |> Async.RunSynchronously

                Expect.equal block (Figures.svg chartSvg Intrinsic None) "the exception does not escape"
            }

            test "a rasteriser returning no bytes is treated as no fallback" {
                let block =
                    Figures.svgWith (Some(EmptyRasterizer())) chartSvg Intrinsic
                    |> Async.RunSynchronously

                Expect.equal block (Figures.svg chartSvg Intrinsic None) "empty payload is not a part"
            }

            test "named construction through the seam keeps the identity" {
                let block =
                    Figures.svgNamedWith (Some(OkRasterizer(ResizeArray()))) "Chart" (Some "alt") chartSvg Intrinsic
                    |> Async.RunSynchronously

                match block with
                | Figure figure ->
                    Expect.equal figure.Name "Chart" "name preserved"
                    Expect.equal figure.Description (Some "alt") "description preserved"
                | other -> failtestf "expected a figure, got %A" other
            }
        ]

        testList "the base package carries no rendering engine" [
            // The strip-imports proof. This pack references
            // `ToolUp.OpenXml` and nothing else that could render an
            // SVG, so the whole suite passing IS the evidence that the
            // figure surface builds and behaves with no companion
            // present. This case pins the property against the assembly
            // itself, so it cannot be undone by a future project
            // reference somewhere else in the graph.
            test "ToolUp.OpenXml references no rendering or native-bearing assembly" {
                let referenced =
                    typeof<ISvgRasterizer>.Assembly.GetReferencedAssemblies()
                    |> Array.map _.Name
                    |> Array.filter (isNull >> not)

                let forbidden = [ "SkiaSharp"; "HarfBuzzSharp"; "Svg.Skia"; "Svg.Model"; "Svg.Custom" ]

                for name in forbidden do
                    Expect.isFalse
                        (referenced |> Array.exists (fun referencedName -> referencedName = name))
                        (sprintf "ToolUp.OpenXml must not reference %s — it is an opt-in companion's dependency" name)
            }

            test "an svg figure emits and reopens with no rasteriser in the process" {
                let bytes = emit [ Figures.svg chartSvg Intrinsic None ]
                let _, contentType, payload = (imagePartsOf bytes).Head
                Expect.equal contentType "image/svg+xml" "a native svg part, produced without rendering anything"
                Expect.isGreaterThan payload.Length 0 "payload present"
            }
        ]
    ]