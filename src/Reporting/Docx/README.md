# ToolUp.Reporting.Docx

DOCX rendering sub-companion for `ToolUp.Reporting`: fills `.docx` report templates
(`{{key}}` placeholder tokens in document text) and returns the filled document bytes.
Implements `IReportRenderer` for `TemplateFormat.Docx` and registers through the standard
`RendererRegistry` path.

Built on `ToolUp.OpenXml`'s structural model — `Import` → substitution over runs →
`Emit` — rather than string surgery inside the package XML. That altitude is what
preserves the template's styles, numbering, tables, comments and unmodelled parts
(carried opaquely, per the import residue report), and keeps the door open to emitting
fills as native tracked changes later.

## Usage

```fsharp
open ToolUp.Reporting

// At composition time (consumer side):
ReportingServerApp.create config
|> ReportingServerApp.withReportRenderer (ToolUp.Reporting.Docx.DocxReportRenderer.create ())
```

Author templates as ordinary `.docx` files containing `{{key}}` tokens; declare each key
in the template's `Placeholders` schema. Scalar kinds (`Text` / `Number` / `Date`) honour
their format hints via the shared substitution machinery.

## Semantics worth knowing

- **Split tokens re-join.** Word routinely splits a typed token across runs (spell-check,
  edit history). Adjacent runs with identical formatting are coalesced before
  substitution, so those tokens still match. A token split across a *formatting boundary*
  (e.g. half the key bolded) is left as authored — fix the template.
- **Tables.** A `Table`-kind placeholder whose token is the entire paragraph renders as a
  native Word table (bold header row + one row per data entry). An inline table token
  renders as tab-separated text.
- **Images.** `Image`-kind values render as a bracketed text marker — the structural
  model does not carry image parts. Use the HTML→PDF renderer for image-bearing output.
- **Unknown tokens pass through** unchanged, so schema/template drift stays visible.
- **Invalid template bytes** (not a readable `.docx`) surface as `RendererFailure`, never
  an exception.

## Dependencies

`ToolUp.Reporting.Core` (the renderer contract) and `ToolUp.OpenXml` (the structural
model; the `DocumentFormat.OpenXml` vendor dependency lives there — this package adds no
vendor dependency of its own).
