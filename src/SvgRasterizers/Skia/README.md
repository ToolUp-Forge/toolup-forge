# ToolUp.OpenXml.SvgRasterizer.Skia

A Skia-backed implementation of `ToolUp.OpenXml`'s `ISvgRasterizer` seam.

`ToolUp.OpenXml` embeds an SVG figure **vector-first**: the SVG document goes into the package
verbatim as an `image/svg+xml` part, referenced through the `svgBlip` blip extension that Word and
PowerPoint have honoured since 2016. That embed needs no rasteriser at all, and the base package
therefore ships none — it carries no rendering engine and no native dependency.

What a rasteriser adds is the **optional PNG fallback part**, which clients predating the extension
render instead. This package produces it.

## Install

```
dotnet add package ToolUp.OpenXml.SvgRasterizer.Skia
```

On Linux, additionally reference a SkiaSharp native asset package for the host — SkiaSharp ships the
Windows and macOS natives itself, but not the Linux ones:

```
dotnet add package SkiaSharp.NativeAssets.Linux
# or, on a container with no fontconfig:
dotnet add package SkiaSharp.NativeAssets.Linux.NoDependencies
```

The `NoDependencies` variant needs no fontconfig, at the cost of the system font set — SVG `<text>`
then renders with whatever fonts the image does carry. If your figures contain text, prefer the plain
variant on Linux.

## Use

```fsharp
open ToolUp.OpenXml
open ToolUp.OpenXml.SvgRasterizer.Skia

// Stateless — hold one for the lifetime of the deployment.
let rasterizer = SkiaSvgRasterizer.create ()

// The seam is asynchronous, so a figure that carries a fallback is built
// inside an async block: `Figures.svgWith` returns `Async<Block>`, not
// `Block`. That is the one shape change composing a rasteriser forces on a
// caller.
let renderChart (chartSvg: string) =
    async {
        // The figure carries both parts: the SVG verbatim, and a PNG fallback.
        let! figure = Figures.svgWith (Some rasterizer) chartSvg (Pixels(640, 360))
        return Emit.toBytes (DocModel.ofBlocks [ figure ])
    }
```

Passing `None` instead of `Some rasterizer` is not a degraded mode — it is the vector-only embed, and
it is what a deployment that never installs this package gets. A rasteriser that fails returns
`Error` and the figure embeds SVG-only; the caller never loses the picture over a missing fallback.

## Behaviour

- **Width in, aspect preserved.** `Rasterize(svg, widthPx)` takes one dimension; the height follows
  the SVG's own bounds, so a fallback can never be produced at a distorted aspect.
- **Transparent background.** The fallback is the same picture as the vector part; an SVG that
  declares no background does not gain one on the way to a raster.
- **Failures are values.** A malformed payload, an SVG with no positive drawing bounds, a
  non-positive width, or a width above the 20000-pixel ceiling all return `Error` with a
  human-readable reason. Nothing in the rasterisation path raises.
- **Native probe at construction.** `create` touches Skia once and raises a message naming the
  missing native asset package if the runtime identifier has none — so an absent RID fails at
  composition, not part-way through a document emit.

## Licensing

MIT throughout the rendering stack: `Svg.Skia` and its `Svg.Model` / `Svg.Custom` /
`Svg.SceneGraph` / `Svg.Animation` siblings, `SkiaSharp`, and `HarfBuzzSharp` (text shaping). This
package itself is Apache-2.0, like the rest of the SDK.
