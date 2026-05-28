// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System
open System.IO
open SkiaSharp

/// Default `IDerivativeRenderer` — SkiaSharp-backed. Cross-
/// platform, MIT-licensed, fast. Decodes JPEG / PNG / WebP /
/// GIF / BMP; encodes JPEG / PNG / WebP / AVIF (AVIF encode
/// requires SkiaSharp 3.x — bundled with the package version
/// pinned in `Directory.Packages.props`).
///
/// **Native assets.** SkiaSharp ships managed bindings + native
/// libs per RID. Windows / macOS dev hosts pick up the
/// SkiaSharp package's bundled assets automatically; Linux
/// containers must additionally reference
/// `SkiaSharp.NativeAssets.Linux` (not declared on this
/// fsproj — kept host-specific so non-Linux dev doesn't drag
/// in Linux natives).
///
/// **Statelessness.** No fields, no caches. Every `Render` call
/// allocates fresh `SKBitmap` / `SKImage` / `SKData` instances
/// and disposes them via `use` bindings. Distributed framework
/// compatible (GP 12.4).
type SkiaSharpDerivativeRenderer() =

    let toSkiaFormat =
        function
        | Jpeg -> SKEncodedImageFormat.Jpeg
        | Png -> SKEncodedImageFormat.Png
        | Webp -> SKEncodedImageFormat.Webp
        | Avif -> SKEncodedImageFormat.Avif

    /// Resolve the resize target preserving aspect ratio,
    /// never upscaling. `None` on a bound = unconstrained.
    let resolveTarget
        (sourceWidth: int)
        (sourceHeight: int)
        (maxWidth: int option)
        (maxHeight: int option)
        : int * int =
        let w = float sourceWidth
        let h = float sourceHeight

        let scaleWidth =
            match maxWidth with
            | Some mw when float mw < w -> float mw / w
            | _ -> 1.0

        let scaleHeight =
            match maxHeight with
            | Some mh when float mh < h -> float mh / h
            | _ -> 1.0

        let scale = min scaleWidth scaleHeight

        if scale >= 1.0 then
            sourceWidth, sourceHeight
        else
            max 1 (int (round (w * scale))), max 1 (int (round (h * scale)))

    interface IDerivativeRenderer with

        member _.Render(originalBytes, spec) = async {
            try
                use sourceStream = new MemoryStream(originalBytes)
                use source = SKBitmap.Decode sourceStream

                if isNull source then
                    return Error(DecodeFailed "SkiaSharp returned null on Decode")
                else
                    let targetW, targetH =
                        resolveTarget source.Width source.Height spec.MaxWidth spec.MaxHeight

                    let resizeInfo =
                        SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul)

                    use resized =
                        source.Resize(resizeInfo, SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))

                    if isNull resized then
                        return Error(RenderFailed "SkiaSharp resize returned null")
                    else
                        use image = SKImage.FromBitmap resized
                        let format = toSkiaFormat spec.Format
                        let quality = max 0 (min 100 spec.Quality)
                        use data = image.Encode(format, quality)

                        if isNull data || data.Size = 0L then
                            return Error(EncodeFailed(string spec.Format, "encoder returned no bytes"))
                        else
                            return Ok(data.ToArray(), ImageFormat.mimeType spec.Format)
            with ex ->
                return Error(RenderFailed ex.Message)
        }

        member _.Probe(originalBytes) = async {
            try
                use stream = new MemoryStream(originalBytes)
                use codec = SKCodec.Create stream

                if isNull codec then
                    return None
                else
                    let info = codec.Info
                    return Some(info.Width, info.Height)
            with _ ->
                return None
        }