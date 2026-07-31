# DOM attribute helpers (`svgProp`, `dataProp`, `ariaProp`)

Three companion helper modules — `ToolUp.Platform.SvgProp`,
`ToolUp.Platform.DataProp`, and `ToolUp.Platform.AriaProp` — that wrap
`prop.custom` with React-compatible attribute names. Use them whenever
a downstream consumer module hand-rolls an SVG element with the
`Html.svg` / `Html.path` / `Html.line` / `Html.rect` / `Html.text` /
`Html.g` Feliz primitives, or needs to set a `data-*`, `aria-*`, or
`role` attribute that Feliz doesn't already type.

## Why these modules exist

Feliz's typed `Svg.*` API takes an `ISvgAttribute list`, which doesn't
compose with the `Html.*` primitives the rest of the SDK and most
consumer modules use. The escape hatch is `prop.custom (name, value)`
— but that opens a real footgun for SVG, and a latent one for the
DOM `data-*` / `aria-*` / `role` families:

- **SVG attributes — silently dropped today.** React requires
  **camelCase** for SVG attribute names (`strokeWidth`, `fillOpacity`,
  `textAnchor`, …). A kebab-case name (`stroke-width`, `fill-opacity`,
  `text-anchor`, …) is **silently dropped from the DOM**. Development
  builds emit a one-shot console warning (`Invalid DOM property
  '<name>'. Did you mean '<camelName>'?`); production builds drop
  the attribute with **no warning at all**.
- **`data-*` / `aria-*` / `role` — preserved verbatim today via a
  hardcoded React exception.** Today these reach the DOM correctly
  through the same `prop.custom` code path that drops the SVG forms
  — they happen to be on React's exception list. A future React
  release tightening the kebab-case heuristic would silently
  regress every consumer-site call. Cheap to insulate today.

A single rule covers all three families:

> **Never use raw `prop.custom` with a `data-*`, `aria-*`, `role`, or
> kebab-case SVG attribute name.** Use the matching helper module
> (`svgProp` / `dataProp` / `ariaProp`) or, for one-off names, the
> module's generic `.custom` escape hatch.

The audit ratchet (`src/ToolUp.Platform.Tests/InProcess/DomAttrCustomAuditTests.fs`)
walks the forge client tree and flags any new `prop.custom
("data-*"|"aria-*"|"role", _)` call outside these helper modules' own
bodies — regressions fail the test pack on the next `dotnet run
--project src/ToolUp.Platform.Tests/`.

## `svgProp` — SVG attribute helpers

### Single-word attributes are React-safe lowercase

These don't need a helper — write them as Feliz `prop.*` (where
Feliz exposes them) or as plain camelCase strings in `prop.custom`:

`x`, `y`, `cx`, `cy`, `r`, `d`, `fill`, `stroke`, `width`, `height`,
`viewBox`, `transform`, `opacity`, `points`, `rx`, `ry`.

### Available helpers

All helpers live under the `svgProp` module (lowercase to match
Feliz's own `prop.*` shape) and return `Feliz.IReactProperty`:

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

`className` and `htmlFor` are provided for symmetry — a consumer
rendering an SVG element with these helpers has one canonical surface
for its props. Functionally they're identical to Feliz's
`prop.className` / `prop.htmlFor`.

### Example

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

### When to add a new `svgProp` helper

Add a helper when:

1. The SVG attribute has a kebab-case name in the SVG spec (i.e. its
   React form differs from its DOM form).
2. The attribute would otherwise tempt a consumer to write
   `prop.custom ("kebab-name", …)`.

Keep parameter types as tight as the attribute warrants: `float` for
numeric stroke/fill/opacity/size values, `string` for enum-shaped or
free-text values, dedicated DUs only when the spec is small and stable
(e.g. linecap, linejoin) and a consumer is likely to mistype.

## `dataProp` — `data-*` attribute helpers

`data-*` is HTML's escape hatch for stashing arbitrary string metadata
on a DOM element. React preserves the names verbatim (kebab-case is
correct here — the DOM attribute is literally `data-foo`, not
`dataFoo`).

### Available helpers

All helpers live under the `dataProp` module and return
`Feliz.IReactProperty`:

| Helper | DOM attribute | Parameter type | Used by |
|---|---|---|---|
| `dataProp.adClient` | `data-ad-client` | `string` | `Components.AdSlot` |
| `dataProp.adSlot` | `data-ad-slot` | `string` | `Components.AdSlot` |
| `dataProp.adFormat` | `data-ad-format` | `string` | `Components.AdSlot` |
| `dataProp.adLayoutKey` | `data-ad-layout-key` | `string` | `Components.AdSlot` (fluid format) |
| `dataProp.aiName` | `data-ai-name` | `string` | `UIToolkit.Forms.aiNamed` |
| `dataProp.area` | `data-area` | `string` | `UIToolkit.Layout.Dashboard` |
| `dataProp.citeIdx` | `data-cite-idx` | `int` | `AI.Client.ConversationPanel` |
| `dataProp.testid` | `data-testid` | `string` | (downstream consumers — Playwright / RTL / Cypress) |
| `dataProp.custom` | (caller-supplied `data-…`) | `string` × `string` | One-off escape hatch |

### Example

```fsharp skip=fragment
open Feliz
open ToolUp.Platform

let pulseTarget (name: string) (child: ReactElement) =
    Html.div [
        dataProp.aiName name
        prop.className "inline-block w-full"
        prop.children [ child ]
    ]
```

### When to add a new `dataProp` helper

Add a helper when the same `data-*` attribute is set in more than one
file or by more than one module, and a typed helper name would
document the intent at the call site better than the raw string. For
one-off attributes, prefer `dataProp.custom rawName value` over
inflating the typed surface.

## `ariaProp` — `aria-*` + `role` attribute helpers

`aria-*` and `role` are the standard hooks for assistive technology
(screen readers, voice control, switch access). React preserves them
verbatim via a hardcoded exception in its attribute normaliser, the
same protection `data-*` enjoys.

> Feliz exposes most ARIA properties through `prop.ariaXxx` and `prop.role`.
> **Prefer the Feliz typed forms** where they cover the attribute. The
> `ariaProp` module is the blessed escape hatch for attributes Feliz
> doesn't type yet, and exists for symmetry with `dataProp` / `svgProp`
> so the audit ratchet has a single rule.

### Available helpers

| Helper | DOM attribute | Parameter type |
|---|---|---|
| `ariaProp.label` | `aria-label` | `string` |
| `ariaProp.labelledBy` | `aria-labelledby` | `string` |
| `ariaProp.describedBy` | `aria-describedby` | `string` |
| `ariaProp.hidden` | `aria-hidden` | `bool` |
| `ariaProp.expanded` | `aria-expanded` | `bool` |
| `ariaProp.selected` | `aria-selected` | `bool` |
| `ariaProp.checked'` | `aria-checked` | `string` (three-state: `"true"` / `"false"` / `"mixed"`) |
| `ariaProp.disabled` | `aria-disabled` | `bool` |
| `ariaProp.busy` | `aria-busy` | `bool` |
| `ariaProp.live` | `aria-live` | `string` (`"off"` / `"polite"` / `"assertive"`) |
| `ariaProp.atomic` | `aria-atomic` | `bool` |
| `ariaProp.controls` | `aria-controls` | `string` |
| `ariaProp.current` | `aria-current` | `string` |
| `ariaProp.pressed` | `aria-pressed` | `string` (three-state) |
| `ariaProp.hasPopup` | `aria-haspopup` | `string` |
| `ariaProp.modal` | `aria-modal` | `bool` |
| `ariaProp.role` | `role` | `string` |
| `ariaProp.custom` | (caller-supplied `aria-…`) | `string` × `string` |

### Example

```fsharp skip=fragment
open Feliz
open ToolUp.Platform

let confirmDialog (titleId: string) (descId: string) (body: ReactElement) =
    Html.div [
        ariaProp.role "dialog"
        ariaProp.modal true
        ariaProp.labelledBy titleId
        ariaProp.describedBy descId
        prop.children [ body ]
    ]
```

## Audit ratchet

`src/ToolUp.Platform.Tests/InProcess/DomAttrCustomAuditTests.fs`
walks `src/*.Client/Client/**/*.fs` and flags any
`prop.custom ("data-*"|"aria-*"|"role", _)` call outside the helper
modules themselves. The test pack runs as part of `dotnet run --project
src/ToolUp.Platform.Tests/`; regressions fail the next pack run.

## See also

- [`README.md`](README.md) — Platform overview.
- [`architecture.md`](architecture.md) — composition root + tier split.
