# SVG attribute helpers

A typed helper module — `ToolUp.Platform.SvgProp` (`Client/UI/SvgProp.fs`) — that wraps `prop.custom` with React-compatible **camelCase** SVG attribute names. Use it whenever a downstream consumer module hand-rolls SVG with the `Html.svg` / `Html.path` / `Html.line` / `Html.rect` / `Html.text` / `Html.g` Feliz primitives.

## Why this exists

Feliz's typed `Svg.*` API takes an `ISvgAttribute list`, which doesn't compose with the `Html.*` primitives that the rest of the SDK and most consumer modules use. The escape hatch is `prop.custom (name, value)`, but that opens a real footgun:

- **React requires camelCase for SVG attribute names** (`strokeWidth`, `fillOpacity`, `textAnchor`, …).
- A kebab-case name (`stroke-width`, `fill-opacity`, `text-anchor`, …) is **silently dropped from the DOM**.
- In a development build, React emits a one-shot console warning of the shape `Invalid DOM property '<name>'. Did you mean '<camelName>'?`. The chart still renders, but unstyled.
- In a production build, React drops the attribute with **no warning at all**. The trap is entered without any diagnostic surface.

The rule:

> **Never use raw `prop.custom` with a kebab-case attribute name in an SVG context.** Either add a typed helper to `SvgProp` or pass a camelCase string to `prop.custom` directly.

## Single-word attributes are React-safe lowercase

These don't need a helper — write them as Feliz `prop.*` (where Feliz exposes them) or as plain camelCase strings in `prop.custom`:

`x`, `y`, `cx`, `cy`, `r`, `d`, `fill`, `stroke`, `width`, `height`, `viewBox`, `transform`, `opacity`, `points`, `rx`, `ry`.

## Available helpers

All helpers live under the `svgProp` module (lowercase to match Feliz's own `prop.*` shape) and return `Feliz.IReactProperty`:

| Helper | SVG attribute (DOM) | Parameter type |
|---|---|---|
| `svgProp.strokeWidth` | `stroke-width` | `float` |
| `svgProp.strokeDasharray` | `stroke-dasharray` | `string` |
| `svgProp.strokeOpacity` | `stroke-opacity` | `float` |
| `svgProp.strokeLinecap` | `stroke-linecap` | `string` |
| `svgProp.strokeLinejoin` | `stroke-linejoin` | `string` |
| `svgProp.fillOpacity` | `fill-opacity` | `float` |
| `svgProp.textAnchor` | `text-anchor` | `string` |
| `svgProp.dominantBaseline` | `dominant-baseline` | `string` |
| `svgProp.alignmentBaseline` | `alignment-baseline` | `string` |
| `svgProp.fontSize` | `font-size` | `float` |
| `svgProp.fontWeight` | `font-weight` | `string` |
| `svgProp.fontFamily` | `font-family` | `string` |
| `svgProp.pointerEvents` | `pointer-events` | `string` |
| `svgProp.clipPath` | `clip-path` | `string` |
| `svgProp.className` | `class` | `string` |
| `svgProp.htmlFor` | `for` | `string` |

`className` and `htmlFor` are provided for symmetry — a consumer rendering an SVG element with these helpers has one canonical surface for its props. Functionally they're identical to Feliz's `prop.className` / `prop.htmlFor`.

## Example

```fsharp
open Feliz
open ToolUp.Platform.SvgProp

let renderTick (x: float) (y: float) (label: string) =
    Html.g [
        Html.line [
            prop.custom ("x1", x)
            prop.custom ("y1", y - 4.0)
            prop.custom ("x2", x)
            prop.custom ("y2", y + 4.0)
            prop.stroke "currentColor"
            svgProp.strokeWidth 1.0
        ]
        Html.text [
            prop.custom ("x", x)
            prop.custom ("y", y + 16.0)
            svgProp.textAnchor "middle"
            svgProp.fontSize 11.0
            svgProp.fontFamily "Inter, system-ui, sans-serif"
            prop.text label
        ]
    ]
```

## When to add a new helper

Add a helper here when:

1. The SVG attribute has a kebab-case name in the SVG spec (i.e. its React form differs from its DOM form).
2. The attribute would otherwise tempt a consumer to write `prop.custom ("kebab-name", …)`.

Keep parameter types as tight as the attribute warrants: `float` for numeric stroke/fill/opacity/size values, `string` for enum-shaped or free-text values, dedicated DUs only when the spec is small and stable (e.g. linecap, linejoin) and a consumer is likely to mistype.

## See also

- [`README.md`](README.md) — Platform overview.
- [`architecture.md`](architecture.md) — composition root + tier split.
