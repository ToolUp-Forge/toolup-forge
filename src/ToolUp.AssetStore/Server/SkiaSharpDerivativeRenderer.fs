// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

open System
open System.IO
open System.Runtime.InteropServices
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
/// That requirement is checked at CONSTRUCTION, not at first
/// render — see the probe below. The companion convention is
/// that an absent RID fails loudly where the operator is
/// composing the app, never at first P/Invoke deep in a
/// request path.
///
/// **Statelessness.** No fields, no caches. Every `Render` call
/// allocates fresh `SKBitmap` / `SKImage` / `SKData` instances
/// and disposes them via `use` bindings. Distributed framework
/// compatible (GP 12.4).
type SkiaSharpDerivativeRenderer() =

    // Force the native load now, and turn the failure into something
    // an operator can act on.
    //
    // Left alone, a missing native surfaces as
    // `TypeInitializationException` wrapping
    // `DllNotFoundException: Unable to load shared library
    // 'libSkiaSharp'` — thrown from a static initialiser at the first
    // pixel operation, i.e. inside `Render`'s `try`, where it is
    // flattened into `RenderFailed "The type initializer for
    // 'SkiaSharp.SKImageInfo' threw an exception."`. The operator
    // sees a derivative that will not render and no mention of the
    // package they are missing. That is the exact shape the
    // native-dependency companion convention exists to forbid, and it
    // is a deployment concern, not only a test one: it is what a
    // Linux container hits in production and what took out ten
    // `IAssetStore` contract cases on a Linux CI runner (Phase 617).
    //
    // The probe is one 1x1 bitmap, once per renderer instance, and a
    // renderer is composed once per app.
    do
        try
            use probe = new SKBitmap(1, 1)
            ignore probe.Width
        with ex ->
            let rid = RuntimeInformation.RuntimeIdentifier

            raise (
                InvalidOperationException(
                    $"SkiaSharpDerivativeRenderer could not load the SkiaSharp native library for "
                    + $"runtime identifier '{rid}'. The managed SkiaSharp package does not carry native "
                    + "assets for every platform: a Linux host additionally needs a "
                    + "'SkiaSharp.NativeAssets.Linux' (or '…Linux.NoDependencies' on a minimal image) "
                    + "PackageReference in the application project, and macOS/Windows hosts need the "
                    + "matching NativeAssets package if they are publishing self-contained. Add the "
                    + "package for this RID, or compose a different IDerivativeRenderer — "
                    + $"ToolUp.AssetStore does not require SkiaSharp specifically. Underlying error: {ex.Message}",
                    ex
                )
            )

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