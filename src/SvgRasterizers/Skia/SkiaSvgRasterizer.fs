// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Skia-backed `ISvgRasterizer` — the first implementation of the
/// Phase 576 seam.
///
/// **Production-ready, not dev-only.** The rendering is in-process and
/// stateless: each call parses its own SVG, renders it onto its own
/// surface and returns the encoded bytes, so the same instance is safe
/// to hold as a singleton across a deployment (GP 12 rule 4). It reads
/// no environment variable, no configuration file and no ambient
/// state.
///
/// **Native dependency.** The rendering engine is native code reached
/// through SkiaSharp (and HarfBuzzSharp for text shaping). SkiaSharp
/// ships Windows and macOS natives in its own package; a Linux host
/// must additionally reference `SkiaSharp.NativeAssets.Linux` (or the
/// `NoDependencies` variant on a container with no fontconfig — note
/// that SVG `<text>` then renders with whatever fonts the image does
/// carry). `create` probes for the native at construction and raises a
/// descriptive error naming the missing package, so an absent RID
/// fails at composition rather than in the middle of a document emit.
module ToolUp.OpenXml.SvgRasterizer.Skia.SkiaSvgRasterizer

open System
open SkiaSharp
open Svg.Skia
open ToolUp.OpenXml

/// Upper bound on the requested raster width, in pixels. A fallback
/// part is a compatibility affordance for old clients, not a print
/// master, and an unbounded width turns a caller's arithmetic slip
/// into an allocation large enough to take the host down. Refused as
/// a value, in keeping with the seam's contract that a rasteriser
/// reports rather than raises.
[<Literal>]
let MaxWidthPx = 20000

/// PNG encode quality. Lossless format, so this selects effort rather
/// than fidelity; 100 is SkiaSharp's own default.
[<Literal>]
let private PngQuality = 100

let private renderToPng (svgText: string) (widthPx: int) : Result<byte[], string> =
    use svg = new SKSvg()

    match svg.FromSvg svgText with
    | null -> Error "the SVG payload parsed but produced no drawable picture"
    | picture ->
        let bounds = picture.CullRect

        if bounds.Width <= 0.0f || bounds.Height <= 0.0f then
            Error(
                sprintf
                    "the SVG declares no positive drawing bounds (width %g, height %g) — it needs a viewBox or width/height"
                    (float bounds.Width)
                    (float bounds.Height)
            )
        else
            // One dimension is declared by the caller; the other follows
            // the document's own aspect ratio, so a fallback can never
            // be produced distorted.
            let scale = float32 widthPx / bounds.Width
            let heightPx = max 1 (int (Math.Round(float (bounds.Height * scale))))

            let info = SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, SKAlphaType.Premul)

            use surface = SKSurface.Create info

            if isNull (box surface) then
                Error(sprintf "could not allocate a %dx%d rendering surface" widthPx heightPx)
            else
                // Transparent, not white: the fallback is the same
                // picture as the vector part, and an SVG that declares
                // no background should not gain one on the way to a
                // raster.
                surface.Canvas.Clear SKColors.Transparent
                surface.Canvas.Scale scale
                surface.Canvas.DrawPicture picture
                surface.Canvas.Flush()

                use image = surface.Snapshot()
                use encoded = image.Encode(SKEncodedImageFormat.Png, PngQuality)

                if isNull (box encoded) then
                    Error "the rendered image could not be encoded as PNG"
                else
                    Ok(encoded.ToArray())

let private rasterize (svgText: string) (widthPx: int) : Result<byte[], string> =
    if String.IsNullOrWhiteSpace svgText then
        Error "the SVG payload is empty"
    elif widthPx <= 0 then
        Error(sprintf "the requested width must be positive, not %d" widthPx)
    elif widthPx > MaxWidthPx then
        Error(sprintf "the requested width %d exceeds the %d pixel ceiling" widthPx MaxWidthPx)
    else
        try
            renderToPng svgText widthPx
        with error ->
            // Malformed SVG surfaces as an XmlException from the
            // parser; anything else is an engine failure. Both are
            // reported as values — the seam's contract is that a
            // figure whose fallback could not be produced still
            // embeds, SVG-only.
            Error(sprintf "SVG rasterisation failed (%s): %s" (error.GetType().Name) error.Message)

[<Sealed>]
type private Rasterizer() =
    interface ISvgRasterizer with
        /// Completes synchronously — the work is CPU-bound and
        /// in-process. The seam is asynchronous so an implementation
        /// that shells a renderer or calls a service is expressible
        /// (GP 12 rule 2); this one simply has nothing to await.
        member _.Rasterize(svg, widthPx) = async { return rasterize svg widthPx }

/// Probe the Skia native at composition time, so a host missing the
/// native asset for its RID learns at `create` rather than at first
/// render — the companion-authoring rule for native-bearing packages.
let private probeNative () =
    try
        use probe = new SKBitmap(1, 1)
        ignore probe.Width
    with
    | :? DllNotFoundException as error ->
        failwithf
            "The SkiaSharp native library is not available for this runtime identifier, so SVG rasterisation cannot start. Reference the native asset package for the host (SkiaSharp.NativeAssets.Linux or SkiaSharp.NativeAssets.Linux.NoDependencies on Linux; SkiaSharp ships the Windows and macOS natives itself). Underlying error: %s"
            error.Message
    | :? TypeInitializationException as error ->
        failwithf
            "The SkiaSharp native library failed to initialise, so SVG rasterisation cannot start. Confirm the native asset package for this runtime identifier is referenced and loadable. Underlying error: %s"
            error.Message

/// Create the rasteriser. Probes the Skia native first and raises a
/// descriptive error when it is missing (see the module header);
/// otherwise returns a stateless instance safe to hold as a singleton.
let create () : ISvgRasterizer =
    probeNative ()
    Rasterizer() :> ISvgRasterizer