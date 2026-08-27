# ToolUp.Reporting.Xlsx

XLSX rendering sub-companion for `ToolUp.Reporting`: fills `.xlsx` report templates and
returns the filled workbook bytes. Implements `IReportRenderer` for
`TemplateFormat.Xlsx` and registers through the standard `RendererRegistry` path.

## Two binding modes

**1. Token templates.** `{{key}}` tokens in cell text (shared-string or inline-string
cells) substitute with the shared placeholder machinery — same syntax and format-hint
behaviour as every other renderer. A cell whose *entire* text is one token bound to an
unhinted `Number` value becomes a **native numeric cell**, so downstream formulas can
consume it; hinted numbers and dates render as formatted text per the hint.

**2. Cell-address map.** A placeholder key shaped as a sheet-qualified A1 reference —
`"Sheet1!B7"`, or `"'My Sheet'!B7"` for quoted names — writes its value directly into
that cell, with no template-side markup. This is the mode for workbooks whose layout was
authored visually in a spreadsheet application:

```fsharp
let values =
    Map [
        "Sheet1!B7", NumberValue 41250.0
        "Sheet1!B8", DateValue reportDate
        "Summary!C2", TextValue "Q3 actuals"
    ]
```

The target cell's style is left untouched, so the **template's own number-format string
keeps governing how the written value displays** (a currency-formatted `B7` shows the
written number as currency). Dates written this way land as OADate serial numbers; give
the target cell a date format in the template.

Keys are classified strictly: only a well-formed `Sheet!A1` reference is a cell write;
everything else — including keys that merely contain `!` — stays an ordinary token key.

## Semantics worth knowing

- **Formula cells are never substituted into**, and any render that writes values marks
  the workbook for full recalculation on open, so formulas referencing filled cells
  re-evaluate in the consumer's spreadsheet application.
- A cell write naming a sheet the workbook does not contain fails the render with a
  `RendererFailure` naming the sheet and key.
- `Image`- and `Table`-kind values are not supported in cell writes
  (`UnsupportedPlaceholderKind`); inline table tokens render as tab-separated text lines.
- Unknown `{{key}}` tokens pass through unchanged, so schema/template drift stays
  visible.
- Invalid template bytes (not a readable `.xlsx`) surface as `RendererFailure`, never an
  exception.

## Dependencies

`ToolUp.Reporting.Core` (the renderer contract) and `DocumentFormat.OpenXml` (the vendor
dependency lives in this package only, per the companion-isolation principle).
