# Phase 576 — OOXML figures + the `ISvgRasterizer` seam

`ToolUp.OpenXml` can now embed pictures. Two things change for a consumer: the `Block` DU gained a
case (a compile-time break for exhaustive matches), and a consumer that had built its own figure
plumbing on top of the package can delete it.

## What changed

| Addition | Where |
|---|---|
| `Block.Figure of FigureModel`, with `FigureContent` / `FigureSize` | `ToolUp.OpenXml` |
| `Figures.image` / `imageNamed` / `svg` / `svgNamed` / `svgWith` / `svgNamedWith` | `ToolUp.OpenXml` |
| `Figures.svgIntrinsicSize` / `rasterIntrinsicSize` / `intrinsicSize` / `extents` | `ToolUp.OpenXml` |
| `ISvgRasterizer` — one async method, optional everywhere | `ToolUp.OpenXml` |
| `SkiaSvgRasterizer.create` — the first implementation | `ToolUp.OpenXml.SvgRasterizer.Skia` (new package) |

Emission lowers a `Figure` to a paragraph carrying an inline `w:drawing` plus the image part(s) it
references off the **main document part's** relationships. An SVG embeds vector-first: the payload
goes in verbatim as an `image/svg+xml` part referenced through the `svgBlip` blip extension, with an
optional PNG fallback part for clients predating it.

## 1. Exhaustive matches over `Block` (required)

`Block` gained `Figure`, and FS0025 is an error tree-wide, so every `match` over a `Block` needs an
arm. Two shapes cover almost every site:

```fsharp
// A pass-through walk — a figure carries bytes, not runs, so nothing to rewrite.
| Block.Figure _
| Block.OpaqueBlock _ -> [ block ]

// A text extraction — a figure's extractable text is its accessible description.
| Block.Figure figure ->
    match figure.Description with
    | Some description when description.Trim().Length > 0 -> emit (description.Trim())
    | Some _
    | None -> ()
```

`Block.text` already does the second for you: it returns the figure's `Description`, or `""` when it
declares none.

## 2. Collapsing a locally-built figure implementation (optional)

A consumer that shipped its own `svgBlip` plumbing ahead of this package — an SVG part, an optional
PNG fallback part, and a local one-method rasteriser interface — replaces it with construction:

```fsharp
// Before — a locally-built figure, embedded by a post-emit pass over the package.
let docx = Emit.toBytes model |> LocalFigures.embedInto rasterizer figuresBySource

// After — the figure is a block in the model, and emission attaches its parts.
let figure = Figures.svgNamed "Revenue" (Some "Quarterly revenue") chartSvg FigureSize.Intrinsic None
let docx = Emit.toBytes (DocModel.ofBlocks (body @ [ figure ]))
```

The sentinel-paragraph round trip goes away with it: a figure is now expressible in the model, so
there is nothing to place a marker for and swap out afterwards.

### The async-over-sync adapter

**A locally-built seam is very likely synchronous, and this one is not.** `ISvgRasterizer.Rasterize`
returns `Async<Result<byte[], string>>` so that an implementation shelling a process or calling a
service is expressible (GP 12 rule 2) — a synchronous implementation simply has nothing to await.
Bridge it in four lines:

```fsharp
type SyncRasterizerAdapter(inner: LocalSyncRasterizer) =
    interface ISvgRasterizer with
        member _.Rasterize(svg, widthPx) = async { return inner.Rasterize(svg, widthPx) }
```

**The other divergence is the height parameter.** A local seam typically takes `svg * widthPx *
heightPx`; this one takes `svg * widthPx` only, and the implementation derives the height from the
document's own aspect ratio — so a fallback can never be produced distorted by a caller's arithmetic.
If the local implementation needs both, recover the second from the payload with the package's own
sizing helper, which is the same code `FigureSize.Intrinsic` uses:

```fsharp
member _.Rasterize(svg, widthPx) =
    async {
        let intrinsicWidth, intrinsicHeight = Figures.svgIntrinsicSize svg
        let heightPx = max 1 (widthPx * intrinsicHeight / intrinsicWidth)
        return inner.Rasterize(svg, widthPx, heightPx)
    }
```

## 3. Composing the rasteriser (optional)

Nothing needs a rasteriser. With none composed, an SVG figure embeds SVG-only, which is what a
current Office client renders anyway — and the base package then carries no rendering engine and no
native dependency. Add one only for old-client fidelity:

```
dotnet add package ToolUp.OpenXml.SvgRasterizer.Skia
# Linux hosts also need a SkiaSharp native asset package:
dotnet add package SkiaSharp.NativeAssets.Linux
```

```fsharp
let rasterizer = SkiaSvgRasterizer.create ()   // stateless; hold one per deployment
let! figure = Figures.svgWith (Some rasterizer) chartSvg (Pixels(640, 360))
```

A rasteriser that returns `Error`, or raises, costs the figure its fallback and never the figure.

## 4. Determinism

Figure relationship ids are assigned by the emitter (`rTuFigImg1` / `rTuFigSvg1` / `rTuFigFbk1`,
ordinal being the figure's position in document order), not by the OpenXml SDK — whose generator
mints a random id per part that lands verbatim in the drawing XML. Two emits of the same model
therefore produce byte-identical document and image parts. **If you kept your own emitter, keep
assigning ids explicitly**; the defect does not fail loudly, it just hashes differently every run.

Package-level byte equality is still not available: OPC ZIP entries carry a wall-clock timestamp.
Compare parts, not containers.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
```

In your own consumer, the two things worth pinning are that a reopened `.docx` carries an
`image/svg+xml` part whose bytes equal the SVG you passed (UTF-8, no BOM), and that two emits of the
same figure produce identical part bytes.

## Rollback

Pin `ToolUp.OpenXml` back to the previous version and drop the rasteriser package reference. Any
`Block.Figure` construction and the `Figure` match arms go with it; nothing else in this release
changes an existing signature, so a consumer that never constructed a figure is unaffected beyond
the added match arms.
