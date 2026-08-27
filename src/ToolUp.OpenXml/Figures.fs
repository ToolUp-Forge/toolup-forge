// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Figure construction and sizing — the model half of Phase 576.
///
/// This module builds `Block.Figure` values and answers the one
/// question emission needs of them: what extents, in EMU, does this
/// figure occupy. It is deliberately free of any OpenXml dependency —
/// the drawing XML and the image parts are `Emit`'s business — so a
/// caller can construct, inspect and test figures without opening a
/// package.
///
/// Sizing is 96 dpi throughout: one CSS pixel (and one SVG user unit)
/// is 9525 EMU, so a payload's own declared dimensions lower to the
/// page at the size a browser would give them. `FigureSize.Emu` is the
/// escape for a caller that already knows the page geometry.
module ToolUp.OpenXml.Figures

open System
open System.Globalization
open System.Xml.Linq

/// EMU per pixel at 96 dpi — 914400 EMU per inch divided by 96. The
/// OOXML drawing extents (`wp:extent`, `a:ext`) are EMU.
[<Literal>]
let EmuPerPixel = 9525L

/// The SVG specification's default replaced-element size, used when a
/// payload declares nothing this module can read. Never zero: a
/// zero-extent drawing is invisible rather than obviously wrong.
let defaultSize = 300, 150

/// The name a figure carries when the caller does not supply one.
[<Literal>]
let DefaultName = "Figure"

let private culture = CultureInfo.InvariantCulture

// ─── SVG intrinsic sizing ────────────────────────────────────────

let private parseLength (value: string) : float option =
    let trimmed = value.Trim()

    let bare =
        if trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase) then
            trimmed.Substring(0, trimmed.Length - 2)
        else
            trimmed

    match Double.TryParse(bare, NumberStyles.Float, culture) with
    | true, parsed when parsed > 0.0 -> Some parsed
    // A unit this module does not interpret (in / cm / % / em) falls
    // through, so the viewBox — which is always in user units — wins.
    | _ -> None

let private roundToPixels (value: float) : int = max 1 (int (Math.Round value))

/// The pixel size an SVG declares for itself: the `viewBox` extents
/// first (the authoritative user-unit box a deterministic renderer
/// emits), then `width` / `height` when unitless or `px`, else
/// `defaultSize`. User units map 1:1 to CSS pixels, so the result
/// lowers to EMU at 96 dpi exactly as a raster figure's pixels do.
///
/// A payload that is not well-formed XML is still embeddable verbatim
/// — the caller owns the bytes — it is simply not measurable, so it
/// takes the default size rather than failing the construction.
let svgIntrinsicSize (svgText: string) : int * int =
    if String.IsNullOrWhiteSpace svgText then
        defaultSize
    else
        try
            let root = XDocument.Parse(svgText).Root

            let attribute (name: string) =
                match root.Attribute(XName.Get name) with
                | null -> None
                | found -> Some found.Value

            let fromViewBox =
                attribute "viewBox"
                |> Option.bind (fun box ->
                    let parts =
                        box.Split([| ' '; ','; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

                    if parts.Length = 4 then
                        match parseLength parts[2], parseLength parts[3] with
                        | Some width, Some height -> Some(roundToPixels width, roundToPixels height)
                        | _ -> None
                    else
                        None)

            let fromAttributes =
                match attribute "width" |> Option.bind parseLength, attribute "height" |> Option.bind parseLength with
                | Some width, Some height -> Some(roundToPixels width, roundToPixels height)
                | _ -> None

            fromViewBox |> Option.orElse fromAttributes |> Option.defaultValue defaultSize
        with _ ->
            defaultSize

// ─── Raster intrinsic sizing ─────────────────────────────────────
//
// Header reads only — enough to answer `FigureSize.Intrinsic` for the
// formats `Figures.image` accepts, and nothing more. Decoding a whole
// image to learn its dimensions would drag a codec (and a native
// payload) into a package whose whole point is not to carry one.

let private bigEndianInt32 (bytes: byte[]) (offset: int) : int =
    (int bytes[offset] <<< 24)
    ||| (int bytes[offset + 1] <<< 16)
    ||| (int bytes[offset + 2] <<< 8)
    ||| int bytes[offset + 3]

let private bigEndianInt16 (bytes: byte[]) (offset: int) : int =
    (int bytes[offset] <<< 8) ||| int bytes[offset + 1]

let private pngSignature = [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]

/// PNG: the 8-byte signature, then the IHDR chunk whose width and
/// height are the first two big-endian 32-bit fields of its payload.
let private pngSize (bytes: byte[]) : (int * int) option =
    if
        bytes.Length >= 24
        && bytes[0..7] = pngSignature
        && bytes[12] = byte 'I'
        && bytes[13] = byte 'H'
        && bytes[14] = byte 'D'
        && bytes[15] = byte 'R'
    then
        let width = bigEndianInt32 bytes 16
        let height = bigEndianInt32 bytes 20

        if width > 0 && height > 0 then
            Some(width, height)
        else
            None
    else
        None

/// JPEG: walk the marker segments from SOI to the first start-of-frame
/// marker, whose payload carries height then width as big-endian
/// 16-bit fields after the one-byte sample precision.
let private jpegSize (bytes: byte[]) : (int * int) option =
    let isStartOfFrame (marker: byte) =
        let value = int marker
        // C0..CF are the frame headers, less the three that are not:
        // C4 define-Huffman-table, C8 JPEG extensions, CC define-
        // arithmetic-coding.
        value >= 0xC0
        && value <= 0xCF
        && value <> 0xC4
        && value <> 0xC8
        && value <> 0xCC

    let isStandalone (marker: byte) =
        let value = int marker
        value = 0x01 || (value >= 0xD0 && value <= 0xD9)

    let rec scan (index: int) : (int * int) option =
        if index + 1 >= bytes.Length || bytes[index] <> 0xFFuy then
            None
        else
            let marker = bytes[index + 1]

            if marker = 0xFFuy then
                // Fill bytes are legal padding before a marker.
                scan (index + 1)
            elif isStandalone marker then
                scan (index + 2)
            elif index + 3 >= bytes.Length then
                None
            else
                let segmentLength = bigEndianInt16 bytes (index + 2)

                if segmentLength < 2 then
                    None
                elif isStartOfFrame marker then
                    if index + 8 < bytes.Length then
                        let height = bigEndianInt16 bytes (index + 5)
                        let width = bigEndianInt16 bytes (index + 7)

                        if width > 0 && height > 0 then
                            Some(width, height)
                        else
                            None
                    else
                        None
                else
                    scan (index + 2 + segmentLength)

    if bytes.Length >= 4 && bytes[0] = 0xFFuy && bytes[1] = 0xD8uy then
        scan 2
    else
        None

/// The pixel dimensions a raster payload declares in its own header —
/// PNG and JPEG are read; any other format returns `None` and takes
/// `defaultSize` under `FigureSize.Intrinsic`.
let rasterIntrinsicSize (bytes: byte[]) : (int * int) option =
    if isNull bytes then
        None
    else
        match pngSize bytes with
        | Some size -> Some size
        | None -> jpegSize bytes

// ─── Extents ─────────────────────────────────────────────────────

/// The pixel size a figure's payload declares for itself.
let intrinsicSize (content: FigureContent) : int * int =
    match content with
    | RasterImage(bytes, _) -> rasterIntrinsicSize bytes |> Option.defaultValue defaultSize
    | VectorSvg(svgText, _) -> svgIntrinsicSize svgText

/// The figure's drawing extents in EMU — what `wp:extent` and `a:ext`
/// carry. Always at least one EMU on each axis, so no declared size
/// can produce a zero-extent (invisible) drawing.
let extents (figure: FigureModel) : int64 * int64 =
    let ofPixels (widthPx: int) (heightPx: int) =
        int64 (max 1 widthPx) * EmuPerPixel, int64 (max 1 heightPx) * EmuPerPixel

    match figure.Size with
    | Pixels(widthPx, heightPx) -> ofPixels widthPx heightPx
    | Emu(cx, cy) -> max 1L cx, max 1L cy
    | Intrinsic ->
        let widthPx, heightPx = intrinsicSize figure.Content
        ofPixels widthPx heightPx

// ─── Construction ────────────────────────────────────────────────

/// A raster figure carrying a name and an accessible description.
///
/// `mimeType` selects the image part's content type: `image/png`,
/// `image/jpeg` (or `image/jpg`), `image/gif`, `image/bmp` and
/// `image/tiff` are recognised; anything else is embedded as a PNG
/// part, which is the shape a caller supplying an unrecognised type
/// almost always meant.
let imageNamed
    (name: string)
    (description: string option)
    (bytes: byte[])
    (mimeType: string)
    (size: FigureSize)
    : Block =
    Figure {
        Content = RasterImage(bytes, mimeType)
        Size = size
        Name = name
        Description = description
    }

/// A raster figure at the default name, with no alt text.
let image (bytes: byte[]) (mimeType: string) (size: FigureSize) : Block =
    imageNamed DefaultName None bytes mimeType size

/// An SVG figure carrying a name and an accessible description.
///
/// The SVG text is embedded verbatim; `pngFallback`, when supplied,
/// becomes a second part that clients predating the `svgBlip`
/// extension render instead. Passing `None` is not a degraded mode —
/// it is the vector-only embed a current Office client renders
/// natively.
let svgNamed
    (name: string)
    (description: string option)
    (svgText: string)
    (size: FigureSize)
    (pngFallback: byte[] option)
    : Block =
    Figure {
        Content = VectorSvg(svgText, pngFallback)
        Size = size
        Name = name
        Description = description
    }

/// An SVG figure at the default name, with no alt text.
let svg (svgText: string) (size: FigureSize) (pngFallback: byte[] option) : Block =
    svgNamed DefaultName None svgText size pngFallback

// ─── The rasteriser seam, applied ────────────────────────────────

/// The width, in pixels, a fallback raster should be produced at for
/// this figure — its declared extents converted back from EMU where
/// the caller declared them that way.
let private fallbackWidthPx (content: FigureContent) (size: FigureSize) : int =
    match size with
    | Pixels(widthPx, _) -> max 1 widthPx
    | Emu(cx, _) -> max 1 (int (cx / EmuPerPixel))
    | Intrinsic -> intrinsicSize content |> fst

/// An SVG figure with a PNG fallback produced through the seam, when
/// one is composed.
///
/// `None` — no rasteriser registered — yields the SVG-only embed,
/// never an error. So does a rasteriser that returns `Error`, and so
/// does one that raises: a fallback is an enhancement for older
/// clients, and losing it must never cost the caller the figure. The
/// failure reason is deliberately swallowed here rather than logged,
/// because this package has no logging seam; a caller that needs to
/// know calls `ISvgRasterizer.Rasterize` itself and passes the result
/// to `svgNamed`.
let svgNamedWith
    (rasterizer: ISvgRasterizer option)
    (name: string)
    (description: string option)
    (svgText: string)
    (size: FigureSize)
    : Async<Block> =
    async {
        match rasterizer with
        | None -> return svgNamed name description svgText size None
        | Some engine ->
            let widthPx = fallbackWidthPx (VectorSvg(svgText, None)) size

            let! rasterised = async {
                try
                    return! engine.Rasterize(svgText, widthPx)
                with error ->
                    return Error error.Message
            }

            let fallback =
                match rasterised with
                | Ok png when not (isNull png) && png.Length > 0 -> Some png
                | Ok _
                | Error _ -> None

            return svgNamed name description svgText size fallback
    }

/// An SVG figure at the default name, with a PNG fallback produced
/// through the seam when one is composed.
let svgWith (rasterizer: ISvgRasterizer option) (svgText: string) (size: FigureSize) : Async<Block> =
    svgNamedWith rasterizer DefaultName None svgText size