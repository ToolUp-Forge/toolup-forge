// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AssetStore

/// Pluggable image-renderer surface. The default impl
/// (`SkiaSharpDerivativeRenderer`) covers JPEG / PNG / WebP /
/// AVIF — the four formats `ImageFormat` exposes. Deployments
/// needing wider format coverage (TIFF, HEIC, BMP, SVG
/// rasterisation) plug in the optional
/// `ImageMagickDerivativeRenderer` companion.
///
/// **Six portability rules** (per `CLAUDE.md` —
/// every substrate interface that could plausibly be implemented
/// by a distributed framework must satisfy all six):
///
///   1. **Identity by value.** Inputs / outputs are `byte[]` +
///      `DerivativeSpec` (record of primitives). No `Stream`
///      handles persisted across calls, no live decoder
///      references. ✓
///   2. **Async at every boundary.** Returns `Async<_>`. ✓
///   3. **Retry + supervision as data.** Failure surfaces as a
///      typed `DerivativeRenderError` DU — never an exception
///      escaping the boundary. The default impl catches every
///      SkiaSharp exception and maps it to `RenderFailed`. ✓
///   4. **Stateless between invocations.** Implementations
///      MUST NOT hold per-asset state between calls. The
///      default impl creates a fresh `SKBitmap` /
///      `SKManagedStream` per call and disposes them before
///      returning. ✓
///   5. **No cross-shard ordering promises.** N/A — each call
///      is independent; no ordering contract. ✓
///   6. **Precision at the lower bound.** Pixel dimensions
///      reported as `int`; quality as `int 0-100`. No sub-pixel
///      promises that wouldn't survive a third-party renderer. ✓
type IDerivativeRenderer =
    /// Decode `originalBytes`, produce a derivative matching
    /// `spec`. Implementations preserve aspect ratio within
    /// the spec's `MaxWidth` / `MaxHeight` bounding box and
    /// never upscale (smaller-than-target originals pass
    /// through at native resolution). Returns the encoded
    /// bytes + the MIME type matching `spec.Format`.
    abstract Render:
        originalBytes: byte[] * spec: DerivativeSpec -> Async<Result<byte[] * string, DerivativeRenderError>>

    /// Cheap pixel-dimension probe — decodes the header only,
    /// not the full bitmap. Used at upload time to populate
    /// `AssetRecord.Width` / `Height` without the cost of a
    /// full decode. Returns `(width, height)` or `None` if the
    /// header is unrecognised / corrupt.
    abstract Probe: originalBytes: byte[] -> Async<(int * int) option>

/// Why a render call failed. Discriminated, not stringly-typed.
and DerivativeRenderError =
    /// Source bytes failed to decode (corrupt header,
    /// unsupported codec, truncated payload).
    | DecodeFailed of message: string
    /// Renderer raised mid-resize (out-of-memory on a giant
    /// source, native-asset misconfiguration on the host).
    | RenderFailed of message: string
    /// Encoder rejected the target format (AVIF encoder
    /// missing on this SkiaSharp build, etc.).
    | EncodeFailed of format: string * message: string