// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.OpenXml

// ─── Phase 576 — the SVG rasterisation seam ──────────────────────
//
// An SVG figure embeds vector-first: the `svgBlip` extension part is
// what a 2016-or-later Office client renders, and it needs no
// rasteriser at all. The PNG fallback part is for the clients that
// predate the extension, and producing it is the one step this
// package cannot do without a rendering engine — so it sits behind
// this seam and nowhere else.
//
// The seam is OPTIONAL by construction, not by convention: every
// entry point that takes a rasteriser takes an `option`, and `None`
// yields the SVG-only embed rather than an error (GP 13 — a
// deployment that does not compose one pays nothing, and
// `ToolUp.OpenXml` itself carries no native dependency). A rasteriser
// that FAILS is treated the same way: the figure loses its fallback,
// never its picture.

/// Renders an SVG document to PNG bytes, supplying the optional
/// fallback part of an SVG figure. A companion package provides an
/// implementation; the base package ships none.
///
/// **Portability audit (GP 12).** Rule 1 — identity by value: the
/// payload crosses as a `string` and comes back as `byte[]`, never as
/// a rendering handle. Rule 2 — async at every boundary: `Async<_>`,
/// so a rasteriser that shells a process, calls a service, or renders
/// on a pooled thread is expressible; an implementation that computes
/// synchronously simply completes immediately. Rule 3 — retry as
/// data: there is none to express, because rasterisation is a total
/// function of its inputs with no partial effect to compensate; a
/// failure is a value (`Error`), not a callback. Rule 4 — stateless
/// between invocations: every input arrives as a parameter, so an
/// implementation may be a singleton, per-call, or remote. Rules 5
/// and 6 do not apply (no sharding, no timing contract).
type ISvgRasterizer =
    /// Rasterise `svg` to PNG bytes `widthPx` pixels wide. The height
    /// is the implementation's to derive from the document's own
    /// aspect ratio — the caller declares one dimension so a fallback
    /// can never be produced at a distorted aspect.
    ///
    /// Returns `Error` with a human-readable reason rather than
    /// raising: a figure whose fallback could not be produced still
    /// embeds, SVG-only.
    abstract Rasterize: svg: string * widthPx: int -> Async<Result<byte[], string>>